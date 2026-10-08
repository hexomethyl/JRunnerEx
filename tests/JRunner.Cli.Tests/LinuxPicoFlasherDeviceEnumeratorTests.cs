using JRunner.Cli.Infrastructure;
using JRunner.Core.Contracts;
using JRunner.Core.Devices.PicoFlasher;
using Xunit;

namespace JRunner.Cli.Tests;

public sealed class LinuxPicoFlasherDeviceEnumeratorTests
{
    [Fact]
    public async Task Enumerates_only_existing_command_cdc_nodes_and_preserves_serial_text()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var tree = new SyntheticLinuxDeviceTree();
        tree.AddTty(
            "ttyUSB9",
            interfaceNumber: "00\n",
            vendorId: "600D\r\n",
            productId: "7001\n",
            serialNumber: "command serial \r\n",
            createDeviceNode: true);
        tree.AddTty(
            "ttyACM10",
            interfaceNumber: "00\n",
            vendorId: "600d\n",
            productId: "7001\n",
            serialNumber: null,
            createDeviceNode: true);
        tree.AddTty(
            "ttyACM0",
            interfaceNumber: "02\n",
            vendorId: "600d\n",
            productId: "7001\n",
            serialNumber: "debug-port\n",
            createDeviceNode: true);
        tree.AddTty(
            "ttyACM1",
            interfaceNumber: "00\n",
            vendorId: "600d\n",
            productId: "7002\n",
            serialNumber: "wrong-product\n",
            createDeviceNode: true);
        tree.AddTty(
            "ttyACM2",
            interfaceNumber: "00\n",
            vendorId: "invalid\n",
            productId: "7001\n",
            serialNumber: "malformed-vendor\n",
            createDeviceNode: true);
        tree.AddTty(
            "ttyACM3",
            interfaceNumber: "not-a-number\n",
            vendorId: "600d\n",
            productId: "7001\n",
            serialNumber: "malformed-interface\n",
            createDeviceNode: true);
        tree.AddTty(
            "ttyUSB4",
            interfaceNumber: "00\n",
            vendorId: "600d\n",
            productId: "7001\n",
            serialNumber: "no-device-node\n",
            createDeviceNode: false);
        tree.AddTty(
            "ttyUSB5",
            interfaceNumber: "00\n",
            vendorId: "600d\n",
            productId: "7001\n",
            serialNumber: "removed-device-node\n",
            createDeviceNode: false);
        tree.CreateDanglingDeviceNodeLink("ttyUSB5");
        tree.AddTty(
            "ttyS0",
            interfaceNumber: "00\n",
            vendorId: "600d\n",
            productId: "7001\n",
            serialNumber: "not-a-cdc-name\n",
            createDeviceNode: true);

        IReadOnlyList<PicoFlasherDeviceEndpoint> endpoints = await new LinuxPicoFlasherDeviceEnumerator(
                tree.TtyClassDirectory,
                tree.DeviceDirectory)
            .EnumerateAsync();

        Assert.Equal(
            new[]
            {
                tree.DevicePath("ttyUSB9"),
                tree.DevicePath("ttyACM10"),
            },
            endpoints.Select(endpoint => endpoint.DevicePath));
        Assert.All(endpoints, endpoint => Assert.Equal(PicoFlasherProtocol.CommandCdcInterfaceNumber, endpoint.InterfaceNumber));

        PicoFlasherDeviceEndpoint commandEndpoint = Assert.Single(
            endpoints,
            endpoint => endpoint.DevicePath == tree.DevicePath("ttyUSB9"));
        Assert.Equal(tree.PhysicalDevicePath("usb-device-0"), commandEndpoint.PhysicalDevicePath);
        Assert.Equal("command serial ", commandEndpoint.SerialNumber);

        PicoFlasherDeviceEndpoint noSerialEndpoint = Assert.Single(
            endpoints,
            endpoint => endpoint.DevicePath == tree.DevicePath("ttyACM10"));
        Assert.Equal(tree.PhysicalDevicePath("usb-device-1"), noSerialEndpoint.PhysicalDevicePath);
        Assert.Null(noSerialEndpoint.SerialNumber);
    }

    [Fact]
    public async Task Sorts_endpoints_by_physical_device_path_with_ordinal_ordering()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var tree = new SyntheticLinuxDeviceTree();
        tree.AddTty(
            "ttyUSB0",
            "00\n",
            "600d\n",
            "7001\n",
            null,
            createDeviceNode: true,
            physicalDeviceName: "usb-device-z");
        tree.AddTty(
            "ttyACM2",
            "00\n",
            "600d\n",
            "7001\n",
            null,
            createDeviceNode: true,
            physicalDeviceName: "usb-device-b");
        tree.AddTty(
            "ttyACM11",
            "00\n",
            "600d\n",
            "7001\n",
            null,
            createDeviceNode: true,
            physicalDeviceName: "usb-device-a");

        IReadOnlyList<PicoFlasherDeviceEndpoint> endpoints = await new LinuxPicoFlasherDeviceEnumerator(
                tree.TtyClassDirectory,
                tree.DeviceDirectory)
            .EnumerateAsync();

        Assert.Equal(
            new[]
            {
                tree.PhysicalDevicePath("usb-device-a"),
                tree.PhysicalDevicePath("usb-device-b"),
                tree.PhysicalDevicePath("usb-device-z"),
            },
            endpoints.Select(endpoint => endpoint.PhysicalDevicePath));
        Assert.Equal(
            new[]
            {
                tree.DevicePath("ttyACM11"),
                tree.DevicePath("ttyACM2"),
                tree.DevicePath("ttyUSB0"),
            },
            endpoints.Select(endpoint => endpoint.DevicePath));
    }

    [Fact]
    public async Task Deduplicates_command_candidates_by_physical_usb_device()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var tree = new SyntheticLinuxDeviceTree();
        tree.AddTty(
            "ttyUSB9",
            "00\n",
            "600d\n",
            "7001\n",
            "duplicate-physical\n",
            createDeviceNode: true,
            physicalDeviceName: "usb-device-z");
        tree.AddTty(
            "ttyACM7",
            "00\n",
            "600d\n",
            "7001\n",
            "duplicate-physical\n",
            createDeviceNode: true,
            physicalDeviceName: "usb-device-z");
        tree.AddTty(
            "ttyACM1",
            "00\n",
            "600d\n",
            "7001\n",
            "other-physical\n",
            createDeviceNode: true,
            physicalDeviceName: "usb-device-a");

        IReadOnlyList<PicoFlasherDeviceEndpoint> endpoints = await new LinuxPicoFlasherDeviceEnumerator(
                tree.TtyClassDirectory,
                tree.DeviceDirectory)
            .EnumerateAsync();

        Assert.Equal(
            new[]
            {
                tree.DevicePath("ttyACM1"),
                tree.DevicePath("ttyACM7"),
            },
            endpoints.Select(endpoint => endpoint.DevicePath));
        Assert.Equal(
            new[]
            {
                tree.PhysicalDevicePath("usb-device-a"),
                tree.PhysicalDevicePath("usb-device-z"),
            },
            endpoints.Select(endpoint => endpoint.PhysicalDevicePath));
    }

    [Fact]
    public async Task Missing_tty_class_root_is_a_sanitized_device_discovery_failure()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var tree = new SyntheticLinuxDeviceTree();
        var enumerator = new LinuxPicoFlasherDeviceEnumerator(
            Path.Combine(tree.RootDirectory, "missing-tty-class"),
            tree.DeviceDirectory);

        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
            () => enumerator.EnumerateAsync().AsTask());

        Assert.Equal(ExitCode.DeviceUnavailable, exception.Code);
        Assert.Equal("pico-device-discovery-failed", exception.Kind);
        Assert.Equal("PicoFlasher device discovery is unavailable.", exception.Message);
    }

    [Fact]
    public async Task Cancellation_is_preserved()
    {
        using var tree = new SyntheticLinuxDeviceTree();
        var enumerator = new LinuxPicoFlasherDeviceEnumerator(tree.TtyClassDirectory, tree.DeviceDirectory);
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => enumerator.EnumerateAsync(cancellationSource.Token).AsTask());
    }

    [Fact]
    public void Non_linux_discovery_is_a_typed_missing_prerequisite_failure()
    {
        if (OperatingSystem.IsLinux())
        {
            return;
        }

        OperationFailureException exception = Assert.Throws<OperationFailureException>(
            () => new LinuxPicoFlasherDeviceEnumerator().EnumerateAsync().GetAwaiter().GetResult());

        Assert.Equal(ExitCode.MissingPrerequisite, exception.Code);
        Assert.Equal("pico-device-discovery-unsupported", exception.Kind);
        Assert.Equal("PicoFlasher device discovery requires Linux.", exception.Message);
    }

    private sealed class SyntheticLinuxDeviceTree : IDisposable
    {
        private readonly string physicalDevicesDirectory;
        private int nextDeviceIndex;

        internal SyntheticLinuxDeviceTree()
        {
            RootDirectory = Path.Combine(
                Path.GetTempPath(),
                $"jrunner-linux-pico-enumerator-{Guid.NewGuid():N}");
            TtyClassDirectory = Path.Combine(RootDirectory, "sys", "class", "tty");
            DeviceDirectory = Path.Combine(RootDirectory, "dev");
            physicalDevicesDirectory = Path.Combine(RootDirectory, "sys", "devices");

            Directory.CreateDirectory(TtyClassDirectory);
            Directory.CreateDirectory(DeviceDirectory);
            Directory.CreateDirectory(physicalDevicesDirectory);
        }

        internal string RootDirectory { get; }

        internal string TtyClassDirectory { get; }

        internal string DeviceDirectory { get; }

        internal void AddTty(
            string ttyName,
            string? interfaceNumber,
            string? vendorId,
            string? productId,
            string? serialNumber,
            bool createDeviceNode,
            string? physicalDeviceName = null)
        {
            physicalDeviceName ??= $"usb-device-{nextDeviceIndex++}";
            string usbDeviceDirectory = Path.Combine(physicalDevicesDirectory, physicalDeviceName);
            string interfaceDirectory = Path.Combine(
                usbDeviceDirectory,
                $"{physicalDeviceName}:1.0-{ttyName}");
            string ttyDirectory = Path.Combine(interfaceDirectory, "tty", ttyName);
            Directory.CreateDirectory(ttyDirectory);

            WriteAttribute(interfaceDirectory, "bInterfaceNumber", interfaceNumber);
            WriteAttribute(usbDeviceDirectory, "idVendor", vendorId);
            WriteAttribute(usbDeviceDirectory, "idProduct", productId);
            WriteAttribute(usbDeviceDirectory, "serial", serialNumber);

            Directory.CreateSymbolicLink(Path.Combine(ttyDirectory, "device"), interfaceDirectory);
            Directory.CreateSymbolicLink(Path.Combine(TtyClassDirectory, ttyName), ttyDirectory);

            if (createDeviceNode)
            {
                File.WriteAllBytes(DevicePath(ttyName), Array.Empty<byte>());
            }
        }

        internal void CreateDanglingDeviceNodeLink(string ttyName)
        {
            File.CreateSymbolicLink(
                DevicePath(ttyName),
                Path.Combine(RootDirectory, $"missing-{ttyName}"));
        }

        internal string DevicePath(string ttyName)
        {
            return Path.Combine(DeviceDirectory, ttyName);
        }

        internal string PhysicalDevicePath(string physicalDeviceName)
        {
            return Path.Combine(physicalDevicesDirectory, physicalDeviceName);
        }

        public void Dispose()
        {
            if (Directory.Exists(RootDirectory))
            {
                Directory.Delete(RootDirectory, recursive: true);
            }
        }

        private static void WriteAttribute(string directory, string name, string? value)
        {
            if (value is not null)
            {
                File.WriteAllText(Path.Combine(directory, name), value);
            }
        }
    }
}
