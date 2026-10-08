using JRunner.Core.Contracts;
using JRunner.Core.Nand.Physical;
using Xunit;

namespace JRunner.Core.Tests.Nand.Physical;

public sealed class NandEccCodecTests
{
    [Fact]
    public void Add_and_remove_ecc_round_trip_every_legacy_layout()
    {
        foreach (var layout in Layouts)
        {
            var logical = CreatePattern(checked(layout.Geometry.LogicalBlockSize * 2));

            var physical = NandEccCodec.AddEcc(logical, layout, firstPhysicalBlock: 0x123);
            var restored = NandEccCodec.RemoveEcc(physical.Span);

            Assert.Equal(checked((logical.Length / NandPhysicalGeometry.LogicalPageSize) * NandPhysicalGeometry.PhysicalPageSize), physical.Length);
            Assert.Equal(logical, restored.ToArray());
            Assert.True(NandEccCodec.HasValidEcc(physical.Span.Slice(0, NandPhysicalGeometry.PhysicalPageSize)));
        }
    }

    [Fact]
    public void Add_ecc_places_legacy_markers_and_block_ids_at_layout_offsets()
    {
        AssertMarkerAndBlockId(NandPhysicalLayout.Layout0, expectedMarkerOffset: 0x205, expectedIdOffset: 0x200);
        AssertMarkerAndBlockId(NandPhysicalLayout.Layout1, expectedMarkerOffset: 0x205, expectedIdOffset: 0x201);
        AssertMarkerAndBlockId(NandPhysicalLayout.Layout2, expectedMarkerOffset: 0x200, expectedIdOffset: 0x201);
    }

    [Fact]
    public void Add_ecc_matches_legacy_known_page_output()
    {
        var zeroPhysical = NandEccCodec.AddEcc(new byte[NandPhysicalGeometry.LogicalPageSize], NandPhysicalLayout.Layout0);
        var sequentialLogical = new byte[NandPhysicalGeometry.LogicalPageSize];
        for (var index = 0; index < sequentialLogical.Length; index++)
        {
            sequentialLogical[index] = (byte)index;
        }

        var sequentialPhysical = NandEccCodec.AddEcc(sequentialLogical, NandPhysicalLayout.Layout0);

        Assert.Equal(byte.MaxValue, zeroPhysical.Span[0x205]);
        Assert.Equal("C0B88822", Convert.ToHexString(zeroPhysical.Span.Slice(0x20C, sizeof(uint))));
        Assert.Equal("805C2AEE", Convert.ToHexString(sequentialPhysical.Span.Slice(0x20C, sizeof(uint))));
        Assert.True(NandEccCodec.HasValidEcc(zeroPhysical.Span));
        Assert.True(NandEccCodec.HasValidEcc(sequentialPhysical.Span));
    }

    [Theory]
    [InlineData(0x2C, "6C87770C")]
    [InlineData(0x30, "70467499")]
    public void Calculate_ecc_preserves_and_includes_low_six_block_type_bits(int blockType, string expectedEcc)
    {
        var physical = NandEccCodec.AddEcc(
            new byte[NandPhysicalGeometry.LogicalPageSize],
            NandPhysicalLayout.Layout0).ToArray();
        physical[0x20C] = (byte)((physical[0x20C] & 0xC0) | blockType);

        Assert.False(NandEccCodec.HasValidEcc(physical));

        NandEccCodec.CalculateEcc(physical);

        Assert.Equal(expectedEcc, Convert.ToHexString(physical.AsSpan(0x20C, sizeof(uint))));
        Assert.Equal((byte)blockType, (byte)(physical[0x20C] & 0x3F));
        Assert.True(NandEccCodec.HasValidEcc(physical));
    }

    [Fact]
    public void Ecc_operations_reject_partial_input_and_format_detection_reports_truncation()
    {
        var physical = NandEccCodec.AddEcc(new byte[NandPhysicalGeometry.LogicalPageSize], NandPhysicalLayout.Layout0);
        var truncatedPhysical = physical.Slice(0, physical.Length - 1);

        var removeFailure = Assert.Throws<OperationFailureException>(() => NandEccCodec.RemoveEcc(truncatedPhysical.Span));
        Assert.Equal("truncated-physical-nand", removeFailure.Kind);

        var addFailure = Assert.Throws<OperationFailureException>(
            () => NandEccCodec.AddEcc(new byte[NandPhysicalGeometry.LogicalPageSize - 1], NandPhysicalLayout.Layout0));
        Assert.Equal("truncated-logical-nand", addFailure.Kind);

        var physicalDetection = NandPhysicalFormatDetector.Detect(physical.Span);
        Assert.Equal(NandPhysicalFormat.InterleavedEcc, physicalDetection.Format);
        Assert.Contains(NandPhysicalLayout.Layout0, physicalDetection.CandidateLayouts);

        var truncatedDetection = NandPhysicalFormatDetector.Detect(truncatedPhysical.Span);
        Assert.Equal(NandPhysicalFormat.Truncated, truncatedDetection.Format);
    }

    [Fact]
    public void Format_detector_identifies_generated_layouts_after_a_block_boundary()
    {
        foreach (var layout in Layouts)
        {
            var logical = CreatePattern(checked(layout.Geometry.LogicalBlockSize * 2));
            var physical = NandEccCodec.AddEcc(logical, layout);

            var detection = NandPhysicalFormatDetector.Detect(physical.Span);

            Assert.Equal(NandPhysicalFormat.InterleavedEcc, detection.Format);
            Assert.Equal(layout, detection.Layout);
        }
    }

    [Fact]
    public void Add_ecc_honors_cancellation_between_erase_blocks()
    {
        var layout = NandPhysicalLayout.Layout0;
        var logical = new byte[checked(layout.Geometry.LogicalBlockSize * 2)];
        using var cancellation = new CancellationTokenSource();
        var progress = new CallbackProgress(operationProgress =>
        {
            if (operationProgress.Completed == layout.Geometry.PagesPerBlock)
            {
                cancellation.Cancel();
            }
        });

        Assert.Throws<OperationCanceledException>(() => NandEccCodec.AddEcc(
            logical,
            layout,
            progress: progress,
            cancellationToken: cancellation.Token));
    }

    [Fact]
    public void Allocating_ecc_operations_honor_a_pre_cancelled_token()
    {
        var logical = new byte[NandPhysicalGeometry.LogicalPageSize];
        var physical = NandEccCodec.AddEcc(logical, NandPhysicalLayout.Layout0);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => NandEccCodec.AddEcc(
            logical,
            NandPhysicalLayout.Layout0,
            cancellationToken: cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => NandEccCodec.RemoveEcc(
            physical.Span,
            cancellationToken: cancellation.Token));
    }

    private static IEnumerable<NandPhysicalLayout> Layouts =>
        new[]
        {
            NandPhysicalLayout.Layout0,
            NandPhysicalLayout.Layout1,
            NandPhysicalLayout.Layout2,
        };

    private static void AssertMarkerAndBlockId(NandPhysicalLayout layout, int expectedMarkerOffset, int expectedIdOffset)
    {
        var logical = new byte[checked(layout.Geometry.LogicalBlockSize * 2)];
        var physical = NandEccCodec.AddEcc(logical, layout, firstPhysicalBlock: 0x102);
        var firstPage = physical.Span.Slice(0, NandPhysicalGeometry.PhysicalPageSize);
        var secondBlockFirstPage = physical.Span.Slice(
            layout.Geometry.PagesPerBlock * NandPhysicalGeometry.PhysicalPageSize,
            NandPhysicalGeometry.PhysicalPageSize);

        Assert.Equal(expectedMarkerOffset, layout.MarkerOffset);
        Assert.Equal(expectedIdOffset, layout.BlockIdOffset);
        Assert.Equal(byte.MaxValue, firstPage[expectedMarkerOffset]);
        Assert.Equal(0x02, firstPage[expectedIdOffset]);
        Assert.Equal(0x01, firstPage[expectedIdOffset + 1]);
        Assert.Equal(byte.MaxValue, secondBlockFirstPage[expectedMarkerOffset]);
        Assert.Equal(0x03, secondBlockFirstPage[expectedIdOffset]);
        Assert.Equal(0x01, secondBlockFirstPage[expectedIdOffset + 1]);
    }

    private static byte[] CreatePattern(int length)
    {
        var bytes = new byte[length];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = unchecked((byte)((index * 31) + 7));
        }

        return bytes;
    }

    private sealed class CallbackProgress : IProgress<OperationProgress>
    {
        private readonly Action<OperationProgress> callback;

        public CallbackProgress(Action<OperationProgress> callback)
        {
            this.callback = callback;
        }

        public void Report(OperationProgress value) => callback(value);
    }
}
