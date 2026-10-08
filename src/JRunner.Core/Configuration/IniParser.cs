using System.Collections.Immutable;

namespace JRunner.Core.Configuration;

/// <summary>
/// Parses INI text into a typed, immutable document without performing filesystem I/O.
/// </summary>
public static class IniParser
{
    /// <summary>
    /// Parses INI text. Leading and trailing whitespace is ignored around syntactic tokens;
    /// ASCII spaces inside legacy keys and section labels are removed to preserve parse_ini
    /// compatibility.
    /// </summary>
    public static IniParseResult Parse(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Parse(text.AsSpan(), cancellationToken);
    }

    /// <summary>
    /// Parses a character span into a typed INI document. Malformed lines remain visible through
    /// diagnostics and do not cause console output or out-of-range reads.
    /// </summary>
    public static IniParseResult Parse(ReadOnlySpan<char> text, CancellationToken cancellationToken = default)
    {
        var lines = ImmutableArray.CreateBuilder<IniLine>();
        var diagnostics = ImmutableArray.CreateBuilder<IniDiagnostic>();
        var position = 0;
        var lineNumber = 1;

        while (position < text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var lineStart = position;
            while (position < text.Length && text[position] is not '\r' and not '\n')
            {
                if (((position - lineStart) & 0xFFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                position++;
            }

            ParseLine(text.Slice(lineStart, position - lineStart), lineNumber, lines, diagnostics);

            if (position < text.Length)
            {
                var terminator = text[position++];
                if (terminator == '\r' && position < text.Length && text[position] == '\n')
                {
                    position++;
                }

                lineNumber++;
            }
        }

        return new IniParseResult(new IniDocument(lines.ToImmutable()), diagnostics.ToImmutable());
    }

    private static void ParseLine(
        ReadOnlySpan<char> input,
        int lineNumber,
        ImmutableArray<IniLine>.Builder lines,
        ImmutableArray<IniDiagnostic>.Builder diagnostics)
    {
        var line = IniText.Trim(input);
        if (line.IsEmpty)
        {
            lines.Add(new IniBlankLine());
            return;
        }

        if (line[0] == ';')
        {
            lines.Add(new IniCommentLine(IniText.Trim(line.Slice(1)).ToString()));
            return;
        }

        if (line[0] == '[')
        {
            ParseSection(line, lineNumber, lines, diagnostics);
            return;
        }

        var equalsOffset = line.IndexOf('=');
        if (equalsOffset < 0)
        {
            lines.Add(new IniLiteralLine(line.ToString()));
            return;
        }

        var keyText = line.Slice(0, equalsOffset);
        if (!IniText.TryNormalizeKey(keyText, out var key))
        {
            diagnostics.Add(
                new IniDiagnostic(
                    lineNumber,
                    IniDiagnosticKind.EmptyKey,
                    "A key/value line must contain a key before its equals sign."));
            lines.Add(new IniMalformedLine(line.ToString()));
            return;
        }

        var value = IniText.RequireValue(line.Slice(equalsOffset + 1).ToString(), "value");
        lines.Add(new IniPropertyLine(key, value));
    }

    private static void ParseSection(
        ReadOnlySpan<char> line,
        int lineNumber,
        ImmutableArray<IniLine>.Builder lines,
        ImmutableArray<IniDiagnostic>.Builder diagnostics)
    {
        var closingBracket = line.IndexOf(']');
        if (closingBracket < 0)
        {
            diagnostics.Add(
                new IniDiagnostic(
                    lineNumber,
                    IniDiagnosticKind.MissingSectionTerminator,
                    "A section declaration beginning with '[' must contain a closing ']'."));
            lines.Add(new IniMalformedLine(line.ToString()));
            return;
        }

        var labelText = line.Slice(1, closingBracket - 1);
        if (!IniText.TryNormalizeLabel(labelText, out var label))
        {
            diagnostics.Add(
                new IniDiagnostic(
                    lineNumber,
                    IniText.Trim(labelText).IsEmpty
                        ? IniDiagnosticKind.EmptySectionLabel
                        : IniDiagnosticKind.InvalidSectionLabel,
                    "A section label must be non-empty and cannot contain brackets or line breaks."));
            lines.Add(new IniMalformedLine(line.ToString()));
            return;
        }

        var trailingText = IniText.Trim(line.Slice(closingBracket + 1));
        if (trailingText.IsEmpty)
        {
            lines.Add(new IniSectionLine(label));
            return;
        }

        if (trailingText[0] == ';')
        {
            lines.Add(new IniSectionLine(label, IniText.Trim(trailingText.Slice(1)).ToString()));
            return;
        }

        diagnostics.Add(
            new IniDiagnostic(
                lineNumber,
                IniDiagnosticKind.UnexpectedTextAfterSection,
                "Only a comment may follow a section's closing bracket."));
        lines.Add(new IniMalformedLine(line.ToString()));
    }
}
