using System.Runtime.InteropServices;
using JRunner.Cli.Infrastructure;
using JRunner.Core.Contracts;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace JRunner.Cli.Tests;

public sealed class WorkspaceDirectoryProtectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bound_directory_is_not_redirected_when_its_path_or_ancestor_is_replaced(bool replaceAncestor)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        string ancestorPath = Path.Combine(temporaryDirectory.Path, "ancestor");
        string directoryPath = Path.Combine(ancestorPath, "workspace");
        string supportPath = Path.Combine(temporaryDirectory.Path, "support");
        string supportDirectory = replaceAncestor ? Path.Combine(supportPath, "workspace") : supportPath;
        CreatePrivateDirectory(ancestorPath);
        CreatePrivateDirectory(directoryPath);
        CreatePrivateDirectory(supportPath);
        if (replaceAncestor)
        {
            CreatePrivateDirectory(supportDirectory);
        }
        string supportFilePath = Path.Combine(supportDirectory, "descriptor.bin");
        await File.WriteAllBytesAsync(supportFilePath, []);
        await File.WriteAllTextAsync(Path.Combine(directoryPath, "sentinel.txt"), "original workspace");
        string replacedPath = replaceAncestor ? ancestorPath : directoryPath;
        string movedPath = Path.Combine(temporaryDirectory.Path, "original-directory");
        using SafeFileHandle boundDirectory = WorkspaceDirectoryProtection.OpenOrCreate(directoryPath);

        Directory.Move(replacedPath, movedPath);
        Directory.CreateSymbolicLink(replacedPath, supportPath);
        const int WriteFlags = 0x1 | 0x40 | 0x80 | 0x20000 | 0x80000; // WRONLY | CREAT | EXCL | NOFOLLOW | CLOEXEC
        int descriptor = OpenAt(boundDirectory, "descriptor.bin", WriteFlags, 0x180);
        Assert.True(descriptor >= 0, $"Descriptor-relative file creation failed: {Marshal.GetLastPInvokeError()}");
        using (var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true))
        using (var stream = new FileStream(handle, FileAccess.Write))
        {
            stream.WriteByte(0x5A);
            stream.Flush(flushToDisk: true);
        }

        string originalDirectory = replaceAncestor ? Path.Combine(movedPath, "workspace") : movedPath;
        Assert.Equal(new byte[] { 0x5A }, await File.ReadAllBytesAsync(Path.Combine(originalDirectory, "descriptor.bin")));
        Assert.Empty(await File.ReadAllBytesAsync(supportFilePath));
        Assert.Equal("original workspace", await File.ReadAllTextAsync(Path.Combine(originalDirectory, "sentinel.txt")));
        Assert.Equal(supportPath, new DirectoryInfo(replacedPath).LinkTarget);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Existing_directory_or_ancestor_symlink_is_rejected_without_touching_its_target(bool linkAncestor)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        string ancestorPath = Path.Combine(temporaryDirectory.Path, "ancestor");
        string directoryPath = Path.Combine(ancestorPath, "workspace");
        string supportPath = Path.Combine(temporaryDirectory.Path, "support");
        string supportDirectory = linkAncestor ? Path.Combine(supportPath, "workspace") : supportPath;
        CreatePrivateDirectory(supportPath);
        if (linkAncestor)
        {
            CreatePrivateDirectory(supportDirectory);
        }
        else
        {
            CreatePrivateDirectory(ancestorPath);
        }
        string sentinelPath = Path.Combine(supportDirectory, "sentinel.txt");
        await File.WriteAllTextAsync(sentinelPath, "caller-owned support-like file");
        string linkPath = linkAncestor ? ancestorPath : directoryPath;
        Directory.CreateSymbolicLink(linkPath, supportPath);

        OperationFailureException failure = Assert.Throws<OperationFailureException>(() =>
        {
            using SafeFileHandle directory = WorkspaceDirectoryProtection.OpenOrCreate(directoryPath);
        });

        Assert.Equal(ExitCode.InvalidData, failure.Code);
        Assert.Equal("workspace-path-unsafe", failure.Kind);
        Assert.Equal("The XeBuild workspace path must not contain symbolic links.", failure.Message);
        Assert.Equal("caller-owned support-like file", await File.ReadAllTextAsync(sentinelPath));
        Assert.Equal(new[] { sentinelPath }, Directory.GetFileSystemEntries(supportDirectory));
        Assert.Equal(supportPath, new DirectoryInfo(linkPath).LinkTarget);
    }

    [Fact]
    public void Missing_directory_components_are_not_created_through_a_symlinked_ancestor()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        string supportPath = Path.Combine(temporaryDirectory.Path, "support");
        CreatePrivateDirectory(supportPath);
        string ancestorPath = Path.Combine(temporaryDirectory.Path, "ancestor");
        Directory.CreateSymbolicLink(ancestorPath, supportPath);
        string directoryPath = Path.Combine(ancestorPath, "missing-workspace", "nested");

        OperationFailureException failure = Assert.Throws<OperationFailureException>(() =>
        {
            using SafeFileHandle directory = WorkspaceDirectoryProtection.OpenOrCreate(directoryPath);
        });

        Assert.Equal(ExitCode.InvalidData, failure.Code);
        Assert.Equal("workspace-path-unsafe", failure.Kind);
        Assert.Equal("The XeBuild workspace path must not contain symbolic links.", failure.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(supportPath));
    }

    private static void CreatePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(SafeFileHandle directory, string path, int flags, uint mode);

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"jrunner-workspace-directory-tests-{Guid.NewGuid():N}");
            CreatePrivateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
