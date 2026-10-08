using System.Buffers;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.InteropServices;
using JRunner.Core.Contracts;
using JRunner.Core.Support;

namespace JRunner.Cli.Infrastructure;

/// <summary>
/// Describes the integrity state of the active external support payload.
/// </summary>
public enum SupportStatusKind
{
    /// <summary>
    /// Neither an activation marker nor an installed generation exists.
    /// </summary>
    Absent,

    /// <summary>
    /// Exactly one activation side exists, or activation metadata cannot be safely verified.
    /// </summary>
    Incomplete,

    /// <summary>
    /// Verified activation metadata points to a generation that does not exactly match the embedded manifest.
    /// </summary>
    Corrupt,

    /// <summary>
    /// The activated generation exactly matches the embedded manifest.
    /// </summary>
    Valid,
}

/// <summary>
/// Contains a read-only assessment of the active external support payload.
/// </summary>
public sealed record SupportStatusResult(
    SupportStatusKind Status,
    string? PayloadId,
    string? ArchiveSha256,
    string? GenerationId,
    int ExpectedFileCount,
    long ExpectedByteLength,
    string? ProblemKind,
    string? ProblemPath,
    string? ReleaseTag,
    string? SourceUrl,
    string? CanonicalManifestSha256);

/// <summary>
/// Validates the active support generation against the application-owned manifest.
/// </summary>
public sealed class SupportPayloadValidator
{
    private const uint LinuxFileTypeMask = 0xF000;
    private const uint LinuxRegularFile = 0x8000;
    private const uint LinuxDirectory = 0x4000;
    private const uint LinuxSymbolicLink = 0xA000;
    private const int LinuxNoSuchFileOrDirectory = 2;
    private const int LinuxNotADirectory = 20;


    private const int BufferSize = 0x10000;
    private const int MaximumActivationRecordLength = 0x4000;
    private const string DownloadDirectoryName = ".downloads";

    private static readonly JsonDocumentOptions ActivationJsonOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
    };

    private readonly ExpectedTree? _expectedTree;
    private readonly bool _manifestInvalid;

    /// <summary>
    /// Creates a validator that trusts the embedded application manifest.
    /// </summary>
    public SupportPayloadValidator()
    {
        try
        {
            if (!TryCreateExpectedTree(EmbeddedSupportManifest.Current, out ExpectedTree? expectedTree) ||
                expectedTree is null)
            {
                _manifestInvalid = true;
                return;
            }

            _expectedTree = expectedTree!;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _manifestInvalid = true;
        }
    }

    internal SupportPayloadValidator(SupportManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!TryCreateExpectedTree(manifest, out ExpectedTree? expectedTree) || expectedTree is null)
        {
            _manifestInvalid = true;
            return;
        }

        _expectedTree = expectedTree!;
    }

    /// <summary>
    /// Determines the state of the active support generation without modifying the filesystem.
    /// </summary>
    public async Task<SupportStatusResult> ValidateAsync(
        SupportRoot supportRoot,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(supportRoot);
        cancellationToken.ThrowIfCancellationRequested();

        if (_manifestInvalid || _expectedTree is null)
        {
            return EmbeddedManifestInvalid();
        }

        ExpectedTree expected = _expectedTree!;

        progress?.Report(new OperationProgress("validating-support", "Validating the active support payload."));

        try
        {
            SupportRootSafety.EnsureSecureAncestors(supportRoot.DirectoryPath);
            return await ValidateRootAsync(supportRoot, expected, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (OperationFailureException failure) when (failure.Kind == "support-root-insecure")
        {
            return Incomplete(expected, "support-root-insecure");
        }
        catch (Exception exception) when (IsInspectionException(exception))
        {
            return Incomplete(expected, "support-root-unreadable");
        }
    }

    /// <summary>
    /// Revalidates one immutable generation without consulting or changing the active marker.
    /// </summary>
    /// <remarks>
    /// Callers that retain an active generation for an operation use this after the operation so
    /// another completed installation cannot make their integrity check observe a different generation.
    /// </remarks>
    internal async Task<SupportStatusResult> ValidateGenerationAsync(
        SupportRoot supportRoot,
        string generationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(supportRoot);
        if (!SupportPayloadLayout.IsSimpleName(generationId))
        {
            throw new ArgumentException(
                "The support generation identifier must be a normalized simple name.",
                nameof(generationId));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_manifestInvalid || _expectedTree is null)
        {
            return EmbeddedManifestInvalid();
        }

        ExpectedTree expected = _expectedTree!;
        string generationPath = SupportPayloadLayout.GetRelativeGenerationPath(generationId);
        try
        {
            SupportRootSafety.EnsureSecureAncestors(supportRoot.DirectoryPath);
            EntryKind rootKind = GetEntryKind(supportRoot.DirectoryPath);
            if (rootKind is EntryKind.Missing)
            {
                return Incomplete(expected, "support-root-missing", generationId: generationId);
            }

            if (rootKind is not EntryKind.Directory)
            {
                return Incomplete(expected, "support-root-invalid", generationId: generationId);
            }

            string installationsDirectory = SupportPayloadLayout.GetInstallationsDirectory(supportRoot);
            if (GetEntryKind(installationsDirectory) is not EntryKind.Directory)
            {
                return Incomplete(
                    expected,
                    "installations-invalid",
                    SupportPayloadLayout.InstallationsDirectoryName,
                    generationId);
            }

            SupportRootSafety.EnsurePrivateDirectory(installationsDirectory);
            string generationDirectory = SupportPayloadLayout.GetGenerationDirectory(supportRoot, generationId);
            EntryKind generationKind = GetEntryKind(generationDirectory);
            if (generationKind is EntryKind.Missing)
            {
                return Incomplete(expected, "generation-missing", generationPath, generationId);
            }

            if (generationKind is not EntryKind.Directory)
            {
                return Incomplete(expected, "generation-invalid", generationPath, generationId);
            }

            SupportRootSafety.EnsurePrivateDirectory(generationDirectory);
            return await ValidateGenerationAsync(
                    generationDirectory,
                    generationId,
                    expected,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (OperationFailureException failure) when (failure.Kind == "support-root-insecure")
        {
            return Incomplete(expected, "support-root-insecure", generationId: generationId);
        }
        catch (Exception exception) when (IsInspectionException(exception))
        {
            return Incomplete(expected, "support-root-unreadable", generationId: generationId);
        }
    }

    private static async Task<SupportStatusResult> ValidateRootAsync(
        SupportRoot supportRoot,
        ExpectedTree expected,
        CancellationToken cancellationToken)
    {
        EntryKind rootKind = GetEntryKind(supportRoot.DirectoryPath);
        if (rootKind is EntryKind.Missing)
        {
            return Absent(expected);
        }

        if (rootKind is not EntryKind.Directory)
        {
            return Incomplete(expected, "support-root-invalid");
        }

        string activeRecordPath = SupportPayloadLayout.GetActiveRecordPath(supportRoot);
        EntryKind activeRecordKind = GetEntryKind(activeRecordPath);
        if (activeRecordKind is EntryKind.Missing)
        {
            return ValidateInactiveRoot(supportRoot, expected, cancellationToken);
        }

        if (activeRecordKind is not EntryKind.File)
        {
            return Incomplete(expected, "active-record-invalid", SupportPayloadLayout.ActiveRecordFileName);
        }

        ParsedActivation? activation = await ReadActivationAsync(activeRecordPath, expected, cancellationToken).ConfigureAwait(false);
        if (activation is null)
        {
            return Incomplete(expected, "active-record-invalid", SupportPayloadLayout.ActiveRecordFileName);
        }

        string installationsDirectory = SupportPayloadLayout.GetInstallationsDirectory(supportRoot);
        if (GetEntryKind(installationsDirectory) is not EntryKind.Directory)
        {
            return Incomplete(
                expected,
                "installations-invalid",
                SupportPayloadLayout.InstallationsDirectoryName,
                activation.GenerationId);
        }
        SupportRootSafety.EnsurePrivateDirectory(installationsDirectory);


        string generationDirectory = SupportPayloadLayout.GetGenerationDirectory(supportRoot, activation.GenerationId);
        string generationPath = activation.ActivePayloadPath;
        EntryKind generationKind = GetEntryKind(generationDirectory);
        if (generationKind is EntryKind.Missing)
        {
            return Incomplete(
                expected,
                "active-generation-missing",
                generationPath,
                activation.GenerationId);
        }

        if (generationKind is not EntryKind.Directory)
        {
            return Incomplete(
                expected,
                "active-generation-invalid",
                generationPath,
                activation.GenerationId);
        }
        SupportRootSafety.EnsurePrivateDirectory(generationDirectory);


        return await ValidateGenerationAsync(
                generationDirectory,
                activation.GenerationId,
                expected,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static SupportStatusResult ValidateInactiveRoot(
        SupportRoot supportRoot,
        ExpectedTree expected,
        CancellationToken cancellationToken)
    {
        string installationsDirectory = SupportPayloadLayout.GetInstallationsDirectory(supportRoot);
        EntryKind installationsKind = GetEntryKind(installationsDirectory);
        if (installationsKind is not EntryKind.Missing and not EntryKind.Directory)
        {
            return Incomplete(expected, "installations-invalid", SupportPayloadLayout.InstallationsDirectoryName);
        }

        if (installationsKind is EntryKind.Directory)
        {
            foreach (string entryPath in EnumerateEntries(installationsDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string entryName = Path.GetFileName(entryPath);
                string? problemPath = TryBuildRelativePath(
                    SupportPayloadLayout.InstallationsDirectoryName,
                    entryName,
                    out string relativePath)
                    ? relativePath
                    : SupportPayloadLayout.InstallationsDirectoryName;
                return Incomplete(expected, "activation-missing", problemPath);
            }
        }

        foreach (string entryPath in EnumerateEntries(supportRoot.DirectoryPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string entryName = Path.GetFileName(entryPath);
            if (entryName == DownloadDirectoryName ||
                entryName == SupportPayloadLayout.StagingDirectoryName ||
                (entryName == SupportPayloadLayout.InstallationsDirectoryName && installationsKind is EntryKind.Directory))
            {
                continue;
            }

            return Incomplete(expected, "support-root-unrecognized-entry", TryBuildRelativePath(entryName));
        }

        return Absent(expected);
    }

    private static async Task<SupportStatusResult> ValidateGenerationAsync(
        string generationDirectory,
        string generationId,
        ExpectedTree expected,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            var seenFiles = new HashSet<string>(StringComparer.Ordinal);
            var pendingDirectories = new Stack<DirectoryToInspect>();
            pendingDirectories.Push(new DirectoryToInspect(generationDirectory, string.Empty));
            long aggregateByteLength = 0;

            while (pendingDirectories.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DirectoryToInspect directory = pendingDirectories.Pop();
                foreach (string entryPath in EnumerateEntries(directory.FullPath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string entryName = Path.GetFileName(entryPath);
                    if (!TryBuildRelativePath(directory.RelativePath, entryName, out string relativePath))
                    {
                        return Corrupt(expected, generationId, "support-tree-entry-invalid", directory.RelativePath);
                    }

                    EntryKind entryKind = GetEntryKind(entryPath);
                    if (entryKind is EntryKind.Link)
                    {
                        return Corrupt(expected, generationId, "support-tree-symlink", relativePath);
                    }

                    if (entryKind is EntryKind.Directory)
                    {
                        if (!expected.Directories.Contains(relativePath))
                        {
                            return Corrupt(expected, generationId, "support-tree-extra-entry", relativePath);
                        }
                        SupportRootSafety.EnsureSecureAncestors(entryPath);


                        pendingDirectories.Push(new DirectoryToInspect(entryPath, relativePath));
                        continue;
                    }

                    if (entryKind is not EntryKind.File)
                    {
                        return Corrupt(expected, generationId, "support-tree-nonregular-entry", relativePath);
                    }

                    if (!expected.FilesByPath.TryGetValue(relativePath, out ExpectedFile? expectedFile))
                    {
                        return Corrupt(expected, generationId, "support-tree-extra-entry", relativePath);
                    }

                    if (!seenFiles.Add(relativePath))
                    {
                        return Corrupt(expected, generationId, "support-tree-duplicate-entry", relativePath);
                    }
                    SupportRootSafety.EnsureSecureFile(entryPath);


                    FileValidationKind fileValidation = await ValidateFileAsync(
                            entryPath,
                            expectedFile,
                            buffer,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (fileValidation is FileValidationKind.NonRegular)
                    {
                        return Corrupt(expected, generationId, "support-tree-nonregular-entry", relativePath);
                    }

                    if (fileValidation is FileValidationKind.LengthMismatch)
                    {
                        return Corrupt(expected, generationId, "support-file-length-mismatch", relativePath);
                    }

                    if (fileValidation is FileValidationKind.DigestMismatch)
                    {
                        return Corrupt(expected, generationId, "support-file-digest-mismatch", relativePath);
                    }

                    aggregateByteLength = checked(aggregateByteLength + expectedFile.ByteLength);
                }
            }

            foreach (ExpectedFile expectedFile in expected.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!seenFiles.Contains(expectedFile.Path))
                {
                    return Corrupt(expected, generationId, "support-file-missing", expectedFile.Path);
                }
            }

            if (seenFiles.Count != expected.Files.Length || aggregateByteLength != expected.ByteLength)
            {
                return Corrupt(expected, generationId, "support-aggregate-mismatch");
            }

            return Status(SupportStatusKind.Valid, expected, generationId, null, null);
        }
        catch (OverflowException)
        {
            return Corrupt(expected, generationId, "support-aggregate-mismatch");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<FileValidationKind> ValidateFileAsync(
        string filePath,
        ExpectedFile expectedFile,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        if (GetEntryKind(filePath) is not EntryKind.File)
        {
            return FileValidationKind.NonRegular;
        }

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long actualByteLength = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            actualByteLength = checked(actualByteLength + read);
            if (actualByteLength > expectedFile.ByteLength)
            {
                return FileValidationKind.LengthMismatch;
            }

            hash.AppendData(buffer, 0, read);
        }

        if (actualByteLength != expectedFile.ByteLength)
        {
            return FileValidationKind.LengthMismatch;
        }

        byte[] actualDigest = hash.GetHashAndReset();
        return CryptographicOperations.FixedTimeEquals(actualDigest, expectedFile.Digest)
            ? FileValidationKind.Valid
            : FileValidationKind.DigestMismatch;
    }

    private static async Task<ParsedActivation?> ReadActivationAsync(
        string activeRecordPath,
        ExpectedTree expected,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(MaximumActivationRecordLength + 1);
        try
        {
            int length = 0;
            try
            {
                await using var stream = new FileStream(
                    activeRecordPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    BufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                while (length <= MaximumActivationRecordLength)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int read = await stream.ReadAsync(
                            buffer.AsMemory(length, MaximumActivationRecordLength + 1 - length),
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    length += read;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (IsInspectionException(exception))
            {
                return null;
            }

            if (length > MaximumActivationRecordLength)
            {
                return null;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(buffer.AsMemory(0, length), ActivationJsonOptions);
                return TryParseActivation(document.RootElement, expected, out ParsedActivation? activation)
                    ? activation
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool TryParseActivation(
        JsonElement document,
        ExpectedTree expected,
        out ParsedActivation? activation)
    {
        activation = null;
        if (document.ValueKind is not JsonValueKind.Object)
        {
            return false;
        }

        bool sawSchemaVersion = false;
        bool sawPayloadId = false;
        bool sawReleaseTag = false;
        bool sawSourceUrl = false;
        bool sawArchiveSha256 = false;
        bool sawCanonicalManifestSha256 = false;
        bool sawInstalledAtUtc = false;
        bool sawActivePayloadPath = false;
        bool sawGenerationId = false;
        int schemaVersion = 0;
        string? payloadId = null;
        string? releaseTag = null;
        string? sourceUrl = null;
        string? archiveSha256 = null;
        string? canonicalManifestSha256 = null;
        DateTimeOffset installedAtUtc = default;
        string? activePayloadPath = null;
        string? generationId = null;

        foreach (JsonProperty property in document.EnumerateObject())
        {
            if (property.NameEquals("schemaVersion"))
            {
                if (sawSchemaVersion || property.Value.ValueKind is not JsonValueKind.Number ||
                    !property.Value.TryGetInt32(out schemaVersion))
                {
                    return false;
                }

                sawSchemaVersion = true;
                continue;
            }

            if (property.NameEquals("payloadId"))
            {
                if (!TryReadString(property.Value, ref sawPayloadId, out payloadId))
                {
                    return false;
                }

                continue;
            }

            if (property.NameEquals("releaseTag"))
            {
                if (!TryReadString(property.Value, ref sawReleaseTag, out releaseTag))
                {
                    return false;
                }

                continue;
            }

            if (property.NameEquals("sourceUrl"))
            {
                if (!TryReadString(property.Value, ref sawSourceUrl, out sourceUrl))
                {
                    return false;
                }

                continue;
            }

            if (property.NameEquals("archiveSha256"))
            {
                if (!TryReadString(property.Value, ref sawArchiveSha256, out archiveSha256))
                {
                    return false;
                }

                continue;
            }

            if (property.NameEquals("canonicalManifestSha256"))
            {
                if (!TryReadString(
                        property.Value,
                        ref sawCanonicalManifestSha256,
                        out canonicalManifestSha256))
                {
                    return false;
                }

                continue;
            }

            if (property.NameEquals("installedAtUtc"))
            {
                if (sawInstalledAtUtc ||
                    property.Value.ValueKind is not JsonValueKind.String ||
                    !property.Value.TryGetDateTimeOffset(out installedAtUtc) ||
                    installedAtUtc.Offset != TimeSpan.Zero)
                {
                    return false;
                }

                sawInstalledAtUtc = true;
                continue;
            }

            if (property.NameEquals("activePayloadPath"))
            {
                if (!TryReadString(property.Value, ref sawActivePayloadPath, out activePayloadPath))
                {
                    return false;
                }

                continue;
            }

            if (property.NameEquals("generationId"))
            {
                if (!TryReadString(property.Value, ref sawGenerationId, out generationId))
                {
                    return false;
                }

                continue;
            }

            return false;
        }

        if (!sawSchemaVersion ||
            !sawPayloadId ||
            !sawReleaseTag ||
            !sawSourceUrl ||
            !sawArchiveSha256 ||
            !sawCanonicalManifestSha256 ||
            !sawInstalledAtUtc ||
            !sawActivePayloadPath ||
            !sawGenerationId ||
            payloadId is null ||
            releaseTag is null ||
            sourceUrl is null ||
            archiveSha256 is null ||
            canonicalManifestSha256 is null ||
            activePayloadPath is null ||
            generationId is null ||
            schemaVersion != SupportPayloadLayout.ActivationSchemaVersion ||
            !string.Equals(payloadId, expected.PayloadId, StringComparison.Ordinal) ||
            !string.Equals(releaseTag, expected.ReleaseTag, StringComparison.Ordinal) ||
            !string.Equals(sourceUrl, expected.SourceUrl, StringComparison.Ordinal) ||
            !string.Equals(archiveSha256, expected.ArchiveSha256, StringComparison.Ordinal) ||
            !string.Equals(
                canonicalManifestSha256,
                expected.CanonicalManifestSha256,
                StringComparison.Ordinal) ||
            !SupportPayloadLayout.IsSimpleName(generationId) ||
            !string.Equals(
                activePayloadPath,
                SupportPayloadLayout.GetRelativeGenerationPath(generationId),
                StringComparison.Ordinal))
        {
            return false;
        }

        activation = new ParsedActivation(generationId, installedAtUtc, activePayloadPath);
        return true;
    }

    private static bool TryReadString(JsonElement value, ref bool seen, out string? result)
    {
        result = null;
        if (seen || value.ValueKind is not JsonValueKind.String)
        {
            return false;
        }

        result = value.GetString();
        if (result is null)
        {
            return false;
        }

        seen = true;
        return true;
    }

    private static bool TryCreateExpectedTree(SupportManifest manifest, out ExpectedTree? expected)
    {
        expected = null;
        try
        {
            if (!IsPayloadId(manifest.PayloadId) ||
                !IsReleaseTag(manifest.ReleaseTag) ||
                !IsSha256(manifest.CanonicalManifestSha256) ||
                manifest.Archive is null ||
                manifest.Archive.DownloadUri is null ||
                !manifest.Archive.DownloadUri.IsAbsoluteUri ||
                !string.Equals(
                    manifest.Archive.DownloadUri.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrEmpty(manifest.Archive.DownloadUri.UserInfo) ||
                !string.IsNullOrEmpty(manifest.Archive.DownloadUri.Fragment) ||
                manifest.Archive.ByteLength <= 0 ||
                !IsSha256(manifest.Archive.Sha256) ||
                manifest.UncompressedByteLength <= 0 ||
                manifest.Files is null ||
                manifest.Files.Count == 0)
            {
                return false;
            }

            var filesByPath = new Dictionary<string, ExpectedFile>(StringComparer.Ordinal);
            var caseInsensitivePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var directories = new HashSet<string>(StringComparer.Ordinal);
            var files = new List<ExpectedFile>(manifest.Files.Count);
            long totalByteLength = 0;

            foreach (SupportFile? file in manifest.Files)
            {
                if (file is null ||
                    !IsSupportPayloadPath(file.Path) ||
                    file.ByteLength < 0 ||
                    !IsSha256(file.Sha256) ||
                    !filesByPath.TryAdd(
                        file.Path,
                        new ExpectedFile(file.Path, file.ByteLength, Convert.FromHexString(file.Sha256))) ||
                    !caseInsensitivePaths.Add(file.Path))
                {
                    return false;
                }

                AddParentDirectories(file.Path, directories);
                totalByteLength = checked(totalByteLength + file.ByteLength);
                files.Add(filesByPath[file.Path]);
            }

            if (totalByteLength != manifest.UncompressedByteLength)
            {
                return false;
            }

            files.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Path, right.Path));
            expected = new ExpectedTree(
                manifest.PayloadId,
                manifest.ReleaseTag,
                manifest.Archive.DownloadUri.AbsoluteUri,
                manifest.Archive.Sha256,
                manifest.CanonicalManifestSha256,
                manifest.UncompressedByteLength,
                files.ToArray(),
                filesByPath,
                directories);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
        {
            return false;
        }
    }

    private static bool IsSupportPayloadPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            Path.IsPathRooted(path) ||
            path.Contains(':', StringComparison.Ordinal) ||
            path.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        string[] segments = path.Split('/', StringSplitOptions.None);
        if (segments.Any(static segment => segment.Length == 0 || segment is "." or ".."))
        {
            return false;
        }

        string normalizedPath = string.Join("/", segments);
        return string.Equals(path, normalizedPath, StringComparison.Ordinal) &&
            (normalizedPath.StartsWith("common/", StringComparison.Ordinal) ||
             normalizedPath.StartsWith("xeBuild/", StringComparison.Ordinal));
    }

    private static bool IsPayloadId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (char character in value)
        {
            if ((character < 'a' || character > 'z') &&
                (character < '0' || character > '9') &&
                character is not '-' and not '.')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsReleaseTag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (char character in value)
        {
            if ((character < 'A' || character > 'Z') &&
                (character < 'a' || character > 'z') &&
                (character < '0' || character > '9') &&
                character is not '-' and not '.')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSha256(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length != 64)
        {
            return false;
        }

        foreach (char character in value)
        {
            if ((character < '0' || character > '9') && (character < 'A' || character > 'F'))
            {
                return false;
            }
        }

        return true;
    }

    private static void AddParentDirectories(string path, ISet<string> directories)
    {
        int separator = path.IndexOf('/');
        while (separator >= 0)
        {
            directories.Add(path[..separator]);
            separator = path.IndexOf('/', separator + 1);
        }
    }

    private static IEnumerable<string> EnumerateEntries(string directoryPath)
    {
        return Directory.EnumerateFileSystemEntries(
            directoryPath,
            "*",
            new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = false,
                AttributesToSkip = (FileAttributes)0,
            });
    }

    private static EntryKind GetEntryKind(string path)
    {
        if (OperatingSystem.IsLinux() &&
            RuntimeInformation.ProcessArchitecture is Architecture.X64)
        {
            return GetLinuxEntryKind(path);
        }

        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return EntryKind.Link;
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                return EntryKind.Directory;
            }

            return (attributes & FileAttributes.Device) != 0 ? EntryKind.Other : EntryKind.File;
        }
        catch (FileNotFoundException)
        {
            return EntryKind.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return EntryKind.Missing;
        }
    }

    private static EntryKind GetLinuxEntryKind(string path)
    {
        if (LStat(path, out LinuxStat status) != 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error is LinuxNoSuchFileOrDirectory or LinuxNotADirectory)
            {
                return EntryKind.Missing;
            }

            throw new IOException("Unable to inspect a support payload entry.");
        }

        return (status.Mode & LinuxFileTypeMask) switch
        {
            LinuxRegularFile => EntryKind.File,
            LinuxDirectory => EntryKind.Directory,
            LinuxSymbolicLink => EntryKind.Link,
            _ => EntryKind.Other,
        };
    }

    private static SupportStatusResult EmbeddedManifestInvalid()
    {
        return new SupportStatusResult(
            SupportStatusKind.Incomplete,
            PayloadId: null,
            ArchiveSha256: null,
            GenerationId: null,
            ExpectedFileCount: 0,
            ExpectedByteLength: 0,
            ProblemKind: "embedded-manifest-invalid",
            ProblemPath: null,
            ReleaseTag: null,
            SourceUrl: null,
            CanonicalManifestSha256: null);
    }

    private static SupportStatusResult Absent(ExpectedTree expected)
    {
        return Status(SupportStatusKind.Absent, expected, generationId: null, problemKind: null, problemPath: null);
    }

    private static SupportStatusResult Incomplete(
        ExpectedTree expected,
        string problemKind,
        string? problemPath = null,
        string? generationId = null)
    {
        return Status(SupportStatusKind.Incomplete, expected, generationId, problemKind, problemPath);
    }

    private static SupportStatusResult Corrupt(
        ExpectedTree expected,
        string generationId,
        string problemKind,
        string? problemPath = null)
    {
        return Status(SupportStatusKind.Corrupt, expected, generationId, problemKind, problemPath);
    }

    private static SupportStatusResult Status(
        SupportStatusKind status,
        ExpectedTree expected,
        string? generationId,
        string? problemKind,
        string? problemPath)
    {
        return new SupportStatusResult(
            status,
            expected.PayloadId,
            expected.ArchiveSha256,
            generationId,
            expected.Files.Length,
            expected.ByteLength,
            problemKind,
            problemPath,
            expected.ReleaseTag,
            expected.SourceUrl,
            expected.CanonicalManifestSha256);
    }

    private static string BuildRelativePath(string firstSegment, string secondSegment)
    {
        return string.Concat(firstSegment, "/", secondSegment);
    }

    private static string? TryBuildRelativePath(string segment)
    {
        return IsSafePathSegment(segment) ? segment : null;
    }

    private static bool TryBuildRelativePath(string parent, string segment, out string path)
    {
        if (!IsSafePathSegment(segment))
        {
            path = string.Empty;
            return false;
        }

        path = parent.Length == 0 ? segment : BuildRelativePath(parent, segment);
        return true;
    }

    private static bool IsSafePathSegment(string segment)
    {
        return !string.IsNullOrEmpty(segment) &&
            segment is not "." and not ".." &&
            !Path.IsPathRooted(segment) &&
            segment.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/', '\\']) < 0;
    }

    private static bool IsInspectionException(Exception exception)
    {
        return exception is IOException or
            UnauthorizedAccessException or
            SecurityException or
            ArgumentException or
            NotSupportedException or
            InvalidDataException or
            CryptographicException;
    }


    private sealed record ExpectedTree(
        string PayloadId,
        string ReleaseTag,
        string SourceUrl,
        string ArchiveSha256,
        string CanonicalManifestSha256,
        long ByteLength,
        ExpectedFile[] Files,
        IReadOnlyDictionary<string, ExpectedFile> FilesByPath,
        IReadOnlySet<string> Directories);

    private sealed record ExpectedFile(string Path, long ByteLength, byte[] Digest);

    private sealed record ParsedActivation(
        string GenerationId,
        DateTimeOffset InstalledAtUtc,
        string ActivePayloadPath);

    private sealed record DirectoryToInspect(string FullPath, string RelativePath);

    private enum EntryKind
    {
        Missing,
        File,
        Directory,
        Link,
        Other,
    }

    private enum FileValidationKind
    {
        Valid,
        LengthMismatch,
        DigestMismatch,
        NonRegular,
    }
    // Linux's 64-bit stat ABI stores st_mode at byte offset 24.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStat
    {
        [FieldOffset(24)]
        internal uint Mode;
    }

    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
    private static extern int LStat(string path, out LinuxStat status);

}
