using System.Collections.Immutable;
using JRunner.Core.Nand.Hacks;
using JRunner.Core.Nand.Inspection;
using JRunner.Core.Nand.Inspection.Models;
using JRunner.Core.Nand.Models;
using Xunit;

namespace JRunner.Core.Tests.Nand.Inspection.Service;

public sealed class NandSemanticEvidenceServiceTests
{
    [Theory]
    [InlineData(0x4342, 9188, NandImageFamily.Retail)]
    [InlineData(0x4342, 9188, NandImageFamily.Devkit)]
    [InlineData(0x4342, 9188, NandImageFamily.Testkit)]
    [InlineData(0x5332, 10375, NandImageFamily.Retail)]
    [InlineData(0x5332, 10375, NandImageFamily.Devkit)]
    [InlineData(0x5332, 10375, NandImageFamily.Testkit)]
    [InlineData(0x4342, 0x8000 | 10375, NandImageFamily.Retail)]
    [InlineData(0x4342, 0x8000 | 10375, NandImageFamily.Devkit)]
    [InlineData(0x4342, 0x8000 | 10375, NandImageFamily.Testkit)]
    [InlineData(0x5332, 0x8000 | 10375, NandImageFamily.Retail)]
    [InlineData(0x5332, 0x8000 | 10375, NandImageFamily.Devkit)]
    [InlineData(0x5332, 0x8000 | 10375, NandImageFamily.Testkit)]
    public void Retail_devkit_and_testkit_requirements_are_not_inferred_from_builds_or_development_magic(
        int magic,
        int build,
        NandImageFamily requestedFamily)
    {
        ImmutableArray<NandBootloaderStage> stages =
        [
            new(
                NandBootloaderStageKind.CB_A,
                numericId: 2,
                magic: new NandMagic((uint)magic, 2),
                build: build,
                logicalOffset: 0x8000,
                declaredLength: 0x3C0,
                roundedLength: 0x3C0,
                decryptionStatus: NandBootloaderDecryptionStatus.NotAttempted),
        ];
        NandSemanticEvidence evidence = CreateEvidence(stages, NandHackEvidenceService.Identify(build, null, null));

        AssertUnavailable(evidence.ImageFamily);
        NandSemanticEvidenceValidationResult result = NandSemanticEvidenceValidator.Validate(
            evidence,
            new NandSemanticEvidenceRequirement(ConsoleId.Trinity16Mb, null, requestedFamily));
        Assert.False(result.IsMatch);
        Assert.Contains(result.Mismatches, mismatch => mismatch.Kind == NandSemanticEvidenceMismatchKind.ImageFamilyEvidenceUnavailable);
        Assert.DoesNotContain(result.Mismatches, mismatch => mismatch.Kind == NandSemanticEvidenceMismatchKind.ImageFamilyMismatch);
    }

    [Theory]
    [InlineData(12000, NandHackType.Rgh3, NandVirtualFuseEvidenceStatus.Absent)]
    [InlineData(12000, NandHackType.Rgh3, NandVirtualFuseEvidenceStatus.Present)]
    [InlineData(12000, NandHackType.Rgh3, NandVirtualFuseEvidenceStatus.Unavailable)]
    [InlineData(42069, NandHackType.Rgh13, NandVirtualFuseEvidenceStatus.Absent)]
    [InlineData(42069, NandHackType.Rgh13, NandVirtualFuseEvidenceStatus.Present)]
    [InlineData(42069, NandHackType.Rgh13, NandVirtualFuseEvidenceStatus.Unavailable)]
    public void CB_X_precedence_and_virtual_fuses_remain_compatibility_diagnostics_without_direct_family_proof(
        int cbXBuild,
        NandHackType legacyPreference,
        NandVirtualFuseEvidenceStatus fuseStatus)
    {
        NandHackEvidence compatibility = NandHackEvidenceService.Identify(13121, 13182, cbXBuild);
        ImmutableArray<NandBootloaderStage> stages =
        [
            new(
                NandBootloaderStageKind.CB_X,
                numericId: 2,
                magic: new NandMagic(0x4342, 2),
                build: cbXBuild,
                logicalOffset: 0x8000,
                declaredLength: 0x20,
                roundedLength: 0x20,
                decryptionStatus: NandBootloaderDecryptionStatus.NotAttempted),
        ];
        var fuses = new NandVirtualFuseEvidence(fuseStatus);

        NandSemanticEvidence evidence = CreateEvidence(stages, compatibility, fuses);

        Assert.Equal(legacyPreference, compatibility.PreferredHack);
        Assert.Equal(NandHackType.Glitch2, compatibility.TablePreferredHack);
        Assert.True(compatibility.HasCbXEvidence);
        Assert.Same(fuses, evidence.VirtualFuses);
        AssertUnavailable(evidence.ImageFamily);
        Assert.Equal(NandEvidenceResolution.Ambiguous, evidence.Hack.Resolution);
        Assert.Null(evidence.Hack.HackType);
        Assert.False(evidence.Hack.IsConfirmed);
        Assert.Equal([legacyPreference], evidence.Hack.Candidates.ToArray());
    }

    [Theory]
    [InlineData(NandVirtualFuseEvidenceStatus.Absent)]
    [InlineData(NandVirtualFuseEvidenceStatus.Present)]
    [InlineData(NandVirtualFuseEvidenceStatus.Unavailable)]
    public void Virtual_fuse_status_does_not_confirm_a_family_or_disambiguate_legacy_Glitch2_labels(NandVirtualFuseEvidenceStatus status)
    {
        NandHackEvidence compatibility = NandHackEvidenceService.Identify(9188, null, null);

        NandSemanticEvidence evidence = CreateEvidence(default, compatibility, new NandVirtualFuseEvidence(status));

        Assert.Equal(NandHackType.Glitch2, compatibility.PreferredHack);
        AssertUnavailable(evidence.ImageFamily);
        Assert.Equal(NandEvidenceResolution.Ambiguous, evidence.Hack.Resolution);
        Assert.Null(evidence.Hack.HackType);
        Assert.False(evidence.Hack.IsConfirmed);
        Assert.Equal([NandHackType.Glitch2, NandHackType.Glitch2m], evidence.Hack.Candidates.ToArray());
    }

    [Fact]
    public void Generic_CF_CG_CE_stage_builds_are_not_image_family_or_exact_dashboard_proof()
    {
        ImmutableArray<NandBootloaderStage> stages =
        [
            new(
                NandBootloaderStageKind.CE,
                numericId: 5,
                magic: new NandMagic(0x4345, 2),
                build: 17559,
                logicalOffset: 0x8000,
                declaredLength: 0x20,
                roundedLength: 0x20,
                decryptionStatus: NandBootloaderDecryptionStatus.NotAttempted),
            new(
                NandBootloaderStageKind.CF0,
                numericId: 6,
                magic: new NandMagic(0x4346, 2),
                build: 17559,
                logicalOffset: 0xA000,
                declaredLength: 0x360,
                roundedLength: 0x360,
                decryptionStatus: NandBootloaderDecryptionStatus.NotAttempted),
            new(
                NandBootloaderStageKind.CG0,
                numericId: 7,
                magic: new NandMagic(0x4347, 2),
                build: 17559,
                logicalOffset: 0xA360,
                declaredLength: 0x50,
                roundedLength: 0x50,
                decryptionStatus: NandBootloaderDecryptionStatus.NotAttempted),
        ];

        NandSemanticEvidence evidence = CreateEvidence(stages, NandHackEvidenceService.Identify(null, null, null));

        AssertUnavailable(evidence.ImageFamily);
        Assert.Equal(NandEvidenceResolution.Absent, evidence.KernelDashboard.Resolution);
        Assert.Null(evidence.KernelDashboard.ExactDashboardVersion);
        Assert.Equal(17559, evidence.KernelDashboard.KernelBootloaderBuild);
    }

    [Fact]
    public void Inspected_exact_CF_dashboard_remains_available_while_the_family_policy_is_unavailable()
    {
        NandSemanticEvidence evidence = NandSemanticEvidenceService.Create(
            default,
            ConsoleIdentifier.Identify(new ConsoleIdentificationEvidence()),
            NandHackEvidenceService.Identify(null, null, null),
            new NandVirtualFuseEvidence(NandVirtualFuseEvidenceStatus.Absent),
            new NandDashboardBuildEvidence(NandEvidenceResolution.Confirmed, 17559));

        Assert.Equal(NandEvidenceResolution.Confirmed, evidence.KernelDashboard.Resolution);
        Assert.Equal(17559, evidence.KernelDashboard.ExactDashboardVersion);
        Assert.True(evidence.KernelDashboard.HasExactDashboardVersion);
        AssertUnavailable(evidence.ImageFamily);
    }

    [Fact]
    public void No_available_inspection_facts_means_matching_unavailable_not_a_false_absent_family_observation()
    {
        NandSemanticEvidence evidence = CreateEvidence(default, NandHackEvidenceService.Identify(null, null, null));

        Assert.Equal(NandEvidenceResolution.Absent, evidence.Console.Resolution);
        Assert.Equal(NandEvidenceResolution.Absent, evidence.KernelDashboard.Resolution);
        AssertUnavailable(evidence.ImageFamily);
    }

    private static NandSemanticEvidence CreateEvidence(
        ImmutableArray<NandBootloaderStage> stages,
        NandHackEvidence compatibility,
        NandVirtualFuseEvidence? virtualFuses = null) =>
        NandSemanticEvidenceService.Create(
            stages,
            ConsoleIdentifier.Identify(new ConsoleIdentificationEvidence()),
            compatibility,
            virtualFuses ?? new NandVirtualFuseEvidence(NandVirtualFuseEvidenceStatus.Absent),
            dashboardBuild: default);

    private static void AssertUnavailable(NandImageFamilyEvidence family)
    {
        Assert.Same(NandImageFamilyEvidence.Unavailable, family);
        Assert.Equal(NandEvidenceResolution.Unavailable, family.Resolution);
        Assert.False(family.IsConfirmed);
        Assert.Null(family.Family);
        Assert.Empty(family.Candidates);
        Assert.Empty(family.DirectEvidence);
    }
}
