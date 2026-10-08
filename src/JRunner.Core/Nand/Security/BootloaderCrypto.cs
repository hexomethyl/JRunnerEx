using System.Security.Cryptography;
using JRunner.Core.Binary;
using JRunner.Core.Contracts;

namespace JRunner.Core.Nand.Security;

/// <summary>
/// Identifies the legacy HMAC-RC4 path used to decrypt a bootloader stage.
/// </summary>
public enum BootloaderDecryptionPath
{
    /// <summary>
    /// CB or SB decrypted with the retail 1BL key.
    /// </summary>
    Cb,

    /// <summary>
    /// DD1 S2 decrypted with the zero 1BL key.
    /// </summary>
    S2,

    /// <summary>
    /// CB_B decrypted with the supplied CPU key.
    /// </summary>
    CbWithCpuKey,

    /// <summary>
    /// CB_B decrypted through the manufacturing zero-key branch.
    /// </summary>
    CbWithManufacturingZeroKey,

    /// <summary>
    /// SC decrypted with the chained zero key.
    /// </summary>
    Sc,

    /// <summary>
    /// CD decrypted with the preceding stage's derived key.
    /// </summary>
    Cd,

    /// <summary>
    /// CD decrypted with the CPU-key retry path after the legacy zero-range heuristic failed.
    /// </summary>
    CdWithCpuKeyFallback,

    /// <summary>
    /// CE decrypted with the decoded CD derived key.
    /// </summary>
    Ce,
}

/// <summary>
/// Immutable result from a legacy bootloader decryption step.
/// </summary>
public sealed record BootloaderDecryptionResult
{
    internal BootloaderDecryptionResult(
        ReadOnlyMemory<byte> output,
        BootloaderDecryptionPath path,
        bool usesNewCbCrypto,
        bool hasLegacyCdZeroRangeEvidence)
    {
        Output = output;
        Path = path;
        UsesNewCbCrypto = usesNewCbCrypto;
        HasLegacyCdZeroRangeEvidence = hasLegacyCdZeroRangeEvidence;
    }

    /// <summary>
    /// Gets the decoded stage. The returned layout preserves the first 16 input bytes, stores the derived RC4 key at <c>0x10</c>, and stores decoded payload bytes at <c>0x20</c>.
    /// </summary>
    public ReadOnlyMemory<byte> Output { get; }

    /// <summary>
    /// Gets the legacy path used for decryption.
    /// </summary>
    public BootloaderDecryptionPath Path { get; }

    /// <summary>
    /// Gets whether CB_B used the CB_A new-crypto derivation branch.
    /// </summary>
    public bool UsesNewCbCrypto { get; }

    /// <summary>
    /// Gets whether the final CD-family payload contains either legacy four-byte zero heuristic range.
    /// </summary>
    /// <remarks>
    /// This evidence is relevant to CD and CE results. It is <see langword="false"/> for other stages.
    /// </remarks>
    public bool HasLegacyCdZeroRangeEvidence { get; }
}

/// <summary>
/// Implements bounded, pure legacy HMAC-SHA1 and RC4 bootloader cryptography helpers.
/// </summary>
public static class BootloaderCrypto
{
    private const int HeaderLength = 0x10;
    private const int DerivedKeyOffset = HeaderLength;
    private const int PayloadOffset = HeaderLength + XeCrypt.HmacSha1TagLength;
    private const int MinimumStageLength = PayloadOffset;
    private const int LegacyCdFirstZeroOffset = 0x20;
    private const int LegacyCdSecondZeroOffset = 0x210;
    private const int LegacyCdZeroLength = 0x04;

    private static readonly byte[] FirstBootLoaderKey =
    {
        0xDD, 0x88, 0xAD, 0x0C, 0x9E, 0xD6, 0x69, 0xE7,
        0xB5, 0x67, 0x94, 0xFB, 0x68, 0x56, 0x3E, 0xFA,
    };

    /// <summary>
    /// Decrypts a CB or SB stage with the legacy retail 1BL key.
    /// </summary>
    /// <param name="encryptedCb">The encrypted CB or SB stage.</param>
    /// <param name="cancellationToken">Cancels RC4 processing before a result is returned.</param>
    /// <returns>The legacy decoded stage layout.</returns>
    public static BootloaderDecryptionResult DecryptCb(
        ReadOnlySpan<byte> encryptedCb,
        CancellationToken cancellationToken = default)
    {
        ValidateStage(encryptedCb, "CB");
        byte[] output = DecryptWithHmac(
            encryptedCb,
            FirstBootLoaderKey,
            encryptedCb.Slice(DerivedKeyOffset, XeCrypt.HmacSha1TagLength),
            cancellationToken);
        return new BootloaderDecryptionResult(output, BootloaderDecryptionPath.Cb, false, false);
    }

    /// <summary>
    /// Encrypts a decoded CB or SB stage with the legacy retail 1BL key and caller-supplied nonce.
    /// </summary>
    /// <remarks>
    /// The decoded layout is the layout returned by <see cref="DecryptCb"/>:
    /// its first <c>0x10</c> bytes are the stage header, bytes <c>0x10</c> through <c>0x1F</c>
    /// contain a derived RC4 key, and bytes from <c>0x20</c> onward are plaintext payload.
    /// The returned encrypted stage replaces the derived key with <paramref name="nonce"/>.
    /// </remarks>
    /// <param name="decodedCb">The decoded CB or SB stage in legacy decoded layout.</param>
    /// <param name="nonce">The original 16-byte encrypted-stage nonce.</param>
    /// <param name="cancellationToken">Cancels RC4 processing before a result is returned.</param>
    /// <returns>A newly allocated encrypted CB or SB stage.</returns>
    public static byte[] EncryptCb(
        ReadOnlySpan<byte> decodedCb,
        ReadOnlySpan<byte> nonce,
        CancellationToken cancellationToken = default)
    {
        ValidateStage(decodedCb, "CB/SB");
        if (nonce.Length != XeCrypt.HmacSha1TagLength)
        {
            throw new OperationFailureException(
                ExitCode.InvalidData,
                "invalid-bootloader-nonce",
                "The CB/SB bootloader nonce must be exactly 0x10 bytes.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        Span<byte> rc4Key = stackalloc byte[XeCrypt.HmacSha1TagLength];
        byte[] output = GC.AllocateUninitializedArray<byte>(decodedCb.Length);
        bool completed = false;
        try
        {
            XeCrypt.HmacSha1Truncated(FirstBootLoaderKey, nonce, rc4Key);
            decodedCb[..HeaderLength].CopyTo(output);
            nonce.CopyTo(output.AsSpan(DerivedKeyOffset, XeCrypt.HmacSha1TagLength));
            decodedCb[PayloadOffset..].CopyTo(output.AsSpan(PayloadOffset));
            Rc4.TransformInPlace(rc4Key, output.AsSpan(PayloadOffset), cancellationToken);

            completed = true;
            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rc4Key);
            if (!completed)
            {
                CryptographicOperations.ZeroMemory(output);
            }
        }
    }

    /// <summary>
    /// Decrypts a DD1 S2 stage with the legacy zero 1BL key.
    /// </summary>
    /// <param name="encryptedS2">The encrypted S2 stage.</param>
    /// <param name="cancellationToken">Cancels RC4 processing before a result is returned.</param>
    /// <returns>The legacy decoded stage layout.</returns>
    public static BootloaderDecryptionResult DecryptS2(
        ReadOnlySpan<byte> encryptedS2,
        CancellationToken cancellationToken = default)
    {
        ValidateStage(encryptedS2, "S2");
        Span<byte> zeroKey = stackalloc byte[CpuKey.ByteLength];
        zeroKey.Clear();
        try
        {
            byte[] output = DecryptWithHmac(
                encryptedS2,
                zeroKey,
                encryptedS2.Slice(DerivedKeyOffset, XeCrypt.HmacSha1TagLength),
                cancellationToken);
            return new BootloaderDecryptionResult(output, BootloaderDecryptionPath.S2, false, false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(zeroKey);
        }
    }

    /// <summary>
    /// Decrypts a CB_B stage using its decoded CB_A and a CPU key, including manufacturing and new-crypto branches.
    /// </summary>
    /// <param name="encryptedCbB">The encrypted CB_B stage.</param>
    /// <param name="decryptedCbA">The decoded CB_A stage in legacy decoded layout.</param>
    /// <param name="cpuKey">The optional parsed CPU key. It is required unless the manufacturing zero-key branch applies.</param>
    /// <param name="cancellationToken">Cancels RC4 processing before a result is returned.</param>
    /// <returns>The legacy decoded CB_B stage layout and selected derivation evidence.</returns>
    public static BootloaderDecryptionResult DecryptCbWithCpuKey(
        ReadOnlySpan<byte> encryptedCbB,
        ReadOnlySpan<byte> decryptedCbA,
        CpuKey? cpuKey,
        CancellationToken cancellationToken = default)
    {
        ValidateStage(encryptedCbB, "CB_B");
        ValidateStage(decryptedCbA, "CB_A");
        cancellationToken.ThrowIfCancellationRequested();

        bool usesManufacturingZeroKey = decryptedCbA[0x07] != 0;
        bool usesNewCrypto = (((ushort)(decryptedCbA[0x06] << 8 | decryptedCbA[0x07])) & 0x1000) != 0;
        int messageLength = usesNewCrypto ? 0x30 : 0x20;
        Span<byte> message = stackalloc byte[0x30];
        Span<byte> keyBytes = stackalloc byte[CpuKey.ByteLength];
        Span<byte> rc4Key = stackalloc byte[XeCrypt.HmacSha1TagLength];

        try
        {
            encryptedCbB.Slice(DerivedKeyOffset, XeCrypt.HmacSha1TagLength).CopyTo(message);
            if (usesManufacturingZeroKey)
            {
                message.Slice(CpuKey.ByteLength, CpuKey.ByteLength).Clear();
            }
            else
            {
                if (!cpuKey.HasValue)
                {
                    throw new OperationFailureException(
                        ExitCode.InvalidData,
                        "invalid-cpu-key",
                        "A parsed CPU key is required for non-manufacturing CB_B decryption.");
                }

                EnsureInitializedCpuKey(cpuKey.Value);
                cpuKey.Value.CopyTo(keyBytes);
                keyBytes.CopyTo(message.Slice(CpuKey.ByteLength, CpuKey.ByteLength));
            }

            if (usesNewCrypto)
            {
                decryptedCbA[..HeaderLength].CopyTo(message.Slice(0x20, HeaderLength));
                message[0x26] = 0;
                message[0x27] = 0;
            }

            XeCrypt.HmacSha1Truncated(
                decryptedCbA.Slice(DerivedKeyOffset, XeCrypt.HmacSha1TagLength),
                message[..messageLength],
                rc4Key);
            byte[] output = DecryptWithRc4Key(encryptedCbB, rc4Key, cancellationToken);
            return new BootloaderDecryptionResult(
                output,
                usesManufacturingZeroKey
                    ? BootloaderDecryptionPath.CbWithManufacturingZeroKey
                    : BootloaderDecryptionPath.CbWithCpuKey,
                usesNewCrypto,
                false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(message);
            CryptographicOperations.ZeroMemory(keyBytes);
            CryptographicOperations.ZeroMemory(rc4Key);
        }
    }

    /// <summary>
    /// Decrypts SC using the legacy all-zero chained key.
    /// </summary>
    /// <param name="encryptedSc">The encrypted SC stage.</param>
    /// <param name="cancellationToken">Cancels RC4 processing before a result is returned.</param>
    /// <returns>The legacy decoded SC stage layout.</returns>
    public static BootloaderDecryptionResult DecryptSc(
        ReadOnlySpan<byte> encryptedSc,
        CancellationToken cancellationToken = default)
    {
        ValidateStage(encryptedSc, "SC");
        Span<byte> zeroKey = stackalloc byte[CpuKey.ByteLength];
        zeroKey.Clear();
        try
        {
            byte[] output = DecryptWithHmac(
                encryptedSc,
                zeroKey,
                encryptedSc.Slice(DerivedKeyOffset, XeCrypt.HmacSha1TagLength),
                cancellationToken);
            return new BootloaderDecryptionResult(output, BootloaderDecryptionPath.Sc, false, false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(zeroKey);
        }
    }

    /// <summary>
    /// Decrypts CD with the derived key stored in its decoded preceding stage.
    /// </summary>
    /// <param name="encryptedCd">The encrypted CD stage.</param>
    /// <param name="decryptedPreviousStage">A decoded CB_B, SC, or SD stage in legacy decoded layout.</param>
    /// <param name="cancellationToken">Cancels RC4 processing before a result is returned.</param>
    /// <returns>The legacy decoded CD stage layout and zero-range evidence.</returns>
    public static BootloaderDecryptionResult DecryptCd(
        ReadOnlySpan<byte> encryptedCd,
        ReadOnlySpan<byte> decryptedPreviousStage,
        CancellationToken cancellationToken = default)
    {
        ValidateStage(encryptedCd, "CD");
        ValidateStage(decryptedPreviousStage, "preceding");

        byte[] output = DecryptWithHmac(
            encryptedCd,
            decryptedPreviousStage.Slice(DerivedKeyOffset, XeCrypt.HmacSha1TagLength),
            encryptedCd.Slice(DerivedKeyOffset, XeCrypt.HmacSha1TagLength),
            cancellationToken);
        return new BootloaderDecryptionResult(
            output,
            BootloaderDecryptionPath.Cd,
            false,
            HasLegacyCdZeroRangeEvidence(output.AsSpan(PayloadOffset)));
    }

    /// <summary>
    /// Decrypts CD with the normal chained key, then applies the legacy CPU-key retry when its zero-range heuristic fails.
    /// </summary>
    /// <param name="encryptedCd">The encrypted CD stage.</param>
    /// <param name="decryptedPreviousStage">A decoded CB_B stage in legacy decoded layout.</param>
    /// <param name="cpuKey">The CPU key used by the optional retry.</param>
    /// <param name="cancellationToken">Cancels RC4 processing before a result is returned.</param>
    /// <returns>The legacy decoded CD stage layout and selected decryption path.</returns>
    public static BootloaderDecryptionResult DecryptCdWithCpuKey(
        ReadOnlySpan<byte> encryptedCd,
        ReadOnlySpan<byte> decryptedPreviousStage,
        CpuKey cpuKey,
        CancellationToken cancellationToken = default)
    {
        ValidateStage(encryptedCd, "CD");
        ValidateStage(decryptedPreviousStage, "preceding");
        EnsureInitializedCpuKey(cpuKey);

        byte[] primaryOutput = DecryptWithHmac(
            encryptedCd,
            decryptedPreviousStage.Slice(DerivedKeyOffset, XeCrypt.HmacSha1TagLength),
            encryptedCd.Slice(DerivedKeyOffset, XeCrypt.HmacSha1TagLength),
            cancellationToken);
        bool primaryHasEvidence = HasLegacyCdZeroRangeEvidence(primaryOutput.AsSpan(PayloadOffset));
        if (primaryHasEvidence)
        {
            return new BootloaderDecryptionResult(primaryOutput, BootloaderDecryptionPath.Cd, false, true);
        }

        Span<byte> keyBytes = stackalloc byte[CpuKey.ByteLength];
        Span<byte> fallbackRc4Key = stackalloc byte[XeCrypt.HmacSha1TagLength];
        try
        {
            cpuKey.CopyTo(keyBytes);
            XeCrypt.HmacSha1Truncated(
                keyBytes,
                primaryOutput.AsSpan(DerivedKeyOffset, XeCrypt.HmacSha1TagLength),
                fallbackRc4Key);
            byte[] fallbackOutput = DecryptWithRc4Key(encryptedCd, fallbackRc4Key, cancellationToken);
            return new BootloaderDecryptionResult(
                fallbackOutput,
                BootloaderDecryptionPath.CdWithCpuKeyFallback,
                false,
                HasLegacyCdZeroRangeEvidence(fallbackOutput.AsSpan(PayloadOffset)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(primaryOutput);
            CryptographicOperations.ZeroMemory(keyBytes);
            CryptographicOperations.ZeroMemory(fallbackRc4Key);
        }
    }

    /// <summary>
    /// Decrypts CE using the derived key stored in decoded CD.
    /// </summary>
    /// <param name="encryptedCe">The encrypted CE stage.</param>
    /// <param name="decryptedCd">The decoded CD stage in legacy decoded layout.</param>
    /// <param name="cancellationToken">Cancels RC4 processing before a result is returned.</param>
    /// <returns>The legacy decoded CE stage layout and zero-range evidence.</returns>
    public static BootloaderDecryptionResult DecryptCe(
        ReadOnlySpan<byte> encryptedCe,
        ReadOnlySpan<byte> decryptedCd,
        CancellationToken cancellationToken = default)
    {
        ValidateStage(encryptedCe, "CE");
        ValidateStage(decryptedCd, "CD");

        byte[] output = DecryptWithHmac(
            encryptedCe,
            decryptedCd.Slice(DerivedKeyOffset, XeCrypt.HmacSha1TagLength),
            encryptedCe.Slice(DerivedKeyOffset, XeCrypt.HmacSha1TagLength),
            cancellationToken);
        return new BootloaderDecryptionResult(
            output,
            BootloaderDecryptionPath.Ce,
            false,
            HasLegacyCdZeroRangeEvidence(output.AsSpan(PayloadOffset)));
    }

    private static byte[] DecryptWithHmac(
        ReadOnlySpan<byte> encryptedStage,
        ReadOnlySpan<byte> secret,
        ReadOnlySpan<byte> message,
        CancellationToken cancellationToken)
    {
        Span<byte> rc4Key = stackalloc byte[XeCrypt.HmacSha1TagLength];
        try
        {
            XeCrypt.HmacSha1Truncated(secret, message, rc4Key);
            return DecryptWithRc4Key(encryptedStage, rc4Key, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rc4Key);
        }
    }

    private static byte[] DecryptWithRc4Key(
        ReadOnlySpan<byte> encryptedStage,
        ReadOnlySpan<byte> rc4Key,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        byte[] output = GC.AllocateUninitializedArray<byte>(encryptedStage.Length);
        bool completed = false;
        try
        {
            encryptedStage[..HeaderLength].CopyTo(output);
            rc4Key.CopyTo(output.AsSpan(DerivedKeyOffset, XeCrypt.HmacSha1TagLength));
            encryptedStage[PayloadOffset..].CopyTo(output.AsSpan(PayloadOffset));
            Rc4.TransformInPlace(rc4Key, output.AsSpan(PayloadOffset), cancellationToken);

            completed = true;
            return output;
        }
        finally
        {
            if (!completed)
            {
                CryptographicOperations.ZeroMemory(output);
            }
        }
    }

    private static bool HasLegacyCdZeroRangeEvidence(ReadOnlySpan<byte> payload)
    {
        return HasAllZeroesAt(payload, LegacyCdFirstZeroOffset, LegacyCdZeroLength) ||
            HasAllZeroesAt(payload, LegacyCdSecondZeroOffset, LegacyCdZeroLength);
    }

    private static bool HasAllZeroesAt(ReadOnlySpan<byte> bytes, int offset, int length)
    {
        if (offset > bytes.Length || length > bytes.Length - offset)
        {
            return false;
        }

        byte aggregate = 0;
        foreach (byte value in bytes.Slice(offset, length))
        {
            aggregate |= value;
        }

        return aggregate == 0;
    }

    private static void ValidateStage(ReadOnlySpan<byte> stage, string stageName)
    {
        if (stage.Length < MinimumStageLength)
        {
            throw new OperationFailureException(
                ExitCode.InvalidData,
                "truncated-bootloader",
                $"The {stageName} bootloader is shorter than 0x20 bytes.");
        }
    }

    private static void EnsureInitializedCpuKey(CpuKey cpuKey)
    {
        if (!cpuKey.IsInitialized)
        {
            throw new OperationFailureException(
                ExitCode.InvalidData,
                "invalid-cpu-key",
                "A parsed CPU key is required for bootloader decryption.");
        }
    }
}
