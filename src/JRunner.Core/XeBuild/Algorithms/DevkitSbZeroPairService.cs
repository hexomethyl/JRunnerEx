using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using JRunner.Core.Binary;
using JRunner.Core.Contracts;
using JRunner.Core.Nand.Physical;
using JRunner.Core.Nand.Security;

namespace JRunner.Core.XeBuild.Algorithms;

/// <summary>
/// Safe, secret-free metadata describing a completed Devkit SB zero-pairing operation.
/// </summary>
public sealed record DevkitSbZeroPairResult
{
    /// <summary>
    /// Creates completed Devkit SB zero-pairing metadata.
    /// </summary>
    public DevkitSbZeroPairResult(
        NandPhysicalFormat imageFormat,
        long logicalSbOffset,
        int sbLength,
        long physicalStartOffset,
        int affectedPhysicalByteLength)
    {
        if (imageFormat is not NandPhysicalFormat.Logical and not NandPhysicalFormat.InterleavedEcc)
        {
            throw new ArgumentOutOfRangeException(nameof(imageFormat), imageFormat, "The NAND image format is not supported.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(logicalSbOffset);
        if (sbLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sbLength), sbLength, "The SB length must be positive.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(physicalStartOffset);
        if (affectedPhysicalByteLength <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(affectedPhysicalByteLength),
                affectedPhysicalByteLength,
                "The affected physical byte length must be positive.");
        }

        ImageFormat = imageFormat;
        LogicalSbOffset = logicalSbOffset;
        SbLength = sbLength;
        PhysicalStartOffset = physicalStartOffset;
        AffectedPhysicalByteLength = affectedPhysicalByteLength;
    }

    /// <summary>
    /// Gets whether the source NAND carries interleaved spare/ECC data or only logical data.
    /// </summary>
    public NandPhysicalFormat ImageFormat { get; }

    /// <summary>
    /// Gets the SB's zero-based logical offset within the NAND image.
    /// </summary>
    public long LogicalSbOffset { get; }

    /// <summary>
    /// Gets the encrypted SB byte length declared by its big-endian header.
    /// </summary>
    public int SbLength { get; }

    /// <summary>
    /// Gets the zero-based physical byte offset at which the affected complete pages begin.
    /// </summary>
    public long PhysicalStartOffset { get; }

    /// <summary>
    /// Gets the number of complete physical bytes rewritten after all validation and transformation succeeds.
    /// </summary>
    public int AffectedPhysicalByteLength { get; }
}

/// <summary>
/// Zero-pairs the encrypted SB in a completed Devkit NAND output without filesystem or UI dependencies.
/// </summary>
/// <remarks>
/// The service accepts only the historical 16 MB/64 MB interleaved-ECC and 48 MB logical image sizes.
/// It reads and transforms every affected page before issuing its sole output write. The caller retains stream
/// ownership; a seekable stream is restored to its original position after either success or failure.
/// </remarks>
public static class DevkitSbZeroPairService
{
    private const int LogicalPageSize = NandPhysicalGeometry.LogicalPageSize;
    private const int PhysicalPageSize = NandPhysicalGeometry.PhysicalPageSize;
    private const int NandHeaderSbOffset = 0x08;
    private const int StageHeaderLength = 0x10;
    private const int StageLengthOffset = 0x0C;
    private const int BootloaderCryptoHeaderLength = 0x20;
    private const int ZeroPairEndOffset = 0x40;
    private const int MaximumSbLength = 0x1000000;
    private const int Physical16MbImageLength = 0x1080000;
    private const int Physical64MbImageLength = 0x4200000;
    private const int Logical16MbImageLength = 0x1000000;
    private const int Logical64MbImageLength = 0x4000000;
    private const int Logical48MbImageLength = 0x3000000;

    /// <summary>
    /// Zero-pairs the SB in the caller-owned writable NAND output stream.
    /// </summary>
    /// <param name="nandOutput">The readable, writable, seekable NAND output stream, read from its current position.</param>
    /// <param name="progress">Optional secret-free progress receiver.</param>
    /// <param name="cancellationToken">Cancels validation, reads, ECC work, and cryptographic transformation before commit.</param>
    /// <returns>Safe metadata for the complete page range written to the output stream.</returns>
    /// <exception cref="OperationFailureException">The image size, SB header, declared range, or encrypted stage is unsupported or malformed.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested before the completed page range was committed.</exception>
    public static async Task<DevkitSbZeroPairResult> ZeroPairAsync(
        Stream nandOutput,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nandOutput);
        EnsureReadableWritableSeekable(nandOutput);

        long imageStart = GetCurrentPosition(nandOutput);
        long imageLength = GetRemainingLength(nandOutput, imageStart);
        byte[]? stageHeader = null;
        byte[]? rawPages = null;
        byte[]? logicalPages = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImageShape imageShape = GetImageShape(imageLength);
            Report(progress, "zero-pairing-devkit-sb", "Validating the Devkit SB range.", completed: 0, total: 2);

            stageHeader = GC.AllocateUninitializedArray<byte>(StageHeaderLength);
            await ReadLogicalRangeAsync(
                nandOutput,
                imageStart,
                imageShape,
                NandHeaderSbOffset,
                stageHeader,
                cancellationToken,
                "NAND header").ConfigureAwait(false);
            long logicalSbOffset = BinaryPrimitives.ReadUInt32BigEndian(
                stageHeader.AsSpan(0, sizeof(uint)));

            if (!FitsWithinImage(logicalSbOffset, StageHeaderLength, imageShape.LogicalLength))
            {
                throw InvalidData(
                    "invalid-devkit-sb-range",
                    "The NAND header points outside the logical image for the Devkit SB header.");
            }

            await ReadLogicalRangeAsync(
                nandOutput,
                imageStart,
                imageShape,
                logicalSbOffset,
                stageHeader,
                cancellationToken,
                "Devkit SB header").ConfigureAwait(false);
            ValidateSbMagic(stageHeader);
            int sbLength = ReadAndValidateSbLength(stageHeader, logicalSbOffset, imageShape.LogicalLength);
            PageRange pageRange = GetPageRange(imageShape, logicalSbOffset, sbLength);

            rawPages = await ReadRangeAsync(
                nandOutput,
                checked(imageStart + pageRange.PhysicalStartOffset),
                pageRange.PhysicalByteLength,
                cancellationToken,
                "Devkit SB pages").ConfigureAwait(false);
            Report(progress, "zero-pairing-devkit-sb", "Decoding and zero-pairing the Devkit SB.", completed: 1, total: 2);

            if (imageShape.Format == NandPhysicalFormat.InterleavedEcc)
            {
                logicalPages = GC.AllocateUninitializedArray<byte>(pageRange.LogicalByteLength);
                NandEccCodec.RemoveEcc(rawPages, logicalPages, cancellationToken: cancellationToken);
            }
            else
            {
                logicalPages = rawPages;
            }

            TransformSbInPlace(logicalPages, pageRange.LogicalSbOffset, sbLength, cancellationToken);

            if (imageShape.Format == NandPhysicalFormat.InterleavedEcc)
            {
                RestorePhysicalPayloadAndEcc(rawPages, logicalPages, cancellationToken);
            }

            var result = new DevkitSbZeroPairResult(
                imageShape.Format,
                logicalSbOffset,
                sbLength,
                pageRange.PhysicalStartOffset,
                pageRange.PhysicalByteLength);
            Report(progress, "zero-pairing-devkit-sb", "Prepared Devkit SB pages for writing.", completed: 2, total: 2);
            cancellationToken.ThrowIfCancellationRequested();

            // Commit only after every validation and transformation step completed. A started write is not
            // cancellable because interruption could leave an arbitrary caller-owned stream partially updated.
            nandOutput.Position = checked(imageStart + pageRange.PhysicalStartOffset);
            await nandOutput.WriteAsync(rawPages, CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        finally
        {
            if (logicalPages is not null && !ReferenceEquals(logicalPages, rawPages))
            {
                CryptographicOperations.ZeroMemory(logicalPages);
            }

            if (rawPages is not null)
            {
                CryptographicOperations.ZeroMemory(rawPages);
            }

            if (stageHeader is not null)
            {
                CryptographicOperations.ZeroMemory(stageHeader);
            }

            RestorePosition(nandOutput, imageStart);
        }
    }

    private static void TransformSbInPlace(
        byte[] logicalPages,
        int logicalSbOffset,
        int expectedSbLength,
        CancellationToken cancellationToken)
    {
        byte[]? decryptedSb = null;
        byte[]? encryptedSb = null;
        try
        {
            Span<byte> encryptedStage = logicalPages.AsSpan(logicalSbOffset, expectedSbLength);
            ValidateSbMagic(encryptedStage);
            int actualSbLength = ReadSbLength(encryptedStage);
            if (actualSbLength != expectedSbLength)
            {
                throw InvalidData(
                    "invalid-devkit-sb-header",
                    "The Devkit SB header changed while its affected pages were being read.");
            }

            BootloaderDecryptionResult decryption = BootloaderCrypto.DecryptCb(encryptedStage, cancellationToken);
            decryptedSb = TakeOwnedBuffer(decryption.Output);
            decryptedSb.AsSpan(BootloaderCryptoHeaderLength, ZeroPairEndOffset - BootloaderCryptoHeaderLength).Clear();
            encryptedSb = BootloaderCrypto.EncryptCb(
                decryptedSb,
                encryptedStage.Slice(StageHeaderLength, XeCrypt.HmacSha1TagLength),
                cancellationToken);
            encryptedSb.CopyTo(encryptedStage);
        }
        finally
        {
            if (encryptedSb is not null)
            {
                CryptographicOperations.ZeroMemory(encryptedSb);
            }

            if (decryptedSb is not null)
            {
                CryptographicOperations.ZeroMemory(decryptedSb);
            }
        }
    }

    private static void RestorePhysicalPayloadAndEcc(
        byte[] rawPages,
        byte[] logicalPages,
        CancellationToken cancellationToken)
    {
        int pageCount = rawPages.Length / PhysicalPageSize;
        for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int logicalOffset = checked(pageIndex * LogicalPageSize);
            int physicalOffset = checked(pageIndex * PhysicalPageSize);
            logicalPages.AsSpan(logicalOffset, LogicalPageSize)
                .CopyTo(rawPages.AsSpan(physicalOffset, LogicalPageSize));
            NandEccCodec.CalculateEcc(rawPages.AsSpan(physicalOffset, PhysicalPageSize));
        }
    }

    private static ImageShape GetImageShape(long imageLength)
    {
        return imageLength switch
        {
            Physical16MbImageLength => new ImageShape(
                NandPhysicalFormat.InterleavedEcc,
                PhysicalPageSize,
                Logical16MbImageLength),
            Physical64MbImageLength => new ImageShape(
                NandPhysicalFormat.InterleavedEcc,
                PhysicalPageSize,
                Logical64MbImageLength),
            Logical48MbImageLength => new ImageShape(
                NandPhysicalFormat.Logical,
                LogicalPageSize,
                Logical48MbImageLength),
            _ => throw InvalidData(
                "unsupported-devkit-sb-image",
                "Devkit SB zero-pairing supports only 16 MB or 64 MB ECC NANDs and 48 MB logical NAND data."),
        };
    }

    private static int ReadAndValidateSbLength(ReadOnlySpan<byte> header, long logicalSbOffset, long logicalImageLength)
    {
        int sbLength = ReadSbLength(header);
        if (sbLength < ZeroPairEndOffset || sbLength > MaximumSbLength)
        {
            throw InvalidData(
                "invalid-devkit-sb-length",
                "The Devkit SB declares an unsupported length.");
        }

        if (!FitsWithinImage(logicalSbOffset, sbLength, logicalImageLength))
        {
            throw InvalidData(
                "invalid-devkit-sb-range",
                "The Devkit SB extends beyond the logical NAND image.");
        }

        return sbLength;
    }

    private static int ReadSbLength(ReadOnlySpan<byte> header)
    {
        uint length = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(StageLengthOffset, sizeof(uint)));
        if (length > int.MaxValue)
        {
            throw InvalidData(
                "invalid-devkit-sb-length",
                "The Devkit SB declares a length that exceeds the supported managed-buffer range.");
        }

        return (int)length;
    }

    private static void ValidateSbMagic(ReadOnlySpan<byte> header)
    {
        if (header.Length < StageHeaderLength || header[0] != (byte)'S' || header[1] != (byte)'B')
        {
            throw InvalidData(
                "invalid-devkit-sb-magic",
                "The NAND header does not point to an encrypted SB bootloader.");
        }
    }

    private static PageRange GetPageRange(ImageShape imageShape, long logicalSbOffset, int sbLength)
    {
        long firstPage = logicalSbOffset / LogicalPageSize;
        long logicalPageStart = checked(firstPage * LogicalPageSize);
        long sbEnd = checked(logicalSbOffset + sbLength);
        long pageCount = checked((sbEnd + (LogicalPageSize - 1)) / LogicalPageSize - firstPage);
        if (pageCount <= 0 || pageCount > int.MaxValue / imageShape.StoredPageSize)
        {
            throw InvalidData(
                "invalid-devkit-sb-range",
                "The Devkit SB page range exceeds the supported managed-buffer limit.");
        }

        int physicalByteLength = checked((int)pageCount * imageShape.StoredPageSize);
        int logicalByteLength = checked((int)pageCount * LogicalPageSize);
        return new PageRange(
            checked(firstPage * imageShape.StoredPageSize),
            physicalByteLength,
            logicalByteLength,
            checked((int)(logicalSbOffset - logicalPageStart)));
    }

    private static async Task ReadLogicalRangeAsync(
        Stream stream,
        long imageStart,
        ImageShape imageShape,
        long logicalOffset,
        Memory<byte> destination,
        CancellationToken cancellationToken,
        string rangeName)
    {
        if (!FitsWithinImage(logicalOffset, destination.Length, imageShape.LogicalLength))
        {
            throw InvalidData(
                "invalid-devkit-sb-range",
                $"The requested {rangeName} range is outside the logical NAND image.");
        }

        int destinationOffset = 0;
        while (destinationOffset < destination.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long currentLogicalOffset = checked(logicalOffset + destinationOffset);
            long pageIndex = currentLogicalOffset / LogicalPageSize;
            int offsetInPage = (int)(currentLogicalOffset % LogicalPageSize);
            int byteCount = Math.Min(LogicalPageSize - offsetInPage, destination.Length - destinationOffset);
            long physicalOffset = checked(
                imageStart + (pageIndex * imageShape.StoredPageSize) + offsetInPage);
            await ReadExactlyAsync(
                stream,
                physicalOffset,
                destination.Slice(destinationOffset, byteCount),
                cancellationToken,
                rangeName).ConfigureAwait(false);
            destinationOffset += byteCount;
        }
    }

    private static async Task<byte[]> ReadRangeAsync(
        Stream stream,
        long offset,
        int length,
        CancellationToken cancellationToken,
        string rangeName)
    {
        byte[] bytes = GC.AllocateUninitializedArray<byte>(length);
        try
        {
            await ReadExactlyAsync(stream, offset, bytes, cancellationToken, rangeName).ConfigureAwait(false);
            return bytes;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        long offset,
        Memory<byte> destination,
        CancellationToken cancellationToken,
        string rangeName)
    {
        stream.Position = offset;
        int copied = 0;
        while (copied < destination.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = await stream.ReadAsync(destination.Slice(copied), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw InvalidData(
                    "truncated-devkit-sb-image",
                    $"The NAND output ended while reading the {rangeName}.");
            }

            copied += read;
        }
    }

    private static bool FitsWithinImage(long offset, long length, long imageLength)
    {
        return offset >= 0 &&
            length >= 0 &&
            offset <= imageLength &&
            length <= imageLength - offset;
    }

    private static byte[] TakeOwnedBuffer(ReadOnlyMemory<byte> memory)
    {
        if (MemoryMarshal.TryGetArray(memory, out ArraySegment<byte> segment) &&
            segment.Array is { } array &&
            segment.Offset == 0 &&
            segment.Count == array.Length)
        {
            return array;
        }

        byte[] copy = memory.ToArray();
        if (MemoryMarshal.TryGetArray(memory, out segment) && segment.Array is not null)
        {
            CryptographicOperations.ZeroMemory(segment.AsSpan());
        }

        return copy;
    }

    private static void EnsureReadableWritableSeekable(Stream stream)
    {
        if (!stream.CanRead || !stream.CanWrite || !stream.CanSeek)
        {
            throw new ArgumentException(
                "Devkit SB zero-pairing requires a readable, writable, seekable NAND output stream.",
                nameof(stream));
        }
    }

    private static long GetCurrentPosition(Stream stream)
    {
        try
        {
            return stream.Position;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or ObjectDisposedException)
        {
            throw new ArgumentException("The NAND output stream must provide a stable position.", nameof(stream), exception);
        }
    }

    private static long GetRemainingLength(Stream stream, long position)
    {
        try
        {
            long length = stream.Length;
            if (position < 0 || length < position)
            {
                throw new ArgumentException("The NAND output stream has an invalid position or length.", nameof(stream));
            }

            return length - position;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or ObjectDisposedException)
        {
            throw new ArgumentException("The NAND output stream must provide a stable length.", nameof(stream), exception);
        }
    }

    private static void RestorePosition(Stream stream, long position)
    {
        try
        {
            stream.Position = position;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or ObjectDisposedException)
        {
            _ = exception;
        }
    }

    private static void Report(
        IProgress<OperationProgress>? progress,
        string kind,
        string message,
        int completed,
        int total)
    {
        progress?.Report(new OperationProgress(kind, message, completed: completed, total: total));
    }

    private static OperationFailureException InvalidData(string kind, string message)
    {
        return new OperationFailureException(ExitCode.InvalidData, kind, message);
    }

    private readonly record struct ImageShape(NandPhysicalFormat Format, int StoredPageSize, long LogicalLength);

    private readonly record struct PageRange(
        long PhysicalStartOffset,
        int PhysicalByteLength,
        int LogicalByteLength,
        int LogicalSbOffset);
}
