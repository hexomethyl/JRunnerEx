using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using JRunner.Core.Contracts;
using Microsoft.Win32.SafeHandles;

namespace JRunner.Cli.Infrastructure;

/// <summary>
/// Resolves the production Wine pair to protected absolute aliases and a private curated helper PATH.
/// </summary>
internal sealed class WineXeBuildToolchainResolver
{
    private const string DefaultSearchPath = "/bin:/usr/bin";
    private const int LinuxDirectory = 0x10000;
    private const int LinuxNoFollow = 0x20000;
    private const int LinuxCloseOnExec = 0x80000;
    private const int LinuxPath = 0x200000;
    private const int LinuxEmptyPath = 0x1000;
    private const int LinuxEffectiveAccess = 0x200;
    private const int LinuxStatxForceSync = 0x2000;
    private const uint LinuxStatxRequiredFields = 0x10B; // TYPE | MODE | UID | INO
    private const uint LinuxStatxNativeFields = 0x3CB; // TYPE | MODE | UID | MTIME | CTIME | INO | SIZE
    private const int LinuxFileTypeMask = 0xF000;
    private const int LinuxDirectoryFile = 0x4000;
    private const int LinuxRegularFile = 0x8000;
    private const int LinuxSymbolicLink = 0xA000;
    private const int LinuxGroupOrOtherWrite = 0x12;
    private const int LinuxExecuteBits = 0x49;
    private const int LinuxExecuteAccess = 1;
    private const int LinuxOperationNotPermitted = 1;
    private const int LinuxNoSuchFileOrDirectory = 2;
    private const int LinuxPermissionDenied = 13;
    private const int LinuxNotADirectory = 20;
    private const int LinuxSymbolicLinkLoop = 40;
    private const int MaximumSymlinkExpansions = 40;
    private const int MaximumSymlinkTargetBytes = 4096;
    private const int DirectoryFlags = LinuxPath | LinuxDirectory | LinuxNoFollow | LinuxCloseOnExec;
    private const int ExecutableFlags = LinuxPath | LinuxNoFollow | LinuxCloseOnExec;
    private const int ReadableDirectoryFlags = LinuxDirectory | LinuxNoFollow | LinuxCloseOnExec;

    private enum ProtectedPathKind
    {
        Executable,
        File,
        Directory,
        Entry,
        PathHelper,
        OptionalProbeDirectory,
        NativeOptionalDirectory,
        NativeOptionalFile,
    }
    private static readonly UTF8Encoding NativePathEncoding = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly string? _searchPath;
    private readonly bool _allowSyntheticNativeInputs;

    internal WineXeBuildToolchainResolver(string? searchPath = null) : this(searchPath, allowSyntheticNativeInputs: false)
    {
    }

    private WineXeBuildToolchainResolver(string? searchPath, bool allowSyntheticNativeInputs)
    {
        _searchPath = searchPath;
        _allowSyntheticNativeInputs = allowSyntheticNativeInputs;
    }

    // Fixture-only vendor assumption: protected private Wine/helper/module layouts stand in
    // for installed root-owned vendor layouts. Descriptor protection and the production native
    // closure/launch checks are unchanged; only this internal factory selects the assumption.
    internal static WineXeBuildToolchainResolver CreateSyntheticNativeFixtureResolver(string? searchPath) =>
        new(searchPath, allowSyntheticNativeInputs: true);

    internal WineXeBuildToolchain Resolve(WineXeBuildToolchain toolchain, string workingDirectory, string? helperDirectory = null)
    {
        if (toolchain is null)
        {
            throw Unavailable(forWinePath: false);
        }
        if (!toolchain.RequiresTrustedPathDiscovery)
        {
            // Explicit synthetic toolchains are an internal injection boundary, not production discovery.
            return toolchain;
        }
        if (!OperatingSystem.IsLinux() ||
            string.IsNullOrEmpty(workingDirectory) ||
            !Path.IsPathFullyQualified(workingDirectory) ||
            workingDirectory.Contains('\0'))
        {
            throw Unavailable(forWinePath: false);
        }

        // Snapshot once for both commands. An empty PATH means the invocation's working directory.
        string searchPath = _searchPath ?? Environment.GetEnvironmentVariable("PATH") ?? DefaultSearchPath;
        if (searchPath.Contains('\0'))
        {
            throw Unavailable(forWinePath: false);
        }
        uint effectiveUserId;
        try
        {
            effectiveUserId = GetEffectiveUserId();
        }
        catch (Exception exception) when (IsNativeSupportFailure(exception))
        {
            throw Unavailable(forWinePath: false);
        }

        var wine = ResolveExecutable(toolchain.WineExecutable, searchPath, workingDirectory, effectiveUserId, forWinePath: false);
        var winePath = ResolveExecutable(toolchain.WinePathExecutable, searchPath, workingDirectory, effectiveUserId, forWinePath: true);
        (string childPath, NativeDependencyClosureSpec nativeInputs) =
            BuildTrustedChildPath(wine, winePath, effectiveUserId, helperDirectory);
        return new WineXeBuildToolchain(wine.AliasPath, winePath.AliasPath)
        {
            TrustedChildPath = childPath,
            NativeInputs = nativeInputs,
            UsesSyntheticNativeInputs = _allowSyntheticNativeInputs,
        };
    }

    internal static void ValidateProtectedExecutable(string absolutePath)
    {
        ValidateProtectedPath(absolutePath, ProtectedPathKind.Executable);
    }

    internal static void ValidateProtectedFile(string absolutePath)
    {
        ValidateProtectedPath(absolutePath, ProtectedPathKind.File);
    }

    internal static uint GetNativeUserId()
    {
        try
        {
            uint realUserId = GetRealUserId();
            if (realUserId != GetEffectiveUserId())
            {
                throw NativeElfReader.Failure();
            }
            return realUserId;
        }
        catch (Exception exception) when (IsNativeSupportFailure(exception))
        {
            throw NativeElfReader.Failure();
        }
    }

    internal static FileStream OpenProtectedRead(string absolutePath)
    {
        SafeFileHandle? readable = null;
        try
        {
            RequireAbsoluteProtectedPath(absolutePath);
            using SafeFileHandle bound = OpenProtectedAlias(
                absolutePath, GetEffectiveUserId(), forWinePath: false, ProtectedPathKind.File);
            NativeFileIdentity expected = GetNativeIdentity(bound);
            // Reopen only the already-admitted descriptor. No pathname lookup can substitute a file.
            int descriptor = OpenNative(DescriptorPath(bound), LinuxCloseOnExec, mode: 0);
            if (descriptor < 0)
            {
                throw NativeElfReader.Failure();
            }
            readable = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
            if (GetNativeIdentity(readable) != expected ||
                (expected.Mode & LinuxFileTypeMask) != LinuxRegularFile)
            {
                throw NativeElfReader.Failure();
            }
            var stream = new FileStream(readable, FileAccess.Read, bufferSize: 1, isAsync: false);
            readable = null;
            return stream;
        }
        catch (Exception exception) when (IsTrustValidationFailure(exception) || exception is OperationFailureException)
        {
            throw NativeElfReader.Failure();
        }
        finally
        {
            readable?.Dispose();
        }
    }

    internal static NativeFileIdentity GetProtectedNativeIdentity(string absolutePath, bool directory)
    {
        try
        {
            RequireAbsoluteProtectedPath(absolutePath);
            using SafeFileHandle handle = OpenProtectedAlias(
                absolutePath, GetEffectiveUserId(), forWinePath: false,
                directory ? ProtectedPathKind.Directory : ProtectedPathKind.File);
            return GetNativeIdentity(handle);
        }
        catch (Exception exception) when (IsTrustValidationFailure(exception) || exception is OperationFailureException)
        {
            throw NativeElfReader.Failure();
        }
    }

    internal static NativeFileIdentity GetNativeIdentity(SafeFileHandle handle)
    {
        try
        {
            if (Statx(handle, string.Empty, LinuxEmptyPath | LinuxStatxForceSync, LinuxStatxNativeFields,
                    out LinuxStatx status) != 0 ||
                (status.Mask & LinuxStatxNativeFields) != LinuxStatxNativeFields ||
                status.ChangeNanoseconds >= 1_000_000_000 || status.ModificationNanoseconds >= 1_000_000_000)
            {
                throw NativeElfReader.Failure();
            }
            return new NativeFileIdentity(
                status.DeviceMajor, status.DeviceMinor, status.Inode, status.Size, status.OwnerUserId, status.Mode,
                status.ChangeSeconds, status.ChangeNanoseconds, status.ModificationSeconds, status.ModificationNanoseconds);
        }
        catch (Exception exception) when (IsTrustValidationFailure(exception))
        {
            throw NativeElfReader.Failure();
        }
    }

    internal static string GetProtectedCanonicalPath(string absolutePath, bool directory)
    {
        try
        {
            RequireAbsoluteProtectedPath(absolutePath);
            using SafeFileHandle handle = OpenProtectedAlias(
                absolutePath, GetEffectiveUserId(), forWinePath: false,
                directory ? ProtectedPathKind.Directory : ProtectedPathKind.File);
            return GetDescriptorCanonicalPath(handle);
        }
        catch (Exception exception) when (IsTrustValidationFailure(exception) || exception is OperationFailureException)
        {
            throw NativeElfReader.Failure();
        }
    }

    internal static (string CanonicalPath, NativeFileIdentity? Identity, string ExistingAncestor,
        NativeFileIdentity AncestorIdentity) GetProtectedNativeOptionalPath(string absolutePath, bool directory)
    {
        try
        {
            RequireAbsoluteProtectedPath(absolutePath);
            using SafeFileHandle? handle = OpenProtectedAliasOrSkip(
                absolutePath, GetEffectiveUserId(), forWinePath: false,
                directory ? ProtectedPathKind.NativeOptionalDirectory : ProtectedPathKind.NativeOptionalFile,
                out var missing);
            if (handle is null)
            {
                return missing ?? throw NativeElfReader.Failure();
            }
            string canonical = GetDescriptorCanonicalPath(handle);
            NativeFileIdentity identity = GetNativeIdentity(handle);
            string ancestor = directory ? canonical : Path.GetDirectoryName(canonical)!;
            NativeFileIdentity ancestorIdentity = directory ? identity : GetProtectedNativeIdentity(ancestor, directory: true);
            return (canonical, identity, ancestor, ancestorIdentity);
        }
        catch (Exception exception) when (IsTrustValidationFailure(exception) || exception is OperationFailureException)
        {
            throw NativeElfReader.Failure();
        }
    }

    internal static bool TryGetProtectedNativeIdentity(
        string absolutePath, bool directory, out NativeFileIdentity identity, out string canonicalPath)
    {
        var proof = GetProtectedNativeOptionalPath(absolutePath, directory);
        canonicalPath = proof.CanonicalPath;
        identity = proof.Identity.GetValueOrDefault();
        return proof.Identity.HasValue;
    }

    internal static (string Target, NativeFileIdentity Identity) GetProtectedNativeLink(string absolutePath)
    {
        try
        {
            RequireAbsoluteProtectedPath(absolutePath);
            using SafeFileHandle parent = OpenProtectedAlias(
                Path.GetDirectoryName(absolutePath)!, GetEffectiveUserId(), forWinePath: false, ProtectedPathKind.Directory);
            int descriptor = OpenAt(parent, Path.GetFileName(absolutePath), ExecutableFlags, mode: 0);
            if (descriptor < 0)
            {
                throw NativeElfReader.Failure();
            }
            using SafeFileHandle link = new((IntPtr)descriptor, ownsHandle: true);
            LinuxStatx status = ReadStatus(link, forWinePath: false);
            if ((status.Mode & LinuxFileTypeMask) != LinuxSymbolicLink ||
                !HasTrustedOwner(in status, GetEffectiveUserId()))
            {
                throw NativeElfReader.Failure();
            }
            byte[] bytes = ArrayPool<byte>.Shared.Rent(MaximumSymlinkTargetBytes);
            try
            {
                return (ReadLink(link, bytes, forWinePath: false), GetNativeIdentity(link));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(bytes);
            }
        }
        catch (Exception exception) when (IsTrustValidationFailure(exception) || exception is OperationFailureException)
        {
            throw NativeElfReader.Failure();
        }
    }

    internal static void ValidateSupportedNativeScript(string absolutePath)
    {
        if (ReadSupportedWineWrapper(absolutePath).IsNative)
        {
            throw NativeElfReader.Failure();
        }
    }

    // These vendor-source prerequisites apply only to the finite packaged Wine shell graph.
    // Generic managed hosts/process launchers keep their existing protected-identity policy.
    internal static void ValidateVendorWineUtility(string absoluteAlias, string expectedName)
    {
        string canonical = GetProtectedCanonicalPath(absoluteAlias, directory: false);
        NativeFileIdentity identity = GetProtectedNativeIdentity(absoluteAlias, directory: false);
        if (identity.OwnerUserId != 0 || Path.GetDirectoryName(canonical) != "/usr/bin" ||
            Path.GetFileName(canonical) != expectedName)
        {
            throw UnsupportedWrapper();
        }
    }

    internal static void ValidateVendorWineShell()
    {
        string canonical = GetProtectedCanonicalPath("/bin/sh", directory: false);
        NativeFileIdentity identity = GetProtectedNativeIdentity("/bin/sh", directory: false);
        if (identity.OwnerUserId != 0 || Path.GetDirectoryName(canonical) != "/usr/bin" ||
            Path.GetFileName(canonical) is not ("dash" or "bash" or "sh"))
        {
            throw UnsupportedWrapper();
        }
    }

    private static string DescriptorPath(SafeFileHandle handle) =>
        string.Concat("/proc/self/fd/", handle.DangerousGetHandle().ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static string GetDescriptorCanonicalPath(SafeFileHandle handle)
    {
        byte[] bytes = ArrayPool<byte>.Shared.Rent(MaximumSymlinkTargetBytes);
        try
        {
            nint length = ReadLinkAt(handle, DescriptorPath(handle), bytes, MaximumSymlinkTargetBytes);
            if (length <= 0 || length >= MaximumSymlinkTargetBytes)
            {
                throw NativeElfReader.Failure();
            }
            string canonical = DecodeNativePath(bytes, (int)length, forWinePath: false);
            if (!Path.IsPathFullyQualified(canonical) || canonical.EndsWith(" (deleted)", StringComparison.Ordinal))
            {
                throw NativeElfReader.Failure();
            }
            return canonical;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    internal static void ValidateProtectedPathHelpers(string absoluteDirectory)
    {
        try
        {
            RequireAbsoluteProtectedPath(absoluteDirectory);
            ValidateProtectedPathHelpers(absoluteDirectory, GetEffectiveUserId());
        }
        catch (Exception exception) when (IsTrustValidationFailure(exception))
        {
            throw Unavailable(forWinePath: false);
        }
    }

    private static void ValidateProtectedPathHelpers(string absoluteDirectory, uint effectiveUserId)
    {
        using SafeFileHandle directory = OpenProtectedAlias(
            absoluteDirectory, effectiveUserId, forWinePath: false, ProtectedPathKind.Directory);
        ValidateDirectoryContents(directory, absoluteDirectory, effectiveUserId, visitedDirectories: null);
    }

    internal static void ValidateProtectedTree(string absoluteDirectory)
    {
        try
        {
            RequireAbsoluteProtectedPath(absoluteDirectory);
            uint effectiveUserId = GetEffectiveUserId();
            using SafeFileHandle directory = OpenProtectedAlias(
                absoluteDirectory, effectiveUserId, forWinePath: false, ProtectedPathKind.Directory);
            var visitedDirectories = new HashSet<(uint DeviceMajor, uint DeviceMinor, ulong Inode)>();
            ValidateDirectoryContents(directory, absoluteDirectory, effectiveUserId, visitedDirectories);
        }
        catch (Exception exception) when (IsTrustValidationFailure(exception))
        {
            throw Unavailable(forWinePath: false);
        }
    }

    private static void ValidateProtectedPath(string absolutePath, ProtectedPathKind kind)
    {
        try
        {
            RequireAbsoluteProtectedPath(absolutePath);
            using SafeFileHandle handle = OpenProtectedAlias(
                absolutePath, GetEffectiveUserId(), forWinePath: false, kind);
        }
        catch (Exception exception) when (IsTrustValidationFailure(exception))
        {
            throw Unavailable(forWinePath: false);
        }
    }

    private static void RequireAbsoluteProtectedPath(string absolutePath)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(absolutePath) ||
            !Path.IsPathFullyQualified(absolutePath) || absolutePath.Contains('\0'))
        {
            throw Unavailable(forWinePath: false);
        }
    }

    private static (string AliasPath, string CanonicalPath) ResolveExecutable(
        string executable,
        string searchPath,
        string workingDirectory,
        uint effectiveUserId,
        bool forWinePath)
    {
        if (string.IsNullOrEmpty(executable) || executable.Contains('\0'))
        {
            throw Unavailable(forWinePath);
        }
        try
        {
            if (executable.Contains('/'))
            {
                string candidate = Path.IsPathFullyQualified(executable)
                    ? executable
                    : Path.Join(workingDirectory, executable);
                string? resolved = ResolveCandidate(candidate, effectiveUserId, forWinePath);
                return resolved is not null ? (candidate, resolved) : throw Unavailable(forWinePath);
            }

            int start = 0;
            while (start <= searchPath.Length)
            {
                int end = searchPath.IndexOf(':', start);
                if (end < 0)
                {
                    end = searchPath.Length;
                }
                ReadOnlySpan<char> searchDirectory = searchPath.AsSpan(start, end - start);
                // Joining is intentionally not GetFullPath: realpath must resolve symlinks before '..'.
                string candidate = Path.IsPathFullyQualified(searchDirectory)
                    ? Path.Join(searchDirectory, executable.AsSpan())
                    : Path.Join(workingDirectory.AsSpan(), searchDirectory, executable.AsSpan());
                string? resolved = ResolveCandidate(candidate, effectiveUserId, forWinePath);
                if (resolved is not null)
                {
                    return (candidate, resolved);
                }
                if (end == searchPath.Length)
                {
                    break;
                }
                start = end + 1;
            }
        }
        catch (Exception exception) when (IsTrustValidationFailure(exception))
        {
            throw Unavailable(forWinePath);
        }
        throw Unavailable(forWinePath);
    }

    private static string? ResolveCandidate(string candidate, uint effectiveUserId, bool forWinePath)
    {
        string? canonicalPath = Canonicalize(candidate, forWinePath);
        if (canonicalPath is null)
        {
            return null;
        }
        if (!IsTrustedCanonicalPath(canonicalPath, effectiveUserId, forWinePath, requireExecutable: true))
        {
            return null;
        }
        // Wine's apploader dispatches on $0, so execution must retain the discovery alias basename.
        // Every link/ancestor must be protected too: a safe final target alone does not protect alias execution.
        using SafeFileHandle alias = OpenProtectedAlias(candidate, effectiveUserId, forWinePath, ProtectedPathKind.Executable);
        return canonicalPath;
    }

    private static string? Canonicalize(string candidate, bool forWinePath)
    {
        IntPtr allocation = RealPath(candidate, IntPtr.Zero);
        if (allocation == IntPtr.Zero)
        {
            if (IsUnavailableCandidate(Marshal.GetLastPInvokeError()))
            {
                return null;
            }
            throw Unavailable(forWinePath);
        }
        try
        {
            nuint length = StringLength(allocation);
            if (length == 0 || length > int.MaxValue)
            {
                throw Unavailable(forWinePath);
            }
            byte[] bytes = ArrayPool<byte>.Shared.Rent((int)length);
            try
            {
                Marshal.Copy(allocation, bytes, 0, (int)length);
                string canonicalPath = DecodeNativePath(bytes, (int)length, forWinePath);
                if (canonicalPath[0] != '/')
                {
                    throw Unavailable(forWinePath);
                }
                return canonicalPath;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(bytes);
            }
        }
        finally
        {
            Free(allocation);
        }
    }

    private static bool IsTrustedCanonicalPath(
        string canonicalPath,
        uint effectiveUserId,
        bool forWinePath,
        bool requireExecutable)
    {
        SafeFileHandle directory = OpenRoot(forWinePath);
        try
        {
            LinuxStatx rootStatus = ReadStatus(directory, forWinePath);
            bool trustedAncestry = IsTrustedDirectory(in rootStatus, effectiveUserId);
            int start = 1;
            while (start < canonicalPath.Length)
            {
                int end = canonicalPath.IndexOf('/', start);
                bool isFinalComponent = end < 0;
                if (isFinalComponent)
                {
                    end = canonicalPath.Length;
                }
                bool isExecutable = isFinalComponent && requireExecutable;
                if (end == start)
                {
                    start++;
                    continue;
                }

                int descriptor = OpenAt(directory, canonicalPath[start..end], isExecutable ? ExecutableFlags : DirectoryFlags, mode: 0);
                if (descriptor < 0)
                {
                    if (IsUnavailableCandidate(Marshal.GetLastPInvokeError()))
                    {
                        return false;
                    }
                    throw Unavailable(forWinePath);
                }
                SafeFileHandle child = new((IntPtr)descriptor, ownsHandle: true);
                if (isExecutable)
                {
                    using (child)
                    {
                        LinuxStatx status = ReadStatus(child, forWinePath);
                        // O_PATH never opens a FIFO/device for I/O and does not require file read permission.
                        if ((status.Mode & LinuxFileTypeMask) != LinuxRegularFile ||
                            (status.Mode & LinuxExecuteBits) == 0 ||
                            !HasExecuteAccess(child, forWinePath))
                        {
                            return false;
                        }
                        // Delay rejection until the candidate is executable: absent, special, and non-executable
                        // entries do not shadow a later command, but an unsafe executable must fail closed.
                        if (!trustedAncestry || !HasTrustedOwnershipAndPermissions(in status, effectiveUserId))
                        {
                            throw Unavailable(forWinePath);
                        }
                        // Protected paths cannot be substituted by another UID after handles close.
                        // Root and a hostile caller with this effective UID are outside this trust boundary.
                        return true;
                    }
                }

                try
                {
                    LinuxStatx status = ReadStatus(child, forWinePath);
                    trustedAncestry &= IsTrustedDirectory(in status, effectiveUserId);
                }
                catch
                {
                    child.Dispose();
                    throw;
                }
                directory.Dispose();
                directory = child;
                start = end + 1;
            }
            if (!requireExecutable && !trustedAncestry)
            {
                throw Unavailable(forWinePath);
            }
            return !requireExecutable;
        }
        finally
        {
            directory.Dispose();
        }
    }

    private static SafeFileHandle OpenProtectedAlias(
        string absoluteAlias,
        uint effectiveUserId,
        bool forWinePath,
        ProtectedPathKind kind)
    {
        return OpenProtectedAliasOrSkip(absoluteAlias, effectiveUserId, forWinePath, kind) ?? throw Unavailable(forWinePath);
    }

    private static SafeFileHandle? OpenProtectedAliasOrSkip(
        string absoluteAlias,
        uint effectiveUserId,
        bool forWinePath,
        ProtectedPathKind kind)
    {
        return OpenProtectedAliasOrSkip(absoluteAlias, effectiveUserId, forWinePath, kind, out _);
    }

    private static SafeFileHandle? OpenProtectedAliasOrSkip(
        string absoluteAlias,
        uint effectiveUserId,
        bool forWinePath,
        ProtectedPathKind kind,
        out (string CanonicalPath, NativeFileIdentity? Identity, string ExistingAncestor,
            NativeFileIdentity AncestorIdentity)? missing)
    {
        bool pathHelper = kind == ProtectedPathKind.PathHelper;
        bool optionalDirectory = kind == ProtectedPathKind.OptionalProbeDirectory;
        bool nativeOptional = kind is ProtectedPathKind.NativeOptionalDirectory or ProtectedPathKind.NativeOptionalFile;
        missing = null;
        bool skipUnavailable = pathHelper || optionalDirectory;
        SafeFileHandle? directory = OpenRoot(forWinePath);
        byte[]? linkBuffer = null;
        try
        {
            LinuxStatx rootStatus = ReadStatus(directory, forWinePath);
            if (!IsTrustedDirectory(in rootStatus, effectiveUserId))
            {
                throw Unavailable(forWinePath);
            }
            string pendingPath = absoluteAlias;
            int start = 1;
            int symlinkExpansions = 0;
            while (start < pendingPath.Length)
            {
                int end = pendingPath.IndexOf('/', start);
                bool isFinalComponent = end < 0;
                if (isFinalComponent)
                {
                    end = pendingPath.Length;
                }
                if (end == start)
                {
                    start++;
                    continue;
                }

                // O_PATH binds links without following them and never performs I/O on special files.
                // In particular, opening ".." here preserves kernel symlink/parent semantics.
                int descriptor = OpenAt(directory, pendingPath[start..end], ExecutableFlags, mode: 0);
                if (descriptor < 0)
                {
                    if (nativeOptional && Marshal.GetLastPInvokeError() == LinuxNoSuchFileOrDirectory)
                    {
                        string ancestor = GetDescriptorCanonicalPath(directory);
                        missing = (Path.Join(ancestor, pendingPath[start..]), null, ancestor, GetNativeIdentity(directory));
                        return null;
                    }
                    // A checked parent cannot be changed by another UID to make an absent or
                    // inaccessible component executable later. Unsafe target ancestry is never skipped.
                    if (skipUnavailable && IsUnavailableCandidate(Marshal.GetLastPInvokeError()))
                    {
                        return null;
                    }
                    throw Unavailable(forWinePath);
                }
                SafeFileHandle? child = new((IntPtr)descriptor, ownsHandle: true);
                try
                {
                    LinuxStatx status = ReadStatus(child, forWinePath);
                    int fileType = status.Mode & LinuxFileTypeMask;
                    if (fileType == LinuxSymbolicLink)
                    {
                        // Symlink mode 0777 is not a writable-file grant on Linux. Its protected parent
                        // and trusted owner protect its contents; every expanded target is checked below.
                        if (!HasTrustedOwner(in status, effectiveUserId))
                        {
                            throw Unavailable(forWinePath);
                        }
                        linkBuffer ??= ArrayPool<byte>.Shared.Rent(MaximumSymlinkTargetBytes);
                        string target = ReadLink(child, linkBuffer, forWinePath);
                        if (++symlinkExpansions > MaximumSymlinkExpansions)
                        {
                            // Protected links cannot be shortened into an executable chain by another UID.
                            if (skipUnavailable)
                            {
                                return null;
                            }
                            throw Unavailable(forWinePath);
                        }
                        bool absoluteTarget = target[0] == '/';
                        if (absoluteTarget)
                        {
                            directory.Dispose();
                            directory = OpenRoot(forWinePath);
                            rootStatus = ReadStatus(directory, forWinePath);
                            if (!IsTrustedDirectory(in rootStatus, effectiveUserId))
                            {
                                throw Unavailable(forWinePath);
                            }
                        }

                        // Retain the separator/suffix, including a trailing slash's directory requirement.
                        // Never lexically collapse ".." before expanding the target.
                        pendingPath = string.Concat(target.AsSpan(), pendingPath.AsSpan(end));
                        start = absoluteTarget ? 1 : 0;
                        continue;
                    }
                    if (fileType == LinuxDirectoryFile)
                    {
                        if (nativeOptional && isFinalComponent && kind == ProtectedPathKind.NativeOptionalFile)
                        {
                            throw NativeElfReader.Failure();
                        }
                        if (pathHelper && isFinalComponent)
                        {
                            return null; // A directory is not a PATH command; its parent already protects its type.
                        }
                        if (!IsTrustedDirectory(in status, effectiveUserId))
                        {
                            throw Unavailable(forWinePath);
                        }
                        directory.Dispose();
                        directory = child;
                        child = null;
                        start = end + 1;
                        continue;
                    }
                    if (pathHelper)
                    {
                        if (!isFinalComponent || !IsProtectedExecutableHelper(child, in status, effectiveUserId))
                        {
                            return null;
                        }
                        SafeFileHandle result = child;
                        child = null;
                        return result;
                    }
                    if (optionalDirectory)
                    {
                        return null; // A protected non-directory cannot become a directory through another UID.
                    }
                    if (isFinalComponent && fileType == LinuxRegularFile &&
                        (kind is ProtectedPathKind.Executable or ProtectedPathKind.File or ProtectedPathKind.Entry or ProtectedPathKind.NativeOptionalFile) &&
                        HasTrustedOwnershipAndPermissions(in status, effectiveUserId) &&
                        (kind != ProtectedPathKind.Executable ||
                            ((status.Mode & LinuxExecuteBits) != 0 && HasExecuteAccess(child, forWinePath))))
                    {
                        SafeFileHandle result = child;
                        child = null;
                        return result;
                    }
                    throw Unavailable(forWinePath);
                }
                finally
                {
                    child?.Dispose();
                }
            }
            if (pathHelper)
            {
                return null;
            }
            if (kind is not (ProtectedPathKind.Directory or ProtectedPathKind.Entry or ProtectedPathKind.OptionalProbeDirectory or ProtectedPathKind.NativeOptionalDirectory))
            {
                throw Unavailable(forWinePath);
            }
            SafeFileHandle resultDirectory = directory;
            directory = null;
            return resultDirectory;
        }
        finally
        {
            directory?.Dispose();
            if (linkBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(linkBuffer);
            }
        }
    }

    private static string ReadLink(SafeFileHandle handle, byte[] bytes, bool forWinePath)
    {
        nint length = ReadLinkAt(handle, string.Empty, bytes, MaximumSymlinkTargetBytes);
        if (length <= 0 || length >= MaximumSymlinkTargetBytes)
        {
            throw Unavailable(forWinePath);
        }
        return DecodeNativePath(bytes, (int)length, forWinePath);
    }

    private static string DecodeNativePath(byte[] bytes, int length, bool forWinePath)
    {
        try
        {
            // Replacement decoding could validate a different pathname than the kernel will execute.
            return NativePathEncoding.GetString(bytes, 0, length);
        }
        catch (DecoderFallbackException)
        {
            throw Unavailable(forWinePath);
        }
    }

    private (string ChildPath, NativeDependencyClosureSpec NativeInputs) BuildTrustedChildPath(
        (string AliasPath, string CanonicalPath) wine,
        (string AliasPath, string CanonicalPath) winePath,
        uint effectiveUserId,
        string? helperDirectory)
    {
        try
        {
            RequireAbsoluteProtectedPath(helperDirectory!);
            if (helperDirectory!.Contains(':'))
            {
                throw Unavailable(forWinePath: false); // PATH cannot escape a colon inside its sole directory.
            }
            using SafeFileHandle directory = OpenProtectedAlias(
                helperDirectory, effectiveUserId, forWinePath: false, ProtectedPathKind.Directory);
            LinuxStatx status = ReadStatus(directory, forWinePath: false);
            if (status.OwnerUserId != effectiveUserId || (status.Mode & 0x1FF) != 0x1C0 ||
                Directory.EnumerateFileSystemEntries(helperDirectory).Any())
            {
                throw Unavailable(forWinePath: false);
            }

            var closure = new CuratedWineHelpers(effectiveUserId, _allowSyntheticNativeInputs);
            closure.AddSelected("wine", wine);
            closure.AddSelected("winepath", winePath);
            closure.Resolve();
            closure.ValidateVendorModuleRoots();
            closure.Materialize(helperDirectory);
            ValidateProtectedPathHelpers(helperDirectory, effectiveUserId);
            return (helperDirectory, closure.CreateNativeInputs(helperDirectory));
        }
        catch (OperationFailureException exception) when (exception.Kind != "wine-wrapper-unsupported")
        {
            throw Unavailable(forWinePath: false);
        }
        catch (Exception exception) when (IsTrustValidationFailure(exception))
        {
            throw Unavailable(forWinePath: false);
        }
    }

    private sealed class CuratedWineHelpers(uint effectiveUserId, bool allowSyntheticNativeInputs)
    {
        private static readonly string[] WineNames =
        [
            "wine", "wine64", "wine32", "wine-loader", "wine64-loader", "wine-preloader",
            "wine64-preloader", "wineapploader", "wineserver", "wineserver32", "wineserver64",
        ];
        private static readonly string[] WineSuffixes = [string.Empty, "-stable", "-development", "-staging"];
        private static readonly string[] VendorWineSuffixes = [string.Empty, "-development"];
        private IReadOnlyList<string> SupportedSuffixes => allowSyntheticNativeInputs ? WineSuffixes : VendorWineSuffixes;
        private static readonly string[] ServerFallbackNames = ["wineserver64", "wineserver32"];
        private static readonly string[] UnixArchitectures = ["x86_64-unix", "i386-unix", "aarch64-unix", "arm-unix"];
        private static readonly string[] InstalledLibraryDirectories =
            ["lib/wine", "lib64/wine", "lib/x86_64-linux-gnu/wine", "lib/i386-linux-gnu/wine",
                "lib/aarch64-linux-gnu/wine", "lib/arm-linux-gnueabihf/wine"];
        private readonly List<string> _directories = [];
        private readonly Dictionary<string, string> _bindings = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SupportedWineWrapper> _inspected = new(StringComparer.Ordinal);
        private readonly HashSet<string> _activeWrappers = new(StringComparer.Ordinal);
        private readonly HashSet<string> _utilities = new(StringComparer.Ordinal);
        private readonly HashSet<string> _requiredNames = new(StringComparer.Ordinal);
        private readonly HashSet<string> _probedDirectories = new(StringComparer.Ordinal);
        private readonly HashSet<string> _executables = new(StringComparer.Ordinal);
        private readonly HashSet<string> _moduleDirectories = new(StringComparer.Ordinal);
        private readonly HashSet<string> _configurationFiles = new(StringComparer.Ordinal);
        private readonly HashSet<string> _packagePrefixes = new(StringComparer.Ordinal);
        private string _preferredServerName = "wineserver";

        internal void AddSelected(string name, (string AliasPath, string CanonicalPath) executable)
        {
            _bindings.Add(name, executable.AliasPath);
            AddDirectory(Path.GetDirectoryName(executable.AliasPath)!);
            AddDirectory(Path.GetDirectoryName(executable.CanonicalPath)!);
            SupportedWineWrapper wrapper = Inspect(executable.AliasPath, executable.CanonicalPath);
            if (wrapper.Kind is WineExecutableKind.ServerLauncher or WineExecutableKind.StaticPreloader ||
                name == "wine" && wrapper.IsAppLoader ||
                name == "winepath" && wrapper.Kind == WineExecutableKind.WineLauncher)
            {
                throw UnsupportedWrapper();
            }
            string aliasName = Path.GetFileName(executable.AliasPath);
            if (name == "wine")
            {
                string canonicalName = Path.GetFileName(executable.CanonicalPath);
                foreach (string suffix in SupportedSuffixes)
                {
                    if (suffix.Length != 0 &&
                        (aliasName.EndsWith(suffix, StringComparison.Ordinal) ||
                            canonicalName.EndsWith(suffix, StringComparison.Ordinal)))
                    {
                        _preferredServerName = string.Concat("wineserver", suffix);
                        break;
                    }
                }
            }
            if (IsWineName(aliasName))
            {
                _bindings.TryAdd(aliasName, executable.AliasPath);
            }
        }

        internal void Resolve()
        {
            // Query only finite Wine names. Never enumerate unrelated commands in /usr/bin,
            // the caller's PATH, or any selected executable's source directory.
            AddDirectory("/usr/bin");
            AddDirectory("/bin");
            AddInstalledPackageRoots("/usr");
            for (int index = 0; index < _directories.Count; index++)
            {
                foreach (string name in WineNames)
                {
                    foreach (string suffix in SupportedSuffixes)
                    {
                        string command = suffix.Length == 0 ? name : string.Concat(name, suffix);
                        string candidate = Path.Join(_directories[index], command);
                        using SafeFileHandle? helper = OpenProtectedAliasOrSkip(
                            candidate, effectiveUserId, forWinePath: false, ProtectedPathKind.PathHelper);
                        if (helper is null)
                        {
                            continue;
                        }
                        string canonical = Canonicalize(candidate, forWinePath: false) ?? throw Unavailable(forWinePath: false);
                        AddDirectory(Path.GetDirectoryName(canonical)!);
                        // Native Wine may try a sibling before consulting PATH or WINESERVER.
                        // Validate every named candidate, even when an earlier binding already exists.
                        Inspect(candidate, canonical);
                        _bindings.TryAdd(command, candidate);
                    }
                }
            }
            // Match an explicitly selected package suffix ahead of another installed version's
            // bare public server. All sibling/server probes were validated above regardless.
            if (_preferredServerName != "wineserver" &&
                _bindings.TryGetValue(_preferredServerName, out string? preferredServer))
            {
                _bindings["wineserver"] = preferredServer;
            }
            else if (!_bindings.ContainsKey("wineserver"))
            {
                // Ubuntu packages may expose only a native 32/64 server.
                foreach (string serverName in ServerFallbackNames)
                {
                    if (_bindings.TryGetValue(serverName, out string? server))
                    {
                        _bindings.Add("wineserver", server);
                        break;
                    }
                }
            }
            _requiredNames.Add("wineserver");
            foreach (string required in _requiredNames)
            {
                if (!_bindings.ContainsKey(required))
                {
                    throw Unavailable(forWinePath: false);
                }
            }
            foreach (string utility in _utilities)
            {
                for (int index = 0; index < _directories.Count; index++)
                {
                    string candidate = Path.Join(_directories[index], utility);
                    using SafeFileHandle? helper = OpenProtectedAliasOrSkip(
                        candidate, effectiveUserId, forWinePath: false, ProtectedPathKind.PathHelper);
                    if (helper is null)
                    {
                        continue;
                    }
                    if (!ReadSupportedWineWrapper(candidate).IsNative)
                    {
                        throw UnsupportedWrapper();
                    }
                    if (!allowSyntheticNativeInputs)
                    {
                        ValidateVendorWineUtility(candidate, utility);
                    }
                    _bindings.TryAdd(utility, candidate);
                    _executables.Add(candidate);
                }
                if (!_bindings.ContainsKey(utility))
                {
                    throw Unavailable(forWinePath: false);
                }
            }
        }

        internal void Materialize(string helperDirectory)
        {
            foreach ((string name, string absoluteAlias) in _bindings)
            {
                string privateAlias = Path.Join(helperDirectory, name);
                File.CreateSymbolicLink(privateAlias, absoluteAlias);
                using SafeFileHandle helper = OpenProtectedAlias(
                    privateAlias, effectiveUserId, forWinePath: false, ProtectedPathKind.Executable);
            }
        }

        internal NativeDependencyClosureSpec CreateNativeInputs(string helperDirectory)
        {
            return new NativeDependencyClosureSpec(
                _executables.Order(StringComparer.Ordinal).ToArray(),
                _moduleDirectories.Append(helperDirectory).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                _configurationFiles.Order(StringComparer.Ordinal).ToArray(),
                new Dictionary<string, string>(StringComparer.Ordinal) { ["PATH"] = helperDirectory });
        }

        private void AddDirectory(string absoluteDirectory)
        {
            string canonical = ResolveTrustedDirectory(absoluteDirectory, effectiveUserId, forWinePath: false);
            if (!_directories.Contains(canonical, StringComparer.Ordinal))
            {
                _directories.Add(canonical);
            }
        }

        private void AddOptionalProbeDirectory(string absoluteDirectory)
        {
            if (!_probedDirectories.Add(absoluteDirectory))
            {
                return;
            }
            using SafeFileHandle? directory = OpenProtectedAliasOrSkip(
                absoluteDirectory, effectiveUserId, forWinePath: false, ProtectedPathKind.OptionalProbeDirectory);
            if (directory is not null)
            {
                AddDirectory(absoluteDirectory);
            }
        }

        private void AddNativeProbeDirectories(string canonicalExecutable)
        {
            var directory = new DirectoryInfo(Path.GetDirectoryName(canonicalExecutable)!);
            string name = Path.GetFileName(canonicalExecutable);
            if ((name is "wine" or "wine64" or "wine-loader" or "wine64-loader" &&
                    (directory.Name == "loader" || directory.FullName.EndsWith("/tools/wine", StringComparison.Ordinal))) ||
                (name == "wineserver" && directory.Name == "server"))
            {
                throw UnsupportedWrapper(); // Native build-tree probes are not an installed-package closure.
            }
            if (directory.Name == "bin" && IsWineName(name) && directory.Parent is DirectoryInfo binPrefix)
            {
                foreach (string library in InstalledLibraryDirectories)
                {
                    AddInstalledWineDirectory(Path.Join(binPrefix.FullName, library));
                }
                AddInstalledPackageRoots(binPrefix.FullName);
                return;
            }
            DirectoryInfo? wineDirectory = directory.Name.EndsWith("-unix", StringComparison.Ordinal)
                ? directory.Parent : directory;
            if (wineDirectory is null || wineDirectory.Name is not ("wine" or "wine-stable" or "wine-development" or "wine-staging"))
            {
                return;
            }
            DirectoryInfo? libraryDirectory = wineDirectory.Parent;
            if (libraryDirectory is not null &&
                (libraryDirectory.Name.EndsWith("-linux-gnu", StringComparison.Ordinal) ||
                    libraryDirectory.Name.EndsWith("-linux-gnueabihf", StringComparison.Ordinal)))
            {
                libraryDirectory = libraryDirectory.Parent;
            }
            if (libraryDirectory is null || libraryDirectory.Name is not ("lib" or "lib64") ||
                libraryDirectory.Parent is not DirectoryInfo prefix)
            {
                return;
            }
            // Source-derived installed layouts only, not an assumed arbitrary compiled BINDIR.
            // Native Wine probes architecture siblings and prefix/bin before falling back to PATH.
            AddOptionalProbeDirectory(Path.Join(prefix.FullName, "bin"));
            AddInstalledPackageRoots(prefix.FullName);
            AddInstalledWineDirectory(wineDirectory.FullName);
        }

        private void AddInstalledWineDirectory(string wineDirectory)
        {
            _moduleDirectories.Add(wineDirectory);
            AddOptionalProbeDirectory(wineDirectory);
            foreach (string architecture in UnixArchitectures)
            {
                AddOptionalProbeDirectory(Path.Join(wineDirectory, architecture));
            }
        }

        private void AddInstalledPackageRoots(string prefix)
        {
            if (!_packagePrefixes.Add(prefix))
            {
                return;
            }
            foreach (string suffix in SupportedSuffixes)
            {
                foreach (string library in InstalledLibraryDirectories)
                {
                    AddInstalledWineDirectory(string.Concat(Path.Join(prefix, library), suffix));
                }
                string dataRoot = Path.Join(prefix, "share", string.Concat("wine", suffix));
                _moduleDirectories.Add(dataRoot);
                _configurationFiles.Add(Path.Join(dataRoot, "wine.inf"));
                _moduleDirectories.Add(Path.Join(prefix, "share", string.Concat("wine-mono", suffix)));
            }
            if (prefix == "/usr")
            {
                _moduleDirectories.Add("/opt/wine/mono");
                _moduleDirectories.Add("/opt/wine/gecko");
            }
        }

        private SupportedWineWrapper Inspect(string alias, string canonical)
        {
            _executables.Add(alias);
            if (_inspected.TryGetValue(canonical, out SupportedWineWrapper? known))
            {
                if (_activeWrappers.Contains(canonical))
                {
                    throw UnsupportedWrapper();
                }
                RequireDerivedLoaderName(alias, known);
                return known;
            }
            SupportedWineWrapper wrapper = ReadSupportedWineWrapper(alias);
            _inspected.Add(canonical, wrapper);
            if (wrapper.IsNative && !allowSyntheticNativeInputs)
            {
                ValidateVendorControlPath(canonical);
            }
            if (wrapper.IsNative)
            {
                AddNativeProbeDirectories(canonical);
                return wrapper;
            }
            _activeWrappers.Add(canonical);
            RequireDerivedLoaderName(alias, wrapper);
            using SafeFileHandle interpreter = OpenProtectedAlias(
                "/bin/sh", effectiveUserId, forWinePath: false, ProtectedPathKind.Executable);
            _executables.Add("/bin/sh");
            if (!ReadSupportedWineWrapper("/bin/sh").IsNative)
            {
                throw UnsupportedWrapper();
            }
            if (!allowSyntheticNativeInputs)
            {
                ValidateVendorWineShell();
            }
            foreach (string utility in wrapper.Utilities)
            {
                _utilities.Add(utility);
            }
            foreach (string name in wrapper.RequiredNames)
            {
                _requiredNames.Add(name);
            }
            bool foundLiteralLoader = false;
            foreach (string target in wrapper.LiteralTargets)
            {
                using SafeFileHandle? loader = OpenProtectedAliasOrSkip(
                    target, effectiveUserId, forWinePath: false, ProtectedPathKind.PathHelper);
                if (loader is null)
                {
                    continue;
                }
                foundLiteralLoader = true;
                string targetCanonical = Canonicalize(target, forWinePath: false) ?? throw Unavailable(forWinePath: false);
                AddDirectory(Path.GetDirectoryName(target)!);
                AddDirectory(Path.GetDirectoryName(targetCanonical)!);
                Inspect(target, targetCanonical);
                string targetName = Path.GetFileName(target);
                if (IsWineName(targetName))
                {
                    _bindings.TryAdd(targetName, target);
                }
            }
            if (wrapper.RequiresLiteralLoader && !foundLiteralLoader)
            {
                throw Unavailable(forWinePath: false);
            }
            _activeWrappers.Remove(canonical);
            return wrapper;
        }

        private static void ValidateVendorControlPath(string canonical)
        {
            NativeFileIdentity identity = GetProtectedNativeIdentity(canonical, directory: false);
            if (identity.OwnerUserId != 0)
            {
                throw UnsupportedWrapper();
            }
            string directory = Path.GetDirectoryName(canonical)!;
            string name = Path.GetFileName(canonical);
            bool knownName = IsVendorWineName(name) ||
                name is "wine-preloader.static" or "wine64-preloader.static";
            if (!knownName)
            {
                throw UnsupportedWrapper();
            }
            if (directory == "/usr/bin")
            {
                return;
            }
            foreach (string suffix in VendorWineSuffixes)
            {
                foreach (string root in new[] { "/usr/lib/wine", "/usr/lib/x86_64-linux-gnu/wine", "/usr/lib/i386-linux-gnu/wine" })
                {
                    string wineRoot = string.Concat(root, suffix);
                    if (directory == wineRoot || directory == Path.Join(wineRoot, "x86_64-unix") ||
                        directory == Path.Join(wineRoot, "i386-unix"))
                    {
                        return;
                    }
                }
            }
            throw UnsupportedWrapper();
        }

        internal void ValidateVendorModuleRoots()
        {
            if (allowSyntheticNativeInputs)
            {
                return;
            }
            var visited = new HashSet<string>(StringComparer.Ordinal);
            int entries = 0;
            foreach (string root in _moduleDirectories)
            {
                if (TryGetProtectedNativeIdentity(root, directory: true, out _, out string canonical))
                {
                    ValidateVendorDirectory(canonical, visited, ref entries);
                }
            }
        }

        private static void ValidateVendorDirectory(string directory, HashSet<string> visited, ref int entries)
        {
            if (!visited.Add(directory))
            {
                return;
            }
            if (GetProtectedNativeIdentity(directory, directory: true).OwnerUserId != 0)
            {
                throw UnsupportedWrapper();
            }
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++entries > NativeDependencyClosure.MaximumPaths)
                {
                    throw NativeElfReader.Failure();
                }
                bool childDirectory = (File.GetAttributes(entry) & FileAttributes.Directory) != 0;
                if (GetProtectedNativeIdentity(entry, childDirectory).OwnerUserId != 0)
                {
                    throw UnsupportedWrapper();
                }
                if (childDirectory)
                {
                    ValidateVendorDirectory(GetProtectedCanonicalPath(entry, directory: true), visited, ref entries);
                }
            }
        }

        private void RequireDerivedLoaderName(string alias, SupportedWineWrapper wrapper)
        {
            if (!wrapper.DerivesLoaderSuffix)
            {
                return;
            }
            string name = Path.GetFileName(alias);
            if (name.EndsWith(".exe", StringComparison.Ordinal))
            {
                name = name[..^4];
            }
            int separator = name.IndexOf('-');
            string suffix = separator < 0 ? string.Empty : name[separator..];
            if (!SupportedSuffixes.Contains(suffix, StringComparer.Ordinal))
            {
                throw UnsupportedWrapper();
            }
            _requiredNames.Add(string.Concat("wine", suffix));
        }

        private static bool IsVendorWineName(string candidate)
        {
            foreach (string name in WineNames)
            {
                foreach (string suffix in VendorWineSuffixes)
                {
                    if (candidate.AsSpan().StartsWith(name, StringComparison.Ordinal) &&
                        candidate.AsSpan(name.Length).SequenceEqual(suffix))
                    {
                        return true;
                    }
                }
            }
            return candidate is "winepath" or "winepath-development";
        }

        private static bool IsWineName(string candidate)
        {
            foreach (string name in WineNames)
            {
                foreach (string suffix in WineSuffixes)
                {
                    if (candidate.AsSpan().StartsWith(name, StringComparison.Ordinal) &&
                        candidate.AsSpan(name.Length).SequenceEqual(suffix))
                    {
                        return true;
                    }
                }
            }
            return candidate is "winepath" or "winepath-stable" or "winepath-development" or "winepath-staging";
        }
    }

    private enum WineExecutableKind
    {
        Native,
        AppLoader,
        WineLauncher,
        ServerLauncher,
        StaticPreloader
    }

    private sealed record SupportedWineWrapper(
        WineExecutableKind Kind,
        string[] Utilities,
        string[] RequiredNames,
        string[] LiteralTargets,
        bool RequiresLiteralLoader = false,
        bool DerivesLoaderSuffix = false)
    {
        internal bool IsNative => Kind == WineExecutableKind.Native;
        internal bool IsAppLoader => Kind == WineExecutableKind.AppLoader;

    }

    private static SupportedWineWrapper ReadSupportedWineWrapper(string protectedAlias)
    {
        try
        {
            using FileStream stream = OpenProtectedRead(protectedAlias);
            Span<byte> header = stackalloc byte[64];
            int length = stream.ReadAtLeast(header, minimumBytes: 64, throwOnEndOfStream: false);
            if (length >= 16 && header[..4].SequenceEqual("\u007FELF"u8))
            {
                NativeElfImage image = NativeElfReader.Read(stream) ?? throw UnsupportedWrapper();
                if (image.ObjectType is not (2 or 3))
                {
                    throw UnsupportedWrapper();
                }
                return new SupportedWineWrapper(WineExecutableKind.Native, [], [], []);
            }
            if (length < 2 || header[0] != '#' || header[1] != '!' || stream.Length > 16 * 1024)
            {
                throw UnsupportedWrapper();
            }
            stream.Position = 0;
            using var reader = new StreamReader(stream, NativePathEncoding, detectEncodingFromByteOrderMarks: false);
            string source = reader.ReadToEnd();
            if (!source.StartsWith("#!/bin/sh\n", StringComparison.Ordinal) &&
                !source.StartsWith("#!/bin/sh -e\n", StringComparison.Ordinal))
            {
                throw UnsupportedWrapper(); // Never validate a different shebang pathname than Linux will execute.
            }
            return RecognizeWineShellScript(NormalizeWineShellScript(source));
        }
        catch (OperationFailureException)
        {
            throw UnsupportedWrapper();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            // Execute-only metadata remains valid for the shared APIs, but an uninspectable wrapper
            // cannot establish the finite helper closure required before production key staging.
            throw UnsupportedWrapper();
        }
    }

    private static string NormalizeWineShellScript(string script)
    {
        var lines = new List<string>();
        foreach (string line in script.Split('\n'))
        {
            string statement = line.Trim();
            if (statement.Length != 0 && (statement[0] != '#' || statement.StartsWith("#!", StringComparison.Ordinal)))
            {
                lines.Add(statement);
            }
        }
        return string.Join('\n', lines);
    }

    private static SupportedWineWrapper RecognizeWineShellScript(string script)
    {
        if (TryExtractShellValue(script, "\nexec wine", " \"$name.exe\" \"$@\"", out string suffix) &&
            IsWinePackageSuffix(suffix) &&
            MatchesWineFingerprint(script.Replace(
                string.Concat("\nexec wine", suffix, " "), "\nexec wine@suffix@ ", StringComparison.Ordinal), DebianAppLoaderHash))
        {
            return new(WineExecutableKind.AppLoader, ["basename", "cut"], [string.Concat("wine", suffix)], []);
        }
        if (MatchesWineFingerprint(script, LegacyDebianAppLoaderHash))
        {
            return new(WineExecutableKind.AppLoader, ["basename", "cut", "sed"], [], [], DerivesLoaderSuffix: true);
        }
        if (MatchesWineFingerprint(script, UbuntuStaticPreloaderHash))
        {
            return new(WineExecutableKind.StaticPreloader, [], [], []);
        }
        if (TryExtractShellValue(script, "\nif [ -x \"/", "/wine\" ];", out string bindirTail))
        {
            string bindir = string.Concat("/", bindirTail);
            if (IsLiteralShellPath(bindir, quoted: true) &&
                MatchesWineFingerprint(script.Replace(
                    string.Concat("\"", bindir, "/wine"), "\"@bindir@/wine", StringComparison.Ordinal), UpstreamAppLoaderHash))
            {
                return new(WineExecutableKind.AppLoader, ["basename", "dirname"], [], [Path.Join(bindir, "wine"), Path.Join(bindir, "wine64")]);
            }
        }
        if (TryExtractInstallDirectory(script, "wine64", "wine64", out string wine64Directory) &&
            TryExtractInstallDirectory(script, "wine32", "wine32", out string wine32Directory) &&
            string.Equals(wine64Directory, wine32Directory, StringComparison.Ordinal))
        {
            string canonical = NormalizeInstallAssignment(script, "wine64", wine64Directory, "wine64");
            canonical = NormalizeInstallAssignment(canonical, "wine32", wine32Directory, "wine32");
            if (MatchesWineFingerprint(canonical, DebianWineLauncherHash))
            {
                return new(WineExecutableKind.WineLauncher, [], [], [Path.Join(wine64Directory, "wine64"), Path.Join(wine32Directory, "wine32")],
                    RequiresLiteralLoader: true);
            }
        }
        if (TryExtractInstallDirectory(script, "wine32", "wine", out wine32Directory) &&
            TryExtractInstallDirectory(script, "wine64", "wine64", out wine64Directory) &&
            string.Equals(wine64Directory, wine32Directory, StringComparison.Ordinal) &&
            TryExtractShellValue(script, "echo \"it looks like wine32", " is missing", out string version) &&
            IsWinePackageSuffix(version))
        {
            string canonical = NormalizeInstallAssignment(script, "wine32", wine32Directory, "wine");
            canonical = NormalizeInstallAssignment(canonical, "wine64", wine64Directory, "wine64");
            canonical = canonical.Replace(string.Concat("wine32", version, " is missing"), "wine32@version@ is missing", StringComparison.Ordinal)
                .Replace(string.Concat("wine32", version, ":i386"), "wine32@version@:i386", StringComparison.Ordinal);
            if (MatchesWineFingerprint(canonical, UbuntuWineLauncherHash))
            {
                return new(WineExecutableKind.WineLauncher, ["dpkg", "grep"], [], [Path.Join(wine32Directory, "wine"), Path.Join(wine64Directory, "wine64")],
                    RequiresLiteralLoader: true);
            }
        }
        if (TryExtractInstallDirectory(script, "wineserver32", "wineserver32", out string server32Directory) &&
            TryExtractInstallDirectory(script, "wineserver64", "wineserver64", out string server64Directory) &&
            string.Equals(server32Directory, server64Directory, StringComparison.Ordinal) &&
            TryExtractShellValue(script, "echo \"wine32", " and/or wine64", out version) &&
            IsWinePackageSuffix(version))
        {
            string canonical = NormalizeInstallAssignment(script, "wineserver32", server32Directory, "wineserver32");
            canonical = NormalizeInstallAssignment(canonical, "wineserver64", server64Directory, "wineserver64");
            canonical = canonical.Replace(
                string.Concat("wine32", version, " and/or wine64", version, " must be installed"),
                "wine32@version@ and/or wine64@version@ must be installed", StringComparison.Ordinal);
            if (MatchesWineFingerprint(canonical, UbuntuWineServerHash))
            {
                return new(WineExecutableKind.ServerLauncher, [], [], [Path.Join(server64Directory, "wineserver64"), Path.Join(server32Directory, "wineserver32")],
                    RequiresLiteralLoader: true);
            }
        }
        if (TryExtractShellValue(script, "\nexec ", " \"$@\"", out string loader) && IsLiteralShellPath(loader, quoted: false))
        {
            string canonical = script.Replace(string.Concat("\nexec ", loader, " "), "\nexec @loader@ ", StringComparison.Ordinal);
            if (MatchesWineFingerprint(canonical, DebianWine64LauncherHash) || MatchesWineFingerprint(canonical, DebianWine32LauncherHash))
            {
                return new(WineExecutableKind.WineLauncher, [], [], [loader], RequiresLiteralLoader: true);
            }
        }
        throw UnsupportedWrapper();
    }

    private static bool TryExtractShellValue(string script, string prefix, string suffix, out string value)
    {
        value = string.Empty;
        int start = script.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }
        start += prefix.Length;
        int length = script.AsSpan(start).IndexOf(suffix, StringComparison.Ordinal);
        if (length < 0)
        {
            return false;
        }
        value = script.Substring(start, length);
        return !value.Contains('\n') && !value.Contains('\r');
    }

    private static bool TryExtractInstallDirectory(string script, string variable, string name, out string directory)
    {
        directory = string.Empty;
        if (!TryExtractShellValue(script, string.Concat("\n", variable, "="), "\n", out string target) ||
            !IsLiteralShellPath(target, quoted: false) || !target.EndsWith(string.Concat("/", name), StringComparison.Ordinal))
        {
            return false;
        }
        directory = target[..^(name.Length + 1)];
        if (directory.Length == 0)
        {
            directory = "/";
        }
        return true;
    }

    private static string NormalizeInstallAssignment(string script, string variable, string directory, string name) =>
        script.Replace(string.Concat("\n", variable, "=", Path.Join(directory, name)),
            string.Concat("\n", variable, "=@bindir@/", name), StringComparison.Ordinal);

    private static bool IsWinePackageSuffix(string suffix) =>
        suffix is "" or "-stable" or "-development" or "-staging";

    private static bool MatchesWineFingerprint(string normalizedScript, string approvedHash)
    {
        int length = NativePathEncoding.GetByteCount(normalizedScript);
        byte[] bytes = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            NativePathEncoding.GetBytes(normalizedScript.AsSpan(), bytes);
            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(bytes.AsSpan(0, length), digest);
            const string hexadecimal = "0123456789ABCDEF";
            for (int index = 0; index < digest.Length; index++)
            {
                if (approvedHash[index * 2] != hexadecimal[digest[index] >> 4] ||
                    approvedHash[index * 2 + 1] != hexadecimal[digest[index] & 15])
                {
                    return false;
                }
            }
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    private static bool IsLiteralShellPath(string path, bool quoted)
    {
        if (!Path.IsPathFullyQualified(path) || path.Contains('\0'))
        {
            return false;
        }
        foreach (char character in path)
        {
            if (quoted)
            {
                if (character is '$' or '`' or '"' or '\\' or '\r' or '\n')
                {
                    return false;
                }
            }
            else if (!(char.IsAsciiLetterOrDigit(character) || character is '/' or '.' or '_' or '-' or '+'))
            {
                return false;
            }
        }
        return true;
    }

    private static OperationFailureException UnsupportedWrapper() => new(
        ExitCode.MissingPrerequisite,
        "wine-wrapper-unsupported",
        "Wine executable wrappers must use a supported installed Wine launcher. Install Wine and winepath from a trusted package.");

    // Fingerprints contain no imported Wine source. Only blank/comment lines and indentation,
    // plus checked literal installation-directory/package substitutions, are normalized.
    // Generated winegcc launchers and source/build-tree .winewrapper scripts are deliberately
    // unsupported: their DLL/appdir/configuration closure is not established by this finite PATH.
    // https://sources.debian.org/src/wine/10.0~repack-12/debian/scripts/
    private const string DebianAppLoaderHash = "2C39B4C2B4B883406203B37B554C4AB080BC61317FB62D361A6DCC283EBA39FB";
    private const string DebianWineLauncherHash = "4C2ED4C74ED6CA06D79276A1C170610A7C2A999AC3E344CCB14006DDA463627A";
    private const string DebianWine64LauncherHash = "E63A13BBD5DC9AD4AF3C931DF0C5FADEFF3C267CA9FD22F5485E2399897DED44";
    private const string DebianWine32LauncherHash = "EAF4906AFBCE1A0A89C10DA330B3595096169695AC4062289D7C6EE5034A1F6C";
    // https://raw.githubusercontent.com/wine-mirror/wine/wine-10.0/tools/wineapploader.in
    private const string UpstreamAppLoaderHash = "03995AD56BBE7B5CA6CD098FA5CCDB12CE77D5FC1BE5B14C6AE6EF704C935A62";
    // https://sources.debian.org/src/wine-development/1.7.29-4/tools/wineapploader.in/
    private const string LegacyDebianAppLoaderHash = "122C2057D3CE83755AF7225628636FCD712F365D1D3FA312CD383B0F8FD25A6B";
    // https://git.launchpad.net/ubuntu/+source/wine/tree/debian/scripts?h=ubuntu/noble
    private const string UbuntuWineLauncherHash = "7CD35D8F3BC2A70F993B4068B17FAA34CCA3781041FD7ACEEE8FC89290D0F682";
    private const string UbuntuWineServerHash = "A660A65B8C493E5ACC72C8763E92AB686F5EDF69BDB6F477C4FB80289B1F8CEF";
    private const string UbuntuStaticPreloaderHash = "1453B8B350B36DE402FC4BA0DBFC50B20E0046C9BEE76448199487EAC4420FF2";

    private static void ValidateDirectoryContents(
        SafeFileHandle directory,
        string absoluteDirectory,
        uint effectiveUserId,
        HashSet<(uint DeviceMajor, uint DeviceMinor, ulong Inode)>? visitedDirectories)
    {
        bool executableOnly = visitedDirectories is null;
        if (visitedDirectories is not null)
        {
            LinuxStatx directoryStatus = ReadStatus(directory, forWinePath: false);
            if (!IsTrustedDirectory(in directoryStatus, effectiveUserId))
            {
                throw Unavailable(forWinePath: false);
            }
            if (!visitedDirectories.Add((directoryStatus.DeviceMajor, directoryStatus.DeviceMinor, directoryStatus.Inode)))
            {
                return;
            }
        }

        // Open "." relative to the already protected descriptor, never an unchecked pathname.
        // Only directory descriptors are opened for reading; every leaf stays O_PATH, including FIFOs/devices.
        int descriptor = OpenAt(directory, ".", ReadableDirectoryFlags, mode: 0);
        if (descriptor < 0)
        {
            throw Unavailable(forWinePath: false);
        }
        using var readable = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        IntPtr iterator = OpenDirectoryIterator(descriptor);
        if (iterator == IntPtr.Zero)
        {
            throw Unavailable(forWinePath: false);
        }
        readable.SetHandleAsInvalid(); // fdopendir owns the descriptor until closedir.
        byte[] nameBuffer = ArrayPool<byte>.Shared.Rent(256);
        try
        {
            while (true)
            {
                Marshal.SetLastPInvokeError(0);
                IntPtr entry = ReadDirectoryEntry(iterator);
                if (entry == IntPtr.Zero)
                {
                    if (Marshal.GetLastPInvokeError() != 0)
                    {
                        throw Unavailable(forWinePath: false);
                    }
                    break;
                }
                string name = ReadDirectoryEntryName(entry, ref nameBuffer);
                if (name is "." or "..")
                {
                    continue;
                }

                int childDescriptor = OpenAt(directory, name, ExecutableFlags, mode: 0);
                if (childDescriptor < 0)
                {
                    if (executableOnly && IsUnavailableCandidate(Marshal.GetLastPInvokeError()))
                    {
                        continue;
                    }
                    throw Unavailable(forWinePath: false);
                }
                using var child = new SafeFileHandle((IntPtr)childDescriptor, ownsHandle: true);
                LinuxStatx status = ReadStatus(child, forWinePath: false);
                if (executableOnly)
                {
                    if ((status.Mode & LinuxFileTypeMask) == LinuxSymbolicLink)
                    {
                        using SafeFileHandle? helper = OpenProtectedAliasOrSkip(
                            Path.Join(absoluteDirectory, name), effectiveUserId, forWinePath: false, ProtectedPathKind.PathHelper);
                    }
                    else
                    {
                        IsProtectedExecutableHelper(child, in status, effectiveUserId);
                    }
                    continue;
                }
                SafeFileHandle inspected = child;
                SafeFileHandle? target = null;
                string? childPath = null;
                try
                {
                    if ((status.Mode & LinuxFileTypeMask) == LinuxSymbolicLink)
                    {
                        childPath = Path.Join(absoluteDirectory, name);
                        // Dependencies, unlike PATH entries, require every link and target to exist
                        // and remain protected even when the final regular file is not executable.
                        target = OpenProtectedAlias(childPath, effectiveUserId, forWinePath: false, ProtectedPathKind.Entry);
                        inspected = target;
                        status = ReadStatus(target, forWinePath: false);
                    }

                    switch (status.Mode & LinuxFileTypeMask)
                    {
                        case LinuxRegularFile:
                            if (!HasTrustedOwnershipAndPermissions(in status, effectiveUserId))
                            {
                                throw Unavailable(forWinePath: false);
                            }
                            break;
                        case LinuxDirectoryFile:
                            childPath ??= Path.Join(absoluteDirectory, name);
                            ValidateDirectoryContents(inspected, childPath, effectiveUserId, visitedDirectories);
                            break;
                        default:
                            throw Unavailable(forWinePath: false);
                    }
                }
                finally
                {
                    target?.Dispose();
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(nameBuffer);
            if (CloseDirectoryIterator(iterator) != 0)
            {
                throw Unavailable(forWinePath: false);
            }
        }
    }

    private static bool IsProtectedExecutableHelper(SafeFileHandle handle, in LinuxStatx status, uint effectiveUserId)
    {
        if ((status.Mode & LinuxFileTypeMask) != LinuxRegularFile)
        {
            return false;
        }
        // A foreign owner can chmod a currently non-executable leaf after private data is staged.
        // Trusted owners may expose writable non-command data, but executable helpers stay strict.
        if (!HasTrustedOwner(in status, effectiveUserId))
        {
            throw Unavailable(forWinePath: false);
        }
        if ((status.Mode & LinuxExecuteBits) == 0 || !HasExecuteAccess(handle, forWinePath: false))
        {
            return false;
        }
        if (!HasTrustedOwnershipAndPermissions(in status, effectiveUserId))
        {
            throw Unavailable(forWinePath: false);
        }
        return true;
    }

    private static string ReadDirectoryEntryName(IntPtr entry, ref byte[] buffer)
    {
        // Linux dirent64 stores its record length at byte 16 and its NUL-terminated name at byte 19.
        int capacity = (ushort)Marshal.ReadInt16(entry, 16) - 19;
        int length = 0;
        while (length < capacity && Marshal.ReadByte(entry, 19 + length) != 0)
        {
            length++;
        }
        if (length == 0 || length >= capacity)
        {
            throw Unavailable(forWinePath: false);
        }
        if (length > buffer.Length)
        {
            byte[] replacement = ArrayPool<byte>.Shared.Rent(length);
            ArrayPool<byte>.Shared.Return(buffer);
            buffer = replacement;
        }
        Marshal.Copy(IntPtr.Add(entry, 19), buffer, 0, length);
        // Replacement decoding would inspect a different path and could silently omit an unsafe helper.
        return DecodeNativePath(buffer, length, forWinePath: false);
    }

    private static string ResolveTrustedDirectory(
        string absoluteAlias,
        uint effectiveUserId,
        bool forWinePath,
        bool aliasAlreadyValidated = false)
    {
        string canonicalPath = Canonicalize(absoluteAlias, forWinePath) ?? throw Unavailable(forWinePath);
        if (!IsTrustedCanonicalPath(canonicalPath, effectiveUserId, forWinePath, requireExecutable: false))
        {
            throw Unavailable(forWinePath);
        }
        if (!aliasAlreadyValidated)
        {
            using SafeFileHandle alias = OpenProtectedAlias(
                absoluteAlias, effectiveUserId, forWinePath, ProtectedPathKind.Directory);
        }
        return canonicalPath;
    }

    private static SafeFileHandle OpenRoot(bool forWinePath)
    {
        int descriptor = OpenNative("/", DirectoryFlags, mode: 0);
        if (descriptor < 0)
        {
            throw Unavailable(forWinePath);
        }
        return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
    }

    private static LinuxStatx ReadStatus(SafeFileHandle handle, bool forWinePath)
    {
        if (Statx(handle, string.Empty, LinuxEmptyPath | LinuxStatxForceSync, LinuxStatxRequiredFields, out LinuxStatx status) != 0 ||
            (status.Mask & LinuxStatxRequiredFields) != LinuxStatxRequiredFields)
        {
            throw Unavailable(forWinePath);
        }
        return status;
    }

    private static bool IsTrustedDirectory(in LinuxStatx status, uint effectiveUserId)
    {
        // Sticky directories are not an exception: every canonical ancestor must prohibit other-UID writes.
        return (status.Mode & LinuxFileTypeMask) == LinuxDirectoryFile &&
            HasTrustedOwnershipAndPermissions(in status, effectiveUserId);
    }

    private static bool HasTrustedOwnershipAndPermissions(in LinuxStatx status, uint effectiveUserId)
    {
        return HasTrustedOwner(in status, effectiveUserId) &&
            (status.Mode & LinuxGroupOrOtherWrite) == 0;
    }

    private static bool HasTrustedOwner(in LinuxStatx status, uint effectiveUserId)
    {
        return status.OwnerUserId == 0 || status.OwnerUserId == effectiveUserId;
    }

    private static bool HasExecuteAccess(SafeFileHandle handle, bool forWinePath)
    {
        if (AccessAt(handle, string.Empty, LinuxExecuteAccess, LinuxEmptyPath | LinuxEffectiveAccess) == 0)
        {
            return true;
        }
        if (Marshal.GetLastPInvokeError() is LinuxPermissionDenied or LinuxOperationNotPermitted)
        {
            return false;
        }
        // In particular, unsupported descriptor/effective-ID flags must not fall back to PATH or mode-only access.
        throw Unavailable(forWinePath);
    }

    private static bool IsUnavailableCandidate(int error)
    {
        return error is LinuxNoSuchFileOrDirectory or LinuxNotADirectory or LinuxPermissionDenied or LinuxSymbolicLinkLoop;
    }

    private static bool IsNativeSupportFailure(Exception exception)
    {
        return exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or MarshalDirectiveException;
    }

    private static bool IsTrustValidationFailure(Exception exception)
    {
        return IsNativeSupportFailure(exception) ||
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException;
    }

    private static OperationFailureException Unavailable(bool forWinePath)
    {
        return new OperationFailureException(
            ExitCode.MissingPrerequisite,
            forWinePath ? "winepath-unavailable" : "wine-unavailable",
            forWinePath
                ? "Wine path conversion is required to run XeBuild but is not available from a trusted executable location. Install winepath."
                : "Wine is required to run XeBuild but is not available from a trusted executable location. Install Wine and winepath.");
    }

    // statx has a stable, architecture-independent 256-byte Linux ABI, unlike struct stat.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatx
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(20)] internal uint OwnerUserId;
        [FieldOffset(28)] internal ushort Mode;
        [FieldOffset(32)] internal ulong Inode;
        [FieldOffset(40)] internal ulong Size;
        [FieldOffset(96)] internal long ChangeSeconds;
        [FieldOffset(104)] internal uint ChangeNanoseconds;
        [FieldOffset(112)] internal long ModificationSeconds;
        [FieldOffset(120)] internal uint ModificationNanoseconds;
        [FieldOffset(136)] internal uint DeviceMajor;
        [FieldOffset(140)] internal uint DeviceMinor;
    }

    [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
    private static extern IntPtr RealPath(string path, IntPtr resolvedPath);

    [DllImport("libc", EntryPoint = "free")]
    private static extern void Free(IntPtr allocation);

    [DllImport("libc", EntryPoint = "strlen")]
    private static extern nuint StringLength(IntPtr value);

    [DllImport("libc", EntryPoint = "readlinkat", SetLastError = true)]
    private static extern nint ReadLinkAt(SafeFileHandle descriptor, string path, [Out] byte[] bytes, nuint size);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenNative(string path, int flags, uint mode);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(SafeFileHandle directory, string path, int flags, uint mode);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(SafeFileHandle descriptor, string path, int flags, uint mask, out LinuxStatx status);

    [DllImport("libc", EntryPoint = "faccessat", SetLastError = true)]
    private static extern int AccessAt(SafeFileHandle descriptor, string path, int mode, int flags);

    [DllImport("libc", EntryPoint = "fdopendir", SetLastError = true)]
    private static extern IntPtr OpenDirectoryIterator(int descriptor);

    [DllImport("libc", EntryPoint = "readdir64", SetLastError = true)]
    private static extern IntPtr ReadDirectoryEntry(IntPtr iterator);

    [DllImport("libc", EntryPoint = "closedir", SetLastError = true)]
    private static extern int CloseDirectoryIterator(IntPtr iterator);

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    [DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetRealUserId();
}
