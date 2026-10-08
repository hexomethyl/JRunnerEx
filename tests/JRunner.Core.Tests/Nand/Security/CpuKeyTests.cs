using System.Text.Json;
using JRunner.Core.Nand.Security;
using Xunit;

namespace JRunner.Core.Tests.Nand.Security;

public sealed class CpuKeyTests
{
    [Fact]
    public void Parse_accepts_exact_hex_and_never_renders_or_serializes_key_bytes()
    {
        const string source = "00112233445566778899aAbBcCdDeEfF";

        CpuKey key = CpuKey.Parse(source);
        Span<byte> copied = stackalloc byte[CpuKey.ByteLength];
        key.CopyTo(copied);

        Assert.Equal(Convert.FromHexString(source), copied.ToArray());
        Assert.Equal("CpuKey [redacted]", key.ToString());
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(key));
    }

    [Theory]
    [InlineData("")]
    [InlineData("00112233445566778899AABBCCDDEEF")]
    [InlineData("00112233445566778899AABBCCDDEEFF00")]
    [InlineData("00112233445566778899AABBCCDDEEFG")]
    public void Parse_rejects_nonexact_or_nonhex_text(string text)
    {
        Assert.False(CpuKey.TryParse(text, out CpuKey key));
        Assert.False(key.IsInitialized);
        Assert.Throws<FormatException>(() => CpuKey.Parse(text));
    }

    [Fact]
    public void FromBytes_and_copy_reject_invalid_lengths()
    {
        Assert.Throws<ArgumentException>(() => CpuKey.FromBytes(new byte[CpuKey.ByteLength - 1]));
        Assert.Throws<ArgumentException>(() => CpuKeyValidation.CalculateCpuKeyEcd(new byte[CpuKey.ByteLength - 1]));

        CpuKey key = CpuKey.Parse("00112233445566778899AABBCCDDEEFF");
        Assert.Throws<ArgumentException>(() => key.CopyTo(new byte[CpuKey.ByteLength - 1]));
    }

    [Fact]
    public void Legacy_validation_reports_hamming_and_ecd_evidence_without_parser_gating()
    {
        byte[] validBytes = CreateLegacyValidVector();
        CpuKey validKey = CpuKey.FromBytes(validBytes);

        CpuKeyValidationEvidence valid = CpuKeyValidation.Inspect(validKey);

        Assert.Equal(53, valid.HammingWeight);
        Assert.True(valid.HasExpectedHammingWeight);
        Assert.True(valid.HasValidEcd);
        Assert.True(valid.IsLegacyValid);

        byte[] invalidBytes = validBytes.ToArray();
        invalidBytes[0] ^= 0x01;
        CpuKeyValidationEvidence invalid = CpuKeyValidation.Inspect(CpuKey.FromBytes(invalidBytes));

        Assert.False(invalid.HasExpectedHammingWeight);
        Assert.False(invalid.HasValidEcd);
        Assert.False(invalid.IsLegacyValid);
    }

    [Fact]
    public void Legacy_validation_keeps_zero_paired_exception_explicit()
    {
        CpuKey zeroKey = CpuKey.FromBytes(new byte[CpuKey.ByteLength]);

        CpuKeyValidationEvidence ordinary = CpuKeyValidation.Inspect(zeroKey);
        CpuKeyValidationEvidence zeroPaired = CpuKeyValidation.Inspect(zeroKey, allowZeroPaired: true);

        Assert.True(ordinary.IsZeroKey);
        Assert.False(ordinary.IsLegacyValid);
        Assert.False(ordinary.ZeroPairedAccepted);
        Assert.True(zeroPaired.ZeroPairedAccepted);
        Assert.True(zeroPaired.IsLegacyValid);
    }

    private static byte[] CreateLegacyValidVector()
    {
        var dataBits = new byte[CpuKey.ByteLength];
        for (int bitIndex = 0; bitIndex < 53; bitIndex++)
        {
            dataBits[bitIndex >> 3] |= (byte)(1 << (bitIndex & 7));
        }

        return CpuKeyValidation.CalculateCpuKeyEcd(dataBits);
    }
}
