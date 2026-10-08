using JRunner.Core.Nand.Physical;
using Xunit;

namespace JRunner.Core.Tests.Nand.Physical;

public sealed class NandBadBlockServiceTests
{
    [Fact]
    public void Detect_bad_block_honors_small_and_large_block_marker_rules()
    {
        var smallBlock = CreateErasedBlock(NandPhysicalLayout.Layout0);
        var smallMarkerPage = 7;
        smallBlock[(smallMarkerPage * NandPhysicalGeometry.PhysicalPageSize) + NandPhysicalLayout.Layout0.MarkerOffset] = 0x00;

        var smallBadBlock = Assert.IsType<NandBadBlock>(
            NandBadBlockService.DetectBadBlock(smallBlock, NandPhysicalLayout.Layout0, physicalBlockNumber: 0x21));
        Assert.Equal(0x21L, smallBadBlock.PhysicalBlock);
        Assert.Equal(smallMarkerPage, smallBadBlock.MarkerPageIndex);
        Assert.Equal(NandBadBlockReason.MarkerByte, smallBadBlock.Reason);

        var largeBlock = CreateErasedBlock(NandPhysicalLayout.Layout2);
        largeBlock[NandPhysicalGeometry.PhysicalPageSize + NandPhysicalLayout.Layout2.MarkerOffset] = 0x00;
        Assert.Null(NandBadBlockService.DetectBadBlock(largeBlock, NandPhysicalLayout.Layout2, physicalBlockNumber: 0x22));

        largeBlock[NandPhysicalLayout.Layout2.MarkerOffset] = 0x00;
        var largeBadBlock = Assert.IsType<NandBadBlock>(
            NandBadBlockService.DetectBadBlock(largeBlock, NandPhysicalLayout.Layout2, physicalBlockNumber: 0x22));
        Assert.Equal(0, largeBadBlock.MarkerPageIndex);
        Assert.Equal(NandBadBlockReason.MarkerByte, largeBadBlock.Reason);
    }

    [Fact]
    public void Detect_bad_block_recognizes_zero_spare_areas_on_large_blocks()
    {
        var block = CreateErasedBlock(NandPhysicalLayout.Layout2);
        var zeroSparePage = 42;
        block.AsSpan(
            (zeroSparePage * NandPhysicalGeometry.PhysicalPageSize) + NandPhysicalGeometry.LogicalPageSize,
            NandPhysicalGeometry.SpareSize).Clear();

        var badBlock = Assert.IsType<NandBadBlock>(
            NandBadBlockService.DetectBadBlock(block, NandPhysicalLayout.Layout2, physicalBlockNumber: 0x42));

        Assert.Equal(zeroSparePage, badBlock.MarkerPageIndex);
        Assert.Equal(NandBadBlockReason.ZeroSpareArea, badBlock.Reason);
    }

    [Fact]
    public void Discover_and_project_remaps_handles_block_id_boundary_without_mutating_source()
    {
        var layout = NandPhysicalLayout.Layout0;
        const long firstPhysicalBlock = 0x100;
        const long firstReservedPhysicalBlock = 0x101;
        var blockSize = layout.Geometry.PhysicalBlockSize;
        var image = new byte[checked(blockSize * 4)];

        var badBlock = CreatePhysicalBlock(layout, physicalBlock: 0x100, fill: 0x11);
        badBlock[layout.MarkerOffset] = 0x00;
        var ordinaryFirstReservedBlock = CreatePhysicalBlock(layout, physicalBlock: 0x101, fill: 0x22);
        var ordinarySecondReservedBlock = CreatePhysicalBlock(layout, physicalBlock: 0x102, fill: 0x33);
        var replacementBlock = CreatePhysicalBlock(layout, physicalBlock: 0x100, fill: 0x5A);

        badBlock.CopyTo(image, 0 * blockSize);
        ordinaryFirstReservedBlock.CopyTo(image, 1 * blockSize);
        ordinarySecondReservedBlock.CopyTo(image, 2 * blockSize);
        replacementBlock.CopyTo(image, 3 * blockSize);
        var original = image.ToArray();

        var scan = NandBadBlockService.Scan(image, layout, firstPhysicalBlock);
        Assert.Single(scan.BadBlocks);
        Assert.Equal(0x100L, scan.BadBlocks[0].PhysicalBlock);

        var discovery = NandBadBlockService.DiscoverRemaps(
            image.AsSpan(blockSize, blockSize * 3),
            firstReservedPhysicalBlock,
            layout,
            scan.BadBlocks);

        var remap = Assert.Single(discovery.Mappings);
        Assert.True(remap.ReplacementPhysicalBlock.HasValue);
        Assert.Equal(0x103L, remap.ReplacementPhysicalBlock.Value);
        Assert.True(remap.IsMapped);

        var projected = NandBadBlockService.ProjectRemaps(image, layout, discovery.Mappings, firstPhysicalBlock).ToArray();

        Assert.Equal(original, image);
        Assert.Equal(replacementBlock, projected.AsSpan(0, blockSize).ToArray());
        Assert.All(projected.AsSpan(3 * blockSize, blockSize).ToArray(), value => Assert.Equal(byte.MaxValue, value));
    }

    [Fact]
    public void Discover_remaps_excludes_the_first_reserved_block()
    {
        var layout = NandPhysicalLayout.Layout0;
        var blockSize = layout.Geometry.PhysicalBlockSize;
        var reservedArea = new byte[checked(blockSize * 2)];
        var targetBadBlock = new NandBadBlock(
            physicalBlock: 0x100,
            markerPageIndex: 0,
            reason: NandBadBlockReason.MarkerByte);
        var excludedReplacement = CreatePhysicalBlock(layout, physicalBlock: 0x100, fill: 0xA5);
        excludedReplacement.CopyTo(reservedArea, 0);
        Array.Fill(reservedArea, byte.MaxValue, blockSize, blockSize);

        var discovery = NandBadBlockService.DiscoverRemaps(
            reservedArea,
            firstReservedPhysicalBlock: 0x101,
            layout: layout,
            badBlocks: new[] { targetBadBlock });

        var remap = Assert.Single(discovery.Mappings);
        Assert.Null(remap.ReplacementPhysicalBlock);
    }

    [Fact]
    public void Discover_remaps_rejects_a_bad_reserved_candidate()
    {
        var layout = NandPhysicalLayout.Layout0;
        var blockSize = layout.Geometry.PhysicalBlockSize;
        var reservedArea = new byte[checked(blockSize * 2)];
        var targetBadBlock = new NandBadBlock(
            physicalBlock: 0x100,
            markerPageIndex: 0,
            reason: NandBadBlockReason.MarkerByte);
        var badReplacement = CreatePhysicalBlock(layout, physicalBlock: 0x100, fill: 0xA5);
        badReplacement[NandPhysicalGeometry.PhysicalPageSize + layout.MarkerOffset] = 0x00;
        badReplacement.CopyTo(reservedArea, blockSize);

        var discovery = NandBadBlockService.DiscoverRemaps(
            reservedArea,
            firstReservedPhysicalBlock: 0x101,
            layout: layout,
            badBlocks: new[] { targetBadBlock });

        var remap = Assert.Single(discovery.Mappings);
        Assert.Null(remap.ReplacementPhysicalBlock);
    }

    [Fact]
    public void Project_remaps_honors_a_pre_cancelled_token()
    {
        var image = CreatePhysicalBlock(NandPhysicalLayout.Layout0, physicalBlock: 0, fill: 0xFF);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => NandBadBlockService.ProjectRemaps(
            image,
            NandPhysicalLayout.Layout0,
            Array.Empty<NandBadBlockRemap>(),
            cancellationToken: cancellation.Token));
    }

    private static byte[] CreateErasedBlock(NandPhysicalLayout layout)
    {
        var block = new byte[layout.Geometry.PhysicalBlockSize];
        Array.Fill(block, byte.MaxValue);
        return block;
    }

    private static byte[] CreatePhysicalBlock(NandPhysicalLayout layout, long physicalBlock, byte fill)
    {
        var logical = new byte[layout.Geometry.LogicalBlockSize];
        Array.Fill(logical, fill);
        return NandEccCodec.AddEcc(logical, layout, physicalBlock).ToArray();
    }
}
