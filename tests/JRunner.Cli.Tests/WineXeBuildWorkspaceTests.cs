using System.Security.Cryptography;
using System.Text;
using JRunner.Cli.Infrastructure;
using JRunner.Core.Configuration;
using JRunner.Core.Contracts;
using JRunner.Core.Nand.Models;
using JRunner.Core.Nand.Security;
using JRunner.Core.XeBuild;
using JRunner.Core.XeBuild.Preparation;
using Xunit;

namespace JRunner.Cli.Tests;

public sealed class WineXeBuildWorkspaceTests
{
    private const string CpuKeyText = "00112233445566778899AABBCCDDEEFF";
    private const long SystemPartitionByteLength = 0x3000000L;
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode PrivateDirectoryMode = PrivateFileMode | UnixFileMode.UserExecute;
    private static readonly byte[] StartMarker = [0x11, 0x22, 0x33, 0x44];
    private static readonly byte[] SystemEndMarker = [0x55, 0x66, 0x77, 0x88];
    private static readonly byte[] DataMarker = [0x91, 0xA2, 0xB3, 0xC4];
    private static readonly byte[] EndMarker = [0xD5, 0xE6, 0xF7, 0x18];

    [Fact]
    public async Task Creation_reserves_unique_private_workspaces_with_fresh_empty_managed_prefixes()
    {
        using var temporary = new TemporaryDirectory();
        string workspaceRoot = Path.Combine(temporary.Path, "workspaces");
        await using WineXeBuildWorkspace first = await WineXeBuildWorkspace.CreateAsync(
            workspaceRoot, CancellationToken.None);

        Assert.Equal(Path.Combine(first.RootDirectory, "wine-prefix"), first.WinePrefixDirectory);
        Assert.Equal(new[] { first.WinePrefixDirectory }, Directory.GetFileSystemEntries(first.RootDirectory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(first.WinePrefixDirectory));
        await File.WriteAllTextAsync(Path.Combine(first.WinePrefixDirectory, "user.reg"), "first operation only");

        await using WineXeBuildWorkspace second = await WineXeBuildWorkspace.CreateAsync(
            workspaceRoot, CancellationToken.None);

        Assert.NotEqual(first.RootDirectory, second.RootDirectory);
        Assert.Equal(Path.Combine(second.RootDirectory, "wine-prefix"), second.WinePrefixDirectory);
        Assert.Equal(new[] { second.WinePrefixDirectory }, Directory.GetFileSystemEntries(second.RootDirectory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(second.WinePrefixDirectory));
        Assert.Equal("first operation only", await File.ReadAllTextAsync(Path.Combine(first.WinePrefixDirectory, "user.reg")));
        if (!OperatingSystem.IsWindows())
        {
            foreach (string directory in new[]
            {
                workspaceRoot, first.RootDirectory, first.WinePrefixDirectory, second.RootDirectory, second.WinePrefixDirectory,
            })
            {
                Assert.Equal(PrivateDirectoryMode, File.GetUnixFileMode(directory));
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Canceled_creation_does_not_create_or_modify_workspace_directories(bool existingRoot)
    {
        using var temporary = new TemporaryDirectory();
        string workspaceRoot = Path.Combine(temporary.Path, "workspaces");
        string sentinel = Path.Combine(workspaceRoot, "existing.txt");
        if (existingRoot)
        {
            Directory.CreateDirectory(workspaceRoot);
            await File.WriteAllTextAsync(sentinel, "existing workspace root");
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Task<WineXeBuildWorkspace> creation = WineXeBuildWorkspace.CreateAsync(workspaceRoot, cancellation.Token);
        OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => creation);

        Assert.True(creation.IsCanceled);
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Equal(existingRoot, Directory.Exists(workspaceRoot));
        if (existingRoot)
        {
            Assert.Equal(new[] { sentinel }, Directory.GetFileSystemEntries(workspaceRoot));
            Assert.Equal("existing workspace root", await File.ReadAllTextAsync(sentinel));
        }
    }

    [Fact]
    public async Task Support_staging_materializes_only_the_declared_closure_and_ordered_overlays()
    {
        using var temporary = new TemporaryDirectory();
        Dictionary<string, byte[]> files = CreateSupportFiles();
        string generation = Path.Combine(temporary.Path, "generation");
        await WriteSupportFilesAsync(generation, files);
        XeBuildPreparedPlan plan = CreatePlan(files, drivePatch: XeBuildDrivePatch.Usb);
        plan = WithWorkspaceLayout(
            plan,
            plan.WorkspaceOverlays.Concat(
            [
                new XeBuildWorkspaceOverlay("xeBuild/common/smc_selected.bin", "xeBuild/data/SMC.bin"),
                new XeBuildWorkspaceOverlay("xeBuild/common/smc_selected.bin", "xeBuild/overlay-parent/nested/SMC.bin"),
                new XeBuildWorkspaceOverlay("xeBuild/common/smc_selected.bin", "xeBuild/common/xell-images/glitch2/SMC.bin"),
            ]),
            plan.RequiredWorkspaceDirectories.Append("xeBuild/generated/nested"));
        await using WineXeBuildWorkspace workspace = await CreateWorkspaceAsync(temporary.Path);

        await workspace.StageSupportAsync(generation, plan, CancellationToken.None);

        Assert.Equal(ExpectedStagedFiles(plan), RelativeFiles(workspace.RootDirectory));
        foreach (string directory in plan.RequiredWorkspaceDirectories)
        {
            Assert.True(Directory.Exists(Resolve(workspace.RootDirectory, directory)));
        }
        if (OperatingSystem.IsLinux())
        {
            foreach (string directory in Directory.EnumerateDirectories(
                         workspace.RootDirectory, "*", SearchOption.AllDirectories).Prepend(workspace.RootDirectory))
            {
                Assert.Equal(PrivateDirectoryMode, File.GetUnixFileMode(directory));
            }
        }

        Assert.Equal(files["xeBuild/17559/xl_usb/xam.xex"],
            await File.ReadAllBytesAsync(Resolve(workspace.RootDirectory, "xeBuild/17559/xam.xex")));
        Assert.Equal(files["xeBuild/17559/xl_usb/_glitch2.ini"],
            await File.ReadAllBytesAsync(Resolve(workspace.RootDirectory, plan.BuildModeIniPath)));
        Assert.Equal(files["xeBuild/common/smc_selected.bin"],
            await File.ReadAllBytesAsync(Resolve(workspace.RootDirectory, "xeBuild/data/SMC.bin")));
        Assert.Equal(files["xeBuild/common/smc_selected.bin"],
            await File.ReadAllBytesAsync(Resolve(workspace.RootDirectory, "xeBuild/overlay-parent/nested/SMC.bin")));
        Assert.Equal(files["xeBuild/common/smc_selected.bin"],
            await File.ReadAllBytesAsync(Resolve(workspace.RootDirectory, "xeBuild/common/xell-images/glitch2/SMC.bin")));
        Assert.False(Directory.Exists(Resolve(workspace.RootDirectory, "xeBuild/17489")));
        Assert.False(File.Exists(Resolve(workspace.RootDirectory, "xeBuild/unselected.exe")));
        Assert.False(File.Exists(Resolve(workspace.RootDirectory, "xeBuild/launch.xex")));
        await AssertSupportUnchangedAsync(generation, files);
    }

    [Theory]
    [InlineData("glitch2", "true")]
    [InlineData("retail", "false")]
    [InlineData("jtag", "false")]
    public async Task Smc_selection_and_reset_policy_change_only_workspace_options(string type, string expectedPatchSmc)
    {
        using var temporary = new TemporaryDirectory();
        Dictionary<string, byte[]> files = CreateSupportFiles();
        string generation = Path.Combine(temporary.Path, "generation");
        await WriteSupportFilesAsync(generation, files);
        XeBuildPreparedPlan plan = CreatePlan(files, type);
        plan = WithWorkspaceLayout(
            plan,
            plan.WorkspaceOverlays.Append(
                new XeBuildWorkspaceOverlay("xeBuild/common/smc_selected.bin", "xeBuild/data/SMC.bin")),
            plan.RequiredWorkspaceDirectories);
        await using WineXeBuildWorkspace workspace = await CreateWorkspaceAsync(temporary.Path);

        await workspace.StageSupportAsync(generation, plan, CancellationToken.None);

        IniParseResult options = IniParser.Parse(
            await File.ReadAllTextAsync(Resolve(workspace.RootDirectory, "xeBuild/data/options.ini")));
        Assert.False(options.HasErrors);
        Assert.True(options.Document.TryGetValue(null, "patchsmc", out string? patchSmc));
        Assert.Equal(expectedPatchSmc, patchSmc);
        Assert.Equal(files["xeBuild/options.ini"],
            await File.ReadAllBytesAsync(Resolve(workspace.RootDirectory, "xeBuild/options.ini")));
        Assert.Equal(files["xeBuild/common/smc_selected.bin"],
            await File.ReadAllBytesAsync(Resolve(workspace.RootDirectory, "xeBuild/data/SMC.bin")));
        await AssertSupportUnchangedAsync(generation, files);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DashLaunch_stages_root_assets_replaces_dashboard_configuration_and_patches_selected_ini(
        bool hasDashboardConfiguration)
    {
        using var temporary = new TemporaryDirectory();
        Dictionary<string, byte[]> files = CreateSupportFiles(hasDashboardConfiguration);
        string generation = Path.Combine(temporary.Path, "generation");
        await WriteSupportFilesAsync(generation, files);
        XeBuildPreparedPlan plan = CreatePlan(files, dashLaunch: true, drivePatch: XeBuildDrivePatch.Usb);
        await using WineXeBuildWorkspace workspace = await CreateWorkspaceAsync(temporary.Path);

        await workspace.StageSupportAsync(generation, plan, CancellationToken.None);

        XeBuildDashLaunchPlan dashLaunch = Assert.IsType<XeBuildDashLaunchPlan>(plan.DashLaunch);
        Assert.Equal(ExpectedStagedFiles(plan), RelativeFiles(workspace.RootDirectory));
        foreach (string rootAsset in dashLaunch.RequiredSupportFiles)
        {
            Assert.Equal(files[rootAsset], await File.ReadAllBytesAsync(Resolve(workspace.RootDirectory, rootAsset)));
        }

        Assert.Equal(files[dashLaunch.LaunchConfigurationSupportPath],
            await File.ReadAllBytesAsync(Resolve(workspace.RootDirectory, dashLaunch.DashboardLaunchConfigurationPath)));
        IniParseResult mode = IniParser.Parse(
            await File.ReadAllTextAsync(Resolve(workspace.RootDirectory, plan.BuildModeIniPath)));
        Assert.False(mode.HasErrors);
        Assert.Contains("overlay.bin,0", mode.Document.GetLiterals(plan.RequiredIniLabel).Select(line => line.Text));
        string[] flashFiles = mode.Document.GetLiterals("flashfs").Select(line => line.Text).ToArray();
        Assert.Contains("xam.xex", flashFiles);
        foreach (string entry in dashLaunch.IniPatchEntries)
        {
            Assert.Single(flashFiles, value => string.Equals(value, entry, StringComparison.Ordinal));
        }

        Assert.Equal(files["xeBuild/17559/_retail.ini"],
            await File.ReadAllBytesAsync(Resolve(workspace.RootDirectory, "xeBuild/17559/_retail.ini")));
        await AssertSupportUnchangedAsync(generation, files);
    }

    [Theory]
    [InlineData(XeBuildFourGigabyteStagingPolicy.SystemPartitionOnly, SystemPartitionByteLength)]
    [InlineData(XeBuildFourGigabyteStagingPolicy.FullData, XeBuildSourceContext.FourGigabyteEmmcByteLength)]
    public async Task Exact_four_gigabyte_sources_obey_the_explicit_staging_policy_without_dense_multi_gigabyte_writes(
        XeBuildFourGigabyteStagingPolicy policy,
        long expectedLength)
    {
        using var temporary = new TemporaryDirectory();
        string inputPath = Path.Combine(temporary.Path, "sparse-emmc.bin");
        await CreateSparseFileAsync(inputPath, XeBuildSourceContext.FourGigabyteEmmcByteLength, writeMarkers: true);
        var source = new XeBuildSourceContext(
            inputPath, XeBuildSourceContext.FourGigabyteEmmcByteLength, await HashSourceAsync(inputPath));
        await using WineXeBuildWorkspace workspace = await CreateWorkspaceAsync(temporary.Path);

        await workspace.StageInputAsync(source, policy, CancellationToken.None);

        Assert.Equal(expectedLength, new FileInfo(workspace.StagedInputPath).Length);
        Assert.Equal(StartMarker, await ReadAtAsync(workspace.StagedInputPath, 0, StartMarker.Length));
        Assert.Equal(SystemEndMarker,
            await ReadAtAsync(workspace.StagedInputPath, SystemPartitionByteLength - SystemEndMarker.Length, SystemEndMarker.Length));
        Assert.Equal(new byte[4], await ReadAtAsync(workspace.StagedInputPath, 0x2000000, 4));
        if (policy is XeBuildFourGigabyteStagingPolicy.FullData)
        {
            Assert.Equal(DataMarker,
                await ReadAtAsync(workspace.StagedInputPath, SystemPartitionByteLength, DataMarker.Length));
            Assert.Equal(EndMarker,
                await ReadAtAsync(workspace.StagedInputPath, expectedLength - EndMarker.Length, EndMarker.Length));
        }

        AssertPrivateFile(workspace.StagedInputPath);
        Assert.Equal(XeBuildSourceContext.FourGigabyteEmmcByteLength, new FileInfo(inputPath).Length);
        Assert.Equal(EndMarker,
            await ReadAtAsync(inputPath, XeBuildSourceContext.FourGigabyteEmmcByteLength - EndMarker.Length, EndMarker.Length));
    }

    [Theory]
    [InlineData(XeBuildSourceContext.FourGigabyteEmmcByteLength, XeBuildFourGigabyteStagingPolicy.None,
        "xebuild-4gb-staging-policy-required")]
    [InlineData(XeBuildSourceContext.FourGigabyteEmmcByteLength - 1, XeBuildFourGigabyteStagingPolicy.SystemPartitionOnly,
        "xebuild-4gb-staging-policy-unexpected")]
    [InlineData(XeBuildSourceContext.FourGigabyteEmmcByteLength + 1, XeBuildFourGigabyteStagingPolicy.FullData,
        "xebuild-4gb-staging-policy-unexpected")]
    [InlineData(256L, XeBuildFourGigabyteStagingPolicy.FullData, "xebuild-4gb-staging-policy-unexpected")]
    public async Task Staging_policies_are_required_only_for_the_exact_four_gigabyte_length(
        long byteLength,
        XeBuildFourGigabyteStagingPolicy policy,
        string expectedKind)
    {
        using var temporary = new TemporaryDirectory();
        string inputPath = Path.Combine(temporary.Path, "input.bin");
        await CreateSparseFileAsync(inputPath, byteLength, writeMarkers: false);
        var source = new XeBuildSourceContext(inputPath, byteLength, await HashSourceAsync(inputPath));
        await using WineXeBuildWorkspace workspace = await CreateWorkspaceAsync(temporary.Path);

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => workspace.StageInputAsync(source, policy, CancellationToken.None));

        Assert.Equal(ExitCode.Usage, failure.Code);
        Assert.Equal(expectedKind, failure.Kind);
        Assert.False(File.Exists(workspace.StagedInputPath));
        Assert.Equal(byteLength, new FileInfo(inputPath).Length);
    }

    [Theory]
    [InlineData(UnixFileMode.GroupWrite)]
    [InlineData(UnixFileMode.OtherWrite)]
    [InlineData(UnixFileMode.GroupWrite | UnixFileMode.StickyBit)]
    [InlineData(UnixFileMode.OtherWrite | UnixFileMode.StickyBit)]
    public async Task Writable_source_is_rejected_when_reopened_before_staging_a_source_key_or_process(
        UnixFileMode unsafeBits)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        string inputPath = Path.Combine(temporary.Path, "input.bin");
        byte[] original = Encoding.ASCII.GetBytes("private source snapshot sentinel");
        await WriteSourceAsync(inputPath, original);
        var source = new XeBuildSourceContext(inputPath, original.Length, await HashSourceAsync(inputPath));
        UnixFileMode originalMode = File.GetUnixFileMode(inputPath);
        UnixFileMode unsafeMode = originalMode | unsafeBits;
        File.SetUnixFileMode(inputPath, unsafeMode);
        try
        {
            await AssertRejectedSourceIsNotStagedAsync(temporary, source, "xebuild-input-unsafe");

            Assert.Equal(original, await File.ReadAllBytesAsync(inputPath));
            Assert.Equal(unsafeMode, File.GetUnixFileMode(inputPath));
        }
        finally
        {
            File.SetUnixFileMode(inputPath, originalMode);
        }
    }

    [Theory]
    [InlineData("writable-parent")]
    [InlineData("writable-ancestor")]
    [InlineData("source-symlink")]
    [InlineData("dangling-source-symlink")]
    [InlineData("ancestor-symlink")]
    public async Task Unsafe_source_path_is_rejected_when_reopened_before_staging_or_process(string unsafePathKind)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        string ancestor = Path.Combine(temporary.Path, "source-parent");
        string parent = Path.Combine(ancestor, "nested");
        Directory.CreateDirectory(parent);
        File.SetUnixFileMode(ancestor, PrivateDirectoryMode);
        File.SetUnixFileMode(parent, PrivateDirectoryMode);
        string inputPath = Path.Combine(parent, "input.bin");
        byte[] original = Encoding.ASCII.GetBytes("private source snapshot sentinel");
        await WriteSourceAsync(inputPath, original);
        byte[] originalSha256 = await HashSourceAsync(inputPath);
        string selectedInputPath = inputPath;
        string? unsafeDirectory = null;
        if (unsafePathKind is "source-symlink" or "dangling-source-symlink")
        {
            selectedInputPath = Path.Combine(temporary.Path, "source-link.bin");
            string linkTarget = unsafePathKind == "dangling-source-symlink"
                ? Path.Combine(parent, "missing.bin")
                : inputPath;
            File.CreateSymbolicLink(selectedInputPath, linkTarget);
        }
        else if (unsafePathKind == "ancestor-symlink")
        {
            string link = Path.Combine(temporary.Path, "source-link");
            Directory.CreateSymbolicLink(link, parent);
            selectedInputPath = Path.Combine(link, "input.bin");
        }
        else
        {
            unsafeDirectory = unsafePathKind == "writable-parent" ? parent : ancestor;
            File.SetUnixFileMode(unsafeDirectory, PrivateDirectoryMode | UnixFileMode.OtherWrite);
        }

        try
        {
            var source = new XeBuildSourceContext(selectedInputPath, original.Length, originalSha256);
            await AssertRejectedSourceIsNotStagedAsync(temporary, source, "xebuild-input-unsafe");

            Assert.Equal(original, await File.ReadAllBytesAsync(inputPath));
        }
        finally
        {
            if (unsafeDirectory is not null)
            {
                File.SetUnixFileMode(unsafeDirectory, PrivateDirectoryMode);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_source_is_rejected_before_staging_an_image_key_or_process(bool missingAncestor)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        string parent = temporary.Path;
        if (missingAncestor)
        {
            parent = Path.Combine(temporary.Path, "source-parent");
            Directory.CreateDirectory(parent);
            File.SetUnixFileMode(parent, PrivateDirectoryMode);
        }

        string inputPath = Path.Combine(parent, "input.bin");
        byte[] original = Encoding.ASCII.GetBytes("private source snapshot sentinel");
        await WriteSourceAsync(inputPath, original);
        var source = new XeBuildSourceContext(inputPath, original.Length, await HashSourceAsync(inputPath));
        File.Delete(inputPath);
        if (missingAncestor)
        {
            Directory.Delete(parent);
        }

        OperationFailureException directFailure = Assert.Throws<OperationFailureException>(
            () => XeBuildSourceFile.Open(inputPath));

        Assert.Equal(ExitCode.InputOutput, directFailure.Code);
        Assert.Equal("xebuild-input-read-failed", directFailure.Kind);
        Assert.DoesNotContain(inputPath, directFailure.Message, StringComparison.Ordinal);
        await AssertRejectedSourceIsNotStagedAsync(temporary, source, "xebuild-input-missing");
    }

    [Theory]
    [InlineData(255L)]
    [InlineData(257L)]
    public async Task Source_length_changes_are_rejected_before_creating_a_staged_image(long observedByteLength)
    {
        using var temporary = new TemporaryDirectory();
        string inputPath = Path.Combine(temporary.Path, "input.bin");
        await CreateSparseFileAsync(inputPath, 256, writeMarkers: false);
        var source = new XeBuildSourceContext(inputPath, observedByteLength, await HashSourceAsync(inputPath));
        await using WineXeBuildWorkspace workspace = await CreateWorkspaceAsync(temporary.Path);

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => workspace.StageInputAsync(
                source,
                XeBuildFourGigabyteStagingPolicy.None,
                CancellationToken.None));

        Assert.Equal(ExitCode.InvalidData, failure.Code);
        Assert.Equal("xebuild-input-length-mismatch", failure.Kind);
        Assert.False(File.Exists(workspace.StagedInputPath));
        Assert.Equal(256L, new FileInfo(inputPath).Length);
    }

    [Fact]
    public async Task Same_length_pathname_replacements_are_rejected_and_remove_the_partial_staged_source()
    {
        using var temporary = new TemporaryDirectory();
        string inputPath = Path.Combine(temporary.Path, "input.bin");
        byte[] original = [1, 2, 3, 4];
        byte[] replacement = [4, 3, 2, 1];
        await WriteSourceAsync(inputPath, original);
        var source = new XeBuildSourceContext(inputPath, original.Length, await HashSourceAsync(inputPath));
        string replacementPath = Path.Combine(temporary.Path, "replacement.bin");
        await WriteSourceAsync(replacementPath, replacement);
        File.Move(replacementPath, inputPath, overwrite: true);
        await using WineXeBuildWorkspace workspace = await CreateWorkspaceAsync(temporary.Path);

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => workspace.StageInputAsync(source, XeBuildFourGigabyteStagingPolicy.None, CancellationToken.None));

        Assert.Equal(ExitCode.InvalidData, failure.Code);
        Assert.Equal("xebuild-input-changed", failure.Kind);
        Assert.False(File.Exists(workspace.StagedInputPath));
        Assert.False(File.Exists(workspace.StagedCpuKeyPath));
        Assert.Equal(source.ByteLength, new FileInfo(inputPath).Length);
        Assert.Equal(replacement, await File.ReadAllBytesAsync(inputPath));
    }

    [Fact]
    public async Task System_partition_only_staging_verifies_the_unstaged_tail_before_any_external_process()
    {
        using var temporary = new TemporaryDirectory();
        string inputPath = Path.Combine(temporary.Path, "sparse-emmc.bin");
        await CreateSparseFileAsync(inputPath, XeBuildSourceContext.FourGigabyteEmmcByteLength, writeMarkers: true);
        var source = new XeBuildSourceContext(
            inputPath, XeBuildSourceContext.FourGigabyteEmmcByteLength, await HashSourceAsync(inputPath));
        await using (FileStream input = new(inputPath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            input.Position = XeBuildSourceContext.FourGigabyteEmmcByteLength - 1;
            await input.WriteAsync(new byte[] { 0x19 });
        }

        await using WineXeBuildWorkspace workspace = await CreateWorkspaceAsync(temporary.Path);
        var runner = new SourceStageRecordingProcessRunner();
        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(async () =>
        {
            await workspace.StageInputAsync(source, XeBuildFourGigabyteStagingPolicy.SystemPartitionOnly, CancellationToken.None);
            await workspace.StageCpuKeyAsync(CpuKey.Parse(CpuKeyText), CancellationToken.None);
            var invocation = new ExternalProcessInvocation("never-invoked-winepath", Array.Empty<string>(), workspace.RootDirectory);
            runner.EnsureExecutableAvailable(invocation);
            await runner.RunAsync(invocation, CancellationToken.None);
        });

        Assert.Equal(ExitCode.InvalidData, failure.Code);
        Assert.Equal("xebuild-input-changed", failure.Kind);
        Assert.Empty(runner.Calls);
        Assert.False(File.Exists(workspace.StagedInputPath));
        Assert.False(File.Exists(workspace.StagedCpuKeyPath));
        Assert.Equal(source.ByteLength, new FileInfo(inputPath).Length);
        Assert.Equal(StartMarker, await ReadAtAsync(inputPath, 0, StartMarker.Length));
        Assert.Equal(SystemEndMarker,
            await ReadAtAsync(inputPath, SystemPartitionByteLength - SystemEndMarker.Length, SystemEndMarker.Length));
        Assert.Equal(new byte[] { 0x19 },
            await ReadAtAsync(inputPath, XeBuildSourceContext.FourGigabyteEmmcByteLength - 1, 1));
        Assert.DoesNotContain(CpuKeyText, failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(PrivateFileMode)]
    [InlineData(PrivateFileMode | UnixFileMode.GroupRead)]
    [InlineData(PrivateFileMode | UnixFileMode.GroupRead | UnixFileMode.OtherRead)]
    public async Task Staged_source_and_cpu_key_are_owner_only_without_changing_the_callers_source_permissions(
        UnixFileMode originalMode)
    {
        using var temporary = new TemporaryDirectory();
        string inputPath = Path.Combine(temporary.Path, "input.bin");
        byte[] input = Encoding.ASCII.GetBytes("caller-owned source image");
        await WriteSourceAsync(inputPath, input);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(inputPath, originalMode);
        }

        await using WineXeBuildWorkspace workspace = await CreateWorkspaceAsync(temporary.Path);
        await workspace.StageInputAsync(
            new XeBuildSourceContext(inputPath, input.Length, await HashSourceAsync(inputPath)),
            XeBuildFourGigabyteStagingPolicy.None,
            CancellationToken.None);
        await workspace.StageCpuKeyAsync(CpuKey.Parse(CpuKeyText), CancellationToken.None);

        Assert.Equal(input, await File.ReadAllBytesAsync(workspace.StagedInputPath));
        Assert.Equal(string.Concat(CpuKeyText, "\n"), await File.ReadAllTextAsync(workspace.StagedCpuKeyPath));
        AssertPrivateFile(workspace.StagedInputPath);
        AssertPrivateFile(workspace.StagedCpuKeyPath);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(originalMode, File.GetUnixFileMode(inputPath));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cleanup_removes_source_key_derived_files_unexpected_logs_and_the_managed_prefix(
        bool retainDiagnostics)
    {
        using var temporary = new TemporaryDirectory();
        Dictionary<string, byte[]> files = CreateSupportFiles();
        string generation = Path.Combine(temporary.Path, "generation");
        await WriteSupportFilesAsync(generation, files);
        XeBuildPreparedPlan plan = CreatePlan(files);
        string inputPath = Path.Combine(temporary.Path, "input.bin");
        byte[] sourceBytes = Encoding.ASCII.GetBytes("private NAND image bytes");
        await WriteSourceAsync(inputPath, sourceBytes);
        await using WineXeBuildWorkspace workspace = await CreateWorkspaceAsync(temporary.Path);
        await workspace.StageSupportAsync(generation, plan, CancellationToken.None);
        await workspace.StageInputAsync(
            new XeBuildSourceContext(inputPath, sourceBytes.Length, await HashSourceAsync(inputPath)),
            XeBuildFourGigabyteStagingPolicy.None,
            CancellationToken.None);
        await workspace.StageCpuKeyAsync(CpuKey.Parse(CpuKeyText), CancellationToken.None);
        byte[] secret = Encoding.ASCII.GetBytes(CpuKeyText);
        await WriteFileAsync(workspace.RootDirectory, "diagnostics.txt", Encoding.UTF8.GetBytes("XeBuild failed; details redacted.\n"));
        await WriteFileAsync(workspace.RootDirectory, "unexpected-secret.log", secret);
        await WriteFileAsync(workspace.RootDirectory, "xeBuild/data/kv.bin", sourceBytes);
        await WriteFileAsync(workspace.RootDirectory, string.Concat(plan.BuildModeIniPath, ".bak"), secret);
        await WriteFileAsync(workspace.RootDirectory, "xeBuild/17559/vfuses_khv.bin", secret);
        await WriteFileAsync(workspace.RootDirectory, "xeBuild/17559/PATCH_sd.bin", sourceBytes);
        await WriteFileAsync(workspace.RootDirectory, "xeBuild/17559/xell_reason.bin", [0, 0x12]);
        await WriteFileAsync(workspace.RootDirectory, "xeBuild/unexpected/raw-process.log", secret);
        await WriteFileAsync(workspace.WinePrefixDirectory, "drive_c/raw-process.log", secret);
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(Path.Combine(workspace.WinePrefixDirectory, "support-link"), generation);
        }

        await workspace.CleanupAsync(retainDiagnostics);
        await workspace.DisposeAsync();

        Assert.False(File.Exists(workspace.StagedInputPath));
        Assert.False(File.Exists(workspace.StagedCpuKeyPath));
        Assert.False(Directory.Exists(Resolve(workspace.RootDirectory, "xeBuild/data")));
        Assert.False(Directory.Exists(workspace.WinePrefixDirectory));
        Assert.Equal(retainDiagnostics, Directory.Exists(workspace.RootDirectory));
        if (retainDiagnostics)
        {
            string[] expectedFiles = plan.RequiredSupportFiles
                .Concat(plan.WorkspaceOverlays.Select(overlay => overlay.DestinationWorkspacePath))
                .Where(path => !path.StartsWith("xeBuild/data/", StringComparison.Ordinal))
                .Append("diagnostics.txt")
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(expectedFiles, RelativeFiles(workspace.RootDirectory));
            foreach (string path in Directory.EnumerateFiles(workspace.RootDirectory, "*", SearchOption.AllDirectories))
            {
                Assert.False((await File.ReadAllTextAsync(path)).Contains(CpuKeyText, StringComparison.OrdinalIgnoreCase));
            }
        }

        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(inputPath));
        await AssertSupportUnchangedAsync(generation, files);
    }

    [Fact]
    public async Task Failed_support_staging_still_sanitizes_a_retained_partial_workspace()
    {
        using var temporary = new TemporaryDirectory();
        Dictionary<string, byte[]> files = CreateSupportFiles();
        XeBuildPreparedPlan plan = CreatePlan(files);
        string generation = Path.Combine(temporary.Path, "generation");
        files.Remove(plan.BuildModeIniPath);
        await WriteSupportFilesAsync(generation, files);
        string inputPath = Path.Combine(temporary.Path, "input.bin");
        byte[] sourceBytes = [1, 2, 3, 4];
        await WriteSourceAsync(inputPath, sourceBytes);
        await using WineXeBuildWorkspace workspace = await CreateWorkspaceAsync(temporary.Path);
        await workspace.StageInputAsync(
            new XeBuildSourceContext(inputPath, sourceBytes.Length, await HashSourceAsync(inputPath)),
            XeBuildFourGigabyteStagingPolicy.None,
            CancellationToken.None);
        await workspace.StageCpuKeyAsync(CpuKey.Parse(CpuKeyText), CancellationToken.None);

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => workspace.StageSupportAsync(generation, plan, CancellationToken.None));
        await workspace.CleanupAsync(retainDiagnostics: true);

        Assert.Equal("xebuild-support-missing", failure.Kind);
        Assert.True(Directory.Exists(workspace.RootDirectory));
        Assert.False(File.Exists(workspace.StagedInputPath));
        Assert.False(File.Exists(workspace.StagedCpuKeyPath));
        Assert.False(Directory.Exists(workspace.WinePrefixDirectory));
        Assert.All(RelativeFiles(workspace.RootDirectory), path => Assert.Contains(path, plan.RequiredSupportFiles));
        await AssertSupportUnchangedAsync(generation, files);
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(inputPath));
    }

    [Theory]
    [InlineData(UnixFileMode.GroupWrite, false)]
    [InlineData(UnixFileMode.OtherWrite, false)]
    [InlineData(UnixFileMode.GroupWrite | UnixFileMode.StickyBit, false)]
    [InlineData(UnixFileMode.OtherWrite | UnixFileMode.StickyBit, false)]
    [InlineData(UnixFileMode.GroupWrite, true)]
    [InlineData(UnixFileMode.OtherWrite, true)]
    public async Task Unsafe_workspace_root_or_ancestry_is_rejected_before_creating_children(
        UnixFileMode unsafeBits,
        bool unsafeAncestor)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        string unsafeDirectory = Path.Combine(temporary.Path, "unsafe");
        Directory.CreateDirectory(unsafeDirectory);
        UnixFileMode privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        UnixFileMode unsafeMode = privateMode | UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute | unsafeBits;
        File.SetUnixFileMode(unsafeDirectory, unsafeMode);
        string workspaceSentinel = Path.Combine(unsafeDirectory, "caller-owned.txt");
        await File.WriteAllTextAsync(workspaceSentinel, "caller-owned workspace root");
        string workspaceRoot = unsafeAncestor
            ? Path.Combine(unsafeDirectory, "missing", "workspaces")
            : unsafeDirectory;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(async () =>
        {
            await using WineXeBuildWorkspace workspace = await WineXeBuildWorkspace.CreateAsync(
                workspaceRoot, timeout.Token);
        });

        Assert.Equal(ExitCode.Usage, failure.Code);
        Assert.Equal("xebuild-workspace-path-unsafe", failure.Kind);

        Assert.Equal(unsafeMode, File.GetUnixFileMode(unsafeDirectory));
        Assert.Equal("caller-owned workspace root", await File.ReadAllTextAsync(workspaceSentinel));
        Assert.Equal(new[] { workspaceSentinel }, Directory.GetFileSystemEntries(unsafeDirectory));
        Assert.False(Directory.Exists(Path.Combine(unsafeDirectory, "missing")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Safe_workspace_ancestry_keeps_new_workspace_components_and_managed_prefix_owner_only(
        bool stickyAncestor)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        string ancestor = Path.Combine(temporary.Path, "ancestor");
        Directory.CreateDirectory(ancestor);
        UnixFileMode privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        UnixFileMode ancestorMode = privateMode | UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        if (stickyAncestor)
        {
            ancestorMode |= UnixFileMode.GroupWrite | UnixFileMode.OtherWrite | UnixFileMode.StickyBit;
        }
        File.SetUnixFileMode(ancestor, ancestorMode);
        string newParent = Path.Combine(ancestor, "new-private-parent");
        string workspaceRoot = Path.Combine(newParent, "workspaces");
        await using WineXeBuildWorkspace workspace = await WineXeBuildWorkspace.CreateAsync(
            workspaceRoot, CancellationToken.None);

        Assert.Equal(ancestorMode, File.GetUnixFileMode(ancestor));
        Assert.Equal(privateMode, File.GetUnixFileMode(newParent));
        Assert.Equal(privateMode, File.GetUnixFileMode(workspaceRoot));
        Assert.Equal(privateMode, File.GetUnixFileMode(workspace.RootDirectory));
        Assert.Equal(privateMode, File.GetUnixFileMode(workspace.WinePrefixDirectory));
        Assert.StartsWith(workspace.RootDirectory + Path.DirectorySeparatorChar, workspace.WinePrefixDirectory);
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.WinePrefixDirectory));

        await workspace.CleanupAsync(retainDiagnostics: false);

        Assert.False(Directory.Exists(workspace.RootDirectory));
        Assert.False(Directory.Exists(workspace.WinePrefixDirectory));
        Assert.Equal(privateMode, File.GetUnixFileMode(workspaceRoot));
        Assert.Equal(ancestorMode, File.GetUnixFileMode(ancestor));
    }

    [Fact]
    public void Private_directory_creation_protects_missing_components_without_changing_existing_ancestors()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        string ancestor = Path.Combine(temporary.Path, "caller-owned");
        Directory.CreateDirectory(ancestor);
        UnixFileMode ancestorMode = PrivateDirectoryMode | UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(ancestor, ancestorMode);
        string common = Path.Combine(ancestor, "common");
        string xellImages = Path.Combine(common, "xell-images");
        string glitch2 = Path.Combine(xellImages, "glitch2");

        WorkspacePathSafety.CreatePrivateDirectory(glitch2);

        Assert.Equal(ancestorMode, File.GetUnixFileMode(ancestor));
        foreach (string directory in new[] { common, xellImages, glitch2 })
        {
            Assert.Equal(PrivateDirectoryMode, File.GetUnixFileMode(directory));
        }
    }

    [Fact]
    public async Task Linked_mutable_directories_cannot_write_through_or_delete_support_files_during_cleanup()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        Dictionary<string, byte[]> files = CreateSupportFiles();
        string generation = Path.Combine(temporary.Path, "generation");
        await WriteSupportFilesAsync(generation, files);
        XeBuildPreparedPlan plan = CreatePlan(files);
        string inputPath = Path.Combine(temporary.Path, "input.bin");
        byte[] sourceBytes = [1, 2, 3, 4];
        await WriteSourceAsync(inputPath, sourceBytes);
        byte[] sourceSha256 = await HashSourceAsync(inputPath);
        await using WineXeBuildWorkspace workspace = await CreateWorkspaceAsync(temporary.Path);
        Directory.CreateDirectory(workspace.XeBuildDirectory);
        string mutableDirectory = Resolve(workspace.RootDirectory, "xeBuild/data");
        Directory.CreateSymbolicLink(mutableDirectory, Resolve(generation, "xeBuild/data"));

        OperationFailureException supportFailure = await Assert.ThrowsAsync<OperationFailureException>(
            () => workspace.StageSupportAsync(generation, plan, CancellationToken.None));
        OperationFailureException keyFailure = await Assert.ThrowsAsync<OperationFailureException>(
            () => workspace.StageCpuKeyAsync(CpuKey.Parse(CpuKeyText), CancellationToken.None));
        OperationFailureException inputFailure = await Assert.ThrowsAsync<OperationFailureException>(
            () => workspace.StageInputAsync(
                new XeBuildSourceContext(inputPath, sourceBytes.Length, sourceSha256),
                XeBuildFourGigabyteStagingPolicy.None,
                CancellationToken.None));
        await workspace.CleanupAsync(retainDiagnostics: true);

        Assert.Equal("workspace-path-unsafe", supportFailure.Kind);
        Assert.Equal("workspace-path-unsafe", keyFailure.Kind);
        Assert.Equal("workspace-path-unsafe", inputFailure.Kind);
        Assert.False(Directory.Exists(mutableDirectory));
        await AssertSupportUnchangedAsync(generation, files);
    }

    private static Dictionary<string, byte[]> CreateSupportFiles(bool dashboardConfiguration = false)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["xeBuild/xeBuild.exe"] = [0x4D, 0x5A],
            ["xeBuild/options.ini"] = Encoding.UTF8.GetBytes("; immutable options\npatchsmc = true\n[settings]\nkeep = yes\n"),
            ["xeBuild/common/SB_priv.bin"] = [0x50, 0x51],
            ["xeBuild/common/bootloader.bin"] = [0x10, 0x20, 0x30],
            ["xeBuild/common/smc_selected.bin"] = [0x53, 0x4D, 0x43],
            ["xeBuild/17559/bin/xl_usb.bin"] = [0x58, 0x4C],
            ["xeBuild/17559/xam.xex"] = [0x01],
            ["xeBuild/17559/xl_usb/xam.xex"] = [0x02],
            ["xeBuild/17559/xl_usb/_glitch2.ini"] = Encoding.UTF8.GetBytes(
                "; selected overlay\n[falconbl]\noverlay.bin,0\n[flashfs]\nxam.xex\n..\\launch.xex\n"),
            ["xeBuild/17559/nested/complete-dashboard-file.bin"] = [0x03],
            ["xeBuild/17489/_glitch2.ini"] = Encoding.UTF8.GetBytes("[falconbl]\nold-dashboard.bin,0\n"),
            ["xeBuild/unselected.exe"] = [0xFF],
            ["xeBuild/XDKbuild/unselected.bin"] = [0xFE],
            ["xeBuild/data/cpukey.txt"] = Encoding.UTF8.GetBytes("support-owned key sentinel"),
            ["xeBuild/data/nanddump.bin"] = [0xFD],
            ["xeBuild/launch.xex"] = [0x4C, 0x58],
            ["xeBuild/lhelper.xex"] = [0x4C, 0x48],
            ["xeBuild/launch.ini"] = Encoding.UTF8.GetBytes("[Paths]\nDefault = Hdd:\\custom.xex\n"),
            ["xeBuild/launch_default.ini"] = Encoding.UTF8.GetBytes("[Paths]\nDefault = Hdd:\\default.xex\n"),
        };
        foreach (string type in new[] { "glitch", "glitch2", "glitch2m", "retail", "jtag", "devkit", "devgl", "devgl16" })
        {
            files.Add(string.Concat("xeBuild/17559/_", type, ".ini"),
                Encoding.UTF8.GetBytes(string.Concat("; immutable ", type, "\n[falconbl]\noriginal.bin,0\n[flashfs]\nxam.xex\n")));
        }

        if (dashboardConfiguration)
        {
            files.Add("xeBuild/17559/launch.ini", Encoding.UTF8.GetBytes("[Paths]\nDefault = Hdd:\\dashboard.xex\n"));
        }

        return files;
    }

    private static XeBuildPreparedPlan CreatePlan(
        IReadOnlyDictionary<string, byte[]> supportFiles,
        string type = "glitch2",
        bool dashLaunch = false,
        XeBuildDrivePatch drivePatch = XeBuildDrivePatch.None)
    {
        return XeBuildPreparationService.Prepare(
            new XeBuildRequest(
                supportRootPath: "support",
                source: new XeBuildSourceContext(
                    "input.bin", 0x1000000, SHA256.HashData(Array.Empty<byte>()), ConsoleId.Falcon16Mb, supportsRgh1: true),
                cpuKey: CpuKey.Parse(CpuKeyText),
                outputPath: "output.bin",
                target: new XeBuildBuildTarget(null, 17559, type,
                    new XeBuildBuildOptions(dashLaunch: dashLaunch, drivePatch: drivePatch))),
            new XeBuildSupportIndex(supportFiles.Keys));
    }

    private static XeBuildPreparedPlan WithWorkspaceLayout(
        XeBuildPreparedPlan plan,
        IEnumerable<XeBuildWorkspaceOverlay> overlays,
        IEnumerable<string> directories)
    {
        return new XeBuildPreparedPlan(
            console: plan.Console,
            sourceDetectedConsole: plan.SourceDetectedConsole,
            consoleWasExplicitlyOverridden: plan.ConsoleWasExplicitlyOverridden,
            dashboardVersion: plan.DashboardVersion,
            requestedHackType: plan.RequestedHackType,
            effectiveHackType: plan.EffectiveHackType,
            configurationName: plan.ConfigurationName,
            requestedModeIniPath: plan.RequestedModeIniPath,
            buildModeIniPath: plan.BuildModeIniPath,
            requiredIniLabel: plan.RequiredIniLabel,
            requiresFlashIni: plan.RequiresFlashIni,
            usesXdkBuildConfiguration: plan.UsesXdkBuildConfiguration,
            requiresDevGl64Preparation: plan.RequiresDevGl64Preparation,
            disablesSmcResetPatching: plan.DisablesSmcResetPatching,
            fourGigabyteStagingPolicy: plan.FourGigabyteStagingPolicy,
            rgh3TemplatePath: plan.Rgh3TemplatePath,
            xdkBuildTemplatePath: plan.XdkBuildTemplatePath,
            dashLaunch: plan.DashLaunch,
            workspaceOverlays: overlays,
            postBuildOperations: plan.PostBuildOperations,
            requiredSupportFiles: plan.RequiredSupportFiles,
            requiredWorkspaceDirectories: directories,
            fixedArguments: plan.FixedArguments);
    }

    private static Task<WineXeBuildWorkspace> CreateWorkspaceAsync(string root)
    {
        return WineXeBuildWorkspace.CreateAsync(Path.Combine(root, "workspaces"), CancellationToken.None);
    }

    private static string Resolve(string root, string relativePath)
    {
        return Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string[] RelativeFiles(string root)
    {
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] ExpectedStagedFiles(XeBuildPreparedPlan plan)
    {
        IEnumerable<string> files = plan.RequiredSupportFiles
            .Concat(plan.WorkspaceOverlays.Select(overlay => overlay.DestinationWorkspacePath));
        if (plan.DashLaunch is { } dashLaunch)
        {
            files = files.Append(dashLaunch.DashboardLaunchConfigurationPath);
        }

        return files.Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }

    private static async Task WriteSupportFilesAsync(string generation, IReadOnlyDictionary<string, byte[]> files)
    {
        foreach ((string relativePath, byte[] bytes) in files)
        {
            await WriteFileAsync(generation, relativePath, bytes);
        }
    }

    private static async Task WriteFileAsync(string root, string relativePath, byte[] bytes)
    {
        string path = Resolve(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes);
    }

    private static async Task AssertSupportUnchangedAsync(string generation, IReadOnlyDictionary<string, byte[]> files)
    {
        Assert.Equal(files.Keys.OrderBy(path => path, StringComparer.Ordinal), RelativeFiles(generation));
        foreach ((string relativePath, byte[] bytes) in files)
        {
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Resolve(generation, relativePath)));
        }
    }

    private static void AssertPrivateFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }

    private static async Task<byte[]> HashSourceAsync(string path)
    {
        await using FileStream input = new(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 0x10000, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(input);
    }

    private static async Task WriteSourceAsync(string path, byte[] bytes)
    {
        await File.WriteAllBytesAsync(path, bytes);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(path, PrivateFileMode);
        }
    }

    private static async Task AssertRejectedSourceIsNotStagedAsync(
        TemporaryDirectory temporary,
        XeBuildSourceContext source,
        string expectedKind)
    {
        await using WineXeBuildWorkspace workspace = await CreateWorkspaceAsync(temporary.Path);
        var runner = new SourceStageRecordingProcessRunner();
        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(async () =>
        {
            await workspace.StageInputAsync(source, XeBuildFourGigabyteStagingPolicy.None, CancellationToken.None);
            await workspace.StageCpuKeyAsync(CpuKey.Parse(CpuKeyText), CancellationToken.None);
            var invocation = new ExternalProcessInvocation("never-invoked-winepath", Array.Empty<string>(), workspace.RootDirectory);
            runner.EnsureExecutableAvailable(invocation);
            await runner.RunAsync(invocation, CancellationToken.None);
        });

        Assert.Equal(ExitCode.InvalidData, failure.Code);
        Assert.Equal(expectedKind, failure.Kind);
        Assert.Empty(runner.Calls);
        Assert.False(File.Exists(workspace.StagedInputPath));
        Assert.False(File.Exists(workspace.StagedCpuKeyPath));
        Assert.DoesNotContain("private source snapshot sentinel", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(source.InputPath, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(CpuKeyText, failure.Message, StringComparison.OrdinalIgnoreCase);
        await workspace.CleanupAsync(retainDiagnostics: false);
        Assert.False(Directory.Exists(workspace.RootDirectory));
    }

    private static async Task CreateSparseFileAsync(string path, long byteLength, bool writeMarkers)
    {
        await using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(path, PrivateFileMode);
        }
        stream.SetLength(byteLength);
        if (!writeMarkers)
        {
            return;
        }

        await stream.WriteAsync(StartMarker);
        stream.Position = SystemPartitionByteLength - SystemEndMarker.Length;
        await stream.WriteAsync(SystemEndMarker);
        stream.Position = SystemPartitionByteLength;
        await stream.WriteAsync(DataMarker);
        stream.Position = byteLength - EndMarker.Length;
        await stream.WriteAsync(EndMarker);
    }

    private static async Task<byte[]> ReadAtAsync(string path, long offset, int length)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Position = offset;
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes);
        return bytes;
    }

    private sealed class SourceStageRecordingProcessRunner : IExternalProcessRunner
    {
        internal List<ExternalProcessInvocation> Calls { get; } = [];

        public void EnsureExecutableAvailable(ExternalProcessInvocation invocation)
        {
            Calls.Add(invocation);
        }

        public Task<ExternalProcessResult> RunAsync(
            ExternalProcessInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(invocation);
            return Task.FromResult(new ExternalProcessResult(0, string.Empty, false, 0, string.Empty, false, 0));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"jrunner-workspace-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(Path, PrivateDirectoryMode);
            }
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
