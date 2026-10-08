using System.Runtime.InteropServices;
using JRunner.Cli.Infrastructure;
using JRunner.Core.Contracts;
using Xunit;

namespace JRunner.Cli.Tests.Support;

public sealed class SupportRootSafetyTests
{
    [Fact]
    public void Private_support_root_below_a_foreign_owned_ancestor_is_rejected()
    {
        if (!OperatingSystem.IsLinux() || GetEffectiveUserId() != 0)
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        string foreignAncestor = Path.Combine(temporaryDirectory.Path, "foreign-ancestor");
        string supportRoot = Path.Combine(foreignAncestor, "support");
        Directory.CreateDirectory(supportRoot);
        File.SetUnixFileMode(
            foreignAncestor,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        Assert.Equal(0, ChangeOwner(foreignAncestor, 65534, 65534));

        OperationFailureException exception = Assert.Throws<OperationFailureException>(
            () => SupportRootSafety.EnsureSecureAncestors(supportRoot));

        Assert.Equal(ExitCode.InputOutput, exception.Code);
        Assert.Equal("support-root-insecure", exception.Kind);
    }

    [Fact]
    public void Linux_support_root_safety_fails_closed_outside_the_x64_stat_abi()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        if (RuntimeInformation.ProcessArchitecture is Architecture.X64)
        {
            SupportRootSafety.EnsureSecureAncestors(temporaryDirectory.Path);
            return;
        }

        OperationFailureException exception = Assert.Throws<OperationFailureException>(
            () => SupportRootSafety.EnsureSecureAncestors(temporaryDirectory.Path));

        Assert.Equal("support-root-insecure", exception.Kind);
    }

    [Fact]
    public void Missing_path_below_a_sticky_shared_ancestor_is_rejected()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        string stickyAncestor = Path.Combine(temporaryDirectory.Path, "sticky");
        Directory.CreateDirectory(stickyAncestor);
        File.SetUnixFileMode(
            stickyAncestor,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute |
            UnixFileMode.StickyBit);

        OperationFailureException exception = Assert.Throws<OperationFailureException>(
            () => SupportRootSafety.EnsureSecureAncestors(
                Path.Combine(stickyAncestor, "missing", "support")));

        Assert.Equal(ExitCode.InputOutput, exception.Code);
        Assert.Equal("support-root-insecure", exception.Kind);
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    [DllImport("libc", EntryPoint = "chown", SetLastError = true)]
    private static extern int ChangeOwner(string path, uint owner, uint group);

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"jrunner-support-safety-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(
                    Path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        internal string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
