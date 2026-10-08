using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using JRunner.Cli.Infrastructure;
using JRunner.Core.Support;
using Xunit;

namespace JRunner.Cli.Tests.Support;

public sealed class SupportPayloadValidatorTests
{
    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    [Fact]
    public async Task Missing_support_root_is_absent()
    {
        TestManifest fixture = CreateManifest(("common/tool.bin", new byte[] { 0x10 }));
        using var temporaryDirectory = new TemporaryDirectory();
        SupportRoot supportRoot = CreateSupportRoot(temporaryDirectory.Path);

        SupportStatusResult result = await ValidateAsync(supportRoot, fixture.Manifest);

        Assert.Equal(SupportStatusKind.Absent, result.Status);
        Assert.Equal(fixture.Manifest.PayloadId, result.PayloadId);
        Assert.Equal(fixture.Manifest.ReleaseTag, result.ReleaseTag);
        Assert.Equal(fixture.Manifest.Archive.DownloadUri.AbsoluteUri, result.SourceUrl);
        Assert.Equal(fixture.Manifest.Archive.Sha256, result.ArchiveSha256);
        Assert.Equal(fixture.Manifest.CanonicalManifestSha256, result.CanonicalManifestSha256);
        Assert.Null(result.GenerationId);
        Assert.Equal(1, result.ExpectedFileCount);
        Assert.Equal(1, result.ExpectedByteLength);
        Assert.Null(result.ProblemKind);
        Assert.Null(result.ProblemPath);
    }

    [Fact]
    public async Task Transient_staging_directories_without_an_activation_are_absent()
    {
        TestManifest fixture = CreateManifest(("common/tool.bin", new byte[] { 0x10 }));
        using var temporaryDirectory = new TemporaryDirectory();
        SupportRoot supportRoot = CreateSupportRoot(temporaryDirectory.Path);
        CreatePrivateDirectory(supportRoot.DirectoryPath);
        Directory.CreateDirectory(Path.Combine(supportRoot.DirectoryPath, ".downloads"));
        Directory.CreateDirectory(Path.Combine(supportRoot.DirectoryPath, ".staging"));
        SupportStatusResult result = await ValidateAsync(supportRoot, fixture.Manifest);

        Assert.Equal(SupportStatusKind.Absent, result.Status);
        Assert.Null(result.ProblemKind);
        Assert.Null(result.ProblemPath);
    }

    [Fact]
    public async Task Orphaned_generation_without_an_active_marker_is_incomplete()
    {
        TestManifest fixture = CreateManifest(("common/tool.bin", new byte[] { 0x10 }));
        using var temporaryDirectory = new TemporaryDirectory();
        SupportRoot supportRoot = CreateSupportRoot(temporaryDirectory.Path);
        await WriteGenerationAsync(supportRoot, "orphan", ("common/tool.bin", new byte[] { 0x10 }));

        SupportStatusResult result = await ValidateAsync(supportRoot, fixture.Manifest);

        Assert.Equal(SupportStatusKind.Incomplete, result.Status);
        Assert.Equal("activation-missing", result.ProblemKind);
        Assert.Equal("installations/orphan", result.ProblemPath);
        Assert.Null(result.GenerationId);
    }

    [Fact]
    public async Task Active_marker_with_an_unknown_field_is_incomplete()
    {
        TestManifest fixture = CreateManifest(("common/tool.bin", new byte[] { 0x10 }));
        using var temporaryDirectory = new TemporaryDirectory();
        SupportRoot supportRoot = CreateSupportRoot(temporaryDirectory.Path);
        CreatePrivateDirectory(supportRoot.DirectoryPath);

        string marker = CreateActiveRecord(fixture.Manifest, "current");
        await File.WriteAllTextAsync(
            Path.Combine(supportRoot.DirectoryPath, "active.json"),
            string.Concat(marker[..^1], ",\"unexpected\":true}"));

        SupportStatusResult result = await ValidateAsync(supportRoot, fixture.Manifest);

        Assert.Equal(SupportStatusKind.Incomplete, result.Status);
        Assert.Equal("active-record-invalid", result.ProblemKind);
        Assert.Equal("active.json", result.ProblemPath);
        Assert.Null(result.GenerationId);
    }

    [Fact]
    public async Task Active_marker_with_a_tampered_canonical_manifest_digest_is_incomplete()
    {
        TestManifest fixture = CreateManifest(("common/tool.bin", new byte[] { 0x10 }));
        using var temporaryDirectory = new TemporaryDirectory();
        SupportRoot supportRoot = CreateSupportRoot(temporaryDirectory.Path);
        await WriteGenerationAsync(supportRoot, "current", fixture.Files);
        string marker = CreateActiveRecord(
            fixture.Manifest,
            "current",
            canonicalManifestSha256: new string('0', 64));
        await File.WriteAllTextAsync(Path.Combine(supportRoot.DirectoryPath, "active.json"), marker);

        SupportStatusResult result = await ValidateAsync(supportRoot, fixture.Manifest);

        Assert.Equal(SupportStatusKind.Incomplete, result.Status);
        Assert.Equal("active-record-invalid", result.ProblemKind);
        Assert.Equal("active.json", result.ProblemPath);
        Assert.Null(result.GenerationId);
    }


    [Fact]
    public async Task Active_marker_with_a_tampered_active_payload_path_is_incomplete()
    {
        TestManifest fixture = CreateManifest(("common/tool.bin", new byte[] { 0x10 }));
        using var temporaryDirectory = new TemporaryDirectory();
        SupportRoot supportRoot = CreateSupportRoot(temporaryDirectory.Path);
        await WriteGenerationAsync(supportRoot, "current", fixture.Files);
        string marker = CreateActiveRecord(
            fixture.Manifest,
            "current",
            activePayloadPath: "installations/other");
        await File.WriteAllTextAsync(Path.Combine(supportRoot.DirectoryPath, "active.json"), marker);

        SupportStatusResult result = await ValidateAsync(supportRoot, fixture.Manifest);

        Assert.Equal(SupportStatusKind.Incomplete, result.Status);
        Assert.Equal("active-record-invalid", result.ProblemKind);
        Assert.Equal("active.json", result.ProblemPath);
        Assert.Null(result.GenerationId);
    }
    [Fact]
    public async Task Active_marker_with_a_non_directory_installations_path_is_incomplete()
    {
        TestManifest fixture = CreateManifest(("common/tool.bin", new byte[] { 0x10 }));
        using var temporaryDirectory = new TemporaryDirectory();
        SupportRoot supportRoot = CreateSupportRoot(temporaryDirectory.Path);
        CreatePrivateDirectory(supportRoot.DirectoryPath);
        await File.WriteAllBytesAsync(
            Path.Combine(supportRoot.DirectoryPath, "installations"),
            new byte[] { 0x00 });
        await WriteActiveRecordAsync(supportRoot, fixture.Manifest, "current");

        SupportStatusResult result = await ValidateAsync(supportRoot, fixture.Manifest);

        Assert.Equal(SupportStatusKind.Incomplete, result.Status);
        Assert.Equal("installations-invalid", result.ProblemKind);
        Assert.Equal("installations", result.ProblemPath);
        Assert.Equal("current", result.GenerationId);
    }

    [Fact]
    public async Task Active_marker_with_a_symlinked_installations_path_is_incomplete()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        TestManifest fixture = CreateManifest(("common/tool.bin", new byte[] { 0x10 }));
        using var temporaryDirectory = new TemporaryDirectory();
        SupportRoot supportRoot = CreateSupportRoot(temporaryDirectory.Path);
        CreatePrivateDirectory(supportRoot.DirectoryPath);
        string externalInstallations = Path.Combine(temporaryDirectory.Path, "external-installations");
        Directory.CreateDirectory(externalInstallations);
        Directory.CreateSymbolicLink(
            Path.Combine(supportRoot.DirectoryPath, "installations"),
            externalInstallations);
        await WriteActiveRecordAsync(supportRoot, fixture.Manifest, "current");

        SupportStatusResult result = await ValidateAsync(supportRoot, fixture.Manifest);

        Assert.Equal(SupportStatusKind.Incomplete, result.Status);
        Assert.Equal("installations-invalid", result.ProblemKind);
        Assert.Equal("installations", result.ProblemPath);
        Assert.Equal("current", result.GenerationId);
    }

    [Fact]
    public async Task Active_generation_with_altered_file_content_is_corrupt()
    {
        TestManifest fixture = CreateManifest(("common/tool.bin", new byte[] { 0x10 }));
        using var temporaryDirectory = new TemporaryDirectory();
        SupportRoot supportRoot = CreateSupportRoot(temporaryDirectory.Path);
        await WriteGenerationAsync(supportRoot, "current", ("common/tool.bin", new byte[] { 0x11 }));
        await WriteActiveRecordAsync(supportRoot, fixture.Manifest, "current");

        SupportStatusResult result = await ValidateAsync(supportRoot, fixture.Manifest);

        Assert.Equal(SupportStatusKind.Corrupt, result.Status);
        Assert.Equal("current", result.GenerationId);
        Assert.Equal("support-file-digest-mismatch", result.ProblemKind);
        string problemPath = Assert.IsType<string>(result.ProblemPath);
        Assert.Equal("common/tool.bin", problemPath);
        Assert.DoesNotContain(temporaryDirectory.Path, problemPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Active_generation_with_a_missing_expected_file_is_corrupt()
    {
        TestManifest fixture = CreateManifest(
            ("common/tool.bin", new byte[] { 0x10 }),
            ("xeBuild/xeBuild.exe", new byte[] { 0x20 }));
        using var temporaryDirectory = new TemporaryDirectory();
        SupportRoot supportRoot = CreateSupportRoot(temporaryDirectory.Path);
        CreatePrivateDirectory(supportRoot.DirectoryPath);
        string installationsDirectory = Path.Combine(supportRoot.DirectoryPath, "installations");
        CreatePrivateDirectory(installationsDirectory);
        string generationDirectory = Path.Combine(installationsDirectory, "current");
        CreatePrivateDirectory(generationDirectory);
        CreatePrivateDirectory(Path.Combine(generationDirectory, "common"));
        CreatePrivateDirectory(Path.Combine(generationDirectory, "xeBuild"));
        await File.WriteAllBytesAsync(Path.Combine(generationDirectory, "common", "tool.bin"), new byte[] { 0x10 });
        SetPrivateFile(Path.Combine(generationDirectory, "common", "tool.bin"));

        await WriteActiveRecordAsync(supportRoot, fixture.Manifest, "current");

        SupportStatusResult result = await ValidateAsync(supportRoot, fixture.Manifest);

        Assert.Equal(SupportStatusKind.Corrupt, result.Status);
        Assert.Equal("support-file-missing", result.ProblemKind);
        Assert.Equal("xeBuild/xeBuild.exe", result.ProblemPath);
    }

    [Fact]
    public async Task Active_generation_with_an_extra_entry_is_corrupt()
    {
        TestManifest fixture = CreateManifest(("common/tool.bin", new byte[] { 0x10 }));
        using var temporaryDirectory = new TemporaryDirectory();
        SupportRoot supportRoot = CreateSupportRoot(temporaryDirectory.Path);
        await WriteGenerationAsync(
            supportRoot,
            "current",
            ("common/tool.bin", new byte[] { 0x10 }),
            ("common/extra.bin", new byte[] { 0x20 }));
        await WriteActiveRecordAsync(supportRoot, fixture.Manifest, "current");

        SupportStatusResult result = await ValidateAsync(supportRoot, fixture.Manifest);

        Assert.Equal(SupportStatusKind.Corrupt, result.Status);
        Assert.Equal("support-tree-extra-entry", result.ProblemKind);
        Assert.Equal("common/extra.bin", result.ProblemPath);
    }

    [Fact]
    public async Task Exact_active_generation_is_valid()
    {
        TestManifest fixture = CreateManifest(
            ("common/tool.bin", new byte[] { 0x10, 0x11 }),
            ("xeBuild/xeBuild.exe", new byte[] { 0x20 }));
        using var temporaryDirectory = new TemporaryDirectory();
        SupportRoot supportRoot = CreateSupportRoot(temporaryDirectory.Path);
        await WriteGenerationAsync(supportRoot, "current", fixture.Files);
        await WriteActiveRecordAsync(supportRoot, fixture.Manifest, "current");

        SupportStatusResult result = await ValidateAsync(supportRoot, fixture.Manifest);

        Assert.Equal(SupportStatusKind.Valid, result.Status);
        Assert.Equal(fixture.Manifest.PayloadId, result.PayloadId);
        Assert.Equal(fixture.Manifest.ReleaseTag, result.ReleaseTag);
        Assert.Equal(fixture.Manifest.Archive.DownloadUri.AbsoluteUri, result.SourceUrl);
        Assert.Equal(fixture.Manifest.Archive.Sha256, result.ArchiveSha256);
        Assert.Equal(fixture.Manifest.CanonicalManifestSha256, result.CanonicalManifestSha256);
        Assert.Equal("current", result.GenerationId);
        Assert.Equal(2, result.ExpectedFileCount);
        Assert.Equal(3, result.ExpectedByteLength);
        Assert.Null(result.ProblemKind);
        Assert.Null(result.ProblemPath);
    }

    [Fact]
    public async Task Retained_generation_is_revalidated_independently_of_active_marker()
    {
        TestManifest fixture = CreateManifest(
            ("common/tool.bin", new byte[] { 0x10, 0x11 }),
            ("xeBuild/xeBuild.exe", new byte[] { 0x20 }));
        using var temporaryDirectory = new TemporaryDirectory();
        SupportRoot supportRoot = CreateSupportRoot(temporaryDirectory.Path);
        await WriteGenerationAsync(supportRoot, "retained", fixture.Files);
        await WriteGenerationAsync(supportRoot, "new-active", fixture.Files);
        await WriteActiveRecordAsync(supportRoot, fixture.Manifest, "new-active");
        var validator = new SupportPayloadValidator(fixture.Manifest);

        SupportStatusResult active = await validator.ValidateAsync(supportRoot);
        SupportStatusResult retained = await validator.ValidateGenerationAsync(supportRoot, "retained");

        Assert.Equal(SupportStatusKind.Valid, active.Status);
        Assert.Equal("new-active", active.GenerationId);
        Assert.Equal(SupportStatusKind.Valid, retained.Status);
        Assert.Equal("retained", retained.GenerationId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Active_generation_requires_private_installations_and_generation_directories(bool relaxGeneration)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        TestManifest fixture = CreateManifest(("common/tool.bin", new byte[] { 0x10 }));
        using var temporaryDirectory = new TemporaryDirectory();
        SupportRoot supportRoot = CreateSupportRoot(temporaryDirectory.Path);
        await WriteGenerationAsync(supportRoot, "current", fixture.Files);
        await WriteActiveRecordAsync(supportRoot, fixture.Manifest, "current");
        string installationsDirectory = Path.Combine(supportRoot.DirectoryPath, "installations");
        string targetDirectory = relaxGeneration
            ? Path.Combine(installationsDirectory, "current")
            : installationsDirectory;
        File.SetUnixFileMode(
            targetDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead);

        SupportStatusResult result = await ValidateAsync(supportRoot, fixture.Manifest);

        Assert.Equal(SupportStatusKind.Incomplete, result.Status);
        Assert.Equal("support-root-insecure", result.ProblemKind);
    }

    [Fact]
    public async Task Unreferenced_failed_generation_does_not_affect_a_valid_active_generation()
    {
        TestManifest fixture = CreateManifest(("common/tool.bin", new byte[] { 0x10 }));
        using var temporaryDirectory = new TemporaryDirectory();
        SupportRoot supportRoot = CreateSupportRoot(temporaryDirectory.Path);
        await WriteGenerationAsync(supportRoot, "active", fixture.Files);
        await WriteGenerationAsync(supportRoot, "failed", ("common/partial.bin", new byte[] { 0x20 }));
        await WriteActiveRecordAsync(supportRoot, fixture.Manifest, "active");

        SupportStatusResult result = await ValidateAsync(supportRoot, fixture.Manifest);

        Assert.Equal(SupportStatusKind.Valid, result.Status);
        Assert.Equal("active", result.GenerationId);
    }

    private static SupportRoot CreateSupportRoot(string temporaryPath)
    {
        return new SupportRoot(Path.Combine(temporaryPath, "support"), SupportRootSource.Explicit);
    }

    private static void CreatePrivateDirectory(string directoryPath)
    {
        Directory.CreateDirectory(directoryPath);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directoryPath, PrivateDirectoryMode);
        }
    }
    private static void SetPrivateFile(string filePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }


    private static async Task<SupportStatusResult> ValidateAsync(SupportRoot supportRoot, SupportManifest manifest)
    {
        var validator = new SupportPayloadValidator(manifest);
        return await validator.ValidateAsync(supportRoot);
    }

    private static async Task WriteGenerationAsync(
        SupportRoot supportRoot,
        string generationId,
        params (string Path, byte[] Content)[] files)
    {
        CreatePrivateDirectory(supportRoot.DirectoryPath);
        string installationsDirectory = Path.Combine(supportRoot.DirectoryPath, "installations");
        CreatePrivateDirectory(installationsDirectory);
        string generationDirectory = Path.Combine(installationsDirectory, generationId);
        CreatePrivateDirectory(generationDirectory);
        foreach ((string path, byte[] content) in files)
        {
            string filePath = Path.Combine(generationDirectory, path.Replace('/', Path.DirectorySeparatorChar));
            string directoryPath = Path.GetDirectoryName(filePath)
                ?? throw new InvalidOperationException("A support test file did not have a parent directory.");
            CreatePrivateDirectory(directoryPath);
            await File.WriteAllBytesAsync(filePath, content);
            SetPrivateFile(filePath);
        }
    }

    private static Task WriteActiveRecordAsync(SupportRoot supportRoot, SupportManifest manifest, string generationId)
    {
        CreatePrivateDirectory(supportRoot.DirectoryPath);
        return File.WriteAllTextAsync(
            Path.Combine(supportRoot.DirectoryPath, "active.json"),
            CreateActiveRecord(manifest, generationId));
    }

    private static string CreateActiveRecord(
        SupportManifest manifest,
        string generationId,
        string? canonicalManifestSha256 = null,
        string? activePayloadPath = null)
    {
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            payloadId = manifest.PayloadId,
            releaseTag = manifest.ReleaseTag,
            sourceUrl = manifest.Archive.DownloadUri.AbsoluteUri,
            archiveSha256 = manifest.Archive.Sha256,
            canonicalManifestSha256 = canonicalManifestSha256 ?? manifest.CanonicalManifestSha256,
            installedAtUtc = DateTimeOffset.UnixEpoch,
            activePayloadPath = activePayloadPath ?? $"installations/{generationId}",
            generationId,
        });
    }

    private static TestManifest CreateManifest(params (string Path, byte[] Content)[] files)
    {
        SupportFile[] manifestFiles = files
            .Select(file => new SupportFile(
                file.Path,
                file.Content.LongLength,
                Convert.ToHexString(SHA256.HashData(file.Content))))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();
        var manifest = new SupportManifest(
            schemaVersion: 1,
            payloadId: "test-support",
            archive: new SupportArchive(
                new Uri("https://example.test/support.zip"),
                byteLength: 1,
                sha256: Convert.ToHexString(SHA256.HashData(new byte[] { 0x01 }))),
            uncompressedByteLength: manifestFiles.Sum(file => file.ByteLength),
            files: new ReadOnlyCollection<SupportFile>(manifestFiles));
        return new TestManifest(manifest, files);
    }

    private sealed record TestManifest(
        SupportManifest Manifest,
        (string Path, byte[] Content)[] Files);

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                $".jrunner-support-validator-{Guid.NewGuid():N}");
            CreatePrivateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
