using System.Security.Cryptography;
using JRunner.FixtureBuilder;
using JRunner.Tests.Fixtures;
using Xunit;

namespace JRunner.Core.Tests.Fixtures;

public sealed class FixtureManifestTests
{
    [Fact]
    public void Manifest_covers_each_fixture_with_its_exact_size_and_SHA256()
    {
        FixtureManifest manifest = FixtureCatalog.Manifest;
        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Equal("jrunner-native-core", manifest.FixtureSet);

        var declaredPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (FixtureFile fixture in manifest.Files)
        {
            Assert.True(declaredPaths.Add(fixture.Path), $"Duplicate fixture path '{fixture.Path}'.");
            string path = FixtureCatalog.GetPath(fixture.Path);
            Assert.True(File.Exists(path), $"Missing fixture '{fixture.Path}'.");
            Assert.Equal(fixture.ByteLength, new FileInfo(path).Length);

            using FileStream stream = File.OpenRead(path);
            string digest = Convert.ToHexString(SHA256.HashData(stream));
            Assert.Equal(fixture.Sha256, digest);
        }

        string[] actualPaths = Directory.EnumerateFiles(FixtureCatalog.RootDirectory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(FixtureCatalog.RootDirectory, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(path => !string.Equals(path, "manifest.v1.json", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(declaredPaths.OrderBy(path => path, StringComparer.Ordinal), actualPaths);
    }

    [Fact]
    public void Manifest_scenarios_only_reference_declared_fixture_files()
    {
        FixtureScenarios scenarios = FixtureCatalog.Manifest.Scenarios
            ?? throw new InvalidOperationException("The fixture manifest did not declare scenarios.");
        NandInspectionFixture nandInspection = scenarios.NandInspection
            ?? throw new InvalidOperationException("The fixture manifest did not declare NAND inspection data.");
        CanonicalComparisonFixture comparison = scenarios.CanonicalComparison
            ?? throw new InvalidOperationException("The fixture manifest did not declare comparison data.");
        PatchInspectionFixture patchInspection = scenarios.PatchInspection
            ?? throw new InvalidOperationException("The fixture manifest did not declare patch inspection data.");

        string[] paths =
        [
            nandInspection.Input,
            nandInspection.CpuKey,
            comparison.Left,
            comparison.Right,
            patchInspection.CompleteInput,
            patchInspection.MalformedInput,
            .. scenarios.PhysicalFormats.Select(format => format.Input),
        ];
        Assert.All(paths, path => Assert.True(File.Exists(FixtureCatalog.GetPath(path))));
    }

    [Fact]
    public void Fixture_generator_reproduces_the_checked_fixture_tree_byte_for_byte()
    {
        using var generatedDirectory = new TemporaryDirectory();
        string generatedRoot = Path.Combine(generatedDirectory.Path, "generated");

        FixtureGenerator.Generate(generatedRoot);

        string[] checkedPaths = EnumerateRelativeFiles(FixtureCatalog.RootDirectory);
        string[] generatedPaths = EnumerateRelativeFiles(generatedRoot);
        Assert.Equal(checkedPaths, generatedPaths);
        foreach (string relativePath in checkedPaths)
        {
            Assert.Equal(
                GetSha256(Path.Combine(FixtureCatalog.RootDirectory, relativePath)),
                GetSha256(Path.Combine(generatedRoot, relativePath)));
        }
    }

    [Fact]
    public void Fixture_generator_rejects_existing_output_trees_without_mutation()
    {
        using var temporaryDirectory = new TemporaryDirectory();

        string emptyRoot = Path.Combine(temporaryDirectory.Path, "empty");
        Directory.CreateDirectory(emptyRoot);
        AssertGenerationIsRejectedWithoutMutation(emptyRoot);

        string unownedRoot = Path.Combine(temporaryDirectory.Path, "unowned");
        Directory.CreateDirectory(unownedRoot);
        File.WriteAllText(Path.Combine(unownedRoot, "stale.txt"), "unowned output");
        AssertGenerationIsRejectedWithoutMutation(unownedRoot);

        string imitationRoot = Path.Combine(temporaryDirectory.Path, "imitation");
        Directory.CreateDirectory(imitationRoot);
        File.WriteAllText(Path.Combine(imitationRoot, "user.txt"), "user data");
        File.WriteAllText(
            Path.Combine(imitationRoot, "manifest.v1.json"),
            """
            {
              "schemaVersion": 1,
              "fixtureSet": "jrunner-native-core",
              "files": [
                { "path": "user.txt" }
              ]
            }
            """);
        AssertGenerationIsRejectedWithoutMutation(imitationRoot);

        string generatedRoot = Path.Combine(temporaryDirectory.Path, "generated");
        FixtureGenerator.Generate(generatedRoot);
        File.WriteAllText(Path.Combine(generatedRoot, "stale.txt"), "undeclared output");
        AssertGenerationIsRejectedWithoutMutation(generatedRoot);
    }

    [Fact]
    public void Fixture_generator_rejects_a_missing_output_root_below_a_symbolic_link()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        string externalDirectory = Path.Combine(temporaryDirectory.Path, "external");
        Directory.CreateDirectory(externalDirectory);
        string linkedParent = Path.Combine(temporaryDirectory.Path, "linked-parent");
        Directory.CreateSymbolicLink(linkedParent, externalDirectory);
        string outputRoot = Path.Combine(linkedParent, "output");

        Assert.Throws<InvalidOperationException>(() => FixtureGenerator.Generate(outputRoot));
        Assert.NotNull(new DirectoryInfo(linkedParent).LinkTarget);
        Assert.Empty(Directory.EnumerateFileSystemEntries(externalDirectory));
    }

    [Fact]
    public void Fixture_catalog_rejects_case_colliding_manifest_paths()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string fixtureRoot = Path.Combine(temporaryDirectory.Path, "case-collision");
        Directory.CreateDirectory(fixtureRoot);
        string digest = Convert.ToHexString(SHA256.HashData([0x5A]));
        File.WriteAllText(
            Path.Combine(fixtureRoot, "manifest.v1.json"),
            $$"""
            {
              "schemaVersion": 1,
              "fixtureSet": "jrunner-native-core",
              "files": [
                { "path": "keys/a.bin", "byteLength": 1, "sha256": "{{digest}}" },
                { "path": "KEYS/a.bin", "byteLength": 1, "sha256": "{{digest}}" }
              ],
              "scenarios": {}
            }
            """);

        Assert.Throws<InvalidOperationException>(() => FixtureCatalog.LoadManifestFromDirectory(fixtureRoot));
    }

    private static void AssertGenerationIsRejectedWithoutMutation(string root)
    {
        IReadOnlyDictionary<string, string> before = CaptureTree(root);

        Assert.Throws<InvalidOperationException>(() => FixtureGenerator.Generate(root));

        AssertTreeEqual(before, CaptureTree(root));
    }

    private static IReadOnlyDictionary<string, string> CaptureTree(string root)
    {
        var entries = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            entries.Add(relativePath, File.Exists(path) ? $"file:{GetSha256(path)}" : "directory");
        }

        return entries;
    }

    private static void AssertTreeEqual(
        IReadOnlyDictionary<string, string> expected,
        IReadOnlyDictionary<string, string> actual)
    {
        Assert.Equal(expected.Keys, actual.Keys);
        foreach ((string path, string expectedValue) in expected)
        {
            Assert.Equal(expectedValue, actual[path]);
        }
    }

    private static string[] EnumerateRelativeFiles(string root) => Directory.EnumerateFiles(
            root,
            "*",
            SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(root, path))
        .OrderBy(path => path, StringComparer.Ordinal)
        .ToArray();

    private static string GetSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"jrunner-fixture-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
