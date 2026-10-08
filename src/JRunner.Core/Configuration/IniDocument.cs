using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace JRunner.Core.Configuration;

/// <summary>
/// An immutable, ordered INI document. It retains comments and literal directive lines so an
/// editor can change XeBuild options without discarding adjacent content.
/// </summary>
public sealed record IniDocument
{
    /// <summary>
    /// Creates a document from ordered INI lines.
    /// </summary>
    public IniDocument(IEnumerable<IniLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var lineBuilder = ImmutableArray.CreateBuilder<IniLine>();
        var labelBuilder = ImmutableArray.CreateBuilder<string>();
        foreach (var line in lines)
        {
            ArgumentNullException.ThrowIfNull(line);
            lineBuilder.Add(line);
            if (line is IniSectionLine section)
            {
                labelBuilder.Add(section.Label);
            }
        }

        Lines = lineBuilder.ToImmutable();
        Labels = labelBuilder.ToImmutable();
    }

    /// <summary>
    /// Gets an empty INI document.
    /// </summary>
    public static IniDocument Empty { get; } = new(Array.Empty<IniLine>());

    /// <summary>
    /// Gets ordered document lines.
    /// </summary>
    public ImmutableArray<IniLine> Lines { get; }

    /// <summary>
    /// Gets section labels in source order. Duplicate labels are preserved.
    /// </summary>
    public ImmutableArray<string> Labels { get; }

    /// <summary>
    /// Gets key/value entries in the selected section. A <see langword="null"/> label selects
    /// the document's global entries before its first section.
    /// </summary>
    public ImmutableArray<IniPropertyLine> GetProperties(string? sectionLabel = null)
    {
        var range = GetSectionRange(sectionLabel);
        var properties = ImmutableArray.CreateBuilder<IniPropertyLine>();
        for (var index = range.Start; index < range.End; index++)
        {
            if (Lines[index] is IniPropertyLine property)
            {
                properties.Add(property);
            }
        }

        return properties.ToImmutable();
    }

    /// <summary>
    /// Gets literal directive entries in the selected section. A <see langword="null"/> label
    /// selects the document's global entries before its first section.
    /// </summary>
    public ImmutableArray<IniLiteralLine> GetLiterals(string? sectionLabel = null)
    {
        var range = GetSectionRange(sectionLabel);
        var literals = ImmutableArray.CreateBuilder<IniLiteralLine>();
        for (var index = range.Start; index < range.End; index++)
        {
            if (Lines[index] is IniLiteralLine literal)
            {
                literals.Add(literal);
            }
        }

        return literals.ToImmutable();
    }

    /// <summary>
    /// Attempts to retrieve the final declaration for a key in the selected section.
    /// A <see langword="null"/> label selects global entries before the first section.
    /// </summary>
    public bool TryGetValue(
        string? sectionLabel,
        string key,
        [NotNullWhen(true)] out string? value)
    {
        var normalizedKey = IniText.RequireKey(key, nameof(key));
        var range = GetSectionRange(sectionLabel);
        value = null;

        for (var index = range.Start; index < range.End; index++)
        {
            if (Lines[index] is IniPropertyLine property &&
                string.Equals(property.Key, normalizedKey, StringComparison.Ordinal))
            {
                value = property.Value;
            }
        }

        return value is not null;
    }

    /// <summary>
    /// Returns a copy with a value inserted or with the final matching value replaced. Missing
    /// named sections are appended before their first value.
    /// </summary>
    public IniDocument WithValue(string? sectionLabel, string key, string value)
    {
        return IniEditor.SetValue(this, sectionLabel, key, value);
    }

    /// <summary>
    /// Returns a copy with every matching key removed from the selected section.
    /// </summary>
    public IniDocument WithoutValue(string? sectionLabel, string key)
    {
        return IniEditor.RemoveValue(this, sectionLabel, key);
    }

    /// <summary>
    /// Returns a copy with a literal XeBuild directive inserted into the selected section when
    /// it is not already present. Missing named sections are appended first.
    /// </summary>
    public IniDocument WithLiteral(string? sectionLabel, string text)
    {
        return IniEditor.AddLiteral(this, sectionLabel, text);
    }

    /// <summary>
    /// Returns a copy with matching literal directives removed from the selected section.
    /// </summary>
    public IniDocument WithoutLiteral(string? sectionLabel, string text)
    {
        return IniEditor.RemoveLiteral(this, sectionLabel, text);
    }

    /// <summary>
    /// Renders the document with canonical section and key/value spacing. The output ends in the
    /// requested line terminator when the document has at least one line.
    /// </summary>
    public string ToText(string lineTerminator = "\n")
    {
        ArgumentNullException.ThrowIfNull(lineTerminator);
        if (lineTerminator is not "\n" and not "\r" and not "\r\n")
        {
            throw new ArgumentException("The line terminator must be LF, CR, or CRLF.", nameof(lineTerminator));
        }

        if (Lines.IsEmpty)
        {
            return string.Empty;
        }

        var result = new StringBuilder();
        foreach (var line in Lines)
        {
            switch (line)
            {
                case IniBlankLine:
                    break;
                case IniCommentLine comment:
                    result.Append(';');
                    if (comment.Text.Length > 0)
                    {
                        result.Append(' ');
                        result.Append(comment.Text);
                    }

                    break;
                case IniSectionLine section:
                    result.Append('[');
                    result.Append(section.Label);
                    result.Append(']');
                    if (section.Comment is not null)
                    {
                        result.Append(" ;");
                        if (section.Comment.Length > 0)
                        {
                            result.Append(' ');
                            result.Append(section.Comment);
                        }
                    }

                    break;
                case IniPropertyLine property:
                    result.Append(property.Key);
                    result.Append(" = ");
                    result.Append(property.Value);
                    break;
                case IniLiteralLine literal:
                    result.Append(literal.Text);
                    break;
                case IniMalformedLine malformed:
                    result.Append(malformed.Text);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported INI line type: {line.GetType().FullName}.");
            }

            result.Append(lineTerminator);
        }

        return result.ToString();
    }

    internal SectionRange GetSectionRange(string? sectionLabel)
    {
        if (sectionLabel is null)
        {
            var globalEnd = FindNextSection(0);
            return new SectionRange(0, globalEnd, Exists: true);
        }

        var normalizedLabel = IniText.RequireLabel(sectionLabel, nameof(sectionLabel));
        var sectionStart = -1;
        for (var index = 0; index < Lines.Length; index++)
        {
            if (Lines[index] is IniSectionLine section &&
                string.Equals(section.Label, normalizedLabel, StringComparison.Ordinal))
            {
                sectionStart = index;
            }
        }

        if (sectionStart < 0)
        {
            return new SectionRange(0, 0, Exists: false);
        }

        return new SectionRange(sectionStart + 1, FindNextSection(sectionStart + 1), Exists: true);
    }

    private int FindNextSection(int start)
    {
        for (var index = start; index < Lines.Length; index++)
        {
            if (Lines[index] is IniSectionLine)
            {
                return index;
            }
        }

        return Lines.Length;
    }

    internal readonly record struct SectionRange(int Start, int End, bool Exists);
}
