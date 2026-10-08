namespace JRunner.Core.Configuration;

/// <summary>
/// Pure transformations for an <see cref="IniDocument"/>. The editor operates only on immutable
/// document data; callers own any filesystem I/O.
/// </summary>
public static class IniEditor
{
    /// <summary>
    /// Inserts a key/value declaration into the selected section or replaces that section's final
    /// matching declaration. A <see langword="null"/> label selects global entries.
    /// </summary>
    public static IniDocument SetValue(IniDocument document, string? sectionLabel, string key, string value)
    {
        ArgumentNullException.ThrowIfNull(document);

        var normalizedKey = IniText.RequireKey(key, nameof(key));
        var normalizedValue = IniText.RequireValue(value, nameof(value));
        var range = document.GetSectionRange(sectionLabel);
        if (!range.Exists)
        {
            return AppendSectionValue(document, sectionLabel!, normalizedKey, normalizedValue);
        }

        for (var index = range.End - 1; index >= range.Start; index--)
        {
            if (document.Lines[index] is IniPropertyLine existing &&
                string.Equals(existing.Key, normalizedKey, StringComparison.Ordinal))
            {
                if (string.Equals(existing.Value, normalizedValue, StringComparison.Ordinal))
                {
                    return document;
                }

                var changedLines = document.Lines.ToList();
                changedLines[index] = new IniPropertyLine(normalizedKey, normalizedValue);
                return new IniDocument(changedLines);
            }
        }

        var addedLines = document.Lines.ToList();
        addedLines.Insert(range.End, new IniPropertyLine(normalizedKey, normalizedValue));
        return new IniDocument(addedLines);
    }

    /// <summary>
    /// Removes every matching key/value declaration from the selected section. A
    /// <see langword="null"/> label selects global entries.
    /// </summary>
    public static IniDocument RemoveValue(IniDocument document, string? sectionLabel, string key)
    {
        ArgumentNullException.ThrowIfNull(document);

        var normalizedKey = IniText.RequireKey(key, nameof(key));
        var range = document.GetSectionRange(sectionLabel);
        var indexes = new List<int>();
        for (var index = range.Start; index < range.End; index++)
        {
            if (document.Lines[index] is IniPropertyLine property &&
                string.Equals(property.Key, normalizedKey, StringComparison.Ordinal))
            {
                indexes.Add(index);
            }
        }

        if (indexes.Count == 0)
        {
            return document;
        }

        var changedLines = document.Lines.ToList();
        for (var index = indexes.Count - 1; index >= 0; index--)
        {
            changedLines.RemoveAt(indexes[index]);
        }

        return new IniDocument(changedLines);
    }

    /// <summary>
    /// Ensures a literal XeBuild directive is present in the selected section. A
    /// <see langword="null"/> label selects global entries.
    /// </summary>
    public static IniDocument AddLiteral(IniDocument document, string? sectionLabel, string text)
    {
        ArgumentNullException.ThrowIfNull(document);

        var normalizedText = IniText.RequireLiteral(text, nameof(text));
        var range = document.GetSectionRange(sectionLabel);
        if (!range.Exists)
        {
            return AppendSectionLiteral(document, sectionLabel!, normalizedText);
        }

        for (var index = range.Start; index < range.End; index++)
        {
            if (document.Lines[index] is IniLiteralLine literal &&
                string.Equals(literal.Text, normalizedText, StringComparison.Ordinal))
            {
                return document;
            }
        }

        var changedLines = document.Lines.ToList();
        changedLines.Insert(range.End, new IniLiteralLine(normalizedText));
        return new IniDocument(changedLines);
    }

    /// <summary>
    /// Removes all matching literal XeBuild directives from the selected section. A
    /// <see langword="null"/> label selects global entries.
    /// </summary>
    public static IniDocument RemoveLiteral(IniDocument document, string? sectionLabel, string text)
    {
        ArgumentNullException.ThrowIfNull(document);

        var normalizedText = IniText.RequireLiteral(text, nameof(text));
        var range = document.GetSectionRange(sectionLabel);
        var indexes = new List<int>();
        for (var index = range.Start; index < range.End; index++)
        {
            if (document.Lines[index] is IniLiteralLine literal &&
                string.Equals(literal.Text, normalizedText, StringComparison.Ordinal))
            {
                indexes.Add(index);
            }
        }

        if (indexes.Count == 0)
        {
            return document;
        }

        var changedLines = document.Lines.ToList();
        for (var index = indexes.Count - 1; index >= 0; index--)
        {
            changedLines.RemoveAt(indexes[index]);
        }

        return new IniDocument(changedLines);
    }

    private static IniDocument AppendSectionValue(IniDocument document, string sectionLabel, string key, string value)
    {
        var lines = document.Lines.ToList();
        lines.Add(new IniSectionLine(sectionLabel));
        lines.Add(new IniPropertyLine(key, value));
        return new IniDocument(lines);
    }

    private static IniDocument AppendSectionLiteral(IniDocument document, string sectionLabel, string text)
    {
        var lines = document.Lines.ToList();
        lines.Add(new IniSectionLine(sectionLabel));
        lines.Add(new IniLiteralLine(text));
        return new IniDocument(lines);
    }
}
