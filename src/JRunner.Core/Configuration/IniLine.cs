namespace JRunner.Core.Configuration;

/// <summary>
/// A line in an immutable INI document.
/// </summary>
public abstract record IniLine;

/// <summary>
/// Represents a blank line.
/// </summary>
public sealed record IniBlankLine : IniLine;

/// <summary>
/// Represents a comment line without its leading semicolon.
/// </summary>
public sealed record IniCommentLine : IniLine
{
    /// <summary>
    /// Creates a comment line.
    /// </summary>
    public IniCommentLine(string text)
    {
        Text = IniText.RequireSingleLine(text, nameof(text), allowEmpty: true);
    }

    /// <summary>
    /// Gets the comment text without its leading semicolon.
    /// </summary>
    public string Text { get; }
}

/// <summary>
/// Represents a section label such as <c>[xenonbl]</c>.
/// </summary>
public sealed record IniSectionLine : IniLine
{
    /// <summary>
    /// Creates a normalized section-label line with an optional suffix comment.
    /// </summary>
    public IniSectionLine(string label, string? comment = null)
    {
        Label = IniText.RequireLabel(label, nameof(label));
        Comment = comment is null
            ? null
            : IniText.RequireSingleLine(comment, nameof(comment), allowEmpty: true);
    }

    /// <summary>
    /// Gets the normalized label without brackets.
    /// </summary>
    public string Label { get; }

    /// <summary>
    /// Gets the optional suffix-comment text without its leading semicolon.
    /// </summary>
    public string? Comment { get; }
}

/// <summary>
/// Represents a key/value declaration.
/// </summary>
public sealed record IniPropertyLine : IniLine
{
    /// <summary>
    /// Creates a normalized key/value declaration.
    /// </summary>
    public IniPropertyLine(string key, string value)
    {
        Key = IniText.RequireKey(key, nameof(key));
        Value = IniText.RequireValue(value, nameof(value));
    }

    /// <summary>
    /// Gets the normalized key. ASCII spaces around and within legacy keys are removed.
    /// </summary>
    public string Key { get; }

    /// <summary>
    /// Gets the value with surrounding whitespace removed.
    /// </summary>
    public string Value { get; }
}

/// <summary>
/// Represents a non-empty line that has literal-directive syntax. XeBuild uses such lines for
/// file directives within a bootloader section.
/// </summary>
public sealed record IniLiteralLine : IniLine
{
    /// <summary>
    /// Creates a literal directive line.
    /// </summary>
    public IniLiteralLine(string text)
    {
        Text = IniText.RequireLiteral(text, nameof(text));
    }

    /// <summary>
    /// Gets the literal directive text.
    /// </summary>
    public string Text { get; }
}

/// <summary>
/// Represents a malformed source line retained so callers can report diagnostics without losing
/// the original text during a later edit.
/// </summary>
public sealed record IniMalformedLine : IniLine
{
    /// <summary>
    /// Creates a malformed source line.
    /// </summary>
    public IniMalformedLine(string text)
    {
        Text = IniText.RequireSingleLine(text, nameof(text), allowEmpty: false);
    }

    /// <summary>
    /// Gets the malformed source text.
    /// </summary>
    public string Text { get; }
}

internal static class IniText
{
    public static string RequireLabel(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!TryNormalizeLabel(value.AsSpan(), out var normalized))
        {
            throw new ArgumentException("A section label must be non-empty and cannot contain brackets or line breaks.", parameterName);
        }

        return normalized;
    }

    public static string RequireKey(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!TryNormalizeKey(value.AsSpan(), out var normalized))
        {
            throw new ArgumentException("A key must contain at least one non-space character and cannot contain a line break, equals sign, or section/comment prefix.", parameterName);
        }

        return normalized;
    }

    public static string RequireValue(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (ContainsLineBreak(value.AsSpan()))
        {
            throw new ArgumentException("A value cannot contain a line break.", parameterName);
        }

        return Trim(value.AsSpan()).ToString();
    }

    public static string RequireLiteral(string value, string parameterName)
    {
        var normalized = RequireSingleLine(value, parameterName, allowEmpty: false);
        if (!IsLiteralText(normalized.AsSpan()))
        {
            throw new ArgumentException("A literal directive cannot be a comment, section declaration, or key/value declaration.", parameterName);
        }

        return normalized;
    }

    public static string RequireSingleLine(string value, string parameterName, bool allowEmpty)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (ContainsLineBreak(value.AsSpan()))
        {
            throw new ArgumentException("INI lines cannot contain line breaks.", parameterName);
        }

        var normalized = Trim(value.AsSpan()).ToString();
        if (!allowEmpty && normalized.Length == 0)
        {
            throw new ArgumentException("A literal line must contain text.", parameterName);
        }

        return normalized;
    }

    public static bool TryNormalizeLabel(ReadOnlySpan<char> value, out string normalized)
    {
        if (ContainsLineBreak(value))
        {
            normalized = string.Empty;
            return false;
        }

        var trimmed = Trim(value);
        if (trimmed.IsEmpty)
        {
            normalized = string.Empty;
            return false;
        }

        var hasAsciiSpace = false;
        foreach (var character in trimmed)
        {
            if (character is '[' or ']')
            {
                normalized = string.Empty;
                return false;
            }

            hasAsciiSpace |= character == ' ';
        }

        if (!hasAsciiSpace)
        {
            normalized = trimmed.ToString();
            return true;
        }

        var builder = new System.Text.StringBuilder(trimmed.Length);
        foreach (var character in trimmed)
        {
            if (character != ' ')
            {
                builder.Append(character);
            }
        }

        normalized = builder.ToString();
        return normalized.Length > 0;
    }

    public static bool TryNormalizeKey(ReadOnlySpan<char> value, out string normalized)
    {
        if (ContainsLineBreak(value))
        {
            normalized = string.Empty;
            return false;
        }

        var trimmed = Trim(value);
        if (trimmed.IsEmpty)
        {
            normalized = string.Empty;
            return false;
        }

        var hasAsciiSpace = false;
        foreach (var character in trimmed)
        {
            if (character == '=')
            {
                normalized = string.Empty;
                return false;
            }

            hasAsciiSpace |= character == ' ';
        }

        if (hasAsciiSpace)
        {
            var builder = new System.Text.StringBuilder(trimmed.Length);
            foreach (var character in trimmed)
            {
                if (character != ' ')
                {
                    builder.Append(character);
                }
            }

            normalized = builder.ToString();
        }
        else
        {
            normalized = trimmed.ToString();
        }

        if (normalized.Length == 0 || normalized[0] is ';' or '[')
        {
            normalized = string.Empty;
            return false;
        }

        return true;
    }

    public static bool IsLiteralText(ReadOnlySpan<char> value)
    {
        return !value.IsEmpty && value[0] is not ';' and not '[' && value.IndexOf('=') < 0;
    }

    public static ReadOnlySpan<char> Trim(ReadOnlySpan<char> value)
    {
        var start = 0;
        var end = value.Length;

        while (start < end && char.IsWhiteSpace(value[start]))
        {
            start++;
        }

        while (end > start && char.IsWhiteSpace(value[end - 1]))
        {
            end--;
        }

        return value.Slice(start, end - start);
    }

    public static bool ContainsLineBreak(ReadOnlySpan<char> value)
    {
        return value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0;
    }
}
