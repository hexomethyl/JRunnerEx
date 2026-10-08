using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using JRunner.Core.Configuration;
using JRunner.Core.Contracts;
using JRunner.Core.Nand.Conversion;
using JRunner.Core.Nand.Inspection;
using JRunner.Core.Nand.Security;
using JRunner.Core.Support;
using JRunner.Core.XeBuild;
using JRunner.Core.XeBuild.Algorithms;
using JRunner.Core.XeBuild.Preparation;

namespace JRunner.Cli.Infrastructure;

/// <summary>
/// Selects the Wine commands used by one XeBuild backend invocation.
/// </summary>
internal sealed record WineXeBuildToolchain(string WineExecutable, string WinePathExecutable)
{
    internal static WineXeBuildToolchain Default { get; } = new("wine", "winepath")
    {
        RequiresTrustedPathDiscovery = true,
    };

    internal bool RequiresTrustedPathDiscovery { get; private init; }

    internal string? TrustedChildPath { get; init; }

    internal ExternalProcessSupervisorLaunch? TrustedSupervisorLaunch { get; init; }

    internal NativeDependencyClosureSpec? NativeInputs { get; init; }
    internal bool UsesSyntheticNativeInputs { get; init; }

    internal NativeDependencyClosure? TrustedNativeClosure { get; init; }

    internal WineNativePrefixPolicy? TrustedPrefixPolicy { get; init; }
}


/// <summary>
/// Stages one fully validated immutable support generation in a private Wine workspace and publishes only a validated image.
/// </summary>
internal sealed class WineXeBuildBackend : IXeBuildBackend
{
    private const int BufferSize = 0x10000;
    private const string WinePrefixEnvironmentVariable = "WINEPREFIX";
    private const string XeBuildExecutableRelativePath = "xeBuild/xeBuild.exe";
    private const string XdkBuildExecutableRelativePath = "xeBuild/XDKbuild/XDKbuild.exe";
    private const string XdkBuildOptionsRelativePath = "xeBuild/options.ini";
    private const string WineEnterInput = "enter\n";

    private readonly SupportPayloadValidator _supportValidator;
    private readonly XeBuildSupportIndex _supportIndex;
    private readonly IExternalProcessRunner _processRunner;
    private readonly WineXeBuildToolchain _toolchain;
    private readonly WineXeBuildToolchainResolver _toolchainResolver;
    private readonly IProgress<OperationProgress>? _progress;

    internal WineXeBuildBackend(IProgress<OperationProgress>? progress = null)
        : this(ReadTrustedSupportManifest(), new ExternalProcessRunner(), progress: progress)
    {
    }

    internal WineXeBuildBackend(
        SupportManifest supportManifest,
        IExternalProcessRunner processRunner,
        WineXeBuildToolchain? toolchain = null,
        IProgress<OperationProgress>? progress = null,
        WineXeBuildToolchainResolver? toolchainResolver = null)
    {
        ArgumentNullException.ThrowIfNull(supportManifest);
        _supportValidator = new SupportPayloadValidator(supportManifest);
        _supportIndex = XeBuildSupportIndex.FromManifest(supportManifest);
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _toolchain = toolchain ?? WineXeBuildToolchain.Default;
        _toolchainResolver = toolchainResolver ?? new WineXeBuildToolchainResolver();
        _progress = progress;
        ValidateToolchain(_toolchain);
    }

    private static SupportManifest ReadTrustedSupportManifest()
    {
        try
        {
            return EmbeddedSupportManifest.Current;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or System.Text.Json.JsonException)
        {
            throw new OperationFailureException(
                ExitCode.MissingPrerequisite,
                "xebuild-support-manifest-invalid",
                "The embedded support manifest is invalid. Reinstall JRunnerEx to restore its verified manifest.");
        }
    }

    public XeBuildBackendKind Kind => XeBuildBackendKind.Wine;

    public async Task<XeBuildResult> BuildAsync(
        XeBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();
        XeBuildOutputEvidenceCatalog.EnsureAvailable(request.Target);
        SupportRoot supportRoot = new(request.SupportRootPath, SupportRootSource.Explicit);
        string activeGeneration = await ResolveValidatedActiveGenerationAsync(
            supportRoot,
            _progress,
            cancellationToken).ConfigureAwait(false);
        string generationDirectory = SupportPayloadLayout.GetGenerationDirectory(supportRoot, activeGeneration);
        WineXeBuildWorkspace? workspace = null;
        AtomicOutputReservation? outputReservation = null;
        DevGl64WorkspacePreparation? devGlPreparation = null;
        var diagnostics = new BuildDiagnostics();
        var outputValidated = false;
        var winePreflightFailed = false;
        long outputLength = 0;

        try
        {
            string workspaceRoot = WineWorkspaceRootResolver.Resolve(request.Execution.WorkspaceRootPath);
            AtomicOutputDestination outputDestination = AtomicOutputPath.Preflight(
                request.OutputPath,
                request.Execution.OverwriteExistingOutput);
            EnsureMutablePathOutsideSupportRoot(
                workspaceRoot,
                supportRoot,
                "xebuild-workspace-overlaps-support",
                "The XeBuild workspace root must not be inside the support payload root.");
            EnsureMutablePathOutsideSupportRoot(
                outputDestination.DestinationPath,
                supportRoot,
                "xebuild-output-overlaps-support",
                "The XeBuild output path must not be inside the support payload root.");

            XeBuildPreparedPlan plan = XeBuildPreparationService.Prepare(request, _supportIndex);
            outputReservation = AtomicOutputReservation.Create(outputDestination);
            workspace = await WineXeBuildWorkspace.CreateAsync(workspaceRoot, cancellationToken)
                .ConfigureAwait(false);
            _progress?.Report(new OperationProgress("staging-xebuild", "Staging the isolated XeBuild workspace."));
            await workspace.StageSupportAsync(generationDirectory, plan, cancellationToken).ConfigureAwait(false);
            await workspace.StageInputAsync(
                request.Source,
                plan.FourGigabyteStagingPolicy,
                cancellationToken).ConfigureAwait(false);
            // Even version-only Wine processes must follow source-snapshot verification.
            // Resolve both production commands before even a version-only invocation or key staging.
            // Probe winepath without running it: winepath --help can initialize a prefix.
            WineXeBuildToolchain toolchain;
            try
            {
                string? helperDirectory = CreateWineHelperDirectory(workspace.RootDirectory, _toolchain);
                toolchain = _toolchainResolver.Resolve(_toolchain, generationDirectory, helperDirectory);
                if (toolchain.TrustedChildPath is not null)
                {
                    // Even injected fake runners cannot turn production discovery into an unchecked
                    // supervisor launch. Bind the actual runner's chain before any CPU key is staged.
                    ExternalProcessSupervisorLaunch launch = _processRunner.PrepareTrustedSupervisorLaunch()
                        ?? throw ExternalProcessSupervisorLaunch.Unavailable();
                    launch.Revalidate();
                    WineNativePrefixPolicy prefixPolicy = WineNativePrefixPolicy.PrepareFresh(
                        workspace.WinePrefixDirectory, workspace.RootDirectory);
                    IReadOnlyDictionary<string, string> closedEnvironment = WineNativePrefixPolicy.CreateClosedEnvironment(
                        workspace.RootDirectory, prefixPolicy, toolchain.TrustedChildPath);
                    NativeDependencyClosureSpec nativeInputs = CombineNativeInputs(
                        toolchain, launch, prefixPolicy, closedEnvironment, workspace.RootDirectory, generationDirectory);
                    toolchain = toolchain with
                    {
                        TrustedSupervisorLaunch = launch,
                        NativeInputs = nativeInputs,
                        TrustedPrefixPolicy = prefixPolicy,
                        TrustedNativeClosure = NativeDependencyClosure.Prepare(nativeInputs),
                    };
                }
                await PreflightWineAsync(toolchain, generationDirectory, workspaceRoot,
                    workspace.WinePrefixDirectory, diagnostics, cancellationToken).ConfigureAwait(false);
                toolchain = await InitializeTrustedPrefixAsync(
                    toolchain, workspace, generationDirectory, diagnostics, cancellationToken).ConfigureAwait(false);
                RevalidateTrustedToolchain(toolchain);
            }
            catch
            {
                winePreflightFailed = true;
                throw;
            }
            await workspace.StageCpuKeyAsync(request.CpuKey, cancellationToken).ConfigureAwait(false);
            await VerifyStagedInputCpuKeyAsync(workspace, request.CpuKey, _progress, cancellationToken)
                .ConfigureAwait(false);
            if (plan.RequiresDevGl64Preparation)
            {
                devGlPreparation = await DevGl64WorkspacePreparation.ApplyAsync(
                    workspace.RootDirectory,
                    plan,
                    request.CpuKey,
                    cancellationToken).ConfigureAwait(false);
            }

            string xeBuildExecutable = RequireWorkspaceFile(workspace.RootDirectory, XeBuildExecutableRelativePath);
            outputReservation.EnsurePathSafe();
            string windowsOutput = await ToWinePathAsync(
                workspace,
                toolchain,
                outputReservation.TemporaryPath,
                cancellationToken).ConfigureAwait(false);
            IEnumerable<string> xeBuildArguments = plan.CreateArgumentTokens(windowsOutput).Prepend(xeBuildExecutable);
            _progress?.Report(new OperationProgress("running-xebuild", "Running XeBuild in the isolated Wine workspace."));
            outputReservation.EnsurePathSafe();
            await RunRequiredProcessAsync(
                new ExternalProcessInvocation(
                    toolchain.WineExecutable,
                    xeBuildArguments,
                    workspace.RootDirectory,
                    WineEnterInput,
                    CreateWineEnvironment(workspace.WinePrefixDirectory, toolchain))
                {
                    TrustedSupervisorLaunch = toolchain.TrustedSupervisorLaunch,
                    TrustedNativeClosure = toolchain.TrustedNativeClosure,
                },
                cancellationToken,
                diagnostics).ConfigureAwait(false);
            outputReservation.EnsurePathSafe();
            EnsureProducedOutput(outputReservation.TemporaryPath);
            try
            {
                await RunPostBuildOperationsAsync(
                    workspace,
                    outputReservation,
                    windowsOutput,
                    plan,
                    request.CpuKey,
                    toolchain,
                    _progress,
                    cancellationToken,
                    diagnostics).ConfigureAwait(false);
            }
            catch (OperationFailureException exception) when (exception.Code == ExitCode.InvalidData)
            {
                throw new OperationFailureException(
                    ExitCode.ExternalProcess,
                    "xebuild-postbuild-failed",
                    "The temporary XeBuild image failed a required post-build transformation.");
            }
            _progress?.Report(new OperationProgress("validating-xebuild-output", "Validating the completed XeBuild image."));
            outputLength = await outputReservation.ValidateAsync(
                (stream, token) => XeBuildOutputValidator.ValidateAsync(
                    stream,
                    request,
                    _progress,
                    token),
                cancellationToken).ConfigureAwait(false);
            outputValidated = true;
        }
        catch (OperationFailureException exception)
        {
            diagnostics.RecordFailure(exception.Kind, exception.Message);
            throw;
        }
        catch (OperationCanceledException)
        {
            diagnostics.RecordFailure("cancelled", "The XeBuild operation was cancelled after process cleanup.");
            throw;
        }
        finally
        {
            Exception? cleanupFailure = null;
            if (devGlPreparation is not null)
            {
                try
                {
                    await devGlPreparation.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    cleanupFailure = exception;
                }
            }

            if (outputReservation is not null)
            {
                try
                {
                    outputReservation.EnsurePathSafe();
                    await diagnostics.CaptureSidecarsAsync(outputReservation.TemporaryPath).ConfigureAwait(false);
                    DeleteOutputSidecarsRequired(outputReservation);
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                }
            }

            try
            {
                SupportStatusResult finalStatus = await _supportValidator.ValidateGenerationAsync(
                    supportRoot,
                    activeGeneration,
                    CancellationToken.None).ConfigureAwait(false);
                if (finalStatus.Status is not SupportStatusKind.Valid)
                {
                    throw new OperationFailureException(
                        ExitCode.MissingPrerequisite,
                        "xebuild-support-changed",
                        "The immutable support payload changed during the build. Run 'jrunner support install'.");
                }
            }
            catch (Exception exception)
            {
                cleanupFailure ??= exception;
            }

            if (workspace is not null)
            {
                bool retainDiagnostics = !winePreflightFailed &&
                    (!outputValidated || cleanupFailure is not null) &&
                    request.Execution.KeepWorkspace;
                try
                {
                    if (retainDiagnostics)
                    {
                        if (cleanupFailure is OperationFailureException failure)
                        {
                            diagnostics.RecordFailure(failure.Kind, failure.Message);
                        }
                        else if (cleanupFailure is not null)
                        {
                            diagnostics.RecordFailure("cleanup-failed", "The XeBuild operation failed during finalization.");
                        }

                        await diagnostics.WriteAsync(workspace.RootDirectory).ConfigureAwait(false);
                    }
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                    retainDiagnostics = false;
                }

                try
                {
                    await workspace.CleanupAsync(retainDiagnostics).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                }
            }

            if ((!outputValidated || cleanupFailure is not null) && outputReservation is not null)
            {
                try
                {
                    outputReservation.Dispose();
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                }
            }

            if (cleanupFailure is not null)
            {
                ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
            }
        }

        using (AtomicOutputReservation validatedOutput = outputReservation!)
        {
            await validatedOutput.CompleteAsync(cancellationToken).ConfigureAwait(false);
            return new XeBuildResult(validatedOutput.DestinationPath, outputLength, Kind);
        }
    }

    private static async Task VerifyStagedInputCpuKeyAsync(
        WineXeBuildWorkspace workspace,
        CpuKey cpuKey,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            await using FileStream input = new(
                workspace.StagedInputPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var inspection = await NandImageService.InspectAsync(
                    input,
                    cpuKey,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            if (inspection.Keyvault.Inspection.CpuKeyVerification == KeyvaultCpuKeyVerificationStatus.Failed)
            {
                throw InputCpuKeyFailure();
            }
        }
        catch (OperationFailureException exception) when (exception.Kind == "cpu-key-verification-failed")
        {
            throw InputCpuKeyFailure();
        }
    }

    private static OperationFailureException InputCpuKeyFailure()
    {
        return new OperationFailureException(
            ExitCode.InvalidData,
            "xebuild-cpu-key-verification-failed",
            "The supplied CPU key does not verify the XeBuild input keyvault.");
    }

    private async Task RunPostBuildOperationsAsync(
        WineXeBuildWorkspace workspace,
        AtomicOutputReservation outputReservation,
        string windowsOutputPath,
        XeBuildPreparedPlan plan,
        CpuKey physicalCpuKey,
        WineXeBuildToolchain toolchain,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken,
        BuildDiagnostics diagnostics)
    {
        string temporaryOutputPath = outputReservation.TemporaryPath;
        foreach (XeBuildPostBuildOperation operation in plan.PostBuildOperations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            outputReservation.EnsurePathSafe();
            switch (operation)
            {
                case XeBuildPostBuildOperation.RepairXeBuildImage:
                    await RepairImageAsync(outputReservation, progress, cancellationToken).ConfigureAwait(false);
                    break;

                case XeBuildPostBuildOperation.RunXdkBuild:
                    await RunXdkBuildAsync(
                        workspace,
                        temporaryOutputPath,
                        windowsOutputPath,
                        plan,
                        toolchain,
                        cancellationToken,
                        diagnostics).ConfigureAwait(false);
                    break;

                case XeBuildPostBuildOperation.ConvertRgh2ToRgh3WithZeroCpuKey:
                    await ConvertRgh2ToRgh3Async(
                        workspace,
                        outputReservation,
                        plan,
                        ZeroCpuKey(),
                        progress,
                        cancellationToken).ConfigureAwait(false);
                    break;

                case XeBuildPostBuildOperation.ConvertRgh2ToRgh3WithPhysicalCpuKey:
                    await ConvertRgh2ToRgh3Async(
                        workspace,
                        outputReservation,
                        plan,
                        physicalCpuKey,
                        progress,
                        cancellationToken).ConfigureAwait(false);
                    break;

                case XeBuildPostBuildOperation.ZeroPairDevkitSb:
                    await ZeroPairDevkitSbAsync(outputReservation, progress, cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    throw new OperationFailureException(
                        ExitCode.InvalidData,
                        "xebuild-postbuild-unsupported",
                        "The prepared XeBuild plan contains an unsupported post-build operation.");
            }

            EnsureProducedOutput(temporaryOutputPath);
        }
    }

    private async Task PreflightWineAsync(
        WineXeBuildToolchain toolchain,
        string workingDirectory,
        string workspaceRoot,
        string winePrefixDirectory,
        BuildDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        // Wine's version-only query does not use the prefix. Explicitly override an inherited
        // WINEPREFIX nevertheless, so no invocation can implicitly target the caller's ~/.wine.
        IReadOnlyList<KeyValuePair<string, string?>> environment = CreateWineEnvironment(
            toolchain.TrustedChildPath is null
                ? Path.Combine(workspaceRoot, $".preflight-{Guid.NewGuid():N}", "wine-prefix")
                : winePrefixDirectory,
            toolchain);
        try
        {
            ExternalProcessResult versionResult = await RunRequiredProcessAsync(
                new ExternalProcessInvocation(toolchain.WineExecutable, ["--version"], workingDirectory, environment)
                {
                    TrustedSupervisorLaunch = toolchain.TrustedSupervisorLaunch,
                    TrustedNativeClosure = toolchain.TrustedNativeClosure,
                },
                cancellationToken,
                diagnostics).ConfigureAwait(false);
            string version = versionResult.StandardOutput.Trim();
            if (versionResult.StandardOutputTruncated ||
                !version.StartsWith("wine-", StringComparison.Ordinal) ||
                version.Length <= 5 ||
                version[5] is < '0' or > '9' ||
                version.Contains('\n') ||
                version.Contains('\r'))
            {
                throw new OperationFailureException(
                    ExitCode.ExternalProcess,
                    "wine-version-invalid",
                    "The Wine version query did not identify an available Wine runtime.");
            }
        }
        catch (OperationFailureException exception) when (exception.Code == ExitCode.ExternalProcess)
        {
            throw new OperationFailureException(
                ExitCode.MissingPrerequisite,
                "wine-unavailable",
                "Wine is required to run XeBuild but is not available. Install Wine and winepath.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var probe = new ExternalProcessInvocation(
                toolchain.WinePathExecutable, [], workingDirectory, environment)
            {
                TrustedSupervisorLaunch = toolchain.TrustedSupervisorLaunch,
                TrustedNativeClosure = toolchain.TrustedNativeClosure,
            };
            RevalidateTrustedInvocation(probe);
            _processRunner.EnsureExecutableAvailable(probe);
        }
        catch (ExternalProcessFailureException)
        {
            throw new OperationFailureException(
                ExitCode.MissingPrerequisite,
                "winepath-unavailable",
                "Wine path conversion is required to run XeBuild but is not available. Install winepath.");
        }
    }

    private async Task<string> ToWinePathAsync(
        WineXeBuildWorkspace workspace,
        WineXeBuildToolchain toolchain,
        string hostPath,
        CancellationToken cancellationToken)
    {
        try
        {
            ExternalProcessResult result = await RunRequiredProcessAsync(
                new ExternalProcessInvocation(
                    toolchain.WinePathExecutable,
                    ["-w", hostPath],
                    workspace.RootDirectory,
                    CreateWineEnvironment(workspace.WinePrefixDirectory, toolchain))
                {
                    TrustedSupervisorLaunch = toolchain.TrustedSupervisorLaunch,
                    TrustedNativeClosure = toolchain.TrustedNativeClosure,
                },
                cancellationToken).ConfigureAwait(false);
            string windowsPath = result.StandardOutput.Trim();
            if (string.IsNullOrWhiteSpace(windowsPath) ||
                windowsPath.Contains('\n') ||
                windowsPath.Contains('\r'))
            {
                throw WinePathFailure();
            }

            return windowsPath;
        }
        catch (OperationFailureException exception) when (
            exception.Kind is "external-process-launch-failed" or
                "external-process-failed" or
                "external-process-execution-failed")
        {
            throw new OperationFailureException(
                ExitCode.MissingPrerequisite,
                "winepath-unavailable",
                "Wine path conversion is required to run XeBuild but is not available.");
        }
    }

    private async Task RunXdkBuildAsync(
        WineXeBuildWorkspace workspace,
        string temporaryOutputPath,
        string windowsOutputPath,
        XeBuildPreparedPlan plan,
        WineXeBuildToolchain toolchain,
        CancellationToken cancellationToken,
        BuildDiagnostics diagnostics)
    {
        string key = await ReadXdkBuildKeyAsync(workspace.RootDirectory, cancellationToken).ConfigureAwait(false);
        string xdkExecutable = RequireWorkspaceFile(workspace.RootDirectory, XdkBuildExecutableRelativePath);
        string xdkWorkingDirectory = Path.GetDirectoryName(xdkExecutable)
            ?? throw XdkFailure();
        string templatePath = RequireWorkspaceFile(workspace.RootDirectory, plan.XdkBuildTemplatePath ?? string.Empty);
        string templateName = Path.GetFileName(templatePath);
        if (string.IsNullOrWhiteSpace(templateName))
        {
            throw XdkFailure();
        }

        string windowsExecutable = await ToWinePathAsync(
            workspace,
            toolchain,
            xdkExecutable,
            cancellationToken).ConfigureAwait(false);
        await RunRequiredProcessAsync(
            new ExternalProcessInvocation(
                toolchain.WineExecutable,
                [windowsExecutable, windowsOutputPath, key, templateName],
                xdkWorkingDirectory,
                WineEnterInput,
                CreateWineEnvironment(workspace.WinePrefixDirectory, toolchain))
            {
                TrustedSupervisorLaunch = toolchain.TrustedSupervisorLaunch,
                TrustedNativeClosure = toolchain.TrustedNativeClosure,
            },
            cancellationToken,
            diagnostics).ConfigureAwait(false);
        EnsureProducedOutput(temporaryOutputPath);
    }

    private static async Task RepairImageAsync(
        AtomicOutputReservation outputReservation,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using FileStream output = outputReservation.OpenOutput(FileAccess.ReadWrite);
        await XeBuildImageRepairService.RepairAsync(output, progress, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ZeroPairDevkitSbAsync(
        AtomicOutputReservation outputReservation,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using FileStream output = outputReservation.OpenOutput(FileAccess.ReadWrite);
        await DevkitSbZeroPairService.ZeroPairAsync(output, progress, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ConvertRgh2ToRgh3Async(
        WineXeBuildWorkspace workspace,
        AtomicOutputReservation outputReservation,
        XeBuildPreparedPlan plan,
        CpuKey cpuKey,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        string templatePath = RequireWorkspaceFile(workspace.RootDirectory, plan.Rgh3TemplatePath ?? string.Empty);
        try
        {
            await using var converted = AtomicOutputFile.Create(outputReservation.TemporaryPath, force: true);
            await using (FileStream template = new(
                             templatePath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             BufferSize,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (FileStream source = outputReservation.OpenOutput(FileAccess.Read))
            {
                await Rgh2ToRgh3ConversionService.ConvertAsync(
                    new Rgh2ToRgh3ConversionRequest(template, source, converted.Stream, cpuKey),
                    progress,
                    cancellationToken).ConfigureAwait(false);
            }

            await converted.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (OperationFailureException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new OperationFailureException(
                ExitCode.InputOutput,
                "xebuild-rgh3-replace-failed",
                "The RGH3 conversion result could not replace the temporary XeBuild output.");
        }
    }

    private static async Task<string> ReadXdkBuildKeyAsync(string workspaceRoot, CancellationToken cancellationToken)
    {
        string optionsPath = RequireWorkspaceFile(workspaceRoot, XdkBuildOptionsRelativePath);
        string text;
        try
        {
            text = await File.ReadAllTextAsync(optionsPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw XdkFailure();
        }

        IniParseResult parsed = IniParser.Parse(text, cancellationToken);
        if (parsed.HasErrors ||
            !parsed.Document.TryGetValue(sectionLabel: null, "1blkey", out string? key) ||
            !IsExactHexadecimalKey(key))
        {
            throw XdkFailure();
        }

        return key;
    }

    private static bool IsExactHexadecimalKey(string? value)
    {
        if (value is null || value.Length != CpuKey.HexadecimalLength)
        {
            return false;
        }

        foreach (char character in value)
        {
            if ((character is < '0' or > '9') &&
                (character is < 'a' or > 'f') &&
                (character is < 'A' or > 'F'))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<string> ResolveValidatedActiveGenerationAsync(
        SupportRoot supportRoot,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        SupportStatusResult status = await _supportValidator.ValidateAsync(
            supportRoot,
            progress,
            cancellationToken).ConfigureAwait(false);
        if (status.Status != SupportStatusKind.Valid || string.IsNullOrWhiteSpace(status.GenerationId))
        {
            throw new OperationFailureException(
                ExitCode.MissingPrerequisite,
                "xebuild-support-unavailable",
                "A valid active support payload is required for XeBuild. Run 'jrunner support install'.");
        }

        return status.GenerationId;
    }

    private static void EnsureMutablePathOutsideSupportRoot(
        string mutablePath,
        SupportRoot supportRoot,
        string kind,
        string message)
    {
        string normalizedSupportRoot = WorkspacePathSafety.NormalizeDirectoryPath(
            supportRoot.DirectoryPath,
            nameof(supportRoot));
        string relativePath = Path.GetRelativePath(normalizedSupportRoot, mutablePath);
        bool isSupportRootOrDescendant = relativePath is "." ||
            (!string.Equals(relativePath, "..", StringComparison.Ordinal) &&
             !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
             !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal) &&
             !Path.IsPathRooted(relativePath));
        if (isSupportRootOrDescendant)
        {
            throw new OperationFailureException(ExitCode.Usage, kind, message);
        }
    }

    private async Task<ExternalProcessResult> RunRequiredProcessAsync(
        ExternalProcessInvocation invocation,
        CancellationToken cancellationToken,
        BuildDiagnostics? diagnostics = null)
    {
        try
        {
            RevalidateTrustedInvocation(invocation);
            ExternalProcessResult result = await _processRunner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
            diagnostics?.Record(result);
            if (!result.Succeeded)
            {
                throw result.ToFailure().ToOperationFailureException();
            }

            return result;
        }
        catch (ExternalProcessFailureException exception)
        {
            throw exception.Failure.ToOperationFailureException();
        }
    }

    private static NativeDependencyClosureSpec CombineNativeInputs(
        WineXeBuildToolchain toolchain,
        ExternalProcessSupervisorLaunch launch,
        WineNativePrefixPolicy prefixPolicy,
        IReadOnlyDictionary<string, string> environment,
        string workspaceDirectory,
        string generationDirectory)
    {
        NativeDependencyClosureSpec wine = toolchain.NativeInputs ?? throw NativeElfReader.Failure();
        NativeDependencyClosureSpec supervisor = launch.NativeInputs;
        return new NativeDependencyClosureSpec(
            wine.ExecutablePaths.Concat(supervisor.ExecutablePaths).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            wine.ModuleDirectories.Concat(supervisor.ModuleDirectories).Concat(prefixPolicy.ModuleDirectories)
                .Append(workspaceDirectory).Append(generationDirectory)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            wine.ConfigurationFiles.Concat(supervisor.ConfigurationFiles).Concat(prefixPolicy.ConfigurationFiles)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            environment)
        {
            PrefixPolicy = prefixPolicy,
        };
    }

    private async Task<WineXeBuildToolchain> InitializeTrustedPrefixAsync(
        WineXeBuildToolchain toolchain,
        WineXeBuildWorkspace workspace,
        string generationDirectory,
        BuildDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        if (toolchain.TrustedChildPath is null)
        {
            return toolchain;
        }
        WineNativePrefixPolicy initialPrefix = toolchain.TrustedPrefixPolicy ?? throw NativeElfReader.Failure();
        if (!initialPrefix.NeedsInitialization)
        {
            throw WineNativePrefixPolicy.Unsupported();
        }
        // Initialization is a fixed, protected pre-key operation, not executable discovery.
        await RunRequiredProcessAsync(new ExternalProcessInvocation(
            toolchain.WineExecutable, ["wineboot.exe", "-i", "-u"], workspace.RootDirectory,
            CreateWineEnvironment(workspace.WinePrefixDirectory, toolchain))
        {
            TrustedSupervisorLaunch = toolchain.TrustedSupervisorLaunch,
            TrustedNativeClosure = toolchain.TrustedNativeClosure,
        }, cancellationToken, diagnostics).ConfigureAwait(false);
        WineNativePrefixPolicy prefixPolicy = WineNativePrefixPolicy.PrepareAfterInitialization(
            initialPrefix);
        ExternalProcessSupervisorLaunch launch = toolchain.TrustedSupervisorLaunch ?? throw NativeElfReader.Failure();
        NativeDependencyClosureSpec initialInputs = toolchain.NativeInputs ?? throw NativeElfReader.Failure();
        NativeDependencyClosureSpec finalInputs = CombineNativeInputs(
            toolchain, launch, prefixPolicy, initialInputs.EnvironmentBindings, workspace.RootDirectory, generationDirectory);
        NativeDependencyClosure closure = NativeDependencyClosure.PrepareExtension(
            toolchain.TrustedNativeClosure ?? throw NativeElfReader.Failure(), finalInputs);
        return toolchain with
        {
            NativeInputs = finalInputs,
            TrustedPrefixPolicy = prefixPolicy,
            TrustedNativeClosure = closure,
        };
    }

    private static void RevalidateTrustedToolchain(WineXeBuildToolchain toolchain)
    {
        if (toolchain.TrustedChildPath is null)
        {
            return;
        }
        toolchain.TrustedPrefixPolicy?.Revalidate();
        ExternalProcessSupervisorLaunch launch = toolchain.TrustedSupervisorLaunch ?? throw NativeElfReader.Failure();
        NativeDependencyClosure closure = toolchain.TrustedNativeClosure ?? throw NativeElfReader.Failure();
        launch.Revalidate();
        closure.Revalidate();
        closure.ValidateExecutable(toolchain.WineExecutable);
        closure.ValidateExecutable(toolchain.WinePathExecutable);
    }

    private static void RevalidateTrustedInvocation(ExternalProcessInvocation invocation)
    {
        if (invocation.TrustedSupervisorLaunch is null && invocation.TrustedNativeClosure is null)
        {
            return;
        }
        ExternalProcessSupervisorLaunch launch = invocation.TrustedSupervisorLaunch ?? throw NativeElfReader.Failure();
        NativeDependencyClosure closure = invocation.TrustedNativeClosure ?? throw NativeElfReader.Failure();
        launch.Revalidate();
        closure.Revalidate();
        closure.ValidateExecutable(invocation.ExecutablePath);
    }

    private static string? CreateWineHelperDirectory(string workspaceRoot, WineXeBuildToolchain toolchain)
    {
        if (!toolchain.RequiresTrustedPathDiscovery)
        {
            return null;
        }
        if (!OperatingSystem.IsLinux())
        {
            throw ExternalProcessSupervisorLaunch.Unavailable();
        }
        try
        {
            string directory = Path.Join(workspaceRoot, "wine-toolchain");
            Directory.CreateDirectory(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return directory;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw ExternalProcessSupervisorLaunch.Unavailable();
        }
    }

    private static IReadOnlyList<KeyValuePair<string, string?>> CreateWineEnvironment(
        string winePrefixDirectory,
        WineXeBuildToolchain toolchain)
    {
        if (toolchain.TrustedChildPath is not string trustedPath)
        {
            // Explicit synthetic test toolchains do not launch a production Wine runtime.
            return [new KeyValuePair<string, string?>(WinePrefixEnvironmentVariable, winePrefixDirectory)];
        }

        NativeDependencyClosureSpec inputs = toolchain.NativeInputs ?? throw NativeElfReader.Failure();
        if (!inputs.EnvironmentBindings.TryGetValue(WinePrefixEnvironmentVariable, out string? boundPrefix) ||
            boundPrefix != winePrefixDirectory || inputs.EnvironmentBindings["PATH"] != trustedPath)
        {
            throw NativeElfReader.Failure();
        }
        var updates = new List<KeyValuePair<string, string?>>(inputs.EnvironmentBindings.Count + 13);
        foreach ((string name, string value) in inputs.EnvironmentBindings)
        {
            updates.Add(new(name, value));
        }
        foreach (string name in new[]
        {
            "WINESERVER", "WINELOADER", "WINELOADERNOEXEC", "WINEDLLPATH", "WINESYSTEMDLLPATH",
            "WINEDATADIR", "WINEHOMEDIR", "WINEBUILDDIR", "WINEARCH", "WINEUSERNAME", "USERNAME", "LOGNAME",
            "BASH_ENV", "ENV", "LD_PRELOAD", "LD_LIBRARY_PATH", "LD_AUDIT", "GLIBC_TUNABLES",
        })
        {
            updates.Add(new(name, null));
        }
        return updates;
    }

    private static string RequireWorkspaceFile(string workspaceRoot, string slashSeparatedRelativePath)
    {
        if (string.IsNullOrWhiteSpace(slashSeparatedRelativePath))
        {
            throw new OperationFailureException(
                ExitCode.InvalidData,
                "xebuild-support-missing",
                "The staged XeBuild workspace is missing a required file.");
        }

        string path = WorkspacePathSafety.ResolveDescendant(workspaceRoot, slashSeparatedRelativePath);
        if (!File.Exists(path))
        {
            throw new OperationFailureException(
                ExitCode.InvalidData,
                "xebuild-support-missing",
                "The staged XeBuild workspace is missing a required file.");
        }

        WorkspacePathSafety.EnsureNotLink(path);
        return path;
    }

    private static void EnsureProducedOutput(string path)
    {
        try
        {
            FileInfo output = new(path);
            if (output.LinkTarget is not null)
            {
                throw new OperationFailureException(
                    ExitCode.ExternalProcess,
                    "external-output-invalid",
                    "The external process output must be a regular private file, not a link.");
            }

            if (!output.Exists)
            {
                throw new OperationFailureException(
                    ExitCode.ExternalProcess,
                    "external-output-missing",
                    "The external process did not produce an output file.");
            }

            if (output.Length == 0)
            {
                throw new OperationFailureException(
                    ExitCode.ExternalProcess,
                    "external-output-empty",
                    "The external process produced an empty output file.");
            }
        }
        catch (OperationFailureException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new OperationFailureException(
                ExitCode.ExternalProcess,
                "external-output-unavailable",
                "The external process output could not be inspected.");
        }
    }

    private static CpuKey ZeroCpuKey()
    {
        return CpuKey.Parse("00000000000000000000000000000000");
    }

    private static void ValidateRequest(XeBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Execution.Backend is not XeBuildBackendKind.Wine)
        {
            throw new OperationFailureException(
                ExitCode.MissingPrerequisite,
                "xebuild-backend-unavailable",
                "The native XeBuild backend is not available. Select the Wine backend explicitly.");
        }
    }

    private static void ValidateToolchain(WineXeBuildToolchain toolchain)
    {
        ArgumentNullException.ThrowIfNull(toolchain);
        if (string.IsNullOrWhiteSpace(toolchain.WineExecutable) ||
            string.IsNullOrWhiteSpace(toolchain.WinePathExecutable))
        {
            throw new OperationFailureException(
                ExitCode.InvalidData,
                "wine-toolchain-invalid",
                "The configured Wine toolchain is invalid.");
        }
    }

    private static OperationFailureException WinePathFailure()
    {
        return new OperationFailureException(
            ExitCode.ExternalProcess,
            "winepath-invalid-output",
            "Wine path conversion returned an invalid path.");
    }

    private static OperationFailureException XdkFailure()
    {
        return new OperationFailureException(
            ExitCode.InvalidData,
            "xdkbuild-options-invalid",
            "The staged XeBuild options do not contain a valid XDKBuild 1BL key.");
    }

    private static IEnumerable<string> EnumerateOutputSidecars(string temporaryOutputPath)
    {
        string directory = Path.GetDirectoryName(temporaryOutputPath)!;
        string temporaryName = Path.GetFileName(temporaryOutputPath);
        string basename = Path.GetFileNameWithoutExtension(temporaryOutputPath);
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            string name = Path.GetFileName(path);
            if (!string.Equals(path, temporaryOutputPath, StringComparison.Ordinal) &&
                (name.StartsWith(basename, StringComparison.Ordinal) ||
                 name.StartsWith(string.Concat(".", temporaryName), StringComparison.Ordinal)))
            {
                yield return path;
            }
        }
    }

    private static void DeleteOutputSidecarsRequired(AtomicOutputReservation outputReservation)
    {
        OperationFailureException? cleanupFailure = null;
        foreach (string path in EnumerateOutputSidecars(outputReservation.TemporaryPath))
        {
            try
            {
                outputReservation.DeleteSidecarFile(Path.GetFileName(path));
            }
            catch (OperationFailureException exception)
            {
                cleanupFailure ??= exception;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                cleanupFailure ??= new OperationFailureException(
                    ExitCode.InputOutput,
                    "xebuild-sidecar-cleanup-failed",
                    "The temporary XeBuild output sidecar could not be removed.");
            }
        }

        if (cleanupFailure is not null)
        {
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }
    }

    private sealed class BuildDiagnostics
    {
        private const int MaximumDiagnosticCharacters = 16 * 1024;
        private const int MaximumSidecarBytes = 64 * 1024;
        private static readonly Regex KeyText = new(
            "(?:[0-9a-fA-F]{2}[:\\-\\s]*){16}|(?:[0-9a-fA-F]{2}[:\\-\\s]+){3,}[0-9a-fA-F]{2}|[0-9a-fA-F]{8,}",
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        private static readonly Regex TruncatedKeyTail = new(
            "[0-9a-fA-F][0-9a-fA-F:\\-\\s]*\\z",
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        private readonly StringBuilder _text = new();

        internal void Record(ExternalProcessResult result)
        {
            Append(string.Concat("process exit: ", result.ExitCode, "\n"));
            Append(RemoveTruncatedKeyTail(result.StandardOutput, result.StandardOutputTruncated));
            Append(RemoveTruncatedKeyTail(result.StandardError, result.StandardErrorTruncated));
            if (result.StandardOutputTruncated || result.StandardErrorTruncated)
            {
                Append("\n[process diagnostics truncated]\n");
            }
        }

        internal void RecordFailure(string kind, string message)
        {
            string failure = KeyText.Replace(string.Concat("[", kind, "] ", message, "\n"), "[redacted]");
            int count = Math.Min(failure.Length, MaximumDiagnosticCharacters);
            if (_text.Length > MaximumDiagnosticCharacters - count)
            {
                _text.Length = MaximumDiagnosticCharacters - count;
                TrimRetainedKeyTail();
            }

            _text.Insert(0, failure.AsSpan(0, count));
        }

        internal async Task CaptureSidecarsAsync(string temporaryOutputPath)
        {
            byte[]? buffer = null;
            foreach (string path in EnumerateOutputSidecars(temporaryOutputPath))
            {
                if (_text.Length >= MaximumDiagnosticCharacters)
                {
                    break;
                }

                try
                {
                    FileInfo file = new(path);
                    if (!file.Exists || file.LinkTarget is not null || file.Length <= 0)
                    {
                        continue;
                    }

                    buffer ??= new byte[MaximumSidecarBytes];
                    await using FileStream stream = new(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        BufferSize,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    int captured = 0;
                    while (captured < buffer.Length)
                    {
                        int read = await stream.ReadAsync(buffer.AsMemory(captured), CancellationToken.None)
                            .ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        captured += read;
                    }

                    Append("\n[temporary output diagnostics]\n");
                    Append(RemoveTruncatedKeyTail(
                        Encoding.UTF8.GetString(buffer, 0, captured),
                        truncated: stream.Length > captured));
                    if (stream.Length > captured)
                    {
                        Append("\n[sidecar diagnostics truncated]\n");
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Append("\n[temporary diagnostics unreadable]\n");
                }
            }
        }

        internal async Task WriteAsync(string workspaceRoot)
        {
            string path = WorkspacePathSafety.ResolveDescendant(workspaceRoot, "diagnostics.txt");
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            try
            {
                await using FileStream stream = new(path, options);
                // The final pass also covers a key split across stdout/stderr or capture chunks.
                byte[] bytes = Encoding.UTF8.GetBytes(KeyText.Replace(_text.ToString(), "[redacted]"));
                await stream.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(false);
                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new OperationFailureException(
                    ExitCode.InputOutput,
                    "xebuild-diagnostics-write-failed",
                    "The sanitized XeBuild diagnostics could not be retained.");
            }
        }

        private void Append(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            if (_text.Length >= MaximumDiagnosticCharacters)
            {
                TrimRetainedKeyTail();
                return;
            }

            // Redact before applying the retention bound, so truncation cannot expose half
            // of a key that appeared near the end of a captured diagnostic.
            string redacted = KeyText.Replace(text, "[redacted]");
            int count = Math.Min(redacted.Length, MaximumDiagnosticCharacters - _text.Length);
            if (count < redacted.Length)
            {
                TrimRetainedKeyTail();
                _text.Append(RemoveTruncatedKeyTail(redacted[..count], truncated: true));
            }
            else
            {
                _text.Append(redacted);
            }
        }

        private void TrimRetainedKeyTail()
        {
            string retained = RemoveTruncatedKeyTail(_text.ToString(), truncated: true);
            _text.Length = retained.Length;
        }

        private static string RemoveTruncatedKeyTail(string text, bool truncated)
        {
            return truncated ? TruncatedKeyTail.Replace(text, string.Empty) : text;
        }
    }
}
