using System.Diagnostics.CodeAnalysis;
using JRunner.Core.Contracts;

namespace JRunner.Core.Devices.PicoFlasher;

/// <summary>
/// Selects at most one PicoFlasher command CDC candidate.
/// </summary>
/// <remarks>
/// A selector is either unspecified, an exact command TTY path, or a USB serial number. Use
/// <see cref="Create"/> for a usage-mapped command-line validation failure, or <see cref="TryCreate"/> when
/// the caller needs the typed validation reason.
/// </remarks>
public sealed record PicoFlasherDeviceSelector
{
    private PicoFlasherDeviceSelector(string? devicePath, string? serialNumber)
    {
        DevicePath = devicePath;
        SerialNumber = serialNumber;
    }

    /// <summary>
    /// Gets the selector that leaves endpoint choice to the single available command candidate.
    /// </summary>
    public static PicoFlasherDeviceSelector Unspecified { get; } = new(null, null);

    /// <summary>
    /// Gets the exact command TTY path to select, or <see langword="null"/> when selection is not by path.
    /// </summary>
    public string? DevicePath { get; }

    /// <summary>
    /// Gets the exact USB serial number to select, or <see langword="null"/> when selection is not by serial.
    /// </summary>
    public string? SerialNumber { get; }

    /// <summary>
    /// Gets whether neither a command TTY path nor a serial number was supplied.
    /// </summary>
    public bool IsUnspecified => DevicePath is null && SerialNumber is null;

    /// <summary>
    /// Validates raw command-line selector values and creates an immutable selector.
    /// </summary>
    /// <param name="devicePath">The exact <c>--device</c> command TTY path, if supplied.</param>
    /// <param name="serialNumber">The exact <c>--serial</c> USB serial number, if supplied.</param>
    /// <returns>The valid selector.</returns>
    /// <exception cref="OperationFailureException">
    /// The supplied values are blank or select by both path and serial. The failure has
    /// <see cref="ExitCode.Usage"/> so command callers can render their standard usage envelope.
    /// </exception>
    public static PicoFlasherDeviceSelector Create(string? devicePath, string? serialNumber)
    {
        if (TryCreate(devicePath, serialNumber, out PicoFlasherDeviceSelector? selector, out PicoFlasherDeviceSelectorError error))
        {
            return selector!;
        }

        throw CreateUsageFailure(error);
    }

    /// <summary>
    /// Validates raw command-line selector values and creates an immutable selector on success.
    /// </summary>
    /// <param name="devicePath">The exact <c>--device</c> command TTY path, if supplied.</param>
    /// <param name="serialNumber">The exact <c>--serial</c> USB serial number, if supplied.</param>
    /// <param name="selector">The valid selector when this method returns <see langword="true"/>; otherwise, <see langword="null"/>.</param>
    /// <param name="error">The validation reason when this method returns <see langword="false"/>; otherwise, <see cref="PicoFlasherDeviceSelectorError.None"/>.</param>
    /// <returns><see langword="true"/> when exactly zero or one nonblank selector was supplied; otherwise, <see langword="false"/>.</returns>
    public static bool TryCreate(
        string? devicePath,
        string? serialNumber,
        [NotNullWhen(true)] out PicoFlasherDeviceSelector? selector,
        out PicoFlasherDeviceSelectorError error)
    {
        if (devicePath is not null && string.IsNullOrWhiteSpace(devicePath))
        {
            selector = null;
            error = PicoFlasherDeviceSelectorError.BlankDevicePath;
            return false;
        }

        if (serialNumber is not null && string.IsNullOrWhiteSpace(serialNumber))
        {
            selector = null;
            error = PicoFlasherDeviceSelectorError.BlankSerialNumber;
            return false;
        }

        if (devicePath is not null && serialNumber is not null)
        {
            selector = null;
            error = PicoFlasherDeviceSelectorError.MultipleSelectors;
            return false;
        }

        selector = devicePath is not null
            ? new PicoFlasherDeviceSelector(devicePath, null)
            : serialNumber is not null
                ? new PicoFlasherDeviceSelector(null, serialNumber)
                : Unspecified;
        error = PicoFlasherDeviceSelectorError.None;
        return true;
    }

    private static OperationFailureException CreateUsageFailure(PicoFlasherDeviceSelectorError error)
    {
        return error switch
        {
            PicoFlasherDeviceSelectorError.BlankDevicePath => new OperationFailureException(
                ExitCode.Usage,
                "pico-device-invalid",
                "A PicoFlasher --device selector cannot be empty or whitespace."),
            PicoFlasherDeviceSelectorError.BlankSerialNumber => new OperationFailureException(
                ExitCode.Usage,
                "pico-serial-invalid",
                "A PicoFlasher --serial selector cannot be empty or whitespace."),
            PicoFlasherDeviceSelectorError.MultipleSelectors => new OperationFailureException(
                ExitCode.Usage,
                "pico-device-selector-conflict",
                "Specify either --device or --serial, not both."),
            _ => throw new ArgumentOutOfRangeException(
                nameof(error),
                error,
                "A selector usage failure requires an invalid selector error."),
        };
    }
}

/// <summary>
/// Identifies why a raw PicoFlasher command-device selector is not valid.
/// </summary>
public enum PicoFlasherDeviceSelectorError
{
    /// <summary>
    /// The selector is valid.
    /// </summary>
    None,

    /// <summary>
    /// The supplied command TTY path is empty or whitespace.
    /// </summary>
    BlankDevicePath,

    /// <summary>
    /// The supplied USB serial number is empty or whitespace.
    /// </summary>
    BlankSerialNumber,

    /// <summary>
    /// Both a command TTY path and a serial number were supplied.
    /// </summary>
    MultipleSelectors,
}
