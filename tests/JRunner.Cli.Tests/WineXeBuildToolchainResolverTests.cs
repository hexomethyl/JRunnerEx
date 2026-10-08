using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using JRunner.Cli.Infrastructure;
using JRunner.Core.Contracts;
using Xunit;
using TemporaryDirectory = JRunner.Cli.Tests.WineXeBuildBackendTests.TemporaryDirectory;

namespace JRunner.Cli.Tests;

public sealed class WineXeBuildToolchainResolverTests
{
    [Theory]
    [InlineData("synthetic-wine", "synthetic-winepath")]
    [InlineData("./synthetic wine", "../synthetic winepath")]
    [InlineData("/not-installed/wine", "/not-installed/winepath")]
    public void Explicit_unmarked_toolchains_are_returned_unchanged_without_discovery(
        string wine,
        string winepath)
    {
        var toolchain = new WineXeBuildToolchain(wine, winepath);
        var resolver = new WineXeBuildToolchainResolver("must-not-inspect-this-PATH\0");

        WineXeBuildToolchain resolved = resolver.Resolve(toolchain, "unused-working-directory\0", "unused-helper-directory\0");

        Assert.Same(toolchain, resolved);
        Assert.False(resolved.RequiresTrustedPathDiscovery);
        Assert.Null(resolved.TrustedChildPath);
        Assert.Equal(wine, resolved.WineExecutable);
        Assert.Equal(winepath, resolved.WinePathExecutable);
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Production_discovery_rejects_private_native_Wine_even_when_static_and_metadata_protected(bool dynamic)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temporary = new TemporaryDirectory();
        string tools = temporary.CreateDirectory("private-native-Wine");
        string wine = temporary.CreateExecutable("private-native-Wine/wine");
        temporary.CreateExecutable("private-native-Wine/winepath");
        temporary.CreateExecutable("private-native-Wine/wineserver");
        if (dynamic)
        {
            File.WriteAllBytes(wine, TemporaryDirectory.CreateNativeElfImage("/lib64/ld-linux-x86-64.so.2", "libc.so.6"));
        }
        string helpers = temporary.CreateDirectory("private-native-helpers");

        OperationFailureException failure = Assert.Throws<OperationFailureException>(() =>
            new WineXeBuildToolchainResolver(tools).Resolve(WineXeBuildToolchain.Default, temporary.Path, helpers));

        Assert.Equal(ExitCode.MissingPrerequisite, failure.Code);
        Assert.Equal("wine-wrapper-unsupported", failure.Kind);
        Assert.DoesNotContain(temporary.Path, failure.ToString(), StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(helpers));
    }

    [SupportedOSPlatform("linux")]
    [Fact]
    public void Named_internal_native_fixture_seam_registers_actual_graph_inputs_without_a_closure_or_runner_bypass()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temporary = new TemporaryDirectory();
        string tools = temporary.CreateDirectory("native-fixture");
        string wine = temporary.CreateExecutable("native-fixture/wine");
        string winepath = temporary.CreateExecutable("native-fixture/winepath");
        string server = temporary.CreateExecutable("native-fixture/wineserver");
        string helpers = temporary.CreateDirectory("native-fixture-helpers");

        WineXeBuildToolchain resolved = WineXeBuildToolchainResolver.CreateSyntheticNativeFixtureResolver(tools)
            .Resolve(WineXeBuildToolchain.Default, temporary.Path, helpers);

        // Resolution consumes the discovery request; TrustedChildPath still requires the real closure.
        Assert.False(resolved.RequiresTrustedPathDiscovery);
        Assert.True(resolved.UsesSyntheticNativeInputs);
        NativeDependencyClosureSpec inputs = Assert.IsType<NativeDependencyClosureSpec>(resolved.NativeInputs);
        Assert.Contains(wine, inputs.ExecutablePaths);
        Assert.Contains(winepath, inputs.ExecutablePaths);
        Assert.Contains(server, inputs.ExecutablePaths);
        Assert.Contains(helpers, inputs.ModuleDirectories);
        Assert.Equal(helpers, inputs.EnvironmentBindings["PATH"]);
        Assert.Null(resolved.TrustedNativeClosure); // The backend must still prepare the full real closure.
        Assert.True(WineXeBuildToolchain.Default.RequiresTrustedPathDiscovery);
        Assert.False(WineXeBuildToolchain.Default.UsesSyntheticNativeInputs);
        Assert.Same(resolved, new WineXeBuildToolchainResolver("never-rediscover\0").Resolve(resolved, "unused\0"));
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("basename")]
    [InlineData("cut")]
    [InlineData("sed")]
    [InlineData("dpkg")]
    public void Wine_vendor_utility_gate_rejects_protected_private_static_native_helpers_without_execution(string utility)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temporary = new TemporaryDirectory();
        string helper = temporary.CreateExecutable("private-Wine-utilities/" + utility);

        OperationFailureException failure = Assert.Throws<OperationFailureException>(() =>
            WineXeBuildToolchainResolver.ValidateVendorWineUtility(helper, utility));

        Assert.Equal(ExitCode.MissingPrerequisite, failure.Code);
        Assert.Equal("wine-wrapper-unsupported", failure.Kind);
        Assert.DoesNotContain(temporary.Path, failure.ToString(), StringComparison.Ordinal);
    }

    [SupportedOSPlatform("linux")]
    [Fact]
    public void Protected_current_uid_aliases_preserve_both_command_names_and_validate_their_shared_canonical_target()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        string target = TemporaryDirectory.GetCanonicalPath(CreateWineExecutable(temporaryDirectory, "canonical-target/wine-apploader"));
        string aliasDirectory = temporaryDirectory.CreateDirectory("protected-aliases");
        File.CreateSymbolicLink(Path.Combine(aliasDirectory, "wine"), "../canonical-target/wine-apploader");
        File.CreateSymbolicLink(Path.Combine(aliasDirectory, "winepath"), "../canonical-target/wine-apploader");
        string unrelatedDirectory = temporaryDirectory.CreateDirectory("unrelated-writable-PATH-entry");
        File.SetUnixFileMode(
            unrelatedDirectory,
            File.GetUnixFileMode(unrelatedDirectory) | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite | UnixFileMode.StickyBit);
        string searchPath = string.Join(Path.PathSeparator, unrelatedDirectory, "protected-aliases", aliasDirectory);
        var resolver = CreateResolver(searchPath, temporaryDirectory);

        WineXeBuildToolchain resolved = resolver.Resolve(WineXeBuildToolchain.Default, temporaryDirectory.Path, HelperDirectory(temporaryDirectory));

        string protectedAliasDirectory = TemporaryDirectory.GetCanonicalPath(aliasDirectory);
        Assert.NotSame(WineXeBuildToolchain.Default, resolved);
        Assert.Equal(Path.Combine(aliasDirectory, "wine"), resolved.WineExecutable);
        Assert.Equal(Path.Combine(aliasDirectory, "winepath"), resolved.WinePathExecutable);
        Assert.NotEqual(resolved.WineExecutable, resolved.WinePathExecutable);
        Assert.Equal(target, TemporaryDirectory.GetCanonicalPath(resolved.WineExecutable));
        Assert.Equal(target, TemporaryDirectory.GetCanonicalPath(resolved.WinePathExecutable));
        Assert.True(WineXeBuildToolchain.Default.RequiresTrustedPathDiscovery);
        Assert.Equal("wine", WineXeBuildToolchain.Default.WineExecutable);
        Assert.Equal("winepath", WineXeBuildToolchain.Default.WinePathExecutable);
        AssertTrustedChildPath(resolved, temporaryDirectory, unrelatedDirectory, protectedAliasDirectory, Path.GetDirectoryName(target)!);
        Assert.NotEqual(searchPath, resolved.TrustedChildPath);
        Assert.Same(resolved, resolver.Resolve(resolved, "never-rediscover\0"));
    }

    [SupportedOSPlatform("linux")]
    [Fact]
    public void Symlink_then_parent_segments_follow_kernel_semantics_instead_of_lexical_normalization()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        string physicalWine = TemporaryDirectory.GetCanonicalPath(CreateWineExecutable(temporaryDirectory, "physical/wine"));
        string physicalWinepath = TemporaryDirectory.GetCanonicalPath(CreateWineExecutable(temporaryDirectory, "physical/winepath"));
        string physicalChild = temporaryDirectory.CreateDirectory("physical/nested");
        string lexicalDirectory = temporaryDirectory.CreateDirectory("lexical");
        CreateWineExecutable(temporaryDirectory, "lexical/wine");
        CreateWineExecutable(temporaryDirectory, "lexical/winepath");
        Directory.CreateSymbolicLink(Path.Combine(lexicalDirectory, "link"), physicalChild);
        string searchPath = Path.Combine(lexicalDirectory, "link", "..");

        WineXeBuildToolchain resolved = CreateResolver(searchPath, temporaryDirectory)
            .Resolve(WineXeBuildToolchain.Default, temporaryDirectory.Path, HelperDirectory(temporaryDirectory));

        Assert.True(Path.IsPathFullyQualified(resolved.WineExecutable));
        Assert.True(Path.IsPathFullyQualified(resolved.WinePathExecutable));
        Assert.Equal("wine", Path.GetFileName(resolved.WineExecutable));
        Assert.Equal("winepath", Path.GetFileName(resolved.WinePathExecutable));
        Assert.Equal(physicalWine, TemporaryDirectory.GetCanonicalPath(resolved.WineExecutable));
        Assert.Equal(physicalWinepath, TemporaryDirectory.GetCanonicalPath(resolved.WinePathExecutable));
        AssertTrustedChildPath(resolved, temporaryDirectory, lexicalDirectory);
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData(":")]
    public void Empty_or_relative_PATH_entries_use_the_supplied_working_directory(string searchPath)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        string wine = TemporaryDirectory.GetCanonicalPath(CreateWineExecutable(temporaryDirectory, "wine"));
        string winepath = TemporaryDirectory.GetCanonicalPath(CreateWineExecutable(temporaryDirectory, "winepath"));

        WineXeBuildToolchain resolved = CreateResolver(searchPath, temporaryDirectory)
            .Resolve(WineXeBuildToolchain.Default, temporaryDirectory.Path, HelperDirectory(temporaryDirectory));

        string entry = searchPath == "." ? "." : string.Empty;
        Assert.Equal(Path.Join(temporaryDirectory.Path, entry, "wine"), resolved.WineExecutable);
        Assert.Equal(Path.Join(temporaryDirectory.Path, entry, "winepath"), resolved.WinePathExecutable);
        Assert.Equal(wine, TemporaryDirectory.GetCanonicalPath(resolved.WineExecutable));
        Assert.Equal(winepath, TemporaryDirectory.GetCanonicalPath(resolved.WinePathExecutable));
        AssertTrustedChildPath(resolved, temporaryDirectory);
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("wine")]
    [InlineData("winepath")]
    public void Execute_only_files_keep_metadata_trust_but_production_requires_an_inspectable_format(string executeOnlyRole)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        string wine = TemporaryDirectory.GetCanonicalPath(CreateWineExecutable(temporaryDirectory, "tools/wine"));
        string winepath = TemporaryDirectory.GetCanonicalPath(CreateWineExecutable(temporaryDirectory, "tools/winepath"));
        File.SetUnixFileMode(executeOnlyRole == "wine" ? wine : winepath, UnixFileMode.UserExecute);

        WineXeBuildToolchainResolver.ValidateProtectedExecutable(executeOnlyRole == "wine" ? wine : winepath);
        var resolver = CreateResolver(Path.GetDirectoryName(wine), temporaryDirectory);
        if (GetEffectiveUserId() != 0)
        {
            AssertUnsupportedWrapper(Assert.Throws<OperationFailureException>(() =>
                resolver.Resolve(WineXeBuildToolchain.Default, temporaryDirectory.Path, HelperDirectory(temporaryDirectory))),
                temporaryDirectory.Path, wine, winepath);
        }
        else
        {
            WineXeBuildToolchain resolved = resolver.Resolve(
                WineXeBuildToolchain.Default, temporaryDirectory.Path, HelperDirectory(temporaryDirectory));
            AssertTrustedChildPath(resolved, temporaryDirectory);
        }
    }

    [SupportedOSPlatform("linux")]
    [Fact]
    public void A_protected_root_owned_system_target_is_accepted_through_current_uid_aliases()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/bin/sh"))
        {
            return;
        }

        string systemTarget = TemporaryDirectory.GetCanonicalPath("/bin/sh");
        NativeStatx targetMetadata = ReadMetadata(systemTarget);
        if (targetMetadata.UserId != 0)
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        string aliases = temporaryDirectory.CreateDirectory("aliases");
        File.CreateSymbolicLink(Path.Combine(aliases, "wine"), "/bin/sh");
        File.CreateSymbolicLink(Path.Combine(aliases, "winepath"), "/bin/sh");

        temporaryDirectory.CreateExecutable("aliases/wineserver");
        WineXeBuildToolchain resolved = CreateResolver(aliases, temporaryDirectory)
            .Resolve(WineXeBuildToolchain.Default, temporaryDirectory.Path, HelperDirectory(temporaryDirectory));
        Assert.Equal(systemTarget, TemporaryDirectory.GetCanonicalPath(resolved.WineExecutable));
        Assert.Equal(systemTarget, TemporaryDirectory.GetCanonicalPath(resolved.WinePathExecutable));
        Assert.Equal("wine", Path.GetFileName(resolved.WineExecutable));
        Assert.Equal("winepath", Path.GetFileName(resolved.WinePathExecutable));
        AssertTrustedChildPath(resolved, temporaryDirectory, "/usr/bin", aliases);
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("wine-alias", UnixFileMode.GroupWrite)]
    [InlineData("wine-alias", UnixFileMode.OtherWrite)]
    [InlineData("winepath-alias", UnixFileMode.GroupWrite)]
    [InlineData("winepath-alias", UnixFileMode.OtherWrite)]
    [InlineData("wine-target", UnixFileMode.GroupWrite)]
    [InlineData("wine-target", UnixFileMode.OtherWrite)]
    [InlineData("winepath-target", UnixFileMode.GroupWrite)]
    [InlineData("winepath-target", UnixFileMode.OtherWrite)]
    public void Every_selected_Wine_alias_and_target_directory_rejects_a_writable_named_executable_helper(
        string helperLocation,
        UnixFileMode unsafeBits)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        string wineTarget = CreateWineExecutable(temporaryDirectory, "wine-target/apploader");
        string winepathTarget = CreateWineExecutable(temporaryDirectory, "winepath-target/apploader");
        string wineAliases = temporaryDirectory.CreateDirectory("wine-alias");
        string winepathAliases = temporaryDirectory.CreateDirectory("winepath-alias");
        File.CreateSymbolicLink(Path.Combine(wineAliases, "wine"), wineTarget);
        File.CreateSymbolicLink(Path.Combine(winepathAliases, "winepath"), winepathTarget);
        string helper = temporaryDirectory.CreateExecutable(Path.Combine(helperLocation, "wine-loader"));
        File.SetUnixFileMode(helper, File.GetUnixFileMode(helper) | unsafeBits);
        string searchPath = string.Join(Path.PathSeparator, wineAliases, winepathAliases);

        OperationFailureException failure = Assert.Throws<OperationFailureException>(() =>
            CreateResolver(searchPath, temporaryDirectory).Resolve(
                WineXeBuildToolchain.Default, temporaryDirectory.Path, HelperDirectory(temporaryDirectory)));

        AssertRedactedUnavailable(failure, "wine", temporaryDirectory.Path, searchPath, helper);
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("target", UnixFileMode.GroupWrite)]
    [InlineData("target", UnixFileMode.OtherWrite)]
    [InlineData("target-parent", UnixFileMode.GroupWrite)]
    [InlineData("target-parent", UnixFileMode.OtherWrite)]
    [InlineData("target-ancestor", UnixFileMode.GroupWrite)]
    [InlineData("target-ancestor", UnixFileMode.OtherWrite)]
    [InlineData("intermediary", UnixFileMode.GroupWrite)]
    [InlineData("intermediary", UnixFileMode.OtherWrite)]
    public void Executable_helper_symlinks_reject_writable_targets_and_every_target_or_intermediary_ancestor(
        string unsafeLocation,
        UnixFileMode unsafeBits)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        string tools = temporaryDirectory.CreateDirectory("tools");
        CreateWineExecutable(temporaryDirectory, "tools/wine");
        CreateWineExecutable(temporaryDirectory, "tools/winepath");
        string ancestor = temporaryDirectory.CreateDirectory("private-helper-target-secret");
        string parent = temporaryDirectory.CreateDirectory("private-helper-target-secret/commands");
        string target = temporaryDirectory.CreateExecutable("private-helper-target-secret/commands/loader");
        string intermediary = temporaryDirectory.CreateDirectory("private-helper-link-secret");
        string intermediateLink = Path.Combine(intermediary, "loader-link");
        File.CreateSymbolicLink(intermediateLink, target);
        File.CreateSymbolicLink(
            Path.Combine(tools, "wine-preloader"),
            unsafeLocation == "intermediary" ? intermediateLink : target);
        string unsafePath = unsafeLocation switch
        {
            "target" => target,
            "target-parent" => parent,
            "target-ancestor" => ancestor,
            _ => intermediary,
        };
        File.SetUnixFileMode(unsafePath, File.GetUnixFileMode(unsafePath) | unsafeBits);

        OperationFailureException failure = Assert.Throws<OperationFailureException>(() =>
            CreateResolver(tools, temporaryDirectory).Resolve(
                WineXeBuildToolchain.Default, temporaryDirectory.Path, HelperDirectory(temporaryDirectory)));

        AssertRedactedUnavailable(failure, "wine", temporaryDirectory.Path, tools, target, unsafePath);
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("leaf")]
    [InlineData("alias")]
    [InlineData("target")]
    [InlineData("target-parent")]
    [InlineData("target-ancestor")]
    [InlineData("intermediate-link")]
    public void Executable_helpers_reject_foreign_owners_on_leaves_links_targets_and_ancestry(string unsafeLocation)
    {
        if (!OperatingSystem.IsLinux() || GetEffectiveUserId() != 0)
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        string tools = temporaryDirectory.CreateDirectory("tools");
        CreateWineExecutable(temporaryDirectory, "tools/wine");
        CreateWineExecutable(temporaryDirectory, "tools/winepath");
        string ancestor = temporaryDirectory.CreateDirectory("private-foreign-helper-secret");
        string parent = temporaryDirectory.CreateDirectory("private-foreign-helper-secret/commands");
        string target = temporaryDirectory.CreateExecutable("private-foreign-helper-secret/commands/loader");
        string intermediateLink = Path.Combine(ancestor, "loader-link");
        File.CreateSymbolicLink(intermediateLink, target);
        string helper = Path.Combine(tools, "wine-loader");
        if (unsafeLocation == "leaf")
        {
            temporaryDirectory.CreateExecutable("tools/wine-loader");
        }
        else
        {
            File.CreateSymbolicLink(helper, unsafeLocation == "intermediate-link" ? intermediateLink : target);
        }
        string unsafePath = unsafeLocation switch
        {
            "leaf" or "alias" => helper,
            "target" => target,
            "target-parent" => parent,
            "target-ancestor" => ancestor,
            _ => intermediateLink,
        };
        Assert.Equal(0, ChangeOwnerNoFollow(unsafePath, userId: 65534, groupId: uint.MaxValue));
        try
        {
            OperationFailureException failure = Assert.Throws<OperationFailureException>(() =>
                CreateResolver(tools, temporaryDirectory).Resolve(
                    WineXeBuildToolchain.Default, temporaryDirectory.Path, HelperDirectory(temporaryDirectory)));

            AssertRedactedUnavailable(failure, "wine", temporaryDirectory.Path, unsafePath);
        }
        finally
        {
            Assert.Equal(0, ChangeOwnerNoFollow(unsafePath, userId: 0, groupId: uint.MaxValue));
        }
    }

    [SupportedOSPlatform("linux")]
    [Fact]
    public void Protected_execute_only_helpers_and_aliases_succeed_without_opening_non_command_special_files()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        string tools = temporaryDirectory.CreateDirectory("tools");
        temporaryDirectory.CreateExecutable("tools/wine");
        temporaryDirectory.CreateExecutable("tools/winepath");
        string helper = temporaryDirectory.CreateExecutable("tools/execute-only-helper");
        File.SetUnixFileMode(helper, UnixFileMode.UserExecute);
        File.CreateSymbolicLink(Path.Combine(tools, "helper-alias"), "execute-only-helper");
        Assert.Equal(0, MakeFifo(Path.Combine(tools, "not-a-command-fifo"), 0x1C0));
        string data = temporaryDirectory.CreateExecutable("tools/not-executable-data");
        File.SetUnixFileMode(data, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupWrite);
        string nestedHelper = temporaryDirectory.CreateExecutable("tools/not-a-command-directory/nested-helper");
        File.SetUnixFileMode(nestedHelper, File.GetUnixFileMode(nestedHelper) | UnixFileMode.OtherWrite);

        WineXeBuildToolchainResolver.ValidateProtectedPathHelpers(tools);
        WineXeBuildToolchainResolver.ValidateProtectedExecutable(helper);
        WineXeBuildToolchainResolver.ValidateProtectedExecutable(Path.Combine(tools, "helper-alias"));
    }

    [SupportedOSPlatform("linux")]
    [Fact]
    public void A_protected_root_owned_system_helper_target_is_accepted_from_a_trusted_child_PATH_alias()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/bin/sh"))
        {
            return;
        }
        string target = TemporaryDirectory.GetCanonicalPath("/bin/sh");
        if (ReadMetadata(target).UserId != 0)
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        string tools = temporaryDirectory.CreateDirectory("tools");
        CreateWineExecutable(temporaryDirectory, "tools/wine");
        CreateWineExecutable(temporaryDirectory, "tools/winepath");
        string helperAlias = Path.Combine(tools, "wine-preloader");
        File.CreateSymbolicLink(helperAlias, "/bin/sh");

        WineXeBuildToolchain resolved = CreateResolver(tools, temporaryDirectory)
            .Resolve(WineXeBuildToolchain.Default, temporaryDirectory.Path, HelperDirectory(temporaryDirectory));

        Assert.Equal(target, TemporaryDirectory.GetCanonicalPath(helperAlias));
        AssertTrustedChildPath(resolved, temporaryDirectory);
    }

    [SupportedOSPlatform("linux")]
    [Fact]
    public void Helper_closure_does_not_scan_unrelated_caller_PATH_entries_or_their_unsafe_and_dangling_links()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        string tools = temporaryDirectory.CreateDirectory("tools");
        CreateWineExecutable(temporaryDirectory, "tools/wine");
        CreateWineExecutable(temporaryDirectory, "tools/winepath");
        string unrelated = temporaryDirectory.CreateDirectory("private-unrelated-PATH-secret");
        string unsafeHelper = temporaryDirectory.CreateExecutable("private-unrelated-PATH-secret/helper");
        File.SetUnixFileMode(unsafeHelper, File.GetUnixFileMode(unsafeHelper) | UnixFileMode.OtherWrite);
        File.CreateSymbolicLink(Path.Combine(unrelated, "unsafe-helper-link"), unsafeHelper);
        File.CreateSymbolicLink(Path.Combine(unrelated, "dangling-helper-link"), "missing-target");
        File.SetUnixFileMode(unrelated, File.GetUnixFileMode(unrelated) | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite);
        string searchPath = string.Join(Path.PathSeparator, unrelated, tools);

        WineXeBuildToolchain resolved = CreateResolver(searchPath, temporaryDirectory)
            .Resolve(WineXeBuildToolchain.Default, temporaryDirectory.Path, HelperDirectory(temporaryDirectory));

        Assert.Equal(Path.Combine(tools, "wine"), resolved.WineExecutable);
        Assert.Equal(Path.Combine(tools, "winepath"), resolved.WinePathExecutable);
        AssertTrustedChildPath(resolved, temporaryDirectory, unrelated);
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("writable-executable")]
    [InlineData("unsafe-target-ancestry")]
    [InlineData("dangling-plantable-alias")]
    [InlineData("directory")]
    [InlineData("fifo")]
    public void Curated_PATH_excludes_unrelated_unsafe_source_entries_instead_of_trusting_or_scanning_them(string entryKind)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        string tools = temporaryDirectory.CreateDirectory("tools");
        CreateWineExecutable(temporaryDirectory, "tools/wine");
        CreateWineExecutable(temporaryDirectory, "tools/winepath");
        string unrelated = Path.Join(tools, "adb");
        string targetDirectory = temporaryDirectory.CreateDirectory("private-unrelated-target-secret");
        switch (entryKind)
        {
            case "writable-executable":
                temporaryDirectory.CreateExecutable("tools/adb");
                File.SetUnixFileMode(unrelated, File.GetUnixFileMode(unrelated) | UnixFileMode.OtherWrite);
                break;
            case "unsafe-target-ancestry":
                string target = temporaryDirectory.CreateExecutable("private-unrelated-target-secret/adb");
                File.CreateSymbolicLink(unrelated, target);
                break;
            case "dangling-plantable-alias":
                File.CreateSymbolicLink(unrelated, Path.Join(targetDirectory, "missing", "adb"));
                break;
            case "directory":
                temporaryDirectory.CreateDirectory("tools/adb");
                break;
            case "fifo":
                Assert.Equal(0, MakeFifo(unrelated, 0x1C0));
                break;
        }
        File.SetUnixFileMode(targetDirectory, File.GetUnixFileMode(targetDirectory) | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite);
        var resolver = CreateResolver(tools, temporaryDirectory);
        WineXeBuildToolchain resolved = resolver.Resolve(
            WineXeBuildToolchain.Default, temporaryDirectory.Path, HelperDirectory(temporaryDirectory));

        AssertTrustedChildPath(resolved, temporaryDirectory, tools);
        Assert.False(File.Exists(Path.Join(resolved.TrustedChildPath!, "adb")));
        Assert.DoesNotContain("adb",
            Directory.EnumerateFileSystemEntries(resolved.TrustedChildPath!).Select(Path.GetFileName));
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("wine-loader", "writable-target")]
    [InlineData("wine-preloader", "writable-ancestor")]
    [InlineData("wineserver", "writable-target")]
    [InlineData("wineserver", "dangling-plantable-target")]
    public void Curated_discovery_rejects_unsafe_named_loader_and_server_aliases_before_materialization(
        string name, string unsafeKind)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        string tools = temporaryDirectory.CreateDirectory("tools");
        CreateWineExecutable(temporaryDirectory, "tools/wine");
        CreateWineExecutable(temporaryDirectory, "tools/winepath");
        string targets = temporaryDirectory.CreateDirectory("private-named-target-secret");
        string target = Path.Join(targets, "executable");
        if (unsafeKind != "dangling-plantable-target")
        {
            temporaryDirectory.CreateExecutable("private-named-target-secret/executable");
        }
        string alias = Path.Join(tools, name);
        if (File.Exists(alias))
        {
            File.Delete(alias);
        }
        File.CreateSymbolicLink(alias, target);
        string unsafePath = unsafeKind == "writable-target" ? target : targets;
        File.SetUnixFileMode(unsafePath, File.GetUnixFileMode(unsafePath) | UnixFileMode.OtherWrite);
        var resolver = CreateResolver(tools, temporaryDirectory);

        OperationFailureException failure = Assert.Throws<OperationFailureException>(() => resolver.Resolve(
            WineXeBuildToolchain.Default, temporaryDirectory.Path, HelperDirectory(temporaryDirectory)));
        AssertRedactedUnavailable(failure, "wine", temporaryDirectory.Path, target, unsafePath);
        Assert.Empty(Directory.EnumerateFileSystemEntries(HelperDirectory(temporaryDirectory)));
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("")]
    [InlineData("-stable")]
    [InlineData("-development")]
    public void Supported_installed_Debian_apploaders_preserve_argv0_and_materialize_only_their_finite_utilities(string suffix)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        string tools = temporaryDirectory.CreateDirectory("tools");
        string wine = CreateWineExecutable(temporaryDirectory, "tools/wine");
        string winepath = CreateWineExecutable(temporaryDirectory, string.Concat("tools/winepath", suffix));
        if (suffix.Length != 0)
        {
            File.CreateSymbolicLink(Path.Join(tools, string.Concat("wine", suffix)), wine);
            File.CreateSymbolicLink(Path.Join(tools, "winepath"), winepath);
        }
        // Short generic installed-package dispatch plumbing; primary source is pinned in the resolver.
        File.WriteAllText(winepath,
            "#!/bin/sh -e\n\n# installed app-loader fixture\nappname=$(basename \"$0\" .exe)\n" +
            "name=$(echo $appname | cut -d- -f1)\n\n" +
            $"exec wine{suffix} \"$name.exe\" \"$@\"\n");
        string unrelated = temporaryDirectory.CreateExecutable("tools/adb");
        File.SetUnixFileMode(unrelated, File.GetUnixFileMode(unrelated) | UnixFileMode.GroupWrite);
        var toolchain = WineXeBuildToolchain.Default with { WinePathExecutable = winepath };
        WineXeBuildToolchain resolved = CreateResolver(tools, temporaryDirectory).Resolve(
            toolchain, temporaryDirectory.Path, HelperDirectory(temporaryDirectory));

        Assert.Equal(winepath, resolved.WinePathExecutable);
        AssertTrustedChildPath(resolved, temporaryDirectory, tools);
        Assert.True(File.Exists(Path.Join(resolved.TrustedChildPath!, "basename")));
        Assert.True(File.Exists(Path.Join(resolved.TrustedChildPath!, "cut")));
        Assert.True(File.Exists(Path.Join(resolved.TrustedChildPath!, string.Concat("wine", suffix))));
        foreach (string excluded in new[] { "adb", "cat", "env", "sh", "sed", "dirname" })
        {
            Assert.False(File.Exists(Path.Join(resolved.TrustedChildPath!, excluded)));
        }
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("launcher64")]
    [InlineData("launcher32")]
    public void Supported_literal_loader_wrappers_bind_their_native_target_and_reject_target_replacement(string wrapperKind)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        string tools = temporaryDirectory.CreateDirectory("tools");
        string native = CreateWineExecutable(temporaryDirectory, "private-native-target-secret/native-wine");
        string wine = CreateWineExecutable(temporaryDirectory, "tools/wine");
        CreateWineExecutable(temporaryDirectory, "tools/winepath");
        string prefix = wrapperKind == "launcher32"
            ? "#!/bin/sh -e\nif test -z \"$WINEPREFIX\"; then\nexport WINEPREFIX=\"$HOME/.wine32\"\nfi\n"
            : "#!/bin/sh -e\n";
        File.WriteAllText(wine, string.Concat(prefix, "exec ", native, " \"$@\"\n"));
        WineXeBuildToolchain resolved = CreateResolver(tools, temporaryDirectory).Resolve(
            WineXeBuildToolchain.Default, temporaryDirectory.Path, HelperDirectory(temporaryDirectory));
        Assert.Equal(wine, resolved.WineExecutable);
        AssertTrustedChildPath(resolved, temporaryDirectory, tools, Path.GetDirectoryName(native)!);

        File.SetUnixFileMode(native, File.GetUnixFileMode(native) | UnixFileMode.OtherWrite);
        string otherHelpers = temporaryDirectory.CreateDirectory("second-private-helper-directory");
        OperationFailureException failure = Assert.Throws<OperationFailureException>(() =>
            WineXeBuildToolchainResolver.CreateSyntheticNativeFixtureResolver(tools).Resolve(WineXeBuildToolchain.Default, temporaryDirectory.Path, otherHelpers));
        AssertRedactedUnavailable(failure, "wine", temporaryDirectory.Path, native);
        Assert.Empty(Directory.EnumerateFileSystemEntries(otherHelpers));
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("arbitrary-shell")]
    [InlineData("build-tree-shell")]
    [InlineData("generated-winegcc-shell")]
    [InlineData("substituted-command")]
    [InlineData("malformed-ELF")]
    [InlineData("crlf-shebang")]
    [InlineData("static-preloader-as-wine")]
    public void Unsupported_shell_generated_build_tree_and_native_formats_fail_with_a_redacted_prerequisite(string kind)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        string tools = temporaryDirectory.CreateDirectory("private-unsupported-wrapper-secret");
        string wine = CreateWineExecutable(temporaryDirectory, "private-unsupported-wrapper-secret/wine");
        CreateWineExecutable(temporaryDirectory, "private-unsupported-wrapper-secret/winepath");
        if (kind == "malformed-ELF")
        {
            File.WriteAllBytes(wine, [0x7F, (byte)'E', (byte)'L', (byte)'F']);
        }
        else
        {
            string script = kind switch
            {
                "build-tree-shell" => "#!/bin/sh\n. \"$HOME/private-source-secret/.winewrapper\"\nexec \"$WINELOADER\" \"$@\"\n",
                "generated-winegcc-shell" => "#!/bin/sh\nappname=\"winepath.exe\"\nWINEDLLPATH=\"$HOME:$WINEDLLPATH\"\nexec wine \"$appname\" \"$@\"\n",
                "substituted-command" => "#!/bin/sh -e\nexec /private/native-$(id) \"$@\"\n",
                "crlf-shebang" => "#!/bin/sh -e\r\nexec " + Path.Join(tools, "winepath") + " \"$@\"\n",
                "static-preloader-as-wine" => "#!/bin/sh\n\necho wine-preloader is not supported on this architecture\nexit 1\n",
                _ => "#!/bin/sh\nexec private-unknown-helper-secret \"$@\"\n",
            };
            File.WriteAllText(wine, script);
        }
        var resolver = CreateResolver(tools, temporaryDirectory);
        OperationFailureException first = Assert.Throws<OperationFailureException>(() => resolver.Resolve(
            WineXeBuildToolchain.Default, temporaryDirectory.Path, HelperDirectory(temporaryDirectory)));
        OperationFailureException second = Assert.Throws<OperationFailureException>(() => resolver.Resolve(
            WineXeBuildToolchain.Default, temporaryDirectory.Path, HelperDirectory(temporaryDirectory)));
        AssertUnsupportedWrapper(first, temporaryDirectory.Path, wine, "private-unknown-helper-secret", "private-source-secret");
        AssertUnsupportedWrapper(second, temporaryDirectory.Path, wine);
        Assert.Equal(first.Message, second.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(HelperDirectory(temporaryDirectory)));
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("loader/wine")]
    [InlineData("tools/wine/wine")]
    public void Recognizable_native_build_tree_loaders_are_rejected_without_a_source_tree_helper_fallback(string relativeWine)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temporaryDirectory = new TemporaryDirectory();
        string wine = CreateWineExecutable(temporaryDirectory, relativeWine);
        string winepath = CreateWineExecutable(temporaryDirectory, "installed/winepath");
        var toolchain = WineXeBuildToolchain.Default with { WineExecutable = wine, WinePathExecutable = winepath };
        var resolver = CreateResolver(string.Empty, temporaryDirectory);

        OperationFailureException failure = Assert.Throws<OperationFailureException>(() =>
            resolver.Resolve(toolchain, temporaryDirectory.Path, HelperDirectory(temporaryDirectory)));
        AssertUnsupportedWrapper(failure, temporaryDirectory.Path, wine);
        Assert.Empty(Directory.EnumerateFileSystemEntries(HelperDirectory(temporaryDirectory)));
    }

    [SupportedOSPlatform("linux")]
    [Fact]
    public void Native_installed_architecture_loaders_validate_early_prefix_bin_server_probes()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temporaryDirectory = new TemporaryDirectory();
        string wine = CreateWineExecutable(temporaryDirectory, "installation/lib/wine/x86_64-unix/wine");
        string winepath = CreateWineExecutable(temporaryDirectory, "installed/winepath");
        string earlyServer = temporaryDirectory.CreateExecutable("installation/bin/wineserver");
        File.SetUnixFileMode(earlyServer, File.GetUnixFileMode(earlyServer) | UnixFileMode.GroupWrite);
        var toolchain = WineXeBuildToolchain.Default with { WineExecutable = wine, WinePathExecutable = winepath };
        var resolver = CreateResolver(string.Empty, temporaryDirectory);

        OperationFailureException failure = Assert.Throws<OperationFailureException>(() =>
            resolver.Resolve(toolchain, temporaryDirectory.Path, HelperDirectory(temporaryDirectory)));
        AssertRedactedUnavailable(failure, "wine", temporaryDirectory.Path, earlyServer);
        Assert.Empty(Directory.EnumerateFileSystemEntries(HelperDirectory(temporaryDirectory)));
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("missing")]
    [InlineData("relative")]
    [InlineData("colon")]
    [InlineData("public-readable")]
    [InlineData("cross-uid-writable")]
    [InlineData("nonempty")]
    [InlineData("file")]
    public void Production_discovery_requires_a_precreated_empty_owner_private_helper_directory(string invalidKind)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temporaryDirectory = new TemporaryDirectory();
        string tools = temporaryDirectory.CreateDirectory("tools");
        CreateWineExecutable(temporaryDirectory, "tools/wine");
        CreateWineExecutable(temporaryDirectory, "tools/winepath");
        string helpers = Path.Join(temporaryDirectory.Path, "private-helper-directory-secret");
        switch (invalidKind)
        {
            case "relative":
                helpers = "private-relative-helper-secret";
                break;
            case "colon":
                helpers = temporaryDirectory.CreateDirectory("private-helper:directory-secret");
                break;
            case "public-readable":
            case "cross-uid-writable":
                temporaryDirectory.CreateDirectory("private-helper-directory-secret");
                File.SetUnixFileMode(helpers, File.GetUnixFileMode(helpers) |
                    (invalidKind == "public-readable" ? UnixFileMode.OtherRead : UnixFileMode.OtherWrite));
                break;
            case "nonempty":
                temporaryDirectory.CreateDirectory("private-helper-directory-secret");
                temporaryDirectory.CreateExecutable("private-helper-directory-secret/preexisting-private-helper-secret");
                break;
            case "file":
                temporaryDirectory.CreateExecutable("private-helper-directory-secret");
                break;
        }
        var resolver = WineXeBuildToolchainResolver.CreateSyntheticNativeFixtureResolver(tools);
        OperationFailureException failure = Assert.Throws<OperationFailureException>(() =>
            resolver.Resolve(WineXeBuildToolchain.Default, temporaryDirectory.Path, helpers));
        AssertRedactedUnavailable(failure, "wine", temporaryDirectory.Path, helpers, "preexisting-private-helper-secret");
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("-stable", false)]
    [InlineData("-stable", true)]
    [InlineData("-development", false)]
    [InlineData("-development", true)]
    [InlineData("-staging", false)]
    [InlineData("-staging", true)]
    public void Server_PATH_fallback_matches_the_selected_package_suffix_instead_of_another_installed_version(
        string suffix, bool bareServerPresent)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temporaryDirectory = new TemporaryDirectory();
        string tools = temporaryDirectory.CreateDirectory("tools");
        string wine = CreateWineExecutable(temporaryDirectory, string.Concat("tools/wine", suffix));
        string winepath = CreateWineExecutable(temporaryDirectory, "tools/winepath");
        string selectedServer = temporaryDirectory.CreateExecutable(string.Concat("tools/wineserver", suffix));
        string otherSuffix = suffix == "-stable" ? "-development" : "-stable";
        temporaryDirectory.CreateExecutable(string.Concat("tools/wineserver", otherSuffix));
        if (!bareServerPresent)
        {
            File.Delete(Path.Join(tools, "wineserver"));
        }
        var toolchain = WineXeBuildToolchain.Default with { WineExecutable = wine, WinePathExecutable = winepath };
        WineXeBuildToolchain resolved = CreateResolver(string.Empty, temporaryDirectory).Resolve(
            toolchain, temporaryDirectory.Path, HelperDirectory(temporaryDirectory));

        AssertTrustedChildPath(resolved, temporaryDirectory, tools);
        Assert.Equal(selectedServer, new FileInfo(Path.Join(resolved.TrustedChildPath!, "wineserver")).LinkTarget);
    }

    [SupportedOSPlatform("linux")]
    [Fact]
    public void Installed_static_preloader_shims_are_allowed_as_optional_helpers_without_exposing_an_interpreter_on_PATH()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temporaryDirectory = new TemporaryDirectory();
        string tools = temporaryDirectory.CreateDirectory("tools");
        CreateWineExecutable(temporaryDirectory, "tools/wine");
        CreateWineExecutable(temporaryDirectory, "tools/winepath");
        string preloader = temporaryDirectory.CreateExecutable("tools/wine-preloader");
        File.WriteAllText(preloader,
            "#!/bin/sh\n\necho wine-preloader is not supported on this architecture\nexit 1\n");
        WineXeBuildToolchain resolved = CreateResolver(tools, temporaryDirectory).Resolve(
            WineXeBuildToolchain.Default, temporaryDirectory.Path, HelperDirectory(temporaryDirectory));

        AssertTrustedChildPath(resolved, temporaryDirectory, tools);
        Assert.Equal(preloader, new FileInfo(Path.Join(resolved.TrustedChildPath!, "wine-preloader")).LinkTarget);
        Assert.False(File.Exists(Path.Join(resolved.TrustedChildPath!, "sh")));
        Assert.False(File.Exists(Path.Join(resolved.TrustedChildPath!, "env")));
    }

    [SupportedOSPlatform("linux")]
    [Fact]
    public void Supported_wrapper_utilities_are_protected_dependencies_not_unvalidated_system_PATH_entries()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temporaryDirectory = new TemporaryDirectory();
        string tools = temporaryDirectory.CreateDirectory("tools");
        CreateWineExecutable(temporaryDirectory, "tools/wine");
        string winepath = CreateWineExecutable(temporaryDirectory, "tools/winepath");
        File.WriteAllText(winepath,
            "#!/bin/sh -e\nappname=$(basename \"$0\" .exe)\nname=$(echo $appname | cut -d- -f1)\n" +
            "exec wine \"$name.exe\" \"$@\"\n");
        string cut = temporaryDirectory.CreateExecutable("tools/cut");
        File.SetUnixFileMode(cut, File.GetUnixFileMode(cut) | UnixFileMode.OtherWrite);
        var resolver = CreateResolver(tools, temporaryDirectory);
        OperationFailureException failure = Assert.Throws<OperationFailureException>(() =>
            resolver.Resolve(WineXeBuildToolchain.Default, temporaryDirectory.Path, HelperDirectory(temporaryDirectory)));
        AssertRedactedUnavailable(failure, "wine", temporaryDirectory.Path, cut);
        Assert.Empty(Directory.EnumerateFileSystemEntries(HelperDirectory(temporaryDirectory)));
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("invalid-target-bytes")]
    [InlineData("overlong-target-component")]
    public void Curated_named_helpers_preserve_redacted_native_target_decoding_and_lookup_failures(string failureKind)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var temporaryDirectory = new TemporaryDirectory();
        string tools = temporaryDirectory.CreateDirectory("tools");
        CreateWineExecutable(temporaryDirectory, "tools/wine");
        CreateWineExecutable(temporaryDirectory, "tools/winepath");
        string helper = Path.Join(tools, "wine-loader");
        if (failureKind == "invalid-target-bytes")
        {
            Assert.Equal(0, CreateNativeSymlink([0xFF, 0], helper));
        }
        else
        {
            File.CreateSymbolicLink(helper, new string('x', 256));
        }
        var resolver = CreateResolver(tools, temporaryDirectory);
        OperationFailureException failure = Assert.Throws<OperationFailureException>(() =>
            resolver.Resolve(WineXeBuildToolchain.Default, temporaryDirectory.Path, HelperDirectory(temporaryDirectory)));
        AssertRedactedUnavailable(failure, "wine", temporaryDirectory.Path, helper);
        Assert.Empty(Directory.EnumerateFileSystemEntries(HelperDirectory(temporaryDirectory)));
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("non-executable-alias")]
    [InlineData("fifo-alias")]
    [InlineData("directory-alias")]
    [InlineData("dangling")]
    [InlineData("dangling-suffix")]
    [InlineData("symlink-loop")]
    [InlineData("non-directory-suffix")]
    public void PATH_helper_filter_skips_unexecutable_bound_targets_without_relaxing_dependency_tree_validation(string targetKind)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        string tools = temporaryDirectory.CreateDirectory("tools");
        string targets = temporaryDirectory.CreateDirectory("targets");
        string helper = Path.Combine(tools, "helper");
        string target = Path.Combine(targets, "target");
        switch (targetKind)
        {
            case "non-executable-alias":
            case "non-directory-suffix":
                temporaryDirectory.CreateExecutable("targets/target");
                File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupWrite);
                break;
            case "fifo-alias":
                Assert.Equal(0, MakeFifo(target, 0x1C0));
                break;
            case "directory-alias":
                temporaryDirectory.CreateDirectory("targets/target");
                string nested = temporaryDirectory.CreateExecutable("targets/target/unsafe-nested-command");
                File.SetUnixFileMode(nested, File.GetUnixFileMode(nested) | UnixFileMode.OtherWrite);
                File.SetUnixFileMode(target, File.GetUnixFileMode(target) | UnixFileMode.OtherWrite);
                break;
        }
        File.CreateSymbolicLink(helper, targetKind switch
        {
            "dangling-suffix" => Path.Join(target, "not-created", "command"),
            "symlink-loop" => "helper",
            "non-directory-suffix" => Path.Join(target, "command"),
            _ => target,
        });

        WineXeBuildToolchainResolver.ValidateProtectedPathHelpers(tools);
        OperationFailureException failure = Assert.Throws<OperationFailureException>(() =>
            WineXeBuildToolchainResolver.ValidateProtectedTree(tools));
        AssertRedactedUnavailable(failure, "wine", temporaryDirectory.Path, tools, target);
    }

    [SupportedOSPlatform("linux")]
    [Fact]
    public void Protected_regular_helper_without_effective_execute_access_is_not_a_PATH_command()
    {
        if (!OperatingSystem.IsLinux() || GetEffectiveUserId() == 0)
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        string tools = temporaryDirectory.CreateDirectory("tools");
        string helper = temporaryDirectory.CreateExecutable("tools/helper");
        File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupExecute);
        File.CreateSymbolicLink(Path.Combine(tools, "helper-alias"), "helper");

        WineXeBuildToolchainResolver.ValidateProtectedPathHelpers(tools);
        AssertRedactedUnavailable(Assert.Throws<OperationFailureException>(() =>
            WineXeBuildToolchainResolver.ValidateProtectedExecutable(helper)), "wine", temporaryDirectory.Path, helper);
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("dangling", "target-parent", UnixFileMode.GroupWrite)]
    [InlineData("dangling", "target-parent", UnixFileMode.OtherWrite)]
    [InlineData("dangling", "target-parent", UnixFileMode.OtherWrite | UnixFileMode.StickyBit)]
    [InlineData("dangling", "target-ancestor", UnixFileMode.OtherWrite)]
    [InlineData("dangling", "intermediary", UnixFileMode.GroupWrite)]
    [InlineData("non-executable", "target-parent", UnixFileMode.OtherWrite)]
    [InlineData("fifo", "target-parent", UnixFileMode.GroupWrite)]
    [InlineData("directory", "target-parent", UnixFileMode.OtherWrite)]
    public void Unexecutable_helper_aliases_fail_when_another_UID_can_plant_a_future_executable(
        string targetKind, string unsafeLocation, UnixFileMode unsafeBits)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        string tools = temporaryDirectory.CreateDirectory("tools");
        string ancestor = temporaryDirectory.CreateDirectory("private-plantable-target-secret");
        string parent = temporaryDirectory.CreateDirectory("private-plantable-target-secret/commands");
        string target = Path.Combine(parent, "target");
        switch (targetKind)
        {
            case "non-executable":
                temporaryDirectory.CreateExecutable("private-plantable-target-secret/commands/target");
                File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                break;
            case "fifo":
                Assert.Equal(0, MakeFifo(target, 0x1C0));
                break;
            case "directory":
                temporaryDirectory.CreateDirectory("private-plantable-target-secret/commands/target");
                break;
        }
        string intermediary = temporaryDirectory.CreateDirectory("private-plantable-link-secret");
        string intermediateLink = Path.Combine(intermediary, "target-link");
        File.CreateSymbolicLink(intermediateLink, target);
        File.CreateSymbolicLink(Path.Combine(tools, "helper"), unsafeLocation == "intermediary" ? intermediateLink : target);
        string unsafePath = unsafeLocation switch
        {
            "target-parent" => parent,
            "target-ancestor" => ancestor,
            _ => intermediary,
        };
        File.SetUnixFileMode(unsafePath, File.GetUnixFileMode(unsafePath) | unsafeBits);

        OperationFailureException first = Assert.Throws<OperationFailureException>(() =>
            WineXeBuildToolchainResolver.ValidateProtectedPathHelpers(tools));
        OperationFailureException second = Assert.Throws<OperationFailureException>(() =>
            WineXeBuildToolchainResolver.ValidateProtectedPathHelpers(tools));
        AssertRedactedUnavailable(first, "wine", temporaryDirectory.Path, tools, target, unsafePath);
        AssertRedactedUnavailable(second, "wine", temporaryDirectory.Path, tools, target, unsafePath);
        Assert.Equal(first.Message, second.Message);
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("non-executable-leaf")]
    [InlineData("non-executable-target")]
    [InlineData("dangling-alias")]
    [InlineData("dangling-target-parent")]
    public void PATH_helpers_reject_foreign_ownership_that_can_make_a_noncommand_executable(string unsafeLocation)
    {
        if (!OperatingSystem.IsLinux() || GetEffectiveUserId() != 0)
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        string tools = temporaryDirectory.CreateDirectory("tools");
        string targets = temporaryDirectory.CreateDirectory("targets");
        string helper = Path.Combine(tools, "helper");
        string target = Path.Combine(targets, "target");
        if (unsafeLocation == "non-executable-leaf")
        {
            temporaryDirectory.CreateExecutable("tools/helper");
            File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        else
        {
            if (unsafeLocation == "non-executable-target")
            {
                temporaryDirectory.CreateExecutable("targets/target");
                File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            File.CreateSymbolicLink(helper, target);
        }
        string unsafePath = unsafeLocation switch
        {
            "non-executable-target" => target,
            "dangling-target-parent" => targets,
            _ => helper,
        };
        Assert.Equal(0, ChangeOwnerNoFollow(unsafePath, userId: 65534, groupId: uint.MaxValue));
        try
        {
            OperationFailureException failure = Assert.Throws<OperationFailureException>(() =>
                WineXeBuildToolchainResolver.ValidateProtectedPathHelpers(tools));
            AssertRedactedUnavailable(failure, "wine", temporaryDirectory.Path, tools, unsafePath);
        }
        finally
        {
            Assert.Equal(0, ChangeOwnerNoFollow(unsafePath, userId: 0, groupId: uint.MaxValue));
        }
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("overlong-target")]
    [InlineData("invalid-native-target")]
    [InlineData("invalid-native-name")]
    public void Uninspectable_helper_aliases_or_native_names_fail_with_stable_redacted_errors(string entryKind)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        string tools = temporaryDirectory.CreateDirectory("private-helper-PATH-secret");
        temporaryDirectory.CreateExecutable("private-helper-PATH-secret/wine");
        temporaryDirectory.CreateExecutable("private-helper-PATH-secret/winepath");
        string helper = Path.Combine(tools, "private-helper-secret");
        byte[]? nativeName = null;
        if (entryKind == "invalid-native-name")
        {
            byte[] prefix = System.Text.Encoding.UTF8.GetBytes(helper);
            nativeName = new byte[prefix.Length + 2];
            prefix.CopyTo(nativeName, 0);
            nativeName[^2] = 0xFF;
            int descriptor = OpenNativeFile(nativeName, 0x800C1, 0x1C0); // O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC, 0700.
            Assert.True(descriptor >= 0);
            Assert.Equal(0, CloseNativeFile(descriptor));
        }
        else if (entryKind == "invalid-native-target")
        {
            Assert.Equal(0, CreateNativeSymlink([0xFF, 0], helper));
        }
        else
        {
            File.CreateSymbolicLink(helper, new string('s', 300));
        }

        try
        {
            OperationFailureException first = Assert.Throws<OperationFailureException>(() =>
                WineXeBuildToolchainResolver.ValidateProtectedPathHelpers(tools));
            OperationFailureException second = Assert.Throws<OperationFailureException>(() =>
                WineXeBuildToolchainResolver.ValidateProtectedPathHelpers(tools));

            AssertRedactedUnavailable(first, "wine", temporaryDirectory.Path, tools, helper);
            AssertRedactedUnavailable(second, "wine", temporaryDirectory.Path, tools, helper);
            Assert.Equal(first.Message, second.Message);
            Assert.DoesNotContain("private-helper-secret", first.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            if (nativeName is not null)
            {
                Assert.Equal(0, UnlinkNativeFile(nativeName));
            }
        }
    }

    public static TheoryData<string, string, UnixFileMode> WritableExecutableCases()
    {
        var cases = new TheoryData<string, string, UnixFileMode>();
        foreach (string role in new[] { "wine", "winepath" })
        {
            foreach (string location in new[] { "executable", "parent", "ancestor" })
            {
                foreach (UnixFileMode unsafeBits in new[]
                {
                    UnixFileMode.GroupWrite,
                    UnixFileMode.OtherWrite,
                    UnixFileMode.GroupWrite | UnixFileMode.StickyBit,
                    UnixFileMode.OtherWrite | UnixFileMode.StickyBit,
                })
                {
                    cases.Add(role, location, unsafeBits);
                }
            }
        }

        return cases;
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [MemberData(nameof(WritableExecutableCases))]
    public void Found_executable_with_writable_permissions_or_ancestry_fails_closed_instead_of_using_a_later_candidate(
        string unsafeRole,
        string unsafeLocation,
        UnixFileMode unsafeBits)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        string ancestor = temporaryDirectory.CreateDirectory("private-PATH-secret/ancestor");
        string candidateDirectory = temporaryDirectory.CreateDirectory("private-PATH-secret/ancestor/owned-tools");
        string executable = CreateWineExecutable(temporaryDirectory, Path.Combine("private-PATH-secret", "ancestor", "owned-tools", unsafeRole));
        string counterpartDirectory = temporaryDirectory.CreateDirectory("counterpart");
        CreateWineExecutable(temporaryDirectory, Path.Combine("counterpart", unsafeRole == "wine" ? "winepath" : "wine"));
        string fallbackDirectory = temporaryDirectory.CreateDirectory("fallback");
        CreateWineExecutable(temporaryDirectory, "fallback/wine");
        CreateWineExecutable(temporaryDirectory, "fallback/winepath");
        string unsafePath = unsafeLocation switch
        {
            "executable" => executable,
            "parent" => candidateDirectory,
            _ => ancestor,
        };
        File.SetUnixFileMode(unsafePath, File.GetUnixFileMode(unsafePath) | unsafeBits);
        string searchPath = string.Join(Path.PathSeparator, candidateDirectory, counterpartDirectory, fallbackDirectory);

        OperationFailureException failure = Assert.Throws<OperationFailureException>(() =>
            CreateResolver(searchPath, temporaryDirectory).Resolve(
                WineXeBuildToolchain.Default, temporaryDirectory.Path, HelperDirectory(temporaryDirectory)));

        AssertRedactedUnavailable(failure, unsafeRole, temporaryDirectory.Path, searchPath, unsafePath);
        Assert.Equal(unsafeBits, File.GetUnixFileMode(unsafePath) & unsafeBits);
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("wine", UnixFileMode.GroupWrite)]
    [InlineData("wine", UnixFileMode.OtherWrite)]
    [InlineData("wine", UnixFileMode.GroupWrite | UnixFileMode.StickyBit)]
    [InlineData("wine", UnixFileMode.OtherWrite | UnixFileMode.StickyBit)]
    [InlineData("winepath", UnixFileMode.GroupWrite)]
    [InlineData("winepath", UnixFileMode.OtherWrite)]
    [InlineData("winepath", UnixFileMode.GroupWrite | UnixFileMode.StickyBit)]
    [InlineData("winepath", UnixFileMode.OtherWrite | UnixFileMode.StickyBit)]
    public void Protected_alias_and_final_target_do_not_excuse_a_writable_intermediary_symlink_directory(
        string unsafeRole,
        UnixFileMode unsafeBits)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        string target = TemporaryDirectory.GetCanonicalPath(CreateWineExecutable(temporaryDirectory, "protected-target/wine-apploader"));
        string aliases = temporaryDirectory.CreateDirectory("protected-aliases");
        CreateWineExecutable(temporaryDirectory, Path.Combine("protected-aliases", unsafeRole == "wine" ? "winepath" : "wine"));
        string intermediary = temporaryDirectory.CreateDirectory("private-PATH-secret-intermediary");
        string intermediateLink = Path.Combine(intermediary, "loader-link");
        File.CreateSymbolicLink(intermediateLink, target);
        string alias = Path.Combine(aliases, unsafeRole);
        File.CreateSymbolicLink(alias, intermediateLink);
        string fallback = temporaryDirectory.CreateDirectory("fallback");
        CreateWineExecutable(temporaryDirectory, "fallback/wine");
        CreateWineExecutable(temporaryDirectory, "fallback/winepath");
        File.SetUnixFileMode(intermediary, File.GetUnixFileMode(intermediary) | unsafeBits);
        string searchPath = string.Join(Path.PathSeparator, aliases, fallback);
        Assert.Equal(target, TemporaryDirectory.GetCanonicalPath(alias));

        OperationFailureException failure = Assert.Throws<OperationFailureException>(() =>
            CreateResolver(searchPath, temporaryDirectory).Resolve(
                WineXeBuildToolchain.Default, temporaryDirectory.Path, HelperDirectory(temporaryDirectory)));

        AssertRedactedUnavailable(failure, unsafeRole, temporaryDirectory.Path, searchPath, intermediary);
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("wine", "absent")]
    [InlineData("wine", "non-executable")]
    [InlineData("wine", "directory")]
    [InlineData("wine", "broken-symlink")]
    [InlineData("wine", "symlink-loop")]
    [InlineData("wine", "fifo")]
    [InlineData("winepath", "absent")]
    [InlineData("winepath", "non-executable")]
    [InlineData("winepath", "directory")]
    [InlineData("winepath", "broken-symlink")]
    [InlineData("winepath", "symlink-loop")]
    [InlineData("winepath", "fifo")]
    public void Absent_non_executable_and_non_regular_candidates_are_skipped_without_trusting_their_writable_directory(
        string skippedRole,
        string candidateKind)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        string skippedDirectory = temporaryDirectory.CreateDirectory("untrusted-skipped-entry");
        string candidate = Path.Combine(skippedDirectory, skippedRole);
        switch (candidateKind)
        {
            case "non-executable":
                CreateWineExecutable(temporaryDirectory, Path.Combine("untrusted-skipped-entry", skippedRole));
                File.SetUnixFileMode(candidate, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                break;
            case "directory":
                temporaryDirectory.CreateDirectory(Path.Combine("untrusted-skipped-entry", skippedRole));
                break;
            case "broken-symlink":
                File.CreateSymbolicLink(candidate, "missing-target");
                break;
            case "symlink-loop":
                File.CreateSymbolicLink(candidate, skippedRole);
                break;
            case "fifo":
                Assert.Equal(0, MakeFifo(candidate, 0x1C0)); // 0700: executable mode must not make a FIFO runnable.
                break;
        }

        string trustedDirectory = temporaryDirectory.CreateDirectory("trusted-tools");
        string wine = TemporaryDirectory.GetCanonicalPath(CreateWineExecutable(temporaryDirectory, "trusted-tools/wine"));
        string winepath = TemporaryDirectory.GetCanonicalPath(CreateWineExecutable(temporaryDirectory, "trusted-tools/winepath"));
        File.SetUnixFileMode(
            skippedDirectory,
            File.GetUnixFileMode(skippedDirectory) | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite | UnixFileMode.StickyBit);
        string searchPath = string.Join(Path.PathSeparator, skippedDirectory, trustedDirectory);

        WineXeBuildToolchain resolved = CreateResolver(searchPath, temporaryDirectory)
            .Resolve(WineXeBuildToolchain.Default, temporaryDirectory.Path, HelperDirectory(temporaryDirectory));

        Assert.Equal(wine, resolved.WineExecutable);
        Assert.Equal(winepath, resolved.WinePathExecutable);
        AssertTrustedChildPath(resolved, temporaryDirectory, skippedDirectory);
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("wine")]
    [InlineData("winepath")]
    public void Missing_roles_produce_stable_redacted_missing_prerequisite_failures(string missingRole)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        string directory = temporaryDirectory.CreateDirectory("private-PATH-secret");
        CreateWineExecutable(temporaryDirectory, Path.Combine("private-PATH-secret", missingRole == "wine" ? "winepath" : "wine"));
        var resolver = CreateResolver(directory, temporaryDirectory);

        OperationFailureException first = Assert.Throws<OperationFailureException>(() =>
            resolver.Resolve(WineXeBuildToolchain.Default, temporaryDirectory.Path, HelperDirectory(temporaryDirectory)));
        OperationFailureException second = Assert.Throws<OperationFailureException>(() =>
            resolver.Resolve(WineXeBuildToolchain.Default, temporaryDirectory.Path, HelperDirectory(temporaryDirectory)));

        AssertRedactedUnavailable(first, missingRole, temporaryDirectory.Path, directory);
        AssertRedactedUnavailable(second, missingRole, temporaryDirectory.Path, directory);
        Assert.Equal(first.Message, second.Message);
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("missing-directory")]
    [InlineData("embedded-nul")]
    [InlineData("overlong-component")]
    public void Invalid_or_unavailable_native_search_paths_are_redacted(string pathKind)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        string privateDirectory = temporaryDirectory.CreateDirectory("private-PATH-secret");
        string searchPath = pathKind switch
        {
            "embedded-nul" => string.Concat(privateDirectory, "\0private-native-error"),
            "overlong-component" => Path.Combine(privateDirectory, new string('s', 300)),
            _ => Path.Combine(privateDirectory, "missing-directory"),
        };

        OperationFailureException failure = Assert.Throws<OperationFailureException>(() =>
            CreateResolver(searchPath, temporaryDirectory).Resolve(
                WineXeBuildToolchain.Default, temporaryDirectory.Path, HelperDirectory(temporaryDirectory)));

        AssertRedactedUnavailable(failure, "wine", temporaryDirectory.Path, privateDirectory);
        Assert.DoesNotContain("private-native-error", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Non_Linux_production_discovery_fails_closed_without_searching()
    {
        if (OperatingSystem.IsLinux())
        {
            return;
        }

        OperationFailureException failure = Assert.Throws<OperationFailureException>(() =>
            new WineXeBuildToolchainResolver("private-PATH-secret").Resolve(WineXeBuildToolchain.Default, "/working-directory"));

        AssertRedactedUnavailable(failure, "wine", "private-PATH-secret", "/working-directory");
    }

    [SupportedOSPlatform("linux")]
    [Fact]
    public void Protected_trust_APIs_distinguish_executables_regular_dependency_files_and_directories()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        string tree = temporaryDirectory.CreateDirectory("protected-tree");
        string executable = temporaryDirectory.CreateExecutable("protected-tree/host");
        File.SetUnixFileMode(executable, UnixFileMode.UserExecute);
        string data = temporaryDirectory.CreateExecutable("protected-tree/runtime.config");
        File.SetUnixFileMode(data, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        string executableAlias = Path.Combine(tree, "host-alias");
        File.CreateSymbolicLink(executableAlias, "host");
        string dataAlias = Path.Combine(tree, "data-alias");
        File.CreateSymbolicLink(dataAlias, "runtime.config");
        temporaryDirectory.CreateDirectory("protected-tree/empty-runtime-directory");
        string treeAlias = Path.Combine(temporaryDirectory.Path, "tree-alias");
        Directory.CreateSymbolicLink(treeAlias, tree);

        WineXeBuildToolchainResolver.ValidateProtectedExecutable(executable);
        WineXeBuildToolchainResolver.ValidateProtectedExecutable(executableAlias);
        WineXeBuildToolchainResolver.ValidateProtectedFile(executable);
        WineXeBuildToolchainResolver.ValidateProtectedFile(data);
        WineXeBuildToolchainResolver.ValidateProtectedFile(dataAlias);
        WineXeBuildToolchainResolver.ValidateProtectedTree(tree);
        WineXeBuildToolchainResolver.ValidateProtectedTree(treeAlias);

        Action[] invalidValidations =
        [
            () => WineXeBuildToolchainResolver.ValidateProtectedExecutable(data),
            () => WineXeBuildToolchainResolver.ValidateProtectedExecutable(dataAlias),
            () => WineXeBuildToolchainResolver.ValidateProtectedExecutable(tree),
            () => WineXeBuildToolchainResolver.ValidateProtectedFile(tree),
            () => WineXeBuildToolchainResolver.ValidateProtectedFile(treeAlias),
            () => WineXeBuildToolchainResolver.ValidateProtectedTree(data),
            () => WineXeBuildToolchainResolver.ValidateProtectedTree(executable),
        ];
        foreach (Action validate in invalidValidations)
        {
            OperationFailureException failure = Assert.Throws<OperationFailureException>(validate);
            AssertRedactedUnavailable(failure, "wine", temporaryDirectory.Path, executable, data, tree);
        }
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("executable", "target")]
    [InlineData("executable", "target-parent")]
    [InlineData("executable", "target-ancestor")]
    [InlineData("executable", "intermediary")]
    [InlineData("file", "target")]
    [InlineData("file", "target-parent")]
    [InlineData("file", "target-ancestor")]
    [InlineData("file", "intermediary")]
    public void Protected_executable_and_data_file_APIs_reject_writable_alias_targets_and_ancestry(
        string validationKind,
        string unsafeLocation)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        string ancestor = temporaryDirectory.CreateDirectory("private-dependency-secret");
        string parent = temporaryDirectory.CreateDirectory("private-dependency-secret/runtime");
        string target = temporaryDirectory.CreateExecutable("private-dependency-secret/runtime/dependency");
        if (validationKind == "file")
        {
            File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        string intermediary = temporaryDirectory.CreateDirectory("private-intermediary-secret");
        string intermediateLink = Path.Combine(intermediary, "dependency-link");
        File.CreateSymbolicLink(intermediateLink, target);
        string aliases = temporaryDirectory.CreateDirectory("aliases");
        string alias = Path.Combine(aliases, "host-dependency");
        File.CreateSymbolicLink(alias, unsafeLocation == "intermediary" ? intermediateLink : target);
        string unsafePath = unsafeLocation switch
        {
            "target" => target,
            "target-parent" => parent,
            "target-ancestor" => ancestor,
            _ => intermediary,
        };
        File.SetUnixFileMode(unsafePath, File.GetUnixFileMode(unsafePath) | UnixFileMode.OtherWrite);

        OperationFailureException failure = Assert.Throws<OperationFailureException>(() =>
        {
            if (validationKind == "executable")
            {
                WineXeBuildToolchainResolver.ValidateProtectedExecutable(alias);
            }
            else
            {
                WineXeBuildToolchainResolver.ValidateProtectedFile(alias);
            }
        });

        AssertRedactedUnavailable(failure, "wine", temporaryDirectory.Path, alias, target, unsafePath);
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("tree-root")]
    [InlineData("empty-directory")]
    [InlineData("data-leaf")]
    [InlineData("linked-data-leaf")]
    [InlineData("linked-directory")]
    [InlineData("target-ancestor")]
    [InlineData("intermediary")]
    public void Protected_dependency_trees_reject_writable_data_empty_directories_and_symlink_target_segments(string unsafeLocation)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        string tree = temporaryDirectory.CreateDirectory("protected-tree");
        string emptyDirectory = temporaryDirectory.CreateDirectory("protected-tree/empty-runtime-directory");
        string data = temporaryDirectory.CreateExecutable("protected-tree/runtime.config");
        File.SetUnixFileMode(data, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        string ancestor = temporaryDirectory.CreateDirectory("private-probing-secret");
        string linkedDirectory = temporaryDirectory.CreateDirectory("private-probing-secret/dependencies");
        string linkedData = temporaryDirectory.CreateExecutable("private-probing-secret/dependencies/module.dll");
        File.SetUnixFileMode(linkedData, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        string intermediary = temporaryDirectory.CreateDirectory("private-probing-link-secret");
        string intermediateLink = Path.Combine(intermediary, "module-link");
        File.CreateSymbolicLink(intermediateLink, linkedData);
        File.CreateSymbolicLink(
            Path.Combine(tree, "module-alias"),
            unsafeLocation == "intermediary" ? intermediateLink : linkedData);
        Directory.CreateSymbolicLink(Path.Combine(tree, "probing-alias"), linkedDirectory);
        string unsafePath = unsafeLocation switch
        {
            "tree-root" => tree,
            "empty-directory" => emptyDirectory,
            "data-leaf" => data,
            "linked-data-leaf" => linkedData,
            "linked-directory" => linkedDirectory,
            "target-ancestor" => ancestor,
            _ => intermediary,
        };
        File.SetUnixFileMode(unsafePath, File.GetUnixFileMode(unsafePath) | UnixFileMode.GroupWrite);

        OperationFailureException failure = Assert.Throws<OperationFailureException>(() =>
            WineXeBuildToolchainResolver.ValidateProtectedTree(tree));

        AssertRedactedUnavailable(failure, "wine", temporaryDirectory.Path, tree, unsafePath);
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("data")]
    [InlineData("empty-directory")]
    public void Protected_dependency_trees_reject_foreign_owned_non_executable_leaves_and_empty_directories(string unsafeLocation)
    {
        if (!OperatingSystem.IsLinux() || GetEffectiveUserId() != 0)
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        string tree = temporaryDirectory.CreateDirectory("protected-tree");
        string emptyDirectory = temporaryDirectory.CreateDirectory("protected-tree/empty-runtime-directory");
        string data = temporaryDirectory.CreateExecutable("protected-tree/runtime.config");
        File.SetUnixFileMode(data, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        string unsafePath = unsafeLocation == "data" ? data : emptyDirectory;
        Assert.Equal(0, ChangeOwnerNoFollow(unsafePath, userId: 65534, groupId: uint.MaxValue));
        try
        {
            OperationFailureException failure = Assert.Throws<OperationFailureException>(() =>
                WineXeBuildToolchainResolver.ValidateProtectedTree(tree));

            AssertRedactedUnavailable(failure, "wine", temporaryDirectory.Path, tree, unsafePath);
        }
        finally
        {
            Assert.Equal(0, ChangeOwnerNoFollow(unsafePath, userId: 0, groupId: uint.MaxValue));
        }
    }

    [SupportedOSPlatform("linux")]
    [Fact]
    public void Protected_dependency_trees_validate_directory_link_cycles_once_without_losing_external_data_closure()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        string tree = temporaryDirectory.CreateDirectory("protected-tree");
        string dependencies = temporaryDirectory.CreateDirectory("protected-dependencies");
        string data = temporaryDirectory.CreateExecutable("protected-dependencies/module.dll");
        File.SetUnixFileMode(data, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.CreateSymbolicLink(Path.Combine(tree, "data-alias"), "../protected-dependencies/module.dll");
        Directory.CreateSymbolicLink(Path.Combine(tree, "self"), ".");
        Directory.CreateSymbolicLink(Path.Combine(tree, "dependencies"), "../protected-dependencies");
        Directory.CreateSymbolicLink(Path.Combine(dependencies, "back-to-host"), "../protected-tree");

        WineXeBuildToolchainResolver.ValidateProtectedTree(tree);

        File.SetUnixFileMode(data, File.GetUnixFileMode(data) | UnixFileMode.OtherWrite);
        OperationFailureException failure = Assert.Throws<OperationFailureException>(() =>
            WineXeBuildToolchainResolver.ValidateProtectedTree(tree));
        AssertRedactedUnavailable(failure, "wine", temporaryDirectory.Path, tree, data);
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("relative")]
    [InlineData("embedded-nul")]
    [InlineData("absent")]
    [InlineData("overlong-component")]
    public void Every_protected_trust_API_rejects_missing_or_non_absolute_native_paths_with_stable_redacted_errors(string pathKind)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        string path = pathKind switch
        {
            "relative" => "private-native-secret/host",
            "embedded-nul" => string.Concat(temporaryDirectory.Path, "/private-native-secret\0"),
            "overlong-component" => Path.Combine(temporaryDirectory.Path, new string('s', 300)),
            _ => Path.Combine(temporaryDirectory.Path, "private-missing-native-secret"),
        };
        Action<string>[] validators =
        [
            WineXeBuildToolchainResolver.ValidateProtectedExecutable,
            WineXeBuildToolchainResolver.ValidateProtectedFile,
            WineXeBuildToolchainResolver.ValidateProtectedTree,
        ];
        foreach (Action<string> validate in validators)
        {
            OperationFailureException first = Assert.Throws<OperationFailureException>(() => validate(path));
            OperationFailureException second = Assert.Throws<OperationFailureException>(() => validate(path));
            AssertRedactedUnavailable(first, "wine", temporaryDirectory.Path, path);
            AssertRedactedUnavailable(second, "wine", temporaryDirectory.Path, path);
            Assert.Equal(first.Message, second.Message);
        }
    }

    [SupportedOSPlatform("linux")]
    [Fact]
    public void Protected_trust_APIs_reject_FIFOs_and_special_file_symlink_targets_without_performing_leaf_IO()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        string tree = temporaryDirectory.CreateDirectory("private-special-file-secret");
        string fifo = Path.Combine(tree, "dependency-fifo");
        Assert.Equal(0, MakeFifo(fifo, 0x1C0));
        string alias = Path.Combine(tree, "fifo-alias");
        File.CreateSymbolicLink(alias, "dependency-fifo");
        Action[] validators =
        [
            () => WineXeBuildToolchainResolver.ValidateProtectedExecutable(fifo),
            () => WineXeBuildToolchainResolver.ValidateProtectedFile(fifo),
            () => WineXeBuildToolchainResolver.ValidateProtectedTree(fifo),
            () => WineXeBuildToolchainResolver.ValidateProtectedExecutable(alias),
            () => WineXeBuildToolchainResolver.ValidateProtectedFile(alias),
            () => WineXeBuildToolchainResolver.ValidateProtectedTree(tree),
        ];
        foreach (Action validate in validators)
        {
            OperationFailureException failure = Assert.Throws<OperationFailureException>(validate);
            AssertRedactedUnavailable(failure, "wine", temporaryDirectory.Path, fifo, alias, tree);
        }
    }

    private static void AssertRedactedUnavailable(
        OperationFailureException failure,
        string role,
        params string[] sensitivePaths)
    {
        Assert.Equal(ExitCode.MissingPrerequisite, failure.Code);
        Assert.Equal(role == "wine" ? "wine-unavailable" : "winepath-unavailable", failure.Kind);
        foreach (string sensitivePath in sensitivePaths)
        {
            Assert.DoesNotContain(sensitivePath, failure.ToString(), StringComparison.Ordinal);
        }
    }

    [SupportedOSPlatform("linux")]
    private static string CreateWineExecutable(TemporaryDirectory temporaryDirectory, string relativePath)
    {
        string executable = temporaryDirectory.CreateExecutable(relativePath);
        string relativeParent = Path.GetDirectoryName(relativePath) ?? string.Empty;
        string server = Path.Join(temporaryDirectory.Path, relativeParent, "wineserver");
        if (!File.Exists(server))
        {
            temporaryDirectory.CreateExecutable(Path.Join(relativeParent, "wineserver"));
        }
        return executable;
    }

    private static string HelperDirectory(TemporaryDirectory temporaryDirectory) =>
        Path.Join(temporaryDirectory.Path, "wine-toolchain");

    [SupportedOSPlatform("linux")]
    private static WineXeBuildToolchainResolver CreateResolver(string? searchPath, TemporaryDirectory temporaryDirectory)
    {
        temporaryDirectory.CreateDirectory("wine-toolchain");
        return WineXeBuildToolchainResolver.CreateSyntheticNativeFixtureResolver(searchPath);
    }

    private static void AssertUnsupportedWrapper(OperationFailureException failure, params string[] sensitivePaths)
    {
        Assert.Equal(ExitCode.MissingPrerequisite, failure.Code);
        Assert.Equal("wine-wrapper-unsupported", failure.Kind);
        Assert.Null(failure.InnerException);
        foreach (string path in sensitivePaths)
        {
            Assert.DoesNotContain(path, failure.ToString(), StringComparison.Ordinal);
        }
    }

    [SupportedOSPlatform("linux")]
    private static void AssertTrustedChildPath(
        WineXeBuildToolchain resolved, TemporaryDirectory temporaryDirectory, params string[] excludedDirectories)
    {
        Assert.False(resolved.RequiresTrustedPathDiscovery);
        string childPath = Assert.IsType<string>(resolved.TrustedChildPath);
        Assert.Equal(HelperDirectory(temporaryDirectory), Assert.Single(childPath.Split(Path.PathSeparator)));
        Assert.Equal(TemporaryDirectory.GetCanonicalPath(childPath), childPath);
        Assert.Equal(resolved.WineExecutable, new FileInfo(Path.Join(childPath, "wine")).LinkTarget);
        Assert.Equal(resolved.WinePathExecutable, new FileInfo(Path.Join(childPath, "winepath")).LinkTarget);
        Assert.True(File.Exists(Path.Join(childPath, "wineserver")));
        Assert.Equal(GetEffectiveUserId(), ReadMetadata(childPath).UserId);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(childPath));
        WineXeBuildToolchainResolver.ValidateProtectedTree(childPath);
        foreach (string excluded in excludedDirectories)
        {
            Assert.NotEqual(TemporaryDirectory.GetCanonicalPath(excluded), childPath);
        }
    }

    [SupportedOSPlatform("linux")]
    private static NativeStatx ReadMetadata(string path)
    {
        Assert.Equal(0, Statx(-100, path, 0, 0x8, out NativeStatx metadata)); // AT_FDCWD, STATX_UID.
        Assert.Equal(0x8U, metadata.Mask & 0x8U);
        return metadata;
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct NativeStatx
    {
        [FieldOffset(0)]
        internal uint Mask;

        [FieldOffset(20)]
        internal uint UserId;
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(
        int directoryDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags,
        uint mask,
        out NativeStatx metadata);

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    [DllImport("libc", EntryPoint = "lchown", SetLastError = true)]
    private static extern int ChangeOwnerNoFollow(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        uint userId,
        uint groupId);

    [DllImport("libc", EntryPoint = "symlink", SetLastError = true)]
    private static extern int CreateNativeSymlink(
        [In] byte[] target, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenNativeFile([In] byte[] path, int flags, uint mode);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int CloseNativeFile(int descriptor);

    [DllImport("libc", EntryPoint = "unlink", SetLastError = true)]
    private static extern int UnlinkNativeFile([In] byte[] path);
}
