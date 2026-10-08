using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using JRunner.Cli.Infrastructure;
using JRunner.Core.Contracts;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace JRunner.Cli.Tests;

public sealed class WorkspacePathProtectionTests
{
    private const string OriginalContent = "original workspace\n";
    private const string AdversarialContent = "adversarial workspace\n";
    private const string ConsumerContent = "consumer reached prefix\n";
    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode TraversableDirectoryMode = PrivateDirectoryMode |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
    private const UnixFileMode WritableDirectoryMode = TraversableDirectoryMode |
        UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
    private const UnixFileMode PublicFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite |
        UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    [Theory]
    [InlineData(UnixFileMode.GroupWrite)]
    [InlineData(UnixFileMode.OtherWrite)]
    [InlineData(UnixFileMode.GroupWrite | UnixFileMode.StickyBit)]
    [InlineData(UnixFileMode.OtherWrite | UnixFileMode.StickyBit)]
    public async Task Writable_final_workspace_directory_is_rejected_without_changing_content_or_permissions(
        UnixFileMode unsafeBits)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        string workspaceDirectory = Path.Combine(temporary.Path, "workspaces");
        UnixFileMode directoryMode = TraversableDirectoryMode | unsafeBits;
        await CreateDirectoryWithSentinelAsync(workspaceDirectory, OriginalContent, directoryMode);
        string[] originalEntries = Entries(workspaceDirectory);

        AssertDirectoryOpenIsUnsafe(workspaceDirectory);

        Assert.Equal(directoryMode, File.GetUnixFileMode(workspaceDirectory));
        Assert.Equal(OriginalContent, await File.ReadAllTextAsync(Path.Combine(workspaceDirectory, "sentinel.txt")));
        Assert.Equal(PublicFileMode, File.GetUnixFileMode(Path.Combine(workspaceDirectory, "sentinel.txt")));
        Assert.Equal(originalEntries, Entries(workspaceDirectory));
    }

    [Theory]
    [InlineData(UnixFileMode.GroupWrite, false)]
    [InlineData(UnixFileMode.OtherWrite, false)]
    [InlineData(UnixFileMode.GroupWrite, true)]
    [InlineData(UnixFileMode.OtherWrite, true)]
    public async Task Writable_ancestor_is_rejected_before_creating_missing_children(
        UnixFileMode unsafeBits,
        bool missingChildren)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        string ancestor = Path.Combine(temporary.Path, "shared");
        Directory.CreateDirectory(ancestor);
        UnixFileMode ancestorMode = TraversableDirectoryMode | unsafeBits;
        File.SetUnixFileMode(ancestor, ancestorMode);
        string ancestorSentinel = Path.Combine(ancestor, "caller-owned.txt");
        await File.WriteAllTextAsync(ancestorSentinel, "caller-owned ancestor");
        string workspaceDirectory = missingChildren
            ? Path.Combine(ancestor, "missing", "workspaces")
            : Path.Combine(ancestor, "workspaces");
        if (!missingChildren)
        {
            await CreateDirectoryWithSentinelAsync(workspaceDirectory, OriginalContent);
        }
        string[] originalEntries = Entries(ancestor);

        AssertDirectoryOpenIsUnsafe(workspaceDirectory);

        Assert.Equal(ancestorMode, File.GetUnixFileMode(ancestor));
        Assert.Equal("caller-owned ancestor", await File.ReadAllTextAsync(ancestorSentinel));
        Assert.Equal(originalEntries, Entries(ancestor));
        Assert.False(Directory.Exists(Path.Combine(ancestor, "missing")));
        if (!missingChildren)
        {
            Assert.Equal(TraversableDirectoryMode, File.GetUnixFileMode(workspaceDirectory));
            Assert.Equal(OriginalContent, await File.ReadAllTextAsync(Path.Combine(workspaceDirectory, "sentinel.txt")));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sticky_writable_intermediate_accepts_trusted_existing_and_new_private_children(bool missingChildren)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        string ancestor = Path.Combine(temporary.Path, "sticky");
        Directory.CreateDirectory(ancestor);
        UnixFileMode ancestorMode = WritableDirectoryMode | UnixFileMode.StickyBit;
        File.SetUnixFileMode(ancestor, ancestorMode);
        string ownedParent = Path.Combine(ancestor, "owned");
        string workspaceDirectory = Path.Combine(ownedParent, "workspaces");
        if (!missingChildren)
        {
            await CreateDirectoryWithSentinelAsync(workspaceDirectory, OriginalContent);
            File.SetUnixFileMode(ownedParent, TraversableDirectoryMode);
        }

        using SafeFileHandle directory = WorkspaceDirectoryProtection.OpenOrCreate(workspaceDirectory);

        Assert.Equal(ancestorMode, File.GetUnixFileMode(ancestor));
        UnixFileMode expectedChildMode = missingChildren ? PrivateDirectoryMode : TraversableDirectoryMode;
        Assert.Equal(expectedChildMode, File.GetUnixFileMode(ownedParent));
        Assert.Equal(expectedChildMode, File.GetUnixFileMode(workspaceDirectory));
        if (missingChildren)
        {
            Assert.Empty(Entries(workspaceDirectory));
        }
        else
        {
            Assert.Equal(OriginalContent, await File.ReadAllTextAsync(Path.Combine(workspaceDirectory, "sentinel.txt")));
        }
    }

    [Fact]
    public async Task Newly_created_workspace_components_are_private_and_discarded_path_segments_are_not_created()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        string selectedPath = Path.Combine(temporary.Path, "discarded", "..", "owned", "nested", "workspaces");
        string normalizedWorkspaceRoot = Path.GetFullPath(selectedPath);
        string relativeSelection = Path.Combine(
            Path.GetRelativePath(Environment.CurrentDirectory, temporary.Path),
            "discarded", "..", "owned", "nested", "workspaces") + Path.DirectorySeparatorChar;

        await using WineXeBuildWorkspace workspace = await WineXeBuildWorkspace.CreateAsync(
            relativeSelection, CancellationToken.None);

        Assert.Equal(normalizedWorkspaceRoot, Path.GetDirectoryName(workspace.RootDirectory));
        Assert.True(Path.IsPathFullyQualified(workspace.WinePrefixDirectory));
        Assert.False(Directory.Exists(Path.Combine(temporary.Path, "discarded")));
        Assert.Equal(PrivateDirectoryMode, File.GetUnixFileMode(Path.Combine(temporary.Path, "owned")));
        Assert.Equal(PrivateDirectoryMode, File.GetUnixFileMode(Path.Combine(temporary.Path, "owned", "nested")));
        Assert.Equal(PrivateDirectoryMode, File.GetUnixFileMode(normalizedWorkspaceRoot));
        Assert.Equal(PrivateDirectoryMode, File.GetUnixFileMode(workspace.RootDirectory));
        Assert.Equal(PrivateDirectoryMode, File.GetUnixFileMode(workspace.WinePrefixDirectory));
        Assert.Empty(Entries(workspace.WinePrefixDirectory));
    }

    [SupportedOSPlatform("linux")]
    [WorkspaceRootLinuxTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Foreign_owned_workspace_root_or_ancestor_is_rejected_even_with_nonwritable_permissions(
        bool foreignAncestor,
        bool missingChildren)
    {
        using var temporary = new TemporaryDirectory();
        string foreign = Path.Combine(temporary.Path, "foreign");
        Directory.CreateDirectory(foreign);
        File.SetUnixFileMode(foreign, TraversableDirectoryMode);
        string workspaceRoot = foreignAncestor
            ? Path.Combine(foreign, missingChildren ? "missing" : "existing", "workspaces")
            : foreign;
        string sentinel = Path.Combine(foreign, "caller-owned.txt");
        await File.WriteAllTextAsync(sentinel, "foreign-owned directory content");
        if (!missingChildren)
        {
            await CreateDirectoryWithSentinelAsync(workspaceRoot, OriginalContent);
        }
        string[] originalEntries = Entries(foreign);
        Assert.Equal(0, ChangeOwner(foreign, 65534, 65534));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        AssertDirectoryOpenIsUnsafe(workspaceRoot);
        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(async () =>
        {
            await using WineXeBuildWorkspace workspace = await WineXeBuildWorkspace.CreateAsync(
                workspaceRoot, cancellation.Token);
        });

        AssertUnsafe(failure);
        Assert.Equal(TraversableDirectoryMode, File.GetUnixFileMode(foreign));
        Assert.Equal("foreign-owned directory content", await File.ReadAllTextAsync(sentinel));
        Assert.Equal(originalEntries, Entries(foreign));
        Assert.False(Directory.Exists(Path.Combine(foreign, "missing")));
        if (!missingChildren)
        {
            Assert.Equal(OriginalContent, await File.ReadAllTextAsync(Path.Combine(workspaceRoot, "sentinel.txt")));
        }
    }

    [SupportedOSPlatform("linux")]
    [WorkspaceRootLinuxTheory(requireSetpriv: true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Another_uid_cannot_swap_a_managed_workspace_or_ancestor_before_a_real_consumer_uses_its_prefix(
        bool replaceAncestor,
        bool stickyParent)
    {
        using var temporary = new TemporaryDirectory();
        File.SetUnixFileMode(temporary.Path, TraversableDirectoryMode);
        using TemporaryDirectory? stickyNode = stickyParent ? new TemporaryDirectory("/tmp") : null;
        string parent = stickyParent ? "/tmp" : Path.Combine(temporary.Path, "protected-parent");
        UnixFileMode parentMode;
        if (stickyParent)
        {
            parentMode = File.GetUnixFileMode(parent);
            const UnixFileMode requiredMode = UnixFileMode.StickyBit |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            Assert.Equal(requiredMode, parentMode & requiredMode);
        }
        else
        {
            Directory.CreateDirectory(parent);
            parentMode = TraversableDirectoryMode;
            File.SetUnixFileMode(parent, parentMode);
        }
        string node = stickyNode?.Path ?? Path.Combine(parent, replaceAncestor ? "ancestor" : "workspaces");
        string workspaceRoot = replaceAncestor ? Path.Combine(node, "workspaces") : node;
        Directory.CreateDirectory(node);
        File.SetUnixFileMode(node, TraversableDirectoryMode);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using WineXeBuildWorkspace workspace = await WineXeBuildWorkspace.CreateAsync(
            workspaceRoot, cancellation.Token);
        string prefix = workspace.WinePrefixDirectory;
        Assert.Empty(Entries(prefix));
        await CreateDirectoryWithSentinelAsync(prefix, OriginalContent, PrivateDirectoryMode);
        string attackerDirectory = Path.Combine(temporary.Path, "attacker");
        Directory.CreateDirectory(attackerDirectory);
        File.SetUnixFileMode(attackerDirectory, TraversableDirectoryMode);
        Assert.Equal(0, ChangeOwner(attackerDirectory, 65534, 65534));
        string control = Path.Combine(attackerDirectory, "control");
        Directory.CreateDirectory(control);
        Assert.Equal(0, ChangeOwner(control, 65534, 65534));
        string adversarialNode = Path.Combine(attackerDirectory, "adversarial");
        string adversarialPrefix = Path.Combine(adversarialNode, Path.GetRelativePath(node, prefix));
        await CreateDirectoryWithSentinelAsync(adversarialPrefix, AdversarialContent);

        const string actorScript = """
            set -eu
            test "$(id -u)" = 65534
            test -x "$1"
            test ! -x "$2"
            mv -T -- "$4/control" "$4/control-moved"
            if mv -T -- "$3" "$4/displaced"; then
                ln -s -- "$5" "$3"
                exit 73
            fi
            if mv -T -- "$2" "$4/displaced-prefix"; then
                exit 74
            fi
            test -d "$3"
            test ! -L "$3"
            printf '%s\n' blocked
            """;
        ProcessResult attack = await RunProcessAsync(
            "/usr/bin/setpriv",
            [
                "--reuid=65534", "--regid=65534", "--clear-groups", "--bounding-set=-all",
                "--inh-caps=-all", "--ambient-caps=-all", "--no-new-privs",
                "/bin/sh", "-c", actorScript, "managed-workspace-attacker",
                parent, prefix, node, attackerDirectory, adversarialNode,
            ],
            winePrefix: null,
            cancellation.Token);

        Assert.True(attack.ExitCode == 0, $"Dropped-UID actor failed ({attack.ExitCode}): {attack.StandardError}");
        Assert.Equal("blocked\n", attack.StandardOutput);
        Assert.NotEmpty(attack.StandardError);
        Assert.True(Directory.Exists(Path.Combine(attackerDirectory, "control-moved")));
        Assert.False(Directory.Exists(Path.Combine(attackerDirectory, "displaced")));
        Assert.False(Directory.Exists(Path.Combine(attackerDirectory, "displaced-prefix")));
        Assert.Null(new DirectoryInfo(node).LinkTarget);
        Assert.Equal(parentMode, File.GetUnixFileMode(parent));
        Assert.Equal(replaceAncestor ? TraversableDirectoryMode : PrivateDirectoryMode, File.GetUnixFileMode(node));
        Assert.Equal(PrivateDirectoryMode, File.GetUnixFileMode(workspaceRoot));
        Assert.Equal(PrivateDirectoryMode, File.GetUnixFileMode(workspace.RootDirectory));
        Assert.Equal(PrivateDirectoryMode, File.GetUnixFileMode(prefix));

        ProcessResult consumer = await RunConsumerAsync(workspace.WinePrefixDirectory, cancellation.Token);

        Assert.Equal(0, consumer.ExitCode);
        Assert.Equal(string.Empty, consumer.StandardError);
        Assert.Equal(OriginalContent, consumer.StandardOutput);
        Assert.Equal(OriginalContent, await File.ReadAllTextAsync(Path.Combine(prefix, "sentinel.txt")));
        Assert.Equal(ConsumerContent, await File.ReadAllTextAsync(Path.Combine(prefix, "consumer.txt")));
        Assert.Equal(AdversarialContent, await File.ReadAllTextAsync(Path.Combine(adversarialPrefix, "sentinel.txt")));
        Assert.False(File.Exists(Path.Combine(adversarialPrefix, "consumer.txt")));

        await workspace.CleanupAsync(retainDiagnostics: false);
        Assert.False(Directory.Exists(workspace.RootDirectory));
        Assert.False(Directory.Exists(prefix));
        Assert.True(Directory.Exists(workspaceRoot));
    }

    [Fact]
    public void Non_linux_directory_protection_is_unavailable_without_creating_directories()
    {
        if (OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        string workspaceDirectory = Path.Combine(temporary.Path, "missing", "workspaces");
        OperationFailureException failure = Assert.Throws<OperationFailureException>(() =>
        {
            using SafeFileHandle directory = WorkspaceDirectoryProtection.OpenOrCreate(workspaceDirectory);
        });

        Assert.Equal(ExitCode.MissingPrerequisite, failure.Code);
        Assert.Equal("workspace-path-protection-unavailable", failure.Kind);
        Assert.Empty(Entries(temporary.Path));
    }

    [SupportedOSPlatform("linux")]
    private static async Task CreateDirectoryWithSentinelAsync(
        string directory,
        string content,
        UnixFileMode mode = TraversableDirectoryMode)
    {
        Directory.CreateDirectory(directory);
        File.SetUnixFileMode(directory, mode);
        string sentinel = Path.Combine(directory, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, content);
        File.SetUnixFileMode(sentinel, PublicFileMode);
    }

    private static void AssertDirectoryOpenIsUnsafe(string workspaceDirectory)
    {
        OperationFailureException failure = Assert.Throws<OperationFailureException>(() =>
        {
            using SafeFileHandle directory = WorkspaceDirectoryProtection.OpenOrCreate(workspaceDirectory);
        });
        AssertUnsafe(failure);
    }

    private static void AssertUnsafe(OperationFailureException failure)
    {
        Assert.Equal(ExitCode.Usage, failure.Code);
        Assert.Equal("xebuild-workspace-path-unsafe", failure.Kind);
    }

    private static string[] Entries(string directory) =>
        Directory.GetFileSystemEntries(directory).OrderBy(path => path, StringComparer.Ordinal).ToArray();

    private static Task<ProcessResult> RunConsumerAsync(string prefix, CancellationToken cancellationToken)
    {
        const string script = """
            set -eu
            cat -- "$WINEPREFIX/sentinel.txt"
            printf '%s\n' 'consumer reached prefix' > "$WINEPREFIX/consumer.txt"
            """;
        return RunProcessAsync("/bin/sh", ["-c", script, "wine-prefix-consumer"], prefix, cancellationToken);
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string executable,
        IEnumerable<string> arguments,
        string? winePrefix,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.Environment["PATH"] = "/usr/bin:/bin";
        startInfo.Environment["LC_ALL"] = "C";
        if (winePrefix is not null)
        {
            startInfo.Environment["WINEPREFIX"] = winePrefix;
        }
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the Wine prefix path actor or consumer.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    [DllImport("libc", EntryPoint = "geteuid")]
    internal static extern uint GetEffectiveUserId();

    [DllImport("libc", EntryPoint = "chown", SetLastError = true)]
    private static extern int ChangeOwner(string path, uint owner, uint group);

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory(string? parent = null)
        {
            Path = System.IO.Path.Combine(
                parent ?? System.IO.Path.GetTempPath(), $"jrunner-workspace-path-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(Path, PrivateDirectoryMode);
            }
        }

        internal string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

internal sealed class WorkspaceRootLinuxTheoryAttribute : TheoryAttribute
{
    public WorkspaceRootLinuxTheoryAttribute(bool requireSetpriv = false)
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Requires Linux directory ownership and rename protection.";
        }
        else if (WorkspacePathProtectionTests.GetEffectiveUserId() != 0)
        {
            Skip = "Requires euid 0 to create a foreign-owned local directory or launch a dropped-UID actor; portable rejection tests remain applicable.";
        }
        else if (requireSetpriv && !File.Exists("/usr/bin/setpriv"))
        {
            Skip = "Requires /usr/bin/setpriv to run the actual cross-UID workspace and prefix swap attempts.";
        }
    }
}
