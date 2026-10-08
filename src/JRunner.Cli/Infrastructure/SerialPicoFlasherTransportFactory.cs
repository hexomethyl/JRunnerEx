using System.IO.Ports;
using JRunner.Core.Contracts;
using JRunner.Core.Devices.PicoFlasher;

namespace JRunner.Cli.Infrastructure;

/// <summary>
/// Opens the command CDC endpoint without using the BlackPill DFU-triggering baud rate.
/// </summary>
internal sealed class SerialPicoFlasherTransportFactory : IPicoFlasherTransportFactory
{
    /// <inheritdoc />
    public ValueTask<IPicoFlasherTransport> OpenAsync(
        PicoFlasherDeviceEndpoint endpoint,
        TimeSpan noProgressTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        noProgressTimeout = SerialPicoFlasherTransport.NormalizeNoProgressTimeout(noProgressTimeout);
        cancellationToken.ThrowIfCancellationRequested();

        SerialPort? port = null;
        try
        {
            port = CreateConfiguredPort(endpoint.DevicePath, noProgressTimeout);
            port.Open();
            cancellationToken.ThrowIfCancellationRequested();

            IPicoFlasherTransport transport = new SerialPicoFlasherTransport(port, noProgressTimeout);
            port = null;
            return new ValueTask<IPicoFlasherTransport>(transport);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            throw DeviceAccessDenied(endpoint.DevicePath);
        }
        catch (Exception)
        {
            throw new OperationFailureException(
                ExitCode.DeviceUnavailable,
                "pico-device-open-failed",
                "The selected PicoFlasher device could not be opened.");
        }
        finally
        {
            DisposeQuietly(port);
        }
    }

    /// <summary>
    /// Creates, but does not open, a command CDC serial port with the required safe line settings.
    /// </summary>
    internal static SerialPort CreateConfiguredPort(string devicePath, TimeSpan noProgressTimeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);
        TimeSpan timeout = SerialPicoFlasherTransport.NormalizeNoProgressTimeout(noProgressTimeout);
        int timeoutMilliseconds = checked((int)(timeout.Ticks / TimeSpan.TicksPerMillisecond));

        return new SerialPort(devicePath)
        {
            BaudRate = PicoFlasherProtocol.CommandBaudRate,
            DataBits = 8,
            Parity = Parity.None,
            StopBits = StopBits.One,
            Handshake = Handshake.None,
            DtrEnable = false,
            RtsEnable = false,
            ReadTimeout = timeoutMilliseconds,
            WriteTimeout = timeoutMilliseconds,
        };
    }

    internal static OperationFailureException DeviceAccessDenied(string devicePath)
    {
        return new OperationFailureException(
            ExitCode.DeviceUnavailable,
            "pico-device-access-denied",
            $"Permission was denied while opening PicoFlasher device '{devicePath}'. Install the packaged PicoFlasher udev rule and ensure the current user belongs to the dialout group.");
    }

    private static void DisposeQuietly(SerialPort? port)
    {
        if (port is null)
        {
            return;
        }

        try
        {
            port.Dispose();
        }
        catch (Exception)
        {
            // A failed open remains the primary outcome.
        }
    }
}

/// <summary>
/// Provides exact framed I/O over one opened PicoFlasher CDC command stream.
/// </summary>
internal sealed class SerialPicoFlasherTransport : IPicoFlasherTransport
{
    private SerialPort? _port;
    private Stream? _stream;
    private readonly TimeProvider _timeProvider;
    private int _disposed;

    internal TimeSpan NoProgressTimeout { get; }

    internal SerialPicoFlasherTransport(SerialPort port, TimeSpan noProgressTimeout)
    {
        ArgumentNullException.ThrowIfNull(port);
        NoProgressTimeout = NormalizeNoProgressTimeout(noProgressTimeout);
        _port = port;
        _stream = port.BaseStream;
        _timeProvider = TimeProvider.System;
    }

    /// <summary>
    /// Creates a transport over a caller-owned stream for infrastructure tests.
    /// </summary>
    internal SerialPicoFlasherTransport(
        Stream stream,
        TimeSpan noProgressTimeout,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        NoProgressTimeout = NormalizeNoProgressTimeout(noProgressTimeout);
        _stream = stream;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> source,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stream stream = GetStream();

        var deadline = new NoProgressDeadline(_timeProvider, cancellationToken);
        try
        {
            await WriteWithTimeoutAsync(stream, source, cancellationToken, deadline.Reset(NoProgressTimeout))
                .ConfigureAwait(false);
            await FlushWithTimeoutAsync(stream, cancellationToken, deadline.Reset(NoProgressTimeout))
                .ConfigureAwait(false);
        }
        finally
        {
            deadline.Dispose();
        }
    }

    /// <inheritdoc />
    public async ValueTask ReadExactlyAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stream stream = GetStream();

        var deadline = new NoProgressDeadline(_timeProvider, cancellationToken);
        try
        {
            while (!destination.IsEmpty)
            {
                int bytesRead = await ReadWithTimeoutAsync(
                        stream,
                        destination,
                        cancellationToken,
                        deadline.Reset(NoProgressTimeout))
                    .ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw TransportClosed();
                }

                if ((uint)bytesRead > (uint)destination.Length)
                {
                    throw TransportIoFailed();
                }

                destination = destination[bytesRead..];
            }
        }
        finally
        {
            deadline.Dispose();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        SerialPort? port = _port;
        Stream? stream = _stream;
        _port = null;
        _stream = null;

        if (port is not null)
        {
            DisposeQuietly(port);
            return;
        }

        if (stream is not null)
        {
            try
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Disposal cannot make a completed device operation fail.
            }
        }
    }

    private async ValueTask<int> ReadWithTimeoutAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken,
        CancellationTokenSource timeoutSource)
    {
        try
        {
            return await stream.ReadAsync(destination, timeoutSource.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw MapTransportException(exception, cancellationToken, timeoutSource.Token);
        }
        finally
        {
            timeoutSource.CancelAfter(Timeout.InfiniteTimeSpan);
        }
    }

    private async ValueTask WriteWithTimeoutAsync(
        Stream stream,
        ReadOnlyMemory<byte> source,
        CancellationToken cancellationToken,
        CancellationTokenSource timeoutSource)
    {
        try
        {
            await stream.WriteAsync(source, timeoutSource.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw MapTransportException(exception, cancellationToken, timeoutSource.Token);
        }
        finally
        {
            timeoutSource.CancelAfter(Timeout.InfiniteTimeSpan);
        }
    }

    private async ValueTask FlushWithTimeoutAsync(
        Stream stream,
        CancellationToken cancellationToken,
        CancellationTokenSource timeoutSource)
    {
        try
        {
            await stream.FlushAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw MapTransportException(exception, cancellationToken, timeoutSource.Token);
        }
        finally
        {
            timeoutSource.CancelAfter(Timeout.InfiniteTimeSpan);
        }
    }

    // Reuse one timer for ordinary progress. Before rearming, reject an expired
    // source or an already-queued callback from the previous completed I/O.
    private struct NoProgressDeadline : IDisposable
    {
        private readonly TimeProvider _timeProvider;
        private readonly CancellationToken _callerCancellationToken;
        private CancellationTokenSource _source;
        private CancellationTokenRegistration _callerCancellation;

        internal NoProgressDeadline(TimeProvider timeProvider, CancellationToken cancellationToken)
        {
            _timeProvider = timeProvider;
            _callerCancellationToken = cancellationToken;
            _source = CreateSource(timeProvider, cancellationToken, out _callerCancellation);
        }

        internal CancellationTokenSource Reset(TimeSpan timeout)
        {
            _callerCancellationToken.ThrowIfCancellationRequested();
            if (!_source.TryReset())
            {
                Dispose();
                _source = CreateSource(_timeProvider, _callerCancellationToken, out _callerCancellation);
            }

            _source.CancelAfter(timeout);
            return _source;
        }

        private static CancellationTokenSource CreateSource(
            TimeProvider timeProvider,
            CancellationToken cancellationToken,
            out CancellationTokenRegistration callerCancellation)
        {
            var source = new CancellationTokenSource(Timeout.InfiniteTimeSpan, timeProvider);
            callerCancellation = cancellationToken.UnsafeRegister(
                static state => ((CancellationTokenSource)state!).Cancel(),
                source);
            return source;
        }

        public void Dispose()
        {
            _callerCancellation.Dispose();
            _source.Dispose();
        }
    }

    private static Exception MapTransportException(
        Exception exception,
        CancellationToken callerCancellationToken,
        CancellationToken timeoutToken)
    {
        if (exception is OperationFailureException)
        {
            return exception;
        }

        if (exception is OperationCanceledException)
        {
            callerCancellationToken.ThrowIfCancellationRequested();
            return timeoutToken.IsCancellationRequested ? TransportTimedOut() : TransportIoFailed();
        }

        return exception switch
        {
            ObjectDisposedException => TransportClosed(),
            TimeoutException => TransportTimedOut(),
            _ => TransportIoFailed(),
        };
    }

    private Stream GetStream()
    {
        if (Volatile.Read(ref _disposed) != 0 || _stream is null)
        {
            throw new ObjectDisposedException(nameof(SerialPicoFlasherTransport));
        }

        return _stream;
    }

    private static void DisposeQuietly(SerialPort port)
    {
        try
        {
            port.Dispose();
        }
        catch (Exception)
        {
            // Closing a disconnected serial device is best effort.
        }
    }

    private static OperationFailureException TransportClosed()
    {
        return new OperationFailureException(
            ExitCode.InputOutput,
            "pico-transport-closed",
            "The PicoFlasher transport closed before the requested response was received.");
    }

    private static OperationFailureException TransportTimedOut()
    {
        return new OperationFailureException(
            ExitCode.InputOutput,
            "pico-transport-timeout",
            "The PicoFlasher transport did not make progress before the I/O timeout.");
    }

    private static OperationFailureException TransportIoFailed()
    {
        return new OperationFailureException(
            ExitCode.InputOutput,
            "pico-transport-io-failed",
            "The PicoFlasher transport could not complete the requested I/O.");
    }

    internal static TimeSpan NormalizeNoProgressTimeout(TimeSpan noProgressTimeout)
    {
        if (!PicoFlasherProtocol.IsValidNoProgressTimeout(noProgressTimeout))
        {
            throw new ArgumentOutOfRangeException(
                nameof(noProgressTimeout),
                noProgressTimeout,
                "The transport no-progress timeout must be positive and representable.");
        }

        long milliseconds = (noProgressTimeout.Ticks - 1) / TimeSpan.TicksPerMillisecond + 1;
        return TimeSpan.FromTicks(milliseconds * TimeSpan.TicksPerMillisecond);
    }
}
