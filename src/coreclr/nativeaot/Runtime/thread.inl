// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#ifndef DACCESS_COMPILE

#include "eventtracebase.h"

const uint32_t SamplingDistributionMean = (100 * 1024);

inline gc_alloc_context* ee_alloc_context::GetGCAllocContext()
{
    return (gc_alloc_context*)&m_rgbAllocContextBuffer;
}

inline uint8_t* ee_alloc_context::GetCombinedLimit()
{
    return combined_limit;
}

// Workaround for https://github.com/dotnet/runtime/issues/96081
struct _thread_inl_gc_alloc_context
{
    uint8_t* alloc_ptr;
    uint8_t* alloc_limit;
};


inline bool ee_alloc_context::IsRandomizedSamplingEnabled()
{
#ifdef FEATURE_EVENT_TRACE
    return IsRuntimeProviderEnabled(TRACE_LEVEL_INFORMATION, CLR_ALLOCATIONSAMPLING_KEYWORD);
#else
    return false;
#endif // FEATURE_EVENT_TRACE
}

inline void ee_alloc_context::UpdateCombinedLimit(bool samplingEnabled)
{
    _thread_inl_gc_alloc_context* gc_alloc_context = (_thread_inl_gc_alloc_context*)GetGCAllocContext();
    if (!samplingEnabled)
    {
        combined_limit = gc_alloc_context->alloc_limit;
    }
    else
    {
        // compute the next sampling budget based on a geometric distribution
        size_t samplingBudget = ComputeGeometricRandom();

        // if the sampling limit is larger than the allocation context, no sampling will occur in this AC
        // We do Min() prior to adding to alloc_ptr to ensure alloc_ptr+samplingBudget doesn't cause an overflow.
        
        size_t size = gc_alloc_context->alloc_limit - gc_alloc_context->alloc_ptr;
        combined_limit = gc_alloc_context->alloc_ptr + min(samplingBudget, size);
    }
}

inline uint32_t ee_alloc_context::ComputeGeometricRandom()
{
    // compute a random sample from the Geometric distribution.
    double probability = t_random.NextDouble();
    uint32_t threshold = (uint32_t)(-log(1 - probability) * SamplingDistributionMean);
    return threshold;
}

// Returns a random double in the range [0, 1).
inline double ee_alloc_context::PerThreadRandom::NextDouble()
{
    uint32_t value = minipal_xoshiro128pp_next(&random_state);
    return value * (1.0/(UINT32_MAX+1.0));
}

// Set the m_pDeferredTransitionFrame field for GC allocation helpers that setup transition frame
// in assembly code. Do not use anywhere else.
inline void Thread::SetDeferredTransitionFrame(PInvokeTransitionFrame* pTransitionFrame)
{
    ASSERT(ThreadStore::GetCurrentThread() == this);
    ASSERT(Thread::IsCurrentThreadInCooperativeMode());
    ASSERT(!Thread::IsHijackTarget(pTransitionFrame->m_RIP));
    m_pDeferredTransitionFrame = pTransitionFrame;
}

// Setup the m_pDeferredTransitionFrame field for GC helpers entered via regular PInvoke.
// Do not use anywhere else.
inline void Thread::DeferTransitionFrame()
{
    ASSERT(ThreadStore::GetCurrentThread() == this);
    ASSERT(!Thread::IsCurrentThreadInCooperativeMode());
    m_pDeferredTransitionFrame = m_pTransitionFrame;
}
#endif // DACCESS_COMPILE

inline bool Thread::IsWithinStackBounds(PTR_VOID p)
{
    ASSERT((m_pStackLow != 0) && (m_pStackHigh != 0));
    return (m_pStackLow <= p) && (p < m_pStackHigh);
}

inline void Thread::GetStackBounds(PTR_VOID * ppStackLow, PTR_VOID * ppStackHigh)
{
    ASSERT((m_pStackLow != 0) && (m_pStackHigh != 0));
    *ppStackLow = m_pStackLow;
    *ppStackHigh = m_pStackHigh;
}

#ifndef DACCESS_COMPILE
inline void Thread::SetThreadStressLog(void* ptsl)
{
    m_pThreadStressLog = ptsl;
}
#endif // DACCESS_COMPILE

inline PTR_VOID Thread::GetThreadStressLog() const
{
    return m_pThreadStressLog;
}

inline void Thread::PushGCFrameRegistration(GCFrameRegistration* pRegistration)
{
    pRegistration->m_pNext = m_pGCFrameRegistrations;
    m_pGCFrameRegistrations = pRegistration;
}

inline void Thread::PopGCFrameRegistration(GCFrameRegistration* pRegistration)
{
    ASSERT(m_pGCFrameRegistrations == pRegistration);
    m_pGCFrameRegistrations = pRegistration->m_pNext;
}

inline ee_alloc_context* Thread::GetEEAllocContext()
{
    return &m_eeAllocContext;
}

inline gc_alloc_context* Thread::GetAllocContext()
{
    return GetEEAllocContext()->GetGCAllocContext();
}

inline bool Thread::IsStateSet(ThreadStateFlags flags)
{
    return ((m_ThreadStateFlags & flags) == (uint32_t)flags);
}

inline bool Thread::IsDoNotTriggerGcSet()
{
    return IsStateSet(TSF_DoNotTriggerGc);
}

inline bool Thread::IsCurrentThreadInCooperativeMode()
{
#ifndef DACCESS_COMPILE
    ASSERT(ThreadStore::GetCurrentThread() == this);
#endif // !DACCESS_COMPILE
    return (m_pTransitionFrame == NULL);
}

// define a specialized version locally so that we do not need to depend on entire gcenv.h
inline void VolatileStoreWithoutBarrier(PInvokeTransitionFrame** frameRef, PInvokeTransitionFrame* val)
{
    *(PInvokeTransitionFrame* volatile*)frameRef = val;
}

FORCEINLINE void Thread::InlineReversePInvokeReturn(ReversePInvokeFrame* pFrame)
{
    // set our mode to preemptive
    VolatileStoreWithoutBarrier(&m_pTransitionFrame, pFrame->m_savedPInvokeTransitionFrame);
}

FORCEINLINE void Thread::InlinePInvoke(PInvokeTransitionFrame* pFrame)
{
    ASSERT(!IsDoNotTriggerGcSet() || ThreadStore::IsTrapThreadsRequested());
    pFrame->m_pThread = this;
    // set our mode to preemptive
    VolatileStoreWithoutBarrier(&m_pTransitionFrame, pFrame);
}

EXTERN_C void FASTCALL RhpWaitForGC2(PInvokeTransitionFrame* pFrame);

FORCEINLINE void Thread::InlinePInvokeReturn(PInvokeTransitionFrame* pFrame)
{
    // must be in cooperative mode when checking the trap flag
    VolatileStoreWithoutBarrier(&m_pTransitionFrame, (PInvokeTransitionFrame*)nullptr);
    if (ThreadStore::IsTrapThreadsRequested())
    {
        RhpWaitForGC2(pFrame);
    }
}

FORCEINLINE bool Thread::InlineTryFastReversePInvoke(ReversePInvokeFrame* pFrame)
{
    // remember the current transition frame, so it will be restored when we return from reverse pinvoke
    pFrame->m_savedPInvokeTransitionFrame = m_pTransitionFrame;

    // If the thread is already in cooperative mode, this is a bad transition that will be a fail fast unless we are in
    // a do not trigger mode.  The exception to the rule allows us to have [UnmanagedCallersOnly] methods that are called via
    // the "restricted GC callouts" as well as from native, which is necessary because the methods are CCW vtable
    // methods on interfaces passed to native.
    // We will allow threads in DoNotTriggerGc mode to do reverse PInvoke regardless of their coop state.
    if (IsDoNotTriggerGcSet())
    {
        // We expect this scenario only when EE is stopped.
        ASSERT(ThreadStore::IsTrapThreadsRequested());
        // no need to do anything
        return true;
    }

    // Do we need to attach the thread?
    if (!IsStateSet(TSF_Attached))
        return false; // thread is not attached

    if (IsCurrentThreadInCooperativeMode())
        return false; // bad transition

    // this is an ordinary transition to managed code
    // GC threads should not do that
    ASSERT(!IsGCSpecial());

    // must be in cooperative mode when checking the trap flag
    VolatileStoreWithoutBarrier(&m_pTransitionFrame, (PInvokeTransitionFrame*)nullptr);

    // now check if we need to trap the thread
    if (ThreadStore::IsTrapThreadsRequested())
    {
        // put the previous frame back (sets us back to preemptive mode)
        m_pTransitionFrame = pFrame->m_savedPInvokeTransitionFrame;
        return false; // need to trap the thread
    }

    return true;
}
