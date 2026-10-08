using System.Buffers.Binary;
using System.Security.Cryptography;
using JRunner.Core.Nand.Conversion;
using JRunner.Core.Nand.Inspection.Models;
using JRunner.Core.Nand.Security;

namespace JRunner.Core.Nand.Inspection;

/// <summary>
/// Matches the source-reviewed patched RGH3 payload produced by the existing RGH2-to-RGH3 converter.
/// </summary>
internal static class NandRgh3OutputEvidence
{
    internal const string FingerprintId = "rgh3-patched-cbx-zero-cpu-v1";
    internal const string ReviewedDescriptor =
        "jrunner-rgh3-output-evidence-v1\n" +
        "source=Rgh2ToRgh3ConversionService.PatchRgh3Payload\n" +
        "topology=CB_A,CB_X,CB_B\n" +
        "decryption=legacy-cb-cba-hmac-sha1-rc4-zero-cpu-key\n" +
        "exclude-cb-x-build=42069\n" +
        "fingerprint=rgh3-patched-cbx-zero-cpu-v1\n" +
        "word-be=0x354:0x64690002\n" +
        "word-be=0x368:0x7D8C482A\n" +
        "word-be=0x370:0x64690006\n" +
        "word-be=0x37C:0xF8491010\n";

    // SHA-256 of the exact UTF-8/LF descriptor above, not of a fabricated stage fixture.
    private const string ReviewedDescriptorSha256 = "8fa67ddba6061b8dbe3555d7841d5aaa9d541d7ebf0bf78997b954eeefcabbde";
    private const int RequiredPayloadLength = 0x380;
    private const int ExcludedRgh13Build = 42069;

    public static NandDirectOutputEvidenceManifest ReviewedManifest { get; } =
        new(
            ReviewedDescriptorSha256,
            [new(FingerprintId, NandImageFamily.Rgh3, NandBootloaderStageKind.CB_X)]);

    internal static NandImageFamilyEvidence Absent { get; } = new(NandEvidenceResolution.Absent, null, default);
    private static readonly NandImageFamilyEvidence Confirmed =
        new(
            NandEvidenceResolution.Confirmed,
            NandImageFamily.Rgh3,
            [NandImageFamily.Rgh3],
            [
                new(
                    NandDirectOutputEvidenceSource.ReviewedDecryptedStageFingerprint,
                    ReviewedDescriptorSha256,
                    FingerprintId,
                    NandBootloaderStageKind.CB_X,
                    NandBootloaderDecryptionStatus.Decrypted),
            ]);

    /// <summary>
    /// Independently decrypts the CB_X of an inspected CB_A/CB_X/CB_B chain using the converter's
    /// exact zero-CPU-key path, then discards every decoded byte and key after matching.
    /// </summary>
    internal static NandImageFamilyEvidence Inspect(
        ReadOnlySpan<byte> decodedCba,
        ReadOnlySpan<byte> encryptedCbX,
        int cbXBuild,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (cbXBuild == ExcludedRgh13Build || decodedCba.Length < 0x20 || encryptedCbX.Length < RequiredPayloadLength)
        {
            return Absent;
        }

        Span<byte> zeroCpuKey = stackalloc byte[CpuKey.ByteLength];
        zeroCpuKey.Clear();
        byte[]? decodedPayload = null;
        try
        {
            decodedPayload = Rgh2ToRgh3ConversionService.DecryptLegacyRgh3Payload(
                encryptedCbX,
                decodedCba,
                zeroCpuKey,
                "invalid-stage-length",
                cancellationToken);
            return InspectDecoded(decodedPayload, cbXBuild, cancellationToken);
        }
        finally
        {
            if (decodedPayload is not null)
            {
                CryptographicOperations.ZeroMemory(decodedPayload);
            }

            CryptographicOperations.ZeroMemory(zeroCpuKey);
        }
    }

    internal static NandImageFamilyEvidence InspectDecoded(
        ReadOnlySpan<byte> decodedCbX,
        int cbXBuild,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return cbXBuild != ExcludedRgh13Build && HasSignature(decodedCbX) ? Confirmed : Absent;
    }

    internal static bool HasSignature(ReadOnlySpan<byte> decodedCbX) =>
        decodedCbX.Length >= RequiredPayloadLength &&
        BinaryPrimitives.ReadUInt32BigEndian(decodedCbX.Slice(0x354, sizeof(uint))) == 0x64690002 &&
        BinaryPrimitives.ReadUInt32BigEndian(decodedCbX.Slice(0x368, sizeof(uint))) == 0x7D8C482A &&
        BinaryPrimitives.ReadUInt32BigEndian(decodedCbX.Slice(0x370, sizeof(uint))) == 0x64690006 &&
        BinaryPrimitives.ReadUInt32BigEndian(decodedCbX.Slice(0x37C, sizeof(uint))) == 0xF8491010;
}
