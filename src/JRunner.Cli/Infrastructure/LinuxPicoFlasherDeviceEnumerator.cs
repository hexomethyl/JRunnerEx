using JRunner.Core.Contracts;
using JRunner.Core.Devices.PicoFlasher;

namespace JRunner.Cli.Infrastructure;

/// <summary>
/// Enumerates one command CDC candidate for each PicoFlasher-compatible USB device on Linux.
/// </summary>
internal sealed class LinuxPicoFlasherDeviceEnumerator : IPicoFlasherDeviceEnumerator
{
    private const string DefaultTtyClassDirectory = "/sys/class/tty";
    private const string DefaultDeviceDirectory = "/dev";
    private const string TtyAcmPrefix = "ttyACM";
    private const string TtyUsbPrefix = "ttyUSB";

    private readonly string ttyClassDirectory;
    private readonly string deviceDirectory;

    /// <summary>
    /// Initializes an enumerator that uses the host Linux sysfs and device-node roots.
    /// </summary>
    internal LinuxPicoFlasherDeviceEnumerator()
        : this(DefaultTtyClassDirectory, DefaultDeviceDirectory)
    {
    }

    /// <summary>
    /// Initializes an enumerator with caller-provided Linux sysfs and device-node roots.
    /// </summary>
    /// <param name="ttyClassDirectory">The directory corresponding to <c>/sys/class/tty</c>.</param>
    /// <param name="deviceDirectory">The directory corresponding to <c>/dev</c>.</param>
    internal LinuxPicoFlasherDeviceEnumerator(string ttyClassDirectory, string deviceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ttyClassDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceDirectory);

        this.ttyClassDirectory = ttyClassDirectory;
        this.deviceDirectory = deviceDirectory;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<PicoFlasherDeviceEndpoint>> EnumerateAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!OperatingSystem.IsLinux())
        {
            throw LinuxDiscoveryRequired();
        }

        List<string> ttyEntries = EnumerateTtyEntries(cancellationToken);
        var endpointsByPhysicalDevicePath = new Dictionary<string, PicoFlasherDeviceEndpoint>(
            StringComparer.Ordinal);

        foreach (string ttyEntry in ttyEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string ttyName = Path.GetFileName(ttyEntry);
            if (!IsCandidateTtyName(ttyName) ||
                !TryCreateEndpoint(ttyEntry, ttyName, out PicoFlasherDeviceEndpoint endpoint))
            {
                continue;
            }

            if (endpointsByPhysicalDevicePath.TryGetValue(
                    endpoint.PhysicalDevicePath,
                    out PicoFlasherDeviceEndpoint? existingEndpoint) &&
                CompareEndpoints(endpoint, existingEndpoint!) >= 0)
            {
                continue;
            }

            endpointsByPhysicalDevicePath[endpoint.PhysicalDevicePath] = endpoint;
        }

        var endpoints = new List<PicoFlasherDeviceEndpoint>(endpointsByPhysicalDevicePath.Values);
        endpoints.Sort(CompareEndpoints);
        return ValueTask.FromResult<IReadOnlyList<PicoFlasherDeviceEndpoint>>(endpoints.AsReadOnly());
    }

    private static int CompareEndpoints(PicoFlasherDeviceEndpoint left, PicoFlasherDeviceEndpoint right)
    {
        int physicalDeviceOrder = StringComparer.Ordinal.Compare(left.PhysicalDevicePath, right.PhysicalDevicePath);
        return physicalDeviceOrder != 0
            ? physicalDeviceOrder
            : StringComparer.Ordinal.Compare(left.DevicePath, right.DevicePath);
    }

    private List<string> EnumerateTtyEntries(CancellationToken cancellationToken)
    {
        var entries = new List<string>();

        try
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(
                         ttyClassDirectory,
                         "*",
                         new EnumerationOptions
                         {
                             AttributesToSkip = 0,
                             IgnoreInaccessible = false,
                             RecurseSubdirectories = false,
                             ReturnSpecialDirectories = false,
                         }))
            {
                cancellationToken.ThrowIfCancellationRequested();
                entries.Add(entry);
            }

            return entries;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or
                ArgumentException or NotSupportedException)
        {
            throw DiscoveryUnavailable();
        }
    }

    private bool TryCreateEndpoint(
        string ttyClassEntry,
        string ttyName,
        out PicoFlasherDeviceEndpoint endpoint)
    {
        endpoint = null!;

        if (!TryResolveActualDirectory(ttyClassEntry, out DirectoryInfo ttyDirectory))
        {
            return false;
        }

        string deviceLinkPath;
        try
        {
            deviceLinkPath = Path.Combine(ttyDirectory.FullName, "device");
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }

        if (!TryResolveActualDirectory(deviceLinkPath, out DirectoryInfo deviceStartDirectory))
        {
            return false;
        }

        if (!TryFindUsbInterface(
                deviceStartDirectory,
                out DirectoryInfo interfaceDirectory,
                out int interfaceNumber) ||
            interfaceNumber != PicoFlasherProtocol.CommandCdcInterfaceNumber ||
            !TryFindUsbDevice(
                interfaceDirectory.Parent,
                out DirectoryInfo usbDeviceDirectory,
                out ushort vendorId,
                out ushort productId) ||
            vendorId != PicoFlasherProtocol.VendorId ||
            productId != PicoFlasherProtocol.ProductId ||
            !TryReadSerialNumber(usbDeviceDirectory, out string? serialNumber))
        {
            return false;
        }

        string devicePath;
        try
        {
            devicePath = Path.Combine(this.deviceDirectory, ttyName);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }

        if (!DeviceNodeExists(devicePath))
        {
            return false;
        }

        endpoint = new PicoFlasherDeviceEndpoint(
            devicePath,
            serialNumber,
            interfaceNumber,
            usbDeviceDirectory.FullName);
        return true;
    }

    private static bool DeviceNodeExists(string devicePath)
    {
        try
        {
            if (!File.Exists(devicePath))
            {
                return false;
            }

            FileSystemInfo? resolvedTarget = new FileInfo(devicePath).ResolveLinkTarget(returnFinalTarget: true);
            return resolvedTarget is null ||
                resolvedTarget is FileInfo resolvedFile && resolvedFile.Exists;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or
                ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryResolveActualDirectory(string path, out DirectoryInfo directory)
    {
        directory = null!;

        try
        {
            var candidate = new DirectoryInfo(path);
            if (!candidate.Exists)
            {
                return false;
            }

            FileSystemInfo? resolvedTarget = candidate.ResolveLinkTarget(returnFinalTarget: true);
            if (resolvedTarget is null)
            {
                directory = candidate;
                return true;
            }

            if (resolvedTarget is not DirectoryInfo resolvedDirectory || !resolvedDirectory.Exists)
            {
                return false;
            }

            directory = resolvedDirectory;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or
                ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryFindUsbInterface(
        DirectoryInfo deviceDirectory,
        out DirectoryInfo interfaceDirectory,
        out int interfaceNumber)
    {
        interfaceDirectory = null!;
        interfaceNumber = default;

        for (DirectoryInfo? current = deviceDirectory; current is not null; current = current.Parent)
        {
            SysfsAttribute attribute = ReadSysfsAttribute(current, "bInterfaceNumber");
            if (attribute.Status == SysfsAttributeStatus.Missing)
            {
                continue;
            }

            if (attribute.Status != SysfsAttributeStatus.Present ||
                attribute.Value is not string value ||
                !TryParseHexAttribute(value, byte.MaxValue, out uint parsedNumber))
            {
                return false;
            }

            interfaceDirectory = current;
            interfaceNumber = (int)parsedNumber;
            return true;
        }

        return false;
    }

    private static bool TryFindUsbDevice(
        DirectoryInfo? startDirectory,
        out DirectoryInfo usbDeviceDirectory,
        out ushort vendorId,
        out ushort productId)
    {
        usbDeviceDirectory = null!;
        vendorId = default;
        productId = default;

        for (DirectoryInfo? current = startDirectory; current is not null; current = current.Parent)
        {
            SysfsAttribute vendorAttribute = ReadSysfsAttribute(current, "idVendor");
            SysfsAttribute productAttribute = ReadSysfsAttribute(current, "idProduct");

            if (vendorAttribute.Status == SysfsAttributeStatus.Missing &&
                productAttribute.Status == SysfsAttributeStatus.Missing)
            {
                continue;
            }

            if (vendorAttribute.Status != SysfsAttributeStatus.Present ||
                productAttribute.Status != SysfsAttributeStatus.Present ||
                vendorAttribute.Value is not string vendorValue ||
                productAttribute.Value is not string productValue ||
                !TryParseHexAttribute(vendorValue, ushort.MaxValue, out uint parsedVendorId) ||
                !TryParseHexAttribute(productValue, ushort.MaxValue, out uint parsedProductId))
            {
                return false;
            }

            usbDeviceDirectory = current;
            vendorId = (ushort)parsedVendorId;
            productId = (ushort)parsedProductId;
            return true;
        }

        return false;
    }

    private static bool TryReadSerialNumber(DirectoryInfo usbDeviceDirectory, out string? serialNumber)
    {
        serialNumber = null;
        SysfsAttribute serialAttribute = ReadSysfsAttribute(usbDeviceDirectory, "serial");

        if (serialAttribute.Status == SysfsAttributeStatus.Missing)
        {
            return true;
        }

        if (serialAttribute.Status != SysfsAttributeStatus.Present || serialAttribute.Value is not string value)
        {
            return false;
        }

        string trimmedValue = TrimSysfsLineEnding(value);
        if (trimmedValue.Length == 0)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(trimmedValue))
        {
            return false;
        }

        serialNumber = trimmedValue;
        return true;
    }

    private static SysfsAttribute ReadSysfsAttribute(DirectoryInfo directory, string name)
    {
        string attributePath;
        try
        {
            attributePath = Path.Combine(directory.FullName, name);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return new SysfsAttribute(SysfsAttributeStatus.Unavailable, null);
        }

        try
        {
            return new SysfsAttribute(SysfsAttributeStatus.Present, File.ReadAllText(attributePath));
        }
        catch (FileNotFoundException)
        {
            return new SysfsAttribute(SysfsAttributeStatus.Missing, null);
        }
        catch (DirectoryNotFoundException)
        {
            return new SysfsAttribute(SysfsAttributeStatus.Unavailable, null);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or
                ArgumentException or NotSupportedException)
        {
            return new SysfsAttribute(SysfsAttributeStatus.Unavailable, null);
        }
    }

    private static bool TryParseHexAttribute(string value, uint maximumValue, out uint parsedValue)
    {
        string text = TrimSysfsLineEnding(value);
        parsedValue = default;

        if (text.Length == 0)
        {
            return false;
        }

        foreach (char character in text)
        {
            int digit = character switch
            {
                >= '0' and <= '9' => character - '0',
                >= 'a' and <= 'f' => character - 'a' + 10,
                >= 'A' and <= 'F' => character - 'A' + 10,
                _ => -1,
            };

            if (digit < 0 || parsedValue > (maximumValue - (uint)digit) / 16)
            {
                parsedValue = default;
                return false;
            }

            parsedValue = (parsedValue * 16) + (uint)digit;
        }

        return true;
    }

    private static string TrimSysfsLineEnding(string value)
    {
        int trimmedLength = value.Length;
        if (trimmedLength > 0 && value[trimmedLength - 1] == '\n')
        {
            trimmedLength--;
            if (trimmedLength > 0 && value[trimmedLength - 1] == '\r')
            {
                trimmedLength--;
            }
        }
        else if (trimmedLength > 0 && value[trimmedLength - 1] == '\r')
        {
            trimmedLength--;
        }

        return trimmedLength == value.Length ? value : value[..trimmedLength];
    }

    private static bool IsCandidateTtyName(string ttyName)
    {
        return ttyName.StartsWith(TtyAcmPrefix, StringComparison.Ordinal) ||
            ttyName.StartsWith(TtyUsbPrefix, StringComparison.Ordinal);
    }

    private static OperationFailureException LinuxDiscoveryRequired()
    {
        return new OperationFailureException(
            ExitCode.MissingPrerequisite,
            "pico-device-discovery-unsupported",
            "PicoFlasher device discovery requires Linux.");
    }

    private static OperationFailureException DiscoveryUnavailable()
    {
        return new OperationFailureException(
            ExitCode.DeviceUnavailable,
            "pico-device-discovery-failed",
            "PicoFlasher device discovery is unavailable.");
    }

    private readonly record struct SysfsAttribute(SysfsAttributeStatus Status, string? Value);

    private enum SysfsAttributeStatus
    {
        Missing,
        Present,
        Unavailable,
    }
}
