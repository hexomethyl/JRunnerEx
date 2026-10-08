using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JRunner.Tests.Fixtures;

internal static class FixtureCatalog
{
    private const string ExpectedFixtureSet = "jrunner-native-core";

    internal static string RootDirectory { get; } = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixtures"));

    private static readonly Lazy<FixtureManifest> ManifestLoader = new(() => LoadManifestFromDirectory(RootDirectory));
    private static readonly ConcurrentDictionary<string, byte> VerifiedFixturePaths = new(StringComparer.Ordinal);

    internal static FixtureManifest Manifest => ManifestLoader.Value;

    internal static string GetPath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        FixtureFile fixture = Manifest.Files.SingleOrDefault(
            file => string.Equals(file.Path, relativePath, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"The fixture manifest does not declare '{relativePath}'.");
        string path = ResolvePath(relativePath);
        _ = VerifiedFixturePaths.GetOrAdd(path, static (fixturePath, manifestFile) =>
        {
            ValidateFixture(fixturePath, manifestFile);
            return 0;
        }, fixture);
        return path;
    }

    internal static FixtureManifest LoadManifestFromDirectory(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        string manifestPath = Path.Combine(Path.GetFullPath(rootDirectory), "manifest.v1.json");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException($"The fixture manifest was not copied to '{manifestPath}'.");
        }

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        FixtureManifest manifest = JsonSerializer.Deserialize<FixtureManifest>(File.ReadAllText(manifestPath), options)
            ?? throw new InvalidOperationException("The fixture manifest is empty.");
        if (manifest.SchemaVersion != 1)
        {
            throw new InvalidOperationException($"Unsupported fixture manifest schema version {manifest.SchemaVersion}.");
        }

        if (!string.Equals(manifest.FixtureSet, ExpectedFixtureSet, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The fixture manifest does not identify the JRunner native fixture set.");
        }

        if (manifest.Files.Count == 0)
        {
            throw new InvalidOperationException("The fixture manifest does not declare any files.");
        }

        var paths = new HashSet<string>(StringComparer.Ordinal);
        var caseInsensitivePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (FixtureFile file in manifest.Files)
        {
            if (string.IsNullOrWhiteSpace(file.Path))
            {
                throw new InvalidOperationException("The fixture manifest declares an empty path.");
            }

            string normalizedPath = NormalizeRelativePath(file.Path);
            if (!string.Equals(file.Path, normalizedPath, StringComparison.Ordinal) ||
                !paths.Add(normalizedPath) ||
                !caseInsensitivePaths.Add(normalizedPath))
            {
                throw new InvalidOperationException(
                    $"The fixture manifest declares duplicate, case-colliding, or non-normalized path '{file.Path}'.");
            }

            if (file.ByteLength < 0)
            {
                throw new InvalidOperationException($"The fixture manifest declares a negative length for '{file.Path}'.");
            }

            if (string.IsNullOrWhiteSpace(file.Sha256) || file.Sha256.Length != 64)
            {
                throw new InvalidOperationException($"The fixture manifest declares an invalid SHA-256 length for '{file.Path}'.");
            }

            try
            {
                _ = Convert.FromHexString(file.Sha256);
            }
            catch (FormatException exception)
            {
                throw new InvalidOperationException($"The fixture manifest declares an invalid SHA-256 for '{file.Path}'.", exception);
            }
        }

        _ = manifest.Scenarios ?? throw new InvalidOperationException("The fixture manifest does not declare scenarios.");
        return manifest;
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException("Fixture paths must be nonempty relative paths.");
        }

        string[] segments = relativePath.Split(['/', '\\'], StringSplitOptions.None);
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new InvalidOperationException("Fixture paths must be normalized descendant paths.");
        }

        return string.Join("/", segments);
    }

    private static string ResolvePath(string relativePath)
    {
        string normalizedRelativePath = NormalizeRelativePath(relativePath).Replace('/', Path.DirectorySeparatorChar);
        string resolvedPath = Path.GetFullPath(Path.Combine(RootDirectory, normalizedRelativePath));
        string rootWithSeparator = RootDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? RootDirectory
            : RootDirectory + Path.DirectorySeparatorChar;
        if (!resolvedPath.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Fixture paths must stay under the fixture root.");
        }

        return resolvedPath;
    }

    private static void ValidateFixture(string path, FixtureFile fixture)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"The fixture '{fixture.Path}' was not copied to the test output.");
        }

        if (new FileInfo(path).Length != fixture.ByteLength)
        {
            throw new InvalidOperationException($"The fixture '{fixture.Path}' does not match its manifested byte length.");
        }

        using FileStream stream = File.OpenRead(path);
        string digest = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(digest, fixture.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The fixture '{fixture.Path}' does not match its manifested SHA-256.");
        }
    }
}

internal sealed class FixtureManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("fixtureSet")]
    public string FixtureSet { get; init; } = string.Empty;

    [JsonPropertyName("files")]
    public List<FixtureFile> Files { get; init; } = [];

    [JsonPropertyName("scenarios")]
    public FixtureScenarios? Scenarios { get; init; }
}

internal sealed class FixtureFile
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("byteLength")]
    public long ByteLength { get; init; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = string.Empty;
}

internal sealed class FixtureScenarios
{
    [JsonPropertyName("nandInspection")]
    public NandInspectionFixture? NandInspection { get; init; }

    [JsonPropertyName("canonicalComparison")]
    public CanonicalComparisonFixture? CanonicalComparison { get; init; }

    [JsonPropertyName("patchInspection")]
    public PatchInspectionFixture? PatchInspection { get; init; }

    [JsonPropertyName("physicalFormats")]
    public List<PhysicalFormatFixture> PhysicalFormats { get; init; } = [];
}

internal sealed class NandInspectionFixture
{
    [JsonPropertyName("input")]
    public string Input { get; init; } = string.Empty;

    [JsonPropertyName("cpuKey")]
    public string CpuKey { get; init; } = string.Empty;

    [JsonPropertyName("format")]
    public string Format { get; init; } = string.Empty;

    [JsonPropertyName("layout")]
    public string Layout { get; init; } = string.Empty;

    [JsonPropertyName("rawByteLength")]
    public long RawByteLength { get; init; }

    [JsonPropertyName("canonicalLogicalByteLength")]
    public long CanonicalLogicalByteLength { get; init; }

    [JsonPropertyName("cbBuild")]
    public int CbBuild { get; init; }

    [JsonPropertyName("smcVersion")]
    public string SmcVersion { get; init; } = string.Empty;

    [JsonPropertyName("cpuKeyVerification")]
    public string CpuKeyVerification { get; init; } = string.Empty;

    [JsonPropertyName("legacyPatches")]
    public List<string> LegacyPatches { get; init; } = [];
}

internal sealed class CanonicalComparisonFixture
{
    [JsonPropertyName("left")]
    public string Left { get; init; } = string.Empty;

    [JsonPropertyName("right")]
    public string Right { get; init; } = string.Empty;

    [JsonPropertyName("layout")]
    public string Layout { get; init; } = string.Empty;

    [JsonPropertyName("badPhysicalBlock")]
    public long BadPhysicalBlock { get; init; }

    [JsonPropertyName("replacementPhysicalBlock")]
    public long ReplacementPhysicalBlock { get; init; }
}

internal sealed class PatchInspectionFixture
{
    [JsonPropertyName("completeInput")]
    public string CompleteInput { get; init; } = string.Empty;

    [JsonPropertyName("malformedInput")]
    public string MalformedInput { get; init; } = string.Empty;

    [JsonPropertyName("recordCount")]
    public int RecordCount { get; init; }

    [JsonPropertyName("legacyPatches")]
    public List<string> LegacyPatches { get; init; } = [];

    [JsonPropertyName("malformedDiagnosticKind")]
    public string MalformedDiagnosticKind { get; init; } = string.Empty;
}

internal sealed class PhysicalFormatFixture
{
    [JsonPropertyName("input")]
    public string Input { get; init; } = string.Empty;

    [JsonPropertyName("layout")]
    public string Layout { get; init; } = string.Empty;
}
