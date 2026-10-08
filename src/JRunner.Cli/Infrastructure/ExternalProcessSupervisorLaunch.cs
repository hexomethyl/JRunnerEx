using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using JRunner.Core.Contracts;

namespace JRunner.Cli.Infrastructure;

/// <summary>
/// Binds the selected Linux launcher and managed supervisor host before private Wine data is staged.
/// Selection never occurs again for a marked invocation; protection is checked again before each launch.
/// </summary>
internal sealed class ExternalProcessSupervisorLaunch
{
    private readonly string _runtimeConfig;
    private readonly string _depsFile;
    private readonly bool _usesDotnetHost;
    private NativeDependencyClosureSpec? _nativeInputs;
    private static readonly string[] FrameworkArrays = ["frameworks", "includedFrameworks"];
    private static readonly string[] AssetGroups = ["runtime", "native", "resources", "runtimeTargets"];
    private static readonly string ArchitectureRuntimeRootVariable =
        $"DOTNET_ROOT_{RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant()}";
    // Native and managed JSON readers need not choose the same duplicate member. Never validate
    // a shadowing value while the native host consumes an earlier loader/search-path value.
    private static readonly JsonDocumentOptions ManifestOptions = new() { AllowDuplicateProperties = false };

    private ExternalProcessSupervisorLaunch(
        string processGroupLauncher,
        string[] hostArguments,
        string assemblyPath,
        string runtimeDirectory,
        string runtimeRoot,
        string hostTargetPath,
        string runtimeConfig,
        string depsFile,
        bool usesDotnetHost)
    {
        ProcessGroupLauncher = processGroupLauncher;
        HostArguments = new ReadOnlyCollection<string>(hostArguments);
        AssemblyPath = assemblyPath;
        RuntimeDirectory = runtimeDirectory;
        RuntimeRoot = runtimeRoot;
        HostTargetPath = hostTargetPath;
        _runtimeConfig = runtimeConfig;
        _depsFile = depsFile;
        _usesDotnetHost = usesDotnetHost;
    }

    internal string ProcessGroupLauncher { get; }

    /// <summary>Gets frozen host tokens, with the protected executable alias first.</summary>
    internal IReadOnlyList<string> HostArguments { get; }

    internal string AssemblyPath { get; }
    internal string RuntimeDirectory { get; }
    internal string RuntimeRoot { get; }
    internal string HostTargetPath { get; }

    /// <summary>
    /// Gets the frozen executable, deployment, runtime, and manifest search inputs already inspected
    /// by this launch plan. Wine combines these with its own inputs before native closure preparation.
    /// </summary>
    internal NativeDependencyClosureSpec NativeInputs => _nativeInputs ?? throw Unavailable();

    /// <summary>
    /// Validates an already selected chain without executing it. This also supplies deterministic
    /// filesystem-only coverage for deployment/configuration failures; production selection is in the runner.
    /// </summary>
    internal static ExternalProcessSupervisorLaunch Prepare(
        string processGroupLauncher,
        IReadOnlyList<string> hostArguments,
        string assemblyPath,
        string runtimeDirectory)
    {
        try
        {
            WineXeBuildToolchainResolver.ValidateProtectedExecutable(processGroupLauncher);
            if (hostArguments is null || hostArguments.Count == 0)
            {
                throw Unavailable();
            }
            string[] hostTokens = hostArguments.ToArray();
            hostArguments = hostTokens;

            WineXeBuildToolchainResolver.ValidateProtectedExecutable(hostArguments[0]);
            WineXeBuildToolchainResolver.ValidateProtectedFile(assemblyPath);
            bool usesDotnetHost = hostArguments.Count != 1;
            string runtimeConfig;
            string depsFile;
            string runtimeRoot;
            string hostTargetPath = WineXeBuildToolchainResolver.GetProtectedCanonicalPath(hostArguments[0], directory: false);
            if (usesDotnetHost)
            {
                if (hostArguments.Count != 7
                    || hostArguments[1] != "exec"
                    || hostArguments[2] != "--runtimeconfig"
                    || hostArguments[4] != "--depsfile"
                    || !string.Equals(hostArguments[6], assemblyPath, StringComparison.Ordinal))
                {
                    throw Unavailable();
                }

                runtimeConfig = hostArguments[3];
                depsFile = hostArguments[5];
                runtimeRoot = Path.GetDirectoryName(hostTargetPath) ?? throw Unavailable();
            }
            else
            {
                runtimeConfig = Path.ChangeExtension(assemblyPath, ".runtimeconfig.json");
                depsFile = Path.ChangeExtension(assemblyPath, ".deps.json");
                var runtime = new DirectoryInfo(runtimeDirectory);
                runtimeRoot = runtime.Parent?.Parent is DirectoryInfo shared
                    && string.Equals(shared.Name, "shared", StringComparison.Ordinal)
                        ? shared.Parent?.FullName ?? throw Unavailable()
                        : runtime.FullName; // A self-contained deployment keeps the CLR alongside its apphost.
            }

            var launch = new ExternalProcessSupervisorLaunch(
                processGroupLauncher, hostTokens, assemblyPath, runtimeDirectory, runtimeRoot, hostTargetPath,
                runtimeConfig, depsFile, usesDotnetHost);
            launch.Revalidate();
            return launch;
        }
        catch (Exception)
        {
            // Do not retain a path-rich filesystem, JSON, or native exception as an inner exception.
            throw Unavailable();
        }
    }

    internal void Revalidate()
    {
        try
        {
            WineXeBuildToolchainResolver.ValidateProtectedExecutable(ProcessGroupLauncher);
            WineXeBuildToolchainResolver.ValidateProtectedExecutable(HostArguments[0]);
            string currentHostTarget = WineXeBuildToolchainResolver.GetProtectedCanonicalPath(HostArguments[0], directory: false);
            if (!string.Equals(currentHostTarget, HostTargetPath, StringComparison.Ordinal))
            {
                throw Unavailable();
            }
            var protectedTrees = new HashSet<string>(StringComparer.Ordinal);
            var inspectedConfigurations = new HashSet<string>(StringComparer.Ordinal);
            string assemblyTarget = ProtectDeploymentFile(AssemblyPath, protectedTrees);
            if (!_usesDotnetHost)
            {
                // An apphost can prefer an app-relative runtime, so protect its complete deployment too.
                ProtectTree(Path.GetDirectoryName(HostTargetPath)!, protectedTrees);
            }

            InspectRuntimeConfiguration(_runtimeConfig, required: true, protectedTrees, inspectedConfigurations);
            InspectDependencyManifest(_depsFile, protectedTrees, inspectedConfigurations);
            if (!_usesDotnetHost && !string.Equals(assemblyTarget, AssemblyPath, StringComparison.Ordinal))
            {
                InspectRuntimeConfiguration(Path.ChangeExtension(assemblyTarget, ".runtimeconfig.json"),
                    required: true, protectedTrees, inspectedConfigurations);
                InspectDependencyManifest(Path.ChangeExtension(assemblyTarget, ".deps.json"),
                    protectedTrees, inspectedConfigurations);
            }
            string sharedRoot = Path.Join(RuntimeRoot, "shared");
            string hostFxrRoot = Path.Join(RuntimeRoot, "host", "fxr");
            if (_usesDotnetHost || !string.Equals(RuntimeRoot, Path.TrimEndingDirectorySeparator(RuntimeDirectory), StringComparison.Ordinal))
            {
                ProtectTree(hostFxrRoot, protectedTrees);
                ProtectTree(sharedRoot, protectedTrees);
                // Framework roll-forward stays inside this bound installation. Inspect configurations
                // for every candidate version, not the caller's unrelated TPA/testhost/NuGet closure.
                foreach (string framework in Directory.EnumerateDirectories(sharedRoot))
                {
                    foreach (string version in Directory.EnumerateDirectories(framework))
                    {
                        InspectRuntimeDirectory(version, protectedTrees, inspectedConfigurations);
                    }
                }
            }

            ProtectTree(RuntimeDirectory, protectedTrees);
            InspectRuntimeDirectory(RuntimeDirectory, protectedTrees, inspectedConfigurations);
            // The installed package store is a candidate for the host policy. Environment-supplied
            // stores and servicing paths are removed, but this fixed root and its absence stay bound.
            string store = Path.Join(RuntimeRoot, "store");
            if (EntryExists(store))
            {
                ProtectTree(store, protectedTrees);
            }
            // Absence is also loader state: a later store cannot become a fresh trusted root.
            protectedTrees.Add(store);
            // The installation root is protected by the host and these search paths' ancestry.
            // Do not recursively admit unrelated SDK, packs, or tooling subtrees as native modules.
            if (_nativeInputs is null)
            {
                _nativeInputs = new NativeDependencyClosureSpec(
                    [ProcessGroupLauncher, HostArguments[0]],
                    protectedTrees.ToArray(),
                    inspectedConfigurations.ToArray(),
                    new Dictionary<string, string>(StringComparer.Ordinal));
            }
            else if (!protectedTrees.SetEquals(_nativeInputs.ModuleDirectories)
                || !inspectedConfigurations.SetEquals(_nativeInputs.ConfigurationFiles))
            {
                throw Unavailable();
            }
        }
        catch (Exception)
        {
            throw Unavailable();
        }
    }

    internal void ApplyClosedRuntimeEnvironment(ProcessStartInfo startInfo)
    {
        // Apply after invocation updates, in both launcher processes: no caller override may restore
        // loader hooks, profiling, native preloads, runtime roots, roll-forward, or extra dependency data.
        foreach (string name in startInfo.Environment.Keys.ToArray())
        {
            if (name.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("COREHOST_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("LD_", StringComparison.Ordinal)
                || string.Equals(name, "CORE_SERVICING", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "DEVPATH", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.Environment.Remove(name);
            }
        }

        startInfo.Environment["DOTNET_ROOT"] = RuntimeRoot;
        startInfo.Environment[ArchitectureRuntimeRootVariable] = RuntimeRoot;
        startInfo.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
        startInfo.Environment["DOTNET_EnableDiagnostics"] = "0";
        // On Linux an absent CORE_SERVICING falls back to /opt/coreservicing. An existing, fully
        // protected bound directory closes that fallback and all of its native-image/package probes.
        startInfo.Environment["CORE_SERVICING"] = RuntimeDirectory;
    }

    private static void ProtectTree(string directory, HashSet<string> protectedTrees)
    {
        directory = Path.TrimEndingDirectorySeparator(directory);
        // A previously protected tree includes every alias/target descendant. Do not rescan a whole
        // deployment for each manifest, but never use lexical coverage across a kernel-resolved '..'.
        if (!directory.Contains("/../", StringComparison.Ordinal) && !directory.EndsWith("/..", StringComparison.Ordinal))
        {
            foreach (string protectedTree in protectedTrees)
            {
                if (directory.StartsWith(protectedTree, StringComparison.Ordinal)
                    && (directory.Length == protectedTree.Length || directory[protectedTree.Length] == '/'))
                {
                    protectedTrees.Add(directory);
                    return;
                }
            }
        }
        if (protectedTrees.Add(directory))
        {
            WineXeBuildToolchainResolver.ValidateProtectedTree(directory);
        }
    }

    private static string ProtectDeploymentFile(string path, HashSet<string> protectedTrees)
    {
        WineXeBuildToolchainResolver.ValidateProtectedFile(path);
        ProtectTree(Path.GetDirectoryName(path)!, protectedTrees);
        string target = WineXeBuildToolchainResolver.GetProtectedCanonicalPath(path, directory: false);
        ProtectTree(Path.GetDirectoryName(target)!, protectedTrees);
        return target;
    }

    private static void InspectRuntimeDirectory(
        string directory,
        HashSet<string> protectedTrees,
        HashSet<string> inspectedConfigurations)
    {
        // Only top-level manifests are consumed by the host. The trust walker has already protected
        // symlink targets and handled directory cycles; do not recursively follow links a second time.
        foreach (string path in Directory.EnumerateFiles(directory, "*.runtimeconfig.json"))
        {
            InspectRuntimeConfiguration(path, required: true, protectedTrees, inspectedConfigurations);
        }

        foreach (string path in Directory.EnumerateFiles(directory, "*.deps.json"))
        {
            InspectDependencyManifest(path, protectedTrees, inspectedConfigurations);
        }
    }

    private static void InspectRuntimeConfiguration(
        string path,
        bool required,
        HashSet<string> protectedTrees,
        HashSet<string> inspectedConfigurations,
        bool isDevelopmentConfig = false)
    {
        if (!inspectedConfigurations.Add(path) || (!required && !EntryExists(path)))
        {
            return;
        }

        string configurationTarget = ProtectDeploymentFile(path, protectedTrees);
        // The host canonicalizes an explicit config and replaces its extension with .json before
        // reading it. Reject ambiguous extension aliases instead of protecting different bytes.
        if (!path.EndsWith(".json", StringComparison.Ordinal)
            || !configurationTarget.EndsWith(".json", StringComparison.Ordinal))
        {
            throw Unavailable();
        }
        using FileStream stream = WineXeBuildToolchainResolver.OpenProtectedRead(path);
        using JsonDocument configuration = JsonDocument.Parse(stream, ManifestOptions);
        if (!configuration.RootElement.TryGetProperty("runtimeOptions", out JsonElement options))
        {
            throw Unavailable();
        }

        if (options.TryGetProperty("additionalProbingPaths", out JsonElement probingPaths))
        {
            if (probingPaths.ValueKind == JsonValueKind.String)
            {
                ProtectProbingPath(probingPaths.GetString() ?? throw Unavailable(), protectedTrees);
            }
            else
            {
                foreach (JsonElement probe in probingPaths.EnumerateArray())
                {
                    ProtectProbingPath(probe.GetString() ?? throw Unavailable(), protectedTrees);
                }
            }
        }

        if (options.TryGetProperty("tfm", out JsonElement targetFramework))
        {
            // The shared-store probe appends this value to its root.
            ValidateRelativeAsset(targetFramework.GetString() ?? throw Unavailable(), singleComponent: true);
        }

        if (options.TryGetProperty("framework", out JsonElement framework))
        {
            ValidateFrameworkReference(framework);
        }
        foreach (string property in FrameworkArrays)
        {
            if (options.TryGetProperty(property, out JsonElement frameworks))
            {
                foreach (JsonElement item in frameworks.EnumerateArray())
                {
                    ValidateFrameworkReference(item);
                }
            }
        }

        if (options.TryGetProperty("configProperties", out JsonElement properties))
        {
            foreach (JsonProperty property in properties.EnumerateObject())
            {
                if (property.Name is "STARTUP_HOOKS" or "APP_CONTEXT_DEPS_FILES" or "TRUSTED_PLATFORM_ASSEMBLIES"
                    or "NATIVE_DLL_SEARCH_DIRECTORIES" or "APP_PATHS" or "PROBING_DIRECTORIES"
                    or "APP_CONTEXT_BASE_DIRECTORY" or "PLATFORM_RESOURCE_ROOTS")
                {
                    throw Unavailable();
                }
            }
        }

        if (!isDevelopmentConfig)
        {
            string developmentConfig = Path.ChangeExtension(path, ".dev.json");
            InspectRuntimeConfiguration(developmentConfig, required: false, protectedTrees, inspectedConfigurations,
                isDevelopmentConfig: true);
        }
        if (!string.Equals(configurationTarget, path, StringComparison.Ordinal))
        {
            InspectRuntimeConfiguration(configurationTarget, required: true, protectedTrees, inspectedConfigurations,
                isDevelopmentConfig);
        }
    }

    private static void ProtectProbingPath(string path, HashSet<string> protectedTrees)
    {
        // Host placeholders and relative paths depend on ambient cwd/host state. Do not guess
        // their expansion or silently ignore a search root which can leave this deployment.
        if (!Path.IsPathFullyQualified(path) || path.Contains('|') || path.Contains('$'))
        {
            throw Unavailable();
        }
        ProtectTree(path, protectedTrees);
    }

    private static void ValidateFrameworkReference(JsonElement framework)
    {
        ValidateRelativeAsset(framework.GetProperty("name").GetString() ?? throw Unavailable(), singleComponent: true);
        ValidateRelativeAsset(framework.GetProperty("version").GetString() ?? throw Unavailable(), singleComponent: true);
    }

    private static void InspectDependencyManifest(
        string path, HashSet<string> protectedTrees, HashSet<string> inspectedConfigurations)
    {
        if (!inspectedConfigurations.Add(path))
        {
            return;
        }
        ProtectDeploymentFile(path, protectedTrees);
        using FileStream stream = WineXeBuildToolchainResolver.OpenProtectedRead(path);
        using JsonDocument dependencies = JsonDocument.Parse(stream, ManifestOptions);
        if (dependencies.RootElement.TryGetProperty("libraries", out JsonElement libraries))
        {
            foreach (JsonProperty library in libraries.EnumerateObject())
            {
                ValidateRelativeAsset(library.Name, singleComponent: false);
                if (library.Value.TryGetProperty("path", out JsonElement libraryPath))
                {
                    ValidateRelativeAsset(libraryPath.GetString() ?? throw Unavailable(), singleComponent: false);
                }
            }
        }

        if (dependencies.RootElement.TryGetProperty("targets", out JsonElement targets))
        {
            foreach (JsonProperty target in targets.EnumerateObject())
            {
                foreach (JsonProperty library in target.Value.EnumerateObject())
                {
                    ValidateRelativeAsset(library.Name, singleComponent: false);
                    foreach (string property in AssetGroups)
                    {
                        if (library.Value.TryGetProperty(property, out JsonElement assets))
                        {
                            foreach (JsonProperty asset in assets.EnumerateObject())
                            {
                                ValidateRelativeAsset(asset.Name, singleComponent: false);
                            }
                        }
                    }
                }
            }
        }
    }

    private static void ValidateRelativeAsset(string asset, bool singleComponent)
    {
        if (string.IsNullOrEmpty(asset) || Path.IsPathRooted(asset) || asset.Contains('\\')
            || asset.Contains(':') || asset.Contains('\0') || (singleComponent && asset.Contains('/')))
        {
            throw Unavailable();
        }
        ReadOnlySpan<char> path = asset.AsSpan();
        foreach (Range range in path.Split('/'))
        {
            ReadOnlySpan<char> component = path[range];
            if (component.IsEmpty || component.SequenceEqual(".") || component.SequenceEqual(".."))
            {
                throw Unavailable();
            }
        }
    }

    private static bool EntryExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    internal static OperationFailureException Unavailable() => new(
        ExitCode.MissingPrerequisite,
        "wine-unavailable",
        "Wine is required to run XeBuild but is not available from a trusted executable location. Install Wine and winepath.");
}
