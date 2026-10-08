using System.Text;
using JRunner.Cli.Infrastructure;
using JRunner.Core.Contracts;

namespace JRunner.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (ExternalProcessRunner.IsSupervisorInvocation(args))
        {
            return await ExternalProcessRunner.RunSupervisorAsync(args).ConfigureAwait(false);
        }

        using var cancellationSource = new CancellationTokenSource();

        ConsoleCancelEventHandler cancellationHandler = (_, eventArguments) =>
        {
            eventArguments.Cancel = true;

            try
            {
                cancellationSource.Cancel();
            }
            catch (AggregateException)
            {
                // Cancellation callbacks must not let SIGINT bypass normal cleanup.
            }
            catch (ObjectDisposedException)
            {
                // A late signal can race with process teardown after the handler is removed.
            }
        };

        Console.CancelKeyPress += cancellationHandler;

        var exitCode = (int)ExitCode.InputOutput;

        try
        {
            var setupFailure = ConfigureOutputEncoding();
            if (setupFailure is not null)
            {
                exitCode = await CliRuntime.RenderFailureAsync(
                        CliApplication.IsJsonRequested(args),
                        Console.Out,
                        Console.Error,
                        setupFailure)
                    .ConfigureAwait(false);
            }
            else
            {
                exitCode = await CliApplication.RunAsync(
                        args,
                        Console.In,
                        Console.Out,
                        Console.Error,
                        cancellationSource.Token)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            Console.CancelKeyPress -= cancellationHandler;
        }

        return exitCode;
    }

    private static OperationFailure? ConfigureOutputEncoding()
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return new OperationFailure(
                ExitCode.DeviceUnavailable,
                "permission-denied",
                "Permission was denied while accessing a required resource.");
        }
        catch (IOException)
        {
            return new OperationFailure(
                ExitCode.InputOutput,
                "io-error",
                "An input or output operation failed.");
        }
        catch (Exception)
        {
            return new OperationFailure(
                ExitCode.InputOutput,
                "unexpected-error",
                "The operation could not be completed.");
        }
    }
}
