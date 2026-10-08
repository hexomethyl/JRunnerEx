using System.Collections.Immutable;

namespace JRunner.Core.Nand.Inspection.Models;

/// <summary>
/// Identifies the source of an image-family claim without treating compatibility diagnostics as proof.
/// </summary>
public enum NandDirectOutputEvidenceSource
{
    /// <summary>No direct source was supplied.</summary>
    None,

    /// <summary>A legacy compatibility-table row, which is not direct image-family proof.</summary>
    CompatibilityTable,

    /// <summary>Generic stage metadata, which is not direct image-family proof.</summary>
    StageMetadata,

    /// <summary>An SMC marker, which is not direct image-family proof.</summary>
    SmcMarker,

    /// <summary>A virtual-fuse marker, which is not direct image-family proof.</summary>
    VirtualFuseMarker,

    /// <summary>Requested types or configuration metadata, which are not observed image-family proof.</summary>
    RequestMetadata,

    /// <summary>A decrypted-stage fingerprint matched under a reviewed manifest.</summary>
    ReviewedDecryptedStageFingerprint,
}

/// <summary>
/// Immutable scalar provenance for a direct-output evidence record. No stage bytes or keys are retained.
/// </summary>
public sealed record NandDirectOutputEvidenceProvenance
{
    internal NandDirectOutputEvidenceProvenance(
        NandDirectOutputEvidenceSource source,
        string? manifestSha256 = null,
        string? fingerprintId = null,
        NandBootloaderStageKind? stage = null,
        NandBootloaderDecryptionStatus? decryptionStatus = null)
    {
        if (!Enum.IsDefined(source))
        {
            throw new ArgumentOutOfRangeException(nameof(source), source, "The direct-evidence source is not supported.");
        }

        if (stage.HasValue && !Enum.IsDefined(stage.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(stage), stage, "The bootloader stage kind is not supported.");
        }

        if (decryptionStatus.HasValue && !Enum.IsDefined(decryptionStatus.Value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(decryptionStatus),
                decryptionStatus,
                "The bootloader decryption status is not supported.");
        }

        Source = source;
        ManifestSha256 = manifestSha256;
        FingerprintId = fingerprintId;
        Stage = stage;
        DecryptionStatus = decryptionStatus;
    }

    /// <summary>Gets the claimed evidence source.</summary>
    public NandDirectOutputEvidenceSource Source { get; }

    /// <summary>Gets the digest of the manifest under which the fingerprint was matched, when supplied.</summary>
    public string? ManifestSha256 { get; }

    /// <summary>Gets the matched reviewed fingerprint identifier, when supplied.</summary>
    public string? FingerprintId { get; }

    /// <summary>Gets the inspected source stage, when supplied.</summary>
    public NandBootloaderStageKind? Stage { get; }

    /// <summary>
    /// Gets the direct matching observation's decryption status, when supplied. An independently
    /// zero-key-decrypted RGH3 observation can differ from the generic bootloader metadata path's status.
    /// </summary>
    public NandBootloaderDecryptionStatus? DecryptionStatus { get; }
}

/// <summary>
/// Reviewed policy metadata binding a fingerprint identifier to an image family and source stage.
/// </summary>
public sealed record NandDirectOutputFingerprintDefinition
{
    internal NandDirectOutputFingerprintDefinition(string id, NandImageFamily family, NandBootloaderStageKind stage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!Enum.IsDefined(family))
        {
            throw new ArgumentOutOfRangeException(nameof(family), family, "The image family is not supported.");
        }

        if (!Enum.IsDefined(stage))
        {
            throw new ArgumentOutOfRangeException(nameof(stage), stage, "The bootloader stage kind is not supported.");
        }

        Id = id;
        Family = family;
        Stage = stage;
    }

    /// <summary>Gets the unique reviewed fingerprint identifier.</summary>
    public string Id { get; }

    /// <summary>Gets the family the fingerprint is required to prove.</summary>
    public NandImageFamily Family { get; }

    /// <summary>Gets the required decrypted source stage.</summary>
    public NandBootloaderStageKind Stage { get; }
}

/// <summary>
/// Immutable reviewed-policy metadata for direct output evidence, without retaining stage bytes or keys.
/// </summary>
/// <remarks>
/// Every fingerprint defined for an observed family is required. A digest and identifier alone do not
/// prove that matching happened; only the inspection pipeline can supply observed provenance.
/// This metadata retains no stage bytes, derived keys, or matching input.
/// </remarks>
public sealed record NandDirectOutputEvidenceManifest
{
    internal NandDirectOutputEvidenceManifest(
        string sha256,
        ImmutableArray<NandDirectOutputFingerprintDefinition> fingerprints)
    {
        ArgumentNullException.ThrowIfNull(sha256);
        if (sha256.Length != 64)
        {
            throw new ArgumentException("A reviewed manifest SHA-256 must contain exactly 64 hexadecimal characters.", nameof(sha256));
        }

        foreach (char character in sha256)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                throw new ArgumentException("A reviewed manifest SHA-256 must contain only hexadecimal characters.", nameof(sha256));
            }
        }

        if (fingerprints.IsDefault)
        {
            fingerprints = ImmutableArray<NandDirectOutputFingerprintDefinition>.Empty;
        }

        for (int index = 0; index < fingerprints.Length; index++)
        {
            NandDirectOutputFingerprintDefinition fingerprint = fingerprints[index];
            ArgumentNullException.ThrowIfNull(fingerprint);
            for (int previous = 0; previous < index; previous++)
            {
                if (string.Equals(fingerprints[previous].Id, fingerprint.Id, StringComparison.Ordinal))
                {
                    throw new ArgumentException("A reviewed manifest cannot contain duplicate fingerprint identifiers.", nameof(fingerprints));
                }
            }
        }

        Sha256 = sha256;
        Fingerprints = fingerprints;
    }

    /// <summary>Gets the SHA-256 identifying the reviewed policy.</summary>
    public string Sha256 { get; }

    /// <summary>Gets the required scalar fingerprint definitions.</summary>
    public ImmutableArray<NandDirectOutputFingerprintDefinition> Fingerprints { get; }

    internal bool HasValidProvenance(
        NandImageFamily family,
        ImmutableArray<NandDirectOutputEvidenceProvenance> evidence)
    {
        if (evidence.IsDefaultOrEmpty)
        {
            return false;
        }

        int requiredCount = 0;
        foreach (NandDirectOutputFingerprintDefinition fingerprint in Fingerprints)
        {
            if (fingerprint.Family == family)
            {
                requiredCount++;
            }
        }

        if (requiredCount == 0 || evidence.Length != requiredCount)
        {
            return false;
        }

        for (int index = 0; index < evidence.Length; index++)
        {
            NandDirectOutputEvidenceProvenance provenance = evidence[index];
            if (provenance is null ||
                provenance.Source != NandDirectOutputEvidenceSource.ReviewedDecryptedStageFingerprint ||
                !string.Equals(provenance.ManifestSha256, Sha256, StringComparison.OrdinalIgnoreCase) ||
                provenance.DecryptionStatus != NandBootloaderDecryptionStatus.Decrypted)
            {
                return false;
            }

            bool matchesDefinition = false;
            foreach (NandDirectOutputFingerprintDefinition fingerprint in Fingerprints)
            {
                if (string.Equals(fingerprint.Id, provenance.FingerprintId, StringComparison.Ordinal) &&
                    fingerprint.Family == family &&
                    fingerprint.Stage == provenance.Stage)
                {
                    matchesDefinition = true;
                    break;
                }
            }

            if (!matchesDefinition)
            {
                return false;
            }

            for (int previous = 0; previous < index; previous++)
            {
                if (string.Equals(evidence[previous].FingerprintId, provenance.FingerprintId, StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }

        return true;
    }
}
