using System.Collections.Immutable;
using System.Globalization;
using JRunner.Core.Contracts;
using JRunner.Core.Nand.Models;
using JRunner.Core.XeBuild;

namespace JRunner.Core.XeBuild.Preparation;

/// <summary>
/// Validates a typed XeBuild request and produces a secret-free workspace and invocation plan without filesystem or process side effects.
/// </summary>
public static class XeBuildPreparationService
{
    private const string XeBuildExecutablePath = "xeBuild/xeBuild.exe";
    private const string XeBuildOptionsPath = "xeBuild/options.ini";
    private const string XeBuildCommonDirectory = "xeBuild/common/";
    private const string XdkBuildExecutablePath = "xeBuild/XDKbuild/XDKbuild.exe";
    private const string DevGlPrivateKeyPath = "xeBuild/common/SB_priv.bin";
    private const string XdkBuildMarkerFileName = "!XDKbuild Only!.txt";
    private const string MutableDataDirectory = "xeBuild/data";

    /// <summary>
    /// Validates <paramref name="request"/> against source facts, board compatibility, and the immutable support index.
    /// </summary>
    /// <exception cref="OperationFailureException">The requested target, option combination, or required support asset is unavailable.</exception>
    public static XeBuildPreparedPlan Prepare(
        XeBuildRequest request,
        XeBuildSupportIndex support)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(support);

        if (request.Execution.Backend is not XeBuildBackendKind.Wine)
        {
            throw Failure(
                ExitCode.MissingPrerequisite,
                "xebuild-backend-unavailable",
                "The native XeBuild backend is not available. Select the Wine backend.");
        }

        ValidateFourGigabyteStagingPolicy(request.Source, request.Execution.FourGigabyteStagingPolicy);

        ConsoleDefinition? sourceDetectedConsole = ResolveSourceDetectedConsole(request.Source);
        ConsoleDefinition console = request.Target.ConsoleOverride ?? sourceDetectedConsole ?? throw Failure(
            ExitCode.Usage,
            "xebuild-console-required",
            "The source image did not identify one console. Supply a canonical --console value from 'jrunner console list'.");
        XeBuildBuildTarget target = request.Target;

        if (!XeBuildCompatibilityMatrix.SupportsHack(console.Id, target.HackType))
        {
            throw Failure(
                ExitCode.Usage,
                "xebuild-hack-unsupported",
                $"The {target.TypeCanonicalName} target is not supported for {console.CanonicalName}.");
        }

        if (target.HackType is XeBuildHackType.Glitch && !request.Source.SupportsRgh1)
        {
            throw Failure(
                ExitCode.InvalidData,
                "xebuild-rgh1-unsupported",
                "The source bootloader does not support an RGH1 XeBuild target.");
        }

        ValidateOptions(console, target, support);

        bool usesXdkBuildConfiguration = UsesXdkBuildConfiguration(target, support);
        bool requiresFlashIni = usesXdkBuildConfiguration &&
            XeBuildCompatibilityMatrix.RequiresFlashIniForXdkBuild(console.Id);
        bool requiresDevGl64Preparation = XeBuildCompatibilityMatrix.RequiresDevGl64Preparation(
            console.Id,
            target.HackType);
        XeBuildHackType effectiveHackType = requiresDevGl64Preparation
            ? XeBuildHackType.Devkit
            : target.HackType;
        string requestedModeIniPath = GetModeIniPath(target.DashboardVersion, target.HackType);
        string buildModeIniPath = GetModeIniPath(
            target.DashboardVersion,
            effectiveHackType,
            requiresFlashIni);
        ValidateModeIni(support, requestedModeIniPath, target.HackType);
        if (!string.Equals(requestedModeIniPath, buildModeIniPath, StringComparison.Ordinal))
        {
            ValidateModeIni(support, buildModeIniPath, effectiveHackType);
        }

        string configurationName = XeBuildCompatibilityMatrix.GetConfigurationName(
            console,
            target.Options.BigFfs,
            usesXdkBuildConfiguration);
        ImmutableArray<XeBuildPostBuildOperation> postBuildOperations = BuildPostBuildOperations(
            console,
            target,
            usesXdkBuildConfiguration,
            requiresDevGl64Preparation);
        string? rgh3TemplatePath = target.Options.Rgh3
            ? GetRgh3TemplatePath(console.Id)
            : null;
        string? xdkBuildTemplatePath = postBuildOperations.Contains(XeBuildPostBuildOperation.RunXdkBuild)
            ? GetXdkBuildTemplatePath(console)
            : null;
        XeBuildDashLaunchPlan? dashLaunch = target.Options.DashLaunch
            ? new XeBuildDashLaunchPlan(target.DashboardVersion)
            : null;

        ImmutableArray<string> requiredSupportFiles = BuildRequiredSupportFiles(
            target,
            support,
            rgh3TemplatePath,
            xdkBuildTemplatePath,
            dashLaunch);
        ImmutableArray<XeBuildWorkspaceOverlay> workspaceOverlays = BuildWorkspaceOverlays(
            target,
            support,
            requestedModeIniPath);

        return new XeBuildPreparedPlan(
            console,
            sourceDetectedConsole,
            target.ConsoleOverride is not null,
            target.DashboardVersion,
            target.HackType,
            effectiveHackType,
            configurationName,
            requestedModeIniPath,
            buildModeIniPath,
            string.Concat(console.IniName, "bl"),
            requiresFlashIni,
            usesXdkBuildConfiguration,
            requiresDevGl64Preparation,
            DisablesSmcResetPatching(target.HackType),
            request.Execution.FourGigabyteStagingPolicy,
            rgh3TemplatePath,
            xdkBuildTemplatePath,
            dashLaunch,
            workspaceOverlays,
            postBuildOperations,
            requiredSupportFiles,
            ImmutableArray.Create(MutableDataDirectory),
            BuildFixedArguments(target, effectiveHackType, configurationName, requiresFlashIni));
    }

    private static ConsoleDefinition? ResolveSourceDetectedConsole(XeBuildSourceContext source)
    {
        if (source.DetectedConsoleId is not { } consoleId)
        {
            return null;
        }

        if (!ConsoleCatalog.TryGet(consoleId, out ConsoleDefinition? console))
        {
            throw Failure(
                ExitCode.InvalidData,
                "xebuild-source-console-invalid",
                "The source inspection returned an unsupported console.");
        }

        return console;
    }

    private static void ValidateFourGigabyteStagingPolicy(
        XeBuildSourceContext source,
        XeBuildFourGigabyteStagingPolicy stagingPolicy)
    {
        if (source.IsFourGigabyteEmmc && stagingPolicy is XeBuildFourGigabyteStagingPolicy.None)
        {
            throw Failure(
                ExitCode.Usage,
                "xebuild-4gb-staging-policy-required",
                "A 4 GB eMMC source requires exactly one staging policy: system-partition-only or full-4gb-data.");
        }

        if (!source.IsFourGigabyteEmmc && stagingPolicy is not XeBuildFourGigabyteStagingPolicy.None)
        {
            throw Failure(
                ExitCode.Usage,
                "xebuild-4gb-staging-policy-unexpected",
                "A 4 GB staging policy can be selected only for a full 4 GB eMMC source.");
        }
    }

    private static void ValidateOptions(
        ConsoleDefinition console,
        XeBuildBuildTarget target,
        XeBuildSupportIndex support)
    {
        XeBuildBuildOptions options = target.Options;
        if (options.BigFfs &&
            target.HackType is not XeBuildHackType.Glitch and
                not XeBuildHackType.Glitch2 and
                not XeBuildHackType.Glitch2m and
                not XeBuildHackType.Jtag and
                not XeBuildHackType.DevGl)
        {
            throw Failure(
                ExitCode.Usage,
                "xebuild-bigffs-unsupported",
                "BigFFS requires a Glitch, Glitch2, Glitch2M, JTAG, or DevGL target.");
        }

        if (options.BigFfs && !XeBuildCompatibilityMatrix.SupportsBigFfs(console.Id))
        {
            throw Failure(
                ExitCode.Usage,
                "xebuild-bigffs-unsupported",
                $"BigFFS is not supported for {console.CanonicalName}.");
        }

        if (options.Rgh3 &&
            target.HackType is not XeBuildHackType.Glitch2 and not XeBuildHackType.Glitch2m)
        {
            throw Failure(
                ExitCode.Usage,
                "xebuild-rgh3-unsupported",
                "RGH3 requires a Glitch2 or Glitch2M XeBuild target.");
        }

        if (options.Rgh3 && !XeBuildCompatibilityMatrix.SupportsRgh3(console.Id))
        {
            throw Failure(
                ExitCode.Usage,
                "xebuild-rgh3-unsupported",
                $"RGH3 is not supported for {console.CanonicalName}.");
        }

        if (options.DashLaunch && target.HackType is XeBuildHackType.Retail)
        {
            throw Failure(
                ExitCode.Usage,
                "xebuild-dashlaunch-unsupported",
                "DashLaunch cannot be selected for a retail XeBuild target.");
        }

        string? drivePatchName = GetDrivePatchName(options.DrivePatch);
        if (drivePatchName is not null && target.HackType is XeBuildHackType.Retail)
        {
            throw Failure(
                ExitCode.Usage,
                "xebuild-drive-patch-unsupported",
                "XL drive patches are not supported for retail targets.");
        }

        if (drivePatchName is not null)
        {
            RequireSupportFile(
                support,
                GetDashboardBinPath(target.DashboardVersion, drivePatchName),
                ExitCode.Usage,
                "xebuild-drive-patch-unavailable",
                $"The selected dashboard does not provide the {drivePatchName} drive patch.");
            RequireSupportFile(
                support,
                GetXlOverlayXamPath(target.DashboardVersion, drivePatchName),
                ExitCode.Usage,
                "xebuild-drive-patch-unavailable",
                $"The selected dashboard does not provide the {drivePatchName} workspace overlay.");
        }

        foreach (string patchName in options.NamedPatches)
        {
            if (IsDrivePatchName(patchName))
            {
                throw Failure(
                    ExitCode.Usage,
                    "xebuild-drive-patch-conflict",
                    "XL drive patches must be selected only with --drive-patch.");
            }

            if (patchName.Equals("usbdsec", StringComparison.OrdinalIgnoreCase) &&
                target.HackType is XeBuildHackType.Retail)
            {
                throw Failure(
                    ExitCode.Usage,
                    "xebuild-named-patch-unsupported",
                    "The usbdsec patch is not supported for retail targets.");
            }

            if (patchName.Equals("corona_key_fix", StringComparison.OrdinalIgnoreCase) &&
                (target.HackType is XeBuildHackType.Retail ||
                 console.Id is not ConsoleId.CoronaBigBlock and not ConsoleId.Corona16Mb and not ConsoleId.Corona4Gb and
                     not ConsoleId.Winchester16Mb and not ConsoleId.Winchester4Gb and not ConsoleId.WinchesterBigBlock))
            {
                throw Failure(
                    ExitCode.Usage,
                    "xebuild-named-patch-unsupported",
                    $"The corona_key_fix patch is not supported for {console.CanonicalName}.");
            }

            RequireSupportFile(
                support,
                GetDashboardBinPath(target.DashboardVersion, patchName),
                ExitCode.Usage,
                "xebuild-named-patch-unavailable",
                $"The selected dashboard does not provide the '{patchName}' XeBuild patch.");
        }

        if (target.HackType is XeBuildHackType.DevGl or XeBuildHackType.DevGl16)
        {
            RequireSupportFile(
                support,
                DevGlPrivateKeyPath,
                ExitCode.MissingPrerequisite,
                "xebuild-support-file-missing",
                $"The active support payload does not contain '{DevGlPrivateKeyPath}'. Run 'jrunner support install'.");
        }

        if (XeBuildCompatibilityMatrix.RequiresDevGl64Preparation(console.Id, target.HackType))
        {
            string devGlPatchPath = string.Concat(
                "xeBuild/",
                target.DashboardVersion.ToString(CultureInfo.InvariantCulture),
                "/bin/patches_dev",
                console.XeBuildName,
                ".bin");
            RequireSupportFile(
                support,
                devGlPatchPath,
                ExitCode.Usage,
                "xebuild-dashboard-asset-unavailable",
                $"The selected dashboard does not provide the required DevGL patch '{devGlPatchPath}'.");
        }
    }

    private static bool UsesXdkBuildConfiguration(XeBuildBuildTarget target, XeBuildSupportIndex support)
    {
        return (target.HackType is XeBuildHackType.Glitch2m or XeBuildHackType.DevGl) &&
            support.ContainsFile(GetXdkBuildMarkerPath(target.DashboardVersion));
    }

    private static void ValidateModeIni(
        XeBuildSupportIndex support,
        string modeIniPath,
        XeBuildHackType hackType)
    {
        RequireSupportFile(
            support,
            modeIniPath,
            ExitCode.Usage,
            "xebuild-dashboard-mode-unavailable",
            $"The selected dashboard does not provide the {XeBuildHackTypeCatalog.GetCanonicalName(hackType)} XeBuild target.");
    }

    private static ImmutableArray<XeBuildPostBuildOperation> BuildPostBuildOperations(
        ConsoleDefinition console,
        XeBuildBuildTarget target,
        bool usesXdkBuildConfiguration,
        bool requiresDevGl64Preparation)
    {
        var operations = ImmutableArray.CreateBuilder<XeBuildPostBuildOperation>();
        if (XeBuildCompatibilityMatrix.RequiresXeBuildImageRepair(console.Id))
        {
            operations.Add(XeBuildPostBuildOperation.RepairXeBuildImage);
        }

        if (usesXdkBuildConfiguration && target.Options.Rgh3)
        {
            operations.Add(XeBuildPostBuildOperation.RunXdkBuild);
            operations.Add(XeBuildPostBuildOperation.ConvertRgh2ToRgh3WithZeroCpuKey);
        }
        else if (usesXdkBuildConfiguration && target.HackType is XeBuildHackType.Glitch2m)
        {
            operations.Add(XeBuildPostBuildOperation.RunXdkBuild);
        }
        else if (target.Options.Rgh3)
        {
            operations.Add(
                target.HackType is XeBuildHackType.Glitch2m
                    ? XeBuildPostBuildOperation.ConvertRgh2ToRgh3WithZeroCpuKey
                    : XeBuildPostBuildOperation.ConvertRgh2ToRgh3WithPhysicalCpuKey);
        }
        else if (requiresDevGl64Preparation)
        {
            operations.Add(XeBuildPostBuildOperation.ZeroPairDevkitSb);
        }

        return operations.ToImmutable();
    }

    private static ImmutableArray<string> BuildRequiredSupportFiles(
        XeBuildBuildTarget target,
        XeBuildSupportIndex support,
        string? rgh3TemplatePath,
        string? xdkBuildTemplatePath,
        XeBuildDashLaunchPlan? dashLaunch)
    {
        var files = ImmutableArray.CreateBuilder<string>();
        AddRequiredFile(
            files,
            support,
            XeBuildExecutablePath,
            ExitCode.MissingPrerequisite,
            "xebuild-support-file-missing",
            $"The active support payload does not contain '{XeBuildExecutablePath}'. Run 'jrunner support install'.");
        AddRequiredFile(
            files,
            support,
            XeBuildOptionsPath,
            ExitCode.MissingPrerequisite,
            "xebuild-support-file-missing",
            $"The active support payload does not contain '{XeBuildOptionsPath}'. Run 'jrunner support install'.");
        AddDirectoryClosure(
            files,
            support,
            XeBuildCommonDirectory,
            ExitCode.MissingPrerequisite,
            "xebuild-common-unavailable",
            "The active support payload does not contain the xeBuild common dependency directory. Run 'jrunner support install'.");
        AddDirectoryClosure(
            files,
            support,
            GetDashboardDirectory(target.DashboardVersion),
            ExitCode.Usage,
            "xebuild-dashboard-unavailable",
            $"Dashboard {target.DashboardVersion.ToString(CultureInfo.InvariantCulture)} is not available in the active support payload.");

        if (rgh3TemplatePath is not null)
        {
            AddRequiredFile(
                files,
                support,
                rgh3TemplatePath,
                ExitCode.MissingPrerequisite,
                "xebuild-support-file-missing",
                $"The active support payload does not contain '{rgh3TemplatePath}'. Run 'jrunner support install'.");
        }

        if (xdkBuildTemplatePath is not null)
        {
            AddRequiredFile(
                files,
                support,
                XdkBuildExecutablePath,
                ExitCode.MissingPrerequisite,
                "xebuild-support-file-missing",
                $"The active support payload does not contain '{XdkBuildExecutablePath}'. Run 'jrunner support install'.");
            AddRequiredFile(
                files,
                support,
                xdkBuildTemplatePath,
                ExitCode.MissingPrerequisite,
                "xebuild-support-file-missing",
                $"The active support payload does not contain '{xdkBuildTemplatePath}'. Run 'jrunner support install'.");
        }

        if (dashLaunch is not null)
        {
            foreach (string dashLaunchAsset in dashLaunch.RequiredSupportFiles)
            {
                AddRequiredFile(
                    files,
                    support,
                    dashLaunchAsset,
                    ExitCode.MissingPrerequisite,
                    "xebuild-dashlaunch-asset-missing",
                    $"The active support payload does not contain the required DashLaunch asset '{dashLaunchAsset}'. Run 'jrunner support install'.");
            }
        }

        return files.ToImmutable();
    }

    private static ImmutableArray<XeBuildWorkspaceOverlay> BuildWorkspaceOverlays(
        XeBuildBuildTarget target,
        XeBuildSupportIndex support,
        string requestedModeIniPath)
    {
        var overlays = ImmutableArray.CreateBuilder<XeBuildWorkspaceOverlay>();
        overlays.Add(new XeBuildWorkspaceOverlay(XeBuildOptionsPath, "xeBuild/data/options.ini"));

        string? drivePatch = GetDrivePatchName(target.Options.DrivePatch);
        if (drivePatch is null)
        {
            return overlays.ToImmutable();
        }

        string dashboardDirectory = GetDashboardDirectory(target.DashboardVersion).TrimEnd('/');
        overlays.Add(
            new XeBuildWorkspaceOverlay(
                GetXlOverlayXamPath(target.DashboardVersion, drivePatch),
                string.Concat(dashboardDirectory, "/xam.xex")));

        string overlayModeIniPath = string.Concat(
            dashboardDirectory,
            "/",
            drivePatch,
            "/_",
            XeBuildHackTypeCatalog.GetCanonicalName(target.HackType),
            ".ini");
        if (support.ContainsFile(overlayModeIniPath))
        {
            overlays.Add(new XeBuildWorkspaceOverlay(overlayModeIniPath, requestedModeIniPath));
        }

        return overlays.ToImmutable();
    }

    private static ImmutableArray<string> BuildFixedArguments(
        XeBuildBuildTarget target,
        XeBuildHackType effectiveHackType,
        string configurationName,
        bool requiresFlashIni)
    {
        var arguments = ImmutableArray.CreateBuilder<string>();
        arguments.Add("-t");
        arguments.Add(XeBuildHackTypeCatalog.GetCanonicalName(effectiveHackType));
        arguments.Add("-c");
        arguments.Add(configurationName);
        if (requiresFlashIni)
        {
            arguments.Add("-i");
            arguments.Add("flash");
        }

        AddPatchArgument(arguments, GetDrivePatchName(target.Options.DrivePatch));
        foreach (string patchName in target.Options.NamedPatches)
        {
            AddPatchArgument(arguments, patchName);
        }

        arguments.Add("-noenter");
        arguments.Add("-f");
        arguments.Add(target.DashboardVersion.ToString(CultureInfo.InvariantCulture));
        arguments.Add("-d");
        arguments.Add("data");
        return arguments.ToImmutable();
    }

    private static bool DisablesSmcResetPatching(XeBuildHackType hackType)
    {
        return hackType is not XeBuildHackType.Glitch and not XeBuildHackType.Glitch2 and not XeBuildHackType.Glitch2m;
    }

    private static string GetRgh3TemplatePath(ConsoleId consoleId)
    {
        string templateName = consoleId switch
        {
            ConsoleId.Trinity16Mb or ConsoleId.TrinityBigBlock => "TRINITY_RGH3",
            ConsoleId.Falcon16Mb or ConsoleId.Falcon64Mb => "FALCON_RGH3",
            ConsoleId.Jasper16Mb or ConsoleId.JasperXsb or ConsoleId.JasperBigBlock => "JASPER_RGH3",
            ConsoleId.CoronaBigBlock or ConsoleId.Corona16Mb => "CORONA_RGH3",
            ConsoleId.Corona4Gb => "CORONA_4GB_RGH3",
            _ => throw new InvalidOperationException("The RGH3 console selection was not validated."),
        };
        return $"common/xell-images/glitch2/{templateName}.ecc";
    }

    private static string GetXdkBuildTemplatePath(ConsoleDefinition console)
    {
        string boardName = console.XeBuildName;
        if (boardName.Contains("jasper", StringComparison.Ordinal))
        {
            boardName = "jasper";
        }
        else if (boardName.Contains("corona", StringComparison.Ordinal))
        {
            boardName = "corona";
        }

        return $"xeBuild/XDKbuild/{boardName}.bin";
    }

    private static string? GetDrivePatchName(XeBuildDrivePatch drivePatch)
    {
        return drivePatch switch
        {
            XeBuildDrivePatch.None => null,
            XeBuildDrivePatch.Usb => "xl_usb",
            XeBuildDrivePatch.Hdd => "xl_hdd",
            XeBuildDrivePatch.Both => "xl_both",
            _ => throw new InvalidOperationException("The XeBuild drive patch was not validated."),
        };
    }

    private static bool IsDrivePatchName(string patchName)
    {
        return patchName.Equals("xl_usb", StringComparison.OrdinalIgnoreCase) ||
            patchName.Equals("xl_hdd", StringComparison.OrdinalIgnoreCase) ||
            patchName.Equals("xl_both", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddPatchArgument(ImmutableArray<string>.Builder arguments, string? patchName)
    {
        if (patchName is not null)
        {
            arguments.Add("-a");
            arguments.Add(patchName);
        }
    }

    private static void AddDirectoryClosure(
        ImmutableArray<string>.Builder files,
        XeBuildSupportIndex support,
        string directory,
        ExitCode code,
        string kind,
        string message)
    {
        ImmutableArray<string> directoryFiles = support.GetFilesUnder(directory);
        if (directoryFiles.IsDefaultOrEmpty)
        {
            throw Failure(code, kind, message);
        }

        foreach (string file in directoryFiles)
        {
            AddUnique(files, file);
        }
    }

    private static void AddRequiredFile(
        ImmutableArray<string>.Builder files,
        XeBuildSupportIndex support,
        string supportPath,
        ExitCode code,
        string kind,
        string message)
    {
        RequireSupportFile(support, supportPath, code, kind, message);
        AddUnique(files, supportPath);
    }

    private static void RequireSupportFile(
        XeBuildSupportIndex support,
        string supportPath,
        ExitCode code,
        string kind,
        string message)
    {
        if (!support.ContainsFile(supportPath))
        {
            throw Failure(code, kind, message);
        }
    }

    private static void AddUnique(ImmutableArray<string>.Builder files, string value)
    {
        if (!files.Contains(value, StringComparer.Ordinal))
        {
            files.Add(value);
        }
    }

    private static string GetModeIniPath(
        int dashboardVersion,
        XeBuildHackType hackType,
        bool flashQualified = false)
    {
        string flashSuffix = flashQualified ? "_flash" : string.Empty;
        return string.Concat(
            GetDashboardDirectory(dashboardVersion),
            "_",
            XeBuildHackTypeCatalog.GetCanonicalName(hackType),
            flashSuffix,
            ".ini");
    }

    private static string GetDashboardDirectory(int dashboardVersion)
    {
        return string.Concat("xeBuild/", dashboardVersion.ToString(CultureInfo.InvariantCulture), "/");
    }

    private static string GetDashboardBinPath(int dashboardVersion, string patchName)
    {
        return string.Concat(GetDashboardDirectory(dashboardVersion), "bin/", patchName, ".bin");
    }

    private static string GetXlOverlayXamPath(int dashboardVersion, string drivePatch)
    {
        return string.Concat(GetDashboardDirectory(dashboardVersion), drivePatch, "/xam.xex");
    }

    private static string GetXdkBuildMarkerPath(int dashboardVersion)
    {
        return string.Concat(GetDashboardDirectory(dashboardVersion), XdkBuildMarkerFileName);
    }

    private static OperationFailureException Failure(ExitCode code, string kind, string message)
    {
        return new OperationFailureException(code, kind, message);
    }
}
