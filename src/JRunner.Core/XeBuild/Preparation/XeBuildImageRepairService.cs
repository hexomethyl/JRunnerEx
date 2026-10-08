using JRunner.Core.Binary;
using JRunner.Core.Contracts;
using JRunner.Core.Nand.Physical;

namespace JRunner.Core.XeBuild.Preparation;

/// <summary>
/// Safe metadata describing the header fields repaired in a completed XeBuild image.
/// </summary>
public sealed record XeBuildImageRepairResult
{
    /// <summary>
    /// Creates repair metadata.
    /// </summary>
    /// <param name="imageFormat">Whether the repaired image is logical eMMC data or interleaved physical NAND data.</param>
    /// <param name="imageByteLength">The complete byte length of the repaired image.</param>
    /// <param name="patchSlotSizeRepaired">Whether the zero-valued patch-slot-size field was populated.</param>
    /// <param name="keyVaultSizeRepaired">Whether the zero-valued key-vault-size field was populated.</param>
    public XeBuildImageRepairResult(
        NandPhysicalFormat imageFormat,
        long imageByteLength,
        bool patchSlotSizeRepaired,
        bool keyVaultSizeRepaired)
    {
        if (imageFormat is not NandPhysicalFormat.Logical and not NandPhysicalFormat.InterleavedEcc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(imageFormat),
                imageFormat,
                "The XeBuild image format is not supported.");
        }

        if (imageByteLength <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(imageByteLength),
                imageByteLength,
                "The XeBuild image length must be positive.");
        }

        ImageFormat = imageFormat;
        ImageByteLength = imageByteLength;
        PatchSlotSizeRepaired = patchSlotSizeRepaired;
        KeyVaultSizeRepaired = keyVaultSizeRepaired;
    }

    /// <summary>
    /// Gets whether the repaired image uses logical eMMC data or interleaved physical NAND pages.
    /// </summary>
    public NandPhysicalFormat ImageFormat { get; }

    /// <summary>
    /// Gets the complete byte length of the repaired image.
    /// </summary>
    public long ImageByteLength { get; }

    /// <summary>
    /// Gets whether logical header bytes <c>0x70..0x73</c> were changed from zero to <c>0x00010000</c>.
    /// </summary>
    public bool PatchSlotSizeRepaired { get; }

    /// <summary>
    /// Gets whether logical header bytes <c>0x60..0x63</c> were changed from zero to <c>0x00004000</c>.
    /// </summary>
    public bool KeyVaultSizeRepaired { get; }

    /// <summary>
    /// Gets whether this operation recalculated the first physical NAND page ECC.
    /// </summary>
    public bool FirstPageEccRecalculated => ImageFormat == NandPhysicalFormat.InterleavedEcc;
}

/// <summary>
/// Repairs the two legacy XeBuild header omissions in the first NAND page of a completed output image.
/// </summary>
/// <remarks>
/// This operation is intentionally stream-only and touches no bytes beyond the first physical page
/// for a physical image, or the first logical page for a 48 MB eMMC image. A supported physical
/// size establishes its interleaved page form; raw first-page spare metadata is retained in place
/// and only its ECC word is regenerated rather than rebuilding a legacy spare layout.
/// </remarks>
public static class XeBuildImageRepairService
{
    private const long Physical16MbImageLength = 0x1080000;
    private const long Physical64MbImageLength = 0x4200000;
    private const long Logical48MbImageLength = 0x3000000;
    private const int KeyVaultSizeOffset = 0x60;
    private const int PatchSlotSizeOffset = 0x70;
    private const uint KeyVaultSize = 0x00004000;
    private const uint PatchSlotSize = 0x00010000;

    /// <summary>
    /// Repairs a completed XeBuild output image in place.
    /// </summary>
    /// <param name="image">
    /// The caller-owned readable, writable, seekable XeBuild output stream. It remains open and its
    /// original position is restored when possible.
    /// </param>
    /// <param name="progress">Optional safe progress receiver.</param>
    /// <param name="cancellationToken">Cancels reads, repair, and writes before publication completes.</param>
    /// <returns>Safe metadata describing the fields populated by this repair.</returns>
    /// <exception cref="OperationFailureException">
    /// The stream is unusable, truncated, or has an unsupported XeBuild output size.
    /// </exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static async Task<XeBuildImageRepairResult> RepairAsync(
        Stream image,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ValidateStreamCapabilities(image);
        cancellationToken.ThrowIfCancellationRequested();

        long originalPosition = GetPosition(image);
        long imageLength = GetLength(image);
        if (originalPosition < 0 || imageLength < 0)
        {
            throw Failure(
                "invalid-xebuild-image-stream",
                "The XeBuild output stream has an invalid position or length.");
        }

        bool completed = false;
        try
        {
            ImageShape shape = GetImageShape(imageLength);
            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, completed: 0, total: 2);

            byte[] firstPage = GC.AllocateUninitializedArray<byte>(shape.FirstPageByteLength);
            await ReadExactlyAtAsync(
                image,
                offset: 0,
                firstPage,
                cancellationToken,
                "first XeBuild NAND page").ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            bool patchSlotSizeRepaired = IsAllZero(firstPage.AsSpan(PatchSlotSizeOffset, sizeof(uint)));
            bool keyVaultSizeRepaired = IsAllZero(firstPage.AsSpan(KeyVaultSizeOffset, sizeof(uint)));
            if (patchSlotSizeRepaired)
            {
                BigEndian.WriteUInt32(firstPage, PatchSlotSizeOffset, PatchSlotSize);
            }

            if (keyVaultSizeRepaired)
            {
                BigEndian.WriteUInt32(firstPage, KeyVaultSizeOffset, KeyVaultSize);
            }

            if (shape.ImageFormat == NandPhysicalFormat.InterleavedEcc)
            {
                cancellationToken.ThrowIfCancellationRequested();
                NandEccCodec.CalculateEcc(firstPage);
            }

            Report(progress, completed: 1, total: 2);
            cancellationToken.ThrowIfCancellationRequested();
            if (patchSlotSizeRepaired || keyVaultSizeRepaired || shape.ImageFormat == NandPhysicalFormat.InterleavedEcc)
            {
                await WriteAtAsync(image, offset: 0, firstPage, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, completed: 2, total: 2);
            XeBuildImageRepairResult result = new(
                shape.ImageFormat,
                imageLength,
                patchSlotSizeRepaired,
                keyVaultSizeRepaired);
            completed = true;
            return result;
        }
        finally
        {
            RestorePosition(image, originalPosition, completed);
        }
    }

    private static ImageShape GetImageShape(long imageLength) => imageLength switch
    {
        Physical16MbImageLength or Physical64MbImageLength => new(
            NandPhysicalFormat.InterleavedEcc,
            NandPhysicalGeometry.PhysicalPageSize),
        Logical48MbImageLength => new(
            NandPhysicalFormat.Logical,
            NandPhysicalGeometry.LogicalPageSize),
        _ => throw Failure(
            "unsupported-xebuild-image-size",
            "The XeBuild output must be a 16 MB or 64 MB interleaved-ECC image, or a 48 MB logical eMMC image."),
    };

    private static void ValidateStreamCapabilities(Stream image)
    {
        if (!image.CanRead)
        {
            throw InputOutputFailure(
                "unreadable-xebuild-image",
                "The XeBuild output stream must be readable to repair its header.");
        }

        if (!image.CanSeek)
        {
            throw InputOutputFailure(
                "unseekable-xebuild-image",
                "The XeBuild output stream must be seekable to repair its first NAND page.");
        }

        if (!image.CanWrite)
        {
            throw InputOutputFailure(
                "unwritable-xebuild-image",
                "The XeBuild output stream must be writable to repair its header.");
        }
    }

    private static async Task ReadExactlyAtAsync(
        Stream image,
        long offset,
        Memory<byte> destination,
        CancellationToken cancellationToken,
        string rangeName)
    {
        SetPosition(image, offset);
        int copied = 0;
        while (copied < destination.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read;
            try
            {
                read = await image.ReadAsync(destination[copied..], cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (IsStreamException(exception))
            {
                throw InputOutputFailure(
                    "xebuild-image-read-failed",
                    $"The XeBuild output stream could not read the required {rangeName}.");
            }

            if (read == 0)
            {
                throw Failure(
                    "truncated-xebuild-image",
                    $"The XeBuild output ended while reading the required {rangeName}.");
            }

            copied = checked(copied + read);
        }
    }

    private static async Task WriteAtAsync(
        Stream image,
        long offset,
        ReadOnlyMemory<byte> source,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetPosition(image, offset);
        try
        {
            await image.WriteAsync(source, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsStreamException(exception))
        {
            throw InputOutputFailure(
                "xebuild-image-write-failed",
                "The XeBuild output stream could not write the repaired first NAND page.");
        }
    }

    private static long GetPosition(Stream image)
    {
        try
        {
            return image.Position;
        }
        catch (Exception exception) when (IsStreamException(exception))
        {
            throw InputOutputFailure(
                "xebuild-image-position-failed",
                "The XeBuild output stream could not provide a stable position.");
        }
    }

    private static long GetLength(Stream image)
    {
        try
        {
            return image.Length;
        }
        catch (Exception exception) when (IsStreamException(exception))
        {
            throw InputOutputFailure(
                "xebuild-image-length-failed",
                "The XeBuild output stream could not provide a stable length.");
        }
    }

    private static void SetPosition(Stream image, long position)
    {
        try
        {
            image.Position = position;
        }
        catch (Exception exception) when (IsStreamException(exception))
        {
            throw InputOutputFailure(
                "xebuild-image-seek-failed",
                "The XeBuild output stream could not seek to the required NAND page.");
        }
    }

    private static void RestorePosition(Stream image, long originalPosition, bool completed)
    {
        try
        {
            image.Position = originalPosition;
        }
        catch (Exception exception) when (IsStreamException(exception))
        {
            if (completed)
            {
                throw InputOutputFailure(
                    "xebuild-image-position-restore-failed",
                    "The repaired XeBuild output stream could not restore its original position.");
            }
        }
    }

    private static bool IsAllZero(ReadOnlySpan<byte> value)
    {
        foreach (byte current in value)
        {
            if (current != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsStreamException(Exception exception) =>
        exception is IOException or NotSupportedException or ObjectDisposedException;

    private static OperationFailureException Failure(string kind, string message) =>
        new(ExitCode.InvalidData, kind, message);

    private static OperationFailureException InputOutputFailure(string kind, string message) =>
        new(ExitCode.InputOutput, kind, message);

    private static void Report(IProgress<OperationProgress>? progress, int completed, int total)
    {
        progress?.Report(new OperationProgress(
            "repairing-xebuild-image",
            "Repairing the first XeBuild NAND page.",
            completed: completed,
            total: total));
    }

    private readonly record struct ImageShape(NandPhysicalFormat ImageFormat, int FirstPageByteLength);
}
