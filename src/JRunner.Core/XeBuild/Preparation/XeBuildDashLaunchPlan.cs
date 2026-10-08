using System.Collections.Immutable;
using System.Globalization;

namespace JRunner.Core.XeBuild.Preparation;

/// <summary>
/// Describes the DashLaunch assets and INI entries that an isolated XeBuild workspace must apply.
/// </summary>
public sealed record XeBuildDashLaunchPlan
{
    private const string LaunchExecutablePath = "xeBuild/launch.xex";
    private const string HelperExecutablePath = "xeBuild/lhelper.xex";
    private const string LaunchConfigurationPath = "xeBuild/launch.ini";
    private const string DefaultLaunchConfigurationPath = "xeBuild/launch_default.ini";

    internal XeBuildDashLaunchPlan(int dashboardVersion)
    {
        if (dashboardVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dashboardVersion),
                dashboardVersion,
                "The dashboard version must be positive.");
        }

        DashboardVersion = dashboardVersion;
        DashboardLaunchConfigurationPath = string.Concat(
            "xeBuild/",
            dashboardVersion.ToString(CultureInfo.InvariantCulture),
            "/launch.ini");
        RequiredSupportFiles = ImmutableArray.Create(
            LaunchExecutablePath,
            HelperExecutablePath,
            LaunchConfigurationPath,
            DefaultLaunchConfigurationPath);
        IniPatchEntries = ImmutableArray.Create(
            "..\\launch.xex",
            "..\\lhelper.xex",
            "..\\launch.ini");
    }

    /// <summary>
    /// Gets the selected dashboard version.
    /// </summary>
    public int DashboardVersion { get; }

    /// <summary>
    /// Gets the root launch executable that must be staged.
    /// </summary>
    public string LaunchExecutableSupportPath => LaunchExecutablePath;

    /// <summary>
    /// Gets the root launch helper that must be staged.
    /// </summary>
    public string HelperExecutableSupportPath => HelperExecutablePath;

    /// <summary>
    /// Gets the root caller-provided DashLaunch configuration path.
    /// </summary>
    public string LaunchConfigurationSupportPath => LaunchConfigurationPath;

    /// <summary>
    /// Gets the root default DashLaunch configuration fallback path.
    /// </summary>
    public string DefaultLaunchConfigurationSupportPath => DefaultLaunchConfigurationPath;

    /// <summary>
    /// Gets the dashboard-local configuration path that workspace materialization must create or replace.
    /// </summary>
    public string DashboardLaunchConfigurationPath { get; }

    /// <summary>
    /// Gets the exact legacy entries that must be present in the selected non-retail dashboard INI.
    /// </summary>
    public ImmutableArray<string> IniPatchEntries { get; }

    /// <summary>
    /// Gets the complete immutable root-asset closure for DashLaunch.
    /// </summary>
    public ImmutableArray<string> RequiredSupportFiles { get; }
}
