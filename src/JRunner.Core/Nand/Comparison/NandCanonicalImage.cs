using System.Buffers;
using System.Collections.Immutable;
using JRunner.Core.Contracts;
using JRunner.Core.Nand.Physical;

namespace JRunner.Core.Nand.Comparison;

/// <summary>
/// A prepared, bounded-memory canonical NAND projection for same-assembly consumers such as inspection.
/// </summary>
internal sealed class NandCanonicalImage : IDisposable, IAsyncDisposable
{
    private readonly CanonicalBackingStore backingStore;
    private readonly NandPhysicalLayout? physicalLayout;
    private readonly ImmutableDictionary<long, long> replacementByBadBlock;
    private readonly ImmutableHashSet<long> consumedReplacementBlocks;
    private bool disposed;

    internal NandCanonicalImage(
        CanonicalBackingStore backingStore,
        NandCanonicalImageSummary summary,
        NandPhysicalLayout? physicalLayout,
        ImmutableDictionary<long, long> replacementByBadBlock,
        ImmutableHashSet<long> consumedReplacementBlocks)
    {
        ArgumentNullException.ThrowIfNull(backingStore);
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(replacementByBadBlock);
        ArgumentNullException.ThrowIfNull(consumedReplacementBlocks);
        if (summary.HasSpareData != (physicalLayout is not null))
        {
            throw new ArgumentException("The physical layout must agree with canonical image metadata.", nameof(physicalLayout));
        }

        this.backingStore = backingStore;
        Summary = summary;
        this.physicalLayout = physicalLayout;
        this.replacementByBadBlock = replacementByBadBlock;
        this.consumedReplacementBlocks = consumedReplacementBlocks;
    }

    internal NandCanonicalImageSummary Summary { get; }

    /// <summary>
    /// Reads a range from the virtual canonical logical projection without materializing the complete image.
    /// </summary>
    /// <returns>The number of bytes read, or zero at the canonical end.</returns>
    internal async ValueTask<int> ReadLogicalAsync(
        long logicalOffset,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (logicalOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(logicalOffset), logicalOffset, "The logical offset cannot be negative.");
        }

        if (destination.IsEmpty || logicalOffset >= Summary.CanonicalLogicalByteLength)
        {
            return 0;
        }

        int byteCount = (int)Math.Min(destination.Length, Summary.CanonicalLogicalByteLength - logicalOffset);
        Memory<byte> target = destination.Slice(0, byteCount);
        cancellationToken.ThrowIfCancellationRequested();
        if (physicalLayout is null)
        {
            await backingStore.ReadExactlyAtAsync(logicalOffset, target, cancellationToken).ConfigureAwait(false);
            return byteCount;
        }

        await ReadPhysicalLogicalRangeAsync(physicalLayout, logicalOffset, target, cancellationToken).ConfigureAwait(false);
        return byteCount;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        backingStore.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private async ValueTask ReadPhysicalLogicalRangeAsync(
        NandPhysicalLayout layout,
        long logicalOffset,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        byte[] physicalPage = ArrayPool<byte>.Shared.Rent(NandPhysicalGeometry.PhysicalPageSize);
        try
        {
            int written = 0;
            long currentLogicalOffset = logicalOffset;
            while (written < destination.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long virtualBlock = currentLogicalOffset / layout.Geometry.LogicalBlockSize;
                int blockOffset = (int)(currentLogicalOffset % layout.Geometry.LogicalBlockSize);
                int blockByteCount = Math.Min(destination.Length - written, layout.Geometry.LogicalBlockSize - blockOffset);
                Memory<byte> blockDestination = destination.Slice(written, blockByteCount);

                if (consumedReplacementBlocks.Contains(virtualBlock))
                {
                    blockDestination.Span.Fill(byte.MaxValue);
                }
                else
                {
                    long sourceBlock = replacementByBadBlock.TryGetValue(virtualBlock, out long replacement)
                        ? replacement
                        : virtualBlock;
                    await CopyPhysicalLogicalBlockRangeAsync(
                        layout,
                        sourceBlock,
                        blockOffset,
                        blockDestination,
                        physicalPage,
                        cancellationToken).ConfigureAwait(false);
                }

                written += blockByteCount;
                currentLogicalOffset = checked(currentLogicalOffset + blockByteCount);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(physicalPage);
        }
    }

    private async ValueTask CopyPhysicalLogicalBlockRangeAsync(
        NandPhysicalLayout layout,
        long sourceBlock,
        int logicalBlockOffset,
        Memory<byte> destination,
        byte[] physicalPage,
        CancellationToken cancellationToken)
    {
        int copied = 0;
        int currentOffset = logicalBlockOffset;
        while (copied < destination.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int pageIndex = currentOffset / NandPhysicalGeometry.LogicalPageSize;
            int pageOffset = currentOffset % NandPhysicalGeometry.LogicalPageSize;
            int pageByteCount = Math.Min(
                destination.Length - copied,
                NandPhysicalGeometry.LogicalPageSize - pageOffset);
            long physicalOffset = checked(
                (sourceBlock * (long)layout.Geometry.PhysicalBlockSize) +
                ((long)pageIndex * NandPhysicalGeometry.PhysicalPageSize));

            await backingStore.ReadExactlyAtAsync(
                physicalOffset,
                physicalPage.AsMemory(0, NandPhysicalGeometry.PhysicalPageSize),
                cancellationToken).ConfigureAwait(false);
            physicalPage.AsMemory(pageOffset, pageByteCount).CopyTo(destination.Slice(copied, pageByteCount));

            copied += pageByteCount;
            currentOffset += pageByteCount;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}

/// <summary>
/// Builds a reusable canonical NAND projection while retaining only page/block-sized working buffers.
/// </summary>
internal static class NandCanonicalImagePreparer
{
    private const int PhysicalProbePageLimit = 0x80;
    private const byte SmallBlockFilesystemType = 0x30;
    private const byte SpareBlockTypeMask = 0x3F;
    private const int MaximumBadBlockCount = 0x20;
    private const int ReservedReplacementBlockCount = 0x20;
    private const long SmallBlockReservationBoundary = 0x4200000L;
    private const long SmallBlockReservationStart = 0x3E0L;
    private const long LargeSmallBlockReservationStart = 0xF80L;
    private const long LargeBlockReservationStart = 0x1E0L;
    private const long LargestLegacyLogicalNandByteLength = 0x20000000L;
    private const long LargestLegacyPhysicalNandRawByteLength = 0x21000000L;
    private const long CoronaOrWinchesterEmmcRawByteLength = 0xE0400000L;

    internal static async Task<NandCanonicalImage> PrepareAsync(
        NandCanonicalInput input,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        CanonicalBackingStore? backingStore = null;
        try
        {
            backingStore = await CanonicalBackingStore.CreateAsync(input.Source, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (backingStore.Length == 0)
            {
                throw InvalidData("empty-nand", "A NAND input cannot be empty.");
            }

            FormatSelection selection = await SelectFormatAsync(
                input,
                backingStore,
                progress,
                cancellationToken).ConfigureAwait(false);
            if (!selection.HasSpareData)
            {
                var logicalSummary = new NandCanonicalImageSummary(
                    detectedFormat: NandPhysicalFormat.Logical,
                    selectedLayout: null,
                    hasSpareData: false,
                    rawByteLength: backingStore.Length,
                    canonicalLogicalByteLength: backingStore.Length,
                    physicalBlockCount: 0,
                    badBlocks: ImmutableArray<NandBadBlock>.Empty,
                    remaps: ImmutableArray<NandBadBlockRemap>.Empty);
                var logicalImage = new NandCanonicalImage(
                    backingStore: backingStore,
                    summary: logicalSummary,
                    physicalLayout: null,
                    replacementByBadBlock: ImmutableDictionary<long, long>.Empty,
                    consumedReplacementBlocks: ImmutableHashSet<long>.Empty);
                backingStore = null;
                return logicalImage;
            }

            NandPhysicalLayout layout = selection.Layout ?? throw new InvalidOperationException("Physical NAND selection requires a layout.");
            if (backingStore.Length % NandPhysicalGeometry.PhysicalPageSize != 0)
            {
                throw InvalidData("truncated-physical-page", "Physical NAND data must contain complete 0x210-byte pages.");
            }

            if (backingStore.Length % layout.Geometry.PhysicalBlockSize != 0)
            {
                throw InvalidData(
                    "truncated-physical-block",
                    $"Physical NAND data must contain complete 0x{layout.Geometry.PhysicalBlockSize:X}-byte blocks for the selected layout.");
            }

            if (backingStore.Length > LargestLegacyPhysicalNandRawByteLength)
            {
                throw InvalidData(
                    "unsupported-nand-format",
                    "The physical NAND length exceeds the largest supported legacy NAND capacity.");
            }

            long physicalBlockCount = backingStore.Length / layout.Geometry.PhysicalBlockSize;
            ImmutableArray<NandBadBlock> badBlocks = await ScanBadBlocksAsync(
                backingStore,
                layout,
                physicalBlockCount,
                progress,
                cancellationToken).ConfigureAwait(false);
            ImmutableArray<NandBadBlockRemap> remaps = await DiscoverRemapsAsync(
                backingStore,
                layout,
                physicalBlockCount,
                badBlocks,
                progress,
                cancellationToken).ConfigureAwait(false);
            await ValidatePhysicalStructureAsync(
                backingStore,
                layout,
                physicalBlockCount,
                badBlocks,
                remaps,
                progress,
                cancellationToken).ConfigureAwait(false);

            ImmutableDictionary<long, long> replacementByBadBlock = CreateReplacementByBadBlock(remaps);
            ImmutableHashSet<long> consumedReplacementBlocks = CreateConsumedReplacementBlocks(remaps);
            var physicalSummary = new NandCanonicalImageSummary(
                detectedFormat: NandPhysicalFormat.InterleavedEcc,
                selectedLayout: layout.LegacyLayout,
                hasSpareData: true,
                rawByteLength: backingStore.Length,
                canonicalLogicalByteLength: checked(
                    (backingStore.Length / NandPhysicalGeometry.PhysicalPageSize) * NandPhysicalGeometry.LogicalPageSize),
                physicalBlockCount: physicalBlockCount,
                badBlocks: badBlocks,
                remaps: remaps);
            var physicalImage = new NandCanonicalImage(
                backingStore: backingStore,
                summary: physicalSummary,
                physicalLayout: layout,
                replacementByBadBlock: replacementByBadBlock,
                consumedReplacementBlocks: consumedReplacementBlocks);
            backingStore = null;
            return physicalImage;
        }
        catch
        {
            backingStore?.Dispose();
            throw;
        }
    }

    private static async Task<FormatSelection> SelectFormatAsync(
        NandCanonicalInput input,
        CanonicalBackingStore backingStore,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (input.Format == NandCanonicalInputFormat.Logical)
        {
            ValidateLogicalLength(backingStore.Length);
            return new FormatSelection(NandPhysicalFormat.Logical, Layout: null);
        }

        NandPhysicalProbe probe = await ProbePhysicalPagesAsync(
            backingStore,
            progress,
            cancellationToken).ConfigureAwait(false);
        bool logicalAligned = backingStore.Length % NandPhysicalGeometry.LogicalPageSize == 0;
        bool physicalAligned = backingStore.Length % NandPhysicalGeometry.PhysicalPageSize == 0;

        if (input.Format == NandCanonicalInputFormat.InterleavedEcc)
        {
            if (!physicalAligned)
            {
                throw InvalidData("truncated-physical-page", "Physical NAND data must contain complete 0x210-byte pages.");
            }

            return new FormatSelection(
                NandPhysicalFormat.InterleavedEcc,
                ResolveLayout(input.LayoutHint, probe));
        }

        if (probe.HasLayoutEvidence)
        {
            if (!physicalAligned)
            {
                throw InvalidData(
                    "truncated-physical-page",
                    "Physical ECC and spare-layout evidence was found before a partial 0x210-byte page.");
            }

            return new FormatSelection(
                NandPhysicalFormat.InterleavedEcc,
                ResolveLayout(input.LayoutHint, probe));
        }

        if (input.LayoutHint is { } hintedLayout)
        {
            if (!physicalAligned)
            {
                throw InvalidData(
                    "truncated-physical-page",
                    "A physical layout hint requires complete 0x210-byte physical pages.");
            }

            return new FormatSelection(NandPhysicalFormat.InterleavedEcc, ResolveLayout(hintedLayout));
        }

        if (physicalAligned && logicalAligned)
        {
            throw InvalidData(
                "ambiguous-nand-format",
                "The NAND length is both logical-page and physical-page aligned without ECC evidence.");
        }

        if (physicalAligned)
        {
            throw InvalidData("invalid-physical-page", "Physical NAND pages contain no valid ECC evidence.");
        }

        if (logicalAligned)
        {
            ValidateLogicalLength(backingStore.Length);
            return new FormatSelection(NandPhysicalFormat.Logical, Layout: null);
        }

        if (probe.CompletePhysicalPageCount > 0)
        {
            throw InvalidData("truncated-nand", "The NAND input ends in a partial page without conclusive ECC evidence.");
        }

        throw InvalidData("truncated-nand", "The NAND input does not contain complete logical or physical pages.");
    }

    private static async Task<NandPhysicalProbe> ProbePhysicalPagesAsync(
        CanonicalBackingStore backingStore,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        long completePhysicalPageCount = backingStore.Length / NandPhysicalGeometry.PhysicalPageSize;
        int sampleCount = (int)Math.Min(completePhysicalPageCount, PhysicalProbePageLimit);
        if (sampleCount == 0)
        {
            return new NandPhysicalProbe(completePhysicalPageCount, 0, 0, 0, 0);
        }

        int layout0Matches = 0;
        int layout1Matches = 0;
        int layout2Matches = 0;
        int validEccPageCount = 0;
        byte[] physicalPage = ArrayPool<byte>.Shared.Rent(NandPhysicalGeometry.PhysicalPageSize);
        try
        {
            Report(progress, "detecting-canonical-nand-format", "Detecting physical NAND page format.", completed: 0, sampleCount);
            for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long pageIndex = GetSamplePageIndex(sampleIndex, sampleCount, completePhysicalPageCount);
                await backingStore.ReadExactlyAtAsync(
                    checked(pageIndex * NandPhysicalGeometry.PhysicalPageSize),
                    physicalPage.AsMemory(0, NandPhysicalGeometry.PhysicalPageSize),
                    cancellationToken).ConfigureAwait(false);
                if (!IsErasedPhysicalPage(physicalPage.AsSpan(0, NandPhysicalGeometry.PhysicalPageSize)) &&
                    NandEccCodec.HasValidEcc(physicalPage.AsSpan(0, NandPhysicalGeometry.PhysicalPageSize)))
                {
                    validEccPageCount++;
                    // Replacement blocks can retain their bad source ID, while Layout0 filesystem
                    // pages can repurpose spare byte zero. Both are intentionally weak evidence.
                    if (MatchesSpareLayoutIdentity(
                        physicalPage.AsSpan(0, NandPhysicalGeometry.PhysicalPageSize),
                        NandPhysicalLayout.Layout0,
                        pageIndex / NandPhysicalLayout.Layout0.Geometry.PagesPerBlock))
                    {
                        layout0Matches++;
                    }

                    if (MatchesSpareLayoutIdentity(
                        physicalPage.AsSpan(0, NandPhysicalGeometry.PhysicalPageSize),
                        NandPhysicalLayout.Layout1,
                        pageIndex / NandPhysicalLayout.Layout1.Geometry.PagesPerBlock))
                    {
                        layout1Matches++;
                    }

                    if (MatchesSpareLayoutIdentity(
                        physicalPage.AsSpan(0, NandPhysicalGeometry.PhysicalPageSize),
                        NandPhysicalLayout.Layout2,
                        pageIndex / NandPhysicalLayout.Layout2.Geometry.PagesPerBlock))
                    {
                        layout2Matches++;
                    }
                }

                int completed = sampleIndex + 1;
                if (completed == sampleCount || completed % 0x20 == 0)
                {
                    Report(progress, "detecting-canonical-nand-format", "Detecting physical NAND page format.", completed, sampleCount);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(physicalPage);
        }

        return new NandPhysicalProbe(
            completePhysicalPageCount,
            validEccPageCount,
            layout0Matches,
            layout1Matches,
            layout2Matches);
    }

    private static NandPhysicalLayout ResolveLayout(NandLegacyLayout? layoutHint, NandPhysicalProbe probe)
    {
        if (layoutHint is { } hintedLayout)
        {
            return ResolveLayout(hintedLayout);
        }

        ImmutableArray<NandPhysicalLayout> candidates = probe.CandidateLayouts;
        if (candidates.Length == 1)
        {
            return candidates[0];
        }

        if (candidates.Length > 1)
        {
            throw InvalidData("ambiguous-nand-layout", "Physical ECC data matches more than one legacy spare layout.");
        }

        if (probe.ValidEccPageCount == 0)
        {
            throw InvalidData("invalid-physical-page", "Physical NAND pages contain no valid ECC evidence.");
        }

        throw InvalidData("invalid-physical-spare", "Physical NAND spare data does not match a supported legacy layout.");
    }

    private static NandPhysicalLayout ResolveLayout(NandLegacyLayout layout)
    {
        if (!Enum.IsDefined(layout))
        {
            throw InvalidData("unsupported-nand-layout", "The supplied NAND spare layout is not supported.");
        }

        return NandPhysicalLayout.FromLegacyLayout(layout);
    }

    private static void ValidateLogicalLength(long rawByteLength)
    {
        if (rawByteLength % NandPhysicalGeometry.LogicalPageSize != 0)
        {
            throw InvalidData("truncated-logical-nand", "Logical NAND data must contain complete 0x200-byte pages.");
        }

        if (rawByteLength > LargestLegacyLogicalNandByteLength &&
            rawByteLength != CoronaOrWinchesterEmmcRawByteLength)
        {
            throw InvalidData(
                "unsupported-nand-format",
                "The logical NAND length is not a supported legacy NAND or Corona/Winchester eMMC size.");
        }
    }

    private static async Task<ImmutableArray<NandBadBlock>> ScanBadBlocksAsync(
        CanonicalBackingStore backingStore,
        NandPhysicalLayout layout,
        long physicalBlockCount,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ImmutableArray<NandBadBlock>.Builder? badBlocks = null;
        byte[] physicalBlock = ArrayPool<byte>.Shared.Rent(layout.Geometry.PhysicalBlockSize);
        try
        {
            Report(progress, "scanning-canonical-bad-blocks", "Scanning NAND physical blocks for bad-block markers.", completed: 0, physicalBlockCount);
            for (long blockIndex = 0; blockIndex < physicalBlockCount; blockIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await backingStore.ReadExactlyAtAsync(
                    checked(blockIndex * (long)layout.Geometry.PhysicalBlockSize),
                    physicalBlock.AsMemory(0, layout.Geometry.PhysicalBlockSize),
                    cancellationToken).ConfigureAwait(false);
                NandBadBlock? badBlock = NandBadBlockService.DetectBadBlock(
                    physicalBlock.AsSpan(0, layout.Geometry.PhysicalBlockSize),
                    layout,
                    blockIndex);
                if (badBlock is not null)
                {
                    (badBlocks ??= ImmutableArray.CreateBuilder<NandBadBlock>()).Add(badBlock);
                    if (badBlocks.Count > MaximumBadBlockCount)
                    {
                        throw InvalidData("too-many-bad-blocks", "A NAND image cannot contain more than 0x20 bad blocks.");
                    }
                }

                long completed = blockIndex + 1;
                if (completed == physicalBlockCount || completed % 0x20 == 0)
                {
                    Report(progress, "scanning-canonical-bad-blocks", "Scanning NAND physical blocks for bad-block markers.", completed, physicalBlockCount);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(physicalBlock);
        }

        return badBlocks is null ? ImmutableArray<NandBadBlock>.Empty : badBlocks.ToImmutable();
    }

    private static async Task<ImmutableArray<NandBadBlockRemap>> DiscoverRemapsAsync(
        CanonicalBackingStore backingStore,
        NandPhysicalLayout layout,
        long physicalBlockCount,
        ImmutableArray<NandBadBlock> badBlocks,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (badBlocks.IsEmpty)
        {
            return ImmutableArray<NandBadBlockRemap>.Empty;
        }

        long firstReservedPhysicalBlock = GetFirstReservedPhysicalBlock(layout, backingStore.Length);
        long requiredBlockCount = checked(firstReservedPhysicalBlock + ReservedReplacementBlockCount);
        if (physicalBlockCount < requiredBlockCount)
        {
            throw InvalidData(
                "truncated-reserved-area",
                "The NAND image does not include the complete 32-block bad-block replacement area.");
        }

        long?[] replacements = new long?[badBlocks.Length];
        byte[] physicalBlock = ArrayPool<byte>.Shared.Rent(layout.Geometry.PhysicalBlockSize);
        try
        {
            int candidateCount = ReservedReplacementBlockCount - 1;
            Report(progress, "discovering-canonical-remaps", "Discovering reserved NAND bad-block replacements.", completed: 0, candidateCount);
            for (int candidateIndex = ReservedReplacementBlockCount - 1; candidateIndex > 0; candidateIndex--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long replacementPhysicalBlock = checked(firstReservedPhysicalBlock + candidateIndex);
                await backingStore.ReadExactlyAtAsync(
                    checked(replacementPhysicalBlock * (long)layout.Geometry.PhysicalBlockSize),
                    physicalBlock.AsMemory(0, layout.Geometry.PhysicalBlockSize),
                    cancellationToken).ConfigureAwait(false);
                if (IsReplacementCandidate(
                    physicalBlock.AsSpan(0, layout.Geometry.PhysicalBlockSize),
                    layout,
                    replacementPhysicalBlock))
                {
                    for (int badBlockIndex = 0; badBlockIndex < badBlocks.Length; badBlockIndex++)
                    {
                        if (replacements[badBlockIndex].HasValue ||
                            replacementPhysicalBlock == badBlocks[badBlockIndex].PhysicalBlock ||
                            !CandidateMatchesBadBlock(
                                physicalBlock.AsSpan(0, layout.Geometry.PhysicalBlockSize),
                                layout,
                                badBlocks[badBlockIndex].PhysicalBlock))
                        {
                            continue;
                        }

                        replacements[badBlockIndex] = replacementPhysicalBlock;
                        break;
                    }
                }

                int completed = ReservedReplacementBlockCount - candidateIndex;
                if (completed == candidateCount || completed % 0x20 == 0)
                {
                    Report(progress, "discovering-canonical-remaps", "Discovering reserved NAND bad-block replacements.", completed, candidateCount);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(physicalBlock);
        }

        var remaps = ImmutableArray.CreateBuilder<NandBadBlockRemap>(badBlocks.Length);
        for (int index = 0; index < badBlocks.Length; index++)
        {
            remaps.Add(new NandBadBlockRemap(badBlocks[index], replacements[index]));
        }

        ImmutableArray<NandBadBlockRemap> result = remaps.MoveToImmutable();
        ValidateDiscoveredRemaps(result, physicalBlockCount);
        return result;
    }

    private static async Task ValidatePhysicalStructureAsync(
        CanonicalBackingStore backingStore,
        NandPhysicalLayout layout,
        long physicalBlockCount,
        ImmutableArray<NandBadBlock> badBlocks,
        ImmutableArray<NandBadBlockRemap> remaps,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var badPhysicalBlocks = new HashSet<long>(badBlocks.Length);
        foreach (NandBadBlock badBlock in badBlocks)
        {
            if (!badPhysicalBlocks.Add(badBlock.PhysicalBlock))
            {
                throw InvalidData("invalid-remap", "Bad-block records must use unique physical block numbers.");
            }
        }

        var sourceBadBlockByReplacement = new Dictionary<long, long>();
        foreach (NandBadBlockRemap remap in remaps)
        {
            if (remap.ReplacementPhysicalBlock is not { } replacementPhysicalBlock)
            {
                continue;
            }

            if (!sourceBadBlockByReplacement.TryAdd(replacementPhysicalBlock, remap.BadBlock.PhysicalBlock))
            {
                throw InvalidData("invalid-remap", "Replacement physical blocks must be unique.");
            }
        }

        byte[] physicalBlock = ArrayPool<byte>.Shared.Rent(layout.Geometry.PhysicalBlockSize);
        try
        {
            Report(progress, "validating-canonical-physical-nand", "Validating physical NAND ECC and spare data.", completed: 0, physicalBlockCount);
            for (long blockIndex = 0; blockIndex < physicalBlockCount; blockIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await backingStore.ReadExactlyAtAsync(
                    checked(blockIndex * (long)layout.Geometry.PhysicalBlockSize),
                    physicalBlock.AsMemory(0, layout.Geometry.PhysicalBlockSize),
                    cancellationToken).ConfigureAwait(false);
                if (!badPhysicalBlocks.Contains(blockIndex))
                {
                    long expectedBlockId = sourceBadBlockByReplacement.TryGetValue(blockIndex, out long sourceBadBlock)
                        ? sourceBadBlock
                        : blockIndex;
                    ValidateNonBadPhysicalBlock(
                        physicalBlock.AsSpan(0, layout.Geometry.PhysicalBlockSize),
                        layout,
                        expectedBlockId);
                }

                long completed = blockIndex + 1;
                if (completed == physicalBlockCount || completed % 0x20 == 0)
                {
                    Report(progress, "validating-canonical-physical-nand", "Validating physical NAND ECC and spare data.", completed, physicalBlockCount);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(physicalBlock);
        }
    }

    private static ImmutableDictionary<long, long> CreateReplacementByBadBlock(
        ImmutableArray<NandBadBlockRemap> remaps)
    {
        var replacements = ImmutableDictionary.CreateBuilder<long, long>();
        foreach (NandBadBlockRemap remap in remaps)
        {
            if (remap.ReplacementPhysicalBlock is not { } replacementPhysicalBlock)
            {
                continue;
            }

            if (replacements.ContainsKey(remap.BadBlock.PhysicalBlock))
            {
                throw InvalidData("invalid-remap", "Bad-block remaps must use unique source physical blocks.");
            }

            replacements.Add(remap.BadBlock.PhysicalBlock, replacementPhysicalBlock);
        }

        return replacements.ToImmutable();
    }

    private static ImmutableHashSet<long> CreateConsumedReplacementBlocks(
        ImmutableArray<NandBadBlockRemap> remaps)
    {
        var replacements = ImmutableHashSet.CreateBuilder<long>();
        foreach (NandBadBlockRemap remap in remaps)
        {
            if (remap.ReplacementPhysicalBlock is { } replacementPhysicalBlock && !replacements.Add(replacementPhysicalBlock))
            {
                throw InvalidData("invalid-remap", "Bad-block remaps must use unique replacement physical blocks.");
            }
        }

        return replacements.ToImmutable();
    }

    private static void ValidateNonBadPhysicalBlock(
        ReadOnlySpan<byte> physicalBlock,
        NandPhysicalLayout layout,
        long expectedBlockId)
    {
        for (int pageIndex = 0; pageIndex < layout.Geometry.PagesPerBlock; pageIndex++)
        {
            ReadOnlySpan<byte> physicalPage = physicalBlock.Slice(
                pageIndex * NandPhysicalGeometry.PhysicalPageSize,
                NandPhysicalGeometry.PhysicalPageSize);
            if (IsErasedPhysicalPage(physicalPage))
            {
                continue;
            }

            if (!MatchesSpareLayoutIdentity(physicalPage, layout, expectedBlockId))
            {
                throw InvalidData("invalid-physical-spare", "Physical NAND spare bytes do not match the selected layout marker and block ID.");
            }

            if (!NandEccCodec.HasValidEcc(physicalPage))
            {
                throw InvalidData("invalid-physical-page", "A non-bad physical NAND page has invalid ECC.");
            }
        }
    }

    private static bool IsReplacementCandidate(
        ReadOnlySpan<byte> candidate,
        NandPhysicalLayout layout,
        long replacementPhysicalBlock)
    {
        ReadOnlySpan<byte> firstPage = candidate.Slice(0, NandPhysicalGeometry.PhysicalPageSize);
        ReadOnlySpan<byte> lastPage = candidate.Slice(
            candidate.Length - NandPhysicalGeometry.PhysicalPageSize,
            NandPhysicalGeometry.PhysicalPageSize);
        if (NandBadBlockService.DetectBadBlock(candidate, layout, replacementPhysicalBlock) is not null)
        {
            return false;
        }

        ReadOnlySpan<byte> firstSpare = firstPage.Slice(NandPhysicalGeometry.LogicalPageSize, NandPhysicalGeometry.SpareSize);
        ReadOnlySpan<byte> lastSpare = lastPage.Slice(NandPhysicalGeometry.LogicalPageSize, NandPhysicalGeometry.SpareSize);
        if (IsAll(firstSpare, byte.MaxValue) && IsAll(lastSpare, byte.MaxValue))
        {
            return false;
        }

        return firstPage[layout.MarkerOffset] == byte.MaxValue || lastPage[layout.MarkerOffset] == byte.MaxValue;
    }

    private static bool CandidateMatchesBadBlock(
        ReadOnlySpan<byte> candidate,
        NandPhysicalLayout layout,
        long badPhysicalBlock)
    {
        if (badPhysicalBlock > ushort.MaxValue)
        {
            return false;
        }

        ReadOnlySpan<byte> firstPage = candidate.Slice(0, NandPhysicalGeometry.PhysicalPageSize);
        ReadOnlySpan<byte> lastPage = candidate.Slice(
            candidate.Length - NandPhysicalGeometry.PhysicalPageSize,
            NandPhysicalGeometry.PhysicalPageSize);
        ushort expectedBlockId = unchecked((ushort)badPhysicalBlock);
        return HasBlockId(firstPage, layout, expectedBlockId) || HasBlockId(lastPage, layout, expectedBlockId);
    }

    private static bool HasBlockId(ReadOnlySpan<byte> physicalPage, NandPhysicalLayout layout, ushort expectedBlockId) =>
        physicalPage[layout.BlockIdOffset] == (byte)expectedBlockId &&
        physicalPage[layout.BlockIdOffset + 1] == (byte)(expectedBlockId >> 8);

    private static void ValidateDiscoveredRemaps(
        ImmutableArray<NandBadBlockRemap> remaps,
        long physicalBlockCount)
    {
        var badBlocks = new HashSet<long>(remaps.Length);
        foreach (NandBadBlockRemap remap in remaps)
        {
            if (!badBlocks.Add(remap.BadBlock.PhysicalBlock))
            {
                throw InvalidData("invalid-remap", "Bad-block remaps must use unique source physical blocks.");
            }
        }

        var replacementBlocks = new HashSet<long>();
        foreach (NandBadBlockRemap remap in remaps)
        {
            if (remap.ReplacementPhysicalBlock is not { } replacementPhysicalBlock)
            {
                continue;
            }

            if (remap.BadBlock.PhysicalBlock < 0 || remap.BadBlock.PhysicalBlock >= physicalBlockCount ||
                replacementPhysicalBlock < 0 || replacementPhysicalBlock >= physicalBlockCount ||
                badBlocks.Contains(replacementPhysicalBlock) ||
                !replacementBlocks.Add(replacementPhysicalBlock))
            {
                throw InvalidData("invalid-remap", "A discovered bad-block remap is not a valid one-to-one physical-block mapping.");
            }
        }
    }

    private static long GetFirstReservedPhysicalBlock(NandPhysicalLayout layout, long rawByteLength) =>
        layout.LegacyLayout == NandLegacyLayout.Layout2
            ? LargeBlockReservationStart
            : rawByteLength < SmallBlockReservationBoundary
                ? SmallBlockReservationStart
                : LargeSmallBlockReservationStart;

    private static bool MatchesSpareLayoutIdentity(
        ReadOnlySpan<byte> physicalPage,
        NandPhysicalLayout layout,
        long physicalBlock)
    {
        if (physicalBlock > ushort.MaxValue || physicalPage[layout.MarkerOffset] != byte.MaxValue)
        {
            return false;
        }

        // Small-block Layout0 filesystem pages use spare byte zero as part of the sequence
        // metadata and identify themselves through the low six bits of the ECC word.
        if (layout.LegacyLayout == NandLegacyLayout.Layout0 &&
            (physicalPage[NandPhysicalGeometry.PhysicalPageSize - sizeof(uint)] & SpareBlockTypeMask) == SmallBlockFilesystemType)
        {
            return true;
        }

        var expectedBlockId = unchecked((ushort)physicalBlock);
        return HasBlockId(physicalPage, layout, expectedBlockId);
    }

    private static bool IsErasedPhysicalPage(ReadOnlySpan<byte> physicalPage) => IsAll(physicalPage, byte.MaxValue);

    private static long GetSamplePageIndex(int sampleIndex, int sampleCount, long pageCount)
    {
        if (sampleCount <= 1)
        {
            return 0;
        }

        return checked((long)sampleIndex * (pageCount - 1) / (sampleCount - 1));
    }

    private static bool IsAll(ReadOnlySpan<byte> bytes, byte value)
    {
        foreach (byte current in bytes)
        {
            if (current != value)
            {
                return false;
            }
        }

        return true;
    }

    private static OperationFailureException InvalidData(string kind, string message) =>
        new(ExitCode.InvalidData, kind, message);

    private static void Report(
        IProgress<OperationProgress>? progress,
        string kind,
        string message,
        long completed,
        long total)
    {
        progress?.Report(new OperationProgress(kind, message, completed: completed, total: total));
    }

    private readonly record struct FormatSelection(NandPhysicalFormat Format, NandPhysicalLayout? Layout)
    {
        internal bool HasSpareData => Format == NandPhysicalFormat.InterleavedEcc;
    }

    private readonly record struct NandPhysicalProbe(
        long CompletePhysicalPageCount,
        int ValidEccPageCount,
        int Layout0Matches,
        int Layout1Matches,
        int Layout2Matches)
    {
        internal ImmutableArray<NandPhysicalLayout> CandidateLayouts
        {
            get
            {
                if (ValidEccPageCount == 0)
                {
                    return ImmutableArray<NandPhysicalLayout>.Empty;
                }

                // Filesystem pages and remapped reserved blocks can carry non-template spare metadata.
                // Vote with positively identified page samples rather than requiring every ECC-valid
                // page to match a synthetic AddEcc spare template. Keep tied strongest candidates
                // explicit so auto mode cannot silently select a layout.
                int highestMatchCount = Math.Max(Layout0Matches, Math.Max(Layout1Matches, Layout2Matches));
                if (highestMatchCount == 0)
                {
                    return ImmutableArray<NandPhysicalLayout>.Empty;
                }

                bool hasLayout0 = Layout0Matches == highestMatchCount;
                bool hasLayout1 = Layout1Matches == highestMatchCount;
                bool hasLayout2 = Layout2Matches == highestMatchCount;
                int candidateCount = (hasLayout0 ? 1 : 0) + (hasLayout1 ? 1 : 0) + (hasLayout2 ? 1 : 0);
                return candidateCount switch
                {
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
        }

        internal bool HasLayoutEvidence => !CandidateLayouts.IsDefaultOrEmpty;
    }
}

/// <summary>
/// A seekable view over a caller stream or a private bounded-copy temporary spool.
/// </summary>
internal sealed class CanonicalBackingStore : IDisposable
{
    private const int SpoolBufferSize = 0x10000;

    private readonly Stream source;
    private readonly long sourceOffset;
    private readonly bool ownsSource;
    private readonly string? temporaryPath;
    private readonly long? restorePosition;
    private bool disposed;

    private CanonicalBackingStore(
        Stream source,
        long sourceOffset,
        long length,
        bool ownsSource,
        string? temporaryPath,
        long? restorePosition)
    {
        this.source = source;
        this.sourceOffset = sourceOffset;
        Length = length;
        this.ownsSource = ownsSource;
        this.temporaryPath = temporaryPath;
        this.restorePosition = restorePosition;
    }

    internal long Length { get; }

    internal static async Task<CanonicalBackingStore> CreateAsync(Stream source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.CanSeek)
        {
            long position = source.Position;
            long length = source.Length;
            if (position < 0 || length < position)
            {
                throw new IOException("The NAND source stream has an invalid seekable position or length.");
            }

            return new CanonicalBackingStore(
                source: source,
                sourceOffset: position,
                length: length - position,
                ownsSource: false,
                temporaryPath: null,
                restorePosition: position);
        }

        string temporaryPath = Path.Combine(Path.GetTempPath(), $"jrunner-canonical-nand-{Guid.NewGuid():N}.tmp");
        FileStream? spool = null;
        try
        {
            spool = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                SpoolBufferSize,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(SpoolBufferSize);
            try
            {
                long length = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int read = await source.ReadAsync(buffer.AsMemory(0, SpoolBufferSize), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await spool.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    length = checked(length + read);
                }

                await spool.FlushAsync(cancellationToken).ConfigureAwait(false);
                spool.Position = 0;
                return new CanonicalBackingStore(
                    source: spool,
                    sourceOffset: 0,
                    length: length,
                    ownsSource: true,
                    temporaryPath: temporaryPath,
                    restorePosition: null);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch
        {
            try
            {
                spool?.Dispose();
            }
            finally
            {
                File.Delete(temporaryPath);
            }

            throw;
        }
    }

    internal async ValueTask ReadExactlyAtAsync(
        long relativeOffset,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (relativeOffset < 0 || relativeOffset > Length || destination.Length > Length - relativeOffset)
        {
            throw new OperationFailureException(
                ExitCode.InvalidData,
                "truncated-nand",
                "A NAND range extends beyond the available source bytes.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        source.Position = checked(sourceOffset + relativeOffset);
        int copied = 0;
        while (copied < destination.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = await source.ReadAsync(destination.Slice(copied), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new OperationFailureException(
                    ExitCode.InvalidData,
                    "truncated-nand",
                    "A NAND source ended before its declared length.");
            }

            copied += read;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (ownsSource)
        {
            try
            {
                source.Dispose();
            }
            finally
            {
                if (temporaryPath is not null)
                {
                    File.Delete(temporaryPath);
                }
            }

            return;
        }

        if (restorePosition is { } position)
        {
            try
            {
                source.Position = position;
            }
            catch (IOException)
            {
                // The source remains caller-owned; position restoration is best effort only.
            }
            catch (NotSupportedException)
            {
                // A stream may change capabilities after preparation; never dispose caller-owned input.
            }
            catch (ObjectDisposedException)
            {
                // Concurrent caller disposal is outside this operation's ownership.
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}
