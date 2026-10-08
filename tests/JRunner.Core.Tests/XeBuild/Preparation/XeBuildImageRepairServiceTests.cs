using System.Buffers.Binary;
using JRunner.Core.Contracts;
using JRunner.Core.Nand.Physical;
using JRunner.Core.XeBuild.Preparation;
using Xunit;

namespace JRunner.Core.Tests.XeBuild.Preparation;

public sealed class XeBuildImageRepairServiceTests
{
    private const long Physical16MbImageLength = 0x1080000;
    private const long Physical64MbImageLength = 0x4200000;
    private const long Logical48MbImageLength = 0x3000000;
    private const int KeyVaultSizeOffset = 0x60;
    private const int PatchSlotSizeOffset = 0x70;

    public static IEnumerable<object[]> PhysicalOutputCases =>
    [
        [Physical16MbImageLength, NandLegacyLayout.Layout0],
        [Physical64MbImageLength, NandLegacyLayout.Layout1],
        [Physical64MbImageLength, NandLegacyLayout.Layout2],
    ];

    [Fact]
    public async Task Repairs_both_zero_fields_in_a_supported_logical_output()
    {
        using var image = new SparseWritableSeekableStream(Logical48MbImageLength);
        byte[] firstPage = CreatePattern(NandPhysicalGeometry.LogicalPageSize);
        firstPage.AsSpan(KeyVaultSizeOffset, sizeof(uint)).Clear();
        firstPage.AsSpan(PatchSlotSizeOffset, sizeof(uint)).Clear();
        image.WriteAt(0, firstPage);
        byte[] untouchedTail = [0xA1, 0xB2, 0xC3, 0xD4];
        image.WriteAt(0x900, untouchedTail);
        long initialPosition = checked(Logical48MbImageLength + 0x20);
        image.Position = initialPosition;

        XeBuildImageRepairResult result = await XeBuildImageRepairService.RepairAsync(image);

        byte[] expectedPage = firstPage.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(expectedPage.AsSpan(KeyVaultSizeOffset, sizeof(uint)), 0x00004000);
        BinaryPrimitives.WriteUInt32BigEndian(expectedPage.AsSpan(PatchSlotSizeOffset, sizeof(uint)), 0x00010000);
        Assert.Equal(NandPhysicalFormat.Logical, result.ImageFormat);
        Assert.Equal(Logical48MbImageLength, result.ImageByteLength);
        Assert.True(result.KeyVaultSizeRepaired);
        Assert.True(result.PatchSlotSizeRepaired);
        Assert.False(result.FirstPageEccRecalculated);
        Assert.Equal(initialPosition, image.Position);
        Assert.Equal(expectedPage, image.ReadAt(0, NandPhysicalGeometry.LogicalPageSize));
        Assert.Equal(untouchedTail, image.ReadAt(0x900, untouchedTail.Length));
    }

    [Theory]
    [MemberData(nameof(PhysicalOutputCases))]
    public async Task Repairs_supported_physical_outputs_without_regenerating_their_spare_data(
        long imageLength,
        NandLegacyLayout legacyLayout)
    {
        NandPhysicalLayout layout = NandPhysicalLayout.FromLegacyLayout(legacyLayout);
        using var image = new SparseWritableSeekableStream(imageLength);
        byte[] logicalPage = CreatePattern(NandPhysicalGeometry.LogicalPageSize);
        logicalPage.AsSpan(KeyVaultSizeOffset, sizeof(uint)).Clear();
        logicalPage.AsSpan(PatchSlotSizeOffset, sizeof(uint)).Clear();
        byte[] physicalPage = NandEccCodec.AddEcc(logicalPage, layout).ToArray();
        physicalPage[0x202] = 0xA5;
        physicalPage[0x20C] = (byte)((physicalPage[0x20C] & 0xC0) | 0x2C);
        NandEccCodec.CalculateEcc(physicalPage);
        byte[] originalPhysicalPage = physicalPage.ToArray();
        byte[] untouchedTail = [0x91, 0x82, 0x73, 0x64];
        image.WriteAt(0, physicalPage);
        image.WriteAt(0x900, untouchedTail);
        image.ReadRanges.Clear();
        image.WriteRanges.Clear();

        XeBuildImageRepairResult result = await XeBuildImageRepairService.RepairAsync(image);

        byte[] expectedLogicalPage = logicalPage.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(expectedLogicalPage.AsSpan(KeyVaultSizeOffset, sizeof(uint)), 0x00004000);
        BinaryPrimitives.WriteUInt32BigEndian(expectedLogicalPage.AsSpan(PatchSlotSizeOffset, sizeof(uint)), 0x00010000);
        byte[] repairedPhysicalPage = image.ReadAt(0, NandPhysicalGeometry.PhysicalPageSize);
        Assert.Equal(NandPhysicalFormat.InterleavedEcc, result.ImageFormat);
        Assert.Equal(imageLength, result.ImageByteLength);
        Assert.True(result.KeyVaultSizeRepaired);
        Assert.True(result.PatchSlotSizeRepaired);
        Assert.True(result.FirstPageEccRecalculated);
        Assert.Equal(expectedLogicalPage, repairedPhysicalPage.AsSpan(0, NandPhysicalGeometry.LogicalPageSize).ToArray());
        Assert.Equal(
            originalPhysicalPage.AsSpan(NandPhysicalGeometry.LogicalPageSize, 0xC).ToArray(),
            repairedPhysicalPage.AsSpan(NandPhysicalGeometry.LogicalPageSize, 0xC).ToArray());
        Assert.Equal(
            (byte)(originalPhysicalPage[0x20C] & 0x3F),
            (byte)(repairedPhysicalPage[0x20C] & 0x3F));
        Assert.True(NandEccCodec.HasValidEcc(repairedPhysicalPage));
        Assert.Equal(untouchedTail, image.ReadAt(0x900, untouchedTail.Length));
        StreamRange readRange = Assert.Single(image.ReadRanges);
        Assert.Equal(0, readRange.Offset);
        Assert.Equal(NandPhysicalGeometry.PhysicalPageSize, readRange.Count);
        StreamRange writeRange = Assert.Single(image.WriteRanges);
        Assert.Equal(0, writeRange.Offset);
        Assert.Equal(NandPhysicalGeometry.PhysicalPageSize, writeRange.Count);
    }

    [Fact]
    public async Task Leaves_populated_fields_unchanged_while_refreshing_physical_ecc()
    {
        using var image = new SparseWritableSeekableStream(Physical16MbImageLength);
        byte[] logicalPage = CreatePattern(NandPhysicalGeometry.LogicalPageSize);
        BinaryPrimitives.WriteUInt32BigEndian(logicalPage.AsSpan(KeyVaultSizeOffset, sizeof(uint)), 0x00012000);
        BinaryPrimitives.WriteUInt32BigEndian(logicalPage.AsSpan(PatchSlotSizeOffset, sizeof(uint)), 0x00020000);
        byte[] physicalPage = NandEccCodec.AddEcc(logicalPage, NandPhysicalLayout.Layout0).ToArray();
        physicalPage[0x20C] ^= 0x80;
        byte[] originalPhysicalPage = physicalPage.ToArray();
        image.WriteAt(0, physicalPage);

        XeBuildImageRepairResult result = await XeBuildImageRepairService.RepairAsync(image);

        byte[] repairedPhysicalPage = image.ReadAt(0, NandPhysicalGeometry.PhysicalPageSize);
        Assert.False(result.KeyVaultSizeRepaired);
        Assert.False(result.PatchSlotSizeRepaired);
        Assert.True(result.FirstPageEccRecalculated);
        Assert.Equal(logicalPage, repairedPhysicalPage.AsSpan(0, NandPhysicalGeometry.LogicalPageSize).ToArray());
        Assert.Equal(
            originalPhysicalPage.AsSpan(NandPhysicalGeometry.LogicalPageSize, 0xC).ToArray(),
            repairedPhysicalPage.AsSpan(NandPhysicalGeometry.LogicalPageSize, 0xC).ToArray());
        Assert.NotEqual(originalPhysicalPage[0x20C], repairedPhysicalPage[0x20C]);
        Assert.True(NandEccCodec.HasValidEcc(repairedPhysicalPage));
    }

    [Fact]
    public async Task Rejects_unsupported_output_sizes_with_a_typed_failure()
    {
        using var image = new SparseWritableSeekableStream(Logical48MbImageLength + 1);

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => XeBuildImageRepairService.RepairAsync(image));

        Assert.Equal(ExitCode.InvalidData, failure.Code);
        Assert.Equal("unsupported-xebuild-image-size", failure.Kind);
        Assert.Empty(image.ReadRanges);
        Assert.Empty(image.WriteRanges);
    }

    [Fact]
    public async Task Rejects_a_truncated_supported_output_with_a_typed_failure()
    {
        using var image = new SparseWritableSeekableStream(
            Physical16MbImageLength,
            readableLength: NandPhysicalGeometry.PhysicalPageSize - 1);

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => XeBuildImageRepairService.RepairAsync(image));

        Assert.Equal(ExitCode.InvalidData, failure.Code);
        Assert.Equal("truncated-xebuild-image", failure.Kind);
        Assert.Empty(image.WriteRanges);
    }

    [Fact]
    public async Task Rejects_an_unwritable_output_with_a_typed_failure()
    {
        using var image = new SparseWritableSeekableStream(Logical48MbImageLength, writable: false);

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => XeBuildImageRepairService.RepairAsync(image));

        Assert.Equal(ExitCode.InputOutput, failure.Code);
        Assert.Equal("unwritable-xebuild-image", failure.Kind);
    }

    [Fact]
    public async Task Honors_cancellation_before_reading_or_writing_the_output()
    {
        using var image = new SparseWritableSeekableStream(Logical48MbImageLength);
        byte[] firstPage = CreatePattern(NandPhysicalGeometry.LogicalPageSize);
        firstPage.AsSpan(KeyVaultSizeOffset, sizeof(uint)).Clear();
        firstPage.AsSpan(PatchSlotSizeOffset, sizeof(uint)).Clear();
        image.WriteAt(0, firstPage);
        image.ReadRanges.Clear();
        image.WriteRanges.Clear();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => XeBuildImageRepairService.RepairAsync(image, cancellationToken: cancellation.Token));

        Assert.Equal(firstPage, image.ReadAt(0, NandPhysicalGeometry.LogicalPageSize));
        Assert.Empty(image.ReadRanges);
        Assert.Empty(image.WriteRanges);
    }

    [Fact]
    public async Task Honors_cancellation_before_publishing_repaired_header_bytes()
    {
        using var image = new SparseWritableSeekableStream(Logical48MbImageLength);
        byte[] firstPage = CreatePattern(NandPhysicalGeometry.LogicalPageSize);
        firstPage.AsSpan(KeyVaultSizeOffset, sizeof(uint)).Clear();
        firstPage.AsSpan(PatchSlotSizeOffset, sizeof(uint)).Clear();
        image.WriteAt(0, firstPage);
        image.ReadRanges.Clear();
        image.WriteRanges.Clear();
        using var cancellation = new CancellationTokenSource();
        var progress = new CallbackProgress(operationProgress =>
        {
            if (operationProgress.Completed == 1)
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => XeBuildImageRepairService.RepairAsync(
                image,
                progress,
                cancellation.Token));

        Assert.Equal(firstPage, image.ReadAt(0, NandPhysicalGeometry.LogicalPageSize));
        StreamRange readRange = Assert.Single(image.ReadRanges);
        Assert.Equal(0, readRange.Offset);
        Assert.Equal(NandPhysicalGeometry.LogicalPageSize, readRange.Count);
        Assert.Empty(image.WriteRanges);
    }

    private static byte[] CreatePattern(int length)
    {
        var value = new byte[length];
        for (int index = 0; index < value.Length; index++)
        {
            value[index] = unchecked((byte)((index * 37) + 11));
        }

        return value;
    }

    private readonly record struct StreamRange(long Offset, int Count);

    private sealed class CallbackProgress : IProgress<OperationProgress>
    {
        private readonly Action<OperationProgress> callback;

        internal CallbackProgress(Action<OperationProgress> callback)
        {
            this.callback = callback;
        }

        public void Report(OperationProgress value) => callback(value);
    }

    private sealed class SparseWritableSeekableStream : Stream
    {
        private readonly Dictionary<long, byte> values = [];
        private readonly long length;
        private readonly long readableLength;
        private readonly bool writable;
        private long position;
        private bool disposed;

        internal SparseWritableSeekableStream(long length, bool writable = true, long? readableLength = null)
        {
            if (length <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            if (readableLength is { } requestedReadableLength &&
                (requestedReadableLength < 0 || requestedReadableLength > length))
            {
                throw new ArgumentOutOfRangeException(nameof(readableLength));
            }

            this.length = length;
            this.readableLength = readableLength ?? length;
            this.writable = writable;
        }

        internal List<StreamRange> ReadRanges { get; } = [];

        internal List<StreamRange> WriteRanges { get; } = [];

        public override bool CanRead => !disposed;

        public override bool CanSeek => !disposed;

        public override bool CanWrite => !disposed && writable;

        public override long Length
        {
            get
            {
                ThrowIfDisposed();
                return length;
            }
        }

        public override long Position
        {
            get
            {
                ThrowIfDisposed();
                return position;
            }

            set
            {
                ThrowIfDisposed();
                if (value < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                position = value;
            }
        }

        internal void WriteAt(long offset, ReadOnlySpan<byte> source)
        {
            ThrowIfDisposed();
            WriteStoredBytes(offset, source);
        }

        internal byte[] ReadAt(long offset, int count)
        {
            ThrowIfDisposed();
            EnsureRange(offset, count);
            var result = new byte[count];
            for (int index = 0; index < result.Length; index++)
            {
                if (values.TryGetValue(offset + index, out byte value))
                {
                    result[index] = value;
                }
            }

            return result;
        }

        public override void Flush()
        {
            ThrowIfDisposed();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            ThrowIfDisposed();
            long available = Math.Max(0, readableLength - position);
            int count = (int)Math.Min(buffer.Length, available);
            if (count == 0)
            {
                return 0;
            }

            long start = position;
            Span<byte> destination = buffer[..count];
            destination.Clear();
            for (int index = 0; index < destination.Length; index++)
            {
                if (values.TryGetValue(start + index, out byte value))
                {
                    destination[index] = value;
                }
            }

            position += count;
            ReadRanges.Add(new StreamRange(start, count));
            return count;
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

        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            Write(buffer.AsSpan(offset, count));
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ThrowIfDisposed();
            if (!writable)
            {
                throw new NotSupportedException();
            }

            EnsureRange(position, buffer.Length);
            long start = position;
            WriteStoredBytes(start, buffer);
            position += buffer.Length;
            WriteRanges.Add(new StreamRange(start, buffer.Length));
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            disposed = true;
            base.Dispose(disposing);
        }

        private void WriteStoredBytes(long offset, ReadOnlySpan<byte> source)
        {
            EnsureRange(offset, source.Length);
            for (int index = 0; index < source.Length; index++)
            {
                values[offset + index] = source[index];
            }
        }

        private void EnsureRange(long offset, int count)
        {
            if (offset < 0 || count < 0 || offset > length - count)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
        }
    }
}
