using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JRunner.Cli.Infrastructure;

internal readonly record struct NativeFileIdentity(
    uint DeviceMajor, uint DeviceMinor, ulong Inode, ulong Size, uint OwnerUserId, ushort Mode,
    long ChangeSeconds, uint ChangeNanoseconds, long ModificationSeconds, uint ModificationNanoseconds);

internal sealed record NativeDependencyClosureSpec
{
    internal NativeDependencyClosureSpec(
        IReadOnlyList<string> executablePaths,
        IReadOnlyList<string> moduleDirectories,
        IReadOnlyList<string> configurationFiles,
        IReadOnlyDictionary<string, string> environmentBindings)
    {
        ExecutablePaths = CopyPaths(executablePaths);
        ModuleDirectories = CopyPaths(moduleDirectories);
        ConfigurationFiles = CopyPaths(configurationFiles);
        if (environmentBindings is null || environmentBindings.Count > 32)
        {
            throw NativeElfReader.Failure();
        }
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string name, string value) in environmentBindings)
        {
            if (!NativeDependencyClosure.IsAllowedEnvironmentBinding(name, value) || !environment.TryAdd(name, value))
            {
                throw NativeElfReader.Failure();
            }
        }
        EnvironmentBindings = new ReadOnlyDictionary<string, string>(environment);
    }

    public IReadOnlyList<string> ExecutablePaths { get; }
    public IReadOnlyList<string> ModuleDirectories { get; }
    public IReadOnlyList<string> ConfigurationFiles { get; }
    public IReadOnlyDictionary<string, string> EnvironmentBindings { get; }
    internal WineNativePrefixPolicy? PrefixPolicy { get; init; }

    private static IReadOnlyList<string> CopyPaths(IReadOnlyList<string> paths)
    {
        if (paths is null || paths.Count > NativeDependencyClosure.MaximumPaths)
        {
            throw NativeElfReader.Failure();
        }
        var result = new HashSet<string>(StringComparer.Ordinal);
        long characters = 0;
        foreach (string path in paths)
        {
            NativeDependencyClosure.RequireAbsoluteAlias(path);
            if ((characters += path.Length) > NativeDependencyClosure.MaximumMetadataCharacters)
            {
                throw NativeElfReader.Failure();
            }
            if (!result.Add(path))
            {
                throw NativeElfReader.Failure();
            }
        }
        return Array.AsReadOnly(result.Order(StringComparer.Ordinal).ToArray());
    }
}

internal sealed record NativeLoaderFileSystemLayout(
    string CachePath, string PreloadPath, string ConfigurationPath, IReadOnlyList<string> DefaultDirectories,
    IReadOnlyList<string> GconvDirectories, string? NssConfigurationPath = null);

internal sealed record NativeDependencyClosureSpecBinding(
    string[] ExecutablePaths, string[] ModuleDirectories, string[] ConfigurationFiles,
    Dictionary<string, string> EnvironmentBindings);

internal sealed record NativePathBinding(
    string AliasPath, bool Directory, bool Exists, string CanonicalPath, NativeFileIdentity Identity,
    string ExistingAncestor, NativeFileIdentity AncestorIdentity, bool FreezeMetadata);

internal sealed record NativeElfImageBinding(
    ushort ObjectType, ushort Machine, byte ElfClass, byte DataEncoding, byte OsAbi, string? Interpreter,
    string[] Needed, string[] RPath, string[] RunPath, string? Soname, ulong Flags, ulong Flags1,
    bool HasDynamicSegment, bool HasSupportedAbi, byte AbiVersion);

internal enum NativeObjectProtection
{
    MetadataOnly,
    ContentFrozen,
}

internal sealed record NativeObjectBinding(
    string CanonicalPath, NativeFileIdentity Identity, NativeObjectProtection Protection, string? ContentSha256,
    NativeElfImageBinding? Image, bool Script, bool PortableExecutable, bool Loadable);

internal sealed record NativeConfigurationDirectoryBinding(string AliasPath, string[] EntryNames);

internal sealed record NativeDependencyClosureBinding
{
    public required int Version { get; init; }
    public required NativeDependencyClosureSpecBinding Inputs { get; init; }
    public required NativeLoaderFileSystemLayout LoaderLayout { get; init; }
    public required string InputsSha256 { get; init; }
    public required string ManifestSha256 { get; init; }
    public required NativePathBinding[] Paths { get; init; }
    public required string[] SearchDirectories { get; init; }
    public required string[] RecursiveDirectories { get; init; }
    public required NativeObjectBinding[] Objects { get; init; }
    public required NativeConfigurationDirectoryBinding[] ConfigurationDirectories { get; init; }
    public required NativeLoaderCacheEntry[] CacheEntries { get; init; }
    public required string[] HardwareCapabilityNames { get; init; }
    public required bool PreloadExists { get; init; }
    public required string[] NssServices { get; init; }
    public required string[] GconvModulePaths { get; init; }
    public WineNativePrefixPolicyBinding? PrefixPolicy { get; init; }
}

/// <summary>
/// A filesystem-only, frozen overapproximation of glibc and registered Wine/CLR native code roots.
/// Protection targets other UIDs, not a hostile current UID or root. Unused search/cache candidates
/// retain protected identities and parsed selectors; executable/module dependency bytes are frozen.
/// </summary>
internal sealed class NativeDependencyClosure
{
    internal const int MaximumBindingBytes = 48 * 1024 * 1024;
    internal const int MaximumMetadataCharacters = 16 * 1024 * 1024;
    internal const int MaximumPaths = 200000;
    private const int MaximumObjects = 50000;
    private const int MaximumRoots = 8192;
    private const int MaximumConfigurationBytes = 1024 * 1024;
    private const int MaximumConfigurationDepth = 16;
    private const int DirectoryType = 0x4000;
    private const int RegularFileType = 0x8000;
    private const int TypeMask = 0xF000;
    private const int GroupOtherPermissions = 0x3F;
    private const string HeadlessWineOverrides = WineNativePrefixPolicy.HeadlessDllOverrides;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    // glibc-2.39 elf/dl-load.c:open_path appends capstr + a filename; it
    // never descends unrelated children of a search root. Older glibc's
    // elf/dl-hwcaps.c powerset can start with tls or one of these x86
    // capability/platform names. Closing each entire first-component tree
    // overapproximates every combination, without guessing the current CPU.
    // https://github.com/bminor/glibc/blob/glibc-2.39/elf/dl-load.c
    // https://github.com/bminor/glibc/blob/glibc-2.36/elf/dl-hwcaps.c
    // https://github.com/bminor/glibc/blob/glibc-2.36/sysdeps/x86/dl-procinfo.c
    private static readonly string[] HardwareCapabilityTreeNames =
        ["glibc-hwcaps", "tls", "sse2", "x86_64", "avx512_1", "i586", "i686", "haswell", "xeon_phi"];
    private static readonly NativeLoaderFileSystemLayout UbuntuLayout = new(
        "/etc/ld.so.cache", "/etc/ld.so.preload", "/etc/ld.so.conf",
        Array.AsReadOnly(new[]
        {
            "/lib", "/usr/lib", "/lib64", "/usr/lib64", "/lib32", "/usr/lib32",
            "/lib/x86_64-linux-gnu", "/usr/lib/x86_64-linux-gnu",
            "/lib/i386-linux-gnu", "/usr/lib/i386-linux-gnu",
            "/lib/i686-linux-gnu", "/usr/lib/i686-linux-gnu",
        }),
        Array.AsReadOnly(new[]
        {
            "/usr/lib/x86_64-linux-gnu/gconv", "/usr/lib/i386-linux-gnu/gconv",
            "/usr/lib/gconv", "/usr/lib64/gconv", "/usr/lib32/gconv",
        }), "/etc/nsswitch.conf");
    // nss_module.c constructs libnss_<service>.so.2 and calls __libc_dlopen;
    // files/dns are built into current glibc. These are known Ubuntu/system
    // NSS services, not authorization for arbitrary path-like module names.
    // nss_database.c also supplies nis/nisplus/compat defaults on older builds.
    // https://github.com/bminor/glibc/blob/glibc-2.39/nss/nss_module.c
    // https://github.com/bminor/glibc/blob/glibc-2.39/nss/nss_database.c
    private static readonly string[] DefaultNssServices = ["compat", "dns", "files", "nis", "nisplus"];
    private static readonly HashSet<string> KnownNssServices = new(StringComparer.Ordinal)
    {
        "files", "dns", "compat", "db", "nis", "nisplus", "systemd", "resolve", "myhostname", "mymachines",
        "mdns", "mdns4", "mdns6", "mdns_minimal", "mdns4_minimal", "mdns6_minimal", "extrausers", "sss", "winbind",
    };
    private readonly NativeDependencyClosureSpec _inputs;
    private readonly NativeDependencyClosureBinding _binding;
    private readonly Dictionary<string, NativePathBinding> _paths;
    private readonly Dictionary<string, NativeObjectBinding> _objects;
    private readonly HashSet<string> _executableAliases;
    private readonly HashSet<string> _searchRootAliases;
    private readonly bool _production;

    private NativeDependencyClosure(NativeDependencyClosureSpec inputs, NativeDependencyClosureBinding binding, bool production)
    {
        _inputs = inputs;
        _binding = binding;
        _production = production;
        _paths = binding.Paths.ToDictionary(static path => path.AliasPath, StringComparer.Ordinal);
        _objects = binding.Objects.ToDictionary(static item => item.CanonicalPath, StringComparer.Ordinal);
        _executableAliases = new HashSet<string>(inputs.ExecutablePaths, StringComparer.Ordinal);
        _searchRootAliases = new HashSet<string>(binding.SearchDirectories, StringComparer.Ordinal);
    }

    internal static NativeDependencyClosure Prepare(NativeDependencyClosureSpec spec) => PrepareCore(spec, UbuntuLayout, production: true);

    // These methods model real ELF/cache/filesystem metadata but can never produce a launchable
    // production closure. A synthetic system layout is not accepted by the authenticated protocol.
    internal static NativeDependencyClosure PrepareForTesting(NativeDependencyClosureSpec spec, NativeLoaderFileSystemLayout layout) =>
        PrepareCore(spec, layout, production: false);

    private static NativeDependencyClosure PrepareCore(NativeDependencyClosureSpec spec, NativeLoaderFileSystemLayout layout, bool production)
    {
        PreparationDiagnostic? diagnostic = null;
        try
        {
            diagnostic = PreparationDiagnostic.CreateIfEnabled(production);
            diagnostic?.Stage("PrepareCore.Inputs");
            if (!OperatingSystem.IsLinux() || spec is null || spec.ExecutablePaths.Count == 0)
            {
                throw NativeElfReader.Failure();
            }
            NativeDependencyClosureSpec inputs = CopyInputs(spec);
            diagnostic?.Stage("PrepareCore.Layout");
            NativeLoaderFileSystemLayout frozenLayout = CopyLayout(layout);
            diagnostic?.Stage("PrepareCore.Builder");
            NativeDependencyClosureBinding binding = new Builder(inputs, frozenLayout, diagnostic).Build();
            var closure = new NativeDependencyClosure(inputs, binding, production);
            diagnostic?.Stage("PrepareCore.ValidateFrozenGraph");
            closure.ValidateFrozenGraph(diagnostic);
            return closure;
        }
        catch (Exception exception)
        {
            diagnostic?.Report(exception);
            throw NativeElfReader.Failure();
        }
    }

    // Temporary, preparation-local CI observer; never retained by a returned closure.
    private sealed class PreparationDiagnostic
    {
        private const string Prefix = "JRUNNER_NATIVE_CLOSURE_DIAGNOSTIC: ";
        private const int MaximumLineCharacters = 8 * 1024 - 1;
        private const int MaximumRetainedPathCharacters = 4096;
        private const int MaximumPathCharacters = 1024;
        private const int MaximumSourceCharacters = 2560;
        private const int MaximumCaughtCharacters = 2048;
        private const int MaximumExceptionDepth = 3;
        private const int MaximumStackFrames = 16;
        private readonly Regex _keyText = new(
            "(?:[0-9a-fA-F]{2}[:\\-\\s]*){16}|(?:[0-9a-fA-F]{2}[:\\-\\s]+){3,}[0-9a-fA-F]{2}|[0-9a-fA-F]{8,}",
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        private string _stage = "PrepareCore";
        private string? _operation;
        private string? _path;
        private string? _canonicalPath;
        private string? _root;
        private string? _sourceDetails;
        private readonly Action<Exception> _originObserver;

        private PreparationDiagnostic()
        {
            _originObserver = CaptureOrigin;
        }

        internal Action<Exception> OriginObserver => _originObserver;

        internal static PreparationDiagnostic? CreateIfEnabled(bool production)
        {
            try
            {
                return production && string.Equals(Environment.GetEnvironmentVariable("JRUNNER_NATIVE_CLOSURE_DIAGNOSTIC"),
                    "1", StringComparison.Ordinal) ? new PreparationDiagnostic() : null;
            }
            catch
            {
                return null;
            }
        }

        internal void Stage(string stage, string? root = null)
        {
            try
            {
                _stage = stage;
                _operation = null;
                if (root is not null)
                {
                    _root = RetainPath(root);
                }
            }
            catch
            {
            }
        }

        internal void Inspect(string operation, string path, string? canonicalPath = null, string? root = null)
        {
            try
            {
                _operation = operation;
                _path = RetainPath(path);
                _canonicalPath = canonicalPath is null ? null : RetainPath(canonicalPath);
                if (root is not null)
                {
                    _root = RetainPath(root);
                }
                _sourceDetails = null;
            }
            catch
            {
            }
        }

        internal void InspectBound(string operation, string path, IReadOnlyDictionary<string, NativePathBinding> paths,
            string? root = null)
        {
            try
            {
                paths.TryGetValue(path, out NativePathBinding? bound);
                Inspect(operation, path, bound?.CanonicalPath, root ?? bound?.ExistingAncestor);
            }
            catch
            {
            }
        }

        private string RetainPath(string path)
        {
            if (path.Length <= MaximumRetainedPathCharacters)
            {
                return path;
            }
            var text = new StringBuilder(MaximumPathCharacters);
            if (!AppendSanitized(text, path, MaximumPathCharacters - 3))
            {
                text.Append("...");
            }
            return text.ToString();
        }

        private void CaptureOrigin(Exception exception)
        {
            try
            {
                // Outer resolver catches must not replace the first, deepest source.
                _sourceDetails ??= DescribeException(exception, "source", MaximumSourceCharacters);
            }
            catch
            {
            }
        }

        internal void Report(Exception exception)
        {
            try
            {
                var text = new StringBuilder(MaximumLineCharacters + 1);
                text.Append(Prefix);
                AppendField(text, "stage", _stage, 96, MaximumLineCharacters);
                AppendField(text, "operation", _operation, 96, MaximumLineCharacters);
                AppendField(text, "path", _path, MaximumPathCharacters, MaximumLineCharacters);
                AppendField(text, "canonical", _canonicalPath, MaximumPathCharacters, MaximumLineCharacters);
                AppendField(text, "root", _root, MaximumPathCharacters, MaximumLineCharacters);
                text.Append(_sourceDetails);
                text.Append(DescribeException(exception, "caught",
                    Math.Min(MaximumCaughtCharacters, MaximumLineCharacters - text.Length)));
                Console.Error.Write(text.Append('\n').ToString());
            }
            catch
            {
                // Diagnostics must never change the existing public redacted failure.
            }
        }

        private string DescribeException(Exception exception, string label, int maximumCharacters)
        {
            var text = new StringBuilder(maximumCharacters);
            Exception? current = exception;
            for (int depth = 0; current is not null && depth < MaximumExceptionDepth; depth++)
            {
                if (text.Length > maximumCharacters - 96)
                {
                    break;
                }
                string field = string.Concat(label, depth.ToString(CultureInfo.InvariantCulture));
                AppendField(text, field + ".type", current.GetType().FullName, 128, maximumCharacters);
                string hresult = string.Concat(" ", field, ".hresult=0x",
                    unchecked((uint)current.HResult).ToString("X8", CultureInfo.InvariantCulture));
                if (text.Length + hresult.Length <= maximumCharacters)
                {
                    text.Append(hresult);
                }
                var stack = new StackTrace(current, fNeedFileInfo: true);
                if (text.Length + field.Length + 16 < maximumCharacters)
                {
                    text.Append(' ').Append(field).Append(".origin=\"");
                    int limit = Math.Min(text.Length + 192, maximumCharacters - 1);
                    if (!AppendMethod(text, current.TargetSite ?? stack.GetFrame(0)?.GetMethod(), limit - 3))
                    {
                        text.Append("...");
                    }
                    text.Append('"');
                }
                if (text.Length + field.Length + 16 < maximumCharacters)
                {
                    text.Append(' ').Append(field).Append(".stack=\"");
                    int limit = Math.Min(text.Length + (depth == 0 ? 1536 : 512), maximumCharacters - 1);
                    bool complete = true;
                    int frames = Math.Min(stack.FrameCount, MaximumStackFrames);
                    for (int index = 0; index < frames; index++)
                    {
                        StackFrame? frame = stack.GetFrame(index);
                        if (index != 0 && !AppendEscaped(text, " | ", limit - 3))
                        {
                            complete = false;
                            break;
                        }
                        if (!AppendMethod(text, frame?.GetMethod(), limit - 3) ||
                            !AppendEscaped(text, " @ ", limit - 3) ||
                            !AppendSanitized(text, frame?.GetFileName(), limit - 3) ||
                            !AppendEscaped(text, string.Concat(":", frame?.GetFileLineNumber().ToString(CultureInfo.InvariantCulture),
                                ":", frame?.GetFileColumnNumber().ToString(CultureInfo.InvariantCulture),
                                " il=", frame?.GetILOffset().ToString(CultureInfo.InvariantCulture)), limit - 3))
                        {
                            complete = false;
                            break;
                        }
                    }
                    if (!complete || stack.FrameCount > frames)
                    {
                        text.Append("...");
                    }
                    text.Append('"');
                }
                current = current.InnerException;
            }
            if (current is not null && text.Length + 21 <= maximumCharacters)
            {
                text.Append(" chain_truncated=true");
            }
            return text.ToString();
        }

        private bool AppendMethod(StringBuilder text, MethodBase? method, int limit)
        {
            return method is null ? AppendEscaped(text, "-", limit) :
                AppendSanitized(text, method.DeclaringType?.FullName, limit) &&
                AppendEscaped(text, ".", limit) && AppendSanitized(text, method.Name, limit);
        }

        private void AppendField(StringBuilder text, string name, string? value, int fieldCharacters, int maximumCharacters)
        {
            if (text.Length + name.Length + 8 > maximumCharacters)
            {
                return;
            }
            text.Append(' ').Append(name).Append("=\"");
            int limit = Math.Min(text.Length + fieldCharacters, maximumCharacters - 1);
            if (!AppendSanitized(text, value, limit - 3))
            {
                text.Append("...");
            }
            text.Append('"');
        }

        private bool AppendSanitized(StringBuilder text, string? value, int limit)
        {
            if (value is null)
            {
                return AppendEscaped(text, "-", limit);
            }
            int offset = 0;
            // Match against the full input before limiting output; never expose a cut-off key prefix.
            foreach (ValueMatch match in _keyText.EnumerateMatches(value))
            {
                if (!AppendEscaped(text, value.AsSpan(offset, match.Index - offset), limit) ||
                    !AppendEscaped(text, "[redacted]", limit))
                {
                    return false;
                }
                offset = match.Index + match.Length;
            }
            return AppendEscaped(text, value.AsSpan(offset), limit);
        }

        private static bool AppendEscaped(StringBuilder text, ReadOnlySpan<char> value, int limit)
        {
            const string digits = "0123456789ABCDEF";
            foreach (char character in value)
            {
                int count = character is '\\' or '"' ? 2 : character is >= ' ' and <= '~' ? 1 : 6;
                if (text.Length > limit - count)
                {
                    return false;
                }
                if (count == 1)
                {
                    text.Append(character);
                }
                else if (count == 2)
                {
                    text.Append('\\').Append(character);
                }
                else
                {
                    text.Append("\\u").Append(digits[character >> 12]).Append(digits[(character >> 8) & 15])
                        .Append(digits[(character >> 4) & 15]).Append(digits[character & 15]);
                }
            }
            return true;
        }
    }

    internal void EnsureProduction()
    {
        if (!_production || !LayoutEquals(_binding.LoaderLayout, UbuntuLayout))
        {
            throw NativeElfReader.Failure();
        }
    }

    internal static NativeDependencyClosure PrepareExtension(NativeDependencyClosure initial, NativeDependencyClosureSpec spec)
    {
        try
        {
            initial.EnsureProduction();
            WineNativePrefixPolicyBinding? requestedPrefix = spec.PrefixPolicy?.ToBinding();
            bool initializing = initial._binding.PrefixPolicy is WineNativePrefixPolicyBinding initialPrefix &&
                initialPrefix.NeedsInitialization && requestedPrefix is not null && !requestedPrefix.NeedsInitialization &&
                initialPrefix.Prefix == requestedPrefix.Prefix && initialPrefix.Workspace == requestedPrefix.Workspace;
            initial.RevalidateCore(revalidatePrefix: !initializing);
            NativeDependencyClosure extended = Prepare(spec);
            extended.ValidateInputRoots(initial._inputs);
            if (initial._binding.PrefixPolicy is WineNativePrefixPolicyBinding originalPrefix &&
                (extended._binding.PrefixPolicy is not WineNativePrefixPolicyBinding finalPrefix ||
                 originalPrefix.Prefix != finalPrefix.Prefix || originalPrefix.Workspace != finalPrefix.Workspace ||
                 (originalPrefix.NeedsInitialization && finalPrefix.NeedsInitialization) ||
                 (!originalPrefix.NeedsInitialization && HashMetadata(originalPrefix) != HashMetadata(finalPrefix))))
            {
                throw NativeElfReader.Failure();
            }
            foreach (NativePathBinding original in initial._binding.Paths)
            {
                if (!extended._paths.TryGetValue(original.AliasPath, out NativePathBinding? retained) ||
                    !EquivalentPath(original, retained))
                {
                    throw NativeElfReader.Failure();
                }
            }
            foreach (NativeObjectBinding original in initial._binding.Objects)
            {
                if (!extended._objects.TryGetValue(original.CanonicalPath, out NativeObjectBinding? retained) ||
                    !RetainsObject(original, retained))
                {
                    throw NativeElfReader.Failure();
                }
            }
            if (!initial._binding.CacheEntries.SequenceEqual(extended._binding.CacheEntries) ||
                !initial._binding.HardwareCapabilityNames.SequenceEqual(extended._binding.HardwareCapabilityNames, StringComparer.Ordinal) ||
                !initial._binding.NssServices.SequenceEqual(extended._binding.NssServices, StringComparer.Ordinal) ||
                !initial._binding.GconvModulePaths.SequenceEqual(extended._binding.GconvModulePaths, StringComparer.Ordinal) ||
                initial._binding.PreloadExists != extended._binding.PreloadExists)
            {
                throw NativeElfReader.Failure();
            }
            initial.RevalidateCore(revalidatePrefix: !initializing);
            return extended;
        }
        catch (Exception)
        {
            throw NativeElfReader.Failure();
        }
    }

    internal void ValidateInputs(NativeDependencyClosureSpec required)
    {
        EnsureProduction();
        ValidateInputRoots(required);
        if (required.PrefixPolicy is not null &&
            (_binding.PrefixPolicy is null || HashMetadata(required.PrefixPolicy.ToBinding()) != HashMetadata(_binding.PrefixPolicy)))
        {
            throw NativeElfReader.Failure();
        }
    }

    private void ValidateInputRoots(NativeDependencyClosureSpec required)
    {
        if (required is null || !IsSubset(required.ExecutablePaths, _inputs.ExecutablePaths) ||
            !IsSubset(required.ModuleDirectories, _inputs.ModuleDirectories) ||
            !IsSubset(required.ConfigurationFiles, _inputs.ConfigurationFiles))
        {
            throw NativeElfReader.Failure();
        }
        foreach ((string name, string value) in required.EnvironmentBindings)
        {
            if (!_inputs.EnvironmentBindings.TryGetValue(name, out string? frozen) || !string.Equals(frozen, value, StringComparison.Ordinal))
            {
                throw NativeElfReader.Failure();
            }
        }
    }

    internal void ValidateExecutable(string absoluteAlias)
    {
        try
        {
            if (!_executableAliases.Contains(absoluteAlias) || !_paths.TryGetValue(absoluteAlias, out NativePathBinding? path) ||
                !path.Exists || path.Directory || !_objects.TryGetValue(path.CanonicalPath, out NativeObjectBinding? item))
            {
                throw NativeElfReader.Failure();
            }
            RevalidatePath(path);
            WineXeBuildToolchainResolver.ValidateProtectedExecutable(absoluteAlias);
            ValidateExecutableObject(item);
        }
        catch (Exception)
        {
            throw NativeElfReader.Failure();
        }
    }

    internal void ApplyClosedEnvironment(ProcessStartInfo startInfo)
    {
        EnsureProduction();
        try
        {
            startInfo.Environment.Clear();
            foreach ((string name, string value) in _inputs.EnvironmentBindings)
            {
                startInfo.Environment[name] = value;
            }
            string home = _inputs.EnvironmentBindings["HOME"];
            string moduleDirectory = Path.Join(home, "native-modules");
            startInfo.Environment["LANG"] = "C";
            startInfo.Environment["LC_ALL"] = "C";
            startInfo.Environment["TZ"] = "UTC";
            foreach (string name in new[] { "XDG_RUNTIME_DIR", "XDG_CONFIG_DIRS", "XDG_DATA_DIRS" })
            {
                if (!startInfo.Environment.ContainsKey(name))
                {
                    startInfo.Environment[name] = home;
                }
            }
            // gconv_cache.c treats non-null GCONV_PATH as disabling its binary
            // cache. gconv_conf.c still appends compiled defaults, whose TEXT
            // module selectors are explicitly parsed and bound below.
            // https://github.com/bminor/glibc/blob/glibc-2.39/iconv/gconv_conf.c
            // https://github.com/bminor/glibc/blob/glibc-2.39/iconv/gconv_cache.c
            startInfo.Environment["GCONV_PATH"] = moduleDirectory;
            startInfo.Environment["GIO_MODULE_DIR"] = moduleDirectory;
            startInfo.Environment["GIO_USE_VFS"] = "local";
            startInfo.Environment["GIO_USE_PROXY_RESOLVER"] = "dummy";
            startInfo.Environment["GSETTINGS_BACKEND"] = "memory";
            startInfo.Environment["GSETTINGS_SCHEMA_DIR"] = moduleDirectory;
            startInfo.Environment["OPENSSL_CONF"] = Path.Join(home, "native-openssl.cnf");
            startInfo.Environment["OPENSSL_MODULES"] = moduleDirectory;
            startInfo.Environment["OPENSSL_ENGINES"] = moduleDirectory;
            if (_inputs.EnvironmentBindings.ContainsKey("WINEPREFIX"))
            {
                startInfo.Environment["WINEDLLOVERRIDES"] = HeadlessWineOverrides;
                startInfo.Environment["WINEDEBUG"] = "-all";
            }
        }
        catch (Exception)
        {
            throw NativeElfReader.Failure();
        }
    }

    internal void Revalidate() => RevalidateCore(revalidatePrefix: true);

    private void RevalidateCore(bool revalidatePrefix, bool validateMetadata = false)
    {
        try
        {
            if (revalidatePrefix)
            {
                _inputs.PrefixPolicy?.Revalidate();
            }
            foreach (NativePathBinding path in _binding.Paths)
            {
                RevalidatePath(path);
            }
            foreach (NativeObjectBinding item in _binding.Objects)
            {
                using FileStream stream = WineXeBuildToolchainResolver.OpenProtectedRead(item.CanonicalPath);
                if (WineXeBuildToolchainResolver.GetNativeIdentity(stream.SafeFileHandle) != item.Identity ||
                    (item.Protection == NativeObjectProtection.ContentFrozen &&
                     !string.Equals(HashFile(stream), item.ContentSha256, StringComparison.Ordinal)))
                {
                    throw NativeElfReader.Failure();
                }
                if (validateMetadata || item.Protection == NativeObjectProtection.MetadataOnly)
                {
                    NativeElfImage? image = NativeElfReader.ReadModuleMetadata(stream);
                    if (!EquivalentImage(item.Image, image) ||
                        (item.Protection == NativeObjectProtection.MetadataOnly && item.Image is null && !HasPortableExecutableMagic(stream)))
                    {
                        throw NativeElfReader.Failure();
                    }
                    if (item.Script)
                    {
                        WineXeBuildToolchainResolver.ValidateSupportedNativeScript(item.CanonicalPath);
                    }
                }
                if (WineXeBuildToolchainResolver.GetNativeIdentity(stream.SafeFileHandle) != item.Identity)
                {
                    throw NativeElfReader.Failure();
                }
            }
            foreach (NativeConfigurationDirectoryBinding directory in _binding.ConfigurationDirectories)
            {
                if (!ReadEntryNames(directory.AliasPath).SequenceEqual(directory.EntryNames, StringComparer.Ordinal))
                {
                    throw NativeElfReader.Failure();
                }
            }
            if (validateMetadata)
            {
                ValidateNssSelection();
            }
        }
        catch (Exception)
        {
            throw NativeElfReader.Failure();
        }
    }

    internal NativeDependencyClosureBinding ToBinding()
    {
        EnsureProduction();
        return CopyBinding(_binding);
    }

    internal NativeDependencyClosureBinding ToBindingForTesting() => CopyBinding(_binding);

    internal static NativeDependencyClosure FromBinding(NativeDependencyClosureBinding binding) =>
        FromBindingCore(binding, UbuntuLayout, production: true);

    internal static NativeDependencyClosure FromBindingForTesting(NativeDependencyClosureBinding binding, NativeLoaderFileSystemLayout layout) =>
        FromBindingCore(binding, layout, production: false);

    private static NativeDependencyClosure FromBindingCore(NativeDependencyClosureBinding binding, NativeLoaderFileSystemLayout expectedLayout, bool production)
    {
        try
        {
            ValidateBindingBounds(binding);
            if (!LayoutEquals(binding.LoaderLayout, expectedLayout) || binding.Version != 2 ||
                !IsHash(binding.InputsSha256) || !IsHash(binding.ManifestSha256) ||
                !string.Equals(HashInputs(binding.Inputs, binding.PrefixPolicy), binding.InputsSha256, StringComparison.Ordinal) ||
                !string.Equals(HashMetadata(binding with { ManifestSha256 = string.Empty }), binding.ManifestSha256, StringComparison.Ordinal))
            {
                throw NativeElfReader.Failure();
            }
            NativeDependencyClosureBinding frozen = CopyBinding(binding);
            NativeDependencyClosureSpec inputs = new(frozen.Inputs.ExecutablePaths, frozen.Inputs.ModuleDirectories,
                frozen.Inputs.ConfigurationFiles, frozen.Inputs.EnvironmentBindings)
            {
                PrefixPolicy = frozen.PrefixPolicy is null ? null : WineNativePrefixPolicy.FromBinding(frozen.PrefixPolicy),
            };
            if (!IsSortedUnique(frozen.Inputs.ExecutablePaths) || !IsSortedUnique(frozen.Inputs.ModuleDirectories) ||
                !IsSortedUnique(frozen.Inputs.ConfigurationFiles))
            {
                throw NativeElfReader.Failure();
            }
            var closure = new NativeDependencyClosure(inputs, frozen, production);
            closure.ValidateFrozenGraph();
            // The authenticated manifest is the ORIGINAL graph. This validates
            // exactly that graph; it never prepares/rescans a replacement graph.
            closure.RevalidateCore(revalidatePrefix: true, validateMetadata: true);
            return closure;
        }
        catch (Exception)
        {
            throw NativeElfReader.Failure();
        }
    }

    private void ValidateFrozenGraph(PreparationDiagnostic? diagnostic = null)
    {
        diagnostic?.Stage("ValidateFrozenGraph.Bounds");
        ValidateBindingBounds(_binding);
        diagnostic?.Stage("ValidateFrozenGraph.Environment");
        ValidateEnvironment(_inputs.EnvironmentBindings, _paths, diagnostic);
        if (_inputs.EnvironmentBindings.ContainsKey("WINEPREFIX"))
        {
            if (_binding.PrefixPolicy is null || _inputs.PrefixPolicy is null ||
                _binding.PrefixPolicy.Prefix != _inputs.EnvironmentBindings["WINEPREFIX"] ||
                !_inputs.EnvironmentBindings.TryGetValue("USER", out string? user) || user != _inputs.PrefixPolicy.UserName ||
                !IsSubset(_inputs.PrefixPolicy.ModuleDirectories, _inputs.ModuleDirectories) ||
                !IsSubset(_inputs.PrefixPolicy.ConfigurationFiles, _inputs.ConfigurationFiles))
            {
                throw NativeElfReader.Failure();
            }
        }
        else if (_binding.PrefixPolicy is not null || _inputs.PrefixPolicy is not null)
        {
            throw NativeElfReader.Failure();
        }
        diagnostic?.Stage("ValidateFrozenGraph.Roots");
        foreach (string root in _binding.SearchDirectories)
        {
            diagnostic?.InspectBound("SearchRoot", root, _paths, root);
            if (!_paths.TryGetValue(root, out NativePathBinding? path) || !path.Directory)
            {
                throw NativeElfReader.Failure();
            }
        }
        foreach (string root in _binding.RecursiveDirectories)
        {
            diagnostic?.InspectBound("RecursiveRoot", root, _paths, root);
            if (!_paths.TryGetValue(root, out NativePathBinding? path) || !path.Directory)
            {
                throw NativeElfReader.Failure();
            }
        }
        foreach (string root in _binding.SearchDirectories)
        {
            foreach (string capability in HardwareCapabilityTreeNames)
            {
                string path = Path.Join(root, capability);
                diagnostic?.InspectBound("CapabilityRoot", path, _paths, root);
                RequireRecursiveRoot(path);
            }
        }
        foreach (string root in _binding.LoaderLayout.DefaultDirectories)
        {
            diagnostic?.InspectBound("DefaultRoot", root, _paths, root);
            RequireSearchRoot(root);
        }
        foreach (string root in _binding.LoaderLayout.GconvDirectories.Concat(_inputs.ModuleDirectories))
        {
            diagnostic?.InspectBound("ModuleRoot", root, _paths, root);
            RequireRecursiveRoot(root);
        }
        diagnostic?.Stage("ValidateFrozenGraph.Configurations");
        foreach (string config in _inputs.ConfigurationFiles.Append(_binding.LoaderLayout.CachePath)
            .Append(_binding.LoaderLayout.PreloadPath).Append(_binding.LoaderLayout.ConfigurationPath))
        {
            diagnostic?.InspectBound("Configuration", config, _paths);
            if (!_paths.TryGetValue(config, out NativePathBinding? path) || path.Directory || !path.FreezeMetadata)
            {
                throw NativeElfReader.Failure();
            }
            if (path.Exists)
            {
                RequireBoundFile(config);
            }
        }
        if (_binding.LoaderLayout.NssConfigurationPath is string nss)
        {
            diagnostic?.InspectBound("NssConfiguration", nss, _paths);
            if (!_paths.TryGetValue(nss, out NativePathBinding? nssConfigurationBinding) ||
                nssConfigurationBinding.Directory || !nssConfigurationBinding.FreezeMetadata)
            {
                throw NativeElfReader.Failure();
            }
            if (nssConfigurationBinding.Exists)
            {
                RequireBoundFile(nss);
            }
        }
        diagnostic?.Stage("ValidateFrozenGraph.Paths");
        foreach (NativePathBinding path in _binding.Paths)
        {
            diagnostic?.Inspect("PathBinding", path.AliasPath, path.CanonicalPath, path.ExistingAncestor);
            RequireAbsoluteAlias(path.AliasPath);
            // An absent alias retains the unresolved suffix after its protected
            // existing ancestor. Never collapse missing/../target into a target.
            RequireLiteralAbsolutePath(path.CanonicalPath, allowDotSegments: !path.Exists);
            RequireLiteralAbsolutePath(path.ExistingAncestor);
            ValidateIdentity(path.AncestorIdentity, directory: true);
            if (path.Exists)
            {
                ValidateIdentity(path.Identity, path.Directory);
                if (!path.Directory && path.FreezeMetadata && !_objects.ContainsKey(path.CanonicalPath))
                {
                    throw NativeElfReader.Failure();
                }
            }
            else if (path.Identity != default || !IsBelow(path.CanonicalPath, path.ExistingAncestor) ||
                string.Equals(path.CanonicalPath, path.ExistingAncestor, StringComparison.Ordinal))
            {
                throw NativeElfReader.Failure();
            }
        }
        var metadataBoundFiles = new HashSet<(string Path, NativeFileIdentity Identity)>();
        var recursiveRoots = new HashSet<string>(_binding.RecursiveDirectories, StringComparer.Ordinal);
        diagnostic?.Stage("ValidateFrozenGraph.PathObjects");
        foreach (NativePathBinding path in _binding.Paths)
        {
            diagnostic?.Inspect("PathObject", path.AliasPath, path.CanonicalPath, path.ExistingAncestor);
            if (path.Exists && !path.Directory && path.FreezeMetadata)
            {
                metadataBoundFiles.Add((path.CanonicalPath, path.Identity));
                if (_objects.TryGetValue(path.CanonicalPath, out NativeObjectBinding? item))
                {
                    if (IsWithinRoot(path.AliasPath, recursiveRoots))
                    {
                        RequireContentFrozen(item);
                    }
                    if (item.Image is NativeElfImageBinding image)
                    {
                        RequireImageAliasPaths(path, image, diagnostic);
                    }
                }
            }
        }
        diagnostic?.Stage("ValidateFrozenGraph.Objects");
        foreach (NativeObjectBinding item in _binding.Objects)
        {
            diagnostic?.InspectBound("ObjectBinding", item.CanonicalPath, _paths);
            RequireLiteralAbsolutePath(item.CanonicalPath);
            ValidateIdentity(item.Identity, directory: false);
            bool validProtection = item.Protection switch
            {
                NativeObjectProtection.MetadataOnly => item.ContentSha256 is null && !item.Script &&
                    (item.Image is not null || item.PortableExecutable),
                NativeObjectProtection.ContentFrozen => IsHash(item.ContentSha256),
                _ => false,
            };
            if (!validProtection || (item.Script && (item.Image is not null || item.PortableExecutable)) ||
                (item.PortableExecutable && item.Image is not null) ||
                !metadataBoundFiles.Contains((item.CanonicalPath, item.Identity)) ||
                item.Loadable != (item.Image is not null && IsCurrentRuntimeImage(item.Image)))
            {
                throw NativeElfReader.Failure();
            }
            if (item.Image is NativeElfImageBinding image)
            {
                ValidateImage(image);
            }
            if (item.Script)
            {
                diagnostic?.InspectBound("ScriptInterpreter", "/bin/sh", _paths);
                RequireBoundFile("/bin/sh");
                if (!_executableAliases.Contains("/bin/sh"))
                {
                    throw NativeElfReader.Failure();
                }
            }
        }
        diagnostic?.Stage("ValidateFrozenGraph.Cache");
        foreach (NativeLoaderCacheEntry entry in _binding.CacheEntries)
        {
            diagnostic?.InspectBound("CacheCandidate", entry.Path, _paths);
            RequireLiteralAbsolutePath(entry.Path);
            RequireSearchRoot(Path.GetDirectoryName(entry.Path)!);
            if (!_paths.TryGetValue(entry.Path, out NativePathBinding? candidate) || candidate.Directory || !candidate.FreezeMetadata ||
                (candidate.Exists && (!_objects.TryGetValue(candidate.CanonicalPath, out NativeObjectBinding? item) || item.Image is null)))
            {
                throw NativeElfReader.Failure();
            }
        }
        diagnostic?.InspectBound("PreloadPresence", _binding.LoaderLayout.PreloadPath, _paths);
        if (!_paths.TryGetValue(_binding.LoaderLayout.PreloadPath, out NativePathBinding? preload) ||
            preload.Exists != _binding.PreloadExists)
        {
            throw NativeElfReader.Failure();
        }
        diagnostic?.Stage("ValidateFrozenGraph.Executables");
        foreach (string executable in _inputs.ExecutablePaths)
        {
            diagnostic?.InspectBound("Executable", executable, _paths, executable);
            if (!_paths.TryGetValue(executable, out NativePathBinding? path) || !path.Exists || path.Directory ||
                !_objects.TryGetValue(path.CanonicalPath, out NativeObjectBinding? item))
            {
                throw NativeElfReader.Failure();
            }
            ValidateExecutableObject(item);
        }
        diagnostic?.Stage("ValidateFrozenGraph.Gconv");
        foreach (string module in _binding.GconvModulePaths)
        {
            diagnostic?.InspectBound("GconvModule", module, _paths);
            RequireAbsoluteAlias(module);
            RequireBoundFile(module);
        }
        diagnostic?.Stage("ValidateFrozenGraph.Dependencies");
        ValidateDependencies(diagnostic);
    }

    private void RequireSearchRoot(string root)
    {
        if (!_searchRootAliases.Contains(root) || !_paths.TryGetValue(root, out NativePathBinding? path) ||
            (path.Exists && (!_searchRootAliases.Contains(path.CanonicalPath) ||
                !_paths.TryGetValue(path.CanonicalPath, out NativePathBinding? canonical) ||
                !canonical.Directory || !canonical.Exists || !SameNode(path.Identity, canonical.Identity))))
        {
            throw NativeElfReader.Failure();
        }
    }

    private void RequireRecursiveRoot(string root)
    {
        if (!_binding.RecursiveDirectories.Contains(root, StringComparer.Ordinal) || !_paths.ContainsKey(root))
        {
            throw NativeElfReader.Failure();
        }
    }

    private void RequireBoundFile(string path)
    {
        if (!_paths.TryGetValue(path, out NativePathBinding? bound) || bound.Directory || !bound.Exists ||
            !bound.FreezeMetadata || !_objects.TryGetValue(bound.CanonicalPath, out NativeObjectBinding? item) ||
            item.Protection != NativeObjectProtection.ContentFrozen)
        {
            throw NativeElfReader.Failure();
        }
    }

    private void RequireCandidateFile(string path, bool required)
    {
        if (!_paths.TryGetValue(path, out NativePathBinding? bound) || bound.Directory || !bound.FreezeMetadata ||
            (required && !bound.Exists) || (bound.Exists && !_objects.ContainsKey(bound.CanonicalPath)))
        {
            throw NativeElfReader.Failure();
        }
    }

    private static string RequireAliasOriginDirectory(string alias, IReadOnlyDictionary<string, NativePathBinding> paths,
        PreparationDiagnostic? diagnostic = null)
    {
        string parent = Path.GetDirectoryName(alias) ?? throw NativeElfReader.Failure();
        diagnostic?.InspectBound("AliasOriginDirectory", parent, paths, parent);
        if (!paths.TryGetValue(parent, out NativePathBinding? bound) || !bound.Directory || !bound.Exists ||
            !paths.TryGetValue(bound.CanonicalPath, out NativePathBinding? canonical) ||
            !canonical.Directory || !canonical.Exists || !SameNode(bound.Identity, canonical.Identity))
        {
            throw NativeElfReader.Failure();
        }
        return bound.CanonicalPath;
    }

    private void RequireImageAliasPaths(NativePathBinding path, NativeElfImageBinding image, PreparationDiagnostic? diagnostic = null)
    {
        string targetOrigin = RequireAliasOriginDirectory(path.CanonicalPath, _paths, diagnostic);
        string aliasOrigin = RequireAliasOriginDirectory(path.AliasPath, _paths, diagnostic);
        CloseImagePaths(targetOrigin, image, RequireSearchRoot, RequireCandidateFile, diagnostic);
        if (aliasOrigin != targetOrigin)
        {
            CloseImagePaths(aliasOrigin, image, RequireSearchRoot, RequireCandidateFile, diagnostic);
        }
    }

    private void ValidateNssSelection()
    {
        string[] actual = DefaultNssServices;
        if (_binding.LoaderLayout.NssConfigurationPath is string path && _paths[path].Exists)
        {
            actual = ParseNssServices(Builder.ReadConfigurationLines(_paths[path]));
        }
        if (!actual.SequenceEqual(_binding.NssServices, StringComparer.Ordinal))
        {
            throw NativeElfReader.Failure();
        }
    }

    private static string[] ParseNssServices(IEnumerable<string> lines)
    {
        var services = new HashSet<string>(DefaultNssServices, StringComparer.Ordinal);
        var knownServices = KnownNssServices.GetAlternateLookup<ReadOnlySpan<char>>();
        foreach (string source in lines)
        {
            int comment = source.IndexOf('#');
            ReadOnlySpan<char> line = (comment < 0 ? source.AsSpan() : source.AsSpan(0, comment)).Trim();
            if (line.IsEmpty)
            {
                continue;
            }
            int colon = line.IndexOf(':');
            if (colon <= 0 || line[(colon + 1)..].Contains(':'))
            {
                throw NativeElfReader.Failure();
            }
            ReadOnlySpan<char> database = line[..colon].Trim();
            if (database.IsEmpty || database.ContainsAnyExcept("abcdefghijklmnopqrstuvwxyz_"))
            {
                throw NativeElfReader.Failure();
            }
            // Unknown database names (e.g. automount/sudoers) are ignored by
            // glibc, but their service syntax is still checked here, not used
            // as a place to conceal an unsupported native selector.
            ReadOnlySpan<char> list = line[(colon + 1)..];
            int position = 0;
            while (true)
            {
                SkipNssWhitespace(list, ref position);
                if (position == list.Length)
                {
                    break;
                }
                int start = position;
                while (position < list.Length && list[position] is not (' ' or '\t' or '[' or ']'))
                {
                    position++;
                }
                if (!knownServices.TryGetValue(list[start..position], out string? service))
                {
                    throw NativeElfReader.Failure();
                }
                services.Add(service);
                SkipNssWhitespace(list, ref position);
                if (position < list.Length && list[position] == '[')
                {
                    position++;
                    int criteria = 0;
                    while (true)
                    {
                        SkipNssWhitespace(list, ref position);
                        if (position >= list.Length)
                        {
                            throw NativeElfReader.Failure();
                        }
                        if (list[position] == ']')
                        {
                            if (criteria == 0)
                            {
                                throw NativeElfReader.Failure();
                            }
                            position++;
                            break;
                        }
                        if (list[position] == '!')
                        {
                            position++;
                        }
                        ReadOnlySpan<char> status = ReadNssWord(list, ref position);
                        if (!status.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase) &&
                            !status.Equals("NOTFOUND", StringComparison.OrdinalIgnoreCase) &&
                            !status.Equals("UNAVAIL", StringComparison.OrdinalIgnoreCase) &&
                            !status.Equals("TRYAGAIN", StringComparison.OrdinalIgnoreCase))
                        {
                            throw NativeElfReader.Failure();
                        }
                        SkipNssWhitespace(list, ref position);
                        if (position >= list.Length || list[position++] != '=')
                        {
                            throw NativeElfReader.Failure();
                        }
                        SkipNssWhitespace(list, ref position);
                        ReadOnlySpan<char> action = ReadNssWord(list, ref position);
                        if (!action.Equals("return", StringComparison.OrdinalIgnoreCase) &&
                            !action.Equals("continue", StringComparison.OrdinalIgnoreCase) &&
                            !action.Equals("merge", StringComparison.OrdinalIgnoreCase))
                        {
                            throw NativeElfReader.Failure();
                        }
                        criteria++;
                    }
                }
            }
        }
        return services.Order(StringComparer.Ordinal).ToArray();
    }

    // nss_action_parse.c grammar: source [ !? STATUS = ACTION ... ]; only
    // SUCCESS/NOTFOUND/UNAVAIL/TRYAGAIN and return/continue/merge are defined.
    // https://github.com/bminor/glibc/blob/glibc-2.39/nss/nss_action_parse.c
    private static void SkipNssWhitespace(ReadOnlySpan<char> text, ref int position)
    {
        while (position < text.Length && text[position] is ' ' or '\t')
        {
            position++;
        }
    }

    private static ReadOnlySpan<char> ReadNssWord(ReadOnlySpan<char> text, ref int position)
    {
        int start = position;
        while (position < text.Length && text[position] is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
        {
            position++;
        }
        return text[start..position];
    }

    private void ValidateDependencies(PreparationDiagnostic? diagnostic = null)
    {
        var candidates = CollectLibraryCandidates(_paths, _objects, _binding.CacheEntries);
        foreach (string canonical in GetContentFrozenObjects(_inputs.ExecutablePaths, _binding.NssServices, _paths, _objects, candidates, diagnostic))
        {
            diagnostic?.InspectBound("ContentFrozenObject", canonical, _paths);
            RequireContentFrozen(_objects[canonical]);
        }
        HashSet<string> unresolved = GetUnresolvedDependencyNames(_objects.Values, candidates);
        if (unresolved.Count == 0)
        {
            return;
        }
        HashSet<string> directories = GetDependencyCandidateDirectories(_binding.SearchDirectories, _paths);
        foreach (string name in unresolved)
        {
            foreach (string directory in directories)
            {
                string path = Path.Join(directory, name);
                diagnostic?.InspectBound("DependencyCandidate", path, _paths, directory);
                RequireCandidateFile(path, required: false);
            }
        }
        // Existing capability trees also freeze their entry inventories. A new
        // hwcap subdirectory must not introduce an unrecorded optional candidate.
        var inventories = new HashSet<string>(
            _binding.ConfigurationDirectories.Select(static directory => directory.AliasPath), StringComparer.Ordinal);
        HashSet<string> capabilities = GetCapabilityRoots(_binding.SearchDirectories);
        foreach (NativePathBinding path in _binding.Paths)
        {
            diagnostic?.Inspect("CapabilityInventory", path.AliasPath, path.CanonicalPath, path.ExistingAncestor);
            if (path.Directory && path.Exists && IsWithinRoot(path.AliasPath, capabilities) &&
                !inventories.Contains(path.CanonicalPath))
            {
                throw NativeElfReader.Failure();
            }
        }
    }

    private static HashSet<string> GetContentFrozenObjects(
        IReadOnlyList<string> executablePaths, IReadOnlyList<string> nssServices,
        IReadOnlyDictionary<string, NativePathBinding> paths, IReadOnlyDictionary<string, NativeObjectBinding> objects,
        Dictionary<(string Name, byte ElfClass, ushort Machine), HashSet<string>> candidates,
        PreparationDiagnostic? diagnostic = null)
    {
        var frozen = new HashSet<string>(StringComparer.Ordinal);
        var required = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<(string Path, bool Required)>();
        foreach (string executable in executablePaths)
        {
            diagnostic?.InspectBound("ContentRootExecutable", executable, paths, executable);
            EnqueueContentFrozenObject(paths[executable].CanonicalPath, true, frozen, required, pending);
        }
        foreach (NativeObjectBinding item in objects.Values)
        {
            if (item.Protection == NativeObjectProtection.ContentFrozen)
            {
                EnqueueContentFrozenObject(item.CanonicalPath, false, frozen, required, pending);
            }
        }
        foreach (string service in nssServices)
        {
            string name = $"libnss_{service}.so.2";
            if (candidates.TryGetValue((name, 1, 3), out HashSet<string>? x86))
            {
                foreach (string candidate in x86)
                {
                    EnqueueContentFrozenObject(candidate, false, frozen, required, pending);
                }
            }
            if (candidates.TryGetValue((name, 2, 62), out HashSet<string>? x64))
            {
                foreach (string candidate in x64)
                {
                    EnqueueContentFrozenObject(candidate, false, frozen, required, pending);
                }
            }
        }
        // Derive promotions from the original parsed graph. Follow every compatible
        // candidate, not a guessed loader-order winner. Optional module dependencies
        // retain absence anchors; only executable-reachable edges must resolve.
        while (pending.TryDequeue(out var next))
        {
            diagnostic?.InspectBound("ContentDependencyObject", next.Path, paths);
            if (!next.Required && required.Contains(next.Path))
            {
                continue;
            }
            NativeObjectBinding item = objects[next.Path];
            if (item.Image is not NativeElfImageBinding image)
            {
                continue;
            }
            if (image.Interpreter is string interpreter)
            {
                diagnostic?.InspectBound("ContentDependencyInterpreter", interpreter, paths);
                EnqueueContentFrozenObject(paths[interpreter].CanonicalPath, next.Required, frozen, required, pending);
            }
            HashSet<string>? origins = null;
            foreach (string name in image.Needed)
            {
                diagnostic?.InspectBound("DependencyConsumer", next.Path, paths);
                if (!name.Contains('/') && !name.Contains('$'))
                {
                    if (!candidates.TryGetValue((name, image.ElfClass, image.Machine), out HashSet<string>? matches))
                    {
                        if (next.Required)
                        {
                            throw NativeElfReader.Failure();
                        }
                        continue;
                    }
                    foreach (string candidate in matches)
                    {
                        EnqueueContentFrozenObject(candidate, next.Required, frozen, required, pending);
                    }
                }
                else if (name.StartsWith("$ORIGIN", StringComparison.Ordinal))
                {
                    if (origins is null)
                    {
                        origins = new HashSet<string>(StringComparer.Ordinal);
                        foreach (NativePathBinding alias in paths.Values)
                        {
                            if (alias.Exists && !alias.Directory && alias.CanonicalPath == next.Path)
                            {
                                origins.Add(RequireAliasOriginDirectory(alias.AliasPath, paths, diagnostic));
                            }
                        }
                    }
                    foreach (string origin in origins)
                    {
                        FollowNeededCandidate(ExpandOriginPath(origin, name), image, next.Required, paths, objects,
                            frozen, required, pending, diagnostic);
                    }
                }
                else
                {
                    FollowNeededCandidate(name, image, next.Required, paths, objects, frozen, required, pending, diagnostic);
                }
            }
        }
        return frozen;
    }

    private static void FollowNeededCandidate(string alias, NativeElfImageBinding consumer, bool requireExistence,
        IReadOnlyDictionary<string, NativePathBinding> paths, IReadOnlyDictionary<string, NativeObjectBinding> objects,
        HashSet<string> frozen, HashSet<string> required, Queue<(string Path, bool Required)> pending,
        PreparationDiagnostic? diagnostic = null)
    {
        diagnostic?.InspectBound("NeededCandidate", alias, paths);
        if (!paths.TryGetValue(alias, out NativePathBinding? path) || !path.Exists || path.Directory ||
            !objects.TryGetValue(path.CanonicalPath, out NativeObjectBinding? item))
        {
            if (requireExistence)
            {
                throw NativeElfReader.Failure();
            }
            return;
        }
        if (requireExistence && (!item.Loadable || item.Image is not NativeElfImageBinding image || image.ObjectType != 3 ||
            image.ElfClass != consumer.ElfClass || image.Machine != consumer.Machine))
        {
            throw NativeElfReader.Failure();
        }
        EnqueueContentFrozenObject(item.CanonicalPath, requireExistence, frozen, required, pending);
    }

    private static void EnqueueContentFrozenObject(string canonical, bool requireExistence,
        HashSet<string> frozen, HashSet<string> required, Queue<(string Path, bool Required)> pending)
    {
        bool added = frozen.Add(canonical);
        if (requireExistence ? required.Add(canonical) : added)
        {
            pending.Enqueue((canonical, requireExistence));
        }
    }

    private static Dictionary<(string Name, byte ElfClass, ushort Machine), HashSet<string>> CollectLibraryCandidates(
        IReadOnlyDictionary<string, NativePathBinding> paths,
        IReadOnlyDictionary<string, NativeObjectBinding> objects,
        IReadOnlyList<NativeLoaderCacheEntry> cache)
    {
        var candidates = new Dictionary<(string Name, byte ElfClass, ushort Machine), HashSet<string>>();
        foreach (NativePathBinding path in paths.Values)
        {
            if (path.Exists && !path.Directory && objects.TryGetValue(path.CanonicalPath, out NativeObjectBinding? item))
            {
                AddLibraryCandidate(candidates, Path.GetFileName(path.AliasPath), item);
            }
        }
        foreach (NativeLoaderCacheEntry entry in cache)
        {
            if (paths.TryGetValue(entry.Path, out NativePathBinding? path) && path.Exists &&
                objects.TryGetValue(path.CanonicalPath, out NativeObjectBinding? item))
            {
                AddLibraryCandidate(candidates, entry.Name, item);
            }
        }
        return candidates;
    }

    private static void AddLibraryCandidate(
        Dictionary<(string Name, byte ElfClass, ushort Machine), HashSet<string>> candidates,
        string name, NativeObjectBinding item)
    {
        if (!item.Loadable || item.Image is not NativeElfImageBinding image || image.ObjectType != 3)
        {
            return;
        }
        var key = (name, image.ElfClass, image.Machine);
        if (!candidates.TryGetValue(key, out HashSet<string>? matches))
        {
            matches = new HashSet<string>(StringComparer.Ordinal);
            candidates.Add(key, matches);
        }
        matches.Add(item.CanonicalPath);
    }

    private static HashSet<string> GetUnresolvedDependencyNames(IEnumerable<NativeObjectBinding> objects,
        Dictionary<(string Name, byte ElfClass, ushort Machine), HashSet<string>> candidates)
    {
        var unresolved = new HashSet<string>(StringComparer.Ordinal);
        foreach (NativeObjectBinding item in objects)
        {
            if (item.Loadable && item.Image is NativeElfImageBinding image)
            {
                foreach (string name in image.Needed)
                {
                    if (!name.Contains('/') && !name.Contains('$') &&
                        !candidates.ContainsKey((name, image.ElfClass, image.Machine)))
                    {
                        unresolved.Add(name);
                    }
                }
            }
        }
        return unresolved;
    }

    private static HashSet<string> GetCapabilityRoots(IEnumerable<string> searchDirectories)
    {
        var capabilities = new HashSet<string>(StringComparer.Ordinal);
        foreach (string directory in searchDirectories)
        {
            foreach (string name in HardwareCapabilityTreeNames)
            {
                capabilities.Add(Path.Join(directory, name));
            }
        }
        return capabilities;
    }

    private static bool IsWithinRoot(string alias, HashSet<string> roots)
    {
        var lookup = roots.GetAlternateLookup<ReadOnlySpan<char>>();
        ReadOnlySpan<char> ancestor = alias;
        while (!ancestor.IsEmpty)
        {
            if (lookup.Contains(ancestor))
            {
                return true;
            }
            int slash = ancestor.LastIndexOf('/');
            if (slash <= 0)
            {
                break;
            }
            ancestor = ancestor[..slash];
        }
        return false;
    }

    private static HashSet<string> GetDependencyCandidateDirectories(IEnumerable<string> searchDirectories,
        IReadOnlyDictionary<string, NativePathBinding> paths)
    {
        var directories = new HashSet<string>(searchDirectories, StringComparer.Ordinal);
        HashSet<string> capabilities = GetCapabilityRoots(directories);
        directories.UnionWith(capabilities);
        foreach (NativePathBinding path in paths.Values)
        {
            if (path.Directory && IsWithinRoot(path.AliasPath, capabilities))
            {
                directories.Add(path.AliasPath);
            }
        }
        return directories;
    }

    private static void RequireContentFrozen(NativeObjectBinding item)
    {
        if (item.Protection != NativeObjectProtection.ContentFrozen || !IsHash(item.ContentSha256))
        {
            throw NativeElfReader.Failure();
        }
    }

    private static void ValidateExecutableObject(NativeObjectBinding item)
    {
        RequireContentFrozen(item);
        if (item.Script)
        {
            return;
        }
        if (!item.Loadable || item.PortableExecutable || item.Image is not NativeElfImageBinding image || image.ObjectType is not (2 or 3) ||
            (image.Interpreter is null && (image.ObjectType != 2 || image.HasDynamicSegment || image.Needed.Length != 0)))
        {
            // ET_DYN without PT_INTERP is a valid module, not a directly
            // executable root. An interpreter-less root must really be static.
            throw NativeElfReader.Failure();
        }
    }
    private static bool IsCurrentRuntimeImage(NativeElfImageBinding image) =>
        image.HasSupportedAbi && image.DataEncoding == 1 && image.OsAbi is 0 or 3 &&
        ((image.ElfClass == 2 && image.Machine == 62) || (image.ElfClass == 1 && image.Machine == 3)) &&
        image.ObjectType is 2 or 3;


    private static void ValidateImage(NativeElfImageBinding image)
    {
        if (image.DataEncoding != 1 || image.ElfClass is not (1 or 2) ||
            image.ObjectType is not (1 or 2 or 3 or 4) || image.Needed is null || image.RPath is null || image.RunPath is null ||
            image.Needed.Length > NativeElfReader.MaximumDynamicEntries || image.RPath.Length > NativeElfReader.MaximumDynamicEntries ||
            image.RunPath.Length > NativeElfReader.MaximumDynamicEntries)
        {
            throw NativeElfReader.Failure();
        }
        if (image.Soname is not null)
        {
            RequireBareName(image.Soname);
        }
        foreach (string needed in image.Needed)
        {
            if (!needed.Contains('/') && !needed.Contains('$'))
            {
                RequireBareName(needed);
            }
        }
    }

    private static void CloseImagePaths(string originDirectory, NativeElfImageBinding image,
        Action<string> addRoot, Action<string, bool> addFile, PreparationDiagnostic? diagnostic = null)
    {
        if (image.Interpreter is not null)
        {
            RequireLiteralAbsolutePath(image.Interpreter);
            diagnostic?.Inspect("ImageInterpreter", image.Interpreter, root: originDirectory);
            string name = Path.GetFileName(image.Interpreter);
            if (IsCurrentRuntimeImage(image) && ((image.Machine == 62 && name != "ld-linux-x86-64.so.2") ||
                (image.Machine == 3 && name != "ld-linux.so.2")))
            {
                throw NativeElfReader.Failure();
            }
            addFile(image.Interpreter, true);
        }
        foreach (string component in image.RPath.Concat(image.RunPath))
        {
            // NativeElfReader already splits path tags on ':', retaining empty
            // components. Empty components select cwd and are unsupported.
            string path = ExpandOriginPath(originDirectory, component);
            diagnostic?.Inspect("ImageSearchRoot", path, root: originDirectory);
            addRoot(path);
        }
        foreach (string needed in image.Needed)
        {
            if (needed.Contains('/') || needed.Contains('$'))
            {
                string path = ExpandOriginPath(originDirectory, needed);
                string directory = Path.GetDirectoryName(path)!;
                diagnostic?.Inspect("ImageDependencyRoot", directory, root: originDirectory);
                addRoot(directory);
                diagnostic?.Inspect("ImageDependency", path, root: originDirectory);
                addFile(path, false);
            }
        }
    }

    internal static string ExpandObjectPath(string canonicalObject, string path)
    {
        RequireLiteralAbsolutePath(canonicalObject);
        return ExpandOriginPath(Path.GetDirectoryName(canonicalObject) ?? throw NativeElfReader.Failure(), path);
    }

    private static string ExpandOriginPath(string originDirectory, string path)
    {
        RequireLiteralAbsolutePath(originDirectory);
        if (string.IsNullOrEmpty(path))
        {
            throw NativeElfReader.Failure();
        }
        if (path == "$ORIGIN")
        {
            return originDirectory;
        }
        if (path.StartsWith("$ORIGIN/", StringComparison.Ordinal))
        {
            string suffix = path[8..];
            RequireLiteralComponents(suffix, allowDotSegments: true);
            // Keep the literal suffix. The protected descriptor bridge resolves
            // links before '.'/'..'; Path.GetFullPath would change that meaning.
            string result = Path.Join(originDirectory, suffix);
            RequireAbsoluteAlias(result);
            return result;
        }
        // No ${ORIGIN}, $LIB, $PLATFORM, unknown token, relative path or cwd.
        // Dot components are admitted only in a literal $ORIGIN suffix.
        RequireLiteralAbsolutePath(path);
        return path;
    }

    private static void RevalidatePath(NativePathBinding path)
    {
        var current = WineXeBuildToolchainResolver.GetProtectedNativeOptionalPath(path.AliasPath, path.Directory);
        if (current.Identity.HasValue != path.Exists || current.CanonicalPath != path.CanonicalPath ||
            current.ExistingAncestor != path.ExistingAncestor || !SameNode(current.AncestorIdentity, path.AncestorIdentity))
        {
            throw NativeElfReader.Failure();
        }
        if (path.Exists && (path.FreezeMetadata ? current.Identity!.Value != path.Identity : !SameNode(current.Identity!.Value, path.Identity)))
        {
            throw NativeElfReader.Failure();
        }
    }

    private static bool SameNode(NativeFileIdentity left, NativeFileIdentity right) =>
        left.DeviceMajor == right.DeviceMajor && left.DeviceMinor == right.DeviceMinor && left.Inode == right.Inode &&
        left.OwnerUserId == right.OwnerUserId && left.Mode == right.Mode;

    private static bool EquivalentPath(NativePathBinding left, NativePathBinding right) =>
        left.AliasPath == right.AliasPath && left.Directory == right.Directory && left.Exists == right.Exists &&
        left.CanonicalPath == right.CanonicalPath && left.ExistingAncestor == right.ExistingAncestor &&
        left.FreezeMetadata == right.FreezeMetadata && SameNode(left.AncestorIdentity, right.AncestorIdentity) &&
        (left.FreezeMetadata ? left.Identity == right.Identity : SameNode(left.Identity, right.Identity));

    private static bool RetainsObject(NativeObjectBinding original, NativeObjectBinding retained) =>
        original.CanonicalPath == retained.CanonicalPath && original.Identity == retained.Identity &&
        (original.Protection == NativeObjectProtection.MetadataOnly ||
         (original.Protection == retained.Protection && original.ContentSha256 == retained.ContentSha256)) &&
        original.Script == retained.Script && original.PortableExecutable == retained.PortableExecutable &&
        original.Loadable == retained.Loadable && EquivalentImage(original.Image, retained.Image);

    private static bool EquivalentImage(NativeElfImageBinding? left, NativeElfImage? right) =>
        left is null ? right is null : right is not null && left.ObjectType == right.ObjectType && left.Machine == right.Machine &&
        left.ElfClass == right.ElfClass && left.DataEncoding == right.DataEncoding && left.OsAbi == right.OsAbi &&
        left.Interpreter == right.Interpreter && left.Soname == right.Soname && left.Flags == right.Flags && left.Flags1 == right.Flags1 &&
        left.HasDynamicSegment == right.HasDynamicSegment && left.HasSupportedAbi == right.HasSupportedAbi &&
        left.AbiVersion == right.AbiVersion && left.Needed.SequenceEqual(right.Needed, StringComparer.Ordinal) &&
        left.RPath.SequenceEqual(right.RPath, StringComparer.Ordinal) && left.RunPath.SequenceEqual(right.RunPath, StringComparer.Ordinal);

    private static bool EquivalentImage(NativeElfImageBinding? left, NativeElfImageBinding? right) =>
        left is null ? right is null : right is not null && left.ObjectType == right.ObjectType && left.Machine == right.Machine &&
        left.ElfClass == right.ElfClass && left.DataEncoding == right.DataEncoding && left.OsAbi == right.OsAbi &&
        left.Interpreter == right.Interpreter && left.Soname == right.Soname && left.Flags == right.Flags && left.Flags1 == right.Flags1 &&
        left.HasDynamicSegment == right.HasDynamicSegment && left.HasSupportedAbi == right.HasSupportedAbi &&
        left.AbiVersion == right.AbiVersion && left.Needed.SequenceEqual(right.Needed, StringComparer.Ordinal) &&
        left.RPath.SequenceEqual(right.RPath, StringComparer.Ordinal) && left.RunPath.SequenceEqual(right.RunPath, StringComparer.Ordinal);

    private static NativeElfImageBinding CopyImage(NativeElfImage image) => new(image.ObjectType, image.Machine, image.ElfClass,
        image.DataEncoding, image.OsAbi, image.Interpreter, image.Needed.ToArray(), image.RPath.ToArray(), image.RunPath.ToArray(),
        image.Soname, image.Flags, image.Flags1, image.HasDynamicSegment, image.HasSupportedAbi, image.AbiVersion);

    private static NativeElfImageBinding? CopyImage(NativeElfImageBinding? image) => image is null ? null : image with
    {
        Needed = image.Needed.ToArray(), RPath = image.RPath.ToArray(), RunPath = image.RunPath.ToArray(),
    };

    private static void ValidateIdentity(NativeFileIdentity identity, bool directory)
    {
        if (identity.Inode == 0 || (identity.Mode & TypeMask) != (directory ? DirectoryType : RegularFileType) ||
            (identity.Mode & 0x12) != 0 || identity.ChangeNanoseconds >= 1_000_000_000 || identity.ModificationNanoseconds >= 1_000_000_000)
        {
            throw NativeElfReader.Failure();
        }
    }

    internal static bool IsAllowedEnvironmentBinding(string name, string value)
    {
        if (string.IsNullOrEmpty(name) || value is null || value.Length > 4096 || value.Contains('\0') || ContainsControl(value))
        {
            return false;
        }
        return name switch
        {
            "PATH" or "HOME" or "WINEPREFIX" or "XDG_CONFIG_HOME" or "XDG_DATA_HOME" or "XDG_CACHE_HOME" or
            "XDG_RUNTIME_DIR" or "XDG_CONFIG_DIRS" or "XDG_DATA_DIRS" or "GIO_MODULE_DIR" or "GSETTINGS_SCHEMA_DIR" or
            "OPENSSL_MODULES" or "OPENSSL_ENGINES" => value.StartsWith('/') && !value.Contains(':') && !value.Contains('$'),
            "LANG" or "LC_ALL" => value == "C",
            "USER" => value.Length is > 0 and <= 64 && value is not ("." or "..") &&
                !value.AsSpan().ContainsAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-."),
            "TZ" => value == "UTC",
            "WINEDEBUG" => value == "-all",
            "WINEDLLOVERRIDES" => value == HeadlessWineOverrides,
            _ => false,
        };
    }

    private static void ValidateEnvironment(IReadOnlyDictionary<string, string> environment,
        IReadOnlyDictionary<string, NativePathBinding> paths, PreparationDiagnostic? diagnostic = null)
    {
        foreach (string required in new[] { "HOME", "PATH", "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME" })
        {
            if (!environment.ContainsKey(required))
            {
                throw NativeElfReader.Failure();
            }
        }
        string privateModules = Path.Join(environment["HOME"], "native-modules");
        foreach ((string name, string value) in environment)
        {
            if (!IsAllowedEnvironmentBinding(name, value))
            {
                throw NativeElfReader.Failure();
            }
            if (value.StartsWith('/'))
            {
                diagnostic?.InspectBound("ValidateEnvironmentDirectory", value, paths, value);
                RequireLiteralAbsolutePath(value);
                if (!paths.TryGetValue(value, out NativePathBinding? path) || !path.Directory || !path.Exists ||
                    (path.Identity.Mode & GroupOtherPermissions) != 0)
                {
                    throw NativeElfReader.Failure();
                }
                if ((name is "GIO_MODULE_DIR" or "GSETTINGS_SCHEMA_DIR" or "OPENSSL_MODULES" or "OPENSSL_ENGINES") && value != privateModules)
                {
                    throw NativeElfReader.Failure();
                }
            }
        }
        diagnostic?.InspectBound("ValidatePrivateModules", privateModules, paths, privateModules);
        if (!paths.TryGetValue(privateModules, out NativePathBinding? modules) || !modules.Directory)
        {
            throw NativeElfReader.Failure();
        }
        string openSslConfiguration = Path.Join(environment["HOME"], "native-openssl.cnf");
        diagnostic?.InspectBound("ValidatePrivateOpenSslConfiguration", openSslConfiguration, paths);
        if (!paths.TryGetValue(openSslConfiguration, out NativePathBinding? openssl) || !openssl.Exists || openssl.Directory ||
            !openssl.FreezeMetadata || openssl.Identity.Size != 0)
        {
            throw NativeElfReader.Failure();
        }
    }

    internal static void RequireAbsoluteAlias(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 4096 || !path.StartsWith('/') || path.Contains('\0') ||
            path.Contains('$') || ContainsControl(path))
        {
            throw NativeElfReader.Failure();
        }
    }

    private static void RequireLiteralAbsolutePath(string path, bool allowDotSegments = false)
    {
        RequireAbsoluteAlias(path);
        if (path != "/")
        {
            RequireLiteralComponents(path.AsSpan(1), allowDotSegments);
        }
    }

    private static void RequireLiteralComponents(ReadOnlySpan<char> path, bool allowDotSegments = false)
    {
        if (path.IsEmpty || path.Contains('$') || path.Contains(':') || ContainsControl(path))
        {
            throw NativeElfReader.Failure();
        }
        while (true)
        {
            int slash = path.IndexOf('/');
            ReadOnlySpan<char> component = slash < 0 ? path : path[..slash];
            if (component.IsEmpty || (!allowDotSegments && (component.SequenceEqual(".") || component.SequenceEqual(".."))))
            {
                throw NativeElfReader.Failure();
            }
            if (slash < 0)
            {
                return;
            }
            path = path[(slash + 1)..];
        }
    }

    private static bool ContainsControl(ReadOnlySpan<char> value)
    {
        foreach (char character in value)
        {
            if (char.IsControl(character))
            {
                return true;
            }
        }
        return false;
    }

    private static void RequireBareName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 4096 || name is "." or ".." || name.Contains('/') || name.Contains('$') ||
            name.Contains(':') || ContainsControl(name))
        {
            throw NativeElfReader.Failure();
        }
    }

    private static bool IsBelow(string path, string root) => root == "/" || path == root ||
        (path.StartsWith(root, StringComparison.Ordinal) && path.Length > root.Length && path[root.Length] == '/');

    private static bool IsSubset(IReadOnlyList<string> required, IReadOnlyList<string> frozen) =>
        required.All(path => frozen.Contains(path, StringComparer.Ordinal));

    private static bool IsSortedUnique(IReadOnlyList<string> values) => IsSortedUnique(values, static value => value);

    private static bool IsSortedUnique<T>(IReadOnlyList<T> values, Func<T, string> selector)
    {
        for (int index = 1; index < values.Count; index++)
        {
            if (string.CompareOrdinal(selector(values[index - 1]), selector(values[index])) >= 0)
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsHash(string? value) => value is not null && value.Length == 64 &&
        !value.AsSpan().ContainsAnyExcept("0123456789ABCDEF");

    private static string HashFile(Stream stream)
    {
        stream.Position = 0;
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool HasPortableExecutableMagic(Stream stream)
    {
        if (stream.Length < 2)
        {
            return false;
        }
        Span<byte> magic = stackalloc byte[2];
        stream.Position = 0;
        stream.ReadExactly(magic);
        return magic.SequenceEqual("MZ"u8);
    }

    private static string HashMetadata<T>(T value)
    {
        using var stream = new HashingStream(MaximumBindingBytes);
        JsonSerializer.Serialize(stream, value);
        return stream.Finish();
    }

    private sealed class HashingStream(int maximumBytes) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _bytes;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _bytes;
        public override long Position { get => _bytes; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if ((_bytes += buffer.Length) > maximumBytes)
            {
                throw NativeElfReader.Failure();
            }
            _hash.AppendData(buffer);
        }
        internal string Finish() => Convert.ToHexString(_hash.GetHashAndReset());
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hash.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private static NativeDependencyClosureSpec CopyInputs(NativeDependencyClosureSpec inputs) =>
        new(inputs.ExecutablePaths, inputs.ModuleDirectories, inputs.ConfigurationFiles, inputs.EnvironmentBindings)
        {
            PrefixPolicy = inputs.PrefixPolicy,
        };

    private sealed record NativeInputsProof(NativeDependencyClosureSpecBinding Inputs, WineNativePrefixPolicyBinding? PrefixPolicy);

    private static string HashInputs(NativeDependencyClosureSpecBinding inputs, WineNativePrefixPolicyBinding? prefix) =>
        HashMetadata(new NativeInputsProof(inputs, prefix));

    private static NativeDependencyClosureSpecBinding BindInputs(NativeDependencyClosureSpec inputs) => new(
        inputs.ExecutablePaths.ToArray(), inputs.ModuleDirectories.ToArray(), inputs.ConfigurationFiles.ToArray(),
        inputs.EnvironmentBindings.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal));

    private static NativeLoaderFileSystemLayout CopyLayout(NativeLoaderFileSystemLayout layout)
    {
        if (layout is null || layout.DefaultDirectories is null || layout.GconvDirectories is null ||
            layout.DefaultDirectories.Count > MaximumRoots || layout.GconvDirectories.Count > MaximumRoots)
        {
            throw NativeElfReader.Failure();
        }
        foreach (string path in layout.DefaultDirectories.Concat(layout.GconvDirectories)
            .Append(layout.CachePath).Append(layout.PreloadPath).Append(layout.ConfigurationPath))
        {
            RequireLiteralAbsolutePath(path);
        }
        if (layout.NssConfigurationPath is not null)
        {
            RequireLiteralAbsolutePath(layout.NssConfigurationPath);
        }
        return layout with
        {
            DefaultDirectories = Array.AsReadOnly(layout.DefaultDirectories.ToArray()),
            GconvDirectories = Array.AsReadOnly(layout.GconvDirectories.ToArray()),
        };
    }

    private static bool LayoutEquals(NativeLoaderFileSystemLayout left, NativeLoaderFileSystemLayout right) =>
        left is not null && right is not null && left.CachePath == right.CachePath && left.PreloadPath == right.PreloadPath &&
        left.ConfigurationPath == right.ConfigurationPath && left.NssConfigurationPath == right.NssConfigurationPath &&
        left.DefaultDirectories is not null && left.GconvDirectories is not null &&
        left.DefaultDirectories.SequenceEqual(right.DefaultDirectories, StringComparer.Ordinal) &&
        left.GconvDirectories.SequenceEqual(right.GconvDirectories, StringComparer.Ordinal);

    private static NativeDependencyClosureBinding CopyBinding(NativeDependencyClosureBinding binding) => binding with
    {
        Inputs = new NativeDependencyClosureSpecBinding(binding.Inputs.ExecutablePaths.ToArray(), binding.Inputs.ModuleDirectories.ToArray(),
            binding.Inputs.ConfigurationFiles.ToArray(), new Dictionary<string, string>(binding.Inputs.EnvironmentBindings, StringComparer.Ordinal)),
        LoaderLayout = CopyLayout(binding.LoaderLayout), Paths = binding.Paths.ToArray(), SearchDirectories = binding.SearchDirectories.ToArray(),
        RecursiveDirectories = binding.RecursiveDirectories.ToArray(),
        Objects = binding.Objects.Select(static item => item with { Image = CopyImage(item.Image) }).ToArray(),
        ConfigurationDirectories = binding.ConfigurationDirectories.Select(static directory => directory with { EntryNames = directory.EntryNames.ToArray() }).ToArray(),
        CacheEntries = binding.CacheEntries.ToArray(), HardwareCapabilityNames = binding.HardwareCapabilityNames.ToArray(),
        NssServices = binding.NssServices.ToArray(),
        GconvModulePaths = binding.GconvModulePaths.ToArray(),
        PrefixPolicy = CopyPrefixBinding(binding.PrefixPolicy),
    };

    private static WineNativePrefixPolicyBinding? CopyPrefixBinding(WineNativePrefixPolicyBinding? prefix) => prefix is null ? null : prefix with
    {
        ModuleDirectories = prefix.ModuleDirectories.ToArray(),
        Mappings = prefix.Mappings.ToArray(), RegistryAliases = prefix.RegistryAliases.ToArray(), Directories = prefix.Directories.ToArray(),
    };

    private static void ValidateBindingBounds(NativeDependencyClosureBinding binding)
    {
        if (binding is null || binding.Inputs is null || binding.Paths is null || binding.Objects is null ||
            binding.SearchDirectories is null || binding.RecursiveDirectories is null ||
            binding.ConfigurationDirectories is null || binding.CacheEntries is null ||
            binding.HardwareCapabilityNames is null || binding.NssServices is null || binding.NssServices.Length > KnownNssServices.Count ||
            !IsSortedUnique(binding.NssServices) || binding.NssServices.Any(static name => !KnownNssServices.Contains(name)) ||
            binding.GconvModulePaths is null || binding.GconvModulePaths.Length > MaximumPaths || !IsSortedUnique(binding.GconvModulePaths) ||
            binding.Paths.Length > MaximumPaths || binding.Objects.Length > MaximumObjects ||
            binding.SearchDirectories.Length + binding.RecursiveDirectories.Length > MaximumRoots ||
            binding.CacheEntries.Length > NativeLoaderCacheReader.MaximumEntries * 2 ||
            binding.HardwareCapabilityNames.Length > 4096 || binding.ConfigurationDirectories.Length > MaximumRoots ||
            binding.Inputs.ExecutablePaths is null || binding.Inputs.ExecutablePaths.Length is < 1 or > MaximumPaths ||
            binding.Inputs.ModuleDirectories is null || binding.Inputs.ModuleDirectories.Length > MaximumPaths ||
            binding.Inputs.ConfigurationFiles is null || binding.Inputs.ConfigurationFiles.Length > MaximumPaths ||
            binding.Inputs.EnvironmentBindings is null || binding.Inputs.EnvironmentBindings.Count > 32 ||
            !IsSortedUnique(binding.SearchDirectories) || !IsSortedUnique(binding.RecursiveDirectories) ||
            !IsSortedUnique(binding.Paths, static path => path.AliasPath) ||
            !IsSortedUnique(binding.Objects, static item => item.CanonicalPath))
        {
            throw NativeElfReader.Failure();
        }
        _ = CopyLayout(binding.LoaderLayout);
        foreach (NativeObjectBinding item in binding.Objects)
        {
            if (item is null || item.Image is NativeElfImageBinding image &&
                (image.Needed is null || image.RPath is null || image.RunPath is null ||
                 image.Needed.Length > NativeElfReader.MaximumDynamicEntries || image.RPath.Length > NativeElfReader.MaximumDynamicEntries ||
                 image.RunPath.Length > NativeElfReader.MaximumDynamicEntries))
            {
                throw NativeElfReader.Failure();
            }
        }
        foreach (NativeConfigurationDirectoryBinding directory in binding.ConfigurationDirectories)
        {
            if (directory is null || directory.EntryNames is null || directory.EntryNames.Length > MaximumPaths || !IsSortedUnique(directory.EntryNames))
            {
                throw NativeElfReader.Failure();
            }
        }
    }

    private static string[] ReadEntryNames(string protectedDirectory, PreparationDiagnostic? diagnostic = null)
    {
        diagnostic?.Inspect("ResolveDirectoryInventory", protectedDirectory, root: protectedDirectory);
        string canonical = WineXeBuildToolchainResolver.GetProtectedCanonicalPath(protectedDirectory, true, diagnostic?.OriginObserver);
        diagnostic?.Inspect("EnumerateDirectoryInventory", protectedDirectory, canonical, protectedDirectory);
        var result = new List<string>();
        long characters = 0;
        foreach (string entry in Directory.EnumerateFileSystemEntries(canonical))
        {
            diagnostic?.Inspect("DirectoryInventoryEntry", entry, root: protectedDirectory);
            string name = Path.GetFileName(entry);
            RequireBareName(name);
            if (result.Count >= MaximumPaths || (characters += name.Length) > MaximumMetadataCharacters)
            {
                throw NativeElfReader.Failure();
            }
            result.Add(name);
        }
        result.Sort(StringComparer.Ordinal);
        return result.ToArray();
    }

    private sealed class Builder(NativeDependencyClosureSpec inputs, NativeLoaderFileSystemLayout layout,
        PreparationDiagnostic? diagnostic = null)
    {
        private readonly Dictionary<string, NativePathBinding> _paths = new(StringComparer.Ordinal);
        private readonly Dictionary<string, NativeObjectBinding> _objects = new(StringComparer.Ordinal);
        private readonly HashSet<string> _searchDirectories = new(StringComparer.Ordinal);
        private readonly HashSet<string> _recursiveDirectories = new(StringComparer.Ordinal);
        private readonly HashSet<(uint Major, uint Minor, ulong Inode)> _visitedDirectories = [];
        private readonly HashSet<(uint Major, uint Minor, ulong Inode)> _visitedRecursiveDirectories = [];
        private readonly HashSet<(uint Major, uint Minor, ulong Inode)> _visitedCapabilityDirectories = [];
        private readonly HashSet<string> _capabilityRoots = new(StringComparer.Ordinal);
        private readonly Dictionary<(uint Major, uint Minor, ulong Inode),
            List<(string Path, FileAttributes Attributes)>> _directoryEntries = [];
        private readonly Dictionary<string, NativeConfigurationDirectoryBinding> _configurationDirectories = new(StringComparer.Ordinal);
        private readonly HashSet<string> _parsedConfigurations = new(StringComparer.Ordinal);
        private readonly Queue<(string Path, bool Recursive, bool Capability)> _pendingDirectories = [];
        private readonly Queue<(string Path, bool Executable, bool Required, bool FreezeContent)> _pendingFiles = [];
        private readonly HashSet<string> _processedImageAliases = new(StringComparer.Ordinal);
        private NativeLoaderCacheEntry[] _cacheEntries = [];
        private string[] _hardwareNames = [];
        private string[] _nssServices = DefaultNssServices;
        private readonly HashSet<string> _gconvModulePaths = new(StringComparer.Ordinal);
        private int _entries;
        private long _metadataCharacters;
        private bool _preloadExists;

        internal NativeDependencyClosureBinding Build()
        {
            diagnostic?.Stage("Builder.SearchRoots");
            foreach (string root in layout.DefaultDirectories)
            {
                AddDirectory(root);
            }
            diagnostic?.Stage("Builder.RecursiveRoots");
            foreach (string root in layout.GconvDirectories.Concat(inputs.ModuleDirectories))
            {
                AddRecursiveDirectory(root);
            }
            diagnostic?.Stage("Builder.ConfigurationRoots");
            foreach (string path in inputs.ConfigurationFiles)
            {
                diagnostic?.Inspect("ConfigurationRoot", path, root: path);
                ProtectConfiguration(path);
            }
            foreach (string executable in inputs.ExecutablePaths)
            {
                _pendingFiles.Enqueue((executable, true, true, true));
            }
            PrepareEnvironment();
            ReadCache();
            ReadPreload();
            ParseLoaderConfiguration(layout.ConfigurationPath, 0);
            if (layout.NssConfigurationPath is string nssPath)
            {
                diagnostic?.Stage("Builder.NssConfiguration", nssPath);
                NativePathBinding nss = ProtectConfiguration(nssPath);
                if (nss.Exists)
                {
                    _nssServices = ParseNssServices(ReadConfigurationLines(nss, diagnostic));
                }
            }
            foreach (string root in layout.GconvDirectories)
            {
                ParseGconvDirectory(root);
            }
            DrainGraph();
            FreezeOptionalDependencyCandidates();
            FreezeContentDependencies();
            diagnostic?.Stage("Builder.FreezeBinding");
            NativeDependencyClosureSpecBinding inputBinding = BindInputs(inputs);
            var binding = new NativeDependencyClosureBinding
            {
                Version = 2, Inputs = inputBinding, LoaderLayout = layout,
                InputsSha256 = HashInputs(inputBinding, inputs.PrefixPolicy?.ToBinding()),
                ManifestSha256 = string.Empty, Paths = _paths.Values.OrderBy(static path => path.AliasPath, StringComparer.Ordinal).ToArray(),
                SearchDirectories = _searchDirectories.Order(StringComparer.Ordinal).ToArray(),
                RecursiveDirectories = _recursiveDirectories.Order(StringComparer.Ordinal).ToArray(),
                Objects = _objects.Values.OrderBy(static item => item.CanonicalPath, StringComparer.Ordinal).ToArray(),
                ConfigurationDirectories = _configurationDirectories.Values.OrderBy(static item => item.AliasPath, StringComparer.Ordinal).ToArray(),
                CacheEntries = _cacheEntries, HardwareCapabilityNames = _hardwareNames, PreloadExists = _preloadExists,
                NssServices = _nssServices,
                GconvModulePaths = _gconvModulePaths.Order(StringComparer.Ordinal).ToArray(),
                PrefixPolicy = inputs.PrefixPolicy?.ToBinding(),
            };
            return binding with { ManifestSha256 = HashMetadata(binding) };
        }

        private void PrepareEnvironment()
        {
            diagnostic?.Stage("Builder.PrepareEnvironment");
            foreach ((string _, string value) in inputs.EnvironmentBindings)
            {
                if (value.StartsWith('/'))
                {
                    diagnostic?.Inspect("EnvironmentDirectory", value, root: value);
                    BindPath(value, directory: true, freezeMetadata: false);
                }
            }
            if (!inputs.EnvironmentBindings.TryGetValue("HOME", out string? home) ||
                !inputs.EnvironmentBindings.TryGetValue("PATH", out string? helperPath))
            {
                throw NativeElfReader.Failure();
            }
            AddDirectory(helperPath);
            if (inputs.PrefixPolicy is not null)
            {
                diagnostic?.Inspect("RevalidatePrefixPolicy", inputs.PrefixPolicy.PrefixDirectory,
                    root: inputs.PrefixPolicy.PrefixDirectory);
            }
            inputs.PrefixPolicy?.Revalidate();
            string privateModules = Path.Join(home, "native-modules");
            diagnostic?.Inspect("PrivateModuleDirectory", privateModules, root: privateModules);
            NativePathBinding modules = BindPath(privateModules, directory: true, freezeMetadata: false);
            if (modules.Exists)
            {
                if ((modules.Identity.Mode & GroupOtherPermissions) != 0 || ReadEntryNames(privateModules, diagnostic).Length != 0)
                {
                    throw NativeElfReader.Failure();
                }
                FreezeConfigurationDirectory(privateModules);
            }
            // The private root suppresses user/system selectors, and the
            // absence of per-user gconv selectors is itself a frozen anchor.
            ProtectConfiguration(Path.Join(privateModules, "gconv-modules"));
            BindPath(Path.Join(privateModules, "gconv-modules.d"), directory: true, freezeMetadata: false);
            NativePathBinding openssl = ProtectConfiguration(Path.Join(home, "native-openssl.cnf"));
            if (!openssl.Exists || openssl.Identity.Size != 0)
            {
                throw NativeElfReader.Failure();
            }
        }

        private void ReadCache()
        {
            diagnostic?.Stage("Builder.ReadCache", layout.CachePath);
            NativePathBinding cache = ProtectConfiguration(layout.CachePath);
            if (!cache.Exists)
            {
                return;
            }
            using FileStream stream = OpenBound(cache, diagnostic);
            NativeLoaderCacheImage image = NativeLoaderCacheReader.Read(stream);
            RequireUnchanged(stream, cache.Identity, diagnostic);
            _cacheEntries = image.Entries.ToArray();
            _hardwareNames = image.HardwareCapabilityNames.ToArray();
            foreach (NativeLoaderCacheEntry entry in _cacheEntries)
            {
                AddDirectory(Path.GetDirectoryName(entry.Path)!);
                InspectFile(entry.Path, executable: false, required: false);
            }
        }

        private void ReadPreload()
        {
            diagnostic?.Stage("Builder.ReadPreload", layout.PreloadPath);
            NativePathBinding preload = ProtectConfiguration(layout.PreloadPath);
            _preloadExists = preload.Exists;
            if (!preload.Exists)
            {
                return;
            }
            foreach (string line in ReadConfigurationLines(preload, diagnostic))
            {
                if (StripComment(line).Length != 0)
                {
                    // glibc elf/rtld.c handle_preload_list accepts whitespace-
                    // separated library names and /etc/ld.so.preload comments.
                    // A system-wide nonempty injection is intentionally unsupported.
                    // https://github.com/bminor/glibc/blob/glibc-2.39/elf/rtld.c
                    throw NativeElfReader.Failure();
                }
            }
        }

        private void ParseLoaderConfiguration(string path, int depth)
        {
            diagnostic?.Stage("Builder.ParseLoaderConfiguration", path);
            diagnostic?.Inspect("LoaderConfigurationDepth", path, root: path);
            if (depth >= MaximumConfigurationDepth)
            {
                throw NativeElfReader.Failure();
            }
            NativePathBinding config = ProtectConfiguration(path);
            if (!config.Exists || !_parsedConfigurations.Add(config.CanonicalPath))
            {
                return;
            }
            // ldconfig.c:parse_conf reads directory lines and include glob.
            // Only the installed Ubuntu absolute-directory / *.conf forms are
            // supported; no execution, relative include, hwcap directive or
            // unknown selector is interpreted as harmless data.
            // https://github.com/bminor/glibc/blob/glibc-2.39/elf/ldconfig.c
            foreach (string source in ReadConfigurationLines(config, diagnostic))
            {
                diagnostic?.Stage("Builder.ParseLoaderConfiguration", config.AliasPath);
                diagnostic?.Inspect("LoaderConfigurationLine", config.AliasPath, config.CanonicalPath, config.AliasPath);
                string line = StripComment(source);
                if (line.Length == 0)
                {
                    continue;
                }
                if (line.StartsWith("include ", StringComparison.Ordinal) || line.StartsWith("include\t", StringComparison.Ordinal))
                {
                    string pattern = line[7..].Trim();
                    if (pattern.Any(char.IsWhiteSpace))
                    {
                        throw NativeElfReader.Failure();
                    }
                    if (pattern.EndsWith("/*.conf", StringComparison.Ordinal))
                    {
                        string directory = pattern[..^7];
                        RequireLiteralAbsolutePath(directory);
                        NativePathBinding parent = BindPath(directory, true, false);
                        if (parent.Exists)
                        {
                            string[] names = FreezeConfigurationDirectory(directory);
                            foreach (string name in names)
                            {
                                if (name.EndsWith(".conf", StringComparison.Ordinal))
                                {
                                    ParseLoaderConfiguration(Path.Join(directory, name), depth + 1);
                                }
                            }
                        }
                    }
                    else
                    {
                        RequireLiteralAbsolutePath(pattern);
                        ParseLoaderConfiguration(pattern, depth + 1);
                    }
                }
                else
                {
                    RequireLiteralAbsolutePath(line);
                    AddDirectory(line);
                }
            }
        }

        private void ParseGconvDirectory(string directory)
        {
            diagnostic?.Stage("Builder.ParseGconvDirectory", directory);
            NativePathBinding root = BindPath(directory, true, false);
            NativePathBinding primary = ProtectConfiguration(Path.Join(directory, "gconv-modules"));
            if (primary.Exists)
            {
                ParseGconvConfiguration(primary, directory);
            }
            string fragments = Path.Join(directory, "gconv-modules.d");
            NativePathBinding parent = BindPath(fragments, true, false);
            if (root.Exists && parent.Exists)
            {
                foreach (string name in FreezeConfigurationDirectory(fragments))
                {
                    if (name.EndsWith(".conf", StringComparison.Ordinal))
                    {
                        NativePathBinding config = ProtectConfiguration(Path.Join(fragments, name));
                        if (config.Exists)
                        {
                            ParseGconvConfiguration(config, directory);
                        }
                    }
                }
            }
        }

        private void ParseGconvConfiguration(NativePathBinding config, string moduleRoot)
        {
            diagnostic?.Stage("Builder.ParseGconvConfiguration", moduleRoot);
            diagnostic?.Inspect("GconvConfiguration", config.AliasPath, config.CanonicalPath, moduleRoot);
            if (!_parsedConfigurations.Add(config.CanonicalPath))
            {
                return;
            }
            // glibc iconv/gconv_parseconfdir.h and gconv_conf.c:add_module:
            // module FROM TO NAME [cost], absolute NAME overrides the config
            // root; otherwise NAME is rooted there, and .so is appended.
            // https://github.com/bminor/glibc/blob/glibc-2.39/iconv/gconv_conf.c
            // https://github.com/bminor/glibc/blob/glibc-2.39/iconv/gconv_parseconfdir.h
            Span<Range> fields = stackalloc Range[5];
            foreach (string source in ReadConfigurationLines(config, diagnostic))
            {
                diagnostic?.Inspect("GconvConfigurationLine", config.AliasPath, config.CanonicalPath, moduleRoot);
                int comment = source.IndexOf('#');
                ReadOnlySpan<char> line = (comment < 0 ? source.AsSpan() : source.AsSpan(0, comment)).Trim();
                if (line.IsEmpty)
                {
                    continue;
                }
                int count = 0;
                int position = 0;
                while (position < line.Length)
                {
                    while (position < line.Length && char.IsWhiteSpace(line[position]))
                    {
                        position++;
                    }
                    if (position == line.Length)
                    {
                        break;
                    }
                    int start = position;
                    while (position < line.Length && !char.IsWhiteSpace(line[position]))
                    {
                        position++;
                    }
                    if (count == fields.Length)
                    {
                        throw NativeElfReader.Failure();
                    }
                    fields[count++] = start..position;
                }
                if (line[fields[0]].SequenceEqual("alias") && count == 3)
                {
                    continue;
                }
                if (!line[fields[0]].SequenceEqual("module") || count is not (4 or 5))
                {
                    throw NativeElfReader.Failure();
                }
                string name = line[fields[3]].ToString();
                string path;
                if (name.StartsWith('/'))
                {
                    RequireLiteralAbsolutePath(name);
                    path = name;
                }
                else
                {
                    RequireLiteralComponents(name);
                    path = Path.Join(moduleRoot, name);
                }
                if (!path.EndsWith(".so", StringComparison.Ordinal))
                {
                    path += ".so";
                }
                if (_gconvModulePaths.Add(path))
                {
                    AccountCharacters(path.Length + 8L);
                }
                AddDirectory(Path.GetDirectoryName(path)!);
                _pendingFiles.Enqueue((path, false, true, true));
            }
        }

        private void AddDirectory(string path)
        {
            diagnostic?.Inspect("AddDirectory", path, root: path);
            RequireAbsoluteAlias(path);
            if (_searchDirectories.Add(path))
            {
                BoundRootCount(path);
                _pendingDirectories.Enqueue((path, false, false));
                foreach (string capability in HardwareCapabilityTreeNames)
                {
                    AddRecursiveDirectory(Path.Join(path, capability), capability: true);
                }
            }
        }

        private void AddRecursiveDirectory(string path, bool capability = false)
        {
            diagnostic?.Inspect("AddRecursiveDirectory", path, root: path);
            RequireAbsoluteAlias(path);
            bool added = _recursiveDirectories.Add(path);
            bool newCapability = capability && _capabilityRoots.Add(path);
            if (added)
            {
                BoundRootCount(path);
            }
            if (added || newCapability)
            {
                _pendingDirectories.Enqueue((path, true, capability));
            }
        }

        private void BoundRootCount(string path)
        {
            if (path == "/" || _searchDirectories.Count + _recursiveDirectories.Count > MaximumRoots)
            {
                throw NativeElfReader.Failure();
            }
        }

        private void DrainGraph()
        {
            while (_pendingDirectories.Count != 0 || _pendingFiles.Count != 0)
            {
                while (_pendingFiles.TryDequeue(out var pending))
                {
                    diagnostic?.InspectBound("QueuedFile", pending.Path, _paths, pending.Path);
                    InspectFile(pending.Path, pending.Executable, pending.Required, pending.FreezeContent);
                    if (!pending.Executable && _paths.TryGetValue(pending.Path, out NativePathBinding? candidate) &&
                        candidate.Exists && !_objects.ContainsKey(candidate.CanonicalPath))
                    {
                        // An explicitly named non-ELF dependency candidate is
                        // a frozen loader failure, not mutable ordinary data.
                        ProtectConfiguration(pending.Path);
                    }
                }
                if (_pendingDirectories.TryDequeue(out var directory))
                {
                    diagnostic?.Inspect("QueuedDirectory", directory.Path, root: directory.Path);
                    WalkDirectory(directory.Path, directory.Recursive, directory.Capability);
                }
            }
        }

        private void FreezeContentDependencies()
        {
            diagnostic?.Stage("Builder.FreezeContentDependencies");
            var candidates = CollectLibraryCandidates(_paths, _objects, _cacheEntries);
            foreach (string canonical in GetContentFrozenObjects(inputs.ExecutablePaths, _nssServices, _paths, _objects, candidates, diagnostic))
            {
                PromoteContent(_objects[canonical]);
            }
        }

        private void FreezeOptionalDependencyCandidates()
        {
            while (true)
            {
                diagnostic?.Stage("Builder.FreezeOptionalDependencyCandidates");
                int roots = _searchDirectories.Count;
                int objects = _objects.Count;
                var candidates = CollectLibraryCandidates(_paths, _objects, _cacheEntries);
                HashSet<string> unresolved = GetUnresolvedDependencyNames(_objects.Values, candidates);
                if (unresolved.Count != 0)
                {
                    HashSet<string> directories = GetDependencyCandidateDirectories(_searchDirectories, _paths);
                    foreach (string name in unresolved)
                    {
                        foreach (string directory in directories)
                        {
                            string path = Path.Join(directory, name);
                            diagnostic?.Inspect("OptionalDependencyCandidate", path, root: directory);
                            ProtectConfiguration(path);
                        }
                    }
                }
                DrainGraph();
                if (_searchDirectories.Count == roots && _objects.Count == objects)
                {
                    return;
                }
            }
        }

        private void WalkDirectory(string path, bool recursive, bool capability)
        {
            diagnostic?.Stage("Builder.WalkDirectory", path);
            NativePathBinding root = BindPath(path, true, false);
            if (!root.Exists)
            {
                return;
            }
            if (root.CanonicalPath == "/")
            {
                throw NativeElfReader.Failure();
            }
            BindPath(root.CanonicalPath, true, false);
            if (!recursive)
            {
                AddDirectory(root.CanonicalPath);
            }
            else if (_recursiveDirectories.Contains(path))
            {
                AddRecursiveDirectory(root.CanonicalPath, capability);
            }
            if (inputs.PrefixPolicy is not null)
            {
                diagnostic?.Inspect("PrefixCanonicalDirectory", inputs.PrefixPolicy.PrefixDirectory, root: path);
            }
            if (inputs.PrefixPolicy is not null &&
                root.CanonicalPath == WineXeBuildToolchainResolver.GetProtectedCanonicalPath(inputs.PrefixPolicy.PrefixDirectory,
                    true, diagnostic?.OriginObserver))
            {
                if (inputs.ModuleDirectories.Contains(path, StringComparer.Ordinal))
                {
                    throw NativeElfReader.Failure();
                }
                // The prefix is a protected non-code container. DOS z: is a
                // data mapping, not trust for '/'; registered Windows/Program
                // Files/c: code roots are visited independently.
                return;
            }
            diagnostic?.Inspect("WalkDirectory", root.AliasPath, root.CanonicalPath, path);
            var node = (root.Identity.DeviceMajor, root.Identity.DeviceMinor, root.Identity.Inode);
            bool firstVisit = recursive ? _visitedRecursiveDirectories.Add(node) :
                (!_visitedRecursiveDirectories.Contains(node) && _visitedDirectories.Add(node));
            bool firstCapabilityVisit = capability && _visitedCapabilityDirectories.Add(node);
            // A distinct registered search alias still supplies DSO load names,
            // even when its directory's physical contents were already parsed.
            if (!firstVisit && !firstCapabilityVisit && !_searchDirectories.Contains(path) && !_recursiveDirectories.Contains(path))
            {
                return;
            }
            // Replay protected load aliases from one bounded physical inventory.
            // This avoids rereading thousands of entries for /lib and /usr/lib,
            // while retaining each registered alias's distinct DSO load names.
            if (!_directoryEntries.TryGetValue(node, out var entries))
            {
                entries = [];
                diagnostic?.Inspect("EnumerateDirectory", root.AliasPath, root.CanonicalPath, path);
                foreach (string entry in Directory.EnumerateFileSystemEntries(root.CanonicalPath))
                {
                    diagnostic?.Inspect("DirectoryEntryAttributes", entry, root: path);
                    if (entries.Count >= MaximumPaths)
                    {
                        throw NativeElfReader.Failure();
                    }
                    RequireLiteralAbsolutePath(entry);
                    entries.Add((entry, File.GetAttributes(entry)));
                }
                _directoryEntries.Add(node, entries);
            }
            if (capability && !_configurationDirectories.ContainsKey(root.CanonicalPath))
            {
                string[] names = entries.Select(static entry => Path.GetFileName(entry.Path))
                    .Order(StringComparer.Ordinal).ToArray();
                FreezeConfigurationDirectory(root.CanonicalPath, names);
            }
            foreach ((string entry, FileAttributes attributes) in entries)
            {
                diagnostic?.Inspect("DirectoryEntry", entry, root: path);
                if (++_entries > MaximumPaths)
                {
                    throw NativeElfReader.Failure();
                }
                string alias = Path.Join(path, Path.GetFileName(entry));
                diagnostic?.Inspect("DirectoryEntryAlias", alias, root: path);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    NativePathBinding child = BindPath(alias, true, false);
                    if (!child.Exists)
                    {
                        throw NativeElfReader.Failure();
                    }
                    if (recursive && (firstVisit || firstCapabilityVisit))
                    {
                        _pendingDirectories.Enqueue((alias, true, capability));
                    }
                }
                else
                {
                    InspectFile(alias, executable: false, required: (attributes & FileAttributes.ReparsePoint) == 0,
                        freezeContent: recursive, retainAlias: alias != entry || (attributes & FileAttributes.ReparsePoint) != 0);
                }
            }
        }

        private NativeObjectBinding PromoteContent(NativeObjectBinding item)
        {
            diagnostic?.InspectBound("PromoteContent", item.CanonicalPath, _paths);
            if (item.Protection == NativeObjectProtection.ContentFrozen)
            {
                return item;
            }
            using FileStream stream = WineXeBuildToolchainResolver.OpenProtectedRead(item.CanonicalPath, diagnostic?.OriginObserver);
            RequireUnchanged(stream, item.Identity, diagnostic);
            NativeObjectBinding frozen = item with
            {
                Protection = NativeObjectProtection.ContentFrozen,
                ContentSha256 = HashFile(stream),
            };
            RequireUnchanged(stream, item.Identity, diagnostic);
            _objects[item.CanonicalPath] = frozen;
            return frozen;
        }

        private void InspectFile(string path, bool executable, bool required, bool freezeContent = false, bool retainAlias = true)
        {
            diagnostic?.Stage("Builder.InspectFile");
            NativePathBinding proof = ProbePath(path, directory: false, freezeMetadata: true);
            if (!proof.Exists)
            {
                if (required)
                {
                    throw NativeElfReader.Failure();
                }
                StorePath(proof);
                return;
            }
            if (executable)
            {
                WineXeBuildToolchainResolver.ValidateProtectedExecutable(path);
            }
            if (_objects.TryGetValue(proof.CanonicalPath, out NativeObjectBinding? existing))
            {
                if (existing.Identity != proof.Identity)
                {
                    throw NativeElfReader.Failure();
                }
                if (executable || freezeContent)
                {
                    existing = PromoteContent(existing);
                }
                StorePath(proof);
                if (executable)
                {
                    ValidateExecutableObject(existing);
                }
                if (existing.Image is NativeElfImageBinding existingImage)
                {
                    CloseInspectedImage(proof, existingImage);
                }
                return;
            }
            using FileStream stream = OpenBound(proof, diagnostic);
            NativeElfImage? native = executable ? NativeElfReader.Read(stream) : NativeElfReader.ReadModuleMetadata(stream);
            bool portable = native is null && HasPortableExecutableMagic(stream);
            bool script = false;
            if (native is null && executable)
            {
                WineXeBuildToolchainResolver.ValidateSupportedNativeScript(path);
                script = true;
                if (!inputs.ExecutablePaths.Contains("/bin/sh", StringComparer.Ordinal))
                {
                    throw NativeElfReader.Failure();
                }
                _pendingFiles.Enqueue(("/bin/sh", true, true, true));
            }
            if (native is null && !script && !portable)
            {
                RequireUnchanged(stream, proof.Identity, diagnostic);
                if (retainAlias)
                {
                    StorePath(proof with { FreezeMetadata = false });
                }
                return;
            }
            NativeElfImageBinding? image = native is null ? null : CopyImage(native);
            if (image is not null)
            {
                ValidateImage(image);
            }
            if (_objects.Count >= MaximumObjects)
            {
                throw NativeElfReader.Failure();
            }
            NativeObjectProtection protection = executable || freezeContent
                ? NativeObjectProtection.ContentFrozen : NativeObjectProtection.MetadataOnly;
            var item = new NativeObjectBinding(proof.CanonicalPath, proof.Identity, protection,
                protection == NativeObjectProtection.ContentFrozen ? HashFile(stream) : null, image, script, portable,
                image is not null && IsCurrentRuntimeImage(image));
            RequireUnchanged(stream, proof.Identity, diagnostic);
            AccountObject(item);
            _objects.Add(item.CanonicalPath, item);
            StorePath(proof);
            // Canonical targets are explicit metadata-bound aliases too, not
            // objects reached through an otherwise mutable symlink name.
            BindPath(proof.CanonicalPath, false, true);
            if (executable)
            {
                ValidateExecutableObject(item);
            }
            if (image is not null)
            {
                CloseInspectedImage(proof, image);
                if (image.Interpreter is not null)
                {
                    NativePathBinding interpreter = ProbePath(image.Interpreter, false, true);
                    using FileStream loader = OpenBound(interpreter, diagnostic);
                    NativeElfImage? loaderImage;
                    if (IsCurrentRuntimeImage(image))
                    {
                        WineXeBuildToolchainResolver.ValidateProtectedExecutable(image.Interpreter);
                        loaderImage = NativeElfReader.Read(loader);
                    }
                    else
                    {
                        // A foreign ELF is data, not an executable/current-ABI
                        // dependency. Its interpreter path is still protected
                        // and parsed, but no foreign loader behavior is admitted.
                        loaderImage = NativeElfReader.ReadModuleMetadata(loader);
                    }
                    if (loaderImage is null || loaderImage.ElfClass != image.ElfClass || loaderImage.Machine != image.Machine ||
                        loaderImage.ObjectType is not (2 or 3))
                    {
                        throw NativeElfReader.Failure();
                    }
                    RequireUnchanged(loader, interpreter.Identity, diagnostic);
                }
            }
        }

        private void CloseInspectedImage(NativePathBinding proof, NativeElfImageBinding image)
        {
            if (!_processedImageAliases.Add(proof.AliasPath))
            {
                return;
            }
            // glibc derives a DSO's ORIGIN from its load alias, not its final
            // symlink target. Bind the original alias parent before using that
            // DIRECTORY descriptor's canonical path as a finite origin anchor.
            // Resolving the parent is not resolving the final DSO symlink.
            NativePathBinding parent = BindPath(Path.GetDirectoryName(proof.AliasPath)!, true, false);
            if (!parent.Exists)
            {
                throw NativeElfReader.Failure();
            }
            BindPath(parent.CanonicalPath, true, false);
            string targetOrigin = Path.GetDirectoryName(proof.CanonicalPath)!;
            BindPath(targetOrigin, true, false);
            BindPath(proof.CanonicalPath, false, true);
            _processedImageAliases.Add(proof.CanonicalPath);
            CloseImagePaths(targetOrigin, image, AddDirectory, EnqueueDependency, diagnostic);
            if (parent.CanonicalPath != targetOrigin)
            {
                CloseImagePaths(parent.CanonicalPath, image, AddDirectory, EnqueueDependency, diagnostic);
            }
        }

        private void EnqueueDependency(string path, bool required) =>
            _pendingFiles.Enqueue((path, false, required, false));

        private NativePathBinding ProtectConfiguration(string path)
        {
            diagnostic?.Inspect("ProtectConfiguration", path);
            NativePathBinding proof = BindPath(path, false, true);
            if (proof.Exists && !_objects.ContainsKey(proof.CanonicalPath))
            {
                using FileStream stream = OpenBound(proof, diagnostic);
                NativeElfImage? native = NativeElfReader.ReadModuleMetadata(stream);
                NativeElfImageBinding? image = native is null ? null : CopyImage(native);
                string hash = HashFile(stream);
                RequireUnchanged(stream, proof.Identity, diagnostic);
                if (_objects.Count >= MaximumObjects)
                {
                    throw NativeElfReader.Failure();
                }
                var item = new NativeObjectBinding(proof.CanonicalPath, proof.Identity, NativeObjectProtection.ContentFrozen,
                    hash, image, false, false, image is not null && IsCurrentRuntimeImage(image));
                AccountObject(item);
                _objects.Add(proof.CanonicalPath, item);
                if (image is not null)
                {
                    ValidateImage(image);
                    CloseInspectedImage(proof, image);
                }
            }
            else if (proof.Exists)
            {
                NativeObjectBinding existing = PromoteContent(_objects[proof.CanonicalPath]);
                if (existing.Image is NativeElfImageBinding existingImage)
                {
                    CloseInspectedImage(proof, existingImage);
                }
            }
            return proof;
        }

        internal static string[] ReadConfigurationLines(NativePathBinding proof, PreparationDiagnostic? diagnostic = null)
        {
            diagnostic?.Inspect("ReadConfigurationLines", proof.AliasPath, proof.CanonicalPath);
            if (proof.Identity.Size > MaximumConfigurationBytes)
            {
                throw NativeElfReader.Failure();
            }
            using FileStream stream = OpenBound(proof, diagnostic);
            diagnostic?.Inspect("ReadConfigurationLines", proof.AliasPath, proof.CanonicalPath);
            using var reader = new StreamReader(stream, StrictUtf8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            var result = new List<string>();
            int characters = 0;
            while (reader.ReadLine() is string line)
            {
                characters += line.Length;
                if (line.Length > 4096 || characters > MaximumConfigurationBytes || line.Contains('\0') ||
                    line.Any(static character => char.IsControl(character) && character != '\t'))
                {
                    throw NativeElfReader.Failure();
                }
                result.Add(line);
            }
            RequireUnchanged(stream, proof.Identity, diagnostic);
            return result.ToArray();
        }

        private string[] FreezeConfigurationDirectory(string path, string[]? capturedNames = null)
        {
            diagnostic?.InspectBound("FreezeConfigurationDirectory", path, _paths, path);
            if (_configurationDirectories.TryGetValue(path, out NativeConfigurationDirectoryBinding? previous))
            {
                return previous.EntryNames;
            }
            if (_configurationDirectories.Count >= MaximumRoots)
            {
                throw NativeElfReader.Failure();
            }
            string[] names = capturedNames ?? ReadEntryNames(path, diagnostic);
            AccountCharacters(path.Length + 64L);
            foreach (string name in names)
            {
                AccountCharacters(name.Length + 8L);
            }
            _configurationDirectories.Add(path, new NativeConfigurationDirectoryBinding(path, names));
            return names;
        }

        private NativePathBinding BindPath(string path, bool directory, bool freezeMetadata)
        {
            NativePathBinding proof = ProbePath(path, directory, freezeMetadata);
            StorePath(proof);
            return _paths[path];
        }

        private NativePathBinding ProbePath(string path, bool directory, bool freezeMetadata)
        {
            diagnostic?.Inspect(directory ? "ProbeDirectory" : "ProbeFile", path);
            RequireAbsoluteAlias(path);
            var proof = WineXeBuildToolchainResolver.GetProtectedNativeOptionalPath(path, directory, diagnostic?.OriginObserver);
            diagnostic?.Inspect(directory ? "ProbeDirectory" : "ProbeFile", path, proof.CanonicalPath);
            return new NativePathBinding(path, directory, proof.Identity.HasValue, proof.CanonicalPath,
                proof.Identity.GetValueOrDefault(), proof.ExistingAncestor, proof.AncestorIdentity, freezeMetadata);
        }

        private void StorePath(NativePathBinding proof)
        {
            if (_paths.TryGetValue(proof.AliasPath, out NativePathBinding? existing))
            {
                if (existing.Directory != proof.Directory || existing.Exists != proof.Exists || existing.CanonicalPath != proof.CanonicalPath ||
                    !SameNode(existing.Identity, proof.Identity))
                {
                    throw NativeElfReader.Failure();
                }
                if (proof.FreezeMetadata && !existing.FreezeMetadata)
                {
                    _paths[proof.AliasPath] = proof;
                }
                else if (existing.FreezeMetadata && proof.FreezeMetadata && existing.Identity != proof.Identity)
                {
                    throw NativeElfReader.Failure();
                }
            }
            else
            {
                if (_paths.Count >= MaximumPaths)
                {
                    throw NativeElfReader.Failure();
                }
                _paths.Add(proof.AliasPath, proof);
                AccountCharacters(proof.AliasPath.Length + proof.CanonicalPath.Length + proof.ExistingAncestor.Length + 128L);
            }
        }

        private void AccountObject(NativeObjectBinding item)
        {
            AccountCharacters(item.CanonicalPath.Length + 256L);
            if (item.Image is NativeElfImageBinding image)
            {
                AccountCharacters((image.Interpreter?.Length ?? 0) + (image.Soname?.Length ?? 0));
                foreach (string value in image.Needed.Concat(image.RPath).Concat(image.RunPath))
                {
                    AccountCharacters(value.Length);
                }
            }
        }

        private void AccountCharacters(long characters)
        {
            if ((_metadataCharacters += characters) > MaximumMetadataCharacters)
            {
                throw NativeElfReader.Failure();
            }
        }

        private static FileStream OpenBound(NativePathBinding proof, PreparationDiagnostic? diagnostic = null)
        {
            diagnostic?.Inspect("OpenBound", proof.AliasPath, proof.CanonicalPath);
            if (!proof.Exists)
            {
                throw NativeElfReader.Failure();
            }
            FileStream stream = WineXeBuildToolchainResolver.OpenProtectedRead(proof.AliasPath, diagnostic?.OriginObserver);
            try
            {
                RequireUnchanged(stream, proof.Identity, diagnostic);
                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        private static void RequireUnchanged(FileStream stream, NativeFileIdentity identity, PreparationDiagnostic? diagnostic = null)
        {
            if (WineXeBuildToolchainResolver.GetNativeIdentity(stream.SafeFileHandle, diagnostic?.OriginObserver) != identity)
            {
                throw NativeElfReader.Failure();
            }
        }

        private static string StripComment(string line)
        {
            int comment = line.IndexOf('#');
            return (comment < 0 ? line : line[..comment]).Trim();
        }
    }
}
