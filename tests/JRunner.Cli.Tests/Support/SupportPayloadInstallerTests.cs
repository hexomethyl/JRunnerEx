using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using JRunner.Cli.Infrastructure;
using JRunner.Core.Contracts;
using JRunner.Core.Support;
using Xunit;

namespace JRunner.Cli.Tests.Support;

public sealed class SupportPayloadInstallerTests
{
    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;


    [Fact]
    public async Task Verified_archive_extracts_only_declared_files_and_activates_as_one_generation()
    {
        byte[] tool = [0x10, 0x20, 0x30];
        byte[] nestedTool = [0x35, 0x36];
        byte[] executable = [0x40, 0x50];
        byte[] archive = CreateArchive(
            ("common/tool.bin", tool),
            ("common/nested/tool.bin", nestedTool),
            ("xeBuild/xeBuild.exe", executable),
            ("JRunner.exe", new byte[] { 0x60 }));
        SupportManifest manifest = CreateManifest(
            archive,
            ("common/tool.bin", tool),
            ("common/nested/tool.bin", nestedTool),
            ("xeBuild/xeBuild.exe", executable));

        using var temporaryDirectory = new TemporaryDirectory();
        SupportRoot supportRoot = CreateSupportRoot(temporaryDirectory.Path);
        using var httpClient = CreateHttpClient(archive);
        var installer = new SupportPayloadInstaller(httpClient, manifest);

        SupportInstallationResult result = await installer.InstallAsync(supportRoot);

        Assert.Equal(manifest.PayloadId, result.PayloadId);
        Assert.Equal(3, result.FileCount);
        Assert.Equal(tool.LongLength + nestedTool.LongLength + executable.LongLength, result.ExtractedByteLength);
        Assert.Equal(tool, await File.ReadAllBytesAsync(Path.Combine(result.PayloadDirectory, "common", "tool.bin")));
        Assert.Equal(
            nestedTool,
            await File.ReadAllBytesAsync(Path.Combine(result.PayloadDirectory, "common", "nested", "tool.bin")));
        Assert.Equal(executable, await File.ReadAllBytesAsync(Path.Combine(result.PayloadDirectory, "xeBuild", "xeBuild.exe")));
        Assert.False(File.Exists(Path.Combine(result.PayloadDirectory, "JRunner.exe")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(supportRoot.DirectoryPath, ".downloads")));
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(PrivateDirectoryMode, File.GetUnixFileMode(Path.Combine(result.PayloadDirectory, "common")));
            Assert.Equal(PrivateDirectoryMode, File.GetUnixFileMode(Path.Combine(result.PayloadDirectory, "common", "nested")));
            Assert.Equal(PrivateDirectoryMode, File.GetUnixFileMode(Path.Combine(result.PayloadDirectory, "xeBuild")));
            Assert.Equal(PrivateFileMode, File.GetUnixFileMode(Path.Combine(result.PayloadDirectory, "common", "tool.bin")));
            Assert.Equal(
                PrivateFileMode,
                File.GetUnixFileMode(Path.Combine(result.PayloadDirectory, "common", "nested", "tool.bin")));
            Assert.Equal(PrivateFileMode, File.GetUnixFileMode(Path.Combine(result.PayloadDirectory, "xeBuild", "xeBuild.exe")));
        }


        using JsonDocument activeRecord = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(supportRoot.DirectoryPath, "active.json")));
        Assert.Equal(2, activeRecord.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(manifest.PayloadId, activeRecord.RootElement.GetProperty("payloadId").GetString());
        Assert.Equal(manifest.ReleaseTag, activeRecord.RootElement.GetProperty("releaseTag").GetString());
        Assert.Equal(
            manifest.Archive.DownloadUri.AbsoluteUri,
            activeRecord.RootElement.GetProperty("sourceUrl").GetString());
        Assert.Equal(manifest.Archive.Sha256, activeRecord.RootElement.GetProperty("archiveSha256").GetString());
        Assert.Equal(
            manifest.CanonicalManifestSha256,
            activeRecord.RootElement.GetProperty("canonicalManifestSha256").GetString());
        Assert.True(activeRecord.RootElement.GetProperty("installedAtUtc").TryGetDateTimeOffset(out DateTimeOffset installedAtUtc));
        Assert.Equal(TimeSpan.Zero, installedAtUtc.Offset);
        string generationId = Path.GetFileName(result.PayloadDirectory);
        Assert.Equal(generationId, activeRecord.RootElement.GetProperty("generationId").GetString());
        Assert.Equal(
            $"installations/{generationId}",
            activeRecord.RootElement.GetProperty("activePayloadPath").GetString());
        Assert.Equal(manifest.ReleaseTag, result.ReleaseTag);
        Assert.Equal(manifest.CanonicalManifestSha256, result.CanonicalManifestSha256);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Completion_activation_progress_callback_failure_retains_the_published_generation(bool hasPriorGeneration)
    {
        byte[] tool = [0x10, 0x20, 0x30];
        byte[] executable = [0x40, 0x50];
        byte[] archive = CreateArchive(("common/tool.bin", tool), ("xeBuild/xeBuild.exe", executable));
        SupportManifest manifest = CreateManifest(archive, ("common/tool.bin", tool), ("xeBuild/xeBuild.exe", executable));

        using var temporaryDirectory = new TemporaryDirectory();
        SupportRoot supportRoot = CreateSupportRoot(temporaryDirectory.Path);
        using var httpClient = CreateHttpClient(archive);
        var installer = new SupportPayloadInstaller(httpClient, manifest);
        var validator = new SupportPayloadValidator(manifest);
        SupportInstallationResult? priorInstallation = null;
        if (hasPriorGeneration)
        {
            priorInstallation = await installer.InstallAsync(supportRoot);
            Assert.Equal(SupportStatusKind.Valid, (await validator.ValidateAsync(supportRoot)).Status);
        }

        var callbackException = new InvalidOperationException("The final activation progress callback failed.");
        var progress = new CallbackProgress(value =>
        {
            if (value.Kind == "activating-support" && value.Message == "Activated the verified support payload.")
            {
                throw callbackException;
            }
        });

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => installer.InstallAsync(supportRoot, progress));

        Assert.Same(callbackException, exception);
        string activationPath = Path.Combine(supportRoot.DirectoryPath, "active.json");
        Assert.True(File.Exists(activationPath));
        using JsonDocument activeRecord = JsonDocument.Parse(await File.ReadAllTextAsync(activationPath));
        string generationId = Assert.IsType<string>(activeRecord.RootElement.GetProperty("generationId").GetString());
        Assert.Equal(
            $"installations/{generationId}",
            activeRecord.RootElement.GetProperty("activePayloadPath").GetString());
        string generationDirectory = Path.Combine(supportRoot.DirectoryPath, "installations", generationId);
        string[] generations = Directory.GetFileSystemEntries(Path.Combine(supportRoot.DirectoryPath, "installations"));
        Assert.Equal(hasPriorGeneration ? 2 : 1, generations.Length);
        Assert.Contains(generationDirectory, generations);
        Assert.True(Directory.Exists(generationDirectory));
        Assert.Equal(tool, await File.ReadAllBytesAsync(Path.Combine(generationDirectory, "common", "tool.bin")));
        Assert.Equal(executable, await File.ReadAllBytesAsync(Path.Combine(generationDirectory, "xeBuild", "xeBuild.exe")));
        if (priorInstallation is not null)
        {
            Assert.NotEqual(priorInstallation.PayloadDirectory, generationDirectory);
            Assert.Contains(priorInstallation.PayloadDirectory, generations);
            Assert.Equal(
                tool,
                await File.ReadAllBytesAsync(Path.Combine(priorInstallation.PayloadDirectory, "common", "tool.bin")));
            Assert.Equal(
                executable,
                await File.ReadAllBytesAsync(Path.Combine(priorInstallation.PayloadDirectory, "xeBuild", "xeBuild.exe")));
        }

        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(supportRoot.DirectoryPath, ".downloads")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(supportRoot.DirectoryPath, ".staging")));
        SupportStatusResult status = await validator.ValidateAsync(supportRoot);
        Assert.Equal(SupportStatusKind.Valid, status.Status);
        Assert.Equal(generationId, status.GenerationId);
        Assert.Null(status.ProblemKind);
        Assert.Null(status.ProblemPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initial_activation_progress_callback_failure_cleans_up_the_unpublished_generation(bool hasPriorGeneration)
    {
        byte[] tool = [0x10, 0x20, 0x30];
        byte[] executable = [0x40, 0x50];
        byte[] archive = CreateArchive(("common/tool.bin", tool), ("xeBuild/xeBuild.exe", executable));
        SupportManifest manifest = CreateManifest(archive, ("common/tool.bin", tool), ("xeBuild/xeBuild.exe", executable));

        using var temporaryDirectory = new TemporaryDirectory();
        SupportRoot supportRoot = CreateSupportRoot(temporaryDirectory.Path);
        using var httpClient = CreateHttpClient(archive);
        var installer = new SupportPayloadInstaller(httpClient, manifest);
        var validator = new SupportPayloadValidator(manifest);
        string activationPath = Path.Combine(supportRoot.DirectoryPath, "active.json");
        SupportInstallationResult? priorInstallation = null;
        byte[]? priorActiveRecord = null;
        if (hasPriorGeneration)
        {
            priorInstallation = await installer.InstallAsync(supportRoot);
            priorActiveRecord = await File.ReadAllBytesAsync(activationPath);
            Assert.Equal(SupportStatusKind.Valid, (await validator.ValidateAsync(supportRoot)).Status);
        }

        var callbackException = new InvalidOperationException("The initial activation progress callback failed.");
        var progress = new CallbackProgress(value =>
        {
            if (value.Kind == "activating-support" && value.Message == "Activating the verified support payload.")
            {
                throw callbackException;
            }
        });

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => installer.InstallAsync(supportRoot, progress));

        Assert.Same(callbackException, exception);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(supportRoot.DirectoryPath, ".downloads")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(supportRoot.DirectoryPath, ".staging")));
        string[] generations = Directory.GetFileSystemEntries(Path.Combine(supportRoot.DirectoryPath, "installations"));
        SupportStatusResult status = await validator.ValidateAsync(supportRoot);
        if (priorInstallation is null)
        {
            Assert.Empty(generations);
            Assert.False(File.Exists(activationPath));
            Assert.Equal(SupportStatusKind.Absent, status.Status);
            Assert.Null(status.GenerationId);
        }
        else
        {
            Assert.Equal(priorInstallation.PayloadDirectory, Assert.Single(generations));
            Assert.Equal(priorActiveRecord, await File.ReadAllBytesAsync(activationPath));
            Assert.Equal(
                tool,
                await File.ReadAllBytesAsync(Path.Combine(priorInstallation.PayloadDirectory, "common", "tool.bin")));
            Assert.Equal(
                executable,
                await File.ReadAllBytesAsync(Path.Combine(priorInstallation.PayloadDirectory, "xeBuild", "xeBuild.exe")));
            Assert.Equal(SupportStatusKind.Valid, status.Status);
            Assert.Equal(Path.GetFileName(priorInstallation.PayloadDirectory), status.GenerationId);
        }

        Assert.Null(status.ProblemKind);
        Assert.Null(status.ProblemPath);
    }


    [Fact]
    public async Task Verified_local_archive_installs_without_downloading()
    {
        byte[] tool = [0x10, 0x20, 0x30];
        byte[] archive = CreateArchive(("common/tool.bin", tool));
        SupportManifest manifest = CreateManifest(archive, ("common/tool.bin", tool));

        using var temporaryDirectory = new TemporaryDirectory();
        string localArchivePath = Path.Combine(temporaryDirectory.Path, "supplied.zip");
        await File.WriteAllBytesAsync(localArchivePath, archive);
        SupportRoot supportRoot = CreateSupportRoot(temporaryDirectory.Path);
        using var httpClient = new HttpClient(new FailingHandler());
        var installer = new SupportPayloadInstaller(httpClient, manifest);

        SupportInstallationResult result = await installer.InstallAsync(
            new SupportInstallationRequest(
                supportRoot,
                new SupportArchiveSource.LocalFile(localArchivePath)));

        Assert.Equal(tool, await File.ReadAllBytesAsync(Path.Combine(result.PayloadDirectory, "common", "tool.bin")));
        Assert.Equal(manifest.Archive.Sha256, result.ArchiveSha256);
        Assert.Equal("installations/" + Path.GetFileName(result.PayloadDirectory), result.ActivePayloadPath);
    }

    [Fact]
    public async Task Local_archive_digest_mismatch_is_rejected_without_an_active_payload()
    {
        byte[] archive = CreateArchive(("common/tool.bin", new byte[] { 0x10 }));
        SupportManifest manifest = CreateManifest(archive, ("common/tool.bin", new byte[] { 0x10 }));
        byte[] alteredArchive = (byte[])archive.Clone();
        alteredArchive[^1] ^= 0xFF;

        using var temporaryDirectory = new TemporaryDirectory();
        string localArchivePath = Path.Combine(temporaryDirectory.Path, "altered.zip");
        await File.WriteAllBytesAsync(localArchivePath, alteredArchive);
        SupportRoot supportRoot = CreateSupportRoot(temporaryDirectory.Path);
        using var httpClient = new HttpClient(new FailingHandler());
        var installer = new SupportPayloadInstaller(httpClient, manifest);

        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(() =>
            installer.InstallAsync(
                new SupportInstallationRequest(
                    supportRoot,
                    new SupportArchiveSource.LocalFile(localArchivePath))));

        Assert.Equal(ExitCode.InvalidData, exception.Code);
        Assert.Equal("support-archive-digest-mismatch", exception.Kind);
        Assert.False(File.Exists(Path.Combine(supportRoot.DirectoryPath, "active.json")));
    }

    [Fact]
    public async Task Archive_digest_mismatch_leaves_no_active_payload()
    {
        byte[] archive = CreateArchive(("common/tool.bin", new byte[] { 0x10 }));
        SupportManifest manifest = CreateManifest(archive, ("common/tool.bin", new byte[] { 0x10 }));
        byte[] alteredArchive = (byte[])archive.Clone();
        alteredArchive[^1] ^= 0xFF;

        using var temporaryDirectory = new TemporaryDirectory();
        SupportRoot supportRoot = CreateSupportRoot(temporaryDirectory.Path);
        using var httpClient = CreateHttpClient(alteredArchive);
        var installer = new SupportPayloadInstaller(httpClient, manifest);

        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
            () => installer.InstallAsync(supportRoot));

        Assert.Equal(ExitCode.InvalidData, exception.Code);
        Assert.Equal("support-archive-digest-mismatch", exception.Kind);
        Assert.False(File.Exists(Path.Combine(supportRoot.DirectoryPath, "active.json")));
    }

    [Fact]
    public async Task Missing_manifest_declared_archive_entry_leaves_no_active_payload()
    {
        byte[] tool = [0x10];
        byte[] executable = [0x20];
        byte[] archive = CreateArchive(("common/tool.bin", tool));
        SupportManifest manifest = CreateManifest(
            archive,
            ("common/tool.bin", tool),
            ("xeBuild/xeBuild.exe", executable));

        using var temporaryDirectory = new TemporaryDirectory();
        SupportRoot supportRoot = CreateSupportRoot(temporaryDirectory.Path);
        using var httpClient = CreateHttpClient(archive);
        var installer = new SupportPayloadInstaller(httpClient, manifest);

        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
            () => installer.InstallAsync(supportRoot));

        Assert.Equal(ExitCode.InvalidData, exception.Code);
        Assert.Equal("support-archive-entry-missing", exception.Kind);
        Assert.False(File.Exists(Path.Combine(supportRoot.DirectoryPath, "active.json")));
    }

    [Fact]
    public async Task Duplicate_archive_entry_leaves_no_active_payload()
    {
        byte[] tool = [0x10];
        byte[] archive = CreateArchive(
            ("common/tool.bin", tool),
            ("common/tool.bin", tool));
        SupportManifest manifest = CreateManifest(archive, ("common/tool.bin", tool));

        using var temporaryDirectory = new TemporaryDirectory();
        SupportRoot supportRoot = CreateSupportRoot(temporaryDirectory.Path);
        using var httpClient = CreateHttpClient(archive);
        var installer = new SupportPayloadInstaller(httpClient, manifest);

        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
            () => installer.InstallAsync(supportRoot));

        Assert.Equal(ExitCode.InvalidData, exception.Code);
        Assert.Equal("support-archive-entry-duplicate", exception.Kind);
        Assert.False(File.Exists(Path.Combine(supportRoot.DirectoryPath, "active.json")));
    }

    [Fact]
    public async Task Per_file_digest_mismatch_after_outer_archive_verification_leaves_no_active_payload()
    {
        byte[] archiveContent = [0x10];
        byte[] manifestContent = [0x20];
        byte[] archive = CreateArchive(("common/tool.bin", archiveContent));
        SupportManifest manifest = CreateManifest(archive, ("common/tool.bin", manifestContent));

        using var temporaryDirectory = new TemporaryDirectory();
        SupportRoot supportRoot = CreateSupportRoot(temporaryDirectory.Path);
        using var httpClient = CreateHttpClient(archive);
        var installer = new SupportPayloadInstaller(httpClient, manifest);

        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
            () => installer.InstallAsync(supportRoot));

        Assert.Equal(ExitCode.InvalidData, exception.Code);
        Assert.Equal("support-file-digest-mismatch", exception.Kind);
        Assert.False(File.Exists(Path.Combine(supportRoot.DirectoryPath, "active.json")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(supportRoot.DirectoryPath, "installations")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(supportRoot.DirectoryPath, ".staging")));
    }

    [Fact]
    public async Task Malformed_zip_with_matching_outer_digest_and_length_leaves_no_active_payload()
    {
        byte[] malformedArchive = [0x50, 0x4B, 0x03, 0x04];
        SupportManifest manifest = CreateManifest(malformedArchive, ("common/tool.bin", new byte[] { 0x10 }));

        using var temporaryDirectory = new TemporaryDirectory();
        SupportRoot supportRoot = CreateSupportRoot(temporaryDirectory.Path);
        using var httpClient = CreateHttpClient(malformedArchive);
        var installer = new SupportPayloadInstaller(httpClient, manifest);

        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
            () => installer.InstallAsync(supportRoot));

        Assert.Equal(ExitCode.InvalidData, exception.Code);
        Assert.Equal("support-archive-invalid", exception.Kind);
        Assert.False(File.Exists(Path.Combine(supportRoot.DirectoryPath, "active.json")));
    }

    [Fact]
    public async Task Undeclared_support_entry_does_not_replace_the_active_generation()
    {
        byte[] tool = [0x10];
        byte[] executable = [0x20];
        byte[] validArchive = CreateArchive(("common/tool.bin", tool), ("xeBuild/xeBuild.exe", executable));
        SupportManifest validManifest = CreateManifest(validArchive, ("common/tool.bin", tool), ("xeBuild/xeBuild.exe", executable));
        byte[] invalidArchive = CreateArchive(
            ("common/tool.bin", tool),
            ("xeBuild/xeBuild.exe", executable),
            ("common/undeclared.bin", new byte[] { 0x30 }));
        SupportManifest invalidManifest = CreateManifest(invalidArchive, ("common/tool.bin", tool), ("xeBuild/xeBuild.exe", executable));

        using var temporaryDirectory = new TemporaryDirectory();
        SupportRoot supportRoot = CreateSupportRoot(temporaryDirectory.Path);
        using (HttpClient validClient = CreateHttpClient(validArchive))
        {
            var validInstaller = new SupportPayloadInstaller(validClient, validManifest);
            _ = await validInstaller.InstallAsync(supportRoot);
        }

        string activationPath = Path.Combine(supportRoot.DirectoryPath, "active.json");
        byte[] activeRecord = await File.ReadAllBytesAsync(activationPath);
        using var invalidClient = CreateHttpClient(invalidArchive);
        var invalidInstaller = new SupportPayloadInstaller(invalidClient, invalidManifest);

        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
            () => invalidInstaller.InstallAsync(supportRoot));

        Assert.Equal(ExitCode.InvalidData, exception.Code);
        Assert.Equal("support-archive-entry-not-declared", exception.Kind);
        Assert.Equal(activeRecord, await File.ReadAllBytesAsync(activationPath));
    }

    [Fact]
    public async Task Traversal_entry_is_rejected_without_writing_outside_the_generation()
    {
        byte[] archive = CreateArchive(("common/../escape.bin", new byte[] { 0x10 }));
        SupportManifest manifest = CreateManifest(archive, ("common/tool.bin", new byte[] { 0x10 }));

        using var temporaryDirectory = new TemporaryDirectory();
        SupportRoot supportRoot = CreateSupportRoot(temporaryDirectory.Path);
        using var httpClient = CreateHttpClient(archive);
        var installer = new SupportPayloadInstaller(httpClient, manifest);

        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
            () => installer.InstallAsync(supportRoot));

        Assert.Equal(ExitCode.InvalidData, exception.Code);
        Assert.Equal("support-archive-path-invalid", exception.Kind);
        Assert.False(File.Exists(Path.Combine(temporaryDirectory.Path, "escape.bin")));
        Assert.False(File.Exists(Path.Combine(supportRoot.DirectoryPath, "active.json")));
    }

    [Fact]
    public async Task Symbolic_link_archive_entry_is_rejected_before_extraction()
    {
        byte[] linkTarget = [0x74, 0x61, 0x72, 0x67, 0x65, 0x74];
        byte[] archive = CreateSymbolicLinkArchive("common/tool.bin", linkTarget);
        SupportManifest manifest = CreateManifest(archive, ("common/tool.bin", linkTarget));

        using var temporaryDirectory = new TemporaryDirectory();
        SupportRoot supportRoot = CreateSupportRoot(temporaryDirectory.Path);
        using var httpClient = CreateHttpClient(archive);
        var installer = new SupportPayloadInstaller(httpClient, manifest);

        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
            () => installer.InstallAsync(supportRoot));

        Assert.Equal(ExitCode.InvalidData, exception.Code);
        Assert.Equal("support-archive-link-invalid", exception.Kind);
        Assert.False(File.Exists(Path.Combine(supportRoot.DirectoryPath, "active.json")));
    }

    [Fact]
    public async Task Symbolic_link_local_archive_is_rejected()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        byte[] archive = CreateArchive(("common/tool.bin", new byte[] { 0x10 }));
        SupportManifest manifest = CreateManifest(archive, ("common/tool.bin", new byte[] { 0x10 }));

        using var temporaryDirectory = new TemporaryDirectory();
        string targetPath = Path.Combine(temporaryDirectory.Path, "target.zip");
        string linkPath = Path.Combine(temporaryDirectory.Path, "link.zip");
        await File.WriteAllBytesAsync(targetPath, archive);
        File.CreateSymbolicLink(linkPath, targetPath);
        SupportRoot supportRoot = CreateSupportRoot(temporaryDirectory.Path);
        using var httpClient = new HttpClient(new FailingHandler());
        var installer = new SupportPayloadInstaller(httpClient, manifest);

        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(() =>
            installer.InstallAsync(
                new SupportInstallationRequest(
                    supportRoot,
                    new SupportArchiveSource.LocalFile(linkPath))));

        Assert.Equal(ExitCode.InvalidData, exception.Code);
        Assert.Equal("support-local-archive-link-invalid", exception.Kind);
        Assert.False(File.Exists(Path.Combine(supportRoot.DirectoryPath, "active.json")));
    }

    [Fact]
    public async Task Missing_final_response_uri_is_rejected_before_the_archive_is_read()
    {
        byte[] archive = CreateArchive(("common/tool.bin", new byte[] { 0x10 }));
        SupportManifest manifest = CreateManifest(archive, ("common/tool.bin", new byte[] { 0x10 }));

        using var temporaryDirectory = new TemporaryDirectory();
        SupportRoot supportRoot = CreateSupportRoot(temporaryDirectory.Path);
        using var httpClient = CreateHttpClient(archive, omitResponseRequestMessage: true);
        var installer = new SupportPayloadInstaller(httpClient, manifest);

        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
            () => installer.InstallAsync(supportRoot));

        Assert.Equal(ExitCode.InvalidData, exception.Code);
        Assert.Equal("support-download-redirect-invalid", exception.Kind);
        Assert.False(File.Exists(Path.Combine(supportRoot.DirectoryPath, "active.json")));
    }

    [Fact]
    public async Task Non_https_final_response_uri_is_rejected_before_the_archive_is_read()
    {
        byte[] archive = CreateArchive(("common/tool.bin", new byte[] { 0x10 }));
        SupportManifest manifest = CreateManifest(archive, ("common/tool.bin", new byte[] { 0x10 }));

        using var temporaryDirectory = new TemporaryDirectory();
        SupportRoot supportRoot = CreateSupportRoot(temporaryDirectory.Path);
        using var httpClient = CreateHttpClient(archive, new Uri("http://example.test/support.zip"));
        var installer = new SupportPayloadInstaller(httpClient, manifest);

        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
            () => installer.InstallAsync(supportRoot));

        Assert.Equal(ExitCode.InvalidData, exception.Code);
        Assert.Equal("support-download-redirect-invalid", exception.Kind);
        Assert.False(File.Exists(Path.Combine(supportRoot.DirectoryPath, "active.json")));
    }

    [Fact]
    public async Task Shared_writable_support_root_is_rejected_without_replacing_its_active_record()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        byte[] archive = CreateArchive(("common/tool.bin", new byte[] { 0x10 }));
        SupportManifest manifest = CreateManifest(archive, ("common/tool.bin", new byte[] { 0x10 }));

        using var temporaryDirectory = new TemporaryDirectory();
        SupportRoot supportRoot = new SupportRoot(temporaryDirectory.Path, SupportRootSource.Explicit);
        string activationPath = Path.Combine(supportRoot.DirectoryPath, "active.json");
        await File.WriteAllTextAsync(activationPath, "existing activation");
        File.SetUnixFileMode(
            temporaryDirectory.Path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        using var httpClient = CreateHttpClient(archive);
        var installer = new SupportPayloadInstaller(httpClient, manifest);

        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
            () => installer.InstallAsync(supportRoot));

        Assert.Equal(ExitCode.InputOutput, exception.Code);
        Assert.Equal("support-root-insecure", exception.Kind);
        Assert.Equal("existing activation", await File.ReadAllTextAsync(activationPath));
    }
    [Fact]
    public async Task Private_support_root_below_an_unsafe_ancestor_is_rejected_without_replacing_its_active_record()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        byte[] archive = CreateArchive(("common/tool.bin", new byte[] { 0x10 }));
        SupportManifest manifest = CreateManifest(archive, ("common/tool.bin", new byte[] { 0x10 }));

        using var temporaryDirectory = new TemporaryDirectory();
        string unsafeAncestor = Path.Combine(temporaryDirectory.Path, "unsafe-ancestor");
        string rootPath = Path.Combine(unsafeAncestor, "support");
        Directory.CreateDirectory(rootPath);
        File.SetUnixFileMode(rootPath, PrivateDirectoryMode);
        File.SetUnixFileMode(
            unsafeAncestor,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        var supportRoot = new SupportRoot(rootPath, SupportRootSource.Explicit);
        string activationPath = Path.Combine(supportRoot.DirectoryPath, "active.json");
        await File.WriteAllTextAsync(activationPath, "existing activation");
        using var httpClient = CreateHttpClient(archive);
        var installer = new SupportPayloadInstaller(httpClient, manifest);

        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
            () => installer.InstallAsync(supportRoot));

        Assert.Equal(ExitCode.InputOutput, exception.Code);
        Assert.Equal("support-root-insecure", exception.Kind);
        Assert.Equal("existing activation", await File.ReadAllTextAsync(activationPath));
    }


    private static SupportRoot CreateSupportRoot(string path)
    {
        return new SupportRoot(Path.Combine(path, "support"), SupportRootSource.Explicit);
    }

    private static SupportManifest CreateManifest(
        byte[] archive,
        params (string Path, byte[] Content)[] files)
    {
        SupportFile[] manifestFiles = files
            .Select(file => new SupportFile(
                file.Path,
                file.Content.LongLength,
                Convert.ToHexString(SHA256.HashData(file.Content))))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();
        return new SupportManifest(
            schemaVersion: 1,
            payloadId: "test-support",
            archive: new SupportArchive(
                new Uri("https://example.test/support.zip"),
                archive.LongLength,
                Convert.ToHexString(SHA256.HashData(archive))),
            uncompressedByteLength: manifestFiles.Sum(file => file.ByteLength),
            files: new ReadOnlyCollection<SupportFile>(manifestFiles));
    }

    private static HttpClient CreateHttpClient(
        byte[] archive,
        Uri? finalUri = null,
        bool omitResponseRequestMessage = false)
    {
        return new HttpClient(
            new ArchiveHandler(archive, finalUri, omitResponseRequestMessage),
            disposeHandler: true);
    }

    private static byte[] CreateArchive(params (string Path, byte[] Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string path, byte[] content) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
                using Stream entryStream = entry.Open();
                entryStream.Write(content);
            }
        }

        return stream.ToArray();
    }

    private static byte[] CreateSymbolicLinkArchive(string path, byte[] target)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
            entry.ExternalAttributes = (0xA000 | 0x1FF) << 16;
            using Stream entryStream = entry.Open();
            entryStream.Write(target);
        }

        return stream.ToArray();
    }

    private sealed class CallbackProgress : IProgress<OperationProgress>
    {
        private readonly Action<OperationProgress> _callback;

        internal CallbackProgress(Action<OperationProgress> callback)
        {
            _callback = callback;
        }

        public void Report(OperationProgress value) => _callback(value);
    }

    private sealed class ArchiveHandler : HttpMessageHandler
    {
        private readonly byte[] _archive;
        private readonly Uri? _finalUri;
        private readonly bool _omitResponseRequestMessage;

        internal ArchiveHandler(byte[] archive, Uri? finalUri, bool omitResponseRequestMessage)
        {
            _archive = archive;
            _finalUri = finalUri;
            _omitResponseRequestMessage = omitResponseRequestMessage;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_archive),
            };
            if (!_omitResponseRequestMessage)
            {
                response.RequestMessage = _finalUri is null
                    ? request
                    : new HttpRequestMessage(HttpMethod.Get, _finalUri);
            }

            response.Content.Headers.ContentLength = _archive.LongLength;
            return Task.FromResult(response);
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("The local archive path must not use HTTP.");
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"jrunner-support-installer-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(Path, PrivateDirectoryMode);
            }

        }

        internal string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
