using System.Diagnostics.CodeAnalysis;

namespace JRunner.Core.Devices.PicoFlasher;

/// <summary>
/// Describes the logical NAND capacity and erase-unit geometry for one recognized PicoFlasher flash configuration.
/// </summary>
/// <remarks>
/// Instances are created only by <see cref="TryFromFlashConfiguration"/>, which intentionally recognizes only
/// configurations with a known-safe NAND geometry.
/// </remarks>
public sealed class PicoFlasherNandGeometry
{
    private const long SixteenMebibytes = 0x0100_0000L;
    private const long SixtyFourMebibytes = 0x0400_0000L;
    private const long TwoHundredFiftySixMebibytes = 0x1000_0000L;
    private const long FiveHundredTwelveMebibytes = 0x2000_0000L;

    private PicoFlasherNandGeometry(
        uint flashConfiguration,
        long logicalByteLength)
    {
        if (!TryGetEraseBlockGeometry(
                flashConfiguration,
                out var eraseBlockLogicalByteLength,
                out var eraseBlockRecordCount))
        {
            throw new ArgumentOutOfRangeException(
                nameof(flashConfiguration),
                flashConfiguration,
                "An absent console or eMMC configuration does not have a NAND geometry.");
        }

        if (logicalByteLength <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(logicalByteLength),
                logicalByteLength,
                "The logical NAND byte length must be positive.");
        }

        if (logicalByteLength % PicoFlasherProtocol.NandDataSize != 0)
        {
            throw new ArgumentException(
                "The logical NAND byte length must contain a whole number of NAND data records.",
                nameof(logicalByteLength));
        }

        if (logicalByteLength % eraseBlockLogicalByteLength != 0)
        {
            throw new ArgumentException(
                "The logical NAND byte length must be an exact multiple of the erase-block logical byte length.",
                nameof(logicalByteLength));
        }

        var recordCount = logicalByteLength / PicoFlasherProtocol.NandDataSize;

        if (recordCount > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(logicalByteLength),
                logicalByteLength,
                "The logical NAND byte length exceeds the representable record count.");
        }

        FlashConfiguration = flashConfiguration;
        LogicalByteLength = logicalByteLength;
        RecordCount = checked((uint)recordCount);
        WireByteLength = checked(recordCount * PicoFlasherProtocol.NandWireRecordSize);
        EraseBlockLogicalByteLength = eraseBlockLogicalByteLength;
        EraseBlockRecordCount = eraseBlockRecordCount;
    }

    /// <summary>
    /// Gets the raw flash-configuration word reported by the firmware.
    /// </summary>
    public uint FlashConfiguration { get; }

    /// <summary>
    /// Gets the number of logical NAND payload bytes, excluding per-record spare bytes.
    /// </summary>
    public long LogicalByteLength { get; }

    /// <summary>
    /// Gets the number of logical NAND records, each containing <see cref="PicoFlasherProtocol.NandDataSize"/> payload bytes.
    /// </summary>
    public uint RecordCount { get; }

    /// <summary>
    /// Gets the number of raw wire bytes required for all NAND records, including per-record spare bytes.
    /// </summary>
    public long WireByteLength { get; }

    /// <summary>
    /// Gets the logical payload-byte length of one erase block.
    /// </summary>
    public int EraseBlockLogicalByteLength { get; }

    /// <summary>
    /// Gets the number of logical NAND records in one erase block.
    /// </summary>
    public uint EraseBlockRecordCount { get; }

    /// <summary>
    /// Determines whether a firmware-reported flash configuration represents an absent console.
    /// </summary>
    /// <param name="flashConfiguration">The raw flash-configuration word reported by the firmware.</param>
    /// <returns><see langword="true"/> when the firmware reported an absent-console sentinel; otherwise, <see langword="false"/>.</returns>
    public static bool IsConsoleAbsent(uint flashConfiguration)
    {
        return flashConfiguration is 0 or uint.MaxValue;
    }

    /// <summary>
    /// Determines whether a firmware-reported flash configuration identifies eMMC rather than NAND.
    /// </summary>
    /// <param name="flashConfiguration">The raw flash-configuration word reported by the firmware.</param>
    /// <returns><see langword="true"/> when the configuration's high nibble is <c>0xC</c>; otherwise, <see langword="false"/>.</returns>
    public static bool IsEmmcFlashConfiguration(uint flashConfiguration)
    {
        return (flashConfiguration & 0xF000_0000U) == 0xC000_0000U;
    }

    /// <summary>
    /// Derives the firmware's NAND erase-block geometry independently of capacity recognition.
    /// </summary>
    /// <param name="flashConfiguration">The raw flash-configuration word reported by the firmware.</param>
    /// <param name="eraseBlockLogicalByteLength">The logical payload-byte length of one erase block, or zero on failure.</param>
    /// <param name="eraseBlockRecordCount">The number of logical NAND records in one erase block, or zero on failure.</param>
    /// <returns>
    /// <see langword="false"/> for absent consoles and eMMC; otherwise, <see langword="true"/> even when the NAND capacity is unknown.
    /// </returns>
    /// <remarks>
    /// This method does not establish a known-safe capacity for destructive operations.
    /// </remarks>
    public static bool TryGetEraseBlockGeometry(
        uint flashConfiguration,
        out int eraseBlockLogicalByteLength,
        out uint eraseBlockRecordCount)
    {
        eraseBlockLogicalByteLength = 0;
        eraseBlockRecordCount = 0;

        if (IsConsoleAbsent(flashConfiguration) || IsEmmcFlashConfiguration(flashConfiguration))
        {
            return false;
        }

        var major = (flashConfiguration >> 17) & 3U;
        var minor = (flashConfiguration >> 4) & 3U;

        eraseBlockLogicalByteLength = 0x4000;
        if (major >= 1)
        {
            if (minor == 2)
            {
                eraseBlockLogicalByteLength = 0x20000;
            }
            else if (minor == 3)
            {
                eraseBlockLogicalByteLength = 0x40000;
            }
        }

        eraseBlockRecordCount = checked((uint)(eraseBlockLogicalByteLength / PicoFlasherProtocol.NandDataSize));
        return true;
    }

    /// <summary>
    /// Tries to create the known-safe NAND geometry for a firmware-reported flash configuration.
    /// </summary>
    /// <param name="flashConfiguration">The raw flash-configuration word reported by the firmware.</param>
    /// <param name="geometry">
    /// The recognized NAND geometry when this method returns <see langword="true"/>; otherwise, <see langword="null"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="flashConfiguration"/> maps to a known-safe NAND geometry; otherwise,
    /// <see langword="false"/> for absent consoles, eMMC, and unsupported NAND configurations.
    /// </returns>
    public static bool TryFromFlashConfiguration(
        uint flashConfiguration,
        [NotNullWhen(true)] out PicoFlasherNandGeometry? geometry)
    {
        geometry = null;

        if (IsConsoleAbsent(flashConfiguration) || IsEmmcFlashConfiguration(flashConfiguration))
        {
            return false;
        }

        switch (flashConfiguration)
        {
            case 0x0119_8010U:
            case 0x0002_3010U:
            case 0x0004_3000U:
                geometry = new PicoFlasherNandGeometry(
                    flashConfiguration,
                    SixteenMebibytes);
                return true;

            case 0x0119_8030U:
                geometry = new PicoFlasherNandGeometry(
                    flashConfiguration,
                    SixtyFourMebibytes);
                return true;

            case 0x008A_3020U:
            case 0x008C_3020U:
                geometry = new PicoFlasherNandGeometry(
                    flashConfiguration,
                    TwoHundredFiftySixMebibytes);
                return true;

            case 0x00AA_3020U:
            case 0x00AC_3020U:
                geometry = new PicoFlasherNandGeometry(
                    flashConfiguration,
                    FiveHundredTwelveMebibytes);
                return true;

            default:
                return false;
        }
    }
}
