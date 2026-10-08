namespace JRunner.Core.Patching;

/// <summary>
/// Classifies a structural condition encountered while reading a NAND patch section.
/// </summary>
public enum PatchSectionDiagnosticKind
{
    /// <summary>
    /// The <c>0xFFFFFFFF</c> address terminator was read successfully.
    /// </summary>
    Terminator,

    /// <summary>
    /// A legacy DevGL or G2M metadata block was skipped without interpreting it as a patch record.
    /// </summary>
    SkippedMetadataBlock,

    /// <summary>
    /// The input ended before a complete address or count DWORD was available.
    /// </summary>
    TruncatedHeader,

    /// <summary>
    /// The input ended before all DWORD values declared by a record were available.
    /// </summary>
    TruncatedPayload,

    /// <summary>
    /// A record's word count exceeds the caller-approved maximum.
    /// </summary>
    ImplausibleCount,

    /// <summary>
    /// The input ended on a record boundary without an address terminator.
    /// </summary>
    MissingTerminator,
}

/// <summary>
/// The severity of a patch-section parser diagnostic.
/// </summary>
public enum PatchSectionDiagnosticSeverity
{
    /// <summary>
    /// Informational parser state, such as a successfully read terminator.
    /// </summary>
    Information,

    /// <summary>
    /// A malformed section that cannot be parsed beyond the diagnostic offset.
    /// </summary>
    Error,
}

/// <summary>
/// An immutable structural diagnostic for a NAND patch section.
/// </summary>
public sealed record PatchSectionDiagnostic
{
    /// <summary>
    /// Creates a structural parser diagnostic at a byte offset relative to the input span.
    /// </summary>
    public PatchSectionDiagnostic(
        int offset,
        PatchSectionDiagnosticKind kind,
        string message,
        PatchSectionDiagnosticSeverity severity)
    {
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "A byte offset cannot be negative.");
        }

        if (!Enum.IsDefined<PatchSectionDiagnosticKind>(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "The diagnostic kind is not supported.");
        }

        if (!Enum.IsDefined<PatchSectionDiagnosticSeverity>(severity))
        {
            throw new ArgumentOutOfRangeException(nameof(severity), severity, "The diagnostic severity is not supported.");
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("A diagnostic message is required.", nameof(message));
        }

        Offset = offset;
        Kind = kind;
        Message = message;
        Severity = severity;
    }

    /// <summary>
    /// Gets the byte offset relative to the supplied input span.
    /// </summary>
    public int Offset { get; }

    /// <summary>
    /// Gets the structural condition classification.
    /// </summary>
    public PatchSectionDiagnosticKind Kind { get; }

    /// <summary>
    /// Gets a human-readable description that callers can render without GUI coupling.
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// Gets the diagnostic severity.
    /// </summary>
    public PatchSectionDiagnosticSeverity Severity { get; }

    /// <summary>
    /// Gets whether this diagnostic describes malformed data.
    /// </summary>
    public bool IsError => Severity == PatchSectionDiagnosticSeverity.Error;
}
