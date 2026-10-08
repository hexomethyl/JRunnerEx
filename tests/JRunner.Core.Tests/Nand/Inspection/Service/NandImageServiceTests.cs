using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Linq;
using System.Text.Json;
using JRunner.Core.Binary;
using JRunner.Core.Contracts;
using JRunner.Core.Nand.Conversion;
using JRunner.Core.Nand.Hacks;
using JRunner.Core.Nand.Models;
using JRunner.Core.Nand.Inspection;
using JRunner.Core.Nand.Inspection.Models;
using JRunner.Core.Nand.Physical;
using JRunner.Core.Nand.Security;
using Xunit;

namespace JRunner.Core.Tests.Nand.Inspection.Service;

public sealed class NandImageServiceTests
{
    private const int LogicalImageLength = 0xD0000;
    private const int FirstStageOffset = 0x8000;
    private const int SmcOffset = 0x1000;
    private const int SmcLength = 0x2DC0;
    private const int KeyvaultOffset = 0x4000;
    private const int PrimaryPatchOffset = 0xC0010;
    private const int LegacyPatchOffset = 0x913F0;
    private const int PatchLength = 0x4000;
    private const int CbStageLength = 0x3C0;
    private const int FirstUpdateSlotOffset = 0xA000;
    private const int UpdateSlotLength = 0x800;
    private const int CfStageLength = 0x360;
    private const int CgStageLength = 0x50;

    [Fact]
    public async Task Inspects_logical_image_from_current_position_with_safe_SMC_KV_CB_and_patch_evidence()
    {
        byte[] logical = CreateLogicalImage();
        byte[] prefixed = new byte[logical.Length + 7];
        logical.CopyTo(prefixed, 7);
        using var image = new MemoryStream(prefixed, writable: false);
        image.Position = 7;
        var progress = new RecordingProgress();

        NandInspectionResult result = await NandImageService.InspectAsync(image, progress: progress);

        Assert.True(image.CanRead);
        Assert.Equal(7, image.Position);
        Assert.Equal(NandPhysicalFormat.Logical, result.CanonicalImage.DetectedFormat);
        Assert.False(result.CanonicalImage.HasSpareData);
        Assert.Equal("0xFF4F", result.Header.Magic.Hexadecimal);
        Assert.Equal((long)FirstStageOffset, result.Header.FirstStageLogicalOffset);
        Assert.Equal((long)SmcOffset, result.Smc.LogicalOffset);
        Assert.Equal((long)SmcLength, result.Smc.Length);
        Assert.Equal((int?)6, result.Smc.MotherboardType);
        Assert.Equal("1.02", result.Smc.Version!.DisplayVersion);
        Assert.NotNull(result.Smc.HackedSmcEvidence);
        Assert.True(result.Smc.HackedSmcEvidence!.MarkerIsAllZero);
        Assert.Equal(KeyvaultCpuKeyVerificationStatus.NotRequired, result.Keyvault.Inspection.CpuKeyVerification);
        Assert.Equal(
            Crc32.Compute(logical.AsSpan(KeyvaultOffset, KeyvaultService.KeyvaultLength)),
            result.Keyvault.Crc32);

        NandBootloaderStage cbA = Assert.Single(result.BootloaderStages);
        Assert.Equal(NandBootloaderStageKind.CB_A, cbA.Kind);
        Assert.Equal(9188, cbA.Build);
        Assert.Equal(NandBootloaderDecryptionStatus.Decrypted, cbA.DecryptionStatus);
        Assert.Equal((BootloaderDecryptionPath?)BootloaderDecryptionPath.Cb, cbA.DecryptionPath);
        Assert.Equal((byte?)9, cbA.Ldv);
        Assert.Equal((uint?)0x332211u, cbA.PairingData);
        Assert.NotNull(result.PatchInspection);
        Assert.True(result.PatchInspection!.IsComplete);
        Assert.Contains(result.Patches, patch => patch.PatchName == "FuseBlow");
        Assert.Equal(NandEvidenceResolution.Conflicting, result.SemanticEvidence.Console.Resolution);
        Assert.Null(result.ConsoleIdentification.Input.RawNandLength);
        Assert.Null(result.ConsoleIdentification.Input.HasSpareData);
        Assert.Contains(progress.Events, progressEvent => progressEvent.Kind == "inspecting-nand-smc");
        Assert.Contains(progress.Events, progressEvent => progressEvent.Kind == "completed-nand-inspection");
    }

    [Fact]
    public async Task Canonicalizes_physical_ECC_input_and_preserves_physical_summary_evidence()
    {
        byte[] logical = CreateLogicalImage();
        byte[] physical = NandEccCodec.AddEcc(logical, NandPhysicalLayout.Layout1).ToArray();
        using var image = new MemoryStream(physical, writable: false);

        NandInspectionResult result = await NandImageService.InspectAsync(image);

        Assert.Equal(NandPhysicalFormat.InterleavedEcc, result.CanonicalImage.DetectedFormat);
        Assert.True(result.CanonicalImage.HasSpareData);
        Assert.Equal((NandLegacyLayout?)NandLegacyLayout.Layout1, result.CanonicalImage.SelectedLayout);
        Assert.Equal((long)physical.Length, result.CanonicalImage.RawByteLength);
        Assert.Equal((long)logical.Length, result.CanonicalImage.CanonicalLogicalByteLength);
        Assert.Empty(result.CanonicalImage.BadBlocks);
        Assert.Empty(result.CanonicalImage.Remaps);
        Assert.Null(result.SemanticEvidence.KernelDashboard.ExactDashboardVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Confirms_reviewed_RGH3_zero_key_payload_independently_of_the_supplied_CPU_key(bool supplyCpuKey)
    {
        byte[] logical = CreateRgh3LogicalImage(length: 0x1000000);
        WriteUpdateSlotMetadata(logical, slotCount: 1);
        WriteCfCgPair(logical, FirstUpdateSlotOffset, targetVersion: 17559);
        byte[] physical = NandEccCodec.AddEcc(logical, NandPhysicalLayout.Layout1).ToArray();
        using var image = new MemoryStream(physical, writable: false);
        CpuKey? cpuKey = supplyCpuKey ? CpuKey.Parse("00112233445566778899AABBCCDDEEFF") : null;

        NandInspectionResult result = await NandImageService.InspectAsync(image, cpuKey);

        AssertReviewedRgh3Evidence(result);
        NandBootloaderStage cbX = Assert.Single(result.BootloaderStages.Where(stage => stage.Kind == NandBootloaderStageKind.CB_X));
        Assert.Equal(
            supplyCpuKey ? NandBootloaderDecryptionStatus.Decrypted : NandBootloaderDecryptionStatus.NotAttempted,
            cbX.DecryptionStatus);
        Assert.Equal(NandEvidenceResolution.Ambiguous, result.SemanticEvidence.Hack.Resolution);
        Assert.Null(result.SemanticEvidence.Hack.HackType);
        Assert.Equal([NandHackType.Rgh3], result.SemanticEvidence.Hack.Candidates.ToArray());
        var requirement = new NandSemanticEvidenceRequirement(
            ConsoleId.Trinity16Mb,
            17559,
            NandImageFamily.Rgh3,
            NandRgh3OutputEvidence.ReviewedManifest);
        Assert.True(NandSemanticEvidenceValidator.Validate(result, requirement).IsMatch);
        Assert.True(NandSemanticEvidenceValidator.Validate(result.SemanticEvidence, requirement).IsMatch);
        Assert.DoesNotContain("00112233445566778899AABBCCDDEEFF", JsonSerializer.Serialize(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_signature_decrypted_only_with_the_real_user_CPU_key_cannot_prove_the_zero_key_RGH3_output_policy()
    {
        CpuKey cpuKey = CpuKey.Parse("00112233445566778899AABBCCDDEEFF");
        using var image = new MemoryStream(CreateRgh3LogicalImage(encryptWithZeroCpuKey: false), writable: false);

        NandInspectionResult result = await NandImageService.InspectAsync(image, cpuKey);

        NandBootloaderStage cbX = Assert.Single(result.BootloaderStages.Where(stage => stage.Kind == NandBootloaderStageKind.CB_X));
        Assert.Equal(NandBootloaderDecryptionStatus.Decrypted, cbX.DecryptionStatus);
        Assert.Equal(BootloaderDecryptionPath.CbWithCpuKey, cbX.DecryptionPath);
        Assert.False(cbX.DecryptionEvidence!.UsesNewCbCrypto);
        AssertImageFamilyAbsent(result);
    }

    [Theory]
    [InlineData(0x354)]
    [InlineData(0x368)]
    [InlineData(0x370)]
    [InlineData(0x37C)]
    public async Task Rejects_a_near_RGH3_signature_despite_CB_X_build_and_hacked_SMC_preferences(int changedOffset)
    {
        using var image = new MemoryStream(CreateRgh3LogicalImage(changedWordOffset: changedOffset), writable: false);

        NandInspectionResult result = await NandImageService.InspectAsync(image);

        Assert.True(result.HackEvidence.HasRgh3Evidence);
        Assert.True(result.Smc.HackedSmcEvidence!.IsHacked);
        AssertImageFamilyAbsent(result);
        NandSemanticEvidenceValidationResult validation = NandSemanticEvidenceValidator.Validate(
            result,
            new NandSemanticEvidenceRequirement(ConsoleId.Trinity16Mb, null, NandImageFamily.Rgh3, NandRgh3OutputEvidence.ReviewedManifest));
        Assert.Contains(validation.Mismatches, mismatch => mismatch.Kind == NandSemanticEvidenceMismatchKind.ImageFamilyAbsent);
    }

    [Theory]
    [InlineData(false, true, 15432, CbStageLength)]
    [InlineData(true, false, 15432, CbStageLength)]
    [InlineData(true, true, 42069, CbStageLength)]
    [InlineData(true, true, 15432, 0x37F)]
    public async Task Requires_zero_key_decryption_complete_topology_and_non_RGH13_CB_X(
        bool encryptWithZeroCpuKey,
        bool includeFinalCbB,
        int cbXBuild,
        int payloadLength)
    {
        using var image = new MemoryStream(
            CreateRgh3LogicalImage(
                encryptWithZeroCpuKey: encryptWithZeroCpuKey,
                includeFinalCbB: includeFinalCbB,
                cbXBuild: cbXBuild,
                payloadLength: payloadLength),
            writable: false);

        NandInspectionResult result = await NandImageService.InspectAsync(image);

        AssertImageFamilyAbsent(result);
    }

    [Theory]
    [InlineData(0x0001)]
    [InlineData(0x1000)]
    [InlineData(0x1001)]
    public async Task Uses_the_exact_legacy_zero_key_path_even_when_CBA_flags_select_other_generic_crypto_branches(int cbaFlags)
    {
        using var image = new MemoryStream(CreateRgh3LogicalImage(cbaFlags: cbaFlags), writable: false);

        NandInspectionResult result = await NandImageService.InspectAsync(image);

        AssertReviewedRgh3Evidence(result);
    }

    [Fact]
    public async Task Inspects_real_converter_output_as_RGH3_with_exact_console_and_CF_dashboard()
    {
        CpuKey cpuKey = CpuKey.Parse("00112233445566778899AABBCCDDEEFF");
        byte[] sourceLogical = CreateLogicalImage(length: 0x1000000, smcMotherboardType: 5);
        sourceLogical.AsSpan(FirstStageOffset, 3 * CbStageLength).Clear();
        byte[] sourceCba = CreateEncryptedCbA(9188, length: 2 * CbStageLength);
        sourceCba.CopyTo(sourceLogical, FirstStageOffset);
        byte[] sourceCbb = new byte[CbStageLength];
        WriteBootloaderHeader(sourceCbb, 0, "CB", 9188, sourceCbb.Length);
        "XBOX_ROM"u8.CopyTo(sourceCbb.AsSpan(0x392, 8));
        ReadOnlyMemory<byte> decodedCba = BootloaderCrypto.DecryptCb(sourceCba).Output;
        byte[] encryptedCbb = EncryptLegacyCbPayload(sourceCbb, decodedCba.Span, cpuKey);
        encryptedCbb.CopyTo(sourceLogical, FirstStageOffset + sourceCba.Length);
        WriteUpdateSlotMetadata(sourceLogical, slotCount: 1);
        WriteCfCgPair(sourceLogical, FirstUpdateSlotOffset, targetVersion: 17559);
        ReadOnlySpan<byte> xellSignature =
        [
            0x48, 0x00, 0x00, 0x20, 0x48, 0x00, 0x00, 0xEC,
            0x48, 0x00, 0x00, 0x00, 0x48, 0x00, 0x00, 0x00,
        ];
        xellSignature.CopyTo(sourceLogical.AsSpan(0x70000));
        byte[] sourcePhysical = NandEccCodec.AddEcc(sourceLogical, NandPhysicalLayout.Layout1).ToArray();
        byte[] templateLogical = CreateRgh3LogicalImage(length: 0x140000, includeFinalCbB: false, firstWord: 0x646A0002);
        using var template = new MemoryStream(templateLogical, writable: false);
        using var source = new MemoryStream(sourcePhysical, writable: false);
        using var output = new MemoryStream();

        Rgh2ToRgh3ConversionResult conversion = await Rgh2ToRgh3ConversionService.ConvertAsync(
            new Rgh2ToRgh3ConversionRequest(template, source, output, cpuKey));
        output.Position = 0;
        NandInspectionResult inspection = await NandImageService.InspectAsync(output);

        Assert.True(conversion.Rgh3PayloadPatched);
        AssertReviewedRgh3Evidence(inspection);
        var requirement = new NandSemanticEvidenceRequirement(
            ConsoleId.Trinity16Mb,
            17559,
            NandImageFamily.Rgh3,
            NandRgh3OutputEvidence.ReviewedManifest);
        Assert.True(NandSemanticEvidenceValidator.Validate(inspection, requirement).IsMatch);
        CryptographicOperations.ZeroMemory(sourceCba);
        CryptographicOperations.ZeroMemory(sourceCbb);
        CryptographicOperations.ZeroMemory(encryptedCbb);
        CryptographicOperations.ZeroMemory(MemoryMarshal.AsMemory(decodedCba).Span);
    }

    [Theory]
    [InlineData(1888, NandHackType.Jtag)]
    [InlineData(4571, NandHackType.Glitch)]
    [InlineData(9188, NandHackType.Glitch2)]
    [InlineData(10375, NandHackType.DevGl)]
    public async Task CB_compatibility_and_decryption_do_not_prove_the_output_family(int cbBuild, NandHackType legacyPreference)
    {
        using var image = new MemoryStream(CreateLogicalImage(cbABuild: cbBuild, hackedSmc: true), writable: false);

        NandInspectionResult result = await NandImageService.InspectAsync(image);

        NandBootloaderStage cb = Assert.Single(result.BootloaderStages);
        Assert.Equal(cbBuild, cb.Build);
        Assert.Equal(NandBootloaderDecryptionStatus.Decrypted, cb.DecryptionStatus);
        Assert.Equal(legacyPreference, result.HackEvidence.PreferredHack);
        Assert.True(result.Smc.HackedSmcEvidence!.IsHacked);
        AssertImageFamilyAbsent(result);
    }

    [Theory]
    [InlineData(0xFF4F, 14699, 9188)]
    [InlineData(0x0F4F, 14699, 10375)]
    [InlineData(0x0F3F, 0x8000 | 17559, 0x8000 | 10375)]
    public async Task Development_header_magic_and_high_bit_builds_do_not_prove_retail_devkit_or_testkit(
        int headerMagic,
        int headerBuild,
        int cbBuild)
    {
        byte[] logical = CreateLogicalImage(cbABuild: cbBuild, headerBuild: headerBuild);
        BinaryPrimitives.WriteUInt16BigEndian(logical.AsSpan(0, sizeof(ushort)), checked((ushort)headerMagic));
        using var image = new MemoryStream(logical, writable: false);

        NandInspectionResult result = await NandImageService.InspectAsync(image);

        Assert.Equal((uint)headerMagic, result.Header.Magic.Value);
        Assert.Equal(headerBuild, result.Header.Build);
        Assert.Equal(cbBuild, Assert.Single(result.BootloaderStages).Build);
        AssertImageFamilyAbsent(result);
        foreach (NandImageFamily family in new[] { NandImageFamily.Retail, NandImageFamily.Devkit, NandImageFamily.Testkit })
        {
            NandSemanticEvidenceValidationResult validation = NandSemanticEvidenceValidator.Validate(
                result,
                new NandSemanticEvidenceRequirement(ConsoleId.Trinity16Mb, null, family));
            Assert.False(validation.IsMatch);
            Assert.Contains(validation.Mismatches, mismatch => mismatch.Kind == NandSemanticEvidenceMismatchKind.ImageFamilyEvidenceUnavailable);
        }
    }

    [Fact]
    public async Task Derives_confirmed_console_and_dashboard_compatibility_without_image_family_proof_from_synthetic_ECC_input()
    {
        byte[] logical = CreateLogicalImage(
            length: 0x1000000,
            smcMotherboardType: 5,
            ceBuild: 17559);
        byte[] physical = NandEccCodec.AddEcc(logical, NandPhysicalLayout.Layout1).ToArray();
        using var image = new MemoryStream(physical, writable: false);

        NandInspectionResult result = await NandImageService.InspectAsync(image);

        Assert.Equal(NandEvidenceResolution.Confirmed, result.SemanticEvidence.Console.Resolution);
        Assert.Equal(ConsoleId.Trinity16Mb, result.SemanticEvidence.Console.Console!.Id);
        Assert.Equal(NandEvidenceResolution.Confirmed, result.SemanticEvidence.KernelDashboard.Resolution);
        Assert.Equal(
            new NandDashboardVersionRange(8498, 14699),
            Assert.Single(result.SemanticEvidence.KernelDashboard.CompatibleVersionRanges));
        Assert.Null(result.SemanticEvidence.KernelDashboard.ExactDashboardVersion);
        Assert.Equal(17559, result.SemanticEvidence.KernelDashboard.KernelBootloaderBuild);
        Assert.Equal(NandVirtualFuseEvidenceStatus.Absent, result.SemanticEvidence.VirtualFuses.Status);
        Assert.Equal(NandHackType.Glitch2, result.HackEvidence.PreferredHack);
        AssertImageFamilyAbsent(result);

        NandSemanticEvidenceValidationResult compatibleValidation = NandSemanticEvidenceValidator.Validate(
            result,
            new NandSemanticEvidenceRequirement(
                ConsoleId.Trinity16Mb,
                dashboardVersion: null,
                imageFamily: NandImageFamily.Glitch2));
        Assert.False(compatibleValidation.IsMatch);
        Assert.Equal(
            NandSemanticEvidenceMismatchKind.ImageFamilyEvidenceUnavailable,
            Assert.Single(compatibleValidation.Mismatches).Kind);

        NandSemanticEvidenceValidationResult exactDashboardValidation = NandSemanticEvidenceValidator.Validate(
            result,
            new NandSemanticEvidenceRequirement(
                ConsoleId.Trinity16Mb,
                dashboardVersion: 14699,
                imageFamily: NandImageFamily.Glitch2));
        Assert.False(exactDashboardValidation.IsMatch);
        Assert.Equal(
            [
                NandSemanticEvidenceMismatchKind.DashboardNotExact,
                NandSemanticEvidenceMismatchKind.ImageFamilyEvidenceUnavailable,
            ],
            exactDashboardValidation.Mismatches.Select(mismatch => mismatch.Kind).ToArray());
    }

    [Fact]
    public async Task Leaves_dashboard_evidence_absent_without_guessing_a_family_when_the_CB_has_no_legacy_mapping()
    {
        byte[] logical = CreateLogicalImage(
            length: 0x1000000,
            cbABuild: 12345,
            smcMotherboardType: 5);
        byte[] physical = NandEccCodec.AddEcc(logical, NandPhysicalLayout.Layout1).ToArray();
        using var image = new MemoryStream(physical, writable: false);

        NandInspectionResult result = await NandImageService.InspectAsync(image);

        Assert.Equal(NandEvidenceResolution.Confirmed, result.SemanticEvidence.Console.Resolution);
        Assert.Equal(ConsoleId.Trinity16Mb, result.SemanticEvidence.Console.Console!.Id);
        Assert.Equal(NandEvidenceResolution.Absent, result.SemanticEvidence.KernelDashboard.Resolution);
        AssertImageFamilyAbsent(result);

        NandSemanticEvidenceValidationResult validation = NandSemanticEvidenceValidator.Validate(
            result,
            new NandSemanticEvidenceRequirement(
                ConsoleId.Trinity16Mb,
                dashboardVersion: 14699,
                imageFamily: NandImageFamily.Glitch2));
        Assert.Equal(
            [
                NandSemanticEvidenceMismatchKind.DashboardAbsent,
                NandSemanticEvidenceMismatchKind.ImageFamilyEvidenceUnavailable,
            ],
            validation.Mismatches.Select(mismatch => mismatch.Kind).ToArray());
    }

    [Fact]
    public async Task Marks_conflicting_console_and_dashboard_evidence_without_selecting_a_winner()
    {
        byte[] logical = CreateLogicalImage(length: 0x1000000, smcMotherboardType: 3);
        byte[] physical = NandEccCodec.AddEcc(logical, NandPhysicalLayout.Layout1).ToArray();
        using var image = new MemoryStream(physical, writable: false);

        NandInspectionResult result = await NandImageService.InspectAsync(image);

        Assert.Equal(NandEvidenceResolution.Conflicting, result.SemanticEvidence.Console.Resolution);
        Assert.Null(result.SemanticEvidence.Console.Console);
        Assert.Empty(result.SemanticEvidence.Console.Candidates);
        Assert.Equal(NandEvidenceResolution.Absent, result.SemanticEvidence.KernelDashboard.Resolution);
        Assert.Null(result.SemanticEvidence.KernelDashboard.ExactDashboardVersion);
        AssertImageFamilyAbsent(result);

        NandSemanticEvidenceValidationResult validation = NandSemanticEvidenceValidator.Validate(
            result,
            new NandSemanticEvidenceRequirement(
                ConsoleId.Trinity16Mb,
                dashboardVersion: null,
                imageFamily: NandImageFamily.Glitch2));
        Assert.Equal(
            [
                NandSemanticEvidenceMismatchKind.ConsoleConflicting,
                NandSemanticEvidenceMismatchKind.ImageFamilyEvidenceUnavailable,
            ],
            validation.Mismatches.Select(mismatch => mismatch.Kind).ToArray());
    }

    [Fact]
    public async Task Retains_ambiguous_console_and_dashboard_compatibility_evidence_without_selecting_a_mapping()
    {
        byte[] logical = CreateLogicalImage(
            length: 0x1000000,
            cbABuild: 10375,
            smcMotherboardType: 0);
        byte[] physical = NandEccCodec.AddEcc(logical, NandPhysicalLayout.Layout1).ToArray();
        using var image = new MemoryStream(physical, writable: false);

        NandInspectionResult result = await NandImageService.InspectAsync(image);

        Assert.Equal(NandEvidenceResolution.Ambiguous, result.SemanticEvidence.Console.Resolution);
        Assert.Equal(
            [
                ConsoleId.Trinity16Mb,
                ConsoleId.Jasper16Mb,
                ConsoleId.Corona16Mb,
                ConsoleId.Winchester16Mb,
            ],
            result.SemanticEvidence.Console.Candidates.Select(candidate => candidate.Id).ToArray());
        Assert.Equal(NandEvidenceResolution.Ambiguous, result.SemanticEvidence.KernelDashboard.Resolution);
        Assert.Equal(
            [
                new NandDashboardVersionRange(4532, 17559),
                new NandDashboardVersionRange(8498, 17559),
            ],
            result.SemanticEvidence.KernelDashboard.CompatibleVersionRanges.ToArray());
        Assert.Equal(NandHackType.DevGl, result.HackEvidence.PreferredHack);
        AssertImageFamilyAbsent(result);
    }

    [Fact]
    public async Task Records_the_legacy_virtual_fuse_marker_without_proving_Glitch2m_or_exposing_a_virtual_CPU_key()
    {
        byte[] logical = CreateLogicalImage(
            length: 0x1000000,
            smcMotherboardType: 5,
            includeVirtualFuseMarker: true);
        byte[] physical = NandEccCodec.AddEcc(logical, NandPhysicalLayout.Layout1).ToArray();
        using var image = new MemoryStream(physical, writable: false);

        NandInspectionResult result = await NandImageService.InspectAsync(image);

        Assert.Equal(NandVirtualFuseEvidenceStatus.Present, result.SemanticEvidence.VirtualFuses.Status);
        Assert.Equal(NandHackType.Glitch2, result.HackEvidence.PreferredHack);
        AssertImageFamilyAbsent(result);
        string serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("00112233445566778899AABBCCDDEEFF", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Records_the_legacy_JTAG_virtual_fuse_fallback_without_image_family_proof()
    {
        const int legacyJtagVirtualFuseLogicalOffset = 0x95000;
        byte[] logical = CreateLogicalImage(length: 0x1000000, smcMotherboardType: 5);
        logical.AsSpan(legacyJtagVirtualFuseLogicalOffset, 8).Fill(byte.MaxValue);
        logical[legacyJtagVirtualFuseLogicalOffset] = 0xC0;
        byte[] physical = NandEccCodec.AddEcc(logical, NandPhysicalLayout.Layout1).ToArray();
        using var image = new MemoryStream(physical, writable: false);

        NandInspectionResult result = await NandImageService.InspectAsync(image);

        Assert.Equal(NandVirtualFuseEvidenceStatus.Present, result.SemanticEvidence.VirtualFuses.Status);
        AssertImageFamilyAbsent(result);
    }

    [Fact]
    public async Task Does_not_guess_a_family_when_legacy_virtual_fuse_locations_are_unavailable()
    {
        byte[] logical = CreateLogicalImage(
            length: 0x1000000,
            smcMotherboardType: 5,
            virtualFuseLocationUnavailable: true);
        byte[] physical = NandEccCodec.AddEcc(logical, NandPhysicalLayout.Layout1).ToArray();
        using var image = new MemoryStream(physical, writable: false);

        NandInspectionResult result = await NandImageService.InspectAsync(image);

        Assert.Equal(NandVirtualFuseEvidenceStatus.Unavailable, result.SemanticEvidence.VirtualFuses.Status);
        AssertImageFamilyAbsent(result);

        NandSemanticEvidenceValidationResult validation = NandSemanticEvidenceValidator.Validate(
            result,
            new NandSemanticEvidenceRequirement(
                ConsoleId.Trinity16Mb,
                dashboardVersion: null,
                imageFamily: NandImageFamily.Glitch2));
        Assert.Equal(
            NandSemanticEvidenceMismatchKind.ImageFamilyEvidenceUnavailable,
            Assert.Single(validation.Mismatches).Kind);
    }

    [Fact]
    public async Task Rejects_wrong_parsed_CPU_key_with_typed_keyvault_verification_failure()
    {
        CpuKey correctKey = CpuKey.Parse("00112233445566778899AABBCCDDEEFF");
        CpuKey wrongKey = CpuKey.Parse("FFEEDDCCBBAA99887766554433221100");
        using var image = new MemoryStream(CreateLogicalImage(keyvaultCpuKey: correctKey), writable: false);

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => NandImageService.InspectAsync(image, wrongKey));

        Assert.Equal(ExitCode.InvalidData, failure.Code);
        Assert.Equal("cpu-key-verification-failed", failure.Kind);
    }

    [Fact]
    public async Task Rejects_malformed_header_and_invalid_supplied_CPU_key_without_exposing_key_data()
    {
        byte[] malformed = CreateLogicalImage();
        malformed[0] = 0;
        malformed[1] = 0;
        using var malformedImage = new MemoryStream(malformed, writable: false);
        OperationFailureException headerFailure = await Assert.ThrowsAsync<OperationFailureException>(
            () => NandImageService.InspectAsync(malformedImage));

        Assert.Equal("invalid-nand-header", headerFailure.Kind);

        using var validImage = new MemoryStream(CreateLogicalImage(), writable: false);
        OperationFailureException keyFailure = await Assert.ThrowsAsync<OperationFailureException>(
            () => NandImageService.InspectAsync(validImage, default(CpuKey)));

        Assert.Equal("invalid-cpu-key", keyFailure.Kind);
        Assert.DoesNotContain("00112233445566778899AABBCCDDEEFF", keyFailure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejects_truncated_SMC_bootloader_and_required_patch_ranges_with_stable_kinds()
    {
        byte[] invalidSmc = CreateLogicalImage();
        WriteUInt32BigEndian(invalidSmc, 0x78, checked((uint)LogicalImageLength));
        using var invalidSmcImage = new MemoryStream(invalidSmc, writable: false);
        OperationFailureException smcFailure = await Assert.ThrowsAsync<OperationFailureException>(
            () => NandImageService.InspectAsync(invalidSmcImage));
        Assert.Equal("invalid-smc-range", smcFailure.Kind);

        byte[] truncatedBootloader = CreateLogicalImage();
        WriteUInt32BigEndian(truncatedBootloader, FirstStageOffset + 0x0C, checked((uint)LogicalImageLength));
        using var truncatedBootloaderImage = new MemoryStream(truncatedBootloader, writable: false);
        OperationFailureException bootloaderFailure = await Assert.ThrowsAsync<OperationFailureException>(
            () => NandImageService.InspectAsync(truncatedBootloaderImage));
        Assert.Equal("truncated-bootloader", bootloaderFailure.Kind);

        using var truncatedPatchImage = new MemoryStream(CreateLogicalImage(length: 0xC1000, includePrimaryPatch: false), writable: false);
        OperationFailureException patchFailure = await Assert.ThrowsAsync<OperationFailureException>(
            () => NandImageService.InspectAsync(truncatedPatchImage));
        Assert.Equal("truncated-patch-section", patchFailure.Kind);
    }

    [Fact]
    public async Task Rejects_bootloader_declared_length_smaller_than_its_header()
    {
        byte[] logical = CreateLogicalImage();
        WriteUInt32BigEndian(logical, FirstStageOffset + 0x0C, 8);
        using var image = new MemoryStream(logical, writable: false);

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => NandImageService.InspectAsync(image));

        Assert.Equal("invalid-stage-length", failure.Kind);
    }

    [Fact]
    public async Task Accepts_an_erased_main_bootloader_chain_terminator()
    {
        byte[] logical = CreateLogicalImage();
        logical.AsSpan(FirstStageOffset + CbStageLength, 0x10).Fill(byte.MaxValue);
        using var image = new MemoryStream(logical, writable: false);

        NandInspectionResult result = await NandImageService.InspectAsync(image);

        Assert.Single(result.BootloaderStages);
        Assert.Equal(NandBootloaderStageKind.CB_A, result.BootloaderStages[0].Kind);
    }

    [Fact]
    public async Task Uses_a_complete_legacy_patch_fallback_only_after_primary_patch_diagnostics_are_malformed()
    {
        byte[] logical = CreateLogicalImage(includePrimaryPatch: false);
        WriteUInt32BigEndian(logical, PrimaryPatchOffset, 0x00100000);
        WriteUInt32BigEndian(logical, PrimaryPatchOffset + sizeof(uint), 0x1001);
        WritePatchSection(logical, LegacyPatchOffset, 0x000E3A7C, 1, 0x3CE02000);
        using var image = new MemoryStream(logical, writable: false);

        NandInspectionResult result = await NandImageService.InspectAsync(image);

        Assert.NotNull(result.PatchInspection);
        Assert.True(result.PatchInspection!.IsComplete);
        Assert.Contains(result.Patches, patch => patch.PatchName == "XLUSB");
    }

    [Fact]
    public async Task Records_RGH3_CB_X_plaintext_CB_B_and_console_evidence_without_a_CPU_key()
    {
        byte[] logical = CreateLogicalImage(cbABuild: 13121, hackedSmc: true);
        int cbXOffset = FirstStageOffset + CbStageLength;
        WriteBootloaderHeader(logical, cbXOffset, "CB", build: 12000, declaredLength: 0x20);
        int cbBOffset = cbXOffset + 0x20;
        WriteBootloaderHeader(logical, cbBOffset, "CB", build: 13182, declaredLength: CbStageLength);
        logical[cbBOffset + 0x20] = 0x44;
        logical[cbBOffset + 0x21] = 0x55;
        logical[cbBOffset + 0x22] = 0x66;
        logical[cbBOffset + 0x3B1] = 7;
        using var image = new MemoryStream(logical, writable: false);

        NandInspectionResult result = await NandImageService.InspectAsync(image);

        NandBootloaderStage cbX = Assert.Single(
            result.BootloaderStages.Where(stage => stage.Kind == NandBootloaderStageKind.CB_X));
        NandBootloaderStage cbB = Assert.Single(
            result.BootloaderStages.Where(stage => stage.Kind == NandBootloaderStageKind.CB_B));
        Assert.Equal(12000, cbX.Build);
        Assert.Equal(NandBootloaderDecryptionStatus.NotAttempted, cbX.DecryptionStatus);
        Assert.Equal(NandBootloaderDecryptionStatus.NotRequired, cbB.DecryptionStatus);
        Assert.Equal((byte?)7, cbB.Ldv);
        Assert.Equal((uint?)0x665544u, cbB.PairingData);
        Assert.True(result.Smc.HackedSmcEvidence!.IsHacked);
        Assert.True(result.HackEvidence.HasRgh3Evidence);
        Assert.True(result.HackEvidence.HasWinbondEvidence);
        Assert.Equal(NandHackType.Rgh3, result.HackEvidence.PreferredHack);
        Assert.Equal((int?)13182, result.ConsoleIdentification.Input.CbBuild);
        Assert.Equal((int?)13182, result.ConsoleIdentification.Input.CbBBuild);
        AssertImageFamilyAbsent(result);
    }

    [Fact]
    public async Task Includes_two_complete_declared_CF_CG_slots_with_independent_generic_builds()
    {
        byte[] logical = CreateLogicalImage(cbABuild: 12345);
        WriteUpdateSlotMetadata(logical, slotCount: 2);
        WriteCfCgPair(logical, FirstUpdateSlotOffset, targetVersion: 17559, cfBuild: 1888, cgBuild: 14699);
        WriteCfCgPair(logical, FirstUpdateSlotOffset + UpdateSlotLength, targetVersion: 17559, cfBuild: 14699, cgBuild: 1888);
        using var image = new MemoryStream(logical, writable: false);

        NandInspectionResult result = await NandImageService.InspectAsync(image);

        Assert.Collection(
            result.BootloaderStages.Where(stage => stage.NumericId is 6 or 7),
            stage => Assert.Equal((NandBootloaderStageKind.CF0, 1888, (long)FirstUpdateSlotOffset), (stage.Kind, stage.Build, stage.LogicalOffset)),
            stage => Assert.Equal((NandBootloaderStageKind.CG0, 14699, (long)(FirstUpdateSlotOffset + CfStageLength)), (stage.Kind, stage.Build, stage.LogicalOffset)),
            stage => Assert.Equal((NandBootloaderStageKind.CF1, 14699, (long)(FirstUpdateSlotOffset + UpdateSlotLength)), (stage.Kind, stage.Build, stage.LogicalOffset)),
            stage => Assert.Equal((NandBootloaderStageKind.CG1, 1888, (long)(FirstUpdateSlotOffset + UpdateSlotLength + CfStageLength)), (stage.Kind, stage.Build, stage.LogicalOffset)));
        Assert.Equal(NandEvidenceResolution.Confirmed, result.SemanticEvidence.KernelDashboard.Resolution);
        Assert.Equal(17559, result.SemanticEvidence.KernelDashboard.ExactDashboardVersion);
        Assert.Equal(14699, result.Header.Build);
    }

    [Theory]
    [InlineData(0x360, 14699)]
    [InlineData(0x360, 1888)]
    [InlineData(0x354, 14699)]
    [InlineData(0x354, 1888)]
    public async Task Confirms_exact_dashboard_from_CF_target_in_a_complete_declared_slot(int cfDeclaredLength, int headerBuild)
    {
        byte[] logical = CreateLogicalImage(cbABuild: 12345, headerBuild: headerBuild);
        WriteUpdateSlotMetadata(logical, slotCount: 1, slotLength: CfStageLength + CgStageLength);
        WriteCfCgPair(logical, FirstUpdateSlotOffset, targetVersion: 17559, cfBuild: 1888, cgBuild: 14699, cfDeclaredLength: cfDeclaredLength);
        using var image = new MemoryStream(logical, writable: false);

        NandInspectionResult result = await NandImageService.InspectAsync(image);

        NandBootloaderStage cf = Assert.Single(result.BootloaderStages.Where(stage => stage.Kind == NandBootloaderStageKind.CF0));
        NandBootloaderStage cg = Assert.Single(result.BootloaderStages.Where(stage => stage.Kind == NandBootloaderStageKind.CG0));
        Assert.Equal(1888, cf.Build);
        Assert.Equal((long)cfDeclaredLength, cf.DeclaredLength);
        Assert.Equal((long)CfStageLength, cf.RoundedLength);
        Assert.Equal(14699, cg.Build);
        Assert.Equal((long)(FirstUpdateSlotOffset + CfStageLength), cg.LogicalOffset);
        Assert.Equal((long)CgStageLength, cg.DeclaredLength);
        Assert.Equal(NandEvidenceResolution.Confirmed, result.SemanticEvidence.KernelDashboard.Resolution);
        Assert.Equal(17559, result.SemanticEvidence.KernelDashboard.ExactDashboardVersion);
        Assert.Equal(headerBuild, result.Header.Build);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(17558)]
    [InlineData(ushort.MaxValue)]
    public async Task Reads_CF_target_independently_from_target_flags(int targetFlags)
    {
        byte[] logical = CreateLogicalImage(cbABuild: 12345);
        WriteUpdateSlotMetadata(logical, slotCount: 1);
        WriteCfCgPair(logical, FirstUpdateSlotOffset, targetVersion: 17559, targetFlags: checked((ushort)targetFlags));
        using var image = new MemoryStream(logical, writable: false);

        NandInspectionResult result = await NandImageService.InspectAsync(image);

        Assert.Equal(NandEvidenceResolution.Confirmed, result.SemanticEvidence.KernelDashboard.Resolution);
        Assert.Equal(17559, result.SemanticEvidence.KernelDashboard.ExactDashboardVersion);
    }

    [Theory]
    [InlineData("CF", true)]
    [InlineData("CG", true)]
    [InlineData("CF", false)]
    [InlineData("CG", false)]
    public async Task Leaves_exact_dashboard_absent_when_any_active_slot_has_missing_or_incomplete_CF_or_CG(string stageMagic, bool missing)
    {
        byte[] logical = CreateLogicalImage(cbABuild: 12345);
        WriteUpdateSlotMetadata(logical, slotCount: 2);
        WriteCfCgPair(logical, FirstUpdateSlotOffset);
        int secondCfOffset = FirstUpdateSlotOffset + UpdateSlotLength;
        WriteCfCgPair(logical, secondCfOffset);
        int damagedStageOffset = stageMagic == "CF" ? secondCfOffset : secondCfOffset + CfStageLength;
        int damagedStageLength = stageMagic == "CF" ? CfStageLength : CgStageLength;
        if (missing)
        {
            logical.AsSpan(damagedStageOffset, damagedStageLength).Clear();
        }
        else
        {
            uint shortHeaderLength = stageMagic == "CF" ? 0x353u : 0x4Fu;
            WriteUInt32BigEndian(logical, damagedStageOffset + 0x0C, shortHeaderLength);
            if (stageMagic == "CG")
            {
                WriteUInt32BigEndian(logical, secondCfOffset + 0x1C, shortHeaderLength);
            }
        }
        using var image = new MemoryStream(logical, writable: false);

        NandInspectionResult result = await NandImageService.InspectAsync(image);

        Assert.Equal(NandEvidenceResolution.Absent, result.SemanticEvidence.KernelDashboard.Resolution);
        Assert.Null(result.SemanticEvidence.KernelDashboard.ExactDashboardVersion);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(ushort.MaxValue)]
    public async Task Leaves_exact_dashboard_absent_for_reserved_CF_targets_even_when_generic_builds_match(int targetVersion)
    {
        byte[] logical = CreateLogicalImage(cbABuild: 12345, headerBuild: 17559);
        WriteUpdateSlotMetadata(logical, slotCount: 1);
        WriteCfCgPair(logical, FirstUpdateSlotOffset, targetVersion: targetVersion, cfBuild: 17559, cgBuild: 17559, targetFlags: 17559);
        using var image = new MemoryStream(logical, writable: false);

        NandInspectionResult result = await NandImageService.InspectAsync(image);

        Assert.Equal(NandEvidenceResolution.Absent, result.SemanticEvidence.KernelDashboard.Resolution);
        Assert.Null(result.SemanticEvidence.KernelDashboard.ExactDashboardVersion);
        Assert.Equal(17559, result.Header.Build);
    }

    [Theory]
    [InlineData(0, 0x50, 0x50, 0x360, UpdateSlotLength)]
    [InlineData(0x360, 0, 0, 0x360, UpdateSlotLength)]
    [InlineData(0x360, 0x60, 0x50, 0x360, UpdateSlotLength)]
    [InlineData(0x360, 0x50, 0x50, 0x370, UpdateSlotLength)]
    [InlineData(0x354, 0x50, 0x50, 0x354, UpdateSlotLength)]
    [InlineData(0x360, 0x50, 0x50, 0x360, 0x3AF)]
    [InlineData(0x360, 0x51, 0x51, 0x360, 0x3B1)]
    [InlineData(0x1000, 0x50, 0x50, 0x360, UpdateSlotLength)]
    [InlineData(0x360, 0x800, 0x800, 0x360, UpdateSlotLength)]
    public async Task Rejects_malformed_or_out_of_slot_CF_CG_pairs_as_exact_evidence(
        int cfDeclaredLength,
        int cfCgLength,
        int cgDeclaredLength,
        int cgRelativeOffset,
        int slotLength)
    {
        byte[] logical = CreateLogicalImage(cbABuild: 12345);
        WriteUpdateSlotMetadata(logical, slotCount: 1, slotLength: checked((uint)slotLength));
        WriteCfCgPair(logical, FirstUpdateSlotOffset);
        logical.AsSpan(FirstUpdateSlotOffset + CfStageLength, CgStageLength).Clear();
        WriteUInt32BigEndian(logical, FirstUpdateSlotOffset + 0x0C, checked((uint)cfDeclaredLength));
        WriteUInt32BigEndian(logical, FirstUpdateSlotOffset + 0x1C, checked((uint)cfCgLength));
        WriteBootloaderHeader(logical, FirstUpdateSlotOffset + cgRelativeOffset, "CG", build: 14699, declaredLength: cgDeclaredLength);
        using var image = new MemoryStream(logical, writable: false);

        NandInspectionResult result = await NandImageService.InspectAsync(image);

        Assert.Equal(NandEvidenceResolution.Absent, result.SemanticEvidence.KernelDashboard.Resolution);
        Assert.Null(result.SemanticEvidence.KernelDashboard.ExactDashboardVersion);
    }

    [Theory]
    [InlineData(0u, 0, 0u)]
    [InlineData(0u, 1, UpdateSlotLength)]
    [InlineData(FirstUpdateSlotOffset, 0, UpdateSlotLength)]
    [InlineData(FirstUpdateSlotOffset, 1, 0u)]
    [InlineData(FirstUpdateSlotOffset, 1, 4u)]
    [InlineData(LogicalImageLength, 1, UpdateSlotLength)]
    [InlineData(LogicalImageLength - UpdateSlotLength, 2, UpdateSlotLength)]
    [InlineData(LogicalImageLength - CfStageLength, 1, CfStageLength + CgStageLength)]
    [InlineData(uint.MaxValue, 1, UpdateSlotLength)]
    [InlineData(FirstUpdateSlotOffset, ushort.MaxValue, uint.MaxValue)]
    public async Task Leaves_exact_dashboard_absent_without_valid_declared_slot_metadata(uint firstSlotOffset, int slotCount, uint slotLength)
    {
        byte[] logical = CreateLogicalImage(cbABuild: 12345);
        WriteCfCgPair(logical, FirstUpdateSlotOffset);
        if (firstSlotOffset > 0 && firstSlotOffset <= logical.Length - CfStageLength - CgStageLength)
        {
            WriteCfCgPair(logical, checked((int)firstSlotOffset));
        }
        WriteUpdateSlotMetadata(logical, slotCount, firstSlotOffset, slotLength);
        using var image = new MemoryStream(logical, writable: false);

        NandInspectionResult result = await NandImageService.InspectAsync(image);

        Assert.Equal(NandEvidenceResolution.Absent, result.SemanticEvidence.KernelDashboard.Resolution);
        Assert.Null(result.SemanticEvidence.KernelDashboard.ExactDashboardVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Does_not_treat_header_build_or_an_undeclared_update_pointer_as_exact_dashboard_evidence(bool includeUndeclaredPair)
    {
        byte[] logical = CreateLogicalImage(cbABuild: 12345, headerBuild: 17559);
        if (includeUndeclaredPair)
        {
            WriteUInt32BigEndian(logical, 0x0C, FirstUpdateSlotOffset);
            WriteCfCgPair(logical, FirstUpdateSlotOffset, targetVersion: 17559, cfBuild: 17559, cgBuild: 17559);
        }
        using var image = new MemoryStream(logical, writable: false);

        NandInspectionResult result = await NandImageService.InspectAsync(image);

        Assert.Equal(17559, result.Header.Build);
        Assert.DoesNotContain(result.BootloaderStages, stage => stage.NumericId is 6 or 7);
        Assert.Equal(NandEvidenceResolution.Absent, result.SemanticEvidence.KernelDashboard.Resolution);
        Assert.Null(result.SemanticEvidence.KernelDashboard.ExactDashboardVersion);
    }

    [Theory]
    [InlineData(0x10)]
    [InlineData(0x100)]
    [InlineData(0x36F)]
    [InlineData(0x3AF)]
    public async Task Leaves_exact_dashboard_absent_for_CF_or_CG_truncated_at_the_image_boundary(int availablePairBytes)
    {
        byte[] logical = CreateLogicalImage(cbABuild: 12345);
        var pair = new byte[CfStageLength + CgStageLength];
        WriteCfCgPair(pair, 0);
        int truncatedSlotOffset = logical.Length - availablePairBytes;
        pair.AsSpan(0, availablePairBytes).CopyTo(logical.AsSpan(truncatedSlotOffset, availablePairBytes));
        WriteUpdateSlotMetadata(
            logical,
            slotCount: 1,
            firstSlotOffset: checked((uint)truncatedSlotOffset),
            slotLength: CfStageLength + CgStageLength);
        using var image = new MemoryStream(logical, writable: false);

        NandInspectionResult result = await NandImageService.InspectAsync(image);

        Assert.Equal(NandEvidenceResolution.Absent, result.SemanticEvidence.KernelDashboard.Resolution);
        Assert.Null(result.SemanticEvidence.KernelDashboard.ExactDashboardVersion);
    }

    [Fact]
    public async Task Ignores_complete_CF_CG_pairs_after_the_declared_slot_count()
    {
        byte[] logical = CreateLogicalImage(cbABuild: 12345);
        WriteUpdateSlotMetadata(logical, slotCount: 1);
        WriteCfCgPair(logical, FirstUpdateSlotOffset, targetVersion: 17559);
        WriteCfCgPair(logical, FirstUpdateSlotOffset + UpdateSlotLength, targetVersion: 17558);
        using var image = new MemoryStream(logical, writable: false);

        NandInspectionResult result = await NandImageService.InspectAsync(image);

        Assert.Collection(
            result.BootloaderStages.Where(stage => stage.NumericId is 6 or 7),
            stage => Assert.Equal(NandBootloaderStageKind.CF0, stage.Kind),
            stage => Assert.Equal(NandBootloaderStageKind.CG0, stage.Kind));
        Assert.Equal(NandEvidenceResolution.Confirmed, result.SemanticEvidence.KernelDashboard.Resolution);
        Assert.Equal(17559, result.SemanticEvidence.KernelDashboard.ExactDashboardVersion);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, byte.MaxValue)]
    [InlineData(false, 0)]
    [InlineData(false, byte.MaxValue)]
    public async Task Treats_only_a_wholly_blank_declared_slot_as_inactive_and_preserves_physical_slot_kinds(bool firstSlotInactive, int filler)
    {
        byte[] logical = CreateLogicalImage(cbABuild: 12345);
        WriteUpdateSlotMetadata(logical, slotCount: 2);
        int inactiveSlotIndex = firstSlotInactive ? 0 : 1;
        int activeSlotIndex = 1 - inactiveSlotIndex;
        logical.AsSpan(FirstUpdateSlotOffset + (inactiveSlotIndex * UpdateSlotLength), UpdateSlotLength).Fill(checked((byte)filler));
        int activeCfOffset = FirstUpdateSlotOffset + (activeSlotIndex * UpdateSlotLength);
        WriteCfCgPair(logical, activeCfOffset);
        using var image = new MemoryStream(logical, writable: false);

        NandInspectionResult result = await NandImageService.InspectAsync(image);

        Assert.Collection(
            result.BootloaderStages.Where(stage => stage.NumericId is 6 or 7),
            stage => Assert.Equal(
                (firstSlotInactive ? NandBootloaderStageKind.CF1 : NandBootloaderStageKind.CF0, (long)activeCfOffset),
                (stage.Kind, stage.LogicalOffset)),
            stage => Assert.Equal(
                (firstSlotInactive ? NandBootloaderStageKind.CG1 : NandBootloaderStageKind.CG0, (long)(activeCfOffset + CfStageLength)),
                (stage.Kind, stage.LogicalOffset)));
        Assert.Equal(NandEvidenceResolution.Confirmed, result.SemanticEvidence.KernelDashboard.Resolution);
        Assert.Equal(17559, result.SemanticEvidence.KernelDashboard.ExactDashboardVersion);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(byte.MaxValue)]
    public async Task Leaves_exact_dashboard_absent_when_all_declared_slots_are_blank(int filler)
    {
        byte[] logical = CreateLogicalImage(cbABuild: 12345);
        WriteUpdateSlotMetadata(logical, slotCount: 2);
        logical.AsSpan(FirstUpdateSlotOffset, 2 * UpdateSlotLength).Fill(checked((byte)filler));
        using var image = new MemoryStream(logical, writable: false);

        NandInspectionResult result = await NandImageService.InspectAsync(image);

        Assert.DoesNotContain(result.BootloaderStages, stage => stage.NumericId is 6 or 7);
        Assert.Equal(NandEvidenceResolution.Absent, result.SemanticEvidence.KernelDashboard.Resolution);
        Assert.Null(result.SemanticEvidence.KernelDashboard.ExactDashboardVersion);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(byte.MaxValue, false)]
    [InlineData(0, true)]
    [InlineData(byte.MaxValue, true)]
    public async Task Rejects_a_blank_slot_header_with_a_hidden_CF_CG_fragment_as_inactive(int filler, bool completeHiddenPair)
    {
        byte[] logical = CreateLogicalImage(cbABuild: 12345);
        WriteUpdateSlotMetadata(logical, slotCount: 2);
        WriteCfCgPair(logical, FirstUpdateSlotOffset);
        int hiddenSlotOffset = FirstUpdateSlotOffset + UpdateSlotLength;
        logical.AsSpan(hiddenSlotOffset, UpdateSlotLength).Fill(checked((byte)filler));
        int hiddenCfOffset = hiddenSlotOffset + 0x100;
        WriteCfCgPair(logical, hiddenCfOffset);
        if (!completeHiddenPair)
        {
            logical.AsSpan(hiddenCfOffset + CfStageLength, CgStageLength).Fill(checked((byte)filler));
        }
        using var image = new MemoryStream(logical, writable: false);

        NandInspectionResult result = await NandImageService.InspectAsync(image);

        Assert.Equal(NandEvidenceResolution.Absent, result.SemanticEvidence.KernelDashboard.Resolution);
        Assert.Null(result.SemanticEvidence.KernelDashboard.ExactDashboardVersion);
    }

    [Theory]
    [InlineData(17559, NandEvidenceResolution.Confirmed, 17559)]
    [InlineData(17558, NandEvidenceResolution.Conflicting, null)]
    public async Task Considers_targets_in_every_declared_slot_including_the_third(
        int thirdTargetVersion,
        NandEvidenceResolution expectedResolution,
        int? expectedExactDashboardVersion)
    {
        byte[] logical = CreateLogicalImage(cbABuild: 12345);
        WriteUpdateSlotMetadata(logical, slotCount: 3);
        WriteCfCgPair(logical, FirstUpdateSlotOffset);
        WriteCfCgPair(logical, FirstUpdateSlotOffset + UpdateSlotLength);
        int thirdCfOffset = FirstUpdateSlotOffset + (2 * UpdateSlotLength);
        WriteCfCgPair(logical, thirdCfOffset, targetVersion: thirdTargetVersion);
        using var image = new MemoryStream(logical, writable: false);

        NandInspectionResult result = await NandImageService.InspectAsync(image);

        Assert.Collection(
            result.BootloaderStages.Where(stage => stage.NumericId is 6 or 7),
            stage => Assert.Equal(NandBootloaderStageKind.CF0, stage.Kind),
            stage => Assert.Equal(NandBootloaderStageKind.CG0, stage.Kind),
            stage => Assert.Equal(NandBootloaderStageKind.CF1, stage.Kind),
            stage => Assert.Equal(NandBootloaderStageKind.CG1, stage.Kind),
            stage => Assert.Equal((NandBootloaderStageKind.Other, (byte)6, (long)thirdCfOffset), (stage.Kind, stage.NumericId, stage.LogicalOffset)),
            stage => Assert.Equal((NandBootloaderStageKind.Other, (byte)7, (long)(thirdCfOffset + CfStageLength)), (stage.Kind, stage.NumericId, stage.LogicalOffset)));
        Assert.Equal(expectedResolution, result.SemanticEvidence.KernelDashboard.Resolution);
        Assert.Equal(expectedExactDashboardVersion, result.SemanticEvidence.KernelDashboard.ExactDashboardVersion);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Reports_conflicting_valid_targets_even_when_another_declared_slot_is_malformed(int malformedSlotIndex)
    {
        byte[] logical = CreateLogicalImage(cbABuild: 12345);
        WriteUpdateSlotMetadata(logical, slotCount: 3);
        int validSlotCount = 0;
        for (int slotIndex = 0; slotIndex < 3; slotIndex++)
        {
            int targetVersion = slotIndex == malformedSlotIndex ? 0 : validSlotCount++ == 0 ? 17559 : 17558;
            WriteCfCgPair(logical, FirstUpdateSlotOffset + (slotIndex * UpdateSlotLength), targetVersion: targetVersion);
        }
        using var image = new MemoryStream(logical, writable: false);

        NandInspectionResult result = await NandImageService.InspectAsync(image);

        Assert.Equal(NandEvidenceResolution.Conflicting, result.SemanticEvidence.KernelDashboard.Resolution);
        Assert.Null(result.SemanticEvidence.KernelDashboard.ExactDashboardVersion);
    }

    [Fact]
    public async Task Rejects_conflicting_complete_CF_targets_across_declared_slots()
    {
        byte[] logical = CreateLogicalImage(cbABuild: 12345);
        WriteUpdateSlotMetadata(logical, slotCount: 2);
        WriteCfCgPair(logical, FirstUpdateSlotOffset, targetVersion: 17559, cfBuild: 14699, cgBuild: 14699);
        WriteCfCgPair(logical, FirstUpdateSlotOffset + UpdateSlotLength, targetVersion: 17558, cfBuild: 14699, cgBuild: 14699);
        using var image = new MemoryStream(logical, writable: false);

        NandInspectionResult result = await NandImageService.InspectAsync(image);

        Assert.Equal(NandEvidenceResolution.Conflicting, result.SemanticEvidence.KernelDashboard.Resolution);
        Assert.Null(result.SemanticEvidence.KernelDashboard.ExactDashboardVersion);
    }

    [Fact]
    public async Task Validators_verify_exact_CF_target_independently_of_unsupported_image_family_requirements()
    {
        byte[] logical = CreateLogicalImage(length: 0x1000000, smcMotherboardType: 5);
        WriteUpdateSlotMetadata(logical, slotCount: 1);
        WriteCfCgPair(logical, FirstUpdateSlotOffset, targetVersion: 17559, cfBuild: 1888, cgBuild: 14699, cfDeclaredLength: 0x354);
        byte[] physical = NandEccCodec.AddEcc(logical, NandPhysicalLayout.Layout1).ToArray();
        using var image = new MemoryStream(physical, writable: false);

        NandInspectionResult result = await NandImageService.InspectAsync(image);

        Assert.Equal(17559, result.SemanticEvidence.KernelDashboard.ExactDashboardVersion);
        var exactRequirement = new NandSemanticEvidenceRequirement(ConsoleId.Trinity16Mb, 17559, NandImageFamily.Glitch2);
        foreach (NandSemanticEvidenceValidationResult validation in new[]
        {
            NandSemanticEvidenceValidator.Validate(result, exactRequirement),
            NandSemanticEvidenceValidator.Validate(result.SemanticEvidence, exactRequirement),
        })
        {
            Assert.False(validation.IsMatch);
            Assert.Equal(
                NandSemanticEvidenceMismatchKind.ImageFamilyEvidenceUnavailable,
                Assert.Single(validation.Mismatches).Kind);
        }
        foreach (int genericBuild in new[] { 1888, 14699 })
        {
            var genericRequirement = new NandSemanticEvidenceRequirement(ConsoleId.Trinity16Mb, genericBuild, NandImageFamily.Glitch2);
            NandSemanticEvidenceValidationResult inspectionValidation = NandSemanticEvidenceValidator.Validate(result, genericRequirement);
            NandSemanticEvidenceValidationResult evidenceValidation = NandSemanticEvidenceValidator.Validate(result.SemanticEvidence, genericRequirement);
            Assert.False(inspectionValidation.IsMatch);
            Assert.False(evidenceValidation.IsMatch);
            Assert.Equal(
                [NandSemanticEvidenceMismatchKind.DashboardMismatch, NandSemanticEvidenceMismatchKind.ImageFamilyEvidenceUnavailable],
                inspectionValidation.Mismatches.Select(mismatch => mismatch.Kind).ToArray());
            Assert.Equal(
                [NandSemanticEvidenceMismatchKind.DashboardMismatch, NandSemanticEvidenceMismatchKind.ImageFamilyEvidenceUnavailable],
                evidenceValidation.Mismatches.Select(mismatch => mismatch.Kind).ToArray());
        }
    }

    [Theory]
    [InlineData(14699, false)]
    [InlineData(17559, false)]
    [InlineData(14699, true)]
    [InlineData(17559, true)]
    public async Task Validators_reject_matching_compatibility_CE_header_and_generic_metadata_without_exact_CF_targets(
        int requestedDashboard,
        bool includeGenericUpdatePair)
    {
        byte[] logical = CreateLogicalImage(
            length: 0x1000000,
            smcMotherboardType: 5,
            ceBuild: requestedDashboard,
            headerBuild: requestedDashboard);
        if (includeGenericUpdatePair)
        {
            WriteUpdateSlotMetadata(logical, slotCount: 1);
            WriteCfCgPair(logical, FirstUpdateSlotOffset, targetVersion: 0, cfBuild: requestedDashboard, cgBuild: requestedDashboard);
        }
        byte[] physical = NandEccCodec.AddEcc(logical, NandPhysicalLayout.Layout1).ToArray();
        using var image = new MemoryStream(physical, writable: false);

        NandInspectionResult result = await NandImageService.InspectAsync(image);

        Assert.Equal(requestedDashboard, result.Header.Build);
        Assert.Equal(requestedDashboard, result.SemanticEvidence.KernelDashboard.KernelBootloaderBuild);
        Assert.Equal(new NandDashboardVersionRange(8498, 14699), Assert.Single(result.SemanticEvidence.KernelDashboard.CompatibleVersionRanges));
        Assert.Null(result.SemanticEvidence.KernelDashboard.ExactDashboardVersion);
        var requirement = new NandSemanticEvidenceRequirement(ConsoleId.Trinity16Mb, requestedDashboard, NandImageFamily.Glitch2);
        NandSemanticEvidenceValidationResult inspectionValidation = NandSemanticEvidenceValidator.Validate(result, requirement);
        NandSemanticEvidenceValidationResult evidenceValidation = NandSemanticEvidenceValidator.Validate(result.SemanticEvidence, requirement);
        Assert.False(inspectionValidation.IsMatch);
        Assert.False(evidenceValidation.IsMatch);
        Assert.Equal(
            [NandSemanticEvidenceMismatchKind.DashboardNotExact, NandSemanticEvidenceMismatchKind.ImageFamilyEvidenceUnavailable],
            inspectionValidation.Mismatches.Select(mismatch => mismatch.Kind).ToArray());
        Assert.Equal(
            [NandSemanticEvidenceMismatchKind.DashboardNotExact, NandSemanticEvidenceMismatchKind.ImageFamilyEvidenceUnavailable],
            evidenceValidation.Mismatches.Select(mismatch => mismatch.Kind).ToArray());
    }

    [Theory]
    [InlineData(17559, false, NandSemanticEvidenceMismatchKind.DashboardNotExact)]
    [InlineData(0, true, NandSemanticEvidenceMismatchKind.DashboardNotExact)]
    [InlineData(17558, true, NandSemanticEvidenceMismatchKind.DashboardConflicting)]
    public async Task Validators_reject_incomplete_malformed_and_conflicting_declared_slots(
        int secondTargetVersion,
        bool includeSecondCg,
        NandSemanticEvidenceMismatchKind expectedMismatch)
    {
        byte[] logical = CreateLogicalImage(length: 0x1000000, smcMotherboardType: 5);
        WriteUpdateSlotMetadata(logical, slotCount: 2);
        WriteCfCgPair(logical, FirstUpdateSlotOffset);
        int secondCfOffset = FirstUpdateSlotOffset + UpdateSlotLength;
        WriteCfCgPair(logical, secondCfOffset, targetVersion: secondTargetVersion);
        if (!includeSecondCg)
        {
            logical.AsSpan(secondCfOffset + CfStageLength, CgStageLength).Clear();
        }
        byte[] physical = NandEccCodec.AddEcc(logical, NandPhysicalLayout.Layout1).ToArray();
        using var image = new MemoryStream(physical, writable: false);

        NandInspectionResult result = await NandImageService.InspectAsync(image);

        Assert.Null(result.SemanticEvidence.KernelDashboard.ExactDashboardVersion);
        var requirement = new NandSemanticEvidenceRequirement(ConsoleId.Trinity16Mb, 17559, NandImageFamily.Glitch2);
        NandSemanticEvidenceValidationResult inspectionValidation = NandSemanticEvidenceValidator.Validate(result, requirement);
        NandSemanticEvidenceValidationResult evidenceValidation = NandSemanticEvidenceValidator.Validate(result.SemanticEvidence, requirement);
        Assert.False(inspectionValidation.IsMatch);
        Assert.False(evidenceValidation.IsMatch);
        Assert.Equal(
            [expectedMismatch, NandSemanticEvidenceMismatchKind.ImageFamilyEvidenceUnavailable],
            inspectionValidation.Mismatches.Select(mismatch => mismatch.Kind).ToArray());
        Assert.Equal(
            [expectedMismatch, NandSemanticEvidenceMismatchKind.ImageFamilyEvidenceUnavailable],
            evidenceValidation.Mismatches.Select(mismatch => mismatch.Kind).ToArray());
    }

    [Fact]
    public async Task Honors_cancellation_and_never_serializes_CPU_key_DVD_key_or_raw_byte_surfaces()
    {
        using var cancelledImage = new MemoryStream(CreateLogicalImage(), writable: false);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => NandImageService.InspectAsync(cancelledImage, cancellationToken: cancellation.Token));

        CpuKey suppliedKey = CpuKey.Parse("00112233445566778899AABBCCDDEEFF");
        using var image = new MemoryStream(CreateLogicalImage(), writable: false);
        NandInspectionResult result = await NandImageService.InspectAsync(image, suppliedKey);
        string json = JsonSerializer.Serialize(result);

        Assert.Equal(KeyvaultCpuKeyVerificationStatus.NotRequired, result.Keyvault.Inspection.CpuKeyVerification);
        Assert.DoesNotContain("00112233445566778899AABBCCDDEEFF", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SYNTHETIC-DVD-KEY", json, StringComparison.Ordinal);
        Assert.DoesNotContain(
            typeof(NandInspectionResult).GetProperties(),
            property => property.PropertyType == typeof(byte[]) ||
                property.PropertyType == typeof(Memory<byte>) ||
                property.PropertyType == typeof(ReadOnlyMemory<byte>) ||
                property.PropertyType == typeof(CpuKey));
    }

    [Fact]
    public async Task Rejects_large_in_range_SMC_and_bootloader_declarations_before_allocation()
    {
        using var oversizedSmc = CreateSparseEmmc(smcLength: 0x100001, stageLength: 0x20);
        OperationFailureException smcFailure = await Assert.ThrowsAsync<OperationFailureException>(
            () => NandImageService.InspectAsync(oversizedSmc));
        Assert.Equal("invalid-smc-range", smcFailure.Kind);

        using var oversizedStage = CreateSparseEmmc(smcLength: 0x103, stageLength: 0x1000010);
        OperationFailureException stageFailure = await Assert.ThrowsAsync<OperationFailureException>(
            () => NandImageService.InspectAsync(oversizedStage));
        Assert.Equal("invalid-stage-length", stageFailure.Kind);
    }

    [Fact]
    public async Task Rejects_an_aggregate_main_bootloader_chain_before_loading_repeated_maximum_stages()
    {
        using var oversizedChain = CreateSparseEmmc(
            smcLength: 0x103,
            stageLength: 0x1000000,
            mainStageCount: 3);

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => NandImageService.InspectAsync(oversizedChain));

        Assert.Equal("invalid-stage-length", failure.Kind);
    }

    private static void AssertImageFamilyAbsent(NandInspectionResult result)
    {
        NandImageFamilyEvidence evidence = result.SemanticEvidence.ImageFamily;
        Assert.Same(NandRgh3OutputEvidence.Absent, evidence);
        Assert.Equal(NandEvidenceResolution.Absent, evidence.Resolution);
        Assert.Null(evidence.Family);
        Assert.False(evidence.IsConfirmed);
        Assert.Empty(evidence.Candidates);
        Assert.Empty(evidence.DirectEvidence);

        NandHackTypeEvidence compatibility = result.SemanticEvidence.Hack;
        Assert.Null(compatibility.HackType);
        Assert.False(compatibility.IsConfirmed);
        if (result.HackEvidence.PreferredHack == NandHackType.Unknown)
        {
            Assert.Equal(NandEvidenceResolution.Absent, compatibility.Resolution);
            Assert.Empty(compatibility.Candidates);
        }
        else
        {
            Assert.Equal(NandEvidenceResolution.Ambiguous, compatibility.Resolution);
            NandHackType[] labels = result.HackEvidence.PreferredHack == NandHackType.Glitch2
                ? [NandHackType.Glitch2, NandHackType.Glitch2m]
                : [result.HackEvidence.PreferredHack];
            Assert.Equal(labels, compatibility.Candidates.ToArray());
        }
    }

    private static void AssertReviewedRgh3Evidence(NandInspectionResult result)
    {
        NandImageFamilyEvidence family = result.SemanticEvidence.ImageFamily;
        Assert.Equal(NandEvidenceResolution.Confirmed, family.Resolution);
        Assert.Equal(NandImageFamily.Rgh3, family.Family);
        Assert.Equal([NandImageFamily.Rgh3], family.Candidates.ToArray());
        Assert.True(NandRgh3OutputEvidence.ReviewedManifest.HasValidProvenance(NandImageFamily.Rgh3, family.DirectEvidence));
        NandDirectOutputEvidenceProvenance provenance = Assert.Single(family.DirectEvidence);
        Assert.Equal(NandDirectOutputEvidenceSource.ReviewedDecryptedStageFingerprint, provenance.Source);
        Assert.Equal(NandRgh3OutputEvidence.ReviewedManifest.Sha256, provenance.ManifestSha256);
        Assert.Equal(NandRgh3OutputEvidence.FingerprintId, provenance.FingerprintId);
        Assert.Equal(NandBootloaderStageKind.CB_X, provenance.Stage);
        Assert.Equal(NandBootloaderDecryptionStatus.Decrypted, provenance.DecryptionStatus);
    }

    private static byte[] CreateRgh3LogicalImage(
        int length = LogicalImageLength,
        bool encryptWithZeroCpuKey = true,
        bool includeFinalCbB = true,
        int cbXBuild = 15432,
        int payloadLength = CbStageLength,
        int? changedWordOffset = null,
        int cbaFlags = 0,
        uint firstWord = 0x64690002)
    {
        byte[] logical = CreateLogicalImage(length: length, smcMotherboardType: 5, hackedSmc: true);
        BinaryPrimitives.WriteUInt16BigEndian(logical.AsSpan(FirstStageOffset + 6, sizeof(ushort)), checked((ushort)cbaFlags));
        ReadOnlyMemory<byte> decodedCba = BootloaderCrypto.DecryptCb(logical.AsSpan(FirstStageOffset, CbStageLength)).Output;
        byte[] payload = new byte[payloadLength];
        byte[]? encryptedPayload = null;
        try
        {
            WriteBootloaderHeader(payload, 0, "CB", cbXBuild, payload.Length);
            foreach ((int offset, uint word) in new (int, uint)[]
            {
                (0x354, firstWord),
                (0x368, 0x7D8C482A),
                (0x370, 0x64690006),
                (0x37C, 0xF8491010),
            })
            {
                if (offset + sizeof(uint) <= payload.Length)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(offset, sizeof(uint)), word);
                }
            }

            if (changedWordOffset.HasValue)
            {
                payload[changedWordOffset.Value + sizeof(uint) - 1] ^= 1;
            }

            CpuKey? encryptionKey = encryptWithZeroCpuKey ? null : CpuKey.Parse("00112233445566778899AABBCCDDEEFF");
            encryptedPayload = EncryptLegacyCbPayload(payload, decodedCba.Span, encryptionKey);
            int cbXOffset = FirstStageOffset + CbStageLength;
            encryptedPayload.CopyTo(logical, cbXOffset);
            if (includeFinalCbB)
            {
                int cbBOffset = cbXOffset + ((payloadLength + 0x0F) & ~0x0F);
                WriteBootloaderHeader(logical, cbBOffset, "CB", 9188, CbStageLength);
                "XBOX_ROM"u8.CopyTo(logical.AsSpan(cbBOffset + 0x392, 8));
            }

            return logical;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsMemory(decodedCba).Span);
            CryptographicOperations.ZeroMemory(payload);
            if (encryptedPayload is not null)
            {
                CryptographicOperations.ZeroMemory(encryptedPayload);
            }
        }
    }

    private static byte[] EncryptLegacyCbPayload(ReadOnlySpan<byte> decodedPayload, ReadOnlySpan<byte> decodedCba, CpuKey? cpuKey)
    {
        byte[] encrypted = decodedPayload.ToArray();
        Span<byte> message = stackalloc byte[CpuKey.ByteLength * 2];
        Span<byte> key = stackalloc byte[XeCrypt.HmacSha1TagLength];
        try
        {
            message.Clear();
            encrypted.AsSpan(0x10, CpuKey.ByteLength).CopyTo(message);
            if (cpuKey.HasValue)
            {
                cpuKey.Value.CopyTo(message.Slice(CpuKey.ByteLength, CpuKey.ByteLength));
            }

            XeCrypt.HmacSha1Truncated(decodedCba.Slice(0x10, XeCrypt.HmacSha1TagLength), message, key);
            Rc4.TransformInPlace(key, encrypted.AsSpan(0x20));
            return encrypted;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(message);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] CreateLogicalImage(
        int length = LogicalImageLength,
        CpuKey? keyvaultCpuKey = null,
        bool includePrimaryPatch = true,
        int cbABuild = 9188,
        bool hackedSmc = false,
        int smcMotherboardType = 6,
        bool includeVirtualFuseMarker = false,
        bool virtualFuseLocationUnavailable = false,
        int? ceBuild = null,
        int headerBuild = 14699)
    {
        var image = new byte[length];
        image[0] = 0xFF;
        image[1] = 0x4F;
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(sizeof(ushort), sizeof(ushort)), checked((ushort)headerBuild));
        WriteUInt32BigEndian(image, 0x08, FirstStageOffset);
        WriteUInt32BigEndian(image, 0x78, SmcLength);
        WriteUInt32BigEndian(image, 0x7C, SmcOffset);
        if (includeVirtualFuseMarker)
        {
            const int virtualFusePatchSlotOffset = 0xB000;
            WriteUInt32BigEndian(image, 0x64, virtualFusePatchSlotOffset);
            BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(0x68, sizeof(ushort)), 1);
            WriteUInt32BigEndian(image, 0x70, 0x40);
            image.AsSpan(virtualFusePatchSlotOffset, 8).Fill(byte.MaxValue);
            image[virtualFusePatchSlotOffset] = 0xC0;
            for (int index = 0; index < CpuKey.ByteLength; index++)
            {
                image[virtualFusePatchSlotOffset + 0x20 + index] = checked((byte)(index * 0x11));
            }
        }
        else if (virtualFuseLocationUnavailable)
        {
            WriteUInt32BigEndian(image, 0x64, 0xB000);
            BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(0x68, sizeof(ushort)), 1);
            WriteUInt32BigEndian(image, 0x70, 4);
        }

        byte[] decryptedSmc = new byte[SmcLength];
        decryptedSmc[0x100] = checked((byte)(smcMotherboardType << 4));
        decryptedSmc[0x101] = 1;
        decryptedSmc[0x102] = 2;
        if (hackedSmc)
        {
            decryptedSmc[0x2DB0] = 1;
        }
        byte[] encryptedSmc = SmcCrypto.Encrypt(decryptedSmc);
        encryptedSmc.CopyTo(image, SmcOffset);

        byte[] keyvault = CreateDecryptedKeyvault();
        if (keyvaultCpuKey.HasValue)
        {
            byte[] encryptedKeyvault = EncryptKeyvault(keyvault, keyvaultCpuKey.Value);
            encryptedKeyvault.CopyTo(image, KeyvaultOffset);
            CryptographicOperations.ZeroMemory(encryptedKeyvault);
        }
        else
        {
            keyvault.CopyTo(image, KeyvaultOffset);
        }

        byte[] cbA = CreateEncryptedCbA(cbABuild);
        cbA.CopyTo(image, FirstStageOffset);

        if (ceBuild.HasValue)
        {
            WriteBootloaderHeader(
                image,
                FirstStageOffset + CbStageLength,
                "CE",
                ceBuild.Value,
                declaredLength: 0x20);
        }

        if (includePrimaryPatch && PrimaryPatchOffset + PatchLength <= image.Length)
        {
            WritePatchSection(image, PrimaryPatchOffset, 0x0000C000, 1, 0x38800000);
        }

        CryptographicOperations.ZeroMemory(decryptedSmc);
        CryptographicOperations.ZeroMemory(encryptedSmc);
        CryptographicOperations.ZeroMemory(keyvault);
        CryptographicOperations.ZeroMemory(cbA);
        return image;
    }

    private static byte[] CreateDecryptedKeyvault()
    {
        var keyvault = new byte[KeyvaultService.KeyvaultLength];
        Encoding.ASCII.GetBytes("TESTSERIAL01").CopyTo(keyvault, 0xB0);
        Encoding.ASCII.GetBytes("SYNTHETIC-DVD-KEY").CopyTo(keyvault, 0x100);
        Encoding.ASCII.GetBytes("SYNTHETIC-DRIVE-INQUIRY-0000").CopyTo(keyvault, 0xC92);
        keyvault[0x9CA] = 0x10;
        keyvault[0x9CB] = 0x20;
        keyvault[0x9CC] = 0x30;
        keyvault[0x9CD] = 0x40;
        keyvault[0x9CE] = 0x50;
        return keyvault;
    }

    private static byte[] CreateEncryptedCbA(int build, int length = CbStageLength)
    {
        byte[] stage = new byte[length];
        WriteBootloaderHeader(stage, 0, "CB", build, stage.Length);
        for (int index = 0; index < 0x10; index++)
        {
            stage[0x10 + index] = (byte)(0x20 + index);
        }

        stage[0x20] = 0x11;
        stage[0x21] = 0x22;
        stage[0x22] = 0x33;
        stage[0x3B1] = 9;
        byte[] firstBootLoaderKey =
        {
            0xDD, 0x88, 0xAD, 0x0C, 0x9E, 0xD6, 0x69, 0xE7,
            0xB5, 0x67, 0x94, 0xFB, 0x68, 0x56, 0x3E, 0xFA,
        };
        byte[] rc4Key = XeCrypt.HmacSha1Truncated(firstBootLoaderKey, stage.AsSpan(0x10, 0x10));
        try
        {
            Rc4.TransformInPlace(rc4Key, stage.AsSpan(0x20));
            return stage;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(firstBootLoaderKey);
            CryptographicOperations.ZeroMemory(rc4Key);
        }
    }

    private static byte[] EncryptKeyvault(ReadOnlySpan<byte> decrypted, CpuKey cpuKey)
    {
        byte[] encrypted = decrypted.ToArray();
        Span<byte> keyBytes = stackalloc byte[CpuKey.ByteLength];
        Span<byte> rc4Key = stackalloc byte[XeCrypt.HmacSha1TagLength];
        try
        {
            cpuKey.CopyTo(keyBytes);
            XeCrypt.HmacSha1Truncated(keyBytes, encrypted.AsSpan(0, 0x10), rc4Key);
            Rc4.TransformInPlace(rc4Key, encrypted.AsSpan(0x10));
            return encrypted;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
            CryptographicOperations.ZeroMemory(rc4Key);
        }
    }

    private static void WriteUpdateSlotMetadata(
        byte[] image,
        int slotCount,
        uint firstSlotOffset = FirstUpdateSlotOffset,
        uint slotLength = UpdateSlotLength)
    {
        WriteUInt32BigEndian(image, 0x64, firstSlotOffset);
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(0x68, sizeof(ushort)), checked((ushort)slotCount));
        WriteUInt32BigEndian(image, 0x70, slotLength);
    }

    private static void WriteCfCgPair(
        byte[] image,
        int cfOffset,
        int targetVersion = 17559,
        int cfBuild = 1888,
        int cgBuild = 14699,
        int cfDeclaredLength = CfStageLength,
        ushort targetFlags = 0)
    {
        int roundedCfLength = (cfDeclaredLength + 0x0F) & ~0x0F;
        image.AsSpan(cfOffset, roundedCfLength + CgStageLength).Clear();
        WriteBootloaderHeader(image, cfOffset, "CF", cfBuild, cfDeclaredLength);
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(cfOffset + 0x14, sizeof(ushort)), checked((ushort)targetVersion));
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(cfOffset + 0x16, sizeof(ushort)), targetFlags);
        WriteUInt32BigEndian(image, cfOffset + 0x1C, CgStageLength);
        WriteBootloaderHeader(image, cfOffset + roundedCfLength, "CG", cgBuild, CgStageLength);
    }

    private static void WriteBootloaderHeader(byte[] image, int offset, string magic, int build, int declaredLength)
    {
        image[offset] = checked((byte)magic[0]);
        image[offset + 1] = checked((byte)magic[1]);
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(offset + 2, sizeof(ushort)), checked((ushort)build));
        WriteUInt32BigEndian(image, offset + 0x0C, checked((uint)declaredLength));
    }

    private static void WritePatchSection(byte[] image, int offset, uint address, uint count, uint value)
    {
        WriteUInt32BigEndian(image, offset, address);
        WriteUInt32BigEndian(image, offset + sizeof(uint), count);
        WriteUInt32BigEndian(image, offset + (2 * sizeof(uint)), value);
        WriteUInt32BigEndian(image, offset + (3 * sizeof(uint)), uint.MaxValue);
    }

    private static void WriteUInt32BigEndian(byte[] bytes, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset, sizeof(uint)), value);
    }

    private static SparseSeekableStream CreateSparseEmmc(int smcLength, uint stageLength, int mainStageCount = 1)
    {
        const long emmcLength = 0xE0400000L;
        const int smcOffset = 0x10000;
        var image = new SparseSeekableStream(emmcLength);
        var header = new byte[0x80];
        header[0] = 0xFF;
        header[1] = 0x4F;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(sizeof(ushort), sizeof(ushort)), 14699);
        WriteUInt32BigEndian(header, 0x08, FirstStageOffset);
        WriteUInt32BigEndian(header, 0x78, checked((uint)smcLength));
        WriteUInt32BigEndian(header, 0x7C, smcOffset);
        image.WriteAt(0, header);

        if (smcLength == 0x103)
        {
            var decryptedSmc = new byte[smcLength];
            decryptedSmc[0x100] = 0x60;
            decryptedSmc[0x101] = 1;
            decryptedSmc[0x102] = 2;
            byte[] encryptedSmc = SmcCrypto.Encrypt(decryptedSmc);
            try
            {
                image.WriteAt(smcOffset, encryptedSmc);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(decryptedSmc);
                CryptographicOperations.ZeroMemory(encryptedSmc);
            }
        }

        for (int stageIndex = 0; stageIndex < mainStageCount; stageIndex++)
        {
            var stageHeader = new byte[0x10];
            stageHeader[0] = mainStageCount == 1 ? (byte)'C' : (byte)'S';
            stageHeader[1] = mainStageCount == 1 ? (byte)'B' : (byte)'C';
            BinaryPrimitives.WriteUInt16BigEndian(stageHeader.AsSpan(2, sizeof(ushort)), 9188);
            BinaryPrimitives.WriteUInt32BigEndian(stageHeader.AsSpan(0x0C, sizeof(uint)), stageLength);
            image.WriteAt(checked(FirstStageOffset + ((long)stageLength * stageIndex)), stageHeader);
        }

        return image;
    }

    private sealed class SparseSeekableStream : Stream
    {
        private readonly Dictionary<long, byte> values = [];
        private readonly long length;
        private long position;
        private bool disposed;

        internal SparseSeekableStream(long length)
        {
            this.length = length;
        }

        public override bool CanRead => !disposed;

        public override bool CanSeek => !disposed;

        public override bool CanWrite => false;

        public override long Length
        {
            get
            {
                ThrowIfDisposed();
                return length;
            }
        }

        public override long Position
        {
            get
            {
                ThrowIfDisposed();
                return position;
            }

            set
            {
                ThrowIfDisposed();
                if (value < 0 || value > length)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                position = value;
            }
        }

        internal void WriteAt(long offset, ReadOnlySpan<byte> source)
        {
            if (offset < 0 || source.Length > length - offset)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            for (int index = 0; index < source.Length; index++)
            {
                values[offset + index] = source[index];
            }
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            ThrowIfDisposed();
            int read = (int)Math.Min(buffer.Length, length - position);
            Span<byte> destination = buffer[..read];
            destination.Clear();
            for (int index = 0; index < destination.Length; index++)
            {
                if (values.TryGetValue(position + index, out byte value))
                {
                    destination[index] = value;
                }
            }

            position += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(position + offset),
                SeekOrigin.End => checked(length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            Position = target;
            return position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            disposed = true;
            base.Dispose(disposing);
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
        }
    }

    private sealed class RecordingProgress : IProgress<OperationProgress>
    {
        internal List<OperationProgress> Events { get; } = [];

        public void Report(OperationProgress value)
        {
            Events.Add(value);
        }
    }
}
