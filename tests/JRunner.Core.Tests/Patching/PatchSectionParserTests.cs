using System.Buffers.Binary;
using JRunner.Core.Patching;
using Xunit;

namespace JRunner.Core.Tests.Patching;

public sealed class PatchSectionParserTests
{
    [Fact]
    public void Parse_recognizes_a_big_endian_terminator()
    {
        var result = PatchSectionParser.Parse(Encode(uint.MaxValue));

        Assert.True(result.IsComplete);
        Assert.True(result.HasTerminator);
        Assert.Empty(result.Records);
        Assert.Equal(0, result.TerminatorOffset);
        Assert.Equal(sizeof(uint), result.NextOffset);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Kind == PatchSectionDiagnosticKind.Terminator);
    }

    [Fact]
    public void Parse_decodes_multiple_big_endian_records_in_order()
    {
        var result = PatchSectionParser.Parse(
            Encode(
                0x00001000U, 2U, 0x11223344U, 0x55667788U,
                0xE0000000U, 1U, 0xAABBCCDDU,
                uint.MaxValue));

        Assert.True(result.IsComplete);
        Assert.Collection(
            result.Records,
            first =>
            {
                Assert.Equal(0x00001000U, first.Address);
                Assert.Equal(2U, first.Count);
                Assert.Equal(new uint[] { 0x11223344U, 0x55667788U }, first.Values);
            },
            second =>
            {
                Assert.Equal(0xE0000000U, second.Address);
                Assert.Equal(1U, second.Count);
                Assert.Equal(new uint[] { 0xAABBCCDDU }, second.Values);
            });
    }

    [Fact]
    public void Parse_skips_legacy_metadata_blocks_before_reading_the_next_record()
    {
        var data = new byte[0x50 + sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0, sizeof(uint)), 0xF0000000U);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x50, sizeof(uint)), uint.MaxValue);

        var result = PatchSectionParser.Parse(data);

        Assert.True(result.IsComplete);
        Assert.Empty(result.Records);
        Assert.Equal(0x50, result.TerminatorOffset);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Kind == PatchSectionDiagnosticKind.SkippedMetadataBlock);
    }

    [Fact]
    public void Parse_reports_a_truncated_header_without_reading_past_the_input()
    {
        var data = Encode(0x00001000U);
        Array.Resize(ref data, data.Length + 2);

        var result = PatchSectionParser.Parse(data);

        Assert.False(result.IsComplete);
        Assert.False(result.HasTerminator);
        Assert.Empty(result.Records);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Kind == PatchSectionDiagnosticKind.TruncatedHeader);
    }

    [Fact]
    public void Parse_reports_a_truncated_payload_without_publishing_a_partial_record()
    {
        var result = PatchSectionParser.Parse(Encode(0x00001000U, 2U, 0x11223344U));

        Assert.False(result.IsComplete);
        Assert.Empty(result.Records);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Kind == PatchSectionDiagnosticKind.TruncatedPayload);
    }

    [Fact]
    public void Parse_rejects_an_implausible_count_before_allocating_or_reading_a_payload()
    {
        var result = PatchSectionParser.Parse(Encode(0x00001000U, 0x1001U, uint.MaxValue));

        Assert.False(result.IsComplete);
        Assert.Empty(result.Records);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Kind == PatchSectionDiagnosticKind.ImplausibleCount);
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
