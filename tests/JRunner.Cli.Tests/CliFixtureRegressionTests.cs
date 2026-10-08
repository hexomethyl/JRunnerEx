using System.Text.Json;
using JRunner.Cli;
using JRunner.Tests.Fixtures;
using Xunit;

namespace JRunner.Cli.Tests;

public sealed class CliFixtureRegressionTests
{
    [Fact]
    public async Task Documented_fixture_commands_produce_the_manifested_JSON_evidence()
    {
        FixtureScenarios scenarios = FixtureCatalog.Manifest.Scenarios
            ?? throw new InvalidOperationException("The fixture manifest did not declare scenarios.");
        NandInspectionFixture nandInspection = scenarios.NandInspection
            ?? throw new InvalidOperationException("The fixture manifest did not declare NAND inspection data.");
        CanonicalComparisonFixture comparison = scenarios.CanonicalComparison
            ?? throw new InvalidOperationException("The fixture manifest did not declare comparison data.");
        PatchInspectionFixture patchInspection = scenarios.PatchInspection
            ?? throw new InvalidOperationException("The fixture manifest did not declare patch inspection data.");
        string cpuKeyText = (await File.ReadAllTextAsync(FixtureCatalog.GetPath(nandInspection.CpuKey))).TrimEnd('\r', '\n');

        (JsonDocument inspectDocument, string inspectStandardError) = await RunJsonAsync(
            [
                "nand", "inspect", "--input", FixtureCatalog.GetPath(nandInspection.Input),
                "--cpu-key-file", FixtureCatalog.GetPath(nandInspection.CpuKey), "--json",
            ]);
        using (inspectDocument)
        {
            JsonElement result = inspectDocument.RootElement.GetProperty("result");
            Assert.True(inspectDocument.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(nandInspection.Format, result.GetProperty("canonicalImage").GetProperty("detectedFormat").GetString());
            Assert.Equal(nandInspection.Layout, result.GetProperty("canonicalImage").GetProperty("selectedLayout").GetString());
            Assert.Equal(nandInspection.RawByteLength, result.GetProperty("canonicalImage").GetProperty("rawByteLength").GetInt64());
            Assert.Equal(nandInspection.CanonicalLogicalByteLength, result.GetProperty("canonicalImage").GetProperty("canonicalLogicalByteLength").GetInt64());
            Assert.DoesNotContain(cpuKeyText, inspectDocument.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(cpuKeyText, inspectStandardError, StringComparison.OrdinalIgnoreCase);
        }

        (JsonDocument comparisonDocument, _) = await RunJsonAsync(
            ["nand", "compare", FixtureCatalog.GetPath(comparison.Left), FixtureCatalog.GetPath(comparison.Right), "--json"]);
        using (comparisonDocument)
        {
            JsonElement result = comparisonDocument.RootElement.GetProperty("result");
            Assert.True(comparisonDocument.RootElement.GetProperty("ok").GetBoolean());
            Assert.True(result.GetProperty("equal").GetBoolean());
            JsonElement remap = Assert.Single(result.GetProperty("right").GetProperty("remaps").EnumerateArray());
            Assert.Equal(comparison.BadPhysicalBlock, remap.GetProperty("badBlock").GetProperty("physicalBlock").GetInt64());
            Assert.Equal(comparison.ReplacementPhysicalBlock, remap.GetProperty("replacementPhysicalBlock").GetInt64());
        }

        (JsonDocument patchDocument, string patchStandardError) = await RunJsonAsync(
            ["patch", "inspect", "--input", FixtureCatalog.GetPath(patchInspection.CompleteInput), "--json"]);
        using (patchDocument)
        {
            JsonElement result = patchDocument.RootElement.GetProperty("result");
            Assert.True(patchDocument.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("complete", result.GetProperty("completionStatus").GetString());
            Assert.Equal(
                patchInspection.LegacyPatches,
                result.GetProperty("recognizedLegacyPatches").EnumerateArray()
                    .Select(patch => patch.GetProperty("patchName").GetString()));
            Assert.Equal(string.Empty, patchStandardError);
        }

        (JsonDocument malformedPatchDocument, string malformedPatchStandardError) = await RunJsonAsync(
            ["patch", "inspect", "--input", FixtureCatalog.GetPath(patchInspection.MalformedInput), "--json"]);
        using (malformedPatchDocument)
        {
            JsonElement result = malformedPatchDocument.RootElement.GetProperty("result");
            Assert.True(malformedPatchDocument.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("malformed", result.GetProperty("completionStatus").GetString());
            Assert.Equal(
                patchInspection.MalformedDiagnosticKind,
                result.GetProperty("structuralDiagnostics")[0].GetProperty("kind").GetString());
            Assert.Equal(string.Empty, malformedPatchStandardError);
        }
    }

    private static async Task<(JsonDocument Document, string StandardError)> RunJsonAsync(
        IReadOnlyList<string> arguments)
    {
        using var standardOutput = new StringWriter();
        using var errorOutput = new StringWriter();
        int exitCode = await CliApplication.RunAsync(arguments, standardOutput, errorOutput);
        Assert.Equal(0, exitCode);
        return (JsonDocument.Parse(standardOutput.ToString()), errorOutput.ToString());
    }
}
