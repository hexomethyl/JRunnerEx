using System.Collections.Immutable;
using JRunner.Core.Nand.Hacks;
using JRunner.Core.Nand.Inspection;
using JRunner.Core.Nand.Inspection.Models;
using JRunner.Core.Nand.Models;
using Xunit;

namespace JRunner.Core.Tests.Nand.Inspection.Service;

public sealed class NandSemanticEvidenceValidatorTests
{
    // Synthetic future-policy records are confined to this test assembly; they are not a production corpus.
    private const string ManifestDigest = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string OtherDigest = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private const string Glitch2CdId = "synthetic-glitch2-cd";
    private const string Glitch2CbId = "synthetic-glitch2-cb";
    private const string RetailCdId = "synthetic-retail-cd";

    [Theory]
    [InlineData(NandEvidenceResolution.Unavailable, NandSemanticEvidenceMismatchKind.ImageFamilyEvidenceUnavailable, false)]
    [InlineData(NandEvidenceResolution.Unavailable, NandSemanticEvidenceMismatchKind.ImageFamilyEvidenceUnavailable, true)]
    [InlineData(NandEvidenceResolution.Absent, NandSemanticEvidenceMismatchKind.ImageFamilyEvidenceUnavailable, false)]
    [InlineData(NandEvidenceResolution.Absent, NandSemanticEvidenceMismatchKind.ImageFamilyAbsent, true)]
    [InlineData(NandEvidenceResolution.Ambiguous, NandSemanticEvidenceMismatchKind.ImageFamilyEvidenceUnavailable, false)]
    [InlineData(NandEvidenceResolution.Ambiguous, NandSemanticEvidenceMismatchKind.ImageFamilyAmbiguous, true)]
    [InlineData(NandEvidenceResolution.Conflicting, NandSemanticEvidenceMismatchKind.ImageFamilyEvidenceUnavailable, false)]
    [InlineData(NandEvidenceResolution.Conflicting, NandSemanticEvidenceMismatchKind.ImageFamilyConflicting, true)]
    public void Distinguishes_unavailable_matching_from_absent_ambiguous_and_conflicting_observations(
        NandEvidenceResolution resolution,
        NandSemanticEvidenceMismatchKind expectedMismatch,
        bool hasManifest)
    {
        NandImageFamilyEvidence family = resolution == NandEvidenceResolution.Ambiguous
            ? new(resolution, null, [NandImageFamily.Glitch2, NandImageFamily.Glitch2m])
            : new(resolution, null, default);
        NandDirectOutputEvidenceManifest? manifest = hasManifest ? CreateManifest() : null;

        AssertMismatch(family, manifest, expectedMismatch);
    }

    [Fact]
    public void Unavailable_family_does_not_hide_independent_console_and_exact_dashboard_failures()
    {
        var evidence = new NandSemanticEvidence(
            new NandConsoleEvidence(NandEvidenceResolution.Conflicting, null, default),
            new NandKernelDashboardEvidence(NandEvidenceResolution.Absent, default, kernelBootloaderBuild: 17559),
            new NandVirtualFuseEvidence(NandVirtualFuseEvidenceStatus.Present),
            NandImageFamilyEvidence.Unavailable);
        var requirement = new NandSemanticEvidenceRequirement(ConsoleId.Trinity16Mb, 17559, NandImageFamily.Glitch2);

        NandSemanticEvidenceValidationResult result = NandSemanticEvidenceValidator.Validate(evidence, requirement);

        Assert.False(result.IsMatch);
        Assert.Equal(
            [
                NandSemanticEvidenceMismatchKind.ConsoleConflicting,
                NandSemanticEvidenceMismatchKind.DashboardAbsent,
                NandSemanticEvidenceMismatchKind.ImageFamilyEvidenceUnavailable,
            ],
            result.Mismatches.Select(mismatch => mismatch.Kind).ToArray());
    }

    [Fact]
    public void Claimed_confirmed_family_without_any_direct_evidence_is_untrusted()
    {
        AssertMismatch(
            Confirmed(NandImageFamily.Glitch2, default),
            CreateManifest(),
            NandSemanticEvidenceMismatchKind.ImageFamilyEvidenceUntrusted);
    }

    [Fact]
    public void Even_a_confirmed_legacy_hack_diagnostic_cannot_substitute_for_direct_family_evidence()
    {
        var compatibility = new NandHackTypeEvidence(
            NandEvidenceResolution.Confirmed,
            NandHackType.Rgh3,
            [NandHackType.Rgh3]);
        NandSemanticEvidence evidence = CreateEvidence(NandImageFamilyEvidence.Unavailable, compatibility);
        var requirement = new NandSemanticEvidenceRequirement(
            ConsoleId.Trinity16Mb,
            17559,
            NandImageFamily.Rgh3,
            NandRgh3OutputEvidence.ReviewedManifest);

        NandSemanticEvidenceValidationResult result = NandSemanticEvidenceValidator.Validate(evidence, requirement);

        Assert.False(result.IsMatch);
        Assert.Equal(NandSemanticEvidenceMismatchKind.ImageFamilyEvidenceUnavailable, Assert.Single(result.Mismatches).Kind);
    }

    [Fact]
    public void Complete_claimed_provenance_cannot_match_without_a_reviewed_manifest()
    {
        AssertMismatch(
            Confirmed(NandImageFamily.Glitch2, CompleteGlitch2Evidence()),
            manifest: null,
            NandSemanticEvidenceMismatchKind.ImageFamilyEvidenceUnavailable);
    }

    [Theory]
    [InlineData(NandDirectOutputEvidenceSource.None)]
    [InlineData(NandDirectOutputEvidenceSource.CompatibilityTable)]
    [InlineData(NandDirectOutputEvidenceSource.StageMetadata)]
    [InlineData(NandDirectOutputEvidenceSource.SmcMarker)]
    [InlineData(NandDirectOutputEvidenceSource.VirtualFuseMarker)]
    [InlineData(NandDirectOutputEvidenceSource.RequestMetadata)]
    public void Compatibility_generic_metadata_markers_and_requests_are_not_direct_family_proof(
        NandDirectOutputEvidenceSource source)
    {
        ImmutableArray<NandDirectOutputEvidenceProvenance> provenance =
        [
            new(source, ManifestDigest, Glitch2CdId, NandBootloaderStageKind.CD, NandBootloaderDecryptionStatus.Decrypted),
            Record(Glitch2CbId, NandBootloaderStageKind.CB_A),
        ];

        AssertMismatch(
            Confirmed(NandImageFamily.Glitch2, provenance),
            CreateManifest(),
            NandSemanticEvidenceMismatchKind.ImageFamilyEvidenceUntrusted);
    }

    [Theory]
    [InlineData(OtherDigest, Glitch2CdId, NandBootloaderStageKind.CD)]
    [InlineData(null, Glitch2CdId, NandBootloaderStageKind.CD)]
    [InlineData("", Glitch2CdId, NandBootloaderStageKind.CD)]
    [InlineData(ManifestDigest, "unknown-fingerprint", NandBootloaderStageKind.CD)]
    [InlineData(ManifestDigest, "SYNTHETIC-GLITCH2-CD", NandBootloaderStageKind.CD)]
    [InlineData(ManifestDigest, null, NandBootloaderStageKind.CD)]
    [InlineData(ManifestDigest, "", NandBootloaderStageKind.CD)]
    [InlineData(ManifestDigest, Glitch2CdId, NandBootloaderStageKind.CE)]
    [InlineData(ManifestDigest, Glitch2CdId, null)]
    public void Provenance_must_bind_the_manifest_digest_record_identifier_and_source_stage(
        string? digest,
        string? fingerprintId,
        NandBootloaderStageKind? stage)
    {
        ImmutableArray<NandDirectOutputEvidenceProvenance> provenance =
        [
            new(
                NandDirectOutputEvidenceSource.ReviewedDecryptedStageFingerprint,
                digest,
                fingerprintId,
                stage,
                NandBootloaderDecryptionStatus.Decrypted),
            Record(Glitch2CbId, NandBootloaderStageKind.CB_A),
        ];

        AssertMismatch(
            Confirmed(NandImageFamily.Glitch2, provenance),
            CreateManifest(),
            NandSemanticEvidenceMismatchKind.ImageFamilyEvidenceUntrusted);
    }

    [Theory]
    [InlineData(NandBootloaderDecryptionStatus.NotAttempted)]
    [InlineData(NandBootloaderDecryptionStatus.NotRequired)]
    [InlineData(NandBootloaderDecryptionStatus.Failed)]
    [InlineData(null)]
    public void A_reviewed_fingerprint_requires_a_successfully_decrypted_source_stage(
        NandBootloaderDecryptionStatus? status)
    {
        ImmutableArray<NandDirectOutputEvidenceProvenance> provenance =
        [
            new(
                NandDirectOutputEvidenceSource.ReviewedDecryptedStageFingerprint,
                ManifestDigest,
                Glitch2CdId,
                NandBootloaderStageKind.CD,
                status),
            Record(Glitch2CbId, NandBootloaderStageKind.CB_A),
        ];

        AssertMismatch(
            Confirmed(NandImageFamily.Glitch2, provenance),
            CreateManifest(),
            NandSemanticEvidenceMismatchKind.ImageFamilyEvidenceUntrusted);
    }

    [Fact]
    public void A_fingerprint_defined_for_another_family_cannot_prove_the_claimed_family()
    {
        ImmutableArray<NandDirectOutputEvidenceProvenance> provenance =
        [
            Record(RetailCdId, NandBootloaderStageKind.CD),
            Record(Glitch2CbId, NandBootloaderStageKind.CB_A),
        ];

        AssertMismatch(
            Confirmed(NandImageFamily.Glitch2, provenance),
            CreateManifest(),
            NandSemanticEvidenceMismatchKind.ImageFamilyEvidenceUntrusted);
    }

    [Fact]
    public void Every_required_fingerprint_must_be_present_exactly_once_without_extra_records()
    {
        NandDirectOutputEvidenceManifest manifest = CreateManifest();
        NandDirectOutputEvidenceProvenance cd = Record(Glitch2CdId, NandBootloaderStageKind.CD);
        NandDirectOutputEvidenceProvenance cb = Record(Glitch2CbId, NandBootloaderStageKind.CB_A);

        Assert.True(manifest.HasValidProvenance(NandImageFamily.Glitch2, [cb, cd]));
        Assert.False(manifest.HasValidProvenance(NandImageFamily.Glitch2, default));
        Assert.False(manifest.HasValidProvenance(NandImageFamily.Glitch2, []));
        Assert.False(manifest.HasValidProvenance(NandImageFamily.Glitch2, [cd]));
        Assert.False(manifest.HasValidProvenance(NandImageFamily.Glitch2, [cb]));
        Assert.False(manifest.HasValidProvenance(NandImageFamily.Glitch2, [cd, cd]));
        Assert.False(manifest.HasValidProvenance(NandImageFamily.Glitch2, [cb, cb]));
        Assert.False(manifest.HasValidProvenance(NandImageFamily.Glitch2, [cd, cb, cd]));
        Assert.False(manifest.HasValidProvenance(NandImageFamily.Glitch2, [cd, cb, Record(RetailCdId, NandBootloaderStageKind.CD)]));
        Assert.False(manifest.HasValidProvenance(NandImageFamily.Glitch2, [cd, null!]));
        Assert.False(manifest.HasValidProvenance(NandImageFamily.Glitch2, [null!, cb]));

        AssertMismatch(Confirmed(NandImageFamily.Glitch2, [cd]), manifest, NandSemanticEvidenceMismatchKind.ImageFamilyEvidenceUntrusted);
        AssertMismatch(Confirmed(NandImageFamily.Glitch2, [cd, cd]), manifest, NandSemanticEvidenceMismatchKind.ImageFamilyEvidenceUntrusted);
        AssertMismatch(Confirmed(NandImageFamily.Glitch2, [cd, cb, cd]), manifest, NandSemanticEvidenceMismatchKind.ImageFamilyEvidenceUntrusted);
    }

    [Fact]
    public void Multiple_required_fingerprints_on_the_same_stage_are_not_collapsed_into_one_record()
    {
        var manifest = new NandDirectOutputEvidenceManifest(
            ManifestDigest,
            [
                new("synthetic-first-cd", NandImageFamily.Glitch2, NandBootloaderStageKind.CD),
                new("synthetic-second-cd", NandImageFamily.Glitch2, NandBootloaderStageKind.CD),
            ]);
        NandDirectOutputEvidenceProvenance first = Record("synthetic-first-cd", NandBootloaderStageKind.CD);
        NandDirectOutputEvidenceProvenance second = Record("synthetic-second-cd", NandBootloaderStageKind.CD);

        Assert.False(manifest.HasValidProvenance(NandImageFamily.Glitch2, [first]));
        Assert.False(manifest.HasValidProvenance(NandImageFamily.Glitch2, [first, first]));
        Assert.True(manifest.HasValidProvenance(NandImageFamily.Glitch2, [second, first]));
    }

    [Fact]
    public void A_manifest_without_definitions_for_the_observed_family_is_not_proof()
    {
        var empty = new NandDirectOutputEvidenceManifest(ManifestDigest, default);

        AssertMismatch(
            Confirmed(NandImageFamily.Glitch2, CompleteGlitch2Evidence()),
            empty,
            NandSemanticEvidenceMismatchKind.ImageFamilyEvidenceUntrusted);
        AssertMismatch(
            Confirmed(NandImageFamily.Devkit, [Record(RetailCdId, NandBootloaderStageKind.CD)]),
            CreateManifest(),
            NandSemanticEvidenceMismatchKind.ImageFamilyEvidenceUntrusted,
            expectedFamily: NandImageFamily.Devkit);
    }

    [Fact]
    public void Trusted_observed_family_is_compared_only_after_its_own_manifest_requirements_are_satisfied()
    {
        NandDirectOutputEvidenceManifest manifest = CreateManifest();
        NandImageFamilyEvidence retail = Confirmed(NandImageFamily.Retail, [Record(RetailCdId, NandBootloaderStageKind.CD)]);

        AssertMismatch(retail, manifest, NandSemanticEvidenceMismatchKind.ImageFamilyMismatch);
        AssertMismatch(
            Confirmed(NandImageFamily.Glitch2, CompleteGlitch2Evidence()),
            manifest,
            NandSemanticEvidenceMismatchKind.ImageFamilyMismatch,
            expectedFamily: NandImageFamily.Retail);
        AssertMismatch(
            Confirmed(NandImageFamily.Retail, [Record(Glitch2CdId, NandBootloaderStageKind.CD)]),
            manifest,
            NandSemanticEvidenceMismatchKind.ImageFamilyEvidenceUntrusted);
        AssertMismatch(retail, manifest: null, NandSemanticEvidenceMismatchKind.ImageFamilyEvidenceUnavailable);
    }

    [Theory]
    [InlineData(NandImageFamily.Retail)]
    [InlineData(NandImageFamily.Jtag)]
    [InlineData(NandImageFamily.Glitch)]
    [InlineData(NandImageFamily.Glitch2)]
    [InlineData(NandImageFamily.Glitch2m)]
    [InlineData(NandImageFamily.DevGl)]
    [InlineData(NandImageFamily.Devkit)]
    [InlineData(NandImageFamily.Testkit)]
    [InlineData(NandImageFamily.Rgh3)]
    [InlineData(NandImageFamily.Rgh13)]
    public void Complete_synthetic_future_policy_can_validate_each_explicit_family_without_legacy_hack_mapping(
        NandImageFamily family)
    {
        const string id = "synthetic-family-cd";
        var manifest = new NandDirectOutputEvidenceManifest(
            ManifestDigest,
            [new(id, family, NandBootloaderStageKind.CD)]);
        NandSemanticEvidence evidence = CreateEvidence(Confirmed(family, [Record(id, NandBootloaderStageKind.CD)]));
        var requirement = new NandSemanticEvidenceRequirement(ConsoleId.Trinity16Mb, 17559, family, manifest);

        NandSemanticEvidenceValidationResult result = NandSemanticEvidenceValidator.Validate(evidence, requirement);

        Assert.True(result.IsMatch);
        Assert.Empty(result.Mismatches);
    }

    [Fact]
    public void Hexadecimal_digest_casing_does_not_change_the_bound_policy()
    {
        ImmutableArray<NandDirectOutputEvidenceProvenance> provenance =
        [
            new(
                NandDirectOutputEvidenceSource.ReviewedDecryptedStageFingerprint,
                ManifestDigest.ToLowerInvariant(),
                Glitch2CdId,
                NandBootloaderStageKind.CD,
                NandBootloaderDecryptionStatus.Decrypted),
            Record(Glitch2CbId, NandBootloaderStageKind.CB_A),
        ];
        var requirement = new NandSemanticEvidenceRequirement(ConsoleId.Trinity16Mb, 17559, NandImageFamily.Glitch2, CreateManifest());

        Assert.True(NandSemanticEvidenceValidator.Validate(CreateEvidence(Confirmed(NandImageFamily.Glitch2, provenance)), requirement).IsMatch);
    }

    [Fact]
    public void Trusted_family_does_not_relax_exact_console_or_dashboard_comparison()
    {
        NandSemanticEvidence evidence = CreateEvidence(Confirmed(NandImageFamily.Glitch2, CompleteGlitch2Evidence()));
        var requirement = new NandSemanticEvidenceRequirement(ConsoleId.Corona16Mb, 17558, NandImageFamily.Glitch2, CreateManifest());

        NandSemanticEvidenceValidationResult result = NandSemanticEvidenceValidator.Validate(evidence, requirement);

        Assert.False(result.IsMatch);
        Assert.Equal(
            [NandSemanticEvidenceMismatchKind.ConsoleMismatch, NandSemanticEvidenceMismatchKind.DashboardMismatch],
            result.Mismatches.Select(mismatch => mismatch.Kind).ToArray());
    }

    private static NandDirectOutputEvidenceManifest CreateManifest() =>
        new(
            ManifestDigest,
            [
                new(Glitch2CdId, NandImageFamily.Glitch2, NandBootloaderStageKind.CD),
                new(Glitch2CbId, NandImageFamily.Glitch2, NandBootloaderStageKind.CB_A),
                new(RetailCdId, NandImageFamily.Retail, NandBootloaderStageKind.CD),
            ]);

    private static ImmutableArray<NandDirectOutputEvidenceProvenance> CompleteGlitch2Evidence() =>
        [Record(Glitch2CdId, NandBootloaderStageKind.CD), Record(Glitch2CbId, NandBootloaderStageKind.CB_A)];

    private static NandDirectOutputEvidenceProvenance Record(string id, NandBootloaderStageKind stage) =>
        new(
            NandDirectOutputEvidenceSource.ReviewedDecryptedStageFingerprint,
            ManifestDigest,
            id,
            stage,
            NandBootloaderDecryptionStatus.Decrypted);

    private static NandImageFamilyEvidence Confirmed(
        NandImageFamily family,
        ImmutableArray<NandDirectOutputEvidenceProvenance> provenance) =>
        new(NandEvidenceResolution.Confirmed, family, [family], provenance);

    private static NandSemanticEvidence CreateEvidence(NandImageFamilyEvidence family, NandHackTypeEvidence? compatibility = null) =>
        new(
            new NandConsoleEvidence(
                NandEvidenceResolution.Confirmed,
                ConsoleCatalog.Get(ConsoleId.Trinity16Mb),
                [ConsoleCatalog.Get(ConsoleId.Trinity16Mb)]),
            new NandKernelDashboardEvidence(
                NandEvidenceResolution.Confirmed,
                [new NandDashboardVersionRange(8498, 14699)],
                kernelBootloaderBuild: 1888,
                exactDashboardVersion: 17559),
            new NandVirtualFuseEvidence(NandVirtualFuseEvidenceStatus.Absent),
            family,
            compatibility);

    private static void AssertMismatch(
        NandImageFamilyEvidence family,
        NandDirectOutputEvidenceManifest? manifest,
        NandSemanticEvidenceMismatchKind expectedMismatch,
        NandImageFamily expectedFamily = NandImageFamily.Glitch2)
    {
        var requirement = new NandSemanticEvidenceRequirement(ConsoleId.Trinity16Mb, 17559, expectedFamily, manifest);

        NandSemanticEvidenceValidationResult result = NandSemanticEvidenceValidator.Validate(CreateEvidence(family), requirement);

        Assert.False(result.IsMatch);
        Assert.Equal(expectedMismatch, Assert.Single(result.Mismatches).Kind);
    }
}
