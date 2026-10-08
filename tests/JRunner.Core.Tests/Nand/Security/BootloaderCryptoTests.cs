using JRunner.Core.Binary;
using JRunner.Core.Contracts;
using JRunner.Core.Nand.Security;
using Xunit;

namespace JRunner.Core.Tests.Nand.Security;

public sealed class BootloaderCryptoTests
{
    private static readonly byte[] FirstBootLoaderKey = Convert.FromHexString("DD88AD0C9ED669E7B56794FB68563EFA");

    [Fact]
    public void Decrypt_cb_and_s2_match_generated_legacy_vectors()
    {
        byte[] header = CreateBytes(0x10, 0x80);
        byte[] nonce = CreateBytes(0x10, 0x10);
        byte[] payload = CreateBytes(0x80, 0x31);

        byte[] cbRc4Key = XeCrypt.HmacSha1Truncated(FirstBootLoaderKey, nonce);
        byte[] encryptedCb = EncryptStage(header, nonce, payload, cbRc4Key);
        BootloaderDecryptionResult cb = BootloaderCrypto.DecryptCb(encryptedCb);

        Assert.Equal(ComposeDecodedStage(header, cbRc4Key, payload), cb.Output.ToArray());
        Assert.Equal(BootloaderDecryptionPath.Cb, cb.Path);

        byte[] zeroKey = new byte[CpuKey.ByteLength];
        byte[] s2Rc4Key = XeCrypt.HmacSha1Truncated(zeroKey, nonce);
        byte[] encryptedS2 = EncryptStage(header, nonce, payload, s2Rc4Key);
        BootloaderDecryptionResult s2 = BootloaderCrypto.DecryptS2(encryptedS2);

        Assert.Equal(ComposeDecodedStage(header, s2Rc4Key, payload), s2.Output.ToArray());
        Assert.Equal(BootloaderDecryptionPath.S2, s2.Path);
    }

    [Fact]
    public void Encrypt_cb_recreates_a_retail_cb_or_sb_with_its_original_nonce()
    {
        byte[] header = CreateBytes(0x10, 0x53);
        header[0] = (byte)'S';
        header[1] = (byte)'B';
        byte[] nonce = CreateBytes(0x10, 0x2A);
        byte[] payload = CreateBytes(0x100, 0x94);
        byte[] rc4Key = XeCrypt.HmacSha1Truncated(FirstBootLoaderKey, nonce);
        byte[] encrypted = EncryptStage(header, nonce, payload, rc4Key);

        BootloaderDecryptionResult decoded = BootloaderCrypto.DecryptCb(encrypted);
        byte[] reEncrypted = BootloaderCrypto.EncryptCb(decoded.Output.Span, nonce);

        Assert.Equal(encrypted, reEncrypted);

        OperationFailureException failure = Assert.Throws<OperationFailureException>(() =>
            BootloaderCrypto.EncryptCb(decoded.Output.Span, nonce.AsSpan(0, 0x0F)));
        Assert.Equal(ExitCode.InvalidData, failure.Code);
        Assert.Equal("invalid-bootloader-nonce", failure.Kind);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            BootloaderCrypto.EncryptCb(decoded.Output.Span, nonce, cancellation.Token));
    }

    [Fact]
    public void Decrypt_cbb_honors_new_crypto_and_manufacturing_zero_key_branches()
    {
        CpuKey cpuKey = CpuKey.Parse("00112233445566778899AABBCCDDEEFF");
        byte[] cpuKeyBytes = CopyCpuKey(cpuKey);
        byte[] header = CreateBytes(0x10, 0xA0);
        byte[] nonce = CreateBytes(0x10, 0x20);
        byte[] payload = CreateBytes(0x60, 0x44);

        byte[] newCryptoCbA = CreateDecodedCbA(flagsHigh: 0x10, flagsLow: 0x00);
        byte[] newCryptoMessage = new byte[0x30];
        nonce.CopyTo(newCryptoMessage, 0);
        cpuKeyBytes.CopyTo(newCryptoMessage, 0x10);
        newCryptoCbA.AsSpan(0, 0x10).CopyTo(newCryptoMessage.AsSpan(0x20));
        newCryptoMessage[0x26] = 0;
        newCryptoMessage[0x27] = 0;
        byte[] newCryptoRc4Key = XeCrypt.HmacSha1Truncated(newCryptoCbA.AsSpan(0x10, 0x10), newCryptoMessage);
        byte[] encryptedNewCryptoCbB = EncryptStage(header, nonce, payload, newCryptoRc4Key);

        BootloaderDecryptionResult newCrypto = BootloaderCrypto.DecryptCbWithCpuKey(
            encryptedNewCryptoCbB,
            newCryptoCbA,
            cpuKey);

        Assert.Equal(ComposeDecodedStage(header, newCryptoRc4Key, payload), newCrypto.Output.ToArray());
        Assert.Equal(BootloaderDecryptionPath.CbWithCpuKey, newCrypto.Path);
        Assert.True(newCrypto.UsesNewCbCrypto);

        byte[] manufacturingCbA = CreateDecodedCbA(flagsHigh: 0x00, flagsLow: 0x01);
        byte[] manufacturingMessage = new byte[0x20];
        nonce.CopyTo(manufacturingMessage, 0);
        byte[] manufacturingRc4Key = XeCrypt.HmacSha1Truncated(manufacturingCbA.AsSpan(0x10, 0x10), manufacturingMessage);
        byte[] encryptedManufacturingCbB = EncryptStage(header, nonce, payload, manufacturingRc4Key);

        BootloaderDecryptionResult manufacturing = BootloaderCrypto.DecryptCbWithCpuKey(
            encryptedManufacturingCbB,
            manufacturingCbA,
            cpuKey: null);

        Assert.Equal(ComposeDecodedStage(header, manufacturingRc4Key, payload), manufacturing.Output.ToArray());
        Assert.Equal(BootloaderDecryptionPath.CbWithManufacturingZeroKey, manufacturing.Path);
        Assert.False(manufacturing.UsesNewCbCrypto);
    }

    [Fact]
    public void Decrypt_sc_cd_and_ce_follow_the_chained_legacy_keys()
    {
        byte[] header = CreateBytes(0x10, 0x60);
        byte[] scNonce = CreateBytes(0x10, 0x13);
        byte[] scPayload = CreateBytes(0x280, 0x45);
        byte[] zeroKey = new byte[CpuKey.ByteLength];
        byte[] scRc4Key = XeCrypt.HmacSha1Truncated(zeroKey, scNonce);
        byte[] encryptedSc = EncryptStage(header, scNonce, scPayload, scRc4Key);

        BootloaderDecryptionResult sc = BootloaderCrypto.DecryptSc(encryptedSc);
        Assert.Equal(ComposeDecodedStage(header, scRc4Key, scPayload), sc.Output.ToArray());
        Assert.Equal(BootloaderDecryptionPath.Sc, sc.Path);

        byte[] cdNonce = CreateBytes(0x10, 0x29);
        var cdPayload = new byte[0x280];
        cdPayload.AsSpan().Fill(0xA5);
        cdPayload.AsSpan(0x20, 4).Clear();
        byte[] cdRc4Key = XeCrypt.HmacSha1Truncated(sc.Output.Span.Slice(0x10, 0x10), cdNonce);
        byte[] encryptedCd = EncryptStage(header, cdNonce, cdPayload, cdRc4Key);

        BootloaderDecryptionResult cd = BootloaderCrypto.DecryptCd(encryptedCd, sc.Output.Span);
        Assert.Equal(ComposeDecodedStage(header, cdRc4Key, cdPayload), cd.Output.ToArray());
        Assert.Equal(BootloaderDecryptionPath.Cd, cd.Path);
        Assert.True(cd.HasLegacyCdZeroRangeEvidence);

        byte[] ceNonce = CreateBytes(0x10, 0x3A);
        byte[] cePayload = CreateBytes(0x280, 0x56);
        byte[] ceRc4Key = XeCrypt.HmacSha1Truncated(cd.Output.Span.Slice(0x10, 0x10), ceNonce);
        byte[] encryptedCe = EncryptStage(header, ceNonce, cePayload, ceRc4Key);

        BootloaderDecryptionResult ce = BootloaderCrypto.DecryptCe(encryptedCe, cd.Output.Span);
        Assert.Equal(ComposeDecodedStage(header, ceRc4Key, cePayload), ce.Output.ToArray());
        Assert.Equal(BootloaderDecryptionPath.Ce, ce.Path);
    }

    [Fact]
    public void Decrypt_cd_retries_with_cpu_key_after_legacy_zero_range_heuristic_fails()
    {
        CpuKey cpuKey = CpuKey.Parse("00112233445566778899AABBCCDDEEFF");
        byte[] previousStage = CreateDecodedCbA(flagsHigh: 0x00, flagsLow: 0x00);
        byte[] header = CreateBytes(0x10, 0x35);
        byte[] baseNonce = CreateBytes(0x10, 0x47);
        byte[] payload = new byte[0x280];
        payload.AsSpan().Fill(0xA5);

        (byte[] encryptedCd, byte[] expectedDecodedCd) = CreateCpuFallbackCdVector(
            previousStage,
            header,
            baseNonce,
            payload,
            cpuKey);
        BootloaderDecryptionResult decrypted = BootloaderCrypto.DecryptCdWithCpuKey(
            encryptedCd,
            previousStage,
            cpuKey);

        Assert.Equal(expectedDecodedCd, decrypted.Output.ToArray());
        Assert.Equal(BootloaderDecryptionPath.CdWithCpuKeyFallback, decrypted.Path);
    }

    [Fact]
    public void Decryption_rejects_truncated_stages_and_honors_cancellation()
    {
        OperationFailureException failure = Assert.Throws<OperationFailureException>(() =>
            BootloaderCrypto.DecryptCb(new byte[0x1F]));
        Assert.Equal(ExitCode.InvalidData, failure.Code);
        Assert.Equal("truncated-bootloader", failure.Kind);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            BootloaderCrypto.DecryptCb(new byte[0x5000], cancellation.Token));
    }

    private static byte[] CreateDecodedCbA(byte flagsHigh, byte flagsLow)
    {
        byte[] cbA = CreateBytes(0x280, 0x71);
        cbA[0x06] = flagsHigh;
        cbA[0x07] = flagsLow;
        for (int index = 0; index < 0x10; index++)
        {
            cbA[0x10 + index] = (byte)(0xC0 + index);
        }

        return cbA;
    }

    private static (byte[] EncryptedCd, byte[] ExpectedDecodedCd) CreateCpuFallbackCdVector(
        ReadOnlySpan<byte> previousStage,
        ReadOnlySpan<byte> header,
        ReadOnlySpan<byte> baseNonce,
        ReadOnlySpan<byte> payload,
        CpuKey cpuKey)
    {
        byte[] cpuKeyBytes = CopyCpuKey(cpuKey);
        for (int tweak = 0; tweak <= byte.MaxValue; tweak++)
        {
            byte[] nonce = baseNonce.ToArray();
            nonce[0] = (byte)tweak;
            byte[] primaryRc4Key = XeCrypt.HmacSha1Truncated(previousStage.Slice(0x10, 0x10), nonce);
            byte[] fallbackRc4Key = XeCrypt.HmacSha1Truncated(cpuKeyBytes, primaryRc4Key);
            byte[] encrypted = EncryptStage(header, nonce, payload, fallbackRc4Key);
            byte[] primaryPayload = Rc4.Transform(primaryRc4Key, encrypted.AsSpan(0x20));
            if (!HasLegacyCdZeroRangeEvidence(primaryPayload))
            {
                return (encrypted, ComposeDecodedStage(header, fallbackRc4Key, payload));
            }
        }

        throw new InvalidOperationException("Unable to generate a deterministic CPU-fallback CD vector.");
    }

    private static byte[] EncryptStage(
        ReadOnlySpan<byte> header,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> rc4Key)
    {
        var encrypted = new byte[checked(0x20 + payload.Length)];
        header.CopyTo(encrypted);
        nonce.CopyTo(encrypted.AsSpan(0x10));
        payload.CopyTo(encrypted.AsSpan(0x20));
        Rc4.TransformInPlace(rc4Key, encrypted.AsSpan(0x20));
        return encrypted;
    }

    private static byte[] ComposeDecodedStage(
        ReadOnlySpan<byte> header,
        ReadOnlySpan<byte> rc4Key,
        ReadOnlySpan<byte> payload)
    {
        var decoded = new byte[checked(0x20 + payload.Length)];
        header.CopyTo(decoded);
        rc4Key.CopyTo(decoded.AsSpan(0x10));
        payload.CopyTo(decoded.AsSpan(0x20));
        return decoded;
    }

    private static byte[] CopyCpuKey(CpuKey key)
    {
        var bytes = new byte[CpuKey.ByteLength];
        key.CopyTo(bytes);
        return bytes;
    }

    private static bool HasLegacyCdZeroRangeEvidence(ReadOnlySpan<byte> payload)
    {
        return HasAllZeroesAt(payload, 0x20, 4) || HasAllZeroesAt(payload, 0x210, 4);
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

    private static byte[] CreateBytes(int length, byte seed)
    {
        var bytes = new byte[length];
        for (int index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)(seed + index);
        }

        return bytes;
    }
}
