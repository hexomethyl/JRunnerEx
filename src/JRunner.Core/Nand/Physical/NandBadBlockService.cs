using System.Collections.Immutable;
using JRunner.Core.Contracts;

namespace JRunner.Core.Nand.Physical;

/// <summary>
/// Identifies why a physical NAND block is marked bad.
/// </summary>
public enum NandBadBlockReason
{
    /// <summary>
    /// A bad-block marker byte was not erased.
    /// </summary>
    MarkerByte,

    /// <summary>
    /// A page's complete spare area was zero-filled.
    /// </summary>
    ZeroSpareArea,
}

/// <summary>
/// Immutable evidence that a physical NAND block is bad.
/// </summary>
public sealed record NandBadBlock
{
    /// <summary>
    /// Creates a bad-block record.
    /// </summary>
    public NandBadBlock(long physicalBlock, int markerPageIndex, NandBadBlockReason reason)
    {
        if (physicalBlock < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(physicalBlock));
        }

        if (markerPageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(markerPageIndex));
        }

        if (!Enum.IsDefined<NandBadBlockReason>(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason), reason, "The bad-block reason is not supported.");
        }

        PhysicalBlock = physicalBlock;
        MarkerPageIndex = markerPageIndex;
        Reason = reason;
    }

    /// <summary>
    /// Gets the physical block index.
    /// </summary>
    public long PhysicalBlock { get; }

    /// <summary>
    /// Gets the page index within the physical block that carried the bad-block evidence.
    /// </summary>
    public int MarkerPageIndex { get; }

    /// <summary>
    /// Gets the evidence type.
    /// </summary>
    public NandBadBlockReason Reason { get; }
}

/// <summary>
/// Associates one bad physical block with its reserved replacement block, if a replacement was discovered.
/// </summary>
public sealed record NandBadBlockRemap
{
    /// <summary>
    /// Creates a bad-block remap record.
    /// </summary>
    public NandBadBlockRemap(NandBadBlock badBlock, long? replacementPhysicalBlock)
    {
        ArgumentNullException.ThrowIfNull(badBlock);
        if (replacementPhysicalBlock is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(replacementPhysicalBlock));
        }

        if (replacementPhysicalBlock == badBlock.PhysicalBlock)
        {
            throw new ArgumentException("A replacement block cannot be the same as its bad block.", nameof(replacementPhysicalBlock));
        }

        BadBlock = badBlock;
        ReplacementPhysicalBlock = replacementPhysicalBlock;
    }

    /// <summary>
    /// Gets the bad physical block and its marker evidence.
    /// </summary>
    public NandBadBlock BadBlock { get; }

    /// <summary>
    /// Gets the absolute replacement physical block, or <see langword="null"/> when no matching replacement was found.
    /// </summary>
    public long? ReplacementPhysicalBlock { get; }

    /// <summary>
    /// Gets whether a replacement was discovered.
    /// </summary>
    public bool IsMapped => ReplacementPhysicalBlock.HasValue;
}

/// <summary>
/// Immutable result of scanning physical NAND blocks for bad-block markers.
/// </summary>
public sealed record NandBadBlockScanResult
{
    internal NandBadBlockScanResult(ImmutableArray<NandBadBlock> badBlocks)
    {
        BadBlocks = badBlocks;
    }

    /// <summary>
    /// Gets bad physical blocks in ascending physical order.
    /// </summary>
    public ImmutableArray<NandBadBlock> BadBlocks { get; }
}

/// <summary>
/// Immutable result of looking up bad-block replacements in a reserved NAND area.
/// </summary>
public sealed record NandRemapDiscoveryResult
{
    internal NandRemapDiscoveryResult(ImmutableArray<NandBadBlockRemap> mappings)
    {
        Mappings = mappings;

        var mappedCount = 0;
        foreach (var mapping in mappings)
        {
            if (mapping.IsMapped)
            {
                mappedCount++;
            }
        }

        MappedCount = mappedCount;
    }

    /// <summary>
    /// Gets one mapping record for every supplied bad physical block, preserving its input order.
    /// </summary>
    public ImmutableArray<NandBadBlockRemap> Mappings { get; }

    /// <summary>
    /// Gets the count of bad blocks for which a replacement was found.
    /// </summary>
    public int MappedCount { get; }
}

/// <summary>
/// Finds legacy bad-block markers, discovers replacement blocks, and projects replacements without mutating input bytes.
/// </summary>
public static class NandBadBlockService
{
    /// <summary>
    /// Checks exactly one complete physical block for legacy bad-block evidence.
    /// </summary>
    /// <param name="physicalBlock">Exactly one block using <paramref name="layout"/>'s geometry.</param>
    /// <param name="layout">The spare-area layout that defines the marker location.</param>
    /// <param name="physicalBlockNumber">The absolute physical block number for the result.</param>
    /// <returns>A bad-block record, or <see langword="null"/> when no marker is present.</returns>
    public static NandBadBlock? DetectBadBlock(
        ReadOnlySpan<byte> physicalBlock,
        NandPhysicalLayout layout,
        long physicalBlockNumber)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ValidatePhysicalBlockNumber(physicalBlockNumber, nameof(physicalBlockNumber));
        ValidatePhysicalBlockLength(physicalBlock.Length, layout, nameof(physicalBlock));

        return DetectBadBlockCore(physicalBlock, layout, physicalBlockNumber);
    }

    private static NandBadBlock? DetectBadBlockCore(
        ReadOnlySpan<byte> physicalBlock,
        NandPhysicalLayout layout,
        long physicalBlockNumber)
    {
        for (var pageIndex = 0; pageIndex < layout.Geometry.PagesPerBlock; pageIndex++)
        {
            var page = physicalBlock.Slice(
                pageIndex * NandPhysicalGeometry.PhysicalPageSize,
                NandPhysicalGeometry.PhysicalPageSize);
            var spare = page.Slice(NandPhysicalGeometry.LogicalPageSize, NandPhysicalGeometry.SpareSize);
            if (IsAll(spare, 0x00))
            {
                return new NandBadBlock(physicalBlockNumber, pageIndex, NandBadBlockReason.ZeroSpareArea);
            }

            if ((!layout.Geometry.IsLargeBlock || pageIndex == 0) && page[layout.MarkerOffset] != byte.MaxValue)
            {
                return new NandBadBlock(physicalBlockNumber, pageIndex, NandBadBlockReason.MarkerByte);
            }
        }

        return null;
    }

    /// <summary>
    /// Finds bad physical blocks in an image containing complete physical blocks.
    /// </summary>
    /// <param name="physicalImage">Physical bytes aligned to complete erase blocks.</param>
    /// <param name="layout">The spare-area layout that defines block geometry and marker location.</param>
    /// <param name="firstPhysicalBlock">Absolute block number represented by the image's first block.</param>
    /// <param name="progress">Optional progress receiver.</param>
    /// <param name="cancellationToken">Token checked before every block.</param>
    /// <exception cref="OperationFailureException">Thrown when the image ends in a partial physical block.</exception>
    public static NandBadBlockScanResult Scan(
        ReadOnlySpan<byte> physicalImage,
        NandPhysicalLayout layout,
        long firstPhysicalBlock = 0,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ValidatePhysicalBlockNumber(firstPhysicalBlock, nameof(firstPhysicalBlock));
        var blockCount = GetPhysicalBlockCount(physicalImage.Length, layout, "truncated-physical-block");

        cancellationToken.ThrowIfCancellationRequested();
        Report(progress, "scanning-bad-blocks", "Scanning physical NAND blocks for bad-block markers.", completed: 0, blockCount);

        ImmutableArray<NandBadBlock>.Builder? badBlocks = null;
        for (var blockIndex = 0; blockIndex < blockCount; blockIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var physicalBlock = physicalImage.Slice(
                blockIndex * layout.Geometry.PhysicalBlockSize,
                layout.Geometry.PhysicalBlockSize);
            var physicalBlockNumber = checked(firstPhysicalBlock + blockIndex);
            var badBlock = DetectBadBlockCore(physicalBlock, layout, physicalBlockNumber);
            if (badBlock is not null)
            {
                (badBlocks ??= ImmutableArray.CreateBuilder<NandBadBlock>()).Add(badBlock);
            }

            var completed = blockIndex + 1;
            if (completed == blockCount || completed % 0x20 == 0)
            {
                Report(progress, "scanning-bad-blocks", "Scanning physical NAND blocks for bad-block markers.", completed, blockCount);
            }
        }

        return new NandBadBlockScanResult(
            badBlocks is null ? ImmutableArray<NandBadBlock>.Empty : badBlocks.ToImmutable());
    }

    /// <summary>
    /// Finds reserved replacement blocks for supplied bad physical blocks.
    /// </summary>
    /// <remarks>
    /// Candidates are considered in descending reserved-area order and the first reserved block is intentionally
    /// excluded, matching the legacy 32-block reserved-area convention.
    /// </remarks>
    /// <param name="reservedArea">Complete physical blocks from a reserved replacement area.</param>
    /// <param name="firstReservedPhysicalBlock">Absolute physical block number represented by the first reserved block.</param>
    /// <param name="layout">The spare-area layout that defines block ID and marker offsets.</param>
    /// <param name="badBlocks">Bad-block records to resolve.</param>
    /// <param name="progress">Optional progress receiver.</param>
    /// <param name="cancellationToken">Token checked before every reserved candidate.</param>
    /// <exception cref="OperationFailureException">Thrown when the reserved area ends in a partial physical block.</exception>
    public static NandRemapDiscoveryResult DiscoverRemaps(
        ReadOnlySpan<byte> reservedArea,
        long firstReservedPhysicalBlock,
        NandPhysicalLayout layout,
        IReadOnlyList<NandBadBlock> badBlocks,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(badBlocks);
        ValidatePhysicalBlockNumber(firstReservedPhysicalBlock, nameof(firstReservedPhysicalBlock));
        var reservedBlockCount = GetPhysicalBlockCount(reservedArea.Length, layout, "truncated-reserved-area");
        ValidateDistinctBadBlocks(badBlocks);

        cancellationToken.ThrowIfCancellationRequested();
        if (badBlocks.Count == 0)
        {
            Report(progress, "discovering-bad-block-remaps", "Discovering reserved NAND replacement blocks.", completed: 0, total: 0);
            return new NandRemapDiscoveryResult(ImmutableArray<NandBadBlockRemap>.Empty);
        }

        var candidateCount = Math.Max(0, reservedBlockCount - 1);
        Report(progress, "discovering-bad-block-remaps", "Discovering reserved NAND replacement blocks.", completed: 0, candidateCount);

        var replacements = new long?[badBlocks.Count];
        var completedCandidates = 0;
        for (var candidateIndex = reservedBlockCount - 1; candidateIndex > 0; candidateIndex--)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var candidateOffset = checked(candidateIndex * layout.Geometry.PhysicalBlockSize);
            var candidate = reservedArea.Slice(candidateOffset, layout.Geometry.PhysicalBlockSize);
            var replacementPhysicalBlock = checked(firstReservedPhysicalBlock + candidateIndex);
            if (IsReplacementCandidate(candidate, layout, replacementPhysicalBlock))
            {
                for (var badBlockIndex = 0; badBlockIndex < badBlocks.Count; badBlockIndex++)
                {
                    if (replacements[badBlockIndex].HasValue ||
                        replacementPhysicalBlock == badBlocks[badBlockIndex].PhysicalBlock ||
                        !CandidateMatchesBadBlock(candidate, layout, badBlocks[badBlockIndex].PhysicalBlock))
                    {
                        continue;
                    }

                    replacements[badBlockIndex] = replacementPhysicalBlock;
                    break;
                }
            }

            completedCandidates++;
            if (completedCandidates == candidateCount || completedCandidates % 0x20 == 0)
            {
                Report(
                    progress,
                    "discovering-bad-block-remaps",
                    "Discovering reserved NAND replacement blocks.",
                    completedCandidates,
                    candidateCount);
            }
        }

        var mappings = ImmutableArray.CreateBuilder<NandBadBlockRemap>(badBlocks.Count);
        for (var index = 0; index < badBlocks.Count; index++)
        {
            mappings.Add(new NandBadBlockRemap(badBlocks[index], replacements[index]));
        }

        return new NandRemapDiscoveryResult(mappings.MoveToImmutable());
    }

    /// <summary>
    /// Projects discovered replacements into a new physical image and erases consumed replacement blocks in the projection.
    /// </summary>
    /// <remarks>
    /// The source span is never mutated. Each mapped replacement is copied over its bad physical block and its original
    /// location is filled with <c>0xFF</c>, yielding a canonical legacy-style projection.
    /// </remarks>
    public static ReadOnlyMemory<byte> ProjectRemaps(
        ReadOnlySpan<byte> physicalImage,
        NandPhysicalLayout layout,
        IReadOnlyList<NandBadBlockRemap> remaps,
        long firstPhysicalBlock = 0,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(remaps);
        ValidatePhysicalBlockNumber(firstPhysicalBlock, nameof(firstPhysicalBlock));
        var blockCount = GetPhysicalBlockCount(physicalImage.Length, layout, "truncated-physical-block");
        ValidateProjectionRemaps(remaps, firstPhysicalBlock, blockCount);

        cancellationToken.ThrowIfCancellationRequested();
        var projected = new byte[physicalImage.Length];
        ProjectRemapsCore(
            physicalImage,
            projected,
            layout,
            remaps,
            firstPhysicalBlock,
            blockCount,
            progress,
            cancellationToken);
        return projected;
    }

    /// <summary>
    /// Projects discovered replacements into a caller-provided physical-image destination.
    /// </summary>
    /// <remarks>
    /// Source and destination spans must not overlap. The destination receives a canonical projection and the source is
    /// never mutated.
    /// </remarks>
    public static void ProjectRemaps(
        ReadOnlySpan<byte> physicalImage,
        Span<byte> projectionDestination,
        NandPhysicalLayout layout,
        IReadOnlyList<NandBadBlockRemap> remaps,
        long firstPhysicalBlock = 0,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(remaps);
        ValidatePhysicalBlockNumber(firstPhysicalBlock, nameof(firstPhysicalBlock));
        var blockCount = GetPhysicalBlockCount(physicalImage.Length, layout, "truncated-physical-block");
        if (projectionDestination.Length != physicalImage.Length)
        {
            throw new ArgumentException("The projection destination must have the same length as the physical image.", nameof(projectionDestination));
        }

        if (physicalImage.Overlaps(projectionDestination))
        {
            throw new ArgumentException("Physical source and projection destination spans must not overlap.", nameof(projectionDestination));
        }

        ValidateProjectionRemaps(remaps, firstPhysicalBlock, blockCount);

        cancellationToken.ThrowIfCancellationRequested();
        ProjectRemapsCore(
            physicalImage,
            projectionDestination,
            layout,
            remaps,
            firstPhysicalBlock,
            blockCount,
            progress,
            cancellationToken);
    }

    private static void ProjectRemapsCore(
        ReadOnlySpan<byte> physicalImage,
        Span<byte> projectionDestination,
        NandPhysicalLayout layout,
        IReadOnlyList<NandBadBlockRemap> remaps,
        long firstPhysicalBlock,
        int blockCount,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var totalWork = checked(blockCount + remaps.Count);
        Report(progress, "projecting-bad-block-remaps", "Projecting NAND bad-block replacements.", completed: 0, totalWork);

        for (var blockIndex = 0; blockIndex < blockCount; blockIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var blockOffset = blockIndex * layout.Geometry.PhysicalBlockSize;
            physicalImage.Slice(blockOffset, layout.Geometry.PhysicalBlockSize).CopyTo(
                projectionDestination.Slice(blockOffset, layout.Geometry.PhysicalBlockSize));

            var completed = blockIndex + 1;
            if (completed == totalWork || completed % 0x20 == 0)
            {
                Report(progress, "projecting-bad-block-remaps", "Projecting NAND bad-block replacements.", completed, totalWork);
            }
        }

        for (var remapIndex = 0; remapIndex < remaps.Count; remapIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remap = remaps[remapIndex];
            if (remap.ReplacementPhysicalBlock is { } replacementPhysicalBlock)
            {
                var replacementOffset = GetBlockOffset(replacementPhysicalBlock, firstPhysicalBlock, layout.Geometry.PhysicalBlockSize);
                var badOffset = GetBlockOffset(remap.BadBlock.PhysicalBlock, firstPhysicalBlock, layout.Geometry.PhysicalBlockSize);
                projectionDestination.Slice(replacementOffset, layout.Geometry.PhysicalBlockSize).CopyTo(
                    projectionDestination.Slice(badOffset, layout.Geometry.PhysicalBlockSize));
                projectionDestination.Slice(replacementOffset, layout.Geometry.PhysicalBlockSize).Fill(byte.MaxValue);
            }

            var completed = checked(blockCount + remapIndex + 1);
            if (completed == totalWork || completed % 0x20 == 0)
            {
                Report(progress, "projecting-bad-block-remaps", "Projecting NAND bad-block replacements.", completed, totalWork);
            }
        }
    }

    private static bool IsReplacementCandidate(
        ReadOnlySpan<byte> candidate,
        NandPhysicalLayout layout,
        long replacementPhysicalBlock)
    {
        var firstPage = candidate.Slice(0, NandPhysicalGeometry.PhysicalPageSize);
        var lastPage = candidate.Slice(candidate.Length - NandPhysicalGeometry.PhysicalPageSize, NandPhysicalGeometry.PhysicalPageSize);

        if (DetectBadBlockCore(candidate, layout, replacementPhysicalBlock) is not null)
        {
            return false;
        }

        var firstSpare = firstPage.Slice(NandPhysicalGeometry.LogicalPageSize, NandPhysicalGeometry.SpareSize);
        var lastSpare = lastPage.Slice(NandPhysicalGeometry.LogicalPageSize, NandPhysicalGeometry.SpareSize);

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

        var firstPage = candidate.Slice(0, NandPhysicalGeometry.PhysicalPageSize);
        var lastPage = candidate.Slice(candidate.Length - NandPhysicalGeometry.PhysicalPageSize, NandPhysicalGeometry.PhysicalPageSize);
        var expectedBlockId = unchecked((ushort)badPhysicalBlock);
        return HasBlockId(firstPage, layout, expectedBlockId) || HasBlockId(lastPage, layout, expectedBlockId);
    }

    private static bool HasBlockId(ReadOnlySpan<byte> physicalPage, NandPhysicalLayout layout, ushort expectedBlockId) =>
        physicalPage[layout.BlockIdOffset] == (byte)expectedBlockId &&
        physicalPage[layout.BlockIdOffset + 1] == (byte)(expectedBlockId >> 8);

    private static void ValidateDistinctBadBlocks(IReadOnlyList<NandBadBlock> badBlocks)
    {
        if (badBlocks.Count == 0)
        {
            return;
        }

        var firstBadBlock = badBlocks[0] ?? throw new ArgumentException("Bad-block entries cannot be null.", nameof(badBlocks));
        if (badBlocks.Count == 1)
        {
            return;
        }

        var physicalBlocks = new HashSet<long>(badBlocks.Count)
        {
            firstBadBlock.PhysicalBlock,
        };
        for (var index = 1; index < badBlocks.Count; index++)
        {
            var badBlock = badBlocks[index] ?? throw new ArgumentException("Bad-block entries cannot be null.", nameof(badBlocks));
            if (!physicalBlocks.Add(badBlock.PhysicalBlock))
            {
                throw new ArgumentException("Bad-block entries must have unique physical block numbers.", nameof(badBlocks));
            }
        }
    }

    private static void ValidateProjectionRemaps(
        IReadOnlyList<NandBadBlockRemap> remaps,
        long firstPhysicalBlock,
        int blockCount)
    {
        if (remaps.Count == 0)
        {
            return;
        }

        var endExclusive = checked(firstPhysicalBlock + blockCount);
        var badPhysicalBlocks = new HashSet<long>(remaps.Count);
        var replacementPhysicalBlocks = new HashSet<long>(remaps.Count);

        for (var index = 0; index < remaps.Count; index++)
        {
            var remap = remaps[index] ?? throw new ArgumentException("Remap entries cannot be null.", nameof(remaps));
            if (!badPhysicalBlocks.Add(remap.BadBlock.PhysicalBlock))
            {
                throw InvalidData("invalid-remap", "Remap entries must have unique bad physical blocks.");
            }
        }

        for (var index = 0; index < remaps.Count; index++)
        {
            var remap = remaps[index] ?? throw new ArgumentException("Remap entries cannot be null.", nameof(remaps));
            if (remap.ReplacementPhysicalBlock is not { } replacementPhysicalBlock)
            {
                continue;
            }

            if (remap.BadBlock.PhysicalBlock < firstPhysicalBlock || remap.BadBlock.PhysicalBlock >= endExclusive ||
                replacementPhysicalBlock < firstPhysicalBlock || replacementPhysicalBlock >= endExclusive)
            {
                throw InvalidData("remap-outside-image", "A bad-block remap refers to a physical block outside the projection image.");
            }

            if (badPhysicalBlocks.Contains(replacementPhysicalBlock))
            {
                throw InvalidData("invalid-remap", "A replacement physical block cannot also be a bad physical block.");
            }

            if (!replacementPhysicalBlocks.Add(replacementPhysicalBlock))
            {
                throw InvalidData("invalid-remap", "Remap entries must have unique replacement physical blocks.");
            }
        }
    }

    private static int GetPhysicalBlockCount(int byteLength, NandPhysicalLayout layout, string failureKind)
    {
        if (byteLength % layout.Geometry.PhysicalBlockSize != 0)
        {
            throw InvalidData(
                failureKind,
                $"Physical NAND data must contain complete 0x{layout.Geometry.PhysicalBlockSize:X}-byte blocks for this layout.");
        }

        return byteLength / layout.Geometry.PhysicalBlockSize;
    }

    private static void ValidatePhysicalBlockLength(int byteLength, NandPhysicalLayout layout, string parameterName)
    {
        if (byteLength != layout.Geometry.PhysicalBlockSize)
        {
            throw new ArgumentException(
                $"A physical NAND block must be exactly 0x{layout.Geometry.PhysicalBlockSize:X} bytes for this layout.",
                parameterName);
        }
    }

    private static void ValidatePhysicalBlockNumber(long physicalBlockNumber, string parameterName)
    {
        if (physicalBlockNumber < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "The physical block number cannot be negative.");
        }
    }

    private static int GetBlockOffset(long physicalBlock, long firstPhysicalBlock, int physicalBlockSize) =>
        checked((int)checked((physicalBlock - firstPhysicalBlock) * physicalBlockSize));

    private static bool IsAll(ReadOnlySpan<byte> bytes, byte value)
    {
        foreach (var current in bytes)
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
        int completed,
        int total)
    {
        progress?.Report(new OperationProgress(kind, message, completed: completed, total: total));
    }
}
