using System.IO.Ports;
using JRunner.Cli.Infrastructure;
using JRunner.Core.Contracts;
using JRunner.Core.Devices.PicoFlasher;
using Xunit;

namespace JRunner.Cli.Tests;

public sealed class SerialPicoFlasherTransportTests
{
    private static readonly TimeSpan DefaultNoProgressTimeout = PicoFlasherProtocol.DefaultNoProgressTimeout;

    [Fact]
    public void Configured_port_uses_explicit_safe_command_settings_and_requested_timeout_without_opening()
    {
        TimeSpan noProgressTimeout = TimeSpan.FromMilliseconds(1_234);
        using SerialPort port = SerialPicoFlasherTransportFactory.CreateConfiguredPort(
            "/dev/ttyACM-test",
            noProgressTimeout);

        Assert.False(port.IsOpen);
        Assert.Equal(PicoFlasherProtocol.CommandBaudRate, port.BaudRate);
        Assert.Equal(8, port.DataBits);
        Assert.Equal(Parity.None, port.Parity);
        Assert.Equal(StopBits.One, port.StopBits);
        Assert.Equal(Handshake.None, port.Handshake);
        Assert.False(port.DtrEnable);
        Assert.False(port.RtsEnable);
        Assert.Equal(1_234, port.ReadTimeout);
        Assert.Equal(1_234, port.WriteTimeout);
    }

    [Theory]
    [InlineData(1L, 1)]
    [InlineData(5_000L, 1)]
    [InlineData(9_999L, 1)]
    [InlineData(10_000L, 1)]
    [InlineData(10_001L, 2)]
    [InlineData(12_340_001L, 1_235)]
    [InlineData(21_474_836_469_999L, int.MaxValue)]
    [InlineData(21_474_836_470_000L, int.MaxValue)]
    public async Task Whole_millisecond_timeout_normalization_matches_port_and_transport(
        long timeoutTicks,
        int expectedMilliseconds)
    {
        TimeSpan requestedTimeout = TimeSpan.FromTicks(timeoutTicks);
        using SerialPort port = SerialPicoFlasherTransportFactory.CreateConfiguredPort(
            "/dev/ttyACM-test",
            requestedTimeout);
        await using SerialPicoFlasherTransport transport = new(
            new MemoryStream(),
            requestedTimeout);

        Assert.Equal(expectedMilliseconds, port.ReadTimeout);
        Assert.Equal(expectedMilliseconds, port.WriteTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), transport.NoProgressTimeout);
    }

    [Fact]
    public void Device_access_denied_failure_preserves_path_and_setup_guidance()
    {
        const string devicePath = "/dev/serial/by-id/pico-test-exact";
        OperationFailureException exception = SerialPicoFlasherTransportFactory.DeviceAccessDenied(devicePath);

        Assert.Equal(5, (int)exception.Code);
        Assert.Equal(ExitCode.DeviceUnavailable, exception.Code);
        Assert.Equal("pico-device-access-denied", exception.Kind);
        Assert.Contains(devicePath, exception.Message, StringComparison.Ordinal);
        Assert.Contains("packaged PicoFlasher udev rule", exception.Message, StringComparison.Ordinal);
        Assert.Contains("dialout group", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Transport_rejects_nonpositive_or_unrepresentable_no_progress_timeouts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SerialPicoFlasherTransportFactory.CreateConfiguredPort("/dev/ttyACM-test", TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SerialPicoFlasherTransportFactory.CreateConfiguredPort(
                "/dev/ttyACM-test",
                PicoFlasherProtocol.MaximumNoProgressTimeout + TimeSpan.FromTicks(1)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SerialPicoFlasherTransport(new MemoryStream(), TimeSpan.Zero));
    }

    [Fact]
    public async Task Exact_read_fills_the_destination_across_partial_stream_reads()
    {
        var stream = new ControlledStream(
        [
            new DataRead([0x10]),
            new DataRead([0x20, 0x30]),
            new DataRead([0x40]),
        ]);
        await using var transport = new SerialPicoFlasherTransport(stream, DefaultNoProgressTimeout);
        byte[] destination = new byte[4];

        await transport.ReadExactlyAsync(destination);

        Assert.Equal(new byte[] { 0x10, 0x20, 0x30, 0x40 }, destination);
        Assert.Equal(3, stream.ReadCallCount);
    }

    [Fact]
    public async Task Exact_read_resets_the_no_progress_deadline_after_each_received_byte()
    {
        TimeSpan noProgressTimeout = TimeSpan.FromMilliseconds(500);
        var stream = new DelayedReadStream(
        [
            new DelayedByte(0x10, TimeSpan.FromMilliseconds(300)),
            new DelayedByte(0x20, TimeSpan.FromMilliseconds(300)),
        ]);
        await using var transport = new SerialPicoFlasherTransport(stream, noProgressTimeout);
        byte[] destination = new byte[2];

        await transport.ReadExactlyAsync(destination);

        Assert.Equal(new byte[] { 0x10, 0x20 }, destination);
        Assert.Equal(2, stream.ReadCallCount);
    }

    [Fact]
    public async Task End_of_stream_is_a_typed_transport_closed_failure()
    {
        var stream = new ControlledStream([]);
        await using var transport = new SerialPicoFlasherTransport(stream, DefaultNoProgressTimeout);

        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
            () => transport.ReadExactlyAsync(new byte[1]).AsTask());

        Assert.Equal(ExitCode.InputOutput, exception.Code);
        Assert.Equal("pico-transport-closed", exception.Kind);
    }

    [Fact]
    public async Task Read_io_errors_are_typed_and_sanitized()
    {
        var stream = new ControlledStream([new ErrorRead(new IOException("private read detail"))]);
        await using var transport = new SerialPicoFlasherTransport(stream, DefaultNoProgressTimeout);

        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
            () => transport.ReadExactlyAsync(new byte[1]).AsTask());

        Assert.Equal(ExitCode.InputOutput, exception.Code);
        Assert.Equal("pico-transport-io-failed", exception.Kind);
        Assert.DoesNotContain("private read detail", exception.Message);
    }

    [Fact]
    public async Task Read_timeouts_are_typed_and_sanitized()
    {
        var stream = new ControlledStream([new ErrorRead(new TimeoutException("private timeout detail"))]);
        await using var transport = new SerialPicoFlasherTransport(stream, DefaultNoProgressTimeout);

        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
            () => transport.ReadExactlyAsync(new byte[1]).AsTask());

        Assert.Equal(ExitCode.InputOutput, exception.Code);
        Assert.Equal("pico-transport-timeout", exception.Kind);
        Assert.DoesNotContain("private timeout detail", exception.Message);
    }

    [Fact]
    public async Task Transport_deadline_cancellation_is_a_typed_timeout_failure()
    {
        var stream = new BlockingReadStream();
        await using var transport = new SerialPicoFlasherTransport(
            stream,
            TimeSpan.FromMilliseconds(50));

        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
            () => transport.ReadExactlyAsync(new byte[1]).AsTask());

        Assert.Equal(ExitCode.InputOutput, exception.Code);
        Assert.Equal("pico-transport-timeout", exception.Kind);
    }

    [Fact]
    public async Task Caller_cancellation_remains_an_operation_canceled_exception()
    {
        var stream = new BlockingReadStream();
        await using var transport = new SerialPicoFlasherTransport(stream, DefaultNoProgressTimeout);
        using var cancellationSource = new CancellationTokenSource();

        Task operation = transport.ReadExactlyAsync(new byte[1], cancellationSource.Token).AsTask();
        await stream.ReadStarted.Task;
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    [Fact]
    public async Task Write_flushes_the_complete_frame()
    {
        var stream = new ControlledStream([]);
        await using var transport = new SerialPicoFlasherTransport(stream, DefaultNoProgressTimeout);
        byte[] frame = [0x00, 0x00, 0x00, 0x00, 0x00];

        await transport.WriteAsync(frame);

        Assert.Single(stream.Writes);
        Assert.Equal(frame, stream.Writes[0]);
        Assert.Equal(1, stream.FlushCallCount);
    }

    [Fact]
    public async Task Write_io_errors_are_typed_and_sanitized()
    {
        var stream = new ControlledStream([], writeException: new IOException("private write detail"));
        await using var transport = new SerialPicoFlasherTransport(stream, DefaultNoProgressTimeout);

        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
            () => transport.WriteAsync(new byte[] { 0x00 }).AsTask());

        Assert.Equal(ExitCode.InputOutput, exception.Code);
        Assert.Equal("pico-transport-io-failed", exception.Kind);
        Assert.DoesNotContain("private write detail", exception.Message);
    }

    [Fact]
    public async Task Flush_io_errors_are_typed_and_sanitized()
    {
        var stream = new ControlledStream([], flushException: new IOException("private flush detail"));
        await using var transport = new SerialPicoFlasherTransport(stream, DefaultNoProgressTimeout);

        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
            () => transport.WriteAsync(new byte[] { 0x00 }).AsTask());

        Assert.Equal(ExitCode.InputOutput, exception.Code);
        Assert.Equal("pico-transport-io-failed", exception.Kind);
        Assert.DoesNotContain("private flush detail", exception.Message);
    }

    private abstract record ReadStep;

    private sealed record DataRead(byte[] Bytes) : ReadStep;

    private sealed record ErrorRead(Exception Exception) : ReadStep;

    private sealed record DelayedByte(byte Value, TimeSpan Delay);

    private sealed class ControlledStream : Stream
    {
        private readonly Queue<ReadStep> _readSteps;
        private readonly Exception? _writeException;
        private readonly Exception? _flushException;

        internal ControlledStream(
            IEnumerable<ReadStep> readSteps,
            Exception? writeException = null,
            Exception? flushException = null)
        {
            _readSteps = new Queue<ReadStep>(readSteps);
            _writeException = writeException;
            _flushException = flushException;
        }

        internal int ReadCallCount { get; private set; }

        internal List<byte[]> Writes { get; } = [];

        internal int FlushCallCount { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            FlushCallCount++;
            if (_flushException is not null)
            {
                throw _flushException;
            }
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FlushCallCount++;
            return _flushException is null
                ? Task.CompletedTask
                : Task.FromException(_flushException);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCallCount++;

            if (_readSteps.Count == 0)
            {
                return new ValueTask<int>(0);
            }

            ReadStep step = _readSteps.Dequeue();
            if (step is ErrorRead error)
            {
                throw error.Exception;
            }

            if (step is DataRead data)
            {
                if (data.Bytes.Length > buffer.Length)
                {
                    throw new InvalidOperationException("Test stream supplied more bytes than requested.");
                }

                data.Bytes.AsSpan().CopyTo(buffer.Span);
                return new ValueTask<int>(data.Bytes.Length);
            }

            throw new InvalidOperationException("Unknown test stream read step.");
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_writeException is not null)
            {
                throw _writeException;
            }

            Writes.Add(buffer.ToArray());
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DelayedReadStream : Stream
    {
        private readonly Queue<DelayedByte> _steps;

        internal DelayedReadStream(IEnumerable<DelayedByte> steps)
        {
            _steps = new Queue<DelayedByte>(steps);
        }

        internal int ReadCallCount { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCallCount++;
            if (_steps.Count == 0)
            {
                return 0;
            }

            DelayedByte step = _steps.Dequeue();
            await Task.Delay(step.Delay, cancellationToken).ConfigureAwait(false);
            buffer.Span[0] = step.Value;
            return 1;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class BlockingReadStream : Stream
    {
        internal TaskCompletionSource<bool> ReadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}
