using System.Buffers.Binary;
using System.Collections.Immutable;

namespace JRunner.Core.Patching;

/// <summary>
/// Decodes the big-endian address/count/value patch sections embedded in NAND images and XeBuild
/// payloads. The parser is structural only: legacy signature tables and GUI side effects are not
/// part of this layer.
/// </summary>
public static class PatchSectionParser
{
    /// <summary>
    /// Gets the legacy safety limit for a record's DWORD payload count.
    /// </summary>
    public const int DefaultMaximumWordCount = 0x1000;

    /// <summary>
    /// Gets the DWORD address used to terminate a patch section.
    /// </summary>
    public const uint TerminatorAddress = uint.MaxValue;

    private const uint LegacyMetadataAddress = 0x00000000U;
    private const uint LegacyMetadataAlternateAddress = 0xF0000000U;
    private const int LegacyMetadataBlockLength = 0x50;

    /// <summary>
    /// Parses a patch section from <paramref name="data"/> beginning at <paramref name="startOffset"/>.
    /// A record is <c>address (u32-be), count (u32-be), values[count] (u32-be)</c>; an address of
    /// <see cref="TerminatorAddress"/> ends the section without a count.
    /// </summary>
    public static PatchSectionParseResult Parse(
        ReadOnlySpan<byte> data,
        int startOffset = 0,
        int maximumWordCount = DefaultMaximumWordCount,
        CancellationToken cancellationToken = default)
    {
        if (startOffset < 0 || startOffset > data.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(startOffset), startOffset, "The start offset must lie within the input span.");
        }

        if (maximumWordCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumWordCount), maximumWordCount, "The maximum word count cannot be negative.");
        }

        var records = ImmutableArray.CreateBuilder<PatchSectionRecord>();
        var diagnostics = ImmutableArray.CreateBuilder<PatchSectionDiagnostic>();
        var offset = startOffset;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var remaining = data.Length - offset;
            if (remaining == 0)
            {
                diagnostics.Add(
                    Error(
                        offset,
                        PatchSectionDiagnosticKind.MissingTerminator,
                        "The patch section ended without a 0xFFFFFFFF address terminator."));
                return CreateResult(records, diagnostics, startOffset, offset, data.Length, terminatorOffset: null);
            }

            if (remaining < sizeof(uint))
            {
                diagnostics.Add(
                    Error(
                        offset,
                        PatchSectionDiagnosticKind.TruncatedHeader,
                        "The patch section ended before a complete address DWORD was available."));
                return CreateResult(records, diagnostics, startOffset, offset, data.Length, terminatorOffset: null);
            }

            var addressOffset = offset;
            var address = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, sizeof(uint)));
            offset += sizeof(uint);
            if (address == TerminatorAddress)
            {
                diagnostics.Add(
                    new PatchSectionDiagnostic(
                        addressOffset,
                        PatchSectionDiagnosticKind.Terminator,
                        "The patch section terminator was found.",
                        PatchSectionDiagnosticSeverity.Information));
                return CreateResult(records, diagnostics, startOffset, offset, data.Length, addressOffset);
            }

            if (address is LegacyMetadataAddress or LegacyMetadataAlternateAddress)
            {
                if (data.Length - addressOffset < LegacyMetadataBlockLength)
                {
                    diagnostics.Add(
                        Error(
                            offset,
                            PatchSectionDiagnosticKind.TruncatedPayload,
                            "The patch section ended before a complete legacy metadata block was available."));
                    return CreateResult(records, diagnostics, startOffset, offset, data.Length, terminatorOffset: null);
                }

                diagnostics.Add(
                    new PatchSectionDiagnostic(
                        addressOffset,
                        PatchSectionDiagnosticKind.SkippedMetadataBlock,
                        "A legacy DevGL or G2M metadata block was skipped.",
                        PatchSectionDiagnosticSeverity.Information));
                offset = addressOffset + LegacyMetadataBlockLength;
                continue;
            }

            remaining = data.Length - offset;
            if (remaining < sizeof(uint))
            {
                diagnostics.Add(
                    Error(
                        offset,
                        PatchSectionDiagnosticKind.TruncatedHeader,
                        "The patch section ended before a complete count DWORD was available."));
                return CreateResult(records, diagnostics, startOffset, offset, data.Length, terminatorOffset: null);
            }

            var countOffset = offset;
            var count = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, sizeof(uint)));
            offset += sizeof(uint);
            if (count > (uint)maximumWordCount)
            {
                diagnostics.Add(
                    Error(
                        countOffset,
                        PatchSectionDiagnosticKind.ImplausibleCount,
                        $"The patch record declares {count} DWORD values, exceeding the maximum of {maximumWordCount}."));
                return CreateResult(records, diagnostics, startOffset, offset, data.Length, terminatorOffset: null);
            }

            var availableValueCount = (data.Length - offset) / sizeof(uint);
            if (count > (uint)availableValueCount)
            {
                diagnostics.Add(
                    Error(
                        offset,
                        PatchSectionDiagnosticKind.TruncatedPayload,
                        $"The patch record declares {count} DWORD values, but only {availableValueCount} remain."));
                return CreateResult(records, diagnostics, startOffset, offset, data.Length, terminatorOffset: null);
            }

            var values = ImmutableArray.CreateBuilder<uint>((int)count);
            for (var index = 0; index < (int)count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                values.Add(BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, sizeof(uint))));
                offset += sizeof(uint);
            }

            records.Add(new PatchSectionRecord(address, values.MoveToImmutable()));
        }
    }

    private static PatchSectionDiagnostic Error(int offset, PatchSectionDiagnosticKind kind, string message)
    {
        return new PatchSectionDiagnostic(offset, kind, message, PatchSectionDiagnosticSeverity.Error);
    }

    private static PatchSectionParseResult CreateResult(
        ImmutableArray<PatchSectionRecord>.Builder records,
        ImmutableArray<PatchSectionDiagnostic>.Builder diagnostics,
        int startOffset,
        int nextOffset,
        int inputLength,
        int? terminatorOffset)
    {
        return new PatchSectionParseResult(
            records.ToImmutable(),
            diagnostics.ToImmutable(),
            startOffset,
            nextOffset,
            inputLength,
            terminatorOffset);
    }
}
