using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using JRunner.Core.Contracts;
using JRunner.Core.Support;

namespace JRunner.Cli.Infrastructure;

/// <summary>
/// Describes the immutable support generation made active by a completed installation.
/// </summary>
public sealed record SupportInstallationResult(
    string PayloadId,
    string PayloadDirectory,
    int FileCount,
    long ExtractedByteLength,
    string ReleaseTag,
    string SourceUrl,
    string ArchiveSha256,
    string CanonicalManifestSha256,
    DateTimeOffset InstalledAtUtc,
    string ActivePayloadPath);

/// <summary>
/// Obtains, verifies, extracts, and atomically activates the embedded support payload.
/// </summary>
public sealed class SupportPayloadInstaller
{
    private const int BufferSize = 0x10000;
    private const long ProgressInterval = 4L * 1024 * 1024;
    private const string DownloadDirectoryName = ".downloads";
    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;


    private static readonly JsonSerializerOptions ActivationJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly HttpClient _httpClient;
    private readonly SupportManifest _manifest;

    /// <summary>
    /// Creates an installer that uses the supplied HTTP client for the embedded pinned download.
    /// </summary>
    public SupportPayloadInstaller(HttpClient httpClient)
        : this(httpClient, EmbeddedSupportManifest.Current)
    {
    }

    internal SupportPayloadInstaller(HttpClient httpClient, SupportManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(manifest);

        _httpClient = httpClient;
        _manifest = manifest;
    }

    /// <summary>
    /// Downloads the manifest-pinned archive, installs a fully verified generation, and atomically makes it active.
    /// </summary>
    public Task<SupportInstallationResult> InstallAsync(
        SupportRoot supportRoot,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return InstallAsync(
            new SupportInstallationRequest(supportRoot, new SupportArchiveSource.PinnedDownload()),
            progress,
            cancellationToken);
    }

    /// <summary>
    /// Installs a fully verified support generation from the requested pinned or local archive source.
    /// </summary>
    /// <remarks>
    /// A progress callback failure after atomic activation is propagated without removing the active generation.
    /// </remarks>
    public async Task<SupportInstallationResult> InstallAsync(
        SupportInstallationRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.SupportRoot);
        ArgumentNullException.ThrowIfNull(request.ArchiveSource);
        cancellationToken.ThrowIfCancellationRequested();

        string? archivePath = null;
        string? stagedGenerationDirectory = null;
        string? generationDirectory = null;
        bool activationPublished = false;
        try
        {
            SupportRoot supportRoot = request.SupportRoot;
            PrepareSupportRoot(supportRoot);
            archivePath = await AcquireArchiveAsync(
                    supportRoot,
                    request.ArchiveSource,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);

            string generationId = SupportPayloadLayout.CreateGenerationId(_manifest.PayloadId);
            stagedGenerationDirectory = SupportPayloadLayout.GetStagedGenerationDirectory(supportRoot, generationId);
            generationDirectory = SupportPayloadLayout.GetGenerationDirectory(supportRoot, generationId);
            CreatePrivateDirectory(stagedGenerationDirectory, requireEmpty: true);
            long extractedByteLength = await ExtractPayloadAsync(
                    archivePath,
                    stagedGenerationDirectory,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);

            SupportRootSafety.EnsureNoLinkAncestors(SupportPayloadLayout.GetInstallationsDirectory(supportRoot));
            Directory.Move(stagedGenerationDirectory, generationDirectory);
            stagedGenerationDirectory = null;

            DateTimeOffset installedAtUtc = DateTimeOffset.UtcNow;
            string activePayloadPath = SupportPayloadLayout.GetRelativeGenerationPath(generationId);
            var activation = new SupportActivationRecord(
                SupportPayloadLayout.ActivationSchemaVersion,
                _manifest.PayloadId,
                _manifest.ReleaseTag,
                _manifest.Archive.DownloadUri.AbsoluteUri,
                _manifest.Archive.Sha256,
                _manifest.CanonicalManifestSha256,
                installedAtUtc,
                activePayloadPath,
                generationId);
            await WriteActivationAsync(supportRoot, activation, progress, cancellationToken).ConfigureAwait(false);
            activationPublished = true;
            progress?.Report(new OperationProgress("activating-support", "Activated the verified support payload."));

            return new SupportInstallationResult(
                _manifest.PayloadId,
                generationDirectory,
                _manifest.Files.Count,
                extractedByteLength,
                _manifest.ReleaseTag,
                _manifest.Archive.DownloadUri.AbsoluteUri,
                _manifest.Archive.Sha256,
                _manifest.CanonicalManifestSha256,
                installedAtUtc,
                activePayloadPath);
        }
        catch (OperationFailureException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            throw Failure(
                ExitCode.InputOutput,
                "support-download-failed",
                "The support archive could not be downloaded.");
        }
        catch (InvalidDataException)
        {
            throw Failure(
                ExitCode.InvalidData,
                "support-archive-invalid",
                "The support archive is invalid.");
        }
        catch (UnauthorizedAccessException)
        {
            throw Failure(
                ExitCode.InputOutput,
                "support-install-failed",
                "The support payload could not be installed.");
        }
        catch (IOException)
        {
            throw Failure(
                ExitCode.InputOutput,
                "support-install-failed",
                "The support payload could not be installed.");
        }
        finally
        {
            if (archivePath is not null)
            {
                DeleteOwnedFile(archivePath);
            }

            if (stagedGenerationDirectory is not null)
            {
                DeleteOwnedDirectory(stagedGenerationDirectory);
            }

            if (!activationPublished && generationDirectory is not null)
            {
                DeleteOwnedDirectory(generationDirectory);
            }
        }
    }

    private Task<string> AcquireArchiveAsync(
        SupportRoot supportRoot,
        SupportArchiveSource archiveSource,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        return archiveSource switch
        {
            SupportArchiveSource.PinnedDownload => DownloadArchiveAsync(
                supportRoot,
                progress,
                cancellationToken),
            SupportArchiveSource.LocalFile localFile => CopyLocalArchiveAsync(
                supportRoot,
                localFile,
                progress,
                cancellationToken),
            _ => throw Failure(
                ExitCode.Usage,
                "support-archive-source-invalid",
                "The support archive source is invalid."),
        };
    }

    private async Task<string> DownloadArchiveAsync(
        SupportRoot supportRoot,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _manifest.Archive.DownloadUri);
        request.Headers.AcceptEncoding.ParseAdd("identity");
        using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw Failure(
                ExitCode.InputOutput,
                "support-download-failed",
                "The support archive server returned an unsuccessful response.");
        }

        Uri? finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is null ||
            !finalUri.IsAbsoluteUri ||
            !string.Equals(finalUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw Failure(
                ExitCode.InvalidData,
                "support-download-redirect-invalid",
                "The support archive download did not remain on HTTPS.");
        }

        if (response.Content.Headers.ContentLength is { } contentLength &&
            contentLength != _manifest.Archive.ByteLength)
        {
            throw Failure(
                ExitCode.InvalidData,
                "support-archive-length-mismatch",
                "The downloaded support archive length did not match the embedded manifest.");
        }

        if (response.Content.Headers.ContentEncoding.Any(
                static encoding => !string.Equals(encoding, "identity", StringComparison.OrdinalIgnoreCase)))
        {
            throw Failure(
                ExitCode.InvalidData,
                "support-download-encoding-invalid",
                "The support archive response used an unsupported content encoding.");
        }

        FileStream destination = CreateTemporaryArchive(supportRoot, out string archivePath);
        try
        {
            await using (destination)
            {
                await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await CopyAndVerifyArchiveAsync(
                        source,
                        destination,
                        "downloading-support",
                        "Downloading the pinned support archive.",
                        "Downloaded and verified the pinned support archive.",
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return archivePath;
        }
        catch
        {
            DeleteOwnedFile(archivePath);
            throw;
        }
    }

    private async Task<string> CopyLocalArchiveAsync(
        SupportRoot supportRoot,
        SupportArchiveSource.LocalFile localFile,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        string localArchivePath = NormalizeLocalArchivePath(localFile.ArchivePath);
        var localArchive = new FileInfo(localArchivePath);
        if (localArchive.LinkTarget is not null)
        {
            throw Failure(
                ExitCode.InvalidData,
                "support-local-archive-link-invalid",
                "The supplied support archive cannot be a symbolic link.");
        }

        if (!localArchive.Exists)
        {
            throw Failure(
                ExitCode.InputOutput,
                "support-local-archive-unavailable",
                "The supplied support archive could not be read.");
        }

        FileStream destination = CreateTemporaryArchive(supportRoot, out string archivePath);
        try
        {
            await using (destination)
            {
                await using var source = new FileStream(
                    localArchivePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    BufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await CopyAndVerifyArchiveAsync(
                        source,
                        destination,
                        "verifying-local-support",
                        "Verifying the supplied support archive.",
                        "Verified the supplied support archive.",
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return archivePath;
        }
        catch
        {
            DeleteOwnedFile(archivePath);
            throw;
        }
    }

    private async Task CopyAndVerifyArchiveAsync(
        Stream source,
        Stream destination,
        string progressKind,
        string initialMessage,
        string completedMessage,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new OperationProgress(
            progressKind,
            initialMessage,
            completed: 0,
            total: _manifest.Archive.ByteLength));

        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long copied = 0;
            long nextProgress = ProgressInterval;
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            while (true)
            {
                int read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                copied = checked(copied + read);
                if (copied > _manifest.Archive.ByteLength)
                {
                    throw Failure(
                        ExitCode.InvalidData,
                        "support-archive-length-mismatch",
                        "The support archive length did not match the embedded manifest.");
                }

                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                if (copied >= nextProgress)
                {
                    progress?.Report(new OperationProgress(
                        progressKind,
                        initialMessage,
                        completed: copied,
                        total: _manifest.Archive.ByteLength));
                    nextProgress = checked(copied + ProgressInterval);
                }
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (copied != _manifest.Archive.ByteLength)
            {
                throw Failure(
                    ExitCode.InvalidData,
                    "support-archive-length-mismatch",
                    "The support archive length did not match the embedded manifest.");
            }

            byte[] expectedDigest = Convert.FromHexString(_manifest.Archive.Sha256);
            byte[] actualDigest = hash.GetHashAndReset();
            if (!CryptographicOperations.FixedTimeEquals(actualDigest, expectedDigest))
            {
                throw Failure(
                    ExitCode.InvalidData,
                    "support-archive-digest-mismatch",
                    "The support archive did not match the embedded manifest.");
            }

            progress?.Report(new OperationProgress(
                progressKind,
                completedMessage,
                completed: copied,
                total: _manifest.Archive.ByteLength));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<long> ExtractPayloadAsync(
        string archivePath,
        string generationDirectory,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var expectedFiles = _manifest.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var extractedPaths = new HashSet<string>(StringComparer.Ordinal);
        var privateDirectories = new HashSet<string>(StringComparer.Ordinal);
        long extractedByteLength = 0;

        try
        {
            await using FileStream archiveStream = new(
                archivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);
            progress?.Report(new OperationProgress(
                "extracting-support",
                "Extracting the verified support archive.",
                completed: 0,
                total: _manifest.Files.Count));

            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool isDirectory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
                string path = NormalizeArchivePath(entry.FullName, isDirectory);
                if (IsSymbolicLink(entry))
                {
                    throw Failure(
                        ExitCode.InvalidData,
                        "support-archive-link-invalid",
                        "The support archive contains a symbolic-link entry.");
                }

                if (isDirectory)
                {
                    continue;
                }

                if (path is "common" or "xeBuild")
                {
                    throw Failure(
                        ExitCode.InvalidData,
                        "support-archive-entry-invalid",
                        "The support archive contains an invalid support entry.");
                }

                if (!IsSupportPayloadPath(path))
                {
                    continue;
                }

                if (!expectedFiles.TryGetValue(path, out SupportFile? expectedFile))
                {
                    throw Failure(
                        ExitCode.InvalidData,
                        "support-archive-entry-not-declared",
                        "The support archive contains an undeclared support file.");
                }

                if (!extractedPaths.Add(path))
                {
                    throw Failure(
                        ExitCode.InvalidData,
                        "support-archive-entry-duplicate",
                        "The support archive contains a duplicate support file.");
                }

                if (entry.Length != expectedFile.ByteLength)
                {
                    throw Failure(
                        ExitCode.InvalidData,
                        "support-file-length-mismatch",
                        "A support file length did not match the embedded manifest.");
                }

                string destinationPath = ResolveExtractionPath(generationDirectory, expectedFile.Path);
                string? destinationDirectory = Path.GetDirectoryName(destinationPath);
                if (destinationDirectory is null)
                {
                    throw new InvalidOperationException("A support file destination did not have a parent directory.");
                }

                CreatePrivateExtractionDirectories(
                    generationDirectory,
                    expectedFile.Path,
                    privateDirectories);
                SupportRootSafety.EnsureNoLinkAncestors(destinationDirectory);
                await using Stream source = entry.Open();
                await using var destination = new FileStream(
                    destinationPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(destinationPath, PrivateFileMode);
                }

                SupportRootSafety.EnsureSecureFile(destinationPath);
                await CopyAndVerifyFileAsync(source, destination, expectedFile, cancellationToken).ConfigureAwait(false);
                extractedByteLength = checked(extractedByteLength + expectedFile.ByteLength);
                progress?.Report(new OperationProgress(
                    "extracting-support",
                    "Extracting the verified support archive.",
                    completed: extractedPaths.Count,
                    total: _manifest.Files.Count));
            }
        }
        catch (InvalidDataException)
        {
            throw Failure(
                ExitCode.InvalidData,
                "support-archive-invalid",
                "The support archive is invalid.");
        }

        if (extractedPaths.Count != expectedFiles.Count)
        {
            throw Failure(
                ExitCode.InvalidData,
                "support-archive-entry-missing",
                "The support archive is missing a declared support file.");
        }

        if (extractedByteLength != _manifest.UncompressedByteLength)
        {
            throw Failure(
                ExitCode.InvalidData,
                "support-extraction-length-mismatch",
                "The extracted support payload length did not match the embedded manifest.");
        }

        return extractedByteLength;
    }

    private static async Task CopyAndVerifyFileAsync(
        Stream source,
        Stream destination,
        SupportFile expectedFile,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long copied = 0;
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            while (true)
            {
                int read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                copied = checked(copied + read);
                if (copied > expectedFile.ByteLength)
                {
                    throw Failure(
                        ExitCode.InvalidData,
                        "support-file-length-mismatch",
                        "A support file length did not match the embedded manifest.");
                }

                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (copied != expectedFile.ByteLength)
            {
                throw Failure(
                    ExitCode.InvalidData,
                    "support-file-length-mismatch",
                    "A support file length did not match the embedded manifest.");
            }

            byte[] expectedDigest = Convert.FromHexString(expectedFile.Sha256);
            byte[] actualDigest = hash.GetHashAndReset();
            if (!CryptographicOperations.FixedTimeEquals(actualDigest, expectedDigest))
            {
                throw Failure(
                    ExitCode.InvalidData,
                    "support-file-digest-mismatch",
                    "A support file did not match the embedded manifest.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task WriteActivationAsync(
        SupportRoot supportRoot,
        SupportActivationRecord activation,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new OperationProgress("activating-support", "Activating the verified support payload."));
        string activationPath = SupportPayloadLayout.GetActiveRecordPath(supportRoot);
        await using var output = AtomicOutputFile.Create(activationPath, force: true);
        await JsonSerializer.SerializeAsync(output.Stream, activation, ActivationJsonOptions, cancellationToken).ConfigureAwait(false);
        await output.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void PrepareSupportRoot(SupportRoot supportRoot)
    {
        CreatePrivateDirectory(supportRoot.DirectoryPath, requireEmpty: false);
        CreatePrivateDirectory(SupportPayloadLayout.GetInstallationsDirectory(supportRoot), requireEmpty: false);
        CreatePrivateDirectory(SupportPayloadLayout.GetStagingDirectory(supportRoot), requireEmpty: false);
        CreatePrivateDirectory(
            Path.Combine(supportRoot.DirectoryPath, DownloadDirectoryName),
            requireEmpty: false);
    }

    private static FileStream CreateTemporaryArchive(SupportRoot supportRoot, out string archivePath)
    {
        string downloadDirectory = Path.Combine(supportRoot.DirectoryPath, DownloadDirectoryName);
        for (int attempt = 0; attempt < 10; attempt++)
        {
            archivePath = Path.Combine(downloadDirectory, $"{Guid.NewGuid():N}.zip");
            try
            {
                return new FileStream(
                    archivePath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (IOException) when (attempt < 9)
            {
                // Retry only with a new cryptographically unique leaf name.
            }
        }

        archivePath = string.Empty;
        throw new IOException("Unable to allocate a unique support archive download path.");
    }

    private static void CreatePrivateExtractionDirectories(
        string generationDirectory,
        string relativePath,
        ISet<string> privateDirectories)
    {
        string currentDirectory = generationDirectory;
        int componentStart = 0;
        while (true)
        {
            int separator = relativePath.IndexOf('/', componentStart);
            if (separator < 0)
            {
                return;
            }

            currentDirectory = Path.Combine(
                currentDirectory,
                relativePath.Substring(componentStart, separator - componentStart));
            if (privateDirectories.Add(currentDirectory))
            {
                CreatePrivateDirectory(currentDirectory, requireEmpty: false);
            }

            componentStart = separator + 1;
        }
    }

    private static void CreatePrivateDirectory(string directoryPath, bool requireEmpty)
    {
        SupportRootSafety.EnsureSecureAncestors(directoryPath);
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directoryPath);
        }
        else
        {
            Directory.CreateDirectory(directoryPath, PrivateDirectoryMode);
        }

        SupportRootSafety.EnsurePrivateDirectory(directoryPath);
        if (requireEmpty && Directory.EnumerateFileSystemEntries(directoryPath).Any())
        {
            throw new IOException("Unable to allocate an empty support payload generation directory.");
        }
    }


    private static string NormalizeLocalArchivePath(string? archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            throw Failure(
                ExitCode.Usage,
                "support-local-archive-invalid",
                "The supplied support archive path is invalid.");
        }

        try
        {
            return Path.GetFullPath(archivePath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            throw Failure(
                ExitCode.Usage,
                "support-local-archive-invalid",
                "The supplied support archive path is invalid.");
        }
    }

    private static string NormalizeArchivePath(string value, bool isDirectory)
    {
        string path = isDirectory ? value[..^1] : value;
        if (string.IsNullOrEmpty(path) ||
            Path.IsPathRooted(path) ||
            path.Contains('\\', StringComparison.Ordinal) ||
            path.Contains(':', StringComparison.Ordinal))
        {
            throw Failure(
                ExitCode.InvalidData,
                "support-archive-path-invalid",
                "The support archive contains an invalid entry path.");
        }

        string[] segments = path.Split('/', StringSplitOptions.None);
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw Failure(
                ExitCode.InvalidData,
                "support-archive-path-invalid",
                "The support archive contains an invalid entry path.");
        }

        return string.Join("/", segments);
    }

    private static bool IsSymbolicLink(ZipArchiveEntry entry)
    {
        const int UnixFileTypeMask = 0xF000;
        const int UnixSymbolicLink = 0xA000;
        return ((entry.ExternalAttributes >> 16) & UnixFileTypeMask) == UnixSymbolicLink;
    }

    private static bool IsSupportPayloadPath(string path)
    {
        return path.StartsWith("common/", StringComparison.Ordinal) ||
            path.StartsWith("xeBuild/", StringComparison.Ordinal);
    }

    private static string ResolveExtractionPath(string generationDirectory, string relativePath)
    {
        string root = Path.GetFullPath(generationDirectory);
        string path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A support file path escaped the payload generation directory.");
        }

        return path;
    }

    private static void DeleteOwnedDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        if (directory.Exists && directory.LinkTarget is null)
        {
            Directory.Delete(path, recursive: true);
        }
    }


    private static void DeleteOwnedFile(string path)
    {
        if (File.Exists(path) && new FileInfo(path).LinkTarget is null)
        {
            File.Delete(path);
        }
    }

    private static OperationFailureException Failure(ExitCode code, string kind, string message)
    {
        return new OperationFailureException(code, kind, message);
    }
}
