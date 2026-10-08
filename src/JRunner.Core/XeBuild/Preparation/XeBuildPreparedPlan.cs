using System.Collections.Immutable;
using JRunner.Core.Nand.Models;
using JRunner.Core.XeBuild;

namespace JRunner.Core.XeBuild.Preparation;

/// <summary>
/// Immutable, secret-free XeBuild invocation plan consumed by a later workspace and backend executor.
/// </summary>
public sealed record XeBuildPreparedPlan
{
    /// <summary>
    /// Gets the verified support subtree used as the base for an isolated XeBuild workspace.
    /// </summary>
    public const string WorkspaceSupportTreeRoot = "xeBuild";

    internal XeBuildPreparedPlan(
        ConsoleDefinition console,
        ConsoleDefinition? sourceDetectedConsole,
        bool consoleWasExplicitlyOverridden,
        int dashboardVersion,
        XeBuildHackType requestedHackType,
        XeBuildHackType effectiveHackType,
        string configurationName,
        string requestedModeIniPath,
        string buildModeIniPath,
        string requiredIniLabel,
        bool requiresFlashIni,
        bool usesXdkBuildConfiguration,
        bool requiresDevGl64Preparation,
        bool disablesSmcResetPatching,
        XeBuildFourGigabyteStagingPolicy fourGigabyteStagingPolicy,
        string? rgh3TemplatePath,
        string? xdkBuildTemplatePath,
        XeBuildDashLaunchPlan? dashLaunch,
        IEnumerable<XeBuildWorkspaceOverlay> workspaceOverlays,
        IEnumerable<XeBuildPostBuildOperation> postBuildOperations,
        IEnumerable<string> requiredSupportFiles,
        IEnumerable<string> requiredWorkspaceDirectories,
        IEnumerable<string> fixedArguments)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedModeIniPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(buildModeIniPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredIniLabel);
        ArgumentNullException.ThrowIfNull(workspaceOverlays);
        ArgumentNullException.ThrowIfNull(postBuildOperations);
        ArgumentNullException.ThrowIfNull(requiredSupportFiles);
        ArgumentNullException.ThrowIfNull(requiredWorkspaceDirectories);
        ArgumentNullException.ThrowIfNull(fixedArguments);
        if (dashboardVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dashboardVersion),
                dashboardVersion,
                "The dashboard version must be positive.");
        }

        if (!Enum.IsDefined(requestedHackType) || !Enum.IsDefined(effectiveHackType))
        {
            throw new ArgumentOutOfRangeException(nameof(requestedHackType), "The XeBuild hack type is not supported.");
        }

        if (!Enum.IsDefined(fourGigabyteStagingPolicy))
        {
            throw new ArgumentOutOfRangeException(
                nameof(fourGigabyteStagingPolicy),
                fourGigabyteStagingPolicy,
                "The 4 GB staging policy is not supported.");
        }

        ImmutableArray<XeBuildWorkspaceOverlay> overlays = workspaceOverlays.ToImmutableArray();
        ImmutableArray<XeBuildPostBuildOperation> operations = postBuildOperations.ToImmutableArray();
        ImmutableArray<string> supportFiles = requiredSupportFiles.ToImmutableArray();
        ImmutableArray<string> workspaceDirectories = requiredWorkspaceDirectories.ToImmutableArray();
        ImmutableArray<string> arguments = fixedArguments.ToImmutableArray();
        if (overlays.IsDefaultOrEmpty)
        {
            throw new ArgumentException("At least one workspace overlay is required.", nameof(workspaceOverlays));
        }

        if (overlays.Any(overlay => overlay is null) ||
            overlays.Select(overlay => overlay.DestinationWorkspacePath).Distinct(StringComparer.Ordinal).Count() != overlays.Length)
        {
            throw new ArgumentException("Workspace overlays must be non-null and have unique destinations.", nameof(workspaceOverlays));
        }

        if (operations.Any(operation => !Enum.IsDefined(operation)))
        {
            throw new ArgumentException("Post-build operations must be supported.", nameof(postBuildOperations));
        }

        if (supportFiles.IsDefaultOrEmpty ||
            supportFiles.Any(file => !IsNormalizedSupportPath(file)) ||
            supportFiles.Distinct(StringComparer.Ordinal).Count() != supportFiles.Length)
        {
            throw new ArgumentException(
                "Required support files must be unique normalized support-relative paths.",
                nameof(requiredSupportFiles));
        }

        if (workspaceDirectories.IsDefaultOrEmpty ||
            workspaceDirectories.Any(directory => !IsNormalizedWorkspaceDirectory(directory)) ||
            workspaceDirectories.Distinct(StringComparer.Ordinal).Count() != workspaceDirectories.Length ||
            !workspaceDirectories.Contains("xeBuild/data", StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "Required workspace directories must include the normalized mutable xeBuild/data directory.",
                nameof(requiredWorkspaceDirectories));
        }

        if (arguments.IsDefaultOrEmpty || arguments.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("At least one non-empty fixed argument is required.", nameof(fixedArguments));
        }

        bool requiresRgh3 = operations.Any(operation => operation is
            XeBuildPostBuildOperation.ConvertRgh2ToRgh3WithZeroCpuKey or
            XeBuildPostBuildOperation.ConvertRgh2ToRgh3WithPhysicalCpuKey);
        if (requiresRgh3 != !string.IsNullOrWhiteSpace(rgh3TemplatePath))
        {
            throw new ArgumentException("An RGH3 template is required exactly when the plan converts the output to RGH3.");
        }

        bool requiresXdkBuild = operations.Contains(XeBuildPostBuildOperation.RunXdkBuild);
        if (requiresXdkBuild != !string.IsNullOrWhiteSpace(xdkBuildTemplatePath))
        {
            throw new ArgumentException("An XDKBuild template is required exactly when the plan runs XDKBuild.");
        }

        bool usesDistinctModeInis = !string.Equals(requestedModeIniPath, buildModeIniPath, StringComparison.Ordinal);
        if (usesDistinctModeInis != (requiresDevGl64Preparation || requiresFlashIni))
        {
            throw new ArgumentException(
                "Only DevGL64 preparation and flash-qualified XDKBuild flows may use distinct requested and build INI files.");
        }

        if (dashLaunch is not null)
        {
            if (requestedHackType is XeBuildHackType.Retail)
            {
                throw new ArgumentException("DashLaunch cannot be selected for a retail target.", nameof(dashLaunch));
            }

            if (dashLaunch.DashboardVersion != dashboardVersion ||
                dashLaunch.RequiredSupportFiles.Any(file => !supportFiles.Contains(file, StringComparer.Ordinal)))
            {
                throw new ArgumentException(
                    "DashLaunch assets must belong to the selected dashboard and dependency closure.",
                    nameof(dashLaunch));
            }
        }

        Console = console;
        SourceDetectedConsole = sourceDetectedConsole;
        ConsoleWasExplicitlyOverridden = consoleWasExplicitlyOverridden;
        DashboardVersion = dashboardVersion;
        RequestedHackType = requestedHackType;
        EffectiveHackType = effectiveHackType;
        ConfigurationName = configurationName;
        RequestedModeIniPath = requestedModeIniPath;
        BuildModeIniPath = buildModeIniPath;
        RequiredIniLabel = requiredIniLabel;
        RequiresFlashIni = requiresFlashIni;
        UsesXdkBuildConfiguration = usesXdkBuildConfiguration;
        RequiresDevGl64Preparation = requiresDevGl64Preparation;
        DisablesSmcResetPatching = disablesSmcResetPatching;
        FourGigabyteStagingPolicy = fourGigabyteStagingPolicy;
        Rgh3TemplatePath = rgh3TemplatePath;
        XdkBuildTemplatePath = xdkBuildTemplatePath;
        DashLaunch = dashLaunch;
        WorkspaceOverlays = overlays;
        PostBuildOperations = operations;
        RequiredSupportFiles = supportFiles;
        RequiredWorkspaceDirectories = workspaceDirectories;
        FixedArguments = arguments;
    }

    /// <summary>
    /// Gets the resolved canonical console definition.
    /// </summary>
    public ConsoleDefinition Console { get; }

    /// <summary>
    /// Gets the unambiguous console determined from the source, if inspection supplied one.
    /// </summary>
    public ConsoleDefinition? SourceDetectedConsole { get; }

    /// <summary>
    /// Gets whether an explicit canonical console selection took precedence over source detection.
    /// </summary>
    public bool ConsoleWasExplicitlyOverridden { get; }

    /// <summary>
    /// Gets the requested dashboard version.
    /// </summary>
    public int DashboardVersion { get; }

    /// <summary>
    /// Gets the user-selected image target.
    /// </summary>
    public XeBuildHackType RequestedHackType { get; }

    /// <summary>
    /// Gets the target actually passed to XeBuild after required compatibility normalization.
    /// </summary>
    public XeBuildHackType EffectiveHackType { get; }

    /// <summary>
    /// Gets the board configuration passed to XeBuild's <c>-c</c> argument.
    /// </summary>
    public string ConfigurationName { get; }

    /// <summary>
    /// Gets the requested dashboard mode INI that must pass preflight validation.
    /// </summary>
    public string RequestedModeIniPath { get; }

    /// <summary>
    /// Gets the dashboard mode INI used by the XeBuild invocation after compatibility normalization.
    /// </summary>
    public string BuildModeIniPath { get; }

    /// <summary>
    /// Gets the required bootloader label in each selected dashboard INI.
    /// </summary>
    public string RequiredIniLabel { get; }

    /// <summary>
    /// Gets whether the invocation needs XeBuild's <c>-i flash</c> option.
    /// </summary>
    public bool RequiresFlashIni { get; }

    /// <summary>
    /// Gets whether the selected dashboard follows the legacy XDKBuild compatibility flow.
    /// </summary>
    public bool UsesXdkBuildConfiguration { get; }

    /// <summary>
    /// Gets whether the executor must prepare, restore, and clean up the special 64 MB DevGL workspace state.
    /// </summary>
    public bool RequiresDevGl64Preparation { get; }

    /// <summary>
    /// Gets whether the mutable workspace options file must disable SMC reset patching.
    /// </summary>
    public bool DisablesSmcResetPatching { get; }

    /// <summary>
    /// Gets the explicit staging policy for a 4 GB source.
    /// </summary>
    public XeBuildFourGigabyteStagingPolicy FourGigabyteStagingPolicy { get; }

    /// <summary>
    /// Gets the selected RGH3 ECC template support path, or <see langword="null"/> when RGH3 is not requested.
    /// </summary>
    public string? Rgh3TemplatePath { get; }

    /// <summary>
    /// Gets the selected XDKBuild board-template support path, or <see langword="null"/> when no XDKBuild conversion runs.
    /// </summary>
    public string? XdkBuildTemplatePath { get; }

    /// <summary>
    /// Gets DashLaunch workspace requirements, or <see langword="null"/> when DashLaunch is not selected.
    /// </summary>
    public XeBuildDashLaunchPlan? DashLaunch { get; }

    /// <summary>
    /// Gets ordered immutable-to-mutable copies applied after staging the dependency closure.
    /// </summary>
    public ImmutableArray<XeBuildWorkspaceOverlay> WorkspaceOverlays { get; }

    /// <summary>
    /// Gets ordered operations applied after a successful XeBuild process invocation.
    /// </summary>
    public ImmutableArray<XeBuildPostBuildOperation> PostBuildOperations { get; }

    /// <summary>
    /// Gets the complete immutable support-file dependency closure required for workspace materialization.
    /// </summary>
    public ImmutableArray<string> RequiredSupportFiles { get; }

    /// <summary>
    /// Gets mutable workspace directories that must be created before staging source data or overlays.
    /// </summary>
    public ImmutableArray<string> RequiredWorkspaceDirectories { get; }

    /// <summary>
    /// Gets deterministic XeBuild argument tokens before the backend-owned temporary output path.
    /// </summary>
    public ImmutableArray<string> FixedArguments { get; }

    /// <summary>
    /// Appends the backend-owned temporary output path after the deterministic XeBuild argument prefix.
    /// </summary>
    /// <param name="temporaryOutputPath">The temporary destination path, never the final published path.</param>
    public ImmutableArray<string> CreateArgumentTokens(string temporaryOutputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryOutputPath);
        return FixedArguments.Add(temporaryOutputPath);
    }

    private static bool IsNormalizedSupportPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            Path.IsPathRooted(value) ||
            value.Contains(':', StringComparison.Ordinal) ||
            value.Contains('\\', StringComparison.Ordinal) ||
            (!value.StartsWith("common/", StringComparison.Ordinal) &&
             !value.StartsWith("xeBuild/", StringComparison.Ordinal)))
        {
            return false;
        }

        return HasOnlyNormalPathSegments(value.AsSpan());
    }

    private static bool IsNormalizedWorkspaceDirectory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            Path.IsPathRooted(value) ||
            value.Contains(':', StringComparison.Ordinal) ||
            value.Contains('\\', StringComparison.Ordinal) ||
            !value.StartsWith("xeBuild/", StringComparison.Ordinal))
        {
            return false;
        }

        return HasOnlyNormalPathSegments(value.AsSpan());
    }

    private static bool HasOnlyNormalPathSegments(ReadOnlySpan<char> path)
    {
        int segmentStart = 0;
        while (segmentStart < path.Length)
        {
            int separator = path[segmentStart..].IndexOf('/');
            ReadOnlySpan<char> segment = separator < 0
                ? path[segmentStart..]
                : path.Slice(segmentStart, separator);
            if (segment.IsEmpty || segment.SequenceEqual(".") || segment.SequenceEqual(".."))
            {
                return false;
            }

            if (separator < 0)
            {
                return true;
            }

            segmentStart += separator + 1;
        }

        return false;
    }
}
