using System.Security.Cryptography;
using JRunner.Core.Nand.Models;

namespace JRunner.Core.XeBuild;

/// <summary>
/// Safe source-image facts bound to a full-content snapshot before XeBuild preparation.
/// </summary>
/// <remarks>
/// This context carries a filesystem path, inspection facts, and a private SHA-256 content snapshot.
/// The facts and digest must come from the same open source descriptor. Before reading source bytes, the
/// Linux CLI requires a regular file opened without following symbolic links, owned by root or its effective
/// UID, with no group/other write permission and trusted ancestry protected against renaming by another UID.
/// It rechecks the descriptor's identity, size, and nanosecond-precision change time after inspection and
/// complete hashing, before preparing a build. Root and the current effective UID are trusted; this is not
/// a defense against hostile same-UID or privileged processes. Callers, not this record, enforce that gate.
/// Staging repeats the safe descriptor checks and requires the complete source SHA-256 to match before Wine
/// runs. The digest covers the entire file, including the uninspected tail of a system-partition-only 4 GB
/// eMMC dump, even when that tail is not staged. The digest is not exposed in diagnostic text or JSON.
/// This context never retains source bytes, decrypted data, or CPU-key text.
/// </remarks>
public sealed record XeBuildSourceContext
{
    /// <summary>
    /// The exact byte length of a full 4 GB eMMC source dump.
    /// </summary>
    public const long FourGigabyteEmmcByteLength = 0xE0400000L;

    private readonly byte[] _contentSha256;

    /// <summary>
    /// Creates source context for one XeBuild operation.
    /// </summary>
    /// <param name="inputPath">The caller-owned source NAND or eMMC file path.</param>
    /// <param name="byteLength">The initial descriptor length, rechecked after inspection and complete hashing.</param>
    /// <param name="contentSha256">
    /// The 32-byte SHA-256 of the complete source file from the same admitted descriptor used for inspection.
    /// The descriptor must be rechecked for changes after hashing. The digest is defensively copied.
    /// </param>
    /// <param name="detectedConsoleId">The unambiguous console detected from inspection, when available.</param>
    /// <param name="supportsRgh1">Whether source bootloader evidence supports an RGH1 target.</param>
    public XeBuildSourceContext(
        string inputPath,
        long byteLength,
        ReadOnlyMemory<byte> contentSha256,
        ConsoleId? detectedConsoleId = null,
        bool supportsRgh1 = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        if (byteLength <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(byteLength),
                byteLength,
                "The XeBuild source length must be positive.");
        }

        if (contentSha256.Length != SHA256.HashSizeInBytes)
        {
            throw new ArgumentException(
                "The XeBuild source content SHA-256 must be exactly 32 bytes.",
                nameof(contentSha256));
        }

        if (detectedConsoleId is { } consoleId && !ConsoleCatalog.TryGet(consoleId, out _))
        {
            throw new ArgumentOutOfRangeException(
                nameof(detectedConsoleId),
                detectedConsoleId,
                "The detected console is not supported by XeBuild.");
        }

        _contentSha256 = contentSha256.ToArray();
        InputPath = inputPath;
        ByteLength = byteLength;
        DetectedConsoleId = detectedConsoleId;
        SupportsRgh1 = supportsRgh1;
    }

    /// <summary>
    /// Gets the caller-owned path to the source image.
    /// </summary>
    public string InputPath { get; }

    /// <summary>
    /// Gets the observed source-file byte length.
    /// </summary>
    public long ByteLength { get; }

    /// <summary>
    /// Gets the unambiguous console detected from source inspection, or <see langword="null"/> when inspection did not decide.
    /// </summary>
    public ConsoleId? DetectedConsoleId { get; }

    /// <summary>
    /// Gets whether source bootloader evidence establishes RGH1 compatibility.
    /// </summary>
    public bool SupportsRgh1 { get; }

    /// <summary>
    /// Gets whether this source has the full eMMC size that requires an explicit staging policy.
    /// </summary>
    public bool IsFourGigabyteEmmc => ByteLength == FourGigabyteEmmcByteLength;

    /// <summary>
    /// Compares a full-source SHA-256 with the inspected content snapshot without exposing the stored digest.
    /// </summary>
    /// <param name="contentSha256">
    /// The SHA-256 of the complete candidate source, including any uninspected 4 GB eMMC tail.
    /// </param>
    /// <returns>
    /// <see langword="true"/> only when all 32 digest bytes match. Other lengths return <see langword="false"/>.
    /// Equal-length comparisons do not exit early based on byte contents.
    /// </returns>
    public bool MatchesContentSha256(ReadOnlySpan<byte> contentSha256)
    {
        return CryptographicOperations.FixedTimeEquals(_contentSha256, contentSha256);
    }
}
