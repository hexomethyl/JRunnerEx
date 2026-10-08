using System.Security.Cryptography;
using System.Text.Json;
using JRunner.Core.Nand.Models;
using JRunner.Core.XeBuild;
using Xunit;

namespace JRunner.Core.Tests.XeBuild;

public sealed class XeBuildSourceContextTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(SHA256.HashSizeInBytes - 1)]
    [InlineData(SHA256.HashSizeInBytes + 1)]
    [InlineData(SHA256.HashSizeInBytes * 2)]
    public void Constructor_rejects_non_sha256_digest_lengths(int length)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            new XeBuildSourceContext("input.bin", 0x01000000, new byte[length]));

        Assert.Equal("contentSha256", exception.ParamName);
    }

    [Fact]
    public void MatchesContentSha256_compares_every_byte_of_the_full_digest()
    {
        byte[] digest = SHA256.HashData("source-content"u8);
        var context = new XeBuildSourceContext("input.bin", 0x01000000, digest);
        byte[] candidate = digest.ToArray();

        Assert.True(context.MatchesContentSha256(digest));
        for (int index = 0; index < candidate.Length; index++)
        {
            candidate[index] ^= 0x01;
            Assert.False(context.MatchesContentSha256(candidate));
            candidate[index] ^= 0x01;
        }

        Assert.True(context.MatchesContentSha256(candidate));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(SHA256.HashSizeInBytes - 1)]
    [InlineData(SHA256.HashSizeInBytes + 1)]
    [InlineData(SHA256.HashSizeInBytes * 2)]
    public void MatchesContentSha256_rejects_digest_length_mismatches(int length)
    {
        byte[] digest = SHA256.HashData("source-content"u8);
        var context = new XeBuildSourceContext("input.bin", 0x01000000, digest);
        var candidate = new byte[length];
        digest.AsSpan(0, Math.Min(length, digest.Length)).CopyTo(candidate);

        Assert.False(context.MatchesContentSha256(candidate));
    }

    [Fact]
    public void Constructor_defensively_copies_the_supplied_digest()
    {
        byte[] suppliedDigest = SHA256.HashData("source-content"u8);
        byte[] expectedDigest = suppliedDigest.ToArray();
        var context = new XeBuildSourceContext("input.bin", 0x01000000, suppliedDigest);

        Array.Clear(suppliedDigest);

        Assert.True(context.MatchesContentSha256(expectedDigest));
        Assert.False(context.MatchesContentSha256(suppliedDigest));
    }

    [Fact]
    public void Diagnostic_text_and_json_expose_facts_but_not_the_digest()
    {
        byte[] digest = SHA256.HashData("source-content"u8);
        var context = new XeBuildSourceContext(
            "input.bin",
            0x01000000,
            digest,
            ConsoleId.Falcon16Mb,
            supportsRgh1: true);
        string diagnostic = context.ToString();
        string json = JsonSerializer.Serialize(context);
        JsonElement serialized = JsonSerializer.SerializeToElement(context);

        Assert.Contains("input.bin", diagnostic, StringComparison.Ordinal);
        Assert.Contains("Falcon16Mb", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("sha256", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("digest", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToHexString(digest), diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToBase64String(digest), diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(digest), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToBase64String(digest), json, StringComparison.Ordinal);
        Assert.Equal(
            ["InputPath", "ByteLength", "DetectedConsoleId", "SupportsRgh1", "IsFourGigabyteEmmc"],
            serialized.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("input.bin", serialized.GetProperty("InputPath").GetString());
        Assert.Equal(0x01000000L, serialized.GetProperty("ByteLength").GetInt64());
        Assert.Equal(ConsoleId.Falcon16Mb, context.DetectedConsoleId);
        Assert.True(context.SupportsRgh1);
    }
}
