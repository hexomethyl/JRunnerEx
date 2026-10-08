using System.Security.Cryptography;

namespace JRunner.Core.Nand.Security;

/// <summary>
/// Immutable evidence from the legacy CPU-key Hamming and ECD checks.
/// </summary>
public sealed record CpuKeyValidationEvidence
{
    internal CpuKeyValidationEvidence(
        bool isZeroKey,
        int hammingWeight,
        bool hasExpectedHammingWeight,
        bool hasValidEcd,
        bool zeroPairedAccepted)
    {
        IsZeroKey = isZeroKey;
        HammingWeight = hammingWeight;
        HasExpectedHammingWeight = hasExpectedHammingWeight;
        HasValidEcd = hasValidEcd;
        ZeroPairedAccepted = zeroPairedAccepted;
    }

    /// <summary>
    /// Gets whether all 16 key bytes are zero.
    /// </summary>
    public bool IsZeroKey { get; }

    /// <summary>
    /// Gets the count of set bits among legacy CPU-key data bits.
    /// </summary>
    /// <remarks>
    /// This covers bytes 0 through 12 and bits 0 and 1 of byte 13, exactly as legacy J-Runner does.
    /// </remarks>
    public int HammingWeight { get; }

    /// <summary>
    /// Gets whether the legacy Hamming weight equals 53.
    /// </summary>
    public bool HasExpectedHammingWeight { get; }

    /// <summary>
    /// Gets whether the stored ECD parity bits match the legacy ECD calculation.
    /// </summary>
    public bool HasValidEcd { get; }

    /// <summary>
    /// Gets whether this all-zero key was accepted through the explicit zero-paired compatibility option.
    /// </summary>
    public bool ZeroPairedAccepted { get; }

    /// <summary>
    /// Gets whether the key would pass legacy <c>Nand.VerifyKey</c> under the selected zero-paired option.
    /// </summary>
    public bool IsLegacyValid => ZeroPairedAccepted || (HasExpectedHammingWeight && HasValidEcd);
}

/// <summary>
/// Implements the CPU-key ECD and Hamming evidence used by legacy J-Runner.
/// </summary>
public static class CpuKeyValidation
{
    private const int LegacyDataBitCount = 0x6A;
    private const int LegacyParityBitEndExclusive = 0x7F;
    private const int ExpectedHammingWeight = 53;
    private const uint EcdPolynomial = 0x360325;

    /// <summary>
    /// Calculates the 16-byte legacy ECD form of a CPU key.
    /// </summary>
    /// <param name="key">The parsed CPU key.</param>
    /// <returns>A newly allocated 16-byte ECD-normalized value.</returns>
    public static byte[] CalculateCpuKeyEcd(CpuKey key)
    {
        Span<byte> keyBytes = stackalloc byte[CpuKey.ByteLength];
        try
        {
            key.CopyTo(keyBytes);
            return CalculateCpuKeyEcd(keyBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }

    /// <summary>
    /// Calculates the 16-byte legacy ECD form of CPU-key bytes.
    /// </summary>
    /// <param name="key">Exactly 16 CPU-key bytes.</param>
    /// <returns>A newly allocated 16-byte ECD-normalized value.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is not exactly 16 bytes.</exception>
    public static byte[] CalculateCpuKeyEcd(ReadOnlySpan<byte> key)
    {
        ValidateKeyLength(key);

        byte[] ecd = key.ToArray();
        uint accumulatorOne = 0;
        uint accumulatorTwo = 0;

        for (int bitIndex = 0; bitIndex < CpuKey.ByteLength * 8; bitIndex++, accumulatorOne >>= 1)
        {
            int byteIndex = bitIndex >> 3;
            int byteBitIndex = bitIndex & 7;
            byte currentByte = ecd[byteIndex];
            uint currentBit = (uint)((currentByte >> byteBitIndex) & 1);

            if (bitIndex < LegacyDataBitCount)
            {
                accumulatorOne ^= currentBit;
                if ((accumulatorOne & 1) != 0)
                {
                    accumulatorOne ^= EcdPolynomial;
                }

                accumulatorTwo ^= currentBit;
            }
            else if (bitIndex < LegacyParityBitEndExclusive)
            {
                uint expectedBit = accumulatorOne & 1;
                if (currentBit != expectedBit)
                {
                    ecd[byteIndex] = (byte)(currentByte ^ (1 << byteBitIndex));
                }

                accumulatorTwo ^= expectedBit;
            }
            else if (currentBit != accumulatorTwo)
            {
                ecd[byteIndex] = (byte)(currentByte ^ 0x80);
            }
        }

        return ecd;
    }

    /// <summary>
    /// Inspects legacy CPU-key validity without using it as a parsing gate.
    /// </summary>
    /// <param name="key">The parsed CPU key.</param>
    /// <param name="allowZeroPaired">Whether to apply legacy zero-paired acceptance.</param>
    /// <returns>Independent Hamming and ECD evidence.</returns>
    public static CpuKeyValidationEvidence Inspect(CpuKey key, bool allowZeroPaired = false)
    {
        Span<byte> keyBytes = stackalloc byte[CpuKey.ByteLength];
        try
        {
            key.CopyTo(keyBytes);
            return Inspect(keyBytes, allowZeroPaired);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }

    /// <summary>
    /// Inspects legacy CPU-key validity without using it as a parsing gate.
    /// </summary>
    /// <param name="key">Exactly 16 CPU-key bytes.</param>
    /// <param name="allowZeroPaired">Whether to apply legacy zero-paired acceptance.</param>
    /// <returns>Independent Hamming and ECD evidence.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is not exactly 16 bytes.</exception>
    public static CpuKeyValidationEvidence Inspect(ReadOnlySpan<byte> key, bool allowZeroPaired = false)
    {
        ValidateKeyLength(key);

        bool isZeroKey = IsAllZero(key);
        int hammingWeight = CalculateLegacyHammingWeight(key);
        bool hasExpectedHammingWeight = hammingWeight == ExpectedHammingWeight;

        byte[] calculatedEcd = CalculateCpuKeyEcd(key);
        try
        {
            bool hasValidEcd = CryptographicOperations.FixedTimeEquals(key, calculatedEcd);
            bool zeroPairedAccepted = allowZeroPaired && isZeroKey;
            return new CpuKeyValidationEvidence(
                isZeroKey,
                hammingWeight,
                hasExpectedHammingWeight,
                hasValidEcd,
                zeroPairedAccepted);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(calculatedEcd);
        }
    }

    /// <summary>
    /// Determines whether a key would pass legacy <c>Nand.VerifyKey</c> under the selected zero-paired option.
    /// </summary>
    /// <param name="key">The parsed CPU key.</param>
    /// <param name="allowZeroPaired">Whether to apply legacy zero-paired acceptance.</param>
    /// <returns><see langword="true"/> only when legacy validation succeeds.</returns>
    public static bool VerifyLegacy(CpuKey key, bool allowZeroPaired = false)
    {
        return Inspect(key, allowZeroPaired).IsLegacyValid;
    }

    /// <summary>
    /// Determines whether raw bytes would pass legacy <c>Nand.VerifyKey</c> under the selected zero-paired option.
    /// </summary>
    /// <param name="key">Exactly 16 CPU-key bytes.</param>
    /// <param name="allowZeroPaired">Whether to apply legacy zero-paired acceptance.</param>
    /// <returns><see langword="true"/> only when legacy validation succeeds.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is not exactly 16 bytes.</exception>
    public static bool VerifyLegacy(ReadOnlySpan<byte> key, bool allowZeroPaired = false)
    {
        return Inspect(key, allowZeroPaired).IsLegacyValid;
    }

    private static int CalculateLegacyHammingWeight(ReadOnlySpan<byte> key)
    {
        int hammingWeight = 0;

        for (int index = 0; index < 13; index++)
        {
            hammingWeight += CountSetBits(key[index]);
        }

        hammingWeight += key[13] & 1;
        hammingWeight += (key[13] >> 1) & 1;
        return hammingWeight;
    }

    private static int CountSetBits(byte value)
    {
        int count = 0;
        int remaining = value;
        while (remaining != 0)
        {
            count += remaining & 1;
            remaining >>= 1;
        }

        return count;
    }

    private static bool IsAllZero(ReadOnlySpan<byte> bytes)
    {
        byte aggregate = 0;
        foreach (byte value in bytes)
        {
            aggregate |= value;
        }

        return aggregate == 0;
    }

    private static void ValidateKeyLength(ReadOnlySpan<byte> key)
    {
        if (key.Length != CpuKey.ByteLength)
        {
            throw new ArgumentException("A CPU key must be exactly 16 bytes.", nameof(key));
        }
    }
}
