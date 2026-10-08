namespace JRunner.Core.Contracts;

/// <summary>
/// The severity of a progress or diagnostic event emitted by an operation.
/// </summary>
public enum OperationDiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

/// <summary>
/// An immutable progress or diagnostic event that Core operations can publish through
/// <c>IProgress&lt;OperationProgress&gt;</c> without depending on terminal APIs.
/// </summary>
public sealed record OperationProgress
{
    public OperationProgress(
        string kind,
        string message,
        OperationDiagnosticSeverity severity = OperationDiagnosticSeverity.Information,
        long? completed = null,
        long? total = null)
    {
        if (!Enum.IsDefined<OperationDiagnosticSeverity>(severity))
        {
            throw new ArgumentOutOfRangeException(nameof(severity), severity, "The diagnostic severity is not supported.");
        }

        if (completed is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(completed), "Completed work cannot be negative.");
        }

        if (total is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(total), "Total work cannot be negative.");
        }

        if (completed is { } completedValue && total is { } totalValue && completedValue > totalValue)
        {
            throw new ArgumentOutOfRangeException(nameof(completed), "Completed work cannot exceed total work.");
        }

        Kind = ContractText.RequireLowerKebabCase(kind, nameof(kind));
        Message = ContractText.RequireMessage(message, nameof(message));
        Severity = severity;
        Completed = completed;
        Total = total;
    }

    public string Kind { get; }

    public string Message { get; }

    public OperationDiagnosticSeverity Severity { get; }

    public long? Completed { get; }

    public long? Total { get; }

    public bool HasProgress => Completed.HasValue || Total.HasValue;
}
