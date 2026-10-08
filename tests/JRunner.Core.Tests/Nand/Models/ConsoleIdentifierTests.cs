using System.Linq;
using JRunner.Core.Nand.Models;
using JRunner.Core.Nand.Physical;
using Xunit;

namespace JRunner.Core.Tests.Nand.Models;

public sealed class ConsoleIdentifierTests
{
    [Fact]
    public void Identify_combines_known_CB_SMC_flash_length_and_layout_evidence()
    {
        var result = ConsoleIdentifier.Identify(new ConsoleIdentificationEvidence(
            cbBuild: 9188,
            smcType: 5,
            rawNandLength: 17_301_504,
            layout: NandLegacyLayout.Layout1,
            flashConfiguration: 0x00023010u,
            hasSpareData: true));

        Assert.Equal(17, result.Candidates.Length);
        Assert.Equal(ConsoleId.Trinity16Mb, result.Candidates[0].Console.Id);
        Assert.Equal(8, result.Candidates[0].Score);
        Assert.Equal(8, result.HighestScore);
        Assert.False(result.IsAmbiguous);
        Assert.True(result.HasPositiveEvidence);
        Assert.Equal(new[] { ConsoleId.Trinity16Mb }, result.HighestScoringCandidates.Select(candidate => candidate.Console.Id));

        AssertContribution(result, ConsoleEvidenceKind.CbBuild, 3, ConsoleId.Trinity16Mb, ConsoleId.TrinityBigBlock);
        AssertContribution(result, ConsoleEvidenceKind.SmcType, 2, ConsoleId.Trinity16Mb, ConsoleId.TrinityBigBlock);
        AssertContribution(result, ConsoleEvidenceKind.FlashConfiguration, 1, ConsoleId.Trinity16Mb, ConsoleId.Jasper16Mb);
        AssertContribution(
            result,
            ConsoleEvidenceKind.RawNandLength,
            1,
            ConsoleId.Trinity16Mb,
            ConsoleId.Falcon16Mb,
            ConsoleId.Zephyr16Mb,
            ConsoleId.Jasper16Mb,
            ConsoleId.JasperXsb,
            ConsoleId.Xenon16Mb,
            ConsoleId.Corona16Mb,
            ConsoleId.Winchester16Mb);
        AssertContribution(result, ConsoleEvidenceKind.SpareData, 0);
        AssertContribution(result, ConsoleEvidenceKind.Layout, 1, ConsoleId.Trinity16Mb, ConsoleId.Jasper16Mb, ConsoleId.Corona16Mb, ConsoleId.Winchester16Mb);
    }

    [Fact]
    public void Identify_preserves_every_CB_rule_including_the_legacy_CB_B_special_case()
    {
        var cases = new[]
        {
            new CbRuleCase(9188, null, null, new[] { ConsoleId.Trinity16Mb, ConsoleId.TrinityBigBlock }),
            new CbRuleCase(9250, null, null, new[] { ConsoleId.Trinity16Mb, ConsoleId.TrinityBigBlock }),
            new CbRuleCase(16000, null, true, new[] { ConsoleId.Winchester16Mb, ConsoleId.WinchesterBigBlock }),
            new CbRuleCase(16000, null, false, new[] { ConsoleId.Winchester4Gb }),
            new CbRuleCase(13121, null, true, new[] { ConsoleId.CoronaBigBlock, ConsoleId.Corona16Mb }),
            new CbRuleCase(13200, null, true, new[] { ConsoleId.CoronaBigBlock, ConsoleId.Corona16Mb }),
            new CbRuleCase(13121, null, false, new[] { ConsoleId.Corona4Gb }),
            new CbRuleCase(6712, null, null, new[] { ConsoleId.Jasper16Mb, ConsoleId.JasperXsb, ConsoleId.JasperBigBlock }),
            new CbRuleCase(6780, null, null, new[] { ConsoleId.Jasper16Mb, ConsoleId.JasperXsb, ConsoleId.JasperBigBlock }),
            new CbRuleCase(4558, null, null, new[] { ConsoleId.Zephyr16Mb, ConsoleId.Zephyr64Mb }),
            new CbRuleCase(4590, null, null, new[] { ConsoleId.Zephyr16Mb, ConsoleId.Zephyr64Mb }),
            new CbRuleCase(1888, null, null, new[] { ConsoleId.Xenon64Mb, ConsoleId.Xenon16Mb }),
            new CbRuleCase(1960, null, null, new[] { ConsoleId.Xenon64Mb, ConsoleId.Xenon16Mb }),
            new CbRuleCase(7373, null, null, new[] { ConsoleId.Xenon64Mb, ConsoleId.Xenon16Mb }),
            new CbRuleCase(7378, null, null, new[] { ConsoleId.Xenon64Mb, ConsoleId.Xenon16Mb }),
            new CbRuleCase(8192, null, null, new[] { ConsoleId.Xenon64Mb, ConsoleId.Xenon16Mb }),
            new CbRuleCase(5761, null, null, new[] { ConsoleId.Falcon16Mb, ConsoleId.Falcon64Mb }),
            new CbRuleCase(5780, null, null, new[] { ConsoleId.Falcon16Mb, ConsoleId.Falcon64Mb }),
            new CbRuleCase(5761, 7373, null, new[] { ConsoleId.Xenon64Mb, ConsoleId.Xenon16Mb }),
        };

        foreach (var testCase in cases)
        {
            var result = ConsoleIdentifier.Identify(new ConsoleIdentificationEvidence(
                cbBuild: testCase.CbBuild,
                cbBBuild: testCase.CbBBuild,
                hasSpareData: testCase.HasSpareData));

            AssertContribution(result, ConsoleEvidenceKind.CbBuild, 3, testCase.ExpectedConsoleIds);
        }
    }

    [Fact]
    public void Identify_preserves_every_SMC_type_rule()
    {
        var cases = new[]
        {
            new ScoredRuleCase(1, 5, new[] { ConsoleId.Xenon64Mb, ConsoleId.Xenon16Mb }),
            new ScoredRuleCase(2, 2, new[] { ConsoleId.Zephyr16Mb, ConsoleId.Zephyr64Mb }),
            new ScoredRuleCase(3, 2, new[] { ConsoleId.Falcon16Mb, ConsoleId.Falcon64Mb }),
            new ScoredRuleCase(4, 2, new[] { ConsoleId.Jasper16Mb, ConsoleId.JasperXsb, ConsoleId.JasperBigBlock }),
            new ScoredRuleCase(5, 2, new[] { ConsoleId.Trinity16Mb, ConsoleId.TrinityBigBlock }),
            new ScoredRuleCase(6, 2, new[] { ConsoleId.CoronaBigBlock, ConsoleId.Corona16Mb, ConsoleId.Corona4Gb }),
            new ScoredRuleCase(7, 2, new[] { ConsoleId.Winchester16Mb, ConsoleId.Winchester4Gb, ConsoleId.WinchesterBigBlock }),
        };

        foreach (var testCase in cases)
        {
            var result = ConsoleIdentifier.Identify(new ConsoleIdentificationEvidence(smcType: testCase.Value));

            AssertContribution(result, ConsoleEvidenceKind.SmcType, testCase.Points, testCase.ExpectedConsoleIds);
            Assert.Equal(testCase.Points, result.HighestScore);
            Assert.Equal(testCase.ExpectedConsoleIds, result.HighestScoringCandidates.Select(candidate => candidate.Console.Id));
        }
    }

    [Fact]
    public void Identify_preserves_every_flash_configuration_rule()
    {
        var cases = new[]
        {
            new FlashConfigurationCase(0x008A3020u, new[] { ConsoleId.JasperBigBlock, ConsoleId.TrinityBigBlock }),
            new FlashConfigurationCase(0x00AA3020u, new[] { ConsoleId.JasperBigBlock, ConsoleId.TrinityBigBlock }),
            new FlashConfigurationCase(0x008C3020u, new[] { ConsoleId.CoronaBigBlock, ConsoleId.WinchesterBigBlock }),
            new FlashConfigurationCase(0x00AC3020u, new[] { ConsoleId.CoronaBigBlock, ConsoleId.WinchesterBigBlock }),
            new FlashConfigurationCase(0xC0462002u, new[] { ConsoleId.Corona4Gb, ConsoleId.Winchester4Gb }),
            new FlashConfigurationCase(0x01198010u, new[] { ConsoleId.Falcon16Mb, ConsoleId.Zephyr16Mb, ConsoleId.JasperXsb, ConsoleId.Xenon16Mb }),
            new FlashConfigurationCase(0x01198030u, new[] { ConsoleId.Xenon64Mb, ConsoleId.Zephyr64Mb, ConsoleId.Falcon64Mb }),
            new FlashConfigurationCase(0x00023010u, new[] { ConsoleId.Trinity16Mb, ConsoleId.Jasper16Mb }),
            new FlashConfigurationCase(0x00043000u, new[] { ConsoleId.Corona16Mb, ConsoleId.Winchester16Mb }),
        };

        foreach (var testCase in cases)
        {
            var result = ConsoleIdentifier.Identify(new ConsoleIdentificationEvidence(flashConfiguration: testCase.FlashConfiguration));

            AssertContribution(result, ConsoleEvidenceKind.FlashConfiguration, 1, testCase.ExpectedConsoleIds);
            Assert.Equal(testCase.ExpectedConsoleIds, result.HighestScoringCandidates.Select(candidate => candidate.Console.Id));
        }
    }

    [Fact]
    public void Identify_preserves_raw_length_and_layout_rules()
    {
        var small = ConsoleIdentifier.Identify(new ConsoleIdentificationEvidence(rawNandLength: 17_301_504));
        AssertContribution(
            small,
            ConsoleEvidenceKind.RawNandLength,
            1,
            ConsoleId.Trinity16Mb,
            ConsoleId.Falcon16Mb,
            ConsoleId.Zephyr16Mb,
            ConsoleId.Jasper16Mb,
            ConsoleId.JasperXsb,
            ConsoleId.Xenon16Mb,
            ConsoleId.Corona16Mb,
            ConsoleId.Winchester16Mb);

        foreach (var largeRawLength in new long[] { 69_206_016, 276_824_064, 553_648_128 })
        {
            var large = ConsoleIdentifier.Identify(new ConsoleIdentificationEvidence(rawNandLength: largeRawLength));
            AssertContribution(
                large,
                ConsoleEvidenceKind.RawNandLength,
                2,
                ConsoleId.JasperBigBlock,
                ConsoleId.Xenon64Mb,
                ConsoleId.CoronaBigBlock,
                ConsoleId.TrinityBigBlock,
                ConsoleId.Zephyr64Mb,
                ConsoleId.Falcon64Mb,
                ConsoleId.WinchesterBigBlock);
        }

        var otherLength = ConsoleIdentifier.Identify(new ConsoleIdentificationEvidence(rawNandLength: 1));
        AssertContribution(otherLength, ConsoleEvidenceKind.RawNandLength, 1, ConsoleId.Corona4Gb, ConsoleId.Winchester4Gb);

        AssertLayout(NandLegacyLayout.Layout0, ConsoleId.Falcon16Mb, ConsoleId.Zephyr16Mb, ConsoleId.JasperXsb, ConsoleId.Xenon64Mb, ConsoleId.Xenon16Mb, ConsoleId.Zephyr64Mb, ConsoleId.Falcon64Mb);
        AssertLayout(NandLegacyLayout.Layout1, ConsoleId.Trinity16Mb, ConsoleId.Jasper16Mb, ConsoleId.Corona16Mb, ConsoleId.Winchester16Mb);
        AssertLayout(NandLegacyLayout.Layout2, ConsoleId.JasperBigBlock, ConsoleId.CoronaBigBlock, ConsoleId.TrinityBigBlock, ConsoleId.WinchesterBigBlock);
    }

    [Fact]
    public void Identify_returns_all_tied_highest_candidates_instead_of_a_silent_guess()
    {
        var result = ConsoleIdentifier.Identify(new ConsoleIdentificationEvidence(cbBuild: 9188));

        Assert.Equal(17, result.Candidates.Length);
        Assert.Equal(3, result.HighestScore);
        Assert.True(result.IsAmbiguous);
        Assert.Equal(
            new[] { ConsoleId.Trinity16Mb, ConsoleId.TrinityBigBlock },
            result.HighestScoringCandidates.Select(candidate => candidate.Console.Id));
        Assert.Equal(ConsoleId.Trinity16Mb, result.Candidates[0].Console.Id);
        Assert.Equal(ConsoleId.TrinityBigBlock, result.Candidates[1].Console.Id);
    }

    [Fact]
    public void Identify_reports_unknown_evidence_without_selecting_a_console()
    {
        var result = ConsoleIdentifier.Identify(new ConsoleIdentificationEvidence(
            cbBuild: 8000,
            smcType: 8,
            flashConfiguration: uint.MaxValue));

        Assert.False(result.HasPositiveEvidence);
        Assert.True(result.IsAmbiguous);
        Assert.Equal(0, result.HighestScore);
        Assert.Equal(ConsoleCatalog.All.Select(console => console.Id), result.HighestScoringCandidates.Select(candidate => candidate.Console.Id));
        Assert.All(result.Evidence, contribution => Assert.False(contribution.HasMatch));
        Assert.Equal(
            new[] { ConsoleEvidenceKind.CbBuild, ConsoleEvidenceKind.SmcType, ConsoleEvidenceKind.FlashConfiguration },
            result.Evidence.Select(contribution => contribution.Kind));
    }

    [Fact]
    public void Identify_returns_all_candidates_when_no_evidence_is_available()
    {
        var result = ConsoleIdentifier.Identify(new ConsoleIdentificationEvidence());

        Assert.Empty(result.Evidence);
        Assert.False(result.HasPositiveEvidence);
        Assert.True(result.IsAmbiguous);
        Assert.Equal(ConsoleCatalog.All.Select(console => console.Id), result.HighestScoringCandidates.Select(candidate => candidate.Console.Id));
    }

    [Fact]
    public void Evidence_requires_the_spare_data_state_for_CB_rules_that_distinguish_eMMC()
    {
        var result = ConsoleIdentifier.Identify(new ConsoleIdentificationEvidence(cbBuild: 13121));

        Assert.False(result.HasPositiveEvidence);
        AssertContribution(result, ConsoleEvidenceKind.CbBuild, 0);
    }

    [Fact]
    public void Legacy_bootloader_factory_uses_CB_B_only_when_CB_X_is_present()
    {
        var withoutCbX = ConsoleIdentificationEvidence.FromLegacyBootloaders(
            cbABuild: 5761,
            cbBBuild: 7373,
            cbXBuild: 0);
        var withCbX = ConsoleIdentificationEvidence.FromLegacyBootloaders(
            cbABuild: 5761,
            cbBBuild: 7373,
            cbXBuild: 1);

        Assert.Equal(5761, withoutCbX.CbBuild);
        Assert.Equal(7373, withCbX.CbBuild);
        AssertContribution(
            ConsoleIdentifier.Identify(withoutCbX),
            ConsoleEvidenceKind.CbBuild,
            3,
            ConsoleId.Xenon64Mb,
            ConsoleId.Xenon16Mb);
        AssertContribution(
            ConsoleIdentifier.Identify(withCbX),
            ConsoleEvidenceKind.CbBuild,
            3,
            ConsoleId.Xenon64Mb,
            ConsoleId.Xenon16Mb);
    }

    [Fact]
    public void Evidence_validates_bounds_and_conflicting_spare_layout_facts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConsoleIdentificationEvidence(cbBuild: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConsoleIdentificationEvidence(smcType: 16));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConsoleIdentificationEvidence(rawNandLength: -1));
        Assert.Throws<ArgumentException>(() => new ConsoleIdentificationEvidence(layout: NandLegacyLayout.Layout0, hasSpareData: false));
    }

    private static void AssertLayout(NandLegacyLayout layout, params ConsoleId[] expectedConsoleIds)
    {
        var result = ConsoleIdentifier.Identify(new ConsoleIdentificationEvidence(layout: layout));
        AssertContribution(result, ConsoleEvidenceKind.Layout, 1, expectedConsoleIds);
        Assert.Equal(expectedConsoleIds, result.HighestScoringCandidates.Select(candidate => candidate.Console.Id));
    }

    private static void AssertContribution(
        ConsoleIdentificationResult result,
        ConsoleEvidenceKind kind,
        int pointsPerAffectedConsole,
        params ConsoleId[] expectedConsoleIds)
    {
        var contribution = Assert.Single(result.Evidence.Where(candidate => candidate.Kind == kind));
        Assert.Equal(pointsPerAffectedConsole, contribution.PointsPerAffectedConsole);
        Assert.Equal(expectedConsoleIds, contribution.AffectedConsoleIds);
        Assert.Equal(expectedConsoleIds.Length > 0, contribution.HasMatch);
    }

    private sealed record CbRuleCase(int CbBuild, int? CbBBuild, bool? HasSpareData, ConsoleId[] ExpectedConsoleIds);

    private sealed record ScoredRuleCase(int Value, int Points, ConsoleId[] ExpectedConsoleIds);

    private sealed record FlashConfigurationCase(uint FlashConfiguration, ConsoleId[] ExpectedConsoleIds);
}
