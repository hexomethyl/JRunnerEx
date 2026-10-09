using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Parsing;
using System.Security.Cryptography;
using JRunner.Cli.Infrastructure;
using JRunner.Core;
using JRunner.Core.Contracts;
using JRunner.Core.Devices.PicoFlasher;
using JRunner.Core.Nand.Comparison;
using JRunner.Core.Nand.Hacks;
using JRunner.Core.Nand.Conversion;
using JRunner.Core.Nand.Inspection;
using JRunner.Core.Nand.Inspection.Models;
using JRunner.Core.Nand.Models;
using JRunner.Core.Nand.Security;
using JRunner.Core.Patching;
using JRunner.Core.Patching.Inspection;
using JRunner.Core.Support;
using JRunner.Core.XeBuild;
using JRunner.Core.XeBuild.Preparation;
namespace JRunner.Cli;

/// <summary>
/// Parses and executes the native NAND, patch, console, device, Pico, support, and XeBuild command surface.
/// </summary>
internal static partial class CliCommandRouter
{
    private const int FileBufferSize = 0x10000;
    private static readonly Lazy<XeBuildSupportIndex> PinnedXeBuildSupportIndex = new(
        static () => XeBuildSupportIndex.FromManifest(EmbeddedSupportManifest.Current),
        LazyThreadSafetyMode.ExecutionAndPublication);


    internal static Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken,
        PicoFlasherConnectionFactory? picoConnectionFactory = null,
        TextReader? standardInput = null,
        IXeBuildBackend? wineBackend = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        standardInput ??= System.Console.In;
        var commands = new CommandDefinitions();
        bool jsonRequested = CliApplication.IsJsonRequested(arguments);
        ParseResult parseResult = commands.Root.Parse(RemoveRouterFlags(arguments));
        if (IsHelpRequested(arguments))
        {
            if (HasInvalidHelpArguments(parseResult))
            {
                return CliRuntime.RenderFailureAsync(
                    jsonRequested,
                    standardOutput,
                    standardError,
                    CreateParseFailure(commands, parseResult, informationRequested: true));
            }

            return RenderInformationAsync(
                jsonRequested,
                GetHelpText(commands, parseResult.CommandResult.Command),
                standardOutput,
                standardError,
                cancellationToken,
                () => ValidateHelpOptions(commands, parseResult));
        }

        if (IsVersionRequested(arguments))
        {
            if (HasInvalidHelpArguments(parseResult))
            {
                return CliRuntime.RenderFailureAsync(
                    jsonRequested,
                    standardOutput,
                    standardError,
                    CreateParseFailure(commands, parseResult, informationRequested: true));
            }

            return RenderInformationAsync(
                jsonRequested,
                JRunnerVersion.Display,
                standardOutput,
                standardError,
                cancellationToken,
                () => ValidateHelpOptions(commands, parseResult));
        }

        if (parseResult.Errors.Count != 0)
        {
            return CliRuntime.RenderFailureAsync(
                jsonRequested,
                standardOutput,
                standardError,
                CreateParseFailure(commands, parseResult));
        }

        Command selectedCommand = parseResult.CommandResult.Command;
        if (ReferenceEquals(selectedCommand, commands.NandInspect))
        {
            return RunNandInspectAsync(
                commands,
                parseResult,
                standardInput,
                jsonRequested,
                standardOutput,
                standardError,
                cancellationToken);
        }

        if (ReferenceEquals(selectedCommand, commands.NandCompare))
        {
            return RunNandCompareAsync(
                commands,
                parseResult,
                jsonRequested,
                standardOutput,
                standardError,
                cancellationToken);
        }

        if (ReferenceEquals(selectedCommand, commands.NandRgh3Convert))
        {
            return RunNandRgh3ConvertAsync(
                commands,
                parseResult,
                standardInput,
                jsonRequested,
                standardOutput,
                standardError,
                cancellationToken);
        }

        if (ReferenceEquals(selectedCommand, commands.PatchInspect))
        {
            return RunPatchInspectAsync(
                commands,
                parseResult,
                jsonRequested,
                standardOutput,
                standardError,
                cancellationToken);
        }

        if (ReferenceEquals(selectedCommand, commands.ConsoleList))
        {
            return RunConsoleListAsync(
                jsonRequested,
                standardOutput,
                standardError,
                cancellationToken);
        }

        if (ReferenceEquals(selectedCommand, commands.XeBuildBuild))
        {
            return RunXeBuildBuildAsync(
                commands,
                parseResult,
                standardInput,
                wineBackend,
                jsonRequested,
                standardOutput,
                standardError,
                cancellationToken);
        }
        if (ReferenceEquals(selectedCommand, commands.DeviceList))
        {
            return RunDeviceListAsync(
                picoConnectionFactory,
                jsonRequested,
                standardOutput,
                standardError,
                cancellationToken);
        }

        if (ReferenceEquals(selectedCommand, commands.PicoSmcStop) ||
            ReferenceEquals(selectedCommand, commands.PicoSmcStart) ||
            ReferenceEquals(selectedCommand, commands.PicoRebootBootloader))
        {
            return RunPicoControlAsync(
                commands,
                selectedCommand,
                parseResult,
                picoConnectionFactory,
                jsonRequested,
                standardOutput,
                standardError,
                cancellationToken);
        }


        if (ReferenceEquals(selectedCommand, commands.PicoProbe))
        {
            return RunPicoProbeAsync(
                commands,
                parseResult,
                picoConnectionFactory,
                jsonRequested,
                standardOutput,
                standardError,
                cancellationToken);
        }

        if (ReferenceEquals(selectedCommand, commands.PicoEmmcProbe))
        {
            return RunPicoEmmcProbeAsync(
                commands,
                parseResult,
                picoConnectionFactory,
                jsonRequested,
                standardOutput,
                standardError,
                cancellationToken);
        }

        if (ReferenceEquals(selectedCommand, commands.PicoEmmcRead))
        {
            return RunPicoEmmcReadAsync(
                commands,
                parseResult,
                picoConnectionFactory,
                jsonRequested,
                standardOutput,
                standardError,
                cancellationToken);
        }

        if (ReferenceEquals(selectedCommand, commands.PicoNandRead))
        {
            return RunPicoNandReadAsync(
                commands,
                parseResult,
                picoConnectionFactory,
                jsonRequested,
                standardOutput,
                standardError,
                cancellationToken);
        }

        if (ReferenceEquals(selectedCommand, commands.PicoNandWrite))
        {
            return RunPicoNandWriteAsync(
                commands,
                parseResult,
                picoConnectionFactory,
                jsonRequested,
                standardOutput,
                standardError,
                cancellationToken);
        }

        if (ReferenceEquals(selectedCommand, commands.PicoNandErase))
        {
            return RunPicoNandEraseAsync(
                commands,
                parseResult,
                picoConnectionFactory,
                jsonRequested,
                standardOutput,
                standardError,
                cancellationToken);
        }

        if (ReferenceEquals(selectedCommand, commands.SupportStatus))
        {
            return RunSupportStatusAsync(
                commands,
                parseResult,
                jsonRequested,
                standardOutput,
                standardError,
                cancellationToken);
        }

        if (ReferenceEquals(selectedCommand, commands.SupportInstall))
        {
            return RunSupportInstallAsync(
                commands,
                parseResult,
                jsonRequested,
                standardOutput,
                standardError,
                cancellationToken);
        }

        return CliRuntime.RenderFailureAsync(
            jsonRequested,
            standardOutput,
            standardError,
            UnsupportedCommand(commands, selectedCommand));
    }

    private static Task<int> RunNandInspectAsync(
        CommandDefinitions commands,
        ParseResult parseResult,
        TextReader standardInput,
        bool jsonRequested,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken)
    {
        return CliRuntime.ExecuteAsync(
            new CliExecution<NandInspectionResult>(
                jsonRequested,
                async (progress, token) =>
                {
                    string inputPath = RequireOption(
                        parseResult,
                        commands.NandInspectInput,
                        "input-required",
                        "A NAND input file is required.");
                    CpuKey? cpuKey = await ResolveCpuKeyAsync(
                            parseResult,
                            commands.NandInspectCpuKey,
                            CpuKeySourcePolicy.Optional,
                            standardInput,
                            token)
                        .ConfigureAwait(false);

                    await using FileStream input = OpenRead(inputPath, "--input");
                    NandInspectionResult result = await NandImageService.InspectAsync(
                            input,
                            cpuKey,
                            progress,
                            token)
                        .ConfigureAwait(false);
                    return OperationResult.Success(result);
                },
                static (result, output, _) => output.WriteLineAsync(
                    $"NAND inspection completed: {result.CanonicalImage.RawByteLength} bytes, {result.CanonicalImage.DetectedFormat}.")),
            standardOutput,
            standardError,
            cancellationToken);
    }

    private static Task<int> RunNandCompareAsync(
        CommandDefinitions commands,
        ParseResult parseResult,
        bool jsonRequested,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken)
    {
        return CliRuntime.ExecuteAsync(
            new CliExecution<NandCanonicalComparisonResult>(
                jsonRequested,
                async (progress, token) =>
                {
                    string leftPath = RequireArgument(
                        parseResult,
                        commands.NandCompareLeft,
                        "left-input-required",
                        "A left NAND input file is required.");
                    string rightPath = RequireArgument(
                        parseResult,
                        commands.NandCompareRight,
                        "right-input-required",
                        "A right NAND input file is required.");

                    await using FileStream left = OpenRead(leftPath, "<left>");
                    await using FileStream right = OpenRead(rightPath, "<right>");
                    NandCanonicalComparisonResult result = await NandCanonicalComparisonService.CompareAsync(
                            new NandCanonicalComparisonRequest(
                                new NandCanonicalInput(left),
                                new NandCanonicalInput(right)),
                            progress,
                            token)
                        .ConfigureAwait(false);
                    return result.Equal ? OperationResult.Success(result) : OperationResult.Negative(result);
                },
                static (result, output, _) => output.WriteLineAsync(
                    result.Equal
                        ? "NAND images are canonically equal."
                        : $"NAND images differ: {result.DifferingByteCount} logical bytes; first difference at {result.FirstDifferingLogicalOffset}.")),
            standardOutput,
            standardError,
            cancellationToken);
    }

    private static Task<int> RunNandRgh3ConvertAsync(
        CommandDefinitions commands,
        ParseResult parseResult,
        TextReader standardInput,
        bool jsonRequested,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken)
    {
        return CliRuntime.ExecuteAsync(
            new CliExecution<Rgh2ToRgh3ConversionResult>(
                jsonRequested,
                async (progress, token) =>
                {
                    string eccPath = RequireOption(
                        parseResult,
                        commands.NandRgh3ConvertEcc,
                        "ecc-required",
                        "An RGH3 ECC template file is required.");
                    string flashPath = RequireOption(
                        parseResult,
                        commands.NandRgh3ConvertFlash,
                        "flash-required",
                        "An RGH2 flash input file is required.");
                    string outputPath = RequireOption(
                        parseResult,
                        commands.NandRgh3ConvertOutput,
                        "output-required",
                        "An output file is required.");
                    CpuKey cpuKey = (await ResolveCpuKeyAsync(
                            parseResult,
                            commands.NandRgh3ConvertCpuKey,
                            CpuKeySourcePolicy.Required,
                            standardInput,
                            token)
                        .ConfigureAwait(false))!.Value;
                    bool patchSmc = !parseResult.GetValue(commands.NandRgh3ConvertNoSmcPatch);
                    bool force = parseResult.GetValue(commands.NandRgh3ConvertForce);

                    EnsureOutputDoesNotMatchInput(
                        outputPath,
                        eccPath,
                        flashPath,
                        parseResult.GetValue(commands.NandRgh3ConvertCpuKey.File));
                    await using var output = AtomicOutputFile.Create(outputPath, force);
                    await using FileStream ecc = OpenRead(eccPath, "--ecc");
                    await using FileStream flash = OpenRead(flashPath, "--flash");
                    Rgh2ToRgh3ConversionResult result = await Rgh2ToRgh3ConversionService.ConvertAsync(
                            new Rgh2ToRgh3ConversionRequest(ecc, flash, output.Stream, cpuKey, patchSmc),
                            progress,
                            token)
                        .ConfigureAwait(false);
                    if (output.Stream.Length != result.OutputByteLength)
                    {
                        throw new OperationFailureException(
                            ExitCode.InvalidData,
                            "conversion-output-length-mismatch",
                            "The converted output length did not match the conversion result.");
                    }

                    await output.CompleteAsync(token).ConfigureAwait(false);
                    return OperationResult.Success(result);
                },
                static (result, output, _) => output.WriteLineAsync(
                    $"RGH3 conversion completed: {result.OutputByteLength} bytes written."),
                successIsCommitted: true),
            standardOutput,
            standardError,
            cancellationToken);
    }

    private static Task<int> RunPatchInspectAsync(
        CommandDefinitions commands,
        ParseResult parseResult,
        bool jsonRequested,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken)
    {
        return CliRuntime.ExecuteAsync(
            new CliExecution<PatchInspectionResult>(
                jsonRequested,
                async (_, token) =>
                {
                    string inputPath = RequireOption(
                        parseResult,
                        commands.PatchInspectInput,
                        "input-required",
                        "A patch input file is required.");
                    byte[] patchBytes = await ReadPatchBytesAsync(inputPath, token).ConfigureAwait(false);
                    try
                    {
                        PatchInspectionResult result = PatchInspectionService.Inspect(patchBytes, cancellationToken: token);
                        return OperationResult.Success(result);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(patchBytes);
                    }
                },
                static (result, output, _) => output.WriteLineAsync(
                    $"Patch inspection completed: {result.Records.Length} records, {result.CompletionStatus}.")),
            standardOutput,
            standardError,
            cancellationToken);
    }

    private static Task<int> RunConsoleListAsync(
        bool jsonRequested,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken)
    {
        return CliRuntime.ExecuteAsync(
            new CliExecution<ConsoleListEntry[]>(
                jsonRequested,
                (_, _) => Task.FromResult(OperationResult.Success(
                    ConsoleCatalog.All
                        .Select(console => new ConsoleListEntry(
                            console.LegacyId,
                            console.CanonicalName,
                            console.XeBuildName,
                            console.IniName,
                            console.NandSizeMegabytes,
                            console.LogicalNandByteLength,
                            console.LegacyLayoutId,
                            XeBuildCompatibilityMatrix.GetSupportedHackTypes(console.Id)
                                .Select(XeBuildHackTypeCatalog.GetCanonicalName)
                                .ToArray()))
                        .ToArray())),
                static async (entries, output, _) =>
                {
                    foreach (ConsoleListEntry entry in entries)
                    {
                        string supportedTargets = string.Join(", ", entry.SupportedHackTargets);
                        await output.WriteLineAsync(
                                $"{entry.LegacyId}: {entry.CanonicalName} (xeBuild: {entry.XeBuildName}; targets: {supportedTargets})")
                            .ConfigureAwait(false);
                    }
                }),
            standardOutput,
            standardError,
            cancellationToken);
    }

    private static Task<int> RunXeBuildBuildAsync(
        CommandDefinitions commands,
        ParseResult parseResult,
        TextReader standardInput,
        IXeBuildBackend? wineBackend,
        bool jsonRequested,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken)
    {
        return CliRuntime.ExecuteAsync(
            new CliExecution<XeBuildResult>(
                jsonRequested,
                async (progress, token) =>
                {
                    string inputPath = RequireOption(
                        parseResult,
                        commands.XeBuildBuildInput,
                        "xebuild-input-required",
                        "A NAND input file is required for XeBuild.");
                    string outputPath = RequireOption(
                        parseResult,
                        commands.XeBuildBuildOutput,
                        "xebuild-output-required",
                        "An output path is required for XeBuild.");
                    (XeBuildBuildTarget? selectedTarget, XeBuildExecutionOptions execution) = ReadXeBuildSelections(commands, parseResult);
                    XeBuildBuildTarget target = selectedTarget
                        ?? throw UsageFailure("xebuild-target-required", "A dashboard and canonical XeBuild type are required.");
                    CpuKey cpuKey = (await ResolveCpuKeyAsync(
                            parseResult,
                            commands.XeBuildBuildCpuKey,
                            CpuKeySourcePolicy.Required,
                            standardInput,
                            token)
                        .ConfigureAwait(false))!.Value;
                    if (execution.Backend is not XeBuildBackendKind.Wine)
                    {
                        throw XeBuildBackendUnavailable();
                    }

                    XeBuildOutputEvidenceCatalog.EnsureAvailable(target);

                    EnsureOutputDoesNotMatchInput(outputPath, inputPath, parseResult.GetValue(commands.XeBuildBuildCpuKey.File));
                    AtomicOutputDestination destination = AtomicOutputPath.Preflight(outputPath, execution.OverwriteExistingOutput);
                    XeBuildSourceContext source;
                    await using (XeBuildSourceFile sourceFile = XeBuildSourceFile.Open(inputPath))
                    {
                        FileStream input = sourceFile.Stream;
                        inputPath = sourceFile.FullPath;
                        long sourceByteLength = sourceFile.ByteLength;
                        ValidateFourGigabyteStagingPolicy(sourceByteLength, execution.FourGigabyteStagingPolicy);
                        NandInspectionResult inspection = await NandImageService.InspectAsync(input, cpuKey, progress, token)
                            .ConfigureAwait(false);

                        // Hash the entire inspected handle, including any unstaged eMMC data partition.
                        // Reopening the pathname here could bind the inspection to a replacement image.
                        input.Position = 0;
                        byte[] contentSha256 = await SHA256.HashDataAsync(input, token).ConfigureAwait(false);
                        sourceFile.EnsureUnchanged();
                        if (input.Length != sourceByteLength || input.Position != sourceByteLength)
                        {
                            throw new OperationFailureException(
                                ExitCode.InvalidData,
                                "xebuild-input-length-mismatch",
                                "The XeBuild source length changed during source inspection.");
                        }

                        source = new XeBuildSourceContext(
                            inputPath,
                            sourceByteLength,
                            contentSha256,
                            inspection.SemanticEvidence.Console.IsConfirmed
                                ? inspection.SemanticEvidence.Console.Console!.Id
                                : null,
                            inspection.HackEvidence.TablePreferredHack is NandHackType.Glitch);
                    }
                    SupportRoot supportRoot = ResolveSupportRoot(parseResult, commands.SupportRoot);
                    var request = new XeBuildRequest(
                        supportRoot.DirectoryPath,
                        source,
                        cpuKey,
                        destination.DestinationPath,
                        target,
                        execution);
                    XeBuildPreparationService.Prepare(request, PinnedXeBuildSupportIndex.Value);
                    IXeBuildBackend backend = wineBackend ?? new WineXeBuildBackend(progress);
                    if (backend.Kind != execution.Backend)
                    {
                        throw XeBuildBackendUnavailable();
                    }

                    XeBuildResult result = await backend.BuildAsync(request, token).ConfigureAwait(false);
                    return OperationResult.Success(result);
                },
                static (result, output, _) => output.WriteLineAsync(
                    $"XeBuild image published: {result.OutputPath} ({result.OutputByteLength} bytes)."),
                successIsCommitted: true),
            standardOutput,
            standardError,
            cancellationToken);
    }

    private static Task<int> RunSupportStatusAsync(
        CommandDefinitions commands,
        ParseResult parseResult,
        bool jsonRequested,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken)
    {
        return CliRuntime.ExecuteAsync(
            new CliExecution<SupportStatusResult>(
                jsonRequested,
                async (progress, token) =>
                {
                    SupportRoot supportRoot = ResolveSupportRoot(parseResult, commands.SupportRoot);
                    var validator = new SupportPayloadValidator();
                    SupportStatusResult result = await validator.ValidateAsync(supportRoot, progress, token).ConfigureAwait(false);
                    if (result.Status is not SupportStatusKind.Valid)
                    {
                        throw CreateSupportStatusFailure(result);
                    }

                    return OperationResult.Success(result);
                },
                static (result, output, _) => output.WriteLineAsync(
                    $"Support payload status: valid ({result.ExpectedFileCount} files, {result.ExpectedByteLength} bytes).")),
            standardOutput,
            standardError,
            cancellationToken);
    }

    private static Task<int> RunSupportInstallAsync(
        CommandDefinitions commands,
        ParseResult parseResult,
        bool jsonRequested,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken)
    {
        return CliRuntime.ExecuteAsync(
            new CliExecution<SupportInstallationResult>(
                jsonRequested,
                async (progress, token) =>
                {
                    SupportRoot supportRoot = ResolveSupportRoot(parseResult, commands.SupportRoot);
                    string? archivePath = parseResult.GetValue(commands.SupportInstallArchive);
                    SupportArchiveSource archiveSource = archivePath is null
                        ? new SupportArchiveSource.PinnedDownload()
                        : new SupportArchiveSource.LocalFile(RequireOption(
                            parseResult,
                            commands.SupportInstallArchive,
                            "support-archive-invalid",
                            "A local support archive path is required."));
                    using var httpClient = new HttpClient();
                    var installer = new SupportPayloadInstaller(httpClient);
                    SupportInstallationResult result = await installer.InstallAsync(
                            new SupportInstallationRequest(supportRoot, archiveSource),
                            progress,
                            token)
                        .ConfigureAwait(false);
                    return OperationResult.Success(result);
                },
                static (result, output, _) => output.WriteLineAsync(
                    $"Support payload installed: {result.FileCount} files, {result.ExtractedByteLength} bytes."),
                successIsCommitted: true),
            standardOutput,
            standardError,
            cancellationToken);
    }

    private static Task<int> RunDeviceListAsync(
        PicoFlasherConnectionFactory? picoConnectionFactory,
        bool jsonRequested,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken)
    {
        return CliRuntime.ExecuteAsync(
            new CliExecution<IReadOnlyList<PicoFlasherDeviceEndpoint>>(
                jsonRequested,
                async (_, token) => OperationResult.Success(
                    await (picoConnectionFactory ?? new PicoFlasherConnectionFactory())
                        .EnumerateAsync(token).ConfigureAwait(false)),
                static async (endpoints, output, _) =>
                {
                    if (endpoints.Count == 0)
                    {
                        await output.WriteLineAsync("No PicoFlasher command interfaces found.").ConfigureAwait(false);
                    }

                    foreach (PicoFlasherDeviceEndpoint endpoint in endpoints)
                    {
                        await output.WriteLineAsync(
                                $"Device path: {endpoint.DevicePath}; serial: {endpoint.SerialNumber ?? "(none)"}; physical path: {endpoint.PhysicalDevicePath}; interface: {endpoint.InterfaceNumber}.")
                            .ConfigureAwait(false);
                    }
                }),
            standardOutput,
            standardError,
            cancellationToken);
    }

    private static Task<int> RunPicoControlAsync(
        CommandDefinitions commands,
        Command selectedCommand,
        ParseResult parseResult,
        PicoFlasherConnectionFactory? picoConnectionFactory,
        bool jsonRequested,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken)
    {
        PicoCommandOptions options = ReferenceEquals(selectedCommand, commands.PicoSmcStop)
            ? commands.PicoSmcStopOptions
            : ReferenceEquals(selectedCommand, commands.PicoSmcStart)
                ? commands.PicoSmcStartOptions
                : commands.PicoRebootBootloaderOptions;
        return CliRuntime.ExecuteAsync(
            new CliExecution<PicoControlResult>(
                jsonRequested,
                async (_, token) =>
                {
                    var (selector, noProgressTimeout) = ReadPicoConnectionOptions(parseResult, options);
                    await using PicoFlasherConnection connection = await OpenPicoConnectionAsync(
                            picoConnectionFactory,
                            selector,
                            noProgressTimeout,
                            token)
                        .ConfigureAwait(false);
                    var service = new PicoFlasherService(connection);
                    if (ReferenceEquals(selectedCommand, commands.PicoSmcStop))
                    {
                        await service.StopSmcAsync(token).ConfigureAwait(false);
                    }
                    else if (ReferenceEquals(selectedCommand, commands.PicoSmcStart))
                    {
                        await service.StartSmcAsync(token).ConfigureAwait(false);
                    }
                    else
                    {
                        await service.RebootToBootloaderAsync(token).ConfigureAwait(false);
                    }

                    return OperationResult.Success(new PicoControlResult(
                        connection.FirmwareVersion,
                        connection.Endpoint.DevicePath,
                        connection.Endpoint.SerialNumber,
                        selectedCommand.Name));
                },
                static (result, output, _) => output.WriteLineAsync(
                    $"PicoFlasher {result.Operation} completed: firmware v{result.FirmwareVersion}, device {result.DevicePath}."),
                successIsCommitted: true),
            standardOutput,
            standardError,
            cancellationToken);
    }

    private static Task<int> RunPicoProbeAsync(
        CommandDefinitions commands,
        ParseResult parseResult,
        PicoFlasherConnectionFactory? picoConnectionFactory,
        bool jsonRequested,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken)
    {
        return CliRuntime.ExecuteAsync(
            new CliExecution<PicoFlasherProbeResult>(
                jsonRequested,
                async (_, token) =>
                {
                    var (selector, noProgressTimeout) = ReadPicoConnectionOptions(parseResult, commands.PicoProbeOptions);
                    await using PicoFlasherConnection connection = await OpenPicoConnectionAsync(
                            picoConnectionFactory,
                            selector,
                            noProgressTimeout,
                            token)
                        .ConfigureAwait(false);
                    var service = new PicoFlasherService(connection);
                    PicoFlasherProbeResult result = await service.ProbeAsync(token).ConfigureAwait(false);
                    return OperationResult.Success(result);
                },
                static (result, output, _) => output.WriteLineAsync(
                    $"PicoFlasher probe completed: firmware v{result.FirmwareVersion}, {result.StorageKind}, flash configuration 0x{result.FlashConfiguration:X8}.")),
            standardOutput,
            standardError,
            cancellationToken);
    }

    private static Task<int> RunPicoEmmcProbeAsync(
        CommandDefinitions commands,
        ParseResult parseResult,
        PicoFlasherConnectionFactory? picoConnectionFactory,
        bool jsonRequested,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken)
    {
        return CliRuntime.ExecuteAsync(
            new CliExecution<PicoFlasherEmmcProbeResult>(
                jsonRequested,
                async (_, token) =>
                {
                    var (selector, noProgressTimeout) = ReadPicoConnectionOptions(parseResult, commands.PicoEmmcProbeOptions);
                    await using PicoFlasherConnection connection = await OpenPicoConnectionAsync(
                            picoConnectionFactory,
                            selector,
                            noProgressTimeout,
                            token)
                        .ConfigureAwait(false);
                    var service = new PicoFlasherService(connection);
                    PicoFlasherEmmcProbeResult result = await service.ProbeEmmcAsync(token).ConfigureAwait(false);
                    return OperationResult.Success(result);
                },
                static (result, output, _) => output.WriteLineAsync(
                    $"PicoFlasher eMMC probe completed: firmware v{result.FirmwareVersion}, {result.CapacitySectorCount} 512-byte sectors, flash configuration 0x{result.FlashConfiguration:X8}.")),
            standardOutput,
            standardError,
            cancellationToken);
    }

    private static Task<int> RunPicoEmmcReadAsync(
        CommandDefinitions commands,
        ParseResult parseResult,
        PicoFlasherConnectionFactory? picoConnectionFactory,
        bool jsonRequested,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken)
    {
        return CliRuntime.ExecuteAsync(
            new CliExecution<PicoFlasherEmmcReadResult>(
                jsonRequested,
                async (progress, token) =>
                {
                    var (selector, noProgressTimeout) = ReadPicoConnectionOptions(parseResult, commands.PicoEmmcReadOptions);
                    uint sectorCount = parseResult.GetValue(commands.PicoEmmcReadBlocks);
                    uint startSector = parseResult.GetValue(commands.PicoEmmcReadStartBlock);
                    if (sectorCount == 0 ||
                        startSector >= PicoFlasherProtocol.EmmcLegacyReadWindowSectorCount ||
                        (ulong)startSector + sectorCount > PicoFlasherProtocol.EmmcLegacyReadWindowSectorCount)
                    {
                        throw UsageFailure(
                            "pico-emmc-range-invalid",
                            "The requested eMMC sector range is empty or outside the supported 48 MiB read window.");
                    }

                    string outputPath = RequireOption(
                        parseResult,
                        commands.PicoEmmcReadOutput,
                        "pico-emmc-output-required",
                        "An eMMC output file is required.");
                    await using var output = AtomicOutputFile.Create(
                        outputPath,
                        parseResult.GetValue(commands.PicoEmmcReadForce));
                    await using PicoFlasherConnection connection = await OpenPicoConnectionAsync(
                            picoConnectionFactory,
                            selector,
                            noProgressTimeout,
                            token)
                        .ConfigureAwait(false);
                    var service = new PicoFlasherService(connection);
                    PicoFlasherEmmcReadResult result = await service.ReadEmmcAsync(
                            new PicoFlasherEmmcReadRequest(
                                output.Stream,
                                startSector,
                                sectorCount),
                            progress,
                            token)
                        .ConfigureAwait(false);
                    if (output.Stream.Length != result.LogicalByteLength)
                    {
                        throw new OperationFailureException(
                            ExitCode.InputOutput,
                            "pico-emmc-output-length-mismatch",
                            "The eMMC output length did not match the completed sector transfer.");
                    }

                    await output.CompleteAsync(token).ConfigureAwait(false);
                    return OperationResult.Success(result);
                },
                static (result, output, _) => output.WriteLineAsync(
                    $"PicoFlasher eMMC read completed: {result.LogicalByteLength} bytes from {result.SectorCount} sectors."),
                successIsCommitted: true),
            standardOutput,
            standardError,
            cancellationToken);
    }

    private static Task<int> RunPicoNandReadAsync(
        CommandDefinitions commands,
        ParseResult parseResult,
        PicoFlasherConnectionFactory? picoConnectionFactory,
        bool jsonRequested,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken)
    {
        return CliRuntime.ExecuteAsync(
            new CliExecution<PicoFlasherNandReadResult>(
                jsonRequested,
                async (progress, token) =>
                {
                    var (selector, noProgressTimeout) = ReadPicoConnectionOptions(parseResult, commands.PicoNandReadOptions);
                    uint? blockCount = parseResult.GetValue(commands.PicoNandReadBlocks);
                    if (blockCount == 0)
                    {
                        throw UsageFailure("pico-nand-range-invalid", "The requested NAND block count must be positive.");
                    }

                    string outputPath = RequireOption(
                        parseResult,
                        commands.PicoNandReadOutput,
                        "pico-nand-output-required",
                        "A NAND output file is required.");
                    await using var output = AtomicOutputFile.Create(
                        outputPath,
                        parseResult.GetValue(commands.PicoNandReadForce));
                    await using PicoFlasherConnection connection = await OpenPicoConnectionAsync(
                            picoConnectionFactory,
                            selector,
                            noProgressTimeout,
                            token)
                        .ConfigureAwait(false);
                    var service = new PicoFlasherService(connection);
                    PicoFlasherNandReadResult result = await service.ReadNandAsync(
                            new PicoFlasherNandReadRequest(
                                output.Stream,
                                parseResult.GetValue(commands.PicoNandReadStartBlock),
                                blockCount),
                            progress,
                            token)
                        .ConfigureAwait(false);
                    if (output.Stream.Length != result.RawByteLength)
                    {
                        throw new OperationFailureException(
                            ExitCode.InputOutput,
                            "pico-nand-output-length-mismatch",
                            "The NAND output length did not match the completed record transfer.");
                    }

                    await output.CompleteAsync(token).ConfigureAwait(false);
                    return OperationResult.Success(result);
                },
                static (result, output, _) => output.WriteLineAsync(
                    $"PicoFlasher NAND read completed: {result.LogicalByteLength} logical bytes, {result.RawByteLength} raw bytes from {result.RecordCount} records."),
                successIsCommitted: true),
            standardOutput,
            standardError,
            cancellationToken);
    }

    private static Task<int> RunPicoNandWriteAsync(
        CommandDefinitions commands,
        ParseResult parseResult,
        PicoFlasherConnectionFactory? picoConnectionFactory,
        bool jsonRequested,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken)
    {
        return CliRuntime.ExecuteAsync(
            new CliExecution<PicoFlasherNandWriteResult>(
                jsonRequested,
                async (progress, token) =>
                {
                    RequireDestructivePicoNandAcknowledgement(parseResult, commands.PicoNandWriteYes);
                    var (selector, noProgressTimeout) = ReadPicoConnectionOptions(parseResult, commands.PicoNandWriteOptions);

                    string inputPath = RequireOption(
                        parseResult,
                        commands.PicoNandWriteInput,
                        "pico-nand-input-required",
                        "A NAND input file is required.");
                    await using FileStream input = OpenRead(inputPath, "--input");
                    await using PicoFlasherConnection connection = await OpenPicoConnectionAsync(
                            picoConnectionFactory,
                            selector,
                            noProgressTimeout,
                            token)
                        .ConfigureAwait(false);
                    var service = new PicoFlasherService(connection);
                    PicoFlasherNandWriteResult result = await service.WriteNandAsync(
                            new PicoFlasherNandWriteRequest(
                                input,
                                parseResult.GetValue(commands.PicoNandWriteStartBlock)),
                            progress,
                            token)
                        .ConfigureAwait(false);
                    return OperationResult.Success(result);
                },
                static (result, output, _) => output.WriteLineAsync(
                    $"PicoFlasher NAND write completed: {result.LogicalByteLength} logical bytes, {result.RawByteLength} raw bytes across {result.RecordCount} records."),
                successIsCommitted: true),
            standardOutput,
            standardError,
            cancellationToken);
    }

    private static Task<int> RunPicoNandEraseAsync(
        CommandDefinitions commands,
        ParseResult parseResult,
        PicoFlasherConnectionFactory? picoConnectionFactory,
        bool jsonRequested,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken)
    {
        return CliRuntime.ExecuteAsync(
            new CliExecution<PicoFlasherNandEraseResult>(
                jsonRequested,
                async (progress, token) =>
                {
                    RequireDestructivePicoNandAcknowledgement(parseResult, commands.PicoNandEraseYes);
                    var (selector, noProgressTimeout) = ReadPicoConnectionOptions(parseResult, commands.PicoNandEraseOptions);
                    uint eraseBlockCount = parseResult.GetValue(commands.PicoNandEraseEraseBlocks);
                    if (eraseBlockCount == 0)
                    {
                        throw UsageFailure("pico-nand-range-invalid", "The requested NAND erase-block count must be positive.");
                    }

                    await using PicoFlasherConnection connection = await OpenPicoConnectionAsync(
                            picoConnectionFactory,
                            selector,
                            noProgressTimeout,
                            token)
                        .ConfigureAwait(false);
                    var service = new PicoFlasherService(connection);
                    PicoFlasherNandEraseResult result = await service.EraseNandAsync(
                            new PicoFlasherNandEraseRequest(
                                parseResult.GetValue(commands.PicoNandEraseStartEraseBlock),
                                eraseBlockCount),
                            progress,
                            token)
                        .ConfigureAwait(false);
                    return OperationResult.Success(result);
                },
                static (result, output, _) => output.WriteLineAsync(
                    $"PicoFlasher NAND erase completed: {result.EraseBlockCount} erase blocks; logical records {result.StartRecord}-{result.EndRecordInclusive}, logical bytes {result.StartLogicalByteOffset}-{result.EndLogicalByteOffsetInclusive} ({result.LogicalByteLength} logical bytes, {result.RawByteLength} raw bytes)."),
                successIsCommitted: true),
            standardOutput,
            standardError,
            cancellationToken);
    }

    private static (PicoFlasherDeviceSelector Selector, TimeSpan NoProgressTimeout) ReadPicoConnectionOptions(
        ParseResult parseResult,
        PicoCommandOptions options)
    {
        PicoFlasherDeviceSelector selector = PicoFlasherDeviceSelector.Create(
            parseResult.GetValue(options.Device),
            parseResult.GetValue(options.Serial));
        double seconds = parseResult.GetValue(options.Timeout) ?? PicoFlasherProtocol.DefaultNoProgressTimeout.TotalSeconds;
        if (!double.IsFinite(seconds) ||
            seconds <= 0 ||
            seconds > PicoFlasherProtocol.MaximumNoProgressTimeout.TotalSeconds)
        {
            throw UsageFailure(
                "pico-timeout-invalid",
                "The PicoFlasher no-progress timeout must be positive and supported by the serial transport.");
        }

        TimeSpan noProgressTimeout = TimeSpan.FromSeconds(seconds);
        if (!PicoFlasherProtocol.IsValidNoProgressTimeout(noProgressTimeout))
        {
            throw UsageFailure(
                "pico-timeout-invalid",
                "The PicoFlasher no-progress timeout must be positive and supported by the serial transport.");
        }

        return (selector, noProgressTimeout);
    }

    private static ValueTask<PicoFlasherConnection> OpenPicoConnectionAsync(
        PicoFlasherConnectionFactory? picoConnectionFactory,
        PicoFlasherDeviceSelector selector,
        TimeSpan noProgressTimeout,
        CancellationToken cancellationToken)
    {
        return (picoConnectionFactory ?? new PicoFlasherConnectionFactory()).OpenAsync(
            selector,
            noProgressTimeout,
            cancellationToken);
    }

    private static OperationFailure CreateParseFailure(
        CommandDefinitions commands,
        ParseResult parseResult,
        bool informationRequested = false)
    {
        Command selectedCommand = parseResult.CommandResult.Command;

        if (HasUnsupportedPicoPath(commands, parseResult))
        {
            return UnsupportedCommand(commands, commands.Pico);
        }

        if ((ReferenceEquals(selectedCommand, commands.Root) ||
             ReferenceEquals(selectedCommand, commands.Nand) ||
             ReferenceEquals(selectedCommand, commands.Patch) ||
             ReferenceEquals(selectedCommand, commands.Console) ||
             ReferenceEquals(selectedCommand, commands.Device) ||
             ReferenceEquals(selectedCommand, commands.Pico) ||
             ReferenceEquals(selectedCommand, commands.Support) ||
             ReferenceEquals(selectedCommand, commands.XeBuild)) &&
            !parseResult.Errors.Any(static error => error.SymbolResult is OptionResult or ArgumentResult))
        {
            return UnsupportedCommand(commands, selectedCommand);
        }

        int unmatchedCount = parseResult.UnmatchedTokens.Count;
        var diagnostics = new List<string>();
        var failingSymbols = new HashSet<Symbol>();
        foreach (ParseError error in parseResult.Errors)
        {
            if (informationRequested && IsIgnorableInformationError(error, parseResult))
            {
                continue;
            }

            // Public symbol identities supply registered names without consulting raw parser text.
            switch (error.SymbolResult)
            {
                case OptionResult optionResult when failingSymbols.Add(optionResult.Option):
                    diagnostics.Add(GetOptionParseDiagnostic(commands, optionResult));
                    break;
                case ArgumentResult argumentResult when failingSymbols.Add(argumentResult.Argument):
                    diagnostics.Add(GetArgumentParseDiagnostic(argumentResult));
                    break;
            }
        }

        if (unmatchedCount != 0)
        {
            diagnostics.Add(
                $"Unexpected or unrecognized arguments: {unmatchedCount}. Use only the options and operands shown in the usage.");
        }

        if (diagnostics.Count == 0)
        {
            diagnostics.Add("The command arguments do not match this command's usage.");
        }

        return new OperationFailure(
            ExitCode.Usage,
            "invalid-command-arguments",
            AppendUsageGuidance(commands, selectedCommand, string.Join("\n", diagnostics)));
    }

    private static string GetOptionParseDiagnostic(CommandDefinitions commands, OptionResult optionResult)
    {
        Option option = optionResult.Option;
        int valueCount = optionResult.Tokens.Count;
        if (option.Required &&
            optionResult.Implicit &&
            optionResult.IdentifierToken is null &&
            valueCount == 0)
        {
            return $"Missing required option '{option.Name}'.";
        }

        if (optionResult.IdentifierTokenCount > 0 && valueCount < option.Arity.MinimumNumberOfValues)
        {
            return $"Option '{option.Name}' requires a value <{option.HelpName ?? "value"}>.";
        }

        if (option.Arity.MaximumNumberOfValues == 1 &&
            (valueCount > 1 || optionResult.IdentifierTokenCount > 1))
        {
            return $"Option '{option.Name}' accepts one value; specify it once.";
        }

        if (ReferenceEquals(option, commands.XeBuildBuildPatches) &&
            optionResult.IdentifierTokenCount != valueCount)
        {
            return "Each --patch option requires exactly one patch name; repeat --patch for multiple names.";
        }

        if (ReferenceEquals(option, commands.SupportRoot))
        {
            return "Option '--support-root' requires a valid non-root directory path.";
        }

        Type valueType = Nullable.GetUnderlyingType(option.ValueType) ?? option.ValueType;
        if (valueType == typeof(uint))
        {
            return $"Option '{option.Name}' requires a whole number from 0 to 4294967295.";
        }

        if (valueType == typeof(int))
        {
            return $"Option '{option.Name}' requires a whole number from -2147483648 to 2147483647.";
        }

        if (valueType == typeof(double))
        {
            return $"Option '{option.Name}' requires a number.";
        }

        return $"The value supplied for option '{option.Name}' is invalid. See its help for accepted values.";
    }

    private static string GetArgumentParseDiagnostic(ArgumentResult argumentResult)
    {
        string name = argumentResult.Argument.Name;
        return argumentResult.Tokens.Count < argumentResult.Argument.Arity.MinimumNumberOfValues
            ? $"Missing required argument '<{name}>'."
            : $"The value supplied for argument '<{name}>' is invalid.";
    }

    private static string AppendUsageGuidance(
        CommandDefinitions commands,
        Command selectedCommand,
        string message)
    {
        return $"{message}\n\n{GetUsageText(commands, selectedCommand)}\n" +
            $"Example: {GetExampleCommand(commands, selectedCommand)}\n" +
            $"Run '{GetCommandPath(commands, selectedCommand)} --help' for details.";
    }

    private static bool HasUnsupportedPicoPath(CommandDefinitions commands, ParseResult parseResult)
    {
        CommandResult leafResult = parseResult.CommandResult;
        if (leafResult.Parent is not CommandResult picoResult ||
            !ReferenceEquals(picoResult.Command, commands.Pico))
        {
            return false;
        }

        bool afterPico = false;
        foreach (Token token in parseResult.Tokens)
        {
            if (ReferenceEquals(token, leafResult.IdentifierToken))
            {
                return false;
            }

            if (ReferenceEquals(token, picoResult.IdentifierToken))
            {
                afterPico = true;
                continue;
            }

            if (!afterPico || token.Type != TokenType.Argument || IsOptionValue(token, leafResult))
            {
                continue;
            }

            // The first unmatched word before the leaf names an unsupported path.
            // An unknown option and its would-be value are malformed arguments instead.
            return !token.Value.StartsWith("-", StringComparison.Ordinal);
        }

        return false;

        static bool IsOptionValue(Token candidateToken, CommandResult selectedResult)
        {
            for (CommandResult? commandResult = selectedResult;
                 commandResult is not null;
                 commandResult = commandResult.Parent as CommandResult)
            {
                foreach (SymbolResult child in commandResult.Children)
                {
                    if (child is not OptionResult optionResult)
                    {
                        continue;
                    }

                    foreach (Token valueToken in optionResult.Tokens)
                    {
                        if (ReferenceEquals(candidateToken, valueToken))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }
    }

    private static OperationFailure UnsupportedCommand(CommandDefinitions commands, Command selectedCommand)
    {
        string message = ReferenceEquals(selectedCommand, commands.Root)
            ? "Choose a supported command.\nAvailable command groups: " +
                string.Join(", ", commands.Root.Subcommands.Select(static command => command.Name)) + "."
            : $"Choose a supported subcommand for '{GetCommandPath(commands, selectedCommand)}'.\n" +
                "Available subcommands: " +
                string.Join(", ", selectedCommand.Subcommands.Select(static command => command.Name)) + ".";
        return new OperationFailure(
            ExitCode.Usage,
            "unsupported-command",
            AppendUsageGuidance(commands, selectedCommand, message));
    }

    private static bool HasInvalidHelpArguments(ParseResult parseResult)
    {
        if (parseResult.UnmatchedTokens.Count != 0)
        {
            return true;
        }

        return parseResult.Errors.Any(error => !IsIgnorableInformationError(error, parseResult));
    }

    private static bool IsIgnorableInformationError(ParseError error, ParseResult parseResult)
    {
        // Information requests may omit required operands, but not malformed supplied values.
        if (error.SymbolResult is OptionResult optionResult &&
            optionResult.Option.Required &&
            optionResult.Implicit &&
            optionResult.IdentifierToken is null &&
            optionResult.Tokens.Count == 0)
        {
            return true;
        }

        if (error.SymbolResult is ArgumentResult argumentResult &&
            argumentResult.Tokens.Count == 0 &&
            argumentResult.Argument.Arity.MinimumNumberOfValues > 0)
        {
            return true;
        }

        // A bare command group has no action; an information request need not name a child.
        return ReferenceEquals(error.SymbolResult, parseResult.CommandResult) &&
            parseResult.CommandResult.Command.Subcommands.Count > 0;
    }

    private static void ValidateHelpOptions(CommandDefinitions commands, ParseResult parseResult)
    {
        ValidatePicoHelpOptions(commands, parseResult);
        Command selectedCommand = parseResult.CommandResult.Command;
        CpuKeyCommandOptions? cpuKeyOptions =
            ReferenceEquals(selectedCommand, commands.NandInspect) ? commands.NandInspectCpuKey :
            ReferenceEquals(selectedCommand, commands.NandRgh3Convert) ? commands.NandRgh3ConvertCpuKey :
            ReferenceEquals(selectedCommand, commands.XeBuildBuild) ? commands.XeBuildBuildCpuKey :
            null;
        if (cpuKeyOptions is not null)
        {
            string? cpuKeyFilePath = parseResult.GetValue(cpuKeyOptions.File);
            string? cpuKeyEnvironmentName = parseResult.GetValue(cpuKeyOptions.Environment);
            int sourceCount = cpuKeyFilePath is null ? 0 : 1;
            sourceCount += cpuKeyEnvironmentName is null ? 0 : 1;
            sourceCount += parseResult.GetValue(cpuKeyOptions.StandardInput) ? 1 : 0;
            if (sourceCount > 1)
            {
                throw UsageFailure(
                    "cpu-key-source-conflict",
                    "Select exactly one CPU key source: file, environment variable, or standard input.");
            }

            // Match CpuKeySourceResolver's selector syntax checks without resolving a file,
            // environment variable, or standard input while rendering help.
            if (cpuKeyFilePath is not null && !IsValidPath(cpuKeyFilePath))
            {
                throw UsageFailure("invalid-cpu-key-file", "The CPU key file path is invalid.");
            }

            if (cpuKeyEnvironmentName is not null && !IsValidCpuKeyEnvironmentVariableName(cpuKeyEnvironmentName))
            {
                throw UsageFailure(
                    "invalid-cpu-key-env",
                    "The CPU key environment variable name is invalid.");
            }
        }

        if (ReferenceEquals(selectedCommand, commands.XeBuildBuild))
        {
            ReadXeBuildSelections(commands, parseResult, allowMissingRequiredOptions: true);
        }
    }

    private static void ValidatePicoHelpOptions(CommandDefinitions commands, ParseResult parseResult)
    {
        Command selectedCommand = parseResult.CommandResult.Command;
        PicoCommandOptions? options =
            ReferenceEquals(selectedCommand, commands.PicoProbe) ? commands.PicoProbeOptions :
            ReferenceEquals(selectedCommand, commands.PicoSmcStop) ? commands.PicoSmcStopOptions :
            ReferenceEquals(selectedCommand, commands.PicoSmcStart) ? commands.PicoSmcStartOptions :
            ReferenceEquals(selectedCommand, commands.PicoRebootBootloader) ? commands.PicoRebootBootloaderOptions :
            ReferenceEquals(selectedCommand, commands.PicoNandRead) ? commands.PicoNandReadOptions :
            ReferenceEquals(selectedCommand, commands.PicoNandWrite) ? commands.PicoNandWriteOptions :
            ReferenceEquals(selectedCommand, commands.PicoNandErase) ? commands.PicoNandEraseOptions :
            ReferenceEquals(selectedCommand, commands.PicoEmmcProbe) ? commands.PicoEmmcProbeOptions :
            ReferenceEquals(selectedCommand, commands.PicoEmmcRead) ? commands.PicoEmmcReadOptions :
            null;
        if (options is not null)
        {
            _ = ReadPicoConnectionOptions(parseResult, options);
        }
    }

    private static Task<int> RenderInformationAsync(
        bool jsonRequested,
        string information,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken,
        Action? validate = null)
    {
        return CliRuntime.ExecuteAsync(
            new CliExecution<string>(
                jsonRequested,
                (_, _) =>
                {
                    validate?.Invoke();
                    return Task.FromResult(OperationResult.Success(information));
                },
                static (result, output, _) => output.WriteAsync(result)),
            standardOutput,
            standardError,
            cancellationToken);
    }

    private static bool IsHelpRequested(IReadOnlyList<string> arguments)
    {
        for (int index = 0; index < arguments.Count; index++)
        {
            if (IsHelpFlag(arguments[index]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsHelpFlag(string argument)
    {
        return argument is "--help" or "-h" or "/h" or "-?" or "/?";
    }

    private static bool IsVersionRequested(IReadOnlyList<string> arguments)
    {
        return HasExactArgument(arguments, "--version");
    }

    private static bool HasExactArgument(IReadOnlyList<string> arguments, string expected)
    {
        for (int index = 0; index < arguments.Count; index++)
        {
            if (string.Equals(arguments[index], expected, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }



    private static SupportRoot ResolveSupportRoot(ParseResult parseResult, Option<string> supportRootOption)
    {
        return SupportRootResolver.Resolve(parseResult.GetValue(supportRootOption));
    }

    private static (XeBuildBuildTarget? Target, XeBuildExecutionOptions Execution) ReadXeBuildSelections(
        CommandDefinitions commands,
        ParseResult parseResult,
        bool allowMissingRequiredOptions = false)
    {
        string? consoleOverride = parseResult.GetValue(commands.XeBuildBuildConsole);
        if (consoleOverride is not null && !ConsoleCatalog.TryGetByCanonicalName(consoleOverride, out _))
        {
            throw UsageFailure(
                "xebuild-console-unsupported",
                "The console must be a canonical name from 'jrunner console list'.");
        }

        // Missing required options carry parser errors; help must not request their values.
        bool dashboardSupplied = parseResult.GetResult(commands.XeBuildBuildDashboard)?.Implicit is false;
        int dashboardVersion = allowMissingRequiredOptions && !dashboardSupplied
            ? 0
            : parseResult.GetValue(commands.XeBuildBuildDashboard);
        if (dashboardVersion <= 0 && (!allowMissingRequiredOptions || dashboardSupplied))
        {
            throw UsageFailure("xebuild-dashboard-invalid", "The XeBuild dashboard version must be positive.");
        }

        bool typeSupplied = parseResult.GetResult(commands.XeBuildBuildType)?.Implicit is false;
        string? typeName = allowMissingRequiredOptions && !typeSupplied
            ? null
            : parseResult.GetValue(commands.XeBuildBuildType);
        if (typeName is null && !allowMissingRequiredOptions)
        {
            throw UsageFailure("xebuild-type-required", "A canonical XeBuild type is required.");
        }

        if (typeName is not null && !XeBuildHackTypeCatalog.TryGetByCanonicalName(typeName, out _))
        {
            throw UsageFailure("xebuild-type-invalid", "The XeBuild type must be a canonical target name.");
        }

        XeBuildBuildOptions options;
        try
        {
            options = new XeBuildBuildOptions(
                bigFfs: parseResult.GetValue(commands.XeBuildBuildBigFfs),
                rgh3: parseResult.GetValue(commands.XeBuildBuildRgh3),
                dashLaunch: parseResult.GetValue(commands.XeBuildBuildDashLaunch),
                drivePatch: ParseDrivePatch(parseResult.GetValue(commands.XeBuildBuildDrivePatch)),
                namedPatches: parseResult.GetValue(commands.XeBuildBuildPatches));
        }
        catch (ArgumentException)
        {
            throw UsageFailure(
                "xebuild-patch-invalid",
                "XeBuild patches must be distinct names containing only ASCII letters, digits, or underscores.");
        }

        bool systemPartitionOnly = parseResult.GetValue(commands.XeBuildBuildSystemPartitionOnly);
        bool fullData = parseResult.GetValue(commands.XeBuildBuildFullFourGigabyteData);
        if (systemPartitionOnly && fullData)
        {
            throw UsageFailure(
                "xebuild-4gb-staging-policy-conflict",
                "Select only one 4 GB staging policy: --system-partition-only or --full-4gb-data.");
        }

        XeBuildFourGigabyteStagingPolicy stagingPolicy = systemPartitionOnly
            ? XeBuildFourGigabyteStagingPolicy.SystemPartitionOnly
            : fullData ? XeBuildFourGigabyteStagingPolicy.FullData : XeBuildFourGigabyteStagingPolicy.None;
        XeBuildBackendKind backend = ParseXeBuildBackend(parseResult.GetValue(commands.XeBuildBuildBackend));

        return (
            dashboardVersion > 0 && typeName is not null
                ? new XeBuildBuildTarget(consoleOverride, dashboardVersion, typeName, options)
                : null,
            new XeBuildExecutionOptions(
                stagingPolicy,
                backend,
                keepWorkspace: parseResult.GetValue(commands.XeBuildBuildKeepWorkspace),
                overwriteExistingOutput: parseResult.GetValue(commands.XeBuildBuildForce)));
    }

    private static XeBuildBackendKind ParseXeBuildBackend(string? value)
    {
        return value switch
        {
            null => XeBuildBackendKind.Wine,
            _ when string.Equals(value, "wine", StringComparison.OrdinalIgnoreCase) => XeBuildBackendKind.Wine,
            _ when string.Equals(value, "native", StringComparison.OrdinalIgnoreCase) => XeBuildBackendKind.Native,
            _ => throw UsageFailure("xebuild-backend-invalid", "The XeBuild backend must be wine or native."),
        };
    }

    private static OperationFailureException XeBuildBackendUnavailable()
    {
        return new OperationFailureException(
            ExitCode.MissingPrerequisite,
            "xebuild-backend-unavailable",
            "The requested XeBuild backend is unavailable. Wine is the only available backend; no fallback was attempted.");
    }

    private static void ValidateFourGigabyteStagingPolicy(
        long sourceByteLength,
        XeBuildFourGigabyteStagingPolicy stagingPolicy)
    {
        if (sourceByteLength == XeBuildSourceContext.FourGigabyteEmmcByteLength)
        {
            if (stagingPolicy is XeBuildFourGigabyteStagingPolicy.None)
            {
                throw UsageFailure(
                    "xebuild-4gb-staging-policy-required",
                    "A 4 GB source requires --system-partition-only or --full-4gb-data.");
            }
        }
        else if (stagingPolicy is not XeBuildFourGigabyteStagingPolicy.None)
        {
            throw UsageFailure(
                "xebuild-4gb-staging-policy-inapplicable",
                "A 4 GB staging policy can be selected only for a full 4 GB source.");
        }
    }

    private static XeBuildDrivePatch ParseDrivePatch(string? value)
    {
        return value switch
        {
            null => XeBuildDrivePatch.None,
            _ when string.Equals(value, "usb", StringComparison.OrdinalIgnoreCase) => XeBuildDrivePatch.Usb,
            _ when string.Equals(value, "hdd", StringComparison.OrdinalIgnoreCase) => XeBuildDrivePatch.Hdd,
            _ when string.Equals(value, "both", StringComparison.OrdinalIgnoreCase) => XeBuildDrivePatch.Both,
            _ => throw UsageFailure("xebuild-drive-patch-invalid", "The XeBuild drive patch must be usb, hdd, or both."),
        };
    }

    private static OperationFailureException CreateSupportStatusFailure(SupportStatusResult result)
    {
        return result.Status switch
        {
            SupportStatusKind.Absent => new OperationFailureException(
                ExitCode.MissingPrerequisite,
                "support-absent",
                "The XeBuild support payload is not installed. Run 'jrunner support install'."),
            SupportStatusKind.Incomplete => new OperationFailureException(
                ExitCode.MissingPrerequisite,
                "support-incomplete",
                "The XeBuild support payload is incomplete. Run 'jrunner support install'."),
            SupportStatusKind.Corrupt => new OperationFailureException(
                ExitCode.MissingPrerequisite,
                "support-corrupt",
                "The XeBuild support payload is corrupt. Run 'jrunner support install'."),
            SupportStatusKind.Valid => throw new InvalidOperationException(
                "A valid support status cannot be converted into a failure."),
            _ => throw new ArgumentOutOfRangeException(nameof(result)),
        };
    }

    private static string RequireOption(
        ParseResult parseResult,
        Option<string> option,
        string kind,
        string message)
    {
        string? value = parseResult.GetValue(option);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw UsageFailure(kind, message);
        }

        return value;
    }

    private static void RequireDestructivePicoNandAcknowledgement(
        ParseResult parseResult,
        Option<bool> acknowledgementOption)
    {
        if (!parseResult.GetValue(acknowledgementOption))
        {
            throw UsageFailure(
                "pico-nand-confirmation-required",
                "PicoFlasher NAND write and erase operations require --yes to acknowledge that they are destructive.");
        }
    }

    private static string RequireArgument(
        ParseResult parseResult,
        Argument<string> argument,
        string kind,
        string message)
    {
        string? value = parseResult.GetValue(argument);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw UsageFailure(kind, message);
        }

        return value;
    }

    private static ValueTask<CpuKey?> ResolveCpuKeyAsync(
        ParseResult parseResult,
        CpuKeyCommandOptions options,
        CpuKeySourcePolicy policy,
        TextReader standardInput,
        CancellationToken cancellationToken)
    {
        return new CpuKeySourceResolver().ResolveAsync(
            new CpuKeySourceSelection(
                parseResult.GetValue(options.File),
                parseResult.GetValue(options.Environment),
                parseResult.GetValue(options.StandardInput)),
            policy,
            standardInput,
            cancellationToken);
    }


    private static bool IsValidPath(string? path, bool allowFileSystemRoot = true)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            string fullPath = Path.GetFullPath(path);
            return allowFileSystemRoot || !string.Equals(fullPath, Path.GetPathRoot(fullPath), StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsValidCpuKeyEnvironmentVariableName(string? value)
    {
        if (string.IsNullOrEmpty(value) || !IsEnvironmentNameStart(value[0]))
        {
            return false;
        }

        for (int index = 1; index < value.Length; index++)
        {
            if (!IsEnvironmentNamePart(value[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsEnvironmentNameStart(char value)
    {
        return value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';
    }

    private static bool IsEnvironmentNamePart(char value)
    {
        return IsEnvironmentNameStart(value) || value is >= '0' and <= '9';
    }


    private static FileStream OpenRead(string path, string operand, int bufferSize = FileBufferSize)
    {
        string normalizedPath = NormalizeInputPath(path);
        try
        {
            return new FileStream(
                normalizedPath,
                new FileStreamOptions
                {
                    Access = FileAccess.Read,
                    Mode = FileMode.Open,
                    Share = FileShare.Read,
                    BufferSize = bufferSize,
                    Options = FileOptions.Asynchronous,
                });
        }
        catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new OperationFailureException(
                ExitCode.InputOutput,
                "io-error",
                $"Cannot open the file for {operand}: the file or its parent directory does not exist. Check the path supplied to {operand}.");
        }
        catch (UnauthorizedAccessException) when (Directory.Exists(normalizedPath))
        {
            throw new OperationFailureException(
                ExitCode.DeviceUnavailable,
                "permission-denied",
                $"Cannot open the file for {operand}: the supplied path names a directory. Select a file.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new OperationFailureException(
                ExitCode.DeviceUnavailable,
                "permission-denied",
                $"Cannot read the file for {operand}: access was denied. Check file and parent-directory permissions.");
        }
        catch (IOException)
        {
            throw new OperationFailureException(
                ExitCode.InputOutput,
                "io-error",
                $"Cannot open the file for {operand} because of an I/O error. Check that the file is available and readable.");
        }
    }

    private static async Task<byte[]> ReadPatchBytesAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream input = OpenRead(path, "--input");
        if (input.Length > int.MaxValue)
        {
            throw new OperationFailureException(
                ExitCode.InvalidData,
                "patch-file-too-large",
                "The patch input is too large to inspect.");
        }

        byte[] patchBytes = GC.AllocateUninitializedArray<byte>(checked((int)input.Length));
        try
        {
            await input.ReadExactlyAsync(patchBytes, cancellationToken).ConfigureAwait(false);
            return patchBytes;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(patchBytes);
            throw;
        }
    }

    private static void EnsureOutputDoesNotMatchInput(string outputPath, params ReadOnlySpan<string?> inputPaths)
    {
        string comparableOutputPath = ResolvePathForComparison(outputPath);
        foreach (string? inputPath in inputPaths)
        {
            if (inputPath is not null &&
                string.Equals(comparableOutputPath, ResolvePathForComparison(inputPath), StringComparison.Ordinal))
            {
                throw UsageFailure(
                    "output-matches-input",
                    "The output path must differ from each input path.");
            }
        }
    }

    private static string ResolvePathForComparison(string path)
    {
        string fullPath = NormalizeInputPath(path);
        string root = Path.GetPathRoot(fullPath)
            ?? throw UsageFailure("invalid-input", "An input file path is invalid.");
        string resolvedPath = root;
        foreach (string segment in fullPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidatePath = Path.Combine(resolvedPath, segment);
            FileSystemInfo? candidate = GetExistingFileSystemInfo(candidatePath);
            FileSystemInfo? target = candidate?.ResolveLinkTarget(returnFinalTarget: true);
            resolvedPath = target?.FullName ?? candidatePath;
        }

        return resolvedPath;
    }

    private static FileSystemInfo? GetExistingFileSystemInfo(string path)
    {
        var directory = new DirectoryInfo(path);
        if (directory.Exists)
        {
            return directory;
        }

        var file = new FileInfo(path);
        return file.Exists || file.LinkTarget is not null ? file : null;
    }

    private static string NormalizeInputPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw UsageFailure("invalid-input", "An input file path is required.");
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            throw UsageFailure("invalid-input", "An input file path is invalid.");
        }
        catch (NotSupportedException)
        {
            throw UsageFailure("invalid-input", "An input file path is invalid.");
        }
        catch (PathTooLongException)
        {
            throw UsageFailure("invalid-input", "An input file path is invalid.");
        }
    }

    private static IReadOnlyList<string> RemoveRouterFlags(IReadOnlyList<string> arguments)
    {
        List<string>? filtered = null;
        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (argument is "--json" or "--version" || IsHelpFlag(argument))
            {
                if (filtered is null)
                {
                    filtered = new List<string>(arguments.Count - 1);
                    for (int previousIndex = 0; previousIndex < index; previousIndex++)
                    {
                        filtered.Add(arguments[previousIndex]);
                    }
                }

                continue;
            }

            filtered?.Add(argument);
        }

        return filtered ?? arguments;
    }

    private static OperationFailureException UsageFailure(string kind, string message)
    {
        return new OperationFailureException(ExitCode.Usage, kind, message);
    }

    private sealed record ConsoleListEntry(
        int LegacyId,
        string CanonicalName,
        string XeBuildName,
        string IniName,
        int NandSizeMegabytes,
        long LogicalNandByteLength,
        int LegacyLayoutId,
        IReadOnlyList<string> SupportedHackTargets);

    private sealed record PicoControlResult(
        uint FirmwareVersion,
        string DevicePath,
        string? SerialNumber,
        string Operation);

    private sealed class CpuKeyCommandOptions
    {
        internal CpuKeyCommandOptions(Command command)
        {
            File = new Option<string>("--cpu-key-file")
            {
                Description = "A file containing one CPU key; mutually exclusive with the environment and stdin sources.",
                HelpName = "file",
                Arity = ArgumentArity.ExactlyOne,
            };
            Environment = new Option<string>("--cpu-key-env")
            {
                Description = "The name of an environment variable containing one CPU key.",
                HelpName = "name",
                Arity = ArgumentArity.ExactlyOne,
            };
            StandardInput = new Option<bool>("--cpu-key-stdin")
            {
                Description = "Read one CPU key from standard input without terminal echo.",
                Arity = ArgumentArity.Zero,
            };
            command.Options.Add(File);
            command.Options.Add(Environment);
            command.Options.Add(StandardInput);
        }

        internal Option<string> File { get; }

        internal Option<string> Environment { get; }

        internal Option<bool> StandardInput { get; }
    }

    private sealed class PicoCommandOptions
    {
        internal PicoCommandOptions(Command command)
        {
            Device = new Option<string>("--device")
            {
                Description = "The exact device path of a PicoFlasher command interface; mutually exclusive with --serial.",
                HelpName = "path",
            };
            Serial = new Option<string>("--serial")
            {
                Description = "The exact USB serial number of one physical device; mutually exclusive with --device.",
                HelpName = "value",
            };
            Timeout = new Option<double?>("--timeout")
            {
                Description = "The positive no-progress timeout in seconds. Defaults to 10 seconds.",
                HelpName = "seconds",
            };
            command.Options.Add(Device);
            command.Options.Add(Serial);
            command.Options.Add(Timeout);
        }

        internal Option<string> Device { get; }

        internal Option<string> Serial { get; }

        internal Option<double?> Timeout { get; }
    }

    private sealed class CommandDefinitions
    {
        internal CommandDefinitions()
        {
            Root = new RootCommand("JRunnerEx native command-line interface");
            // Built-in actions write through System.CommandLine's console configuration. The router
            // handles every standard alias through caller-owned writers before dispatch instead.
            Root.Options.Remove(Root.Options.OfType<HelpOption>().Single());
            Root.Options.Remove(Root.Options.OfType<VersionOption>().Single());
            SupportRoot = SupportRootOption();
            Root.Options.Add(SupportRoot);


            Nand = new Command("nand", "Inspect, compare, and convert NAND images.");
            NandInspect = new Command("inspect", "Inspect a NAND image.");
            NandInspectInput = RequiredStringOption("--input", "The NAND image to inspect.", "file");
            NandInspectCpuKey = new CpuKeyCommandOptions(NandInspect);
            NandInspect.Options.Add(NandInspectInput);

            NandCompare = new Command("compare", "Compare canonical NAND image contents.");
            NandCompareLeft = new Argument<string>("left")
            {
                Description = "The first NAND image.",
            };
            NandCompareRight = new Argument<string>("right")
            {
                Description = "The second NAND image.",
            };
            NandCompare.Arguments.Add(NandCompareLeft);
            NandCompare.Arguments.Add(NandCompareRight);

            NandRgh3Convert = new Command("rgh3-convert", "Convert an RGH2 NAND image to RGH3.");
            NandRgh3ConvertEcc = RequiredStringOption("--ecc", "The RGH3 ECC template.", "file");
            NandRgh3ConvertFlash = RequiredStringOption("--flash", "The RGH2 NAND image.", "file");
            NandRgh3ConvertCpuKey = new CpuKeyCommandOptions(NandRgh3Convert);
            NandRgh3ConvertOutput = RequiredStringOption("--output", "The converted output image.", "file");
            NandRgh3ConvertNoSmcPatch = new Option<bool>("--no-smc-patch")
            {
                Description = "Preserve the source SMC instead of replacing it with the template SMC.",
            };
            NandRgh3ConvertForce = new Option<bool>("--force")
            {
                Description = "Replace an existing output file atomically.",
            };
            NandRgh3Convert.Options.Add(NandRgh3ConvertEcc);
            NandRgh3Convert.Options.Add(NandRgh3ConvertFlash);
            NandRgh3Convert.Options.Add(NandRgh3ConvertOutput);
            NandRgh3Convert.Options.Add(NandRgh3ConvertNoSmcPatch);
            NandRgh3Convert.Options.Add(NandRgh3ConvertForce);

            Nand.Subcommands.Add(NandInspect);
            Nand.Subcommands.Add(NandCompare);
            Nand.Subcommands.Add(NandRgh3Convert);

            Patch = new Command("patch", "Inspect legacy patch sections.");
            PatchInspect = new Command("inspect", "Inspect a patch section.");
            PatchInspectInput = RequiredStringOption("--input", "The patch section file to inspect.", "file");
            PatchInspect.Options.Add(PatchInspectInput);
            Patch.Subcommands.Add(PatchInspect);

            Console = new Command("console", "List canonical console targets.");
            ConsoleList = new Command("list", "List canonical console targets and compatible XeBuild targets.");
            Console.Subcommands.Add(ConsoleList);

            Device = new Command("device", "List PicoFlasher and BlackPill command interfaces without opening them.");
            DeviceList = new Command("list", "List candidate command interfaces without sending any protocol commands.");
            Device.Subcommands.Add(DeviceList);

            Pico = new Command("pico", "Probe and operate a v4 PicoFlasher or BlackPill flasher.");
            PicoProbe = new Command("probe", "Probe the connected console flash configuration.");
            PicoProbeOptions = new PicoCommandOptions(PicoProbe);
            PicoSmcStop = new Command("smc-stop", "Stop the SMC and intentionally leave it stopped.");
            PicoSmcStopOptions = new PicoCommandOptions(PicoSmcStop);
            PicoSmcStart = new Command("smc-start", "Start the SMC.");
            PicoSmcStartOptions = new PicoCommandOptions(PicoSmcStart);
            PicoRebootBootloader = new Command("reboot-bootloader", "Reboot the flasher to its bootloader.");
            PicoRebootBootloaderOptions = new PicoCommandOptions(PicoRebootBootloader);

            PicoNandRead = new Command("nand-read", "Read raw NAND records to an atomically published file.");
            PicoNandReadOptions = new PicoCommandOptions(PicoNandRead);
            PicoNandReadOutput = RequiredStringOption("--output", "The raw NAND output file to publish.", "file");
            PicoNandReadStartBlock = new Option<uint>("--start-block")
            {
                Description = "The first 512-byte logical-data record to read. Defaults to zero.",
                HelpName = "record",
            };
            PicoNandReadBlocks = new Option<uint?>("--blocks")
            {
                Description = "The positive number of 512-byte logical-data records to read. Defaults through the detected NAND end.",
                HelpName = "count",
            };
            PicoNandReadForce = new Option<bool>("--force")
            {
                Description = "Replace an existing output file atomically.",
            };
            PicoNandRead.Options.Add(PicoNandReadOutput);
            PicoNandRead.Options.Add(PicoNandReadStartBlock);
            PicoNandRead.Options.Add(PicoNandReadBlocks);
            PicoNandRead.Options.Add(PicoNandReadForce);

            PicoNandWrite = new Command("nand-write", "Write raw NAND data; firmware automatically erases at each erase-unit boundary.");
            PicoNandWriteOptions = new PicoCommandOptions(PicoNandWrite);
            PicoNandWriteInput = RequiredStringOption("--input", "The nonempty raw NAND data-and-spare input, covering complete erase units.", "file");
            PicoNandWriteStartBlock = new Option<uint>("--start-block")
            {
                Description = "The first 512-byte logical-data record to write, aligned to an erase-unit boundary.",
                HelpName = "record",
                Required = true,
            };
            PicoNandWriteYes = new Option<bool>("--yes")
            {
                Description = "Acknowledge that writing physically modifies the NAND.",
            };
            PicoNandWrite.Options.Add(PicoNandWriteInput);
            PicoNandWrite.Options.Add(PicoNandWriteStartBlock);
            PicoNandWrite.Options.Add(PicoNandWriteYes);

            PicoNandErase = new Command("nand-erase", "Erase complete NAND erase units.");
            PicoNandEraseOptions = new PicoCommandOptions(PicoNandErase);
            PicoNandEraseStartEraseBlock = new Option<uint>("--start-erase-block")
            {
                Description = "The first NAND erase-block index to erase.",
                HelpName = "index",
                Required = true,
            };
            PicoNandEraseEraseBlocks = new Option<uint>("--erase-blocks")
            {
                Description = "The positive number of complete NAND erase blocks to erase.",
                HelpName = "count",
                Required = true,
            };
            PicoNandEraseYes = new Option<bool>("--yes")
            {
                Description = "Acknowledge that erasing physically modifies the NAND.",
            };
            PicoNandErase.Options.Add(PicoNandEraseStartEraseBlock);
            PicoNandErase.Options.Add(PicoNandEraseEraseBlocks);
            PicoNandErase.Options.Add(PicoNandEraseYes);

            PicoEmmcProbe = new Command("emmc-probe", "Read eMMC metadata and capacity.");
            PicoEmmcProbeOptions = new PicoCommandOptions(PicoEmmcProbe);
            PicoEmmcRead = new Command("emmc-read", "Read raw 512-byte eMMC records to an atomically published file.");
            PicoEmmcReadOptions = new PicoCommandOptions(PicoEmmcRead);
            PicoEmmcReadOutput = RequiredStringOption("--output", "The raw eMMC output file to publish.", "file");
            PicoEmmcReadStartBlock = new Option<uint>("--start-block")
            {
                Description = "The first 512-byte eMMC record to read. Defaults to zero.",
                HelpName = "record",
            };
            PicoEmmcReadBlocks = new Option<uint>("--blocks")
            {
                Description = "The positive number of 512-byte eMMC records to read.",
                HelpName = "count",
                Required = true,
            };
            PicoEmmcReadForce = new Option<bool>("--force")
            {
                Description = "Replace an existing output file atomically.",
            };
            PicoEmmcRead.Options.Add(PicoEmmcReadOutput);
            PicoEmmcRead.Options.Add(PicoEmmcReadStartBlock);
            PicoEmmcRead.Options.Add(PicoEmmcReadBlocks);
            PicoEmmcRead.Options.Add(PicoEmmcReadForce);

            Pico.Subcommands.Add(PicoProbe);
            Pico.Subcommands.Add(PicoSmcStop);
            Pico.Subcommands.Add(PicoSmcStart);
            Pico.Subcommands.Add(PicoRebootBootloader);
            Pico.Subcommands.Add(PicoNandRead);
            Pico.Subcommands.Add(PicoNandWrite);
            Pico.Subcommands.Add(PicoNandErase);
            Pico.Subcommands.Add(PicoEmmcProbe);
            Pico.Subcommands.Add(PicoEmmcRead);

            XeBuild = new Command("xebuild", "Build validated XeBuild images with an explicit backend policy.");
            XeBuildBuild = new Command("build", "Build a XeBuild image from an inspected NAND input.");
            XeBuildBuildInput = RequiredStringOption("--input", "The NAND image to build from.", "nand");
            XeBuildBuildCpuKey = new CpuKeyCommandOptions(XeBuildBuild);
            XeBuildBuildOutput = RequiredStringOption("--output", "The output image to publish.", "file");
            XeBuildBuildConsole = new Option<string>("--console")
            {
                Description = "An optional canonical console name from 'jrunner console list'; otherwise use source inspection.",
                HelpName = "canonical-name",
                Arity = ArgumentArity.ExactlyOne,
            };
            XeBuildBuildDashboard = new Option<int>("--dashboard")
            {
                Description = "The positive dashboard version to build.",
                HelpName = "number",
                Required = true,
                Arity = ArgumentArity.ExactlyOne,
            };
            XeBuildBuildType = RequiredStringOption("--type", "The canonical XeBuild target name.", "canonical");
            XeBuildBuildPatches = new Option<string[]>("--patch")
            {
                Description = "A named patch; repeat this option to apply patches in the requested order.",
                HelpName = "name",
                Arity = ArgumentArity.OneOrMore,
                AllowMultipleArgumentsPerToken = false,
            };
            XeBuildBuildPatches.Validators.Add(static result =>
            {
                if (result.IdentifierTokenCount != result.Tokens.Count)
                {
                    result.AddError("Each --patch option requires exactly one patch name.");
                }
            });
            XeBuildBuildBigFfs = FlagOption("--bigffs", "Use the supported BigFFS board configuration.");
            XeBuildBuildRgh3 = FlagOption("--rgh3", "Run the supported RGH3 post-build conversion.");
            XeBuildBuildDashLaunch = FlagOption("--dashlaunch", "Stage and inject the compatible DashLaunch assets.");
            XeBuildBuildDrivePatch = new Option<string>("--drive-patch")
            {
                Description = "Apply an XL drive patch: usb, hdd, or both.",
                HelpName = "usb|hdd|both",
                Arity = ArgumentArity.ExactlyOne,
            };
            XeBuildBuildSystemPartitionOnly = FlagOption("--system-partition-only", "Stage only the system partition of a full 4 GB source.");
            XeBuildBuildFullFourGigabyteData = FlagOption("--full-4gb-data", "Stage all data from a full 4 GB source.");
            XeBuildBuildBackend = new Option<string>("--backend")
            {
                Description = "Select wine (default) or native (unavailable); never fall back to another backend.",
                HelpName = "wine|native",
                Arity = ArgumentArity.ExactlyOne,
            };
            XeBuildBuildKeepWorkspace = FlagOption("--keep-workspace", "Retain only non-secret workspace diagnostics after a failed build.");
            XeBuildBuildForce = FlagOption("--force", "Replace an existing output image atomically.");
            XeBuildBuild.Options.Add(XeBuildBuildInput);
            XeBuildBuild.Options.Add(XeBuildBuildOutput);
            XeBuildBuild.Options.Add(XeBuildBuildConsole);
            XeBuildBuild.Options.Add(XeBuildBuildDashboard);
            XeBuildBuild.Options.Add(XeBuildBuildType);
            XeBuildBuild.Options.Add(XeBuildBuildPatches);
            XeBuildBuild.Options.Add(XeBuildBuildBigFfs);
            XeBuildBuild.Options.Add(XeBuildBuildRgh3);
            XeBuildBuild.Options.Add(XeBuildBuildDashLaunch);
            XeBuildBuild.Options.Add(XeBuildBuildDrivePatch);
            XeBuildBuild.Options.Add(XeBuildBuildSystemPartitionOnly);
            XeBuildBuild.Options.Add(XeBuildBuildFullFourGigabyteData);
            XeBuildBuild.Options.Add(XeBuildBuildBackend);
            XeBuildBuild.Options.Add(XeBuildBuildKeepWorkspace);
            XeBuildBuild.Options.Add(XeBuildBuildForce);
            XeBuild.Subcommands.Add(XeBuildBuild);

            Support = new Command("support", "Inspect and install the XeBuild support payload.");
            SupportStatus = new Command("status", "Validate the active XeBuild support payload.");
            SupportInstall = new Command("install", "Install the pinned XeBuild support payload from a download or local archive.");
            SupportInstallArchive = new Option<string>("--archive")
            {
                Description = "Use a local archive matching the pinned support release instead of downloading it.",
                HelpName = "local-file",
                Arity = ArgumentArity.ExactlyOne,
            };
            SupportInstall.Options.Add(SupportInstallArchive);
            Support.Subcommands.Add(SupportStatus);
            Support.Subcommands.Add(SupportInstall);


            Root.Subcommands.Add(Nand);
            Root.Subcommands.Add(Patch);
            Root.Subcommands.Add(Console);
            Root.Subcommands.Add(Device);
            Root.Subcommands.Add(Pico);
            Root.Subcommands.Add(Support);
            Root.Subcommands.Add(XeBuild);
        }

        internal RootCommand Root { get; }

        internal Option<string> SupportRoot { get; }


        internal Command Nand { get; }


        internal Command NandInspect { get; }

        internal Option<string> NandInspectInput { get; }

        internal CpuKeyCommandOptions NandInspectCpuKey { get; }

        internal Command NandCompare { get; }

        internal Argument<string> NandCompareLeft { get; }

        internal Argument<string> NandCompareRight { get; }

        internal Command NandRgh3Convert { get; }

        internal Option<string> NandRgh3ConvertEcc { get; }

        internal Option<string> NandRgh3ConvertFlash { get; }

        internal CpuKeyCommandOptions NandRgh3ConvertCpuKey { get; }

        internal Option<string> NandRgh3ConvertOutput { get; }

        internal Option<bool> NandRgh3ConvertNoSmcPatch { get; }

        internal Option<bool> NandRgh3ConvertForce { get; }

        internal Command Patch { get; }

        internal Command PatchInspect { get; }

        internal Option<string> PatchInspectInput { get; }

        internal Command Console { get; }

        internal Command ConsoleList { get; }

        internal Command Device { get; }

        internal Command DeviceList { get; }

        internal Command Pico { get; }

        internal Command PicoProbe { get; }

        internal PicoCommandOptions PicoProbeOptions { get; }

        internal Command PicoSmcStop { get; }

        internal PicoCommandOptions PicoSmcStopOptions { get; }

        internal Command PicoSmcStart { get; }

        internal PicoCommandOptions PicoSmcStartOptions { get; }

        internal Command PicoRebootBootloader { get; }

        internal PicoCommandOptions PicoRebootBootloaderOptions { get; }

        internal Command PicoEmmcProbe { get; }

        internal PicoCommandOptions PicoEmmcProbeOptions { get; }

        internal Command PicoEmmcRead { get; }

        internal PicoCommandOptions PicoEmmcReadOptions { get; }

        internal Option<string> PicoEmmcReadOutput { get; }

        internal Option<uint> PicoEmmcReadStartBlock { get; }

        internal Option<uint> PicoEmmcReadBlocks { get; }

        internal Option<bool> PicoEmmcReadForce { get; }

        internal Command PicoNandRead { get; }

        internal PicoCommandOptions PicoNandReadOptions { get; }

        internal Option<string> PicoNandReadOutput { get; }

        internal Option<uint> PicoNandReadStartBlock { get; }

        internal Option<uint?> PicoNandReadBlocks { get; }

        internal Option<bool> PicoNandReadForce { get; }

        internal Command PicoNandWrite { get; }

        internal PicoCommandOptions PicoNandWriteOptions { get; }

        internal Option<string> PicoNandWriteInput { get; }

        internal Option<uint> PicoNandWriteStartBlock { get; }

        internal Option<bool> PicoNandWriteYes { get; }

        internal Command PicoNandErase { get; }

        internal PicoCommandOptions PicoNandEraseOptions { get; }

        internal Option<uint> PicoNandEraseStartEraseBlock { get; }

        internal Option<uint> PicoNandEraseEraseBlocks { get; }

        internal Option<bool> PicoNandEraseYes { get; }


        internal Command XeBuild { get; }

        internal Command XeBuildBuild { get; }

        internal Option<string> XeBuildBuildInput { get; }

        internal CpuKeyCommandOptions XeBuildBuildCpuKey { get; }

        internal Option<string> XeBuildBuildOutput { get; }

        internal Option<string> XeBuildBuildConsole { get; }

        internal Option<int> XeBuildBuildDashboard { get; }

        internal Option<string> XeBuildBuildType { get; }

        internal Option<string[]> XeBuildBuildPatches { get; }

        internal Option<bool> XeBuildBuildBigFfs { get; }

        internal Option<bool> XeBuildBuildRgh3 { get; }

        internal Option<bool> XeBuildBuildDashLaunch { get; }

        internal Option<string> XeBuildBuildDrivePatch { get; }

        internal Option<bool> XeBuildBuildSystemPartitionOnly { get; }

        internal Option<bool> XeBuildBuildFullFourGigabyteData { get; }

        internal Option<bool> XeBuildBuildForce { get; }

        internal Option<string> XeBuildBuildBackend { get; }

        internal Option<bool> XeBuildBuildKeepWorkspace { get; }

        internal Command Support { get; }

        internal Command SupportStatus { get; }

        internal Command SupportInstall { get; }

        internal Option<string> SupportInstallArchive { get; }

        private static Option<string> RequiredStringOption(string name, string description, string helpName)
        {
            return new Option<string>(name)
            {
                Description = description,
                HelpName = helpName,
                Required = true,
                Arity = ArgumentArity.ExactlyOne,
            };
        }


        private static Option<bool> FlagOption(string name, string description)
        {
            return new Option<bool>(name)
            {
                Description = description,
                Arity = ArgumentArity.Zero,
            };
        }

        private static Option<string> SupportRootOption()
        {
            var option = new Option<string>("--support-root")
            {
                Description = "Override the XeBuild support root (before environment and XDG defaults).",
                HelpName = "path",
                Arity = ArgumentArity.ExactlyOne,
                Recursive = true,
            };
            option.Validators.Add(static result =>
            {
                if (!IsValidPath(result.GetValueOrDefault<string>(), allowFileSystemRoot: false))
                {
                    result.AddError("The support root path is invalid.");
                }
            });
            return option;
        }
    }
}
