using JRunner.Core.Patching;
using JRunner.Core.Patching.Inspection;
using JRunner.Tests.Fixtures;
using Xunit;

namespace JRunner.Core.Tests.Patching;

public sealed class FixtureRegressionTests
{
    [Fact]
    public async Task Inspects_the_checked_patch_fixture_with_all_manifested_legacy_evidence()
    {
        PatchInspectionFixture fixture = GetFixture();
        byte[] source = await File.ReadAllBytesAsync(FixtureCatalog.GetPath(fixture.CompleteInput));

        PatchInspectionResult result = PatchInspectionService.Inspect(source);

        Assert.Equal(PatchInspectionCompletionStatus.Complete, result.CompletionStatus);
        Assert.True(result.IsComplete);
        Assert.False(result.IsMalformed);
        Assert.Equal(fixture.RecordCount, result.Records.Length);
        Assert.Equal(fixture.LegacyPatches, result.RecognizedLegacyPatches.Select(patch => patch.PatchName));
    }

    [Fact]
    public async Task Classifies_the_checked_truncated_patch_fixture_without_discarding_structural_evidence()
    {
        PatchInspectionFixture fixture = GetFixture();
        byte[] source = await File.ReadAllBytesAsync(FixtureCatalog.GetPath(fixture.MalformedInput));

        PatchInspectionResult result = PatchInspectionService.Inspect(source);

        Assert.Equal("truncated-payload", fixture.MalformedDiagnosticKind);
        Assert.Equal(PatchInspectionCompletionStatus.Malformed, result.CompletionStatus);
        Assert.True(result.IsMalformed);
        Assert.Contains(
            result.StructuralDiagnostics,
            diagnostic => diagnostic.Kind == PatchSectionDiagnosticKind.TruncatedPayload);
    }

    private static PatchInspectionFixture GetFixture() => FixtureCatalog.Manifest.Scenarios?.PatchInspection
        ?? throw new InvalidOperationException("The fixture manifest did not declare patch inspection data.");
}
