using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace JRunner.Cli.Infrastructure;

/// <summary>
/// Binds an output directory and its Linux ancestors without following links. All Linux
/// file operations stay relative to the retained directory descriptor, including cleanup.
/// </summary>
internal sealed class AtomicOutputDirectory : IDisposable
{
    private const int LinuxReadOnly = 0;
    private const int LinuxWriteOnly = 1;
    private const int LinuxReadWrite = 2;
    private const int LinuxCreate = 0x40;
    private const int LinuxExclusive = 0x80;
    private const int LinuxNonBlocking = 0x800;
    private const int LinuxDirectory = 0x10000;
    private const int LinuxNoFollow = 0x20000;
    private const int LinuxCloseOnExec = 0x80000;
    private const int LinuxPath = 0x200000;
    private const int LinuxDuplicateCloseOnExec = 1030;
    private const int LinuxNoFollowAt = 0x100;
    private const int LinuxEmptyPath = 0x1000;
    private const uint LinuxStatxRequired = 0x3CF;
    private const uint LinuxPrivateFileMode = 0x180;
    private const uint LinuxPrivateDirectoryMode = 0x1C0;
    private const int LinuxRemoveDirectory = 0x200;
    private const int LinuxTypeMask = 0xF000;
    private const int LinuxRegularFile = 0x8000;
    private const int LinuxDirectoryType = 0x4000;
    private const int LinuxSymbolicLink = 0xA000;
    private const int LinuxWritableByOthers = 0x12;
    private const int LinuxSticky = 0x200;
    private const int LinuxNoSuchFile = 2;
    private const int LinuxAlreadyExists = 17;
    private const int LinuxNotADirectory = 20;
    private const int LinuxSymbolicLinkLoop = 40;
    private const uint LinuxRenameNoReplace = 1;
    private const uint WindowsReadAttributes = 0x80;
    private const uint WindowsGenericRead = 0x80000000;
    private const uint WindowsGenericWrite = 0x40000000;
    private const uint WindowsShareAll = 7;
    private const uint WindowsOpenExisting = 3;
    private const uint WindowsBackupSemantics = 0x02000000;
    private const uint WindowsOpenReparsePoint = 0x00200000;
    private const uint WindowsDirectoryAttribute = 0x10;
    private const uint WindowsReparsePointAttribute = 0x400;

    private readonly string _path;
    private readonly List<DirectoryBinding> _directories;
    private readonly uint _effectiveUserId;
    private readonly bool _privateRoot;
    private bool _disposed;

    private AtomicOutputDirectory(string path, List<DirectoryBinding> directories, uint effectiveUserId, bool privateRoot = false)
    {
        _path = path;
        _directories = directories;
        _effectiveUserId = effectiveUserId;
        _privateRoot = privateRoot;
    }

    private SafeFileHandle Handle => _directories[^1].Handle;

    internal string DirectoryPath => _path;
    internal AtomicOutputFileIdentity Identity => _directories[^1].Identity;

    /// <summary>
    /// Binds no-follow directory identities. The caller checks nonwriting destination facts
    /// before calling EnsurePathSafe to authorize any creation or publication.
    /// </summary>
    internal static AtomicOutputDirectory Open(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            WorkspacePathSafety.EnsureNoLinkAncestors(path);
            if (!Directory.Exists(path))
            {
                throw AtomicOutputPath.MissingDirectory();
            }

            using SafeFileHandle probe = OpenWindowsPath(path, WindowsReadAttributes);
            NativeStatus status = ReadStatus(probe);
            EnsureDirectory(status);
            SafeFileHandle retained = DuplicateHandle(probe);
            return new AtomicOutputDirectory(path, [new DirectoryBinding(path, retained, status.State.Identity)], 0);
        }

        uint effectiveUserId = GetEffectiveUserId();
        var directories = new List<DirectoryBinding>();
        try
        {
            SafeFileHandle root = OpenLinuxDirectory("/", parent: null, pathChanged: false);
            try
            {
                directories.Add(new DirectoryBinding("/", root, ReadStatus(root).State.Identity));
            }
            catch
            {
                root.Dispose();
                throw;
            }
            int start = 1;
            while (start < path.Length)
            {
                int end = path.IndexOf(Path.DirectorySeparatorChar, start);
                if (end < 0)
                {
                    end = path.Length;
                }
                if (end > start)
                {
                    string component = path[start..end];
                    SafeFileHandle child = OpenLinuxDirectory(component, directories[^1].Handle, pathChanged: false);
                    try
                    {
                        directories.Add(new DirectoryBinding(component, child, ReadStatus(child).State.Identity));
                    }
                    catch
                    {
                        child.Dispose();
                        throw;
                    }
                }
                start = end + 1;
            }

            return new AtomicOutputDirectory(path, directories, effectiveUserId);
        }
        catch
        {
            foreach (DirectoryBinding directory in directories)
            {
                directory.Handle.Dispose();
            }
            throw;
        }
    }

    /// <summary>
    /// Rechecks ownership, permissions, and the identity of every pathname component.
    /// Directory timestamps are deliberately not identities: creating an output changes them.
    /// </summary>
    internal void EnsurePathSafe()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!OperatingSystem.IsLinux())
        {
            try
            {
                WorkspacePathSafety.EnsureNoLinkAncestors(_path);
                using SafeFileHandle current = OpenWindowsPath(_path, WindowsReadAttributes);
                NativeStatus status = ReadStatus(current);
                EnsureDirectory(status);
                if (status.State.Identity != _directories[0].Identity)
                {
                    throw AtomicOutputPath.OutputChanged(externalOutput: false);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException ||
                                             exception is JRunner.Core.Contracts.OperationFailureException failure &&
                                             failure.Kind == "workspace-path-unsafe")
            {
                throw AtomicOutputPath.OutputChanged(externalOutput: false);
            }
            return;
        }

        SafeFileHandle currentDirectory = OpenLinuxDirectory("/", parent: null, pathChanged: true);
        try
        {
            for (int index = 0; index < _directories.Count; index++)
            {
                DirectoryBinding expected = _directories[index];
                NativeStatus status = ReadStatus(currentDirectory);
                EnsureTrustedDirectory(status, _effectiveUserId);
                if (status.State.Identity != expected.Identity)
                {
                    throw AtomicOutputPath.OutputChanged(externalOutput: false);
                }
                if (_privateRoot && index == _directories.Count - 1)
                {
                    EnsurePrivateDirectory(status);
                }

                if (index + 1 < _directories.Count)
                {
                    SafeFileHandle child = OpenLinuxDirectory(_directories[index + 1].Name, currentDirectory, pathChanged: true);
                    currentDirectory.Dispose();
                    currentDirectory = child;
                }
            }

            // These retained descriptors also catch permissions changed behind the pathname walk.
            EnsureBoundDirectoryTrust();
        }
        finally
        {
            currentDirectory.Dispose();
        }
    }

    internal void EnsureDestinationAvailable(AtomicOutputDestination destination)
    {
        if (!TryReadNamedStatus(Path.GetFileName(destination.DestinationPath), out NativeStatus status))
        {
            return;
        }
        if ((status.Mode & LinuxTypeMask) == LinuxSymbolicLink)
        {
            throw AtomicOutputPath.UnsafePath();
        }
        if ((status.Mode & LinuxTypeMask) == LinuxDirectoryType)
        {
            throw AtomicOutputPath.InvalidDestinationPath();
        }
        if (!destination.Force)
        {
            throw AtomicOutputPath.DestinationAlreadyExists();
        }
    }

    internal AtomicOutputDirectory CreatePrivateStagingDirectory(string name)
    {
        EnsurePathSafe();
        string path = Path.Combine(_path, name);
        SafeFileHandle? child = null;
        List<DirectoryBinding>? bindings = null;
        AtomicOutputFileIdentity? createdIdentity = null;
        try
        {
            if (OperatingSystem.IsLinux())
            {
                if (MakeDirectoryAt(Handle, name, LinuxPrivateDirectoryMode) != 0)
                {
                    throw NativeFailure("The private output staging directory could not be created.");
                }
                child = OpenLinuxDirectory(name, Handle, pathChanged: false);
                NativeStatus status = ReadStatus(child);
                createdIdentity = status.State.Identity;
                if ((status.Mode & LinuxTypeMask) != LinuxDirectoryType || status.Owner != _effectiveUserId)
                {
                    throw AtomicOutputPath.OutputChanged(externalOutput: false);
                }
                if ((status.Mode & 0xFFF) != LinuxPrivateDirectoryMode &&
                    ChangeDirectoryModeAt(Handle, name, LinuxPrivateDirectoryMode, LinuxNoFollowAt) != 0)
                {
                    throw NativeFailure("The private output staging directory permissions could not be secured.");
                }
            }
            else
            {
                if (!CreateWindowsDirectory(path, IntPtr.Zero))
                {
                    throw NativeFailure("The private output staging directory could not be created.");
                }
                child = OpenWindowsPath(path, WindowsReadAttributes);
                createdIdentity = ReadStatus(child).State.Identity;
            }

            bindings = new List<DirectoryBinding>(OperatingSystem.IsLinux() ? _directories.Count + 1 : 1);
            if (OperatingSystem.IsLinux())
            {
                foreach (DirectoryBinding parent in _directories)
                {
                    bindings.Add(new DirectoryBinding(parent.Name, DuplicateHandle(parent.Handle), parent.Identity));
                }
            }
            bindings.Add(new DirectoryBinding(OperatingSystem.IsLinux() ? name : path, child, createdIdentity.Value));
            child = null;
            var staging = new AtomicOutputDirectory(path, bindings, _effectiveUserId, privateRoot: true);
            staging.EnsurePathSafe();
            return staging;
        }
        catch
        {
            try
            {
                if (createdIdentity is { } identity)
                {
                    RemoveStagingDirectory(name, identity);
                }
            }
            catch
            {
                // Retain the creation failure without unlinking an unverified replacement.
            }
            finally
            {
                child?.Dispose();
                if (bindings is not null)
                {
                    foreach (DirectoryBinding binding in bindings)
                    {
                        binding.Handle.Dispose();
                    }
                }
            }
            throw;
        }
    }

    internal AtomicOutputFileState SecureExternalOutput(SafeFileHandle handle, AtomicOutputFileState expected)
    {
        NativeStatus status = ReadStatus(handle);
        if (!IsPrivateExternalOutput(status) || status.State != expected)
        {
            throw AtomicOutputPath.OutputChanged(externalOutput: true);
        }
        if (OperatingSystem.IsLinux() && (status.Mode & 0xFFF) != LinuxPrivateFileMode)
        {
            if (ChangeMode(handle, LinuxPrivateFileMode) != 0)
            {
                throw NativeFailure("The output publication permissions could not be secured.");
            }
            return GetStateAfterOwnMetadataChange(handle, expected);
        }
        return expected;
    }

    internal AtomicOutputFileState GetStateAfterOwnMetadataChange(SafeFileHandle handle, AtomicOutputFileState expected)
    {
        AtomicOutputFileState current = GetFileState(handle, externalOutput: true);
        if (current.Identity != expected.Identity || current.Length != expected.Length ||
            current.ModificationSeconds != expected.ModificationSeconds ||
            current.ModificationNanoseconds != expected.ModificationNanoseconds)
        {
            throw AtomicOutputPath.OutputChanged(externalOutput: true);
        }
        // fchmod and rename can change ctime. The subsequent fresh digest still binds bytes,
        // and publication requires the complete post-operation change state to remain stable.
        return current;
    }

    internal void MoveTo(AtomicOutputDirectory destination, string name, string destinationName,
        SafeFileHandle handle, AtomicOutputFileState expected)
    {
        EnsureFileUnchanged(name, handle, expected, externalOutput: true);
        destination.EnsurePathSafe();
        if (OperatingSystem.IsLinux())
        {
            if (RenameAt(Handle, name, destination.Handle, destinationName, LinuxRenameNoReplace) != 0)
            {
                if (Marshal.GetLastPInvokeError() == LinuxNoSuchFile)
                {
                    throw AtomicOutputPath.OutputChanged(externalOutput: true);
                }
                throw NativeFailure("The output publication path could not be reserved.");
            }
            return;
        }
        File.Move(Path.Combine(_path, name), Path.Combine(destination._path, destinationName), overwrite: false);
    }

    internal void DeleteEntry(string name)
    {
        if (OperatingSystem.IsLinux())
        {
            DeleteLinuxEntry(Handle, name);
            return;
        }
        EnsurePathSafe();
        string entry = Path.Combine(_path, name);
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(entry);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return;
        }
        if ((attributes & FileAttributes.Directory) != 0)
        {
            Directory.Delete(entry, recursive: (attributes & FileAttributes.ReparsePoint) == 0);
        }
        else
        {
            File.Delete(entry);
        }
    }

    internal void DeleteContents()
    {
        if (OperatingSystem.IsLinux())
        {
            DeleteLinuxContents(Handle);
            return;
        }
        EnsurePathSafe();
        foreach (string entry in Directory.EnumerateFileSystemEntries(_path))
        {
            DeleteEntry(Path.GetFileName(entry));
        }
    }

    internal void PrepareStagingCleanup(string name, AtomicOutputDirectory staging)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        NativeStatus status = ReadStatus(staging.Handle);
        if (status.Owner != _effectiveUserId || (status.Mode & 0xFFF) == LinuxPrivateDirectoryMode)
        {
            return;
        }
        if (!TryReadNamedStatus(name, out NativeStatus named) ||
            (named.Mode & LinuxTypeMask) != LinuxDirectoryType || named.State.Identity != staging.Identity)
        {
            throw AtomicOutputPath.OutputChanged(externalOutput: false);
        }
        // An external writer can remove the owner's search/read bits. Restore only our
        // verified staging inode, through the held parent, so failure cleanup can finish.
        if (ChangeDirectoryModeAt(Handle, name, LinuxPrivateDirectoryMode, LinuxNoFollowAt) != 0)
        {
            throw NativeFailure("The private output staging directory could not be prepared for cleanup.");
        }
    }

    internal void RemoveStagingDirectory(string name, AtomicOutputFileIdentity expected)
    {
        if (!TryReadNamedStatus(name, out NativeStatus status))
        {
            return;
        }
        if ((status.Mode & LinuxTypeMask) != LinuxDirectoryType || status.State.Identity != expected)
        {
            throw AtomicOutputPath.OutputChanged(externalOutput: false);
        }
        if (OperatingSystem.IsLinux())
        {
            if (UnlinkAt(Handle, name, LinuxRemoveDirectory) != 0 && Marshal.GetLastPInvokeError() != LinuxNoSuchFile)
            {
                throw NativeFailure("The private output staging directory could not be removed.");
            }
            return;
        }
        EnsurePathSafe();
        Directory.Delete(Path.Combine(_path, name));
    }

    internal FileStream? TryCreateTemporaryFile(string name, FileShare fileShare)
    {
        if (!OperatingSystem.IsLinux())
        {
            string path = Path.Combine(_path, name);
            try
            {
                return new FileStream(path, FileMode.CreateNew, FileAccess.Write, fileShare,
                    bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (IOException) when (File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null)
            {
                return null;
            }
        }

        int descriptor = OpenAt(Handle, name,
            LinuxWriteOnly | LinuxCreate | LinuxExclusive | LinuxNoFollow | LinuxCloseOnExec,
            LinuxPrivateFileMode);
        if (descriptor < 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == LinuxAlreadyExists)
            {
                return null;
            }
            throw NativeFailure("The temporary output file could not be created.", error);
        }

        var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        try
        {
            // Set exactly 0600 through the descriptor, independently of the caller's umask.
            if (ChangeMode(handle, LinuxPrivateFileMode) != 0)
            {
                throw NativeFailure("The temporary output file permissions could not be secured.");
            }
            EnsureRegularFile(ReadStatus(handle), externalOutput: false);
            return new FileStream(handle, FileAccess.Write, bufferSize: 64 * 1024, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            DeleteTemporaryFile(name);
            throw;
        }
    }

    internal FileStream OpenExternalOutput(string name, FileAccess access = FileAccess.Read)
    {
        if (access is not (FileAccess.Read or FileAccess.Write or FileAccess.ReadWrite))
        {
            throw new ArgumentOutOfRangeException(nameof(access));
        }
        EnsurePathSafe();
        SafeFileHandle handle;
        if (OperatingSystem.IsLinux())
        {
            int descriptor = OpenAt(Handle, name,
                (access == FileAccess.Read ? LinuxReadOnly : access == FileAccess.Write ? LinuxWriteOnly : LinuxReadWrite) |
                LinuxNonBlocking | LinuxNoFollow | LinuxCloseOnExec, mode: 0);
            if (descriptor < 0)
            {
                if (Marshal.GetLastPInvokeError() == LinuxNoSuchFile)
                {
                    throw AtomicOutputPath.ExternalOutputMissing();
                }
                throw AtomicOutputPath.ExternalOutputInvalid();
            }
            handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        }
        else
        {
            handle = OpenWindowsPath(Path.Combine(_path, name),
                access == FileAccess.Read ? WindowsGenericRead : access == FileAccess.Write ? WindowsGenericWrite : WindowsGenericRead | WindowsGenericWrite,
                missingExternalOutput: true);
        }

        try
        {
            NativeStatus status = ReadStatus(handle);
            if (!IsPrivateExternalOutput(status))
            {
                throw AtomicOutputPath.ExternalOutputInvalid();
            }
            if (status.State.Length == 0)
            {
                throw AtomicOutputPath.ExternalOutputEmpty();
            }
            EnsureFileUnchanged(name, handle, status.State, externalOutput: true);
            // No FileStream read buffer may survive semantic validation into the final digest.
            return new FileStream(handle, access, bufferSize: 1, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static SafeFileHandle DuplicateHandle(SafeFileHandle handle)
    {
        if (OperatingSystem.IsLinux())
        {
            int descriptor = DuplicateLinuxHandle(handle, LinuxDuplicateCloseOnExec, minimumDescriptor: 0);
            if (descriptor < 0)
            {
                throw NativeFailure("The output identity descriptor could not be retained.");
            }
            return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        }
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Atomic output requires Linux or Windows file identity support.");
        }
        IntPtr process = GetCurrentProcess();
        if (!DuplicateWindowsHandle(process, handle, process, out SafeFileHandle duplicate, 0, false, options: 2))
        {
            throw NativeFailure("The output identity handle could not be retained.");
        }
        return duplicate;
    }

    internal AtomicOutputFileState GetFileState(SafeFileHandle handle, bool externalOutput)
    {
        try
        {
            NativeStatus status = ReadStatus(handle);
            EnsureRegularFile(status, externalOutput);
            if (externalOutput && !IsPrivateExternalOutput(status))
            {
                throw AtomicOutputPath.OutputChanged(externalOutput: true);
            }
            return status.State;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            throw AtomicOutputPath.OutputChanged(externalOutput);
        }
    }

    internal void EnsureFileUnchanged(string name, SafeFileHandle handle, AtomicOutputFileState expected, bool externalOutput)
    {
        EnsurePathSafe();
        if (GetFileState(handle, externalOutput) != expected ||
            !TryReadNamedStatus(name, out NativeStatus current) ||
            (current.Mode & LinuxTypeMask) != LinuxRegularFile || current.State != expected)
        {
            throw AtomicOutputPath.OutputChanged(externalOutput);
        }
    }

    internal void Publish(AtomicOutputDestination destination, string name, SafeFileHandle handle,
        AtomicOutputFileState expected, bool externalOutput)
    {
        EnsureDestinationAvailable(destination);
        EnsurePathSafe();
        if (GetFileState(handle, externalOutput) != expected ||
            !TryReadNamedStatus(name, out NativeStatus current) ||
            (current.Mode & LinuxTypeMask) != LinuxRegularFile || current.State != expected)
        {
            throw AtomicOutputPath.OutputChanged(externalOutput);
        }

        if (OperatingSystem.IsLinux())
        {
            if (RenameAt(Handle, name, Handle, Path.GetFileName(destination.DestinationPath),
                destination.Force ? 0 : LinuxRenameNoReplace) != 0)
            {
                int error = Marshal.GetLastPInvokeError();
                if (!destination.Force && error == LinuxAlreadyExists)
                {
                    throw AtomicOutputPath.DestinationAlreadyExists();
                }
                if (error == LinuxNoSuchFile)
                {
                    throw AtomicOutputPath.OutputChanged(externalOutput);
                }
                throw NativeFailure("The temporary output file could not be published.", error);
            }
            return;
        }

        try
        {
            File.Move(Path.Combine(_path, name), destination.DestinationPath, destination.Force);
        }
        catch (IOException) when (!destination.Force && TryReadNamedStatus(Path.GetFileName(destination.DestinationPath), out _))
        {
            throw AtomicOutputPath.DestinationAlreadyExists();
        }
    }

    internal void DeleteTemporaryFile(string name)
    {
        if (!OperatingSystem.IsLinux())
        {
            EnsurePathSafe();
            File.Delete(Path.Combine(_path, name));
            return;
        }
        if (UnlinkAt(Handle, name, flags: 0) != 0 && Marshal.GetLastPInvokeError() != LinuxNoSuchFile)
        {
            throw NativeFailure("The temporary output file could not be removed.");
        }
    }

    private static void DeleteLinuxContents(SafeFileHandle directory)
    {
        int descriptor = OpenAt(directory, ".", LinuxReadOnly | LinuxDirectory | LinuxNoFollow | LinuxCloseOnExec, mode: 0);
        if (descriptor < 0)
        {
            throw NativeFailure("The private output staging directory could not be enumerated.");
        }
        using var readable = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        IntPtr iterator = OpenDirectoryIterator(descriptor);
        if (iterator == IntPtr.Zero)
        {
            throw NativeFailure("The private output staging directory could not be enumerated.");
        }
        readable.SetHandleAsInvalid(); // fdopendir owns this descriptor until closedir.
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
                        throw NativeFailure("The private output staging directory could not be enumerated.");
                    }
                    break;
                }
                // readdir64 exposes Linux dirent64: its NUL-terminated name starts at byte 19.
                string name = Marshal.PtrToStringUTF8(IntPtr.Add(entry, 19))
                    ?? throw new IOException("The private output staging directory contained an invalid entry.");
                if (name is "." or "..")
                {
                    continue;
                }
                DeleteLinuxEntry(directory, name);
            }
        }
        finally
        {
            CloseDirectoryIterator(iterator);
        }
    }

    private static void DeleteLinuxEntry(SafeFileHandle directory, string name)
    {
        if (Statx(directory, name, LinuxNoFollowAt, LinuxStatxRequired, out LinuxStatx native) != 0)
        {
            if (Marshal.GetLastPInvokeError() == LinuxNoSuchFile)
            {
                return;
            }
            throw NativeFailure("A private output staging entry could not be inspected.");
        }
        NativeStatus status = ConvertStatus(native);
        if ((status.Mode & LinuxTypeMask) == LinuxDirectoryType)
        {
            using SafeFileHandle child = OpenLinuxDirectory(name, directory, pathChanged: true);
            AtomicOutputFileIdentity identity = ReadStatus(child).State.Identity;
            if (identity != status.State.Identity)
            {
                throw AtomicOutputPath.OutputChanged(externalOutput: false);
            }
            DeleteLinuxContents(child);
            if (Statx(directory, name, LinuxNoFollowAt, LinuxStatxRequired, out native) != 0 ||
                ConvertStatus(native).State.Identity != identity)
            {
                throw AtomicOutputPath.OutputChanged(externalOutput: false);
            }
            if (UnlinkAt(directory, name, LinuxRemoveDirectory) != 0)
            {
                throw NativeFailure("A private output staging directory could not be removed.");
            }
        }
        else if (UnlinkAt(directory, name, flags: 0) != 0 && Marshal.GetLastPInvokeError() != LinuxNoSuchFile)
        {
            throw NativeFailure("A private output staging file could not be removed.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        foreach (DirectoryBinding directory in _directories)
        {
            directory.Handle.Dispose();
        }
    }

    private void EnsureBoundDirectoryTrust()
    {
        for (int index = 0; index < _directories.Count; index++)
        {
            NativeStatus status = ReadStatus(_directories[index].Handle);
            EnsureTrustedDirectory(status, _effectiveUserId);
            if (_privateRoot && index == _directories.Count - 1)
            {
                EnsurePrivateDirectory(status);
            }
        }
    }

    private static void EnsureTrustedDirectory(NativeStatus status, uint effectiveUserId)
    {
        EnsureDirectory(status);
        if (status.Owner != 0 && status.Owner != effectiveUserId ||
            (status.Mode & LinuxWritableByOthers) != 0 && (status.Mode & LinuxSticky) == 0)
        {
            throw AtomicOutputPath.UnsafeDirectory();
        }
        // Trusted sticky directories protect newly created, current-user-owned temporary
        // entries as well as existing owned child directories from replacement by other UIDs.
    }

    private void EnsurePrivateDirectory(NativeStatus status)
    {
        if (status.Owner != _effectiveUserId || (status.Mode & 0xFFF) != LinuxPrivateDirectoryMode)
        {
            throw AtomicOutputPath.UnsafeDirectory();
        }
    }

    private bool IsPrivateExternalOutput(NativeStatus status)
    {
        return (status.Mode & LinuxTypeMask) == LinuxRegularFile &&
            (!OperatingSystem.IsLinux() ||
             status.Owner == _effectiveUserId && status.LinkCount == 1 && (status.Mode & LinuxWritableByOthers) == 0);
    }

    private static void EnsureDirectory(NativeStatus status)
    {
        if ((status.Mode & LinuxTypeMask) != LinuxDirectoryType)
        {
            throw AtomicOutputPath.UnsafePath();
        }
    }

    private static void EnsureRegularFile(NativeStatus status, bool externalOutput)
    {
        if ((status.Mode & LinuxTypeMask) != LinuxRegularFile)
        {
            throw AtomicOutputPath.OutputChanged(externalOutput);
        }
    }

    private static SafeFileHandle OpenLinuxDirectory(string path, SafeFileHandle? parent, bool pathChanged)
    {
        const int Flags = LinuxPath | LinuxDirectory | LinuxNoFollow | LinuxCloseOnExec;
        int descriptor = parent is null ? OpenNative(path, Flags, mode: 0) : OpenAt(parent, path, Flags, mode: 0);
        if (descriptor >= 0)
        {
            return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        }
        int error = Marshal.GetLastPInvokeError();
        if (pathChanged && error is LinuxNoSuchFile or LinuxNotADirectory or LinuxSymbolicLinkLoop)
        {
            throw AtomicOutputPath.OutputChanged(externalOutput: false);
        }
        if (error is LinuxNotADirectory or LinuxSymbolicLinkLoop)
        {
            throw AtomicOutputPath.UnsafePath();
        }
        if (error == LinuxNoSuchFile)
        {
            throw AtomicOutputPath.MissingDirectory();
        }
        throw NativeFailure("The output directory could not be opened safely.", error);
    }

    private bool TryReadNamedStatus(string name, out NativeStatus status)
    {
        if (OperatingSystem.IsLinux())
        {
            if (Statx(Handle, name, LinuxNoFollowAt, LinuxStatxRequired, out LinuxStatx native) == 0)
            {
                status = ConvertStatus(native);
                return true;
            }
            int error = Marshal.GetLastPInvokeError();
            if (error == LinuxNoSuchFile)
            {
                status = default;
                return false;
            }
            throw NativeFailure("The output file identity could not be inspected.", error);
        }

        using SafeFileHandle handle = CreateWindowsFile(Path.Combine(_path, name), WindowsReadAttributes,
            WindowsShareAll, IntPtr.Zero, WindowsOpenExisting, WindowsBackupSemantics | WindowsOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error is 2 or 3)
            {
                status = default;
                return false;
            }
            throw NativeFailure("The output file identity could not be inspected.", error);
        }
        status = ReadStatus(handle);
        return true;
    }

    private static NativeStatus ReadStatus(SafeFileHandle handle)
    {
        if (OperatingSystem.IsLinux())
        {
            if (Statx(handle, string.Empty, LinuxEmptyPath, LinuxStatxRequired, out LinuxStatx status) != 0)
            {
                throw NativeFailure("The output descriptor identity could not be inspected.");
            }
            return ConvertStatus(status);
        }
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Atomic output requires Linux or Windows file identity support.");
        }
        if (!GetWindowsFileInformation(handle, out WindowsFileInformation information) ||
            !GetWindowsBasicInformation(handle, informationClass: 0, out WindowsBasicInformation basic, size: 40))
        {
            throw NativeFailure("The output handle identity could not be inspected.");
        }
        int mode = (information.Attributes & WindowsReparsePointAttribute) != 0 ? LinuxSymbolicLink :
            (information.Attributes & WindowsDirectoryAttribute) != 0 ? LinuxDirectoryType :
            GetWindowsFileType(handle) == 1 ? LinuxRegularFile : 0;
        ulong length = ((ulong)information.SizeHigh << 32) | information.SizeLow;
        if (length > long.MaxValue)
        {
            throw new IOException("The output file length is unsupported.");
        }
        return new NativeStatus(mode, 0, information.LinkCount, new AtomicOutputFileState(
            new AtomicOutputFileIdentity(((ulong)information.IndexHigh << 32) | information.IndexLow,
                information.VolumeSerialNumber, 0),
            (long)length, basic.ChangeTime, 0, basic.LastWriteTime, 0));
    }

    private static NativeStatus ConvertStatus(LinuxStatx status)
    {
        if ((status.Mask & LinuxStatxRequired) != LinuxStatxRequired || status.Size > long.MaxValue)
        {
            throw new IOException("The filesystem did not return a complete output file identity.");
        }
        return new NativeStatus(status.Mode, status.UserId, status.LinkCount, new AtomicOutputFileState(
            new AtomicOutputFileIdentity(status.Inode, status.DeviceMajor, status.DeviceMinor),
            (long)status.Size, status.ChangeSeconds, status.ChangeNanoseconds,
            status.ModificationSeconds, status.ModificationNanoseconds));
    }

    private static SafeFileHandle OpenWindowsPath(string path, uint access, bool missingExternalOutput = false)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Atomic output requires Linux or Windows file identity support.");
        }
        SafeFileHandle handle = CreateWindowsFile(path, access, WindowsShareAll, IntPtr.Zero,
            WindowsOpenExisting, WindowsBackupSemantics | WindowsOpenReparsePoint, IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            return handle;
        }
        int error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        if (missingExternalOutput)
        {
            throw error is 2 or 3 ? AtomicOutputPath.ExternalOutputMissing() : AtomicOutputPath.ExternalOutputInvalid();
        }
        throw NativeFailure("The output path could not be opened safely.", error);
    }

    private static IOException NativeFailure(string message, int? error = null)
    {
        return new IOException(message, new Win32Exception(error ?? Marshal.GetLastPInvokeError()));
    }

    private readonly record struct DirectoryBinding(string Name, SafeFileHandle Handle, AtomicOutputFileIdentity Identity);
    private readonly record struct NativeStatus(int Mode, uint Owner, uint LinkCount, AtomicOutputFileState State);

    // Unlike struct stat, statx has a stable, architecture-independent 256-byte Linux ABI.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatx
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(16)] internal uint LinkCount;
        [FieldOffset(20)] internal uint UserId;
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

    [StructLayout(LayoutKind.Explicit, Size = 52)]
    private struct WindowsFileInformation
    {
        [FieldOffset(0)] internal uint Attributes;
        [FieldOffset(28)] internal uint VolumeSerialNumber;
        [FieldOffset(32)] internal uint SizeHigh;
        [FieldOffset(36)] internal uint SizeLow;
        [FieldOffset(40)] internal uint LinkCount;
        [FieldOffset(44)] internal uint IndexHigh;
        [FieldOffset(48)] internal uint IndexLow;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct WindowsBasicInformation
    {
        [FieldOffset(16)] internal long LastWriteTime;
        [FieldOffset(24)] internal long ChangeTime;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenNative(string path, int flags, uint mode);
    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(SafeFileHandle directory, string path, int flags, uint mode);
    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int DuplicateLinuxHandle(SafeFileHandle handle, int command, int minimumDescriptor);
    [DllImport("libc", EntryPoint = "fchmod", SetLastError = true)]
    private static extern int ChangeMode(SafeFileHandle handle, uint mode);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(SafeFileHandle descriptor, string path, int flags, uint mask, out LinuxStatx status);
    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int RenameAt(SafeFileHandle sourceDirectory, string source, SafeFileHandle destinationDirectory,
        string destination, uint flags);
    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
    private static extern int UnlinkAt(SafeFileHandle directory, string path, int flags);
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();
    [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)]
    private static extern int MakeDirectoryAt(SafeFileHandle directory, string name, uint mode);
    [DllImport("libc", EntryPoint = "fchmodat", SetLastError = true)]
    private static extern int ChangeDirectoryModeAt(SafeFileHandle directory, string name, uint mode, int flags);
    [DllImport("libc", EntryPoint = "fdopendir", SetLastError = true)]
    private static extern IntPtr OpenDirectoryIterator(int descriptor);
    [DllImport("libc", EntryPoint = "readdir64", SetLastError = true)]
    private static extern IntPtr ReadDirectoryEntry(IntPtr iterator);
    [DllImport("libc", EntryPoint = "closedir", SetLastError = true)]
    private static extern int CloseDirectoryIterator(IntPtr iterator);
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateWindowsDirectory(string path, IntPtr securityAttributes);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateWindowsFile(string path, uint access, uint share, IntPtr securityAttributes,
        uint creationDisposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowsFileInformation(SafeFileHandle handle, out WindowsFileInformation information);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowsBasicInformation(SafeFileHandle handle, int informationClass,
        out WindowsBasicInformation information, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileType", SetLastError = true)]
    private static extern uint GetWindowsFileType(SafeFileHandle handle);
    [DllImport("kernel32.dll", EntryPoint = "GetCurrentProcess")]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", EntryPoint = "DuplicateHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateWindowsHandle(IntPtr sourceProcess, SafeFileHandle source, IntPtr targetProcess,
        out SafeFileHandle target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
}

internal readonly record struct AtomicOutputFileIdentity(ulong Inode, uint DeviceMajor, uint DeviceMinor);

internal readonly record struct AtomicOutputFileState(AtomicOutputFileIdentity Identity, long Length,
    long ChangeSeconds, uint ChangeNanoseconds, long ModificationSeconds, uint ModificationNanoseconds);
