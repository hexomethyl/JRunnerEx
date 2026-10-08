using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using JRunner.Core.Devices.PicoFlasher;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace JRunner.Cli.Tests;

internal sealed class PtyPicoFirmware : IAsyncDisposable
{
    private const int SlaveNameBufferLength = 256;
    private const int NativeTermiosSize = 60;
    private const int PollIntervalMilliseconds = 50;
    private const short PollIn = 0x001;
    private const short PollOut = 0x004;
    private const short PollErrorMask = 0x008 | 0x010 | 0x020;
    private const int InterruptedSystemCall = 4;
    private const int WouldBlock = 11;
    private const int GetFileStatusFlags = 3;
    private const int SetFileStatusFlags = 4;
    private const int Nonblocking = 0x800;
    private static readonly int[] FragmentSizes = [1, 2, 1, 7, 31, 127];

    private readonly FileStream _master;
    private readonly SafeFileHandle _masterHandle;
    private readonly SafeFileHandle _slaveHandle;
    private readonly CancellationTokenSource _lifetime;
    private readonly List<byte[]> _frames = [];
    private readonly object _lifecycleLock = new();
    private Task? _script;
    private Task? _disposal;
    private bool _disposed;

    private PtyPicoFirmware(
        FileStream master,
        SafeFileHandle slaveHandle,
        string slavePath,
        CancellationTokenSource lifetime)
    {
        _master = master;
        _masterHandle = master.SafeFileHandle;
        _slaveHandle = slaveHandle;
        _lifetime = lifetime;
        SlavePath = slavePath;
        LifetimeToken = lifetime.Token;
        Frames = _frames.AsReadOnly();
    }

    public string SlavePath { get; }

    public CancellationToken LifetimeToken { get; }

    public IReadOnlyList<byte[]> Frames { get; }

    public byte[] CaptureSlaveSettings()
    {
        ThrowIfCancellationRequested(CancellationToken.None);
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException("The settings snapshot requires the grounded Linux x64 glibc termios ABI.");
        }

        // glibc termios: four uint flags, a byte line discipline, 32 control
        // characters, three padding bytes, and two uint speed codes.
        byte[] settings = new byte[NativeTermiosSize];
        while (true)
        {
            ThrowIfCancellationRequested(CancellationToken.None);
            int result = GetTerminalAttributes(_slaveHandle, settings);
            int error = result < 0 ? Marshal.GetLastWin32Error() : 0;
            ThrowIfCancellationRequested(CancellationToken.None);
            if (result == 0)
            {
                return settings;
            }

            if (error != InterruptedSystemCall)
            {
                throw new IOException($"Unable to capture pseudo-terminal slave settings: {error}.");
            }
        }
    }

    public uint GetSlaveInputSpeedCode() => GetInputSpeed(CaptureSlaveSettings());

    public uint GetSlaveOutputSpeedCode() => GetOutputSpeed(CaptureSlaveSettings());

    public static PtyPicoFirmware Create(TimeSpan? maximumDuration = null)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("PicoFlasher pseudo-terminal firmware requires Linux.");
        }

        TimeSpan duration = maximumDuration ?? TimeSpan.FromSeconds(20);
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDuration), "The firmware lifetime must be positive and finite.");
        }

        IntPtr nameBuffer = Marshal.AllocHGlobal(SlaveNameBufferLength);
        int masterFileDescriptor = -1;
        int slaveFileDescriptor = -1;
        SafeFileHandle? masterHandle = null;
        SafeFileHandle? slaveHandle = null;
        FileStream? master = null;
        CancellationTokenSource? lifetime = null;
        try
        {
            lifetime = new CancellationTokenSource(duration);
            if (OpenPty(out masterFileDescriptor, out slaveFileDescriptor, nameBuffer, IntPtr.Zero, IntPtr.Zero) != 0)
            {
                int error = Marshal.GetLastWin32Error();
                masterFileDescriptor = -1;
                slaveFileDescriptor = -1;
                throw new IOException($"Unable to allocate a pseudo-terminal: {error}.");
            }

            masterHandle = new SafeFileHandle((IntPtr)masterFileDescriptor, ownsHandle: true);
            masterFileDescriptor = -1;
            slaveHandle = new SafeFileHandle((IntPtr)slaveFileDescriptor, ownsHandle: true);
            slaveFileDescriptor = -1;
            string slavePath = Marshal.PtrToStringAnsi(nameBuffer)
                ?? throw new IOException("The pseudo-terminal allocator did not provide a slave device path.");

            SetNonblocking(masterHandle);
            // The stream owns the descriptor without read-ahead. Native nonblocking I/O
            // also prevents a readiness race from stranding a firmware thread on shutdown.
            master = new FileStream(masterHandle, FileAccess.ReadWrite, bufferSize: 1, isAsync: false);
            masterHandle = null;
            var firmware = new PtyPicoFirmware(master, slaveHandle, slavePath, lifetime);
            master = null;
            slaveHandle = null;
            lifetime = null;
            return firmware;
        }
        finally
        {
            master?.Dispose();
            masterHandle?.Dispose();
            slaveHandle?.Dispose();
            lifetime?.Dispose();
            if (masterFileDescriptor >= 0)
            {
                _ = Close(masterFileDescriptor);
            }

            if (slaveFileDescriptor >= 0)
            {
                _ = Close(slaveFileDescriptor);
            }

            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    public void Start(Func<PtyPicoFirmware, CancellationToken, Task> script)
    {
        ArgumentNullException.ThrowIfNull(script);
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_script is not null)
            {
                throw new InvalidOperationException("Only one firmware script can run on a pseudo-terminal.");
            }

            LifetimeToken.ThrowIfCancellationRequested();
            _script = Task.Run(() => script(this, LifetimeToken));
        }
    }

    public Task CompleteAsync()
    {
        lock (_lifecycleLock)
        {
            Task script = _script
                ?? throw new InvalidOperationException("Start a firmware script before awaiting its completion.");
            return script.WaitAsync(LifetimeToken);
        }
    }

    public byte[] ReceiveCommand(PicoFlasherCommand expectedCommand, uint expectedLba, CancellationToken ct)
    {
        byte[] frame = ReceiveFrame(PicoFlasherProtocol.CommandSize, ct);
        Assert.Equal(Command(expectedCommand, expectedLba), frame);
        return frame;
    }

    public byte[] ReceiveWrite(uint expectedLba, ReadOnlySpan<byte> expectedPayload, CancellationToken ct)
    {
        Assert.Equal(PicoFlasherProtocol.NandWireRecordSize, expectedPayload.Length);
        byte[] frame = ReceiveFrame(PicoFlasherProtocol.CommandSize + PicoFlasherProtocol.NandWireRecordSize, ct);
        byte[] expected = new byte[frame.Length];
        PicoFlasherProtocol.WriteCommand(expected.AsSpan(0, PicoFlasherProtocol.CommandSize), PicoFlasherCommand.WriteFlash, expectedLba);
        expectedPayload.CopyTo(expected.AsSpan(PicoFlasherProtocol.CommandSize));
        Assert.Equal(expected, frame);
        return frame;
    }

    public async Task WriteFragmentedAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        ThrowIfCancellationRequested(ct);
        if (bytes.IsEmpty)
        {
            return;
        }

        if (!MemoryMarshal.TryGetArray(bytes, out ArraySegment<byte> source))
        {
            source = new ArraySegment<byte>(bytes.ToArray());
        }

        int offset = 0;
        for (int fragment = 0; offset < source.Count; fragment++)
        {
            int remaining = source.Count - offset;
            int count = fragment < FragmentSizes.Length ? Math.Min(FragmentSizes[fragment], remaining) : remaining;
            WriteFragment(source.Array!, source.Offset + offset, count, ct);
            offset += count;
            if (offset < source.Count)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5), ct).ConfigureAwait(false);
            }
        }
    }

    public Task WriteUInt32Async(uint value, CancellationToken ct)
    {
        byte[] response = new byte[PicoFlasherProtocol.StatusSize];
        BinaryPrimitives.WriteUInt32LittleEndian(response, value);
        return WriteFragmentedAsync(response, ct);
    }

    public Task AssertQuietAsync(TimeSpan duration, CancellationToken ct)
    {
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        // Scripts run on Task.Run, so bounded native polling does not block the CLI.
        // Poll only: consuming a byte here could steal the next expected command.
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            ThrowIfCancellationRequested(ct);
            TimeSpan remaining = duration - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return Task.CompletedTask;
            }

            int timeout = (int)Math.Min(PollIntervalMilliseconds, Math.Ceiling(remaining.TotalMilliseconds));
            Assert.False(PollReady(PollIn, timeout, ct), "Unexpected outgoing PicoFlasher bytes during the quiet interval.");
        }
    }

    public static byte[] Command(PicoFlasherCommand command, uint lba)
    {
        byte[] frame = new byte[PicoFlasherProtocol.CommandSize];
        PicoFlasherProtocol.WriteCommand(frame, command, lba);
        return frame;
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleLock)
        {
            if (_disposal is null)
            {
                _disposed = true;
                _disposal = DisposeCoreAsync(_script);
            }

            return new ValueTask(_disposal);
        }
    }

    private async Task DisposeCoreAsync(Task? script)
    {
        bool scriptWasRunning = script is { IsCompleted: false };
        try
        {
            _lifetime.Cancel();
            if (script is not null)
            {
                try
                {
                    await script.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (LifetimeToken.IsCancellationRequested)
                {
                }
                catch (IOException) when (scriptWasRunning && LifetimeToken.IsCancellationRequested)
                {
                }
                catch (ObjectDisposedException) when (scriptWasRunning && LifetimeToken.IsCancellationRequested)
                {
                }
                catch (TimeoutException) when (!script.IsCompleted)
                {
                    _ = script.ContinueWith(
                        static failedScript => { _ = failedScript.Exception; },
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    throw new TimeoutException("The firmware script did not stop within the one-second disposal grace period.");
                }
            }
        }
        finally
        {
            try
            {
                _master.Dispose();
            }
            finally
            {
                try
                {
                    _slaveHandle.Dispose();
                }
                finally
                {
                    _lifetime.Dispose();
                }
            }
        }
    }

    private byte[] ReceiveFrame(int length, CancellationToken ct)
    {
        byte[] frame = new byte[length];
        GCHandle pinned = GCHandle.Alloc(frame, GCHandleType.Pinned);
        try
        {
            IntPtr destination = pinned.AddrOfPinnedObject();
            int offset = 0;
            while (offset < frame.Length)
            {
                if (!PollReady(PollIn, PollIntervalMilliseconds, ct))
                {
                    continue;
                }

                ThrowIfCancellationRequested(ct);
                nint count = Read(_masterHandle, IntPtr.Add(destination, offset), (nuint)(frame.Length - offset));
                if (count < 0)
                {
                    int error = Marshal.GetLastWin32Error();
                    ThrowIfCancellationRequested(ct);
                    if (error == InterruptedSystemCall || error == WouldBlock)
                    {
                        continue;
                    }

                    throw new IOException($"Unable to read the pseudo-terminal master: {error}.");
                }

                if (count == 0)
                {
                    throw new EndOfStreamException("The pseudo-terminal master closed before the complete frame arrived.");
                }

                offset += checked((int)count);
            }
        }
        finally
        {
            pinned.Free();
        }

        _frames.Add(frame);
        return frame;
    }

    private void WriteFragment(byte[] source, int offset, int count, CancellationToken ct)
    {
        GCHandle pinned = GCHandle.Alloc(source, GCHandleType.Pinned);
        try
        {
            IntPtr position = IntPtr.Add(pinned.AddrOfPinnedObject(), offset);
            while (count > 0)
            {
                if (!PollReady(PollOut, PollIntervalMilliseconds, ct))
                {
                    continue;
                }

                ThrowIfCancellationRequested(ct);
                nint written = Write(_masterHandle, position, (nuint)count);
                if (written < 0)
                {
                    int error = Marshal.GetLastWin32Error();
                    ThrowIfCancellationRequested(ct);
                    if (error == InterruptedSystemCall || error == WouldBlock)
                    {
                        continue;
                    }

                    throw new IOException($"Unable to write the pseudo-terminal master: {error}.");
                }

                if (written == 0)
                {
                    throw new IOException("The pseudo-terminal master accepted no response bytes.");
                }

                int writtenCount = checked((int)written);
                position = IntPtr.Add(position, writtenCount);
                count -= writtenCount;
            }
        }
        finally
        {
            pinned.Free();
        }
    }

    private bool PollReady(short events, int timeoutMilliseconds, CancellationToken ct)
    {
        ThrowIfCancellationRequested(ct);
        bool addedReference = false;
        try
        {
            _masterHandle.DangerousAddRef(ref addedReference);
            var descriptor = new PollFileDescriptor
            {
                FileDescriptor = checked((int)_masterHandle.DangerousGetHandle().ToInt64()),
                Events = events,
            };
            int result = Poll(ref descriptor, 1, timeoutMilliseconds);
            int error = result < 0 ? Marshal.GetLastWin32Error() : 0;
            ThrowIfCancellationRequested(ct);
            if (result < 0)
            {
                if (error == InterruptedSystemCall)
                {
                    return false;
                }

                throw new IOException($"Unable to poll the pseudo-terminal master: {error}.");
            }

            if ((descriptor.Revents & PollErrorMask) != 0)
            {
                throw new IOException($"The pseudo-terminal master reported terminal events: 0x{descriptor.Revents:X4}.");
            }

            return result > 0 && (descriptor.Revents & events) != 0;
        }
        finally
        {
            if (addedReference)
            {
                _masterHandle.DangerousRelease();
            }
        }
    }

    private void ThrowIfCancellationRequested(CancellationToken ct)
    {
        LifetimeToken.ThrowIfCancellationRequested();
        ct.ThrowIfCancellationRequested();
    }

    private static void SetNonblocking(SafeFileHandle handle)
    {
        int flags;
        do
        {
            flags = Fcntl(handle, GetFileStatusFlags, 0);
        }
        while (flags < 0 && Marshal.GetLastWin32Error() == InterruptedSystemCall);

        if (flags < 0)
        {
            throw new IOException($"Unable to get pseudo-terminal flags: {Marshal.GetLastWin32Error()}.");
        }

        int result;
        do
        {
            result = Fcntl(handle, SetFileStatusFlags, flags | Nonblocking);
        }
        while (result < 0 && Marshal.GetLastWin32Error() == InterruptedSystemCall);

        if (result < 0)
        {
            throw new IOException($"Unable to make the pseudo-terminal master nonblocking: {Marshal.GetLastWin32Error()}.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFileDescriptor
    {
        internal int FileDescriptor;
        internal short Events;
        internal short Revents;
    }

    [DllImport("libutil.so.1", EntryPoint = "openpty", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern int OpenPty(out int master, out int slave, IntPtr name, IntPtr termp, IntPtr winp);

    [DllImport("libc", EntryPoint = "poll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern int Poll(ref PollFileDescriptor descriptor, nuint count, int timeoutMilliseconds);

    [DllImport("libc", EntryPoint = "read", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern nint Read(SafeFileHandle descriptor, IntPtr destination, nuint count);

    [DllImport("libc", EntryPoint = "write", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern nint Write(SafeFileHandle descriptor, IntPtr source, nuint count);

    [DllImport("libc", EntryPoint = "fcntl", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern int Fcntl(SafeFileHandle descriptor, int command, int argument);

    [DllImport("libc", EntryPoint = "close", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern int Close(int descriptor);

    [DllImport("libc", EntryPoint = "tcgetattr", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern int GetTerminalAttributes(SafeFileHandle descriptor, [In, Out] byte[] settings);

    [DllImport("libc", EntryPoint = "cfgetispeed", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint GetInputSpeed([In] byte[] settings);

    [DllImport("libc", EntryPoint = "cfgetospeed", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint GetOutputSpeed([In] byte[] settings);
}

internal sealed class PtyLinuxFactAttribute : FactAttribute
{
    public PtyLinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Requires a real Linux pseudo-terminal.";
        }
    }
}

internal sealed class PtyLinuxTheoryAttribute : TheoryAttribute
{
    public PtyLinuxTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Requires a real Linux pseudo-terminal.";
        }
    }
}
