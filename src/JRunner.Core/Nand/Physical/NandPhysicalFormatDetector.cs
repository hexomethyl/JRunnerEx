using System.Collections.Immutable;
using JRunner.Core.Contracts;

namespace JRunner.Core.Nand.Physical;

/// <summary>
/// Classifies a NAND byte sequence without assuming that a length divisible by both page sizes is logical data.
/// </summary>
public enum NandPhysicalFormat
{
    /// <summary>
    /// The sequence is empty and contains no format evidence.
    /// </summary>
    Empty,

    /// <summary>
    /// The sequence is aligned to logical pages but contains no interleaved ECC evidence.
    /// </summary>
    Logical,

    /// <summary>
    /// The sequence contains complete interleaved physical pages.
    /// </summary>
    InterleavedEcc,

    /// <summary>
    /// The sequence is compatible with both logical and physical page lengths but has no ECC evidence.
    /// </summary>
    Ambiguous,

    /// <summary>
    /// The sequence contains a partial physical page or valid physical-page evidence followed by trailing bytes.
    /// </summary>
    Truncated,
}

/// <summary>
/// Immutable evidence returned by <see cref="NandPhysicalFormatDetector"/>.
/// </summary>
public sealed record NandPhysicalFormatDetection
{
    internal NandPhysicalFormatDetection(
        NandPhysicalFormat format,
        ImmutableArray<NandPhysicalLayout> candidateLayouts,
        int completePhysicalPageCount,
        int inspectedPageCount,
        int validEccPageCount)
    {
        Format = format;
        CandidateLayouts = candidateLayouts;
        CompletePhysicalPageCount = completePhysicalPageCount;
        InspectedPageCount = inspectedPageCount;
        ValidEccPageCount = validEccPageCount;
    }

    /// <summary>
    /// Gets the detected byte arrangement.
    /// </summary>
    public NandPhysicalFormat Format { get; }

    /// <summary>
    /// Gets the layouts whose generated spare bytes matched every validated sample.
    /// An empty set means that ECC evidence exists but spare bytes did not match a generated legacy layout.
    /// </summary>
    public ImmutableArray<NandPhysicalLayout> CandidateLayouts { get; }

    /// <summary>
    /// Gets the only candidate layout when format evidence is unambiguous; otherwise <see langword="null"/>.
    /// </summary>
    public NandPhysicalLayout? Layout => CandidateLayouts.Length == 1 ? CandidateLayouts[0] : null;

    /// <summary>
    /// Gets the count of full <c>0x210</c>-byte pages present before any trailing bytes.
    /// </summary>
    public int CompletePhysicalPageCount { get; }

    /// <summary>
    /// Gets the number of physical pages inspected for ECC evidence.
    /// </summary>
    public int InspectedPageCount { get; }

    /// <summary>
    /// Gets the number of inspected pages whose stored ECC matched the legacy calculation.
    /// </summary>
    public int ValidEccPageCount { get; }

    /// <summary>
    /// Gets whether the data can be consumed by <c>NandEccCodec.RemoveEcc</c>.
    /// </summary>
    public bool IsInterleavedEcc => Format == NandPhysicalFormat.InterleavedEcc;
}

/// <summary>
/// Detects legacy interleaved NAND pages by sampling ECC and generated spare bytes.
/// </summary>
public static class NandPhysicalFormatDetector
{
    private const int DefaultMaximumPagesToInspect = 0x80;

    /// <summary>
    /// Detects whether a sequence is logical data, complete interleaved physical data, or truncated data.
    /// </summary>
    /// <param name="data">The bytes to classify.</param>
    /// <param name="firstPhysicalBlock">Physical block number represented by the first physical page.</param>
    /// <param name="maximumPagesToInspect">Maximum evenly distributed physical pages to inspect.</param>
    /// <param name="progress">Optional progress receiver.</param>
    /// <param name="cancellationToken">Token checked before each sampled page.</param>
    public static NandPhysicalFormatDetection Detect(
        ReadOnlySpan<byte> data,
        long firstPhysicalBlock = 0,
        int maximumPagesToInspect = DefaultMaximumPagesToInspect,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (firstPhysicalBlock < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(firstPhysicalBlock), "The first physical block cannot be negative.");
        }

        if (maximumPagesToInspect <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPagesToInspect), "At least one page must be inspected.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (data.IsEmpty)
        {
            return new NandPhysicalFormatDetection(
                NandPhysicalFormat.Empty,
                ImmutableArray<NandPhysicalLayout>.Empty,
                completePhysicalPageCount: 0,
                inspectedPageCount: 0,
                validEccPageCount: 0);
        }

        var completePhysicalPageCount = data.Length / NandPhysicalGeometry.PhysicalPageSize;
        var sampleCount = Math.Min(completePhysicalPageCount, maximumPagesToInspect);
        var layout0Matches = 0;
        var layout1Matches = 0;
        var layout2Matches = 0;
        var validEccPageCount = 0;

        Report(progress, completed: 0, sampleCount);
        for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pageIndex = GetSamplePageIndex(sampleIndex, sampleCount, completePhysicalPageCount);
            var physicalPage = data.Slice(
                pageIndex * NandPhysicalGeometry.PhysicalPageSize,
                NandPhysicalGeometry.PhysicalPageSize);
            if (NandEccCodec.HasValidEcc(physicalPage))
            {
                validEccPageCount++;
                RecordMatchingLayouts(
                    physicalPage,
                    pageIndex,
                    firstPhysicalBlock,
                    ref layout0Matches,
                    ref layout1Matches,
                    ref layout2Matches);
            }

            var completed = sampleIndex + 1;
            if (completed == sampleCount || completed % 0x20 == 0)
            {
                Report(progress, completed, sampleCount);
            }
        }

        var candidateLayouts = GetCandidateLayouts(
            layout0Matches,
            layout1Matches,
            layout2Matches,
            validEccPageCount);
        var isPhysicalLength = data.Length % NandPhysicalGeometry.PhysicalPageSize == 0;
        var isLogicalLength = data.Length % NandPhysicalGeometry.LogicalPageSize == 0;
        var format = ClassifyFormat(isPhysicalLength, isLogicalLength, validEccPageCount);
        return new NandPhysicalFormatDetection(
            format,
            candidateLayouts,
            completePhysicalPageCount,
            sampleCount,
            validEccPageCount);
    }

    private static NandPhysicalFormat ClassifyFormat(bool isPhysicalLength, bool isLogicalLength, int validEccPageCount)
    {
        if (!isPhysicalLength)
        {
            return validEccPageCount > 0 || !isLogicalLength
                ? NandPhysicalFormat.Truncated
                : NandPhysicalFormat.Logical;
        }

        if (validEccPageCount > 0 || !isLogicalLength)
        {
            return NandPhysicalFormat.InterleavedEcc;
        }

        return NandPhysicalFormat.Ambiguous;
    }

    private static int GetSamplePageIndex(int sampleIndex, int sampleCount, int pageCount)
    {
        if (sampleCount <= 1)
        {
            return 0;
        }

        return (int)((long)sampleIndex * (pageCount - 1) / (sampleCount - 1));
    }

    private static void RecordMatchingLayouts(
        ReadOnlySpan<byte> physicalPage,
        int pageIndex,
        long firstPhysicalBlock,
        ref int layout0Matches,
        ref int layout1Matches,
        ref int layout2Matches)
    {
        if (MatchesLayout(physicalPage, pageIndex, firstPhysicalBlock, NandPhysicalLayout.Layout0))
        {
            layout0Matches++;
        }

        if (MatchesLayout(physicalPage, pageIndex, firstPhysicalBlock, NandPhysicalLayout.Layout1))
        {
            layout1Matches++;
        }

        if (MatchesLayout(physicalPage, pageIndex, firstPhysicalBlock, NandPhysicalLayout.Layout2))
        {
            layout2Matches++;
        }
    }

    private static bool MatchesLayout(
        ReadOnlySpan<byte> physicalPage,
        int pageIndex,
        long firstPhysicalBlock,
        NandPhysicalLayout layout)
    {
        var physicalBlock = checked(firstPhysicalBlock + (pageIndex / layout.Geometry.PagesPerBlock));
        return MatchesGeneratedSpare(physicalPage, layout, physicalBlock);
    }

    private static bool MatchesGeneratedSpare(
        ReadOnlySpan<byte> physicalPage,
        NandPhysicalLayout layout,
        long physicalBlock)
    {
        var spare = physicalPage.Slice(NandPhysicalGeometry.LogicalPageSize, NandPhysicalGeometry.SpareSize);
        var blockId = unchecked((ushort)physicalBlock);
        for (var spareOffset = 0; spareOffset < NandPhysicalGeometry.SpareSize - sizeof(uint); spareOffset++)
        {
            var expected = (byte)0;
            if (spareOffset == layout.MarkerSpareOffset)
            {
                expected = byte.MaxValue;
            }
            else if (spareOffset == layout.BlockIdSpareOffset)
            {
                expected = (byte)blockId;
            }
            else if (spareOffset == layout.BlockIdSpareOffset + 1)
            {
                expected = (byte)(blockId >> 8);
            }

            if (spare[spareOffset] != expected)
            {
                return false;
            }
        }

        return true;
    }

    private static ImmutableArray<NandPhysicalLayout> GetCandidateLayouts(
        int layout0Matches,
        int layout1Matches,
        int layout2Matches,
        int validEccPageCount)
    {
        if (validEccPageCount == 0)
        {
            return ImmutableArray<NandPhysicalLayout>.Empty;
        }

        var hasLayout0 = layout0Matches == validEccPageCount;
        var hasLayout1 = layout1Matches == validEccPageCount;
        var hasLayout2 = layout2Matches == validEccPageCount;
        var candidateCount = (hasLayout0 ? 1 : 0) + (hasLayout1 ? 1 : 0) + (hasLayout2 ? 1 : 0);

        return candidateCount switch
        {
            0 => ImmutableArray<NandPhysicalLayout>.Empty,
            1 when hasLayout0 => ImmutableArray.Create(NandPhysicalLayout.Layout0),
            1 when hasLayout1 => ImmutableArray.Create(NandPhysicalLayout.Layout1),
            1 => ImmutableArray.Create(NandPhysicalLayout.Layout2),
            2 when !hasLayout0 => ImmutableArray.Create(NandPhysicalLayout.Layout1, NandPhysicalLayout.Layout2),
            2 when !hasLayout1 => ImmutableArray.Create(NandPhysicalLayout.Layout0, NandPhysicalLayout.Layout2),
            2 => ImmutableArray.Create(NandPhysicalLayout.Layout0, NandPhysicalLayout.Layout1),
            _ => ImmutableArray.Create(
                NandPhysicalLayout.Layout0,
                NandPhysicalLayout.Layout1,
                NandPhysicalLayout.Layout2),
        };
    }

    private static void Report(IProgress<OperationProgress>? progress, int completed, int total)
    {
        progress?.Report(new OperationProgress(
            "detecting-physical-format",
            "Detecting physical NAND page format.",
            completed: completed,
            total: total));
    }
}
