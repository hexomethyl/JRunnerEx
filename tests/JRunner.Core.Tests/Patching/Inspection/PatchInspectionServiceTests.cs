using System.Buffers.Binary;
using JRunner.Core.Patching;
using JRunner.Core.Patching.Inspection;
using Xunit;

namespace JRunner.Core.Tests.Patching.Inspection;

public sealed class PatchInspectionServiceTests
{
    [Fact]
    public void Inspect_recognizes_all_legacy_patch_table_signatures()
    {
        var result = PatchInspectionService.Inspect(
            Encode(
                0x0000C000U, 1U, 0x38800000U,
                0x000E3A7CU, 1U, 0x3CE02000U,
                0x0015D8ECU, 1U, 0x39401000U,
                0x000D8748U, 2U, 0x38600001U, 0x11223344U,
                0x00003B8CU, 1U, 0x389F0010U,
                uint.MaxValue));

        Assert.Equal(PatchInspectionCompletionStatus.Complete, result.CompletionStatus);
        Assert.True(result.IsComplete);
        Assert.False(result.IsMalformed);
        Assert.True(result.HasRecognizedLegacyPatches);
        Assert.Equal(64, result.TerminatorOffset);
        Assert.Collection(
            result.RecognizedLegacyPatches,
            evidence => AssertEvidence(evidence, "FuseBlow", 0, 0x0000C000U, 1U, 0x38800000U),
            evidence => AssertEvidence(evidence, "XLUSB", 1, 0x000E3A7CU, 1U, 0x3CE02000U),
            evidence => AssertEvidence(evidence, "XLHDD", 2, 0x0015D8ECU, 1U, 0x39401000U),
            evidence => AssertEvidence(evidence, "UsbdSec", 3, 0x000D8748U, 2U, 0x38600001U),
            evidence => AssertEvidence(evidence, "CoronaKeyFix", 4, 0x00003B8CU, 1U, 0x389F0010U));
    }

    [Fact]
    public void Inspect_reports_a_valid_unknown_patch_without_legacy_evidence()
    {
        var result = PatchInspectionService.Inspect(
            Encode(
                0x00123456U, 2U, 0xAABBCCDDU, 0x11223344U,
                uint.MaxValue));

        Assert.Equal(PatchInspectionCompletionStatus.Complete, result.CompletionStatus);
        Assert.True(result.IsComplete);
        Assert.False(result.IsMalformed);
        Assert.False(result.HasRecognizedLegacyPatches);
        Assert.Single(result.Records);
        Assert.Equal(16, result.TerminatorOffset);
        Assert.Empty(result.RecognizedLegacyPatches);
        Assert.DoesNotContain(result.StructuralDiagnostics, diagnostic => diagnostic.IsError);
    }

    [Fact]
    public void Inspect_reports_a_complete_terminator_only_section()
    {
        var result = PatchInspectionService.Inspect(Encode(uint.MaxValue));

        Assert.Equal(PatchInspectionCompletionStatus.Complete, result.CompletionStatus);
        Assert.True(result.IsComplete);
        Assert.Empty(result.Records);
        Assert.Equal(0, result.TerminatorOffset);
        Assert.Empty(result.RecognizedLegacyPatches);
        Assert.Contains(
            result.StructuralDiagnostics,
            diagnostic => diagnostic.Kind == PatchSectionDiagnosticKind.Terminator);
    }

    [Fact]
    public void Inspect_reports_a_truncated_section_as_malformed()
    {
        var result = PatchInspectionService.Inspect(Encode(0x0000C000U, 2U, 0x38800000U));

        Assert.Equal(PatchInspectionCompletionStatus.Malformed, result.CompletionStatus);
        Assert.True(result.IsMalformed);
        Assert.False(result.IsComplete);
        Assert.Null(result.TerminatorOffset);
        Assert.Empty(result.Records);
        Assert.Empty(result.RecognizedLegacyPatches);
        Assert.Contains(
            result.StructuralDiagnostics,
            diagnostic => diagnostic.Kind == PatchSectionDiagnosticKind.TruncatedPayload);
    }

    [Fact]
    public void Inspect_reports_an_overlarge_count_as_malformed()
    {
        var result = PatchInspectionService.Inspect(Encode(0x00001000U, 0x1001U, uint.MaxValue));

        Assert.Equal(PatchInspectionCompletionStatus.Malformed, result.CompletionStatus);
        Assert.True(result.IsMalformed);
        Assert.Null(result.TerminatorOffset);
        Assert.Empty(result.Records);
        Assert.Empty(result.RecognizedLegacyPatches);
        Assert.Contains(
            result.StructuralDiagnostics,
            diagnostic => diagnostic.Kind == PatchSectionDiagnosticKind.ImplausibleCount);
    }

    private static void AssertEvidence(
        LegacyPatchEvidence evidence,
        string patchName,
        int recordIndex,
        uint address,
        uint wordCount,
        uint firstValue)
    {
        Assert.Equal(patchName, evidence.PatchName);
        Assert.Equal(recordIndex, evidence.RecordIndex);
        Assert.Equal(address, evidence.Address);
        Assert.Equal(wordCount, evidence.WordCount);
        Assert.Equal(firstValue, evidence.FirstValue);
    }

    private static byte[] Encode(params uint[] words)
    {
        var data = new byte[checked(words.Length * sizeof(uint))];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(index * sizeof(uint), sizeof(uint)), words[index]);
        }

        return data;
    }
}
