using System.Security.Cryptography;

namespace JRunner.Core.Binary;

/// <summary>
/// Provides Xbox-compatible cryptographic primitives used by NAND and bootloader formats.
/// </summary>
public static class XeCrypt
{
    /// <summary>
    /// The number of bytes retained from an HMAC-SHA1 digest by Xbox binary formats.
    /// </summary>
    public const int HmacSha1TagLength = 16;

    private const int Sha1DigestLength = 20;

    /// <summary>
    /// Computes HMAC-SHA1 for one message and returns its first 16 bytes.
    /// </summary>
    /// <param name="key">The HMAC key.</param>
    /// <param name="message">The message to authenticate.</param>
    /// <returns>The first 16 bytes of the HMAC-SHA1 digest.</returns>
    public static byte[] HmacSha1Truncated(ReadOnlySpan<byte> key, ReadOnlySpan<byte> message)
    {
        byte[] tag = GC.AllocateUninitializedArray<byte>(HmacSha1TagLength);
        bool completed = false;
        try
        {
            HmacSha1Truncated(key, message, tag);
            completed = true;
            return tag;
        }
        finally
        {
            if (!completed)
            {
                CryptographicOperations.ZeroMemory(tag);
            }
        }
    }

    /// <summary>
    /// Computes HMAC-SHA1 across message segments and returns its first 16 bytes without
    /// concatenating those segments.
    /// </summary>
    /// <param name="key">The HMAC key.</param>
    /// <param name="segments">The message segments in authentication order.</param>
    /// <returns>The first 16 bytes of the HMAC-SHA1 digest.</returns>
    public static byte[] HmacSha1Truncated(ReadOnlySpan<byte> key, params ReadOnlyMemory<byte>[] segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        return HmacSha1Truncated(key, segments.AsSpan());
    }

    /// <summary>
    /// Computes HMAC-SHA1 across message segments and returns its first 16 bytes without
    /// concatenating those segments.
    /// </summary>
    /// <param name="key">The HMAC key.</param>
    /// <param name="segments">The message segments in authentication order.</param>
    /// <returns>The first 16 bytes of the HMAC-SHA1 digest.</returns>
    public static byte[] HmacSha1Truncated(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<ReadOnlyMemory<byte>> segments)
    {
        byte[] tag = GC.AllocateUninitializedArray<byte>(HmacSha1TagLength);
        bool completed = false;
        try
        {
            HmacSha1Truncated(key, segments, tag);
            completed = true;
            return tag;
        }
        finally
        {
            if (!completed)
            {
                CryptographicOperations.ZeroMemory(tag);
            }
        }
    }

    /// <summary>
    /// Computes HMAC-SHA1 for one message and writes exactly its first 16 bytes to
    /// <paramref name="destination"/>.
    /// </summary>
    /// <param name="key">The HMAC key.</param>
    /// <param name="message">The message to authenticate.</param>
    /// <param name="destination">An exactly 16-byte destination for the authentication tag.</param>
    public static void HmacSha1Truncated(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> message,
        Span<byte> destination)
    {
        ValidateDestination(destination);

        Span<byte> digest = stackalloc byte[Sha1DigestLength];
        try
        {
            int bytesWritten = HMACSHA1.HashData(key, message, digest);
            if (bytesWritten != Sha1DigestLength)
            {
                throw new CryptographicException("HMAC-SHA1 produced an unexpected digest length.");
            }

            digest[..HmacSha1TagLength].CopyTo(destination);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    /// <summary>
    /// Computes HMAC-SHA1 across message segments and writes exactly its first 16 bytes to
    /// <paramref name="destination"/> without concatenating those segments.
    /// </summary>
    /// <param name="key">The HMAC key.</param>
    /// <param name="segments">The message segments in authentication order.</param>
    /// <param name="destination">An exactly 16-byte destination for the authentication tag.</param>
    public static void HmacSha1Truncated(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<ReadOnlyMemory<byte>> segments,
        Span<byte> destination)
    {
        ValidateDestination(destination);

        if (segments.Length == 0)
        {
            HmacSha1Truncated(key, ReadOnlySpan<byte>.Empty, destination);
            return;
        }

        if (segments.Length == 1)
        {
            HmacSha1Truncated(key, segments[0].Span, destination);
            return;
        }

        byte[] keyCopy = key.ToArray();
        Span<byte> digest = stackalloc byte[Sha1DigestLength];

        try
        {
            using IncrementalHash hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, keyCopy);
            for (int index = 0; index < segments.Length; index++)
            {
                hmac.AppendData(segments[index].Span);
            }

            if (!hmac.TryGetHashAndReset(digest, out int bytesWritten) || bytesWritten != Sha1DigestLength)
            {
                throw new CryptographicException("HMAC-SHA1 produced an unexpected digest length.");
            }

            digest[..HmacSha1TagLength].CopyTo(destination);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyCopy);
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    /// <summary>
    /// Compares two byte sequences without an early exit based on their contents. Length mismatches
    /// return <see langword="false"/> immediately because tag lengths are not secret.
    /// </summary>
    /// <param name="left">The expected bytes.</param>
    /// <param name="right">The candidate bytes.</param>
    /// <returns><see langword="true"/> only when both sequences have the same bytes.</returns>
    public static bool FixedTimeEquals(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        return CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static void ValidateDestination(Span<byte> destination)
    {
        if (destination.Length != HmacSha1TagLength)
        {
            throw new ArgumentException(
                $"The HMAC-SHA1 destination must be exactly {HmacSha1TagLength} bytes.",
                nameof(destination));
        }
    }
}
