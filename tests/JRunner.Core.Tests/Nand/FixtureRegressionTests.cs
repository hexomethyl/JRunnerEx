using JRunner.Core.Nand.Comparison;
using JRunner.Core.Nand.Inspection;
using JRunner.Core.Nand.Inspection.Models;
using JRunner.Core.Nand.Physical;
using JRunner.Core.Nand.Security;
using JRunner.Tests.Fixtures;
using Xunit;

namespace JRunner.Core.Tests.Nand;

public sealed class FixtureRegressionTests
{
    [Fact]
    public async Task Inspects_the_checked_small_block_fixture_with_manifested_safe_evidence()
    {
        NandInspectionFixture fixture = GetScenarios().NandInspection
            ?? throw new InvalidOperationException("The fixture manifest did not declare NAND inspection data.");
        string keyText = await File.ReadAllTextAsync(FixtureCatalog.GetPath(fixture.CpuKey));
        CpuKey cpuKey = CpuKey.Parse(keyText.TrimEnd('\r', '\n'));
        await using FileStream source = File.OpenRead(FixtureCatalog.GetPath(fixture.Input));

        NandInspectionResult result = await NandImageService.InspectAsync(source, cpuKey);

        Assert.Equal("interleaved-ecc", fixture.Format);
        Assert.Equal(NandPhysicalFormat.InterleavedEcc, result.CanonicalImage.DetectedFormat);
        Assert.Equal(ParseLayout(fixture.Layout), result.CanonicalImage.SelectedLayout);
        Assert.Equal(fixture.RawByteLength, result.CanonicalImage.RawByteLength);
        Assert.Equal(fixture.CanonicalLogicalByteLength, result.CanonicalImage.CanonicalLogicalByteLength);
        Assert.Equal(fixture.CbBuild, Assert.Single(result.BootloaderStages).Build);
        Assert.Equal(fixture.SmcVersion, result.Smc.Version?.DisplayVersion);
        Assert.Equal(fixture.CpuKeyVerification, result.Keyvault.Inspection.CpuKeyVerification.ToString().ToLowerInvariant());
        Assert.Equal(fixture.LegacyPatches, result.Patches.Select(patch => patch.PatchName));
    }

    [Fact]
    public async Task Canonically_compares_clean_and_remapped_fixture_images()
    {
        CanonicalComparisonFixture fixture = GetScenarios().CanonicalComparison
            ?? throw new InvalidOperationException("The fixture manifest did not declare comparison data.");
        await using FileStream left = File.OpenRead(FixtureCatalog.GetPath(fixture.Left));
        await using FileStream right = File.OpenRead(FixtureCatalog.GetPath(fixture.Right));

        NandCanonicalComparisonResult result = await NandCanonicalComparisonService.CompareAsync(
            new NandCanonicalComparisonRequest(new NandCanonicalInput(left), new NandCanonicalInput(right)));

        Assert.True(result.Equal);
        Assert.Equal(0, result.DifferingByteCount);
        Assert.Null(result.FirstDifferingLogicalOffset);
        Assert.Equal(ParseLayout(fixture.Layout), result.Left.SelectedLayout);
        Assert.Equal(ParseLayout(fixture.Layout), result.Right.SelectedLayout);
        NandBadBlock badBlock = Assert.Single(result.Right.BadBlocks);
        Assert.Equal(fixture.BadPhysicalBlock, badBlock.PhysicalBlock);
        NandBadBlockRemap remap = Assert.Single(result.Right.Remaps);
        Assert.Equal(fixture.BadPhysicalBlock, remap.BadBlock.PhysicalBlock);
        Assert.Equal(fixture.ReplacementPhysicalBlock, remap.ReplacementPhysicalBlock);
    }

    [Fact]
    public async Task Locks_layout_detection_for_each_checked_physical_fixture()
    {
        foreach (PhysicalFormatFixture fixture in GetScenarios().PhysicalFormats)
        {
            byte[] source = await File.ReadAllBytesAsync(FixtureCatalog.GetPath(fixture.Input));
            try
            {
                NandPhysicalFormatDetection detection = NandPhysicalFormatDetector.Detect(source);
                Assert.Equal(NandPhysicalFormat.InterleavedEcc, detection.Format);
                Assert.Equal(ParseLayout(fixture.Layout), detection.Layout?.LegacyLayout);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(source);
            }
        }
    }

    [Fact]
    public async Task Reports_a_difference_on_the_canonical_comparison_buffer_boundary()
    {
        var leftBytes = new byte[0x10200];
        var rightBytes = new byte[leftBytes.Length];
        rightBytes[0x10000] = 0xA5;
        await using var left = new MemoryStream(leftBytes, writable: false);
        await using var right = new MemoryStream(rightBytes, writable: false);

        NandCanonicalComparisonResult result = await NandCanonicalComparisonService.CompareAsync(
            new NandCanonicalComparisonRequest(
                new NandCanonicalInput(left, NandCanonicalInputFormat.Logical),
                new NandCanonicalInput(right, NandCanonicalInputFormat.Logical)));

        Assert.False(result.Equal);
        Assert.Equal(1, result.DifferingByteCount);
        Assert.Equal(0x10000, result.FirstDifferingLogicalOffset);
    }

    private static FixtureScenarios GetScenarios() => FixtureCatalog.Manifest.Scenarios
        ?? throw new InvalidOperationException("The fixture manifest did not declare scenarios.");

    private static NandLegacyLayout ParseLayout(string layout) => layout switch
    {
        "layout0" => NandLegacyLayout.Layout0,
        "layout1" => NandLegacyLayout.Layout1,
        "layout2" => NandLegacyLayout.Layout2,
        _ => throw new InvalidOperationException($"Unsupported fixture layout '{layout}'."),
    };
}
