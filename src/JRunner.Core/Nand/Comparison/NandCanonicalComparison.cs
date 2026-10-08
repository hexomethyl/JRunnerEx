using System.Buffers;
using System.Collections.Immutable;
using JRunner.Core.Contracts;
using JRunner.Core.Nand.Physical;

namespace JRunner.Core.Nand.Comparison;

/// <summary>
/// Declares how a NAND input should be interpreted before canonical comparison.
/// </summary>
public enum NandCanonicalInputFormat
{
    /// <summary>
    /// Detects logical data or legacy interleaved ECC from the bytes and supplied layout hint.
    /// </summary>
    Auto,

    /// <summary>
    /// Treats the input as logical <c>0x200</c>-byte NAND pages without spare data.
    /// </summary>
    Logical,

    /// <summary>
    /// Treats the input as legacy interleaved <c>0x210</c>-byte data-and-spare pages.
    /// </summary>
    InterleavedEcc,
}

/// <summary>
/// Immutable caller-owned stream input for canonical NAND comparison.
/// </summary>
public sealed record NandCanonicalInput
{
    /// <summary>
    /// Creates a canonical NAND input.
    /// </summary>
    /// <param name="source">The readable caller-owned source stream. It is never disposed by comparison.</param>
    /// <param name="format">The requested input interpretation.</param>
    /// <param name="layoutHint">An optional legacy spare-layout hint for physical input.</param>
    public NandCanonicalInput(
        Stream source,
        NandCanonicalInputFormat format = NandCanonicalInputFormat.Auto,
        NandLegacyLayout? layoutHint = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("The NAND source stream must be readable.", nameof(source));
        }

        if (!Enum.IsDefined(format))
        {
            throw new ArgumentOutOfRangeException(nameof(format), format, "The NAND input format is not supported.");
        }

        if (layoutHint is { } layout && !Enum.IsDefined(layout))
        {
            throw new ArgumentOutOfRangeException(nameof(layoutHint), layout, "The NAND spare layout is not supported.");
        }

        if (format == NandCanonicalInputFormat.Logical && layoutHint.HasValue)
        {
            throw new ArgumentException("A NAND spare layout hint is incompatible with logical input.", nameof(layoutHint));
        }

        Source = source;
        Format = format;
        LayoutHint = layoutHint;
    }

    /// <summary>
    /// Gets the readable caller-owned source stream. Comparison leaves the stream open.
    /// </summary>
    public Stream Source { get; }

    /// <summary>
    /// Gets the requested input interpretation.
    /// </summary>
    public NandCanonicalInputFormat Format { get; }

    /// <summary>
    /// Gets an optional legacy physical spare-layout hint.
    /// </summary>
    public NandLegacyLayout? LayoutHint { get; }
}

/// <summary>
/// Immutable request to compare two NAND inputs through the same canonical logical projection.
/// </summary>
public sealed record NandCanonicalComparisonRequest
{
    /// <summary>
    /// Creates a canonical NAND comparison request.
    /// </summary>
    public NandCanonicalComparisonRequest(NandCanonicalInput left, NandCanonicalInput right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        Left = left;
        Right = right;
    }

    /// <summary>
    /// Gets the left input.
    /// </summary>
    public NandCanonicalInput Left { get; }

    /// <summary>
    /// Gets the right input.
    /// </summary>
    public NandCanonicalInput Right { get; }
}

/// <summary>
/// Immutable metadata for one input after its canonical logical projection has been prepared.
/// </summary>
public sealed record NandCanonicalImageSummary
{
    /// <summary>
    /// Creates a canonical image summary.
    /// </summary>
    public NandCanonicalImageSummary(
        NandPhysicalFormat detectedFormat,
        NandLegacyLayout? selectedLayout,
        bool hasSpareData,
        long rawByteLength,
        long canonicalLogicalByteLength,
        long physicalBlockCount,
        ImmutableArray<NandBadBlock> badBlocks,
        ImmutableArray<NandBadBlockRemap> remaps)
    {
        if (detectedFormat is not NandPhysicalFormat.Logical and not NandPhysicalFormat.InterleavedEcc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(detectedFormat),
                detectedFormat,
                "A canonical image must have a logical or interleaved-ECC format.");
        }

        if (selectedLayout is { } layout && !Enum.IsDefined(layout))
        {
            throw new ArgumentOutOfRangeException(nameof(selectedLayout), layout, "The NAND spare layout is not supported.");
        }

        if (hasSpareData != (detectedFormat == NandPhysicalFormat.InterleavedEcc))
        {
            throw new ArgumentException("Spare-data metadata must agree with the detected NAND format.", nameof(hasSpareData));
        }

        if (hasSpareData != selectedLayout.HasValue)
        {
            throw new ArgumentException("A physical NAND summary must have exactly one selected spare layout.", nameof(selectedLayout));
        }

        if (rawByteLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rawByteLength), rawByteLength, "The raw NAND length must be positive.");
        }

        if (canonicalLogicalByteLength <= 0 ||
            canonicalLogicalByteLength % NandPhysicalGeometry.LogicalPageSize != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(canonicalLogicalByteLength),
                canonicalLogicalByteLength,
                "The canonical NAND length must contain complete logical pages.");
        }

        if (physicalBlockCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(physicalBlockCount), physicalBlockCount, "The physical block count cannot be negative.");
        }

        if (hasSpareData && physicalBlockCount == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(physicalBlockCount), physicalBlockCount, "A physical NAND must contain at least one block.");
        }

        if (!hasSpareData && physicalBlockCount != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(physicalBlockCount), physicalBlockCount, "Logical NAND data has no physical block count.");
        }

        if (badBlocks.IsDefault)
        {
            badBlocks = ImmutableArray<NandBadBlock>.Empty;
        }

        if (remaps.IsDefault)
        {
            remaps = ImmutableArray<NandBadBlockRemap>.Empty;
        }

        foreach (NandBadBlock badBlock in badBlocks)
        {
            ArgumentNullException.ThrowIfNull(badBlock);
        }

        foreach (NandBadBlockRemap remap in remaps)
        {
            ArgumentNullException.ThrowIfNull(remap);
        }

        DetectedFormat = detectedFormat;
        SelectedLayout = selectedLayout;
        HasSpareData = hasSpareData;
        RawByteLength = rawByteLength;
        CanonicalLogicalByteLength = canonicalLogicalByteLength;
        PhysicalBlockCount = physicalBlockCount;
        BadBlocks = badBlocks;
        Remaps = remaps;
    }

    /// <summary>
    /// Gets the detected byte arrangement after format resolution.
    /// </summary>
    public NandPhysicalFormat DetectedFormat { get; }

    /// <summary>
    /// Gets the selected physical spare layout, or <see langword="null"/> for logical input.
    /// </summary>
    public NandLegacyLayout? SelectedLayout { get; }

    /// <summary>
    /// Gets whether the input carries legacy spare data.
    /// </summary>
    public bool HasSpareData { get; }

    /// <summary>
    /// Gets the byte count consumed from the source stream's starting position.
    /// </summary>
    public long RawByteLength { get; }

    /// <summary>
    /// Gets the length of the virtual logical projection used for comparison.
    /// </summary>
    public long CanonicalLogicalByteLength { get; }

    /// <summary>
    /// Gets the physical erase-block count for a spare-bearing input, or zero for logical input.
    /// </summary>
    public long PhysicalBlockCount { get; }

    /// <summary>
    /// Gets bad physical-block evidence in ascending physical-block order.
    /// </summary>
    public ImmutableArray<NandBadBlock> BadBlocks { get; }

    /// <summary>
    /// Gets one remap record per bad physical block in ascending physical-block order.
    /// </summary>
    public ImmutableArray<NandBadBlockRemap> Remaps { get; }
}

/// <summary>
/// Immutable completed result of a canonical NAND comparison.
/// </summary>
public sealed record NandCanonicalComparisonResult
{
    /// <summary>
    /// Creates a canonical NAND comparison result.
    /// </summary>
    public NandCanonicalComparisonResult(
        bool equal,
        NandCanonicalImageSummary left,
        NandCanonicalImageSummary right,
        long differingByteCount,
        long? firstDifferingLogicalOffset)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (differingByteCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(differingByteCount), differingByteCount, "The differing byte count cannot be negative.");
        }

        if (firstDifferingLogicalOffset is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(firstDifferingLogicalOffset),
                firstDifferingLogicalOffset,
                "The first differing logical offset cannot be negative.");
        }

        if (firstDifferingLogicalOffset is { } offset &&
            offset >= Math.Max(left.CanonicalLogicalByteLength, right.CanonicalLogicalByteLength))
        {
            throw new ArgumentOutOfRangeException(
                nameof(firstDifferingLogicalOffset),
                firstDifferingLogicalOffset,
                "The first differing logical offset must lie within at least one canonical image.");
        }

        if (equal && (differingByteCount != 0 || firstDifferingLogicalOffset.HasValue))
        {
            throw new ArgumentException("An equal result cannot report differences.", nameof(equal));
        }

        if (!equal && (differingByteCount == 0 || !firstDifferingLogicalOffset.HasValue))
        {
            throw new ArgumentException("An unequal result must report its first difference and a positive count.", nameof(differingByteCount));
        }

        Equal = equal;
        Left = left;
        Right = right;
        DifferingByteCount = differingByteCount;
        FirstDifferingLogicalOffset = firstDifferingLogicalOffset;
    }

    /// <summary>
    /// Gets whether the complete canonical logical projections are equal.
    /// </summary>
    public bool Equal { get; }

    /// <summary>
    /// Gets canonical metadata for the left input.
    /// </summary>
    public NandCanonicalImageSummary Left { get; }

    /// <summary>
    /// Gets canonical metadata for the right input.
    /// </summary>
    public NandCanonicalImageSummary Right { get; }

    /// <summary>
    /// Gets the count of unequal logical bytes, including every byte in an unequal tail.
    /// </summary>
    public long DifferingByteCount { get; }

    /// <summary>
    /// Gets the zero-based logical offset of the first difference, or <see langword="null"/> when equal.
    /// </summary>
    public long? FirstDifferingLogicalOffset { get; }
}

/// <summary>
/// Performs bounded-memory comparisons over canonical logical NAND projections.
/// </summary>
public static class NandCanonicalComparisonService
{
    private const int ComparisonBufferSize = 0x10000;

    /// <summary>
    /// Canonicalizes both inputs, including physical bad-block remapping, and compares all logical bytes.
    /// </summary>
    /// <exception cref="OperationFailureException">Thrown when either input is malformed or unsupported.</exception>
    /// <exception cref="OperationCanceledException">Thrown when cancellation is requested.</exception>
    public static async Task<NandCanonicalComparisonResult> CompareAsync(
        NandCanonicalComparisonRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        await using NandCanonicalImage left = await NandCanonicalImagePreparer.PrepareAsync(
            request.Left,
            progress,
            cancellationToken).ConfigureAwait(false);
        await using NandCanonicalImage right = await NandCanonicalImagePreparer.PrepareAsync(
            request.Right,
            progress,
            cancellationToken).ConfigureAwait(false);

        byte[] leftBuffer = ArrayPool<byte>.Shared.Rent(ComparisonBufferSize);
        try
        {
            byte[] rightBuffer = ArrayPool<byte>.Shared.Rent(ComparisonBufferSize);
            try
            {
                long offset = 0;
                long differingByteCount = 0;
                long? firstDifferingLogicalOffset = null;
                long commonLength = Math.Min(
                    left.Summary.CanonicalLogicalByteLength,
                    right.Summary.CanonicalLogicalByteLength);

                ReportComparisonProgress(progress, completed: 0, total: Math.Max(
                    left.Summary.CanonicalLogicalByteLength,
                    right.Summary.CanonicalLogicalByteLength));
                while (offset < commonLength)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int byteCount = (int)Math.Min(ComparisonBufferSize, commonLength - offset);
                    int leftRead = await left.ReadLogicalAsync(
                        offset,
                        leftBuffer.AsMemory(0, byteCount),
                        cancellationToken).ConfigureAwait(false);
                    int rightRead = await right.ReadLogicalAsync(
                        offset,
                        rightBuffer.AsMemory(0, byteCount),
                        cancellationToken).ConfigureAwait(false);
                    if (leftRead != byteCount || rightRead != byteCount)
                    {
                        throw InvalidData("truncated-nand", "A NAND source ended before its validated canonical length.");
                    }

                    for (int index = 0; index < byteCount; index++)
                    {
                        if (leftBuffer[index] == rightBuffer[index])
                        {
                            continue;
                        }

                        differingByteCount = checked(differingByteCount + 1);
                        firstDifferingLogicalOffset ??= checked(offset + index);
                    }

                    offset = checked(offset + byteCount);
                    if (offset == commonLength || offset % ComparisonBufferSize == 0)
                    {
                        ReportComparisonProgress(progress, offset, Math.Max(
                            left.Summary.CanonicalLogicalByteLength,
                            right.Summary.CanonicalLogicalByteLength));
                    }
                }

                long tailLength = Math.Abs(
                    left.Summary.CanonicalLogicalByteLength - right.Summary.CanonicalLogicalByteLength);
                if (tailLength != 0)
                {
                    differingByteCount = checked(differingByteCount + tailLength);
                    firstDifferingLogicalOffset ??= commonLength;
                    ReportComparisonProgress(progress, Math.Max(
                        left.Summary.CanonicalLogicalByteLength,
                        right.Summary.CanonicalLogicalByteLength), Math.Max(
                        left.Summary.CanonicalLogicalByteLength,
                        right.Summary.CanonicalLogicalByteLength));
                }

                bool equal = differingByteCount == 0;
                return new NandCanonicalComparisonResult(
                    equal,
                    left.Summary,
                    right.Summary,
                    differingByteCount,
                    firstDifferingLogicalOffset);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rightBuffer);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(leftBuffer);
        }
    }

    private static OperationFailureException InvalidData(string kind, string message) =>
        new(ExitCode.InvalidData, kind, message);

    private static void ReportComparisonProgress(IProgress<OperationProgress>? progress, long completed, long total)
    {
        progress?.Report(new OperationProgress(
            "comparing-canonical-nand",
            "Comparing canonical NAND logical data.",
            completed: completed,
            total: total));
    }
}
