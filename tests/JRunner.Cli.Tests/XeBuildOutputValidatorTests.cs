using System.Collections.Immutable;
using System.Security.Cryptography;
using JRunner.Cli.Infrastructure;
using JRunner.Core.Contracts;
using JRunner.Core.Nand.Hacks;
using JRunner.Core.Nand.Inspection;
using JRunner.Core.Nand.Inspection.Models;
using JRunner.Core.Nand.Models;
using JRunner.Core.Nand.Security;
using JRunner.Core.XeBuild;
using JRunner.Core.XeBuild.Preparation;
using JRunner.Tests.Fixtures;
using Xunit;

namespace JRunner.Cli.Tests;

public sealed class XeBuildOutputValidatorTests
{
    // These descriptors exercise a future reviewed-policy boundary only. They are not NAND
    // fingerprints, an installed corpus, or a capability that can enable the Wine backend.
    private const string SyntheticManifestSha256 = "0000000000000000000000000000000000000000000000000000000000000000";
    private const string DifferentManifestSha256 = "1111111111111111111111111111111111111111111111111111111111111111";
    private static readonly NandDirectOutputEvidenceManifest SyntheticManifest = new(
        SyntheticManifestSha256,
        ImmutableArray.Create(
            new NandDirectOutputFingerprintDefinition("test-only-retail", NandImageFamily.Retail, NandBootloaderStageKind.CB_A),
            new NandDirectOutputFingerprintDefinition("test-only-jtag", NandImageFamily.Jtag, NandBootloaderStageKind.CD),
            new NandDirectOutputFingerprintDefinition("test-only-glitch", NandImageFamily.Glitch, NandBootloaderStageKind.CB_A),
            new NandDirectOutputFingerprintDefinition("test-only-glitch2", NandImageFamily.Glitch2, NandBootloaderStageKind.CB_B),
            new NandDirectOutputFingerprintDefinition("test-only-glitch2m", NandImageFamily.Glitch2m, NandBootloaderStageKind.CB_B),
            new NandDirectOutputFingerprintDefinition("test-only-devgl-a", NandImageFamily.DevGl, NandBootloaderStageKind.CB_A),
            new NandDirectOutputFingerprintDefinition("test-only-devgl-d", NandImageFamily.DevGl, NandBootloaderStageKind.CD),
            new NandDirectOutputFingerprintDefinition("test-only-devkit", NandImageFamily.Devkit, NandBootloaderStageKind.SC),
            new NandDirectOutputFingerprintDefinition("test-only-testkit", NandImageFamily.Testkit, NandBootloaderStageKind.CE),
            new NandDirectOutputFingerprintDefinition("test-only-rgh3", NandImageFamily.Rgh3, NandBootloaderStageKind.CB_X),
            new NandDirectOutputFingerprintDefinition("test-only-rgh13", NandImageFamily.Rgh13, NandBootloaderStageKind.CB_X)));

    [Theory]
    [InlineData("retail", NandImageFamily.Retail)]
    [InlineData("jtag", NandImageFamily.Jtag)]
    [InlineData("glitch", NandImageFamily.Glitch)]
    [InlineData("glitch2", NandImageFamily.Glitch2)]
    [InlineData("glitch2m", NandImageFamily.Glitch2m)]
    [InlineData("devgl", NandImageFamily.DevGl)]
    [InlineData("devgl16", NandImageFamily.DevGl)]
    [InlineData("devkit", NandImageFamily.Devkit)]
    [InlineData("devkit16", NandImageFamily.Devkit)]
    [InlineData("testkit", NandImageFamily.Testkit)]
    [InlineData("testkit16", NandImageFamily.Testkit)]
    public void Future_reviewed_direct_evidence_can_match_a_canonical_family_but_cannot_enable_the_production_catalog(
        string targetType,
        NandImageFamily expectedFamily)
    {
        XeBuildRequest request = CreateRequest(targetType: targetType);
        NandSemanticEvidence evidence = CreateEvidence(imageFamily: ConfirmedFamily(expectedFamily));
        XeBuildOutputEvidenceCapability capability = TestOnlyCapability(request);

        Assert.Equal(expectedFamily, capability.RequiredFamily);
        XeBuildOutputValidator.Validate(evidence, request, capability);
        AssertInvalid(evidence, request, "ImageFamilyEvidenceUnavailable", useProductionCatalog: true);
        Assert.False(XeBuildOutputEvidenceCatalog.Get(request.Target.HackType).IsAvailable);
    }

    [Fact]
    public void Exact_dashboard_evidence_does_not_require_a_legacy_CB_compatibility_range()
    {
        NandSemanticEvidence evidence = CreateEvidence(dashboard: new NandKernelDashboardEvidence(
            NandEvidenceResolution.Confirmed,
            ImmutableArray<NandDashboardVersionRange>.Empty,
            kernelBootloaderBuild: null,
            exactDashboardVersion: 17559));
        XeBuildRequest request = CreateRequest();

        XeBuildOutputValidator.Validate(evidence, request, TestOnlyCapability(request));
    }

    [Theory]
    [InlineData(NandEvidenceResolution.Absent, "ConsoleAbsent")]
    [InlineData(NandEvidenceResolution.Ambiguous, "ConsoleAmbiguous")]
    [InlineData(NandEvidenceResolution.Conflicting, "ConsoleConflicting")]
    [InlineData(NandEvidenceResolution.Confirmed, "ConsoleMismatch")]
    public void Future_enabled_capabilities_still_require_exact_console_evidence(
        NandEvidenceResolution resolution,
        string expectedMismatch)
    {
        ConsoleDefinition trinity = ConsoleCatalog.Get(ConsoleId.Trinity16Mb);
        ConsoleDefinition corona = ConsoleCatalog.Get(ConsoleId.Corona16Mb);
        NandConsoleEvidence console = resolution switch
        {
            NandEvidenceResolution.Absent or NandEvidenceResolution.Conflicting => new(resolution, null, ImmutableArray<ConsoleDefinition>.Empty),
            NandEvidenceResolution.Ambiguous => new(resolution, null, ImmutableArray.Create(trinity, corona)),
            NandEvidenceResolution.Confirmed => new(resolution, corona, ImmutableArray.Create(corona)),
            _ => throw new InvalidOperationException("Unknown console evidence test case."),
        };

        AssertInvalid(CreateEvidence(console: console), CreateRequest(), expectedMismatch);
    }

    [Theory]
    [InlineData(NandEvidenceResolution.Absent, "DashboardAbsent")]
    [InlineData(NandEvidenceResolution.Ambiguous, "DashboardAmbiguous")]
    [InlineData(NandEvidenceResolution.Conflicting, "DashboardConflicting")]
    [InlineData(NandEvidenceResolution.Confirmed, "DashboardMismatch")]
    public void Future_enabled_capabilities_still_require_exact_dashboard_evidence(
        NandEvidenceResolution resolution,
        string expectedMismatch)
    {
        NandKernelDashboardEvidence dashboard = resolution switch
        {
            NandEvidenceResolution.Absent or NandEvidenceResolution.Conflicting => new(
                resolution,
                ImmutableArray<NandDashboardVersionRange>.Empty,
                kernelBootloaderBuild: 17559),
            NandEvidenceResolution.Ambiguous => new(
                resolution,
                ImmutableArray.Create(new NandDashboardVersionRange(8498, 14699), new NandDashboardVersionRange(14717, 17559)),
                kernelBootloaderBuild: 17559),
            NandEvidenceResolution.Confirmed => new(
                resolution,
                ImmutableArray.Create(new NandDashboardVersionRange(0, 65535)),
                kernelBootloaderBuild: 17559,
                exactDashboardVersion: 17526),
            _ => throw new InvalidOperationException("Unknown dashboard evidence test case."),
        };

        AssertInvalid(CreateEvidence(dashboard: dashboard), CreateRequest(), expectedMismatch);
    }

    [Theory]
    [InlineData(17559, 17559, 17559)]
    [InlineData(0, 65535, 17559)]
    [InlineData(8498, 14699, 14699)]
    public void CB_compatibility_ranges_and_matching_generic_stage_builds_cannot_replace_exact_dashboard_evidence(
        int minimumDashboard,
        int maximumDashboard,
        int requestedDashboard)
    {
        NandKernelDashboardEvidence dashboard = new(
            NandEvidenceResolution.Confirmed,
            ImmutableArray.Create(new NandDashboardVersionRange(minimumDashboard, maximumDashboard)),
            kernelBootloaderBuild: requestedDashboard);

        AssertInvalid(CreateEvidence(dashboard: dashboard), CreateRequest(dashboardVersion: requestedDashboard), "DashboardNotExact");
    }

    [Theory]
    [InlineData(NandEvidenceResolution.Unavailable, "ImageFamilyEvidenceUnavailable")]
    [InlineData(NandEvidenceResolution.Absent, "ImageFamilyAbsent")]
    [InlineData(NandEvidenceResolution.Ambiguous, "ImageFamilyAmbiguous")]
    [InlineData(NandEvidenceResolution.Conflicting, "ImageFamilyConflicting")]
    [InlineData(NandEvidenceResolution.Confirmed, "ImageFamilyMismatch")]
    public void Unavailable_absent_ambiguous_conflicting_and_mismatched_output_evidence_are_distinct(
        NandEvidenceResolution resolution,
        string expectedMismatch)
    {
        NandImageFamilyEvidence imageFamily = resolution switch
        {
            NandEvidenceResolution.Unavailable => NandImageFamilyEvidence.Unavailable,
            NandEvidenceResolution.Absent or NandEvidenceResolution.Conflicting => new(resolution, null, ImmutableArray<NandImageFamily>.Empty),
            NandEvidenceResolution.Ambiguous => new(resolution, null, ImmutableArray.Create(NandImageFamily.Glitch2, NandImageFamily.Glitch2m)),
            NandEvidenceResolution.Confirmed => ConfirmedFamily(NandImageFamily.Glitch),
            _ => throw new InvalidOperationException("Unknown image-family evidence test case."),
        };

        AssertInvalid(CreateEvidence(imageFamily: imageFamily), CreateRequest(), expectedMismatch);
    }

    [Theory]
    [InlineData(NandDirectOutputEvidenceSource.None)]
    [InlineData(NandDirectOutputEvidenceSource.CompatibilityTable)]
    [InlineData(NandDirectOutputEvidenceSource.StageMetadata)]
    [InlineData(NandDirectOutputEvidenceSource.SmcMarker)]
    [InlineData(NandDirectOutputEvidenceSource.VirtualFuseMarker)]
    [InlineData(NandDirectOutputEvidenceSource.RequestMetadata)]
    public void Non_direct_sources_cannot_satisfy_provenance_even_with_a_confirmed_claim_and_matching_manifest_metadata(
        NandDirectOutputEvidenceSource source)
    {
        var provenance = new NandDirectOutputEvidenceProvenance(
            source,
            SyntheticManifestSha256,
            "test-only-glitch2",
            NandBootloaderStageKind.CB_B,
            NandBootloaderDecryptionStatus.Decrypted);
        var family = new NandImageFamilyEvidence(
            NandEvidenceResolution.Confirmed,
            NandImageFamily.Glitch2,
            ImmutableArray.Create(NandImageFamily.Glitch2),
            ImmutableArray.Create(provenance));

        AssertInvalid(CreateEvidence(imageFamily: family), CreateRequest(), "ImageFamilyEvidenceUntrusted");
    }

    [Theory]
    [InlineData("jtag", NandImageFamily.Jtag)]
    [InlineData("glitch", NandImageFamily.Glitch)]
    [InlineData("glitch2", NandImageFamily.Glitch2)]
    [InlineData("glitch2m", NandImageFamily.Glitch2m)]
    [InlineData("devgl", NandImageFamily.DevGl)]
    public void Table_compatible_confirmed_family_claims_are_rejected_if_the_validator_is_invoked_directly(
        string targetType,
        NandImageFamily claimedFamily)
    {
        var tableOnly = new NandImageFamilyEvidence(
            NandEvidenceResolution.Confirmed,
            claimedFamily,
            ImmutableArray.Create(claimedFamily));
        XeBuildRequest request = CreateRequest(targetType: targetType);

        AssertInvalid(CreateEvidence(imageFamily: tableOnly), request, "ImageFamilyEvidenceUntrusted");
        AssertInvalid(CreateEvidence(imageFamily: tableOnly), request, "ImageFamilyEvidenceUnavailable", useProductionCatalog: true);
    }

    [Theory]
    [InlineData(NandHackType.Jtag)]
    [InlineData(NandHackType.Glitch)]
    [InlineData(NandHackType.Glitch2)]
    [InlineData(NandHackType.Glitch2m)]
    [InlineData(NandHackType.DevGl)]
    [InlineData(NandHackType.Rgh3)]
    [InlineData(NandHackType.Rgh13)]
    public void Legacy_Hack_diagnostics_even_claimed_confirmed_never_replace_direct_ImageFamily_evidence(NandHackType compatibilityLabel)
    {
        var compatibilityHack = new NandHackTypeEvidence(
            NandEvidenceResolution.Confirmed,
            compatibilityLabel,
            ImmutableArray.Create(compatibilityLabel));
        NandSemanticEvidence evidence = CreateEvidence(
            imageFamily: new NandImageFamilyEvidence(NandEvidenceResolution.Absent, null, ImmutableArray<NandImageFamily>.Empty),
            compatibilityHack: compatibilityHack);
        XeBuildRequest request = CreateRequest(rgh3: true);

        AssertInvalid(evidence, request, "ImageFamilyAbsent", useProductionCatalog: true);
        Assert.Same(compatibilityHack, evidence.Hack);
    }

    [Theory]
    [InlineData("retail", NandImageFamily.Retail)]
    [InlineData("devkit", NandImageFamily.Devkit)]
    [InlineData("devkit16", NandImageFamily.Devkit)]
    [InlineData("testkit", NandImageFamily.Testkit)]
    [InlineData("testkit16", NandImageFamily.Testkit)]
    public void Retail_devkit_and_testkit_cannot_be_guessed_from_stage_metadata_or_development_class(
        string targetType,
        NandImageFamily claimedFamily)
    {
        var guessed = new NandImageFamilyEvidence(
            NandEvidenceResolution.Confirmed,
            claimedFamily,
            ImmutableArray.Create(claimedFamily),
            ImmutableArray.Create(new NandDirectOutputEvidenceProvenance(NandDirectOutputEvidenceSource.StageMetadata)));

        AssertInvalid(CreateEvidence(imageFamily: guessed), CreateRequest(targetType: targetType), "ImageFamilyEvidenceUntrusted");
    }

    [Theory]
    [InlineData("missing-manifest")]
    [InlineData("wrong-manifest")]
    [InlineData("missing-record")]
    [InlineData("unknown-record")]
    [InlineData("wrong-stage")]
    [InlineData("other-family-record")]
    public void A_direct_provenance_claim_must_bind_to_the_reviewed_manifest_record_family_and_stage_before_comparing_the_family(
        string invalidProvenance)
    {
        string? digest = invalidProvenance switch
        {
            "missing-manifest" => null,
            "wrong-manifest" => DifferentManifestSha256,
            _ => SyntheticManifestSha256,
        };
        string? recordId = invalidProvenance switch
        {
            "missing-record" => null,
            "unknown-record" => "test-only-not-in-manifest",
            "other-family-record" => "test-only-glitch2",
            _ => "test-only-retail",
        };
        var provenance = new NandDirectOutputEvidenceProvenance(
            NandDirectOutputEvidenceSource.ReviewedDecryptedStageFingerprint,
            digest,
            recordId,
            invalidProvenance == "wrong-stage" ? NandBootloaderStageKind.CD : NandBootloaderStageKind.CB_A,
            NandBootloaderDecryptionStatus.Decrypted);
        var claimed = new NandImageFamilyEvidence(
            NandEvidenceResolution.Confirmed,
            NandImageFamily.Retail,
            ImmutableArray.Create(NandImageFamily.Retail),
            ImmutableArray.Create(provenance));

        // The claimed family is also wrong for this request. Untrusted provenance must not
        // become a reviewed positive conclusion and reach the family comparison first.
        OperationFailureException failure = AssertInvalid(CreateEvidence(imageFamily: claimed), CreateRequest(), "ImageFamilyEvidenceUntrusted");
        Assert.DoesNotContain("ImageFamilyMismatch", failure.Message);
    }

    [Theory]
    [InlineData(NandBootloaderDecryptionStatus.NotAttempted)]
    [InlineData(NandBootloaderDecryptionStatus.NotRequired)]
    [InlineData(NandBootloaderDecryptionStatus.Failed)]
    public void Direct_output_fingerprint_evidence_requires_successful_stage_decryption(NandBootloaderDecryptionStatus status)
    {
        var provenance = new NandDirectOutputEvidenceProvenance(
            NandDirectOutputEvidenceSource.ReviewedDecryptedStageFingerprint,
            SyntheticManifestSha256,
            "test-only-glitch2",
            NandBootloaderStageKind.CB_B,
            status);
        var family = new NandImageFamilyEvidence(
            NandEvidenceResolution.Confirmed,
            NandImageFamily.Glitch2,
            ImmutableArray.Create(NandImageFamily.Glitch2),
            ImmutableArray.Create(provenance));

        AssertInvalid(CreateEvidence(imageFamily: family), CreateRequest(), "ImageFamilyEvidenceUntrusted");
    }

    [Fact]
    public void Partial_duplicate_or_mixed_trust_fingerprint_sets_are_not_positive_output_evidence()
    {
        XeBuildRequest request = CreateRequest(targetType: "devgl");
        ImmutableArray<NandDirectOutputEvidenceProvenance> complete = DirectEvidence(NandImageFamily.DevGl);
        ImmutableArray<NandDirectOutputEvidenceProvenance>[] invalidSets =
        [
            ImmutableArray.Create(complete[0]),
            complete.Add(complete[0]),
            complete.Add(new NandDirectOutputEvidenceProvenance(NandDirectOutputEvidenceSource.VirtualFuseMarker)),
        ];
        foreach (ImmutableArray<NandDirectOutputEvidenceProvenance> invalid in invalidSets)
        {
            var family = new NandImageFamilyEvidence(
                NandEvidenceResolution.Confirmed,
                NandImageFamily.DevGl,
                ImmutableArray.Create(NandImageFamily.DevGl),
                invalid);
            AssertInvalid(CreateEvidence(imageFamily: family), request, "ImageFamilyEvidenceUntrusted");
        }
    }

    [Fact]
    public void Every_independent_mismatch_is_reported_without_arbitrating_a_console_or_family_candidate()
    {
        NandSemanticEvidence evidence = CreateEvidence(
            console: new NandConsoleEvidence(NandEvidenceResolution.Conflicting, null, ImmutableArray<ConsoleDefinition>.Empty),
            dashboard: new NandKernelDashboardEvidence(NandEvidenceResolution.Absent, ImmutableArray<NandDashboardVersionRange>.Empty, kernelBootloaderBuild: null),
            imageFamily: new NandImageFamilyEvidence(NandEvidenceResolution.Ambiguous, null, ImmutableArray.Create(NandImageFamily.Glitch2, NandImageFamily.Glitch2m)));

        OperationFailureException failure = AssertInvalid(evidence, CreateRequest(), "ConsoleConflicting");

        Assert.Contains("DashboardAbsent", failure.Message);
        Assert.Contains("ImageFamilyAmbiguous", failure.Message);
    }

    [Fact]
    public void An_explicit_console_override_is_the_requirement_not_the_detected_source_console()
    {
        XeBuildRequest request = CreateRequest(consoleOverride: "Corona 16MB", detectedConsole: ConsoleId.Trinity16Mb);

        XeBuildOutputValidator.Validate(CreateEvidence(console: ConfirmedConsole(ConsoleId.Corona16Mb)), request, TestOnlyCapability(request));
        AssertInvalid(CreateEvidence(console: ConfirmedConsole(ConsoleId.Trinity16Mb)), request, "ConsoleMismatch");
    }

    [Fact]
    public void A_request_without_an_override_or_detected_console_cannot_publish_even_with_direct_output_evidence()
    {
        AssertInvalid(CreateEvidence(), CreateRequest(detectedConsole: null), "no positive inspection evidence");
    }

    [Theory]
    [InlineData("glitch2")]
    [InlineData("glitch2m")]
    public void RGH3_options_require_the_direct_RGH3_family_instead_of_the_underlying_target(string targetType)
    {
        XeBuildRequest request = CreateRequest(targetType: targetType, rgh3: true);

        XeBuildOutputValidator.Validate(CreateEvidence(imageFamily: ConfirmedFamily(NandImageFamily.Rgh3)), request, TestOnlyCapability(request));
        AssertInvalid(CreateEvidence(imageFamily: ConfirmedFamily(targetType == "glitch2" ? NandImageFamily.Glitch2 : NandImageFamily.Glitch2m)), request, "ImageFamilyMismatch");
    }

    [Theory]
    [InlineData(NandImageFamily.Glitch2)]
    [InlineData(NandImageFamily.Glitch2m)]
    [InlineData(NandImageFamily.Rgh13)]
    public void RGH2_RGH2M_and_RGH13_do_not_satisfy_an_RGH3_requirement(NandImageFamily observedFamily)
    {
        AssertInvalid(CreateEvidence(imageFamily: ConfirmedFamily(observedFamily)), CreateRequest(rgh3: true), "ImageFamilyMismatch");
    }

    [Fact]
    public void RGH3_does_not_satisfy_a_plain_Glitch2_requirement()
    {
        AssertInvalid(CreateEvidence(imageFamily: ConfirmedFamily(NandImageFamily.Rgh3)), CreateRequest(), "ImageFamilyMismatch");
    }

    [Theory]
    [InlineData("devgl", ConsoleId.TrinityBigBlock, ConsoleId.Trinity16Mb, NandImageFamily.DevGl)]
    [InlineData("devgl16", ConsoleId.Trinity16Mb, ConsoleId.TrinityBigBlock, NandImageFamily.DevGl)]
    [InlineData("devkit", ConsoleId.TrinityBigBlock, ConsoleId.Trinity16Mb, NandImageFamily.Devkit)]
    [InlineData("devkit16", ConsoleId.Trinity16Mb, ConsoleId.TrinityBigBlock, NandImageFamily.Devkit)]
    [InlineData("testkit", ConsoleId.TrinityBigBlock, ConsoleId.Trinity16Mb, NandImageFamily.Testkit)]
    [InlineData("testkit16", ConsoleId.Trinity16Mb, ConsoleId.TrinityBigBlock, NandImageFamily.Testkit)]
    public void Sixteen_MiB_variants_share_their_family_but_still_require_exact_console_layout(
        string targetType,
        ConsoleId requestedConsole,
        ConsoleId otherLayout,
        NandImageFamily family)
    {
        XeBuildRequest request = CreateRequest(targetType: targetType, consoleOverride: ConsoleCatalog.Get(requestedConsole).CanonicalName);

        XeBuildOutputValidator.Validate(CreateEvidence(console: ConfirmedConsole(requestedConsole), imageFamily: ConfirmedFamily(family)), request, TestOnlyCapability(request));
        AssertInvalid(CreateEvidence(console: ConfirmedConsole(otherLayout), imageFamily: ConfirmedFamily(family)), request, "ConsoleMismatch");
        AssertInvalid(CreateEvidence(console: ConfirmedConsole(requestedConsole), imageFamily: ConfirmedFamily(NandImageFamily.Glitch2)), request, "ImageFamilyMismatch");
    }

    [Fact]
    public void A_capability_for_another_requested_type_or_family_cannot_satisfy_the_post_build_gate()
    {
        XeBuildRequest request = CreateRequest();
        var wrongType = new XeBuildOutputEvidenceCapability(XeBuildHackType.Jtag, NandImageFamily.Glitch2, SyntheticManifest);
        var wrongFamily = new XeBuildOutputEvidenceCapability(XeBuildHackType.Glitch2, NandImageFamily.Retail, SyntheticManifest);
        foreach (XeBuildOutputEvidenceCapability capability in new[] { wrongType, wrongFamily })
        {
            OperationFailureException failure = Assert.Throws<OperationFailureException>(() =>
                XeBuildOutputValidator.Validate(CreateEvidence(), request, capability));
            AssertFailure(failure, "does not describe the requested image family");
        }
    }

    [Fact]
    public async Task Inspection_overload_rejects_the_real_table_compatible_fixture_independently_of_the_backend_gate()
    {
        string inputPath = FixtureCatalog.GetPath("nand/small-block.bin");
        string keyText = (await File.ReadAllTextAsync(FixtureCatalog.GetPath("keys/small-block.cpukey"))).Trim();
        CpuKey key = CpuKey.Parse(keyText);
        await using FileStream input = File.OpenRead(inputPath);
        NandInspectionResult inspection = await NandImageService.InspectAsync(input, key);
        XeBuildRequest request = CreateRequest();

        OperationFailureException failure = Assert.Throws<OperationFailureException>(() => XeBuildOutputValidator.Validate(inspection, request));

        AssertFailure(failure, "ImageFamilyEvidenceUnavailable");
        Assert.Equal(NandHackType.Glitch2, inspection.HackEvidence.PreferredHack);
        Assert.Equal(NandEvidenceResolution.Absent, inspection.SemanticEvidence.ImageFamily.Resolution);
        Assert.Null(inspection.SemanticEvidence.ImageFamily.Family);
        Assert.Null(inspection.SemanticEvidence.KernelDashboard.ExactDashboardVersion);
        Assert.DoesNotContain(inspection.BootloaderStages, stage => stage.Kind is NandBootloaderStageKind.CF0 or NandBootloaderStageKind.CG0);
        AssertInvalid(inspection.SemanticEvidence, request, "ImageFamilyAbsent");
    }

    [Theory]
    [InlineData("glitch2")]
    [InlineData("glitch2m")]
    public void Enabled_RGH3_capability_requires_the_actual_reviewed_signature_provenance_and_exact_semantics(string targetType)
    {
        XeBuildRequest request = CreateRequest(targetType: targetType, rgh3: true);
        NandDirectOutputEvidenceManifest manifest = NandRgh3OutputEvidence.ReviewedManifest;
        NandDirectOutputFingerprintDefinition fingerprint = Assert.Single(manifest.Fingerprints);
        var directFamily = new NandImageFamilyEvidence(
            NandEvidenceResolution.Confirmed,
            NandImageFamily.Rgh3,
            ImmutableArray.Create(NandImageFamily.Rgh3),
            ImmutableArray.Create(new NandDirectOutputEvidenceProvenance(
                NandDirectOutputEvidenceSource.ReviewedDecryptedStageFingerprint,
                manifest.Sha256,
                fingerprint.Id,
                fingerprint.Stage,
                NandBootloaderDecryptionStatus.Decrypted)));
        NandSemanticEvidence evidence = CreateEvidence(imageFamily: directFamily);

        Assert.True(XeBuildOutputEvidenceCatalog.Get(request.Target.HackType, rgh3: true).IsAvailable);
        Assert.False(XeBuildOutputEvidenceCatalog.Get(request.Target.HackType).IsAvailable);
        XeBuildOutputValidator.Validate(evidence, request);
        AssertInvalid(
            CreateEvidence(imageFamily: new NandImageFamilyEvidence(NandEvidenceResolution.Absent, null, ImmutableArray<NandImageFamily>.Empty)),
            request,
            "ImageFamilyAbsent",
            useProductionCatalog: true);
        AssertInvalid(CreateEvidence(imageFamily: NandImageFamilyEvidence.Unavailable), request, "ImageFamilyEvidenceUnavailable", useProductionCatalog: true);
        AssertInvalid(CreateEvidence(imageFamily: ConfirmedFamily(NandImageFamily.Rgh3)), request, "ImageFamilyEvidenceUntrusted", useProductionCatalog: true);
        AssertInvalid(CreateEvidence(console: ConfirmedConsole(ConsoleId.Corona16Mb), imageFamily: directFamily), request, "ConsoleMismatch", useProductionCatalog: true);
        AssertInvalid(
            CreateEvidence(
                dashboard: new NandKernelDashboardEvidence(
                    NandEvidenceResolution.Confirmed,
                    ImmutableArray<NandDashboardVersionRange>.Empty,
                    kernelBootloaderBuild: 17559,
                    exactDashboardVersion: 17526),
                imageFamily: directFamily),
            request,
            "DashboardMismatch",
            useProductionCatalog: true);
    }

    [Fact]
    public async Task Async_output_validation_never_accepts_table_compatibility_as_positive_family_proof()
    {
        string keyText = (await File.ReadAllTextAsync(FixtureCatalog.GetPath("keys/small-block.cpukey"))).Trim();
        var request = new XeBuildRequest(
            "synthetic-support",
            new XeBuildSourceContext("synthetic-input.bin", 0x1080000, SHA256.HashData(Array.Empty<byte>()), ConsoleId.Trinity16Mb),
            CpuKey.Parse(keyText),
            "synthetic-output.bin",
            new XeBuildBuildTarget(null, 17559, "glitch2"));

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(() =>
            XeBuildOutputValidator.ValidateAsync(FixtureCatalog.GetPath("nand/small-block.bin"), request, null, CancellationToken.None));

        AssertFailure(failure, "ImageFamilyEvidenceUnavailable");
    }

    private static XeBuildRequest CreateRequest(
        string targetType = "glitch2",
        int dashboardVersion = 17559,
        string? consoleOverride = null,
        ConsoleId? detectedConsole = ConsoleId.Trinity16Mb,
        bool rgh3 = false) =>
        new(
            "synthetic-support",
            new XeBuildSourceContext("synthetic-input.bin", 0x1080000, SHA256.HashData(Array.Empty<byte>()), detectedConsole),
            CpuKey.Parse("00112233445566778899AABBCCDDEEFF"),
            "synthetic-output.bin",
            new XeBuildBuildTarget(consoleOverride, dashboardVersion, targetType, new XeBuildBuildOptions(rgh3: rgh3)));

    private static NandSemanticEvidence CreateEvidence(
        NandConsoleEvidence? console = null,
        NandKernelDashboardEvidence? dashboard = null,
        NandImageFamilyEvidence? imageFamily = null,
        NandHackTypeEvidence? compatibilityHack = null) =>
        new(
            console ?? ConfirmedConsole(ConsoleId.Trinity16Mb),
            dashboard ?? new NandKernelDashboardEvidence(
                NandEvidenceResolution.Confirmed,
                ImmutableArray.Create(new NandDashboardVersionRange(8498, 14699)),
                kernelBootloaderBuild: 1888,
                exactDashboardVersion: 17559),
            new NandVirtualFuseEvidence(NandVirtualFuseEvidenceStatus.Absent),
            imageFamily ?? ConfirmedFamily(NandImageFamily.Glitch2),
            compatibilityHack);

    private static NandConsoleEvidence ConfirmedConsole(ConsoleId id)
    {
        ConsoleDefinition console = ConsoleCatalog.Get(id);
        return new NandConsoleEvidence(NandEvidenceResolution.Confirmed, console, ImmutableArray.Create(console));
    }

    private static NandImageFamilyEvidence ConfirmedFamily(NandImageFamily family) =>
        new(NandEvidenceResolution.Confirmed, family, ImmutableArray.Create(family), DirectEvidence(family));

    private static ImmutableArray<NandDirectOutputEvidenceProvenance> DirectEvidence(NandImageFamily family) =>
        SyntheticManifest.Fingerprints
            .Where(definition => definition.Family == family)
            .Select(definition => new NandDirectOutputEvidenceProvenance(
                NandDirectOutputEvidenceSource.ReviewedDecryptedStageFingerprint,
                SyntheticManifest.Sha256,
                definition.Id,
                definition.Stage,
                NandBootloaderDecryptionStatus.Decrypted))
            .ToImmutableArray();

    private static XeBuildOutputEvidenceCapability TestOnlyCapability(XeBuildRequest request) =>
        new(
            request.Target.HackType,
            XeBuildOutputEvidenceCatalog.Get(request.Target.HackType, request.Target.Options.Rgh3).RequiredFamily,
            SyntheticManifest);

    private static OperationFailureException AssertInvalid(
        NandSemanticEvidence evidence,
        XeBuildRequest request,
        string expectedMismatch,
        bool useProductionCatalog = false)
    {
        OperationFailureException failure = Assert.Throws<OperationFailureException>(() =>
        {
            if (useProductionCatalog)
            {
                XeBuildOutputValidator.Validate(evidence, request);
            }
            else
            {
                XeBuildOutputValidator.Validate(evidence, request, TestOnlyCapability(request));
            }
        });
        AssertFailure(failure, expectedMismatch);
        return failure;
    }

    private static void AssertFailure(OperationFailureException failure, string expectedDetail)
    {
        Assert.Equal(ExitCode.ExternalProcess, failure.Code);
        Assert.Equal(7, (int)failure.Code);
        Assert.Equal("xebuild-output-invalid", failure.Kind);
        Assert.Contains(expectedDetail, failure.Message);
    }
}
