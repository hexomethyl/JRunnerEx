namespace JRunner.Core.Contracts;

/// <summary>
/// A stable, user-safe failure that an operation can return or throw.
/// </summary>
public sealed record OperationFailure
{
    public OperationFailure(ExitCode code, string kind, string message)
    {
        if (!Enum.IsDefined<ExitCode>(code) || code is ExitCode.Success or ExitCode.Cancelled)
        {
            throw new ArgumentOutOfRangeException(
                nameof(code),
                code,
                "A failure must use a non-success, non-cancellation exit code.");
        }

        Code = code;
        Kind = ContractText.RequireLowerKebabCase(kind, nameof(kind));
        Message = ContractText.RequireMessage(message, nameof(message));
    }

    public ExitCode Code { get; }

    public string Kind { get; }

    public string Message { get; }
}

/// <summary>
/// Signals an expected operation failure without coupling Core code to a CLI renderer.
/// </summary>
public sealed class OperationFailureException : Exception
{
    public OperationFailureException(OperationFailure failure)
        : base(GetMessage(failure))
    {
        Failure = failure;
    }

    public OperationFailureException(ExitCode code, string kind, string message)
        : this(new OperationFailure(code, kind, message))
    {
    }

    public OperationFailure Failure { get; }

    public ExitCode Code => Failure.Code;

    public string Kind => Failure.Kind;

    private static string GetMessage(OperationFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return failure.Message;
    }
}
