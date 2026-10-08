using System.Runtime.Versioning;
using JRunner.Cli.Infrastructure;
using JRunner.Core.Contracts;
using Xunit;

namespace JRunner.Cli.Tests;

[SupportedOSPlatform("linux")]
public sealed class WineNativePrefixPolicyTests
{
    [Fact]
    public void Fresh_managed_prefix_prepares_private_empty_default_startup_and_sealed_local_user_before_any_Wine()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temporary = new WineXeBuildBackendTests.TemporaryDirectory();
        string workspace = CreatePrivateDirectory(Path.Join(temporary.Path, "workspace"));
        string prefix = CreatePrivateDirectory(Path.Join(workspace, "wine-prefix"));

        WineNativePrefixPolicy policy = WineNativePrefixPolicy.PrepareFresh(prefix, workspace);
        WineNativePrefixPolicy restored = WineNativePrefixPolicy.FromBinding(policy.ToBinding());
        IReadOnlyDictionary<string, string> environment = WineNativePrefixPolicy.CreateClosedEnvironment(workspace, policy, workspace);

        Assert.True(policy.NeedsInitialization);
        Assert.Equal(policy.UserName, environment["USER"]);
        Assert.False(string.IsNullOrEmpty(policy.UserName));
        Assert.Contains("/etc/passwd", policy.ConfigurationFiles);
        Assert.Contains(Path.Join(prefix, "drive_c"), policy.ModuleDirectories);
        Assert.Contains(Path.Join(prefix, "dosdevices", "c:"), policy.ModuleDirectories);
        Assert.DoesNotContain("/", policy.ModuleDirectories);
        Assert.Equal(policy.ModuleDirectories, restored.ModuleDirectories);
        Assert.Equal(policy.UserName, restored.UserName);
        foreach (string startup in StartupDirectories(prefix, policy.UserName))
        {
            Assert.True(Directory.Exists(startup));
            Assert.Empty(Directory.EnumerateFileSystemEntries(startup));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(startup));
        }
        Assert.Equal("../drive_c", new DirectoryInfo(Path.Join(prefix, "dosdevices", "c:")).LinkTarget);
        Assert.Equal("/", new DirectoryInfo(Path.Join(prefix, "dosdevices", "z:")).LinkTarget);
    }

    [Theory]
    [InlineData("system.reg", false)]
    [InlineData(".hidden", false)]
    [InlineData("drive_c", true)]
    [InlineData("dosdevices", true)]
    public void Any_preexisting_prefix_tree_is_rejected_before_it_can_be_consumed(string name, bool directory)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temporary = new WineXeBuildBackendTests.TemporaryDirectory();
        string workspace = CreatePrivateDirectory(Path.Join(temporary.Path, "workspace"));
        string prefix = CreatePrivateDirectory(Path.Join(workspace, "wine-prefix"));
        string entry = Path.Join(prefix, name);
        if (directory)
        {
            CreatePrivateDirectory(entry);
        }
        else
        {
            File.WriteAllText(entry, "caller-controlled prefix state");
            File.SetUnixFileMode(entry, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        OperationFailureException failure = Assert.Throws<OperationFailureException>(() => WineNativePrefixPolicy.PrepareFresh(prefix, workspace));

        Assert.Equal(ExitCode.MissingPrerequisite, failure.Code);
        Assert.Equal("wine-native-prefix-unsupported", failure.Kind);
        Assert.DoesNotContain(prefix, failure.Message, StringComparison.Ordinal);
        Assert.Equal(entry, Assert.Single(Directory.EnumerateFileSystemEntries(prefix)));
    }

    [Fact]
    public void Prepared_startup_item_is_rejected_before_version_or_initialization_and_cannot_rebind()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temporary = new WineXeBuildBackendTests.TemporaryDirectory();
        string workspace = CreatePrivateDirectory(Path.Join(temporary.Path, "workspace"));
        string prefix = CreatePrivateDirectory(Path.Join(workspace, "wine-prefix"));
        WineNativePrefixPolicy policy = WineNativePrefixPolicy.PrepareFresh(prefix, workspace);
        WineNativePrefixPolicyBinding binding = policy.ToBinding();
        File.WriteAllText(Path.Join(StartupDirectories(prefix, policy.UserName)[0], ".hidden.lnk"), "not even an ELF file");

        Assert.Equal("wine-native-prefix-unsupported", Assert.Throws<OperationFailureException>(policy.Revalidate).Kind);
        Assert.Throws<OperationFailureException>(() => WineNativePrefixPolicy.FromBinding(binding));
        Assert.Throws<OperationFailureException>(() => WineNativePrefixPolicy.PrepareAfterInitialization(policy));
    }

    [Fact]
    public void Only_initial_fresh_policy_can_accept_vendor_initialization_and_atomic_private_registry_saves()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temporary = new WineXeBuildBackendTests.TemporaryDirectory();
        string workspace = CreatePrivateDirectory(Path.Join(temporary.Path, "workspace"));
        string prefix = CreatePrivateDirectory(Path.Join(workspace, "wine-prefix"));
        WineNativePrefixPolicy initial = WineNativePrefixPolicy.PrepareFresh(prefix, workspace);
        InitializePrefix(prefix);

        WineNativePrefixPolicy initialized = WineNativePrefixPolicy.PrepareAfterInitialization(initial);
        WineNativePrefixPolicyBinding binding = initialized.ToBinding();
        string registry = Path.Join(prefix, "user.reg");
        string replacement = registry + ".new";
        File.WriteAllText(replacement, File.ReadAllText(registry) + "[Control Panel\\\\Colors] 0\n\"Window\"=\"255 255 255\"\n");
        File.SetUnixFileMode(replacement, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(replacement, registry, overwrite: true);

        Assert.False(initialized.NeedsInitialization);
        initialized.Revalidate();
        WineNativePrefixPolicy.FromBinding(binding);
        Assert.Throws<OperationFailureException>(() => WineNativePrefixPolicy.PrepareAfterInitialization(initialized));
    }

    [Theory]
    [InlineData("extra-mapping")]
    [InlineData("registry-write")]
    [InlineData("startup-item")]
    public void Initialized_managed_prefix_loses_trust_on_changed_mapping_writable_registry_or_startup_item(string mutation)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temporary = new WineXeBuildBackendTests.TemporaryDirectory();
        string workspace = CreatePrivateDirectory(Path.Join(temporary.Path, "workspace"));
        string prefix = CreatePrivateDirectory(Path.Join(workspace, "wine-prefix"));
        WineNativePrefixPolicy initial = WineNativePrefixPolicy.PrepareFresh(prefix, workspace);
        InitializePrefix(prefix);
        WineNativePrefixPolicy initialized = WineNativePrefixPolicy.PrepareAfterInitialization(initial);
        WineNativePrefixPolicyBinding binding = initialized.ToBinding();
        switch (mutation)
        {
            case "extra-mapping":
                File.CreateSymbolicLink(Path.Join(prefix, "dosdevices", "y:"), "/");
                break;
            case "registry-write":
                File.SetUnixFileMode(Path.Join(prefix, "system.reg"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherWrite);
                break;
            case "startup-item":
                File.WriteAllText(Path.Join(StartupDirectories(prefix, initialized.UserName)[1], "unexpected.exe"), "code selector");
                break;
        }

        Assert.Equal("wine-native-prefix-unsupported", Assert.Throws<OperationFailureException>(initialized.Revalidate).Kind);
        Assert.Throws<OperationFailureException>(() => WineNativePrefixPolicy.FromBinding(binding));
    }

    [Fact]
    public void Frozen_private_directory_identity_is_not_silently_rerooted_from_binding()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temporary = new WineXeBuildBackendTests.TemporaryDirectory();
        string workspace = CreatePrivateDirectory(Path.Join(temporary.Path, "workspace"));
        string prefix = CreatePrivateDirectory(Path.Join(workspace, "wine-prefix"));
        WineNativePrefixPolicy policy = WineNativePrefixPolicy.PrepareFresh(prefix, workspace);
        WineNativePrefixPolicyBinding binding = policy.ToBinding();
        WineNativeDirectoryBinding[] directories = binding.Directories.ToArray();
        directories[0] = directories[0] with { Identity = directories[0].Identity with { Inode = directories[0].Identity.Inode + 1 } };

        Assert.Throws<OperationFailureException>(() => WineNativePrefixPolicy.FromBinding(binding with { Directories = directories }));
        Assert.Throws<OperationFailureException>(() => WineNativePrefixPolicy.FromBinding(binding with { UserName = "../other-user" }));
    }

    internal static void InitializePrefix(string prefix)
    {
        foreach (string directory in new[]
        {
            prefix, Path.Join(prefix, "dosdevices"), Path.Join(prefix, "drive_c"),
            Path.Join(prefix, "drive_c", "windows"), Path.Join(prefix, "drive_c", "windows", "system32"),
            Path.Join(prefix, "drive_c", "windows", "syswow64"),
            Path.Join(prefix, "drive_c", "Program Files"), Path.Join(prefix, "drive_c", "Program Files (x86)"),
        })
        {
            CreatePrivateDirectory(directory);
        }
        if (!Directory.Exists(Path.Join(prefix, "dosdevices", "c:")))
        {
            File.CreateSymbolicLink(Path.Join(prefix, "dosdevices", "c:"), "../drive_c");
        }
        if (!Directory.Exists(Path.Join(prefix, "dosdevices", "z:")))
        {
            File.CreateSymbolicLink(Path.Join(prefix, "dosdevices", "z:"), "/");
        }
        foreach (string name in new[] { "system.reg", "user.reg", "userdef.reg" })
        {
            File.WriteAllText(Path.Join(prefix, name), "WINE REGISTRY Version 2\n\n#arch=win64\n\n");
            File.SetUnixFileMode(Path.Join(prefix, name), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static string[] StartupDirectories(string prefix, string userName) =>
    [
        Path.Join(prefix, "drive_c", "users", userName, "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs", "StartUp"),
        Path.Join(prefix, "drive_c", "ProgramData", "Microsoft", "Windows", "Start Menu", "Programs", "StartUp"),
    ];

    private static string CreatePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

}
