using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace JRunner.Cli.Tests;

/// <summary>
/// Runs the real apphost with a PTY on fd 0 and independent output pipes.
/// All descriptor rebinding happens inside posix_spawn, never in the test host.
/// </summary>
internal sealed class CpuKeyPtyProcess : IAsyncDisposable
{
    internal const uint EchoFlags = 0x00000008 | 0x00000040; // ECHO | ECHONL
    private const uint CanonicalInput = 0x00000002;
    private const int LocalFlagsOffset = 12;
    private const int ControlCharactersOffset = 17;
    // Linux x64/arm64 glibc termios: four flags, c_line, 32 control chars,
    // padding, and both speed fields. Compare the entire initialized snapshot.
    private const int NativeTermiosLength = 60;
    private const int ReadWrite = 2;
    private const int NoControllingTerminal = 0x100;
    private const int Nonblocking = 0x800;
    private const int CloseOnExec = 0x80000;
    private const int GetFileStatusFlags = 3;
    private const int SetFileStatusFlags = 4;
    private const int InterruptedSystemCall = 4;
    private const int WouldBlock = 11;
    private const int NoSuchProcess = 3;
    private const int NoHang = 1;
    private const int InterruptSignal = 2;
    private const int KillSignal = 9;
    private const short PollInput = 0x001;
    private const short PollOutput = 0x004;
    private const short PollHangup = 0x010;
    private const short PollFailure = 0x008 | 0x020;
    private const short SpawnSetProcessGroupFlag = 0x02;
    private const short SpawnSetSignalDefaultFlag = 0x04;
    private const short SpawnSetSignalMaskFlag = 0x08;
    private static readonly TimeSpan CaseTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);
    private static readonly Lazy<string?> SkipReason = new(ProbePseudoTerminal);

    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly byte[] _readBuffer = new byte[4096];
    private readonly BoundedCapture _standardOutput = new();
    private readonly BoundedCapture _standardError = new();
    private readonly BoundedCapture _terminalOutput = new();
    private int _master = -1;
    private int _observerSlave = -1;
    private int _outputReader = -1;
    private int _errorReader = -1;
    private int _processId = -1;
    private int? _exitStatus;
    private bool _outputEnded;
    private bool _errorEnded;
    private bool _terminalEnded;
    private bool _outputFlowStopped;
    private bool _disposed;

    private CpuKeyPtyProcess()
    {
    }

    internal byte[] OriginalAttributes { get; private set; } = [];
    internal ReadOnlyMemory<byte> StandardOutput => _standardOutput.Bytes;
    internal ReadOnlyMemory<byte> StandardError => _standardError.Bytes;
    internal ReadOnlyMemory<byte> TerminalOutput => _terminalOutput.Bytes;

    internal int ExitCode => _exitStatus is int status
        ? (status & 0x7f) == 0 ? (status >> 8) & 0xff : 128 + (status & 0x7f)
        : throw new InvalidOperationException("The CPU-key PTY child has not exited.");

    internal static string? GetSkipReason() => SkipReason.Value;

    internal static CpuKeyPtyProcess Start(IReadOnlyList<string> arguments, bool canonicalInput = true)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("CPU-key subprocess PTY coverage requires Linux.");
        }

        var process = new CpuKeyPtyProcess();
        int outputWriter = -1;
        int errorWriter = -1;
        try
        {
            (process._master, process._observerSlave) = OpenPseudoTerminal();
            (process._outputReader, outputWriter) = CreateOutputPipe();
            (process._errorReader, errorWriter) = CreateOutputPipe();
            process.SeedAttributes(canonicalInput);
            process.OriginalAttributes = process.CaptureAttributes();
            process._processId = SpawnAppHost(arguments, process._observerSlave, outputWriter, errorWriter);
            return process;
        }
        catch
        {
            // No operation that can fail follows a successful SpawnAppHost call.
            process.CloseDescriptors();
            process.ClearBuffers();
            throw;
        }
        finally
        {
            CloseDescriptor(ref outputWriter);
            CloseDescriptor(ref errorWriter);
        }
    }

    internal static uint ReadLocalFlags(byte[] attributes) => BitConverter.ToUInt32(attributes, LocalFlagsOffset);

    internal static byte[] WithoutEcho(byte[] attributes)
    {
        byte[] expected = (byte[])attributes.Clone();
        WriteFlags(expected, LocalFlagsOffset, ReadLocalFlags(expected) & ~EchoFlags);
        return expected;
    }

    internal byte[] CaptureAttributes()
    {
        byte[] attributes = new byte[NativeTermiosLength];
        while (GetTerminalAttributes(_observerSlave, attributes) != 0)
        {
            int error = Marshal.GetLastPInvokeError();
            CheckDeadline();
            if (error != InterruptedSystemCall)
            {
                throw NativeFailure("capture PTY attributes", error);
            }
        }

        return attributes;
    }

    internal async Task<byte[]> WaitForEchoDisabledAsync()
    {
        while (true)
        {
            CheckDeadline();
            Pump();
            byte[] attributes = CaptureAttributes();
            if ((ReadLocalFlags(attributes) & EchoFlags) == 0)
            {
                return attributes;
            }

            if (_exitStatus.HasValue)
            {
                throw new InvalidOperationException("The apphost exited before disabling CPU-key input echo.");
            }

            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    internal Task WriteInputAsync(ReadOnlyMemory<byte> input, bool requireSingleWrite = false) =>
        WriteInputCoreAsync(input, requireSingleWrite, allowExited: false);

    internal Task WriteAfterExitAsync(ReadOnlyMemory<byte> input)
    {
        if (!_exitStatus.HasValue)
        {
            throw new InvalidOperationException("The child must be reaped before writing an observer-only terminal line.");
        }

        return WriteInputCoreAsync(input, requireSingleWrite: true, allowExited: true);
    }

    private async Task WriteInputCoreAsync(ReadOnlyMemory<byte> input, bool requireSingleWrite, bool allowExited)
    {
        if (input.IsEmpty)
        {
            return;
        }

        if (!MemoryMarshal.TryGetArray(input, out ArraySegment<byte> segment))
        {
            throw new ArgumentException("PTY input must use an owned, zeroable byte array.", nameof(input));
        }

        GCHandle pin = GCHandle.Alloc(segment.Array!, GCHandleType.Pinned);
        try
        {
            IntPtr source = Marshal.UnsafeAddrOfPinnedArrayElement(segment.Array!, segment.Offset);
            int written = 0;
            while (written < input.Length)
            {
                CheckDeadline();
                Pump();
                if (_exitStatus.HasValue && !allowExited)
                {
                    throw new InvalidOperationException("The apphost exited before PTY input was written.");
                }

                if (IsReady(_master, PollOutput))
                {
                    nint result = Write(_master, IntPtr.Add(source, written), (nuint)(input.Length - written));
                    if (result > 0)
                    {
                        if (requireSingleWrite && result != input.Length)
                        {
                            throw new IOException("The short prequeued PTY batch was not accepted in one native write.");
                        }

                        written += checked((int)result);
                        continue;
                    }

                    int error = result < 0 ? Marshal.GetLastPInvokeError() : 0;
                    if (error is not InterruptedSystemCall and not WouldBlock)
                    {
                        throw NativeFailure("write PTY input", error);
                    }
                }

                await Task.Delay(10).ConfigureAwait(false);
            }
        }
        finally
        {
            pin.Free();
        }
    }

    internal void StopOutputFlow()
    {
        // Arm cleanup before attempting TCOOFF, including an ambiguous failure.
        _outputFlowStopped = true;
        SetOutputFlow(0, cleanup: false); // TCOOFF
    }

    internal void ResumeOutputFlow()
    {
        SetOutputFlow(1, cleanup: false); // TCOON
        _outputFlowStopped = false;
    }

    private void SetOutputFlow(int action, bool cleanup)
    {
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            if (cleanup)
            {
                if (Stopwatch.GetElapsedTime(started) >= CleanupTimeout)
                {
                    throw new TimeoutException("Resuming PTY output flow exceeded the finite cleanup deadline.");
                }
            }
            else
            {
                CheckDeadline();
            }

            // TCOOFF/TCOON only change flow state; neither drains output nor
            // writes terminal bytes, and neither modifies the termios snapshot.
            if (ControlOutputFlow(_observerSlave, action) == 0)
            {
                return;
            }

            int error = Marshal.GetLastPInvokeError();
            if (error != InterruptedSystemCall)
            {
                throw NativeFailure(action == 0 ? "stop PTY output flow" : "resume PTY output flow", error);
            }
        }
    }

    internal void SendInterrupt()
    {
        CheckDeadline();
        if (Kill(_processId, InterruptSignal) != 0)
        {
            throw NativeFailure("send SIGINT to the apphost", Marshal.GetLastPInvokeError());
        }
    }

    internal async Task WaitForExitAsync(TimeSpan? maximumWait = null)
    {
        var wait = Stopwatch.StartNew();
        while (true)
        {
            CheckDeadline();
            Pump();
            if (_exitStatus.HasValue && _outputEnded && _errorEnded)
            {
                // Reaping closes the race with a final child write between the
                // pump's PTY drain and waitpid; the observer slave stays open.
                Drain(_master, _terminalOutput, ref _terminalEnded);
                return;
            }

            if (maximumWait.HasValue && wait.Elapsed >= maximumWait.Value)
            {
                throw new TimeoutException("The CPU-key apphost did not exit within the bounded signal deadline.");
            }

            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    internal async Task<byte[]> ReadRemainingSlaveInputAsync(int length)
    {
        if (!_exitStatus.HasValue)
        {
            throw new InvalidOperationException("The child must be reaped before observing its remaining stdin bytes.");
        }

        byte[] remaining = new byte[length];
        try
        {
            int count = 0;
            while (count < remaining.Length)
            {
                CheckDeadline();
                if (IsReady(_observerSlave, PollInput))
                {
                    // With the only reader reaped, canonical readiness cannot be stolen.
                    // Read one byte so that the observer itself does not read ahead either.
                    nint result = Read(_observerSlave, _readBuffer, 1);
                    if (result == 1)
                    {
                        remaining[count++] = _readBuffer[0];
                        CryptographicOperations.ZeroMemory(_readBuffer);
                        continue;
                    }

                    int error = result < 0 ? Marshal.GetLastPInvokeError() : 0;
                    if (error != InterruptedSystemCall)
                    {
                        throw NativeFailure("read remaining PTY slave input", error);
                    }
                }

                await Task.Delay(10).ConfigureAwait(false);
            }

            // Observer reads can follow a deliberate post-exit write. Capture its
            // legitimate restored echo too, so leak checks cover that interval.
            Drain(_master, _terminalOutput, ref _terminalEnded);
            return remaining;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(remaining);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(_readBuffer);
        }
    }

    internal bool HasRemainingSlaveInput()
    {
        if (!_exitStatus.HasValue)
        {
            throw new InvalidOperationException("The child must be reaped before checking remaining stdin readiness.");
        }

        CheckDeadline();
        return IsReady(_observerSlave, PollInput);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_processId > 0 && !_exitStatus.HasValue)
            {
                // The child gets its own process group, so failure cleanup cannot signal
                // the test host and also terminates any unexpected inherited descendants.
                if (Kill(-_processId, KillSignal) != 0 && Marshal.GetLastPInvokeError() != NoSuchProcess)
                {
                    throw NativeFailure("terminate the PTY child process group", Marshal.GetLastPInvokeError());
                }

                var cleanup = Stopwatch.StartNew();
                while (!_exitStatus.HasValue)
                {
                    TryReap();
                    if (cleanup.Elapsed >= CleanupTimeout && !_exitStatus.HasValue)
                    {
                        throw new TimeoutException("The terminated CPU-key PTY child could not be reaped.");
                    }

                    if (!_exitStatus.HasValue)
                    {
                        await Task.Delay(10).ConfigureAwait(false);
                    }
                }
            }
        }
        finally
        {
            try
            {
                if (_outputFlowStopped)
                {
                    SetOutputFlow(1, cleanup: true);
                    _outputFlowStopped = false;
                }
            }
            finally
            {
                CloseDescriptors();
                ClearBuffers();
            }
        }
    }

    private void SeedAttributes(bool canonicalInput)
    {
        byte[] attributes = CaptureAttributes();
        // Preserve literal CRLF, and deliberately exercise non-default unrelated flags.
        WriteFlags(attributes, 0, (BitConverter.ToUInt32(attributes, 0) & ~(0x40U | 0x80U | 0x100U)) | 0x4U | 0x1000U);
        WriteFlags(attributes, 4, BitConverter.ToUInt32(attributes, 4) | 0x1U | 0x20U);
        WriteFlags(attributes, 8, BitConverter.ToUInt32(attributes, 8) | 0x40U | 0x800U);
        uint local = ReadLocalFlags(attributes) | EchoFlags | 0x1U | 0x8000U; // ISIG | IEXTEN
        WriteFlags(attributes, LocalFlagsOffset, canonicalInput ? local | CanonicalInput : local & ~CanonicalInput);
        attributes[ControlCharactersOffset + 2] = 0x08; // VERASE
        attributes[ControlCharactersOffset + 3] = 0x18; // VKILL
        attributes[ControlCharactersOffset + 5] = canonicalInput ? (byte)7 : (byte)0; // VTIME
        attributes[ControlCharactersOffset + 6] = canonicalInput ? (byte)3 : (byte)1; // VMIN
        attributes[ControlCharactersOffset + 11] = 0x1e; // VEOL
        attributes[ControlCharactersOffset + 16] = 0x1f; // VEOL2
        const uint baud19200 = 14;
        CheckZero(SetInputSpeed(attributes, baud19200), "set the initial PTY input speed");
        CheckZero(SetOutputSpeed(attributes, baud19200), "set the initial PTY output speed");
        while (SetTerminalAttributes(_observerSlave, 0, attributes) != 0)
        {
            int error = Marshal.GetLastPInvokeError();
            CheckDeadline();
            if (error != InterruptedSystemCall)
            {
                throw NativeFailure("seed PTY attributes", error);
            }
        }
    }

    private static void WriteFlags(byte[] attributes, int offset, uint flags)
    {
        _ = BitConverter.TryWriteBytes(attributes.AsSpan(offset, sizeof(uint)), flags);
    }

    private void Pump()
    {
        Drain(_outputReader, _standardOutput, ref _outputEnded);
        Drain(_errorReader, _standardError, ref _errorEnded);
        Drain(_master, _terminalOutput, ref _terminalEnded);
        TryReap();
    }

    private void Drain(int descriptor, BoundedCapture capture, ref bool ended)
    {
        if (ended)
        {
            return;
        }

        try
        {
            while (IsReady(descriptor, PollInput))
            {
                CheckDeadline();
                nint result = Read(descriptor, _readBuffer, (nuint)_readBuffer.Length);
                if (result > 0)
                {
                    capture.Append(_readBuffer.AsSpan(0, checked((int)result)));
                    CryptographicOperations.ZeroMemory(_readBuffer);
                    continue;
                }

                if (result == 0)
                {
                    ended = true;
                    return;
                }

                int error = Marshal.GetLastPInvokeError();
                if (error == WouldBlock)
                {
                    return;
                }

                if (error != InterruptedSystemCall)
                {
                    throw NativeFailure("capture PTY subprocess output", error);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(_readBuffer);
        }
    }

    private static bool IsReady(int descriptor, short events)
    {
        var pollDescriptor = new PollDescriptor { Descriptor = descriptor, Events = events };
        int result = Poll(ref pollDescriptor, 1, 0);
        if (result < 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == InterruptedSystemCall)
            {
                return false;
            }

            throw NativeFailure("poll a PTY subprocess descriptor", error);
        }

        if ((pollDescriptor.ReturnedEvents & PollFailure) != 0)
        {
            throw new IOException("A CPU-key PTY subprocess descriptor failed.");
        }

        return result > 0 && (pollDescriptor.ReturnedEvents & (events | PollHangup)) != 0;
    }

    private void TryReap()
    {
        if (_processId <= 0 || _exitStatus.HasValue)
        {
            return;
        }

        int result = WaitPid(_processId, out int status, NoHang);
        if (result == _processId)
        {
            _exitStatus = status;
        }
        else if (result < 0 && Marshal.GetLastPInvokeError() != InterruptedSystemCall)
        {
            throw NativeFailure("reap the PTY apphost", Marshal.GetLastPInvokeError());
        }
    }

    private void CheckDeadline()
    {
        if (_elapsed.Elapsed >= CaseTimeout)
        {
            throw new TimeoutException("The CPU-key PTY subprocess case exceeded its finite deadline.");
        }
    }

    private void CloseDescriptors()
    {
        CloseDescriptor(ref _master);
        CloseDescriptor(ref _observerSlave);
        CloseDescriptor(ref _outputReader);
        CloseDescriptor(ref _errorReader);
    }

    private void ClearBuffers()
    {
        CryptographicOperations.ZeroMemory(_readBuffer);
        CryptographicOperations.ZeroMemory(OriginalAttributes);
        _standardOutput.Clear();
        _standardError.Clear();
        _terminalOutput.Clear();
    }

    private static (int Master, int Slave) OpenPseudoTerminal()
    {
        // Unlike openpty + fcntl, these descriptors are close-on-exec from creation.
        int master = PosixOpenPt(ReadWrite | NoControllingTerminal | CloseOnExec | Nonblocking);
        if (master < 0)
        {
            throw PtyOpenFailure(Marshal.GetLastPInvokeError());
        }

        int slave = -1;
        try
        {
            CheckPtyResult(GrantPt(master));
            CheckPtyResult(UnlockPt(master));
            byte[] slaveName = new byte[256];
            int error = PtsName(master, slaveName, (nuint)slaveName.Length);
            if (error != 0)
            {
                throw PtyOpenFailure(error);
            }

            int nameLength = Array.IndexOf(slaveName, (byte)0);
            if (nameLength <= 0)
            {
                throw new IOException("The CPU-key PTY allocator did not provide a slave name.");
            }

            slave = Open(Encoding.UTF8.GetString(slaveName, 0, nameLength), ReadWrite | NoControllingTerminal | CloseOnExec);
            if (slave < 0)
            {
                throw PtyOpenFailure(Marshal.GetLastPInvokeError());
            }

            if (master < 3 || slave < 3)
            {
                throw new IOException("The PTY test host must keep its standard descriptors open.");
            }

            return (master, slave);
        }
        catch
        {
            CloseDescriptor(ref master);
            CloseDescriptor(ref slave);
            throw;
        }
    }

    private static (int Reader, int Writer) CreateOutputPipe()
    {
        int[] descriptors = [-1, -1];
        CheckZero(Pipe(descriptors, CloseOnExec), "create a CPU-key subprocess output pipe");
        int reader = descriptors[0];
        int writer = descriptors[1];
        try
        {
            int flags = Fcntl(reader, GetFileStatusFlags, 0);
            if (flags < 0)
            {
                throw NativeFailure("read pipe status flags", Marshal.GetLastPInvokeError());
            }

            // Only the parent's reader is nonblocking. Child stdout/stderr stay blocking.
            CheckZero(Fcntl(reader, SetFileStatusFlags, flags | Nonblocking), "set parent pipe reader nonblocking");
            return (reader, writer);
        }
        catch
        {
            CloseDescriptor(ref reader);
            CloseDescriptor(ref writer);
            throw;
        }
    }

    private static int SpawnAppHost(IReadOnlyList<string> arguments, int slave, int outputWriter, int errorWriter)
    {
        string appHost = Path.Combine(AppContext.BaseDirectory, "jrunner");
        using var argv = new NativeStringVector(new[] { appHost }.Concat(arguments));
        using var environment = new NativeStringVector(
            Environment.GetEnvironmentVariables().Cast<DictionaryEntry>().Select(entry => $"{entry.Key}={entry.Value}"));
        // Opaque glibc storage, deliberately larger than the grounded 80-byte file
        // actions and 336-byte spawn attributes. Only libc interprets their layout.
        IntPtr actions = IntPtr.Zero;
        IntPtr attributes = IntPtr.Zero;
        IntPtr signals = IntPtr.Zero;
        bool actionsInitialized = false;
        bool attributesInitialized = false;
        try
        {
            actions = Marshal.AllocHGlobal(512);
            attributes = Marshal.AllocHGlobal(512);
            signals = Marshal.AllocHGlobal(128); // glibc sigset_t
            CheckSpawnResult(SpawnActionsInitialize(actions), "initialize spawn file actions");
            actionsInitialized = true;
            CheckSpawnResult(SpawnAttributesInitialize(attributes), "initialize spawn attributes");
            attributesInitialized = true;
            CheckSpawnResult(SpawnAddDup2(actions, slave, 0), "attach the PTY to child fd 0");
            CheckSpawnResult(SpawnAddDup2(actions, outputWriter, 1), "attach child stdout capture");
            CheckSpawnResult(SpawnAddDup2(actions, errorWriter, 2), "attach child stderr capture");
            CheckSpawnResult(SpawnCloseFrom(actions, 3), "close every nonstandard child descriptor");
            CheckZero(EmptySignalSet(signals), "initialize the child signal mask");
            CheckSpawnResult(SpawnSetSignalMask(attributes, signals), "unblock child signals");
            CheckZero(AddSignal(signals, InterruptSignal), "initialize default SIGINT handling");
            CheckSpawnResult(SpawnSetSignalDefault(attributes, signals), "reset inherited SIGINT disposition");
            CheckSpawnResult(SpawnSetProcessGroup(attributes, 0), "isolate the child process group");
            CheckSpawnResult(SpawnSetFlags(attributes, SpawnSetProcessGroupFlag | SpawnSetSignalDefaultFlag | SpawnSetSignalMaskFlag), "set spawn attributes");
            CheckSpawnResult(Spawn(out int processId, appHost, actions, attributes, argv.Pointer, environment.Pointer), "spawn the jrunner apphost");
            return processId;
        }
        finally
        {
            if (actionsInitialized)
            {
                _ = SpawnActionsDestroy(actions);
            }

            if (attributesInitialized)
            {
                _ = SpawnAttributesDestroy(attributes);
            }

            Marshal.FreeHGlobal(signals);
            Marshal.FreeHGlobal(attributes);
            Marshal.FreeHGlobal(actions);
        }
    }

    private static string? ProbePseudoTerminal()
    {
        if (!OperatingSystem.IsLinux())
        {
            return "Requires a real Linux CPU-key pseudo-terminal.";
        }

        try
        {
            (int master, int slave) = OpenPseudoTerminal();
            CloseDescriptor(ref master);
            CloseDescriptor(ref slave);
            return null;
        }
        catch (PtyUnavailableException exception)
        {
            return exception.Message;
        }
    }

    private static void CheckPtyResult(int result)
    {
        if (result != 0)
        {
            throw PtyOpenFailure(Marshal.GetLastPInvokeError());
        }
    }

    private static IOException PtyOpenFailure(int error) => error is 2 or 6 or 19 or 38
        ? new PtyUnavailableException($"Linux pseudo-terminals are unavailable (errno {error}).")
        : NativeFailure("allocate a CPU-key pseudo-terminal", error);

    private static void CheckZero(int result, string operation)
    {
        if (result != 0)
        {
            throw NativeFailure(operation, Marshal.GetLastPInvokeError());
        }
    }

    private static void CheckSpawnResult(int error, string operation)
    {
        if (error != 0)
        {
            throw NativeFailure(operation, error);
        }
    }

    private static IOException NativeFailure(string operation, int error) => new($"Unable to {operation} (errno {error}).");

    private static void CloseDescriptor(ref int descriptor)
    {
        if (descriptor >= 0)
        {
            // On Linux close releases the descriptor even when reporting EINTR.
            _ = Close(descriptor);
            descriptor = -1;
        }
    }

    private sealed class PtyUnavailableException(string message) : IOException(message)
    {
    }

    private sealed class BoundedCapture
    {
        private readonly byte[] _buffer = new byte[128 * 1024];
        private int _length;
        internal ReadOnlyMemory<byte> Bytes => _buffer.AsMemory(0, _length);

        internal void Append(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length > _buffer.Length - _length)
            {
                throw new IOException("CPU-key subprocess capture exceeded its finite byte limit.");
            }

            bytes.CopyTo(_buffer.AsSpan(_length));
            _length += bytes.Length;
        }

        internal void Clear()
        {
            CryptographicOperations.ZeroMemory(_buffer);
            _length = 0;
        }
    }

    private sealed class NativeStringVector : IDisposable
    {
        private static readonly byte[] Zeros = new byte[4096];
        private readonly List<(IntPtr Pointer, int Length)> _strings = [];
        internal IntPtr Pointer { get; private set; }

        internal NativeStringVector(IEnumerable<string> values)
        {
            try
            {
                foreach (string value in values)
                {
                    byte[] bytes = new byte[Encoding.UTF8.GetByteCount(value) + 1];
                    try
                    {
                        _ = Encoding.UTF8.GetBytes(value, bytes.AsSpan(0, bytes.Length - 1));
                        IntPtr pointer = Marshal.AllocHGlobal(bytes.Length);
                        _strings.Add((pointer, bytes.Length));
                        Marshal.Copy(bytes, 0, pointer, bytes.Length);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(bytes);
                    }
                }

                Pointer = Marshal.AllocHGlobal(checked((_strings.Count + 1) * IntPtr.Size));
                for (int index = 0; index < _strings.Count; index++)
                {
                    Marshal.WriteIntPtr(Pointer, index * IntPtr.Size, _strings[index].Pointer);
                }

                Marshal.WriteIntPtr(Pointer, _strings.Count * IntPtr.Size, IntPtr.Zero);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            foreach ((IntPtr pointer, int length) in _strings)
            {
                for (int offset = 0; offset < length; offset += Zeros.Length)
                {
                    Marshal.Copy(Zeros, 0, IntPtr.Add(pointer, offset), Math.Min(Zeros.Length, length - offset));
                }

                Marshal.FreeHGlobal(pointer);
            }

            _strings.Clear();
            Marshal.FreeHGlobal(Pointer);
            Pointer = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollDescriptor
    {
        internal int Descriptor;
        internal short Events;
        internal short ReturnedEvents;
    }

    [DllImport("libc", EntryPoint = "posix_openpt", SetLastError = true)]
    private static extern int PosixOpenPt(int flags);
    [DllImport("libc", EntryPoint = "grantpt", SetLastError = true)]
    private static extern int GrantPt(int descriptor);
    [DllImport("libc", EntryPoint = "unlockpt", SetLastError = true)]
    private static extern int UnlockPt(int descriptor);
    [DllImport("libc", EntryPoint = "ptsname_r")]
    private static extern int PtsName(int descriptor, [Out] byte[] name, nuint length);
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "pipe2", SetLastError = true)]
    private static extern int Pipe([Out] int[] descriptors, int flags);
    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int Fcntl(int descriptor, int command, int argument);
    [DllImport("libc", EntryPoint = "close")]
    private static extern int Close(int descriptor);
    [DllImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static extern int Poll(ref PollDescriptor descriptor, nuint count, int timeout);
    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern nint Read(int descriptor, [Out] byte[] bytes, nuint length);
    [DllImport("libc", EntryPoint = "write", SetLastError = true)]
    private static extern nint Write(int descriptor, IntPtr bytes, nuint length);
    [DllImport("libc", EntryPoint = "tcgetattr", SetLastError = true)]
    private static extern int GetTerminalAttributes(int descriptor, [Out] byte[] attributes);
    [DllImport("libc", EntryPoint = "tcsetattr", SetLastError = true)]
    private static extern int SetTerminalAttributes(int descriptor, int action, [In] byte[] attributes);
    [DllImport("libc", EntryPoint = "tcflow", SetLastError = true)]
    private static extern int ControlOutputFlow(int descriptor, int action);
    [DllImport("libc", EntryPoint = "cfsetispeed", SetLastError = true)]
    private static extern int SetInputSpeed([In, Out] byte[] attributes, uint speed);
    [DllImport("libc", EntryPoint = "cfsetospeed", SetLastError = true)]
    private static extern int SetOutputSpeed([In, Out] byte[] attributes, uint speed);
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int processId, int signal);
    [DllImport("libc", EntryPoint = "waitpid", SetLastError = true)]
    private static extern int WaitPid(int processId, out int status, int options);
    [DllImport("libc", EntryPoint = "posix_spawn")]
    private static extern int Spawn(out int processId, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr actions, IntPtr attributes, IntPtr argv, IntPtr environment);
    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_init")]
    private static extern int SpawnActionsInitialize(IntPtr actions);
    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_destroy")]
    private static extern int SpawnActionsDestroy(IntPtr actions);
    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_adddup2")]
    private static extern int SpawnAddDup2(IntPtr actions, int descriptor, int destination);
    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_addclosefrom_np")]
    private static extern int SpawnCloseFrom(IntPtr actions, int firstDescriptor);
    [DllImport("libc", EntryPoint = "posix_spawnattr_init")]
    private static extern int SpawnAttributesInitialize(IntPtr attributes);
    [DllImport("libc", EntryPoint = "posix_spawnattr_destroy")]
    private static extern int SpawnAttributesDestroy(IntPtr attributes);
    [DllImport("libc", EntryPoint = "posix_spawnattr_setsigmask")]
    private static extern int SpawnSetSignalMask(IntPtr attributes, IntPtr mask);
    [DllImport("libc", EntryPoint = "posix_spawnattr_setsigdefault")]
    private static extern int SpawnSetSignalDefault(IntPtr attributes, IntPtr signals);
    [DllImport("libc", EntryPoint = "posix_spawnattr_setpgroup")]
    private static extern int SpawnSetProcessGroup(IntPtr attributes, int group);
    [DllImport("libc", EntryPoint = "posix_spawnattr_setflags")]
    private static extern int SpawnSetFlags(IntPtr attributes, short flags);
    [DllImport("libc", EntryPoint = "sigemptyset", SetLastError = true)]
    private static extern int EmptySignalSet(IntPtr signals);
    [DllImport("libc", EntryPoint = "sigaddset", SetLastError = true)]
    private static extern int AddSignal(IntPtr signals, int signal);
}

internal sealed class CpuKeyPtyLinuxFactAttribute : FactAttribute
{
    public CpuKeyPtyLinuxFactAttribute() => Skip = CpuKeyPtyProcess.GetSkipReason();
}

internal sealed class CpuKeyPtyLinuxTheoryAttribute : TheoryAttribute
{
    public CpuKeyPtyLinuxTheoryAttribute() => Skip = CpuKeyPtyProcess.GetSkipReason();
}
