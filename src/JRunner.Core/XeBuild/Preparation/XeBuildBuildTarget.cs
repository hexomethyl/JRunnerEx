using JRunner.Core.Nand.Models;

namespace JRunner.Core.XeBuild.Preparation;

/// <summary>
/// Validated semantic selection for one XeBuild image.
/// </summary>
/// <remarks>
/// Console overrides are accepted only through <see cref="ConsoleCatalog.TryGetByCanonicalName"/>;
/// callers cannot inject numeric legacy identifiers or board aliases into the public model.
/// </remarks>
public sealed record XeBuildBuildTarget
{
    /// <summary>
    /// Creates a semantic XeBuild target.
    /// </summary>
    /// <param name="consoleOverrideCanonicalName">
    /// An optional canonical console name. When absent, preparation requires an unambiguous source-detected console.
    /// </param>
    /// <param name="dashboardVersion">The requested dashboard version.</param>
    /// <param name="typeCanonicalName">The canonical XeBuild target name.</param>
    /// <param name="options">Typed optional image behaviors.</param>
    public XeBuildBuildTarget(
        string? consoleOverrideCanonicalName,
        int dashboardVersion,
        string typeCanonicalName,
        XeBuildBuildOptions? options = null)
    {
        if (dashboardVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dashboardVersion),
                dashboardVersion,
                "The dashboard version must be positive.");
        }

        ConsoleDefinition? console = null;
        if (consoleOverrideCanonicalName is not null &&
            !ConsoleCatalog.TryGetByCanonicalName(consoleOverrideCanonicalName, out console))
        {
            throw new ArgumentException(
                "The console override must be one of the canonical console names returned by 'jrunner console list'.",
                nameof(consoleOverrideCanonicalName));
        }

        if (!XeBuildHackTypeCatalog.TryGetByCanonicalName(typeCanonicalName, out XeBuildHackType hackType))
        {
            throw new ArgumentException(
                "The XeBuild type must be a canonical target name.",
                nameof(typeCanonicalName));
        }

        ConsoleOverride = console;
        DashboardVersion = dashboardVersion;
        HackType = hackType;
        TypeCanonicalName = XeBuildHackTypeCatalog.GetCanonicalName(hackType);
        Options = options ?? new XeBuildBuildOptions();
    }

    /// <summary>
    /// Gets the explicitly selected canonical console, or <see langword="null"/> when source detection must choose it.
    /// </summary>
    public ConsoleDefinition? ConsoleOverride { get; }

    /// <summary>
    /// Gets the requested dashboard version.
    /// </summary>
    public int DashboardVersion { get; }

    /// <summary>
    /// Gets the typed XeBuild target.
    /// </summary>
    public XeBuildHackType HackType { get; }

    /// <summary>
    /// Gets the normalized canonical target name supplied through <c>--type</c>.
    /// </summary>
    public string TypeCanonicalName { get; }

    /// <summary>
    /// Gets typed optional image behaviors.
    /// </summary>
    public XeBuildBuildOptions Options { get; }
}
