using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using JRunner.Core.Nand.Inspection;
using JRunner.Core.Nand.Inspection.Models;
using Xunit;

namespace JRunner.Core.Tests.Nand.Inspection.Service;

public sealed class NandRgh3OutputEvidenceTests
{
    [Fact]
    public void Reviewed_manifest_digest_binds_the_actual_converter_output_predicate()
    {
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NandRgh3OutputEvidence.ReviewedDescriptor)));
        NandDirectOutputEvidenceManifest manifest = NandRgh3OutputEvidence.ReviewedManifest;

        Assert.Equal(digest.ToLowerInvariant(), manifest.Sha256);
        Assert.Contains("source=Rgh2ToRgh3ConversionService.PatchRgh3Payload\n", NandRgh3OutputEvidence.ReviewedDescriptor, StringComparison.Ordinal);
        NandDirectOutputFingerprintDefinition fingerprint = Assert.Single(manifest.Fingerprints);
        Assert.Equal("rgh3-patched-cbx-zero-cpu-v1", fingerprint.Id);
        Assert.Equal(NandImageFamily.Rgh3, fingerprint.Family);
        Assert.Equal(NandBootloaderStageKind.CB_X, fingerprint.Stage);
    }

    [Theory]
    [InlineData(0x354)]
    [InlineData(0x368)]
    [InlineData(0x370)]
    [InlineData(0x37C)]
    public void Each_reviewed_output_instruction_is_required(int changedOffset)
    {
        byte[] payload = CreatePatchedPayload();
        Assert.True(NandRgh3OutputEvidence.HasSignature(payload));
        payload[changedOffset + 3] ^= 1;

        Assert.False(NandRgh3OutputEvidence.HasSignature(payload));
    }

    [Fact]
    public void The_converter_input_probe_is_not_the_patched_output_signature()
    {
        byte[] payload = CreatePatchedPayload();
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0x354, sizeof(uint)), 0x646A0002);

        Assert.False(NandRgh3OutputEvidence.HasSignature(payload));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x354)]
    [InlineData(0x358)]
    [InlineData(0x37F)]
    public void Truncated_payloads_cannot_match_even_if_earlier_words_are_present(int length)
    {
        byte[] payload = CreatePatchedPayload();

        Assert.False(NandRgh3OutputEvidence.HasSignature(payload.AsSpan(0, length)));
        Assert.Equal(
            NandEvidenceResolution.Absent,
            NandRgh3OutputEvidence.InspectDecoded(payload.AsSpan(0, length), 15432, default).Resolution);
    }

    [Fact]
    public void Signature_words_are_read_in_big_endian_order()
    {
        byte[] payload = CreatePatchedPayload();
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0x354, sizeof(uint)), 0x64690002);

        Assert.False(NandRgh3OutputEvidence.HasSignature(payload));
    }

    [Fact]
    public void RGH13_build_is_excluded_even_when_all_RGH3_instruction_words_are_present()
    {
        byte[] payload = CreatePatchedPayload();

        NandImageFamilyEvidence result = NandRgh3OutputEvidence.InspectDecoded(payload, 42069, default);

        Assert.Equal(NandEvidenceResolution.Absent, result.Resolution);
        Assert.Null(result.Family);
        Assert.Empty(result.Candidates);
        Assert.Empty(result.DirectEvidence);
    }

    [Fact]
    public void Matching_provenance_binds_the_decrypted_CB_X_to_the_source_reviewed_manifest()
    {
        NandImageFamilyEvidence result = NandRgh3OutputEvidence.InspectDecoded(CreatePatchedPayload(), 15432, default);

        Assert.Equal(NandEvidenceResolution.Confirmed, result.Resolution);
        Assert.Equal(NandImageFamily.Rgh3, result.Family);
        Assert.Equal([NandImageFamily.Rgh3], result.Candidates.ToArray());
        Assert.True(NandRgh3OutputEvidence.ReviewedManifest.HasValidProvenance(NandImageFamily.Rgh3, result.DirectEvidence));
        NandDirectOutputEvidenceProvenance provenance = Assert.Single(result.DirectEvidence);
        Assert.Equal(NandDirectOutputEvidenceSource.ReviewedDecryptedStageFingerprint, provenance.Source);
        Assert.Equal(NandRgh3OutputEvidence.ReviewedManifest.Sha256, provenance.ManifestSha256);
        Assert.Equal(NandRgh3OutputEvidence.FingerprintId, provenance.FingerprintId);
        Assert.Equal(NandBootloaderStageKind.CB_X, provenance.Stage);
        Assert.Equal(NandBootloaderDecryptionStatus.Decrypted, provenance.DecryptionStatus);
    }

    [Fact]
    public void Matcher_honors_cancellation_before_decryption_or_signature_matching()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        byte[] payload = CreatePatchedPayload();

        Assert.Throws<OperationCanceledException>(() => NandRgh3OutputEvidence.InspectDecoded(payload, 15432, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => NandRgh3OutputEvidence.Inspect(new byte[0x20], payload, 15432, cancellation.Token));
    }

    private static byte[] CreatePatchedPayload()
    {
        var payload = new byte[0x380];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0x354, sizeof(uint)), 0x64690002);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0x368, sizeof(uint)), 0x7D8C482A);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0x370, sizeof(uint)), 0x64690006);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0x37C, sizeof(uint)), 0xF8491010);
        return payload;
    }
}
