using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using JRunner.Cli.Infrastructure;
using JRunner.Core.Contracts;
using Xunit;
using TemporaryDirectory = JRunner.Cli.Tests.WineXeBuildBackendTests.TemporaryDirectory;

namespace JRunner.Cli.Tests;

[SupportedOSPlatform("linux")]
public sealed class NativeDependencyClosureTests
{
    [Fact]
    public void Static_executable_is_content_frozen_but_unused_direct_modules_are_metadata_only()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        string module = fixture.WriteImage("libraries/native-module.data", new NativeElfTestImage());

        NativeDependencyClosure closure = fixture.Prepare();
        NativeDependencyClosureBinding binding = closure.ToBindingForTesting();

        Assert.Equal(2, binding.Version);
        NativeObjectBinding executable = Assert.Single(binding.Objects, item => item.CanonicalPath == fixture.Executable);
        Assert.Equal(NativeObjectProtection.ContentFrozen, executable.Protection);
        Assert.NotNull(executable.ContentSha256);
        NativeObjectBinding unused = Assert.Single(binding.Objects, item => item.CanonicalPath == module);
        Assert.Equal(NativeObjectProtection.MetadataOnly, unused.Protection);
        Assert.Null(unused.ContentSha256);
        closure.ValidateExecutable(fixture.Executable);
        closure.Revalidate();
        Assert.Contains(binding.Objects, item => item.CanonicalPath == module && item.Loadable && item.Image!.ObjectType == 3);
        AssertUnavailable(() => closure.ValidateExecutable(module));
    }

    [Theory]
    [InlineData("interpreter")]
    [InlineData("library")]
    [InlineData("nonexecuting-elf")]
    [InlineData("nonelf-data")]
    [InlineData("library-root")]
    [InlineData("alias-directory")]
    [InlineData("canonical-target")]
    [InlineData("nested-directory")]
    public void Unsafe_native_candidates_and_data_leaves_fail_before_any_discovery_execution(string location)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        string loader = fixture.WriteImage("loader/ld-linux-x86-64.so.2", new NativeElfTestImage(), executable: true);
        fixture.SetExecutable(new NativeElfTestImage(objectType: 2) { Interpreter = loader }.AddStringTag(1, "private.so"));
        string library = fixture.WriteImage("libraries/private.so", new NativeElfTestImage());
        string leaf = fixture.WriteImage("libraries/not-a-library-name.payload", new NativeElfTestImage());
        string data = fixture.WriteData("libraries/not-native.txt", "ordinary protected data");
        string nested = fixture.CreateDirectory("libraries/nested");
        string aliasDirectory = fixture.CreateDirectory("aliases");
        string target = fixture.WriteImage("targets/shared-target.so", new NativeElfTestImage());
        string alias = Path.Join(fixture.LibraryDirectory, "alias.so");
        File.CreateSymbolicLink(alias, target);
        string selectedAlias = Path.Join(aliasDirectory, "selected");
        File.CreateSymbolicLink(selectedAlias, fixture.Executable);
        string changed = location switch
        {
            "interpreter" => loader,
            "library" => library,
            "nonexecuting-elf" => leaf,
            "nonelf-data" => data,
            "library-root" => fixture.LibraryDirectory,
            "alias-directory" => aliasDirectory,
            "canonical-target" => target,
            _ => nested,
        };
        File.SetUnixFileMode(changed, File.GetUnixFileMode(changed) | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite);

        AssertUnavailable(() => fixture.Prepare(executables: [selectedAlias]));
        Assert.False(File.Exists(fixture.Marker));
    }

    [Fact]
    public void Every_hwcaps_and_cache_candidate_is_protected_not_just_the_current_winner()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        string safe = fixture.WriteImage("libraries/private.so", new NativeElfTestImage());
        string alternate = fixture.WriteImage("cache-alternate/private.so", new NativeElfTestImage());
        fixture.WriteImage("libraries/glibc-hwcaps/x86-64-v2/unrelated.data", new NativeElfTestImage());
        string higher = fixture.WriteImage("libraries/glibc-hwcaps/x86-64-v4/unrelated.payload", new NativeElfTestImage());
        File.SetUnixFileMode(higher, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherWrite);
        fixture.WriteCache(
            [("private.so", safe, 0x303, 0), ("private.so", alternate, 0x303, 0)]);

        AssertUnavailable(() => fixture.Prepare());

        File.SetUnixFileMode(higher, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.SetUnixFileMode(Path.GetDirectoryName(alternate)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherWrite);
        AssertUnavailable(() => fixture.Prepare());
    }

    [Fact]
    public void Every_ELF_in_an_unused_cached_root_still_has_its_path_tags_closed()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        string candidate = fixture.WriteImage("cache-only/candidate.so", new NativeElfTestImage());
        fixture.WriteImage("cache-only/ordinary-data.dat", new NativeElfTestImage().AddStringTag(29, "relative/attacker"));
        fixture.WriteCache([("candidate.so", candidate, 0x303, 0)]);

        AssertUnavailable(() => fixture.Prepare());
    }

    [Fact]
    public void Unused_cached_candidate_with_Wireshark_sized_rela_is_metadata_only_but_identity_frozen()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        const ulong tableSize = 1_846_573UL * 24;
        var image = new NativeElfTestImage();
        image.AddTag(7, 0).AddTag(8, tableSize).AddTag(9, 24).AddTag(0x6ffffff9, 1_836_258);
        byte[] bytes = image.Build();
        long logicalLength = checked(bytes.LongLength + (long)tableSize);
        image.WriteAddress(bytes, image.DynamicEntryOffset(7) + image.WordSize,
            image.BaseAddress + (ulong)bytes.Length);
        NativeElfTestImage.Write64(bytes, image.ProgramHeaderOffset(0) + 32, (ulong)logicalLength);
        NativeElfTestImage.Write64(bytes, image.ProgramHeaderOffset(0) + 40, (ulong)logicalLength + image.BssBytes);
        string candidate = fixture.WriteData("cache-only/optional.so", string.Empty);
        using (FileStream stream = File.Open(candidate, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            // Only the metadata prefix occupies storage; the 44,317,752-byte table is a sparse extent.
            stream.Write(bytes);
            stream.SetLength(logicalLength);
        }
        string alias = Path.Join(Path.GetDirectoryName(candidate)!, "optional-alias.so");
        File.CreateSymbolicLink(alias, candidate);
        fixture.WriteCache([("optional.so", alias, 0x303, 0)]);

        NativeDependencyClosure closure = fixture.Prepare();
        NativeDependencyClosureBinding binding = closure.ToBindingForTesting();
        NativeObjectBinding unused = Assert.Single(binding.Objects, item => item.CanonicalPath == candidate);
        Assert.True(unused.Loadable);
        Assert.True(unused.Image!.HasSupportedAbi);
        Assert.Empty(unused.Image.Needed);
        Assert.Equal((ushort)3, unused.Image.ObjectType);
        Assert.Equal((ulong)logicalLength, unused.Identity.Size);
        Assert.Equal(NativeObjectProtection.MetadataOnly, unused.Protection);
        Assert.Null(unused.ContentSha256);
        Assert.Contains(binding.CacheEntries, entry => entry.Path == alias);
        Assert.Contains(Path.GetDirectoryName(alias)!, binding.SearchDirectories);
        Assert.Contains(binding.Paths, path => path.AliasPath == candidate && path.Exists && path.FreezeMetadata);
        Assert.Contains(binding.Paths, path => path.AliasPath == alias && path.CanonicalPath == candidate &&
            path.Exists && path.FreezeMetadata);
        closure.ValidateExecutable(fixture.Executable);
        NativeDependencyClosure restored = NativeDependencyClosure.FromBindingForTesting(binding, fixture.Layout);
        restored.ValidateExecutable(fixture.Executable);

        MutateBytePreservingModificationTimeForTesting(candidate, logicalLength - 1);

        AssertUnavailable(closure.Revalidate);
        AssertUnavailable(restored.Revalidate);
        AssertUnavailable(() => NativeDependencyClosure.FromBindingForTesting(binding, fixture.Layout));
        NativeDependencyClosureBinding rebound =
            RefreshFileIdentityKeepingSelectorsAndContentHashForTesting(binding, candidate);
        NativeDependencyClosure current = NativeDependencyClosure.FromBindingForTesting(rebound, fixture.Layout);
        current.Revalidate();

        File.SetUnixFileMode(candidate, File.GetUnixFileMode(candidate) | UnixFileMode.OtherWrite);
        AssertUnavailable(() => fixture.Prepare());
        AssertUnavailable(current.Revalidate);
        AssertUnavailable(() => NativeDependencyClosure.FromBindingForTesting(rebound, fixture.Layout));
    }

    [Theory]
    [InlineData("direct-elf", false)]
    [InlineData("direct-elf", true)]
    [InlineData("cache-elf", false)]
    [InlineData("cache-elf", true)]
    [InlineData("direct-pe", false)]
    [InlineData("direct-pe", true)]
    [InlineData("cache-pe", false)]
    [InlineData("cache-pe", true)]
    public void Unused_direct_and_cache_native_or_PE_objects_reparse_selectors_without_freezing_nonselector_bytes(
        string location, bool selectorMutation)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        bool portable = location.EndsWith("-pe", StringComparison.Ordinal);
        bool cached = location.StartsWith("cache-", StringComparison.Ordinal);
        string relativeRoot = cached ? "cache-only" : "libraries";
        string candidate;
        long selectorOffset;
        long contentOffset;
        if (portable)
        {
            const string portableBytes = "MZ unused native fixture payload";
            candidate = fixture.WriteData(relativeRoot + "/unused.payload", portableBytes);
            selectorOffset = 0;
            contentOffset = portableBytes.Length - 1;
        }
        else
        {
            var image = new NativeElfTestImage().AddStringTag(14, "before.so");
            NativeElfTestImage.Blob payload = image.AddBlob([0x5a]);
            candidate = fixture.WriteImage(relativeRoot + "/unused.payload", image);
            selectorOffset = image.StringTableOffset + 1;
            contentOffset = payload.Offset;
        }
        if (cached)
        {
            string cacheCandidate = portable
                ? fixture.WriteImage(relativeRoot + "/cache-provider.so", new NativeElfTestImage())
                : candidate;
            fixture.WriteCache([("optional.so", cacheCandidate, 0x303, 0)]);
        }
        NativeDependencyClosure closure = fixture.Prepare();
        NativeDependencyClosureBinding binding = closure.ToBindingForTesting();
        NativeObjectBinding unused = Assert.Single(binding.Objects, item => item.CanonicalPath == candidate);
        Assert.Equal(NativeObjectProtection.MetadataOnly, unused.Protection);
        Assert.Null(unused.ContentSha256);
        Assert.Equal(portable, unused.PortableExecutable);
        Assert.Equal(!portable, unused.Loadable);
        Assert.Contains(binding.Paths, path => path.AliasPath == candidate && path.Exists && path.FreezeMetadata);
        if (cached)
        {
            Assert.Contains(Path.GetDirectoryName(candidate)!, binding.SearchDirectories);
            Assert.Single(binding.CacheEntries);
        }
        closure.Revalidate();
        NativeDependencyClosure.FromBindingForTesting(binding, fixture.Layout).Revalidate();

        long offset = selectorMutation ? selectorOffset : contentOffset;
        MutateBytePreservingModificationTimeForTesting(candidate, offset);
        AssertUnavailable(closure.Revalidate);
        AssertUnavailable(() => NativeDependencyClosure.FromBindingForTesting(binding, fixture.Layout));
        NativeDependencyClosureBinding rebound =
            RefreshFileIdentityKeepingSelectorsAndContentHashForTesting(binding, candidate);
        if (selectorMutation)
        {
            AssertUnavailable(() => NativeDependencyClosure.FromBindingForTesting(rebound, fixture.Layout));
        }
        else
        {
            NativeDependencyClosure restored = NativeDependencyClosure.FromBindingForTesting(rebound, fixture.Layout);
            restored.ValidateExecutable(fixture.Executable);
            restored.Revalidate();
        }
    }

    [Fact]
    public void An_absent_cached_candidate_is_frozen_and_cannot_be_planted_later()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        string missing = Path.Join(fixture.Root, "cache-candidate", "private.so");
        fixture.WriteCache([("private.so", missing, 0x303, 0)]);
        NativeDependencyClosure closure = fixture.Prepare();
        Assert.Contains(closure.ToBindingForTesting().Paths, path => path.AliasPath == missing && !path.Exists);

        fixture.WriteImage("cache-candidate/private.so", new NativeElfTestImage());

        AssertUnavailable(closure.Revalidate);
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("configuration")]
    [InlineData("cache")]
    [InlineData("preload")]
    public void Missing_loader_anchors_cannot_be_planted_after_prepare(string anchor)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        string absentRoot = Path.Join(fixture.Root, "missing-search", "nested");
        string absentConfiguration = Path.Join(fixture.Root, "missing.config");
        NativeDependencyClosure closure = fixture.Prepare(modules: [absentRoot], configurations: [absentConfiguration]);
        switch (anchor)
        {
            case "directory": fixture.CreateDirectory("missing-search/nested"); break;
            case "configuration": fixture.WriteData("missing.config", "new selector"); break;
            case "cache": fixture.WriteCache([]); break;
            case "preload": fixture.WriteData("system/ld.so.preload", ""); break;
        }

        AssertUnavailable(closure.Revalidate);
        AssertUnavailable(() => NativeDependencyClosure.FromBindingForTesting(closure.ToBindingForTesting(), fixture.Layout));
    }

    [Fact]
    public void Missing_root_proof_binds_the_existing_canonical_ancestor_not_just_the_text_name()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        string first = fixture.CreateDirectory("first-parent");
        string second = fixture.CreateDirectory("second-parent");
        string alias = Path.Join(fixture.Root, "missing-root-alias");
        Directory.CreateSymbolicLink(alias, first);
        string absent = Path.Join(alias, "not-installed");
        NativeDependencyClosure closure = fixture.Prepare(modules: [absent]);

        Directory.Delete(alias);
        Directory.CreateSymbolicLink(alias, second);

        AssertUnavailable(closure.Revalidate);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\n")]
    [InlineData("# no system injection\n\t# another comment\n")]
    public void Empty_or_comment_only_preload_is_supported_and_frozen(string text)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        fixture.WriteData("system/ld.so.preload", text);

        NativeDependencyClosure closure = fixture.Prepare();

        Assert.True(closure.ToBindingForTesting().PreloadExists);
        closure.Revalidate();
        fixture.WriteData("system/ld.so.preload", "libattacker.so");
        AssertUnavailable(closure.Revalidate);
    }

    [Theory]
    [InlineData("libattacker.so")]
    [InlineData("/attacker/libinject.so\n")]
    [InlineData("\0")]
    [InlineData("\uFEFF")]
    public void Active_or_unparseable_system_preload_is_not_suppressed_as_a_warning(string text)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        fixture.WriteData("system/ld.so.preload", text);

        AssertUnavailable(() => fixture.Prepare());
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("relative/library")]
    [InlineData("/protected/../attacker")]
    [InlineData("/protected/./library")]
    [InlineData("/protected//library")]
    [InlineData("$ORIGIN/")]
    [InlineData("$ORIGIN//library")]
    [InlineData("${ORIGIN}")]
    [InlineData("$LIB")]
    [InlineData("$PLATFORM")]
    [InlineData("$UNKNOWN/library")]
    [InlineData("/protected/$ORIGIN")]
    public void Unsupported_origin_and_path_forms_are_rejected(string path)
    {
        AssertUnavailable(() => NativeDependencyClosure.ExpandObjectPath("/protected/object.so", path));
    }

    [Theory]
    [InlineData("$ORIGIN", "/protected")]
    [InlineData("$ORIGIN/native/literal", "/protected/native/literal")]
    [InlineData("$ORIGIN/../native", "/protected/../native")]
    [InlineData("$ORIGIN/./native", "/protected/./native")]
    [InlineData("/system/lib", "/system/lib")]
    public void Exact_origin_and_literal_suffixes_use_the_canonical_object_directory(string path, string expected)
    {
        Assert.Equal(expected, NativeDependencyClosure.ExpandObjectPath("/protected/object.so", path));
    }

    [Theory]
    [InlineData(15UL, "$ORIGIN:")]
    [InlineData(29UL, "$ORIGIN:")]
    [InlineData(15UL, ":$ORIGIN")]
    [InlineData(29UL, ":$ORIGIN")]
    [InlineData(15UL, "$ORIGIN::$ORIGIN")]
    [InlineData(29UL, "$ORIGIN::$ORIGIN")]
    public void Empty_components_in_nonempty_path_tags_never_select_a_working_directory(ulong tag, string path)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        fixture.WriteImage("libraries/path-bearing.data", new NativeElfTestImage().AddStringTag(tag, path));

        AssertUnavailable(() => fixture.Prepare());
    }

    [Theory]
    [InlineData(15UL)]
    [InlineData(29UL)]
    public void Wholly_empty_path_tags_are_ignored_but_their_native_object_identity_stays_frozen(ulong tag)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        string module = fixture.WriteImage("libraries/path-bearing.data", new NativeElfTestImage().AddStringTag(tag, ""));

        NativeDependencyClosure closure = fixture.Prepare();
        NativeObjectBinding item = Assert.Single(closure.ToBindingForTesting().Objects, item => item.CanonicalPath == module);
        Assert.Equal(NativeObjectProtection.MetadataOnly, item.Protection);
        Assert.Null(item.ContentSha256);

        Assert.Empty(item.Image!.RPath);
        Assert.Empty(item.Image.RunPath);
        closure.Revalidate();
        fixture.WriteImage("libraries/path-bearing.data", new NativeElfTestImage().AddStringTag(tag, ":"));
        AssertUnavailable(closure.Revalidate);
    }

    [Fact]
    public void Both_the_selected_alias_parent_and_final_target_origin_directories_are_closed()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        string loader = fixture.WriteImage("loader/ld-linux-x86-64.so.2", new NativeElfTestImage(), executable: true);
        string target = fixture.WriteImage("package/program", new NativeElfTestImage(objectType: 2) { Interpreter = loader }
            .AddStringTag(29, "$ORIGIN/helpers").AddStringTag(1, "private.so"), executable: true);
        string library = fixture.WriteImage("package/helpers/private.so", new NativeElfTestImage());
        string aliases = fixture.CreateDirectory("aliases");
        string aliasLibrary = fixture.WriteImage("aliases/helpers/private.so", new NativeElfTestImage());
        string alias = Path.Join(aliases, "program");
        File.CreateSymbolicLink(alias, target);

        NativeDependencyClosure closure = fixture.Prepare(executables: [alias]);

        Assert.Contains(closure.ToBindingForTesting().Objects, item => item.CanonicalPath == library);
        Assert.Contains(closure.ToBindingForTesting().Objects, item => item.CanonicalPath == aliasLibrary);
        Assert.Contains(Path.GetDirectoryName(aliasLibrary)!, closure.ToBindingForTesting().SearchDirectories);
        closure.ValidateExecutable(alias);
    }

    [Fact]
    public void A_final_DSO_symlink_does_not_hide_an_unsafe_load_alias_origin_directory()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        string target = fixture.WriteImage("package/provider.so", new NativeElfTestImage().AddStringTag(29, "$ORIGIN/deep/native"));
        fixture.WriteImage("package/deep/native/private.so", new NativeElfTestImage());
        string unsafeOrigin = fixture.CreateDirectory("libraries/deep/native");
        File.SetUnixFileMode(unsafeOrigin, File.GetUnixFileMode(unsafeOrigin) | UnixFileMode.OtherWrite);
        File.CreateSymbolicLink(Path.Join(fixture.LibraryDirectory, "provider.so"), target);

        AssertUnavailable(() => fixture.Prepare());
    }

    [Theory]
    [InlineData("../attacker", "attacker")]
    [InlineData("./attacker", "libraries/attacker")]
    public void Literal_origin_dot_components_still_require_other_UID_protected_directories(string suffix, string directory)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        fixture.WriteImage("libraries/provider.so", new NativeElfTestImage().AddStringTag(29, "$ORIGIN/" + suffix));
        string unsafeOrigin = fixture.CreateDirectory(directory);
        File.SetUnixFileMode(unsafeOrigin, File.GetUnixFileMode(unsafeOrigin) | UnixFileMode.OtherWrite);

        AssertUnavailable(() => fixture.Prepare());
    }

    [Fact]
    public void Origin_dot_dot_is_resolved_after_a_directory_symlink_and_retains_its_original_alias()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        string destination = fixture.CreateDirectory("alternate/deep");
        string bridge = Path.Join(fixture.LibraryDirectory, "bridge");
        Directory.CreateSymbolicLink(bridge, destination);
        fixture.WriteImage("libraries/provider.so", new NativeElfTestImage().AddStringTag(29, "$ORIGIN/bridge/../native"));
        string dependency = fixture.WriteImage("alternate/native/private.so", new NativeElfTestImage());
        string lexicalDecoy = fixture.WriteImage("libraries/native/unused.payload", new NativeElfTestImage());
        File.SetUnixFileMode(lexicalDecoy, File.GetUnixFileMode(lexicalDecoy) | UnixFileMode.OtherWrite);

        NativeDependencyClosure closure = fixture.Prepare();
        NativeDependencyClosureBinding binding = closure.ToBindingForTesting();
        string rootAlias = Path.Join(fixture.LibraryDirectory, "bridge/../native");

        Assert.Contains(rootAlias, binding.SearchDirectories);
        Assert.Contains(Path.GetDirectoryName(dependency)!, binding.SearchDirectories);
        Assert.Contains(binding.Paths, path => path.AliasPath == rootAlias &&
            path.CanonicalPath == Path.GetDirectoryName(dependency));
        Assert.Contains(binding.Objects, item => item.CanonicalPath == dependency);
        Assert.DoesNotContain(binding.Objects, item => item.CanonicalPath == lexicalDecoy);
        NativeDependencyClosure.FromBindingForTesting(binding, fixture.Layout).Revalidate();
        Directory.Delete(bridge);
        Directory.CreateSymbolicLink(bridge, fixture.CreateDirectory("alternate/other-deep"));
        AssertUnavailable(closure.Revalidate);
        AssertUnavailable(() => NativeDependencyClosure.FromBindingForTesting(binding, fixture.Layout));
    }

    [Fact]
    public void A_missing_origin_component_before_dot_dot_is_not_lexically_collapsed()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        fixture.WriteImage("libraries/provider.so", new NativeElfTestImage().AddStringTag(29, "$ORIGIN/not-installed/../target"));
        string decoy = fixture.WriteImage("libraries/target/private.so", new NativeElfTestImage());
        string rootAlias = Path.Join(fixture.LibraryDirectory, "not-installed/../target");

        NativeDependencyClosure closure = fixture.Prepare();
        NativeDependencyClosureBinding binding = closure.ToBindingForTesting();
        NativePathBinding absent = Assert.Single(binding.Paths, path => path.AliasPath == rootAlias);

        Assert.False(absent.Exists);
        Assert.Equal(rootAlias, absent.CanonicalPath);
        Assert.Equal(fixture.LibraryDirectory, absent.ExistingAncestor);
        Assert.DoesNotContain(binding.Objects, item => item.CanonicalPath == decoy);
        NativeDependencyClosure.FromBindingForTesting(binding, fixture.Layout).Revalidate();
        fixture.CreateDirectory("libraries/not-installed");
        AssertUnavailable(closure.Revalidate);
        AssertUnavailable(() => NativeDependencyClosure.FromBindingForTesting(binding, fixture.Layout));
    }


    [Fact]
    public void Absolute_needed_paths_expand_roots_and_protect_every_direct_native_sibling()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        string dependency = fixture.WriteImage("absolute-needed/private.so", new NativeElfTestImage());
        fixture.WriteImage("libraries/object.data", new NativeElfTestImage().AddStringTag(1, dependency));
        string sibling = fixture.WriteImage("absolute-needed/unused.payload", new NativeElfTestImage());

        NativeDependencyClosure closure = fixture.Prepare();

        Assert.Contains(closure.ToBindingForTesting().SearchDirectories, path => path == Path.GetDirectoryName(dependency));
        Assert.Contains(closure.ToBindingForTesting().Objects, item => item.CanonicalPath == sibling);
        fixture.WriteImage("absolute-needed/unused.payload", new NativeElfTestImage().AddStringTag(29, "relative"));
        AssertUnavailable(closure.Revalidate);
    }

    [Fact]
    public void Native_dependency_cycles_terminate_without_skipping_either_objects_path_tags()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        string first = fixture.WriteImage("libraries/first.so", new NativeElfTestImage().AddStringTag(1, "second.so").AddStringTag(29, "$ORIGIN"));
        string second = fixture.WriteImage("libraries/second.so", new NativeElfTestImage().AddStringTag(1, "first.so").AddStringTag(15, "$ORIGIN"));

        NativeDependencyClosure closure = fixture.Prepare();

        Assert.Single(closure.ToBindingForTesting().Objects, item => item.CanonicalPath == first);
        Assert.Single(closure.ToBindingForTesting().Objects, item => item.CanonicalPath == second);
        closure.Revalidate();
    }

    [Fact]
    public void Optional_native_modules_keep_unresolved_dependencies_and_frozen_candidate_absences()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        const string missing = "liblttng-ust.so.0";
        string provider = fixture.WriteImage("libraries/libcoreclrtraceptprovider.so", new NativeElfTestImage().AddStringTag(1, missing));
        fixture.WriteImage("libraries/liblttng-ust.so.1", new NativeElfTestImage());

        NativeDependencyClosure closure = fixture.Prepare();
        NativeDependencyClosureBinding binding = closure.ToBindingForTesting();
        NativeObjectBinding module = Assert.Single(binding.Objects, item => item.CanonicalPath == provider);

        Assert.True(module.Loadable);
        Assert.Equal([missing], module.Image!.Needed);
        Assert.Contains(binding.Paths, path => path.AliasPath == Path.Join(fixture.LibraryDirectory, missing) &&
            !path.Exists && !path.Directory && path.FreezeMetadata);
        Assert.Contains(binding.Paths, path => path.AliasPath == Path.Join(fixture.LibraryDirectory, "glibc-hwcaps", missing) &&
            !path.Exists && path.FreezeMetadata);
        NativeDependencyClosure restored = NativeDependencyClosure.FromBindingForTesting(binding, fixture.Layout);
        Assert.Equal(binding.ManifestSha256, restored.ToBindingForTesting().ManifestSha256);
        restored.Revalidate();
    }

    [Theory]
    [InlineData("libraries")]
    [InlineData("libraries/glibc-hwcaps/x86-64-v3")]
    [InlineData("rpath")]
    [InlineData("cache-alternate")]
    public void Optional_missing_dependencies_cannot_acquire_unsafe_candidates_in_any_frozen_search_namespace(string relative)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        const string missing = "liboptional.so.0";
        fixture.CreateDirectory("libraries/glibc-hwcaps/x86-64-v3");
        string rpath = fixture.CreateDirectory("rpath");
        string cache = fixture.CreateDirectory("cache-alternate");
        fixture.WriteCache([("libcache.so", Path.Join(cache, "not-installed.so"), 0x303, 0)]);
        fixture.WriteImage("libraries/provider.so", new NativeElfTestImage().AddStringTag(1, missing).AddStringTag(29, rpath));
        NativeDependencyClosure closure = fixture.Prepare();
        NativeDependencyClosureBinding binding = closure.ToBindingForTesting();

        string candidate = fixture.WriteImage(relative + "/" + missing, new NativeElfTestImage());
        File.SetUnixFileMode(candidate, File.GetUnixFileMode(candidate) | UnixFileMode.OtherWrite);

        AssertUnavailable(closure.Revalidate);
        AssertUnavailable(() => NativeDependencyClosure.FromBindingForTesting(binding, fixture.Layout));
    }

    [Fact]
    public void Optional_missing_dependencies_freeze_existing_hwcap_directory_inventories()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        const string missing = "liboptional.so.0";
        string capability = fixture.CreateDirectory("libraries/glibc-hwcaps/x86-64-v3");
        fixture.WriteImage("libraries/provider.so", new NativeElfTestImage().AddStringTag(1, missing));
        NativeDependencyClosure closure = fixture.Prepare();
        NativeDependencyClosureBinding binding = closure.ToBindingForTesting();
        Assert.Contains(binding.ConfigurationDirectories, directory => directory.AliasPath == Path.GetDirectoryName(capability));

        fixture.WriteImage("libraries/glibc-hwcaps/x86-64-v4/" + missing, new NativeElfTestImage());

        AssertUnavailable(closure.Revalidate);
        AssertUnavailable(() => NativeDependencyClosure.FromBindingForTesting(binding, fixture.Layout));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Optional_direct_needed_paths_bind_missing_absolute_and_origin_candidates(bool origin)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        string directory = fixture.CreateDirectory("needed");
        string candidate = Path.Join(directory, "liboptional.so.0");
        string needed = origin ? "$ORIGIN/../needed/liboptional.so.0" : candidate;
        fixture.WriteImage("libraries/provider.so", new NativeElfTestImage().AddStringTag(1, needed));
        NativeDependencyClosure closure = fixture.Prepare();
        NativeDependencyClosureBinding binding = closure.ToBindingForTesting();
        Assert.Contains(binding.Paths, path => !path.Directory && !path.Exists && path.FreezeMetadata &&
            path.CanonicalPath == candidate);
        NativeDependencyClosure.FromBindingForTesting(binding, fixture.Layout).Revalidate();

        fixture.WriteImage("needed/liboptional.so.0", new NativeElfTestImage());

        AssertUnavailable(closure.Revalidate);
        AssertUnavailable(() => NativeDependencyClosure.FromBindingForTesting(binding, fixture.Layout));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Launch_required_direct_needed_paths_must_exist_even_when_their_namespaces_are_protected(bool origin)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        string loader = fixture.WriteImage("loader/ld-linux-x86-64.so.2", new NativeElfTestImage(), executable: true);
        string missing = Path.Join(fixture.CreateDirectory("needed"), "librequired.so.0");
        fixture.SetExecutable(new NativeElfTestImage(objectType: 2) { Interpreter = loader }
            .AddStringTag(1, origin ? "$ORIGIN/../needed/librequired.so.0" : missing));

        AssertUnavailable(() => fixture.Prepare());
    }

    [Fact]
    public void Launch_required_dependencies_follow_every_compatible_candidate_transitively()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        string loader = fixture.WriteImage("loader/ld-linux-x86-64.so.2", new NativeElfTestImage(), executable: true);
        string alternate = fixture.CreateDirectory("alternate");
        fixture.WriteImage("libraries/libselected.so", new NativeElfTestImage());
        fixture.WriteImage("alternate/libselected.so", new NativeElfTestImage().AddStringTag(1, "libtransitive-missing.so"));
        fixture.SetExecutable(new NativeElfTestImage(objectType: 2) { Interpreter = loader }
            .AddStringTag(1, "libselected.so").AddStringTag(29, alternate));

        AssertUnavailable(() => fixture.Prepare());
    }

    [Theory]
    [InlineData("executable")]
    [InlineData("interpreter")]
    [InlineData("configuration")]
    [InlineData("selected-library")]
    [InlineData("selected-transitive")]
    [InlineData("ambiguous-direct")]
    [InlineData("ambiguous-runpath")]
    [InlineData("ambiguous-cache")]
    [InlineData("transitive-direct")]
    [InlineData("transitive-runpath")]
    [InlineData("transitive-cache")]
    [InlineData("nss-module")]
    [InlineData("nss-transitive")]
    [InlineData("gconv-module")]
    [InlineData("gconv-transitive")]
    public void Selected_objects_reject_content_changes_and_protection_downgrades_after_manifest_rebinding(string role)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        var targets = new Dictionary<string, (string Path, long Offset)>(StringComparer.Ordinal);
        string WriteTargetImage(string name, string relative, NativeElfTestImage image, bool executable = false)
        {
            NativeElfTestImage.Blob payload = image.AddBlob([0x5a, 0xa5]);
            string path = fixture.WriteImage(relative, image, executable);
            targets.Add(name, (path, payload.Offset));
            return path;
        }

        string loader = WriteTargetImage("interpreter", "loader/ld-linux-x86-64.so.2", new NativeElfTestImage(), executable: true);
        string alternate = fixture.CreateDirectory("alternate");
        WriteTargetImage("selected-library", "libraries/libselected.so",
            new NativeElfTestImage().AddStringTag(1, "libselected-transitive.so"));
        WriteTargetImage("selected-transitive", "libraries/libselected-transitive.so", new NativeElfTestImage());
        WriteTargetImage("ambiguous-direct", "libraries/libambiguous.so",
            new NativeElfTestImage().AddStringTag(1, "libdirect-transitive.so"));
        WriteTargetImage("ambiguous-runpath", "alternate/libambiguous.so",
            new NativeElfTestImage().AddStringTag(1, "librunpath-transitive.so"));
        string cached = WriteTargetImage("ambiguous-cache", "cache-selected/differently-named.payload",
            new NativeElfTestImage().AddStringTag(1, "libcache-transitive.so"));
        WriteTargetImage("transitive-direct", "libraries/libdirect-transitive.so", new NativeElfTestImage());
        WriteTargetImage("transitive-runpath", "libraries/librunpath-transitive.so", new NativeElfTestImage());
        WriteTargetImage("transitive-cache", "libraries/libcache-transitive.so", new NativeElfTestImage());
        WriteTargetImage("nss-module", "libraries/libnss_compat.so.2",
            new NativeElfTestImage().AddStringTag(1, "libnss-transitive.so"));
        WriteTargetImage("nss-transitive", "libraries/libnss-transitive.so", new NativeElfTestImage());
        string gconv = fixture.CreateDirectory("gconv-default");
        string provider = WriteTargetImage("gconv-module", "external-gconv/private.so",
            new NativeElfTestImage().AddStringTag(1, "libgconv-transitive.so"));
        WriteTargetImage("gconv-transitive", "libraries/libgconv-transitive.so", new NativeElfTestImage());
        fixture.WriteData("gconv-default/gconv-modules", $"module EXAMPLE// INTERNAL {provider} 1\n");
        WriteTargetImage("executable", "bin/program", new NativeElfTestImage(objectType: 2) { Interpreter = loader }
            .AddStringTag(1, "libselected.so").AddStringTag(1, "libambiguous.so").AddStringTag(29, alternate), executable: true);
        const string configurationBytes = "sealed loader configuration contents";
        string configuration = fixture.WriteData("system/registered.conf", configurationBytes);
        targets.Add("configuration", (configuration, configurationBytes.Length - 1));
        fixture.WriteCache([("libambiguous.so", cached, 0x303, 0)]);
        NativeLoaderFileSystemLayout layout = fixture.Layout with { GconvDirectories = [gconv] };
        NativeDependencyClosure closure = fixture.Prepare(configurations: [configuration], layout: layout);
        NativeDependencyClosureBinding binding = closure.ToBindingForTesting();
        (string target, long offset) = targets[role];
        NativeObjectBinding frozen = Assert.Single(binding.Objects, item => item.CanonicalPath == target);
        Assert.Equal(NativeObjectProtection.ContentFrozen, frozen.Protection);
        Assert.NotNull(frozen.ContentSha256);
        Assert.Contains(binding.Paths, path => path.CanonicalPath == target && path.Exists && path.FreezeMetadata);
        using (FileStream stream = WineXeBuildToolchainResolver.OpenProtectedRead(target))
        {
            Assert.Equal(Convert.ToHexString(SHA256.HashData(stream)), frozen.ContentSha256);
        }
        NativeDependencyClosure.FromBindingForTesting(binding, layout).Revalidate();

        NativeDependencyClosureBinding downgraded = RecomputeManifestDigestForTesting(binding with
        {
            Objects = binding.Objects.Select(item => item.CanonicalPath == target
                ? item with { Protection = NativeObjectProtection.MetadataOnly, ContentSha256 = null }
                : item).ToArray(),
        });
        AssertUnavailable(() => NativeDependencyClosure.FromBindingForTesting(downgraded, layout));
        closure.Revalidate();

        MutateBytePreservingModificationTimeForTesting(target, offset);
        AssertUnavailable(closure.Revalidate);
        AssertUnavailable(() => NativeDependencyClosure.FromBindingForTesting(binding, layout));
        NativeDependencyClosureBinding rebound =
            RefreshFileIdentityKeepingSelectorsAndContentHashForTesting(binding, target);
        using (FileStream stream = WineXeBuildToolchainResolver.OpenProtectedRead(target))
        {
            Assert.NotEqual(Convert.ToHexString(SHA256.HashData(stream)), frozen.ContentSha256);
        }
        AssertUnavailable(() => NativeDependencyClosure.FromBindingForTesting(rebound, layout));
    }


    [Fact]
    public void A_soname_without_a_filename_alias_or_cache_mapping_is_not_bare_needed_existence()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        fixture.WriteImage("libraries/different-file.so", new NativeElfTestImage().AddStringTag(14, "missing-name.so"));
        string loader = fixture.WriteImage("loader/ld-linux-x86-64.so.2", new NativeElfTestImage(), executable: true);
        fixture.SetExecutable(new NativeElfTestImage(objectType: 2) { Interpreter = loader }.AddStringTag(1, "missing-name.so"));

        AssertUnavailable(() => fixture.Prepare());
    }

    [Fact]
    public void Cache_name_mapping_counts_as_an_existing_compatible_bare_dependency()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        string library = fixture.WriteImage("libraries/different-file.so", new NativeElfTestImage());
        string loader = fixture.WriteImage("loader/ld-linux-x86-64.so.2", new NativeElfTestImage(), executable: true);
        fixture.SetExecutable(new NativeElfTestImage(objectType: 2) { Interpreter = loader }.AddStringTag(1, "cache-name.so"));
        fixture.WriteCache([("cache-name.so", library, 0x303, 0)]);

        fixture.Prepare().Revalidate();
    }

    [Fact]
    public void Both_library_ABIs_are_inspected_but_one_cannot_satisfy_the_others_needed_name()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        fixture.WriteImage("libraries/i386/private.so", new NativeElfTestImage(elfClass: 1));
        string loader = fixture.WriteImage("loader/ld-linux-x86-64.so.2", new NativeElfTestImage(), executable: true);
        fixture.SetExecutable(new NativeElfTestImage(objectType: 2) { Interpreter = loader }.AddStringTag(1, "private.so"));

        AssertUnavailable(() => fixture.Prepare(modules: [fixture.LibraryDirectory]));
    }

    [Fact]
    public void Foreign_ELF_data_is_structurally_bound_but_never_admitted_as_current_native_code()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        string foreign = fixture.WriteImage("libraries/firmware.payload", new NativeElfTestImage(machine: 0xFEED));
        NativeDependencyClosure closure = fixture.Prepare();
        NativeObjectBinding binding = Assert.Single(closure.ToBindingForTesting().Objects, item => item.CanonicalPath == foreign);
        Assert.False(binding.Loadable);
        Assert.Equal((ushort)0xFEED, binding.Image!.Machine);
        AssertUnavailable(() => fixture.Prepare(executables: [foreign]));

        string loader = fixture.WriteImage("loader/ld-linux-x86-64.so.2", new NativeElfTestImage(), executable: true);
        fixture.SetExecutable(new NativeElfTestImage(objectType: 2) { Interpreter = loader }.AddStringTag(1, "firmware.payload"));
        AssertUnavailable(() => fixture.Prepare());
    }

    [Fact]
    public void Foreign_data_ELF_cannot_hide_a_relative_or_unsafe_path_selector()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        fixture.WriteImage("libraries/firmware.payload", new NativeElfTestImage(machine: 0xFEED).AddStringTag(29, "$ORIGIN/../attacker"));
        string attacker = fixture.CreateDirectory("attacker");
        File.SetUnixFileMode(attacker, File.GetUnixFileMode(attacker) | UnixFileMode.OtherWrite);

        AssertUnavailable(() => fixture.Prepare());
    }

    [Theory]
    [InlineData(0x6FFFFEFAUL)] // DT_CONFIG.
    [InlineData(0x6FFFFEFBUL)] // DT_DEPAUDIT.
    [InlineData(0x6FFFFEFCUL)] // DT_AUDIT.
    [InlineData(0x7FFFFFFDUL)] // DT_AUXILIARY.
    [InlineData(0x7FFFFFFFUL)] // DT_FILTER.
    public void Unsupported_dynamic_loader_selectors_fail_even_in_a_nonexecuting_ELF_leaf(ulong tag)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        fixture.WriteImage("libraries/ordinary-payload.data", new NativeElfTestImage().AddStringTag(tag, "/attacker/injection.so"));

        AssertUnavailable(() => fixture.Prepare());
        Assert.False(File.Exists(fixture.Marker));
    }

    [Theory]
    [InlineData("unknown-interpreter")]
    [InlineData("dynamic-without-interpreter")]
    [InlineData("ordinary-dso")]
    [InlineData("unrecognized-script")]
    public void Executable_roots_require_supported_loader_or_a_genuinely_static_native_image(string kind)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        string executable;
        switch (kind)
        {
            case "unknown-interpreter":
                string loader = fixture.WriteImage("loader/not-a-reviewed-loader", new NativeElfTestImage());
                executable = fixture.WriteImage("bin/program", new NativeElfTestImage(objectType: 2) { Interpreter = loader }, executable: true);
                break;
            case "dynamic-without-interpreter":
                executable = fixture.WriteImage("bin/program", new NativeElfTestImage(objectType: 2), executable: true);
                break;
            case "ordinary-dso":
                executable = fixture.WriteImage("bin/program", new NativeElfTestImage(), executable: true);
                break;
            default:
                executable = fixture.WriteData("bin/program", $"#!/bin/sh\ntouch '{fixture.Marker}'\n");
                File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                break;
        }

        AssertUnavailable(() => fixture.Prepare(executables: [executable]));
        Assert.False(File.Exists(fixture.Marker));
    }

    [Fact]
    public void Loader_configuration_includes_register_all_configured_roots_and_freeze_the_include_inventory()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        string fragments = fixture.CreateDirectory("system/ld.so.conf.d");
        string configured = fixture.CreateDirectory("configured-native");
        fixture.WriteData("system/ld.so.conf", $"include {fragments}/*.conf\n");
        fixture.WriteData("system/ld.so.conf.d/native.conf", configured + "\n");
        string library = fixture.WriteImage("configured-native/no-so-extension", new NativeElfTestImage());

        NativeDependencyClosure closure = fixture.Prepare();

        Assert.Contains(closure.ToBindingForTesting().SearchDirectories, path => path == configured);
        Assert.Contains(closure.ToBindingForTesting().Objects, item => item.CanonicalPath == library);
        fixture.WriteData("system/ld.so.conf.d/new.conf", fixture.LibraryDirectory);
        AssertUnavailable(closure.Revalidate);
    }

    [Theory]
    [InlineData("include relative/*.conf")]
    [InlineData("include /protected/../attacker/*.conf")]
    [InlineData("hwcap 1 attacker")]
    [InlineData("relative/library")]
    public void Unknown_loader_configuration_search_semantics_fail_closed(string text)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        fixture.WriteData("system/ld.so.conf", text);

        AssertUnavailable(() => fixture.Prepare());
    }

    [Fact]
    public void Gconv_text_config_module_paths_expand_the_frozen_graph_without_using_the_binary_cache()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        string gconv = fixture.CreateDirectory("gconv-default");
        string provider = fixture.WriteImage("external-gconv/private.so", new NativeElfTestImage());
        fixture.WriteData("gconv-default/gconv-modules", $"alias EXAMPLE// UTF-8//\nmodule EXAMPLE// INTERNAL {provider} 1\n");
        NativeLoaderFileSystemLayout layout = fixture.Layout with { GconvDirectories = [gconv] };

        NativeDependencyClosure closure = fixture.Prepare(layout: layout);

        NativeDependencyClosureBinding binding = closure.ToBindingForTesting();
        Assert.Contains(provider, binding.GconvModulePaths);
        NativeObjectBinding selected = Assert.Single(binding.Objects, item => item.CanonicalPath == provider);
        Assert.Equal(NativeObjectProtection.ContentFrozen, selected.Protection);
        Assert.NotNull(selected.ContentSha256);
        fixture.WriteData("gconv-default/gconv-modules", "module EXAMPLE// INTERNAL ../attacker 1\n");
        AssertUnavailable(closure.Revalidate);
        AssertUnavailable(() => fixture.Prepare(layout: layout));
    }

    [Fact]
    public void Binding_roundtrip_preserves_the_original_input_alias_root_and_object_proof()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        fixture.WriteImage("libraries/private.so", new NativeElfTestImage().AddStringTag(29, "$ORIGIN"));
        NativeDependencyClosure original = fixture.Prepare();
        byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(original.ToBindingForTesting());
        NativeDependencyClosureBinding binding = JsonSerializer.Deserialize<NativeDependencyClosureBinding>(serialized)!;

        NativeDependencyClosure restored = NativeDependencyClosure.FromBindingForTesting(binding, fixture.Layout);

        Assert.Equal(original.ToBindingForTesting().InputsSha256, restored.ToBindingForTesting().InputsSha256);
        Assert.Equal(original.ToBindingForTesting().ManifestSha256, restored.ToBindingForTesting().ManifestSha256);
        restored.ValidateExecutable(fixture.Executable);
        restored.Revalidate();
    }

    [Theory]
    [InlineData("downgrade")]
    [InlineData("stale-schema")]
    [InlineData("input-rebinding")]
    [InlineData("root-removal")]
    [InlineData("recursive-root-removal")]
    [InlineData("nss-downgrade")]
    [InlineData("object-removal")]
    [InlineData("alias-rebinding")]
    [InlineData("environment-rebinding")]
    [InlineData("loader-rebinding")]
    public void Serialized_bindings_never_silently_prepare_a_different_trust_set(string mutation)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        NativeDependencyClosure original = fixture.Prepare();
        NativeDependencyClosureBinding binding = original.ToBindingForTesting();
        switch (mutation)
        {
            case "downgrade": binding = binding with { Version = 0 }; break;
            case "stale-schema": binding = binding with { Version = 1 }; break;
            case "input-rebinding": binding.Inputs.ExecutablePaths[0] = "/unregistered/program"; break;
            case "root-removal": binding = binding with { SearchDirectories = [] }; break;
            case "recursive-root-removal": binding = binding with { RecursiveDirectories = [] }; break;
            case "nss-downgrade": binding = binding with { NssServices = [] }; break;
            case "object-removal": binding = binding with { Objects = [] }; break;
            case "alias-rebinding": binding.Paths[0] = binding.Paths[0] with { CanonicalPath = "/different/root" }; break;
            case "environment-rebinding": binding.Inputs.EnvironmentBindings["HOME"] = "/different/home"; break;
            case "loader-rebinding": binding = binding with { LoaderLayout = binding.LoaderLayout with { CachePath = "/different/cache" } }; break;
        }
        binding = RecomputeManifestDigestForTesting(binding);

        AssertUnavailable(() => NativeDependencyClosure.FromBindingForTesting(binding, fixture.Layout));
        original.Revalidate();
    }

    [Fact]
    public void Caller_owned_input_collections_and_returned_binding_arrays_cannot_mutate_the_prepared_closure()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        string[] executables = [fixture.Executable];
        string[] modules = [fixture.LibraryDirectory];
        var environment = new Dictionary<string, string>(fixture.Environment, StringComparer.Ordinal);
        var spec = new NativeDependencyClosureSpec(executables, modules, [], environment);
        executables[0] = "/unregistered/program";
        modules[0] = "/unregistered/native";
        environment["HOME"] = "/unregistered/home";
        NativeDependencyClosure closure = NativeDependencyClosure.PrepareForTesting(spec, fixture.Layout);
        NativeDependencyClosureBinding binding = closure.ToBindingForTesting();
        binding.Inputs.ExecutablePaths[0] = "/unregistered/program";
        binding.Objects[0] = binding.Objects[0] with { ContentSha256 = new string('0', 64) };

        closure.ValidateExecutable(fixture.Executable);
        closure.Revalidate();
        Assert.Equal([fixture.Executable], closure.ToBindingForTesting().Inputs.ExecutablePaths);
    }

    [Theory]
    [InlineData("native-content")]
    [InlineData("native-mode")]
    [InlineData("root-mode")]
    [InlineData("root-inode")]
    [InlineData("alias-target")]
    [InlineData("cache-content")]
    public void Postprepare_mutations_are_rejected_instead_of_rescanning_and_rebinding(string mutation)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        string library = fixture.WriteImage("libraries/private.so", new NativeElfTestImage());
        string second = fixture.WriteImage("other/private.so", new NativeElfTestImage());
        string alias = Path.Join(fixture.LibraryDirectory, "alias.so");
        File.CreateSymbolicLink(alias, library);
        fixture.WriteCache([("private.so", library, 0x303, 0)]);
        NativeDependencyClosure closure = fixture.Prepare();
        NativeDependencyClosureBinding binding = closure.ToBindingForTesting();
        switch (mutation)
        {
            case "native-content":
                byte[] bytes = File.ReadAllBytes(library);
                bytes[^1] ^= 1;
                DateTime timestamp = File.GetLastWriteTimeUtc(library);
                File.WriteAllBytes(library, bytes);
                File.SetLastWriteTimeUtc(library, timestamp);
                break;
            case "native-mode": File.SetUnixFileMode(library, File.GetUnixFileMode(library) | UnixFileMode.GroupWrite); break;
            case "root-mode": File.SetUnixFileMode(fixture.LibraryDirectory, File.GetUnixFileMode(fixture.LibraryDirectory) | UnixFileMode.OtherWrite); break;
            case "root-inode": Directory.Move(fixture.LibraryDirectory, fixture.LibraryDirectory + "-old"); fixture.CreateDirectory("libraries"); break;
            case "alias-target": File.Delete(alias); File.CreateSymbolicLink(alias, second); break;
            case "cache-content": fixture.WriteCache([("private.so", second, 0x303, 0)]); break;
        }

        AssertUnavailable(closure.Revalidate);
        AssertUnavailable(() => NativeDependencyClosure.FromBindingForTesting(binding, fixture.Layout));
    }

    [Fact]
    public void Mutable_noncode_data_does_not_byte_freeze_a_private_working_directory()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        string data = fixture.WriteData("libraries/build-data.txt", "before");
        NativeDependencyClosure closure = fixture.Prepare();

        File.WriteAllText(data, "after, with a different length");
        fixture.WriteData("libraries/output-data.txt", "new private output");

        closure.Revalidate();
    }

    [Fact]
    public void Test_system_layout_can_never_cross_the_marked_production_launch_boundary()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        NativeDependencyClosure closure = fixture.Prepare();
        var startInfo = new System.Diagnostics.ProcessStartInfo();
        startInfo.Environment["UNTRUSTED_MARKER"] = fixture.Marker;

        AssertUnavailable(closure.EnsureProduction);
        AssertUnavailable(() => closure.ToBinding());
        AssertUnavailable(() => closure.ApplyClosedEnvironment(startInfo));
        AssertUnavailable(() => NativeDependencyClosure.FromBinding(closure.ToBindingForTesting()));
        Assert.Equal(fixture.Marker, startInfo.Environment["UNTRUSTED_MARKER"]);
    }

    [Theory]
    [InlineData("LD_PRELOAD")]
    [InlineData("LD_LIBRARY_PATH")]
    [InlineData("GLIBC_TUNABLES")]
    [InlineData("GCONV_PATH")]
    [InlineData("OPENSSL_CONF")]
    [InlineData("DOTNET_STARTUP_HOOKS")]
    [InlineData("WINEDLLPATH")]
    [InlineData("WINESYSTEMDLLPATH")]
    [InlineData("BASH_ENV")]
    [InlineData("DISPLAY")]
    [InlineData("GIO_EXTRA_MODULES")]
    public void Loader_and_module_environment_hooks_cannot_be_registered_as_allowlist_bindings(string name)
    {
        Assert.False(NativeDependencyClosure.IsAllowedEnvironmentBinding(name, "/attacker/injection"));
        AssertUnavailable(() => new NativeDependencyClosureSpec(["/protected/program"], [], [],
            new Dictionary<string, string> { [name] = "/attacker/injection" }));
    }

    [Fact]
    public void Installed_CLR_native_assets_are_supported_static_ELF_facts_without_running_a_loader()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
        string runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        using var fixture = new NativeFixture();
        string copiedRuntime = fixture.CreateDirectory("copied-runtime");
        foreach (string name in new[] { "libcoreclr.so", "libclrjit.so", "libhostpolicy.so", "libSystem.Native.so" })
        {
            string path = Path.Join(copiedRuntime, name);
            // Inspect genuine CLR bytes from a protected fixture, not the test runner's arbitrary install ancestry.
            File.Copy(Path.Join(runtime, name), path);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            using FileStream stream = WineXeBuildToolchainResolver.OpenProtectedRead(path);
            NativeElfImage image = NativeElfReader.Read(stream)!;
            Assert.NotNull(image);
            Assert.Equal((ushort)62, image.Machine);
            Assert.Equal((byte)2, image.ElfClass);
            Assert.Equal((ushort)3, image.ObjectType);
            Assert.True(image.HasSupportedAbi);
            Assert.True(image.HasDynamicSegment);
            Assert.Contains("libc.so.6", image.Needed);
            foreach (string component in image.RPath.Concat(image.RunPath))
            {
                Assert.StartsWith("/", NativeDependencyClosure.ExpandObjectPath(path, component));
            }
        }
    }

    [Fact]
    public void Loader_search_roots_do_not_descend_unrelated_children_but_explicit_module_roots_do()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        string nested = fixture.WriteImage("libraries/unrelated/nested/unused.payload",
            new NativeElfTestImage().AddStringTag(29, "relative"));

        NativeDependencyClosure direct = fixture.Prepare();

        Assert.DoesNotContain(direct.ToBindingForTesting().Objects, item => item.CanonicalPath == nested);
        direct.Revalidate();
        AssertUnavailable(() => fixture.Prepare(modules: [fixture.LibraryDirectory]));
    }

    [Theory]
    [InlineData("module", "native")]
    [InlineData("module", "pe")]
    [InlineData("module", "nonlocal-dependency")]
    [InlineData("module", "unsafe-mode")]
    [InlineData("hwcap", "native")]
    [InlineData("hwcap", "pe")]
    [InlineData("hwcap", "nonlocal-dependency")]
    [InlineData("legacy-capability", "native")]
    [InlineData("legacy-capability", "pe")]
    [InlineData("legacy-capability", "nonlocal-dependency")]
    public void Recursive_module_and_capability_roots_freeze_deep_native_and_PE_code_and_nonlocal_dependencies(
        string rootKind, string role)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        string extra = fixture.CreateDirectory("module-selected-code");
        var dependencyImage = new NativeElfTestImage();
        NativeElfTestImage.Blob dependencyPayload = dependencyImage.AddBlob([0x5a]);
        string dependency = fixture.WriteImage("module-selected-code/private.so", dependencyImage);
        string relativeRoot = rootKind switch
        {
            "module" => "wine",
            "hwcap" => "libraries/glibc-hwcaps/x86-64-v4",
            _ => "libraries/tls/haswell/x86_64",
        };
        var image = new NativeElfTestImage().AddStringTag(29, extra).AddStringTag(1, "private.so");
        NativeElfTestImage.Blob payload = image.AddBlob([0x5a]);
        string objectFile = fixture.WriteImage(relativeRoot + "/modules/deep/native-binary.payload", image);
        const string portableBytes = "MZ recursive native fixture payload";
        string portable = fixture.WriteData(relativeRoot + "/modules/deep/windows-binary.payload", portableBytes);
        string recursiveRoot = Path.Join(fixture.Root, rootKind switch
        {
            "module" => "wine",
            "hwcap" => "libraries/glibc-hwcaps",
            _ => "libraries/tls",
        });
        NativeDependencyClosure closure = fixture.Prepare(modules: rootKind == "module" ? [recursiveRoot] : []);
        NativeDependencyClosureBinding binding = closure.ToBindingForTesting();

        Assert.Contains(recursiveRoot, binding.RecursiveDirectories);
        Assert.Contains(extra, binding.SearchDirectories);
        foreach (string path in new[] { objectFile, portable, dependency })
        {
            NativeObjectBinding frozen = Assert.Single(binding.Objects, item => item.CanonicalPath == path);
            Assert.Equal(NativeObjectProtection.ContentFrozen, frozen.Protection);
            Assert.NotNull(frozen.ContentSha256);
        }
        Assert.True(Assert.Single(binding.Objects, item => item.CanonicalPath == portable).PortableExecutable);
        NativeDependencyClosure restored = NativeDependencyClosure.FromBindingForTesting(binding, fixture.Layout);
        restored.Revalidate();
        if (role == "unsafe-mode")
        {
            File.SetUnixFileMode(dependency, File.GetUnixFileMode(dependency) | UnixFileMode.OtherWrite);
            AssertUnavailable(restored.Revalidate);
            AssertUnavailable(() => fixture.Prepare(modules: [recursiveRoot]));
            return;
        }
        (string target, long offset) = role switch
        {
            "native" => (objectFile, payload.Offset),
            "pe" => (portable, portableBytes.Length - 1),
            _ => (dependency, dependencyPayload.Offset),
        };
        NativeDependencyClosureBinding downgraded = RecomputeManifestDigestForTesting(binding with
        {
            Objects = binding.Objects.Select(item => item.CanonicalPath == target
                ? item with { Protection = NativeObjectProtection.MetadataOnly, ContentSha256 = null }
                : item).ToArray(),
        });
        AssertUnavailable(() => NativeDependencyClosure.FromBindingForTesting(downgraded, fixture.Layout));
        closure.Revalidate();

        MutateBytePreservingModificationTimeForTesting(target, offset);
        AssertUnavailable(closure.Revalidate);
        AssertUnavailable(restored.Revalidate);
        NativeDependencyClosureBinding rebound =
            RefreshFileIdentityKeepingSelectorsAndContentHashForTesting(binding, target);
        AssertUnavailable(() => NativeDependencyClosure.FromBindingForTesting(rebound, fixture.Layout));
    }

    [Theory]
    [InlineData("glibc-hwcaps/x86-64-v4/deep/unused.payload")]
    [InlineData("tls/haswell/x86_64/deep/unused.payload")]
    [InlineData("sse2/tls/i686/deep/unused.payload")]
    public void Modern_and_legacy_capability_trees_are_recursive_even_when_not_selected_by_the_current_CPU(string relative)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        fixture.WriteImage("libraries/" + relative, new NativeElfTestImage().AddStringTag(29, "relative"));

        AssertUnavailable(() => fixture.Prepare());
        Assert.False(File.Exists(fixture.Marker));
    }

    [Fact]
    public void Absent_capability_roots_cannot_be_planted_after_the_frozen_binding()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        NativeDependencyClosure closure = fixture.Prepare();
        NativeDependencyClosureBinding binding = closure.ToBindingForTesting();
        string capabilities = Path.Join(fixture.LibraryDirectory, "glibc-hwcaps");
        Assert.Contains(binding.Paths, path => path.AliasPath == capabilities && path.Directory && !path.Exists);
        fixture.WriteImage("libraries/glibc-hwcaps/x86-64-v4/private.so", new NativeElfTestImage());

        AssertUnavailable(closure.Revalidate);
        AssertUnavailable(() => NativeDependencyClosure.FromBindingForTesting(binding, fixture.Layout));
    }

    [Fact]
    public void Relocatable_ELF_data_is_bound_but_cannot_satisfy_a_native_needed_name()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        string data = fixture.WriteImage("libraries/object.payload", new NativeElfTestImage(objectType: 1));
        NativeDependencyClosure closure = fixture.Prepare();
        NativeObjectBinding image = Assert.Single(closure.ToBindingForTesting().Objects, item => item.CanonicalPath == data);
        Assert.False(image.Loadable);
        Assert.Equal((ushort)1, image.Image!.ObjectType);
        string loader = fixture.WriteImage("loader/ld-linux-x86-64.so.2", new NativeElfTestImage(), executable: true);
        fixture.SetExecutable(new NativeElfTestImage(objectType: 2) { Interpreter = loader }.AddStringTag(1, "object.payload"));

        AssertUnavailable(() => fixture.Prepare());
    }

    [Theory]
    [InlineData("hosts: files mdns4_minimal [NOTFOUND=return] dns\npasswd: files systemd sss winbind\nnetgroup: nis")]
    [InlineData("hosts: files [!UNAVAIL=continue SUCCESS=return NOTFOUND=merge TRYAGAIN=continue] dns")]
    [InlineData("automount: sss\npasswd : files\n# Ubuntu base has files/db/dns/nis selectors\nservices: db files")]
    public void Nss_source_configuration_is_frozen_and_normal_Ubuntu_service_actions_are_supported(string contents)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        string configuration = fixture.WriteData("system/nsswitch.conf", contents);
        NativeLoaderFileSystemLayout layout = fixture.Layout with { NssConfigurationPath = configuration };
        string library = fixture.WriteImage("libraries/libnss_files.so.2", new NativeElfTestImage());
        NativeDependencyClosure closure = fixture.Prepare(layout: layout);
        NativeDependencyClosureBinding binding = closure.ToBindingForTesting();

        Assert.Contains(binding.Paths, path => path.AliasPath == configuration && path.FreezeMetadata);
        Assert.Contains("files", binding.NssServices);
        Assert.Contains("dns", binding.NssServices);
        NativeObjectBinding selected = Assert.Single(binding.Objects, item => item.CanonicalPath == library);
        Assert.Equal(NativeObjectProtection.ContentFrozen, selected.Protection);
        Assert.NotNull(selected.ContentSha256);
        NativeDependencyClosure.FromBindingForTesting(binding, layout).Revalidate();
        File.WriteAllText(configuration, "passwd: files\nhosts: dns");
        AssertUnavailable(closure.Revalidate);
    }

    [Theory]
    [InlineData("passwd files")]
    [InlineData("passwd: /attacker/module")]
    [InlineData("passwd: $ORIGIN")]
    [InlineData("passwd: custom-unmodeled-service")]
    [InlineData("passwd: files [NOTFOUND=execute]")]
    [InlineData("passwd: files [UNKNOWN=return]")]
    [InlineData("passwd: files []")]
    [InlineData("passwd: files [NOTFOUND=return")]
    [InlineData("passwd: files ]")]
    [InlineData("passwd: files: dns")]
    public void Unknown_pathlike_or_malformed_NSS_selectors_fail_without_executing_discovery(string contents)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        string configuration = fixture.WriteData("system/nsswitch.conf", contents);

        AssertUnavailable(() => fixture.Prepare(layout: fixture.Layout with { NssConfigurationPath = configuration }));
        Assert.False(File.Exists(fixture.Marker));
    }

    [Fact]
    public void Missing_NSS_configuration_and_its_default_service_selection_remain_frozen()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        string path = Path.Join(fixture.Root, "system/nsswitch.conf");
        NativeLoaderFileSystemLayout layout = fixture.Layout with { NssConfigurationPath = path };
        NativeDependencyClosure closure = fixture.Prepare(layout: layout);
        NativeDependencyClosureBinding binding = closure.ToBindingForTesting();
        Assert.Equal(new[] { "compat", "dns", "files", "nis", "nisplus" }, binding.NssServices);
        fixture.WriteData("system/nsswitch.conf", "passwd: files");

        AssertUnavailable(closure.Revalidate);
        AssertUnavailable(() => NativeDependencyClosure.FromBindingForTesting(binding, layout));
    }

    [Theory]
    [InlineData("direct", "elf-metadata")]
    [InlineData("direct", "elf-needed")]
    [InlineData("direct", "elf-rpath")]
    [InlineData("direct", "elf-runpath")]
    [InlineData("direct", "elf-soname")]
    [InlineData("direct", "elf-flags")]
    [InlineData("cache", "elf-metadata")]
    [InlineData("cache", "elf-needed")]
    [InlineData("cache", "elf-rpath")]
    [InlineData("cache", "elf-runpath")]
    [InlineData("cache", "elf-soname")]
    [InlineData("cache", "elf-flags")]
    [InlineData("direct", "nss-selection")]
    public void Restoring_a_binding_reparses_bound_selectors_even_if_the_unkeyed_manifest_digest_is_recomputed(
        string location, string mutation)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        string relativeRoot = location == "cache" ? "cache-only" : "libraries";
        string library = fixture.WriteImage(relativeRoot + "/private.so", new NativeElfTestImage()
            .AddStringTag(1, "liboptional-missing.so").AddStringTag(15, "$ORIGIN").AddStringTag(29, "$ORIGIN")
            .AddStringTag(14, "before.so"));
        if (location == "cache")
        {
            fixture.WriteCache([("private.so", library, 0x303, 0)]);
        }
        string nss = fixture.WriteData("system/nsswitch.conf", "hosts: files dns");
        NativeLoaderFileSystemLayout layout = fixture.Layout with { NssConfigurationPath = nss };
        NativeDependencyClosure closure = fixture.Prepare(layout: layout);
        NativeDependencyClosureBinding binding = closure.ToBindingForTesting();
        if (mutation == "nss-selection")
        {
            binding = binding with { NssServices = ["files"] };
        }
        else
        {
            int index = Array.FindIndex(binding.Objects, item => item.CanonicalPath == library);
            NativeObjectBinding original = binding.Objects[index];
            Assert.Equal(NativeObjectProtection.MetadataOnly, original.Protection);
            Assert.Null(original.ContentSha256);
            NativeElfImageBinding image = original.Image!;
            binding.Objects[index] = mutation switch
            {
                "elf-metadata" => original with { Image = null, Loadable = false },
                "elf-needed" => original with { Image = image with { Needed = [] } },
                "elf-rpath" => original with { Image = image with { RPath = [] } },
                "elf-runpath" => original with { Image = image with { RunPath = [] } },
                "elf-soname" => original with { Image = image with { Soname = "after.so" } },
                "elf-flags" => original with { Image = image with { Flags = 1, Flags1 = 1 } },
                _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
            };
        }
        binding = RecomputeManifestDigestForTesting(binding);

        AssertUnavailable(() => NativeDependencyClosure.FromBindingForTesting(binding, layout));
        closure.Revalidate();
    }

    [Theory]
    [InlineData("root", true)]
    [InlineData("wine-user.17", true)]
    [InlineData("normal_user", true)]
    [InlineData("", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("../attacker", false)]
    [InlineData("$ORIGIN", false)]
    [InlineData("remote:user", false)]
    public void Sealed_USER_is_a_literal_name_not_a_native_path_or_module_selector(string name, bool allowed)
    {
        Assert.Equal(allowed, NativeDependencyClosure.IsAllowedEnvironmentBinding("USER", name));
    }

    [Fact]
    public void Ordinary_data_may_be_replaced_or_removed_but_frozen_native_code_and_search_anchors_may_not()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new NativeFixture();
        string data = fixture.WriteData("libraries/build-data.txt", "before");
        string code = fixture.WriteImage("libraries/private.so", new NativeElfTestImage());
        NativeDependencyClosure closure = fixture.Prepare(modules: [fixture.LibraryDirectory]);
        NativeDependencyClosureBinding binding = closure.ToBindingForTesting();
        Assert.DoesNotContain(binding.Paths, path => path.AliasPath == data);
        File.Delete(data);
        fixture.WriteData("libraries/build-data.txt", "replacement with a new inode");
        fixture.WriteData("libraries/cpukey.txt", new string('0', 32));
        closure.Revalidate();
        File.Delete(data);
        closure.Revalidate();
        File.Delete(code);

        AssertUnavailable(closure.Revalidate);
    }

    [Theory]
    [InlineData("module")]
    [InlineData("search-root")]
    [InlineData("alias-target")]
    [InlineData("configuration")]
    public void Cross_UID_owned_code_search_roots_and_selector_files_are_never_trusted_even_when_not_world_writable(string location)
    {
        if (!OperatingSystem.IsLinux() || WineXeBuildToolchainResolver.GetNativeUserId() != 0) return;
        using var fixture = new NativeFixture();
        string module = fixture.WriteImage("libraries/private.so", new NativeElfTestImage());
        string target = fixture.WriteImage("targets/native.so", new NativeElfTestImage());
        File.CreateSymbolicLink(Path.Join(fixture.LibraryDirectory, "alias.so"), target);
        string configuration = fixture.WriteData("system/native.conf", "ordinary protected selector data");
        string changed = location switch
        {
            "module" => module,
            "search-root" => fixture.LibraryDirectory,
            "alias-target" => target,
            _ => configuration,
        };
        Assert.Equal(0, ChangeOwner(changed, 65534, uint.MaxValue));
        try
        {
            AssertUnavailable(() => fixture.Prepare(configurations: [configuration]));
            Assert.False(File.Exists(fixture.Marker));
        }
        finally
        {
            Assert.Equal(0, ChangeOwner(changed, 0, uint.MaxValue));
        }
    }

    [DllImport("libc", EntryPoint = "chown", SetLastError = true)]
    private static extern int ChangeOwner([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint owner, uint group);


    private static NativeDependencyClosureBinding RecomputeManifestDigestForTesting(NativeDependencyClosureBinding binding) =>
        binding with
        {
            ManifestSha256 = Convert.ToHexString(SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(binding with { ManifestSha256 = string.Empty }))),
        };

    private static NativeDependencyClosureBinding RefreshFileIdentityKeepingSelectorsAndContentHashForTesting(
        NativeDependencyClosureBinding binding, string canonicalPath)
    {
        // TEST-ONLY: refresh real descriptor identities, never the original Image/selectors or frozen content hash.
        // A recomputed unkeyed manifest then isolates metadata reparsing from content SHA protection.
        using FileStream stream = WineXeBuildToolchainResolver.OpenProtectedRead(canonicalPath);
        NativeFileIdentity identity = WineXeBuildToolchainResolver.GetNativeIdentity(stream.SafeFileHandle);
        NativeObjectBinding original = Assert.Single(binding.Objects, item => item.CanonicalPath == canonicalPath);
        Assert.NotEqual(original.Identity, identity);
        return RecomputeManifestDigestForTesting(binding with
        {
            Objects = binding.Objects.Select(item => item.CanonicalPath == canonicalPath
                ? item with { Identity = identity }
                : item).ToArray(),
            Paths = binding.Paths.Select(path => path.Exists && !path.Directory && path.CanonicalPath == canonicalPath
                ? path with { Identity = identity }
                : path).ToArray(),
        });
    }

    private static void MutateBytePreservingModificationTimeForTesting(string path, long offset)
    {
        DateTime timestamp = File.GetLastWriteTimeUtc(path);
        using (FileStream stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.Position = offset;
            int value = stream.ReadByte();
            Assert.InRange(value, 0, 255);
            stream.Position = offset;
            stream.WriteByte((byte)(value ^ 1));
        }
        File.SetLastWriteTimeUtc(path, timestamp);
    }

    private static void AssertUnavailable(Action action)
    {
        OperationFailureException failure = Assert.Throws<OperationFailureException>(action);
        Assert.Equal(ExitCode.MissingPrerequisite, failure.Code);
        Assert.Equal("native-closure-unavailable", failure.Kind);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain("attacker", failure.Message, StringComparison.Ordinal);
    }

    private sealed class NativeFixture : IDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        internal NativeFixture()
        {
            Root = TemporaryDirectory.GetCanonicalPath(_directory.Path);
            LibraryDirectory = CreateDirectory("libraries");
            CreateDirectory("system");
            string home = CreateDirectory("private-home");
            string tools = CreateDirectory("private-tools");
            string config = CreateDirectory("private-home/config");
            string data = CreateDirectory("private-home/data");
            string cache = CreateDirectory("private-home/cache");
            CreateDirectory("private-home/native-modules");
            WriteData("private-home/native-openssl.cnf", "");
            Environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["HOME"] = home, ["PATH"] = tools, ["XDG_CONFIG_HOME"] = config,
                ["XDG_DATA_HOME"] = data, ["XDG_CACHE_HOME"] = cache,
            };
            Executable = WriteImage("bin/program", new NativeElfTestImage(objectType: 2) { HasDynamicSegment = false }, executable: true);
            Marker = Path.Join(Root, "never-executed.marker");
            Layout = new NativeLoaderFileSystemLayout(Path.Join(Root, "system/ld.so.cache"),
                Path.Join(Root, "system/ld.so.preload"), Path.Join(Root, "system/ld.so.conf"), [LibraryDirectory], []);
        }
        internal string Root { get; }
        internal string Executable { get; }
        internal string LibraryDirectory { get; }
        internal string Marker { get; }
        internal IReadOnlyDictionary<string, string> Environment { get; }
        internal NativeLoaderFileSystemLayout Layout { get; }
        internal string CreateDirectory(string relative) => TemporaryDirectory.GetCanonicalPath(_directory.CreateDirectory(relative));
        internal string WriteImage(string relative, NativeElfTestImage image, bool executable = false)
        {
            string path = WriteBytes(relative, image.Build());
            if (executable)
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            return path;
        }
        internal string WriteData(string relative, string text) => WriteBytes(relative, System.Text.Encoding.UTF8.GetBytes(text));
        private string WriteBytes(string relative, byte[] bytes)
        {
            _directory.CreateDirectory(Path.GetDirectoryName(relative) ?? string.Empty);
            string path = Path.Join(Root, relative);
            File.WriteAllBytes(path, bytes);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return path;
        }
        internal void SetExecutable(NativeElfTestImage image) => WriteImage("bin/program", image, executable: true);
        internal void WriteCache((string Name, string Path, int Flags, ulong HardwareCapabilities)[] entries) =>
            WriteBytes("system/ld.so.cache", NativeLoaderCacheReaderTests.CreateCache(entries));
        internal NativeDependencyClosure Prepare(string[]? executables = null, string[]? modules = null, string[]? configurations = null,
            NativeLoaderFileSystemLayout? layout = null)
        {
            var spec = new NativeDependencyClosureSpec(executables ?? [Executable], modules ?? [], configurations ?? [], Environment);
            return NativeDependencyClosure.PrepareForTesting(spec, layout ?? Layout);
        }
        public void Dispose() => _directory.Dispose();
    }
}
