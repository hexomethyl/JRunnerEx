using System.Collections.Immutable;
using JRunner.Core.Patching;

namespace JRunner.Core.Patching.Inspection;

/// <summary>
/// Inspects a big-endian NAND patch section without user-interface, process-wide, or filesystem
/// side effects.
/// </summary>
public static class PatchInspectionService
{
    private static readonly LegacyPatchSignature[] LegacyPatchSignatures =
    [
        // The legacy table's zero count is a wildcard; matching still requires a first value.
        new("FuseBlow", 0x0000C000U, null, 0x38800000U),
        new("XLUSB", 0x000E3A7CU, 1U, 0x3CE02000U),
        new("XLHDD", 0x0015D8ECU, 1U, 0x39401000U),
        new("UsbdSec", 0x000D8748U, 2U, 0x38600001U),
        new("CoronaKeyFix", 0x00003B8CU, 1U, 0x389F0010U),
    ];

    /// <summary>
    /// Structurally parses a patch section and identifies legacy J-Runner patch-table signatures.
    /// </summary>
    /// <param name="data">Patch-section bytes containing big-endian address/count/value records.</param>
    /// <param name="startOffset">The byte offset at which to begin parsing.</param>
    /// <param name="maximumWordCount">Maximum permitted DWORD count in one record.</param>
    /// <param name="cancellationToken">Token checked during structural parsing and signature inspection.</param>
    /// <returns>Immutable structural diagnostics, decoded records, and recognized legacy evidence.</returns>
    public static PatchInspectionResult Inspect(
        ReadOnlySpan<byte> data,
        int startOffset = 0,
        int maximumWordCount = PatchSectionParser.DefaultMaximumWordCount,
        CancellationToken cancellationToken = default)
    {
        var parseResult = PatchSectionParser.Parse(data, startOffset, maximumWordCount, cancellationToken);
        var recognizedLegacyPatches = FindLegacyPatchEvidence(parseResult.Records, cancellationToken);
        return new PatchInspectionResult(parseResult, recognizedLegacyPatches);
    }

    private static ImmutableArray<LegacyPatchEvidence> FindLegacyPatchEvidence(
        ImmutableArray<PatchSectionRecord> records,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var evidenceCount = CountLegacyPatchEvidence(records, cancellationToken);
        if (evidenceCount == 0)
        {
            return ImmutableArray<LegacyPatchEvidence>.Empty;
        }

        var evidence = ImmutableArray.CreateBuilder<LegacyPatchEvidence>(evidenceCount);
        for (var recordIndex = 0; recordIndex < records.Length; recordIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = records[recordIndex];

            for (var signatureIndex = 0; signatureIndex < LegacyPatchSignatures.Length; signatureIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var signature = LegacyPatchSignatures[signatureIndex];
                if (!signature.Matches(record))
                {
                    continue;
                }

                evidence.Add(
                    new LegacyPatchEvidence(
                        signature.Name,
                        recordIndex,
                        record.Address,
                        record.Count,
                        record.Values[0]));
            }
        }

        return evidence.MoveToImmutable();
    }

    private static int CountLegacyPatchEvidence(
        ImmutableArray<PatchSectionRecord> records,
        CancellationToken cancellationToken)
    {
        var count = 0;
        for (var recordIndex = 0; recordIndex < records.Length; recordIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = records[recordIndex];

            for (var signatureIndex = 0; signatureIndex < LegacyPatchSignatures.Length; signatureIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (LegacyPatchSignatures[signatureIndex].Matches(record))
                {
                    count = checked(count + 1);
                }
            }
        }

        return count;
    }

    private readonly record struct LegacyPatchSignature(
        string Name,
        uint Address,
        uint? ExpectedWordCount,
        uint ExpectedFirstValue)
    {
        public bool Matches(PatchSectionRecord record)
        {
            return record.Address == Address &&
                (ExpectedWordCount is null || record.Count == ExpectedWordCount.Value) &&
                record.Values.Length > 0 &&
                record.Values[0] == ExpectedFirstValue;
        }
    }
}
