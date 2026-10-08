namespace JRunner.Core.Nand.Inspection.Models;

/// <summary>
/// Safe structural facts decoded from the logical NAND header.
/// </summary>
/// <remarks>
/// This model deliberately retains scalar offsets and magic metadata only; it never retains header bytes.
/// </remarks>
public sealed record NandHeaderInspection
{
    /// <summary>
    /// Creates header-inspection facts when the header build was not available to the caller.
    /// </summary>
    public NandHeaderInspection(
        NandMagic magic,
        long firstStageLogicalOffset,
        NandLogicalRange smcLogicalRange,
        NandLogicalRange keyvaultLogicalRange)
        : this(magic, build: null, firstStageLogicalOffset, smcLogicalRange, keyvaultLogicalRange)
    {
    }

    /// <summary>
    /// Creates header-inspection facts.
    /// </summary>
    /// <param name="magic">The safe representation of the header magic.</param>
    /// <param name="build">The positive big-endian header build, or <see langword="null"/> when unavailable.</param>
    /// <param name="firstStageLogicalOffset">The zero-based logical offset of the first bootloader stage.</param>
    /// <param name="smcLogicalRange">The logical range occupied by the encrypted SMC.</param>
    /// <param name="keyvaultLogicalRange">The logical range occupied by the keyvault.</param>
    public NandHeaderInspection(
        NandMagic magic,
        int? build,
        long firstStageLogicalOffset,
        NandLogicalRange smcLogicalRange,
        NandLogicalRange keyvaultLogicalRange)
    {
        magic.Validate(nameof(magic));

        if (build is <= 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(build), "A supplied header build must be a positive 16-bit value.");
        }
        if (firstStageLogicalOffset < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(firstStageLogicalOffset),
                firstStageLogicalOffset,
                "The first bootloader-stage offset cannot be negative.");
        }

        smcLogicalRange.Validate(nameof(smcLogicalRange));
        keyvaultLogicalRange.Validate(nameof(keyvaultLogicalRange));

        Magic = magic;
        Build = build;
        FirstStageLogicalOffset = firstStageLogicalOffset;
        SmcLogicalRange = smcLogicalRange;
        KeyvaultLogicalRange = keyvaultLogicalRange;
    }

    /// <summary>
    /// Gets the safe representation of the header magic.
    /// </summary>
    public NandMagic Magic { get; }

    /// <summary>
    /// Gets the exact big-endian build from the logical NAND header, or <see langword="null"/>
    /// when the header was constructed without that parsed field.
    /// </summary>
    public int? Build { get; }

    /// <summary>
    /// Gets the zero-based logical offset of the first bootloader stage.
    /// </summary>
    public long FirstStageLogicalOffset { get; }

    /// <summary>
    /// Gets the logical SMC range.
    /// </summary>
    public NandLogicalRange SmcLogicalRange { get; }

    /// <summary>
    /// Gets the zero-based logical SMC offset.
    /// </summary>
    public long SmcLogicalOffset => SmcLogicalRange.Offset;

    /// <summary>
    /// Gets the logical SMC byte length.
    /// </summary>
    public long SmcLength => SmcLogicalRange.Length;

    /// <summary>
    /// Gets the logical keyvault range.
    /// </summary>
    public NandLogicalRange KeyvaultLogicalRange { get; }
}
