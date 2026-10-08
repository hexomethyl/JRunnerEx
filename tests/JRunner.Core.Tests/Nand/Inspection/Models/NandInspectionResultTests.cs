using System.Collections.Immutable;
using System.Text.Json;
using JRunner.Core.Nand.Comparison;
using JRunner.Core.Nand.Hacks;
using JRunner.Core.Nand.Inspection.Models;
using JRunner.Core.Nand.Models;
using JRunner.Core.Nand.Physical;
using JRunner.Core.Nand.Security;
using Xunit;

namespace JRunner.Core.Tests.Nand.Inspection.Models;

public sealed class NandInspectionResultTests
{
    [Fact]
    public void Bootloader_stage_rejects_invalid_ranges_and_decryption_combinations()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NandBootloaderStage(
            NandBootloaderStageKind.CB_A,
            numericId: 2,
            magic: new NandMagic(0x4342, 2),
            build: 9188,
            logicalOffset: -1,
            declaredLength: 0x100,
            roundedLength: 0x100,
            decryptionStatus: NandBootloaderDecryptionStatus.NotAttempted));

        Assert.Throws<ArgumentException>(() => new NandBootloaderStage(
            NandBootloaderStageKind.CB_A,
            numericId: 2,
            magic: new NandMagic(0x4342, 2),
            build: 9188,
            logicalOffset: 0x10000,
            declaredLength: 0x100,
            roundedLength: 0x100,
            decryptionStatus: NandBootloaderDecryptionStatus.Decrypted));

        Assert.Throws<ArgumentException>(() => new NandBootloaderStage(
            NandBootloaderStageKind.SC,
            numericId: 3,
            magic: new NandMagic(0x5343, 2),
            build: 1888,
            logicalOffset: 0x10000,
            declaredLength: 0x100,
            roundedLength: 0x100,
            decryptionStatus: NandBootloaderDecryptionStatus.Decrypted,
            decryptionPath: BootloaderDecryptionPath.Cb,
            decryptionEvidence: new NandBootloaderDecryptionEvidence(
                usesNewCbCrypto: false,
                hasLegacyCdZeroRangeEvidence: false)));

        Assert.Throws<ArgumentException>(() => new NandBootloaderStage(
            NandBootloaderStageKind.CB_A,
            numericId: 2,
            magic: new NandMagic(0x4342, 2),
            build: 9188,
            logicalOffset: 0x10000,
            declaredLength: 0x100,
            roundedLength: 0x100,
            decryptionStatus: NandBootloaderDecryptionStatus.NotAttempted,
            decryptionPath: BootloaderDecryptionPath.Cb));
    }

    [Fact]
    public void Result_normalizes_stages_and_requires_header_matched_keyvault_crc_range()
    {
        NandInspectionResult result = CreateValidResult(bootloaderStages: default);

        Assert.False(result.BootloaderStages.IsDefault);
        Assert.Empty(result.BootloaderStages);

        NandKeyvaultInspection mismatchedKeyvault = new(
            new NandLogicalRange(offset: 0xC000, length: KeyvaultService.KeyvaultLength),
            crc32: 0,
            inspection: KeyvaultService.Inspect(new byte[KeyvaultService.KeyvaultLength]));

        Assert.Throws<ArgumentException>(() => CreateValidResult(keyvault: mismatchedKeyvault));
    }

    [Fact]
    public void Semantic_evidence_distinguishes_absent_facts_from_unavailable_image_family_matching()
    {
        NandInspectionResult result = CreateValidResult();

        Assert.Equal(NandEvidenceResolution.Absent, result.SemanticEvidence.Console.Resolution);
        Assert.Equal(NandEvidenceResolution.Absent, result.SemanticEvidence.KernelDashboard.Resolution);
        Assert.Null(result.SemanticEvidence.KernelDashboard.ExactDashboardVersion);
        Assert.Equal(NandVirtualFuseEvidenceStatus.Absent, result.SemanticEvidence.VirtualFuses.Status);
        Assert.Same(NandImageFamilyEvidence.Unavailable, result.SemanticEvidence.ImageFamily);
        Assert.Equal(NandEvidenceResolution.Unavailable, result.SemanticEvidence.ImageFamily.Resolution);
        Assert.Null(result.SemanticEvidence.ImageFamily.Family);
        Assert.Empty(result.SemanticEvidence.ImageFamily.Candidates);
        Assert.Empty(result.SemanticEvidence.ImageFamily.DirectEvidence);
        Assert.Equal(NandEvidenceResolution.Absent, result.SemanticEvidence.Hack.Resolution);
        Assert.Null(result.SemanticEvidence.Hack.HackType);
        Assert.Empty(result.SemanticEvidence.Hack.Candidates);
    }

    [Fact]
    public void Public_result_constructor_cannot_infer_dashboard_from_generic_update_stage_builds()
    {
        ImmutableArray<NandBootloaderStage> stages =
        [
            new(
                NandBootloaderStageKind.CF0,
                numericId: 6,
                magic: new NandMagic(0x4346, 2),
                build: 17559,
                logicalOffset: 0x10000,
                declaredLength: 0x360,
                roundedLength: 0x360,
                decryptionStatus: NandBootloaderDecryptionStatus.NotAttempted),
            new(
                NandBootloaderStageKind.CG0,
                numericId: 7,
                magic: new NandMagic(0x4347, 2),
                build: 17559,
                logicalOffset: 0x10360,
                declaredLength: 0x50,
                roundedLength: 0x50,
                decryptionStatus: NandBootloaderDecryptionStatus.NotAttempted),
        ];

        NandInspectionResult result = CreateValidResult(bootloaderStages: stages);

        Assert.Equal(NandEvidenceResolution.Absent, result.SemanticEvidence.KernelDashboard.Resolution);
        Assert.Null(result.SemanticEvidence.KernelDashboard.ExactDashboardVersion);
        Assert.False(result.SemanticEvidence.KernelDashboard.HasExactDashboardVersion);
        Assert.All(result.BootloaderStages, static stage => Assert.Equal(17559, stage.Build));
    }

    [Fact]
    public void Default_virtual_fuse_evidence_is_unavailable_and_cannot_assert_Glitch2m()
    {
        var evidence = new NandVirtualFuseEvidence(default);

        Assert.Equal(NandVirtualFuseEvidenceStatus.Unavailable, evidence.Status);
        Assert.False(evidence.IsPresent);
    }

    [Fact]
    public void Retains_legacy_Hack_JSON_diagnostics_while_adding_unavailable_direct_ImageFamily_evidence()
    {
        NandInspectionResult result = CreateValidResult(
            hackEvidence: NandHackEvidenceService.Identify(9188, null, null),
            virtualFuseStatus: NandVirtualFuseEvidenceStatus.Present);
        JsonElement semantic = JsonSerializer.SerializeToElement(result).GetProperty("SemanticEvidence");
        JsonElement compatibility = semantic.GetProperty("Hack");

        Assert.Equal((int)NandEvidenceResolution.Ambiguous, compatibility.GetProperty("Resolution").GetInt32());
        Assert.Equal(JsonValueKind.Null, compatibility.GetProperty("HackType").ValueKind);
        Assert.Equal(
            [(int)NandHackType.Glitch2, (int)NandHackType.Glitch2m],
            compatibility.GetProperty("Candidates").EnumerateArray().Select(candidate => candidate.GetInt32()).ToArray());
        Assert.False(compatibility.GetProperty("IsConfirmed").GetBoolean());
        JsonElement family = semantic.GetProperty("ImageFamily");
        Assert.Equal((int)NandEvidenceResolution.Unavailable, family.GetProperty("Resolution").GetInt32());
        Assert.Equal(JsonValueKind.Null, family.GetProperty("Family").ValueKind);
        Assert.Equal(0, family.GetProperty("Candidates").GetArrayLength());
        Assert.Equal(0, family.GetProperty("DirectEvidence").GetArrayLength());
    }

    [Fact]
    public void Public_inspection_contract_exposes_no_raw_memory_or_cpu_key()
    {
        Type[] modelTypes =
        [
            typeof(NandMagic),
            typeof(NandLogicalRange),
            typeof(NandHeaderInspection),
            typeof(NandBootloaderDecryptionEvidence),
            typeof(NandBootloaderStage),
            typeof(NandHackedSmcEvidence),
            typeof(NandSmcInspection),
            typeof(NandKeyvaultInspection),
            typeof(NandVirtualFuseEvidence),
            typeof(NandDashboardVersionRange),
            typeof(NandConsoleEvidence),
            typeof(NandKernelDashboardEvidence),
            typeof(NandHackTypeEvidence),
            typeof(NandImageFamilyEvidence),
            typeof(NandDirectOutputEvidenceProvenance),
            typeof(NandDirectOutputFingerprintDefinition),
            typeof(NandDirectOutputEvidenceManifest),
            typeof(NandSemanticEvidence),
            typeof(NandSemanticEvidenceRequirement),
            typeof(NandSemanticEvidenceMismatch),
            typeof(NandSemanticEvidenceValidationResult),
            typeof(NandInspectionResult),
        ];

        foreach (Type modelType in modelTypes)
        {
            Assert.All(modelType.GetProperties(), static property => Assert.False(property.CanWrite));
            Assert.DoesNotContain(
                modelType.GetProperties().Select(static property => property.PropertyType),
                IsUnsafeSurfaceType);
            Assert.DoesNotContain(
                modelType.GetConstructors()
                    .SelectMany(static constructor => constructor.GetParameters())
                    .Select(static parameter => parameter.ParameterType),
                IsUnsafeSurfaceType);
        }
    }

    private static NandInspectionResult CreateValidResult(
        ImmutableArray<NandBootloaderStage> bootloaderStages = default,
        NandKeyvaultInspection? keyvault = null,
        NandVirtualFuseEvidenceStatus virtualFuseStatus = NandVirtualFuseEvidenceStatus.Absent,
        NandHackEvidence? hackEvidence = null)
    {
        NandCanonicalImageSummary canonicalImage = new(
            detectedFormat: NandPhysicalFormat.Logical,
            selectedLayout: null,
            hasSpareData: false,
            rawByteLength: 0x20000,
            canonicalLogicalByteLength: 0x20000,
            physicalBlockCount: 0,
            badBlocks: ImmutableArray<NandBadBlock>.Empty,
            remaps: ImmutableArray<NandBadBlockRemap>.Empty);
        NandLogicalRange smcRange = new(offset: 0x8000, length: 0x2000);
        NandLogicalRange keyvaultRange = new(offset: 0x4000, length: KeyvaultService.KeyvaultLength);
        NandHeaderInspection header = new(
            magic: new NandMagic(0xFF4F, 2),
            firstStageLogicalOffset: 0x10000,
            smcLogicalRange: smcRange,
            keyvaultLogicalRange: keyvaultRange);
        NandSmcInspection smc = new(
            logicalRange: smcRange,
            motherboardType: null,
            version: null);
        NandKeyvaultInspection effectiveKeyvault = keyvault ?? new NandKeyvaultInspection(
            logicalRange: keyvaultRange,
            crc32: 0,
            inspection: KeyvaultService.Inspect(new byte[KeyvaultService.KeyvaultLength]));

        return new NandInspectionResult(
            canonicalImage,
            header,
            bootloaderStages,
            smc,
            effectiveKeyvault,
            ConsoleIdentifier.Identify(new ConsoleIdentificationEvidence()),
            hackEvidence ?? NandHackEvidenceService.Identify(cbABuild: null, cbBBuild: null, cbXBuild: null),
            patchInspection: null,
            new NandVirtualFuseEvidence(virtualFuseStatus));
    }

    private static bool IsUnsafeSurfaceType(Type type)
    {
        return type == typeof(byte[]) ||
            type == typeof(Memory<byte>) ||
            type == typeof(ReadOnlyMemory<byte>) ||
            type == typeof(CpuKey);
    }
}
