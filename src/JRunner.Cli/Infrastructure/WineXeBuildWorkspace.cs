using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using JRunner.Core.Configuration;
using JRunner.Core.Contracts;
using JRunner.Core.Nand.Security;
using JRunner.Core.XeBuild;
using JRunner.Core.XeBuild.Preparation;
using Microsoft.Win32.SafeHandles;

namespace JRunner.Cli.Infrastructure;

/// <summary>
/// A private, disposable filesystem tree containing one complete mutable XeBuild operation.
/// </summary>
internal sealed class WineXeBuildWorkspace : IAsyncDisposable
{
    private const int BufferSize = 0x10000;
    private const int MaximumDirectoryReservationAttempts = 16;
    private const string XeBuildDirectoryName = "xeBuild";
    private const string StagedInputRelativePath = "xeBuild/data/nanddump.bin";
    private const string StagedCpuKeyRelativePath = "xeBuild/data/cpukey.txt";

    private XeBuildPreparedPlan? _plan;
    private bool _disposed;

    private WineXeBuildWorkspace(string rootDirectory, string winePrefixDirectory)
    {
        RootDirectory = rootDirectory;
        WinePrefixDirectory = winePrefixDirectory;
    }

    internal string RootDirectory { get; }

    internal string XeBuildDirectory => Path.Combine(RootDirectory, XeBuildDirectoryName);

    internal string StagedInputPath => WorkspacePathSafety.ResolveDescendant(RootDirectory, StagedInputRelativePath);

    internal string StagedCpuKeyPath => WorkspacePathSafety.ResolveDescendant(RootDirectory, StagedCpuKeyRelativePath);

    internal string WinePrefixDirectory { get; }

    internal static Task<WineXeBuildWorkspace> CreateAsync(
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        string? operationDirectory = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string normalizedWorkspaceRoot = WorkspacePathSafety.NormalizeDirectoryPath(workspaceRoot, nameof(workspaceRoot));
            WorkspacePathSafety.CreatePrivateDirectory(normalizedWorkspaceRoot);
            operationDirectory = CreateOperationDirectory(normalizedWorkspaceRoot);
            cancellationToken.ThrowIfCancellationRequested();
            string winePrefixDirectory = Path.Combine(operationDirectory, "wine-prefix");
            WorkspacePathSafety.CreatePrivateDirectory(winePrefixDirectory);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new WineXeBuildWorkspace(operationDirectory, winePrefixDirectory));
        }
        catch (Exception exception)
        {
            if (operationDirectory is not null)
            {
                _ = TryDeleteDirectory(operationDirectory);
            }

            return exception is OperationCanceledException && cancellationToken.IsCancellationRequested
                ? Task.FromCanceled<WineXeBuildWorkspace>(cancellationToken)
                : Task.FromException<WineXeBuildWorkspace>(exception);
        }
    }

    internal async Task StageSupportAsync(
        string generationDirectory,
        XeBuildPreparedPlan plan,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(generationDirectory);
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();

        string normalizedGenerationDirectory = WorkspacePathSafety.NormalizeDirectoryPath(
            generationDirectory,
            nameof(generationDirectory));
        WorkspacePathSafety.EnsureNoLinkAncestors(normalizedGenerationDirectory);
        if (!Directory.Exists(normalizedGenerationDirectory))
        {
            throw SupportMissing();
        }

        _plan = plan;
        foreach (string directory in plan.RequiredWorkspaceDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WorkspacePathSafety.CreatePrivateDirectory(
                WorkspacePathSafety.ResolveDescendant(RootDirectory, directory));
        }

        foreach (string supportPath in plan.RequiredSupportFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string sourcePath = ResolveSupportFile(normalizedGenerationDirectory, supportPath);
            string destinationPath = WorkspacePathSafety.ResolveDescendant(RootDirectory, supportPath);
            await CopyFileEnsuringParentAsync(sourcePath, destinationPath, cancellationToken).ConfigureAwait(false);
        }

        foreach (XeBuildWorkspaceOverlay overlay in plan.WorkspaceOverlays)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!plan.RequiredSupportFiles.Contains(overlay.SourceSupportPath, StringComparer.Ordinal))
            {
                throw SupportMissing();
            }

            string sourcePath = ResolveSupportFile(normalizedGenerationDirectory, overlay.SourceSupportPath);
            string destinationPath = WorkspacePathSafety.ResolveDescendant(RootDirectory, overlay.DestinationWorkspacePath);
            await CopyFileEnsuringParentAsync(sourcePath, destinationPath, cancellationToken).ConfigureAwait(false);
        }

        if (plan.DisablesSmcResetPatching)
        {
            await DisableSmcResetPatchingAsync(cancellationToken).ConfigureAwait(false);
        }

        if (plan.DashLaunch is { } dashLaunch)
        {
            await StageDashLaunchConfigurationAsync(dashLaunch, cancellationToken).ConfigureAwait(false);
        }

        await ValidateModeInisAsync(plan, cancellationToken).ConfigureAwait(false);
    }

    internal async Task StageInputAsync(
        XeBuildSourceContext source,
        XeBuildFourGigabyteStagingPolicy policy,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(source);
        if (!Enum.IsDefined(policy))
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (source.IsFourGigabyteEmmc && policy is XeBuildFourGigabyteStagingPolicy.None)
        {
            throw new OperationFailureException(
                ExitCode.Usage,
                "xebuild-4gb-staging-policy-required",
                "A 4 GB eMMC source requires a system-partition-only or full-4gb-data staging policy.");
        }

        if (!source.IsFourGigabyteEmmc && policy is not XeBuildFourGigabyteStagingPolicy.None)
        {
            throw new OperationFailureException(
                ExitCode.Usage,
                "xebuild-4gb-staging-policy-unexpected",
                "A 4 GB staging policy can be selected only for a full 4 GB eMMC source.");
        }

        string normalizedInputPath;
        try
        {
            normalizedInputPath = Path.GetFullPath(source.InputPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new OperationFailureException(
                ExitCode.InvalidData,
                "xebuild-input-invalid",
                "The XeBuild NAND input path is invalid.");
        }

        bool staged = false;
        try
        {
            await using XeBuildSourceFile inputFile = XeBuildSourceFile.Open(
                normalizedInputPath, reportMissingInput: true);
            FileStream input = inputFile.Stream;
            if (inputFile.ByteLength != source.ByteLength)
            {
                throw SourceLengthMismatch();
            }

            long stagedLength = policy is XeBuildFourGigabyteStagingPolicy.SystemPartitionOnly
                ? 0x3000000L
                : source.ByteLength;
            await using FileStream output = OpenPrivateOutput(StagedInputPath, FileMode.CreateNew);
            await CopyInputAsync(
                input,
                output,
                source,
                stagedLength,
                preserveSparseRegions: policy is XeBuildFourGigabyteStagingPolicy.FullData,
                cancellationToken).ConfigureAwait(false);
            inputFile.EnsureUnchanged();
            if (input.Length != source.ByteLength)
            {
                throw SourceLengthMismatch();
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            staged = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new OperationFailureException(
                ExitCode.InputOutput,
                "workspace-copy-failed",
                "The XeBuild NAND input could not be staged.");
        }
        finally
        {
            if (!staged)
            {
                _ = TryDeleteFile(StagedInputPath);
            }
        }
    }

    internal async Task StageCpuKeyAsync(CpuKey cpuKey, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        byte[] keyBytes = new byte[CpuKey.ByteLength];
        byte[] hexadecimal = new byte[CpuKey.HexadecimalLength + 1];
        try
        {
            cpuKey.CopyTo(keyBytes);
            for (int index = 0; index < keyBytes.Length; index++)
            {
                int destinationIndex = index * 2;
                hexadecimal[destinationIndex] = ToHexadecimalByte(keyBytes[index] >> 4);
                hexadecimal[destinationIndex + 1] = ToHexadecimalByte(keyBytes[index] & 0x0F);
            }

            hexadecimal[^1] = (byte)'\n';
            try
            {
                await using FileStream output = OpenPrivateOutput(StagedCpuKeyPath, FileMode.CreateNew);
                await output.WriteAsync(hexadecimal, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new OperationFailureException(
                    ExitCode.InputOutput,
                    "xebuild-cpu-key-stage-failed",
                    "The XeBuild workspace could not stage the CPU key.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
            CryptographicOperations.ZeroMemory(hexadecimal);
        }
    }

    internal ValueTask CleanupAsync(bool retainDiagnostics)
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _ = TryDeleteFile(StagedCpuKeyPath);
        _ = TryDeleteFile(StagedInputPath);
        bool cleaned = retainDiagnostics && TryRetainDiagnostics();
        if (!cleaned)
        {
            cleaned = TryDeleteDirectory(RootDirectory);
        }

        if (!cleaned)
        {
            throw new OperationFailureException(
                ExitCode.InputOutput,
                "workspace-cleanup-failed",
                "The private XeBuild workspace could not be sanitized or removed safely.");
        }

        _disposed = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return CleanupAsync(retainDiagnostics: false);
    }

    private static string CreateOperationDirectory(string workspaceRoot)
    {
        for (int attempt = 0; attempt < MaximumDirectoryReservationAttempts; attempt++)
        {
            string candidate = Path.Combine(workspaceRoot, $"operation-{Guid.NewGuid():N}");
            if (Directory.Exists(candidate) || File.Exists(candidate))
            {
                continue;
            }

            try
            {
                WorkspacePathSafety.CreatePrivateDirectory(candidate);
                if (!Directory.EnumerateFileSystemEntries(candidate).Any())
                {
                    return candidate;
                }
            }
            catch (OperationFailureException)
            {
                throw;
            }
            catch (IOException) when (attempt < MaximumDirectoryReservationAttempts - 1)
            {
                continue;
            }
        }

        throw new OperationFailureException(
            ExitCode.InputOutput,
            "workspace-reservation-failed",
            "A private XeBuild workspace could not be reserved.");
    }

    private async Task DisableSmcResetPatchingAsync(CancellationToken cancellationToken)
    {
        string optionsPath = WorkspacePathSafety.ResolveDescendant(RootDirectory, "xeBuild/data/options.ini");
        string text = await ReadTextAsync(optionsPath, cancellationToken).ConfigureAwait(false);
        IniParseResult parsed = IniParser.Parse(text, cancellationToken);
        if (parsed.HasErrors)
        {
            throw InvalidIni();
        }

        IniDocument updated = IniEditor.SetValue(parsed.Document, sectionLabel: null, "patchsmc", "false");
        await WriteTextAsync(optionsPath, updated.ToText(), cancellationToken).ConfigureAwait(false);
    }

    private async Task StageDashLaunchConfigurationAsync(
        XeBuildDashLaunchPlan dashLaunch,
        CancellationToken cancellationToken)
    {
        string destinationPath = WorkspacePathSafety.ResolveDescendant(
            RootDirectory,
            dashLaunch.DashboardLaunchConfigurationPath);
        WorkspacePathSafety.EnsureNoLinkAncestors(destinationPath);
        WorkspacePathSafety.EnsureNotLink(destinationPath);
        string sourcePath = WorkspacePathSafety.ResolveDescendant(
            RootDirectory,
            dashLaunch.LaunchConfigurationSupportPath);
        if (!File.Exists(sourcePath))
        {
            sourcePath = ResolveSupportFile(RootDirectory, dashLaunch.DefaultLaunchConfigurationSupportPath);
        }

        await CopyFileEnsuringParentAsync(sourcePath, destinationPath, cancellationToken).ConfigureAwait(false);
    }

    private async Task ValidateModeInisAsync(XeBuildPreparedPlan plan, CancellationToken cancellationToken)
    {
        var modePaths = new HashSet<string>(StringComparer.Ordinal)
        {
            plan.RequestedModeIniPath,
            plan.BuildModeIniPath,
        };

        foreach (string modePath in modePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fullPath = WorkspacePathSafety.ResolveDescendant(RootDirectory, modePath);
            string text = await ReadTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
            IniParseResult parsed = IniParser.Parse(text, cancellationToken);
            IniDocument updated = parsed.Document;
            if (parsed.HasErrors || !HasRequiredLabel(parsed.Document, plan.RequiredIniLabel))
            {
                throw InvalidIni();
            }

            if (plan.DashLaunch is { } dashLaunch)
            {
                foreach (string entry in dashLaunch.IniPatchEntries)
                {
                    updated = IniEditor.AddLiteral(updated, "flashfs", entry);
                }

                if (!ReferenceEquals(updated, parsed.Document))
                {
                    await WriteTextAsync(fullPath, updated.ToText(), cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private static bool HasRequiredLabel(IniDocument document, string requiredLabel)
    {
        foreach (IniLine line in document.Lines)
        {
            if (line is IniSectionLine section &&
                string.Equals(section.Label, requiredLabel, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string ResolveSupportFile(string generationDirectory, string supportPath)
    {
        string sourcePath = WorkspacePathSafety.ResolveDescendant(generationDirectory, supportPath);
        if (!File.Exists(sourcePath))
        {
            throw SupportMissing();
        }

        WorkspacePathSafety.EnsureNotLink(sourcePath);
        WorkspacePathSafety.EnsureNoLinkAncestors(sourcePath);
        return sourcePath;
    }

    private static async Task CopyFileEnsuringParentAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        WorkspacePathSafety.EnsureNotLink(sourcePath);
        try
        {
            await using (FileStream input = new(
                             sourcePath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             BufferSize,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await using (FileStream output = OpenPrivateOutput(destinationPath, FileMode.Create))
                {
                    await input.CopyToAsync(output, BufferSize, cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new OperationFailureException(
                ExitCode.InputOutput,
                "workspace-copy-failed",
                "A XeBuild workspace file could not be staged.");
        }
    }

    internal static FileStream OpenPrivateOutput(string path, FileMode mode)
    {
        string parentDirectory = Path.GetDirectoryName(path)
            ?? throw new OperationFailureException(
                ExitCode.InvalidData,
                "workspace-path-invalid",
                "The XeBuild workspace path is invalid.");
        WorkspacePathSafety.CreatePrivateDirectory(parentDirectory);
        WorkspacePathSafety.EnsureNotLink(path);
        if (mode is FileMode.Create && File.Exists(path))
        {
            WorkspacePathSafety.SetPrivateFileMode(path);
        }

        var options = new FileStreamOptions
        {
            Mode = mode,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = BufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(path, options);
    }

    private static async Task CopyInputAsync(
        FileStream input,
        FileStream output,
        XeBuildSourceContext source,
        long stagedByteLength,
        bool preserveSparseRegions,
        CancellationToken cancellationToken)
    {
        using IncrementalHash contentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long remaining = source.ByteLength;
            long remainingToStage = stagedByteLength;
            while (remaining > 0)
            {
                int requested = (int)Math.Min(remaining, BufferSize);
                int count = await input.ReadAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    throw SourceLengthMismatch();
                }

                // Hash every source byte, even when only the eMMC system partition is staged.
                contentHash.AppendData(buffer.AsSpan(0, count));
                int stagedCount = (int)Math.Min(remainingToStage, count);
                if (stagedCount > 0)
                {
                    // Keep full, sparse eMMC dumps sparse without changing any staged byte.
                    if (preserveSparseRegions && buffer.AsSpan(0, stagedCount).IndexOfAnyExcept((byte)0) < 0)
                    {
                        output.Seek(stagedCount, SeekOrigin.Current);
                    }
                    else
                    {
                        await output.WriteAsync(buffer.AsMemory(0, stagedCount), cancellationToken).ConfigureAwait(false);
                    }

                    remainingToStage -= stagedCount;
                }

                remaining -= count;
            }

            output.SetLength(stagedByteLength);
            Span<byte> contentSha256 = stackalloc byte[SHA256.HashSizeInBytes];
            contentHash.GetHashAndReset(contentSha256);
            if (!source.MatchesContentSha256(contentSha256))
            {
                throw new OperationFailureException(
                    ExitCode.InvalidData,
                    "xebuild-input-changed",
                    "The XeBuild source content changed after source inspection.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private bool TryRetainDiagnostics()
    {
        try
        {
            WorkspacePathSafety.EnsureNoLinkAncestors(RootDirectory);
            var retainedFiles = new HashSet<string>(StringComparer.Ordinal) { "diagnostics.txt" };
            if (_plan is { } plan)
            {
                foreach (string supportPath in plan.RequiredSupportFiles)
                {
                    AddRetainedFile(supportPath, plan, retainedFiles);
                }

                foreach (XeBuildWorkspaceOverlay overlay in plan.WorkspaceOverlays)
                {
                    AddRetainedFile(overlay.DestinationWorkspacePath, plan, retainedFiles);
                }
            }

            var retainedDirectories = new HashSet<string>(StringComparer.Ordinal);
            foreach (string file in retainedFiles)
            {
                int separator = file.LastIndexOf('/');
                while (separator > 0)
                {
                    retainedDirectories.Add(file[..separator]);
                    separator = file.LastIndexOf('/', separator - 1);
                }
            }

            return PruneDiagnosticsDirectory(RootDirectory, string.Empty, retainedFiles, retainedDirectories);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            OperationFailureException or NotSupportedException)
        {
            return false;
        }
    }

    private static void AddRetainedFile(
        string path,
        XeBuildPreparedPlan plan,
        HashSet<string> retainedFiles)
    {
        if (path.StartsWith("xeBuild/data/", StringComparison.Ordinal) ||
            string.Equals(path, string.Concat(plan.BuildModeIniPath, ".bak"), StringComparison.Ordinal) ||
            string.Equals(Path.GetFileName(path), "vfuses_khv.bin", StringComparison.OrdinalIgnoreCase) ||
            plan.RequiredWorkspaceDirectories.Any(directory =>
                path.StartsWith(string.Concat(directory, "/"), StringComparison.Ordinal)))
        {
            return;
        }

        retainedFiles.Add(path);
    }

    private static bool PruneDiagnosticsDirectory(
        string directory,
        string relativeDirectory,
        HashSet<string> retainedFiles,
        HashSet<string> retainedDirectories)
    {
        bool cleaned = true;
        foreach (FileSystemInfo entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            string relativePath = string.Concat(relativeDirectory, entry.Name);
            if (entry.LinkTarget is not null || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                try
                {
                    entry.Delete();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    cleaned = false;
                }
            }
            else if (entry is DirectoryInfo childDirectory)
            {
                if (retainedDirectories.Contains(relativePath))
                {
                    cleaned &= PruneDiagnosticsDirectory(
                        childDirectory.FullName,
                        string.Concat(relativePath, "/"),
                        retainedFiles,
                        retainedDirectories);
                    if (!childDirectory.EnumerateFileSystemInfos().Any())
                    {
                        cleaned &= TryDeleteDirectory(childDirectory.FullName);
                    }
                }
                else
                {
                    cleaned &= TryDeleteDirectory(childDirectory.FullName);
                }
            }
            else if (!retainedFiles.Contains(relativePath))
            {
                cleaned &= TryDeleteFile(entry.FullName);
            }
        }

        return cleaned;
    }

    private static async Task<string> ReadTextAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw InvalidIni();
        }
    }

    private static async Task WriteTextAsync(string path, string text, CancellationToken cancellationToken)
    {
        try
        {
            await File.WriteAllTextAsync(path, text, cancellationToken).ConfigureAwait(false);
            WorkspacePathSafety.SetPrivateFileMode(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new OperationFailureException(
                ExitCode.InputOutput,
                "workspace-ini-write-failed",
                "The XeBuild workspace INI file could not be updated.");
        }
    }

    private static byte ToHexadecimalByte(int value)
    {
        return (byte)(value < 10 ? '0' + value : 'A' + (value - 10));
    }

    private static OperationFailureException SupportMissing()
    {
        return new OperationFailureException(
            ExitCode.InvalidData,
            "xebuild-support-missing",
            "The active support payload is missing a required XeBuild file.");
    }

    private static OperationFailureException SourceLengthMismatch()
    {
        return new OperationFailureException(
            ExitCode.InvalidData,
            "xebuild-input-length-mismatch",
            "The XeBuild source length changed after source inspection.");
    }

    private static OperationFailureException InvalidIni()
    {
        return new OperationFailureException(
            ExitCode.InvalidData,
            "xebuild-ini-invalid",
            "The staged XeBuild configuration is malformed or missing its required bootloader label.");
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            WorkspacePathSafety.EnsureNoLinkAncestors(Path.GetDirectoryName(path) ?? path);
            if (Directory.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return !File.Exists(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            OperationFailureException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryDeleteDirectory(string directory)
    {
        try
        {
            WorkspacePathSafety.EnsureNoLinkAncestors(Path.GetDirectoryName(directory) ?? directory);
            if (File.Exists(directory))
            {
                return false;
            }

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            return !Directory.Exists(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            OperationFailureException or NotSupportedException)
        {
            return false;
        }
    }
}

/// <summary>
/// Owns a source descriptor admitted by Linux ownership, permission, and ancestry checks before any bytes are read.
/// </summary>
/// <remarks>
/// Every pathname component is opened relative to its bound parent without following symbolic links.
/// Root and the current effective UID are trusted; hostile processes with the same UID or privileged access
/// are outside this cross-UID boundary. A trusted-owned sticky writable ancestor is permitted only because
/// the next child is also checked for trusted ownership, preventing another UID from renaming that entry.
/// Callers must recheck this descriptor after inspection and complete hashing or copying. These metadata
/// checks do not replace the full-source SHA-256 comparison when staging a previously inspected source.
/// </remarks>
internal sealed class XeBuildSourceFile : IAsyncDisposable
{
    private const int BufferSize = 0x10000;
    private const int LinuxReadOnly = 0;
    private const int LinuxNonBlocking = 0x800;
    private const int LinuxDirectory = 0x10000;
    private const int LinuxNoFollow = 0x20000;
    private const int LinuxCloseOnExec = 0x80000;
    private const int LinuxPath = 0x200000;
    private const int LinuxEmptyPath = 0x1000;
    private const int LinuxStatxForceSync = 0x2000;
    private const uint LinuxStatxDirectoryFields = 0xB; // TYPE | MODE | UID
    private const uint LinuxStatxSourceFields = 0x38F; // Directory fields | NLINK | CTIME | INO | SIZE
    private const int LinuxFileTypeMask = 0xF000;
    private const int LinuxDirectoryFile = 0x4000;
    private const int LinuxRegularFile = 0x8000;
    private const int LinuxGroupOrOtherWrite = 0x12;
    private const int LinuxStickyBit = 0x200;
    private const int LinuxNoSuchFileOrDirectory = 2;
    private const int LinuxNoDeviceOrAddress = 6;
    private const int LinuxNoDevice = 19;
    private const int LinuxNotADirectory = 20;
    private const int LinuxInvalidArgument = 22;
    private const int LinuxNotImplemented = 38;
    private const int LinuxSymbolicLinkLoop = 40;
    private const int LinuxOperationNotSupported = 95;

    private readonly SafeFileHandle _handle;
    private readonly uint _deviceMajor;
    private readonly uint _deviceMinor;
    private readonly ulong _inode;
    private readonly long _changeSeconds;
    private readonly uint _changeNanoseconds;
    private readonly uint _ownerUserId;
    private readonly ushort _mode;
    private readonly uint _linkCount;
    private bool _disposed;

    private XeBuildSourceFile(
        string fullPath,
        SafeFileHandle handle,
        FileStream stream,
        in LinuxStatx status)
    {
        FullPath = fullPath;
        Stream = stream;
        ByteLength = (long)status.Size;
        _handle = handle;
        _deviceMajor = status.DeviceMajor;
        _deviceMinor = status.DeviceMinor;
        _inode = status.Inode;
        _changeSeconds = status.ChangeSeconds;
        _changeNanoseconds = status.ChangeNanoseconds;
        _ownerUserId = status.OwnerUserId;
        _mode = status.Mode;
        _linkCount = status.LinkCount;
    }

    internal FileStream Stream { get; }

    internal string FullPath { get; }

    internal long ByteLength { get; }

    /// <summary>
    /// Opens one existing regular file through protected, descriptor-bound Linux ancestry.
    /// </summary>
    internal static XeBuildSourceFile Open(string inputPath, bool reportMissingInput = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        if (!OperatingSystem.IsLinux())
        {
            throw SafetyUnavailable();
        }

        string fullPath = Path.GetFullPath(inputPath);
        const int DirectoryFlags = LinuxPath | LinuxDirectory | LinuxNoFollow | LinuxCloseOnExec;
        try
        {
            uint effectiveUserId = GetEffectiveUserId();
            SafeFileHandle directory = OwnDescriptor(OpenNative("/", DirectoryFlags, mode: 0), reportMissingInput);
            try
            {
                LinuxStatx directoryStatus = ReadDescriptorStatus(directory, LinuxStatxDirectoryFields);
                EnsureTrustedDirectory(in directoryStatus, effectiveUserId);
                int start = 1;
                while (start < fullPath.Length)
                {
                    int end = fullPath.IndexOf(Path.DirectorySeparatorChar, start);
                    if (end < 0)
                    {
                        return OpenSource(directory, fullPath[start..], fullPath, effectiveUserId, reportMissingInput);
                    }

                    if (end == start)
                    {
                        start++;
                        continue;
                    }

                    SafeFileHandle child = OwnDescriptor(OpenAt(
                        directory,
                        fullPath[start..end],
                        DirectoryFlags,
                        mode: 0), reportMissingInput);
                    try
                    {
                        LinuxStatx childStatus = ReadDescriptorStatus(child, LinuxStatxDirectoryFields);
                        EnsureTrustedDirectory(in childStatus, effectiveUserId);
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

                // A root or directory-only pathname is not a source file.
                throw UnsafeSource();
            }
            finally
            {
                directory.Dispose();
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            throw SafetyUnavailable();
        }
    }

    /// <summary>
    /// Rechecks identity and mutation metadata on the same still-open descriptor, not its pathname.
    /// </summary>
    internal void EnsureUnchanged()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        LinuxStatx status = ReadDescriptorStatus(_handle, LinuxStatxSourceFields);
        if (status.DeviceMajor != _deviceMajor ||
            status.DeviceMinor != _deviceMinor ||
            status.Inode != _inode ||
            status.Size != (ulong)ByteLength ||
            status.ChangeSeconds != _changeSeconds ||
            status.ChangeNanoseconds != _changeNanoseconds ||
            status.OwnerUserId != _ownerUserId ||
            status.Mode != _mode ||
            status.LinkCount != _linkCount)
        {
            throw new OperationFailureException(
                ExitCode.InvalidData,
                "xebuild-input-changed",
                "The XeBuild source changed while it was being read.");
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        // FileStream owns this exact SafeFileHandle; closing it closes the admitted descriptor.
        return Stream.DisposeAsync();
    }

    private static XeBuildSourceFile OpenSource(
        SafeFileHandle directory,
        string fileName,
        string fullPath,
        uint effectiveUserId,
        bool reportMissingInput)
    {
        // O_NONBLOCK prevents a FIFO from blocking before its descriptor type can be rejected.
        SafeFileHandle handle = OwnDescriptor(OpenAt(
            directory,
            fileName,
            LinuxReadOnly | LinuxNonBlocking | LinuxNoFollow | LinuxCloseOnExec,
            mode: 0), reportMissingInput);
        FileStream? stream = null;
        try
        {
            LinuxStatx status = ReadDescriptorStatus(handle, LinuxStatxSourceFields);
            if ((status.Mode & LinuxFileTypeMask) != LinuxRegularFile ||
                (status.OwnerUserId != 0 && status.OwnerUserId != effectiveUserId) ||
                (status.Mode & LinuxGroupOrOtherWrite) != 0 ||
                status.Size > long.MaxValue)
            {
                throw UnsafeSource();
            }

            stream = new FileStream(handle, FileAccess.Read, bufferSize: BufferSize, isAsync: false);
            return new XeBuildSourceFile(fullPath, handle, stream, in status);
        }
        catch
        {
            if (stream is null)
            {
                handle.Dispose();
            }
            else
            {
                stream.Dispose();
            }
            throw;
        }
    }

    private static void EnsureTrustedDirectory(in LinuxStatx status, uint effectiveUserId)
    {
        if ((status.Mode & LinuxFileTypeMask) != LinuxDirectoryFile ||
            (status.OwnerUserId != 0 && status.OwnerUserId != effectiveUserId) ||
            ((status.Mode & LinuxGroupOrOtherWrite) != 0 && (status.Mode & LinuxStickyBit) == 0))
        {
            throw UnsafeSource();
        }

        // Trusted owners alone can change these modes. At a shared sticky boundary, the next
        // descriptor must also have a trusted owner before traversal can admit any source bytes.
    }

    private static LinuxStatx ReadDescriptorStatus(SafeFileHandle handle, uint requiredFields)
    {
        try
        {
            // AT_EMPTY_PATH binds the snapshot to the descriptor; FORCE_SYNC avoids stale remote attributes.
            if (Statx(handle, string.Empty, LinuxEmptyPath | LinuxStatxForceSync, requiredFields, out LinuxStatx status) != 0)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error is LinuxInvalidArgument or LinuxNotImplemented or LinuxOperationNotSupported)
                {
                    throw SafetyUnavailable();
                }
                throw ReadFailed();
            }
            if ((status.Mask & requiredFields) != requiredFields)
            {
                throw SafetyUnavailable();
            }
            return status;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            throw SafetyUnavailable();
        }
    }

    private static SafeFileHandle OwnDescriptor(int descriptor, bool reportMissingInput)
    {
        if (descriptor >= 0)
        {
            return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        }

        int error = Marshal.GetLastPInvokeError();
        if (reportMissingInput && error == LinuxNoSuchFileOrDirectory)
        {
            throw MissingSource();
        }
        if (error is LinuxNoDeviceOrAddress or LinuxNoDevice or LinuxNotADirectory or LinuxSymbolicLinkLoop)
        {
            throw UnsafeSource();
        }
        if (error is LinuxNotImplemented or LinuxOperationNotSupported)
        {
            throw SafetyUnavailable();
        }
        throw ReadFailed();
    }

    private static OperationFailureException MissingSource()
    {
        return new OperationFailureException(
            ExitCode.InvalidData,
            "xebuild-input-missing",
            "The XeBuild NAND input file does not exist.");
    }

    private static OperationFailureException UnsafeSource()
    {
        return new OperationFailureException(
            ExitCode.InvalidData,
            "xebuild-input-unsafe",
            "The XeBuild source must be a regular file with trusted ownership, permissions, and protected directory ancestry.");
    }

    private static OperationFailureException SafetyUnavailable()
    {
        return new OperationFailureException(
            ExitCode.MissingPrerequisite,
            "xebuild-input-safety-unavailable",
            "Safely reading the XeBuild source requires Linux descriptor-based ownership, permission, and identity checks.");
    }

    private static OperationFailureException ReadFailed()
    {
        return new OperationFailureException(
            ExitCode.InputOutput,
            "xebuild-input-read-failed",
            "The XeBuild source file could not be opened or inspected.");
    }

    // statx has a stable, architecture-independent 256-byte Linux ABI, unlike struct stat.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatx
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(16)] internal uint LinkCount;
        [FieldOffset(20)] internal uint OwnerUserId;
        [FieldOffset(28)] internal ushort Mode;
        [FieldOffset(32)] internal ulong Inode;
        [FieldOffset(40)] internal ulong Size;
        [FieldOffset(96)] internal long ChangeSeconds;
        [FieldOffset(104)] internal uint ChangeNanoseconds;
        [FieldOffset(136)] internal uint DeviceMajor;
        [FieldOffset(140)] internal uint DeviceMinor;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenNative(string path, int flags, uint mode);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(SafeFileHandle directory, string path, int flags, uint mode);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(
        SafeFileHandle descriptor,
        string path,
        int flags,
        uint mask,
        out LinuxStatx status);

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();
}
