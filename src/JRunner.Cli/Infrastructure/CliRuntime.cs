using System.Text.Json;
using System.Text.Json.Serialization;
using JRunner.Core.Contracts;

namespace JRunner.Cli.Infrastructure;

/// <summary>
/// Renders completed operations and expected failures through the native CLI contract.
/// </summary>
public static class CliRuntime
{
    private const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower),
        },
    };

    /// <summary>
    /// Executes one command and writes either a human-readable result or one JSON envelope.
    /// </summary>
    public static async Task<int> ExecuteAsync<T>(
        CliExecution<T> execution,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        var progress = new StandardErrorProgress(standardError);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await execution.ExecuteAsync(progress, cancellationToken).ConfigureAwait(false);
            if (result is null)
            {
                throw new InvalidOperationException("A CLI execution returned no result.");
            }

            if (!execution.SuccessIsCommitted)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (execution.JsonRequested)
            {
                var json = JsonSerializer.Serialize(
                    new SuccessEnvelope<T>(SchemaVersion, true, result.Result),
                    JsonOptions);
                if (!execution.SuccessIsCommitted)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                return await WriteJsonSuccessAsync(
                        standardOutput,
                        standardError,
                        json,
                        result.ExitCode)
                    .ConfigureAwait(false);
            }

            return await WriteHumanSuccessAsync(
                    execution,
                    result.Result,
                    standardOutput,
                    standardError,
                    execution.SuccessIsCommitted ? CancellationToken.None : cancellationToken,
                    result.ExitCode)
                .ConfigureAwait(false);
        }
        catch (OperationFailureException exception)
        {
            return await RenderFailureAsync(
                    execution.JsonRequested,
                    standardOutput,
                    standardError,
                    exception.Failure)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return await RenderErrorAsync(
                    execution.JsonRequested,
                    standardOutput,
                    standardError,
                    ExitCode.Cancelled,
                    "cancelled",
                    "The operation was cancelled.")
                .ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        {
            return await RenderFailureAsync(
                    execution.JsonRequested,
                    standardOutput,
                    standardError,
                    new OperationFailure(
                        ExitCode.DeviceUnavailable,
                        "permission-denied",
                        "Permission was denied while accessing a required resource."))
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
            return await RenderFailureAsync(
                    execution.JsonRequested,
                    standardOutput,
                    standardError,
                    new OperationFailure(
                        ExitCode.InputOutput,
                        "io-error",
                        "An input or output operation failed."))
                .ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            return await RenderFailureAsync(
                    execution.JsonRequested,
                    standardOutput,
                    standardError,
                    new OperationFailure(
                        ExitCode.MissingPrerequisite,
                        "unsupported-operation",
                        "The requested operation is not supported."))
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return await RenderFailureAsync(
                    execution.JsonRequested,
                    standardOutput,
                    standardError,
                    new OperationFailure(
                        ExitCode.InputOutput,
                        "unexpected-error",
                        "The operation could not be completed."))
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Renders an expected failure through the same stream contract used for command execution.
    /// </summary>
    public static Task<int> RenderFailureAsync(
        bool jsonRequested,
        TextWriter standardOutput,
        TextWriter standardError,
        OperationFailure failure)
    {
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);
        ArgumentNullException.ThrowIfNull(failure);

        return RenderErrorAsync(
            jsonRequested,
            standardOutput,
            standardError,
            failure.Code,
            failure.Kind,
            failure.Message);
    }

    private static async Task<int> WriteJsonSuccessAsync(
        TextWriter standardOutput,
        TextWriter standardError,
        string json,
        ExitCode exitCode)
    {
        try
        {
            await standardOutput.WriteAsync(json).ConfigureAwait(false);
            return (int)exitCode;
        }
        catch (Exception exception)
        {
            return await RenderOutputExceptionAsync(standardError, exception).ConfigureAwait(false);
        }
    }

    private static async Task<int> WriteHumanSuccessAsync<T>(
        CliExecution<T> execution,
        T result,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken,
        ExitCode exitCode)
    {
        try
        {
            await execution.WriteHumanResultAsync(result, standardOutput, cancellationToken).ConfigureAwait(false);
            return (int)exitCode;
        }
        catch (Exception exception)
        {
            return await RenderOutputExceptionAsync(standardError, exception).ConfigureAwait(false);
        }
    }

    private static Task<int> RenderOutputExceptionAsync(TextWriter standardError, Exception exception)
    {
        return exception switch
        {
            OperationCanceledException => RenderOutputFailureAsync(
                standardError,
                ExitCode.Cancelled,
                "The operation was cancelled."),
            UnauthorizedAccessException => RenderOutputFailureAsync(
                standardError,
                ExitCode.DeviceUnavailable,
                "Permission was denied while accessing a required resource."),
            IOException => RenderOutputFailureAsync(
                standardError,
                ExitCode.InputOutput,
                "An input or output operation failed."),
            _ => RenderOutputFailureAsync(
                standardError,
                ExitCode.InputOutput,
                "The operation could not be completed."),
        };
    }

    private static async Task<int> RenderErrorAsync(
        bool jsonRequested,
        TextWriter standardOutput,
        TextWriter standardError,
        ExitCode code,
        string kind,
        string message)
    {
        try
        {
            if (jsonRequested)
            {
                var json = JsonSerializer.Serialize(
                    new FailureEnvelope(
                        SchemaVersion,
                        false,
                        new ErrorEnvelope((int)code, kind, message)),
                    JsonOptions);
                await standardOutput.WriteAsync(json).ConfigureAwait(false);
            }

            await standardError.WriteLineAsync(message).ConfigureAwait(false);
        }
        catch (Exception)
        {
            TryWriteDiagnostic(standardError, "Unable to write command output.");
        }

        return (int)code;
    }

    private static async Task<int> RenderOutputFailureAsync(
        TextWriter standardError,
        ExitCode code,
        string message)
    {
        try
        {
            await standardError.WriteLineAsync(message).ConfigureAwait(false);
        }
        catch (Exception)
        {
            TryWriteDiagnostic(standardError, "Unable to write command output.");
        }

        return (int)code;
    }

    private static void TryWriteDiagnostic(TextWriter standardError, string message)
    {
        try
        {
            standardError.WriteLine(message);
        }
        catch (Exception)
        {
            // There is no reliable stream left for a diagnostic.
        }
    }

    private sealed class StandardErrorProgress : IProgress<OperationProgress>
    {
        private readonly object _writeLock = new();
        private readonly TextWriter _standardError;

        public StandardErrorProgress(TextWriter standardError)
        {
            _standardError = standardError;
        }

        public void Report(OperationProgress value)
        {
            ArgumentNullException.ThrowIfNull(value);

            lock (_writeLock)
            {
                _standardError.WriteLine(Format(value));
            }
        }

        private static string Format(OperationProgress value)
        {
            var severity = value.Severity switch
            {
                OperationDiagnosticSeverity.Information => "info",
                OperationDiagnosticSeverity.Warning => "warning",
                OperationDiagnosticSeverity.Error => "error",
                _ => "info",
            };
            var prefix = $"{severity} [{value.Kind}]: {value.Message}";

            if (value.Completed is { } completed && value.Total is { } total)
            {
                return $"{prefix} ({completed}/{total})";
            }

            if (value.Completed is { } completedOnly)
            {
                return $"{prefix} (completed: {completedOnly})";
            }

            if (value.Total is { } totalOnly)
            {
                return $"{prefix} (total: {totalOnly})";
            }

            return prefix;
        }
    }

    private sealed record SuccessEnvelope<T>(int SchemaVersion, bool Ok, T Result);

    private sealed record FailureEnvelope(int SchemaVersion, bool Ok, ErrorEnvelope Error);

    private sealed record ErrorEnvelope(int Code, string Kind, string Message);
}
