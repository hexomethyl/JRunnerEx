using JRunner.Core.Nand.Security;

namespace JRunner.Core.Nand.Inspection.Models;

/// <summary>
/// Identifies a bootloader stage in the logical NAND chain.
/// </summary>
public enum NandBootloaderStageKind
{
    /// <summary>The first second-stage bootloader, commonly called CB_A.</summary>
    CB_A = 1,

    /// <summary>The second second-stage bootloader, commonly called CB_B.</summary>
    CB_B,

    /// <summary>An injected or intermediate second-stage bootloader, commonly called CB_X.</summary>
    CB_X,

    /// <summary>The third-stage bootloader, commonly called SC.</summary>
    SC,

    /// <summary>The fourth-stage bootloader, commonly called CD.</summary>
    CD,

    /// <summary>The fifth-stage bootloader, commonly called CE.</summary>
    CE,

    /// <summary>The first CF patch bootloader.</summary>
    CF0,

    /// <summary>The second CF patch bootloader.</summary>
    CF1,

    /// <summary>The first CG patch bootloader.</summary>
    CG0,

    /// <summary>The second CG patch bootloader.</summary>
    CG1,

    /// <summary>A parsed bootloader whose stage does not have a dedicated public classification.</summary>
    Other,
}

/// <summary>
/// Describes whether a bootloader stage was decrypted while it was inspected.
/// </summary>
public enum NandBootloaderDecryptionStatus
{
    /// <summary>No decryption was attempted for the stage.</summary>
    NotAttempted,

    /// <summary>The stage was known to be available without decryption.</summary>
    NotRequired,

    /// <summary>The stage was successfully decrypted by a known path.</summary>
    Decrypted,

    /// <summary>A required decryption attempt did not yield usable stage evidence.</summary>
    Failed,
}

/// <summary>
/// Safe derived facts from a successful bootloader decryption operation.
/// </summary>
/// <remarks>
/// This evidence intentionally excludes derived keys and decoded bootloader bytes.
/// </remarks>
public sealed record NandBootloaderDecryptionEvidence
{
    /// <summary>
    /// Creates safe decryption evidence.
    /// </summary>
    /// <param name="usesNewCbCrypto">Whether CB_B used the newer CB_A-derived cryptographic branch.</param>
    /// <param name="hasLegacyCdZeroRangeEvidence">
    /// Whether the decrypted CD-family payload contained the legacy zero-range heuristic.
    /// </param>
    public NandBootloaderDecryptionEvidence(bool usesNewCbCrypto, bool hasLegacyCdZeroRangeEvidence)
    {
        if (usesNewCbCrypto && hasLegacyCdZeroRangeEvidence)
        {
            throw new ArgumentException(
                "CB new-crypto evidence and CD-family zero-range evidence cannot originate from the same stage.",
                nameof(hasLegacyCdZeroRangeEvidence));
        }

        UsesNewCbCrypto = usesNewCbCrypto;
        HasLegacyCdZeroRangeEvidence = hasLegacyCdZeroRangeEvidence;
    }

    /// <summary>
    /// Gets whether CB_B used the newer CB_A-derived cryptographic branch.
    /// </summary>
    public bool UsesNewCbCrypto { get; }

    /// <summary>
    /// Gets whether a CD-family payload contained either legacy zero-range heuristic.
    /// </summary>
    public bool HasLegacyCdZeroRangeEvidence { get; }
}

/// <summary>
/// Safe structural and decrypted-metadata evidence for one bootloader stage.
/// </summary>
/// <remarks>
/// This model intentionally excludes encrypted and decrypted stage bytes, CPU keys, and derived keys.
/// </remarks>
public sealed record NandBootloaderStage
{
    private const uint MaximumPairingData = 0x00FF_FFFF;

    /// <summary>
    /// Creates a bootloader-stage inspection result.
    /// </summary>
    /// <param name="kind">The explicit stage classification.</param>
    /// <param name="numericId">The numeric bootloader identifier from the stage header.</param>
    /// <param name="magic">The safe representation of the stage magic.</param>
    /// <param name="build">The decoded non-negative bootloader build number.</param>
    /// <param name="logicalOffset">The zero-based logical offset of the stage.</param>
    /// <param name="declaredLength">The positive byte length declared in the stage header.</param>
    /// <param name="roundedLength">The positive aligned byte length occupied in the logical image.</param>
    /// <param name="decryptionStatus">The outcome of attempting to decrypt the stage.</param>
    /// <param name="decryptionPath">The successful cryptographic path, when decryption succeeded.</param>
    /// <param name="decryptionEvidence">Safe derived evidence from successful decryption.</param>
    /// <param name="ldv">The optional lock-down value extracted from decrypted stage metadata.</param>
    /// <param name="pairingData">The optional 24-bit pairing data extracted from decrypted stage metadata.</param>
    public NandBootloaderStage(
        NandBootloaderStageKind kind,
        byte numericId,
        NandMagic magic,
        int build,
        long logicalOffset,
        long declaredLength,
        long roundedLength,
        NandBootloaderDecryptionStatus decryptionStatus,
        BootloaderDecryptionPath? decryptionPath = null,
        NandBootloaderDecryptionEvidence? decryptionEvidence = null,
        byte? ldv = null,
        uint? pairingData = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "The bootloader stage kind is not supported.");
        }

        magic.Validate(nameof(magic));
        if (build < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(build), build, "The bootloader build cannot be negative.");
        }

        ValidateRange(logicalOffset, declaredLength, roundedLength);

        if (!Enum.IsDefined(decryptionStatus))
        {
            throw new ArgumentOutOfRangeException(
                nameof(decryptionStatus),
                decryptionStatus,
                "The bootloader decryption status is not supported.");
        }

        if (decryptionPath is { } path && !Enum.IsDefined(path))
        {
            throw new ArgumentOutOfRangeException(
                nameof(decryptionPath),
                path,
                "The bootloader decryption path is not supported.");
        }

        if (decryptionStatus == NandBootloaderDecryptionStatus.Decrypted)
        {
            if (!decryptionPath.HasValue)
            {
                throw new ArgumentException(
                    "A decrypted bootloader stage must identify its decryption path.",
                    nameof(decryptionPath));
            }

            ArgumentNullException.ThrowIfNull(decryptionEvidence);
            EnsurePathMatchesStage(kind, decryptionPath.Value);
            EnsureEvidenceMatchesPath(decryptionPath.Value, decryptionEvidence);
        }
        else if (decryptionPath.HasValue || decryptionEvidence is not null)
        {
            throw new ArgumentException(
                "Only successfully decrypted bootloader stages can expose a decryption path or evidence.",
                nameof(decryptionStatus));
        }

        if (pairingData is > MaximumPairingData)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pairingData),
                pairingData,
                "Bootloader pairing data must fit in three bytes.");
        }

        Kind = kind;
        NumericId = numericId;
        Magic = magic;
        Build = build;
        LogicalOffset = logicalOffset;
        DeclaredLength = declaredLength;
        RoundedLength = roundedLength;
        DecryptionStatus = decryptionStatus;
        DecryptionPath = decryptionPath;
        DecryptionEvidence = decryptionEvidence;
        Ldv = ldv;
        PairingData = pairingData;
    }

    /// <summary>
    /// Gets the explicit stage classification.
    /// </summary>
    public NandBootloaderStageKind Kind { get; }

    /// <summary>
    /// Gets the numeric bootloader identifier from the stage header.
    /// </summary>
    public byte NumericId { get; }

    /// <summary>
    /// Gets the safe representation of the stage magic.
    /// </summary>
    public NandMagic Magic { get; }

    /// <summary>
    /// Gets the decoded bootloader build number.
    /// </summary>
    public int Build { get; }

    /// <summary>
    /// Gets the zero-based logical stage offset.
    /// </summary>
    public long LogicalOffset { get; }

    /// <summary>
    /// Gets the positive byte length declared by the stage header before alignment.
    /// </summary>
    public long DeclaredLength { get; }

    /// <summary>
    /// Gets the positive aligned byte length occupied by the stage in the logical image.
    /// </summary>
    public long RoundedLength { get; }

    /// <summary>
    /// Gets the occupied logical range of the stage.
    /// </summary>
    public NandLogicalRange LogicalRange => new(LogicalOffset, RoundedLength);

    /// <summary>
    /// Gets the decryption outcome for this stage.
    /// </summary>
    public NandBootloaderDecryptionStatus DecryptionStatus { get; }

    /// <summary>
    /// Gets the successful decryption path, or <see langword="null"/> when no usable decryption occurred.
    /// </summary>
    public BootloaderDecryptionPath? DecryptionPath { get; }

    /// <summary>
    /// Gets safe derived decryption facts, or <see langword="null"/> when no usable decryption occurred.
    /// </summary>
    public NandBootloaderDecryptionEvidence? DecryptionEvidence { get; }

    /// <summary>
    /// Gets the lock-down value from decrypted stage metadata, or <see langword="null"/> when unavailable.
    /// </summary>
    public byte? Ldv { get; }

    /// <summary>
    /// Gets the three-byte pairing data from decrypted stage metadata, or <see langword="null"/> when unavailable.
    /// </summary>
    public uint? PairingData { get; }

    private static void ValidateRange(long logicalOffset, long declaredLength, long roundedLength)
    {
        if (logicalOffset < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(logicalOffset),
                logicalOffset,
                "The bootloader logical offset cannot be negative.");
        }

        if (declaredLength <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(declaredLength),
                declaredLength,
                "The declared bootloader length must be positive.");
        }

        if (roundedLength < declaredLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(roundedLength),
                roundedLength,
                "The rounded bootloader length cannot be shorter than its declared length.");
        }

        if (logicalOffset > long.MaxValue - roundedLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(logicalOffset),
                "The bootloader offset and rounded length exceed the supported range.");
        }
    }

    private static void EnsurePathMatchesStage(
        NandBootloaderStageKind kind,
        BootloaderDecryptionPath decryptionPath)
    {
        if (kind == NandBootloaderStageKind.Other)
        {
            return;
        }

        bool matches = decryptionPath switch
        {
            BootloaderDecryptionPath.Cb or BootloaderDecryptionPath.S2 =>
                kind == NandBootloaderStageKind.CB_A,
            BootloaderDecryptionPath.CbWithCpuKey or BootloaderDecryptionPath.CbWithManufacturingZeroKey =>
                kind is NandBootloaderStageKind.CB_B or NandBootloaderStageKind.CB_X,
            BootloaderDecryptionPath.Sc => kind == NandBootloaderStageKind.SC,
            BootloaderDecryptionPath.Cd or BootloaderDecryptionPath.CdWithCpuKeyFallback =>
                kind == NandBootloaderStageKind.CD,
            BootloaderDecryptionPath.Ce => kind == NandBootloaderStageKind.CE,
            _ => false,
        };

        if (!matches)
        {
            throw new ArgumentException(
                "The declared decryption path is incompatible with the bootloader stage kind.",
                nameof(decryptionPath));
        }
    }

    private static void EnsureEvidenceMatchesPath(
        BootloaderDecryptionPath decryptionPath,
        NandBootloaderDecryptionEvidence decryptionEvidence)
    {
        if (decryptionEvidence.UsesNewCbCrypto &&
            decryptionPath is not (
                BootloaderDecryptionPath.CbWithCpuKey or
                BootloaderDecryptionPath.CbWithManufacturingZeroKey))
        {
            throw new ArgumentException(
                "New-CB cryptographic evidence requires a CB_B CPU-key decryption path.",
                nameof(decryptionEvidence));
        }

        if (decryptionEvidence.HasLegacyCdZeroRangeEvidence &&
            decryptionPath is not (
                BootloaderDecryptionPath.Cd or
                BootloaderDecryptionPath.CdWithCpuKeyFallback or
                BootloaderDecryptionPath.Ce))
        {
            throw new ArgumentException(
                "Legacy CD zero-range evidence requires a CD-family decryption path.",
                nameof(decryptionEvidence));
        }
    }
}
