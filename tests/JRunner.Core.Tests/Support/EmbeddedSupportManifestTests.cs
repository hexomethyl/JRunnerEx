using System.Security.Cryptography;
using JRunner.Core.Support;
using Xunit;

namespace JRunner.Core.Tests.Support;

public sealed class EmbeddedSupportManifestTests
{
    [Fact]
    public void Current_manifest_pins_the_complete_v340r7_support_payload()
    {
        SupportManifest manifest = EmbeddedSupportManifest.Current;

        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Equal("jrunner-with-extras-v3.4.0-r7", manifest.PayloadId);
        Assert.Equal("V3.4.0-r7", manifest.ReleaseTag);
        Assert.Equal(EmbeddedSupportManifest.CanonicalManifestSha256, manifest.CanonicalManifestSha256);
        Assert.Equal(
            "https://github.com/J-Runner-With-Extras/J-Runner-with-Extras/releases/download/V3.4.0-r7/J-Runner-with-Extras.zip",
            manifest.Archive.DownloadUri.AbsoluteUri);
        Assert.Equal(233542155, manifest.Archive.ByteLength);
        Assert.Equal("C92A31D21D7DC617B3DAE47E44B3AE9988ACF8B94C0DA390F8A69E6805BC45B6", manifest.Archive.Sha256);
        Assert.Equal(1098, manifest.Files.Count);
        Assert.Equal(299763308, manifest.UncompressedByteLength);
        Assert.Equal(manifest.UncompressedByteLength, manifest.Files.Sum(file => file.ByteLength));

        AssertFile(
            manifest,
            "common/7z/7za.exe",
            858624,
            "392D39CADDFFB4B078807EE05E69C94719027AC9C9445503806E47116ACF6686");
        AssertFile(
            manifest,
            "common/xell-images/glitch2/CORONA_RGH3.ecc",
            1351680,
            "2D877AE18D69907D5CE0DCF6B6F82382B35D58C045B1B7E460ABFF4D5ACEB555");
        AssertFile(
            manifest,
            "xeBuild/17559/_glitch2.ini",
            2016,
            "23EA64D9353F37E8FA01D949AB28DDE605E9847B9FB098E572C535930C63E565");
        AssertFile(
            manifest,
            "xeBuild/xeBuild.exe",
            451584,
            "10DAC648197FA5CDBB82D975AB9FF11F64075C9686908DFD9A33EDC533F1971C");
    }

    [Fact]
    public void Current_manifest_resource_matches_the_reviewed_canonical_digest()
    {
        using Stream stream = typeof(EmbeddedSupportManifest).Assembly.GetManifestResourceStream(
                "JRunner.Core.Support.SupportManifest.v1.json")
            ?? throw new InvalidOperationException("The embedded support manifest resource was missing.");
        using SHA256 hash = SHA256.Create();

        Assert.Equal(
            EmbeddedSupportManifest.CanonicalManifestSha256,
            Convert.ToHexString(hash.ComputeHash(stream)));
    }

    [Fact]
    public void Current_manifest_exposes_a_canonical_read_only_file_list()
    {
        SupportManifest manifest = EmbeddedSupportManifest.Current;
        string[] paths = manifest.Files.Select(file => file.Path).ToArray();

        Assert.Equal(paths.OrderBy(path => path, StringComparer.Ordinal), paths);
        Assert.All(
            paths,
            path => Assert.True(
                path.StartsWith("common/", StringComparison.Ordinal) ||
                path.StartsWith("xeBuild/", StringComparison.Ordinal)));

        var files = Assert.IsAssignableFrom<IList<SupportFile>>(manifest.Files);
        Assert.Throws<NotSupportedException>(() => files.RemoveAt(0));
    }

    private static void AssertFile(SupportManifest manifest, string path, long byteLength, string sha256)
    {
        SupportFile file = Assert.Single(manifest.Files, file => file.Path == path);
        Assert.Equal(byteLength, file.ByteLength);
        Assert.Equal(sha256, file.Sha256);
    }
}
