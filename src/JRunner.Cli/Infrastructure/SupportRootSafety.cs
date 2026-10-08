using System.Runtime.InteropServices;
using JRunner.Core.Contracts;

namespace JRunner.Cli.Infrastructure;

/// <summary>
/// Validates that a support-root path remains under directories controlled by the current user or root.
/// </summary>
internal static class SupportRootSafety
{
    internal static void EnsureSecureAncestors(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        EnsureNoLinkAncestors(normalizedPath);
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        if (RuntimeInformation.ProcessArchitecture is not Architecture.X64)
        {
            throw InsecureRoot();
        }

        uint effectiveUserId = GetEffectiveUserId();
        bool hasMissingDescendant = false;
        for (string? candidate = normalizedPath; candidate is not null; candidate = Path.GetDirectoryName(candidate))
        {
            if (!Directory.Exists(candidate))
            {
                hasMissingDescendant = true;
                continue;
            }

            if (Stat(candidate, out LinuxStat status) != 0)
            {
                throw new IOException("Unable to inspect a support-root ancestor.");
            }

            if (status.UserId != 0 && status.UserId != effectiveUserId)
            {
                throw InsecureRoot();
            }

            UnixFileMode mode = File.GetUnixFileMode(candidate);
            bool sharedWritable = (mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0;
            bool isSupportRoot = string.Equals(candidate, normalizedPath, StringComparison.Ordinal);
            if (sharedWritable &&
                (isSupportRoot ||
                 hasMissingDescendant ||
                 (mode & UnixFileMode.StickyBit) == 0))
            {
                throw InsecureRoot();
            }

            hasMissingDescendant = false;
        }
    }

    internal static void EnsureSecureFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        if (RuntimeInformation.ProcessArchitecture is not Architecture.X64)
        {
            throw InsecureRoot();
        }

        var file = new FileInfo(path);
        if (file.LinkTarget is not null)
        {
            throw new IOException("A support payload file cannot be a symbolic link.");
        }

        if (Stat(path, out LinuxStat status) != 0)
        {
            throw new IOException("Unable to inspect a support payload file.");
        }

        if ((status.Mode & LinuxFileTypeMask) != LinuxRegularFile)
        {
            throw new IOException("A support payload entry is not a regular file.");
        }

        uint effectiveUserId = GetEffectiveUserId();
        if (status.UserId != 0 && status.UserId != effectiveUserId)
        {
            throw InsecureRoot();
        }

        UnixFileMode mode = File.GetUnixFileMode(path);
        if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
        {
            throw InsecureRoot();
        }
    }

    internal static void EnsurePrivateDirectory(string path)
    {
        EnsureSecureAncestors(path);
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        UnixFileMode mode = File.GetUnixFileMode(path);
        const UnixFileMode GroupOrOtherPermissions =
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        if ((mode & GroupOrOtherPermissions) != 0)
        {
            throw InsecureRoot();
        }
    }

    private const uint LinuxFileTypeMask = 0xF000;
    private const uint LinuxRegularFile = 0x8000;



    internal static void EnsureNoLinkAncestors(string path)
    {
        for (string? candidate = path; candidate is not null; candidate = Path.GetDirectoryName(candidate))
        {
            var directory = new DirectoryInfo(candidate);
            if (directory.LinkTarget is not null)
            {
                throw new IOException("The support root cannot contain symbolic links.");
            }

            if (directory.Exists)
            {
                continue;
            }

            var file = new FileInfo(candidate);
            if (file.Exists || file.LinkTarget is not null)
            {
                throw new IOException("The support root cannot cross a file or symbolic link.");
            }
        }
    }

    private static OperationFailureException InsecureRoot()
    {
        return new OperationFailureException(
            ExitCode.InputOutput,
            "support-root-insecure",
            "The support root must be owned by the current user or root and protected from untrusted writes.");
    }

    // Linux x86_64's stat ABI stores st_mode at byte 24 and st_uid at byte 28.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStat
    {
        [FieldOffset(24)]
        internal uint Mode;

        [FieldOffset(28)]
        internal uint UserId;
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    private static extern int Stat(string path, out LinuxStat status);
}
