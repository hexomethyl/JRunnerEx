using System.Security.Cryptography;
using System.Text;
using JRunner.Core.Binary;
using JRunner.Core.Contracts;
using JRunner.Core.Nand.Security;
using Xunit;

namespace JRunner.Core.Tests.Nand.Security;

public sealed class KeyvaultServiceTests
{
    [Fact]
    public void Decrypt_and_inspect_follow_legacy_nonce_hmac_rc4_and_metadata_offsets()
    {
        CpuKey key = CreateCpuKey();
        byte[] decrypted = CreateDecryptedKeyvault();
        byte[] encrypted = EncryptLegacyKeyvault(decrypted, key);

        byte[] roundTrip = KeyvaultService.Decrypt(encrypted, key);
        KeyvaultInspection inspection = KeyvaultService.Inspect(encrypted, key);
        KeyvaultMetadata metadata = Assert.IsType<KeyvaultMetadata>(inspection.Metadata);

        Assert.Equal(decrypted, roundTrip);
        Assert.Equal(encrypted.AsSpan(0, 0x10).ToArray(), roundTrip.AsSpan(0, 0x10).ToArray());
        Assert.Equal(KeyvaultStorageState.Encrypted, inspection.StorageState);
        Assert.Equal(KeyvaultCpuKeyVerificationStatus.Verified, inspection.CpuKeyVerification);
        Assert.True(inspection.IsVerified);
        Assert.Equal("SN1234567890", metadata.ConsoleSerial);
        Assert.Equal(new string('O', 0x1C), metadata.OpticalDriveInquiryString);
        Assert.Equal("DEADBEEF01", metadata.ConsoleId);
        Assert.Equal("0102", metadata.Region);
        Assert.Equal(KeyvaultType.Type1, metadata.Type);
        Assert.Equal("20261005", metadata.ManufacturingDate);
        Assert.True(metadata.FcrtFlag);
    }

    [Fact]
    public void Inspect_accepts_already_decrypted_keyvault_without_cpu_key()
    {
        byte[] decrypted = CreateDecryptedKeyvault();

        KeyvaultInspection inspection = KeyvaultService.Inspect(decrypted);

        Assert.Equal(KeyvaultStorageState.Decrypted, inspection.StorageState);
        Assert.Equal(KeyvaultCpuKeyVerificationStatus.NotRequired, inspection.CpuKeyVerification);
        Assert.NotNull(inspection.Metadata);
        Assert.True(inspection.IsVerified);
    }

    [Fact]
    public void Inspect_reports_bad_key_without_exposing_unverified_metadata()
    {
        byte[] encrypted = EncryptLegacyKeyvault(CreateDecryptedKeyvault(), CreateCpuKey());
        CpuKey wrongKey = CpuKey.Parse("FFEEDDCCBBAA99887766554433221100");

        KeyvaultInspection inspection = KeyvaultService.Inspect(encrypted, wrongKey);
        OperationFailureException failure = Assert.Throws<OperationFailureException>(() =>
            KeyvaultService.VerifyAndExtract(encrypted, wrongKey));

        Assert.Equal(KeyvaultCpuKeyVerificationStatus.Failed, inspection.CpuKeyVerification);
        Assert.Null(inspection.Metadata);
        Assert.Equal(ExitCode.InvalidData, failure.Code);
        Assert.Equal("cpu-key-verification-failed", failure.Kind);
    }

    [Fact]
    public void Keyvault_operations_reject_any_size_other_than_0x4000()
    {
        OperationFailureException tooShort = Assert.Throws<OperationFailureException>(() =>
            KeyvaultService.Inspect(new byte[KeyvaultService.KeyvaultLength - 1]));
        OperationFailureException tooLong = Assert.Throws<OperationFailureException>(() =>
            KeyvaultService.HasLegacyVerificationRange(new byte[KeyvaultService.KeyvaultLength + 1]));

        Assert.Equal("invalid-keyvault-size", tooShort.Kind);
        Assert.Equal("invalid-keyvault-size", tooLong.Kind);
    }

    [Fact]
    public void Metadata_extraction_requires_legacy_zero_verification_range()
    {
        byte[] keyvault = CreateDecryptedKeyvault();
        keyvault[0x40] = 0x01;

        OperationFailureException failure = Assert.Throws<OperationFailureException>(() =>
            KeyvaultService.ExtractMetadata(keyvault));

        Assert.Equal(ExitCode.InvalidData, failure.Code);
        Assert.Equal("keyvault-verification-failed", failure.Kind);
    }

    [Fact]
    public void Decrypt_honors_pre_cancelled_token()
    {
        byte[] encrypted = EncryptLegacyKeyvault(CreateDecryptedKeyvault(), CreateCpuKey());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            KeyvaultService.Decrypt(encrypted, CreateCpuKey(), cancellation.Token));
    }

    private static CpuKey CreateCpuKey()
    {
        return CpuKey.Parse("00112233445566778899AABBCCDDEEFF");
    }

    private static byte[] CreateDecryptedKeyvault()
    {
        var keyvault = new byte[KeyvaultService.KeyvaultLength];
        keyvault.AsSpan().Fill(0x5A);

        for (int index = 0; index < 0x10; index++)
        {
            keyvault[index] = (byte)(0xA0 + index);
        }

        keyvault.AsSpan(0x40, 0x20).Clear();
        Encoding.ASCII.GetBytes("SN1234567890").CopyTo(keyvault, 0xB0);
        keyvault[0xC8] = 0x01;
        keyvault[0xC9] = 0x02;
        new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01 }.CopyTo(keyvault, 0x9CA);
        Encoding.ASCII.GetBytes("20261005").CopyTo(keyvault, 0x9E4);
        keyvault.AsSpan(0xC92, 0x1C).Fill((byte)'O');
        keyvault.AsSpan(0x100, 0x10).Fill(0xA5);
        keyvault.AsSpan(0x1DF8, 8).Fill(byte.MaxValue);
        keyvault[0x1C] = 0x01;
        keyvault[0x1D] = 0x20;
        return keyvault;
    }

    private static byte[] EncryptLegacyKeyvault(ReadOnlySpan<byte> decrypted, CpuKey key)
    {
        var encrypted = decrypted.ToArray();
        Span<byte> keyBytes = stackalloc byte[CpuKey.ByteLength];
        Span<byte> rc4Key = stackalloc byte[XeCrypt.HmacSha1TagLength];
        try
        {
            key.CopyTo(keyBytes);
            XeCrypt.HmacSha1Truncated(keyBytes, encrypted.AsSpan(0, 0x10), rc4Key);
            Rc4.TransformInPlace(rc4Key, encrypted.AsSpan(0x10));
            return encrypted;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
            CryptographicOperations.ZeroMemory(rc4Key);
        }
    }
}
