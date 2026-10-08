using JRunner.Core.Contracts;

namespace JRunner.Cli.Infrastructure;

/// <summary>
/// Describes one command execution independently of argument parsing and console ownership.
/// </summary>
public sealed class CliExecution<T>
{
    public CliExecution(
        bool jsonRequested,
        Func<IProgress<OperationProgress>, CancellationToken, Task<OperationResult<T>>> executeAsync,
        Func<T, TextWriter, CancellationToken, Task> writeHumanResultAsync,
        bool successIsCommitted = false)
    {
        ArgumentNullException.ThrowIfNull(executeAsync);
        ArgumentNullException.ThrowIfNull(writeHumanResultAsync);

        JsonRequested = jsonRequested;
        ExecuteAsync = executeAsync;
        WriteHumanResultAsync = writeHumanResultAsync;
        SuccessIsCommitted = successIsCommitted;
    }

    public bool JsonRequested { get; }

    public Func<IProgress<OperationProgress>, CancellationToken, Task<OperationResult<T>>> ExecuteAsync { get; }

    public Func<T, TextWriter, CancellationToken, Task> WriteHumanResultAsync { get; }

    /// <summary>
    /// Gets whether a successful execution has already published an external result and therefore
    /// establishes the cancellation boundary before rendering its result.
    /// </summary>
    public bool SuccessIsCommitted { get; }
}
