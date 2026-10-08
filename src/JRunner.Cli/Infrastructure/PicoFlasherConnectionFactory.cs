using System.Buffers;
using JRunner.Core.Contracts;
using JRunner.Core.Devices.PicoFlasher;

namespace JRunner.Cli.Infrastructure;

/// <summary>
/// Selects one PicoFlasher command endpoint and verifies its firmware before exposing it to a command.
/// </summary>
internal sealed class PicoFlasherConnectionFactory
{
    private readonly IPicoFlasherDeviceEnumerator _deviceEnumerator;
    private readonly IPicoFlasherTransportFactory _transportFactory;

    /// <summary>
    /// Creates the production Linux CDC connection factory.
    /// </summary>
    internal PicoFlasherConnectionFactory()
        : this(new LinuxPicoFlasherDeviceEnumerator(), new SerialPicoFlasherTransportFactory())
    {
    }

    /// <summary>
    /// Creates a connection factory over explicit endpoint discovery and transport dependencies.
    /// </summary>
    internal PicoFlasherConnectionFactory(
        IPicoFlasherDeviceEnumerator deviceEnumerator,
        IPicoFlasherTransportFactory transportFactory)
    {
        ArgumentNullException.ThrowIfNull(deviceEnumerator);
        ArgumentNullException.ThrowIfNull(transportFactory);

        _deviceEnumerator = deviceEnumerator;
        _transportFactory = transportFactory;
    }

    /// <summary>
    /// Enumerates deterministic PicoFlasher candidates without opening a transport.
    /// </summary>
    /// <param name="cancellationToken">A token that can cancel enumeration.</param>
    /// <returns>The available physical-device-deduplicated command candidates.</returns>
    internal ValueTask<IReadOnlyList<PicoFlasherDeviceEndpoint>> EnumerateAsync(
        CancellationToken cancellationToken = default)
    {
        return _deviceEnumerator.EnumerateAsync(cancellationToken);
    }

    /// <summary>
    /// Opens the endpoint selected by <paramref name="selector"/> and verifies its firmware version.
    /// </summary>
    /// <param name="selector">The validated command-device selector.</param>
    /// <param name="noProgressTimeout">The positive no-progress deadline for transport I/O.</param>
    /// <param name="cancellationToken">A token that can cancel discovery, opening, or firmware verification.</param>
    /// <returns>A firmware-gated command transport connection.</returns>
    internal async ValueTask<PicoFlasherConnection> OpenAsync(
        PicoFlasherDeviceSelector selector,
        TimeSpan noProgressTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ValidateNoProgressTimeout(noProgressTimeout);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<PicoFlasherDeviceEndpoint> endpoints = await EnumerateAsync(cancellationToken)
            .ConfigureAwait(false);
        PicoFlasherDeviceEndpoint endpoint = SelectEndpoint(endpoints, selector);

        IPicoFlasherTransport? transport = null;
        try
        {
            transport = await _transportFactory
                .OpenAsync(endpoint, noProgressTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (transport is null)
            {
                throw DeviceOpenFailed();
            }

            byte[] buffer = ArrayPool<byte>.Shared.Rent(PicoFlasherProtocol.CommandSize);
            try
            {
                PicoFlasherProtocol.WriteCommand(
                    buffer.AsSpan(0, PicoFlasherProtocol.CommandSize),
                    PicoFlasherCommand.GetVersion,
                    0);
                await transport.WriteAsync(
                    buffer.AsMemory(0, PicoFlasherProtocol.CommandSize),
                    cancellationToken).ConfigureAwait(false);
                await transport.ReadExactlyAsync(
                    buffer.AsMemory(0, PicoFlasherProtocol.StatusSize),
                    cancellationToken).ConfigureAwait(false);
                uint firmwareVersion = PicoFlasherProtocol.ReadStatus(
                    buffer.AsSpan(0, PicoFlasherProtocol.StatusSize));
                if (firmwareVersion < PicoFlasherProtocol.MinimumSupportedFirmwareVersion)
                {
                    throw new OperationFailureException(
                        ExitCode.MissingPrerequisite,
                        "pico-firmware-unsupported",
                        "The connected PicoFlasher firmware is unsupported. Update it to version 4 or later.");
                }

                return new PicoFlasherConnection(endpoint, firmwareVersion, transport);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch
        {
            if (transport is not null)
            {
                await DisposeAfterFailedGateAsync(transport).ConfigureAwait(false);
            }

            throw;
        }
    }

    private static PicoFlasherDeviceEndpoint SelectEndpoint(
        IReadOnlyList<PicoFlasherDeviceEndpoint> endpoints,
        PicoFlasherDeviceSelector selector)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(selector);

        var endpointsByPhysicalDevicePath = new Dictionary<string, PicoFlasherDeviceEndpoint>(
            StringComparer.Ordinal);
        foreach (PicoFlasherDeviceEndpoint endpoint in endpoints)
        {
            if (endpoint.InterfaceNumber != PicoFlasherProtocol.CommandCdcInterfaceNumber ||
                !MatchesSelector(endpoint, selector))
            {
                continue;
            }

            if (!endpointsByPhysicalDevicePath.TryGetValue(
                    endpoint.PhysicalDevicePath,
                    out PicoFlasherDeviceEndpoint? existingEndpoint) ||
                CompareEndpoints(endpoint, existingEndpoint!) < 0)
            {
                endpointsByPhysicalDevicePath[endpoint.PhysicalDevicePath] = endpoint;
            }
        }

        if (endpointsByPhysicalDevicePath.Count == 0)
        {
            throw DeviceNotFound();
        }

        if (selector.SerialNumber is not null && endpointsByPhysicalDevicePath.Count > 1)
        {
            throw DuplicateSerial();
        }

        if (endpointsByPhysicalDevicePath.Count > 1)
        {
            throw DeviceAmbiguous();
        }

        foreach (PicoFlasherDeviceEndpoint endpoint in endpointsByPhysicalDevicePath.Values)
        {
            return endpoint;
        }

        throw new InvalidOperationException("A nonempty endpoint selection must contain one endpoint.");
    }

    private static bool MatchesSelector(
        PicoFlasherDeviceEndpoint endpoint,
        PicoFlasherDeviceSelector selector)
    {
        if (selector.DevicePath is string devicePath)
        {
            return StringComparer.Ordinal.Equals(endpoint.DevicePath, devicePath);
        }

        return selector.SerialNumber is not string serialNumber ||
            StringComparer.Ordinal.Equals(endpoint.SerialNumber, serialNumber);
    }

    private static int CompareEndpoints(PicoFlasherDeviceEndpoint left, PicoFlasherDeviceEndpoint right)
    {
        int physicalDeviceOrder = StringComparer.Ordinal.Compare(left.PhysicalDevicePath, right.PhysicalDevicePath);
        return physicalDeviceOrder != 0
            ? physicalDeviceOrder
            : StringComparer.Ordinal.Compare(left.DevicePath, right.DevicePath);
    }

    private static void ValidateNoProgressTimeout(TimeSpan noProgressTimeout)
    {
        if (!PicoFlasherProtocol.IsValidNoProgressTimeout(noProgressTimeout))
        {
            throw new OperationFailureException(
                ExitCode.Usage,
                "pico-timeout-invalid",
                "The PicoFlasher no-progress timeout must be positive and supported by the serial transport.");
        }
    }

    private static async ValueTask DisposeAfterFailedGateAsync(IPicoFlasherTransport transport)
    {
        try
        {
            await transport.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Gate cleanup must never hide its primary failure.
        }
    }

    private static OperationFailureException DeviceNotFound()
    {
        return new OperationFailureException(
            ExitCode.DeviceUnavailable,
            "pico-device-not-found",
            "No matching PicoFlasher command device is available.");
    }

    private static OperationFailureException DeviceAmbiguous()
    {
        return new OperationFailureException(
            ExitCode.DeviceUnavailable,
            "pico-device-ambiguous",
            "Multiple PicoFlasher command devices are available. Select one with --device or --serial.");
    }

    private static OperationFailureException DuplicateSerial()
    {
        return new OperationFailureException(
            ExitCode.Usage,
            "pico-device-serial-ambiguous",
            "Multiple physical PicoFlasher devices share the selected serial number. Select one with --device.");
    }

    private static OperationFailureException DeviceOpenFailed()
    {
        return new OperationFailureException(
            ExitCode.DeviceUnavailable,
            "pico-device-open-failed",
            "The selected PicoFlasher device could not be opened.");
    }
}

/// <summary>
/// Owns one firmware-gated PicoFlasher command transport.
/// </summary>
internal sealed class PicoFlasherConnection : IAsyncDisposable
{
    private int _disposed;
    private int _retired;
    internal PicoFlasherConnection(
        PicoFlasherDeviceEndpoint endpoint,
        uint firmwareVersion,
        IPicoFlasherTransport transport)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(transport);

        Endpoint = endpoint;
        FirmwareVersion = firmwareVersion;
        Transport = transport;
    }

    /// <summary>
    /// Gets the selected interface-zero command endpoint.
    /// </summary>
    public PicoFlasherDeviceEndpoint Endpoint { get; }

    /// <summary>
    /// Gets the firmware version confirmed by the GET_VERSION gate.
    /// </summary>
    public uint FirmwareVersion { get; }

    /// <summary>
    /// Gets the open command transport.
    /// </summary>
    public IPicoFlasherTransport Transport { get; }

    /// <summary>
    /// Gets whether the transport has been retired after an unrecoverable protocol framing failure.
    /// </summary>
    internal bool IsRetired => Volatile.Read(ref _retired) != 0;

    /// <summary>
    /// Retires and closes the transport so no later operation can reuse a desynchronized protocol stream.
    /// </summary>
    internal ValueTask RetireAsync()
    {
        Interlocked.Exchange(ref _retired, 1);
        return DisposeAsync();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _retired, 1);
        return Interlocked.Exchange(ref _disposed, 1) == 0
            ? Transport.DisposeAsync()
            : ValueTask.CompletedTask;
    }
}
