using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JRunner.Core.Support;

/// <summary>
/// Describes the exact external support payload accepted by the native application.
/// </summary>
public sealed class SupportManifest
{
    internal SupportManifest(
        int schemaVersion,
        string payloadId,
        SupportArchive archive,
        long uncompressedByteLength,
        IReadOnlyList<SupportFile> files)
        : this(
            schemaVersion,
            payloadId,
            payloadId,
            archive,
            uncompressedByteLength,
            files,
            DeriveCanonicalManifestSha256(
                schemaVersion,
                payloadId,
                payloadId,
                archive,
                uncompressedByteLength,
                files))
    {
    }

    internal SupportManifest(
        int schemaVersion,
        string payloadId,
        string releaseTag,
        SupportArchive archive,
        long uncompressedByteLength,
        IReadOnlyList<SupportFile> files,
        string canonicalManifestSha256)
    {
        SchemaVersion = schemaVersion;
        PayloadId = payloadId;
        ReleaseTag = releaseTag;
        Archive = archive;
        UncompressedByteLength = uncompressedByteLength;
        Files = files;
        CanonicalManifestSha256 = canonicalManifestSha256;
    }

    /// <summary>
    /// Gets the schema used to describe this payload.
    /// </summary>
    public int SchemaVersion { get; }

    /// <summary>
    /// Gets the stable identifier for this immutable payload release.
    /// </summary>
    public string PayloadId { get; }

    /// <summary>
    /// Gets the immutable upstream release tag that identifies this payload.
    /// </summary>
    public string ReleaseTag { get; }

    /// <summary>
    /// Gets the pinned archive acquisition and integrity information.
    /// </summary>
    public SupportArchive Archive { get; }

    /// <summary>
    /// Gets the expected aggregate uncompressed size of all extracted support files.
    /// </summary>
    public long UncompressedByteLength { get; }

    /// <summary>
    /// Gets every support file that may be extracted from the pinned archive.
    /// </summary>
    public IReadOnlyList<SupportFile> Files { get; }

    /// <summary>
    /// Gets the canonical SHA-256 bound to this manifest.
    /// </summary>
    public string CanonicalManifestSha256 { get; }

    private static string DeriveCanonicalManifestSha256(
        int schemaVersion,
        string payloadId,
        string releaseTag,
        SupportArchive archive,
        long uncompressedByteLength,
        IReadOnlyList<SupportFile> files)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(files);

        var canonical = new StringBuilder();
        canonical.Append(schemaVersion).Append('\n');
        canonical.Append(payloadId).Append('\n');
        canonical.Append(releaseTag).Append('\n');
        canonical.Append(archive.DownloadUri.AbsoluteUri).Append('\n');
        canonical.Append(archive.ByteLength).Append('\n');
        canonical.Append(archive.Sha256).Append('\n');
        canonical.Append(uncompressedByteLength).Append('\n');
        foreach (SupportFile file in files)
        {
            canonical.Append(file.Path).Append('\n');
            canonical.Append(file.ByteLength).Append('\n');
            canonical.Append(file.Sha256).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }
}

/// <summary>
/// Describes the archive containing the immutable support payload.
/// </summary>
public sealed class SupportArchive
{
    internal SupportArchive(Uri downloadUri, long byteLength, string sha256)
    {
        DownloadUri = downloadUri;
        ByteLength = byteLength;
        Sha256 = sha256;
    }

    /// <summary>
    /// Gets the HTTPS location of the pinned archive.
    /// </summary>
    public Uri DownloadUri { get; }

    /// <summary>
    /// Gets the expected archive byte length.
    /// </summary>
    public long ByteLength { get; }

    /// <summary>
    /// Gets the uppercase hexadecimal SHA-256 of the archive.
    /// </summary>
    public string Sha256 { get; }
}

/// <summary>
/// Describes one exact extracted file in the immutable support payload.
/// </summary>
public sealed class SupportFile
{
    internal SupportFile(string path, long byteLength, string sha256)
    {
        Path = path;
        ByteLength = byteLength;
        Sha256 = sha256;
    }

    /// <summary>
    /// Gets the normalized slash-separated path relative to the support root.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Gets the expected file length.
    /// </summary>
    public long ByteLength { get; }

    /// <summary>
    /// Gets the uppercase hexadecimal SHA-256 of the file.
    /// </summary>
    public string Sha256 { get; }
}

/// <summary>
/// Loads the application-owned support manifest embedded in the Core assembly.
/// </summary>
public static class EmbeddedSupportManifest
{
    private const int SupportedSchemaVersion = 1;
    private const string ExpectedPayloadId = "jrunner-with-extras-v3.4.0-r7";
    private const string ExpectedReleaseTag = "V3.4.0-r7";
    private const string ExpectedArchiveUri =
        "https://github.com/J-Runner-With-Extras/J-Runner-with-Extras/releases/download/V3.4.0-r7/J-Runner-with-Extras.zip";
    private const long ExpectedArchiveByteLength = 233542155;
    private const string ExpectedArchiveSha256 = "C92A31D21D7DC617B3DAE47E44B3AE9988ACF8B94C0DA390F8A69E6805BC45B6";
    private const string ResourceName = "JRunner.Core.Support.SupportManifest.v1.json";

    /// <summary>
    /// Gets the SHA-256 expected for the canonical reviewed manifest resource.
    /// </summary>
    public const string CanonicalManifestSha256 = "E14FC03AA0FB449B75EF10C8F5B545ECDEFC5489BF18FCB259C042F95D7946E7";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly Lazy<SupportManifest> ManifestLoader = new(Load);

    /// <summary>
    /// Gets the sole support manifest trusted by this application build.
    /// </summary>
    public static SupportManifest Current => ManifestLoader.Value;

    private static SupportManifest Load()
    {
        byte[] expectedDigest = Convert.FromHexString(CanonicalManifestSha256);
        byte[] actualDigest;
        using (Stream integrityStream = OpenResource())
        {
            actualDigest = SHA256.HashData(integrityStream);
        }

        if (!CryptographicOperations.FixedTimeEquals(actualDigest, expectedDigest))
        {
            throw Invalid("the canonical SHA-256 does not match the embedded resource");
        }

        using Stream stream = OpenResource();
        ManifestDocument document = JsonSerializer.Deserialize<ManifestDocument>(stream, JsonOptions)
            ?? throw Invalid("the resource is empty");
        return Validate(document, Convert.ToHexString(actualDigest));
    }

    private static Stream OpenResource()
    {
        return typeof(EmbeddedSupportManifest).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw Invalid("the resource is missing");
    }

    private static SupportManifest Validate(ManifestDocument document, string canonicalManifestSha256)
    {
        if (document.SchemaVersion != SupportedSchemaVersion)
        {
            throw Invalid($"schema version {document.SchemaVersion} is unsupported");
        }

        if (!IsPayloadId(document.PayloadId))
        {
            throw Invalid("the payload identifier is invalid");
        }

        if (!IsReleaseTag(document.ReleaseTag))
        {
            throw Invalid("the release tag is invalid");
        }

        ArchiveDocument archiveDocument = document.Archive
            ?? throw Invalid("the archive is missing");
        if (!Uri.TryCreate(archiveDocument.Uri, UriKind.Absolute, out Uri? archiveUri) ||
            archiveUri is null ||
            !string.Equals(archiveUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(archiveUri.UserInfo) ||
            !string.IsNullOrEmpty(archiveUri.Fragment) ||
            archiveDocument.ByteLength <= 0 ||
            !IsSha256(archiveDocument.Sha256))
        {
            throw Invalid("the archive declaration is invalid");
        }

        if (document.UncompressedByteLength <= 0 || document.Files is null || document.Files.Count == 0)
        {
            throw Invalid("the file list is empty");
        }

        var files = new List<SupportFile>(document.Files.Count);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var caseInsensitivePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? previousPath = null;
        long totalByteLength = 0;
        foreach (FileDocument file in document.Files)
        {
            string normalizedPath = NormalizeRelativePath(file.Path);
            if (!string.Equals(file.Path, normalizedPath, StringComparison.Ordinal) ||
                (!normalizedPath.StartsWith("common/", StringComparison.Ordinal) &&
                 !normalizedPath.StartsWith("xeBuild/", StringComparison.Ordinal)) ||
                !paths.Add(normalizedPath) ||
                !caseInsensitivePaths.Add(normalizedPath) ||
                (previousPath is not null && StringComparer.Ordinal.Compare(previousPath, normalizedPath) >= 0) ||
                file.ByteLength < 0 ||
                !IsSha256(file.Sha256))
            {
                throw Invalid($"file declaration '{file.Path}' is invalid");
            }

            try
            {
                totalByteLength = checked(totalByteLength + file.ByteLength);
            }
            catch (OverflowException exception)
            {
                throw Invalid("the uncompressed byte length overflows", exception);
            }

            files.Add(new SupportFile(normalizedPath, file.ByteLength, file.Sha256));
            previousPath = normalizedPath;
        }

        if (totalByteLength != document.UncompressedByteLength)
        {
            throw Invalid("the uncompressed byte length does not match the file list");
        }

        var manifest = new SupportManifest(
            document.SchemaVersion,
            document.PayloadId,
            document.ReleaseTag,
            new SupportArchive(archiveUri, archiveDocument.ByteLength, archiveDocument.Sha256),
            totalByteLength,
            new ReadOnlyCollection<SupportFile>(files),
            canonicalManifestSha256);
        if (!string.Equals(manifest.PayloadId, ExpectedPayloadId, StringComparison.Ordinal) ||
            !string.Equals(manifest.ReleaseTag, ExpectedReleaseTag, StringComparison.Ordinal) ||
            !string.Equals(manifest.Archive.DownloadUri.AbsoluteUri, ExpectedArchiveUri, StringComparison.Ordinal) ||
            manifest.Archive.ByteLength != ExpectedArchiveByteLength ||
            !string.Equals(manifest.Archive.Sha256, ExpectedArchiveSha256, StringComparison.Ordinal))
        {
            throw Invalid("the pinned V3.4.0-r7 archive declaration is invalid");
        }

        return manifest;
    }

    private static string NormalizeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            Path.IsPathRooted(path) ||
            path.Contains(':', StringComparison.Ordinal))
        {
            throw Invalid("a file path is empty or rooted");
        }

        string[] segments = path.Split(['/', '\\'], StringSplitOptions.None);
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw Invalid("a file path is not a normalized descendant path");
        }

        return string.Join("/", segments);
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

    private static InvalidOperationException Invalid(string reason, Exception? innerException = null)
    {
        return new InvalidOperationException($"The embedded support manifest is invalid: {reason}.", innerException);
    }

    private sealed class ManifestDocument
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("payloadId")]
        public string PayloadId { get; init; } = string.Empty;

        [JsonPropertyName("releaseTag")]
        public string ReleaseTag { get; init; } = string.Empty;

        [JsonPropertyName("archive")]
        public ArchiveDocument? Archive { get; init; }

        [JsonPropertyName("uncompressedByteLength")]
        public long UncompressedByteLength { get; init; }

        [JsonPropertyName("files")]
        public List<FileDocument>? Files { get; init; }
    }

    private sealed class ArchiveDocument
    {
        [JsonPropertyName("uri")]
        public string Uri { get; init; } = string.Empty;

        [JsonPropertyName("byteLength")]
        public long ByteLength { get; init; }

        [JsonPropertyName("sha256")]
        public string Sha256 { get; init; } = string.Empty;
    }

    private sealed class FileDocument
    {
        [JsonPropertyName("path")]
        public string Path { get; init; } = string.Empty;

        [JsonPropertyName("byteLength")]
        public long ByteLength { get; init; }

        [JsonPropertyName("sha256")]
        public string Sha256 { get; init; } = string.Empty;
    }
}
