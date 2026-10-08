using System.Collections.Immutable;

namespace JRunner.Core.Patching;

/// <summary>
/// The immutable result of decoding one address/count/value NAND patch section.
/// </summary>
public sealed record PatchSectionParseResult
{
    /// <summary>
    /// Creates a patch-section parse result.
    /// </summary>
    public PatchSectionParseResult(
        ImmutableArray<PatchSectionRecord> records,
        ImmutableArray<PatchSectionDiagnostic> diagnostics,
        int startOffset,
        int nextOffset,
        int inputLength,
        int? terminatorOffset)
    {
        if (startOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startOffset), startOffset, "The start offset cannot be negative.");
        }

        if (inputLength < startOffset)
        {
            throw new ArgumentOutOfRangeException(nameof(inputLength), inputLength, "The input length must include the start offset.");
        }

        if (nextOffset < startOffset || nextOffset > inputLength)
        {
            throw new ArgumentOutOfRangeException(nameof(nextOffset), nextOffset, "The next offset must lie within the input span.");
        }

        if (terminatorOffset is { } offset && (offset < startOffset || offset > inputLength - sizeof(uint)))
        {
            throw new ArgumentOutOfRangeException(nameof(terminatorOffset), terminatorOffset, "The terminator must fit within the input span.");
        }

        if (records.IsDefault)
        {
            records = ImmutableArray<PatchSectionRecord>.Empty;
        }

        if (diagnostics.IsDefault)
        {
            diagnostics = ImmutableArray<PatchSectionDiagnostic>.Empty;
        }

        foreach (var record in records)
        {
            ArgumentNullException.ThrowIfNull(record);
        }

        foreach (var diagnostic in diagnostics)
        {
            ArgumentNullException.ThrowIfNull(diagnostic);
        }

        Records = records;
        Diagnostics = diagnostics;
        StartOffset = startOffset;
        NextOffset = nextOffset;
        InputLength = inputLength;
        TerminatorOffset = terminatorOffset;
    }

    /// <summary>
    /// Gets decoded records in their original order.
    /// </summary>
    public ImmutableArray<PatchSectionRecord> Records { get; }

    /// <summary>
    /// Gets diagnostics in decoding order.
    /// </summary>
    public ImmutableArray<PatchSectionDiagnostic> Diagnostics { get; }

    /// <summary>
    /// Gets the requested initial byte offset relative to the input span.
    /// </summary>
    public int StartOffset { get; }

    /// <summary>
    /// Gets the first byte not consumed by parsing. It points after a successful terminator or at
    /// the first unread portion of malformed input.
    /// </summary>
    public int NextOffset { get; }

    /// <summary>
    /// Gets the supplied input span length.
    /// </summary>
    public int InputLength { get; }

    /// <summary>
    /// Gets the offset of the terminator address when one was found.
    /// </summary>
    public int? TerminatorOffset { get; }

    /// <summary>
    /// Gets whether a complete <c>0xFFFFFFFF</c> terminator was found.
    /// </summary>
    public bool HasTerminator => TerminatorOffset.HasValue;

    /// <summary>
    /// Gets the count of bytes consumed from <see cref="StartOffset"/>.
    /// </summary>
    public int BytesConsumed => NextOffset - StartOffset;

    /// <summary>
    /// Gets the count of bytes not consumed from the input span.
    /// </summary>
    public int RemainingByteCount => InputLength - NextOffset;

    /// <summary>
    /// Gets whether malformed input prevented complete parsing.
    /// </summary>
    public bool HasErrors
    {
        get
        {
            foreach (var diagnostic in Diagnostics)
            {
                if (diagnostic.IsError)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Gets whether decoding reached a terminator without malformed-data diagnostics.
    /// </summary>
    public bool IsComplete => HasTerminator && !HasErrors;
}
