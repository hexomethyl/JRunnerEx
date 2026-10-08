namespace JRunner.Core.Contracts;

/// <summary>
/// A completed operation result and its process exit code.
/// </summary>
public sealed record OperationResult<T>
{
    public OperationResult(T result, ExitCode exitCode = ExitCode.Success)
    {
        if (exitCode is not ExitCode.Success and not ExitCode.CompletedNegativeResult)
        {
            throw new ArgumentOutOfRangeException(
                nameof(exitCode),
                exitCode,
                "A completed result must use either the success or completed-negative-result exit code.");
        }

        Result = result;
        ExitCode = exitCode;
    }

    public T Result { get; }

    public ExitCode ExitCode { get; }
}

/// <summary>
/// Factory methods for completed operation results.
/// </summary>
public static class OperationResult
{
    public static OperationResult<T> Success<T>(T result)
    {
        return new OperationResult<T>(result);
    }

    public static OperationResult<T> Negative<T>(T result)
    {
        return new OperationResult<T>(result, ExitCode.CompletedNegativeResult);
    }
}
