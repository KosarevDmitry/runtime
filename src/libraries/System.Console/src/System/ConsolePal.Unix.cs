// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace System
{
    // Provides Unix-based support for System.Console.
    //
    // NOTE: The test class reflects over this class to run the tests due to limitations in
    //       the test infrastructure that prevent OS-specific builds of test binaries. If you
    //       change any of the class / struct / function names, parameters, etc then you need
    //       to also change the test class.
    internal static partial class ConsolePal
    {
        // StdInReader is only used when input isn't redirected and we're working
        // with an interactive terminal.  In that case, performance isn't critical
        // and we can use a smaller buffer to minimize working set.
        private const int InteractiveBufferSize = 255;

        // For performance we cache Cursor{Left,Top} and Window{Width,Height}.
        // These values must be read/written under lock (Console.Out).
        // We also need to invalidate these values when certain signals occur.
        // We don't want to take the lock in the signal handling thread for this.
        // Instead, we set a flag. Before reading a cached value, a call to CheckTerminalSettingsInvalidated
        // will invalidate the cached values if a signal has occurred.
        private static int s_cursorVersion; // Gets incremented each time the cursor position changed.
                                            // Used to synchronize between lock (Console.Out) blocks.
        private static int s_cursorLeft;    // Cached CursorLeft, -1 when invalid.
        private static int s_cursorTop;     // Cached CursorTop, invalid when s_cursorLeft == -1.
        private static int s_windowWidth;   // Cached WindowWidth, -1 when invalid.
        private static int s_windowHeight;  // Cached WindowHeight, invalid when s_windowWidth == -1.
        private static int s_invalidateCachedSettings = 1; // Tracks whether we should invalidate the cached settings.
        private static SafeFileHandle? s_terminalHandle; // Tracks the handle used for writing to the terminal.

        /// <summary>Gets the lazily-initialized terminal information for the terminal.</summary>
        public static TerminalFormatStrings TerminalFormatStringsInstance { get { return s_terminalFormatStringsInstance.Value; } }
        private static readonly Lazy<TerminalFormatStrings> s_terminalFormatStringsInstance = new(() => new TerminalFormatStrings(TermInfo.DatabaseFactory.ReadActiveDatabase()));

        public static Stream OpenStandardInput()
        {
            return new UnixConsoleStream(Interop.CheckIo(Interop.Sys.Dup(Interop.Sys.FileDescriptors.STDIN_FILENO)), FileAccess.Read,
                                         useReadLine: !Console.IsInputRedirected);
        }

        public static Stream OpenStandardOutput()
        {
            return new UnixConsoleStream(Interop.CheckIo(Interop.Sys.Dup(Interop.Sys.FileDescriptors.STDOUT_FILENO)), FileAccess.Write);
        }

        public static Stream OpenStandardError()
        {
            return new UnixConsoleStream(Interop.CheckIo(Interop.Sys.Dup(Interop.Sys.FileDescriptors.STDERR_FILENO)), FileAccess.Write);
        }

        public static Encoding InputEncoding
        {
            get { return GetConsoleEncoding(); }
        }

        public static Encoding OutputEncoding
        {
            get { return GetConsoleEncoding(); }
        }

        private static SyncTextReader? s_stdInReader;

        internal static SyncTextReader StdInReader
        {
            get
            {
                return Volatile.Read(ref s_stdInReader) ?? EnsureInitialized();

                static SyncTextReader EnsureInitialized()
                {
                    EnsureConsoleInitialized();

                    SyncTextReader reader = SyncTextReader.GetSynchronizedTextReader(
                                                new StdInReader(
                                                    encoding: Console.InputEncoding
                                                ));

                    // Don't overwrite a set reader.
                    // The reader doesn't own resources, so we don't need to dispose
                    // when it was already set.
                    Interlocked.CompareExchange(ref s_stdInReader, reader, null);

                    return s_stdInReader;
                }
            }
        }

        internal static TextReader GetOrCreateReader()
        {
            if (Console.IsInputRedirected)
            {
                Stream inputStream = OpenStandardInput();
                return SyncTextReader.GetSynchronizedTextReader(
                    inputStream == Stream.Null ?
                    StreamReader.Null :
                    new StreamReader(
                        stream: inputStream,
                        encoding: Console.InputEncoding,
                        detectEncodingFromByteOrderMarks: false,
                        bufferSize: Console.ReadBufferSize,
                        leaveOpen: true));
            }
            else
            {
                return StdInReader;
            }
        }

        public static bool KeyAvailable { get { return StdInReader.KeyAvailable; } }

        public static ConsoleKeyInfo ReadKey(bool intercept)
        {
            if (Console.IsInputRedirected)
            {
                // We could leverage Console.Read() here however
                // windows fails when stdin is redirected.
                throw new InvalidOperationException(SR.InvalidOperation_ConsoleReadKeyOnFile);
            }

            ConsoleKeyInfo keyInfo = StdInReader.ReadKey(intercept);
            return keyInfo;
        }

        public static bool TreatControlCAsInput
        {
            get
            {
                if (Console.IsInputRedirected)
                    return false;

                EnsureConsoleInitialized();
                return Interop.Sys.GetSignalForBreak() == 0;
            }
            set
            {
                if (!Console.IsInputRedirected)
                {
                    EnsureConsoleInitialized();
                    if (Interop.Sys.SetSignalForBreak(Convert.ToInt32(!value)) == 0)
                        throw Interop.GetExceptionForIoErrno(Interop.Sys.GetLastErrorInfo());
                }
            }
        }

        private static ConsoleColor s_trackedForegroundColor = Console.UnknownColor;
        private static ConsoleColor s_trackedBackgroundColor = Console.UnknownColor;

        public static ConsoleColor ForegroundColor
        {
            get { return s_trackedForegroundColor; }
            set { RefreshColors(ref s_trackedForegroundColor, value); }
        }

        public static ConsoleColor BackgroundColor
        {
            get { return s_trackedBackgroundColor; }
            set { RefreshColors(ref s_trackedBackgroundColor, value); }
        }

        public static void ResetColor()
        {
            lock (Console.Out) // synchronize with other writers
            {
                s_trackedForegroundColor = Console.UnknownColor;
                s_trackedBackgroundColor = Console.UnknownColor;
                WriteResetColorString();
            }
        }

        public static bool NumberLock { get { throw new PlatformNotSupportedException(); } }

        public static bool CapsLock { get { throw new PlatformNotSupportedException(); } }

        public static int CursorSize
        {
            get { return 100; }
            set { throw new PlatformNotSupportedException(); }
        }

        public static string Title
        {
            get { throw new PlatformNotSupportedException(); }
            set
            {
                if (Console.IsOutputRedirected)
                    return;

                string? titleFormat = TerminalFormatStringsInstance.Title;
                if (!string.IsNullOrEmpty(titleFormat))
                {
                    string ansiStr = TermInfo.ParameterizedStrings.Evaluate(titleFormat, value);
                    WriteTerminalAnsiString(ansiStr, mayChangeCursorPosition: false);
                }
            }
        }

        public static void Beep()
        {
            if (!Console.IsOutputRedirected)
            {
                WriteTerminalAnsiString(TerminalFormatStringsInstance.Bell, mayChangeCursorPosition: false);
            }
        }

        public static void Clear()
        {
            if (!Console.IsOutputRedirected)
            {
                WriteTerminalAnsiString(TerminalFormatStringsInstance.Clear);
            }
        }

        public static void SetCursorPosition(int left, int top)
        {
            if (Console.IsOutputRedirected)
                return;

            SetTerminalCursorPosition(left, top);
        }

        public static void SetTerminalCursorPosition(int left, int top)
        {
            lock (Console.Out)
            {
                if (TryGetCachedCursorPosition(out int leftCurrent, out int topCurrent) &&
                    left == leftCurrent &&
                    top == topCurrent)
                {
                    return;
                }

                string? cursorAddressFormat = TerminalFormatStringsInstance.CursorAddress;
                if (!string.IsNullOrEmpty(cursorAddressFormat))
                {
                    string ansiStr = TermInfo.ParameterizedStrings.Evaluate(cursorAddressFormat, top, left);
                    WriteTerminalAnsiString(ansiStr);
                }

                SetCachedCursorPosition(left, top);
            }
        }

        private static void SetCachedCursorPosition(int left, int top, int? version = null)
        {
            Debug.Assert(left >= 0);

            bool setPosition = version == null || version == s_cursorVersion;

            if (setPosition)
            {
                s_cursorLeft = left;
                s_cursorTop = top;
                s_cursorVersion++;
            }
            else
            {
                InvalidateCachedCursorPosition();
            }
        }

        private static void InvalidateCachedCursorPosition()
        {
            s_cursorLeft = -1;
            s_cursorVersion++;
        }

        private static bool TryGetCachedCursorPosition(out int left, out int top)
        {
            // Invalidate before reading cached values.
            CheckTerminalSettingsInvalidated();

            bool hasCachedCursorPosition = s_cursorLeft >= 0;
            if (hasCachedCursorPosition)
            {
                left = s_cursorLeft;
                top = s_cursorTop;
            }
            else
            {
                left = 0;
                top = 0;
            }
            return hasCachedCursorPosition;
        }

        public static int BufferWidth
        {
            get { return WindowWidth; }
            set { throw new PlatformNotSupportedException(); }
        }

        public static int BufferHeight
        {
            get { return WindowHeight; }
            set { throw new PlatformNotSupportedException(); }
        }

        public static int LargestWindowWidth
        {
            get { return WindowWidth; }
        }

        public static int LargestWindowHeight
        {
            get { return WindowHeight; }
        }

        public static int WindowLeft
        {
            get { return 0; }
            set { throw new PlatformNotSupportedException(); }
        }

        public static int WindowTop
        {
            get { return 0; }
            set { throw new PlatformNotSupportedException(); }
        }

        public static int WindowWidth
        {
            get
            {
                GetWindowSize(out int width, out _);
                return width;
            }
            set => SetWindowSize(value, WindowHeight);
        }

        public static int WindowHeight
        {
            get
            {
                GetWindowSize(out _, out int height);
                return height;
            }
            set => SetWindowSize(WindowWidth, value);
        }

        private static void GetWindowSize(out int width, out int height)
        {
            lock (Console.Out)
            {
                // Invalidate before reading cached values.
                CheckTerminalSettingsInvalidated();

                if (s_windowWidth == -1)
                {
                    Interop.Sys.WinSize winsize;
                    if (s_terminalHandle != null &&
                        Interop.Sys.GetWindowSize(s_terminalHandle, out winsize) == 0)
                    {
                        s_windowWidth = winsize.Col;
                        s_windowHeight = winsize.Row;
                    }
                    else
                    {
                        s_windowWidth = TerminalFormatStringsInstance.Columns;
                        s_windowHeight = TerminalFormatStringsInstance.Lines;
                    }
                }

                width = s_windowWidth;
                height = s_windowHeight;
            }
        }

        public static void SetWindowSize(int width, int height)
        {
            // note: We can't implement SetWindowSize using TIOCSWINSZ.
            // TIOCSWINSZ is meant to inform the kernel of the terminal size.
            // The window that shows the terminal doesn't change to match that size.

            throw new PlatformNotSupportedException();
        }

        public static bool CursorVisible
        {
            get { throw new PlatformNotSupportedException(); }
            set
            {
                if (!Console.IsOutputRedirected)
                {
                    WriteTerminalAnsiString(value ?
                        TerminalFormatStringsInstance.CursorVisible :
                        TerminalFormatStringsInstance.CursorInvisible);
                }
            }
        }

        public static (int Left, int Top) GetCursorPosition()
        {
            if (Console.IsInputRedirected || Console.IsOutputRedirected)
            {
                return (0, 0);
            }

            TryGetCursorPosition(out int left, out int top);
            return (left, top);
        }

        /// <summary>
        /// Tracks whether we've ever successfully received a response to a cursor position request (CPR).
        /// If we have, then we can be more aggressive about expecting a response to subsequent requests,
        /// e.g. using a longer timeout.
        /// </summary>
        private static bool s_everReceivedCursorPositionResponse;

        /// <summary>
        /// Tracks if this is out first attempt to send a cursor posotion request. If it is, we start the
        /// timer immediately (i.e. minChar = 0), but we use a slightly longer timeout to avoid the CPR response
        /// being written to the console.
        /// </summary>
        private static bool s_firstCursorPositionRequest = true;

        /// <summary>Gets the current cursor position.  This involves both writing to stdout and reading stdin.</summary>
        /// <param name="left">Cursor column.</param>
        /// <param name="top">Cursor row.</param>
        /// <param name="reinitializeForRead">Indicates whether this method is called as part of a on-going Read operation.</param>
        internal static bool TryGetCursorPosition(out int left, out int top, bool reinitializeForRead = false)
        {
            Debug.Assert(!Console.IsInputRedirected);

            left = top = 0;

            int cursorVersion;
            lock (Console.Out)
            {
                if (TryGetCachedCursorPosition(out left, out top))
                {
                    return true;
                }

                cursorVersion = s_cursorVersion;
            }

            // Create a buffer to read the response into.  We start with stack memory and grow
            // into the heap only if we need to, and we choose a limit that should be large
            // enough for the vast, vast majority of use cases, such that when we do grow, we
            // just allocate, rather than employing any complicated pooling strategy.
            int readBytesPos = 0;
            Span<byte> readBytes = stackalloc byte[256];

            // Synchronize with all other stdin readers.  We need to do this in case multiple threads are
            // trying to read/write concurrently, and to minimize the chances of resulting conflicts.
            // This does mean that Console.get_CursorLeft/Top can't be used concurrently with Console.Read*, etc.;
            // attempting to do so will block one of them until the other completes, but in doing so we prevent
            // one thread's get_CursorLeft/Top from providing input to the other's Console.Read*.
            lock (StdInReader)
            {
                // Because the CPR request/response protocol involves blocking until we get a certain
                // response from the terminal, we want to avoid doing so if we don't know the terminal
                // will definitely respond.  As such, we start with minChars == 0, which causes the
                // terminal's read timer to start immediately.  Once we've received a response for
                // a request such that we know the terminal supports the protocol, we then specify
                // minChars == 1.  With that, the timer won't start until the first character is
                // received.  This makes the mechanism more reliable when there are high latencies
                // involved in reading/writing, such as when accessing a remote system. We also extend
                // the timeout on the very first request to 15 seconds, to account for potential latency
                // before we know if we will receive a response.
                Interop.Sys.InitializeConsoleBeforeRead(minChars: (byte)(s_everReceivedCursorPositionResponse ? 1 : 0), decisecondsTimeout: (byte)(s_firstCursorPositionRequest ? 100 : 10));
                try
                {
                    // Write out the cursor position report request.
                    Debug.Assert(!string.IsNullOrEmpty(TerminalFormatStrings.CursorPositionReport));
                    WriteTerminalAnsiString(TerminalFormatStrings.CursorPositionReport, mayChangeCursorPosition: false);

                    // Read the cursor position report (CPR), of the form \ESC[row;colR. This is not
                    // as easy as it sounds.  Prior to the CPR having been supplied to stdin, other
                    // user input could have come in and be available to read first from stdin.  Plus,
                    // that user input could include escape sequences, and those escape sequences could
                    // have a prefix very similar to that of the CPR (e.g. other escape sequences start
                    // with \ESC + '['.  It's also possible that some terminal implementations may not
                    // write the CPR to stdin atomically, such that the CPR could have other user input
                    // in the middle of it, and that user input could have escape sequences!  Handling
                    // that last case is very challenging, and rare, so we don't try, but we do need to
                    // handle the rest.  The min bar here is doing something reasonable, which may include
                    // giving up and just returning default top and left values.

                    // Consume from stdin until we find all of the key markers for the CPR:
                    // \ESC, '[', ';', and 'R'.  For everything before the \ESC, it's definitely
                    // not part of the CPR sequence, so we just immediately move any such bytes
                    // over to the StdInReader's extra buffer.  From there until the end, we buffer
                    // everything into readBytes for subsequent parsing.
                    const byte Esc = 0x1B;
                    StdInReader r = StdInReader.Inner;
                    int escPos, bracketPos, semiPos, rPos;
                    if (!AppendToStdInReaderUntil(Esc, r, readBytes, ref readBytesPos, out escPos) ||
                        !BufferUntil((byte)'[', ref readBytes, ref readBytesPos, out bracketPos) ||
                        !BufferUntil((byte)';', ref readBytes, ref readBytesPos, out semiPos) ||
                        !BufferUntil((byte)'R', ref readBytes, ref readBytesPos, out rPos))
                    {
                        // We were unable to read everything from stdin, e.g. a timeout occurred.
                        // Since we couldn't get the complete CPR, transfer any bytes we did read
                        // back to the StdInReader's extra buffer, treating it all as user input,
                        // and exit having not computed a valid cursor position.
                        TransferBytes(readBytes.Slice(readBytesPos), r);
                        return false;
                    }

                    // At this point, readBytes starts with \ESC and ends with 'R'.
                    Debug.Assert(readBytesPos > 0 && readBytesPos <= readBytes.Length);
                    Debug.Assert(escPos == 0 && bracketPos > escPos && semiPos > bracketPos && rPos > semiPos);
                    Debug.Assert(readBytes[escPos] == Esc);
                    Debug.Assert(readBytes[bracketPos] == '[');
                    Debug.Assert(readBytes[semiPos] == ';');
                    Debug.Assert(readBytes[rPos] == 'R');

                    // There are other sequences that begin with \ESC + '[' and that might be in our sequence before
                    // the CPR, so we don't immediately trust escPos and bracketPos.  Instead, as a heuristic we trust
                    // semiPos (which we only tracked after seeing a '[' after seeing an \ESC) and search backwards from
                    // there looking for '[' and then \ESC.
                    bracketPos = readBytes.Slice(0, semiPos).LastIndexOf((byte)'[');
                    escPos = readBytes.Slice(0, bracketPos).LastIndexOf(Esc);

                    // Everything before the \ESC is transferred back to the StdInReader. As is everything
                    // between the \ESC and the '['; there really shouldn't be anything there, but we're
                    // defensive in case the CPR wasn't written atomically and something crept in.
                    TransferBytes(readBytes.Slice(0, escPos), r);
                    TransferBytes(readBytes.Slice(escPos + 1, bracketPos - (escPos + 1)), r);

                    // Now loop through all characters between the '[' and the ';' to compute the row,
                    // and then between the ';' and the 'R' to compute the column. We incorporate any
                    // digits we find, and while we shouldn't find anything else, we defensively put anything
                    // else back into the StdInReader.
                    ReadRowOrCol(bracketPos, semiPos, r, readBytes, ref top);
                    ReadRowOrCol(semiPos, rPos, r, readBytes, ref left);

                    // Mark that we've successfully received a CPR response at least once.
                    s_everReceivedCursorPositionResponse = true;
                }
                finally
                {
                    if (reinitializeForRead)
                    {
                        Interop.Sys.InitializeConsoleBeforeRead();
                    }
                    else
                    {
                        Interop.Sys.UninitializeConsoleAfterRead();
                    }
                    s_firstCursorPositionRequest = false;
                }

                static unsafe bool BufferUntil(byte toFind, ref Span<byte> dst, ref int dstPos, out int foundPos)
                {
                    // Loop until we find the target byte.
                    while (true)
                    {
                        // Read the next byte from stdin.
                        byte b;
                        if (System.IO.StdInReader.ReadStdin(&b, 1) != 1)
                        {
                            foundPos = -1;
                            return false;
                        }

                        // Make sure we have enough room to store the byte.
                        if (dstPos == dst.Length)
                        {
                            var tmpReadBytes = new byte[dst.Length * 2];
                            dst.CopyTo(tmpReadBytes);
                            dst = tmpReadBytes;
                        }

                        // Store the byte.
                        dst[dstPos++] = b;

                        // If this is the target, we're done.
                        if (b == toFind)
                        {
                            foundPos = dstPos - 1;
                            return true;
                        }
                    }
                }

                static unsafe bool AppendToStdInReaderUntil(byte toFind, StdInReader reader, Span<byte> foundByteDst, ref int foundByteDstPos, out int foundPos)
                {
                    // Loop until we find the target byte.
                    while (true)
                    {
                        // Read the next byte from stdin.
                        byte b;
                        if (System.IO.StdInReader.ReadStdin(&b, 1) != 1)
                        {
                            foundPos = -1;
                            return false;
                        }

                        // If it's the target byte, store it and exit.
                        if (b == toFind)
                        {
                            Debug.Assert(foundByteDstPos < foundByteDst.Length, "Should only be called when there's room for at least one byte.");
                            foundPos = foundByteDstPos;
                            foundByteDst[foundByteDstPos++] = b;
                            return true;
                        }

                        // Otherwise, push it back into the reader's extra buffer.
                        reader.AppendExtraBuffer(new ReadOnlySpan<byte>(in b));
                    }
                }

                static void ReadRowOrCol(int startExclusive, int endExclusive, StdInReader reader, ReadOnlySpan<byte> source, ref int result)
                {
                    int row = 0;

                    for (int i = startExclusive + 1; i < endExclusive; i++)
                    {
                        byte b = source[i];
                        if (char.IsAsciiDigit((char)b))
                        {
                            try
                            {
                                row = checked((row * 10) + (b - '0'));
                            }
                            catch (OverflowException) { }
                        }
                        else
                        {
                            reader.AppendExtraBuffer(new ReadOnlySpan<byte>(in b));
                        }
                    }

                    if (row >= 1)
                    {
                        result = row - 1;
                    }
                }
            }

            static void TransferBytes(ReadOnlySpan<byte> src, StdInReader dst)
            {
                for (int i = 0; i < src.Length; i++)
                {
                    dst.AppendExtraBuffer(src.Slice(i, 1));
                }
            }

            lock (Console.Out)
            {
                SetCachedCursorPosition(left, top, cursorVersion);
                return true;
            }
        }

        /// <summary>
        /// Gets whether the specified file descriptor was redirected.
        /// It's considered redirected if it doesn't refer to a terminal.
        /// </summary>
        private static bool IsHandleRedirected(SafeFileHandle fd)
        {
            return !Interop.Sys.IsATty(fd);
        }

        /// <summary>
        /// Gets whether Console.In is redirected.
        /// We approximate the behavior by checking whether the underlying stream is our UnixConsoleStream and it's wrapping a character device.
        /// </summary>
        public static bool IsInputRedirectedCore()
        {
            return IsHandleRedirected(Interop.Sys.FileDescriptors.STDIN_FILENO);
        }

        /// <summary>Gets whether Console.Out is redirected.
        /// We approximate the behavior by checking whether the underlying stream is our UnixConsoleStream and it's wrapping a character device.
        /// </summary>
        public static bool IsOutputRedirectedCore()
        {
            return IsHandleRedirected(Interop.Sys.FileDescriptors.STDOUT_FILENO);
        }

        /// <summary>Gets whether Console.Error is redirected.
        /// We approximate the behavior by checking whether the underlying stream is our UnixConsoleStream and it's wrapping a character device.
        /// </summary>
        public static bool IsErrorRedirectedCore()
        {
            return IsHandleRedirected(Interop.Sys.FileDescriptors.STDERR_FILENO);
        }

        /// <summary>Creates an encoding from the current environment.</summary>
        /// <returns>The encoding.</returns>
        private static Encoding GetConsoleEncoding()
        {
            Encoding? enc = EncodingHelper.GetEncodingFromCharset();
            return enc != null ?
                enc.RemovePreamble() :
                Encoding.Default;
        }

#pragma warning disable IDE0060
        public static void Beep(int frequency, int duration)
        {
            throw new PlatformNotSupportedException();
        }

        public static void MoveBufferArea(int sourceLeft, int sourceTop, int sourceWidth, int sourceHeight, int targetLeft, int targetTop)
        {
            throw new PlatformNotSupportedException();
        }

        public static void MoveBufferArea(int sourceLeft, int sourceTop, int sourceWidth, int sourceHeight, int targetLeft, int targetTop, char sourceChar, ConsoleColor sourceForeColor, ConsoleColor sourceBackColor)
        {
            throw new PlatformNotSupportedException();
        }

        public static void SetBufferSize(int width, int height)
        {
            throw new PlatformNotSupportedException();
        }

        public static void SetConsoleInputEncoding(Encoding enc)
        {
            // No-op.
            // There is no good way to set the terminal console encoding.
        }

        public static void SetConsoleOutputEncoding(Encoding enc)
        {
            // No-op.
            // There is no good way to set the terminal console encoding.
        }

        public static void SetWindowPosition(int left, int top)
        {
            throw new PlatformNotSupportedException();
        }

#pragma warning restore IDE0060

        /// <summary>
        /// Refreshes the foreground and background colors in use by the terminal by resetting
        /// the colors and then reissuing commands for both foreground and background, if necessary.
        /// Before doing so, the <paramref name="toChange"/> ref is changed to <paramref name="value"/>
        /// if <paramref name="value"/> is valid.
        /// </summary>
        private static void RefreshColors(ref ConsoleColor toChange, ConsoleColor value)
        {
            if (((int)value & ~0xF) != 0 && value != Console.UnknownColor)
            {
                throw new ArgumentException(SR.Arg_InvalidConsoleColor);
            }

            lock (Console.Out)
            {
                toChange = value; // toChange is either s_trackedForegroundColor or s_trackedBackgroundColor

                WriteResetColorString();

                if (s_trackedForegroundColor != Console.UnknownColor)
                {
                    WriteSetColorString(foreground: true, color: s_trackedForegroundColor);
                }

                if (s_trackedBackgroundColor != Console.UnknownColor)
                {
                    WriteSetColorString(foreground: false, color: s_trackedBackgroundColor);
                }
            }
        }

        /// <summary>Outputs the format string evaluated and parameterized with the color.</summary>
        /// <param name="foreground">true for foreground; false for background.</param>
        /// <param name="color">The color to store into the field and to use as an argument to the format string.</param>
        private static void WriteSetColorString(bool foreground, ConsoleColor color)
        {
            // Changing the color involves writing an ANSI character sequence out to the output stream.
            // We only want to do this if we know that sequence will be interpreted by the output.
            // rather than simply displayed visibly.
            if (!ConsoleUtils.EmitAnsiColorCodes)
            {
                return;
            }

            // See if we've already cached a format string for this foreground/background
            // and specific color choice.  If we have, just output that format string again.
            int fgbgIndex = foreground ? 0 : 1;
            int ccValue = (int)color;
            string evaluatedString = s_fgbgAndColorStrings[fgbgIndex, ccValue]; // benign race
            if (evaluatedString != null)
            {
                WriteTerminalAnsiColorString(evaluatedString);
                return;
            }

            // We haven't yet computed a format string.  Compute it, use it, then cache it.
            string? formatString = foreground ? TerminalFormatStringsInstance.Foreground : TerminalFormatStringsInstance.Background;
            if (!string.IsNullOrEmpty(formatString))
            {
                int maxColors = TerminalFormatStringsInstance.MaxColors; // often 8 or 16; 0 is invalid
                if (maxColors > 0)
                {
                    // The values of the ConsoleColor enums unfortunately don't map to the
                    // corresponding ANSI values.  We need to do the mapping manually.
                    // See http://en.wikipedia.org/wiki/ANSI_escape_code#Colors
                    ReadOnlySpan<byte> consoleColorToAnsiCode =
                    [
                        // Dark/Normal colors
                        0, // Black,
                        4, // DarkBlue,
                        2, // DarkGreen,
                        6, // DarkCyan,
                        1, // DarkRed,
                        5, // DarkMagenta,
                        3, // DarkYellow,
                        7, // Gray,

                        // Bright colors
                        8,  // DarkGray,
                        12, // Blue,
                        10, // Green,
                        14, // Cyan,
                        9,  // Red,
                        13, // Magenta,
                        11, // Yellow,
                        15  // White
                    ];

                    int ansiCode = consoleColorToAnsiCode[ccValue] % maxColors;
                    evaluatedString = TermInfo.ParameterizedStrings.Evaluate(formatString, ansiCode);

                    WriteTerminalAnsiColorString(evaluatedString);

                    s_fgbgAndColorStrings[fgbgIndex, ccValue] = evaluatedString; // benign race
                }
            }
        }

        /// <summary>Writes out the ANSI string to reset colors.</summary>
        private static void WriteResetColorString()
        {
            if (ConsoleUtils.EmitAnsiColorCodes)
            {
                WriteTerminalAnsiColorString(TerminalFormatStringsInstance.Reset);
            }
        }

        /// <summary>Cache of the format strings for foreground/background and ConsoleColor.</summary>
        private static readonly string[,] s_fgbgAndColorStrings = new string[2, 16]; // 2 == fg vs bg, 16 == ConsoleColor values

        /// <summary>Whether keypad_xmit has already been written out to the terminal.</summary>
        private static volatile bool s_initialized;

        /// <summary>Value used to indicate that a special character code isn't available.</summary>
        internal static byte s_posixDisableValue;
        /// <summary>Special control character code used to represent an erase (backspace).</summary>
        internal static byte s_veraseCharacter;
        /// <summary>Special control character that represents the end of a line.</summary>
        internal static byte s_veolCharacter;
        /// <summary>Special control character that represents the end of a line.</summary>
        internal static byte s_veol2Character;
        /// <summary>Special control character that represents the end of a file.</summary>
        internal static byte s_veofCharacter;

        /// <summary>Ensures that the console has been initialized for use.</summary>
        internal static void EnsureConsoleInitialized()
        {
            if (!s_initialized)
            {
                EnsureInitializedCore(); // factored out for inlinability
            }
        }

        /// <summary>Ensures that the console has been initialized for use.</summary>
        private static unsafe void EnsureInitializedCore()
        {
            lock (Console.Out) // ensure that writing the ANSI string and setting initialized to true are done atomically
            {
                if (!s_initialized)
                {
                    // Do this even when redirected to make CancelKeyPress works.
                    if (!Interop.Sys.InitializeTerminalAndSignalHandling())
                    {
                        throw new Win32Exception();
                    }
                    // InitializeTerminalAndSignalHandling will reset the terminal on a normal exit.
                    // This also resets it for termination due to an unhandled exception.
                    AppDomain.CurrentDomain.UnhandledException += (_, _) => { Interop.Sys.UninitializeTerminal(); };

                    s_terminalHandle = !Console.IsOutputRedirected ? Interop.Sys.FileDescriptors.STDOUT_FILENO :
                                       !Console.IsInputRedirected  ? Interop.Sys.FileDescriptors.STDIN_FILENO :
                                       null;

                    // Provide the native lib with the correct code from the terminfo to transition us into
                    // "application mode".  This will both transition it immediately, as well as allow
                    // the native lib later to handle signals that require re-entering the mode.
                    if (s_terminalHandle != null &&
                        TerminalFormatStringsInstance.KeypadXmit is string keypadXmit)
                    {
                        Interop.Sys.SetKeypadXmit(s_terminalHandle, keypadXmit);
                    }

                    if (!Console.IsInputRedirected)
                    {
                        // Register a callback for signals that may invalidate our cached terminal settings.
                        // This includes: SIGCONT, SIGCHLD, SIGWINCH.
                        Interop.Sys.SetTerminalInvalidationHandler(&InvalidateTerminalSettings);

                        // Load special control character codes used for input processing
                        const int NumControlCharacterNames = 4;
                        Interop.Sys.ControlCharacterNames* controlCharacterNames = stackalloc Interop.Sys.ControlCharacterNames[NumControlCharacterNames]
                        {
                            Interop.Sys.ControlCharacterNames.VERASE,
                            Interop.Sys.ControlCharacterNames.VEOL,
                            Interop.Sys.ControlCharacterNames.VEOL2,
                            Interop.Sys.ControlCharacterNames.VEOF
                        };
                        byte* controlCharacterValues = stackalloc byte[NumControlCharacterNames];
                        Interop.Sys.GetControlCharacters(controlCharacterNames, controlCharacterValues, NumControlCharacterNames, out s_posixDisableValue);
                        s_veraseCharacter = controlCharacterValues[0];
                        s_veolCharacter = controlCharacterValues[1];
                        s_veol2Character = controlCharacterValues[2];
                        s_veofCharacter = controlCharacterValues[3];
                    }

                    // Mark us as initialized
                    s_initialized = true;
                }
            }
        }

        /// <summary>Reads data from the file descriptor into the buffer.</summary>
        /// <param name="fd">The file descriptor.</param>
        /// <param name="buffer">The buffer to read into.</param>
        /// <returns>The number of bytes read, or an exception if there's an error.</returns>
        private static unsafe int Read(SafeFileHandle fd, Span<byte> buffer)
        {
            fixed (byte* bufPtr = buffer)
            {
                int result = Interop.CheckIo(Interop.Sys.Read(fd, bufPtr, buffer.Length));
                Debug.Assert(result <= buffer.Length);
                return result;
            }
        }

        internal static void WriteToTerminal(ReadOnlySpan<byte> buffer, SafeFileHandle? handle = null, bool mayChangeCursorPosition = true)
        {
            handle ??= s_terminalHandle;
            Debug.Assert(handle is not null);

            lock (Console.Out) // synchronize with other writers
            {
                Write(handle, buffer, mayChangeCursorPosition);
            }
        }

        internal static unsafe void WriteFromConsoleStream(SafeFileHandle fd, ReadOnlySpan<byte> buffer)
        {
            EnsureConsoleInitialized();

            lock (Console.Out) // synchronize with other writers
            {
                Write(fd, buffer);
            }
        }

        /// <summary>Writes data from the buffer into the file descriptor.</summary>
        /// <param name="fd">The file descriptor.</param>
        /// <param name="buffer">The buffer from which to write data.</param>
        /// <param name="mayChangeCursorPosition">Writing this buffer may change the cursor position.</param>
        private static unsafe void Write(SafeFileHandle fd, ReadOnlySpan<byte> buffer, bool mayChangeCursorPosition = true)
        {
            fixed (byte* p = buffer)
            {
                byte* bufPtr = p;
                int count = buffer.Length;
                while (count > 0)
                {
                    int cursorVersion = mayChangeCursorPosition ? Volatile.Read(ref s_cursorVersion) : -1;

                    int bytesWritten = Interop.Sys.Write(fd, bufPtr, count);
                    if (bytesWritten < 0)
                    {
                        Interop.ErrorInfo errorInfo = Interop.Sys.GetLastErrorInfo();
                        if (errorInfo.Error == Interop.Error.EPIPE)
                        {
                            // Broken pipe... likely due to being redirected to a program
                            // that ended, so simply pretend we were successful.
                            return;
                        }
                        else if (errorInfo.Error == Interop.Error.EAGAIN) // aka EWOULDBLOCK
                        {
                            // May happen if the file handle is configured as non-blocking.
                            // In that case, we need to wait to be able to write and then
                            // try again. We poll, but don't actually care about the result,
                            // only the blocking behavior, and thus ignore any poll errors
                            // and loop around to do another write (which may correctly fail
                            // if something else has gone wrong).
                            Interop.Sys.Poll(fd, Interop.PollEvents.POLLOUT, Timeout.Infinite, out Interop.PollEvents triggered);
                            continue;
                        }
                        else
                        {
                            // Something else... fail.
                            throw Interop.GetExceptionForIoErrno(errorInfo);
                        }
                    }
                    else
                    {
                        if (mayChangeCursorPosition)
                        {
                            UpdatedCachedCursorPosition(bufPtr, bytesWritten, cursorVersion);
                        }
                    }

                    count -= bytesWritten;
                    bufPtr += bytesWritten;
                }
            }
        }

        private static unsafe void UpdatedCachedCursorPosition(byte* bufPtr, int count, int cursorVersion)
        {
            lock (Console.Out)
            {
                int left, top;
                if (cursorVersion != s_cursorVersion               ||  // the cursor was changed during the write by another operation
                    !TryGetCachedCursorPosition(out left, out top) ||  // we don't have a cursor position
                    count > InteractiveBufferSize)                     // limit the amount of bytes we are willing to inspect
                {
                    InvalidateCachedCursorPosition();
                    return;
                }

                GetWindowSize(out int width, out int height);

                for (int i = 0; i < count; i++)
                {
                    byte c = bufPtr[i];
                    if (c < 127 && c >= 32) // ASCII/UTF-8 characters that take up a single position
                    {
                        left++;

                        // After printing in the last column, setting CursorLeft is expected to
                        // place the cursor back in that same row.
                        // Invalidate the cursor position rather than moving it to the next row.
                        if (left >= width)
                        {
                            InvalidateCachedCursorPosition();
                            return;
                        }
                    }
                    else if (c == (byte)'\r')
                    {
                        left = 0;
                    }
                    else if (c == (byte)'\n')
                    {
                        left = 0;
                        top++;

                        if (top >= height)
                        {
                            top = height - 1;
                        }
                    }
                    else if (c == (byte)'\b')
                    {
                        if (left > 0)
                        {
                            left--;
                        }
                    }
                    else
                    {
                        InvalidateCachedCursorPosition();
                        return;
                    }
                }

                // We pass cursorVersion because it may have changed the earlier check by calling GetWindowSize.
                SetCachedCursorPosition(left, top, cursorVersion);
            }
        }

        private static void CheckTerminalSettingsInvalidated()
        {
            // Register for signals that invalidate cached values.
            EnsureConsoleInitialized();

            bool invalidateSettings = Interlocked.CompareExchange(ref s_invalidateCachedSettings, 0, 1) == 1;
            if (invalidateSettings)
            {
                InvalidateCachedCursorPosition();
                s_windowWidth = -1;
            }
        }

        [UnmanagedCallersOnly]
        private static void InvalidateTerminalSettings()
        {
            Volatile.Write(ref s_invalidateCachedSettings, 1);
        }

        // Ansi colors are enabled when stdout is a terminal or when
        // DOTNET_SYSTEM_CONSOLE_ALLOW_ANSI_COLOR_REDIRECTION is set.
        // In both cases, they are written to stdout.
        internal static void WriteTerminalAnsiColorString(string? value)
            => WriteTerminalAnsiString(value, Interop.Sys.FileDescriptors.STDOUT_FILENO, mayChangeCursorPosition: false);

        /// <summary>Writes a terminfo-based ANSI escape string to stdout.</summary>
        /// <param name="value">The string to write.</param>
        /// <param name="handle">Handle to use instead of s_terminalHandle.</param>
        /// <param name="mayChangeCursorPosition">Writing this value may change the cursor position.</param>
        internal static void WriteTerminalAnsiString(string? value, SafeFileHandle? handle = null, bool mayChangeCursorPosition = true)
        {
            if (string.IsNullOrEmpty(value))
                return;

            scoped Span<byte> data;
            if (value.Length <= 256) // except for extremely rare cases, ANSI escape strings are very short
            {
                data = stackalloc byte[Encoding.UTF8.GetMaxByteCount(value.Length)];
                int bytesToWrite = Encoding.UTF8.GetBytes(value, data);
                data = data.Slice(0, bytesToWrite);
            }
            else
            {
                data = Encoding.UTF8.GetBytes(value);
            }

            EnsureConsoleInitialized();
            WriteToTerminal(data, handle, mayChangeCursorPosition);
        }
    }
}
