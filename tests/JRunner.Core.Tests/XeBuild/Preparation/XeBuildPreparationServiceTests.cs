using System.Security.Cryptography;
using System.Text.Json;
using JRunner.Core.Contracts;
using JRunner.Core.Nand.Models;
using JRunner.Core.Nand.Security;
using JRunner.Core.XeBuild;
using JRunner.Core.XeBuild.Preparation;
using Xunit;

namespace JRunner.Core.Tests.XeBuild.Preparation;

public sealed class XeBuildPreparationServiceTests
{
    private const string CpuKeyText = "00112233445566778899AABBCCDDEEFF";

    [Theory]
    [InlineData(ConsoleId.Trinity16Mb, false, false, true, false, false)]
    [InlineData(ConsoleId.Falcon16Mb, true, true, true, false, true)]
    [InlineData(ConsoleId.Zephyr16Mb, true, true, false, false, true)]
    [InlineData(ConsoleId.Jasper16Mb, true, true, true, false, false)]
    [InlineData(ConsoleId.JasperXsb, true, true, true, false, true)]
    [InlineData(ConsoleId.JasperBigBlock, true, true, true, true, false)]
    [InlineData(ConsoleId.Xenon64Mb, false, true, false, false, true)]
    [InlineData(ConsoleId.Xenon16Mb, false, true, false, false, true)]
    [InlineData(ConsoleId.CoronaBigBlock, false, false, true, true, false)]
    [InlineData(ConsoleId.Corona16Mb, false, false, true, false, false)]
    [InlineData(ConsoleId.Corona4Gb, false, false, true, false, false)]
    [InlineData(ConsoleId.TrinityBigBlock, false, false, true, true, false)]
    [InlineData(ConsoleId.Zephyr64Mb, true, true, false, false, true)]
    [InlineData(ConsoleId.Falcon64Mb, true, true, true, false, true)]
    [InlineData(ConsoleId.Winchester16Mb, false, false, false, false, false)]
    [InlineData(ConsoleId.Winchester4Gb, false, false, false, false, false)]
    [InlineData(ConsoleId.WinchesterBigBlock, false, false, false, true, false)]
    public void Compatibility_matrix_preserves_board_level_rules(
        ConsoleId consoleId,
        bool glitchSupported,
        bool jtagSupported,
        bool rgh3Supported,
        bool bigFfsSupported,
        bool requiresImageRepair)
    {
        Assert.Equal(glitchSupported, XeBuildCompatibilityMatrix.SupportsHack(consoleId, XeBuildHackType.Glitch));
        Assert.Equal(jtagSupported, XeBuildCompatibilityMatrix.SupportsHack(consoleId, XeBuildHackType.Jtag));
        Assert.Equal(rgh3Supported, XeBuildCompatibilityMatrix.SupportsRgh3(consoleId));
        Assert.Equal(bigFfsSupported, XeBuildCompatibilityMatrix.SupportsBigFfs(consoleId));
        Assert.Equal(requiresImageRepair, XeBuildCompatibilityMatrix.RequiresXeBuildImageRepair(consoleId));
        Assert.Contains(XeBuildHackType.Retail, XeBuildCompatibilityMatrix.GetSupportedHackTypes(consoleId));
    }

    [Fact]
    public void Target_accepts_only_canonical_console_and_type_vocabulary()
    {
        var target = new XeBuildBuildTarget("fAlCoN 16mB", 17559, "GLITCH2");

        Assert.Equal(ConsoleId.Falcon16Mb, target.ConsoleOverride!.Id);
        Assert.Equal(XeBuildHackType.Glitch2, target.HackType);
        Assert.Equal("glitch2", target.TypeCanonicalName);
        Assert.True(XeBuildHackTypeCatalog.TryGetByCanonicalName("devgl16", out XeBuildHackType devGl16));
        Assert.Equal(XeBuildHackType.DevGl16, devGl16);
        Assert.False(XeBuildHackTypeCatalog.TryGetByCanonicalName("dev-gl", out _));

        Assert.Throws<ArgumentException>(() => new XeBuildBuildTarget("falcon", 17559, "glitch2"));
        Assert.Throws<ArgumentException>(() => new XeBuildBuildTarget("2", 17559, "glitch2"));
        Assert.Throws<ArgumentException>(() => new XeBuildBuildTarget("Falcon 16MB", 17559, "glitch-2"));
    }

    [Fact]
    public void Prepare_uses_source_detected_console_when_no_canonical_override_is_supplied()
    {
        XeBuildPreparedPlan plan = XeBuildPreparationService.Prepare(
            CreateRequest(consoleOverrideCanonicalName: null, detectedConsoleId: ConsoleId.Falcon16Mb),
            CreateSupportIndex());

        Assert.Equal(ConsoleId.Falcon16Mb, plan.Console.Id);
        Assert.Equal(ConsoleId.Falcon16Mb, plan.SourceDetectedConsole!.Id);
        Assert.False(plan.ConsoleWasExplicitlyOverridden);
    }

    [Fact]
    public void Prepare_allows_a_canonical_console_override_of_source_detection()
    {
        XeBuildPreparedPlan plan = XeBuildPreparationService.Prepare(
            CreateRequest(
                consoleOverrideCanonicalName: "Trinity 16MB",
                detectedConsoleId: ConsoleId.Falcon16Mb,
                typeCanonicalName: "glitch2"),
            CreateSupportIndex());

        Assert.Equal(ConsoleId.Trinity16Mb, plan.Console.Id);
        Assert.Equal(ConsoleId.Falcon16Mb, plan.SourceDetectedConsole!.Id);
        Assert.True(plan.ConsoleWasExplicitlyOverridden);
    }

    [Fact]
    public void Prepare_rejects_an_ambiguous_source_without_a_canonical_override()
    {
        OperationFailureException exception = Assert.Throws<OperationFailureException>(() => XeBuildPreparationService.Prepare(
            CreateRequest(consoleOverrideCanonicalName: null, detectedConsoleId: null),
            CreateSupportIndex()));

        Assert.Equal(ExitCode.Usage, exception.Code);
        Assert.Equal("xebuild-console-required", exception.Kind);
    }

    [Fact]
    public void Prepare_rejects_an_rgh1_target_without_source_bootloader_evidence()
    {
        OperationFailureException exception = Assert.Throws<OperationFailureException>(() => XeBuildPreparationService.Prepare(
            CreateRequest(typeCanonicalName: "glitch", supportsRgh1: false),
            CreateSupportIndex()));

        Assert.Equal(ExitCode.InvalidData, exception.Code);
        Assert.Equal("xebuild-rgh1-unsupported", exception.Kind);
    }

    [Fact]
    public void Prepare_requires_an_explicit_staging_policy_for_a_full_4gb_source()
    {
        OperationFailureException exception = Assert.Throws<OperationFailureException>(() => XeBuildPreparationService.Prepare(
            CreateRequest(sourceByteLength: XeBuildSourceContext.FourGigabyteEmmcByteLength),
            CreateSupportIndex()));

        Assert.Equal(ExitCode.Usage, exception.Code);
        Assert.Equal("xebuild-4gb-staging-policy-required", exception.Kind);
    }

    [Fact]
    public void Prepare_rejects_a_4gb_staging_policy_for_non_4gb_source()
    {
        OperationFailureException exception = Assert.Throws<OperationFailureException>(() => XeBuildPreparationService.Prepare(
            CreateRequest(fourGigabyteStagingPolicy: XeBuildFourGigabyteStagingPolicy.FullData),
            CreateSupportIndex()));

        Assert.Equal(ExitCode.Usage, exception.Code);
        Assert.Equal("xebuild-4gb-staging-policy-unexpected", exception.Kind);
    }

    [Theory]
    [InlineData(XeBuildFourGigabyteStagingPolicy.SystemPartitionOnly)]
    [InlineData(XeBuildFourGigabyteStagingPolicy.FullData)]
    public void Prepare_preserves_the_explicit_4gb_staging_policy(XeBuildFourGigabyteStagingPolicy policy)
    {
        XeBuildPreparedPlan plan = XeBuildPreparationService.Prepare(
            CreateRequest(
                consoleOverrideCanonicalName: "Corona 4GB",
                detectedConsoleId: ConsoleId.Corona4Gb,
                sourceByteLength: XeBuildSourceContext.FourGigabyteEmmcByteLength,
                fourGigabyteStagingPolicy: policy),
            CreateSupportIndex());

        Assert.Equal(policy, plan.FourGigabyteStagingPolicy);
    }

    [Fact]
    public void Prepare_rejects_native_until_a_real_native_backend_exists()
    {
        OperationFailureException exception = Assert.Throws<OperationFailureException>(() => XeBuildPreparationService.Prepare(
            CreateRequest(backend: XeBuildBackendKind.Native),
            CreateSupportIndex()));

        Assert.Equal(ExitCode.MissingPrerequisite, exception.Code);
        Assert.Equal("xebuild-backend-unavailable", exception.Kind);
        Assert.True(XeBuildBackendCatalog.TryGetByCanonicalName("native", out XeBuildBackendKind backend));
        Assert.Equal(XeBuildBackendKind.Native, backend);
    }

    [Fact]
    public void Prepare_emits_a_complete_dependency_closure_and_deterministic_argument_order()
    {
        var options = new XeBuildBuildOptions(
            dashLaunch: true,
            drivePatch: XeBuildDrivePatch.Usb,
            namedPatches: ["nofcrt", "usbdsec"]);
        XeBuildPreparedPlan plan = XeBuildPreparationService.Prepare(
            CreateRequest(options: options),
            CreateSupportIndex());

        Assert.Equal(ConsoleId.Falcon16Mb, plan.Console.Id);
        Assert.Contains("xeBuild/xeBuild.exe", plan.RequiredSupportFiles);
        Assert.Contains("xeBuild/options.ini", plan.RequiredSupportFiles);
        Assert.Contains("xeBuild/common/SB_priv.bin", plan.RequiredSupportFiles);
        Assert.Contains("xeBuild/common/SD_17559.bin", plan.RequiredSupportFiles);
        Assert.Contains("xeBuild/17559/readme.txt", plan.RequiredSupportFiles);
        Assert.Contains("xeBuild/17559/bin/nofcrt.bin", plan.RequiredSupportFiles);
        Assert.Contains("xeBuild/17559/xl_usb/xam.xex", plan.RequiredSupportFiles);
        Assert.Contains("xeBuild/launch.xex", plan.RequiredSupportFiles);
        Assert.Contains("xeBuild/lhelper.xex", plan.RequiredSupportFiles);
        Assert.Contains("xeBuild/launch.ini", plan.RequiredSupportFiles);
        Assert.Contains("xeBuild/launch_default.ini", plan.RequiredSupportFiles);
        Assert.Equal(["xeBuild/data"], plan.RequiredWorkspaceDirectories.ToArray());
        Assert.NotNull(plan.DashLaunch);
        Assert.Equal(
            ["..\\launch.xex", "..\\lhelper.xex", "..\\launch.ini"],
            plan.DashLaunch!.IniPatchEntries.ToArray());
        Assert.Equal(
            [
                "-t", "glitch2",
                "-c", "falcon",
                "-a", "xl_usb",
                "-a", "nofcrt",
                "-a", "usbdsec",
                "-noenter",
                "-f", "17559",
                "-d", "data",
                "/workspace/updflash.bin.tmp",
            ],
            plan.CreateArgumentTokens("/workspace/updflash.bin.tmp").ToArray());
    }

    [Fact]
    public void Named_patches_preserve_order_and_reject_non_token_or_duplicate_values()
    {
        var options = new XeBuildBuildOptions(namedPatches: ["noSShdd", "nofcrt"]);

        Assert.Equal(["noSShdd", "nofcrt"], options.NamedPatches.ToArray());
        Assert.Throws<ArgumentException>(() => new XeBuildBuildOptions(namedPatches: ["../escape"]));
        Assert.Throws<ArgumentException>(() => new XeBuildBuildOptions(namedPatches: ["-a"]));
        Assert.Throws<ArgumentException>(() => new XeBuildBuildOptions(namedPatches: ["nofcrt", "nofcrt"]));
    }

    [Fact]
    public void Prepare_rejects_drive_patch_names_in_the_named_patch_surface()
    {
        OperationFailureException exception = Assert.Throws<OperationFailureException>(() => XeBuildPreparationService.Prepare(
            CreateRequest(options: new XeBuildBuildOptions(namedPatches: ["xl_hdd"])),
            CreateSupportIndex()));

        Assert.Equal(ExitCode.Usage, exception.Code);
        Assert.Equal("xebuild-drive-patch-conflict", exception.Kind);
    }

    [Fact]
    public void Prepare_rejects_a_named_patch_that_the_selected_dashboard_does_not_provide()
    {
        OperationFailureException exception = Assert.Throws<OperationFailureException>(() => XeBuildPreparationService.Prepare(
            CreateRequest(options: new XeBuildBuildOptions(namedPatches: ["missing_patch"])),
            CreateSupportIndex()));

        Assert.Equal(ExitCode.Usage, exception.Code);
        Assert.Equal("xebuild-named-patch-unavailable", exception.Kind);
    }

    [Fact]
    public void Prepare_rejects_bigffs_on_the_legacy_jasper_xsb_gap()
    {
        OperationFailureException exception = Assert.Throws<OperationFailureException>(() => XeBuildPreparationService.Prepare(
            CreateRequest(
                consoleOverrideCanonicalName: "Jasper XSB",
                detectedConsoleId: ConsoleId.JasperXsb,
                options: new XeBuildBuildOptions(bigFfs: true)),
            CreateSupportIndex()));

        Assert.Equal(ExitCode.Usage, exception.Code);
        Assert.Equal("xebuild-bigffs-unsupported", exception.Kind);
    }

    [Fact]
    public void Prepare_rejects_rgh3_for_a_xenon_board()
    {
        OperationFailureException exception = Assert.Throws<OperationFailureException>(() => XeBuildPreparationService.Prepare(
            CreateRequest(
                consoleOverrideCanonicalName: "Xenon 16MB",
                detectedConsoleId: ConsoleId.Xenon16Mb,
                options: new XeBuildBuildOptions(rgh3: true)),
            CreateSupportIndex()));

        Assert.Equal(ExitCode.Usage, exception.Code);
        Assert.Equal("xebuild-rgh3-unsupported", exception.Kind);
    }

    [Fact]
    public void Prepare_rejects_dashlaunch_for_retail()
    {
        OperationFailureException exception = Assert.Throws<OperationFailureException>(() => XeBuildPreparationService.Prepare(
            CreateRequest(
                typeCanonicalName: "retail",
                options: new XeBuildBuildOptions(dashLaunch: true)),
            CreateSupportIndex()));

        Assert.Equal(ExitCode.Usage, exception.Code);
        Assert.Equal("xebuild-dashlaunch-unsupported", exception.Kind);
    }

    [Fact]
    public void Prepare_fails_closed_when_a_dashlaunch_root_asset_is_missing()
    {
        OperationFailureException exception = Assert.Throws<OperationFailureException>(() => XeBuildPreparationService.Prepare(
            CreateRequest(options: new XeBuildBuildOptions(dashLaunch: true)),
            CreateSupportIndex("xeBuild/launch_default.ini")));

        Assert.Equal(ExitCode.MissingPrerequisite, exception.Code);
        Assert.Equal("xebuild-dashlaunch-asset-missing", exception.Kind);
    }

    [Fact]
    public void Prepare_requires_a_complete_common_dependency_directory()
    {
        var support = new XeBuildSupportIndex(
        [
            "xeBuild/xeBuild.exe",
            "xeBuild/options.ini",
            "xeBuild/17559/_glitch2.ini",
        ]);

        OperationFailureException exception = Assert.Throws<OperationFailureException>(() => XeBuildPreparationService.Prepare(
            CreateRequest(),
            support));

        Assert.Equal(ExitCode.MissingPrerequisite, exception.Code);
        Assert.Equal("xebuild-common-unavailable", exception.Kind);
    }

    [Fact]
    public void Prepare_requires_the_selected_dashboard_directory()
    {
        var support = new XeBuildSupportIndex(
        [
            "xeBuild/xeBuild.exe",
            "xeBuild/options.ini",
            "xeBuild/common/SB_priv.bin",
        ]);

        OperationFailureException exception = Assert.Throws<OperationFailureException>(() => XeBuildPreparationService.Prepare(
            CreateRequest(),
            support));

        Assert.Equal(ExitCode.Usage, exception.Code);
        Assert.Equal("xebuild-dashboard-mode-unavailable", exception.Kind);
    }

    [Fact]
    public void Prepare_derives_the_xdkbuild_flow_from_the_selected_dashboard()
    {
        XeBuildPreparedPlan plan = XeBuildPreparationService.Prepare(
            CreateRequest(
                consoleOverrideCanonicalName: "Corona 4GB",
                detectedConsoleId: ConsoleId.Corona4Gb,
                dashboardVersion: 17489,
                typeCanonicalName: "glitch2m",
                sourceByteLength: XeBuildSourceContext.FourGigabyteEmmcByteLength,
                fourGigabyteStagingPolicy: XeBuildFourGigabyteStagingPolicy.FullData,
                options: new XeBuildBuildOptions(rgh3: true)),
            CreateSupportIndex());

        Assert.True(plan.UsesXdkBuildConfiguration);
        Assert.True(plan.RequiresFlashIni);
        Assert.Equal("xeBuild/17489/_glitch2m.ini", plan.RequestedModeIniPath);
        Assert.Equal("xeBuild/17489/_glitch2m_flash.ini", plan.BuildModeIniPath);
        Assert.Equal("xeBuild/XDKbuild/corona.bin", plan.XdkBuildTemplatePath);
        Assert.Contains("xeBuild/XDKbuild/XDKbuild.exe", plan.RequiredSupportFiles);
        Assert.Equal(
            [
                XeBuildPostBuildOperation.RunXdkBuild,
                XeBuildPostBuildOperation.ConvertRgh2ToRgh3WithZeroCpuKey,
            ],
            plan.PostBuildOperations.ToArray());
        Assert.Equal(
            ["-t", "glitch2m", "-c", "corona4g", "-i", "flash"],
            plan.FixedArguments.Take(6).ToArray());
    }

    [Fact]
    public void Prepare_preserves_the_devgl64_and_image_repair_behaviors()
    {
        XeBuildPreparedPlan plan = XeBuildPreparationService.Prepare(
            CreateRequest(
                consoleOverrideCanonicalName: "Xenon 64MB",
                detectedConsoleId: ConsoleId.Xenon64Mb,
                typeCanonicalName: "devgl"),
            CreateSupportIndex());

        Assert.Equal(XeBuildHackType.DevGl, plan.RequestedHackType);
        Assert.Equal(XeBuildHackType.Devkit, plan.EffectiveHackType);
        Assert.True(plan.RequiresDevGl64Preparation);
        Assert.Contains("xeBuild/17559/bin/patches_devxenon.bin", plan.RequiredSupportFiles);
        Assert.Equal(
            [XeBuildPostBuildOperation.RepairXeBuildImage, XeBuildPostBuildOperation.ZeroPairDevkitSb],
            plan.PostBuildOperations.ToArray());
    }

    [Fact]
    public void Request_keeps_cpu_key_out_of_json_dtos_and_diagnostic_text()
    {
        XeBuildRequest request = CreateRequest();

        string json = JsonSerializer.Serialize(request);
        string diagnostic = request.ToString() ?? string.Empty;

        Assert.DoesNotContain(CpuKeyText, json, StringComparison.Ordinal);
        Assert.DoesNotContain(CpuKeyText, diagnostic, StringComparison.Ordinal);
        Assert.Contains("output.bin", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Backend_contract_uses_the_typed_request_and_result_boundary()
    {
        var buildMethod = typeof(IXeBuildBackend).GetMethod(nameof(IXeBuildBackend.BuildAsync));
        var kindProperty = typeof(IXeBuildBackend).GetProperty(nameof(IXeBuildBackend.Kind));

        Assert.NotNull(buildMethod);
        Assert.Equal(typeof(Task<XeBuildResult>), buildMethod!.ReturnType);
        Assert.Equal(
            [typeof(XeBuildRequest), typeof(CancellationToken)],
            buildMethod.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
        Assert.NotNull(kindProperty);
        Assert.Equal(typeof(XeBuildBackendKind), kindProperty!.PropertyType);
    }

    [Fact]
    public void Support_index_rejects_paths_outside_the_immutable_support_layout_and_sorts_directory_closures()
    {
        Assert.Throws<ArgumentException>(() => new XeBuildSupportIndex(["xeBuild/../escape.exe"]));
        Assert.Throws<ArgumentException>(() => new XeBuildSupportIndex(["/xeBuild/xeBuild.exe"]));

        var index = new XeBuildSupportIndex(["xeBuild/common/z.bin", "xeBuild/common/a.bin"]);
        Assert.Equal(["xeBuild/common/a.bin", "xeBuild/common/z.bin"], index.GetFilesUnder("xeBuild/common/").ToArray());
        Assert.Throws<ArgumentException>(() => index.GetFilesUnder("xeBuild/common"));
    }

    private static XeBuildRequest CreateRequest(
        string? consoleOverrideCanonicalName = "Falcon 16MB",
        ConsoleId? detectedConsoleId = ConsoleId.Falcon16Mb,
        long sourceByteLength = 0x01000000,
        bool supportsRgh1 = false,
        int dashboardVersion = 17559,
        string typeCanonicalName = "glitch2",
        XeBuildBuildOptions? options = null,
        XeBuildFourGigabyteStagingPolicy fourGigabyteStagingPolicy = XeBuildFourGigabyteStagingPolicy.None,
        XeBuildBackendKind backend = XeBuildBackendKind.Wine)
    {
        return new XeBuildRequest(
            supportRootPath: "support",
            source: new XeBuildSourceContext(
                inputPath: "input.bin",
                byteLength: sourceByteLength,
                contentSha256: SHA256.HashData(Array.Empty<byte>()),
                detectedConsoleId: detectedConsoleId,
                supportsRgh1: supportsRgh1),
            cpuKey: CpuKey.Parse(CpuKeyText),
            outputPath: "output.bin",
            target: new XeBuildBuildTarget(
                consoleOverrideCanonicalName,
                dashboardVersion,
                typeCanonicalName,
                options),
            execution: new XeBuildExecutionOptions(
                fourGigabyteStagingPolicy: fourGigabyteStagingPolicy,
                backend: backend));
    }

    private static XeBuildSupportIndex CreateSupportIndex(params string[] omittedPaths)
    {
        string[] files =
        [
            "common/xell-images/glitch2/CORONA_4GB_RGH3.ecc",
            "common/xell-images/glitch2/FALCON_RGH3.ecc",
            "xeBuild/xeBuild.exe",
            "xeBuild/options.ini",
            "xeBuild/common/SB_priv.bin",
            "xeBuild/common/SD_17489.bin",
            "xeBuild/common/SD_17559.bin",
            "xeBuild/17559/_devgl.ini",
            "xeBuild/17559/_devkit.ini",
            "xeBuild/17559/_glitch.ini",
            "xeBuild/17559/_glitch2.ini",
            "xeBuild/17559/_glitch2m.ini",
            "xeBuild/17559/_jtag.ini",
            "xeBuild/17559/_retail.ini",
            "xeBuild/17559/bin/corona_key_fix.bin",
            "xeBuild/17559/bin/nofcrt.bin",
            "xeBuild/17559/bin/patches_devxenon.bin",
            "xeBuild/17559/bin/usbdsec.bin",
            "xeBuild/17559/bin/xl_both.bin",
            "xeBuild/17559/bin/xl_hdd.bin",
            "xeBuild/17559/bin/xl_usb.bin",
            "xeBuild/17559/readme.txt",
            "xeBuild/17559/xl_both/xam.xex",
            "xeBuild/17559/xl_hdd/xam.xex",
            "xeBuild/17559/xl_usb/_glitch2.ini",
            "xeBuild/17559/xl_usb/xam.xex",
            "xeBuild/17489/!XDKbuild Only!.txt",
            "xeBuild/17489/_glitch2m.ini",
            "xeBuild/17489/_glitch2m_flash.ini",
            "xeBuild/17489/bin/patches_g2mcorona.bin",
            "xeBuild/17489/readme.txt",
            "xeBuild/XDKbuild/XDKbuild.exe",
            "xeBuild/XDKbuild/corona.bin",
            "xeBuild/launch.xex",
            "xeBuild/lhelper.xex",
            "xeBuild/launch.ini",
            "xeBuild/launch_default.ini",
        ];

        return new XeBuildSupportIndex(files.Except(omittedPaths, StringComparer.Ordinal));
    }
}
