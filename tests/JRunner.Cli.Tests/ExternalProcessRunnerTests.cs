using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JRunner.Cli.Infrastructure;
using JRunner.Core.Contracts;
using JRunner.Core.Nand.Security;
using Xunit;
using ProtectedDirectory = JRunner.Cli.Tests.WineXeBuildBackendTests.TemporaryDirectory;
using ProtectedSupervisorDeployment = JRunner.Cli.Tests.ExternalProcessSupervisorLaunchTests.ProtectedSupervisorDeployment;
using ProtectedRunnableSupervisorDeployment = JRunner.Cli.Tests.ExternalProcessSupervisorLaunchTests.ProtectedRunnableSupervisorDeployment;

namespace JRunner.Cli.Tests;

public sealed class ExternalProcessRunnerTests
{
    private const string ShellPath = "/bin/sh";
    private const string PrintfPath = "/usr/bin/printf";
    private const string WordCountPath = "/usr/bin/wc";
    // The real frozen loader/runtime graph is revalidated in each protected process, not cached.
    // Isolated integration launches take about a minute; keep setup/execution bounded under suite contention.
    private static readonly TimeSpan ProtectedSupervisorTimeout = TimeSpan.FromMinutes(3);

    [Fact]
    public async Task Runner_passes_argument_tokens_without_interpreting_shell_text()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string markerPath = temporaryDirectory.File("shell-was-not-invoked");
        string argument = $"; touch {markerPath}";
        IExternalProcessRunner runner = new ExternalProcessRunner();

        ExternalProcessResult result = await runner.RunAsync(
            new ExternalProcessInvocation(
                PrintfPath,
                ["%s", argument],
                temporaryDirectory.Path));

        Assert.True(result.Succeeded);
        Assert.Equal(argument, result.StandardOutput);
        Assert.Equal(string.Empty, result.StandardError);
        Assert.False(File.Exists(markerPath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cpu_key_environment_is_consumed_before_later_supervised_children_start(bool validValue)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var standardInput = new StringReader(string.Empty);
        var runner = new ExternalProcessRunner();
        var resolver = new CpuKeySourceResolver();
        const string cpuKeyText = "00112233445566778899AABBCCDDEEFF";
        const string controlValue = "unrelated-control-sentinel";
        string suffix = Guid.NewGuid().ToString("N");
        string selectedVariable = $"JRUNNER_CPU_KEY_INHERITANCE_{suffix}";
        string controlVariable = $"JRUNNER_CPU_KEY_CONTROL_{suffix}";
        string sourceValue = validValue ? cpuKeyText : cpuKeyText + "X";
        var selection = new CpuKeySourceSelection(null, selectedVariable, readFromStandardInput: false);

        try
        {
            Environment.SetEnvironmentVariable(selectedVariable, sourceValue);
            Environment.SetEnvironmentVariable(controlVariable, controlValue);

            // Establish that the real supervisor and child inherit both current-process variables.
            await AssertChildEnvironmentAsync(selectedIsPresent: true);

            if (validValue)
            {
                CpuKey? result = await resolver.ResolveAsync(selection, CpuKeySourcePolicy.Required, standardInput);

                Assert.True(result.HasValue);
                Assert.Equal(CpuKey.Parse(cpuKeyText), result.Value);
            }
            else
            {
                OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
                    () => resolver.ResolveAsync(selection, CpuKeySourcePolicy.Required, standardInput).AsTask());

                Assert.Equal(ExitCode.Usage, exception.Code);
                Assert.True(
                    string.Equals(exception.Kind, "invalid-cpu-key", StringComparison.Ordinal),
                    "Invalid CPU-key parsing must preserve its stable failure kind.");
                Assert.True(
                    string.Equals(
                        exception.Message,
                        "The CPU key source must contain exactly one 32-character hexadecimal CPU key.",
                        StringComparison.Ordinal),
                    "Invalid CPU-key parsing must use its stable redacted diagnostic.");
                Assert.False(
                    exception.ToString().Contains(cpuKeyText, StringComparison.OrdinalIgnoreCase),
                    "Invalid CPU-key parsing must not expose the selected environment value.");
            }

            bool selectedWasRemoved = Environment.GetEnvironmentVariable(selectedVariable) is null;
            bool controlWasPreserved = string.Equals(
                controlValue,
                Environment.GetEnvironmentVariable(controlVariable),
                StringComparison.Ordinal);

            // No environment overrides: removal must happen in the caller, before this new supervisor starts.
            await AssertChildEnvironmentAsync(selectedIsPresent: false);

            Assert.True(selectedWasRemoved, "The selected CPU-key variable must be absent from the current process.");
            Assert.True(controlWasPreserved, "The unrelated control variable must remain unchanged.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(selectedVariable, null);
            Environment.SetEnvironmentVariable(controlVariable, null);
        }

        async Task AssertChildEnvironmentAsync(bool selectedIsPresent)
        {
            // The generated names are safe shell identifiers. Expand only presence, never the CPU-key value.
            const string script = """
                eval 'selected_is_set=${'"$1"'+set}'
                eval 'control_value=${'"$2"'-}'
                [ "${selected_is_set:-unset}" = "$3" ] || exit 41
                [ "$control_value" = "$4" ] || exit 42
                """;
            ExternalProcessResult result = await runner.RunAsync(
                new ExternalProcessInvocation(
                    ShellPath,
                    [
                        "-c",
                        script,
                        "check-cpu-key-environment",
                        selectedVariable,
                        controlVariable,
                        selectedIsPresent ? "set" : "unset",
                        controlValue,
                    ],
                    temporaryDirectory.Path));

            Assert.Equal(0, result.ExitCode);
            Assert.True(result.StandardOutput.Length == 0, "The environment probe must not print values.");
            Assert.True(result.StandardError.Length == 0, "The environment probe must not print diagnostics.");
        }
    }

    [Fact]
    public async Task Runner_writes_binary_standard_input_and_closes_the_child_stream()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        byte[] standardInput = [0x00, 0x01, 0x02];
        var runner = new ExternalProcessRunner();

        ExternalProcessResult result = await runner.RunAsync(
            new ExternalProcessInvocation(
                WordCountPath,
                ["-c"],
                temporaryDirectory.Path,
                standardInput: standardInput));

        Assert.True(result.Succeeded);
        Assert.Equal("3", result.StandardOutput.Trim());
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task Runner_writes_utf8_standard_input_text()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var runner = new ExternalProcessRunner();

        ExternalProcessResult result = await runner.RunAsync(
            new ExternalProcessInvocation(
                WordCountPath,
                ["-c"],
                temporaryDirectory.Path,
                standardInput: "enter\n"));

        Assert.True(result.Succeeded);
        Assert.Equal("6", result.StandardOutput.Trim());
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task Runner_captures_each_stream_to_its_configured_bound_and_retains_truncation_state()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var runner = new ExternalProcessRunner(maximumCapturedBytes: 3);

        ExternalProcessResult result = await runner.RunAsync(
            new ExternalProcessInvocation(
                ShellPath,
                [
                    "-c",
                    "printf '%s' \"$JRUNNER_EXTERNAL_PROCESS_TEST_VALUE\"; printf 'abcdef' >&2; exit 19",
                ],
                temporaryDirectory.Path,
                [new KeyValuePair<string, string?>("JRUNNER_EXTERNAL_PROCESS_TEST_VALUE", "abcdef")]));

        Assert.Equal(19, result.ExitCode);
        Assert.False(result.Succeeded);
        Assert.Equal("abc", result.StandardOutput);
        Assert.Equal("abc", result.StandardError);
        Assert.Equal(3, result.StandardOutputByteCount);
        Assert.Equal(3, result.StandardErrorByteCount);
        Assert.True(result.StandardOutputTruncated);
        Assert.True(result.StandardErrorTruncated);

        ExternalProcessFailure failure = result.ToFailure();
        Assert.Equal(ExternalProcessFailureKind.ProcessExited, failure.Kind);
        Assert.Same(result, failure.Result);
        Assert.Equal(ExitCode.ExternalProcess, failure.ToOperationFailure().Code);
        Assert.Equal("external-process-failed", failure.ToOperationFailure().Kind);
    }

    [Fact]
    public async Task Runner_drains_both_redirected_streams_while_the_child_is_running()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runner = new ExternalProcessRunner(maximumCapturedBytes: 8);

        ExternalProcessResult result = await runner.RunAsync(
            new ExternalProcessInvocation(
                ShellPath,
                ["-c", "head -c 131072 /dev/zero; head -c 131072 /dev/zero >&2"],
                temporaryDirectory.Path),
            timeout.Token);

        Assert.True(result.Succeeded);
        Assert.Equal(8, result.StandardOutputByteCount);
        Assert.Equal(8, result.StandardErrorByteCount);
        Assert.True(result.StandardOutputTruncated);
        Assert.True(result.StandardErrorTruncated);
    }

    [Fact]
    public async Task Cancellation_signals_the_complete_group_and_reaps_graceful_children_before_rethrowing()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        ProcessTreeScripts scripts = await CreateProcessTreeScriptsAsync(temporaryDirectory);
        var runner = new ExternalProcessRunner();
        Task<ExternalProcessResult> runTask = runner.RunAsync(scripts.Invocation, cancellationSource.Token);

        try
        {
            await WaitUntilAsync(() => File.Exists(scripts.ReadyPath));
            var elapsed = Stopwatch.StartNew();
            cancellationSource.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => runTask.WaitAsync(TimeSpan.FromSeconds(10)));
            elapsed.Stop();

            Assert.True(File.Exists(scripts.LauncherTerminationPath));
            Assert.True(File.Exists(scripts.ChildTerminationPath));
            Assert.True(File.Exists(scripts.ChildExitPath));
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"Graceful cleanup took {elapsed.Elapsed}.");
            await AssertTreeReapedAsync(scripts);
        }
        finally
        {
            await CancelAndObserveAsync(cancellationSource, runTask);
        }
    }

    [Fact]
    public async Task Cancellation_reserves_force_and_reap_time_within_the_five_second_cleanup_deadline()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        ProcessTreeScripts scripts = await CreateProcessTreeScriptsAsync(temporaryDirectory, stubbornChild: true);
        var runner = new ExternalProcessRunner();
        Task<ExternalProcessResult> runTask = runner.RunAsync(
            new ExternalProcessInvocation(
                scripts.Invocation.ExecutablePath,
                scripts.Invocation.Arguments,
                temporaryDirectory.Path,
                standardInput: new byte[ExternalProcessInvocation.MaximumStandardInputBytes]),
            cancellationSource.Token);

        try
        {
            await WaitUntilAsync(() => File.Exists(scripts.ReadyPath));
            var elapsed = Stopwatch.StartNew();
            cancellationSource.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => runTask.WaitAsync(TimeSpan.FromSeconds(10)));
            elapsed.Stop();

            Assert.True(File.Exists(scripts.LauncherTerminationPath));
            Assert.True(File.Exists(scripts.ChildTerminationPath));
            Assert.False(File.Exists(scripts.ChildExitPath));
            Assert.InRange(elapsed.Elapsed, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(6));
            await AssertTreeReapedAsync(scripts);
        }
        finally
        {
            await CancelAndObserveAsync(cancellationSource, runTask);
        }
    }

    [Fact]
    public async Task Cancellation_force_kills_and_reaps_descendants_after_the_wrapper_has_already_exited()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        ProcessTreeScripts scripts = await CreateProcessTreeScriptsAsync(
            temporaryDirectory,
            stubbornChild: true,
            exitLauncher: true);
        var runner = new ExternalProcessRunner();
        Task<ExternalProcessResult> runTask = runner.RunAsync(scripts.Invocation, cancellationSource.Token);

        try
        {
            await WaitUntilAsync(() => File.Exists(scripts.ReadyPath));
            int launcherId = int.Parse(await File.ReadAllTextAsync(scripts.LauncherPidPath));
            await WaitUntilAsync(() => !Directory.Exists($"/proc/{launcherId}"));
            Assert.False(runTask.IsCompleted);
            var elapsed = Stopwatch.StartNew();
            cancellationSource.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => runTask.WaitAsync(TimeSpan.FromSeconds(10)));
            elapsed.Stop();

            Assert.True(File.Exists(scripts.ChildTerminationPath));
            Assert.False(File.Exists(scripts.LauncherTerminationPath));
            Assert.InRange(elapsed.Elapsed, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(6));
            await AssertTreeReapedAsync(scripts);
        }
        finally
        {
            await CancelAndObserveAsync(cancellationSource, runTask);
        }
    }

    [Fact]
    public async Task Cancellation_reaps_an_adopted_descendant_that_started_a_different_session()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        ProcessTreeScripts scripts = await CreateProcessTreeScriptsAsync(
            temporaryDirectory,
            exitLauncher: true,
            changeChildSession: true);
        var runner = new ExternalProcessRunner();
        Task<ExternalProcessResult> runTask = runner.RunAsync(scripts.Invocation, cancellationSource.Token);

        try
        {
            await WaitUntilAsync(() => File.Exists(scripts.ReadyPath));
            int launcherId = int.Parse(await File.ReadAllTextAsync(scripts.LauncherPidPath));
            await WaitUntilAsync(() => !Directory.Exists($"/proc/{launcherId}"));
            cancellationSource.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => runTask.WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.True(File.Exists(scripts.ChildTerminationPath));
            Assert.True(File.Exists(scripts.ChildExitPath));
            await AssertTreeReapedAsync(scripts);
        }
        finally
        {
            await CancelAndObserveAsync(cancellationSource, runTask);
        }
    }

    [Fact]
    public async Task Concurrent_scopes_do_not_signal_or_reap_each_others_adopted_session_children()
    {
        using var firstDirectory = new TemporaryDirectory();
        using var secondDirectory = new TemporaryDirectory();
        using var firstCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var secondCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        ProcessTreeScripts firstScripts = await CreateProcessTreeScriptsAsync(
            firstDirectory,
            exitLauncher: true,
            changeChildSession: true,
            respondToRequests: true);
        ProcessTreeScripts secondScripts = await CreateProcessTreeScriptsAsync(
            secondDirectory,
            exitLauncher: true,
            changeChildSession: true,
            respondToRequests: true);
        var runner = new ExternalProcessRunner();
        Task<ExternalProcessResult> firstTask = runner.RunAsync(firstScripts.Invocation, firstCancellation.Token);
        Task<ExternalProcessResult> secondTask = runner.RunAsync(secondScripts.Invocation, secondCancellation.Token);

        try
        {
            await WaitUntilAsync(() => File.Exists(firstScripts.ReadyPath) && File.Exists(secondScripts.ReadyPath));
            int firstLauncher = int.Parse(await File.ReadAllTextAsync(firstScripts.LauncherPidPath));
            int secondLauncher = int.Parse(await File.ReadAllTextAsync(secondScripts.LauncherPidPath));
            int firstChild = int.Parse(await File.ReadAllTextAsync(firstScripts.ChildPidPath));
            int firstLeaf = int.Parse(await File.ReadAllTextAsync(firstScripts.LeafPidPath));
            int secondChild = int.Parse(await File.ReadAllTextAsync(secondScripts.ChildPidPath));
            int secondLeaf = int.Parse(await File.ReadAllTextAsync(secondScripts.LeafPidPath));
            await WaitUntilAsync(
                () => !Directory.Exists($"/proc/{firstLauncher}") && !Directory.Exists($"/proc/{secondLauncher}"));
            var firstIdentity = await AssertAdoptedSessionTreeAsync(firstChild, firstLeaf);
            var secondIdentity = await AssertAdoptedSessionTreeAsync(secondChild, secondLeaf);
            Assert.NotEqual(firstIdentity.SupervisorId, secondIdentity.SupervisorId);
            await AssertTreeWorkAsync(firstScripts, firstChild, firstLeaf, request: 7);
            await AssertTreeWorkAsync(secondScripts, secondChild, secondLeaf, request: 11);

            firstCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => firstTask.WaitAsync(TimeSpan.FromSeconds(10)));
            await AssertTreeReapedAsync(firstScripts);

            Assert.False(secondTask.IsCompleted);
            // A fresh result needs both surviving processes, not merely a pending runner task or
            // a ready marker left behind before the other invocation was cancelled.
            await AssertTreeWorkAsync(secondScripts, secondChild, secondLeaf, request: 17);
            Assert.Equal(secondIdentity, await AssertAdoptedSessionTreeAsync(secondChild, secondLeaf));
            Assert.True(Directory.Exists($"/proc/{secondChild}"));
            Assert.True(Directory.Exists($"/proc/{secondLeaf}"));
            Assert.False(File.Exists(secondScripts.ChildTerminationPath));

            secondCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => secondTask.WaitAsync(TimeSpan.FromSeconds(10)));
            await AssertTreeReapedAsync(secondScripts);
        }
        finally
        {
            await CancelAndObserveAsync(firstCancellation, firstTask);
            await CancelAndObserveAsync(secondCancellation, secondTask);
        }
    }

    [Fact]
    public async Task Runner_waits_for_wrapper_exited_descendants_even_after_their_redirected_streams_close()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string childPidPath = temporaryDirectory.File("child.pid");
        string launcherPidPath = temporaryDirectory.File("launcher.pid");
        string readyPath = temporaryDirectory.File("child.ready");
        string releasePath = temporaryDirectory.File("child.release");
        string childScriptPath = temporaryDirectory.File("child.sh");
        string launcherScriptPath = temporaryDirectory.File("launcher.sh");
        await File.WriteAllTextAsync(
            childScriptPath,
            $"""
            exec 1>/dev/null 2>/dev/null
            printf '%s' "$$" > {QuoteForShell(childPidPath)}
            printf ready > {QuoteForShell(readyPath)}
            while [ ! -f {QuoteForShell(releasePath)} ]; do sleep 0.01; done
            """);
        await File.WriteAllTextAsync(
            launcherScriptPath,
            $"""
            printf '%s' "$$" > {QuoteForShell(launcherPidPath)}
            {ShellPath} {QuoteForShell(childScriptPath)} &
            while [ ! -f {QuoteForShell(readyPath)} ]; do sleep 0.01; done
            exit 0
            """);
        var runner = new ExternalProcessRunner();
        Task<ExternalProcessResult> runTask = runner.RunAsync(
            new ExternalProcessInvocation(ShellPath, [launcherScriptPath], temporaryDirectory.Path),
            cancellationSource.Token);

        try
        {
            await WaitUntilAsync(() => File.Exists(readyPath));
            int launcherId = int.Parse(await File.ReadAllTextAsync(launcherPidPath));
            int childId = int.Parse(await File.ReadAllTextAsync(childPidPath));
            await WaitUntilAsync(() => !Directory.Exists($"/proc/{launcherId}"));
            Assert.False(runTask.IsCompleted);

            await File.WriteAllTextAsync(releasePath, "release");
            ExternalProcessResult result = await runTask.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(result.Succeeded);
            Assert.False(Directory.Exists($"/proc/{childId}"));
        }
        finally
        {
            await CancelAndObserveAsync(cancellationSource, runTask);
        }
    }

    [Fact]
    public async Task Runner_reaps_quick_exiting_changed_session_orphans_while_the_launcher_is_still_alive()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string childPidPath = temporaryDirectory.File("quick-child.pid");
        string childDonePath = temporaryDirectory.File("quick-child.done");
        string readyPath = temporaryDirectory.File("launcher.ready");
        string releasePath = temporaryDirectory.File("launcher.release");
        string childScriptPath = temporaryDirectory.File("quick-child.sh");
        string middleScriptPath = temporaryDirectory.File("middle.sh");
        string launcherScriptPath = temporaryDirectory.File("launcher.sh");
        await File.WriteAllTextAsync(
            childScriptPath,
            $"""
            exec 0</dev/null 1>/dev/null 2>/dev/null
            printf '%s' "$$" > {QuoteForShell(childPidPath)}
            printf done > {QuoteForShell(childDonePath)}
            exit 0
            """);
        await File.WriteAllTextAsync(
            middleScriptPath,
            $"""
            /usr/bin/setsid -- {ShellPath} {QuoteForShell(childScriptPath)} &
            exit 0
            """);
        await File.WriteAllTextAsync(
            launcherScriptPath,
            $"""
            {ShellPath} {QuoteForShell(middleScriptPath)} &
            middle=$!
            wait "$middle"
            while [ ! -f {QuoteForShell(childDonePath)} ]; do sleep 0.01; done
            printf ready > {QuoteForShell(readyPath)}
            while [ ! -f {QuoteForShell(releasePath)} ]; do sleep 0.01; done
            exit 19
            """);
        var runner = new ExternalProcessRunner();
        Task<ExternalProcessResult> runTask = runner.RunAsync(
            new ExternalProcessInvocation(ShellPath, [launcherScriptPath], temporaryDirectory.Path),
            cancellationSource.Token);

        try
        {
            await WaitUntilAsync(() => File.Exists(readyPath));
            int childId = int.Parse(await File.ReadAllTextAsync(childPidPath));
            await WaitUntilAsync(() => !Directory.Exists($"/proc/{childId}"));
            Assert.False(runTask.IsCompleted);

            await File.WriteAllTextAsync(releasePath, "release");
            ExternalProcessResult result = await runTask.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(19, result.ExitCode);
            Assert.False(Directory.Exists($"/proc/{childId}"));
        }
        finally
        {
            await CancelAndObserveAsync(cancellationSource, runTask);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Runner_loaded_by_testhost_launches_its_supervisor_without_an_apphost(bool copyHostConfiguration)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string assemblyPath = typeof(ExternalProcessRunner).Assembly.Location;
        string isolatedAssemblyPath = temporaryDirectory.File(Path.GetFileName(assemblyPath));
        string sourceDirectory = Path.GetDirectoryName(assemblyPath)!;
        // Mirror the testhost's dependency deployment, but omit the executable and optionally its
        // configuration. A deps manifest still requires its actual managed/native assets at startup.
        foreach (string dependencyPath in Directory.EnumerateFiles(sourceDirectory, "*.dll"))
        {
            File.Copy(dependencyPath, temporaryDirectory.File(Path.GetFileName(dependencyPath)));
        }

        foreach (string dependencyPath in Directory.EnumerateFiles(sourceDirectory, "*.so", SearchOption.AllDirectories))
        {
            string destinationPath = temporaryDirectory.File(Path.GetRelativePath(sourceDirectory, dependencyPath));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(dependencyPath, destinationPath);
        }

        if (copyHostConfiguration)
        {
            foreach (string extension in new[] { ".runtimeconfig.json", ".deps.json" })
            {
                File.Copy(Path.ChangeExtension(assemblyPath, extension), Path.ChangeExtension(isolatedAssemblyPath, extension));
            }
        }

        string scriptPath = temporaryDirectory.File("testhost-helper.sh");
        await File.WriteAllTextAsync(scriptPath, "printf 'from-testhost'; printf 'bounded-diagnostic' >&2; exit 19");
        var loadContext = new AssemblyLoadContext($"jrunner-supervisor-tests-{Guid.NewGuid():N}", isCollectible: true);
        loadContext.Resolving += (_, name) => name.Name == typeof(ExitCode).Assembly.GetName().Name ? typeof(ExitCode).Assembly : null;
        Task? runTask = null;
        try
        {
            Assembly isolatedAssembly = loadContext.LoadFromAssemblyPath(isolatedAssemblyPath);
            Assert.NotEqual(Assembly.GetEntryAssembly(), isolatedAssembly);
            Type runnerType = isolatedAssembly.GetType(typeof(ExternalProcessRunner).FullName!, throwOnError: true)!;
            Type invocationType = isolatedAssembly.GetType(typeof(ExternalProcessInvocation).FullName!, throwOnError: true)!;
            object runner = Activator.CreateInstance(runnerType, [8])!;
            ConstructorInfo constructor = invocationType.GetConstructor(
                [
                    typeof(string),
                    typeof(IEnumerable<string>),
                    typeof(string),
                    typeof(IEnumerable<KeyValuePair<string, string>>),
                    typeof(ReadOnlyMemory<byte>?),
                ])!;
            object invocation = constructor.Invoke([ShellPath, new[] { scriptPath }, temporaryDirectory.Path, null, null]);
            runTask = (Task)runnerType.GetMethod(nameof(ExternalProcessRunner.RunAsync))!
                .Invoke(runner, [invocation, cancellationSource.Token])!;
            await runTask.WaitAsync(TimeSpan.FromSeconds(10));
            object result = runTask.GetType().GetProperty("Result")!.GetValue(runTask)!;
            Type resultType = result.GetType();

            Assert.Equal(19, (int)resultType.GetProperty(nameof(ExternalProcessResult.ExitCode))!.GetValue(result)!);
            Assert.Equal("from-tes", (string)resultType.GetProperty(nameof(ExternalProcessResult.StandardOutput))!.GetValue(result)!);
            Assert.Equal("bounded-", (string)resultType.GetProperty(nameof(ExternalProcessResult.StandardError))!.GetValue(result)!);
            Assert.True((bool)resultType.GetProperty(nameof(ExternalProcessResult.StandardOutputTruncated))!.GetValue(result)!);
            Assert.True((bool)resultType.GetProperty(nameof(ExternalProcessResult.StandardErrorTruncated))!.GetValue(result)!);
        }
        finally
        {
            cancellationSource.Cancel();
            if (runTask is not null)
            {
                try
                {
                    await runTask.WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch
                {
                    // Preserve any assertion failure while observing cancellation of the isolated runner.
                }
            }

            loadContext.Unload();
        }
    }

    [Fact]
    public async Task Supervisor_arguments_never_contain_the_closed_invocation_or_standard_input()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        const string privateArgument = "synthetic-private-argument";
        const string privateInput = "synthetic-private-standard-input";
        string supervisorPidPath = temporaryDirectory.File("supervisor.pid");
        string observedArgumentPath = temporaryDirectory.File("argument.observed");
        string observedInputPath = temporaryDirectory.File("input.observed");
        string readyPath = temporaryDirectory.File("helper.ready");
        string releasePath = temporaryDirectory.File("helper.release");
        string scriptPath = temporaryDirectory.File("private-helper.sh");
        await File.WriteAllTextAsync(
            scriptPath,
            $"""
            printf '%s' "$PPID" > {QuoteForShell(supervisorPidPath)}
            printf '%s' "$1" > {QuoteForShell(observedArgumentPath)}
            IFS= read -r value
            printf '%s' "$value" > {QuoteForShell(observedInputPath)}
            printf ready > {QuoteForShell(readyPath)}
            while [ ! -f {QuoteForShell(releasePath)} ]; do sleep 0.01; done
            """);
        var runner = new ExternalProcessRunner();
        Task<ExternalProcessResult> runTask = runner.RunAsync(
            new ExternalProcessInvocation(
                ShellPath,
                [scriptPath, privateArgument],
                temporaryDirectory.Path,
                standardInput: $"{privateInput}\n"),
            cancellationSource.Token);

        try
        {
            await WaitUntilAsync(() => File.Exists(readyPath));
            int supervisorId = int.Parse(await File.ReadAllTextAsync(supervisorPidPath));
            string commandLine = System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync($"/proc/{supervisorId}/cmdline"));

            Assert.DoesNotContain(scriptPath, commandLine, StringComparison.Ordinal);
            Assert.DoesNotContain(privateArgument, commandLine, StringComparison.Ordinal);
            Assert.DoesNotContain(privateInput, commandLine, StringComparison.Ordinal);
            Assert.Equal(privateArgument, await File.ReadAllTextAsync(observedArgumentPath));
            Assert.Equal(privateInput, await File.ReadAllTextAsync(observedInputPath));
            await File.WriteAllTextAsync(releasePath, "release");
            Assert.True((await runTask.WaitAsync(TimeSpan.FromSeconds(10))).Succeeded);
        }
        finally
        {
            await CancelAndObserveAsync(cancellationSource, runTask);
        }
    }

    [Fact]
    public async Task Private_supervisor_rejects_standard_descriptors_without_starting_a_command()
    {
        Assert.Equal(
            1,
            await ExternalProcessRunner.RunSupervisorAsync(["--jrunner-private-process-supervisor", "0", "1", "2", "3"]));
    }

    [Fact]
    public async Task Forged_private_mode_from_a_shell_cannot_execute_a_well_formed_invocation()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var invocationPipe = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        using var controlPipe = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        using var resultPipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        MethodInfo createSockets = typeof(ExternalProcessRunner).GetMethod("CreateAuthenticationSockets", BindingFlags.NonPublic | BindingFlags.Static)!;
        var sockets = ((Socket Server, Socket Client))createSockets.Invoke(null, null)!;
        using Socket server = sockets.Server;
        using Socket client = sockets.Client;
        byte[] authenticationHeader = (byte[])typeof(ExternalProcessRunner)
            .GetMethod("CreateAuthenticationHeader", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [invocationPipe, controlPipe, resultPipe])!;
        using (var authenticationStream = new NetworkStream(server, ownsSocket: false))
        {
            await authenticationStream.WriteAsync(authenticationHeader);
        }

        System.Security.Cryptography.CryptographicOperations.ZeroMemory(authenticationHeader);
        string markerPath = temporaryDirectory.File("unauthorized-command");
        await JsonSerializer.SerializeAsync(
            invocationPipe,
            new
            {
                Protocol = "JRunner.ExternalProcess/1",
                ExecutablePath = ShellPath,
                Arguments = new[] { "-c", $"printf executed > {QuoteForShell(markerPath)}" },
                WorkingDirectory = temporaryDirectory.Path,
                EnvironmentUpdates = new Dictionary<string, string?>(),
            });
        string wrapperExitedPath = temporaryDirectory.File("wrapper.exited");
        string wrapperPath = temporaryDirectory.File("untrusted-parent.sh");
        var hostArguments = new ProcessStartInfo();
        typeof(ExternalProcessRunner).GetMethod("AddSupervisorHostArguments", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [hostArguments]);
        string command = string.Join(" ", hostArguments.ArgumentList.Select(QuoteForShell));
        await File.WriteAllTextAsync(
            wrapperPath,
            $"""
            {command} --jrunner-private-process-supervisor {invocationPipe.GetClientHandleAsString()} {controlPipe.GetClientHandleAsString()} {resultPipe.GetClientHandleAsString()} {client.SafeHandle.DangerousGetHandle().ToInt32()}
            printf exited > {QuoteForShell(wrapperExitedPath)}
            """);
        using Process wrapper = Process.Start(
            new ProcessStartInfo
            {
                FileName = ShellPath,
                ArgumentList = { wrapperPath },
                WorkingDirectory = temporaryDirectory.Path,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
        invocationPipe.DisposeLocalCopyOfClientHandle();
        controlPipe.DisposeLocalCopyOfClientHandle();
        resultPipe.DisposeLocalCopyOfClientHandle();
        client.Dispose();
        invocationPipe.Dispose();
        Task<string> errorTask = wrapper.StandardError.ReadToEndAsync();
        try
        {
            await wrapper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(File.Exists(wrapperExitedPath));
            Assert.False(File.Exists(markerPath));
            Assert.Equal(string.Empty, await errorTask.WaitAsync(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            if (!wrapper.HasExited)
            {
                wrapper.Kill(entireProcessTree: true);
                await wrapper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    [Fact]
    public async Task Invocation_serialization_waits_for_authenticated_capability_acknowledgement()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var pipe = new MemoryStream();
        var invocation = new ExternalProcessInvocation(ShellPath, ["-c", "printf private"], temporaryDirectory.Path);
        Task failedAuthentication = Task.FromException(new IOException("The private process capability is unavailable."));
        Task writeTask = (Task)typeof(ExternalProcessRunner).GetMethod("WriteInvocationAsync", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [pipe, invocation, failedAuthentication, CancellationToken.None])!;

        await Assert.ThrowsAsync<IOException>(() => writeTask);
        Assert.Empty(pipe.ToArray());
    }

    [Fact]
    public void Capability_acknowledgement_is_bound_to_fresh_nonce_child_identity_and_descriptor_set()
    {
        using var invocationPipe = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        using var controlPipe = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        using var resultPipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        MethodInfo createHeader = typeof(ExternalProcessRunner).GetMethod("CreateAuthenticationHeader", BindingFlags.NonPublic | BindingFlags.Static)!;
        CapabilityAcknowledgement createAcknowledgement = typeof(ExternalProcessRunner)
            .GetMethod("CreateAuthenticationAcknowledgement", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate<CapabilityAcknowledgement>();
        byte[] header = (byte[])createHeader.Invoke(null, [invocationPipe, controlPipe, resultPipe])!;
        byte[] nextHeader = (byte[])createHeader.Invoke(null, [invocationPipe, controlPipe, resultPipe])!;
        try
        {
            byte[] acknowledgement = createAcknowledgement(header, Environment.ProcessId);
            byte[] differentNonce = createAcknowledgement(nextHeader, Environment.ProcessId);
            byte[] differentChild = createAcknowledgement(header, Environment.ProcessId + 1);
            byte[] differentDescriptorsHeader = header.ToArray();
            differentDescriptorsHeader[56] ^= 1;
            byte[] differentDescriptors = createAcknowledgement(differentDescriptorsHeader, Environment.ProcessId);

            Assert.False(header.AsSpan(4, 32).SequenceEqual(nextHeader.AsSpan(4, 32)));
            Assert.False(acknowledgement.AsSpan(4).SequenceEqual(differentNonce.AsSpan(4)));
            Assert.False(acknowledgement.AsSpan(4).SequenceEqual(differentChild.AsSpan(4)));
            Assert.False(acknowledgement.AsSpan(4).SequenceEqual(differentDescriptors.AsSpan(4)));
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(differentDescriptorsHeader);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(header);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(nextHeader);
        }
    }

    private delegate byte[] CapabilityAcknowledgement(ReadOnlySpan<byte> header, int childId);

    [Theory]
    [InlineData(10, 100UL, 10, 100UL, 10, true)]
    [InlineData(10, 100UL, 10, 101UL, 10, false)]
    [InlineData(10, 100UL, 10, 100UL, 99, false)]
    [InlineData(10, 100UL, 11, 100UL, 11, false)]
    public void Discovery_rejects_reused_parent_or_foreign_child_identity(
        int sampledParentId, ulong sampledStart, int currentParentId, ulong currentStart, int childParentId, bool expected)
    {
        Type processInfo = typeof(ExternalProcessRunner).GetNestedType("LinuxProcessInfo", BindingFlags.NonPublic)!;
        object sampledParent = Activator.CreateInstance(processInfo, [sampledParentId, 1, sampledParentId, sampledStart, 'S'])!;
        object currentParent = Activator.CreateInstance(processInfo, [currentParentId, 1, currentParentId, currentStart, 'S'])!;
        object child = Activator.CreateInstance(processInfo, [20, childParentId, 20, 200UL, 'S'])!;
        bool actual = (bool)typeof(ExternalProcessRunner).GetMethod("HasExpectedProcessParent", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [sampledParent, currentParent, child])!;

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData('S')]
    [InlineData('R')]
    [InlineData('Z')]
    [InlineData('X')]
    public void Procfs_stat_exit_sentinel_returns_false_and_default_identity_regardless_of_sampled_state(char state)
    {
        const int processId = 27182;
        Type processInfo = typeof(ExternalProcessRunner).GetNestedType("LinuxProcessInfo", BindingFlags.NonPublic)!;
        object?[] arguments =
        [
            processId,
            CreateProcfsStat(processId, state, parentId: 0, groupId: -1),
            Activator.CreateInstance(processInfo, [processId, 27101, processId, 139714052UL, state]),
            null,
            CancellationToken.None,
        ];

        // do_task_stat samples state before lock_task_sighand, so S/R can survive task exit.
        // If the lock fails, ppid/pgrp retain 0/-1 even in a complete stat record:
        // https://github.com/torvalds/linux/blob/v6.14/fs/proc/array.c#L473-L545
        bool found = (bool)typeof(ExternalProcessRunner)
            .GetMethod("TryParseProcessInfo", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, arguments)!;

        Assert.False(found);
        Assert.Equal(Activator.CreateInstance(processInfo), arguments[2]);
    }

    [Theory]
    [InlineData('S')]
    [InlineData('R')]
    [InlineData('Z')]
    [InlineData('X')]
    public void Procfs_stat_preserves_pid_parent_group_starttime_and_state_for_normal_identity(char state)
    {
        const int processId = 27182;
        const int parentId = 27101;
        const int groupId = 27000;
        const ulong startTime = 139714052UL;
        object?[] arguments =
        [
            processId,
            CreateProcfsStat(processId, state, parentId, groupId),
            null,
            null,
            CancellationToken.None,
        ];

        bool found = (bool)typeof(ExternalProcessRunner)
            .GetMethod("TryParseProcessInfo", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, arguments)!;

        Assert.True(found);
        Assert.NotNull(arguments[2]);
        object info = arguments[2]!;
        Type processInfo = info.GetType();
        Assert.Equal(processId, (int)processInfo.GetProperty("ProcessId")!.GetValue(info)!);
        Assert.Equal(parentId, (int)processInfo.GetProperty("ParentProcessId")!.GetValue(info)!);
        Assert.Equal(groupId, (int)processInfo.GetProperty("ProcessGroupId")!.GetValue(info)!);
        Assert.Equal(startTime, (ulong)processInfo.GetProperty("StartTime")!.GetValue(info)!);
        Assert.Equal(state, (char)processInfo.GetProperty("State")!.GetValue(info)!);
    }

    [Theory]
    [InlineData(0, -2)]
    [InlineData(27101, -1)]
    [InlineData(27101, -2)]
    public void Procfs_stat_rejects_negative_groups_other_than_the_exact_exit_sentinel(int parentId, int groupId)
    {
        string stat = CreateProcfsStat(27182, 'S', parentId, groupId);

        TargetInvocationException exception = Assert.Throws<TargetInvocationException>(
            () => typeof(ExternalProcessRunner).GetMethod("TryParseProcessInfo", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [27182, stat, null, null, CancellationToken.None]));

        Assert.IsType<IOException>(exception.InnerException);
    }

    [Theory]
    [InlineData("invalid-starttime")]
    [InlineData("-1")]
    [InlineData("18446744073709551616")]
    public void Procfs_stat_exit_sentinel_does_not_hide_malformed_starttime(string startTime)
    {
        string stat = CreateProcfsStat(27182, 'S', parentId: 0, groupId: -1, startTime: startTime);

        TargetInvocationException exception = Assert.Throws<TargetInvocationException>(
            () => typeof(ExternalProcessRunner).GetMethod("TryParseProcessInfo", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [27182, stat, null, null, CancellationToken.None]));

        Assert.IsType<IOException>(exception.InnerException);
    }

    [Theory]
    [InlineData("27182 (jrunner worker S 0 -1")]
    [InlineData("27182 (jrunner worker) SS 0 -1")]
    [InlineData("27182 (jrunner worker) S 0 -1")]
    [InlineData("27182 (jrunner worker) S 0 -1 27182 0 -1 4194560 312 0 0 0 4 2 0 0 20 0 1 0")]
    public void Procfs_stat_rejects_malformed_or_truncated_identity(string stat)
    {
        TargetInvocationException exception = Assert.Throws<TargetInvocationException>(
            () => typeof(ExternalProcessRunner).GetMethod("TryParseProcessInfo", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [27182, stat, null, null, CancellationToken.None]));

        Assert.IsType<IOException>(exception.InnerException);
    }

    [Theory]
    [InlineData(3, true)] // ESRCH: the opened descriptor's task vanished, independently of its pathname.
    [InlineData(5, false)] // EIO
    [InlineData(13, false)] // EACCES
    public void Procfs_disappeared_task_errno_is_not_overruled_by_a_later_successful_path_lookup(
        int nativeError, bool expected)
    {
        using var directory = new TemporaryDirectory();
        Assert.True(Directory.Exists(directory.Path));
        var exception = new IOException("procfs read failed", nativeError);

        bool vanished = (bool)typeof(ExternalProcessRunner)
            .GetMethod("IsVanishedProcEntry", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [exception, directory.Path])!;

        Assert.Equal(expected, vanished);
        Assert.True(Directory.Exists(directory.Path));
    }

    [Fact]
    public void Expired_cleanup_budget_fails_before_discovery_can_report_an_empty_scope()
    {
        long expired = Stopwatch.GetTimestamp() - 6 * Stopwatch.Frequency;
        TargetInvocationException exception = Assert.Throws<TargetInvocationException>(
            () => typeof(ExternalProcessRunner).GetMethod("CheckCleanupDeadline", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [expired, CancellationToken.None]));

        Assert.IsType<IOException>(exception.InnerException);
    }

    [Theory]
    [InlineData("host.runtimeconfig.json")]
    [InlineData("host.deps.json")]
    public async Task Relative_host_configuration_is_bound_to_parent_directory_not_invocation_directory(string name)
    {
        using var parentDirectory = new TemporaryDirectory();
        using var invocationDirectory = new TemporaryDirectory();
        string expectedPath = parentDirectory.File(name);
        await File.WriteAllTextAsync(expectedPath, "{}");
        string resolvedPath = (string)typeof(ExternalProcessRunner)
            .GetMethod("ResolveHostConfigurationPath", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [name, parentDirectory.Path])!;

        Assert.Equal(expectedPath, resolvedPath);
        Assert.True(Path.IsPathFullyQualified(resolvedPath));
        ExternalProcessResult result = await new ExternalProcessRunner().RunAsync(
            new ExternalProcessInvocation(ShellPath, ["-c", "[ -f \"$1\" ]", "check-host-path", resolvedPath], invocationDirectory.Path));
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Executable_probe_does_not_launch_a_helper_or_initialize_its_environment()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string markerPath = temporaryDirectory.File("probe-launched");
        string prefixPath = temporaryDirectory.File("unused-prefix");
        string scriptPath = temporaryDirectory.File("probe.sh");
        await File.WriteAllTextAsync(
            scriptPath,
            $"printf launched > {QuoteForShell(markerPath)}; mkdir \"$WINEPREFIX\"");
        IExternalProcessRunner runner = new ExternalProcessRunner();

        runner.EnsureExecutableAvailable(
            new ExternalProcessInvocation(
                ShellPath,
                [scriptPath],
                temporaryDirectory.Path,
                [new KeyValuePair<string, string?>("WINEPREFIX", prefixPath)]));

        Assert.False(File.Exists(markerPath));
        Assert.False(Directory.Exists(prefixPath));
    }

    [Fact]
    public void Executable_probe_maps_missing_executables_to_the_safe_launch_failure()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string executablePath = temporaryDirectory.File("private-executable-path");
        IExternalProcessRunner runner = new ExternalProcessRunner();

        ExternalProcessFailureException exception = Assert.Throws<ExternalProcessFailureException>(
            () => runner.EnsureExecutableAvailable(
                new ExternalProcessInvocation(executablePath, ["private-argument"], temporaryDirectory.Path)));

        Assert.Equal(ExternalProcessFailureKind.LaunchFailed, exception.Failure.Kind);
        Assert.Equal("external-process-launch-failed", exception.Failure.ToOperationFailure().Kind);
        Assert.DoesNotContain(executablePath, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-argument", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Launch_failure_has_a_stable_typed_external_process_shape()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var runner = new ExternalProcessRunner();
        var invocation = new ExternalProcessInvocation(
            temporaryDirectory.File("does-not-exist"),
            Array.Empty<string>(),
            temporaryDirectory.Path);

        ExternalProcessFailureException exception = await Assert.ThrowsAsync<ExternalProcessFailureException>(
            () => runner.RunAsync(invocation));

        Assert.Equal(ExternalProcessFailureKind.LaunchFailed, exception.Failure.Kind);
        Assert.Null(exception.Failure.Result);
        OperationFailure failure = exception.Failure.ToOperationFailure();
        Assert.Equal(ExitCode.ExternalProcess, failure.Code);
        Assert.Equal("external-process-launch-failed", failure.Kind);
    }

    [Fact]
    public async Task Missing_executable_name_is_a_typed_launch_failure_before_the_launcher_runs()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var runner = new ExternalProcessRunner();
        var invocation = new ExternalProcessInvocation(
            $"jrunner-missing-command-{Guid.NewGuid():N}",
            Array.Empty<string>(),
            temporaryDirectory.Path);

        ExternalProcessFailureException exception = await Assert.ThrowsAsync<ExternalProcessFailureException>(
            () => runner.RunAsync(invocation));

        Assert.Equal(ExternalProcessFailureKind.LaunchFailed, exception.Failure.Kind);
        Assert.Equal("external-process-launch-failed", exception.Failure.ToOperationFailure().Kind);
    }

    [Fact]
    public async Task Nonexecutable_file_is_a_typed_launch_failure_before_the_launcher_runs()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string executablePath = temporaryDirectory.File("not-executable");
        await File.WriteAllTextAsync(executablePath, "not an executable");
        var runner = new ExternalProcessRunner();

        ExternalProcessFailureException exception = await Assert.ThrowsAsync<ExternalProcessFailureException>(
            () => runner.RunAsync(
                new ExternalProcessInvocation(
                    executablePath,
                    Array.Empty<string>(),
                    temporaryDirectory.Path)));

        Assert.Equal(ExternalProcessFailureKind.LaunchFailed, exception.Failure.Kind);
        Assert.Equal("external-process-launch-failed", exception.Failure.ToOperationFailure().Kind);
    }

    [SupportedOSPlatform("linux")]
    [Fact]
    public async Task Authenticated_invocation_roundtrip_preserves_trust_marker_bound_launcher_and_absolute_alias()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var deployment = new ProtectedRunnableSupervisorDeployment();
        ExternalProcessSupervisorLaunch launch = deployment.Prepare();
        string aliases = deployment.CreateDirectory("wine-aliases");
        string executableAlias = Path.Join(aliases, "winepath");
        File.CreateSymbolicLink(executableAlias, PrintfPath);
        NativeDependencyClosure native = deployment.PrepareNativeClosure(launch, executableAlias);
        var invocation = new ExternalProcessInvocation(
            executableAlias, ["private-argument"], deployment.Root,
            [new KeyValuePair<string, string?>("WINEPREFIX", "private-prefix")])
        {
            TrustedSupervisorLaunch = launch,
            TrustedNativeClosure = native,
        };
        byte[] serialized = await SerializeInvocationAsync(invocation);
        using JsonDocument request = JsonDocument.Parse(serialized);
        Assert.True(request.RootElement.GetProperty("RequiresTrustedSupervisor").GetBoolean());
        Assert.Equal(launch.ProcessGroupLauncher, request.RootElement.GetProperty("TrustedSupervisorLaunch")
            .GetProperty("ProcessGroupLauncher").GetString());
        Assert.Equal(2, request.RootElement.GetProperty("TrustedNativeClosure").GetProperty("Version").GetInt32());
        using var pipe = new MemoryStream(serialized);

        ExternalProcessInvocation restored = await ExternalProcessRunner.ReadInvocationAsync(pipe, launch.ProcessGroupLauncher);

        ExternalProcessSupervisorLaunch restoredLaunch = Assert.IsType<ExternalProcessSupervisorLaunch>(restored.TrustedSupervisorLaunch);
        NativeDependencyClosure restoredNative = Assert.IsType<NativeDependencyClosure>(restored.TrustedNativeClosure);
        restoredNative.ValidateInputs(launch.NativeInputs);
        restoredNative.ValidateExecutable(executableAlias);
        NativeDependencyClosureBinding restoredBinding = restoredNative.ToBinding();
        JsonElement serializedNative = request.RootElement.GetProperty("TrustedNativeClosure");
        Assert.Equal(serializedNative.GetProperty("InputsSha256").GetString(), restoredBinding.InputsSha256);
        Assert.Equal(serializedNative.GetProperty("ManifestSha256").GetString(), restoredBinding.ManifestSha256);
        Assert.Equal(executableAlias, restored.ExecutablePath);
        Assert.Equal(invocation.Arguments, restored.Arguments);
        Assert.Equal("private-prefix", restored.EnvironmentUpdates["WINEPREFIX"]);
        Assert.Equal(launch.ProcessGroupLauncher, restoredLaunch.ProcessGroupLauncher);
        Assert.Equal(launch.HostArguments, restoredLaunch.HostArguments);
        Assert.Equal(launch.RuntimeRoot, restoredLaunch.RuntimeRoot);
        Assert.Equal(launch.HostTargetPath, restoredLaunch.HostTargetPath);
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("marker-removed")]
    [InlineData("chain-removed")]
    [InlineData("native-removed")]
    [InlineData("native-downgraded")]
    [InlineData("native-empty")]
    [InlineData("native-root-changed")]
    [InlineData("native-executable-input-changed")]
    [InlineData("native-environment-changed")]
    [InlineData("native-input-hash-changed")]
    [InlineData("native-object-hash-changed")]
    [InlineData("native-layout-changed")]
    [InlineData("native-path-identity-changed")]
    [InlineData("native-objects-removed")]
    [InlineData("native-nested-bool-removed")]
    [InlineData("native-nested-number-removed")]
    [InlineData("native-nested-nonnull-null")]
    [InlineData("launcher-changed")]
    [InlineData("runtime-root-changed")]
    [InlineData("host-target-changed")]
    [InlineData("unbound-private-entrypoint")]
    public async Task Authenticated_protocol_rejects_missing_downgraded_or_rebound_trusted_launch_state(string mutation)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var deployment = new ProtectedRunnableSupervisorDeployment();
        ExternalProcessSupervisorLaunch launch = deployment.Prepare();
        NativeDependencyClosure native = deployment.PrepareNativeClosure(launch, PrintfPath);
        var invocation = new ExternalProcessInvocation(PrintfPath, ["private-protocol-data"], deployment.Root)
        {
            TrustedSupervisorLaunch = launch,
            TrustedNativeClosure = native,
        };
        JsonObject request = JsonNode.Parse(await SerializeInvocationAsync(invocation))!.AsObject();
        switch (mutation)
        {
            case "marker-removed":
                request["RequiresTrustedSupervisor"] = false;
                break;
            case "chain-removed":
                request["TrustedSupervisorLaunch"] = null;
                break;
            case "native-removed":
                request["TrustedNativeClosure"] = null;
                break;
            case "native-downgraded":
                request["TrustedNativeClosure"]!["Version"] = 0;
                break;
            case "native-empty":
                request["TrustedNativeClosure"] = new JsonObject { ["Version"] = 2 };
                break;
            case "native-root-changed":
                request["TrustedNativeClosure"]!["Inputs"]!["ModuleDirectories"]![0] = "/private/rebound-module-root";
                break;
            case "native-executable-input-changed":
                request["TrustedNativeClosure"]!["Inputs"]!["ExecutablePaths"]![0] = "/private/rebound-controller";
                break;
            case "native-environment-changed":
                request["TrustedNativeClosure"]!["Inputs"]!["EnvironmentBindings"]!["HOME"] = "/private/rebound-home";
                break;
            case "native-input-hash-changed":
                request["TrustedNativeClosure"]!["InputsSha256"] = new string('0', 64);
                break;
            case "native-object-hash-changed":
                request["TrustedNativeClosure"]!["Objects"]![0]!["ContentSha256"] = new string('0', 64);
                break;
            case "native-layout-changed":
                request["TrustedNativeClosure"]!["LoaderLayout"]!["CachePath"] = "/private/rebound-cache";
                break;
            case "native-path-identity-changed":
                request["TrustedNativeClosure"]!["Paths"]![0]!["Identity"]!["Inode"] = 0;
                break;
            case "native-objects-removed":
                request["TrustedNativeClosure"]!["Objects"] = new JsonArray();
                break;
            case "native-nested-bool-removed":
            {
                JsonObject fileProof = request["TrustedNativeClosure"]!["Paths"]!.AsArray()
                    .First(static path => !path!["Directory"]!.GetValue<bool>())!.AsObject();
                // Without required constructor fields, false could be silently restored and still
                // match the original manifest hash. Missing proof data is not a complete binding.
                fileProof.Remove("Directory");
                break;
            }
            case "native-nested-number-removed":
            {
                JsonObject image = request["TrustedNativeClosure"]!["Objects"]!.AsArray()
                    .Select(static item => item!["Image"]).OfType<JsonObject>()
                    .First(static candidate => candidate["OsAbi"]!.GetValue<byte>() == 0
                        || candidate["Flags"]!.GetValue<ulong>() == 0 || candidate["Flags1"]!.GetValue<ulong>() == 0);
                string numericField = image["OsAbi"]!.GetValue<byte>() == 0 ? "OsAbi"
                    : image["Flags"]!.GetValue<ulong>() == 0 ? "Flags" : "Flags1";
                image.Remove(numericField);
                break;
            }
            case "native-nested-nonnull-null":
                request["TrustedNativeClosure"]!["Paths"]![0]!["ExistingAncestor"] = null;
                break;
            case "launcher-changed":
                request["TrustedSupervisorLaunch"]!["ProcessGroupLauncher"] = "/private/rebound-launcher";
                break;
            case "runtime-root-changed":
                request["TrustedSupervisorLaunch"]!["RuntimeRoot"] = "/private/rebound-runtime";
                break;
            case "host-target-changed":
                request["TrustedSupervisorLaunch"]!["HostTargetPath"] = "/private/rebound-host";
                break;
        }
        using var pipe = new MemoryStream(Encoding.UTF8.GetBytes(request.ToJsonString()));

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(() =>
            ExternalProcessRunner.ReadInvocationAsync(pipe,
                mutation == "unbound-private-entrypoint" ? null : launch.ProcessGroupLauncher));

        AssertNativeUnavailable(failure, deployment.Root, "/private/rebound");
        Assert.DoesNotContain(deployment.Root, failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("/private/rebound", failure.ToString(), StringComparison.Ordinal);
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("relative-wine")]
    [InlineData("unsafe-wine")]
    [InlineData("unsafe-runtime")]
    [InlineData("unsafe-launcher")]
    public async Task Authenticated_inner_invocation_revalidates_protected_absolute_Wine_and_the_bound_chain(string mutation)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var deployment = new ProtectedRunnableSupervisorDeployment();
        string launcher = deployment.CopyProtectedExecutable(deployment.LauncherPath, "private-launcher/setsid");
        string wine = deployment.CopyProtectedExecutable(ShellPath, "wine-tools/wine");
        ExternalProcessSupervisorLaunch launch = deployment.Prepare(launcher);
        NativeDependencyClosure native = deployment.PrepareNativeClosure(launch, wine);
        var invocation = new ExternalProcessInvocation(wine, ["private-inner-argument"], deployment.Root)
        {
            TrustedSupervisorLaunch = launch,
            TrustedNativeClosure = native,
        };
        JsonObject request = JsonNode.Parse(await SerializeInvocationAsync(invocation))!.AsObject();
        if (mutation == "relative-wine")
        {
            request["ExecutablePath"] = "wine";
        }
        else
        {
            string unsafePath = mutation switch
            {
                "unsafe-wine" => wine,
                "unsafe-runtime" => Path.Join(deployment.RuntimeDirectory, "libcoreclr.so"),
                _ => launcher,
            };
            File.SetUnixFileMode(unsafePath, File.GetUnixFileMode(unsafePath) | UnixFileMode.OtherWrite);
        }
        using var pipe = new MemoryStream(Encoding.UTF8.GetBytes(request.ToJsonString()));

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(() =>
            ExternalProcessRunner.ReadInvocationAsync(pipe, launch.ProcessGroupLauncher));

        AssertNativeUnavailable(failure, deployment.Root, "private-inner-argument");
        Assert.DoesNotContain(deployment.Root, failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-inner-argument", failure.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(deployment.LauncherPath + ".launched"));
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("duplicate-marker")]
    [InlineData("duplicate-native-version")]
    [InlineData("unknown-member")]
    public async Task Marked_protocol_rejects_ambiguous_or_unrecognized_native_control_members(string mutation)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var deployment = new ProtectedRunnableSupervisorDeployment();
        ExternalProcessSupervisorLaunch launch = deployment.Prepare();
        var invocation = new ExternalProcessInvocation(PrintfPath, ["private-protocol-token"], deployment.Root)
        {
            TrustedSupervisorLaunch = launch,
            TrustedNativeClosure = deployment.PrepareNativeClosure(launch, PrintfPath),
        };
        string serialized = Encoding.UTF8.GetString(await SerializeInvocationAsync(invocation));
        serialized = mutation switch
        {
            "duplicate-marker" => serialized.Replace("\"RequiresTrustedSupervisor\":true",
                "\"RequiresTrustedSupervisor\":false,\"RequiresTrustedSupervisor\":true", StringComparison.Ordinal),
            "duplicate-native-version" => serialized.Replace("\"Version\":2",
                "\"Version\":0,\"Version\":2", StringComparison.Ordinal),
            _ => "{\"UnrecognizedNativeControl\":true," + serialized[1..],
        };
        using var pipe = new MemoryStream(Encoding.UTF8.GetBytes(serialized));

        AssertNativeUnavailable(await Assert.ThrowsAsync<OperationFailureException>(() =>
            ExternalProcessRunner.ReadInvocationAsync(pipe, launch.ProcessGroupLauncher)),
            deployment.Root, "private-protocol-token");
    }

    [Fact]
    public async Task Authenticated_unmarked_protocol_roundtrip_preserves_synthetic_executable_semantics()
    {
        var invocation = new ExternalProcessInvocation("synthetic-tool", ["synthetic-argument"], "synthetic-directory");
        using var pipe = new MemoryStream(await SerializeInvocationAsync(invocation));

        ExternalProcessInvocation restored = await ExternalProcessRunner.ReadInvocationAsync(pipe, null);

        Assert.Null(restored.TrustedSupervisorLaunch);
        Assert.Null(restored.TrustedNativeClosure);
        Assert.Equal(invocation.ExecutablePath, restored.ExecutablePath);
        Assert.Equal(invocation.WorkingDirectory, restored.WorkingDirectory);
        Assert.Equal(invocation.Arguments, restored.Arguments);
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Trusted_protected_system_launch_uses_the_bound_custom_setsid_alias_and_closes_loader_environment(bool useApphost)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var directory = new ProtectedDirectory();
        using var deployment = new ProtectedRunnableSupervisorDeployment(useApphost);
        string tools = directory.CreateDirectory("protected-aliases");
        ExternalProcessSupervisorLaunch defaults = deployment.Prepare();
        string launcherAlias = Path.Join(tools, "selected-setsid");
        string wineAlias = Path.Join(tools, "winepath");
        File.CreateSymbolicLink(launcherAlias, defaults.ProcessGroupLauncher);
        File.CreateSymbolicLink(wineAlias, ShellPath);
        var runner = new ExternalProcessRunner(launcherAlias, deployment.HostPath);
        ExternalProcessSupervisorLaunch launch = runner.PrepareTrustedSupervisorLaunch(deployment.AssemblyPath, deployment.RuntimeDirectory);
        Assert.Equal(Path.Join(deployment.Root, "runtime-install"), launch.RuntimeRoot);
        Assert.Equal(deployment.RuntimeDirectory, launch.RuntimeDirectory);
        Assert.Equal(deployment.AssemblyPath, launch.AssemblyPath);
        Assert.Equal(deployment.HostPath, launch.HostArguments[0]);
        if (!useApphost)
        {
            Assert.Equal(
                [deployment.DotnetPath, "exec", "--runtimeconfig", Path.ChangeExtension(deployment.AssemblyPath, ".runtimeconfig.json"),
                    "--depsfile", Path.ChangeExtension(deployment.AssemblyPath, ".deps.json"), deployment.AssemblyPath],
                launch.HostArguments);
        }
        string[] loaderNames =
        [
            "DOTNET_STARTUP_HOOKS", "DOTNET_ADDITIONAL_DEPS", "DOTNET_SHARED_STORE", "DOTNET_ROLL_FORWARD",
            "CORECLR_ENABLE_PROFILING", "CORECLR_PROFILER_PATH", "CORE_SERVICING", "LD_PRELOAD", "LD_AUDIT",
            "GLIBC_TUNABLES", "BASH_ENV", "ENV", "OPENSSL_CONF", "OPENSSL_MODULES",
        ];
        var updates = loaderNames.Select(name => new KeyValuePair<string, string?>(name, "/private/loader-payload")).ToList();
        if (!useApphost)
        {
            // Also prove that the inherited, genuinely loaded protected parent hook is removed,
            // without replacing it with the caller's malicious hook value first.
            updates.RemoveAll(static update => update.Key == "DOTNET_STARTUP_HOOKS");
        }
        updates.Add(new KeyValuePair<string, string?>("DOTNET_ROOT", "/private/runtime-root"));
        updates.Add(new KeyValuePair<string, string?>("PATH", "/private/mutable-PATH"));
        updates.Add(new KeyValuePair<string, string?>("HOME", "/private/mutable-home"));
        updates.Add(new KeyValuePair<string, string?>("XDG_CONFIG_HOME", "/private/mutable-config"));
        string privateOutput = Path.Join(directory.Path, "private-native-output");
        string originalUmask = File.ReadLines("/proc/self/status").Single(static line =>
            line.StartsWith("Umask:", StringComparison.Ordinal));
        var invocation = new ExternalProcessInvocation(
            wineAlias,
            ["-c", "printf '%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s' \"$0\" \"$DOTNET_ROOT\" \"$DOTNET_STARTUP_HOOKS\" \"$DOTNET_ADDITIONAL_DEPS\" \"$DOTNET_SHARED_STORE\" \"$DOTNET_ROLL_FORWARD\" \"$CORECLR_ENABLE_PROFILING\" \"$CORECLR_PROFILER_PATH\" \"$CORE_SERVICING\" \"$LD_PRELOAD\" \"$LD_AUDIT\" \"$PATH\" \"$HOME\" \"$XDG_CONFIG_HOME\" \"$GLIBC_TUNABLES\" \"$BASH_ENV\" \"$ENV\" \"$OPENSSL_CONF\" \"$OPENSSL_MODULES\"; printf '|%s' \"$(umask)\"; : > \"$1\"", wineAlias, privateOutput],
            directory.Path, updates)
        {
            TrustedSupervisorLaunch = launch,
            TrustedNativeClosure = deployment.PrepareNativeClosureForTestParent(launch, wineAlias),
        };
        using var timeout = new CancellationTokenSource(ProtectedSupervisorTimeout);

        await using var parent = await ProtectedSupervisorTestParent.StartAsync(deployment, invocation, timeout.Token);
        ProtectedSupervisorTestParent.ParentResponse response = await parent.ExecuteAsync(timeout.Token);
        Assert.Null(response.FailureKind);
        Assert.Null(response.PrerequisiteKind);
        ProtectedSupervisorTestParent.CapturedResult result = Assert.IsType<ProtectedSupervisorTestParent.CapturedResult>(response.Result);

        Assert.Equal(0, result.ExitCode);
        string home = Path.Join(deployment.Root, "native-home");
        Assert.Equal(wineAlias + "|" + launch.RuntimeRoot + new string('|', 7) + launch.RuntimeDirectory + "|||"
            + Path.Join(deployment.Root, "native-path") + "|" + home + "|" + Path.Join(home, "config")
            + "||||" + Path.Join(home, "native-openssl.cnf") + "|" + Path.Join(home, "native-modules") + "|0077",
            result.StandardOutput);
        Assert.Equal(string.Empty, result.StandardError);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(privateOutput));
        Assert.Equal(originalUmask, File.ReadLines("/proc/self/status").Single(static line =>
            line.StartsWith("Umask:", StringComparison.Ordinal)));
        Assert.Equal(launcherAlias, launch.ProcessGroupLauncher);
        Assert.Same(launch, invocation.TrustedSupervisorLaunch);
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("launcher")]
    [InlineData("host")]
    public async Task Trusted_launch_rejects_changed_bound_alias_without_falling_back_to_protected_defaults(string role)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var directory = new ProtectedDirectory();
        using var deployment = new ProtectedRunnableSupervisorDeployment();
        string tools = directory.CreateDirectory("protected-selection");
        ExternalProcessSupervisorLaunch defaults = deployment.Prepare();
        string selectedPath = Path.Join(tools, role);
        File.CreateSymbolicLink(selectedPath, role == "launcher" ? defaults.ProcessGroupLauncher : defaults.HostArguments[0]);
        var runner = role == "launcher"
            ? new ExternalProcessRunner(selectedPath, deployment.HostPath)
            : new ExternalProcessRunner(null, selectedPath);
        ExternalProcessSupervisorLaunch launch = runner.PrepareTrustedSupervisorLaunch(deployment.AssemblyPath, deployment.RuntimeDirectory);
        Assert.Equal(selectedPath, role == "launcher" ? launch.ProcessGroupLauncher : launch.HostArguments[0]);
        var invocation = new ExternalProcessInvocation(PrintfPath, ["must-not-launch"], directory.Path)
        {
            TrustedSupervisorLaunch = launch,
            TrustedNativeClosure = deployment.PrepareNativeClosureForTestParent(launch, PrintfPath),
        };
        using var timeout = new CancellationTokenSource(ProtectedSupervisorTimeout);
        await using var parent = await ProtectedSupervisorTestParent.StartAsync(deployment, invocation, timeout.Token);
        File.Delete(selectedPath);
        File.WriteAllText(selectedPath, "#!/bin/sh\nprintf launched > \"$0.launched\"\nexit 93\n");
        File.SetUnixFileMode(selectedPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherWrite);

        OperationFailureException prerequisite = Assert.Throws<OperationFailureException>(() => launch.Revalidate());
        Assert.Equal(ExitCode.MissingPrerequisite, prerequisite.Code);
        Assert.Equal("wine-unavailable", prerequisite.Kind);
        Assert.Null(prerequisite.InnerException);
        Assert.DoesNotContain(selectedPath, prerequisite.ToString(), StringComparison.Ordinal);
        ProtectedSupervisorTestParent.ParentResponse response = await parent.ExecuteAsync(timeout.Token);

        Assert.Null(response.Result);
        Assert.Null(response.FailureKind);
        Assert.Equal(ExitCode.MissingPrerequisite, response.PrerequisiteCode);
        Assert.Equal("native-closure-unavailable", response.PrerequisiteKind);
        Assert.False(response.PrerequisiteHasInnerException);
        Assert.False(response.DiagnosticsContainBoundPath);
        Assert.DoesNotContain(selectedPath, response.FailureMessage!, StringComparison.Ordinal);
        Assert.False(File.Exists(selectedPath + ".launched"));
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("assembly", UnixFileMode.GroupWrite)]
    [InlineData("assembly-directory", UnixFileMode.OtherWrite)]
    [InlineData("managed-dependency", UnixFileMode.GroupWrite)]
    public void Trusted_preparation_rejects_writable_real_deployment_code_instead_of_using_an_unprotected_checkout(
        string role, UnixFileMode unsafeMode)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var deployment = new ProtectedRunnableSupervisorDeployment();
        var runner = new ExternalProcessRunner(deployment.LauncherPath, deployment.HostPath);
        runner.PrepareTrustedSupervisorLaunch(deployment.AssemblyPath, deployment.RuntimeDirectory);
        string unsafePath = role switch
        {
            "assembly" => deployment.AssemblyPath,
            "assembly-directory" => Path.GetDirectoryName(deployment.AssemblyPath)!,
            _ => Path.Join(Path.GetDirectoryName(deployment.AssemblyPath), "JRunner.Core.dll"),
        };
        File.SetUnixFileMode(unsafePath, File.GetUnixFileMode(unsafePath) | unsafeMode);

        OperationFailureException failure = Assert.Throws<OperationFailureException>(() =>
            runner.PrepareTrustedSupervisorLaunch(deployment.AssemblyPath, deployment.RuntimeDirectory));

        Assert.Equal(ExitCode.MissingPrerequisite, failure.Code);
        Assert.Equal("wine-unavailable", failure.Kind);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain(deployment.Root, failure.ToString(), StringComparison.Ordinal);
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("launcher")]
    [InlineData("host")]
    public void Runner_interface_preserves_unsafe_trusted_selection_failures(string role)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var deployment = new ProtectedSupervisorDeployment();
        string selectedPath = role == "launcher" ? deployment.LauncherPath : deployment.HostPath;
        File.SetUnixFileMode(selectedPath, File.GetUnixFileMode(selectedPath) | UnixFileMode.OtherWrite);
        IExternalProcessRunner runner = role == "launcher"
            ? new ExternalProcessRunner(selectedPath, null)
            : new ExternalProcessRunner(null, selectedPath);

        OperationFailureException failure = Assert.Throws<OperationFailureException>(() => runner.PrepareTrustedSupervisorLaunch());

        Assert.Equal(ExitCode.MissingPrerequisite, failure.Code);
        Assert.Equal("wine-unavailable", failure.Kind);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain(selectedPath, failure.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(selectedPath + ".launched"));
    }

    [Fact]
    public async Task Unmarked_invocations_do_not_consume_trusted_selection_overrides()
    {
        using var directory = new TemporaryDirectory();
        var runner = new ExternalProcessRunner("/missing/private-setsid", "/missing/private-supervisor-host");
        var invocation = new ExternalProcessInvocation(PrintfPath, ["synthetic-unchanged"], directory.Path);

        ExternalProcessResult result = await runner.RunAsync(invocation);

        Assert.Null(invocation.TrustedSupervisorLaunch);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("synthetic-unchanged", result.StandardOutput);
    }

    [Fact]
    public void Native_protocol_stream_is_bounded_without_buffering_the_manifest()
    {
        Type boundedStream = typeof(ExternalProcessRunner).GetNestedType(
            "BoundedInvocationStream", BindingFlags.NonPublic)!;
        using Stream stream = (Stream)Activator.CreateInstance(boundedStream,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [Stream.Null], null)!;
        int maximum = (int)typeof(ExternalProcessRunner).GetField(
            "MaximumSupervisorInvocationBytes", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
        byte[] buffer = new byte[8 * 1024];
        for (int written = 0; written < maximum; written += buffer.Length)
        {
            stream.Write(buffer, 0, Math.Min(buffer.Length, maximum - written));
        }

        AssertNativeUnavailable(Assert.Throws<OperationFailureException>(() => stream.Write(buffer, 0, 1)));
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("native")]
    [InlineData("supervisor")]
    public async Task Marked_invocations_without_both_closures_fail_redacted_before_probe_serialization_or_launch(
        string missing)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var deployment = new ProtectedRunnableSupervisorDeployment();
        string executable = deployment.CopyProtectedExecutable(ShellPath, "private-command/wine");
        string marker = Path.Join(deployment.Root, "program.launched");
        ExternalProcessSupervisorLaunch launch = deployment.Prepare();
        NativeDependencyClosure native = deployment.PrepareNativeClosure(launch, executable);
        var invocation = new ExternalProcessInvocation(executable,
            ["-c", "printf launched > \"$1\"", "private-program", marker], deployment.Root)
        {
            TrustedSupervisorLaunch = missing == "supervisor" ? null : launch,
            TrustedNativeClosure = missing == "native" ? null : native,
        };
        var runner = new ExternalProcessRunner();

        AssertNativeUnavailable(Assert.Throws<OperationFailureException>(
            () => runner.EnsureExecutableAvailable(invocation)), deployment.Root, marker);
        AssertNativeUnavailable(await Assert.ThrowsAsync<OperationFailureException>(
            () => SerializeInvocationAsync(invocation)), deployment.Root, marker);
        AssertNativeUnavailable(await Assert.ThrowsAsync<OperationFailureException>(
            () => runner.RunAsync(invocation)), deployment.Root, marker);
        Assert.False(File.Exists(marker));
    }

    [SupportedOSPlatform("linux")]
    [Theory]
    [InlineData("outer", "library")]
    [InlineData("outer", "interpreter")]
    [InlineData("outer", "controller")]
    [InlineData("outer", "helper")]
    [InlineData("outer", "program")]
    [InlineData("outer", "host")]
    [InlineData("inner", "library")]
    [InlineData("inner", "interpreter")]
    [InlineData("inner", "controller")]
    [InlineData("inner", "helper")]
    [InlineData("inner", "program")]
    [InlineData("inner", "host")]
    public async Task Native_graph_mutation_is_rejected_at_each_final_setsid_start_without_executing_any_marker(
        string boundary, string role)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var deployment = new ProtectedRunnableSupervisorDeployment();
        string code = deployment.CreateDirectory("private-native-code");
        string controller = deployment.CopyProtectedExecutable(deployment.LauncherPath, "private-controller/setsid");
        string helper = deployment.CopyProtectedExecutable(PrintfPath, "private-helper/wineserver");
        using FileStream shell = WineXeBuildToolchainResolver.OpenProtectedRead(ShellPath);
        string systemInterpreter = NativeElfReader.Read(shell)?.Interpreter
            ?? throw new InvalidOperationException("The fixture shell must use a real ELF interpreter.");
        string interpreter = deployment.CopyProtectedExecutable(
            systemInterpreter, Path.Join("private-native-code", Path.GetFileName(systemInterpreter)));
        string library = Path.Join(code, "libprivate-native.so");
        File.WriteAllBytes(library, new NativeElfTestImage().AddStringTag(14, "libprivate-native.so").Build());
        File.SetUnixFileMode(library, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        string program = Path.Join(code, "wine");
        File.WriteAllBytes(program, new NativeElfTestImage(objectType: 2)
        {
            Interpreter = interpreter,
            EntryPoint = 0x10000,
        }.AddStringTag(1, "libprivate-native.so").AddStringTag(29, "$ORIGIN").Build());
        File.SetUnixFileMode(program, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        ExternalProcessSupervisorLaunch launch = deployment.Prepare(controller);
        NativeDependencyClosure native = deployment.PrepareNativeClosure(
            launch, program, additionalExecutables: [helper], additionalModuleDirectories: [code]);
        var invocation = new ExternalProcessInvocation(program, ["private-native-argument"], deployment.Root)
        {
            TrustedSupervisorLaunch = launch,
            TrustedNativeClosure = native,
        };
        if (boundary == "inner")
        {
            using var pipe = new MemoryStream(await SerializeInvocationAsync(invocation));
            invocation = await ExternalProcessRunner.ReadInvocationAsync(pipe, controller);
        }
        string changed = role switch
        {
            "library" => library,
            "interpreter" => interpreter,
            "controller" => controller,
            "helper" => helper,
            "program" => program,
            _ => deployment.HostPath,
        };
        File.WriteAllText(changed, "#!/bin/sh\nprintf launched > \"$0.launched\"\nexit 93\n");
        File.SetUnixFileMode(changed,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherWrite);
        string method = boundary == "outer" ? "StartSupervisor" : "StartContainedProcess";

        TargetInvocationException reflected = Assert.Throws<TargetInvocationException>(() =>
            typeof(ExternalProcessRunner).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [invocation]));

        AssertNativeUnavailable(Assert.IsType<OperationFailureException>(reflected.InnerException),
            deployment.Root, changed, "private-native-argument");
        foreach (string path in new[] { library, interpreter, controller, helper, program, deployment.HostPath })
        {
            Assert.False(File.Exists(path + ".launched"));
        }
    }

    [SupportedOSPlatform("linux")]
    [Fact]
    public async Task Frozen_native_proof_does_not_admit_a_new_protected_executable_alias()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var deployment = new ProtectedRunnableSupervisorDeployment();
        string registered = deployment.CopyProtectedExecutable(ShellPath, "registered-command/wine");
        string unregistered = deployment.CopyProtectedExecutable(ShellPath, "unregistered-command/wine");
        string marker = Path.Join(deployment.Root, "unbound-program.launched");
        ExternalProcessSupervisorLaunch launch = deployment.Prepare();
        NativeDependencyClosure native = deployment.PrepareNativeClosure(launch, registered);
        var registeredInvocation = new ExternalProcessInvocation(
            registered, ["-c", "printf launched > \"$1\"", "private-program", marker], deployment.Root)
        {
            TrustedSupervisorLaunch = launch,
            TrustedNativeClosure = native,
        };
        JsonObject request = JsonNode.Parse(await SerializeInvocationAsync(registeredInvocation))!.AsObject();
        request["ExecutablePath"] = unregistered;
        using var pipe = new MemoryStream(Encoding.UTF8.GetBytes(request.ToJsonString()));

        AssertNativeUnavailable(await Assert.ThrowsAsync<OperationFailureException>(() =>
            ExternalProcessRunner.ReadInvocationAsync(pipe, launch.ProcessGroupLauncher)), deployment.Root, marker);
        var unboundInvocation = new ExternalProcessInvocation(
            unregistered, registeredInvocation.Arguments, deployment.Root)
        {
            TrustedSupervisorLaunch = launch,
            TrustedNativeClosure = native,
        };
        AssertNativeUnavailable(await Assert.ThrowsAsync<OperationFailureException>(() =>
            new ExternalProcessRunner().RunAsync(unboundInvocation)), deployment.Root, marker);
        Assert.False(File.Exists(marker));
    }

    private static string CreateProcfsStat(
        int processId, char state, int parentId, int groupId, string startTime = "139714052")
    {
        // All 52 Linux stat fields: session through itrealvalue precede starttime (field 22);
        // realistic memory, signal, scheduler, address and exit fields follow it.
        return $"{processId} (jrunner worker) {state} {parentId} {groupId}"
            + $" 27182 0 -1 4194560 312 0 0 0 4 2 0 0 20 0 1 0 {startTime}"
            + " 4788224 384 18446744073709551615 4198400 4210941 140732587987488 0 0 0 0 0 65536 1 0 0 17 3 0 0 0"
            + " 0 0 4218368 4218880 109355008 140732587995203 140732587995219 140732587995219 140732587995629 0\n";
    }

    private static void AssertNativeUnavailable(OperationFailureException failure, params string[] privateData)
    {
        Assert.Equal(ExitCode.MissingPrerequisite, failure.Code);
        Assert.Equal("native-closure-unavailable", failure.Kind);
        Assert.Null(failure.InnerException);
        foreach (string value in privateData)
        {
            Assert.DoesNotContain(value, failure.ToString(), StringComparison.Ordinal);
        }
    }

    private static async Task<byte[]> SerializeInvocationAsync(ExternalProcessInvocation invocation)
    {
        using var pipe = new MemoryStream();
        Task writeTask = (Task)typeof(ExternalProcessRunner).GetMethod(
            "WriteInvocationAsync", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [pipe, invocation, Task.CompletedTask, CancellationToken.None])!;
        await writeTask;
        return pipe.ToArray();
    }

    private static async Task<ProcessTreeScripts> CreateProcessTreeScriptsAsync(
        TemporaryDirectory temporaryDirectory,
        bool stubbornChild = false,
        bool exitLauncher = false,
        bool changeChildSession = false,
        bool respondToRequests = false)
    {
        string launcherPidPath = temporaryDirectory.File("launcher.pid");
        string childPidPath = temporaryDirectory.File("child.pid");
        string leafPidPath = temporaryDirectory.File("leaf.pid");
        string readyPath = temporaryDirectory.File("tree.ready");
        string childReadyPath = temporaryDirectory.File("child.ready");
        string leafReadyPath = temporaryDirectory.File("leaf.ready");
        string launcherTerminationPath = temporaryDirectory.File("launcher.term");
        string childTerminationPath = temporaryDirectory.File("child.term");
        string childExitPath = temporaryDirectory.File("child.exit");
        string requestPath = temporaryDirectory.File("tree.request");
        string responsePath = temporaryDirectory.File("tree.response");
        string leafRequestPath = temporaryDirectory.File("leaf.request");
        string leafResponsePath = temporaryDirectory.File("leaf.response");
        string leafScriptPath = temporaryDirectory.File("leaf.sh");
        string childScriptPath = temporaryDirectory.File("child.sh");
        string launcherScriptPath = temporaryDirectory.File("launcher.sh");
        string leafWork = respondToRequests
            ? $"""
              while :; do
                  while [ ! -f {QuoteForShell(leafRequestPath)} ]; do sleep 0.01; done
                  IFS= read -r request < {QuoteForShell(leafRequestPath)}
                  rm -- {QuoteForShell(leafRequestPath)}
                  printf '%s %s\n' "$$" "$((request * request))" > {QuoteForShell(leafResponsePath + ".tmp")}
                  mv -- {QuoteForShell(leafResponsePath + ".tmp")} {QuoteForShell(leafResponsePath)}
              done
              """
            : "exec sleep 30";
        await File.WriteAllTextAsync(
            leafScriptPath,
            $"""
            {(stubbornChild ? "trap '' TERM" : string.Empty)}
            printf '%s' "$$" > {QuoteForShell(leafPidPath)}
            printf ready > {QuoteForShell(leafReadyPath)}
            {leafWork}
            """);
        string childTermination = $"printf term > {QuoteForShell(childTerminationPath)}"
            + (stubbornChild ? string.Empty : $"; printf exited > {QuoteForShell(childExitPath)}; exit 0");
        string childWork = respondToRequests
            ? $"""
              while :; do
                  while [ ! -f {QuoteForShell(requestPath)} ]; do sleep 0.01; done
                  IFS= read -r request < {QuoteForShell(requestPath)}
                  rm -- {QuoteForShell(requestPath)}
                  printf '%s\n' "$request" > {QuoteForShell(leafRequestPath + ".tmp")}
                  mv -- {QuoteForShell(leafRequestPath + ".tmp")} {QuoteForShell(leafRequestPath)}
                  while [ ! -f {QuoteForShell(leafResponsePath)} ]; do sleep 0.01; done
                  IFS=' ' read -r respondingLeaf leafResult < {QuoteForShell(leafResponsePath)}
                  rm -- {QuoteForShell(leafResponsePath)}
                  printf '%s %s %s\n' "$$" "$respondingLeaf" "$((leafResult + request))" > {QuoteForShell(responsePath + ".tmp")}
                  mv -- {QuoteForShell(responsePath + ".tmp")} {QuoteForShell(responsePath)}
              done
              """
            : "while :; do wait \"$leaf\" || :; done";
        await File.WriteAllTextAsync(
            childScriptPath,
            $"""
            trap {QuoteForShell(childTermination)} TERM
            printf '%s' "$$" > {QuoteForShell(childPidPath)}
            {ShellPath} {QuoteForShell(leafScriptPath)} &
            leaf=$!
            while [ ! -f {QuoteForShell(leafReadyPath)} ]; do sleep 0.01; done
            printf ready > {QuoteForShell(childReadyPath)}
            {childWork}
            """);
        string childLauncher = changeChildSession ? "/usr/bin/setsid -- " : string.Empty;
        string launcherTermination =
            $"printf term > {QuoteForShell(launcherTerminationPath)}; wait \"$child\" || :; exit 0";
        await File.WriteAllTextAsync(
            launcherScriptPath,
            $"""
            printf '%s' "$$" > {QuoteForShell(launcherPidPath)}
            {childLauncher}{ShellPath} {QuoteForShell(childScriptPath)} &
            child=$!
            trap {QuoteForShell(launcherTermination)} TERM
            while [ ! -f {QuoteForShell(childReadyPath)} ]; do sleep 0.01; done
            printf ready > {QuoteForShell(readyPath)}
            {(exitLauncher ? "exit 0" : "wait \"$child\"")}
            """);

        return new ProcessTreeScripts(
            new ExternalProcessInvocation(ShellPath, [launcherScriptPath], temporaryDirectory.Path),
            launcherPidPath,
            childPidPath,
            leafPidPath,
            readyPath,
            launcherTerminationPath,
            childTerminationPath,
            childExitPath,
            requestPath,
            responsePath);
    }

    private static async Task AssertTreeWorkAsync(
        ProcessTreeScripts scripts,
        int childId,
        int leafId,
        int request)
    {
        File.Delete(scripts.ResponsePath);
        string pendingRequestPath = scripts.RequestPath + ".tmp";
        await File.WriteAllTextAsync(pendingRequestPath, $"{request}\n");
        File.Move(pendingRequestPath, scripts.RequestPath);
        await WaitUntilAsync(() => File.Exists(scripts.ResponsePath));

        // The leaf squares the challenge; the child adds it, returning both actual shell PIDs.
        Assert.Equal(
            $"{childId} {leafId} {request * request + request}\n",
            await File.ReadAllTextAsync(scripts.ResponsePath));
    }

    private static async Task<(int SupervisorId, ulong ChildStartTime, ulong LeafStartTime)> AssertAdoptedSessionTreeAsync(
        int childId,
        int leafId)
    {
        string[] child = await ReadLiveFieldsAsync(childId);
        string[] leaf = await ReadLiveFieldsAsync(leafId);
        int supervisorId = int.Parse(child[1]);
        Assert.NotEqual(Environment.ProcessId, supervisorId);
        Assert.Equal(childId, int.Parse(child[2]));
        Assert.Equal(childId, int.Parse(child[3]));
        Assert.Equal(childId, int.Parse(leaf[1]));
        Assert.Equal(child[2], leaf[2]);
        Assert.Equal(child[3], leaf[3]);
        string[] supervisor = await ReadLiveFieldsAsync(supervisorId);
        Assert.Equal(supervisorId, int.Parse(supervisor[2]));
        Assert.Equal(supervisorId, int.Parse(supervisor[3]));
        return (supervisorId, ulong.Parse(child[19]), ulong.Parse(leaf[19]));

        static async Task<string[]> ReadLiveFieldsAsync(int processId)
        {
            string stat = await File.ReadAllTextAsync($"/proc/{processId}/stat");
            string[] fields = stat[(stat.LastIndexOf(')') + 1)..]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Assert.NotEqual("Z", fields[0]);
            return fields;
        }
    }

    private static async Task AssertTreeReapedAsync(ProcessTreeScripts scripts)
    {
        foreach (string pidPath in new[] { scripts.LauncherPidPath, scripts.ChildPidPath, scripts.LeafPidPath })
        {
            int processId = int.Parse(await File.ReadAllTextAsync(pidPath));
            // Assert at the cancellation boundary, not after a second wait that could hide late cleanup.
            Assert.False(Directory.Exists($"/proc/{processId}"), $"Process {processId} was not reaped.");
        }
    }

    private static async Task CancelAndObserveAsync(
        CancellationTokenSource cancellationSource,
        Task<ExternalProcessResult> runTask)
    {
        cancellationSource.Cancel();
        try
        {
            await runTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch
        {
            // Preserve the primary assertion failure while still requesting and observing cleanup.
        }
    }

    private sealed record ProcessTreeScripts(
        ExternalProcessInvocation Invocation,
        string LauncherPidPath,
        string ChildPidPath,
        string LeafPidPath,
        string ReadyPath,
        string LauncherTerminationPath,
        string ChildTerminationPath,
        string ChildExitPath,
        string RequestPath,
        string ResponsePath);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 500; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }

        throw new TimeoutException("The expected child-process state was not observed.");
    }

    private static string QuoteForShell(string value)
    {
        return $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"jrunner-process-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        internal string File(string name)
        {
            return System.IO.Path.Combine(Path, name);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
