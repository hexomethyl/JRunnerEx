using JRunner.Core.Contracts;

namespace JRunner.Cli.Infrastructure;

/// <summary>
/// Resolves the private cache root used for short-lived Wine XeBuild workspaces.
/// </summary>
internal static class WineWorkspaceRootResolver
{
    internal const string XdgCacheHomeEnvironmentVariable = "XDG_CACHE_HOME";
    internal const string HomeEnvironmentVariable = "HOME";

    internal static string Resolve(string? explicitWorkspaceRoot)
    {
        return Resolve(explicitWorkspaceRoot, Environment.GetEnvironmentVariable);
    }

    internal static string Resolve(
        string? explicitWorkspaceRoot,
        Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        if (!string.IsNullOrWhiteSpace(explicitWorkspaceRoot))
        {
            return WorkspacePathSafety.NormalizeDirectoryPath(explicitWorkspaceRoot, nameof(explicitWorkspaceRoot));
        }

        string? xdgCacheHome = getEnvironmentVariable(XdgCacheHomeEnvironmentVariable);
        if (TryNormalizeAbsoluteDirectory(xdgCacheHome, out string normalizedCacheHome))
        {
            return Path.Combine(normalizedCacheHome, "jrunner", "work");
        }

        string? home = getEnvironmentVariable(HomeEnvironmentVariable);
        if (TryNormalizeAbsoluteDirectory(home, out string normalizedHome))
        {
            return Path.Combine(normalizedHome, ".cache", "jrunner", "work");
        }

        throw new OperationFailureException(
            ExitCode.MissingPrerequisite,
            "workspace-home-unavailable",
            "A private XeBuild workspace requires an absolute XDG cache or home directory.");
    }

    private static bool TryNormalizeAbsoluteDirectory(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
        {
            return false;
        }

        try
        {
            normalized = WorkspacePathSafety.NormalizeDirectoryPath(path, nameof(path));
            return true;
        }
        catch (OperationFailureException)
        {
            return false;
        }
    }
}
