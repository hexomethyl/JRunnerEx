using JRunner.Core.Contracts;

namespace JRunner.Cli.Infrastructure;

/// <summary>
/// Identifies how the support root was selected.
/// </summary>
public enum SupportRootSource
{
    /// <summary>
    /// The caller explicitly selected the root.
    /// </summary>
    Explicit,

    /// <summary>
    /// The <c>JRUNNER_SUPPORT_ROOT</c> environment override selected the root.
    /// </summary>
    EnvironmentOverride,

    /// <summary>
    /// The XDG data-home environment variable selected the root.
    /// </summary>
    XdgDataHome,

    /// <summary>
    /// The home-directory XDG fallback selected the root.
    /// </summary>
    HomeFallback,
}

/// <summary>
/// A normalized support root and the configuration source that selected it.
/// </summary>
public sealed record SupportRoot(string DirectoryPath, SupportRootSource Source);

/// <summary>
/// Resolves the application-owned root for externally acquired support files.
/// </summary>
public static class SupportRootResolver
{
    /// <summary>
    /// Gets the environment variable that overrides every default support-root location.
    /// </summary>
    public const string SupportRootEnvironmentVariable = "JRUNNER_SUPPORT_ROOT";

    /// <summary>
    /// Gets the XDG environment variable that selects a user data home.
    /// </summary>
    public const string XdgDataHomeEnvironmentVariable = "XDG_DATA_HOME";

    /// <summary>
    /// Gets the environment variable used by the XDG home fallback.
    /// </summary>
    public const string HomeEnvironmentVariable = "HOME";

    /// <summary>
    /// Resolves the command option, environment override, or XDG data-root default.
    /// </summary>
    public static SupportRoot Resolve(string? explicitSupportRoot)
    {
        return Resolve(explicitSupportRoot, Environment.GetEnvironmentVariable);
    }

    /// <summary>
    /// Resolves the command option, environment override, or XDG data-root default using a caller-supplied environment lookup.
    /// </summary>
    public static SupportRoot Resolve(
        string? explicitSupportRoot,
        Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        if (explicitSupportRoot is not null)
        {
            return new SupportRoot(NormalizeExplicitPath(explicitSupportRoot), SupportRootSource.Explicit);
        }

        if (getEnvironmentVariable(SupportRootEnvironmentVariable) is { } environmentSupportRoot)
        {
            return new SupportRoot(
                NormalizeExplicitPath(environmentSupportRoot),
                SupportRootSource.EnvironmentOverride);
        }

        if (TryNormalizeAbsolutePath(
                getEnvironmentVariable(XdgDataHomeEnvironmentVariable),
                out string xdgDataHome))
        {
            return new SupportRoot(
                Path.Combine(xdgDataHome, "jrunner", "support"),
                SupportRootSource.XdgDataHome);
        }

        if (TryNormalizeAbsolutePath(getEnvironmentVariable(HomeEnvironmentVariable), out string homeDirectory))
        {
            return new SupportRoot(
                Path.Combine(homeDirectory, ".local", "share", "jrunner", "support"),
                SupportRootSource.HomeFallback);
        }

        throw new OperationFailureException(
            ExitCode.MissingPrerequisite,
            "support-root-unavailable",
            "Unable to resolve the support root because HOME is not an absolute path.");
    }

    private static string NormalizeExplicitPath(string explicitSupportRoot)
    {
        if (string.IsNullOrWhiteSpace(explicitSupportRoot))
        {
            throw InvalidSupportRoot();
        }

        try
        {
            string normalizedPath = Path.GetFullPath(explicitSupportRoot);
            string? fileSystemRoot = Path.GetPathRoot(normalizedPath);
            if (string.Equals(
                    Path.TrimEndingDirectorySeparator(normalizedPath),
                    Path.TrimEndingDirectorySeparator(fileSystemRoot ?? string.Empty),
                    StringComparison.Ordinal))
            {
                throw InvalidSupportRoot();
            }

            return normalizedPath;
        }
        catch (ArgumentException)
        {
            throw InvalidSupportRoot();
        }
        catch (NotSupportedException)
        {
            throw InvalidSupportRoot();
        }
        catch (PathTooLongException)
        {
            throw InvalidSupportRoot();
        }
    }

    private static bool TryNormalizeAbsolutePath(string? value, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
        {
            return false;
        }

        try
        {
            normalizedPath = Path.GetFullPath(value);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }
    }

    private static OperationFailureException InvalidSupportRoot()
    {
        return new OperationFailureException(
            ExitCode.Usage,
            "invalid-support-root",
            "The support root is invalid.");
    }
}
