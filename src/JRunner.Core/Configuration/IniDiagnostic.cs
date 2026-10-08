namespace JRunner.Core.Configuration;

/// <summary>
/// Classifies a condition encountered while parsing INI text.
/// </summary>
public enum IniDiagnosticKind
{
    /// <summary>
    /// A line beginning with an opening section bracket has no closing bracket.
    /// </summary>
    MissingSectionTerminator,

    /// <summary>
    /// A section declaration has no usable label.
    /// </summary>
    EmptySectionLabel,

    /// <summary>
    /// A section label contains characters that cannot be represented by this document model.
    /// </summary>
    InvalidSectionLabel,

    /// <summary>
    /// A section declaration contains non-comment text after its closing bracket.
    /// </summary>
    UnexpectedTextAfterSection,

    /// <summary>
    /// A key/value declaration has no key before its equals sign.
    /// </summary>
    EmptyKey,
}

/// <summary>
/// The severity of an INI parser diagnostic.
/// </summary>
public enum IniDiagnosticSeverity
{
    /// <summary>
    /// The input can be interpreted, but should be corrected before it is persisted.
    /// </summary>
    Warning,

    /// <summary>
    /// The affected input cannot be represented as a typed INI element.
    /// </summary>
    Error,
}

/// <summary>
/// An immutable diagnostic for a single line of INI input.
/// </summary>
public sealed record IniDiagnostic
{
    /// <summary>
    /// Creates a diagnostic associated with a one-based input line number.
    /// </summary>
    public IniDiagnostic(
        int lineNumber,
        IniDiagnosticKind kind,
        string message,
        IniDiagnosticSeverity severity = IniDiagnosticSeverity.Error)
    {
        if (lineNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(lineNumber), lineNumber, "Line numbers are one-based.");
        }

        if (!Enum.IsDefined<IniDiagnosticKind>(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "The diagnostic kind is not supported.");
        }

        if (!Enum.IsDefined<IniDiagnosticSeverity>(severity))
        {
            throw new ArgumentOutOfRangeException(nameof(severity), severity, "The diagnostic severity is not supported.");
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("A diagnostic message is required.", nameof(message));
        }

        LineNumber = lineNumber;
        Kind = kind;
        Message = message;
        Severity = severity;
    }

    /// <summary>
    /// Gets the one-based source line associated with this diagnostic.
    /// </summary>
    public int LineNumber { get; }

    /// <summary>
    /// Gets the diagnostic classification.
    /// </summary>
    public IniDiagnosticKind Kind { get; }

    /// <summary>
    /// Gets a human-readable description that is safe for a caller to render.
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// Gets the diagnostic severity.
    /// </summary>
    public IniDiagnosticSeverity Severity { get; }

    /// <summary>
    /// Gets whether the diagnostic prevents a complete typed interpretation of its line.
    /// </summary>
    public bool IsError => Severity == IniDiagnosticSeverity.Error;
}
