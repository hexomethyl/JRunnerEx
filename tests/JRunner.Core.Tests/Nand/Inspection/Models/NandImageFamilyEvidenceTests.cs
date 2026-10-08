using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using JRunner.Core.Nand.Hacks;
using JRunner.Core.Nand.Inspection.Models;
using JRunner.Core.Nand.Models;
using JRunner.Core.Nand.Security;
using Xunit;

namespace JRunner.Core.Tests.Nand.Inspection.Models;

public sealed class NandImageFamilyEvidenceTests
{
    private const string SyntheticManifestDigest = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public void Image_families_have_their_own_explicit_contract_instead_of_extending_legacy_hack_preferences()
    {
        Assert.Equal(
            ["Retail", "Jtag", "Glitch", "Glitch2", "Glitch2m", "DevGl", "Devkit", "Testkit", "Rgh3", "Rgh13"],
            Enum.GetNames<NandImageFamily>());
        Assert.NotEqual(typeof(NandHackType), typeof(NandImageFamily));
        Assert.Equal(4, (int)NandEvidenceResolution.Unavailable);
    }

    [Fact]
    public void Unavailable_normalizes_default_arrays_and_exposes_no_selected_family_candidates_or_provenance()
    {
        var evidence = new NandImageFamilyEvidence(NandEvidenceResolution.Unavailable, null, default);

        Assert.Equal(NandImageFamilyEvidence.Unavailable, evidence);
        Assert.Equal(NandEvidenceResolution.Unavailable, evidence.Resolution);
        Assert.Null(evidence.Family);
        Assert.False(evidence.IsConfirmed);
        Assert.False(evidence.Candidates.IsDefault);
        Assert.False(evidence.DirectEvidence.IsDefault);
        Assert.Empty(evidence.Candidates);
        Assert.Empty(evidence.DirectEvidence);
    }

    [Fact]
    public void Unavailable_resolution_is_family_only_and_not_a_new_console_or_dashboard_resolution()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NandConsoleEvidence(NandEvidenceResolution.Unavailable, null, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NandKernelDashboardEvidence(NandEvidenceResolution.Unavailable, default, null));
    }

    [Fact]
    public void A_single_legacy_compatibility_label_remains_ambiguous_and_does_not_select_a_hack_type()
    {
        var compatibility = new NandHackTypeEvidence(NandEvidenceResolution.Ambiguous, null, [NandHackType.Jtag]);

        Assert.Equal(NandEvidenceResolution.Ambiguous, compatibility.Resolution);
        Assert.Null(compatibility.HackType);
        Assert.False(compatibility.IsConfirmed);
        Assert.Equal([NandHackType.Jtag], compatibility.Candidates.ToArray());
        Assert.Throws<ArgumentException>(() => new NandHackTypeEvidence(NandEvidenceResolution.Ambiguous, null, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NandHackTypeEvidence(NandEvidenceResolution.Unavailable, null, []));
    }

    [Fact]
    public void Internal_confirmed_claims_may_lack_trust_so_the_validator_can_reject_them_independently()
    {
        var withoutProvenance = new NandImageFamilyEvidence(NandEvidenceResolution.Confirmed, NandImageFamily.Retail, [NandImageFamily.Retail]);
        var weakProvenance = new NandImageFamilyEvidence(
            NandEvidenceResolution.Confirmed,
            NandImageFamily.Retail,
            [NandImageFamily.Retail],
            [new NandDirectOutputEvidenceProvenance(NandDirectOutputEvidenceSource.RequestMetadata)]);

        Assert.True(withoutProvenance.IsConfirmed);
        Assert.Equal(NandImageFamily.Retail, withoutProvenance.Family);
        Assert.Empty(withoutProvenance.DirectEvidence);
        Assert.True(weakProvenance.IsConfirmed);
        Assert.Equal(NandDirectOutputEvidenceSource.RequestMetadata, Assert.Single(weakProvenance.DirectEvidence).Source);
    }

    [Fact]
    public void Family_evidence_requires_consistent_distinct_recognized_candidates()
    {
        Assert.Throws<ArgumentException>(() => new NandImageFamilyEvidence(NandEvidenceResolution.Absent, NandImageFamily.Retail, []));
        Assert.Throws<ArgumentException>(() => new NandImageFamilyEvidence(NandEvidenceResolution.Conflicting, null, [NandImageFamily.Retail]));
        Assert.Throws<ArgumentException>(() => new NandImageFamilyEvidence(NandEvidenceResolution.Confirmed, null, [NandImageFamily.Retail]));
        Assert.Throws<ArgumentException>(() => new NandImageFamilyEvidence(NandEvidenceResolution.Confirmed, NandImageFamily.Jtag, [NandImageFamily.Retail]));
        Assert.Throws<ArgumentException>(() => new NandImageFamilyEvidence(NandEvidenceResolution.Confirmed, NandImageFamily.Retail, [NandImageFamily.Retail, NandImageFamily.Jtag]));
        Assert.Throws<ArgumentException>(() => new NandImageFamilyEvidence(NandEvidenceResolution.Ambiguous, null, [NandImageFamily.Retail]));
        Assert.Throws<ArgumentException>(() => new NandImageFamilyEvidence(NandEvidenceResolution.Ambiguous, NandImageFamily.Retail, [NandImageFamily.Retail, NandImageFamily.Jtag]));
        Assert.Throws<ArgumentException>(() => new NandImageFamilyEvidence(NandEvidenceResolution.Ambiguous, null, [NandImageFamily.Retail, NandImageFamily.Retail]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NandImageFamilyEvidence(NandEvidenceResolution.Confirmed, (NandImageFamily)99, [(NandImageFamily)99]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NandImageFamilyEvidence((NandEvidenceResolution)99, null, []));
        Assert.Throws<ArgumentNullException>(() => new NandImageFamilyEvidence(NandEvidenceResolution.Confirmed, NandImageFamily.Retail, [NandImageFamily.Retail], [null!]));
        Assert.Throws<ArgumentException>(() => new NandImageFamilyEvidence(
            NandEvidenceResolution.Unavailable,
            null,
            [],
            [new NandDirectOutputEvidenceProvenance(NandDirectOutputEvidenceSource.None)]));
    }

    [Fact]
    public void Reviewed_metadata_validates_digest_and_unique_record_definitions_but_does_not_supply_a_corpus()
    {
        var definition = new NandDirectOutputFingerprintDefinition("synthetic-cd", NandImageFamily.Glitch2, NandBootloaderStageKind.CD);
        var manifest = new NandDirectOutputEvidenceManifest(SyntheticManifestDigest, [definition]);
        var emptyManifest = new NandDirectOutputEvidenceManifest(SyntheticManifestDigest, default);

        Assert.Equal(SyntheticManifestDigest, manifest.Sha256);
        Assert.Same(definition, Assert.Single(manifest.Fingerprints));
        Assert.False(emptyManifest.Fingerprints.IsDefault);
        Assert.Empty(emptyManifest.Fingerprints);
        Assert.False(emptyManifest.HasValidProvenance(NandImageFamily.Glitch2, default));
        Assert.Throws<ArgumentNullException>(() => new NandDirectOutputEvidenceManifest(null!, [definition]));
        Assert.Throws<ArgumentException>(() => new NandDirectOutputEvidenceManifest("", [definition]));
        Assert.Throws<ArgumentException>(() => new NandDirectOutputEvidenceManifest(new string('A', 63), [definition]));
        Assert.Throws<ArgumentException>(() => new NandDirectOutputEvidenceManifest(new string('Z', 64), [definition]));
        Assert.Throws<ArgumentException>(() => new NandDirectOutputEvidenceManifest(
            SyntheticManifestDigest,
            [definition, new NandDirectOutputFingerprintDefinition("synthetic-cd", NandImageFamily.Retail, NandBootloaderStageKind.CD)]));
        Assert.Throws<ArgumentNullException>(() => new NandDirectOutputEvidenceManifest(SyntheticManifestDigest, [null!]));
    }

    [Fact]
    public void Provenance_and_definitions_reject_unknown_enums_without_requiring_a_trustworthy_claim_at_construction()
    {
        var claimed = new NandDirectOutputEvidenceProvenance(NandDirectOutputEvidenceSource.ReviewedDecryptedStageFingerprint);

        Assert.Null(claimed.ManifestSha256);
        Assert.Null(claimed.FingerprintId);
        Assert.Null(claimed.Stage);
        Assert.Null(claimed.DecryptionStatus);
        Assert.Throws<ArgumentOutOfRangeException>(() => new NandDirectOutputEvidenceProvenance((NandDirectOutputEvidenceSource)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NandDirectOutputEvidenceProvenance(NandDirectOutputEvidenceSource.None, stage: (NandBootloaderStageKind)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NandDirectOutputEvidenceProvenance(NandDirectOutputEvidenceSource.None, decryptionStatus: (NandBootloaderDecryptionStatus)99));
        Assert.Throws<ArgumentNullException>(() => new NandDirectOutputFingerprintDefinition(null!, NandImageFamily.Retail, NandBootloaderStageKind.CD));
        Assert.Throws<ArgumentException>(() => new NandDirectOutputFingerprintDefinition(" ", NandImageFamily.Retail, NandBootloaderStageKind.CD));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NandDirectOutputFingerprintDefinition("synthetic", (NandImageFamily)99, NandBootloaderStageKind.CD));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NandDirectOutputFingerprintDefinition("synthetic", NandImageFamily.Retail, (NandBootloaderStageKind)99));
    }

    [Fact]
    public void Requirement_keeps_exact_console_dashboard_family_and_optional_reviewed_manifest()
    {
        var manifest = new NandDirectOutputEvidenceManifest(SyntheticManifestDigest, []);
        var requirement = new NandSemanticEvidenceRequirement(ConsoleId.Trinity16Mb, 17559, NandImageFamily.Devkit, manifest);
        var unavailableRequirement = new NandSemanticEvidenceRequirement(ConsoleId.Trinity16Mb, null, NandImageFamily.Retail);

        Assert.Equal(ConsoleId.Trinity16Mb, requirement.Console);
        Assert.Equal(17559, requirement.DashboardVersion);
        Assert.Equal(NandImageFamily.Devkit, requirement.ImageFamily);
        Assert.Same(manifest, requirement.ReviewedManifest);
        Assert.Null(unavailableRequirement.DashboardVersion);
        Assert.Null(unavailableRequirement.ReviewedManifest);
        Assert.Throws<ArgumentOutOfRangeException>(() => new NandSemanticEvidenceRequirement((ConsoleId)99, null, NandImageFamily.Retail));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NandSemanticEvidenceRequirement(ConsoleId.Trinity16Mb, 0, NandImageFamily.Retail));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NandSemanticEvidenceRequirement(ConsoleId.Trinity16Mb, null, (NandImageFamily)99));
    }

    [Fact]
    public void Model_collections_remain_immutable_and_do_not_alias_mutable_fixture_arrays()
    {
        NandImageFamily[] candidates = [NandImageFamily.Glitch2];
        NandDirectOutputEvidenceProvenance[] records = [new(NandDirectOutputEvidenceSource.StageMetadata)];
        NandDirectOutputFingerprintDefinition[] definitions = [new("synthetic-cd", NandImageFamily.Glitch2, NandBootloaderStageKind.CD)];
        var evidence = new NandImageFamilyEvidence(
            NandEvidenceResolution.Confirmed,
            NandImageFamily.Glitch2,
            ImmutableArray.CreateRange(candidates),
            ImmutableArray.CreateRange(records));
        var manifest = new NandDirectOutputEvidenceManifest(SyntheticManifestDigest, ImmutableArray.CreateRange(definitions));
        candidates[0] = NandImageFamily.Retail;
        records[0] = new NandDirectOutputEvidenceProvenance(NandDirectOutputEvidenceSource.RequestMetadata);
        definitions[0] = new NandDirectOutputFingerprintDefinition("replacement", NandImageFamily.Retail, NandBootloaderStageKind.CE);

        Assert.Equal(NandImageFamily.Glitch2, Assert.Single(evidence.Candidates));
        Assert.Equal(NandDirectOutputEvidenceSource.StageMetadata, Assert.Single(evidence.DirectEvidence).Source);
        Assert.Equal("synthetic-cd", Assert.Single(manifest.Fingerprints).Id);
        Assert.NotEqual(evidence.Candidates, evidence.Candidates.SetItem(0, NandImageFamily.Retail));
        Assert.Equal(NandImageFamily.Glitch2, Assert.Single(evidence.Candidates));
    }

    [Fact]
    public void Direct_evidence_contracts_expose_only_readonly_scalars_and_immutable_metadata_without_keys_or_stage_bytes()
    {
        Type[] types =
        [
            typeof(NandImageFamilyEvidence),
            typeof(NandDirectOutputEvidenceProvenance),
            typeof(NandDirectOutputFingerprintDefinition),
            typeof(NandDirectOutputEvidenceManifest),
        ];
        foreach (Type type in types)
        {
            Assert.Empty(type.GetConstructors());
            Assert.All(type.GetProperties(), static property => Assert.False(property.CanWrite));
            Assert.DoesNotContain(type.GetProperties().Select(property => property.PropertyType), IsUnsafeSurfaceType);
            Assert.DoesNotContain(
                type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Select(field => field.FieldType),
                IsUnsafeSurfaceType);
            Assert.DoesNotContain(
                type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .SelectMany(constructor => constructor.GetParameters())
                    .Select(parameter => parameter.ParameterType),
                IsUnsafeSurfaceType);
        }

        Assert.Equal(
            ["Source", "ManifestSha256", "FingerprintId", "Stage", "DecryptionStatus"],
            typeof(NandDirectOutputEvidenceProvenance).GetProperties().Select(property => property.Name).ToArray());
        var provenance = new NandDirectOutputEvidenceProvenance(
            NandDirectOutputEvidenceSource.ReviewedDecryptedStageFingerprint,
            SyntheticManifestDigest,
            "synthetic-cd",
            NandBootloaderStageKind.CD,
            NandBootloaderDecryptionStatus.Decrypted);
        string json = JsonSerializer.Serialize(provenance);
        Assert.DoesNotContain("CpuKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("StageBytes", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("synthetic-cd", json, StringComparison.Ordinal);
    }

    private static bool IsUnsafeSurfaceType(Type type) =>
        type == typeof(byte[]) ||
        type == typeof(Memory<byte>) ||
        type == typeof(ReadOnlyMemory<byte>) ||
        type == typeof(Span<byte>) ||
        type == typeof(ReadOnlySpan<byte>) ||
        type == typeof(ImmutableArray<byte>) ||
        type == typeof(CpuKey);
}
