using System.Collections.Immutable;

namespace JRunner.Core.Configuration;

/// <summary>
/// The immutable output of parsing INI text. Syntax errors are returned as diagnostics instead of
/// being written to a console or coupled to a user interface.
/// </summary>
public sealed record IniParseResult
{
    /// <summary>
    /// Creates a parse result.
    /// </summary>
    public IniParseResult(IniDocument document, ImmutableArray<IniDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (diagnostics.IsDefault)
        {
            diagnostics = ImmutableArray<IniDiagnostic>.Empty;
        }

        foreach (var diagnostic in diagnostics)
        {
            ArgumentNullException.ThrowIfNull(diagnostic);
        }

        Document = document;
        Diagnostics = diagnostics;
    }

    /// <summary>
    /// Gets the parsed document, including all valid lines that preceded or followed malformed
    /// input.
    /// </summary>
    public IniDocument Document { get; }

    /// <summary>
    /// Gets parser diagnostics in source order.
    /// </summary>
    public ImmutableArray<IniDiagnostic> Diagnostics { get; }

    /// <summary>
    /// Gets whether any diagnostic has error severity.
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
}
