using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using JRunner.Cli.Infrastructure;
using JRunner.Core.Contracts;
using Xunit;
using ProtectedDirectory = JRunner.Cli.Tests.WineXeBuildBackendTests.TemporaryDirectory;

namespace JRunner.Cli.Tests;

public sealed class ExternalProcessSupervisorLaunchTests
{
    [SupportedOSPlatform("linux")]
    [Fact]
    public void Preparation_snapshots_host_tokens_and_accepts_protected_nonexecutable_dependency_data()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var deployment = new ProtectedSupervisorDeployment();
        var arguments = new List<string> { deployment.HostPath };

        ExternalProcessSupervisorLaunch launch = ExternalProcessSupervisorLaunch.Prepare(
            deployment.LauncherPath, arguments, deployment.AssemblyPath, deployment.RuntimeDirectory);
        arguments[0] = "untrusted-reselection";
        arguments.Add("untrusted-extra-argument");

        Assert.Equal(deployment.LauncherPath, launch.ProcessGroupLauncher);
        Assert.Equal([deployment.HostPath], launch.HostArguments);
        var immutableArguments = Assert.IsAssignableFrom<IList<string>>(launch.HostArguments);
        Assert.Throws<NotSupportedException>(() => immutableArguments[0] = "replacement");
        Assert.False(typeof(ExternalProcessSupervisorLaunch).GetProperty("ProcessGroupLauncher",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.CanWrite);
        launch.Revalidate();
        Assert.False(File.Exists(deployment.LauncherPath + ".launched"));
        Assert.False(File.Exists(deployment.HostPath + ".launched"));
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("launcher", UnixFileMode.GroupWrite)]
    [InlineData("launcher", UnixFileMode.OtherWrite)]
    [InlineData("host", UnixFileMode.GroupWrite)]
    [InlineData("host", UnixFileMode.OtherWrite)]
    [InlineData("assembly", UnixFileMode.GroupWrite)]
    [InlineData("runtime-config", UnixFileMode.OtherWrite)]
    [InlineData("deps", UnixFileMode.GroupWrite)]
    [InlineData("runtime-directory", UnixFileMode.OtherWrite)]
    [InlineData("runtime-file", UnixFileMode.GroupWrite)]
    public void Preparation_rejects_unsafe_selected_code_and_dependency_data_without_starting_any_process(
        string role, UnixFileMode unsafeMode)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var deployment = new ProtectedSupervisorDeployment();
        string path = role switch
        {
            "launcher" => deployment.LauncherPath,
            "host" => deployment.HostPath,
            "assembly" => deployment.AssemblyPath,
            "runtime-config" => deployment.RuntimeConfigPath,
            "deps" => deployment.DepsPath,
            "runtime-directory" => deployment.RuntimeDirectory,
            _ => deployment.RuntimeDataPath,
        };
        File.SetUnixFileMode(path, File.GetUnixFileMode(path) | unsafeMode);

        OperationFailureException failure = Assert.Throws<OperationFailureException>(() => deployment.Prepare());

        AssertUnavailable(failure, deployment.Root, path);
        Assert.False(File.Exists(deployment.LauncherPath + ".launched"));
        Assert.False(File.Exists(deployment.HostPath + ".launched"));
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("launcher", "target")]
    [InlineData("launcher", "alias-ancestor")]
    [InlineData("host", "target")]
    [InlineData("host", "alias-ancestor")]
    public void Preparation_checks_selected_executable_alias_and_target_ancestry(string role, string unsafeLocation)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var deployment = new ProtectedSupervisorDeployment();
        string target = role == "launcher" ? deployment.LauncherPath : deployment.HostPath;
        string aliases = deployment.CreateDirectory("private-aliases");
        string alias = Path.Join(aliases, role);
        File.CreateSymbolicLink(alias, target);
        string unsafePath = unsafeLocation == "target" ? target : aliases;
        File.SetUnixFileMode(unsafePath, File.GetUnixFileMode(unsafePath) | UnixFileMode.OtherWrite);

        OperationFailureException failure = Assert.Throws<OperationFailureException>(() =>
            ExternalProcessSupervisorLaunch.Prepare(
                role == "launcher" ? alias : deployment.LauncherPath,
                [role == "host" ? alias : deployment.HostPath], deployment.AssemblyPath, deployment.RuntimeDirectory));

        AssertUnavailable(failure, deployment.Root, unsafePath, alias);
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("host-fxr")]
    [InlineData("other-framework-version")]
    [InlineData("shared-store")]
    public void Dotnet_preparation_protects_hostfxr_all_rollforward_candidates_and_the_fixed_shared_store(string role)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var deployment = new ProtectedSupervisorDeployment(frameworkDependent: true);
        string unsafeFile = role switch
        {
            "host-fxr" => deployment.HostFxrPath,
            "other-framework-version" => deployment.WriteData(
                "runtime-install/shared/Microsoft.NETCore.App/10.0.1/System.Private.CoreLib.dll", "framework-data"),
            _ => deployment.WriteData("runtime-install/store/x64/net10.0/package/1.0.0/library.dll", "stored-data"),
        };
        File.SetUnixFileMode(unsafeFile, File.GetUnixFileMode(unsafeFile) | UnixFileMode.GroupWrite);

        OperationFailureException failure = Assert.Throws<OperationFailureException>(() => deployment.PrepareDotnet());

        AssertUnavailable(failure, deployment.Root, unsafeFile);
        Assert.False(File.Exists(deployment.DotnetPath + ".launched"));
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData(false, "asset")]
    [InlineData(false, "ancestor")]
    [InlineData(true, "asset")]
    [InlineData(true, "ancestor")]
    public void Runtime_configuration_and_development_configuration_protect_external_probing_trees(
        bool developmentConfig, string unsafeRole)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var deployment = new ProtectedSupervisorDeployment();
        string probe = deployment.CreateDirectory("external-probe");
        string asset = deployment.WriteData("external-probe/package/1.0.0/library.dll", "probe-data");
        string config = developmentConfig
            ? Path.ChangeExtension(deployment.AssemblyPath, ".runtimeconfig.dev.json")
            : deployment.RuntimeConfigPath;
        deployment.WriteAbsoluteData(config, JsonSerializer.Serialize(new
        {
            runtimeOptions = new { additionalProbingPaths = new[] { probe } },
        }));
        string packageDirectory = Path.Join(probe, "package");
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(packageDirectory));
        ExternalProcessSupervisorLaunch launch = deployment.Prepare();
        string unsafePath = unsafeRole == "asset" ? asset : packageDirectory;
        File.SetUnixFileMode(unsafePath, File.GetUnixFileMode(unsafePath) | UnixFileMode.OtherWrite);

        AssertUnavailable(Assert.Throws<OperationFailureException>(() => launch.Revalidate()), deployment.Root, unsafePath);
        AssertUnavailable(Assert.Throws<OperationFailureException>(() => deployment.Prepare()), deployment.Root, unsafePath);
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("relative/probe")]
    [InlineData("/probe/|arch|/|tfm|")]
    [InlineData("/probe/$HOME/packages")]
    public void Preparation_fails_closed_for_ambient_or_placeholder_probing_paths(string probe)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var deployment = new ProtectedSupervisorDeployment();
        deployment.WriteAbsoluteData(deployment.RuntimeConfigPath, JsonSerializer.Serialize(new
        {
            runtimeOptions = new { additionalProbingPaths = new[] { probe } },
        }));

        AssertUnavailable(Assert.Throws<OperationFailureException>(() => deployment.Prepare()), deployment.Root, probe);
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("runtime", "../../private-asset.dll")]
    [InlineData("native", "/private/asset.so")]
    [InlineData("resources", "../private/resource.dll")]
    [InlineData("runtimeTargets", "..\\private\\asset.dll")]
    public void Preparation_rejects_dependency_asset_paths_escaping_the_protected_search_roots(string group, string asset)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var deployment = new ProtectedSupervisorDeployment();
        deployment.WriteAbsoluteData(deployment.DepsPath, JsonSerializer.Serialize(new
        {
            targets = new Dictionary<string, object>
            {
                [".NETCoreApp,Version=v10.0"] = new Dictionary<string, object>
                {
                    ["package/1.0.0"] = new Dictionary<string, object>
                    {
                        [group] = new Dictionary<string, object> { [asset] = new { } },
                    },
                },
            },
        }));

        AssertUnavailable(Assert.Throws<OperationFailureException>(() => deployment.Prepare()), deployment.Root, asset);
    }

    [SupportedOSPlatform("linux")]
    [Fact]
    public void Preparation_rejects_external_library_framework_and_configured_startup_hook_references()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var deployment = new ProtectedSupervisorDeployment();
        deployment.WriteAbsoluteData(deployment.DepsPath,
            "{\"libraries\":{\"package/1.0.0\":{\"path\":\"../../private-package\"}}}");
        AssertUnavailable(Assert.Throws<OperationFailureException>(() => deployment.Prepare()), deployment.Root, "private-package");
        deployment.WriteAbsoluteData(deployment.DepsPath, "{}");
        deployment.WriteAbsoluteData(deployment.RuntimeConfigPath,
            "{\"runtimeOptions\":{\"framework\":{\"name\":\"../../private-framework\",\"version\":\"10.0.0\"}}}");
        AssertUnavailable(Assert.Throws<OperationFailureException>(() => deployment.Prepare()), deployment.Root, "private-framework");
        deployment.WriteAbsoluteData(deployment.RuntimeConfigPath,
            "{\"runtimeOptions\":{\"configProperties\":{\"STARTUP_HOOKS\":\"/private/startup-hook.dll\"}}}");
        AssertUnavailable(Assert.Throws<OperationFailureException>(() => deployment.Prepare()), deployment.Root, "startup-hook.dll");
        deployment.WriteAbsoluteData(deployment.RuntimeConfigPath, "{\"runtimeOptions\":{\"tfm\":\"../../private-store\"}}");
        AssertUnavailable(Assert.Throws<OperationFailureException>(() => deployment.Prepare()), deployment.Root, "private-store");
    }

    [SupportedOSPlatform("linux")]
    [Fact]
    public void Preparation_rejects_foreign_uid_managed_dependency_data_even_without_group_or_other_write()
    {
        if (!OperatingSystem.IsLinux() || GetEffectiveUserId() != 0)
        {
            return;
        }
        using var deployment = new ProtectedSupervisorDeployment();
        Assert.Equal(0, ChangeOwner(deployment.RuntimeDataPath, 65534, uint.MaxValue));

        AssertUnavailable(Assert.Throws<OperationFailureException>(() => deployment.Prepare()), deployment.Root, deployment.RuntimeDataPath);
    }

    [SupportedOSPlatform("linux")]
    [Fact]
    public void Trusted_environment_closure_removes_inherited_and_explicit_loader_overrides()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var deployment = new ProtectedSupervisorDeployment();
        ExternalProcessSupervisorLaunch launch = deployment.Prepare();
        var startInfo = new ProcessStartInfo();
        string[] overrides =
        [
            "DOTNET_STARTUP_HOOKS", "DOTNET_ADDITIONAL_DEPS", "DOTNET_SHARED_STORE", "DOTNET_ROOT_X64",
            "DOTNET_ROLL_FORWARD", "DOTNET_HOST_PATH", "DOTNET_BUNDLE_EXTRACT_BASE_DIR",
            "CORECLR_ENABLE_PROFILING", "CORECLR_PROFILER_PATH", "COMPlus_ReadyToRun",
            "COREHOST_TRACEFILE", "CORE_SERVICING", "LD_PRELOAD", "LD_LIBRARY_PATH", "LD_AUDIT", "DEVPATH",
        ];
        foreach (string name in overrides)
        {
            startInfo.Environment[name] = "private-loader-override";
        }
        startInfo.Environment["WINEPREFIX"] = "unchanged-wine-prefix";

        launch.ApplyClosedRuntimeEnvironment(startInfo);

        string architectureRoot = $"DOTNET_ROOT_{RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant()}";
        foreach (string name in overrides.Where(name => name != architectureRoot && name != "CORE_SERVICING"))
        {
            Assert.False(startInfo.Environment.ContainsKey(name));
        }
        Assert.Equal(launch.RuntimeRoot, startInfo.Environment["DOTNET_ROOT"]);
        Assert.Equal(launch.RuntimeRoot, startInfo.Environment[architectureRoot]);
        Assert.Equal("0", startInfo.Environment["DOTNET_MULTILEVEL_LOOKUP"]);
        Assert.Equal(launch.RuntimeDirectory, startInfo.Environment["CORE_SERVICING"]);
        Assert.Equal("0", startInfo.Environment["DOTNET_EnableDiagnostics"]);
        Assert.Equal("unchanged-wine-prefix", startInfo.Environment["WINEPREFIX"]);
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("launcher")]
    [InlineData("host")]
    [InlineData("assembly")]
    [InlineData("configuration")]
    [InlineData("dependencies")]
    [InlineData("runtime")]
    public void Preparation_rejects_missing_bound_prerequisites_without_using_alternate_locations(string role)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var deployment = new ProtectedSupervisorDeployment();
        string missingPath = role switch
        {
            "launcher" => deployment.LauncherPath,
            "host" => deployment.HostPath,
            "assembly" => deployment.AssemblyPath,
            "configuration" => deployment.RuntimeConfigPath,
            "dependencies" => deployment.DepsPath,
            _ => deployment.RuntimeDirectory,
        };
        if (role == "runtime")
        {
            Directory.Delete(missingPath, recursive: true);
        }
        else
        {
            File.Delete(missingPath);
        }

        AssertUnavailable(Assert.Throws<OperationFailureException>(() => deployment.Prepare()), deployment.Root, missingPath);
        Assert.False(File.Exists(deployment.LauncherPath + ".launched"));
        Assert.False(File.Exists(deployment.HostPath + ".launched"));
    }

    [SupportedOSPlatform("linux")]
    [Fact]
    public void Protected_single_string_probing_path_is_supported_and_revalidated()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var deployment = new ProtectedSupervisorDeployment();
        string probe = deployment.CreateDirectory("single-string-probe");
        string asset = deployment.WriteData("single-string-probe/package/1.0.0/library.dll", "protected-asset");
        deployment.WriteAbsoluteData(deployment.RuntimeConfigPath, JsonSerializer.Serialize(new
        {
            runtimeOptions = new { additionalProbingPaths = probe },
        }));
        ExternalProcessSupervisorLaunch launch = deployment.Prepare();
        File.SetUnixFileMode(asset, File.GetUnixFileMode(asset) | UnixFileMode.GroupWrite);

        AssertUnavailable(Assert.Throws<OperationFailureException>(() => launch.Revalidate()), deployment.Root, asset);
    }

    [SupportedOSPlatform("linux")]
    [Fact]
    public void Explicit_dotnet_configuration_uses_its_actual_dev_companion_even_without_the_runtimeconfig_suffix()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var deployment = new ProtectedSupervisorDeployment(frameworkDependent: true);
        string config = deployment.WriteData("configuration/custom.json", "{\"runtimeOptions\":{}}");
        string probe = deployment.CreateDirectory("private-dev-probe");
        string asset = deployment.WriteData("private-dev-probe/package/1.0.0/library.dll", "unsafe-probing-asset");
        deployment.WriteData("configuration/custom.dev.json", JsonSerializer.Serialize(new
        {
            runtimeOptions = new { additionalProbingPaths = new[] { probe } },
        }));
        File.SetUnixFileMode(asset, File.GetUnixFileMode(asset) | UnixFileMode.OtherWrite);

        OperationFailureException failure = Assert.Throws<OperationFailureException>(() =>
            ExternalProcessSupervisorLaunch.Prepare(deployment.LauncherPath,
                [deployment.DotnetPath, "exec", "--runtimeconfig", config, "--depsfile", deployment.DepsPath, deployment.AssemblyPath],
                deployment.AssemblyPath, deployment.RuntimeDirectory));

        AssertUnavailable(failure, deployment.Root, asset);
        Assert.False(File.Exists(deployment.DotnetPath + ".launched"));
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("configuration")]
    [InlineData("runtime")]
    public void Preparation_rejects_special_dependency_files_without_opening_them(string role)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var deployment = new ProtectedSupervisorDeployment();
        string specialPath = role == "configuration" ? deployment.RuntimeConfigPath : deployment.RuntimeDataPath;
        File.Delete(specialPath);
        Assert.Equal(0, MakeFifo(specialPath, 0x180)); // 0600: even protected permissions do not make a FIFO a dependency.

        AssertUnavailable(Assert.Throws<OperationFailureException>(() => deployment.Prepare()), deployment.Root, specialPath);
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("configuration")]
    [InlineData("dependencies")]
    public void Preparation_rejects_duplicate_loader_members_instead_of_validating_a_shadowing_value(string role)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var deployment = new ProtectedSupervisorDeployment();
        if (role == "configuration")
        {
            deployment.WriteAbsoluteData(deployment.RuntimeConfigPath,
                "{\"runtimeOptions\":{\"additionalProbingPaths\":[\"/private/first-probe\"],\"additionalProbingPaths\":[]}}");
        }
        else
        {
            deployment.WriteAbsoluteData(deployment.DepsPath,
                "{\"libraries\":{\"package/1.0.0\":{\"path\":\"../../private-first-path\",\"path\":\"package/1.0.0\"}}}");
        }

        AssertUnavailable(Assert.Throws<OperationFailureException>(() => deployment.Prepare()),
            deployment.Root, "/private/first-probe", "private-first-path");
    }

    [SupportedOSPlatform("linux")]
    [Fact]
    public void Protected_runnable_deployment_copies_the_real_application_and_entire_actual_runtime_with_private_parents()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var deployment = new ProtectedRunnableSupervisorDeployment();
        ExternalProcessSupervisorLaunch launch = deployment.Prepare();
        ExternalProcessSupervisorLaunch parentLaunch = deployment.PrepareTestParent();
        NativeDependencyClosure native = deployment.PrepareNativeClosureForTestParent(launch, launch.HostArguments[0]);
        native.ValidateInputs(launch.NativeInputs);
        native.ValidateInputs(parentLaunch.NativeInputs);
        native.ValidateExecutable(launch.ProcessGroupLauncher);
        native.ValidateExecutable(launch.HostArguments[0]);
        native.ValidateExecutable(parentLaunch.HostArguments[0]);
        Assert.DoesNotContain(launch.RuntimeRoot, launch.NativeInputs.ModuleDirectories);
        Assert.Contains(Path.Join(launch.RuntimeRoot, "host", "fxr"), launch.NativeInputs.ModuleDirectories);
        Assert.Contains(Path.Join(launch.RuntimeRoot, "shared"), launch.NativeInputs.ModuleDirectories);
        Assert.Contains(Path.ChangeExtension(deployment.AssemblyPath, ".runtimeconfig.json"),
            launch.NativeInputs.ConfigurationFiles);
        string sourceAssembly = typeof(ExternalProcessRunner).Assembly.Location;
        string sourceDirectory = Path.GetDirectoryName(sourceAssembly)!;
        string sourceTestAssembly = typeof(ProtectedSupervisorTestParent).Assembly.Location;
        string sourceRuntimeDirectory = Path.TrimEndingDirectorySeparator(RuntimeEnvironment.GetRuntimeDirectory());
        string sourceRuntimeRoot = Path.GetFullPath(Path.Combine(sourceRuntimeDirectory, "..", "..", ".."));
        using FileStream runtimeManifestStream = File.OpenRead(Path.Join(sourceRuntimeDirectory, "Microsoft.NETCore.App.deps.json"));
        using JsonDocument runtimeManifest = JsonDocument.Parse(runtimeManifestStream);
        string runtimeIdentifier = RuntimeInformation.RuntimeIdentifier;
        JsonElement runtimeFallbacks = runtimeManifest.RootElement.GetProperty("runtimes").GetProperty(runtimeIdentifier);
        string[] runtimeIdentifiers = new string[runtimeFallbacks.GetArrayLength() + 1];
        runtimeIdentifiers[0] = runtimeIdentifier;
        int fallbackIndex = 1;
        foreach (JsonElement fallback in runtimeFallbacks.EnumerateArray())
        {
            runtimeIdentifiers[fallbackIndex++] = fallback.GetString()!;
        }

        Assert.Equal(Path.Join(deployment.Root, "runtime-install"), launch.RuntimeRoot);
        Assert.Equal(Path.Join(launch.RuntimeRoot, Path.GetRelativePath(sourceRuntimeRoot, sourceRuntimeDirectory)),
            launch.RuntimeDirectory);
        string copiedTesthost = Path.Join(Path.GetDirectoryName(deployment.AssemblyPath), "testhost.dll");
        Assert.Equal(launch.RuntimeRoot, parentLaunch.RuntimeRoot);
        Assert.Equal(copiedTesthost, parentLaunch.AssemblyPath);
        Assert.Equal(
            [deployment.DotnetPath, "exec", "--runtimeconfig", Path.ChangeExtension(deployment.TestAssemblyPath, ".runtimeconfig.json"),
                "--depsfile", Path.ChangeExtension(deployment.TestAssemblyPath, ".deps.json"), copiedTesthost],
            parentLaunch.HostArguments);
        AssertCopiedImage(sourceAssembly, deployment.AssemblyPath);
        AssertCopiedImage(Path.ChangeExtension(sourceAssembly, ".runtimeconfig.json"),
            Path.ChangeExtension(deployment.AssemblyPath, ".runtimeconfig.json"));
        AssertCopiedImage(Path.ChangeExtension(sourceAssembly, ".deps.json"),
            Path.ChangeExtension(deployment.AssemblyPath, ".deps.json"));
        AssertCopiedImage(Path.Join(sourceDirectory, "testhost.dll"),
            Path.Join(Path.GetDirectoryName(deployment.AssemblyPath), "testhost.dll"));
        AssertCopiedImage(Path.Join(sourceRuntimeRoot, "dotnet"), deployment.DotnetPath);
        AssertCopiedImage(sourceTestAssembly, deployment.TestAssemblyPath);
        AssertCopiedImage(Path.ChangeExtension(sourceTestAssembly, ".runtimeconfig.json"),
            Path.ChangeExtension(deployment.TestAssemblyPath, ".runtimeconfig.json"));
        AssertCopiedImage(Path.ChangeExtension(sourceTestAssembly, ".deps.json"),
            Path.ChangeExtension(deployment.TestAssemblyPath, ".deps.json"));
        string sourceApphost = Path.ChangeExtension(sourceAssembly, null);
        Assert.Equal(Path.Join(deployment.Root, "app", Path.GetFileName(sourceApphost)), deployment.HostPath);
        AssertCopiedImage(sourceApphost, deployment.HostPath);
        AssertCopiedManifest(sourceAssembly, deployment.AssemblyPath, runtimeIdentifiers);
        AssertCopiedManifest(sourceTestAssembly, deployment.TestAssemblyPath, runtimeIdentifiers);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Join(deployment.Root, "native-home", "native-modules")));
        foreach (string relativeDirectory in new[] { Path.Join("host", "fxr"), "shared", "store" })
        {
            string sourceTree = Path.Join(sourceRuntimeRoot, relativeDirectory);
            if (relativeDirectory == "store" && !Directory.Exists(sourceTree))
            {
                continue;
            }
            foreach (string sourceFile in Directory.EnumerateFiles(sourceTree, "*", SearchOption.AllDirectories))
            {
                AssertCopiedImage(sourceFile, Path.Join(launch.RuntimeRoot, Path.GetRelativePath(sourceRuntimeRoot, sourceFile)));
            }
        }
        foreach (string directory in Directory.EnumerateDirectories(deployment.Root, "*", SearchOption.AllDirectories).Prepend(deployment.Root))
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(directory));
        }
    }

    [SupportedOSPlatform("linux")]
    [Fact]
    public void Native_inputs_freeze_all_inspected_framework_probes_configs_and_absent_store_roots()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var deployment = new ProtectedSupervisorDeployment(frameworkDependent: true);
        string probe = deployment.CreateDirectory("private-native-probe");
        string candidateRuntime = deployment.CreateDirectory("runtime-install/shared/Microsoft.NETCore.App/11.0.0");
        string config = deployment.WriteData("runtime-install/shared/Microsoft.NETCore.App/11.0.0/framework.runtimeconfig.json",
            JsonSerializer.Serialize(new { runtimeOptions = new { additionalProbingPaths = new[] { probe } } }));
        string dependencies = deployment.WriteData(
            "runtime-install/shared/Microsoft.NETCore.App/11.0.0/framework.deps.json", "{}");
        ExternalProcessSupervisorLaunch launch = deployment.PrepareDotnet();
        NativeDependencyClosureSpec inputs = launch.NativeInputs;

        Assert.Equal(new[] { deployment.LauncherPath, deployment.DotnetPath }.Order(StringComparer.Ordinal),
            inputs.ExecutablePaths);
        Assert.Contains(Path.GetDirectoryName(deployment.AssemblyPath)!, inputs.ModuleDirectories);
        Assert.DoesNotContain(launch.RuntimeRoot, inputs.ModuleDirectories);
        Assert.Contains(Path.Join(launch.RuntimeRoot, "host", "fxr"), inputs.ModuleDirectories);
        Assert.Contains(Path.Join(launch.RuntimeRoot, "shared"), inputs.ModuleDirectories);
        Assert.Contains(Path.Join(launch.RuntimeRoot, "store"), inputs.ModuleDirectories);
        Assert.Contains(candidateRuntime, inputs.ModuleDirectories);
        Assert.Contains(probe, inputs.ModuleDirectories);
        Assert.Contains(config, inputs.ConfigurationFiles);
        Assert.Contains(Path.ChangeExtension(config, ".dev.json"), inputs.ConfigurationFiles);
        Assert.Contains(dependencies, inputs.ConfigurationFiles);
        Assert.Empty(inputs.EnvironmentBindings);
        var immutableDirectories = Assert.IsAssignableFrom<IList<string>>(inputs.ModuleDirectories);
        Assert.Throws<NotSupportedException>(() => immutableDirectories[0] = "/private/rebound-root");
        launch.Revalidate();
        Assert.False(File.Exists(deployment.DotnetPath + ".launched"));
        Assert.False(File.Exists(deployment.LauncherPath + ".launched"));
    }

    [SupportedOSPlatform("linux")]
    [Fact]
    public void Native_closure_does_not_promote_unrelated_SDK_subtrees_into_recursive_module_roots()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var deployment = new ProtectedRunnableSupervisorDeployment(useApphost: false);
        string sdk = deployment.CreateDirectory("runtime-install/sdk/unrelated-version");
        string unrelatedModule = Path.Join(sdk, "libunrelated-sdk.so");
        File.WriteAllBytes(unrelatedModule,
            new NativeElfTestImage().AddStringTag(29, "$ORIGIN/../unrelated-library").Build());
        File.SetUnixFileMode(unrelatedModule, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherWrite);
        ExternalProcessSupervisorLaunch launch = deployment.Prepare();

        NativeDependencyClosure native = deployment.PrepareNativeClosure(launch, launch.HostArguments[0]);

        native.ValidateInputs(launch.NativeInputs);
        native.ValidateExecutable(launch.HostArguments[0]);
        Assert.DoesNotContain(launch.RuntimeRoot, launch.NativeInputs.ModuleDirectories);
        Assert.DoesNotContain(native.ToBinding().Objects,
            item => string.Equals(item.CanonicalPath, unrelatedModule, StringComparison.Ordinal));
    }

    private static void AssertCopiedManifest(
        string sourceAssembly, string copiedAssembly, IReadOnlyList<string> runtimeIdentifiers)
    {
        string sourceDirectory = Path.GetDirectoryName(sourceAssembly)!;
        string copiedDirectory = Path.GetDirectoryName(copiedAssembly)!;
        using FileStream manifestStream = File.OpenRead(Path.ChangeExtension(sourceAssembly, ".deps.json"));
        using JsonDocument manifest = JsonDocument.Parse(manifestStream);
        string selectedTarget = manifest.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString()!;
        foreach (JsonProperty target in manifest.RootElement.GetProperty("targets").EnumerateObject())
        {
            if (target.Name != selectedTarget)
            {
                continue;
            }
            foreach (JsonProperty library in target.Value.EnumerateObject())
            {
                foreach (JsonProperty group in library.Value.EnumerateObject())
                {
                    if (group.Name is not ("runtime" or "native" or "resources" or "runtimeTargets"))
                    {
                        continue;
                    }
                    foreach (JsonProperty asset in group.Value.EnumerateObject())
                    {
                        if (group.Name == "runtimeTargets")
                        {
                            string assetType = asset.Value.GetProperty("assetType").GetString()!;
                            string? selectedRid = null;
                            foreach (string runtimeIdentifier in runtimeIdentifiers)
                            {
                                foreach (JsonProperty candidate in group.Value.EnumerateObject())
                                {
                                    if (candidate.Value.GetProperty("assetType").GetString() == assetType &&
                                        candidate.Value.GetProperty("rid").GetString() == runtimeIdentifier)
                                    {
                                        selectedRid = runtimeIdentifier;
                                        break;
                                    }
                                }
                                if (selectedRid is not null)
                                {
                                    break;
                                }
                            }
                            if (asset.Value.GetProperty("rid").GetString() != selectedRid)
                            {
                                Assert.False(File.Exists(Path.Join(copiedDirectory, asset.Name)));
                                continue;
                            }
                        }
                        string relativePath = group.Name == "resources"
                            ? Path.Join(asset.Value.GetProperty("locale").GetString(), Path.GetFileName(asset.Name))
                            : File.Exists(Path.Join(sourceDirectory, asset.Name)) ? asset.Name : Path.GetFileName(asset.Name);
                        AssertCopiedImage(Path.Join(sourceDirectory, relativePath), Path.Join(copiedDirectory, relativePath));
                    }
                }
            }
        }
    }

    private static void AssertCopiedImage(string source, string copy)
    {
        using FileStream sourceStream = File.OpenRead(source);
        using FileStream copiedStream = File.OpenRead(copy);
        Assert.Equal(sourceStream.Length, copiedStream.Length);
        Span<byte> sourceHash = stackalloc byte[SHA256.HashSizeInBytes];
        Span<byte> copiedHash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(sourceStream, sourceHash);
        SHA256.HashData(copiedStream, copiedHash);
        Assert.True(CryptographicOperations.FixedTimeEquals(sourceHash, copiedHash));
    }

    [SupportedOSPlatform("linux")]
    private static string CreateProtectedDirectory(ProtectedDirectory directory, string relativePath)
    {
        string current = directory.Path;
        // Create one component at a time: the Unix-mode overload gives intermediate parents the
        // ambient default mode, not the requested leaf mode.
        foreach (string component in relativePath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (component != ".")
            {
                current = directory.CreateDirectory(Path.GetRelativePath(directory.Path, Path.Join(current, component)));
            }
        }
        return current;
    }

    private static void AssertUnavailable(OperationFailureException failure, params string[] privateData)
    {
        Assert.Equal(ExitCode.MissingPrerequisite, failure.Code);
        Assert.Equal("wine-unavailable", failure.Kind);
        Assert.Null(failure.InnerException);
        foreach (string value in privateData)
        {
            Assert.DoesNotContain(value, failure.ToString(), StringComparison.Ordinal);
        }
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    [DllImport("libc", EntryPoint = "chown", SetLastError = true)]
    private static extern int ChangeOwner(string path, uint user, uint group);

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeFifo(string path, uint mode);

    /// <summary>A protected data-only deployment; preparation must never execute its marker scripts.</summary>
    [SupportedOSPlatform("linux")]
    internal sealed class ProtectedSupervisorDeployment : IDisposable
    {
        private readonly ProtectedDirectory _directory = new();

        internal ProtectedSupervisorDeployment(bool frameworkDependent = false)
        {
            LauncherPath = CreateExecutable("tools/setsid");
            HostPath = CreateExecutable("app/JRunner");
            AssemblyPath = WriteData("app/JRunner.dll", "managed-image");
            RuntimeConfigPath = WriteData("app/JRunner.runtimeconfig.json", "{\"runtimeOptions\":{}}");
            DepsPath = WriteData("app/JRunner.deps.json", "{}");
            RuntimeDirectory = CreateDirectory(frameworkDependent
                ? "runtime-install/shared/Microsoft.NETCore.App/10.0.0"
                : "runtime");
            RuntimeDataPath = WriteAbsoluteData(Path.Join(RuntimeDirectory, "System.Private.CoreLib.dll"), "runtime-data");
            HostFxrPath = frameworkDependent ? WriteData("runtime-install/host/fxr/10.0.0/libhostfxr.so", "hostfxr-data") : string.Empty;
            DotnetPath = frameworkDependent ? CreateExecutable("runtime-install/dotnet") : string.Empty;
        }

        internal string Root => _directory.Path;
        internal string LauncherPath { get; }
        internal string HostPath { get; }
        internal string AssemblyPath { get; }
        internal string RuntimeConfigPath { get; }
        internal string DepsPath { get; }
        internal string RuntimeDirectory { get; }
        internal string RuntimeDataPath { get; }
        internal string HostFxrPath { get; }
        internal string DotnetPath { get; }

        internal string CreateDirectory(string relativePath) => CreateProtectedDirectory(_directory, relativePath);

        internal string WriteData(string relativePath, string contents) =>
            WriteAbsoluteData(Path.Join(Root, relativePath), contents);

        internal string WriteAbsoluteData(string path, string contents)
        {
            string directory = Path.GetDirectoryName(path)!;
            CreateDirectory(Path.GetRelativePath(Root, directory));
            File.WriteAllText(path, contents);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return path;
        }

        private string CreateExecutable(string relativePath)
        {
            CreateDirectory(Path.GetDirectoryName(relativePath)!);
            string path = _directory.CreateExecutable(relativePath);
            File.WriteAllText(path, "#!/bin/sh\nprintf launched > \"$0.launched\"\nexit 93\n");
            return path;
        }

        internal ExternalProcessSupervisorLaunch Prepare() =>
            ExternalProcessSupervisorLaunch.Prepare(LauncherPath, [HostPath], AssemblyPath, RuntimeDirectory);

        internal ExternalProcessSupervisorLaunch PrepareDotnet() => ExternalProcessSupervisorLaunch.Prepare(
            LauncherPath,
            [DotnetPath, "exec", "--runtimeconfig", RuntimeConfigPath, "--depsfile", DepsPath, AssemblyPath],
            AssemblyPath, RuntimeDirectory);

        public void Dispose() => _directory.Dispose();
    }

    /// <summary>Copies the real CLI's selected RID assets and entire actual CLR into protected directories.</summary>
    [SupportedOSPlatform("linux")]
    internal sealed class ProtectedRunnableSupervisorDeployment : IDisposable
    {
        private static readonly string[] AssetGroups = ["runtime", "native", "resources", "runtimeTargets"];
        private readonly ProtectedDirectory _directory = new();
        private readonly HashSet<string> _copiedPaths = new(StringComparer.Ordinal);
        private readonly string[] _runtimeIdentifiers;
        private readonly string _runtimeConfig;
        private readonly string _depsFile;
        private readonly string _testRuntimeConfig;
        private readonly string _testDepsFile;
        private readonly string _testhostAssembly;
        private readonly bool _usesApphost;
        private readonly IReadOnlyDictionary<string, string> _nativeEnvironment;

        internal ProtectedRunnableSupervisorDeployment(bool useApphost = true)
        {
            string sourceRuntimeDirectory = Path.TrimEndingDirectorySeparator(RuntimeEnvironment.GetRuntimeDirectory());
            string sourceRuntimeRoot = Path.GetFullPath(Path.Combine(sourceRuntimeDirectory, "..", "..", ".."));
            _runtimeIdentifiers = ReadRuntimeIdentifiers(sourceRuntimeDirectory);
            string sourceAssembly = typeof(ExternalProcessRunner).Assembly.Location;
            string sourceDirectory = Path.GetDirectoryName(sourceAssembly)!;
            AssemblyPath = CopyFile(sourceDirectory, Path.GetFileName(sourceAssembly));
            string sourceConfig = Path.ChangeExtension(sourceAssembly, ".runtimeconfig.json");
            _runtimeConfig = CopyFile(sourceDirectory, Path.GetFileName(sourceConfig));
            _depsFile = CopyFile(sourceDirectory, Path.GetFileName(Path.ChangeExtension(sourceAssembly, ".deps.json")));
            string developmentConfig = Path.ChangeExtension(sourceConfig, ".dev.json");
            if (File.Exists(developmentConfig))
            {
                CopyFile(sourceDirectory, Path.GetFileName(developmentConfig));
            }
            if (File.Exists(Path.Join(sourceDirectory, "testhost.dll")))
            {
                // Authentication compares this exact image with the running parent's deployed
                // testhost; it is evidence only, not a host or dependency-closure replacement.
                CopyFile(sourceDirectory, "testhost.dll");
            }

            // Preserve the manifests byte-for-byte, but deploy their selected RID assets just as
            // a single-platform publish does. Copying every RID also admits musl/bionic x64 modules
            // whose native dependencies are not part of the executing glibc runtime.
            CopyManifestAssets(sourceDirectory, _depsFile);

            // The actual test parent runs only copied code, including the test-only startup hook
            // and every selected managed/native dependency in its genuine test deployment manifest.
            string sourceTestAssembly = typeof(ProtectedSupervisorTestParent).Assembly.Location;
            string sourceTestDirectory = Path.GetDirectoryName(sourceTestAssembly)!;
            TestAssemblyPath = CopyFile(sourceTestDirectory, Path.GetFileName(sourceTestAssembly));
            string sourceTestConfig = Path.ChangeExtension(sourceTestAssembly, ".runtimeconfig.json");
            _testRuntimeConfig = CopyFile(sourceTestDirectory, Path.GetFileName(sourceTestConfig));
            _testDepsFile = CopyFile(sourceTestDirectory, Path.GetFileName(Path.ChangeExtension(sourceTestAssembly, ".deps.json")));
            string testDevelopmentConfig = Path.ChangeExtension(sourceTestConfig, ".dev.json");
            if (File.Exists(testDevelopmentConfig))
            {
                CopyFile(sourceTestDirectory, Path.GetFileName(testDevelopmentConfig));
            }
            _testhostAssembly = CopyFile(sourceTestDirectory, "testhost.dll");
            CopyManifestAssets(sourceTestDirectory, _testDepsFile);

            // The executing test runtime can itself live beneath a writable /tmp or SDK checkout.
            // Copy its actual installation closure, not merely the application or a different system CLR.
            CopyRuntimeTree(sourceRuntimeRoot, Path.Join("host", "fxr"));
            CopyRuntimeTree(sourceRuntimeRoot, "shared");
            if (Directory.Exists(Path.Join(sourceRuntimeRoot, "store")))
            {
                CopyRuntimeTree(sourceRuntimeRoot, "store");
            }
            DotnetPath = CopyFile(sourceRuntimeRoot, "dotnet", executable: true, deploymentDirectory: "runtime-install");
            RuntimeDirectory = Path.Join(Root, "runtime-install", Path.GetRelativePath(sourceRuntimeRoot, sourceRuntimeDirectory));
            LauncherPath = File.Exists("/usr/bin/setsid") ? "/usr/bin/setsid" : "/bin/setsid";
            string sourceHost = Path.ChangeExtension(sourceAssembly, null);
            _usesApphost = useApphost;
            if (_usesApphost && !File.Exists(sourceHost))
            {
                throw new InvalidOperationException("The runnable fixture requires the actual CLI apphost.");
            }
            HostPath = _usesApphost
                ? CopyFile(sourceDirectory, Path.GetFileName(sourceHost), executable: true)
                : DotnetPath;
            string home = CreateDirectory("native-home");
            CreateDirectory("native-home/native-modules");
            _nativeEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["HOME"] = home,
                ["PATH"] = CreateDirectory("native-path"),
                ["XDG_CONFIG_HOME"] = CreateDirectory("native-home/config"),
                ["XDG_DATA_HOME"] = CreateDirectory("native-home/data"),
                ["XDG_CACHE_HOME"] = CreateDirectory("native-home/cache"),
                ["XDG_RUNTIME_DIR"] = CreateDirectory("native-home/runtime"),
                ["XDG_CONFIG_DIRS"] = CreateDirectory("native-home/config"),
                ["XDG_DATA_DIRS"] = CreateDirectory("native-home/data"),
            };
            string opensslConfiguration = Path.Join(home, "native-openssl.cnf");
            File.WriteAllText(opensslConfiguration, string.Empty);
            File.SetUnixFileMode(opensslConfiguration, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        internal string Root => _directory.Path;
        internal string LauncherPath { get; }
        internal string HostPath { get; }
        internal string DotnetPath { get; }
        internal string AssemblyPath { get; }
        internal string TestAssemblyPath { get; }
        internal string RuntimeDirectory { get; }

        internal string CreateDirectory(string relativePath) => CreateProtectedDirectory(_directory, relativePath);

        internal ExternalProcessSupervisorLaunch Prepare(string? launcher = null, string? host = null) =>
            ExternalProcessSupervisorLaunch.Prepare(
                launcher ?? LauncherPath,
                _usesApphost ? [host ?? HostPath]
                    : [host ?? HostPath, "exec", "--runtimeconfig", _runtimeConfig, "--depsfile", _depsFile, AssemblyPath],
                AssemblyPath, RuntimeDirectory);

        internal ExternalProcessSupervisorLaunch PrepareTestParent() =>
            ExternalProcessSupervisorLaunch.Prepare(LauncherPath,
                [DotnetPath, "exec", "--runtimeconfig", _testRuntimeConfig, "--depsfile", _testDepsFile, _testhostAssembly],
                _testhostAssembly, RuntimeDirectory);

        internal string CopyProtectedExecutable(string sourcePath, string relativePath)
        {
            CreateDirectory(Path.GetDirectoryName(relativePath)!);
            string destination = Path.Join(Root, relativePath);
            File.Copy(sourcePath, destination);
            File.SetUnixFileMode(destination,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return destination;
        }

        /// <summary>
        /// Prepares one native closure for both the runnable launch and its test parent.
        /// </summary>
        internal NativeDependencyClosure PrepareNativeClosureForTestParent(
            ExternalProcessSupervisorLaunch launch,
            string executablePath)
        {
            ExternalProcessSupervisorLaunch parentLaunch = PrepareTestParent();
            NativeDependencyClosureSpec parentInputs = parentLaunch.NativeInputs;
            return PrepareNativeClosure(launch, executablePath, parentInputs.ExecutablePaths,
                parentInputs.ModuleDirectories, parentInputs.ConfigurationFiles);
        }

        internal NativeDependencyClosure PrepareNativeClosure(
            ExternalProcessSupervisorLaunch launch,
            string executablePath,
            IEnumerable<string>? additionalExecutables = null,
            IEnumerable<string>? additionalModuleDirectories = null,
            IEnumerable<string>? additionalConfigurationFiles = null)
        {
            NativeDependencyClosureSpec inputs = launch.NativeInputs;
            return NativeDependencyClosure.Prepare(new NativeDependencyClosureSpec(
                inputs.ExecutablePaths.Append(executablePath).Concat(additionalExecutables ?? [])
                    .Distinct(StringComparer.Ordinal).ToArray(),
                inputs.ModuleDirectories.Concat(additionalModuleDirectories ?? [])
                    .Distinct(StringComparer.Ordinal).ToArray(),
                inputs.ConfigurationFiles.Concat(additionalConfigurationFiles ?? [])
                    .Distinct(StringComparer.Ordinal).ToArray(),
                _nativeEnvironment));
        }

        private static string[] ReadRuntimeIdentifiers(string runtimeDirectory)
        {
            using FileStream manifestStream = File.OpenRead(Path.Join(runtimeDirectory, "Microsoft.NETCore.App.deps.json"));
            using JsonDocument manifest = JsonDocument.Parse(manifestStream);
            string runtimeIdentifier = RuntimeInformation.RuntimeIdentifier;
            JsonElement fallbacks = manifest.RootElement.GetProperty("runtimes").GetProperty(runtimeIdentifier);
            string[] identifiers = new string[fallbacks.GetArrayLength() + 1];
            identifiers[0] = runtimeIdentifier;
            int index = 1;
            foreach (JsonElement fallback in fallbacks.EnumerateArray())
            {
                identifiers[index++] = fallback.GetString()!;
            }
            return identifiers;
        }

        private bool IsSelectedRuntimeTarget(JsonElement assets, JsonElement asset)
        {
            string assetType = asset.GetProperty("assetType").GetString()!;
            foreach (string runtimeIdentifier in _runtimeIdentifiers)
            {
                foreach (JsonProperty candidate in assets.EnumerateObject())
                {
                    if (candidate.Value.GetProperty("assetType").GetString() == assetType &&
                        candidate.Value.GetProperty("rid").GetString() == runtimeIdentifier)
                    {
                        return asset.GetProperty("rid").GetString() == runtimeIdentifier;
                    }
                }
            }
            return false;
        }

        private void CopyManifestAssets(string sourceDirectory, string depsFile)
        {
            using FileStream dependenciesStream = File.OpenRead(depsFile);
            using JsonDocument dependencies = JsonDocument.Parse(dependenciesStream);
            string selectedTarget = dependencies.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString()!;
            foreach (JsonProperty target in dependencies.RootElement.GetProperty("targets").EnumerateObject())
            {
                if (target.Name != selectedTarget)
                {
                    continue;
                }
                foreach (JsonProperty library in target.Value.EnumerateObject())
                {
                    foreach (string group in AssetGroups)
                    {
                        if (!library.Value.TryGetProperty(group, out JsonElement assets))
                        {
                            continue;
                        }
                        foreach (JsonProperty asset in assets.EnumerateObject())
                        {
                            if (group == "runtimeTargets" && !IsSelectedRuntimeTarget(assets, asset.Value))
                            {
                                continue;
                            }
                            string relativePath = group == "resources"
                                ? Path.Join(asset.Value.GetProperty("locale").GetString(), Path.GetFileName(asset.Name))
                                : File.Exists(Path.Join(sourceDirectory, asset.Name)) ? asset.Name : Path.GetFileName(asset.Name);
                            CopyFile(sourceDirectory, relativePath);
                        }
                    }
                }
            }
        }

        private void CopyRuntimeTree(string sourceRuntimeRoot, string relativeDirectory)
        {
            string sourceDirectory = Path.Join(sourceRuntimeRoot, relativeDirectory);
            CreateDirectory(Path.Join("runtime-install", relativeDirectory));
            const UnixFileMode executeBits = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            foreach (string entry in Directory.EnumerateFileSystemEntries(sourceDirectory))
            {
                string relativePath = Path.GetRelativePath(sourceRuntimeRoot, entry);
                if (Directory.Exists(entry))
                {
                    CopyRuntimeTree(sourceRuntimeRoot, relativePath);
                }
                else
                {
                    CopyFile(sourceRuntimeRoot, relativePath,
                        executable: (File.GetUnixFileMode(entry) & executeBits) != 0, deploymentDirectory: "runtime-install");
                }
            }
        }

        private string CopyFile(
            string sourceDirectory, string relativePath, bool executable = false, string deploymentDirectory = "app")
        {
            string relativeDestination = Path.Join(deploymentDirectory, relativePath);
            string destination = Path.Join(Root, relativeDestination);
            if (_copiedPaths.Add(relativeDestination))
            {
                CreateDirectory(Path.GetRelativePath(Root, Path.GetDirectoryName(destination)!));
                File.Copy(Path.Join(sourceDirectory, relativePath), destination);
                File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite
                    | (executable ? UnixFileMode.UserExecute : 0));
            }
            return destination;
        }

        public void Dispose() => _directory.Dispose();
    }
}
