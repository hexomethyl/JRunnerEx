using System.Text.Json;
using JRunner.Cli.Infrastructure;
using JRunner.Core.Contracts;
using Xunit;

namespace JRunner.Cli.Tests;

public sealed class CliRuntimeTests
{
    [Fact]
    public async Task Typed_failure_maps_to_a_stable_error_envelope()
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await CliRuntime.ExecuteAsync(
            new CliExecution<object?>(
                jsonRequested: true,
                static (_, _) => Task.FromException<OperationResult<object?>>(
                    new OperationFailureException(
                        ExitCode.InvalidData,
                        "invalid-image",
                        "The image data is invalid.")),
                static (_, _, _) => Task.CompletedTask),
            standardOutput,
            standardError);

        Assert.Equal((int)ExitCode.InvalidData, exitCode);

        using var document = JsonDocument.Parse(standardOutput.ToString());
        var error = document.RootElement.GetProperty("error");
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal((int)ExitCode.InvalidData, error.GetProperty("code").GetInt32());
        Assert.Equal("invalid-image", error.GetProperty("kind").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
    }

    [Fact]
    public async Task Unexpected_io_failure_maps_to_a_sanitized_io_error_envelope()
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await CliRuntime.ExecuteAsync(
            new CliExecution<object?>(
                jsonRequested: true,
                static (_, _) => Task.FromException<OperationResult<object?>>(
                    new IOException("do-not-leak-this-detail")),
                static (_, _, _) => Task.CompletedTask),
            standardOutput,
            standardError);

        Assert.Equal((int)ExitCode.InputOutput, exitCode);

        using var document = JsonDocument.Parse(standardOutput.ToString());
        var error = document.RootElement.GetProperty("error");
        Assert.Equal((int)ExitCode.InputOutput, error.GetProperty("code").GetInt32());
        Assert.Equal("io-error", error.GetProperty("kind").GetString());
        Assert.DoesNotContain("do-not-leak-this-detail", standardOutput.ToString());
        Assert.DoesNotContain("do-not-leak-this-detail", standardError.ToString());
    }

    [Fact]
    public async Task Failed_json_output_does_not_append_a_second_error_envelope()
    {
        using var standardOutput = new PartialWriteTextWriter();
        using var standardError = new StringWriter();

        var exitCode = await CliRuntime.ExecuteAsync(
            new CliExecution<string>(
                jsonRequested: true,
                static (_, _) => Task.FromResult(OperationResult.Success("value")),
                static (_, _, _) => Task.CompletedTask),
            standardOutput,
            standardError);

        Assert.Equal((int)ExitCode.InputOutput, exitCode);
        Assert.Equal("{", standardOutput.ToString());
        Assert.DoesNotContain("\"error\"", standardOutput.ToString());
        Assert.False(string.IsNullOrWhiteSpace(standardError.ToString()));
    }

    [Fact]
    public async Task Cancellation_maps_to_the_cancellation_error_envelope()
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await CliRuntime.ExecuteAsync(
            new CliExecution<object?>(
                jsonRequested: true,
                static (_, _) => Task.FromException<OperationResult<object?>>(new OperationCanceledException()),
                static (_, _, _) => Task.CompletedTask),
            standardOutput,
            standardError);

        Assert.Equal((int)ExitCode.Cancelled, exitCode);

        using var document = JsonDocument.Parse(standardOutput.ToString());
        var error = document.RootElement.GetProperty("error");
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal((int)ExitCode.Cancelled, error.GetProperty("code").GetInt32());
        Assert.Equal("cancelled", error.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Committed_success_remains_successful_when_cancellation_arrives_after_commit()
    {
        using var cancellationSource = new CancellationTokenSource();
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        int exitCode = await CliRuntime.ExecuteAsync(
            new CliExecution<string>(
                jsonRequested: true,
                (_, _) =>
                {
                    cancellationSource.Cancel();
                    return Task.FromResult(OperationResult.Success("published"));
                },
                static (_, _, _) => Task.CompletedTask,
                successIsCommitted: true),
            standardOutput,
            standardError,
            cancellationSource.Token);

        Assert.Equal((int)ExitCode.Success, exitCode);
        Assert.Equal(string.Empty, standardError.ToString());
        using var document = JsonDocument.Parse(standardOutput.ToString());
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("published", document.RootElement.GetProperty("result").GetString());
    }

    [Fact]
    public async Task Progress_stays_on_standard_error_for_json_results()
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await CliRuntime.ExecuteAsync(
            new CliExecution<ComparisonResult>(
                jsonRequested: true,
                static (progress, _) =>
                {
                    progress.Report(
                        new OperationProgress(
                            "comparing-images",
                            "Comparing images.",
                            completed: 1,
                            total: 2));
                    progress.Report(new OperationProgress("copying-data", "Copying data.", completed: 3));
                    progress.Report(new OperationProgress("sizing-input", "Sizing input.", total: 9));
                    return Task.FromResult(OperationResult.Negative(new ComparisonResult(false)));
                },
                static (_, _, _) => Task.CompletedTask),
            standardOutput,
            standardError);

        Assert.Equal((int)ExitCode.CompletedNegativeResult, exitCode);
        var diagnostics = standardError.ToString();
        Assert.Contains("Comparing images.", diagnostics);
        Assert.Contains("[comparing-images]", diagnostics);
        Assert.Contains("completed: 3", diagnostics);
        Assert.Contains("total: 9", diagnostics);

        using var document = JsonDocument.Parse(standardOutput.ToString());
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(document.RootElement.GetProperty("result").GetProperty("equal").GetBoolean());
    }

    private sealed class PartialWriteTextWriter : StringWriter
    {
        public override Task WriteAsync(string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                Write(value[0]);
            }

            return Task.FromException(new IOException("simulated output failure"));
        }
    }

    public sealed record ComparisonResult(bool Equal);
}
