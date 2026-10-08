using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JRunner.Cli.Infrastructure;
using JRunner.Core.Binary;
using JRunner.Core.Contracts;
using JRunner.Core.Nand.Inspection;
using JRunner.Core.Nand.Inspection.Models;
using JRunner.Core.Nand.Models;
using JRunner.Core.Nand.Physical;
using JRunner.Core.Nand.Security;
using JRunner.Core.Support;
using JRunner.Core.XeBuild;
using JRunner.Core.XeBuild.Preparation;
using Xunit;

namespace JRunner.Cli.Tests;

public sealed class WineXeBuildBackendTests
{
    private const string CpuKeyText = "00112233445566778899AABBCCDDEEFF";
    private const string Rgh3TemplatePath = "common/xell-images/glitch2/TRINITY_RGH3.ecc";
    private const int FirstStageOffset = 0x8000;
    private const int SmcOffset = 0x1000;
    private const int SmcLength = 0x2DC0;
    private const int CbStageLength = 0x400;
    private const int UpdateSlotOffset = 0xA000;
    private const int CfStageLength = 0x360;
    private const int CgStageLength = 0x50;

    [Theory]
    [InlineData("retail")]
    [InlineData("glitch")]
    [InlineData("jtag")]
    [InlineData("glitch2")]
    [InlineData("glitch2m")]
    [InlineData("devgl")]
    [InlineData("devgl16")]
    [InlineData("devkit")]
    [InlineData("devkit16")]
    [InlineData("testkit")]
    [InlineData("testkit16")]
    public async Task Every_canonical_type_without_rgh3_fails_closed_before_support_input_process_or_workspace_access(string type)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var fixture = new TestBuildFixture(temporaryDirectory);
        var runner = new RejectingProcessRunner();
        var progress = new RecordingProgress();
        XeBuildRequest request = fixture.CreateRequest("never-published.bin", targetType: type);
        FileTreeSnapshot before = await CaptureTreeAsync(temporaryDirectory.Path);

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(() =>
            fixture.CreateBackend(runner, progress).BuildAsync(request));

        Assert.True(XeBuildHackTypeCatalog.TryGetByCanonicalName(type, out XeBuildHackType parsed));
        Assert.Equal(parsed, request.Target.HackType);
        Assert.False(XeBuildOutputEvidenceCatalog.Get(parsed).IsAvailable);
        AssertUnavailable(failure, request.Target);
        Assert.Empty(runner.Calls);
        Assert.Empty(progress.Events);
        Assert.False(Directory.Exists(fixture.SupportRoot.DirectoryPath));
        Assert.False(Directory.Exists(fixture.WorkspaceRoot));
        Assert.False(Directory.Exists(fixture.OutputDirectory));
        Assert.False(File.Exists(fixture.InputPath));
        Assert.False(File.Exists(request.OutputPath));
        await AssertTreeUnchangedAsync(before, temporaryDirectory.Path);
    }

    [Theory]
    [InlineData("glitch2")]
    [InlineData("glitch2m")]
    public async Task Supported_Rgh3_selection_enters_support_resolution_without_a_gate_bypass(string type)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var fixture = new TestBuildFixture(temporaryDirectory);
        var runner = new RejectingProcessRunner();
        var progress = new RecordingProgress();
        XeBuildRequest request = fixture.CreateRequest(
            "rgh3.bin",
            targetType: type,
            options: new XeBuildBuildOptions(rgh3: true));
        FileTreeSnapshot before = await CaptureTreeAsync(temporaryDirectory.Path);

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(() =>
            fixture.CreateBackend(runner, progress).BuildAsync(request));

        XeBuildOutputEvidenceCapability capability = XeBuildOutputEvidenceCatalog.Get(request.Target.HackType, rgh3: true);
        Assert.Equal(NandImageFamily.Rgh3, capability.RequiredFamily);
        Assert.True(capability.IsAvailable);
        Assert.Same(NandRgh3OutputEvidence.ReviewedManifest, capability.ReviewedManifest);
        Assert.Equal(ExitCode.MissingPrerequisite, failure.Code);
        Assert.Equal("xebuild-support-unavailable", failure.Kind);
        Assert.Empty(runner.Calls);
        await AssertTreeUnchangedAsync(before, temporaryDirectory.Path);
    }

    [Theory]
    [InlineData("retail")]
    [InlineData("glitch")]
    [InlineData("jtag")]
    [InlineData("devgl")]
    [InlineData("devgl16")]
    [InlineData("devkit")]
    [InlineData("devkit16")]
    [InlineData("testkit")]
    [InlineData("testkit16")]
    public async Task Rgh3_selection_does_not_enable_other_canonical_families(string type)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var fixture = new TestBuildFixture(temporaryDirectory);
        var runner = new RejectingProcessRunner();
        var progress = new RecordingProgress();
        XeBuildRequest request = fixture.CreateRequest(
            "unsupported-rgh3.bin",
            targetType: type,
            options: new XeBuildBuildOptions(rgh3: true));
        FileTreeSnapshot before = await CaptureTreeAsync(temporaryDirectory.Path);

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(() =>
            fixture.CreateBackend(runner, progress).BuildAsync(request));

        AssertUnavailable(failure, request.Target);
        Assert.False(XeBuildOutputEvidenceCatalog.Get(request.Target.HackType, rgh3: true).IsAvailable);
        Assert.Empty(runner.Calls);
        Assert.Empty(progress.Events);
        await AssertTreeUnchangedAsync(before, temporaryDirectory.Path);
    }

    [Theory]
    [InlineData("glitch2")]
    [InlineData("glitch2m")]
    public async Task Supported_Rgh3_rejects_changed_source_snapshot_before_any_Wine_process_and_cleans_up(string type)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using Rgh3BuildInputs inputs = CreateRgh3BuildInputs(type);
        var fixture = new TestBuildFixture(
            temporaryDirectory,
            CreateSupportFiles().Append((Rgh3TemplatePath, inputs.Template)).ToArray());
        await fixture.InstallSupportAsync();
        await File.WriteAllBytesAsync(fixture.InputPath, inputs.Source);
        SetPrivateFile(fixture.InputPath);
        CreatePrivateDirectory(fixture.OutputDirectory);
        string outputPath = Path.Combine(fixture.OutputDirectory, "preserved-before-wine.bin");
        byte[] originalOutput = [0x4F, 0x52, 0x49, 0x47, 0x49, 0x4E, 0x41, 0x4C];
        await File.WriteAllBytesAsync(outputPath, originalOutput);
        SetPrivateFile(outputPath);
        byte[] inputHash = SHA256.HashData(inputs.Source);
        FileTreeSnapshot supportBefore = await CaptureTreeAsync(fixture.SupportRoot.DirectoryPath);
        XeBuildRequest request = fixture.CreateRequest(
            outputPath,
            targetType: type,
            options: new XeBuildBuildOptions(rgh3: true),
            force: true,
            source: new XeBuildSourceContext(fixture.InputPath, inputs.Source.LongLength, inputHash, ConsoleId.Trinity16Mb));

        // Replace the inspected path only after its original content digest is bound into the request.
        inputs.Source[0] ^= 0xFF;
        File.Delete(fixture.InputPath);
        await File.WriteAllBytesAsync(fixture.InputPath, inputs.Source);
        SetPrivateFile(fixture.InputPath);
        Assert.Equal(inputs.Source.LongLength, new FileInfo(fixture.InputPath).Length);
        byte[] changedInputHash = SHA256.HashData(inputs.Source);
        var runner = new RejectingProcessRunner();

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(() =>
            fixture.CreateBackend(runner).BuildAsync(request));

        Assert.Equal(ExitCode.InvalidData, failure.Code);
        Assert.Equal("xebuild-input-changed", failure.Kind);
        Assert.Empty(runner.Calls);
        Assert.Equal(originalOutput, await File.ReadAllBytesAsync(outputPath));
        Assert.Equal(outputPath, Assert.Single(Directory.EnumerateFileSystemEntries(fixture.OutputDirectory)));
        if (Directory.Exists(fixture.WorkspaceRoot))
        {
            Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.WorkspaceRoot));
        }

        await using FileStream unchangedInput = File.OpenRead(fixture.InputPath);
        Assert.Equal(changedInputHash, await SHA256.HashDataAsync(unchangedInput));
        await AssertTreeUnchangedAsync(supportBefore, fixture.SupportRoot.DirectoryPath);
    }

    [Theory]
    [InlineData("glitch2")]
    [InlineData("glitch2m")]
    public async Task Supported_Rgh3_with_verified_source_reaches_Wine_preflight_without_publication_or_retained_workspace(string type)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using Rgh3BuildInputs inputs = CreateRgh3BuildInputs(type);
        var fixture = new TestBuildFixture(
            temporaryDirectory,
            CreateSupportFiles().Append((Rgh3TemplatePath, inputs.Template)).ToArray());
        await fixture.InstallSupportAsync();
        await File.WriteAllBytesAsync(fixture.InputPath, inputs.Source);
        SetPrivateFile(fixture.InputPath);
        CreatePrivateDirectory(fixture.OutputDirectory);
        string outputPath = Path.Combine(fixture.OutputDirectory, "preserved-without-wine.bin");
        byte[] originalOutput = [0x4F, 0x52, 0x49, 0x47, 0x49, 0x4E, 0x41, 0x4C];
        await File.WriteAllBytesAsync(outputPath, originalOutput);
        SetPrivateFile(outputPath);
        byte[] inputHash = SHA256.HashData(inputs.Source);
        FileTreeSnapshot supportBefore = await CaptureTreeAsync(fixture.SupportRoot.DirectoryPath);
        var runner = new CopyingProcessRunner(versionOutput: "not Wine\n");
        XeBuildRequest request = fixture.CreateRequest(
            outputPath,
            targetType: type,
            options: new XeBuildBuildOptions(rgh3: true),
            force: true,
            keepWorkspace: true,
            source: new XeBuildSourceContext(fixture.InputPath, inputs.Source.LongLength, inputHash, ConsoleId.Trinity16Mb));

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(() =>
            fixture.CreateBackend(runner).BuildAsync(request));

        Assert.Equal(ExitCode.MissingPrerequisite, failure.Code);
        Assert.Equal("wine-unavailable", failure.Kind);
        ProcessCall version = Assert.Single(runner.Calls);
        Assert.Equal("version", version.Kind);
        Assert.Equal(["--version"], version.Invocation.Arguments);
        Assert.Equal(originalOutput, await File.ReadAllBytesAsync(outputPath));
        Assert.Equal(outputPath, Assert.Single(Directory.EnumerateFileSystemEntries(fixture.OutputDirectory)));
        if (Directory.Exists(fixture.WorkspaceRoot))
        {
            Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.WorkspaceRoot));
        }

        await using FileStream unchangedInput = File.OpenRead(fixture.InputPath);
        Assert.Equal(inputHash, await SHA256.HashDataAsync(unchangedInput));
        await AssertTreeUnchangedAsync(supportBefore, fixture.SupportRoot.DirectoryPath);
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("wine", UnixFileMode.GroupWrite)]
    [InlineData("wine", UnixFileMode.OtherWrite)]
    [InlineData("wine", UnixFileMode.GroupWrite | UnixFileMode.StickyBit)]
    [InlineData("wine", UnixFileMode.OtherWrite | UnixFileMode.StickyBit)]
    [InlineData("winepath", UnixFileMode.GroupWrite)]
    [InlineData("winepath", UnixFileMode.OtherWrite)]
    [InlineData("winepath", UnixFileMode.GroupWrite | UnixFileMode.StickyBit)]
    [InlineData("winepath", UnixFileMode.OtherWrite | UnixFileMode.StickyBit)]
    public async Task Production_discovery_rejects_either_unsafe_executable_before_any_process_probe_or_CPU_key_staging(
        string unsafeExecutable,
        UnixFileMode unsafeBits)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        using Rgh3BuildInputs inputs = CreateRgh3BuildInputs("glitch2");
        var fixture = new TestBuildFixture(
            temporaryDirectory,
            CreateSupportFiles().Append((Rgh3TemplatePath, inputs.Template)).ToArray());
        await fixture.InstallSupportAsync();
        await File.WriteAllBytesAsync(fixture.InputPath, inputs.Source);
        SetPrivateFile(fixture.InputPath);
        CreatePrivateDirectory(fixture.OutputDirectory);
        string unsafeAncestor = temporaryDirectory.CreateDirectory("private-PATH-value/unsafe-ancestor");
        string unsafeDirectory = temporaryDirectory.CreateDirectory("private-PATH-value/unsafe-ancestor/owned-tools");
        temporaryDirectory.CreateExecutable(Path.Combine("private-PATH-value", "unsafe-ancestor", "owned-tools", unsafeExecutable));
        string counterpartDirectory = temporaryDirectory.CreateDirectory("protected-counterpart");
        temporaryDirectory.CreateExecutable(Path.Combine("protected-counterpart", unsafeExecutable == "wine" ? "winepath" : "wine"));
        string fallbackDirectory = temporaryDirectory.CreateDirectory("protected-fallback");
        temporaryDirectory.CreateExecutable(Path.Combine("protected-fallback", "wine"));
        temporaryDirectory.CreateExecutable(Path.Combine("protected-fallback", "winepath"));
        File.SetUnixFileMode(unsafeAncestor, File.GetUnixFileMode(unsafeAncestor) | unsafeBits);
        string searchPath = string.Join(Path.PathSeparator, unsafeDirectory, counterpartDirectory, fallbackDirectory);
        var runner = new RejectingProcessRunner();
        var progress = new RecordingProgress();
        XeBuildRequest request = fixture.CreateRequest(
            "never-published.bin",
            options: new XeBuildBuildOptions(rgh3: true),
            keepWorkspace: true,
            source: new XeBuildSourceContext(
                fixture.InputPath, inputs.Source.LongLength, SHA256.HashData(inputs.Source), ConsoleId.Trinity16Mb));

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(() =>
            fixture.CreateBackend(
                runner,
                progress,
                WineXeBuildToolchain.Default,
                WineXeBuildToolchainResolver.CreateSyntheticNativeFixtureResolver(searchPath)).BuildAsync(request));

        Assert.Equal(ExitCode.MissingPrerequisite, failure.Code);
        Assert.Equal(unsafeExecutable == "wine" ? "wine-unavailable" : "winepath-unavailable", failure.Kind);
        Assert.DoesNotContain(temporaryDirectory.Path, failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(searchPath, failure.ToString(), StringComparison.Ordinal);
        Assert.Empty(runner.Calls);
        Assert.Contains(progress.Events, value => value.Kind == "staging-xebuild");
        Assert.DoesNotContain(progress.Events, value =>
            value.Kind is "staging-cpukey" or "verifying-xebuild-input" or "running-xebuild");
        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path, "cpukey.txt", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.WorkspaceRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.OutputDirectory));
        Assert.False(File.Exists(request.OutputPath));
        Assert.Equal(unsafeBits, File.GetUnixFileMode(unsafeAncestor) & unsafeBits);
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("wineserver", "leaf", UnixFileMode.GroupWrite)]
    [InlineData("wineserver", "leaf", UnixFileMode.OtherWrite)]
    [InlineData("wine-loader", "target", UnixFileMode.GroupWrite)]
    [InlineData("wine-loader", "target", UnixFileMode.OtherWrite)]
    [InlineData("wine-preloader", "target-ancestor", UnixFileMode.GroupWrite)]
    [InlineData("wine-preloader", "target-ancestor", UnixFileMode.OtherWrite)]
    public async Task Production_discovery_rejects_unsafe_PATH_helper_closure_before_any_process_or_CPU_key_staging(
        string helperName,
        string unsafeLocation,
        UnixFileMode unsafeBits)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        using Rgh3BuildInputs inputs = CreateRgh3BuildInputs("glitch2");
        var fixture = new TestBuildFixture(
            temporaryDirectory,
            CreateSupportFiles().Append((Rgh3TemplatePath, inputs.Template)).ToArray());
        await fixture.InstallSupportAsync();
        await File.WriteAllBytesAsync(fixture.InputPath, inputs.Source);
        SetPrivateFile(fixture.InputPath);
        CreatePrivateDirectory(fixture.OutputDirectory);
        string tools = temporaryDirectory.CreateDirectory("protected-tools");
        temporaryDirectory.CreateExecutable("protected-tools/wine");
        temporaryDirectory.CreateExecutable("protected-tools/winepath");
        if (helperName != "wineserver")
        {
            temporaryDirectory.CreateExecutable("protected-tools/wineserver");
        }
        string unsafePath;
        if (unsafeLocation == "leaf")
        {
            unsafePath = temporaryDirectory.CreateExecutable(Path.Combine("protected-tools", helperName));
        }
        else
        {
            string targetDirectory = temporaryDirectory.CreateDirectory("private-helper-target");
            string target = temporaryDirectory.CreateExecutable(Path.Combine("private-helper-target", helperName));
            File.CreateSymbolicLink(Path.Combine(tools, helperName), target);
            unsafePath = unsafeLocation == "target" ? target : targetDirectory;
        }
        File.SetUnixFileMode(unsafePath, File.GetUnixFileMode(unsafePath) | unsafeBits);
        var runner = new RejectingProcessRunner();
        var progress = new RecordingProgress();
        XeBuildRequest request = fixture.CreateRequest(
            "never-published-helper.bin",
            options: new XeBuildBuildOptions(rgh3: true),
            keepWorkspace: true,
            source: new XeBuildSourceContext(
                fixture.InputPath, inputs.Source.LongLength, SHA256.HashData(inputs.Source), ConsoleId.Trinity16Mb));

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(() =>
            fixture.CreateBackend(
                runner,
                progress,
                WineXeBuildToolchain.Default,
                WineXeBuildToolchainResolver.CreateSyntheticNativeFixtureResolver(tools)).BuildAsync(request));

        Assert.Equal(ExitCode.MissingPrerequisite, failure.Code);
        Assert.Equal("wine-unavailable", failure.Kind);
        Assert.DoesNotContain(temporaryDirectory.Path, failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(CpuKeyText, failure.ToString(), StringComparison.Ordinal);
        Assert.Empty(runner.Calls);
        Assert.Contains(progress.Events, value => value.Kind == "staging-xebuild");
        Assert.DoesNotContain(progress.Events, value =>
            value.Kind is "staging-cpukey" or "verifying-xebuild-input" or "running-xebuild");
        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path, "cpukey.txt", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.WorkspaceRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.OutputDirectory));
        Assert.False(File.Exists(request.OutputPath));
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("setsid", "leaf", UnixFileMode.GroupWrite)]
    [InlineData("setsid", "leaf", UnixFileMode.OtherWrite)]
    [InlineData("setsid", "target", UnixFileMode.GroupWrite)]
    [InlineData("setsid", "target", UnixFileMode.OtherWrite)]
    [InlineData("setsid", "ancestor", UnixFileMode.GroupWrite)]
    [InlineData("setsid", "ancestor", UnixFileMode.OtherWrite)]
    [InlineData("supervisor", "leaf", UnixFileMode.GroupWrite)]
    [InlineData("supervisor", "leaf", UnixFileMode.OtherWrite)]
    [InlineData("supervisor", "target", UnixFileMode.GroupWrite)]
    [InlineData("supervisor", "target", UnixFileMode.OtherWrite)]
    [InlineData("supervisor", "ancestor", UnixFileMode.GroupWrite)]
    [InlineData("supervisor", "ancestor", UnixFileMode.OtherWrite)]
    public async Task Production_chain_rejects_unsafe_selected_launcher_or_supervisor_before_launch_or_CPU_key_staging(
        string unsafeRole,
        string unsafeLocation,
        UnixFileMode unsafeBits)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        using Rgh3BuildInputs inputs = CreateRgh3BuildInputs("glitch2");
        var fixture = new TestBuildFixture(
            temporaryDirectory,
            CreateSupportFiles().Append((Rgh3TemplatePath, inputs.Template)).ToArray());
        await fixture.InstallSupportAsync();
        await File.WriteAllBytesAsync(fixture.InputPath, inputs.Source);
        SetPrivateFile(fixture.InputPath);
        CreatePrivateDirectory(fixture.OutputDirectory);
        string tools = temporaryDirectory.CreateDirectory("protected-wine-tools");
        temporaryDirectory.CreateExecutable("protected-wine-tools/wine");
        temporaryDirectory.CreateExecutable("protected-wine-tools/winepath");
        temporaryDirectory.CreateExecutable("protected-wine-tools/wineserver");
        // Keep the selected chain component outside Wine's PATH. Its own trust validation,
        // rather than the PATH helper scan, must reject it before any process is started.
        string hostDirectory = temporaryDirectory.CreateDirectory("private-supervisor-selection");
        string target = temporaryDirectory.CreateExecutable(Path.Combine("private-supervisor-selection", unsafeRole));
        File.WriteAllText(target, "#!/bin/sh\nprintf launched > \"$0.launched\"\nexit 93\n");
        string selectedPath = target;
        if (unsafeLocation == "target")
        {
            string aliases = temporaryDirectory.CreateDirectory("protected-supervisor-aliases");
            selectedPath = Path.Combine(aliases, unsafeRole);
            File.CreateSymbolicLink(selectedPath, target);
        }
        string unsafePath = unsafeLocation == "ancestor" ? hostDirectory : target;
        File.SetUnixFileMode(unsafePath, File.GetUnixFileMode(unsafePath) | unsafeBits);
        var runner = unsafeRole == "setsid"
            ? new ExternalProcessRunner(selectedPath, null)
            : new ExternalProcessRunner(null, selectedPath);
        var progress = new RecordingProgress();
        XeBuildRequest request = fixture.CreateRequest(
            "never-published-supervisor.bin",
            options: new XeBuildBuildOptions(rgh3: true),
            keepWorkspace: true,
            source: new XeBuildSourceContext(
                fixture.InputPath, inputs.Source.LongLength, SHA256.HashData(inputs.Source), ConsoleId.Trinity16Mb));

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(() =>
            fixture.CreateBackend(
                runner,
                progress,
                WineXeBuildToolchain.Default,
                WineXeBuildToolchainResolver.CreateSyntheticNativeFixtureResolver(tools)).BuildAsync(request));

        Assert.Equal(ExitCode.MissingPrerequisite, failure.Code);
        Assert.Equal("wine-unavailable", failure.Kind);
        Assert.DoesNotContain(temporaryDirectory.Path, failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(CpuKeyText, failure.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(selectedPath + ".launched"));
        Assert.False(File.Exists(target + ".launched"));
        Assert.Contains(progress.Events, value => value.Kind == "staging-xebuild");
        Assert.DoesNotContain(progress.Events, value =>
            value.Kind is "staging-cpukey" or "verifying-xebuild-input" or "running-xebuild");
        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path, "cpukey.txt", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.WorkspaceRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.OutputDirectory));
        Assert.False(File.Exists(request.OutputPath));
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("wine")]
    [InlineData("winepath")]
    [InlineData("wineserver")]
    public async Task Unsupported_Wine_wrappers_fail_with_a_redacted_prerequisite_before_any_process_or_CPU_key_staging(
        string unsupportedRole)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        using Rgh3BuildInputs inputs = CreateRgh3BuildInputs("glitch2");
        var fixture = new TestBuildFixture(
            temporaryDirectory,
            CreateSupportFiles().Append((Rgh3TemplatePath, inputs.Template)).ToArray());
        await fixture.InstallSupportAsync();
        await File.WriteAllBytesAsync(fixture.InputPath, inputs.Source);
        SetPrivateFile(fixture.InputPath);
        CreatePrivateDirectory(fixture.OutputDirectory);
        string tools = temporaryDirectory.CreateDirectory("protected-wrapper-tools");
        foreach (string role in new[] { "wine", "winepath", "wineserver" })
        {
            temporaryDirectory.CreateExecutable(Path.Combine("protected-wrapper-tools", role));
        }
        string unsupportedPath = Path.Combine(tools, unsupportedRole);
        File.WriteAllText(unsupportedPath, "#!/bin/sh\nprintf launched > \"$0.launched\"\nexec /private/unchecked-helper \"$@\"\n");
        var runner = new RejectingProcessRunner();
        var progress = new RecordingProgress();
        XeBuildRequest request = fixture.CreateRequest(
            "never-published-unsupported-wrapper.bin",
            options: new XeBuildBuildOptions(rgh3: true),
            keepWorkspace: true,
            source: new XeBuildSourceContext(
                fixture.InputPath, inputs.Source.LongLength, SHA256.HashData(inputs.Source), ConsoleId.Trinity16Mb));

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(() =>
            fixture.CreateBackend(
                runner,
                progress,
                WineXeBuildToolchain.Default,
                WineXeBuildToolchainResolver.CreateSyntheticNativeFixtureResolver(tools)).BuildAsync(request));

        Assert.Equal(ExitCode.MissingPrerequisite, failure.Code);
        Assert.Equal("wine-wrapper-unsupported", failure.Kind);
        Assert.Contains("supported installed Wine launcher", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(temporaryDirectory.Path, failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("/private/unchecked-helper", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(CpuKeyText, failure.ToString(), StringComparison.Ordinal);
        Assert.Empty(runner.Calls);
        Assert.False(File.Exists(unsupportedPath + ".launched"));
        Assert.DoesNotContain(progress.Events, value =>
            value.Kind is "staging-cpukey" or "verifying-xebuild-input" or "running-xebuild");
        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path, "cpukey.txt", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.WorkspaceRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.OutputDirectory));
        Assert.False(File.Exists(request.OutputPath));
    }

    [SupportedOSPlatform("linux")]
    [Fact]
    public async Task Production_discovery_still_follows_source_snapshot_verification_before_any_external_process()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        using Rgh3BuildInputs inputs = CreateRgh3BuildInputs("glitch2");
        var fixture = new TestBuildFixture(
            temporaryDirectory,
            CreateSupportFiles().Append((Rgh3TemplatePath, inputs.Template)).ToArray());
        await fixture.InstallSupportAsync();
        await File.WriteAllBytesAsync(fixture.InputPath, inputs.Source);
        SetPrivateFile(fixture.InputPath);
        CreatePrivateDirectory(fixture.OutputDirectory);
        XeBuildRequest request = fixture.CreateRequest(
            "changed-input.bin",
            options: new XeBuildBuildOptions(rgh3: true),
            source: new XeBuildSourceContext(
                fixture.InputPath, inputs.Source.LongLength, SHA256.HashData(inputs.Source), ConsoleId.Trinity16Mb));
        inputs.Source[0] ^= 0xFF;
        await File.WriteAllBytesAsync(fixture.InputPath, inputs.Source);
        SetPrivateFile(fixture.InputPath);
        var runner = new RejectingProcessRunner();
        var progress = new RecordingProgress();
        var resolver = new WineXeBuildToolchainResolver(Path.Combine(temporaryDirectory.Path, "absent-PATH-entry"));

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(() =>
            fixture.CreateBackend(runner, progress, WineXeBuildToolchain.Default, resolver).BuildAsync(request));

        Assert.Equal(ExitCode.InvalidData, failure.Code);
        Assert.Equal("xebuild-input-changed", failure.Kind);
        Assert.Empty(runner.Calls);
        Assert.Contains(progress.Events, value => value.Kind == "staging-xebuild");
        Assert.DoesNotContain(progress.Events, value =>
            value.Kind is "staging-cpukey" or "verifying-xebuild-input" or "running-xebuild");
        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path, "cpukey.txt", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.WorkspaceRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.OutputDirectory));
    }

    [SupportedOSPlatform("linux")]
    [Fact]
    public async Task Production_discovery_binds_both_protected_absolute_aliases_and_closes_the_child_environment_before_the_first_process()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        using var trustedSupervisor = new ExternalProcessSupervisorLaunchTests.ProtectedRunnableSupervisorDeployment();
        using Rgh3BuildInputs inputs = CreateRgh3BuildInputs("glitch2");
        var fixture = new TestBuildFixture(
            temporaryDirectory,
            CreateSupportFiles().Append((Rgh3TemplatePath, inputs.Template)).ToArray());
        await fixture.InstallSupportAsync();
        await File.WriteAllBytesAsync(fixture.InputPath, inputs.Source);
        SetPrivateFile(fixture.InputPath);
        CreatePrivateDirectory(fixture.OutputDirectory);
        byte[] inputHash = SHA256.HashData(inputs.Source);
        string canonicalWine = TemporaryDirectory.GetCanonicalPath(temporaryDirectory.CreateExecutable("canonical-tools/wine"));
        string canonicalWinepath = TemporaryDirectory.GetCanonicalPath(temporaryDirectory.CreateExecutable("canonical-tools/winepath"));
        temporaryDirectory.CreateExecutable("canonical-tools/wineserver");
        string searchDirectory = temporaryDirectory.CreateDirectory("original-PATH-entry");
        string wineLink = Path.Combine(searchDirectory, "wine");
        string winepathLink = Path.Combine(searchDirectory, "winepath");
        File.CreateSymbolicLink(wineLink, canonicalWine);
        File.CreateSymbolicLink(winepathLink, canonicalWinepath);
        string unrelatedHelper = temporaryDirectory.CreateExecutable("original-PATH-entry/adb");
        File.SetUnixFileMode(unrelatedHelper, File.GetUnixFileMode(unrelatedHelper) | UnixFileMode.OtherWrite);
        string absoluteWineAlias = wineLink;
        string absoluteWinepathAlias = winepathLink;
        string plantedDirectory = temporaryDirectory.CreateDirectory("writable-inherited-PATH-entry");
        string plantedWine = temporaryDirectory.CreateExecutable("writable-inherited-PATH-entry/wine-decoy");
        string plantedWinepath = temporaryDirectory.CreateExecutable("writable-inherited-PATH-entry/winepath-decoy");
        File.SetUnixFileMode(
            plantedDirectory,
            File.GetUnixFileMode(plantedDirectory) | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite | UnixFileMode.StickyBit);
        string searchPath = string.Join(Path.PathSeparator, plantedDirectory, searchDirectory);
        bool changedExcludedPathEntry = false;
        var runner = new CopyingProcessRunner(
            expectedToolchain: new WineXeBuildToolchain(absoluteWineAlias, absoluteWinepathAlias),
            trustedSupervisorLaunch: trustedSupervisor.Prepare(),
            onVersion: invocation =>
            {
                Assert.Equal(absoluteWineAlias, invocation.ExecutablePath);
                Assert.NotNull(invocation.TrustedSupervisorLaunch);
                Assert.NotNull(invocation.TrustedNativeClosure);
                string operationDirectory = Assert.Single(Directory.EnumerateDirectories(fixture.WorkspaceRoot));
                string stagedInput = Path.Combine(operationDirectory, "xeBuild", "data", "nanddump.bin");
                Assert.Equal(inputs.Source.LongLength, new FileInfo(stagedInput).Length);
                using (FileStream input = File.OpenRead(stagedInput))
                {
                    Assert.Equal(inputHash, SHA256.HashData(input));
                }

                Assert.False(File.Exists(Path.Combine(operationDirectory, "xeBuild", "data", "cpukey.txt")));
                string helperDirectory = invocation.EnvironmentUpdates["PATH"]!;
                Assert.Equal(Path.Combine(operationDirectory, "wine-toolchain"), helperDirectory);
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(helperDirectory));
                WineXeBuildToolchainResolver.ValidateProtectedTree(helperDirectory);
                string[] helperNames = Directory.EnumerateFileSystemEntries(helperDirectory)
                    .Select(path => Path.GetFileName(path)!).ToArray();
                Assert.Contains("wine", helperNames);
                Assert.Contains("winepath", helperNames);
                Assert.Contains("wineserver", helperNames);
                Assert.DoesNotContain("adb", helperNames);
                Assert.Equal(canonicalWine,
                    File.ResolveLinkTarget(Path.Combine(helperDirectory, "wine"), returnFinalTarget: true)!.FullName);
                Assert.Equal(canonicalWinepath,
                    File.ResolveLinkTarget(Path.Combine(helperDirectory, "winepath"), returnFinalTarget: true)!.FullName);
                // Change an earlier, excluded PATH entry without mutating the trusted same-UID aliases.
                // A second discovery or an inherited-PATH lookup would now find these unsafe commands.
                File.Move(plantedWine, Path.Combine(plantedDirectory, "wine"));
                File.Move(plantedWinepath, Path.Combine(plantedDirectory, "winepath"));
                changedExcludedPathEntry = true;
            });
        var progress = new RecordingProgress();
        XeBuildRequest request = fixture.CreateRequest(
            "absolute-alias-commands.bin",
            options: new XeBuildBuildOptions(rgh3: true),
            source: new XeBuildSourceContext(
                fixture.InputPath, inputs.Source.LongLength, inputHash, ConsoleId.Trinity16Mb));

        XeBuildResult result = await fixture.CreateBackend(
            runner,
            progress,
            WineXeBuildToolchain.Default,
            WineXeBuildToolchainResolver.CreateSyntheticNativeFixtureResolver(searchPath)).BuildAsync(request);

        Assert.True(changedExcludedPathEntry);
        Assert.Equal(canonicalWine, TemporaryDirectory.GetCanonicalPath(absoluteWineAlias));
        Assert.Equal(canonicalWinepath, TemporaryDirectory.GetCanonicalPath(absoluteWinepathAlias));
        Assert.Equal(["version", "probe", "initialize", "winepath", "build"], runner.Calls.Select(call => call.Kind));
        Assert.All(runner.Calls, call =>
        {
            ExternalProcessSupervisorLaunch launch = Assert.IsType<ExternalProcessSupervisorLaunch>(call.Invocation.TrustedSupervisorLaunch);
            Assert.IsType<NativeDependencyClosure>(call.Invocation.TrustedNativeClosure);
            Assert.True(Path.IsPathFullyQualified(launch.ProcessGroupLauncher));
            Assert.True(Path.IsPathFullyQualified(launch.HostArguments[0]));
            Assert.True(Path.IsPathFullyQualified(call.Invocation.ExecutablePath));
            Assert.Equal(
                call.Kind is "probe" or "winepath" ? absoluteWinepathAlias : absoluteWineAlias,
                call.Invocation.ExecutablePath);
            Assert.True(call.Invocation.EnvironmentUpdates.ContainsKey("WINEPREFIX"));
            foreach (string name in new[] { "HOME", "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME", "XDG_RUNTIME_DIR" })
            {
                Assert.StartsWith(fixture.WorkspaceRoot + Path.DirectorySeparatorChar,
                    call.Invocation.EnvironmentUpdates[name], StringComparison.Ordinal);
            }
            Assert.Equal("-all", call.Invocation.EnvironmentUpdates["WINEDEBUG"]);
            Assert.Equal(WineNativePrefixPolicy.HeadlessDllOverrides, call.Invocation.EnvironmentUpdates["WINEDLLOVERRIDES"]);
            foreach (string variable in new[]
            {
                "WINESERVER", "WINELOADER", "WINELOADERNOEXEC", "WINEDLLPATH",
                "BASH_ENV", "ENV",
                "LD_PRELOAD", "LD_LIBRARY_PATH", "LD_AUDIT",
            })
            {
                Assert.True(call.Invocation.EnvironmentUpdates.TryGetValue(variable, out string? value));
                Assert.Null(value);
            }
            Assert.True(call.Invocation.EnvironmentUpdates.TryGetValue("PATH", out string? childPath));
            Assert.False(string.IsNullOrWhiteSpace(childPath));
            string[] directories = childPath!.Split(Path.PathSeparator);
            string helperDirectory = Assert.Single(directories);
            Assert.True(Path.IsPathFullyQualified(helperDirectory));
            Assert.Equal("wine-toolchain", Path.GetFileName(helperDirectory));
            Assert.StartsWith(fixture.WorkspaceRoot + Path.DirectorySeparatorChar, helperDirectory, StringComparison.Ordinal);
            Assert.DoesNotContain(Path.GetDirectoryName(canonicalWine)!, directories);
            Assert.DoesNotContain(searchDirectory, directories);
            Assert.DoesNotContain(plantedDirectory, directories);
            Assert.NotEqual(searchPath, childPath);
        });
        Assert.Single(runner.Calls.Select(call => call.Invocation.EnvironmentUpdates["PATH"]).Distinct(StringComparer.Ordinal));
        ExternalProcessSupervisorLaunch preparedLaunch = Assert.IsType<ExternalProcessSupervisorLaunch>(runner.Calls.First().Invocation.TrustedSupervisorLaunch);
        Assert.All(runner.Calls, call => Assert.Same(preparedLaunch, call.Invocation.TrustedSupervisorLaunch));
        NativeDependencyClosure initialClosure = Assert.IsType<NativeDependencyClosure>(runner.Calls.First().Invocation.TrustedNativeClosure);
        Assert.All(runner.Calls.Where(call => call.Kind is "version" or "probe" or "initialize"),
            call => Assert.Same(initialClosure, call.Invocation.TrustedNativeClosure));
        NativeDependencyClosure finalClosure = Assert.IsType<NativeDependencyClosure>(
            runner.Calls.First(call => call.Kind == "winepath").Invocation.TrustedNativeClosure);
        Assert.NotSame(initialClosure, finalClosure);
        Assert.All(runner.Calls.Where(call => call.Kind is "winepath" or "build"),
            call => Assert.Same(finalClosure, call.Invocation.TrustedNativeClosure));
        Assert.Equal(request.OutputPath, result.OutputPath);
        Assert.Equal(inputs.Source.LongLength, result.OutputByteLength);
        Assert.True(File.Exists(result.OutputPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.WorkspaceRoot));
        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path, "cpukey.txt", SearchOption.AllDirectories));
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("interpreter")]
    [InlineData("rpath")]
    [InlineData("runpath")]
    [InlineData("needed-path")]
    [InlineData("unknown-token")]
    [InlineData("module-library")]
    [InlineData("package-config")]
    public async Task Native_path_closure_failures_precede_all_external_calls_and_sensitive_staging(string selector)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temporaryDirectory = new TemporaryDirectory();
        using var trustedSupervisor = new ExternalProcessSupervisorLaunchTests.ProtectedRunnableSupervisorDeployment();
        using Rgh3BuildInputs inputs = CreateRgh3BuildInputs("glitch2");
        var fixture = new TestBuildFixture(
            temporaryDirectory,
            CreateSupportFiles().Append((Rgh3TemplatePath, inputs.Template)).ToArray());
        await fixture.InstallSupportAsync();
        await File.WriteAllBytesAsync(fixture.InputPath, inputs.Source);
        SetPrivateFile(fixture.InputPath);
        CreatePrivateDirectory(fixture.OutputDirectory);
        string tools = temporaryDirectory.CreateDirectory("wine-package/bin");
        string wine = temporaryDirectory.CreateExecutable("wine-package/bin/wine");
        string winepath = temporaryDirectory.CreateExecutable("wine-package/bin/winepath");
        temporaryDirectory.CreateExecutable("wine-package/bin/wineserver");
        string hostile = temporaryDirectory.CreateDirectory("cross-uid-native-root");
        File.SetUnixFileMode(hostile, File.GetUnixFileMode(hostile) | UnixFileMode.OtherWrite);
        string interpreter = selector == "interpreter" ? Path.Join(hostile, "ld-evil.so") : "/lib64/ld-linux-x86-64.so.2";
        string? needed = selector == "needed-path" ? Path.Join(hostile, "libevil.so") : "libc.so.6";
        string? rpath = selector switch
        {
            "rpath" => hostile,
            _ => null,
        };
        string? runpath = selector switch
        {
            "runpath" => hostile,
            "unknown-token" => "$LIB/wine",
            _ => null,
        };
        if (selector == "module-library")
        {
            string moduleDirectory = temporaryDirectory.CreateDirectory("wine-package/lib/wine/x86_64-unix");
            File.WriteAllBytes(Path.Join(moduleDirectory, "late-loaded.so"),
                new NativeElfTestImage().AddStringTag(15, hostile).Build());
            SetPrivateFile(Path.Join(moduleDirectory, "late-loaded.so"));
        }
        else if (selector == "package-config")
        {
            string data = temporaryDirectory.CreateDirectory("wine-package/share/wine");
            string config = Path.Join(data, "wine.inf");
            File.WriteAllText(config, "protected package configuration prerequisite\n");
            File.SetUnixFileMode(config, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherWrite);
        }
        else
        {
            File.WriteAllBytes(wine, TemporaryDirectory.CreateNativeElfImage(interpreter, needed, rpath, runpath));
        }
        var runner = new CopyingProcessRunner(
            expectedToolchain: new WineXeBuildToolchain(wine, winepath),
            trustedSupervisorLaunch: trustedSupervisor.Prepare());
        var progress = new RecordingProgress();
        XeBuildRequest request = fixture.CreateRequest(
            "native-unavailable.bin",
            options: new XeBuildBuildOptions(rgh3: true),
            keepWorkspace: true,
            source: new XeBuildSourceContext(
                fixture.InputPath, inputs.Source.LongLength, SHA256.HashData(inputs.Source), ConsoleId.Trinity16Mb));

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(() =>
            fixture.CreateBackend(runner, progress, WineXeBuildToolchain.Default,
                WineXeBuildToolchainResolver.CreateSyntheticNativeFixtureResolver(tools)).BuildAsync(request));

        Assert.Equal(ExitCode.MissingPrerequisite, failure.Code);
        Assert.Equal("native-closure-unavailable", failure.Kind);
        Assert.DoesNotContain(temporaryDirectory.Path, failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(CpuKeyText, failure.ToString(), StringComparison.Ordinal);
        Assert.Empty(runner.Calls);
        Assert.DoesNotContain(progress.Events, value =>
            value.Kind is "staging-cpukey" or "verifying-xebuild-input" or "running-xebuild");
        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path, "cpukey.txt", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.WorkspaceRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.OutputDirectory));
        Assert.False(File.Exists(request.OutputPath));
    }

    [SupportedOSPlatform("linux")]
    [Fact]
    public async Task Unexpected_managed_prefix_registry_state_is_rejected_before_version_or_key_staging()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temporaryDirectory = new TemporaryDirectory();
        using var trustedSupervisor = new ExternalProcessSupervisorLaunchTests.ProtectedRunnableSupervisorDeployment();
        using Rgh3BuildInputs inputs = CreateRgh3BuildInputs("glitch2");
        var fixture = new TestBuildFixture(
            temporaryDirectory,
            CreateSupportFiles().Append((Rgh3TemplatePath, inputs.Template)).ToArray());
        await fixture.InstallSupportAsync();
        await File.WriteAllBytesAsync(fixture.InputPath, inputs.Source);
        SetPrivateFile(fixture.InputPath);
        CreatePrivateDirectory(fixture.OutputDirectory);
        string tools = temporaryDirectory.CreateDirectory("wine-tools");
        string wine = temporaryDirectory.CreateExecutable("wine-tools/wine");
        string winepath = temporaryDirectory.CreateExecutable("wine-tools/winepath");
        temporaryDirectory.CreateExecutable("wine-tools/wineserver");
        var runner = new CopyingProcessRunner(
            expectedToolchain: new WineXeBuildToolchain(wine, winepath),
            trustedSupervisorLaunch: trustedSupervisor.Prepare(),
            onPrepare: () =>
            {
                string workspace = Assert.Single(Directory.EnumerateDirectories(fixture.WorkspaceRoot));
                string registry = Path.Join(workspace, "wine-prefix", "system.reg");
                File.WriteAllText(registry, "WINE REGISTRY Version 2\n\n" +
                    "[Software\\\\Microsoft\\\\Cryptography\\\\Defaults\\\\Provider\\\\Custom] 0\n" +
                    "\"Image Path\"=\"Z:\\\\tmp\\\\evil.dll\"\n");
                SetPrivateFile(registry);
            });
        var progress = new RecordingProgress();
        XeBuildRequest request = fixture.CreateRequest(
            "unexpected-prefix.bin",
            options: new XeBuildBuildOptions(rgh3: true),
            keepWorkspace: true,
            source: new XeBuildSourceContext(
                fixture.InputPath, inputs.Source.LongLength, SHA256.HashData(inputs.Source), ConsoleId.Trinity16Mb));

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(() =>
            fixture.CreateBackend(runner, progress, WineXeBuildToolchain.Default,
                WineXeBuildToolchainResolver.CreateSyntheticNativeFixtureResolver(tools)).BuildAsync(request));

        Assert.Equal(ExitCode.MissingPrerequisite, failure.Code);
        Assert.Equal("wine-native-prefix-unsupported", failure.Kind);
        Assert.Empty(runner.Calls);
        Assert.DoesNotContain(progress.Events, value =>
            value.Kind is "staging-cpukey" or "verifying-xebuild-input" or "running-xebuild");
        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path, "cpukey.txt", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.WorkspaceRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.OutputDirectory));
        Assert.False(File.Exists(request.OutputPath));
    }

    [SupportedOSPlatform("linux")]
    [Fact]
    public async Task Post_key_native_helper_mutation_prevents_build_and_removes_sensitive_staging_and_output_sidecars()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temporaryDirectory = new TemporaryDirectory();
        using var trustedSupervisor = new ExternalProcessSupervisorLaunchTests.ProtectedRunnableSupervisorDeployment();
        using Rgh3BuildInputs inputs = CreateRgh3BuildInputs("glitch2");
        var fixture = new TestBuildFixture(
            temporaryDirectory,
            CreateSupportFiles().Append((Rgh3TemplatePath, inputs.Template)).ToArray());
        await fixture.InstallSupportAsync();
        await File.WriteAllBytesAsync(fixture.InputPath, inputs.Source);
        SetPrivateFile(fixture.InputPath);
        CreatePrivateDirectory(fixture.OutputDirectory);
        string tools = temporaryDirectory.CreateDirectory("wine-tools");
        string wine = temporaryDirectory.CreateExecutable("wine-tools/wine");
        string winepath = temporaryDirectory.CreateExecutable("wine-tools/winepath");
        string server = temporaryDirectory.CreateExecutable("wine-tools/wineserver");
        string? temporaryOutput = null;
        var runner = new CopyingProcessRunner(
            expectedToolchain: new WineXeBuildToolchain(wine, winepath),
            trustedSupervisorLaunch: trustedSupervisor.Prepare(),
            onWinePath: invocation =>
            {
                Assert.NotNull(invocation.TrustedNativeClosure);
                Assert.True(File.Exists(Path.Join(invocation.WorkingDirectory, "xeBuild", "data", "cpukey.txt")));
                temporaryOutput = invocation.Arguments[1];
                File.WriteAllText(temporaryOutput, CpuKeyText);
                File.WriteAllText(temporaryOutput + ".log", CpuKeyText);
                File.SetUnixFileMode(server, File.GetUnixFileMode(server) | UnixFileMode.OtherWrite);
            });
        XeBuildRequest request = fixture.CreateRequest(
            "never-published-native-change.bin",
            options: new XeBuildBuildOptions(rgh3: true),
            keepWorkspace: true,
            source: new XeBuildSourceContext(
                fixture.InputPath, inputs.Source.LongLength, SHA256.HashData(inputs.Source), ConsoleId.Trinity16Mb));

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(() =>
            fixture.CreateBackend(runner, toolchain: WineXeBuildToolchain.Default,
                toolchainResolver: WineXeBuildToolchainResolver.CreateSyntheticNativeFixtureResolver(tools)).BuildAsync(request));

        Assert.Equal(ExitCode.MissingPrerequisite, failure.Code);
        Assert.Equal("native-closure-unavailable", failure.Kind);
        Assert.Equal(["version", "probe", "initialize", "winepath"], runner.Calls.Select(call => call.Kind));
        Assert.False(File.Exists(request.OutputPath));
        Assert.NotNull(temporaryOutput);
        Assert.False(File.Exists(temporaryOutput));
        Assert.False(File.Exists(temporaryOutput + ".log"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.OutputDirectory));
        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path, "cpukey.txt", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(fixture.WorkspaceRoot, "nanddump.bin", SearchOption.AllDirectories));
        foreach (string log in Directory.EnumerateFiles(fixture.WorkspaceRoot, "*.log", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain(CpuKeyText, await File.ReadAllTextAsync(log), StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain(CpuKeyText, failure.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("glitch2", false)]
    [InlineData("glitch2m", false)]
    [InlineData("glitch2", true)]
    [InlineData("glitch2m", true)]
    public async Task Supported_Rgh3_pipeline_converts_then_independently_matches_and_atomically_publishes_real_stage_evidence(
        string type,
        bool recreateOutput)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using Rgh3BuildInputs inputs = CreateRgh3BuildInputs(type);
        var fixture = new TestBuildFixture(
            temporaryDirectory,
            CreateSupportFiles().Append((Rgh3TemplatePath, inputs.Template)).ToArray());
        await fixture.InstallSupportAsync();
        await File.WriteAllBytesAsync(fixture.InputPath, inputs.Source);
        SetPrivateFile(fixture.InputPath);
        CreatePrivateDirectory(fixture.OutputDirectory);
        string outputPath = Path.Combine(fixture.OutputDirectory, "converted RGH3.bin");
        await File.WriteAllBytesAsync(outputPath, [0x4F, 0x4C, 0x44]);
        SetPrivateFile(outputPath);
        byte[] inputHash = SHA256.HashData(inputs.Source);
        FileTreeSnapshot supportBefore = await CaptureTreeAsync(fixture.SupportRoot.DirectoryPath);
        var runner = new CopyingProcessRunner(recreateOutput: recreateOutput);
        XeBuildRequest request = fixture.CreateRequest(
            outputPath,
            targetType: type,
            options: new XeBuildBuildOptions(rgh3: true),
            force: true,
            keepWorkspace: true,
            source: new XeBuildSourceContext(fixture.InputPath, inputs.Source.LongLength, inputHash, ConsoleId.Trinity16Mb));

        XeBuildResult result = await fixture.CreateBackend(runner).BuildAsync(request);

        Assert.Equal(outputPath, result.OutputPath);
        Assert.Equal(inputs.Source.LongLength, result.OutputByteLength);
        Assert.Equal(XeBuildBackendKind.Wine, result.Backend);
        Assert.Equal(["version", "probe", "winepath", "build"], runner.Calls.Select(call => call.Kind));
        Assert.All(runner.Calls, call =>
        {
            Assert.Single(call.Invocation.EnvironmentUpdates);
            Assert.Null(call.Invocation.TrustedSupervisorLaunch);
            Assert.Null(call.Invocation.TrustedNativeClosure);
            Assert.True(call.Invocation.EnvironmentUpdates.ContainsKey("WINEPREFIX"));
            Assert.False(call.Invocation.EnvironmentUpdates.ContainsKey("PATH"));
        });
        Assert.Equal(recreateOutput, runner.RecreatedOutput);
        if (OperatingSystem.IsLinux())
        {
            ProcessCall winepath = Assert.Single(runner.Calls, call => call.Kind == "winepath");
            string stageDirectory = Path.GetDirectoryName(winepath.Invocation.Arguments[1])!;
            Assert.Equal(fixture.OutputDirectory, Path.GetDirectoryName(stageDirectory));
            Assert.False(Directory.Exists(stageDirectory));
        }

        Assert.Equal(outputPath, Assert.Single(Directory.EnumerateFileSystemEntries(fixture.OutputDirectory)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.WorkspaceRoot));
        Assert.DoesNotContain(runner.Calls.SelectMany(call => call.Invocation.Arguments), argument =>
            argument.Contains(CpuKeyText, StringComparison.OrdinalIgnoreCase) ||
            argument.Contains(fixture.InputPath, StringComparison.Ordinal));
        await using FileStream published = File.OpenRead(outputPath);
        NandInspectionResult inspection = await NandImageService.InspectAsync(published, request.CpuKey);
        Assert.Equal(KeyvaultCpuKeyVerificationStatus.Verified, inspection.Keyvault.Inspection.CpuKeyVerification);
        Assert.Equal(ConsoleId.Trinity16Mb, inspection.SemanticEvidence.Console.Console!.Id);
        Assert.Equal(17559, inspection.SemanticEvidence.KernelDashboard.ExactDashboardVersion);
        Assert.True(inspection.SemanticEvidence.ImageFamily.IsConfirmed);
        Assert.Equal(NandImageFamily.Rgh3, inspection.SemanticEvidence.ImageFamily.Family);
        NandDirectOutputEvidenceProvenance direct = Assert.Single(inspection.SemanticEvidence.ImageFamily.DirectEvidence);
        Assert.Equal(NandDirectOutputEvidenceSource.ReviewedDecryptedStageFingerprint, direct.Source);
        Assert.Equal(NandRgh3OutputEvidence.ReviewedManifest.Sha256, direct.ManifestSha256);
        Assert.Equal("rgh3-patched-cbx-zero-cpu-v1", direct.FingerprintId);
        Assert.Equal(NandBootloaderStageKind.CB_X, direct.Stage);
        Assert.Equal(NandBootloaderDecryptionStatus.Decrypted, direct.DecryptionStatus);
        await using FileStream unchangedInput = File.OpenRead(fixture.InputPath);
        Assert.Equal(inputHash, await SHA256.HashDataAsync(unchangedInput));
        await AssertTreeUnchangedAsync(supportBefore, fixture.SupportRoot.DirectoryPath);
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("glitch2")]
    [InlineData("glitch2m")]
    public async Task Supported_Rgh3_atomically_replaces_owned_output_directly_in_tmp_without_leaving_sidecars(string type)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        using Rgh3BuildInputs inputs = CreateRgh3BuildInputs(type);
        var fixture = new TestBuildFixture(
            temporaryDirectory,
            CreateSupportFiles().Append((Rgh3TemplatePath, inputs.Template)).ToArray());
        await fixture.InstallSupportAsync();
        await File.WriteAllBytesAsync(fixture.InputPath, inputs.Source);
        SetPrivateFile(fixture.InputPath);
        byte[] inputHash = SHA256.HashData(inputs.Source);
        FileTreeSnapshot supportBefore = await CaptureTreeAsync(fixture.SupportRoot.DirectoryPath);
        string outputPath = Path.Combine("/tmp", $"jrunner-wine-direct-tmp-{Guid.NewGuid():N}.bin");
        var runner = new CopyingProcessRunner();
        string? temporaryOutputPath = null;
        string? temporaryStageDirectory = null;
        var progress = new RecordingProgress(value =>
        {
            if (value.Kind != "validating-xebuild-output")
            {
                return;
            }

            ProcessCall winepath = Assert.Single(runner.Calls, call => call.Kind == "winepath");
            temporaryOutputPath = winepath.Invocation.Arguments[1];
            temporaryStageDirectory = Path.GetDirectoryName(temporaryOutputPath);
            Assert.NotNull(temporaryStageDirectory);
            Assert.Equal("/tmp", Path.GetDirectoryName(temporaryStageDirectory));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(temporaryStageDirectory));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(temporaryOutputPath));
            string logPath = string.Concat(temporaryOutputPath, ".log");
            File.WriteAllText(logPath, "Direct sticky-directory output regression.");
            SetPrivateFile(logPath);
        });

        try
        {
            await File.WriteAllBytesAsync(outputPath, [0x4F, 0x4C, 0x44]);
            SetPrivateFile(outputPath);
            XeBuildRequest request = fixture.CreateRequest(
                outputPath,
                targetType: type,
                options: new XeBuildBuildOptions(rgh3: true),
                force: true,
                source: new XeBuildSourceContext(fixture.InputPath, inputs.Source.LongLength, inputHash, ConsoleId.Trinity16Mb));

            XeBuildResult result = await fixture.CreateBackend(runner, progress).BuildAsync(request);

            Assert.Equal(outputPath, result.OutputPath);
            Assert.Equal(inputs.Source.LongLength, result.OutputByteLength);
            Assert.Equal(XeBuildBackendKind.Wine, result.Backend);
            Assert.Equal(["version", "probe", "winepath", "build"], runner.Calls.Select(call => call.Kind));
            Assert.NotNull(temporaryOutputPath);
            Assert.NotNull(temporaryStageDirectory);
            Assert.False(File.Exists(temporaryOutputPath));
            Assert.False(File.Exists(string.Concat(temporaryOutputPath, ".log")));
            Assert.False(Directory.Exists(temporaryStageDirectory));
            Assert.Empty(Directory.EnumerateFileSystemEntries(
                "/tmp", string.Concat(Path.GetFileName(temporaryStageDirectory), "*")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.WorkspaceRoot));
            await using FileStream published = File.OpenRead(outputPath);
            NandInspectionResult inspection = await NandImageService.InspectAsync(published, request.CpuKey);
            Assert.True(inspection.SemanticEvidence.ImageFamily.IsConfirmed);
            Assert.Equal(NandImageFamily.Rgh3, inspection.SemanticEvidence.ImageFamily.Family);
            await using FileStream unchangedInput = File.OpenRead(fixture.InputPath);
            Assert.Equal(inputHash, await SHA256.HashDataAsync(unchangedInput));
            await AssertTreeUnchangedAsync(supportBefore, fixture.SupportRoot.DirectoryPath);
        }
        finally
        {
            File.Delete(outputPath);
            foreach (ProcessCall winepath in runner.Calls.Where(call => call.Kind == "winepath"))
            {
                string reservedPath = winepath.Invocation.Arguments[1];
                string? reservedDirectory = Path.GetDirectoryName(reservedPath);
                if (reservedDirectory is null || !Directory.Exists(reservedDirectory))
                {
                    continue;
                }

                string logPath = string.Concat(reservedPath, ".log");
                if (File.Exists(logPath))
                {
                    File.Delete(logPath);
                }

                if (File.Exists(reservedPath))
                {
                    File.Delete(reservedPath);
                }
                if (string.Equals(Path.GetDirectoryName(reservedDirectory), "/tmp", StringComparison.Ordinal) &&
                    Directory.Exists(reservedDirectory))
                {
                    Directory.Delete(reservedDirectory, recursive: true);
                }
            }
        }
    }

    [Theory]
    [InlineData("glitch2")]
    [InlineData("glitch2m")]
    public async Task Supported_Rgh3_without_the_real_converted_stage_signature_fails_with_exit_7_and_preserves_output(string type)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using Rgh3BuildInputs inputs = CreateRgh3BuildInputs(type, patchableTemplate: false);
        var fixture = new TestBuildFixture(
            temporaryDirectory,
            CreateSupportFiles().Append((Rgh3TemplatePath, inputs.Template)).ToArray());
        await fixture.InstallSupportAsync();
        await File.WriteAllBytesAsync(fixture.InputPath, inputs.Source);
        SetPrivateFile(fixture.InputPath);
        CreatePrivateDirectory(fixture.OutputDirectory);
        string outputPath = Path.Combine(fixture.OutputDirectory, "preserved.bin");
        byte[] originalOutput = [0x4F, 0x52, 0x49, 0x47, 0x49, 0x4E, 0x41, 0x4C];
        await File.WriteAllBytesAsync(outputPath, originalOutput);
        SetPrivateFile(outputPath);
        byte[] inputHash = SHA256.HashData(inputs.Source);
        FileTreeSnapshot supportBefore = await CaptureTreeAsync(fixture.SupportRoot.DirectoryPath);
        var runner = new CopyingProcessRunner();
        XeBuildRequest request = fixture.CreateRequest(
            outputPath,
            targetType: type,
            options: new XeBuildBuildOptions(rgh3: true),
            force: true,
            source: new XeBuildSourceContext(fixture.InputPath, inputs.Source.LongLength, inputHash, ConsoleId.Trinity16Mb));

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(() =>
            fixture.CreateBackend(runner).BuildAsync(request));

        Assert.Equal(ExitCode.ExternalProcess, failure.Code);
        Assert.Equal(7, (int)failure.Code);
        Assert.Equal("xebuild-output-invalid", failure.Kind);
        Assert.Contains("ImageFamilyAbsent", failure.Message, StringComparison.Ordinal);
        Assert.Equal(["version", "probe", "winepath", "build"], runner.Calls.Select(call => call.Kind));
        Assert.Equal(originalOutput, await File.ReadAllBytesAsync(outputPath));
        Assert.Equal(outputPath, Assert.Single(Directory.EnumerateFileSystemEntries(fixture.OutputDirectory)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.WorkspaceRoot));
        await using FileStream unchangedInput = File.OpenRead(fixture.InputPath);
        Assert.Equal(inputHash, await SHA256.HashDataAsync(unchangedInput));
        await AssertTreeUnchangedAsync(supportBefore, fixture.SupportRoot.DirectoryPath);
    }

    [Theory]
    [SupportedOSPlatform("linux")]
    [InlineData("glitch2", "same-size-rewrite")]
    [InlineData("glitch2m", "same-size-rewrite")]
    [InlineData("glitch2", "regular-replacement")]
    [InlineData("glitch2m", "regular-replacement")]
    [InlineData("glitch2", "symlink-replacement")]
    [InlineData("glitch2m", "symlink-replacement")]
    public async Task Supported_Rgh3_output_mutation_after_completed_semantic_inspection_fails_closed_and_cleans_up(
        string type,
        string mutation)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        using Rgh3BuildInputs inputs = CreateRgh3BuildInputs(type);
        var fixture = new TestBuildFixture(
            temporaryDirectory,
            CreateSupportFiles().Append((Rgh3TemplatePath, inputs.Template)).ToArray());
        await fixture.InstallSupportAsync();
        await File.WriteAllBytesAsync(fixture.InputPath, inputs.Source);
        SetPrivateFile(fixture.InputPath);
        CreatePrivateDirectory(fixture.OutputDirectory);
        string outputPath = Path.Combine(fixture.OutputDirectory, "preserved-after-validation.bin");
        byte[] originalOutput = [0x4F, 0x52, 0x49, 0x47, 0x49, 0x4E, 0x41, 0x4C];
        await File.WriteAllBytesAsync(outputPath, originalOutput);
        SetPrivateFile(outputPath);
        string attackerTargetPath = Path.Combine(temporaryDirectory.Path, "attacker-target.bin");
        if (mutation == "symlink-replacement")
        {
            await File.WriteAllBytesAsync(attackerTargetPath, inputs.Source);
            SetPrivateFile(attackerTargetPath);
        }

        var runner = new CopyingProcessRunner();
        bool completedInputInspection = false;
        bool outputValidationStarted = false;
        bool mutationApplied = false;
        string? temporaryOutputPath = null;
        var progress = new RecordingProgress(value =>
        {
            if (value.Kind == "validating-xebuild-output")
            {
                outputValidationStarted = true;
                return;
            }

            if (value.Kind != "completed-nand-inspection")
            {
                return;
            }

            if (!outputValidationStarted)
            {
                completedInputInspection = true;
                return;
            }

            if (mutationApplied)
            {
                return;
            }

            // This synchronous callback runs after the output's semantic reads, not input inspection.
            Assert.True(completedInputInspection);
            temporaryOutputPath = Assert.Single(Directory.EnumerateFiles(
                fixture.OutputDirectory, "*.tmp", SearchOption.AllDirectories));
            string stageDirectory = Path.GetDirectoryName(temporaryOutputPath)!;
            Assert.Equal(fixture.OutputDirectory, Path.GetDirectoryName(stageDirectory));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(stageDirectory));
            Assert.Equal(inputs.Source.LongLength, new FileInfo(temporaryOutputPath).Length);
            string logPath = string.Concat(temporaryOutputPath, ".log");
            File.WriteAllText(logPath, "Output publication mutation regression.");
            SetPrivateFile(logPath);
            // The unconverted fixture has the same length but lacks the validated RGH3 stage evidence.
            switch (mutation)
            {
                case "same-size-rewrite":
                    File.WriteAllBytes(temporaryOutputPath, inputs.Source);
                    break;
                case "regular-replacement":
                    File.Delete(temporaryOutputPath);
                    File.WriteAllBytes(temporaryOutputPath, inputs.Source);
                    SetPrivateFile(temporaryOutputPath);
                    break;
                case "symlink-replacement":
                    File.Delete(temporaryOutputPath);
                    File.CreateSymbolicLink(temporaryOutputPath, attackerTargetPath);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mutation));
            }

            if (mutation == "symlink-replacement")
            {
                Assert.Equal(attackerTargetPath, new FileInfo(temporaryOutputPath).LinkTarget);
            }
            else
            {
                Assert.Equal(inputs.Source.LongLength, new FileInfo(temporaryOutputPath).Length);
            }
            mutationApplied = true;
        });
        XeBuildRequest request = fixture.CreateRequest(
            outputPath,
            targetType: type,
            options: new XeBuildBuildOptions(rgh3: true),
            force: true,
            source: new XeBuildSourceContext(fixture.InputPath, inputs.Source.LongLength, SHA256.HashData(inputs.Source), ConsoleId.Trinity16Mb));

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(() =>
            fixture.CreateBackend(runner, progress).BuildAsync(request));

        Assert.True(completedInputInspection);
        Assert.True(outputValidationStarted);
        Assert.True(mutationApplied);
        Assert.Equal(ExitCode.ExternalProcess, failure.Code);
        Assert.Contains(failure.Kind, new[] { "external-output-changed", "xebuild-output-invalid" });
        Assert.Equal(["version", "probe", "winepath", "build"], runner.Calls.Select(call => call.Kind));
        Assert.Equal(originalOutput, await File.ReadAllBytesAsync(outputPath));
        if (mutation == "symlink-replacement")
        {
            Assert.Equal(inputs.Source, await File.ReadAllBytesAsync(attackerTargetPath));
        }

        Assert.Equal(outputPath, Assert.Single(Directory.EnumerateFileSystemEntries(fixture.OutputDirectory)));
        Assert.False(File.Exists(temporaryOutputPath));
        Assert.False(File.Exists(string.Concat(temporaryOutputPath, ".log")));
        Assert.False(Directory.Exists(Path.GetDirectoryName(temporaryOutputPath)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.WorkspaceRoot));
    }

    [Theory]
    [InlineData("glitch2", UnixFileMode.GroupWrite)]
    [InlineData("glitch2m", UnixFileMode.GroupWrite)]
    [InlineData("glitch2", UnixFileMode.OtherWrite)]
    [InlineData("glitch2m", UnixFileMode.OtherWrite)]
    public async Task Supported_Rgh3_rejects_an_attacker_writable_output_directory_before_Wine_or_workspace_creation(
        string type,
        UnixFileMode unsafeWritePermission)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        var fixture = new TestBuildFixture(temporaryDirectory);
        await fixture.InstallSupportAsync();
        CreatePrivateDirectory(fixture.OutputDirectory);
        string outputPath = Path.Combine(fixture.OutputDirectory, "preserved.bin");
        byte[] originalOutput = [0x4F, 0x52, 0x49, 0x47, 0x49, 0x4E, 0x41, 0x4C];
        await File.WriteAllBytesAsync(outputPath, originalOutput);
        SetPrivateFile(outputPath);
        File.SetUnixFileMode(
            fixture.OutputDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | unsafeWritePermission);
        FileTreeSnapshot before = await CaptureTreeAsync(temporaryDirectory.Path);
        var runner = new RejectingProcessRunner();
        XeBuildRequest request = fixture.CreateRequest(
            outputPath,
            targetType: type,
            options: new XeBuildBuildOptions(rgh3: true),
            force: true);

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(() =>
            fixture.CreateBackend(runner).BuildAsync(request));

        Assert.Equal(ExitCode.Usage, failure.Code);
        Assert.Equal("output-directory-unsafe", failure.Kind);
        Assert.Contains("private", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runner.Calls);
        Assert.Equal(originalOutput, await File.ReadAllBytesAsync(outputPath));
        Assert.Equal(outputPath, Assert.Single(Directory.EnumerateFileSystemEntries(fixture.OutputDirectory)));
        Assert.False(Directory.Exists(fixture.WorkspaceRoot));
        await AssertTreeUnchangedAsync(before, temporaryDirectory.Path);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("valid")]
    [InlineData("missing-activation")]
    [InlineData("legacy-activation")]
    [InlineData("corrupt-generation")]
    [InlineData("undeclared-file")]
    public async Task Evidence_gate_precedes_support_resolution_Wine_preflight_and_dependency_planning(string supportState)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var fixture = new TestBuildFixture(temporaryDirectory);
        if (supportState != "absent")
        {
            await fixture.InstallSupportAsync();
            SupportStatusResult installed = await new SupportPayloadValidator(fixture.Manifest).ValidateAsync(fixture.SupportRoot);
            Assert.Equal(SupportStatusKind.Valid, installed.Status);
        }

        switch (supportState)
        {
            case "missing-activation":
                File.Delete(fixture.ActiveMarkerPath);
                break;
            case "legacy-activation":
                await File.WriteAllTextAsync(fixture.ActiveMarkerPath, JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    payloadId = fixture.Manifest.PayloadId,
                    archiveSha256 = fixture.Manifest.Archive.Sha256,
                    generationId = "current",
                }));
                break;
            case "corrupt-generation":
                await File.WriteAllTextAsync(fixture.GenerationFile("xeBuild/options.ini"), "mutated support\n");
                break;
            case "undeclared-file":
                string undeclared = fixture.GenerationFile("xeBuild/extra.bin");
                await File.WriteAllTextAsync(undeclared, "undeclared\n");
                SetPrivateFile(undeclared);
                break;
        }

        var runner = new RejectingProcessRunner();
        var progress = new RecordingProgress();
        XeBuildRequest request = fixture.CreateRequest(
            "blocked-support.bin",
            dashboardVersion: 99999,
            options: new XeBuildBuildOptions(dashLaunch: true));
        FileTreeSnapshot before = await CaptureTreeAsync(temporaryDirectory.Path);

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(() =>
            fixture.CreateBackend(runner, progress).BuildAsync(request));

        AssertUnavailable(failure, request.Target);
        Assert.Empty(runner.Calls);
        Assert.Empty(progress.Events);
        Assert.False(Directory.Exists(fixture.WorkspaceRoot));
        Assert.False(Directory.Exists(fixture.OutputDirectory));
        await AssertTreeUnchangedAsync(before, temporaryDirectory.Path);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Evidence_gate_preserves_existing_output_workspaces_without_sidecars(
        bool keepWorkspace,
        bool force)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var fixture = new TestBuildFixture(temporaryDirectory);
        await fixture.InstallSupportAsync();
        CreatePrivateDirectory(fixture.WorkspaceRoot);
        string workspaceSentinel = Path.Combine(fixture.WorkspaceRoot, "caller-file.txt");
        await File.WriteAllTextAsync(workspaceSentinel, "existing caller workspace\n");
        SetPrivateFile(workspaceSentinel);
        CreatePrivateDirectory(fixture.OutputDirectory);
        string outputPath = Path.Combine(fixture.OutputDirectory, "caller image.bin");
        byte[] originalOutput = [0x43, 0x41, 0x4C, 0x4C, 0x45, 0x52];
        await File.WriteAllBytesAsync(outputPath, originalOutput);
        SetPrivateFile(outputPath);

        var runner = new RejectingProcessRunner();
        var progress = new RecordingProgress();
        XeBuildRequest request = fixture.CreateRequest(
            outputPath,
            force: force,
            keepWorkspace: keepWorkspace);
        FileTreeSnapshot before = await CaptureTreeAsync(temporaryDirectory.Path);

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(() =>
            fixture.CreateBackend(runner, progress).BuildAsync(request));

        AssertUnavailable(failure, request.Target);
        Assert.Empty(runner.Calls);
        Assert.Empty(progress.Events);
        Assert.Equal(originalOutput, await File.ReadAllBytesAsync(outputPath));
        Assert.Equal(outputPath, Assert.Single(Directory.EnumerateFileSystemEntries(fixture.OutputDirectory)));
        Assert.Equal(workspaceSentinel, Assert.Single(Directory.EnumerateFileSystemEntries(fixture.WorkspaceRoot)));
        await AssertTreeUnchangedAsync(before, temporaryDirectory.Path);
    }

    [Fact]
    public async Task Evidence_gate_also_precedes_mutable_path_overlap_preflights_without_mutating_support()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var fixture = new TestBuildFixture(temporaryDirectory);
        await fixture.InstallSupportAsync();
        var runner = new RejectingProcessRunner();
        var progress = new RecordingProgress();
        XeBuildRequest request = fixture.CreateRequest(
            Path.Combine(fixture.SupportRoot.DirectoryPath, "not-created.bin"),
            workspaceRoot: Path.Combine(fixture.SupportRoot.DirectoryPath, "not-created-workspaces"),
            keepWorkspace: true,
            force: true);
        FileTreeSnapshot before = await CaptureTreeAsync(temporaryDirectory.Path);

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(() =>
            fixture.CreateBackend(runner, progress).BuildAsync(request));

        AssertUnavailable(failure, request.Target);
        Assert.Empty(runner.Calls);
        Assert.Empty(progress.Events);
        await AssertTreeUnchangedAsync(before, temporaryDirectory.Path);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Kind_is_Wine_and_native_request_validation_precedes_the_evidence_gate_and_cancellation(bool cancelled, bool rgh3)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var cancellationSource = new CancellationTokenSource();
        if (cancelled)
        {
            cancellationSource.Cancel();
        }

        var fixture = new TestBuildFixture(temporaryDirectory);
        var runner = new RejectingProcessRunner();
        var progress = new RecordingProgress();
        IXeBuildBackend backend = fixture.CreateBackend(runner, progress);
        XeBuildRequest request = fixture.CreateRequest(
            "native.bin",
            backend: XeBuildBackendKind.Native,
            options: new XeBuildBuildOptions(rgh3: rgh3));
        FileTreeSnapshot before = await CaptureTreeAsync(temporaryDirectory.Path);

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(() =>
            backend.BuildAsync(request, cancellationSource.Token));

        Assert.Equal(XeBuildBackendKind.Wine, backend.Kind);
        Assert.Equal(ExitCode.MissingPrerequisite, failure.Code);
        Assert.Equal("xebuild-backend-unavailable", failure.Kind);
        Assert.Empty(runner.Calls);
        Assert.Empty(progress.Events);
        await AssertTreeUnchangedAsync(before, temporaryDirectory.Path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_cancelled_Wine_request_still_cancels_before_the_evidence_gate_or_any_side_effect(bool rgh3)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var fixture = new TestBuildFixture(temporaryDirectory);
        var runner = new RejectingProcessRunner();
        var progress = new RecordingProgress();
        XeBuildRequest request = fixture.CreateRequest(
            "cancelled.bin",
            keepWorkspace: true,
            options: new XeBuildBuildOptions(rgh3: rgh3));
        FileTreeSnapshot before = await CaptureTreeAsync(temporaryDirectory.Path);

        OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.CreateBackend(runner, progress).BuildAsync(request, cancellationSource.Token));

        Assert.Equal(cancellationSource.Token, failure.CancellationToken);
        Assert.Empty(runner.Calls);
        Assert.Empty(progress.Events);
        await AssertTreeUnchangedAsync(before, temporaryDirectory.Path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Null_request_validation_retains_precedence_over_cancellation_and_evidence(bool cancelled)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var cancellationSource = new CancellationTokenSource();
        if (cancelled)
        {
            cancellationSource.Cancel();
        }

        var fixture = new TestBuildFixture(temporaryDirectory);
        var runner = new RejectingProcessRunner();
        var progress = new RecordingProgress();
        FileTreeSnapshot before = await CaptureTreeAsync(temporaryDirectory.Path);

        ArgumentNullException failure = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            fixture.CreateBackend(runner, progress).BuildAsync(null!, cancellationSource.Token));

        Assert.Equal("request", failure.ParamName);
        Assert.Empty(runner.Calls);
        Assert.Empty(progress.Events);
        await AssertTreeUnchangedAsync(before, temporaryDirectory.Path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Production_evidence_failure_renders_stable_human_and_JSON_exit_code_and_kind(bool jsonRequested)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var fixture = new TestBuildFixture(temporaryDirectory);
        var runner = new RejectingProcessRunner();
        var progress = new RecordingProgress();
        XeBuildRequest request = fixture.CreateRequest("envelope.bin");
        FileTreeSnapshot before = await CaptureTreeAsync(temporaryDirectory.Path);
        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(() =>
            fixture.CreateBackend(runner, progress).BuildAsync(request));
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        int exitCode = await CliRuntime.RenderFailureAsync(jsonRequested, standardOutput, standardError, failure.Failure);

        Assert.Equal(4, exitCode);
        AssertUnavailable(failure, request.Target);
        Assert.Equal(failure.Message + Environment.NewLine, standardError.ToString());
        if (jsonRequested)
        {
            using JsonDocument document = JsonDocument.Parse(standardOutput.ToString());
            JsonElement envelope = document.RootElement;
            Assert.Equal(1, envelope.GetProperty("schemaVersion").GetInt32());
            Assert.False(envelope.GetProperty("ok").GetBoolean());
            JsonElement error = envelope.GetProperty("error");
            Assert.Equal(4, error.GetProperty("code").GetInt32());
            Assert.Equal("xebuild-output-evidence-unavailable", error.GetProperty("kind").GetString());
            Assert.Equal(failure.Message, error.GetProperty("message").GetString());
        }
        else
        {
            Assert.Equal(string.Empty, standardOutput.ToString());
        }

        Assert.DoesNotContain(CpuKeyText, standardOutput.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(CpuKeyText, standardError.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runner.Calls);
        Assert.Empty(progress.Events);
        await AssertTreeUnchangedAsync(before, temporaryDirectory.Path);
    }

    private static Rgh3BuildInputs CreateRgh3BuildInputs(string targetType, bool patchableTemplate = true)
    {
        CpuKey requestCpuKey = CpuKey.Parse(CpuKeyText);
        CpuKey zeroCpuKey = CpuKey.Parse("00000000000000000000000000000000");
        CpuKey sourceStageKey = targetType == "glitch2m" ? zeroCpuKey : requestCpuKey;
        var logical = new byte[0x01000000];
        var template = new byte[0x140000];
        byte[]? source = null;
        byte[]? encryptedSmc = null;
        byte[]? sourceCba = null;
        byte[]? sourceCbb = null;
        byte[]? templateCba = null;
        byte[]? templatePayload = null;
        var plainSmc = new byte[SmcLength];
        bool completed = false;
        try
        {
            logical[0] = template[0] = 0xFF;
            logical[1] = template[1] = 0x4F;
            BinaryPrimitives.WriteUInt16BigEndian(logical.AsSpan(2, sizeof(ushort)), 14699);
            WriteUInt32BigEndian(logical, 0x08, FirstStageOffset);
            WriteUInt32BigEndian(template, 0x08, FirstStageOffset);
            WriteUInt32BigEndian(logical, 0x78, SmcLength);
            WriteUInt32BigEndian(template, 0x78, SmcLength);
            WriteUInt32BigEndian(logical, 0x7C, SmcOffset);
            WriteUInt32BigEndian(template, 0x7C, SmcOffset);
            plainSmc[0x100] = 0x50;
            plainSmc[0x101] = 1;
            plainSmc[0x102] = 2;
            encryptedSmc = SmcCrypto.Encrypt(plainSmc);
            encryptedSmc.CopyTo(logical, SmcOffset);
            encryptedSmc.CopyTo(template, SmcOffset);

            Span<byte> keyvault = logical.AsSpan(0x4000, KeyvaultService.KeyvaultLength);
            "TESTSERIAL01"u8.CopyTo(keyvault.Slice(0xB0));
            EncryptKeyvaultInPlace(keyvault, requestCpuKey);

            // The real converter replaces source CBA(0x800)+CBB(0x400) with three
            // 0x400-byte stages. Equal lengths keep the declared CF/CG slot in place.
            sourceCba = CreateEncryptedCbA(0x800);
            sourceCbb = CreateEncryptedCbB(9188, sourceCba, sourceStageKey);
            sourceCba.CopyTo(logical, FirstStageOffset);
            sourceCbb.CopyTo(logical, FirstStageOffset + sourceCba.Length);
            templateCba = CreateEncryptedCbA(CbStageLength);
            templatePayload = CreateEncryptedCbB(
                15432,
                templateCba,
                zeroCpuKey,
                patchProbe: patchableTemplate ? 0x646A0002u : 0x646A0003u);
            templateCba.CopyTo(template, FirstStageOffset);
            templatePayload.CopyTo(template, FirstStageOffset + templateCba.Length);

            WriteUInt32BigEndian(logical, 0x64, UpdateSlotOffset);
            BinaryPrimitives.WriteUInt16BigEndian(logical.AsSpan(0x68, sizeof(ushort)), 1);
            WriteUInt32BigEndian(logical, 0x70, 0x1000);
            WriteBootloaderHeader(logical, UpdateSlotOffset, "CF", 1888, CfStageLength);
            BinaryPrimitives.WriteUInt16BigEndian(logical.AsSpan(UpdateSlotOffset + 0x14, sizeof(ushort)), 17559);
            WriteUInt32BigEndian(logical, UpdateSlotOffset + 0x1C, CgStageLength);
            WriteBootloaderHeader(logical, UpdateSlotOffset + CfStageLength, "CG", 14699, CgStageLength);
            ReadOnlySpan<byte> xellSignature =
            [
                0x48, 0x00, 0x00, 0x20, 0x48, 0x00, 0x00, 0xEC,
                0x48, 0x00, 0x00, 0x00, 0x48, 0x00, 0x00, 0x00,
            ];
            xellSignature.CopyTo(logical.AsSpan(0x70000));
            source = new byte[NandEccCodec.GetPhysicalLengthForLogicalLength(logical.Length)];
            NandEccCodec.AddEcc(logical, source, NandPhysicalLayout.Layout1);
            var inputs = new Rgh3BuildInputs(source, template);
            completed = true;
            return inputs;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(logical);
            CryptographicOperations.ZeroMemory(plainSmc);
            if (encryptedSmc is not null) CryptographicOperations.ZeroMemory(encryptedSmc);
            if (sourceCba is not null) CryptographicOperations.ZeroMemory(sourceCba);
            if (sourceCbb is not null) CryptographicOperations.ZeroMemory(sourceCbb);
            if (templateCba is not null) CryptographicOperations.ZeroMemory(templateCba);
            if (templatePayload is not null) CryptographicOperations.ZeroMemory(templatePayload);
            if (!completed)
            {
                CryptographicOperations.ZeroMemory(template);
                if (source is not null) CryptographicOperations.ZeroMemory(source);
            }
        }
    }

    private static byte[] CreateEncryptedCbA(int length)
    {
        var plain = new byte[length];
        Span<byte> nonce = stackalloc byte[XeCrypt.HmacSha1TagLength];
        try
        {
            WriteBootloaderHeader(plain, 0, "CB", 9188, length);
            for (int index = 0; index < nonce.Length; index++)
            {
                nonce[index] = checked((byte)(0x60 + index));
            }

            return BootloaderCrypto.EncryptCb(plain, nonce);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(nonce);
        }
    }

    private static byte[] CreateEncryptedCbB(
        int build,
        ReadOnlySpan<byte> encryptedCba,
        CpuKey cpuKey,
        uint? patchProbe = null)
    {
        var stage = new byte[CbStageLength];
        if (!MemoryMarshal.TryGetArray(BootloaderCrypto.DecryptCb(encryptedCba).Output, out ArraySegment<byte> decodedCba))
        {
            throw new InvalidOperationException("Bootloader decryption must return its owned array-backed buffer.");
        }
        Span<byte> message = stackalloc byte[CpuKey.ByteLength * 2];
        Span<byte> rc4Key = stackalloc byte[XeCrypt.HmacSha1TagLength];
        bool completed = false;
        try
        {
            WriteBootloaderHeader(stage, 0, "CB", build, stage.Length);
            "XBOX_ROM"u8.CopyTo(stage.AsSpan(0x392));
            if (patchProbe is { } probe)
            {
                WriteUInt32BigEndian(stage, 0x354, probe);
            }

            stage.AsSpan(0x10, XeCrypt.HmacSha1TagLength).CopyTo(message);
            cpuKey.CopyTo(message.Slice(CpuKey.ByteLength));
            XeCrypt.HmacSha1Truncated(decodedCba.AsSpan(0x10, XeCrypt.HmacSha1TagLength), message, rc4Key);
            Rc4.TransformInPlace(rc4Key, stage.AsSpan(0x20));
            completed = true;
            return stage;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decodedCba.AsSpan());
            CryptographicOperations.ZeroMemory(message);
            CryptographicOperations.ZeroMemory(rc4Key);
            if (!completed) CryptographicOperations.ZeroMemory(stage);
        }
    }

    private static void EncryptKeyvaultInPlace(Span<byte> keyvault, CpuKey cpuKey)
    {
        Span<byte> keyBytes = stackalloc byte[CpuKey.ByteLength];
        Span<byte> rc4Key = stackalloc byte[XeCrypt.HmacSha1TagLength];
        try
        {
            cpuKey.CopyTo(keyBytes);
            XeCrypt.HmacSha1Truncated(keyBytes, keyvault.Slice(0, 0x10), rc4Key);
            Rc4.TransformInPlace(rc4Key, keyvault.Slice(0x10));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
            CryptographicOperations.ZeroMemory(rc4Key);
        }
    }

    private static void WriteBootloaderHeader(byte[] image, int offset, string magic, int build, int length)
    {
        image[offset] = checked((byte)magic[0]);
        image[offset + 1] = checked((byte)magic[1]);
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(offset + 2, sizeof(ushort)), checked((ushort)build));
        WriteUInt32BigEndian(image, offset + 0x0C, checked((uint)length));
    }

    private static void WriteUInt32BigEndian(byte[] image, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(offset, sizeof(uint)), value);

    private static void AssertUnavailable(OperationFailureException failure, XeBuildBuildTarget target)
    {
        Assert.Equal(ExitCode.MissingPrerequisite, failure.Code);
        Assert.Equal(4, (int)failure.Code);
        Assert.Equal("xebuild-output-evidence-unavailable", failure.Kind);
        Assert.Contains(target.TypeCanonicalName, failure.Message, StringComparison.Ordinal);
        Assert.Contains("reviewed decrypted-stage output fingerprint/signature corpus", failure.Message, StringComparison.Ordinal);
        Assert.Contains("reviewed JRunnerEx source update", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Installing support payloads or Wine alone cannot resolve", failure.Message, StringComparison.Ordinal);
        if (target.Options.Rgh3)
        {
            Assert.Contains("with RGH3", failure.Message, StringComparison.Ordinal);
        }
    }

    private static async Task<FileTreeSnapshot> CaptureTreeAsync(string root)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var modes = new Dictionary<string, UnixFileMode>(StringComparer.Ordinal);
        string[] directories = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Prepend(root)
            .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            files.Add(relative, await File.ReadAllBytesAsync(path));
            if (!OperatingSystem.IsWindows())
            {
                modes.Add(relative, File.GetUnixFileMode(path));
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            foreach (string directory in directories)
            {
                modes.Add(string.Concat(directory, "/"), File.GetUnixFileMode(Path.Combine(root, directory)));
            }
        }

        return new FileTreeSnapshot(files, directories, modes);
    }

    private static async Task AssertTreeUnchangedAsync(FileTreeSnapshot expected, string root)
    {
        FileTreeSnapshot actual = await CaptureTreeAsync(root);
        Assert.Equal(expected.Directories, actual.Directories);
        Assert.Equal(expected.Files.Keys.OrderBy(path => path, StringComparer.Ordinal), actual.Files.Keys.OrderBy(path => path, StringComparer.Ordinal));
        foreach ((string path, byte[] content) in expected.Files)
        {
            Assert.Equal(content, actual.Files[path]);
        }

        Assert.Equal(expected.Modes.OrderBy(entry => entry.Key, StringComparer.Ordinal), actual.Modes.OrderBy(entry => entry.Key, StringComparer.Ordinal));
    }

    private static (string Path, byte[] Content)[] CreateSupportFiles() =>
    [
        ("xeBuild/17559/_glitch2.ini", Encoding.ASCII.GetBytes("[trinitybl]\n[coronabl]\n")),
        ("xeBuild/17559/_glitch2m.ini", Encoding.ASCII.GetBytes("[trinitybl]\n[coronabl]\n")),
        ("xeBuild/17559/dashboard-dependency.bin", [0x17, 0x55, 0x09]),
        ("xeBuild/common/placeholder.bin", [0x01]),
        ("xeBuild/data/.keep", [0x00]),
        ("xeBuild/options.ini", Encoding.ASCII.GetBytes("options = preserved\n")),
        ("xeBuild/xeBuild.exe", Encoding.ASCII.GetBytes("MZ synthetic xeBuild\n")),
    ];

    private static SupportManifest CreateManifest((string Path, byte[] Content)[] files)
    {
        SupportFile[] manifestFiles = files.Select(file => new SupportFile(file.Path, file.Content.LongLength, Convert.ToHexString(SHA256.HashData(file.Content))))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();
        return new SupportManifest(
            schemaVersion: 1,
            payloadId: "wine-backend-test",
            archive: new SupportArchive(new Uri("https://example.test/support.zip"), byteLength: 1, sha256: Convert.ToHexString(SHA256.HashData([0x01]))),
            uncompressedByteLength: manifestFiles.Sum(file => file.ByteLength),
            files: new ReadOnlyCollection<SupportFile>(manifestFiles));
    }

    private static async Task WriteGenerationAsync(SupportRoot root, (string Path, byte[] Content)[] files)
    {
        CreatePrivateDirectory(root.DirectoryPath);
        string installations = Path.Combine(root.DirectoryPath, "installations");
        CreatePrivateDirectory(installations);
        string generation = Path.Combine(installations, "current");
        CreatePrivateDirectory(generation);
        foreach ((string relative, byte[] content) in files)
        {
            string directory = generation;
            string[] segments = relative.Split('/');
            foreach (string segment in segments[..^1])
            {
                directory = Path.Combine(directory, segment);
                CreatePrivateDirectory(directory);
            }

            string destination = Path.Combine(directory, segments[^1]);
            await File.WriteAllBytesAsync(destination, content);
            SetPrivateFile(destination);
        }
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(path, privateMode);
            File.SetUnixFileMode(path, privateMode);
        }
        else
        {
            Directory.CreateDirectory(path);
        }
    }

    private static void SetPrivateFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private sealed record FileTreeSnapshot(
        IReadOnlyDictionary<string, byte[]> Files,
        string[] Directories,
        IReadOnlyDictionary<string, UnixFileMode> Modes);

    private sealed class RecordingProgress(Action<OperationProgress>? callback = null) : IProgress<OperationProgress>
    {
        internal ConcurrentQueue<OperationProgress> Events { get; } = new();

        public void Report(OperationProgress value)
        {
            Events.Enqueue(value);
            callback?.Invoke(value);
        }
    }

    private sealed class RejectingProcessRunner : IExternalProcessRunner
    {
        internal static WineXeBuildToolchain Toolchain { get; } = new("never-invoked-wine", "never-invoked-winepath");
        internal ConcurrentQueue<ExternalProcessInvocation> Calls { get; } = new();

        public void EnsureExecutableAvailable(ExternalProcessInvocation invocation)
        {
            Calls.Enqueue(invocation);
            throw new InvalidOperationException("The unavailable output evidence gate must precede executable probing.");
        }

        public Task<ExternalProcessResult> RunAsync(ExternalProcessInvocation invocation, CancellationToken cancellationToken = default)
        {
            Calls.Enqueue(invocation);
            throw new InvalidOperationException("The unavailable output evidence gate must precede every external process.");
        }
    }

    private sealed record ProcessCall(string Kind, ExternalProcessInvocation Invocation);

    private sealed class CopyingProcessRunner(
        string versionOutput = "wine-10.0\n",
        bool recreateOutput = false,
        WineXeBuildToolchain? expectedToolchain = null,
        Action<ExternalProcessInvocation>? onVersion = null,
        ExternalProcessSupervisorLaunch? trustedSupervisorLaunch = null,
        Action<ExternalProcessInvocation>? onWinePath = null,
        Action? onPrepare = null) : IExternalProcessRunner
    {
        private readonly WineXeBuildToolchain _expectedToolchain = expectedToolchain ?? RejectingProcessRunner.Toolchain;
        private readonly Dictionary<string, string> _hostOutputPaths = new(StringComparer.Ordinal);
        internal ConcurrentQueue<ProcessCall> Calls { get; } = new();
        internal bool RecreatedOutput { get; private set; }

        public ExternalProcessSupervisorLaunch PrepareTrustedSupervisorLaunch()
        {
            onPrepare?.Invoke();
            return trustedSupervisorLaunch ?? ExternalProcessRunner.PrepareTrustedDefaultSupervisorLaunch();
        }

        public void EnsureExecutableAvailable(ExternalProcessInvocation invocation)
        {
            Assert.Equal(_expectedToolchain.WinePathExecutable, invocation.ExecutablePath);
            Assert.Empty(invocation.Arguments);
            Calls.Enqueue(new ProcessCall("probe", invocation));
        }

        public Task<ExternalProcessResult> RunAsync(ExternalProcessInvocation invocation, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (invocation.ExecutablePath == _expectedToolchain.WinePathExecutable)
            {
                Calls.Enqueue(new ProcessCall("winepath", invocation));
                Assert.Equal(2, invocation.Arguments.Count);
                Assert.Equal("-w", invocation.Arguments[0]);
                string hostPath = invocation.Arguments[1];
                string windowsPath = string.Concat("Z:\\fixture\\", Path.GetFileName(hostPath));
                _hostOutputPaths.Add(windowsPath, hostPath);
                onWinePath?.Invoke(invocation);
                return Task.FromResult(ProcessResult(string.Concat(windowsPath, "\n")));
            }

            Assert.Equal(_expectedToolchain.WineExecutable, invocation.ExecutablePath);
            if (invocation.Arguments.Count == 1 && invocation.Arguments[0] == "--version")
            {
                Calls.Enqueue(new ProcessCall("version", invocation));
                onVersion?.Invoke(invocation);
                return Task.FromResult(ProcessResult(versionOutput));
            }

            if (invocation.Arguments.SequenceEqual(["wineboot.exe", "-i", "-u"]))
            {
                if (!OperatingSystem.IsLinux())
                {
                    throw new InvalidOperationException("The native Wine prefix initialization fixture requires Linux.");
                }

                Assert.NotNull(invocation.TrustedSupervisorLaunch);
                Assert.NotNull(invocation.TrustedNativeClosure);
                Calls.Enqueue(new ProcessCall("initialize", invocation));
                Assert.Empty(Directory.EnumerateFiles(
                    invocation.WorkingDirectory,
                    "cpukey.txt",
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        AttributesToSkip = FileAttributes.ReparsePoint,
                    }));
                WineNativePrefixPolicyTests.InitializePrefix(invocation.EnvironmentUpdates["WINEPREFIX"]!);
                return Task.FromResult(ProcessResult(string.Empty));
            }

            Calls.Enqueue(new ProcessCall("build", invocation));
            Assert.EndsWith(Path.Combine("xeBuild", "xeBuild.exe"), invocation.Arguments[0]);
            string stagedSource = Path.Combine(invocation.WorkingDirectory, "xeBuild", "data", "nanddump.bin");
            string hostOutput = _hostOutputPaths[invocation.Arguments[^1]];
            if (recreateOutput)
            {
                if (OperatingSystem.IsLinux())
                {
                    Assert.Equal(
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                        File.GetUnixFileMode(Path.GetDirectoryName(hostOutput)!));
                }

                File.Delete(hostOutput);
                Assert.False(File.Exists(hostOutput));
                File.Copy(stagedSource, hostOutput);
                if (OperatingSystem.IsLinux())
                {
                    Assert.Equal(
                        UnixFileMode.UserRead | UnixFileMode.UserWrite,
                        File.GetUnixFileMode(hostOutput));
                }

                RecreatedOutput = true;
            }
            else
            {
                File.Copy(stagedSource, hostOutput, overwrite: true);
            }
            return Task.FromResult(ProcessResult(string.Empty));
        }

        private static ExternalProcessResult ProcessResult(string output) =>
            new(0, output, false, Encoding.UTF8.GetByteCount(output), string.Empty, false, 0);
    }

    private sealed class Rgh3BuildInputs(byte[] source, byte[] template) : IDisposable
    {
        internal byte[] Source { get; } = source;
        internal byte[] Template { get; } = template;

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(Source);
            CryptographicOperations.ZeroMemory(Template);
        }
    }

    private sealed class TestBuildFixture
    {
        private readonly (string Path, byte[] Content)[] _supportFiles;

        internal TestBuildFixture(TemporaryDirectory directory, (string Path, byte[] Content)[]? supportFiles = null)
        {
            _supportFiles = supportFiles ?? CreateSupportFiles();
            Manifest = CreateManifest(_supportFiles);
            SupportRoot = new SupportRoot(Path.Combine(directory.Path, "support"), SupportRootSource.Explicit);
            InputPath = Path.Combine(directory.Path, "absent-input.bin");
            WorkspaceRoot = Path.Combine(directory.Path, "workspaces");
            OutputDirectory = Path.Combine(directory.Path, "outputs");
        }

        internal SupportRoot SupportRoot { get; }
        internal SupportManifest Manifest { get; }
        internal string InputPath { get; }
        internal string WorkspaceRoot { get; }
        internal string OutputDirectory { get; }
        internal string ActiveMarkerPath => Path.Combine(SupportRoot.DirectoryPath, "active.json");

        internal WineXeBuildBackend CreateBackend(
            IExternalProcessRunner runner,
            IProgress<OperationProgress>? progress = null,
            WineXeBuildToolchain? toolchain = null,
            WineXeBuildToolchainResolver? toolchainResolver = null) =>
            new(Manifest, runner, toolchain ?? RejectingProcessRunner.Toolchain, progress, toolchainResolver);

        internal XeBuildRequest CreateRequest(
            string outputName,
            bool force = false,
            string? workspaceRoot = null,
            bool keepWorkspace = false,
            int dashboardVersion = 17559,
            XeBuildBuildOptions? options = null,
            string targetType = "glitch2",
            XeBuildBackendKind backend = XeBuildBackendKind.Wine,
            XeBuildSourceContext? source = null) =>
            new(
                SupportRoot.DirectoryPath,
                source ?? new XeBuildSourceContext(InputPath, 0x01000000, ComputeInputSha256(), ConsoleId.Trinity16Mb),
                CpuKey.Parse(CpuKeyText),
                Path.IsPathRooted(outputName) ? outputName : Path.Combine(OutputDirectory, outputName),
                new XeBuildBuildTarget("Trinity 16MB", dashboardVersion, targetType, options),
                new XeBuildExecutionOptions(
                    backend: backend,
                    workspaceRootPath: workspaceRoot ?? WorkspaceRoot,
                    keepWorkspace: keepWorkspace,
                    overwriteExistingOutput: force));

        private byte[] ComputeInputSha256()
        {
            if (!File.Exists(InputPath))
            {
                return SHA256.HashData(Array.Empty<byte>());
            }

            using FileStream input = File.OpenRead(InputPath);
            return SHA256.HashData(input);
        }

        internal string GenerationFile(string relativePath) =>
            Path.Combine(SupportRoot.DirectoryPath, "installations", "current", relativePath.Replace('/', Path.DirectorySeparatorChar));

        internal async Task InstallSupportAsync()
        {
            await WriteGenerationAsync(SupportRoot, _supportFiles);
            await File.WriteAllTextAsync(ActiveMarkerPath, JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                payloadId = Manifest.PayloadId,
                releaseTag = Manifest.ReleaseTag,
                sourceUrl = Manifest.Archive.DownloadUri.AbsoluteUri,
                archiveSha256 = Manifest.Archive.Sha256,
                canonicalManifestSha256 = Manifest.CanonicalManifestSha256,
                installedAtUtc = DateTimeOffset.UnixEpoch,
                activePayloadPath = "installations/current",
                generationId = "current",
            }));
            SetPrivateFile(ActiveMarkerPath);
        }
    }

    internal sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), $".jrunner-wine-backend-tests-{Guid.NewGuid():N}");
            CreatePrivateDirectory(Path);
        }

        internal string Path { get; }

        internal string CreateDirectory(string relativePath)
        {
            string directory = Path;
            foreach (string component in relativePath.Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                if (component == ".")
                {
                    continue;
                }
                if (component == ".." || System.IO.Path.IsPathRooted(relativePath))
                {
                    throw new ArgumentException("Fixture directories must remain beneath their temporary root.", nameof(relativePath));
                }
                directory = System.IO.Path.Combine(directory, component);
                CreatePrivateDirectory(directory);
            }
            return directory;
        }

        internal string CreateExecutable(string relativePath)
        {
            string executable = System.IO.Path.Combine(Path, relativePath);
            CreateDirectory(System.IO.Path.GetDirectoryName(relativePath) ?? string.Empty);
            // A real static ELF layout for metadata tests. The injected runner never executes it.
            File.WriteAllBytes(executable, CreateNativeElfImage());
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    executable,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            return executable;
        }

        internal static byte[] CreateNativeElfImage(
            string? interpreter = null, string? needed = null, string? rpath = null, string? runpath = null)
        {
            var image = new NativeElfTestImage(2, 2)
            {
                EntryPoint = 0x10000,
                Interpreter = interpreter,
                HasDynamicSegment = needed is not null || rpath is not null || runpath is not null,
            };
            if (needed is not null)
            {
                image.AddStringTag(1, needed);
            }
            if (rpath is not null)
            {
                image.AddStringTag(15, rpath);
            }
            if (runpath is not null)
            {
                image.AddStringTag(29, runpath);
            }
            return image.Build();
        }


        [SupportedOSPlatform("linux")]
        internal static string GetCanonicalPath(string path)
        {
            IntPtr resolved = RealPath(path, IntPtr.Zero);
            Assert.NotEqual(IntPtr.Zero, resolved);
            try
            {
                return Assert.IsType<string>(Marshal.PtrToStringUTF8(resolved));
            }
            finally
            {
                Free(resolved);
            }
        }

        [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
        private static extern IntPtr RealPath(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            IntPtr resolvedPath);

        [DllImport("libc", EntryPoint = "free")]
        private static extern void Free(IntPtr pointer);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
