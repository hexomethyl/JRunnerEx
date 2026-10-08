using JRunner.Core.Nand.Hacks;
using Xunit;

namespace JRunner.Core.Tests.Nand.Hacks;

public sealed class NandHackEvidenceServiceTests
{
    [Fact]
    public void Identify_preserves_every_legacy_CB_table_mapping()
    {
        foreach (var (build, expectedHack) in ExpectedLegacyMappings)
        {
            var evidence = NandHackEvidenceService.Identify(build, cbBBuild: null, cbXBuild: null);

            Assert.Equal((int?)build, evidence.CbABuild);
            Assert.Null(evidence.CbBBuild);
            Assert.Null(evidence.CbXBuild);
            Assert.True(evidence.HasTableMatch);
            Assert.Equal(expectedHack, evidence.TablePreferredHack);
            Assert.Equal(expectedHack, evidence.PreferredHack);
            Assert.False(evidence.HasCbXEvidence);
        }
    }

    [Fact]
    public void Adding_Glitch2m_preserves_existing_hack_type_numeric_values()
    {
        Assert.Equal(0, (int)NandHackType.Unknown);
        Assert.Equal(1, (int)NandHackType.Jtag);
        Assert.Equal(2, (int)NandHackType.Glitch);
        Assert.Equal(3, (int)NandHackType.Glitch2);
        Assert.Equal(4, (int)NandHackType.DevGl);
        Assert.Equal(5, (int)NandHackType.Rgh3);
        Assert.Equal(6, (int)NandHackType.Rgh13);
        Assert.Equal(7, (int)NandHackType.Glitch2m);
    }

    [Fact]
    public void Identify_reports_unknown_when_CB_A_has_no_table_match()
    {
        var evidence = NandHackEvidenceService.Identify(12345, cbBBuild: 9188, cbXBuild: null);

        Assert.False(evidence.HasTableMatch);
        Assert.Equal(NandHackType.Unknown, evidence.TablePreferredHack);
        Assert.Equal(NandHackType.Unknown, evidence.PreferredHack);
        Assert.Equal((int?)9188, evidence.CbBBuild);
    }

    [Fact]
    public void Identify_uses_positive_CB_X_for_RGH3_and_RGH13_evidence()
    {
        var rgh3 = NandHackEvidenceService.Identify(9188, cbBBuild: null, cbXBuild: 15432);
        var rgh13 = NandHackEvidenceService.Identify(9188, cbBBuild: null, cbXBuild: 42069);

        Assert.Equal(NandHackType.Glitch2, rgh3.TablePreferredHack);
        Assert.Equal(NandHackType.Rgh3, rgh3.PreferredHack);
        Assert.True(rgh3.HasCbXEvidence);
        Assert.True(rgh3.HasRgh3Evidence);
        Assert.False(rgh3.HasRgh13Evidence);

        Assert.Equal(NandHackType.Glitch2, rgh13.TablePreferredHack);
        Assert.Equal(NandHackType.Rgh13, rgh13.PreferredHack);
        Assert.True(rgh13.HasCbXEvidence);
        Assert.False(rgh13.HasRgh3Evidence);
        Assert.True(rgh13.HasRgh13Evidence);
    }

    [Fact]
    public void Identify_does_not_infer_RGH3_from_legacy_zero_CB_X_sentinel()
    {
        var evidence = NandHackEvidenceService.Identify(9188, cbBBuild: null, cbXBuild: 0);

        Assert.Equal((int?)0, evidence.CbXBuild);
        Assert.False(evidence.HasCbXEvidence);
        Assert.False(evidence.HasRgh3Evidence);
        Assert.False(evidence.HasRgh13Evidence);
        Assert.Equal(NandHackType.Glitch2, evidence.PreferredHack);
    }

    [Fact]
    public void Identify_flags_Winbond_only_for_the_exact_legacy_CB_pair()
    {
        var winbond = NandHackEvidenceService.Identify(13121, 13182, cbXBuild: null);
        var wrongCbB = NandHackEvidenceService.Identify(13121, 13181, cbXBuild: null);
        var wrongCbA = NandHackEvidenceService.Identify(13120, 13182, cbXBuild: null);

        Assert.True(winbond.HasWinbondEvidence);
        Assert.Equal(NandHackType.Glitch2, winbond.PreferredHack);
        Assert.False(wrongCbB.HasWinbondEvidence);
        Assert.False(wrongCbA.HasWinbondEvidence);
    }

    [Fact]
    public void Identify_flags_the_inclusive_legacy_Elpis_range_independently_from_hack_type()
    {
        var mappedElpis = NandHackEvidenceService.Identify(7373, cbBBuild: null, cbXBuild: null);
        var unlistedCbA = NandHackEvidenceService.Identify(7374, cbBBuild: null, cbXBuild: null);
        var unlistedCbB = NandHackEvidenceService.Identify(cbABuild: null, cbBBuild: 7376, cbXBuild: null);
        var outsideRange = NandHackEvidenceService.Identify(7372, 7379, cbXBuild: null);

        Assert.True(mappedElpis.HasElpisEvidence);
        Assert.Equal(NandHackType.Glitch2, mappedElpis.PreferredHack);

        Assert.True(unlistedCbA.HasElpisEvidence);
        Assert.False(unlistedCbA.HasTableMatch);
        Assert.Equal(NandHackType.Unknown, unlistedCbA.PreferredHack);

        Assert.True(unlistedCbB.HasElpisEvidence);
        Assert.False(unlistedCbB.HasTableMatch);
        Assert.Equal(NandHackType.Unknown, unlistedCbB.PreferredHack);

        Assert.False(outsideRange.HasElpisEvidence);
    }

    [Fact]
    public void Identify_does_not_use_CB_B_as_a_preferred_hack_lookup()
    {
        var evidence = NandHackEvidenceService.Identify(cbABuild: null, cbBBuild: 9188, cbXBuild: null);

        Assert.False(evidence.HasTableMatch);
        Assert.Equal(NandHackType.Unknown, evidence.TablePreferredHack);
        Assert.Equal(NandHackType.Unknown, evidence.PreferredHack);
    }

    [Fact]
    public void Identify_rejects_negative_build_values()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NandHackEvidenceService.Identify(-1, cbBBuild: null, cbXBuild: null));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NandHackEvidenceService.Identify(cbABuild: null, cbBBuild: -1, cbXBuild: null));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NandHackEvidenceService.Identify(cbABuild: null, cbBBuild: null, cbXBuild: -1));
    }

    [Fact]
    public void Evidence_is_immutable_and_exposes_no_raw_bootloader_bytes()
    {
        var publicProperties = typeof(NandHackEvidence).GetProperties();

        Assert.All(publicProperties, static property => Assert.False(property.CanWrite));
        Assert.DoesNotContain(publicProperties, static property => property.PropertyType == typeof(byte[]));
        Assert.DoesNotContain(publicProperties, static property => property.PropertyType == typeof(ReadOnlyMemory<byte>));
    }

    private static IReadOnlyList<(int Build, NandHackType PreferredHack)> ExpectedLegacyMappings { get; } =
    [
        (1888, NandHackType.Jtag),
        (1897, NandHackType.Jtag),
        (1902, NandHackType.Jtag),
        (1903, NandHackType.Jtag),
        (1920, NandHackType.Jtag),
        (1921, NandHackType.Jtag),
        (8192, NandHackType.Jtag),
        (4540, NandHackType.Jtag),
        (4558, NandHackType.Jtag),
        (4570, NandHackType.Jtag),
        (4580, NandHackType.Jtag),
        (5760, NandHackType.Jtag),
        (5761, NandHackType.Jtag),
        (5766, NandHackType.Jtag),
        (5770, NandHackType.Jtag),
        (6712, NandHackType.Jtag),
        (6723, NandHackType.Jtag),
        (1922, NandHackType.Glitch2),
        (1940, NandHackType.Glitch2),
        (1923, NandHackType.Glitch2),
        (7373, NandHackType.Glitch2),
        (7375, NandHackType.Glitch2),
        (4571, NandHackType.Glitch),
        (4579, NandHackType.Glitch),
        (4572, NandHackType.Glitch),
        (4578, NandHackType.Glitch),
        (5771, NandHackType.Glitch),
        (6750, NandHackType.Glitch),
        (6751, NandHackType.Glitch),
        (9188, NandHackType.Glitch2),
        (10918, NandHackType.Glitch2),
        (13121, NandHackType.Glitch2),
        (1925, NandHackType.Glitch2),
        (1941, NandHackType.Glitch2),
        (1926, NandHackType.Glitch2),
        (7377, NandHackType.Glitch2),
        (4577, NandHackType.Glitch2),
        (4559, NandHackType.Glitch2),
        (4576, NandHackType.Glitch2),
        (4560, NandHackType.Glitch2),
        (4575, NandHackType.Glitch2),
        (5772, NandHackType.Glitch2),
        (5773, NandHackType.Glitch2),
        (6752, NandHackType.Glitch2),
        (6753, NandHackType.Glitch2),
        (9230, NandHackType.Glitch2),
        (13180, NandHackType.Glitch2),
        (1927, NandHackType.Glitch2),
        (1942, NandHackType.Glitch2),
        (1928, NandHackType.Glitch2),
        (7378, NandHackType.Glitch2),
        (4561, NandHackType.Glitch2),
        (4574, NandHackType.Glitch2),
        (4562, NandHackType.Glitch2),
        (4569, NandHackType.Glitch2),
        (5774, NandHackType.Glitch2),
        (6754, NandHackType.Glitch2),
        (9231, NandHackType.Glitch2),
        (13181, NandHackType.Glitch2),
        (13182, NandHackType.Glitch2),
        (16128, NandHackType.Glitch2),
        (10375, NandHackType.DevGl),
        (14352, NandHackType.DevGl),
        (10375, NandHackType.DevGl),
        (14352, NandHackType.DevGl),
        (10375, NandHackType.DevGl),
        (14352, NandHackType.DevGl),
        (10375, NandHackType.DevGl),
        (14352, NandHackType.DevGl),
        (10375, NandHackType.DevGl),
        (14352, NandHackType.DevGl),
        (14352, NandHackType.DevGl),
        (14352, NandHackType.DevGl),
    ];
}
