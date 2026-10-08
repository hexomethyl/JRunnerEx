using JRunner.Core.Contracts;
using JRunner.Core.Nand.Comparison;
using JRunner.Core.Nand.Physical;
using Xunit;

namespace JRunner.Core.Tests.Nand.Comparison;

public sealed class NandCanonicalComparisonServiceTests
{
    [Fact]
    public void Canonical_input_validates_source_and_incompatible_hints()
    {
        Assert.Throws<ArgumentNullException>(() => new NandCanonicalInput(null!));
        Assert.Throws<ArgumentException>(() => new NandCanonicalInput(
            Stream.Null,
            NandCanonicalInputFormat.Logical,
            NandLegacyLayout.Layout0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NandCanonicalInput(
            Stream.Null,
            (NandCanonicalInputFormat)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NandCanonicalInput(
            Stream.Null,
            NandCanonicalInputFormat.Auto,
            (NandLegacyLayout)99));
    }
    [Fact]
    public async Task Logical_inputs_report_equality_difference_and_unequal_tail()
    {
        byte[] logical = CreatePattern(NandPhysicalGeometry.LogicalPageSize * 2);
        using var equalLeft = new MemoryStream(logical, writable: false);
        using var equalRight = new MemoryStream(logical, writable: false);

        NandCanonicalComparisonResult equal = await NandCanonicalComparisonService.CompareAsync(
            CreateRequest(equalLeft, NandCanonicalInputFormat.Logical, equalRight, NandCanonicalInputFormat.Logical));

        Assert.True(equal.Equal);
        Assert.Equal(0L, equal.DifferingByteCount);
        Assert.Null(equal.FirstDifferingLogicalOffset);
        Assert.Equal(NandPhysicalFormat.Logical, equal.Left.DetectedFormat);
        Assert.False(equal.Left.HasSpareData);
        Assert.Equal((long)logical.Length, equal.Left.CanonicalLogicalByteLength);

        byte[] changed = logical.ToArray();
        changed[NandPhysicalGeometry.LogicalPageSize + 7] ^= 0xFF;
        using var differentLeft = new MemoryStream(logical, writable: false);
        using var differentRight = new MemoryStream(changed, writable: false);

        NandCanonicalComparisonResult different = await NandCanonicalComparisonService.CompareAsync(
            CreateRequest(differentLeft, NandCanonicalInputFormat.Logical, differentRight, NandCanonicalInputFormat.Logical));

        Assert.False(different.Equal);
        Assert.Equal(1L, different.DifferingByteCount);
        Assert.Equal((long?)NandPhysicalGeometry.LogicalPageSize + 7, different.FirstDifferingLogicalOffset);

        byte[] longer = new byte[logical.Length + NandPhysicalGeometry.LogicalPageSize];
        logical.CopyTo(longer, 0);
        using var tailLeft = new MemoryStream(logical, writable: false);
        using var tailRight = new MemoryStream(longer, writable: false);

        NandCanonicalComparisonResult tail = await NandCanonicalComparisonService.CompareAsync(
            CreateRequest(tailLeft, NandCanonicalInputFormat.Logical, tailRight, NandCanonicalInputFormat.Logical));

        Assert.False(tail.Equal);
        Assert.Equal((long)NandPhysicalGeometry.LogicalPageSize, tail.DifferingByteCount);
        Assert.Equal((long?)logical.Length, tail.FirstDifferingLogicalOffset);
    }

    [Fact]
    public async Task Auto_mode_does_not_treat_erased_logical_bytes_as_physical_ECC_evidence()
    {
        var logical = new byte[0x4000];
        logical.AsSpan().Fill(byte.MaxValue);
        using var left = new MemoryStream(logical, writable: false);
        using var right = new MemoryStream(logical, writable: false);

        NandCanonicalComparisonResult result = await NandCanonicalComparisonService.CompareAsync(
            CreateRequest(left, NandCanonicalInputFormat.Auto, right, NandCanonicalInputFormat.Auto));

        Assert.True(result.Equal);
        Assert.Equal(NandPhysicalFormat.Logical, result.Left.DetectedFormat);
        Assert.Equal(NandPhysicalFormat.Logical, result.Right.DetectedFormat);
    }

    [Fact]
    public async Task Logical_and_interleaved_ecc_inputs_compare_equal()
    {
        var layout = NandPhysicalLayout.Layout0;
        byte[] logical = CreatePattern(layout.Geometry.LogicalBlockSize);
        byte[] physical = NandEccCodec.AddEcc(logical, layout).ToArray();
        using var logicalSource = new MemoryStream(logical, writable: false);
        using var physicalSource = new MemoryStream(physical, writable: false);

        NandCanonicalComparisonResult result = await NandCanonicalComparisonService.CompareAsync(
            CreateRequest(
                logicalSource,
                NandCanonicalInputFormat.Logical,
                physicalSource,
                NandCanonicalInputFormat.InterleavedEcc,
                rightLayout: layout.LegacyLayout));

        Assert.True(result.Equal);
        Assert.Equal(NandPhysicalFormat.InterleavedEcc, result.Right.DetectedFormat);
        Assert.True(result.Right.HasSpareData);
        Assert.Equal((NandLegacyLayout?)layout.LegacyLayout, result.Right.SelectedLayout);
        Assert.Equal(1L, result.Right.PhysicalBlockCount);
        Assert.Equal((long)physical.Length, result.Right.RawByteLength);
        Assert.Equal((long)logical.Length, result.Right.CanonicalLogicalByteLength);
    }

    [Fact]
    public async Task Physical_spare_layout_differences_do_not_change_canonical_data()
    {
        byte[] logical = CreatePattern(NandPhysicalLayout.Layout0.Geometry.LogicalBlockSize);
        byte[] layout0 = NandEccCodec.AddEcc(logical, NandPhysicalLayout.Layout0).ToArray();
        byte[] layout1 = NandEccCodec.AddEcc(logical, NandPhysicalLayout.Layout1).ToArray();
        using var left = new MemoryStream(layout0, writable: false);
        using var right = new MemoryStream(layout1, writable: false);

        NandCanonicalComparisonResult result = await NandCanonicalComparisonService.CompareAsync(
            CreateRequest(
                left,
                NandCanonicalInputFormat.InterleavedEcc,
                right,
                NandCanonicalInputFormat.InterleavedEcc,
                NandLegacyLayout.Layout0,
                NandLegacyLayout.Layout1));

        Assert.True(result.Equal);
        Assert.Equal((NandLegacyLayout?)NandLegacyLayout.Layout0, result.Left.SelectedLayout);
        Assert.Equal((NandLegacyLayout?)NandLegacyLayout.Layout1, result.Right.SelectedLayout);
    }

    [Fact]
    public async Task Physical_validation_accepts_ecc_with_nonzero_block_type_bits()
    {
        var layout = NandPhysicalLayout.Layout0;
        byte[] logical = CreatePattern(layout.Geometry.LogicalBlockSize);
        byte[] physical = NandEccCodec.AddEcc(logical, layout).ToArray();
        int pageOffset = NandPhysicalGeometry.PhysicalPageSize * 5;
        int eccOffset = pageOffset + NandPhysicalGeometry.PhysicalPageSize - sizeof(uint);
        physical[eccOffset] = (byte)((physical[eccOffset] & 0xC0) | 0x2C);
        NandEccCodec.CalculateEcc(physical.AsSpan(pageOffset, NandPhysicalGeometry.PhysicalPageSize));
        using var logicalSource = new MemoryStream(logical, writable: false);
        using var physicalSource = new MemoryStream(physical, writable: false);

        NandCanonicalComparisonResult result = await NandCanonicalComparisonService.CompareAsync(
            CreateRequest(
                logicalSource,
                NandCanonicalInputFormat.Logical,
                physicalSource,
                NandCanonicalInputFormat.InterleavedEcc,
                rightLayout: layout.LegacyLayout));

        Assert.True(result.Equal);
    }

    [Theory]
    [InlineData(NandLegacyLayout.Layout0, 0x30)]
    [InlineData(NandLegacyLayout.Layout2, 0x2C)]
    public async Task Physical_validation_accepts_filesystem_spare_metadata(
        NandLegacyLayout legacyLayout,
        int blockType)
    {
        NandPhysicalLayout layout = NandPhysicalLayout.FromLegacyLayout(legacyLayout);
        byte[] logical = CreatePattern(checked(layout.Geometry.LogicalBlockSize * 2));
        byte[] physical = NandEccCodec.AddEcc(logical, layout).ToArray();
        const int pageIndex = 5;
        int pageOffset = pageIndex * NandPhysicalGeometry.PhysicalPageSize;
        int spareOffset = pageOffset + NandPhysicalGeometry.LogicalPageSize;
        physical[spareOffset + 3] = 0xA7;
        physical[spareOffset + 4] = 0x5D;
        physical[spareOffset + 6] = 0x3C;
        if (legacyLayout == NandLegacyLayout.Layout0)
        {
            physical[spareOffset] = 0x7A;
        }
        physical[pageOffset + NandPhysicalGeometry.PhysicalPageSize - sizeof(uint)] =
            (byte)((physical[pageOffset + NandPhysicalGeometry.PhysicalPageSize - sizeof(uint)] & 0xC0) | blockType);
        NandEccCodec.CalculateEcc(physical.AsSpan(pageOffset, NandPhysicalGeometry.PhysicalPageSize));
        using var logicalSource = new MemoryStream(logical, writable: false);
        using var physicalSource = new MemoryStream(physical, writable: false);

        NandCanonicalComparisonResult result = await NandCanonicalComparisonService.CompareAsync(
            CreateRequest(
                logicalSource,
                NandCanonicalInputFormat.Logical,
                physicalSource,
                NandCanonicalInputFormat.Auto));

        Assert.True(result.Equal);
        Assert.Equal((NandLegacyLayout?)legacyLayout, result.Right.SelectedLayout);
    }

    [Fact]
    public async Task Physical_validation_accepts_erased_all_ff_pages()
    {
        var layout = NandPhysicalLayout.Layout0;
        byte[] logical = new byte[layout.Geometry.LogicalBlockSize];
        logical.AsSpan().Fill(byte.MaxValue);
        byte[] physical = NandEccCodec.AddEcc(logical, layout).ToArray();
        physical.AsSpan(
            NandPhysicalGeometry.PhysicalPageSize,
            NandPhysicalGeometry.PhysicalPageSize).Fill(byte.MaxValue);
        using var logicalSource = new MemoryStream(logical, writable: false);
        using var physicalSource = new MemoryStream(physical, writable: false);

        NandCanonicalComparisonResult result = await NandCanonicalComparisonService.CompareAsync(
            CreateRequest(
                logicalSource,
                NandCanonicalInputFormat.Logical,
                physicalSource,
                NandCanonicalInputFormat.InterleavedEcc,
                rightLayout: layout.LegacyLayout));

        Assert.True(result.Equal);
    }

    [Fact]
    public async Task Remapped_bad_blocks_project_to_the_same_logical_image()
    {
        var layout = NandPhysicalLayout.Layout0;
        const int blockCount = 0x400;
        int logicalBlockSize = layout.Geometry.LogicalBlockSize;
        int physicalBlockSize = layout.Geometry.PhysicalBlockSize;
        byte[] logical = new byte[checked(blockCount * logicalBlockSize)];
        logical.AsSpan().Fill(byte.MaxValue);
        logical.AsSpan(0, logicalBlockSize).Fill(0x5A);
        byte[] physical = CreateSmallBlockPhysicalImage(logical, layout, blockCount);
        byte[] replacement = physical.AsSpan(0, physicalBlockSize).ToArray();
        replacement.CopyTo(physical, checked((blockCount - 1) * physicalBlockSize));
        physical[layout.MarkerOffset] = 0x00;
        using var logicalSource = new MemoryStream(logical, writable: false);
        using var physicalSource = new MemoryStream(physical, writable: false);

        NandCanonicalComparisonResult result = await NandCanonicalComparisonService.CompareAsync(
            CreateRequest(
                logicalSource,
                NandCanonicalInputFormat.Logical,
                physicalSource,
                NandCanonicalInputFormat.Auto));

        Assert.True(result.Equal);
        NandBadBlock badBlock = Assert.Single(result.Right.BadBlocks);
        Assert.Equal(0L, badBlock.PhysicalBlock);
        NandBadBlockRemap remap = Assert.Single(result.Right.Remaps);
        Assert.Equal((long?)((long)blockCount - 1), remap.ReplacementPhysicalBlock);
    }

    [Fact]
    public async Task Malformed_truncated_and_ambiguous_inputs_have_stable_failure_kinds()
    {
        using var empty = new MemoryStream(Array.Empty<byte>(), writable: false);
        OperationFailureException emptyFailure = await Assert.ThrowsAsync<OperationFailureException>(() =>
            NandCanonicalComparisonService.CompareAsync(CreateRequest(empty, NandCanonicalInputFormat.Logical, new MemoryStream(new byte[NandPhysicalGeometry.LogicalPageSize], writable: false), NandCanonicalInputFormat.Logical)));
        Assert.Equal("empty-nand", emptyFailure.Kind);

        using var truncatedLogical = new MemoryStream(new byte[NandPhysicalGeometry.LogicalPageSize - 1], writable: false);
        OperationFailureException truncatedLogicalFailure = await Assert.ThrowsAsync<OperationFailureException>(() =>
            NandCanonicalComparisonService.CompareAsync(CreateRequest(truncatedLogical, NandCanonicalInputFormat.Logical, new MemoryStream(new byte[NandPhysicalGeometry.LogicalPageSize], writable: false), NandCanonicalInputFormat.Logical)));
        Assert.Equal("truncated-logical-nand", truncatedLogicalFailure.Kind);

        byte[] physicalPage = NandEccCodec.AddEcc(new byte[NandPhysicalGeometry.LogicalPageSize], NandPhysicalLayout.Layout0).ToArray();
        using var truncatedPhysical = new MemoryStream(physicalPage, writable: false);
        OperationFailureException truncatedPhysicalFailure = await Assert.ThrowsAsync<OperationFailureException>(() =>
            NandCanonicalComparisonService.CompareAsync(CreateRequest(
                truncatedPhysical,
                NandCanonicalInputFormat.InterleavedEcc,
                new MemoryStream(new byte[NandPhysicalGeometry.LogicalPageSize], writable: false),
                NandCanonicalInputFormat.Logical,
                leftLayout: NandLegacyLayout.Layout0)));
        Assert.Equal("truncated-physical-block", truncatedPhysicalFailure.Kind);

        using var partialPhysical = new MemoryStream(physicalPage.AsSpan(0, physicalPage.Length - 1).ToArray(), writable: false);
        OperationFailureException partialPhysicalFailure = await Assert.ThrowsAsync<OperationFailureException>(() =>
            NandCanonicalComparisonService.CompareAsync(CreateRequest(
                partialPhysical,
                NandCanonicalInputFormat.InterleavedEcc,
                new MemoryStream(new byte[NandPhysicalGeometry.LogicalPageSize], writable: false),
                NandCanonicalInputFormat.Logical,
                leftLayout: NandLegacyLayout.Layout0)));
        Assert.Equal("truncated-physical-page", partialPhysicalFailure.Kind);

        byte[] oneBlock = NandEccCodec.AddEcc(
            new byte[NandPhysicalLayout.Layout0.Geometry.LogicalBlockSize],
            NandPhysicalLayout.Layout0).ToArray();
        for (int pageIndex = 0; pageIndex < NandPhysicalLayout.Layout0.Geometry.PagesPerBlock; pageIndex++)
        {
            oneBlock[(pageIndex * NandPhysicalGeometry.PhysicalPageSize) + NandPhysicalGeometry.PhysicalPageSize - sizeof(uint)] ^= 0x40;
        }

        using var ambiguous = new MemoryStream(oneBlock, writable: false);
        OperationFailureException ambiguousFailure = await Assert.ThrowsAsync<OperationFailureException>(() =>
            NandCanonicalComparisonService.CompareAsync(CreateRequest(
                ambiguous,
                NandCanonicalInputFormat.Auto,
                new MemoryStream(new byte[NandPhysicalGeometry.LogicalPageSize], writable: false),
                NandCanonicalInputFormat.Logical)));
        Assert.Equal("ambiguous-nand-format", ambiguousFailure.Kind);

        using var unsupportedLarge = new ZeroFilledSeekableStream(0xE0400000L - NandPhysicalGeometry.LogicalPageSize);
        OperationFailureException unsupportedFailure = await Assert.ThrowsAsync<OperationFailureException>(() =>
            NandCanonicalComparisonService.CompareAsync(CreateRequest(
                unsupportedLarge,
                NandCanonicalInputFormat.Auto,
                new MemoryStream(new byte[NandPhysicalGeometry.LogicalPageSize], writable: false),
                NandCanonicalInputFormat.Logical)));
        Assert.Equal("unsupported-nand-format", unsupportedFailure.Kind);
    }

    [Fact]
    public async Task Physical_page_spare_reserved_area_and_layout_ambiguity_fail_strictly()
    {
        var layout = NandPhysicalLayout.Layout0;
        byte[] invalidPage = NandEccCodec.AddEcc(
            new byte[layout.Geometry.LogicalBlockSize],
            layout).ToArray();
        invalidPage[NandPhysicalGeometry.PhysicalPageSize - sizeof(uint)] ^= 0x40;
        using var invalidPageSource = new MemoryStream(invalidPage, writable: false);
        OperationFailureException invalidPageFailure = await Assert.ThrowsAsync<OperationFailureException>(() =>
            NandCanonicalComparisonService.CompareAsync(CreateRequest(
                invalidPageSource,
                NandCanonicalInputFormat.InterleavedEcc,
                new MemoryStream(new byte[NandPhysicalGeometry.LogicalPageSize], writable: false),
                NandCanonicalInputFormat.Logical,
                leftLayout: layout.LegacyLayout)));
        Assert.Equal("invalid-physical-page", invalidPageFailure.Kind);

        byte[] invalidSpare = NandEccCodec.AddEcc(
            new byte[layout.Geometry.LogicalBlockSize],
            layout).ToArray();
        invalidSpare[layout.BlockIdOffset] = 0x7F;
        NandEccCodec.CalculateEcc(invalidSpare.AsSpan(0, NandPhysicalGeometry.PhysicalPageSize));
        using var invalidSpareSource = new MemoryStream(invalidSpare, writable: false);
        OperationFailureException invalidSpareFailure = await Assert.ThrowsAsync<OperationFailureException>(() =>
            NandCanonicalComparisonService.CompareAsync(CreateRequest(
                invalidSpareSource,
                NandCanonicalInputFormat.InterleavedEcc,
                new MemoryStream(new byte[NandPhysicalGeometry.LogicalPageSize], writable: false),
                NandCanonicalInputFormat.Logical,
                leftLayout: layout.LegacyLayout)));
        Assert.Equal("invalid-physical-spare", invalidSpareFailure.Kind);

        byte[] badBlock = NandEccCodec.AddEcc(
            new byte[layout.Geometry.LogicalBlockSize],
            layout).ToArray();
        badBlock[layout.MarkerOffset] = 0x00;
        using var badBlockSource = new MemoryStream(badBlock, writable: false);
        OperationFailureException reservedFailure = await Assert.ThrowsAsync<OperationFailureException>(() =>
            NandCanonicalComparisonService.CompareAsync(CreateRequest(
                badBlockSource,
                NandCanonicalInputFormat.InterleavedEcc,
                new MemoryStream(new byte[NandPhysicalGeometry.LogicalPageSize], writable: false),
                NandCanonicalInputFormat.Logical,
                leftLayout: layout.LegacyLayout)));
        Assert.Equal("truncated-reserved-area", reservedFailure.Kind);

        byte[] ambiguousLayout = NandEccCodec.AddEcc(
            new byte[layout.Geometry.LogicalBlockSize],
            layout).ToArray();
        using var ambiguousLayoutSource = new MemoryStream(ambiguousLayout, writable: false);
        OperationFailureException layoutFailure = await Assert.ThrowsAsync<OperationFailureException>(() =>
            NandCanonicalComparisonService.CompareAsync(CreateRequest(
                ambiguousLayoutSource,
                NandCanonicalInputFormat.Auto,
                new MemoryStream(new byte[NandPhysicalGeometry.LogicalPageSize], writable: false),
                NandCanonicalInputFormat.Logical)));
        Assert.Equal("ambiguous-nand-layout", layoutFailure.Kind);

        const int excessiveBadBlockCount = 0x21;
        byte[] excessiveBadLogical = new byte[checked(excessiveBadBlockCount * layout.Geometry.LogicalBlockSize)];
        byte[] excessiveBadPhysical = CreateSmallBlockPhysicalImage(excessiveBadLogical, layout, excessiveBadBlockCount);
        for (int blockIndex = 0; blockIndex < excessiveBadBlockCount; blockIndex++)
        {
            excessiveBadPhysical[(blockIndex * layout.Geometry.PhysicalBlockSize) + layout.MarkerOffset] = 0x00;
        }

        using var excessiveBadSource = new MemoryStream(excessiveBadPhysical, writable: false);
        OperationFailureException excessiveBadFailure = await Assert.ThrowsAsync<OperationFailureException>(() =>
            NandCanonicalComparisonService.CompareAsync(CreateRequest(
                excessiveBadSource,
                NandCanonicalInputFormat.InterleavedEcc,
                new MemoryStream(new byte[NandPhysicalGeometry.LogicalPageSize], writable: false),
                NandCanonicalInputFormat.Logical,
                leftLayout: layout.LegacyLayout)));
        Assert.Equal("too-many-bad-blocks", excessiveBadFailure.Kind);
    }

    [Fact]
    public async Task Comparison_honors_cancellation()
    {
        byte[] logical = CreatePattern(NandPhysicalGeometry.LogicalPageSize);
        using var left = new MemoryStream(logical, writable: false);
        using var right = new MemoryStream(logical, writable: false);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            NandCanonicalComparisonService.CompareAsync(
                CreateRequest(left, NandCanonicalInputFormat.Logical, right, NandCanonicalInputFormat.Logical),
                cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task Comparison_leaves_caller_streams_open_and_spools_nonseekable_input_from_its_current_position()
    {
        byte[] logical = CreatePattern(NandPhysicalGeometry.LogicalPageSize);
        using var prefixed = new MemoryStream();
        prefixed.WriteByte(0xA5);
        prefixed.Write(logical);
        prefixed.Position = 1;
        using var left = new NonSeekableReadStream(prefixed);
        using var right = new MemoryStream(logical, writable: false);

        NandCanonicalComparisonResult result = await NandCanonicalComparisonService.CompareAsync(
            CreateRequest(left, NandCanonicalInputFormat.Logical, right, NandCanonicalInputFormat.Logical));

        Assert.True(result.Equal);
        Assert.False(left.WasDisposed);
        Assert.True(left.CanRead);
        Assert.True(right.CanRead);
        Assert.Equal(0L, right.Position);
    }

    [Fact]
    public async Task Auto_mode_accepts_exact_corona_or_winchester_emmc_length_before_comparison()
    {
        const long coronaOrWinchesterEmmcRawByteLength = 0xE0400000L;
        using var left = new ZeroFilledSeekableStream(coronaOrWinchesterEmmcRawByteLength);
        using var right = new ZeroFilledSeekableStream(coronaOrWinchesterEmmcRawByteLength);
        using var cancellation = new CancellationTokenSource();
        var reachedComparison = false;
        var progress = new CallbackProgress(operationProgress =>
        {
            if (operationProgress.Kind == "comparing-canonical-nand" && operationProgress.Completed == 0)
            {
                reachedComparison = true;
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            NandCanonicalComparisonService.CompareAsync(
                CreateRequest(left, NandCanonicalInputFormat.Auto, right, NandCanonicalInputFormat.Auto),
                progress,
                cancellation.Token));

        Assert.True(reachedComparison);
    }

    private static NandCanonicalComparisonRequest CreateRequest(
        Stream left,
        NandCanonicalInputFormat leftFormat,
        Stream right,
        NandCanonicalInputFormat rightFormat,
        NandLegacyLayout? leftLayout = null,
        NandLegacyLayout? rightLayout = null) =>
        new(
            new NandCanonicalInput(left, leftFormat, leftLayout),
            new NandCanonicalInput(right, rightFormat, rightLayout));

    private static byte[] CreatePattern(int length)
    {
        var bytes = new byte[length];
        for (int index = 0; index < bytes.Length; index++)
        {
            bytes[index] = unchecked((byte)((index * 31) + 7));
        }

        return bytes;
    }

    private static byte[] CreateSmallBlockPhysicalImage(
        ReadOnlySpan<byte> logical,
        NandPhysicalLayout layout,
        int blockCount)
    {
        int logicalBlockSize = layout.Geometry.LogicalBlockSize;
        int physicalBlockSize = layout.Geometry.PhysicalBlockSize;
        var physical = new byte[checked(blockCount * physicalBlockSize)];
        for (int blockIndex = 0; blockIndex < blockCount; blockIndex++)
        {
            ReadOnlySpan<byte> logicalPage = logical.Slice(
                blockIndex * logicalBlockSize,
                NandPhysicalGeometry.LogicalPageSize);
            byte[] physicalPage = NandEccCodec.AddEcc(logicalPage, layout, blockIndex).ToArray();
            for (int pageIndex = 0; pageIndex < layout.Geometry.PagesPerBlock; pageIndex++)
            {
                physicalPage.CopyTo(physical, checked(
                    (blockIndex * physicalBlockSize) +
                    (pageIndex * NandPhysicalGeometry.PhysicalPageSize)));
            }
        }

        return physical;
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

    private sealed class NonSeekableReadStream : Stream
    {
        private readonly Stream inner;

        public NonSeekableReadStream(Stream inner)
        {
            this.inner = inner;
        }

        public bool WasDisposed { get; private set; }

        public override bool CanRead => !WasDisposed && inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                WasDisposed = true;
            }

            base.Dispose(disposing);
        }
    }

    private sealed class ZeroFilledSeekableStream : Stream
    {
        private readonly long length;
        private long position;

        public ZeroFilledSeekableStream(long length)
        {
            this.length = length;
        }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => position;
            set
            {
                if (value < 0 || value > length)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                position = value;
            }
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            if (offset < 0 || count < 0 || buffer.Length - offset < count)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            int read = GetReadLength(count);
            Array.Clear(buffer, offset, read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            int read = GetReadLength(buffer.Length);
            buffer.Slice(0, read).Clear();
            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(position + offset),
                SeekOrigin.End => checked(length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            Position = target;
            return position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int GetReadLength(int requestedLength)
        {
            if (position >= length)
            {
                return 0;
            }

            int read = (int)Math.Min(requestedLength, length - position);
            position += read;
            return read;
        }
    }
}
