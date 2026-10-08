using JRunner.Core.Contracts;

namespace JRunner.Cli.Infrastructure;

/// <summary>
/// Applies the narrow filesystem safety rules required for private, operation-scoped workspaces.
/// </summary>
internal static class WorkspacePathSafety
{
    internal static string NormalizeDirectoryPath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);

        string normalized;
        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw InvalidPath();
        }

        if (string.IsNullOrEmpty(normalized) ||
            string.Equals(normalized, Path.GetPathRoot(normalized), StringComparison.Ordinal))
        {
            throw InvalidPath();
        }

        return normalized;
    }

    internal static void EnsureNoLinkAncestors(string path)
    {
        string normalized = NormalizeDirectoryPath(path, nameof(path));
        string root = Path.GetPathRoot(normalized)
            ?? throw InvalidPath();
        string relative = normalized[root.Length..];
        string candidate = root;

        foreach (string segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            candidate = Path.Combine(candidate, segment);
            if (!PathExists(candidate))
            {
                continue;
            }

            EnsureNotLink(candidate);
        }
    }

    internal static void EnsureNotLink(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            var file = new FileInfo(path);
            var directory = new DirectoryInfo(path);
            if (file.LinkTarget is not null || directory.LinkTarget is not null)
            {
                throw UnsafePath();
            }

            if (!file.Exists && !directory.Exists)
            {
                return;
            }

            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw UnsafePath();
            }
        }
        catch (OperationFailureException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw UnsafePath();
        }
    }

    internal static void CreatePrivateDirectory(string path)
    {
        string normalized = NormalizeDirectoryPath(path, nameof(path));
        EnsureNoLinkAncestors(normalized);

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(normalized);
            }
            else
            {
                if (OperatingSystem.IsLinux())
                {
                    // Protect the ancestry and create every missing component privately, not just the leaf.
                    using var protectedDirectory = WorkspaceDirectoryProtection.OpenOrCreate(normalized);
                }
                else
                {
                    Directory.CreateDirectory(
                        normalized,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                File.SetUnixFileMode(
                    normalized,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new OperationFailureException(
                ExitCode.InputOutput,
                "workspace-create-failed",
                "The private XeBuild workspace could not be created.");
        }

        EnsureNotLink(normalized);
    }

    internal static void SetPrivateFileMode(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                throw new OperationFailureException(
                    ExitCode.InputOutput,
                    "workspace-permissions-failed",
                    "The XeBuild workspace could not protect a private file.");
            }
        }
    }

    internal static string ResolveDescendant(string root, string slashSeparatedRelativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slashSeparatedRelativePath);
        string normalizedRoot = NormalizeDirectoryPath(root, nameof(root));
        string relative = slashSeparatedRelativePath.Replace('/', Path.DirectorySeparatorChar);
        string candidate = Path.GetFullPath(Path.Combine(normalizedRoot, relative));
        string actualRelative = Path.GetRelativePath(normalizedRoot, candidate);
        if (actualRelative is "." or ".." ||
            actualRelative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            Path.IsPathRooted(actualRelative))
        {
            throw new OperationFailureException(
                ExitCode.InvalidData,
                "workspace-path-invalid",
                "The XeBuild workspace path is invalid.");
        }

        return candidate;
    }

    private static bool PathExists(string path)
    {
        try
        {
            return File.Exists(path) || Directory.Exists(path) ||
                new FileInfo(path).LinkTarget is not null ||
                new DirectoryInfo(path).LinkTarget is not null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw UnsafePath();
        }
    }

    private static OperationFailureException InvalidPath()
    {
        return new OperationFailureException(
            ExitCode.InvalidData,
            "workspace-path-invalid",
            "The XeBuild workspace path is invalid.");
    }

    private static OperationFailureException UnsafePath()
    {
        return new OperationFailureException(
            ExitCode.InvalidData,
            "workspace-path-unsafe",
            "The XeBuild workspace path must not contain symbolic links.");
    }
}
