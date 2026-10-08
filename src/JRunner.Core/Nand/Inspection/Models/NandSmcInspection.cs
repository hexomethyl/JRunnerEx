using JRunner.Core.Nand.Security;

namespace JRunner.Core.Nand.Inspection.Models;

/// <summary>
/// Safe evidence produced by evaluating a decrypted SMC hack marker.
/// </summary>
/// <remarks>
/// The marker bytes themselves are deliberately not retained.
/// </remarks>
public sealed record NandHackedSmcEvidence
{
    /// <summary>
    /// Creates safe hacked-SMC marker evidence.
    /// </summary>
    /// <param name="markerOffset">The zero-based offset of the inspected marker within the decrypted SMC.</param>
    /// <param name="markerLength">The positive length of the inspected marker.</param>
    /// <param name="markerIsAllZero">Whether every inspected marker byte was zero.</param>
    public NandHackedSmcEvidence(long markerOffset, long markerLength, bool markerIsAllZero)
    {
        if (markerOffset < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(markerOffset),
                markerOffset,
                "The hacked-SMC marker offset cannot be negative.");
        }

        if (markerLength <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(markerLength),
                markerLength,
                "The hacked-SMC marker length must be positive.");
        }

        if (markerOffset > long.MaxValue - markerLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(markerOffset),
                "The hacked-SMC marker range exceeds the supported range.");
        }

        MarkerOffset = markerOffset;
        MarkerLength = markerLength;
        MarkerIsAllZero = markerIsAllZero;
    }

    /// <summary>
    /// Gets the zero-based marker offset within the decrypted SMC.
    /// </summary>
    public long MarkerOffset { get; }

    /// <summary>
    /// Gets the positive marker length within the decrypted SMC.
    /// </summary>
    public long MarkerLength { get; }

    /// <summary>
    /// Gets whether every inspected marker byte was zero.
    /// </summary>
    public bool MarkerIsAllZero { get; }

    /// <summary>
    /// Gets whether the inspected marker supplied hacked-SMC evidence.
    /// </summary>
    public bool IsHacked => !MarkerIsAllZero;

    internal void EnsureFitsWithin(long smcLength, string parameterName)
    {
        if (smcLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(smcLength), smcLength, "The SMC length cannot be negative.");
        }

        if (MarkerOffset > long.MaxValue - MarkerLength || MarkerOffset + MarkerLength > smcLength)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "The hacked-SMC marker must lie entirely within the SMC range.");
        }
    }
}

/// <summary>
/// Safe SMC location and version evidence from a NAND inspection.
/// </summary>
/// <remarks>
/// This model intentionally excludes encrypted and decrypted SMC bytes.
/// </remarks>
public sealed record NandSmcInspection
{
    /// <summary>
    /// Creates SMC-inspection facts.
    /// </summary>
    /// <param name="logicalRange">The logical range occupied by the encrypted SMC.</param>
    /// <param name="motherboardType">The optional decoded four-bit motherboard type.</param>
    /// <param name="version">The optional decoded SMC version.</param>
    /// <param name="hackedSmcEvidence">Optional safe evidence from a hacked-SMC marker check.</param>
    public NandSmcInspection(
        NandLogicalRange logicalRange,
        int? motherboardType,
        SmcVersion? version,
        NandHackedSmcEvidence? hackedSmcEvidence = null)
    {
        logicalRange.Validate(nameof(logicalRange));
        if (motherboardType is < 0 or > 0xF)
        {
            throw new ArgumentOutOfRangeException(
                nameof(motherboardType),
                motherboardType,
                "The SMC motherboard type must be a four-bit value.");
        }

        if (version is not null)
        {
            if (!motherboardType.HasValue)
            {
                throw new ArgumentException(
                    "A decoded SMC version also supplies motherboard-type evidence.",
                    nameof(motherboardType));
            }

            if (motherboardType.Value != version.MotherboardType)
            {
                throw new ArgumentException(
                    "The SMC motherboard type must agree with the decoded SMC version.",
                    nameof(motherboardType));
            }
        }

        hackedSmcEvidence?.EnsureFitsWithin(logicalRange.Length, nameof(hackedSmcEvidence));

        LogicalRange = logicalRange;
        MotherboardType = motherboardType;
        Version = version;
        HackedSmcEvidence = hackedSmcEvidence;
    }

    /// <summary>
    /// Gets the logical range occupied by the encrypted SMC.
    /// </summary>
    public NandLogicalRange LogicalRange { get; }

    /// <summary>
    /// Gets the zero-based logical SMC offset.
    /// </summary>
    public long LogicalOffset => LogicalRange.Offset;

    /// <summary>
    /// Gets the logical SMC byte length.
    /// </summary>
    public long Length => LogicalRange.Length;

    /// <summary>
    /// Gets the decoded four-bit SMC motherboard type, or <see langword="null"/> when unavailable.
    /// </summary>
    public int? MotherboardType { get; }

    /// <summary>
    /// Gets the decoded SMC version, or <see langword="null"/> when version bytes were unavailable.
    /// </summary>
    public SmcVersion? Version { get; }

    /// <summary>
    /// Gets safe hacked-SMC marker evidence, or <see langword="null"/> when the marker was not inspected.
    /// </summary>
    public NandHackedSmcEvidence? HackedSmcEvidence { get; }
}
