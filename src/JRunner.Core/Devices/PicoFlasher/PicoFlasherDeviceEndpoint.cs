namespace JRunner.Core.Devices.PicoFlasher;

/// <summary>
/// Identifies one physical PicoFlasher-compatible USB device through its command CDC endpoint.
/// </summary>
public sealed record PicoFlasherDeviceEndpoint
{
    /// <summary>
    /// Creates an immutable command CDC endpoint identifier.
    /// </summary>
    /// <param name="devicePath">The platform-provided command TTY path.</param>
    /// <param name="serialNumber">The optional USB serial number.</param>
    /// <param name="interfaceNumber">The nonnegative USB interface number for the endpoint.</param>
    /// <param name="physicalDevicePath">
    /// The canonical physical USB-device identity. When omitted for a synthetic or manually supplied endpoint,
    /// <paramref name="devicePath"/> is used as its physical identity.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="devicePath"/> is blank; <paramref name="serialNumber"/> is non-<see langword="null"/> and blank;
    /// or <paramref name="physicalDevicePath"/> is non-<see langword="null"/> and blank.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="interfaceNumber"/> is negative.</exception>
    public PicoFlasherDeviceEndpoint(
        string devicePath,
        string? serialNumber,
        int interfaceNumber,
        string? physicalDevicePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);

        if (serialNumber is not null && string.IsNullOrWhiteSpace(serialNumber))
        {
            throw new ArgumentException("A PicoFlasher serial number cannot be empty or whitespace when provided.", nameof(serialNumber));
        }

        if (physicalDevicePath is not null && string.IsNullOrWhiteSpace(physicalDevicePath))
        {
            throw new ArgumentException(
                "A PicoFlasher physical-device path cannot be empty or whitespace when provided.",
                nameof(physicalDevicePath));
        }

        if (interfaceNumber < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(interfaceNumber),
                interfaceNumber,
                "A PicoFlasher interface number cannot be negative.");
        }

        DevicePath = devicePath;
        SerialNumber = serialNumber;
        InterfaceNumber = interfaceNumber;
        PhysicalDevicePath = physicalDevicePath ?? devicePath;
    }

    /// <summary>
    /// Gets the platform-provided command TTY path exactly as supplied, without normalization.
    /// </summary>
    public string DevicePath { get; }

    /// <summary>
    /// Gets the canonical physical USB-device identity exactly as supplied, without normalization.
    /// </summary>
    public string PhysicalDevicePath { get; }

    /// <summary>
    /// Gets the optional USB serial number exactly as supplied, or <see langword="null"/> when it is unavailable.
    /// </summary>
    public string? SerialNumber { get; }

    /// <summary>
    /// Gets the nonnegative USB interface number associated with <see cref="DevicePath"/>.
    /// </summary>
    public int InterfaceNumber { get; }
}
