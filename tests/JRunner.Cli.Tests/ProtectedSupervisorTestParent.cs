using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using JRunner.Cli.Infrastructure;
using JRunner.Core.Contracts;
using ProtectedDeployment = JRunner.Cli.Tests.ExternalProcessSupervisorLaunchTests.ProtectedRunnableSupervisorDeployment;

// Loaded explicitly only by the protected test parent, never by a production deployment.
// Hosting the genuine testhost image preserves the runner's existing parent authentication.
[SupportedOSPlatform("linux")]
public static class StartupHook
{
    public static void Initialize() => JRunner.Cli.Tests.ProtectedSupervisorTestParent.RunParent();
}

namespace JRunner.Cli.Tests
{
    /// <summary>
    /// Runs the actual runner from a complete protected testhost deployment. The ready/continue
    /// handshake freezes its healthy plan before a test can replace a selected executable alias.
    /// One original native graph binds both launch chains; every process still revalidates that graph.
    /// </summary>
    [SupportedOSPlatform("linux")]
    internal sealed class ProtectedSupervisorTestParent : IAsyncDisposable
    {
        private const string Ready = "protected-supervisor-ready";
        private const string Continue = "run";
        private readonly Process _process;
        private readonly Task<string> _standardError;

        private ProtectedSupervisorTestParent(Process process)
        {
            _process = process;
            _standardError = process.StandardError.ReadToEndAsync();
        }

        internal static async Task<ProtectedSupervisorTestParent> StartAsync(
            ProtectedDeployment deployment, ExternalProcessInvocation invocation, CancellationToken cancellationToken)
        {
            ExternalProcessSupervisorLaunch launch = invocation.TrustedSupervisorLaunch
                ?? throw new InvalidOperationException("The protected test invocation has no bound supervisor.");
            NativeDependencyClosure native = invocation.TrustedNativeClosure ?? throw NativeElfReader.Failure();
            ExternalProcessSupervisorLaunch parentLaunch = deployment.PrepareTestParent();
            native.ValidateInputs(parentLaunch.NativeInputs);
            var startInfo = new ProcessStartInfo
            {
                FileName = parentLaunch.HostArguments[0],
                WorkingDirectory = deployment.Root,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            for (int index = 1; index < parentLaunch.HostArguments.Count; index++)
            {
                startInfo.ArgumentList.Add(parentLaunch.HostArguments[index]);
            }
            native.ApplyClosedEnvironment(startInfo);
            parentLaunch.ApplyClosedRuntimeEnvironment(startInfo);
            // This test-only parent hook is itself a real asset in the protected test manifest.
            // The actual marked child launch must remove it with the unchanged production policy.
            startInfo.Environment["DOTNET_STARTUP_HOOKS"] = deployment.TestAssemblyPath;
            parentLaunch.Revalidate();
            native.Revalidate();
            native.ValidateExecutable(parentLaunch.HostArguments[0]);
            Process process = Process.Start(startInfo)
                ?? throw new IOException("The protected supervisor test parent could not start.");
            var parent = new ProtectedSupervisorTestParent(process);
            try
            {
                var request = new ParentRequest(
                    launch.ProcessGroupLauncher, launch.HostArguments, launch.AssemblyPath, launch.RuntimeDirectory,
                    launch.RuntimeRoot, launch.HostTargetPath,
                    invocation.ExecutablePath, invocation.Arguments, invocation.WorkingDirectory, invocation.EnvironmentUpdates,
                    native.ToBinding());
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), cancellationToken);
                await process.StandardInput.FlushAsync(cancellationToken);
                if (!string.Equals(await process.StandardOutput.ReadLineAsync(cancellationToken), Ready, StringComparison.Ordinal))
                {
                    throw new IOException("The protected supervisor test parent could not prepare its bound plan.");
                }
                return parent;
            }
            catch
            {
                await parent.DisposeAsync();
                throw;
            }
        }

        internal async Task<ParentResponse> ExecuteAsync(CancellationToken cancellationToken)
        {
            await _process.StandardInput.WriteLineAsync(Continue.AsMemory(), cancellationToken);
            await _process.StandardInput.FlushAsync(cancellationToken);
            string responseText = await _process.StandardOutput.ReadLineAsync(cancellationToken)
                ?? throw new IOException("The protected supervisor test parent did not return a result.");
            ParentResponse response = JsonSerializer.Deserialize<ParentResponse>(responseText)
                ?? throw new IOException("The protected supervisor test parent returned no result.");
            await _process.WaitForExitAsync(cancellationToken);
            if (_process.ExitCode != 0 || (await _standardError).Length != 0)
            {
                throw new IOException("The protected supervisor test parent could not complete its invocation.");
            }
            return response;
        }

        internal static void RunParent()
        {
            try
            {
                ParentRequest request = JsonSerializer.Deserialize<ParentRequest>(Console.ReadLine()
                    ?? throw new IOException("The protected test request is unavailable."))
                    ?? throw new IOException("The protected test request is unavailable.");
                NativeDependencyClosure native = NativeDependencyClosure.FromBinding(request.NativeClosure);
                ExternalProcessSupervisorLaunch launch = ExternalProcessSupervisorLaunch.Prepare(
                    request.ProcessGroupLauncher, request.HostArguments, request.AssemblyPath, request.RuntimeDirectory);
                if (!string.Equals(launch.RuntimeRoot, request.RuntimeRoot, StringComparison.Ordinal)
                    || !string.Equals(launch.HostTargetPath, request.HostTargetPath, StringComparison.Ordinal))
                {
                    throw NativeElfReader.Failure();
                }
                native.ValidateInputs(launch.NativeInputs);
                native.ValidateExecutable(request.ExecutablePath);
                var invocation = new ExternalProcessInvocation(
                    request.ExecutablePath, request.Arguments, request.WorkingDirectory, request.EnvironmentUpdates)
                {
                    TrustedSupervisorLaunch = launch,
                    TrustedNativeClosure = native,
                };
                Console.WriteLine(Ready);
                Console.Out.Flush();
                if (!string.Equals(Console.ReadLine(), Continue, StringComparison.Ordinal))
                {
                    throw new IOException("The protected test continuation is unavailable.");
                }

                ParentResponse response;
                try
                {
                    ExternalProcessResult result = new ExternalProcessRunner().RunAsync(invocation).GetAwaiter().GetResult();
                    response = new ParentResponse(new CapturedResult(
                        result.ExitCode, result.StandardOutput, result.StandardOutputTruncated, result.StandardOutputByteCount,
                        result.StandardError, result.StandardErrorTruncated, result.StandardErrorByteCount),
                        null, null, null, false, false, null);
                }
                catch (OperationFailureException failure)
                {
                    string diagnostics = failure.ToString();
                    response = new ParentResponse(null, null, failure.Code, failure.Kind,
                        failure.InnerException is not null,
                        diagnostics.Contains(launch.ProcessGroupLauncher, StringComparison.Ordinal)
                            || diagnostics.Contains(launch.HostArguments[0], StringComparison.Ordinal)
                            || diagnostics.Contains(launch.AssemblyPath, StringComparison.Ordinal),
                        failure.Message);
                }
                catch (ExternalProcessFailureException failure)
                {
                    OperationFailureException? prerequisite = failure.InnerException as OperationFailureException;
                    string diagnostics = failure.ToString();
                    response = new ParentResponse(null, failure.Failure.Kind, prerequisite?.Code, prerequisite?.Kind,
                        prerequisite?.InnerException is not null,
                        diagnostics.Contains(launch.ProcessGroupLauncher, StringComparison.Ordinal)
                            || diagnostics.Contains(launch.HostArguments[0], StringComparison.Ordinal)
                            || diagnostics.Contains(launch.AssemblyPath, StringComparison.Ordinal),
                        failure.Message);
                }
                Console.WriteLine(JsonSerializer.Serialize(response));
                Console.Out.Flush();
                Environment.Exit(0);
            }
            catch (Exception)
            {
                // Unexpected harness failures are failures, never a successful or untrusted fallback.
                // Do not expose path-rich startup, filesystem, JSON, or child exceptions on this channel.
                Console.Error.WriteLine("The protected supervisor test parent could not complete.");
                Environment.Exit(1);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
                await _process.WaitForExitAsync();
                await _standardError;
            }
            finally
            {
                _process.Dispose();
            }
        }

        private sealed record ParentRequest(
            string ProcessGroupLauncher, IReadOnlyList<string> HostArguments, string AssemblyPath, string RuntimeDirectory,
            string RuntimeRoot, string HostTargetPath,
            string ExecutablePath, IReadOnlyList<string> Arguments, string WorkingDirectory,
            IReadOnlyDictionary<string, string?> EnvironmentUpdates, NativeDependencyClosureBinding NativeClosure);

        internal sealed record CapturedResult(
            int ExitCode, string StandardOutput, bool StandardOutputTruncated, int StandardOutputByteCount,
            string StandardError, bool StandardErrorTruncated, int StandardErrorByteCount);

        internal sealed record ParentResponse(
            CapturedResult? Result, ExternalProcessFailureKind? FailureKind, ExitCode? PrerequisiteCode,
            string? PrerequisiteKind, bool PrerequisiteHasInnerException, bool DiagnosticsContainBoundPath,
            string? FailureMessage);
    }
}
