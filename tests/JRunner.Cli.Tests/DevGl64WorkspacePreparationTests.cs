using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using JRunner.Cli.Infrastructure;
using JRunner.Core.Configuration;
using JRunner.Core.Contracts;
using JRunner.Core.Nand.Models;
using JRunner.Core.Nand.Security;
using JRunner.Core.XeBuild;
using JRunner.Core.XeBuild.Algorithms;
using JRunner.Core.XeBuild.Preparation;
using Xunit;

namespace JRunner.Cli.Tests;

public sealed class DevGl64WorkspacePreparationTests
{
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode PrivateDirectoryMode = PrivateFileMode | UnixFileMode.UserExecute;

    [Fact]
    public async Task Apply_and_dispose_restore_the_raw_ini_and_remove_every_generated_file()
    {
        using var workspace = new TemporaryDirectory();
        XeBuildPreparedPlan plan = CreateDevGl64Plan();
        WorkspaceFiles files = await CreateWorkspaceFilesAsync(workspace.Path, plan);
        byte[] originalIni = await File.ReadAllBytesAsync(files.IniPath);
        CpuKey cpuKey = CpuKey.Parse("00112233445566778899AABBCCDDEEFF");

        DevGl64WorkspacePreparation preparation = await DevGl64WorkspacePreparation.ApplyAsync(
            workspace.Path,
            plan,
            cpuKey,
            CancellationToken.None);
        try
        {
            byte[] mutatedIni = await File.ReadAllBytesAsync(files.IniPath);
            byte[] backupIni = await File.ReadAllBytesAsync(files.BackupPath);
            Assert.False(originalIni.AsSpan().SequenceEqual(mutatedIni));
            Assert.Equal(originalIni, backupIni);
            Assert.True(File.Exists(files.PatchedSdPath));
            Assert.True(File.Exists(files.VFusePath));
            Assert.True(File.Exists(files.XellReasonPath));
            if (!OperatingSystem.IsWindows())
            {
                foreach (string path in new[]
                         {
                             files.BackupPath,
                             files.PatchedSdPath,
                             files.VFusePath,
                             files.XellReasonPath,
                         })
                {
                    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
                }
            }

            byte[] patchedSd = await File.ReadAllBytesAsync(files.PatchedSdPath);
            Assert.Equal((byte)0xD1, patchedSd[0x20]);
            Assert.Equal((byte)0xD2, patchedSd[0x21]);
            Assert.Equal((byte)0xD3, patchedSd[0x22]);
            Assert.Equal((byte)0xD4, patchedSd[0x23]);

            byte[] fuses = await File.ReadAllBytesAsync(files.VFusePath);
            Assert.Equal((byte)0xC0, fuses[0]);
            Assert.Equal(Enumerable.Repeat((byte)0x0F, 8), fuses.AsSpan(0x8, 0x8).ToArray());
            var expectedKey = new byte[CpuKey.ByteLength];
            cpuKey.CopyTo(expectedKey);
            Assert.Equal(expectedKey[..0x8], fuses.AsSpan(0x18, 0x8).ToArray());
            Assert.Equal(expectedKey[..0x8], fuses.AsSpan(0x20, 0x8).ToArray());
            Assert.Equal(expectedKey[0x8..], fuses.AsSpan(0x28, 0x8).ToArray());
            Assert.Equal(expectedKey[0x8..], fuses.AsSpan(0x30, 0x8).ToArray());
            Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, fuses.AsSpan(0x60).ToArray());
            Assert.Equal(new byte[] { 0x00, 0x12 }, await File.ReadAllBytesAsync(files.XellReasonPath));
        }
        finally
        {
            await preparation.DisposeAsync();
            await preparation.DisposeAsync();
        }

        Assert.Equal(originalIni, await File.ReadAllBytesAsync(files.IniPath));
        Assert.False(File.Exists(files.BackupPath));
        Assert.False(File.Exists(files.PatchedSdPath));
        Assert.False(File.Exists(files.VFusePath));
        Assert.False(File.Exists(files.XellReasonPath));
    }

    [Fact]
    public async Task Failed_apply_restores_the_ini_and_removes_files_it_already_generated()
    {
        using var workspace = new TemporaryDirectory();
        XeBuildPreparedPlan plan = CreateDevGl64Plan();
        WorkspaceFiles files = await CreateWorkspaceFilesAsync(workspace.Path, plan);
        byte[] originalIni = await File.ReadAllBytesAsync(files.IniPath);
        Directory.CreateDirectory(files.XellReasonPath);

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => DevGl64WorkspacePreparation.ApplyAsync(
                workspace.Path,
                plan,
                CpuKey.Parse("00112233445566778899AABBCCDDEEFF"),
                CancellationToken.None));

        Assert.Equal(ExitCode.InvalidData, failure.Code);
        Assert.Equal("devgl64-generated-file-exists", failure.Kind);
        Assert.Equal(originalIni, await File.ReadAllBytesAsync(files.IniPath));
        Assert.False(File.Exists(files.BackupPath));
        Assert.False(File.Exists(files.PatchedSdPath));
        Assert.False(File.Exists(files.VFusePath));
        Assert.True(Directory.Exists(files.XellReasonPath));
    }

    [Fact]
    public async Task Linked_sd_source_is_rejected_without_leaving_workspace_mutations()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TemporaryDirectory();
        XeBuildPreparedPlan plan = CreateDevGl64Plan();
        WorkspaceFiles files = await CreateWorkspaceFilesAsync(workspace.Path, plan, createSourceSd: false);
        byte[] originalIni = await File.ReadAllBytesAsync(files.IniPath);
        string externalSd = Path.Combine(workspace.Path, "external-sd.bin");
        await File.WriteAllBytesAsync(externalSd, CreateBootloader());
        File.CreateSymbolicLink(files.SourceSdPath, externalSd);

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => DevGl64WorkspacePreparation.ApplyAsync(
                workspace.Path,
                plan,
                CpuKey.Parse("00112233445566778899AABBCCDDEEFF"),
                CancellationToken.None));

        Assert.Equal("workspace-path-unsafe", failure.Kind);
        Assert.Equal(originalIni, await File.ReadAllBytesAsync(files.IniPath));
        Assert.False(File.Exists(files.BackupPath));
        Assert.False(File.Exists(files.PatchedSdPath));
        Assert.False(File.Exists(files.VFusePath));
        Assert.False(File.Exists(files.XellReasonPath));
    }

    [Fact]
    public async Task Failed_ini_restoration_still_removes_cpu_key_derived_fuses()
    {
        using var workspace = new TemporaryDirectory();
        XeBuildPreparedPlan plan = CreateDevGl64Plan();
        WorkspaceFiles files = await CreateWorkspaceFilesAsync(workspace.Path, plan);
        DevGl64WorkspacePreparation preparation = await DevGl64WorkspacePreparation.ApplyAsync(
            workspace.Path,
            plan,
            CpuKey.Parse("00112233445566778899AABBCCDDEEFF"),
            CancellationToken.None);
        File.Delete(files.IniPath);
        Directory.CreateDirectory(files.IniPath);

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => preparation.DisposeAsync().AsTask());

        Assert.Equal("devgl64-cleanup-failed", failure.Kind);
        Assert.False(File.Exists(files.VFusePath));
        Assert.False(File.Exists(files.PatchedSdPath));
        Assert.False(File.Exists(files.XellReasonPath));
    }

    [Fact]
    public async Task Typed_workspace_keeps_devgl_outputs_until_disposal_and_retains_only_non_secret_diagnostics()
    {
        using var generation = new TemporaryDirectory();
        XeBuildPreparedPlan plan = CreateDevGl64Plan(dashLaunch: true);
        WorkspaceFiles supportFiles = await CreateWorkspaceFilesAsync(generation.Path, plan);
        byte[] rawIni = await File.ReadAllBytesAsync(supportFiles.IniPath);
        var supportSnapshot = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (string supportPath in plan.RequiredSupportFiles)
        {
            string path = Path.Combine(generation.Path, supportPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                byte[] payload = supportPath == plan.RequestedModeIniPath
                    ? rawIni
                    : supportPath == "xeBuild/options.ini"
                        ? Encoding.ASCII.GetBytes("patchsmc = true\n")
                        : supportPath.EndsWith(".ini", StringComparison.Ordinal)
                            ? Encoding.ASCII.GetBytes("[Paths]\nDefault = Hdd:\\default.xex\n")
                            : [0x01];
                await File.WriteAllBytesAsync(path, payload);
            }

            supportSnapshot.Add(supportPath, await File.ReadAllBytesAsync(path));
        }

        await using WineXeBuildWorkspace workspace = await WineXeBuildWorkspace.CreateAsync(
            Path.Combine(generation.Path, "workspaces"), CancellationToken.None);
        await workspace.StageSupportAsync(generation.Path, plan, CancellationToken.None);
        foreach (string modePath in new[] { plan.RequestedModeIniPath, plan.BuildModeIniPath })
        {
            IniParseResult parsed = IniParser.Parse(await File.ReadAllTextAsync(
                Path.Combine(workspace.RootDirectory, modePath.Replace('/', Path.DirectorySeparatorChar))));
            Assert.False(parsed.HasErrors);
            Assert.Equal(plan.DashLaunch!.IniPatchEntries.ToArray(),
                parsed.Document.GetLiterals("flashfs").Select(line => line.Text).ToArray());
        }

        string inputPath = Path.Combine(generation.Path, "input.bin");
        byte[] source = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(inputPath, source);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(inputPath, PrivateFileMode);
        }

        byte[] sourceSha256;
        await using (FileStream inspectedSource = File.OpenRead(inputPath))
        {
            sourceSha256 = await SHA256.HashDataAsync(inspectedSource);
        }
        await workspace.StageInputAsync(
            new XeBuildSourceContext(inputPath, source.Length, sourceSha256),
            XeBuildFourGigabyteStagingPolicy.None,
            CancellationToken.None);
        CpuKey cpuKey = CpuKey.Parse("00112233445566778899AABBCCDDEEFF");
        await workspace.StageCpuKeyAsync(cpuKey, CancellationToken.None);
        string dashboard = Path.Combine(workspace.XeBuildDirectory, "17559");
        DevGl64WorkspacePreparation preparation = await DevGl64WorkspacePreparation.ApplyAsync(
            workspace.RootDirectory, plan, cpuKey, CancellationToken.None);
        try
        {
            Assert.True(File.Exists(Path.Combine(dashboard, "PATCH_sd.bin")));
            Assert.True(File.Exists(Path.Combine(dashboard, "vfuses_khv.bin")));
            Assert.True(File.Exists(Path.Combine(dashboard, "xell_reason.bin")));
            foreach ((string supportPath, byte[] original) in supportSnapshot)
            {
                Assert.Equal(original, await File.ReadAllBytesAsync(
                    Path.Combine(generation.Path, supportPath.Replace('/', Path.DirectorySeparatorChar))));
            }
        }
        finally
        {
            try
            {
                await preparation.DisposeAsync();
            }
            finally
            {
                await workspace.CleanupAsync(retainDiagnostics: true);
            }
        }

        Assert.True(Directory.Exists(workspace.RootDirectory));
        Assert.False(File.Exists(workspace.StagedCpuKeyPath));
        Assert.False(File.Exists(workspace.StagedInputPath));
        Assert.False(File.Exists(Path.Combine(dashboard, "vfuses_khv.bin")));
        Assert.False(File.Exists(Path.Combine(dashboard, "PATCH_sd.bin")));
        Assert.False(File.Exists(Path.Combine(dashboard, "xell_reason.bin")));
        Assert.False(File.Exists(Path.Combine(workspace.RootDirectory,
            string.Concat(plan.BuildModeIniPath, ".bak").Replace('/', Path.DirectorySeparatorChar))));
        Assert.Equal(source, await File.ReadAllBytesAsync(inputPath));
    }

    private static XeBuildPreparedPlan CreateDevGl64Plan(bool dashLaunch = false)
    {
        var support = new XeBuildSupportIndex(
        [
            "xeBuild/xeBuild.exe",
            "xeBuild/options.ini",
            "xeBuild/common/SB_priv.bin",
            "xeBuild/17559/_devgl.ini",
            "xeBuild/17559/_devkit.ini",
            "xeBuild/17559/bin/patches_devxenon.bin",
            "xeBuild/17559/sd.bin",
            "xeBuild/launch.xex",
            "xeBuild/lhelper.xex",
            "xeBuild/launch.ini",
            "xeBuild/launch_default.ini",
        ]);
        return XeBuildPreparationService.Prepare(
            new XeBuildRequest(
                supportRootPath: "support",
                source: new XeBuildSourceContext(
                    "nanddump.bin", 0x4000000, SHA256.HashData(Array.Empty<byte>()), ConsoleId.Xenon64Mb),
                cpuKey: CpuKey.Parse("00112233445566778899AABBCCDDEEFF"),
                outputPath: "updflash.bin",
                target: new XeBuildBuildTarget(null, 17559, "devgl", new XeBuildBuildOptions(dashLaunch: dashLaunch))),
            support);
    }

    private static async Task<WorkspaceFiles> CreateWorkspaceFilesAsync(
        string workspaceRoot,
        XeBuildPreparedPlan plan,
        bool createSourceSd = true)
    {
        string dashboardDirectory = Path.Combine(
            workspaceRoot,
            "xeBuild",
            plan.DashboardVersion.ToString(CultureInfo.InvariantCulture));
        string binDirectory = Path.Combine(dashboardDirectory, "bin");
        WorkspacePathSafety.CreatePrivateDirectory(binDirectory);

        byte[] sourceSd = CreateBootloader();
        uint sourceCrc = BootloaderCrcCalculator.Calculate(sourceSd).Value;
        string iniPath = Path.Combine(workspaceRoot, plan.BuildModeIniPath.Replace('/', Path.DirectorySeparatorChar));
        byte[] iniBytes = Encoding.ASCII.GetBytes(string.Concat(
            "; preserve this raw prefix\r\n",
            "[xenonbl]\r\n",
            "SB\r\n",
            "SC\r\n",
            "sd.bin,",
            sourceCrc.ToString("x", CultureInfo.InvariantCulture),
            "\r\n",
            "CE\r\n"));
        await File.WriteAllBytesAsync(iniPath, iniBytes);
        await File.WriteAllBytesAsync(
            Path.Combine(binDirectory, "patches_devxenon.bin"),
            CreatePatchPayload());

        string sourceSdPath = Path.Combine(dashboardDirectory, "sd.bin");
        if (createSourceSd)
        {
            await File.WriteAllBytesAsync(sourceSdPath, sourceSd);
        }

        return new WorkspaceFiles(
            iniPath,
            string.Concat(iniPath, ".bak"),
            sourceSdPath,
            Path.Combine(dashboardDirectory, "PATCH_sd.bin"),
            Path.Combine(dashboardDirectory, "vfuses_khv.bin"),
            Path.Combine(dashboardDirectory, "xell_reason.bin"));
    }

    private static byte[] CreateBootloader()
    {
        var bootloader = new byte[0x40];
        bootloader[1] = 0x44;
        BinaryPrimitives.WriteUInt32BigEndian(bootloader.AsSpan(0xC, sizeof(uint)), (uint)bootloader.Length);
        return bootloader;
    }

    private static byte[] CreatePatchPayload()
    {
        return
        [
            0xFF, 0xFF, 0xFF, 0xFF,
            0x00, 0x00, 0x00, 0x20,
            0x00, 0x00, 0x00, 0x01,
            0xD1, 0xD2, 0xD3, 0xD4,
            0xFF, 0xFF, 0xFF, 0xFF,
            0xFF, 0xFF, 0xFF, 0xFF,
        ];
    }

    private sealed record WorkspaceFiles(
        string IniPath,
        string BackupPath,
        string SourceSdPath,
        string PatchedSdPath,
        string VFusePath,
        string XellReasonPath);

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"jrunner-devgl64-tests-{Guid.NewGuid():N}");
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
