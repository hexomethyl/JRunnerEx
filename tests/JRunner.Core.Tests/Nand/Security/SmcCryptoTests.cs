using JRunner.Core.Contracts;
using JRunner.Core.Nand.Security;
using Xunit;

namespace JRunner.Core.Tests.Nand.Security;

public sealed class SmcCryptoTests
{
    [Fact]
    public void Encrypt_and_decrypt_match_the_legacy_rolling_stream_vector()
    {
        byte[] decrypted = CreateDecryptedSmc();
        byte[] original = decrypted.ToArray();

        byte[] encrypted = SmcCrypto.Encrypt(decrypted);
        byte[] roundTrip = SmcCrypto.Decrypt(encrypted);

        Assert.Equal(Convert.FromHexString("422ABEEF"), encrypted.AsSpan(0, 4).ToArray());
        Assert.Equal(original, decrypted);
        Assert.Equal(decrypted, roundTrip);
    }

    [Fact]
    public void Version_reads_legacy_offsets_from_decrypted_smc()
    {
        SmcVersion version = SmcCrypto.GetVersion(CreateDecryptedSmc());

        Assert.Equal(6, version.MotherboardType);
        Assert.Equal((byte)7, version.Major);
        Assert.Equal((byte)9, version.Minor);
        Assert.Equal("7.09", version.DisplayVersion);
    }

    [Fact]
    public void Version_rejects_truncated_smc()
    {
        Assert.False(SmcCrypto.TryGetVersion(new byte[0x102], out SmcVersion? version));
        Assert.Null(version);

        OperationFailureException failure = Assert.Throws<OperationFailureException>(() =>
            SmcCrypto.GetVersion(new byte[0x102]));

        Assert.Equal(ExitCode.InvalidData, failure.Code);
        Assert.Equal("truncated-smc", failure.Kind);
    }

    [Fact]
    public void Transform_honors_pre_cancelled_token()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            SmcCrypto.Encrypt(new byte[0x5000], cancellation.Token));
    }

    private static byte[] CreateDecryptedSmc()
    {
        var smc = new byte[0x180];
        for (int index = 0; index < smc.Length; index++)
        {
            smc[index] = (byte)index;
        }

        smc[0x100] = 0x60;
        smc[0x101] = 7;
        smc[0x102] = 9;
        return smc;
    }
}
