using System.Globalization;
using JRunner.Core.Contracts;

namespace JRunner.Core.Nand.Security;

/// <summary>
/// Safe SMC version evidence decoded from a decrypted legacy SMC image.
/// </summary>
public sealed record SmcVersion
{
    internal SmcVersion(int motherboardType, byte major, byte minor)
    {
        MotherboardType = motherboardType;
        Major = major;
        Minor = minor;
    }

    /// <summary>
    /// Gets the legacy motherboard type nibble from decrypted SMC byte <c>0x100</c>.
    /// </summary>
    public int MotherboardType { get; }

    /// <summary>
    /// Gets the SMC major version at offset <c>0x101</c>.
    /// </summary>
    public byte Major { get; }

    /// <summary>
    /// Gets the SMC minor version at offset <c>0x102</c>.
    /// </summary>
    public byte Minor { get; }

    /// <summary>
    /// Gets the legacy display form, with a two-digit minor component.
    /// </summary>
    public string DisplayVersion => string.Format(CultureInfo.InvariantCulture, "{0}.{1:D2}", Major, Minor);
}

/// <summary>
/// Implements the reversible rolling stream transform used by legacy Xbox SMC binaries.
/// </summary>
public static class SmcCrypto
{
    private const int VersionMotherboardOffset = 0x100;
    private const int VersionMajorOffset = 0x101;
    private const int VersionMinorOffset = 0x102;
    private const int CancellationCheckIntervalMask = 0x0FFF;

    /// <summary>
    /// Decrypts a legacy SMC image without modifying the source bytes.
    /// </summary>
    /// <param name="encryptedSmc">The encrypted SMC bytes.</param>
    /// <param name="cancellationToken">Cancels a large transform before a result is returned.</param>
    /// <returns>A separately allocated decrypted SMC image.</returns>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static byte[] Decrypt(ReadOnlySpan<byte> encryptedSmc, CancellationToken cancellationToken = default)
    {
        return Transform(encryptedSmc, inputIsCiphertext: true, cancellationToken);
    }

    /// <summary>
    /// Encrypts a decrypted legacy SMC image without modifying the source bytes.
    /// </summary>
    /// <param name="decryptedSmc">The decrypted SMC bytes.</param>
    /// <param name="cancellationToken">Cancels a large transform before a result is returned.</param>
    /// <returns>A separately allocated encrypted SMC image.</returns>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static byte[] Encrypt(ReadOnlySpan<byte> decryptedSmc, CancellationToken cancellationToken = default)
    {
        return Transform(decryptedSmc, inputIsCiphertext: false, cancellationToken);
    }

    /// <summary>
    /// Attempts to extract SMC version evidence from a decrypted SMC image.
    /// </summary>
    /// <param name="decryptedSmc">The decrypted SMC bytes.</param>
    /// <param name="version">The decoded version on success; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> only when the three version bytes are present.</returns>
    public static bool TryGetVersion(ReadOnlySpan<byte> decryptedSmc, out SmcVersion? version)
    {
        if (decryptedSmc.Length <= VersionMinorOffset)
        {
            version = null;
            return false;
        }

        version = new SmcVersion(
            (decryptedSmc[VersionMotherboardOffset] >> 4) & 0x0F,
            decryptedSmc[VersionMajorOffset],
            decryptedSmc[VersionMinorOffset]);
        return true;
    }

    /// <summary>
    /// Extracts SMC version evidence from a decrypted SMC image.
    /// </summary>
    /// <param name="decryptedSmc">The decrypted SMC bytes.</param>
    /// <returns>The decoded version evidence.</returns>
    /// <exception cref="OperationFailureException">The SMC is too short to contain the legacy version bytes.</exception>
    public static SmcVersion GetVersion(ReadOnlySpan<byte> decryptedSmc)
    {
        if (TryGetVersion(decryptedSmc, out SmcVersion? version) && version is not null)
        {
            return version;
        }

        throw new OperationFailureException(
            ExitCode.InvalidData,
            "truncated-smc",
            "The SMC is too short to contain its version bytes.");
    }

    private static byte[] Transform(
        ReadOnlySpan<byte> source,
        bool inputIsCiphertext,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (source.IsEmpty)
        {
            return Array.Empty<byte>();
        }

        byte[] transformed = GC.AllocateUninitializedArray<byte>(source.Length);
        int[] keys = { 0x42, 0x75, 0x4E, 0x79 };
        bool completed = false;

        try
        {
            for (int index = 0; index < source.Length; index++)
            {
                if ((index & CancellationCheckIntervalMask) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                int keyIndex = index & 3;
                int cipherByte;
                if (inputIsCiphertext)
                {
                    cipherByte = source[index];
                }
                else
                {
                    cipherByte = source[index] ^ (keys[keyIndex] & byte.MaxValue);
                }

                transformed[index] = (byte)(source[index] ^ (keys[keyIndex] & byte.MaxValue));

                int mod = cipherByte * 0xFB;
                keys[(index + 1) & 3] = unchecked(keys[(index + 1) & 3] + mod);
                keys[(index + 2) & 3] = unchecked(keys[(index + 2) & 3] + (mod >> 8));
            }

            completed = true;
            return transformed;
        }
        finally
        {
            Array.Clear(keys, 0, keys.Length);
            if (!completed)
            {
                Array.Clear(transformed, 0, transformed.Length);
            }
        }
    }
}
