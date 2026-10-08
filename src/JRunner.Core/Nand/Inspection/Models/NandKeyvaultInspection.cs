using JRunner.Core.Nand.Security;

namespace JRunner.Core.Nand.Inspection.Models;

/// <summary>
/// Safe keyvault inspection facts associated with a canonical logical NAND image.
/// </summary>
/// <remarks>
/// This model exposes a CRC and <see cref="KeyvaultInspection"/> metadata only. It never retains keyvault bytes,
/// decrypted DVD keys, or CPU keys.
/// </remarks>
public sealed record NandKeyvaultInspection
{
    /// <summary>
    /// Creates keyvault-inspection facts.
    /// </summary>
    /// <param name="logicalRange">The exact logical range over which <paramref name="crc32"/> was calculated.</param>
    /// <param name="crc32">The CRC-32 calculated from the complete keyvault logical range.</param>
    /// <param name="inspection">The safe keyvault metadata and CPU-key verification status.</param>
    public NandKeyvaultInspection(NandLogicalRange logicalRange, uint crc32, KeyvaultInspection inspection)
    {
        logicalRange.Validate(nameof(logicalRange));
        if (logicalRange.Length != KeyvaultService.KeyvaultLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(logicalRange),
                "A keyvault CRC must cover exactly one complete legacy keyvault.");
        }

        ArgumentNullException.ThrowIfNull(inspection);

        LogicalRange = logicalRange;
        Crc32 = crc32;
        Inspection = inspection;
    }

    /// <summary>
    /// Gets the exact logical range over which <see cref="Crc32"/> was calculated.
    /// </summary>
    public NandLogicalRange LogicalRange { get; }

    /// <summary>
    /// Gets the CRC-32 of the complete keyvault logical range.
    /// </summary>
    public uint Crc32 { get; }

    /// <summary>
    /// Gets safe keyvault storage, verification, and metadata evidence.
    /// </summary>
    public KeyvaultInspection Inspection { get; }
}
