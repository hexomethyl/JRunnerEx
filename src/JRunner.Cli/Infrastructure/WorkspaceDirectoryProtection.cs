using System.Runtime.InteropServices;
using JRunner.Core.Contracts;
using Microsoft.Win32.SafeHandles;

namespace JRunner.Cli.Infrastructure;

/// <summary>
/// Creates and opens a workspace directory through protected, descriptor-bound Linux ancestry.
/// </summary>
internal static class WorkspaceDirectoryProtection
{
    private const int LinuxDirectory = 0x10000;
    private const int LinuxNoFollow = 0x20000;
    private const int LinuxCloseOnExec = 0x80000;
    private const int LinuxPath = 0x200000;
    private const uint LinuxPrivateDirectoryMode = 0x1C0;
    private const int LinuxEmptyPath = 0x1000;
    private const uint LinuxStatxDirectoryFields = 0xB;
    private const int LinuxFileTypeMask = 0xF000;
    private const int LinuxDirectoryFile = 0x4000;
    private const int LinuxGroupOrOtherWrite = 0x12;
    private const int LinuxStickyBit = 0x200;
    private const int LinuxNoSuchFile = 2;
    private const int LinuxAlreadyExists = 17;
    private const int LinuxNotADirectory = 20;
    private const int LinuxSymbolicLinkLoop = 40;

    /// <summary>
    /// Opens or creates each component of an absolute, normalized Linux path relative to its bound parent.
    /// Trusted ownership and protected ancestry keep the same pathname safe for Wine and staged secrets.
    /// </summary>
    internal static SafeFileHandle OpenOrCreate(string normalizedDirectory)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw ProtectionUnavailable();
        }

        const int DirectoryFlags = LinuxPath | LinuxDirectory | LinuxNoFollow | LinuxCloseOnExec;
        string root = Path.GetPathRoot(normalizedDirectory)
            ?? throw ProtectionFailed();
        SafeFileHandle directory = OwnDescriptor(Open(root, DirectoryFlags, mode: 0));
        uint currentUserId = GetEffectiveUserId();
        try
        {
            EnsureProtectedDirectory(directory, currentUserId, isLeaf: false);
            int start = root.Length;
            while (start < normalizedDirectory.Length)
            {
                int end = normalizedDirectory.IndexOf(Path.DirectorySeparatorChar, start);
                if (end < 0)
                {
                    end = normalizedDirectory.Length;
                }
                if (end == start)
                {
                    start++;
                    continue;
                }

                string component = normalizedDirectory[start..end];
                int descriptor = OpenAt(directory, component, DirectoryFlags, mode: 0);
                if (descriptor < 0 && Marshal.GetLastPInvokeError() == LinuxNoSuchFile)
                {
                    if (MakeDirectoryAt(directory, component, LinuxPrivateDirectoryMode) != 0 &&
                        Marshal.GetLastPInvokeError() != LinuxAlreadyExists)
                    {
                        throw ProtectionFailed();
                    }

                    descriptor = OpenAt(directory, component, DirectoryFlags, mode: 0);
                }

                SafeFileHandle child = OwnDescriptor(descriptor);
                try
                {
                    EnsureProtectedDirectory(child, currentUserId, isLeaf: end == normalizedDirectory.Length);
                }
                catch
                {
                    child.Dispose();
                    throw;
                }

                directory.Dispose();
                directory = child;
                start = end + 1;
            }

            return directory;
        }
        catch
        {
            directory.Dispose();
            throw;
        }
    }

    private static void EnsureProtectedDirectory(SafeFileHandle directory, uint currentUserId, bool isLeaf)
    {
        if (Statx(directory, string.Empty, LinuxEmptyPath, LinuxStatxDirectoryFields, out LinuxStatx status) != 0 ||
            (status.Mask & LinuxStatxDirectoryFields) != LinuxStatxDirectoryFields ||
            (status.Mode & LinuxFileTypeMask) != LinuxDirectoryFile)
        {
            throw ProtectionFailed();
        }

        // Only root and our UID can change these permissions; hostile same-UID processes are outside this boundary.
        // A trusted sticky ancestor such as /tmp cannot have its trusted-owned child renamed by another user.
        // The workspace itself cannot be shared-writable: it contains the managed Wine prefix and staged secrets.
        if ((status.OwnerUserId != 0 && status.OwnerUserId != currentUserId) ||
            ((status.Mode & LinuxGroupOrOtherWrite) != 0 && (isLeaf || (status.Mode & LinuxStickyBit) == 0)))
        {
            throw new OperationFailureException(
                ExitCode.Usage,
                "xebuild-workspace-path-unsafe",
                "The XeBuild workspace root and its ancestors must be owned by the current user or root and prevent replacement by other users.");
        }
    }

    private static SafeFileHandle OwnDescriptor(int descriptor)
    {
        if (descriptor >= 0)
        {
            return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        }

        int error = Marshal.GetLastPInvokeError();
        if (error is LinuxSymbolicLinkLoop or LinuxNotADirectory)
        {
            throw new OperationFailureException(
                ExitCode.InvalidData,
                "workspace-path-unsafe",
                "The XeBuild workspace path must not contain symbolic links.");
        }

        throw ProtectionFailed();
    }

    private static OperationFailureException ProtectionFailed()
    {
        return new OperationFailureException(
            ExitCode.InputOutput,
            "workspace-path-protection-failed",
            "The XeBuild workspace directory could not be created or opened safely.");
    }

    private static OperationFailureException ProtectionUnavailable()
    {
        return new OperationFailureException(
            ExitCode.MissingPrerequisite,
            "workspace-path-protection-unavailable",
            "Protecting XeBuild workspace directories requires Linux filesystem ownership and permission checks.");
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatx
    {
        [FieldOffset(0)]
        internal uint Mask;

        [FieldOffset(20)]
        internal uint OwnerUserId;

        [FieldOffset(28)]
        internal ushort Mode;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags, uint mode);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(SafeFileHandle directory, string path, int flags, uint mode);

    [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)]
    private static extern int MakeDirectoryAt(SafeFileHandle directory, string path, uint mode);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(
        SafeFileHandle descriptor,
        string path,
        int flags,
        uint mask,
        out LinuxStatx status);

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();
}
