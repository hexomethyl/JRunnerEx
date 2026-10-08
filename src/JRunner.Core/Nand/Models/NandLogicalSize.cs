namespace JRunner.Core.Nand.Models;

/// <summary>
/// Classifies the logical NAND capacity using the numeric values of legacy <c>Nandsize</c>.
/// Each nonzero value is the corresponding legacy small-block count, not a byte count.
/// </summary>
public enum NandLogicalSize
{
    /// <summary>No logical NAND classification, used by the legacy 4 GB eMMC definitions.</summary>
    S0 = 0x0000,

    /// <summary>16 MB logical NAND capacity.</summary>
    S16 = 0x0400,

    /// <summary>64 MB logical NAND capacity.</summary>
    S64 = 0x1000,

    /// <summary>256 MB logical NAND capacity.</summary>
    S256 = 0x4000,

    /// <summary>512 MB logical NAND capacity.</summary>
    S512 = 0x8000,
}

/// <summary>
/// Converts legacy logical NAND size classifications to explicit capacities.
/// </summary>
public static class NandLogicalSizeExtensions
{
    /// <summary>
    /// Gets the logical NAND capacity in bytes.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="size"/> is not a defined classification.</exception>
    public static long GetByteLength(this NandLogicalSize size) => size switch
    {
        NandLogicalSize.S0 => 0,
        NandLogicalSize.S16 => 16L * 1024 * 1024,
        NandLogicalSize.S64 => 64L * 1024 * 1024,
        NandLogicalSize.S256 => 256L * 1024 * 1024,
        NandLogicalSize.S512 => 512L * 1024 * 1024,
        _ => throw new ArgumentOutOfRangeException(nameof(size), size, "The NAND size classification is not supported."),
    };

    /// <summary>
    /// Gets the logical NAND capacity in mebibytes.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="size"/> is not a defined classification.</exception>
    public static int GetMebibytes(this NandLogicalSize size) => size switch
    {
        NandLogicalSize.S0 => 0,
        NandLogicalSize.S16 => 16,
        NandLogicalSize.S64 => 64,
        NandLogicalSize.S256 => 256,
        NandLogicalSize.S512 => 512,
        _ => throw new ArgumentOutOfRangeException(nameof(size), size, "The NAND size classification is not supported."),
    };
}
