using JRunner.Cli.Infrastructure;
using JRunner.Core.Contracts;
using JRunner.Core.Devices.PicoFlasher;
using Xunit;

namespace JRunner.Cli.Tests;

public sealed class PicoFlasherConnectionFactoryTests
{
    private static readonly TimeSpan DefaultNoProgressTimeout = PicoFlasherProtocol.DefaultNoProgressTimeout;

    [Fact]
    public async Task Enumeration_reports_candidates_without_opening_a_transport()
    {
        var endpoint = Endpoint("/dev/ttyACM0", "serial", "/sys/devices/usb1/1-2");
        var enumerator = new FakeEnumerator([endpoint]);
        var transportFactory = new FakeTransportFactory(new FakeTransport(Status(4)));
        var factory = new PicoFlasherConnectionFactory(enumerator, transportFactory);

        IReadOnlyList<PicoFlasherDeviceEndpoint> endpoints = await factory.EnumerateAsync();

        Assert.Same(endpoint, Assert.Single(endpoints));
        Assert.Equal(1, enumerator.EnumerateCallCount);
        Assert.Equal(0, transportFactory.OpenCallCount);
    }

    [Fact]
    public async Task No_command_endpoint_is_a_typed_device_not_found_failure()
    {
        var transportFactory = new FakeTransportFactory(new FakeTransport(Status(4)));
        var factory = new PicoFlasherConnectionFactory(new FakeEnumerator([]), transportFactory);

        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
            () => factory.OpenAsync(PicoFlasherDeviceSelector.Unspecified, DefaultNoProgressTimeout).AsTask());

        Assert.Equal(ExitCode.DeviceUnavailable, exception.Code);
        Assert.Equal("pico-device-not-found", exception.Kind);
        Assert.Equal(0, transportFactory.OpenCallCount);
    }

    [Fact]
    public async Task Nonmatching_serial_is_a_typed_device_not_found_failure()
    {
        var endpoint = Endpoint("/dev/ttyACM2", "present", "/sys/devices/usb1/1-2");
        var transportFactory = new FakeTransportFactory(new FakeTransport(Status(4)));
        var factory = new PicoFlasherConnectionFactory(new FakeEnumerator([endpoint]), transportFactory);

        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
            () => factory.OpenAsync(
                PicoFlasherDeviceSelector.Create(null, "missing"),
                DefaultNoProgressTimeout).AsTask());

        Assert.Equal(ExitCode.DeviceUnavailable, exception.Code);
        Assert.Equal("pico-device-not-found", exception.Kind);
        Assert.Equal(0, transportFactory.OpenCallCount);
    }

    [Fact]
    public async Task Noncandidate_device_path_is_a_typed_device_not_found_failure_without_opening_it()
    {
        var endpoint = Endpoint("/dev/ttyACM2", "present", "/sys/devices/usb1/1-2");
        var transportFactory = new FakeTransportFactory(new FakeTransport(Status(4)));
        var factory = new PicoFlasherConnectionFactory(new FakeEnumerator([endpoint]), transportFactory);

        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
            () => factory.OpenAsync(
                PicoFlasherDeviceSelector.Create("/dev/ttyACM9", null),
                DefaultNoProgressTimeout).AsTask());

        Assert.Equal(ExitCode.DeviceUnavailable, exception.Code);
        Assert.Equal("pico-device-not-found", exception.Kind);
        Assert.Equal(0, transportFactory.OpenCallCount);
    }

    [Fact]
    public async Task Duplicate_matching_physical_devices_with_one_serial_are_usage_failure()
    {
        var firstEndpoint = Endpoint("/dev/ttyACM0", "duplicate", "/sys/devices/usb1/1-2");
        var secondEndpoint = Endpoint("/dev/ttyACM1", "duplicate", "/sys/devices/usb1/1-3");
        var transportFactory = new FakeTransportFactory(new FakeTransport(Status(4)));
        var factory = new PicoFlasherConnectionFactory(
            new FakeEnumerator([firstEndpoint, secondEndpoint]),
            transportFactory);

        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
            () => factory.OpenAsync(
                PicoFlasherDeviceSelector.Create(null, "duplicate"),
                DefaultNoProgressTimeout).AsTask());

        Assert.Equal(ExitCode.Usage, exception.Code);
        Assert.Equal("pico-device-serial-ambiguous", exception.Kind);
        Assert.Contains("--device", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, transportFactory.OpenCallCount);
    }

    [Fact]
    public async Task Duplicate_endpoints_for_one_physical_device_select_the_deterministic_command_path()
    {
        var laterEndpoint = Endpoint("/dev/ttyUSB9", "duplicate", "/sys/devices/usb1/1-2");
        var selectedEndpoint = Endpoint("/dev/ttyACM7", "duplicate", "/sys/devices/usb1/1-2");
        var transport = new FakeTransport(Status(4));
        var transportFactory = new FakeTransportFactory(transport);
        var factory = new PicoFlasherConnectionFactory(
            new FakeEnumerator([laterEndpoint, selectedEndpoint]),
            transportFactory);

        await using PicoFlasherConnection connection = await factory.OpenAsync(
            PicoFlasherDeviceSelector.Create(null, "duplicate"),
            DefaultNoProgressTimeout);

        Assert.Same(selectedEndpoint, connection.Endpoint);
        Assert.Same(selectedEndpoint, transportFactory.OpenedEndpoint);
    }

    [Fact]
    public async Task Multiple_command_endpoints_without_a_selector_are_ambiguous()
    {
        var endpointA = Endpoint("/dev/ttyACM1", "one", "/sys/devices/usb1/1-2");
        var endpointB = Endpoint("/dev/ttyACM0", "two", "/sys/devices/usb1/1-3");
        var debugEndpoint = Endpoint("/dev/ttyACM3", "two", "/sys/devices/usb1/1-4", interfaceNumber: 1);
        var transportFactory = new FakeTransportFactory(new FakeTransport(Status(4)));
        var factory = new PicoFlasherConnectionFactory(
            new FakeEnumerator([endpointA, debugEndpoint, endpointB]),
            transportFactory);

        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
            () => factory.OpenAsync(PicoFlasherDeviceSelector.Unspecified, DefaultNoProgressTimeout).AsTask());

        Assert.Equal(ExitCode.DeviceUnavailable, exception.Code);
        Assert.Equal("pico-device-ambiguous", exception.Kind);
        Assert.Equal(0, transportFactory.OpenCallCount);
    }

    [Fact]
    public async Task Device_path_selection_uses_the_exact_matching_interface_zero_endpoint()
    {
        var selectedEndpoint = Endpoint("/dev/ttyACM4", "selected", "/sys/devices/usb1/1-2");
        var debugEndpoint = Endpoint("/dev/ttyACM0", "selected", "/sys/devices/usb1/1-2", interfaceNumber: 1);
        var otherEndpoint = Endpoint("/dev/ttyACM1", "other", "/sys/devices/usb1/1-3");
        var transport = new FakeTransport(Status(4));
        var transportFactory = new FakeTransportFactory(transport);
        var factory = new PicoFlasherConnectionFactory(
            new FakeEnumerator([debugEndpoint, otherEndpoint, selectedEndpoint]),
            transportFactory);

        await using PicoFlasherConnection connection = await factory.OpenAsync(
            PicoFlasherDeviceSelector.Create("/dev/ttyACM4", null),
            DefaultNoProgressTimeout);

        Assert.Same(selectedEndpoint, connection.Endpoint);
        Assert.Same(selectedEndpoint, transportFactory.OpenedEndpoint);
        Assert.Equal((uint)4, connection.FirmwareVersion);
    }

    [Fact]
    public async Task Serial_selection_uses_the_matching_interface_zero_endpoint()
    {
        var selectedEndpoint = Endpoint("/dev/ttyACM4", "selected", "/sys/devices/usb1/1-2");
        var debugEndpoint = Endpoint("/dev/ttyACM0", "selected", "/sys/devices/usb1/1-2", interfaceNumber: 1);
        var otherEndpoint = Endpoint("/dev/ttyACM1", "other", "/sys/devices/usb1/1-3");
        var transport = new FakeTransport(Status(4));
        var transportFactory = new FakeTransportFactory(transport);
        var factory = new PicoFlasherConnectionFactory(
            new FakeEnumerator([debugEndpoint, otherEndpoint, selectedEndpoint]),
            transportFactory);

        await using PicoFlasherConnection connection = await factory.OpenAsync(
            PicoFlasherDeviceSelector.Create(null, "selected"),
            DefaultNoProgressTimeout);

        Assert.Same(selectedEndpoint, connection.Endpoint);
        Assert.Same(selectedEndpoint, transportFactory.OpenedEndpoint);
        Assert.Equal((uint)4, connection.FirmwareVersion);
    }

    [Fact]
    public async Task Open_forwards_the_configured_no_progress_timeout_to_the_transport_factory()
    {
        var endpoint = Endpoint("/dev/ttyACM0", "serial", "/sys/devices/usb1/1-2");
        var transport = new FakeTransport(Status(4));
        var transportFactory = new FakeTransportFactory(transport);
        var factory = new PicoFlasherConnectionFactory(new FakeEnumerator([endpoint]), transportFactory);
        TimeSpan noProgressTimeout = TimeSpan.FromMilliseconds(1_234);

        await using PicoFlasherConnection connection = await factory.OpenAsync(
            PicoFlasherDeviceSelector.Unspecified,
            noProgressTimeout);

        Assert.Equal(noProgressTimeout, transportFactory.OpenedNoProgressTimeout);
    }

    [Fact]
    public async Task Invalid_no_progress_timeout_is_usage_failure_before_enumeration()
    {
        var enumerator = new FakeEnumerator([]);
        var transportFactory = new FakeTransportFactory(new FakeTransport(Status(4)));
        var factory = new PicoFlasherConnectionFactory(enumerator, transportFactory);

        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
            () => factory.OpenAsync(PicoFlasherDeviceSelector.Unspecified, TimeSpan.Zero).AsTask());

        Assert.Equal(ExitCode.Usage, exception.Code);
        Assert.Equal("pico-timeout-invalid", exception.Kind);
        Assert.Equal(0, enumerator.EnumerateCallCount);
        Assert.Equal(0, transportFactory.OpenCallCount);
    }

    [Fact]
    public async Task Version_gate_writes_one_exact_frame_and_decodes_little_endian_response()
    {
        var endpoint = Endpoint("/dev/ttyACM0", "serial", "/sys/devices/usb1/1-2");
        var transport = new FakeTransport([0x12, 0x34, 0x56, 0x78]);
        var transportFactory = new FakeTransportFactory(transport);
        var factory = new PicoFlasherConnectionFactory(new FakeEnumerator([endpoint]), transportFactory);

        await using PicoFlasherConnection connection = await factory.OpenAsync(
            PicoFlasherDeviceSelector.Unspecified,
            DefaultNoProgressTimeout);

        Assert.Same(endpoint, connection.Endpoint);
        Assert.Same(transport, connection.Transport);
        Assert.Equal(0x78563412U, connection.FirmwareVersion);
        Assert.Equal(1, transportFactory.OpenCallCount);
        Assert.Single(transport.WrittenFrames);
        Assert.Equal(new byte[] { (byte)PicoFlasherCommand.GetVersion, 0x00, 0x00, 0x00, 0x00 }, transport.WrittenFrames[0]);
        Assert.Equal(1, transport.ReadCallCount);
        Assert.Equal(PicoFlasherProtocol.StatusSize, transport.LastReadLength);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Firmware_versions_one_through_three_fail_after_exactly_get_version(int firmwareVersion)
    {
        var endpoint = Endpoint("/dev/ttyACM0", "serial", "/sys/devices/usb1/1-2");
        var transport = new FakeTransport(Status(checked((uint)firmwareVersion)));
        var factory = new PicoFlasherConnectionFactory(
            new FakeEnumerator([endpoint]),
            new FakeTransportFactory(transport));

        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
            () => factory.OpenAsync(PicoFlasherDeviceSelector.Unspecified, DefaultNoProgressTimeout).AsTask());

        Assert.Equal(ExitCode.MissingPrerequisite, exception.Code);
        Assert.Equal("pico-firmware-unsupported", exception.Kind);
        Assert.Contains("Update", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, transport.DisposeCallCount);
        Assert.Single(transport.WrittenFrames);
        Assert.Equal(
            new byte[] { (byte)PicoFlasherCommand.GetVersion, 0x00, 0x00, 0x00, 0x00 },
            transport.WrittenFrames[0]);
        Assert.Equal(1, transport.ReadCallCount);
    }

    [Fact]
    public async Task Gate_failure_remains_primary_when_transport_disposal_fails()
    {
        var endpoint = Endpoint("/dev/ttyACM0", "serial", "/sys/devices/usb1/1-2");
        var transport = new FakeTransport(
            Status(3),
            disposeException: new InvalidOperationException("cleanup failure"));
        var factory = new PicoFlasherConnectionFactory(
            new FakeEnumerator([endpoint]),
            new FakeTransportFactory(transport));

        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
            () => factory.OpenAsync(PicoFlasherDeviceSelector.Unspecified, DefaultNoProgressTimeout).AsTask());

        Assert.Equal(ExitCode.MissingPrerequisite, exception.Code);
        Assert.Equal("pico-firmware-unsupported", exception.Kind);
        Assert.Equal(1, transport.DisposeCallCount);
    }

    private static PicoFlasherDeviceEndpoint Endpoint(
        string devicePath,
        string? serialNumber,
        string physicalDevicePath,
        int interfaceNumber = PicoFlasherProtocol.CommandCdcInterfaceNumber)
    {
        return new PicoFlasherDeviceEndpoint(
            devicePath,
            serialNumber,
            interfaceNumber,
            physicalDevicePath);
    }

    private static byte[] Status(uint value)
    {
        return
        [
            unchecked((byte)value),
            unchecked((byte)(value >> 8)),
            unchecked((byte)(value >> 16)),
            unchecked((byte)(value >> 24)),
        ];
    }

    private sealed class FakeEnumerator : IPicoFlasherDeviceEnumerator
    {
        private readonly IReadOnlyList<PicoFlasherDeviceEndpoint> _endpoints;

        internal FakeEnumerator(IReadOnlyList<PicoFlasherDeviceEndpoint> endpoints)
        {
            _endpoints = endpoints;
        }

        internal int EnumerateCallCount { get; private set; }

        public ValueTask<IReadOnlyList<PicoFlasherDeviceEndpoint>> EnumerateAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnumerateCallCount++;
            return new ValueTask<IReadOnlyList<PicoFlasherDeviceEndpoint>>(_endpoints);
        }
    }

    private sealed class FakeTransportFactory : IPicoFlasherTransportFactory
    {
        private readonly IPicoFlasherTransport _transport;

        internal FakeTransportFactory(IPicoFlasherTransport transport)
        {
            _transport = transport;
        }

        internal int OpenCallCount { get; private set; }

        internal PicoFlasherDeviceEndpoint? OpenedEndpoint { get; private set; }

        internal TimeSpan OpenedNoProgressTimeout { get; private set; }

        public ValueTask<IPicoFlasherTransport> OpenAsync(
            PicoFlasherDeviceEndpoint endpoint,
            TimeSpan noProgressTimeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenCallCount++;
            OpenedEndpoint = endpoint;
            OpenedNoProgressTimeout = noProgressTimeout;
            return new ValueTask<IPicoFlasherTransport>(_transport);
        }
    }

    private sealed class FakeTransport : IPicoFlasherTransport
    {
        private readonly byte[] _response;
        private readonly Exception? _disposeException;

        internal FakeTransport(byte[] response, Exception? disposeException = null)
        {
            _response = response;
            _disposeException = disposeException;
        }

        internal List<byte[]> WrittenFrames { get; } = [];

        internal int ReadCallCount { get; private set; }

        internal int LastReadLength { get; private set; }

        internal int DisposeCallCount { get; private set; }

        public ValueTask WriteAsync(
            ReadOnlyMemory<byte> source,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WrittenFrames.Add(source.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask ReadExactlyAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCallCount++;
            LastReadLength = destination.Length;
            _response.AsSpan().CopyTo(destination.Span);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCallCount++;
            return _disposeException is null
                ? ValueTask.CompletedTask
                : new ValueTask(Task.FromException(_disposeException));
        }
    }
}
