using JRunner.Cli.Infrastructure;
using JRunner.Core;
using JRunner.Core.Contracts;

namespace JRunner.Cli;

public static class CliApplication
{
    public static Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(
            arguments,
            Console.In,
            standardOutput,
            standardError,
            cancellationToken);
    }

    internal static Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        TextReader standardInput,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(standardInput);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        var jsonRequested = IsJsonRequested(arguments);

        if (IsVersionRequest(arguments))
        {
            return CliRuntime.ExecuteAsync(
                new CliExecution<string>(
                    jsonRequested,
                    static (_, _) => Task.FromResult(OperationResult.Success(JRunnerVersion.Display)),
                    static (result, output, _) => output.WriteLineAsync(result)),
                standardOutput,
                standardError,
                cancellationToken);
        }

        return CliCommandRouter.RunAsync(
            arguments,
            standardOutput,
            standardError,
            cancellationToken,
            standardInput: standardInput);
    }

    internal static bool IsJsonRequested(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            if (string.Equals(arguments[index], "--json", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsVersionRequest(IReadOnlyList<string> arguments)
    {
        return arguments.Count switch
        {
            1 => string.Equals(arguments[0], "--version", StringComparison.Ordinal),
            2 =>
                (string.Equals(arguments[0], "--version", StringComparison.Ordinal) &&
                 string.Equals(arguments[1], "--json", StringComparison.Ordinal)) ||
                (string.Equals(arguments[0], "--json", StringComparison.Ordinal) &&
                 string.Equals(arguments[1], "--version", StringComparison.Ordinal)),
            _ => false,
        };
    }
}
