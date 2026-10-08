using System.Collections.Immutable;
using JRunner.Core.Patching;

namespace JRunner.Core.Patching.Inspection;

/// <summary>
/// Immutable structural and legacy-signature inspection output for one patch section.
/// </summary>
public sealed record PatchInspectionResult
{
    internal PatchInspectionResult(
        PatchSectionParseResult parseResult,
        ImmutableArray<LegacyPatchEvidence> recognizedLegacyPatches)
    {
        ArgumentNullException.ThrowIfNull(parseResult);

        if (recognizedLegacyPatches.IsDefault)
        {
            recognizedLegacyPatches = ImmutableArray<LegacyPatchEvidence>.Empty;
        }

        Records = parseResult.Records;
        TerminatorOffset = parseResult.TerminatorOffset;
        StructuralDiagnostics = parseResult.Diagnostics;
        CompletionStatus = parseResult.IsComplete
            ? PatchInspectionCompletionStatus.Complete
            : PatchInspectionCompletionStatus.Malformed;

        foreach (var evidence in recognizedLegacyPatches)
        {
            ArgumentNullException.ThrowIfNull(evidence);
            if ((uint)evidence.RecordIndex >= (uint)Records.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(recognizedLegacyPatches),
                    "Legacy patch evidence must refer to a decoded patch record.");
            }

            var record = Records[evidence.RecordIndex];
            if (record.Address != evidence.Address ||
                record.Count != evidence.WordCount ||
                record.Values.Length == 0 ||
                record.Values[0] != evidence.FirstValue)
            {
                throw new ArgumentException(
                    "Legacy patch evidence must describe the referenced decoded patch record.",
                    nameof(recognizedLegacyPatches));
            }
        }

        RecognizedLegacyPatches = recognizedLegacyPatches;
    }

    /// <summary>
    /// Gets whether the patch section completed at its terminator or was structurally malformed.
    /// </summary>
    public PatchInspectionCompletionStatus CompletionStatus { get; }

    /// <summary>
    /// Gets decoded patch records in encoded order. Records parsed before a later structural error
    /// remain available for diagnostics and signature evidence.
    /// </summary>
    public ImmutableArray<PatchSectionRecord> Records { get; }

    /// <summary>
    /// Gets the byte offset of the <c>0xFFFFFFFF</c> terminator, or <see langword="null"/> when
    /// no complete terminator was found.
    /// </summary>
    public int? TerminatorOffset { get; }

    /// <summary>
    /// Gets structural parser diagnostics in input order, including malformed-data details.
    /// </summary>
    public ImmutableArray<PatchSectionDiagnostic> StructuralDiagnostics { get; }

    /// <summary>
    /// Gets legacy patch-table matches in record order. An empty array on a complete result means
    /// the section was valid but did not contain a recognized legacy signature.
    /// </summary>
    public ImmutableArray<LegacyPatchEvidence> RecognizedLegacyPatches { get; }

    /// <summary>
    /// Gets whether the patch section reached a terminator without structural errors.
    /// </summary>
    public bool IsComplete => CompletionStatus == PatchInspectionCompletionStatus.Complete;

    /// <summary>
    /// Gets whether malformed or truncated input prevented complete inspection.
    /// </summary>
    public bool IsMalformed => CompletionStatus == PatchInspectionCompletionStatus.Malformed;

    /// <summary>
    /// Gets whether any decoded record matched a legacy patch-table signature.
    /// </summary>
    public bool HasRecognizedLegacyPatches => !RecognizedLegacyPatches.IsEmpty;
}
