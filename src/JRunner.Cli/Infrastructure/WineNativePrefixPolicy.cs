using System.Globalization;
using System.Text;
using JRunner.Core.Contracts;

namespace JRunner.Cli.Infrastructure;

// Only the backend's freshly created managed prefix is supported. Vendor Wine creates its
// registry/code state in this private tree under the already closed native/environment graph.
internal sealed class WineNativePrefixPolicy
{
    internal const string HeadlessDllOverrides =
        "winemenubuilder.exe,winex11.drv,winewayland.drv,winemac.drv,wineandroid.drv," +
        "winealsa.drv,winepulse.drv,wineoss.drv,winecoreaudio.drv,mmdevapi,opencl,opengl32," +
        "vulkan-1,winevulkan,dxgi,d3d8,d3d9,d3d10,d3d10_1,d3d11,d3d12=";
    private const string PasswordFile = "/etc/passwd";
    private const int MaximumPasswordBytes = 1024 * 1024;
    private const int MaximumPasswordLineCharacters = 8192;
    private const int MaximumDirectoryBindings = 64;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] RegistryNames = ["system.reg", "user.reg", "userdef.reg"];
    private static readonly string[] ModuleNames = ["drive_c", "dosdevices/c:"];
    private readonly string _prefix;
    private readonly string _workspace;
    private readonly string _userName;
    private readonly string[] _startupDirectories;
    private readonly WineNativeDirectoryBinding[] _directories;
    private readonly WineNativeMappingBinding[] _mappings;
    private readonly WineNativeRegistryAliasBinding[] _registryAliases;

    private WineNativePrefixPolicy(string prefix, string workspace, string userName, bool needsInitialization,
        WineNativeDirectoryBinding[] directories, WineNativeMappingBinding[] mappings,
        WineNativeRegistryAliasBinding[] registryAliases)
    {
        _prefix = prefix;
        _workspace = workspace;
        _userName = userName;
        NeedsInitialization = needsInitialization;
        _startupDirectories = GetStartupDirectories(prefix, userName);
        _directories = directories;
        _mappings = mappings;
        _registryAliases = registryAliases;
        ModuleDirectories = Array.AsReadOnly(ModuleNames.Select(name => Path.Join(prefix, name))
            .Append(Path.Join(workspace, "xeBuild")).Order(StringComparer.Ordinal).ToArray());
        ConfigurationFiles = Array.AsReadOnly(new[] { PasswordFile });
    }

    internal bool NeedsInitialization { get; }
    internal IReadOnlyList<string> ModuleDirectories { get; }
    internal IReadOnlyList<string> ConfigurationFiles { get; }
    internal string PrefixDirectory => _prefix;
    internal string WorkspaceDirectory => _workspace;
    internal string UserName => _userName;

    internal static WineNativePrefixPolicy PrepareFresh(string prefix, string workspace)
    {
        try
        {
            RequireManagedLocation(prefix, workspace);
            CapturePrivateDirectory(workspace);
            CapturePrivateDirectory(prefix);
            // This check precedes even the version invocation. A later empty-directory check
            // cannot certify an arbitrary tree which Wine has already consumed.
            if (Directory.EnumerateFileSystemEntries(prefix).Any())
            {
                throw Unsupported();
            }
            string userName = ReadProtectedUserName();
            string[] directories = GetPreparedDirectories(prefix, workspace, userName);
            foreach (string directory in directories)
            {
                CreatePrivateDirectory(directory);
            }
            File.CreateSymbolicLink(Path.Join(prefix, "dosdevices", "c:"), "../drive_c");
            File.CreateSymbolicLink(Path.Join(prefix, "dosdevices", "z:"), "/");
            var policy = new WineNativePrefixPolicy(prefix, workspace, userName, needsInitialization: true,
                directories.Select(CapturePrivateDirectory).ToArray(), CaptureMappings(prefix), []);
            policy.Revalidate();
            return policy;
        }
        catch (Exception)
        {
            throw Unsupported();
        }
    }

    internal static WineNativePrefixPolicy PrepareAfterInitialization(WineNativePrefixPolicy initial)
    {
        try
        {
            if (initial is null || !initial.NeedsInitialization)
            {
                throw Unsupported();
            }
            // Preserve the pre-execution private roots and empty startup proof. Only the fixed
            // trusted initialization operation may populate the remaining managed code tree.
            initial.RevalidateDirectoriesAndMappings();
            var policy = new WineNativePrefixPolicy(initial._prefix, initial._workspace, initial._userName,
                needsInitialization: false, initial._directories.ToArray(), initial._mappings.ToArray(),
                RegistryNames.Select(name => CaptureRegistryAlias(Path.Join(initial._prefix, name))).ToArray());
            policy.Revalidate();
            return policy;
        }
        catch (Exception)
        {
            throw Unsupported();
        }
    }

    internal void Revalidate()
    {
        try
        {
            RevalidateDirectoriesAndMappings();
            if (NeedsInitialization)
            {
                // The version/availability phases may not silently initialize or plant state.
                foreach (string name in RegistryNames)
                {
                    if (WineXeBuildToolchainResolver.TryGetProtectedNativeIdentity(
                        Path.Join(_prefix, name), directory: false, out _, out _))
                    {
                        throw Unsupported();
                    }
                }
                ValidatePreparedTree();
            }
            else
            {
                foreach (WineNativeRegistryAliasBinding registry in _registryAliases)
                {
                    if (CaptureRegistryAlias(registry.AliasPath) != registry)
                    {
                        throw Unsupported();
                    }
                }
            }
        }
        catch (Exception)
        {
            throw Unsupported();
        }
    }

    private void RevalidateDirectoriesAndMappings()
    {
        foreach (WineNativeDirectoryBinding directory in _directories)
        {
            if (CapturePrivateDirectory(directory.AliasPath) != directory)
            {
                throw Unsupported();
            }
        }
        if (!_mappings.SequenceEqual(CaptureMappings(_prefix)))
        {
            throw Unsupported();
        }
        foreach (string directory in _startupDirectories)
        {
            WineXeBuildToolchainResolver.GetProtectedNativeIdentity(directory, directory: true);
            if (Directory.EnumerateFileSystemEntries(directory).Any())
            {
                throw Unsupported(); // ShellExecute runs every startup non-folder, including hidden links.
            }
        }
    }

    private void ValidatePreparedTree()
    {
        var allowed = new HashSet<string>(_directories.Select(directory => directory.AliasPath), StringComparer.Ordinal)
        {
            Path.Join(_prefix, "dosdevices", "c:"),
            Path.Join(_prefix, "dosdevices", "z:"),
        };
        var pending = new Stack<string>();
        pending.Push(_prefix);
        while (pending.TryPop(out string? directory))
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (!allowed.Contains(entry))
                {
                    throw Unsupported();
                }
                if (_directories.Any(binding => binding.AliasPath == entry))
                {
                    pending.Push(entry);
                }
            }
        }
    }

    internal WineNativePrefixPolicyBinding ToBinding() => new(
        _prefix, _workspace, NeedsInitialization, ModuleDirectories.ToArray(), _userName,
        _mappings.ToArray(), _registryAliases.ToArray(), _directories.ToArray());

    internal static WineNativePrefixPolicy FromBinding(WineNativePrefixPolicyBinding binding)
    {
        try
        {
            if (binding is null || !IsSafeUserName(binding.UserName) || binding.Directories is null ||
                binding.Directories.Count is 0 or > MaximumDirectoryBindings || binding.Mappings is null ||
                binding.Mappings.Count != 2 || binding.RegistryAliases is null ||
                binding.RegistryAliases.Count != (binding.NeedsInitialization ? 0 : RegistryNames.Length) ||
                binding.ModuleDirectories is null)
            {
                throw Unsupported();
            }
            RequireManagedLocation(binding.Prefix, binding.Workspace);
            string[] expectedDirectories = GetPreparedDirectories(binding.Prefix, binding.Workspace, binding.UserName);
            if (!binding.Directories.Select(directory => directory.AliasPath).SequenceEqual(expectedDirectories, StringComparer.Ordinal))
            {
                throw Unsupported();
            }
            var policy = new WineNativePrefixPolicy(binding.Prefix, binding.Workspace, binding.UserName,
                binding.NeedsInitialization, binding.Directories.ToArray(), binding.Mappings.ToArray(), binding.RegistryAliases.ToArray());
            if (!policy.ModuleDirectories.SequenceEqual(binding.ModuleDirectories, StringComparer.Ordinal) ||
                !binding.RegistryAliases.Select(registry => registry.AliasPath).SequenceEqual(
                    binding.NeedsInitialization ? [] : RegistryNames.Select(name => Path.Join(binding.Prefix, name)), StringComparer.Ordinal))
            {
                throw Unsupported();
            }
            policy.Revalidate();
            return policy;
        }
        catch (Exception)
        {
            throw Unsupported();
        }
    }

    private static void RequireManagedLocation(string prefix, string workspace)
    {
        if (string.IsNullOrEmpty(prefix) || string.IsNullOrEmpty(workspace) ||
            !Path.IsPathFullyQualified(prefix) || !Path.IsPathFullyQualified(workspace) ||
            prefix.IndexOf('\0') >= 0 || workspace.IndexOf('\0') >= 0 ||
            Path.GetFullPath(prefix) != prefix || Path.GetFullPath(workspace) != workspace ||
            prefix != Path.Join(workspace, "wine-prefix"))
        {
            throw Unsupported();
        }
    }

    private static string[] GetStartupDirectories(string prefix, string userName) =>
    [
        Path.Join(prefix, "drive_c", "users", userName, "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs", "StartUp"),
        Path.Join(prefix, "drive_c", "ProgramData", "Microsoft", "Windows", "Start Menu", "Programs", "StartUp"),
    ];

    private static string[] GetPreparedDirectories(string prefix, string workspace, string userName)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal)
        {
            workspace, prefix, Path.Join(prefix, "dosdevices"), Path.Join(prefix, "drive_c"),
            Path.Join(prefix, "drive_c", "windows"), Path.Join(prefix, "drive_c", "windows", "system32"),
            Path.Join(prefix, "drive_c", "windows", "syswow64"), Path.Join(prefix, "drive_c", "Program Files"),
            Path.Join(prefix, "drive_c", "Program Files (x86)"),
        };
        foreach (string startup in GetStartupDirectories(prefix, userName))
        {
            string? current = startup;
            while (current is not null && current != prefix)
            {
                paths.Add(current);
                current = Path.GetDirectoryName(current);
            }
        }
        return paths.Order(StringComparer.Ordinal).ToArray();
    }

    private static WineNativeDirectoryBinding CapturePrivateDirectory(string path)
    {
        NativeFileIdentity identity = WineXeBuildToolchainResolver.GetProtectedNativeIdentity(path, directory: true);
        if ((identity.Mode & 0x3F) != 0 || identity.OwnerUserId != WineXeBuildToolchainResolver.GetNativeUserId())
        {
            throw Unsupported();
        }
        return new WineNativeDirectoryBinding(path,
            WineXeBuildToolchainResolver.GetProtectedCanonicalPath(path, directory: true), DirectoryIdentity(identity));
    }

    private static NativeFileIdentity DirectoryIdentity(NativeFileIdentity identity) => identity with
    {
        Size = 0, ChangeSeconds = 0, ChangeNanoseconds = 0, ModificationSeconds = 0, ModificationNanoseconds = 0,
    };

    private static WineNativeMappingBinding[] CaptureMappings(string prefix)
    {
        string directory = Path.Join(prefix, "dosdevices");
        string[] entries = Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;
        if (!entries.SequenceEqual(new[] { "c:", "z:" }, StringComparer.Ordinal))
        {
            throw Unsupported();
        }
        var mappings = new WineNativeMappingBinding[2];
        int index = 0;
        foreach (string name in new[] { "c:", "z:" })
        {
            (string target, NativeFileIdentity identity) = WineXeBuildToolchainResolver.GetProtectedNativeLink(Path.Join(directory, name));
            if (target != (name == "c:" ? "../drive_c" : "/"))
            {
                throw Unsupported();
            }
            mappings[index++] = new WineNativeMappingBinding(name, target, identity);
        }
        return mappings;
    }

    private static WineNativeRegistryAliasBinding CaptureRegistryAlias(string path)
    {
        NativeFileIdentity identity = WineXeBuildToolchainResolver.GetProtectedNativeIdentity(path, directory: false);
        string canonical = WineXeBuildToolchainResolver.GetProtectedCanonicalPath(path, directory: false);
        string ancestor = Path.GetDirectoryName(canonical)!;
        if (canonical != Path.Join(WineXeBuildToolchainResolver.GetProtectedCanonicalPath(
                Path.GetDirectoryName(path)!, directory: true), Path.GetFileName(path)) ||
            identity.OwnerUserId != WineXeBuildToolchainResolver.GetNativeUserId())
        {
            throw Unsupported();
        }
        // Wine atomically saves its own registry. It is not caller-supplied configuration;
        // preserve its private parent/alias and trust only the closed vendor process as writer.
        return new WineNativeRegistryAliasBinding(path, canonical, identity.OwnerUserId,
            (ushort)(identity.Mode & 0xF012), ancestor,
            DirectoryIdentity(WineXeBuildToolchainResolver.GetProtectedNativeIdentity(ancestor, directory: true)));
    }

    private static uint ReadProtectedProcessUserId()
    {
        // /proc/self is kernel process metadata, not a serializable PID-specific search root.
        // OpenProtectedRead binds its actual descriptor before parsing the real/effective UID.
        using FileStream file = WineXeBuildToolchainResolver.OpenProtectedRead("/proc/self/status");
        using var reader = new StreamReader(file, StrictUtf8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        uint nativeUserId = WineXeBuildToolchainResolver.GetNativeUserId();
        uint? realUserId = null;
        int lines = 0;
        string? line;
        while ((line = ReadBoundedLine(reader)) is not null)
        {
            if (++lines > 1024)
            {
                throw Unsupported();
            }
            if (!line.StartsWith("Uid:", StringComparison.Ordinal))
            {
                continue;
            }
            string[] fields = line[4..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (realUserId is not null || fields.Length != 4 ||
                !uint.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out uint real) ||
                !uint.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint effective) ||
                !uint.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out _) ||
                !uint.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out _) ||
                real != nativeUserId || effective != nativeUserId)
            {
                throw Unsupported();
            }
            realUserId = real;
        }
        return realUserId ?? throw Unsupported();
    }

    private static string ReadProtectedUserName()
    {
        using FileStream file = WineXeBuildToolchainResolver.OpenProtectedRead(PasswordFile);
        if (file.Length > MaximumPasswordBytes)
        {
            throw Unsupported();
        }
        uint userId = ReadProtectedProcessUserId();
        string? userName = null;
        using var reader = new StreamReader(file, StrictUtf8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        string? line;
        while ((line = ReadBoundedLine(reader)) is not null)
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }
            string[] fields = line.Split(':');
            if (fields.Length != 7 || fields[0].Length == 0 || fields[0][0] is '+' or '-' ||
                !uint.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out uint entryId))
            {
                throw Unsupported();
            }
            if (entryId != userId)
            {
                continue;
            }
            if (userName is not null || !IsSafeUserName(fields[0]))
            {
                throw Unsupported();
            }
            userName = fields[0];
        }
        return userName ?? throw Unsupported();
    }

    private static bool IsSafeUserName(string? value) => value is { Length: > 0 and <= 64 } &&
        value is not "." and not ".." && value.All(character =>
            character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '.' or '-');

    private static string? ReadBoundedLine(StreamReader reader)
    {
        var line = new StringBuilder();
        int character;
        while ((character = reader.Read()) >= 0)
        {
            if (character == '\n')
            {
                return line.Length > 0 && line[^1] == '\r' ? line.ToString(0, line.Length - 1) : line.ToString();
            }
            if (line.Length == MaximumPasswordLineCharacters)
            {
                throw Unsupported();
            }
            line.Append((char)character);
        }
        return line.Length == 0 ? null : line.ToString();
    }

    internal static IReadOnlyDictionary<string, string> CreateClosedEnvironment(
        string workspace, WineNativePrefixPolicy prefix, string helperDirectory)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw Unsupported();
        }

        string home = CreatePrivateDirectory(Path.Join(workspace, "wine-home"));
        string config = CreatePrivateDirectory(Path.Join(home, "config"));
        string data = CreatePrivateDirectory(Path.Join(home, "data"));
        string cache = CreatePrivateDirectory(Path.Join(home, "cache"));
        string runtime = CreatePrivateDirectory(Path.Join(home, "runtime"));
        string modules = CreatePrivateDirectory(Path.Join(home, "native-modules"));
        string opensslConfig = Path.Join(home, "native-openssl.cnf");
        if (!WineXeBuildToolchainResolver.TryGetProtectedNativeIdentity(opensslConfig, directory: false, out _, out _))
        {
            using FileStream created = new(opensslConfig, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, BufferSize = 1,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
        }
        using (FileStream configFile = WineXeBuildToolchainResolver.OpenProtectedRead(opensslConfig))
        {
            if (configFile.Length != 0)
            {
                throw NativeElfReader.Failure();
            }
        }
        if (Directory.EnumerateFileSystemEntries(modules).Any())
        {
            throw NativeElfReader.Failure();
        }
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = helperDirectory, ["WINEPREFIX"] = prefix._prefix, ["HOME"] = home, ["USER"] = prefix._userName,
            ["XDG_CONFIG_HOME"] = config, ["XDG_DATA_HOME"] = data, ["XDG_CACHE_HOME"] = cache,
            ["XDG_RUNTIME_DIR"] = runtime, ["XDG_CONFIG_DIRS"] = config, ["XDG_DATA_DIRS"] = data,
            ["WINEDEBUG"] = "-all", ["WINEDLLOVERRIDES"] = HeadlessDllOverrides,
            ["GIO_MODULE_DIR"] = modules, ["GSETTINGS_SCHEMA_DIR"] = modules,
            ["OPENSSL_MODULES"] = modules, ["OPENSSL_ENGINES"] = modules,
        };
    }

    private static string CreatePrivateDirectory(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw Unsupported();
        }

        Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        CapturePrivateDirectory(path);
        return path;
    }

    internal static OperationFailureException Unsupported() => new(
        ExitCode.MissingPrerequisite, "wine-native-prefix-unsupported",
        "Wine requires a freshly created private managed prefix with closed native code and empty standard startup directories for headless XeBuild.");
}

internal sealed record WineNativePrefixPolicyBinding(
    string Prefix, string Workspace, bool NeedsInitialization,
    IReadOnlyList<string> ModuleDirectories, string UserName,
    IReadOnlyList<WineNativeMappingBinding> Mappings, IReadOnlyList<WineNativeRegistryAliasBinding> RegistryAliases,
    IReadOnlyList<WineNativeDirectoryBinding> Directories);

internal sealed record WineNativeDirectoryBinding(string AliasPath, string CanonicalPath, NativeFileIdentity Identity);
internal sealed record WineNativeMappingBinding(string Name, string Target, NativeFileIdentity Identity);
internal sealed record WineNativeRegistryAliasBinding(
    string AliasPath, string CanonicalPath, uint OwnerUserId, ushort Mode, string AncestorPath, NativeFileIdentity AncestorIdentity);
