using JRunner.Core.Contracts;

namespace JRunner.Cli.Infrastructure;

/// <summary>
/// A bounded, captured result from an external process. Child output is retained only as data for its caller.
/// </summary>
internal sealed class ExternalProcessResult
{
    internal ExternalProcessResult(
        int exitCode,
        string standardOutput,
        bool standardOutputTruncated,
        int standardOutputByteCount,
        string standardError,
        bool standardErrorTruncated,
        int standardErrorByteCount)
    {
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        if (standardOutputByteCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(standardOutputByteCount));
        }

        if (standardErrorByteCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(standardErrorByteCount));
        }

        ExitCode = exitCode;
        StandardOutput = standardOutput;
        StandardOutputTruncated = standardOutputTruncated;
        StandardOutputByteCount = standardOutputByteCount;
        StandardError = standardError;
        StandardErrorTruncated = standardErrorTruncated;
        StandardErrorByteCount = standardErrorByteCount;
    }

    /// <summary>
    /// Gets the child process exit code.
    /// </summary>
    public int ExitCode { get; }

    /// <summary>
    /// Gets the bounded UTF-8 standard-output capture. This is never written to a CLI stream by the runner.
    /// </summary>
    public string StandardOutput { get; }

    /// <summary>
    /// Gets whether standard output exceeded the configured capture limit.
    /// </summary>
    public bool StandardOutputTruncated { get; }

    /// <summary>
    /// Gets the number of standard-output bytes retained before decoding.
    /// </summary>
    public int StandardOutputByteCount { get; }

    /// <summary>
    /// Gets the bounded UTF-8 standard-error capture. This is never written to a CLI stream by the runner.
    /// </summary>
    public string StandardError { get; }

    /// <summary>
    /// Gets whether standard error exceeded the configured capture limit.
    /// </summary>
    public bool StandardErrorTruncated { get; }

    /// <summary>
    /// Gets the number of standard-error bytes retained before decoding.
    /// </summary>
    public int StandardErrorByteCount { get; }

    /// <summary>
    /// Gets whether the child exited successfully.
    /// </summary>
    public bool Succeeded => ExitCode == 0;

    /// <summary>
    /// Creates the stable external-process failure shape for a nonzero process exit.
    /// </summary>
    public ExternalProcessFailure ToFailure()
    {
        if (Succeeded)
        {
            throw new InvalidOperationException("A successful process result does not represent a failure.");
        }

        return new ExternalProcessFailure(ExternalProcessFailureKind.ProcessExited, this);
    }
}

/// <summary>
/// Categorizes failures that a backend can map to the external-process CLI exit contract.
/// </summary>
internal enum ExternalProcessFailureKind
{
    /// <summary>
    /// The configured executable could not be started.
    /// </summary>
    LaunchFailed,

    /// <summary>
    /// The process exited with a nonzero exit code.
    /// </summary>
    ProcessExited,

    /// <summary>
    /// The runner could not complete process execution or bounded output capture.
    /// </summary>
    ExecutionFailed,
}

/// <summary>
/// A safe, typed external-process failure that excludes unbounded child output and raw exception details.
/// </summary>
internal sealed class ExternalProcessFailure
{
    internal ExternalProcessFailure(ExternalProcessFailureKind kind, ExternalProcessResult? result = null)
    {
        if (!Enum.IsDefined<ExternalProcessFailureKind>(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (kind == ExternalProcessFailureKind.ProcessExited && result is null)
        {
            throw new ArgumentNullException(nameof(result));
        }

        if (kind != ExternalProcessFailureKind.ProcessExited && result is not null)
        {
            throw new ArgumentException("Only a process-exit failure may include a process result.", nameof(result));
        }

        Kind = kind;
        Result = result;
    }

    /// <summary>
    /// Gets the stable failure category.
    /// </summary>
    public ExternalProcessFailureKind Kind { get; }

    /// <summary>
    /// Gets the bounded process result when the process itself exited unsuccessfully.
    /// </summary>
    public ExternalProcessResult? Result { get; }

    /// <summary>
    /// Maps this infrastructure failure into the shared CLI failure contract without exposing child output.
    /// </summary>
    public OperationFailure ToOperationFailure()
    {
        return Kind switch
        {
            ExternalProcessFailureKind.LaunchFailed => new OperationFailure(
                ExitCode.ExternalProcess,
                "external-process-launch-failed",
                "The external process could not be started."),
            ExternalProcessFailureKind.ProcessExited => new OperationFailure(
                ExitCode.ExternalProcess,
                "external-process-failed",
                "The external process did not complete successfully."),
            ExternalProcessFailureKind.ExecutionFailed => new OperationFailure(
                ExitCode.ExternalProcess,
                "external-process-execution-failed",
                "The external process could not be completed."),
            _ => throw new InvalidOperationException("The external-process failure kind is not supported."),
        };
    }

    /// <summary>
    /// Creates the shared exception form for a backend that reports this failure directly.
    /// </summary>
    public OperationFailureException ToOperationFailureException()
    {
        return new OperationFailureException(ToOperationFailure());
    }
}

/// <summary>
/// Signals a launch or execution failure while retaining a typed, safe failure payload for a backend.
/// </summary>
internal sealed class ExternalProcessFailureException : Exception
{
    internal ExternalProcessFailureException(ExternalProcessFailure failure, Exception? innerException = null)
        : base(GetMessage(failure), innerException)
    {
        Failure = failure;
    }

    /// <summary>
    /// Gets the stable failure payload.
    /// </summary>
    public ExternalProcessFailure Failure { get; }

    private static string GetMessage(ExternalProcessFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return failure.ToOperationFailure().Message;
    }
}
