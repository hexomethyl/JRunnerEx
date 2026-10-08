using System.Buffers.Binary;
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
        var clock = new ControlledTimeProvider();
        var stream = new ControlledStream(
        [
            new DataRead([0x10]),
            new DataRead([0x20]),
        ],
        beforeRead: (_, cancellationToken) =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(300));
            Assert.False(cancellationToken.IsCancellationRequested);
        });
        await using var transport = new SerialPicoFlasherTransport(stream, noProgressTimeout, clock);
        byte[] destination = new byte[2];

        await transport.ReadExactlyAsync(destination);

        Assert.Equal(new byte[] { 0x10, 0x20 }, destination);
        Assert.Equal(2, stream.ReadCallCount);
        Assert.True(clock.Elapsed > noProgressTimeout);
    }

    [Fact]
    public async Task No_progress_expires_one_full_deadline_after_the_most_recent_byte()
    {
        TimeSpan noProgressTimeout = TimeSpan.FromMilliseconds(500);
        var clock = new ControlledTimeProvider();
        var stream = new ControlledStream(
        [
            new DataRead([0x10]),
            new DataRead([0x20]),
        ],
        beforeRead: (readCall, cancellationToken) =>
        {
            if (readCall <= 2)
            {
                clock.Advance(TimeSpan.FromMilliseconds(300));
                Assert.False(cancellationToken.IsCancellationRequested);
                return;
            }

            clock.Advance(TimeSpan.FromMilliseconds(499));
            Assert.False(cancellationToken.IsCancellationRequested);
            clock.Advance(TimeSpan.FromMilliseconds(1));
            Assert.True(cancellationToken.IsCancellationRequested);
        });
        await using var transport = new SerialPicoFlasherTransport(stream, noProgressTimeout, clock);
        byte[] destination = new byte[3];

        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
            () => transport.ReadExactlyAsync(destination).AsTask());

        Assert.Equal(ExitCode.InputOutput, exception.Code);
        Assert.Equal("pico-transport-timeout", exception.Kind);
        Assert.Equal(new byte[] { 0x10, 0x20, 0x00 }, destination);
        Assert.Equal(3, stream.ReadCallCount);
    }

    [Fact]
    public async Task Completed_partial_read_does_not_carry_an_expired_deadline_into_the_next_read()
    {
        TimeSpan noProgressTimeout = TimeSpan.FromMilliseconds(500);
        var clock = new ControlledTimeProvider();
        var stream = new ControlledStream(
        [
            new CallbackRead((buffer, cancellationToken) =>
            {
                clock.Advance(TimeSpan.FromMilliseconds(300));
                Assert.False(cancellationToken.IsCancellationRequested);
                buffer.Span[0] = 0x10;
                var completedRead = new ValueTask<int>(1);

                // The byte is ready, but the transport has not consumed its result.
                clock.Advance(TimeSpan.FromMilliseconds(300));
                Assert.True(cancellationToken.IsCancellationRequested);
                return completedRead;
            }),
            new DataRead([0x20]),
        ],
        beforeRead: (readCall, cancellationToken) =>
        {
            if (readCall == 2)
            {
                Assert.False(cancellationToken.IsCancellationRequested);
                clock.Advance(TimeSpan.FromMilliseconds(300));
                Assert.False(cancellationToken.IsCancellationRequested);
            }
        });
        await using var transport = new SerialPicoFlasherTransport(stream, noProgressTimeout, clock);
        byte[] destination = new byte[2];

        await transport.ReadExactlyAsync(destination);

        Assert.Equal(new byte[] { 0x10, 0x20 }, destination);
        Assert.Equal(2, stream.ReadCallCount);
    }

    [Fact]
    public async Task Completed_partial_read_does_not_carry_a_queued_deadline_callback_into_the_next_read()
    {
        TimeSpan noProgressTimeout = TimeSpan.FromMilliseconds(500);
        var clock = new ControlledTimeProvider();
        CancellationToken previousReadToken = default;
        var stream = new ControlledStream(
        [
            new CallbackRead((buffer, cancellationToken) =>
            {
                previousReadToken = cancellationToken;
                clock.Advance(TimeSpan.FromMilliseconds(300));
                Assert.False(previousReadToken.IsCancellationRequested);
                buffer.Span[0] = 0x10;
                var completedRead = new ValueTask<int>(1);

                // The byte is ready, but its old timeout callback is queued before consumption.
                clock.Advance(TimeSpan.FromMilliseconds(300), dispatchCallbacks: false);
                Assert.False(previousReadToken.IsCancellationRequested);
                return completedRead;
            }),
            new DataRead([0x20]),
        ],
        beforeRead: (readCall, cancellationToken) =>
        {
            if (readCall == 2)
            {
                Assert.False(previousReadToken.IsCancellationRequested);
                Assert.False(cancellationToken.IsCancellationRequested);
                clock.DispatchQueuedCallbacks();
                Assert.True(previousReadToken.IsCancellationRequested);
                Assert.False(cancellationToken.IsCancellationRequested);
            }
        });
        await using var transport = new SerialPicoFlasherTransport(stream, noProgressTimeout, clock);
        byte[] destination = new byte[2];

        await transport.ReadExactlyAsync(destination);

        Assert.Equal(new byte[] { 0x10, 0x20 }, destination);
        Assert.Equal(2, stream.ReadCallCount);
    }

    [Fact]
    public async Task Nand_stream_cleanup_preserves_firmware_failure_when_completed_byte_outlives_quiescence_deadline()
    {
        TimeSpan quiescenceTimeout = TimeSpan.FromMilliseconds(100);
        var clock = new ControlledTimeProvider();
        byte[] configuration = new byte[PicoFlasherProtocol.StatusSize];
        BinaryPrimitives.WriteUInt32LittleEndian(configuration, 0x0119_8010U);
        byte[] firmwareStatus = new byte[PicoFlasherProtocol.StatusSize];
        BinaryPrimitives.WriteUInt32LittleEndian(firmwareStatus, 0xBADU);
        CancellationToken expiredCleanupReadToken = default;
        var stream = new ControlledStream(
        [
            new DataRead(configuration),
            new DataRead(firmwareStatus),
            new CallbackRead((buffer, cancellationToken) =>
            {
                Assert.Equal(1, buffer.Length);
                Assert.False(cancellationToken.IsCancellationRequested);
                expiredCleanupReadToken = cancellationToken;
                buffer.Span[0] = 0xA4;
                var completedRead = new ValueTask<int>(1);

                // Cleanup expires after the byte is ready, before its result is consumed.
                // The serial transport's own 500 ms deadline has not elapsed.
                clock.Advance(quiescenceTimeout);
                Assert.True(cancellationToken.IsCancellationRequested);
                return completedRead;
            }),
            new CallbackRead((buffer, cancellationToken) =>
            {
                Assert.Equal(1, buffer.Length);
                Assert.True(expiredCleanupReadToken.IsCancellationRequested);
                Assert.False(cancellationToken.IsCancellationRequested);
                buffer.Span[0] = 0xB4;
                return new ValueTask<int>(1);
            }),
            new CallbackRead((buffer, cancellationToken) =>
            {
                Assert.Equal(1, buffer.Length);
                Assert.False(cancellationToken.IsCancellationRequested);
                clock.Advance(quiescenceTimeout);
                Assert.True(cancellationToken.IsCancellationRequested);
                throw new OperationCanceledException(cancellationToken);
            }),
        ]);
        var transport = new SerialPicoFlasherTransport(stream, TimeSpan.FromMilliseconds(500), clock);
        await using var connection = new PicoFlasherConnection(
            new PicoFlasherDeviceEndpoint("/dev/ttyACM-test", "cleanup-progress", interfaceNumber: 0),
            firmwareVersion: 4U,
            transport);
        var service = new PicoFlasherService(connection, TimeSpan.Zero, clock);
        using var output = new MemoryStream();

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => service.ReadNandAsync(
                new PicoFlasherNandReadRequest(output, StartRecord: 0, RecordCount: 2)));

        Assert.Equal(ExitCode.DeviceUnavailable, failure.Code);
        Assert.Equal("pico-nand-read-failed", failure.Kind);
        Assert.Contains("record 0", failure.Message, StringComparison.Ordinal);
        Assert.Contains("0x00000BAD", failure.Message, StringComparison.Ordinal);
        Assert.Empty(output.ToArray());
        Assert.Equal(5, stream.ReadCallCount); // Two statuses, two queued bytes, then quiescence.
        Assert.True(connection.IsRetired);
        (PicoFlasherCommand Command, uint Address)[] expectedCommands =
        [
            (PicoFlasherCommand.SetSmcWorkaround, 0),
            (PicoFlasherCommand.StopSmc, 0),
            (PicoFlasherCommand.GetFlashConfiguration, 0),
            (PicoFlasherCommand.ReadFlashStream, 2),
            (PicoFlasherCommand.ReadFlashStream, 0),
            (PicoFlasherCommand.StartSmc, 0),
        ];
        Assert.Equal(expectedCommands.Length, stream.Writes.Count);
        for (int index = 0; index < expectedCommands.Length; index++)
        {
            byte[] expectedFrame = new byte[PicoFlasherProtocol.CommandSize];
            PicoFlasherProtocol.WriteCommand(
                expectedFrame,
                expectedCommands[index].Command,
                expectedCommands[index].Address);
            Assert.Equal(expectedFrame, stream.Writes[index]);
        }

        OperationFailureException followUpFailure = await Assert.ThrowsAsync<OperationFailureException>(
            () => service.ProbeAsync());

        Assert.Equal(ExitCode.InputOutput, followUpFailure.Code);
        Assert.Equal("pico-stream-recovery-required", followUpFailure.Kind);
        Assert.Equal(expectedCommands.Length, stream.Writes.Count);
        Assert.Equal(5, stream.ReadCallCount);
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
        var clock = new ControlledTimeProvider();
        var stream = new BlockingReadStream();
        await using var transport = new SerialPicoFlasherTransport(
            stream,
            TimeSpan.FromMilliseconds(50),
            clock);

        Task operation = transport.ReadExactlyAsync(new byte[1]).AsTask();
        await stream.ReadStarted.Task;
        clock.Advance(TimeSpan.FromMilliseconds(49));
        Assert.False(stream.ReadCancellationToken.IsCancellationRequested);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(stream.ReadCancellationToken.IsCancellationRequested);

        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(() => operation);

        Assert.Equal(ExitCode.InputOutput, exception.Code);
        Assert.Equal("pico-transport-timeout", exception.Kind);
    }

    [Fact]
    public async Task Caller_cancellation_remains_an_operation_canceled_exception()
    {
        var clock = new ControlledTimeProvider();
        var stream = new BlockingReadStream();
        await using var transport = new SerialPicoFlasherTransport(stream, DefaultNoProgressTimeout, clock);
        using var cancellationSource = new CancellationTokenSource();

        Task operation = transport.ReadExactlyAsync(new byte[1], cancellationSource.Token).AsTask();
        await stream.ReadStarted.Task;
        cancellationSource.Cancel();

        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(cancellationSource.Token, exception.CancellationToken);
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

    private sealed record CallbackRead(Func<Memory<byte>, CancellationToken, ValueTask<int>> Read) : ReadStep;

    private sealed class ControlledStream : Stream
    {
        private readonly Queue<ReadStep> _readSteps;
        private readonly Exception? _writeException;
        private readonly Exception? _flushException;
        private readonly Action<int, CancellationToken>? _beforeRead;

        internal ControlledStream(
            IEnumerable<ReadStep> readSteps,
            Exception? writeException = null,
            Exception? flushException = null,
            Action<int, CancellationToken>? beforeRead = null)
        {
            _readSteps = new Queue<ReadStep>(readSteps);
            _writeException = writeException;
            _flushException = flushException;
            _beforeRead = beforeRead;
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
            _beforeRead?.Invoke(ReadCallCount, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (_readSteps.Count == 0)
            {
                return new ValueTask<int>(0);
            }

            ReadStep step = _readSteps.Dequeue();
            if (step is ErrorRead error)
            {
                throw error.Exception;
            }

            if (step is CallbackRead callback)
            {
                return callback.Read(buffer, cancellationToken);
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

    private sealed class ControlledTimeProvider : TimeProvider
    {
        private readonly List<ControlledTimer> _timers = [];
        private readonly Queue<(TimerCallback Callback, object? State)> _queuedCallbacks = [];

        internal TimeSpan Elapsed { get; private set; }

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + Elapsed;

        public override long GetTimestamp() => Elapsed.Ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ControlledTimer(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }

        internal void Advance(TimeSpan elapsed, bool dispatchCallbacks = true)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
            Elapsed += elapsed;
            foreach (ControlledTimer timer in _timers.ToArray())
            {
                timer.FireIfDue(dispatchCallbacks);
            }
        }

        internal void DispatchQueuedCallbacks()
        {
            while (_queuedCallbacks.TryDequeue(out var callback))
            {
                callback.Callback(callback.State);
            }
        }

        private sealed class ControlledTimer : ITimer
        {
            private readonly ControlledTimeProvider _clock;
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private TimeSpan? _deadline;
            private bool _disposed;

            internal ControlledTimer(ControlledTimeProvider clock, TimerCallback callback, object? state)
            {
                _clock = clock;
                _callback = callback;
                _state = state;
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed)
                {
                    return false;
                }

                if (period != Timeout.InfiniteTimeSpan)
                {
                    throw new NotSupportedException("The controlled clock supports only one-shot I/O deadlines.");
                }

                _deadline = dueTime == Timeout.InfiniteTimeSpan ? null : _clock.Elapsed + dueTime;

                return true;
            }

            internal void FireIfDue(bool dispatchCallbacks)
            {
                if (_disposed || _deadline is not TimeSpan deadline || deadline > _clock.Elapsed)
                {
                    return;
                }

                _deadline = null;
                if (dispatchCallbacks)
                {
                    _callback(_state);
                }
                else
                {
                    // Already queued callbacks survive a later Change or Dispose.
                    _clock._queuedCallbacks.Enqueue((_callback, _state));
                }
            }

            public void Dispose()
            {
                _disposed = true;
                _deadline = null;
                _clock._timers.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class BlockingReadStream : Stream
    {
        internal TaskCompletionSource<bool> ReadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal CancellationToken ReadCancellationToken { get; private set; }

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
            ReadCancellationToken = cancellationToken;
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
