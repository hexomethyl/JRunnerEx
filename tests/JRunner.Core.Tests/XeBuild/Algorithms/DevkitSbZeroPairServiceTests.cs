using System.Buffers.Binary;
using System.Security.Cryptography;
using JRunner.Core.Contracts;
using JRunner.Core.Nand.Physical;
using JRunner.Core.Nand.Security;
using JRunner.Core.XeBuild.Algorithms;
using Xunit;

namespace JRunner.Core.Tests.XeBuild.Algorithms;

public sealed class DevkitSbZeroPairServiceTests
{
    private const int LogicalPageSize = NandPhysicalGeometry.LogicalPageSize;
    private const int PhysicalPageSize = NandPhysicalGeometry.PhysicalPageSize;
    private const int SbLogicalOffset = 0x1F8;
    private const int SbLength = 0x180;

    [Theory]
    [InlineData(0x1080000, true)]
    [InlineData(0x4200000, true)]
    [InlineData(0x3000000, false)]
    public async Task Zero_pair_supports_every_legacy_image_size_and_writes_only_complete_affected_pages(
        int imageLength,
        bool hasEcc)
    {
        using var nand = new SparseWritableStream(imageLength);
        byte[] decodedInput = CreateDecodedSb(SbLength);
        byte[] nonce = CreateBytes(0x10, 0x21);
        byte[] encryptedInput = BootloaderCrypto.EncryptCb(decodedInput, nonce);
        byte[] expectedDecoded = BootloaderCrypto.DecryptCb(encryptedInput).Output.ToArray();
        byte[]? originalSpare = null;
        try
        {
            SeedLogical(nand, hasEcc, 0x08, EncodeUInt32(SbLogicalOffset));
            SeedLogical(nand, hasEcc, SbLogicalOffset, encryptedInput);
            if (hasEcc)
            {
                originalSpare = CreateBytes(NandPhysicalGeometry.SpareSize, 0xA0);
                nand.Seed(PhysicalPageSize - NandPhysicalGeometry.SpareSize, originalSpare);
            }

            DevkitSbZeroPairResult result = await DevkitSbZeroPairService.ZeroPairAsync(nand);

            NandPhysicalFormat expectedFormat = hasEcc
                ? NandPhysicalFormat.InterleavedEcc
                : NandPhysicalFormat.Logical;
            int expectedByteLength = hasEcc ? 2 * PhysicalPageSize : 2 * LogicalPageSize;
            Assert.Equal(expectedFormat, result.ImageFormat);
            Assert.Equal(SbLogicalOffset, result.LogicalSbOffset);
            Assert.Equal(SbLength, result.SbLength);
            Assert.Equal(0, result.PhysicalStartOffset);
            Assert.Equal(expectedByteLength, result.AffectedPhysicalByteLength);
            Assert.Equal(0, nand.Position);

            WriteRecord write = Assert.Single(nand.Writes);
            Assert.Equal(0, write.Offset);
            Assert.Equal(expectedByteLength, write.Length);

            byte[] encryptedOutput = ReadLogical(nand, hasEcc, SbLogicalOffset, SbLength);
            byte[] actualDecoded = BootloaderCrypto.DecryptCb(encryptedOutput).Output.ToArray();
            try
            {
                Assert.Equal(expectedDecoded[..0x20], actualDecoded[..0x20]);
                Assert.Equal(new byte[0x20], actualDecoded[0x20..0x40]);
                Assert.Equal(expectedDecoded[0x40..], actualDecoded[0x40..]);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encryptedOutput);
                CryptographicOperations.ZeroMemory(actualDecoded);
            }

            if (hasEcc)
            {
                for (int pageIndex = 0; pageIndex < 2; pageIndex++)
                {
                    byte[] physicalPage = nand.ReadAt(pageIndex * PhysicalPageSize, PhysicalPageSize);
                    try
                    {
                        Assert.True(NandEccCodec.HasValidEcc(physicalPage));
                        if (pageIndex == 0)
                        {
                            byte[] expectedSpare = Assert.IsType<byte[]>(originalSpare);
                            Assert.Equal(expectedSpare.AsSpan(0, 0x0C).ToArray(), physicalPage.AsSpan(0x200, 0x0C).ToArray());
                            Assert.Equal(expectedSpare[0x0C] & 0x3F, physicalPage[0x20C] & 0x3F);
                        }
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(physicalPage);
                    }
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decodedInput);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(encryptedInput);
            CryptographicOperations.ZeroMemory(expectedDecoded);
            if (originalSpare is not null)
            {
                CryptographicOperations.ZeroMemory(originalSpare);
            }
        }
    }

    [Fact]
    public async Task Zero_pair_rejects_unsupported_image_sizes_before_writing()
    {
        using var nand = new SparseWritableStream(0x100000);

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => DevkitSbZeroPairService.ZeroPairAsync(nand));

        Assert.Equal(ExitCode.InvalidData, failure.Code);
        Assert.Equal("unsupported-devkit-sb-image", failure.Kind);
        Assert.Empty(nand.Writes);
        Assert.Equal(0, nand.Position);
    }

    [Fact]
    public async Task Zero_pair_rejects_an_out_of_range_declared_sb_before_writing()
    {
        const int logicalImageLength = 0x1000000;
        const int sbOffset = logicalImageLength - 0x10;
        using var nand = new SparseWritableStream(0x1080000);
        byte[] header = CreateDecodedSb(0x40);
        try
        {
            SeedLogical(nand, hasEcc: true, 0x08, EncodeUInt32(sbOffset));
            SeedLogical(nand, hasEcc: true, sbOffset, header.AsSpan(0, 0x10));

            OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
                () => DevkitSbZeroPairService.ZeroPairAsync(nand));

            Assert.Equal(ExitCode.InvalidData, failure.Code);
            Assert.Equal("invalid-devkit-sb-range", failure.Kind);
            Assert.Empty(nand.Writes);
            Assert.Equal(0, nand.Position);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(header);
        }
    }

    [Fact]
    public async Task Zero_pair_rejects_non_sb_magic_before_writing()
    {
        using var nand = new SparseWritableStream(0x3000000);
        byte[] header = CreateDecodedSb(0x40);
        try
        {
            header[0] = (byte)'C';
            header[1] = (byte)'B';
            SeedLogical(nand, hasEcc: false, 0x08, EncodeUInt32(0x200));
            SeedLogical(nand, hasEcc: false, 0x200, header);

            OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
                () => DevkitSbZeroPairService.ZeroPairAsync(nand));

            Assert.Equal(ExitCode.InvalidData, failure.Code);
            Assert.Equal("invalid-devkit-sb-magic", failure.Kind);
            Assert.Empty(nand.Writes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(header);
        }
    }

    [Fact]
    public async Task Zero_pair_honors_pre_cancellation_without_writing()
    {
        using var nand = new SparseWritableStream(0x3000000);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => DevkitSbZeroPairService.ZeroPairAsync(nand, cancellationToken: cancellation.Token));

        Assert.Empty(nand.Writes);
        Assert.Equal(0, nand.Position);
    }

    [Fact]
    public async Task Zero_pair_cancellation_after_preparation_does_not_write()
    {
        using var nand = new SparseWritableStream(0x3000000);
        using var cancellation = new CancellationTokenSource();
        byte[] decodedInput = CreateDecodedSb(SbLength);
        byte[] nonce = CreateBytes(0x10, 0x47);
        byte[] encryptedInput = BootloaderCrypto.EncryptCb(decodedInput, nonce);
        try
        {
            SeedLogical(nand, hasEcc: false, 0x08, EncodeUInt32(SbLogicalOffset));
            SeedLogical(nand, hasEcc: false, SbLogicalOffset, encryptedInput);

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                DevkitSbZeroPairService.ZeroPairAsync(
                    nand,
                    new CancelAtPreparedProgress(cancellation),
                    cancellation.Token));

            Assert.Empty(nand.Writes);
            Assert.Equal(0, nand.Position);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decodedInput);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(encryptedInput);
        }
    }

    private static byte[] CreateDecodedSb(int length)
    {
        byte[] stage = CreateBytes(length, 0x51);
        stage[0] = (byte)'S';
        stage[1] = (byte)'B';
        BinaryPrimitives.WriteUInt32BigEndian(stage.AsSpan(0x0C, sizeof(uint)), (uint)length);
        stage.AsSpan(0x20, 0x20).Fill(0xD7);
        return stage;
    }

    private static byte[] EncodeUInt32(int value)
    {
        byte[] bytes = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)value);
        return bytes;
    }

    private static byte[] CreateBytes(int length, byte seed)
    {
        var bytes = new byte[length];
        for (int index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)(seed + index);
        }

        return bytes;
    }

    private static void SeedLogical(SparseWritableStream stream, bool hasEcc, int logicalOffset, ReadOnlySpan<byte> source)
    {
        int copied = 0;
        int storedPageSize = hasEcc ? PhysicalPageSize : LogicalPageSize;
        while (copied < source.Length)
        {
            int currentLogicalOffset = checked(logicalOffset + copied);
            int pageOffset = currentLogicalOffset % LogicalPageSize;
            int copyLength = Math.Min(LogicalPageSize - pageOffset, source.Length - copied);
            long physicalOffset = ((long)(currentLogicalOffset / LogicalPageSize) * storedPageSize) + pageOffset;
            stream.Seed(physicalOffset, source.Slice(copied, copyLength));
            copied += copyLength;
        }
    }

    private static byte[] ReadLogical(SparseWritableStream stream, bool hasEcc, int logicalOffset, int length)
    {
        byte[] output = new byte[length];
        int copied = 0;
        int storedPageSize = hasEcc ? PhysicalPageSize : LogicalPageSize;
        while (copied < output.Length)
        {
            int currentLogicalOffset = checked(logicalOffset + copied);
            int pageOffset = currentLogicalOffset % LogicalPageSize;
            int copyLength = Math.Min(LogicalPageSize - pageOffset, output.Length - copied);
            long physicalOffset = ((long)(currentLogicalOffset / LogicalPageSize) * storedPageSize) + pageOffset;
            stream.CopyTo(physicalOffset, output.AsSpan(copied, copyLength));
            copied += copyLength;
        }

        return output;
    }

    private sealed class SparseWritableStream : Stream
    {
        private readonly Dictionary<long, byte> bytes = new();
        private readonly List<WriteRecord> writes = new();
        private readonly long length;
        private long position;

        public SparseWritableStream(long length)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(length);
            this.length = length;
        }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => true;

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

        public IReadOnlyList<WriteRecord> Writes => writes;

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            int count = (int)Math.Min(buffer.Length, length - position);
            CopyTo(position, buffer[..count]);
            position += count;
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

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            Write(buffer.AsSpan(offset, count));
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureWritableRange(position, buffer.Length);
            Seed(position, buffer);
            writes.Add(new WriteRecord(position, buffer.Length));
            position += buffer.Length;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public byte[] ReadAt(long offset, int count)
        {
            byte[] output = new byte[count];
            CopyTo(offset, output);
            return output;
        }

        public void Seed(long offset, ReadOnlySpan<byte> source)
        {
            EnsureWritableRange(offset, source.Length);
            for (int index = 0; index < source.Length; index++)
            {
                bytes[offset + index] = source[index];
            }
        }

        public void CopyTo(long offset, Span<byte> destination)
        {
            EnsureReadableRange(offset, destination.Length);
            destination.Clear();
            for (int index = 0; index < destination.Length; index++)
            {
                if (bytes.TryGetValue(offset + index, out byte value))
                {
                    destination[index] = value;
                }
            }
        }

        private void EnsureReadableRange(long offset, int count)
        {
            if (offset < 0 || count < 0 || offset > length || count > length - offset)
            {
                throw new EndOfStreamException();
            }
        }

        private void EnsureWritableRange(long offset, int count)
        {
            if (offset < 0 || count < 0 || offset > length || count > length - offset)
            {
                throw new IOException("The write exceeds the fixed NAND image stream.");
            }
        }
    }

    private sealed class CancelAtPreparedProgress : IProgress<OperationProgress>
    {
        private readonly CancellationTokenSource cancellation;

        public CancelAtPreparedProgress(CancellationTokenSource cancellation)
        {
            this.cancellation = cancellation;
        }

        public void Report(OperationProgress value)
        {
            if (value.Completed == value.Total)
            {
                cancellation.Cancel();
            }
        }
    }

    private sealed record WriteRecord(long Offset, int Length);
}
