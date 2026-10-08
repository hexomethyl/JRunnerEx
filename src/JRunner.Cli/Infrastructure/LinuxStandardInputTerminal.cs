using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace JRunner.Cli.Infrastructure;

/// <summary>
/// Saves and restores Linux standard-input terminal attributes and reads secret input with cancellable polls.
/// </summary>
internal sealed class LinuxStandardInputTerminal : IStandardInputTerminal
{
    private const int StandardInputFileDescriptor = 0;
    private const int TerminalLocalFlagsOffset = sizeof(uint) * 3;
    // Linux x64 and arm64 termios is four tcflag_t fields, c_line, NCCS (32), and two speed_t fields.
    private const int NativeTermiosByteLength = 60;
    private const int ApplyAttributesImmediately = 0;
    private const int FlushPendingInput = 0; // TCIFLUSH.
    private const int InterruptedSystemCallError = 4;
    private const int WouldBlockError = 11;
    private const int NotATerminalError = 25;
    private const uint EchoMask = 0x00000008 | 0x00000040; // ECHO | ECHONL.
    private const int InputPollTimeoutMilliseconds = 100;
    private const short PollInput = 0x0001;
    private const short PollError = 0x0008;
    private const short PollHangup = 0x0010;
    private const short PollInvalidDescriptor = 0x0020;

    private readonly SemaphoreSlim _echoGate = new(initialCount: 1, maxCount: 1);

    internal static LinuxStandardInputTerminal Instance { get; } = new();

    private LinuxStandardInputTerminal()
    {
    }

    /// <inheritdoc />
    public bool IsTerminal
    {
        get
        {
            return OperatingSystem.IsLinux() && IsStandardInputTerminal();
        }
    }

    /// <summary>
    /// Determines whether Linux standard input is a terminal without silently masking probe failures.
    /// </summary>
    private static bool IsStandardInputTerminal()
    {
        while (true)
        {
            if (IsATerminal(StandardInputFileDescriptor) == 1)
            {
                return true;
            }

            int error = Marshal.GetLastPInvokeError();
            if (error == InterruptedSystemCallError)
            {
                continue;
            }

            if (error == NotATerminalError)
            {
                return false;
            }

            throw TerminalIoFailure();
        }
    }

    /// <inheritdoc />
    public async ValueTask<IStandardInputEchoScope> DisableEchoAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsTerminal)
        {
            throw new InvalidOperationException("Standard input is not an interactive Linux terminal.");
        }

        if (!BitConverter.IsLittleEndian
            || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
        {
            throw TerminalIoFailure();
        }

        await _echoGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        byte[]? originalAttributes = null;
        bool attributesRead = false;
        bool terminalWriteAttempted = false;
        bool handedOff = false;
        try
        {
            if (!IsTerminal)
            {
                throw new InvalidOperationException("Standard input is not an interactive Linux terminal.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            originalAttributes = GC.AllocateUninitializedArray<byte>(NativeTermiosByteLength);
            if (!TryGetTerminalAttributes(originalAttributes))
            {
                throw TerminalIoFailure();
            }

            attributesRead = true;
            uint localFlags = BinaryPrimitives.ReadUInt32LittleEndian(
                originalAttributes.AsSpan(TerminalLocalFlagsOffset, sizeof(uint)));
            try
            {
                BinaryPrimitives.WriteUInt32LittleEndian(
                    originalAttributes.AsSpan(TerminalLocalFlagsOffset, sizeof(uint)),
                    localFlags & ~EchoMask);

                terminalWriteAttempted = true;
                if (!TrySetTerminalAttributes(originalAttributes))
                {
                    throw TerminalIoFailure();
                }
            }
            finally
            {
                // Keep the saved snapshot exact even if applying the temporary echo mask fails.
                BinaryPrimitives.WriteUInt32LittleEndian(
                    originalAttributes.AsSpan(TerminalLocalFlagsOffset, sizeof(uint)),
                    localFlags);
            }

            var scope = new TerminalEchoScope(originalAttributes, _echoGate);
            handedOff = true;
            return scope;
        }
        finally
        {
            if (!handedOff)
            {
                try
                {
                    if (originalAttributes is not null && attributesRead && terminalWriteAttempted)
                    {
                        RestoreAttributesBestEffort(originalAttributes);
                    }
                }
                finally
                {
                    if (originalAttributes is not null)
                    {
                        CryptographicOperations.ZeroMemory(originalAttributes);
                    }

                    _echoGate.Release();
                }
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<int> ReadCharacterAsync(CancellationToken cancellationToken)
    {
        Span<byte> readBuffer = stackalloc byte[1];
        try
        {
            var descriptor = new PollFileDescriptor
            {
                FileDescriptor = StandardInputFileDescriptor,
                Events = PollInput,
            };
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                descriptor.ReturnedEvents = 0;
                int pollResult = Poll(ref descriptor, 1, InputPollTimeoutMilliseconds);
                int pollError = pollResult < 0 ? Marshal.GetLastPInvokeError() : 0;
                cancellationToken.ThrowIfCancellationRequested();
                if (pollResult < 0)
                {
                    if (pollError is InterruptedSystemCallError or WouldBlockError)
                    {
                        continue;
                    }

                    throw TerminalInputFailure();
                }

                if (pollResult == 0)
                {
                    continue;
                }

                if ((descriptor.ReturnedEvents & (PollError | PollInvalidDescriptor)) != 0)
                {
                    throw TerminalInputFailure();
                }

                if ((descriptor.ReturnedEvents & PollInput) == 0)
                {
                    if ((descriptor.ReturnedEvents & PollHangup) != 0)
                    {
                        return ValueTask.FromResult(-1);
                    }

                    // An unexpected ready event must not become a tight polling loop.
                    throw TerminalInputFailure();
                }

                // Read exactly one byte only after readiness; do not buffer a later line.
                nint readCount = Read(StandardInputFileDescriptor, ref readBuffer[0], 1);
                int readError = readCount < 0 ? Marshal.GetLastPInvokeError() : 0;
                cancellationToken.ThrowIfCancellationRequested();
                if (readCount < 0)
                {
                    if (readError is InterruptedSystemCallError or WouldBlockError)
                    {
                        continue;
                    }

                    throw TerminalInputFailure();
                }

                return ValueTask.FromResult(readCount == 0 ? -1 : (int)readBuffer[0]);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(readBuffer);
        }
    }

    private static void RestoreAttributesBestEffort(byte[] originalAttributes)
    {
        _ = TrySetTerminalAttributes(originalAttributes);
    }

    private static bool TryGetTerminalAttributes(byte[] attributes)
    {
        while (true)
        {
            if (GetTerminalAttributes(StandardInputFileDescriptor, attributes) == 0)
            {
                return true;
            }

            if (Marshal.GetLastPInvokeError() != InterruptedSystemCallError)
            {
                return false;
            }
        }
    }

    private static bool TrySetTerminalAttributes(byte[] attributes)
    {
        while (true)
        {
            if (SetTerminalAttributes(
                    StandardInputFileDescriptor,
                    ApplyAttributesImmediately,
                    attributes) == 0)
            {
                return true;
            }

            if (Marshal.GetLastPInvokeError() != InterruptedSystemCallError)
            {
                return false;
            }
        }
    }

    private static bool TryFlushStandardInput()
    {
        while (true)
        {
            if (FlushTerminalInput(StandardInputFileDescriptor, FlushPendingInput) == 0)
            {
                return true;
            }

            if (Marshal.GetLastPInvokeError() != InterruptedSystemCallError)
            {
                return false;
            }
        }
    }

    private static IOException TerminalIoFailure()
    {
        return new IOException("Terminal attributes could not be updated.");
    }

    private static IOException TerminalInputFailure()
    {
        return new IOException("Terminal input could not be read.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFileDescriptor
    {
        internal int FileDescriptor;
        internal short Events;
        internal short ReturnedEvents;
    }

    [DllImport("libc", EntryPoint = "poll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern int Poll([In, Out] ref PollFileDescriptor descriptor, nuint count, int timeoutMilliseconds);

    [DllImport("libc", EntryPoint = "read", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern nint Read(int fileDescriptor, [In, Out] ref byte destination, nuint count);

    [DllImport("libc", EntryPoint = "isatty", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern int IsATerminal(int fileDescriptor);

    [DllImport("libc", EntryPoint = "tcflush", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern int FlushTerminalInput(int fileDescriptor, int queueSelector);

    [DllImport("libc", EntryPoint = "tcgetattr", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern int GetTerminalAttributes(int fileDescriptor, [Out] byte[] attributes);

    [DllImport("libc", EntryPoint = "tcsetattr", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern int SetTerminalAttributes(
        int fileDescriptor,
        int optionalActions,
        [In] byte[] attributes);

    private sealed class TerminalEchoScope : IStandardInputEchoScope
    {
        private readonly SemaphoreSlim _echoGate;
        private byte[]? _originalAttributes;
        private bool _inputComplete;

        internal TerminalEchoScope(byte[] originalAttributes, SemaphoreSlim echoGate)
        {
            _originalAttributes = originalAttributes;
            _echoGate = echoGate;
        }

        public void CompleteInput()
        {
            _inputComplete = true;
        }

        public ValueTask DisposeAsync()
        {
            byte[]? originalAttributes = Interlocked.Exchange(ref _originalAttributes, null);
            if (originalAttributes is null)
            {
                return ValueTask.CompletedTask;
            }

            bool inputComplete = _inputComplete;

            try
            {
                try
                {
                    if (!inputComplete && !TryFlushStandardInput())
                    {
                        throw TerminalIoFailure();
                    }
                }
                finally
                {
                    // Never wait for terminal output, which may be flow-stopped during cancellation.
                    if (!TrySetTerminalAttributes(originalAttributes))
                    {
                        throw TerminalIoFailure();
                    }
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(originalAttributes);
                _echoGate.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
