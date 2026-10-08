using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JRunner.Core.Contracts;

namespace JRunner.Cli.Infrastructure;

/// <summary>
/// Probes and runs closed external-process invocations without exposing child diagnostics.
/// </summary>
internal interface IExternalProcessRunner
{
    void EnsureExecutableAvailable(ExternalProcessInvocation invocation);

    ExternalProcessSupervisorLaunch PrepareTrustedSupervisorLaunch() =>
        ExternalProcessRunner.PrepareTrustedDefaultSupervisorLaunch();

    Task<ExternalProcessResult> RunAsync(
        ExternalProcessInvocation invocation,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs tokenized invocations in a private Linux supervisor and captures bounded diagnostics.
/// </summary>
internal sealed class ExternalProcessRunner : IExternalProcessRunner
{
    /// <summary>
    /// The default maximum retained byte count for each redirected child stream.
    /// </summary>
    public const int DefaultMaximumCapturedBytes = 64 * 1024;

    private const int ReadBufferSize = 8 * 1024;
    private const int SignalKill = 9;
    private const int SignalTerminate = 15;
    private const int SetChildSubreaper = 36;
    private const int ExecuteAccessMode = 1;
    private const int NoSuchFileOrDirectoryError = 2;
    private const int NoSuchProcessError = 3;
    private const int AccessDeniedError = 13;
    private const string SupervisorArgument = "--jrunner-private-process-supervisor";
    private const string SupervisorProtocol = "JRunner.ExternalProcess/1";
    private const byte CancelCommand = 1;
    private static readonly byte[] CancellationMessage = [CancelCommand];
    private const int AuthenticationHeaderSize = 60;
    private const int AuthenticationNonceOffset = 4;
    private const int AuthenticationNonceSize = 32;
    private const uint AuthenticationMagic = 0x3150524A;
    private const int MaximumSupervisorInvocationBytes = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions InvocationJsonOptions = new()
    {
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 64,
    };
    // Reserve the final second of one five-second cleanup deadline for SIGKILL and actual reaping.
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan GracefulTerminationTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan StreamCompletionTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SupervisorResponseTimeout = CleanupTimeout + StreamCompletionTimeout;
    private static readonly TimeSpan ProcessGroupPollInterval = TimeSpan.FromMilliseconds(10);
    private static readonly string[] ProcessGroupLaunchers = ["/usr/bin/setsid", "/bin/setsid"];
    private static readonly Encoding ChildStandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly object SupervisorLaunchGate = new();
    private readonly int _maximumCapturedBytes;
    private readonly string? _trustedProcessGroupLauncher;
    private readonly string? _trustedSupervisorHost;

    public ExternalProcessRunner(int maximumCapturedBytes = DefaultMaximumCapturedBytes)
    {
        if (maximumCapturedBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumCapturedBytes),
                maximumCapturedBytes,
                "The maximum captured byte count must be positive.");
        }

        _maximumCapturedBytes = maximumCapturedBytes;
    }

    /// <summary>Supplies deterministic trusted-selection overrides without changing ordinary invocations.</summary>
    internal ExternalProcessRunner(
        string? trustedProcessGroupLauncher,
        string? trustedSupervisorHost,
        int maximumCapturedBytes = DefaultMaximumCapturedBytes)
        : this(maximumCapturedBytes)
    {
        _trustedProcessGroupLauncher = trustedProcessGroupLauncher;
        _trustedSupervisorHost = trustedSupervisorHost;
    }

    internal ExternalProcessSupervisorLaunch PrepareTrustedSupervisorLaunch() =>
        PrepareTrustedSupervisorLaunch(typeof(ExternalProcessRunner).Assembly.Location, RuntimeEnvironment.GetRuntimeDirectory());

    ExternalProcessSupervisorLaunch IExternalProcessRunner.PrepareTrustedSupervisorLaunch() =>
        PrepareTrustedSupervisorLaunch();

    /// <summary>Exercises the same selection against an explicitly located protected deployment.</summary>
    internal ExternalProcessSupervisorLaunch PrepareTrustedSupervisorLaunch(string assemblyPath, string runtimeDirectory) =>
        PrepareTrustedSupervisorLaunch(_trustedProcessGroupLauncher, _trustedSupervisorHost, assemblyPath, runtimeDirectory);

    internal static ExternalProcessSupervisorLaunch PrepareTrustedDefaultSupervisorLaunch() =>
        PrepareTrustedSupervisorLaunch(null, null, typeof(ExternalProcessRunner).Assembly.Location, RuntimeEnvironment.GetRuntimeDirectory());

    private static ExternalProcessSupervisorLaunch PrepareTrustedSupervisorLaunch(
        string? processGroupLauncher,
        string? supervisorHost,
        string assemblyPath,
        string runtimeDirectory)
    {
        try
        {
            EnsureLinuxExecution();
            string selectedLauncher = processGroupLauncher ?? GetProcessGroupLauncher();
            // Reject an unsafe selected launcher before reading any host/configuration data.
            WineXeBuildToolchainResolver.ValidateProtectedExecutable(selectedLauncher);
            if (supervisorHost is not null)
            {
                WineXeBuildToolchainResolver.ValidateProtectedExecutable(supervisorHost);
            }
            IReadOnlyList<string> hostArguments = SelectSupervisorHostArguments(assemblyPath);
            if (supervisorHost is not null)
            {
                string[] overriddenArguments = hostArguments.ToArray();
                overriddenArguments[0] = supervisorHost;
                hostArguments = overriddenArguments;
            }
            return ExternalProcessSupervisorLaunch.Prepare(selectedLauncher, hostArguments, assemblyPath, runtimeDirectory);
        }
        catch (Exception)
        {
            throw ExternalProcessSupervisorLaunch.Unavailable();
        }
    }

    /// <summary>
    /// Checks executable resolution and execute access without starting a process or changing its environment.
    /// </summary>
    public void EnsureExecutableAvailable(ExternalProcessInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        try
        {
            EnsureLinuxExecution();
            EnsureExecutableCanStart(invocation);
        }
        catch (OperationFailureException exception) when (IsNativeClosureFailure(exception))
        {
            throw NativeElfReader.Failure();
        }
        catch (Exception exception)
        {
            throw new ExternalProcessFailureException(
                new ExternalProcessFailure(ExternalProcessFailureKind.LaunchFailed),
                exception);
        }
    }

    /// <summary>
    /// Uses ArgumentList for every launch. Cancellation is exposed only after the private supervisor
    /// confirms that the launcher and all native descendants have been terminated and reaped.
    /// </summary>
    public async Task<ExternalProcessResult> RunAsync(
        ExternalProcessInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureExecutableAvailable(invocation);

        using SupervisorProcess supervisor = StartSupervisor(invocation);
        using var streamLifetime = new CancellationTokenSource();
        using var inputLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, streamLifetime.Token);
        Task<CapturedProcessStream>? standardOutputTask = null;
        Task<CapturedProcessStream>? standardErrorTask = null;
        Task? standardInputTask = null;
        Task? invocationTask = null;
        Task? authenticationTask = null;
        Task<SupervisorOutcome>? outcomeTask = null;
        try
        {
            standardOutputTask = CaptureAsync(supervisor.Process.StandardOutput.BaseStream, _maximumCapturedBytes, streamLifetime.Token);
            standardErrorTask = CaptureAsync(supervisor.Process.StandardError.BaseStream, _maximumCapturedBytes, streamLifetime.Token);
            outcomeTask = ReadOutcomeAsync(supervisor.ResultPipe, streamLifetime.Token);
            authenticationTask = AuthenticateSupervisorAsync(supervisor, streamLifetime.Token);
            invocationTask = WriteInvocationAsync(supervisor.InvocationPipe, invocation, authenticationTask, streamLifetime.Token);
            if (invocation.HasStandardInput)
            {
                standardInputTask = WriteStandardInputAsync(supervisor.Process.StandardInput, invocation.StandardInputBytes, inputLifetime.Token);
            }

            await supervisor.Process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            // Native children cannot inherit the private protocol descriptors. Bound I/O settlement anyway,
            // including capture pipes held by an unexpected escaped holder or a delayed I/O completion.
            streamLifetime.CancelAfter(StreamCompletionTimeout);
            await invocationTask.WaitAsync(streamLifetime.Token).ConfigureAwait(false);
            SupervisorOutcome outcome = await outcomeTask.WaitAsync(streamLifetime.Token).ConfigureAwait(false);
            EnsureSuccessfulSupervision(outcome);
            if (standardInputTask is not null)
            {
                await standardInputTask.WaitAsync(streamLifetime.Token).ConfigureAwait(false);
            }

            CapturedProcessStream[] captures = await Task.WhenAll(standardOutputTask, standardErrorTask)
                .WaitAsync(streamLifetime.Token)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new ExternalProcessResult(
                outcome.ExitCode!.Value,
                captures[0].Text,
                captures[0].Truncated,
                captures[0].ByteCount,
                captures[1].Text,
                captures[1].Truncated,
                captures[1].ByteCount);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                SupervisorOutcome outcome = await CancelSupervisorAsync(supervisor, outcomeTask).ConfigureAwait(false);
                if (!outcome.ScopeReaped)
                {
                    throw new IOException("The external process tree could not be terminated and reaped.");
                }
            }
            catch (Exception exception)
            {
                throw new ExternalProcessFailureException(
                    new ExternalProcessFailure(ExternalProcessFailureKind.ExecutionFailed),
                    exception);
            }
            finally
            {
                CloseRedirectedStreams(supervisor.Process, invocation.HasStandardInput, streamLifetime);
                supervisor.ClosePipes();
                await ObserveTasksAsync(standardOutputTask, standardErrorTask, standardInputTask, invocationTask, outcomeTask, authenticationTask)
                    .ConfigureAwait(false);
            }

            throw;
        }
        catch (Exception exception)
        {
            Exception cause = exception;
            ExternalProcessFailure failure = exception is ExternalProcessFailureException processFailure
                ? processFailure.Failure
                : new ExternalProcessFailure(ExternalProcessFailureKind.ExecutionFailed);
            try
            {
                SupervisorOutcome outcome = await CancelSupervisorAsync(supervisor, outcomeTask).ConfigureAwait(false);
                if (!outcome.ScopeReaped)
                {
                    throw new IOException("The external process tree could not be terminated and reaped.");
                }
            }
            catch (Exception cleanupException)
            {
                failure = new ExternalProcessFailure(ExternalProcessFailureKind.ExecutionFailed);
                cause = new AggregateException(exception, cleanupException);
            }
            finally
            {
                CloseRedirectedStreams(supervisor.Process, invocation.HasStandardInput, streamLifetime);
                supervisor.ClosePipes();
                await ObserveTasksAsync(standardOutputTask, standardErrorTask, standardInputTask, invocationTask, outcomeTask, authenticationTask)
                    .ConfigureAwait(false);
            }

            if (IsNativeClosureFailure(cause))
            {
                throw NativeElfReader.Failure();
            }
            throw new ExternalProcessFailureException(failure, cause);
        }
    }

    internal static bool IsSupervisorInvocation(IReadOnlyList<string> arguments)
    {
        return arguments.Count > 0 && string.Equals(arguments[0], SupervisorArgument, StringComparison.Ordinal);
    }

    /// <summary>
    /// Private entrypoint, before ordinary CLI initialization. Authenticated inherited descriptors carry
    /// the invocation and result; argv contains descriptor numbers and an optional bound protected
    /// launcher alias, never invocation arguments or standard-input secrets.
    /// This process launches exactly one managed Process, so its adopted native orphans cannot be siblings
    /// from another invocation. That ownership remains true even for an unseen, already-zombie double fork.
    /// </summary>
    internal static async Task<int> RunSupervisorAsync(IReadOnlyList<string> arguments)
    {
        AnonymousPipeClientStream? invocationPipe = null;
        AnonymousPipeClientStream? controlPipe = null;
        AnonymousPipeClientStream? resultPipe = null;
        Socket? authenticationSocket = null;
        using var cancellation = new CancellationTokenSource();
        using var controlLifetime = new CancellationTokenSource();
        Task? controlTask = null;
        SupervisorOutcome outcome;
        try
        {
            EnsureLinuxExecution();
            if (arguments.Count is not (5 or 6) || !TryReadDescriptor(arguments[1], out int invocationDescriptor)
                || !TryReadDescriptor(arguments[2], out int controlDescriptor)
                || !TryReadDescriptor(arguments[3], out int resultDescriptor)
                || !TryReadDescriptor(arguments[4], out int authenticationDescriptor)
                || invocationDescriptor == controlDescriptor || invocationDescriptor == resultDescriptor
                || invocationDescriptor == authenticationDescriptor || controlDescriptor == resultDescriptor
                || controlDescriptor == authenticationDescriptor || resultDescriptor == authenticationDescriptor)
            {
                throw new IOException("The private process protocol is unavailable.");
            }

            string? trustedLauncher = arguments.Count == 6 ? arguments[5] : null;
            if (trustedLauncher is not null)
            {
                WineXeBuildToolchainResolver.ValidateProtectedExecutable(trustedLauncher);
            }

            invocationPipe = new AnonymousPipeClientStream(PipeDirection.In, arguments[1]);
            controlPipe = new AnonymousPipeClientStream(PipeDirection.In, arguments[2]);
            resultPipe = new AnonymousPipeClientStream(PipeDirection.Out, arguments[3]);
            authenticationSocket = new Socket(new SafeSocketHandle((nint)authenticationDescriptor, ownsHandle: true));
            MakePrivateDescriptor(invocationDescriptor);
            MakePrivateDescriptor(controlDescriptor);
            MakePrivateDescriptor(resultDescriptor);
            MakePrivateDescriptor(authenticationDescriptor);
            await AuthenticateParentAsync(authenticationSocket, invocationDescriptor, controlDescriptor, resultDescriptor,
                    trustedLauncher ?? GetProcessGroupLauncher())
                .ConfigureAwait(false);
            authenticationSocket.Dispose();
            authenticationSocket = null;
            controlTask = ListenForCancellationAsync(controlPipe, cancellation, controlLifetime.Token);
            ExternalProcessInvocation invocation = await ReadInvocationAsync(
                invocationPipe, trustedLauncher, cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            if (SetProcessControl(SetChildSubreaper, 1, 0, 0, 0) != 0)
            {
                throw new IOException("Unable to establish Linux child-process reaping.");
            }

            if (invocation.TrustedNativeClosure is not null)
            {
                // Only this dedicated supervisor changes its umask; the CLI and unmarked calls keep
                // their existing creation policy. Wine's newly created prefix code stays owner-private.
                SetFileCreationMask(0x3f); // 0077
            }
            outcome = await ExecuteSupervisedInvocationAsync(invocation, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            outcome = new SupervisorOutcome(null, null, Cancelled: true, ScopeReaped: true);
        }
        catch (OperationFailureException exception) when (IsNativeClosureFailure(exception))
        {
            outcome = new SupervisorOutcome(ExternalProcessFailureKind.LaunchFailed, null,
                Cancelled: false, ScopeReaped: true, NativeClosureUnavailable: true);
        }
        catch (ExternalProcessFailureException exception)
        {
            outcome = new SupervisorOutcome(exception.Failure.Kind, null, Cancelled: false, ScopeReaped: true);
        }
        catch (Exception)
        {
            outcome = new SupervisorOutcome(ExternalProcessFailureKind.LaunchFailed, null, Cancelled: false, ScopeReaped: true);
        }

        try
        {
            if (resultPipe is null)
            {
                return 1;
            }

            await JsonSerializer.SerializeAsync(resultPipe, outcome)
                .WaitAsync(StreamCompletionTimeout)
                .ConfigureAwait(false);
            return outcome.NativeClosureUnavailable ? (int)ExitCode.MissingPrerequisite
                : outcome.FailureKind is null ? 0 : 1;
        }
        catch (Exception)
        {
            return 1;
        }
        finally
        {
            controlLifetime.Cancel();
            invocationPipe?.Dispose();
            controlPipe?.Dispose();
            resultPipe?.Dispose();
            authenticationSocket?.Dispose();
            await ObserveTasksAsync(controlTask).ConfigureAwait(false);
        }
    }

    private static async Task<SupervisorOutcome> ExecuteSupervisedInvocationAsync(
        ExternalProcessInvocation invocation,
        CancellationToken cancellationToken)
    {
        using Process process = StartContainedProcess(invocation);
        var tree = new LinuxProcessTree(process);
        try
        {
            await tree.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new SupervisorOutcome(null, process.ExitCode, Cancelled: false, ScopeReaped: true);
        }
        catch (Exception exception)
        {
            bool cancelled = exception is OperationCanceledException && cancellationToken.IsCancellationRequested;
            try
            {
                await tree.TerminateAndReapAsync(gracefully: cancelled).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return new SupervisorOutcome(ExternalProcessFailureKind.ExecutionFailed, null, Cancelled: false, ScopeReaped: false);
            }

            return cancelled
                ? new SupervisorOutcome(null, null, Cancelled: true, ScopeReaped: true)
                : new SupervisorOutcome(ExternalProcessFailureKind.ExecutionFailed, null, Cancelled: false, ScopeReaped: true);
        }
    }

    private static SupervisorProcess StartSupervisor(ExternalProcessInvocation invocation)
    {
        // Only the client ends are inheritable, for this short launch interval. Serialize this interval
        // so concurrent runner launches cannot inherit each other's result writers and delay their EOF.
        lock (SupervisorLaunchGate)
        {
            AnonymousPipeServerStream? invocationPipe = null;
            AnonymousPipeServerStream? controlPipe = null;
            AnonymousPipeServerStream? resultPipe = null;
            Socket? authenticationServer = null;
            Socket? authenticationClient = null;
            byte[]? authenticationHeader = null;
            Process? process = null;
            try
            {
                invocationPipe = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
                controlPipe = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
                resultPipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
                (authenticationServer, authenticationClient) = CreateAuthenticationSockets();
                authenticationHeader = CreateAuthenticationHeader(invocationPipe, controlPipe, resultPipe);
                var startInfo = new ProcessStartInfo
                {
                    FileName = invocation.TrustedSupervisorLaunch?.ProcessGroupLauncher ?? GetProcessGroupLauncher(),
                    WorkingDirectory = invocation.WorkingDirectory,
                    UseShellExecute = false,
                    RedirectStandardInput = invocation.HasStandardInput,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                if (invocation.HasStandardInput)
                {
                    startInfo.StandardInputEncoding = ChildStandardInputEncoding;
                }

                startInfo.ArgumentList.Add("--wait");
                startInfo.ArgumentList.Add("--");
                if (invocation.TrustedSupervisorLaunch is ExternalProcessSupervisorLaunch trustedLaunch)
                {
                    foreach (string argument in trustedLaunch.HostArguments)
                    {
                        startInfo.ArgumentList.Add(argument);
                    }
                }
                else
                {
                    AddSupervisorHostArguments(startInfo);
                }
                startInfo.ArgumentList.Add(SupervisorArgument);
                startInfo.ArgumentList.Add(invocationPipe.GetClientHandleAsString());
                startInfo.ArgumentList.Add(controlPipe.GetClientHandleAsString());
                startInfo.ArgumentList.Add(resultPipe.GetClientHandleAsString());
                startInfo.ArgumentList.Add(authenticationClient.SafeHandle.DangerousGetHandle().ToInt32().ToString(CultureInfo.InvariantCulture));
                if (invocation.TrustedSupervisorLaunch is not null)
                {
                    startInfo.ArgumentList.Add(invocation.TrustedSupervisorLaunch.ProcessGroupLauncher);
                }
                ApplyEnvironmentUpdates(startInfo, invocation.EnvironmentUpdates);
                ApplyTrustedLaunchProtection(startInfo, invocation);
                process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("The external process supervisor could not be started.");
                invocationPipe.DisposeLocalCopyOfClientHandle();
                controlPipe.DisposeLocalCopyOfClientHandle();
                resultPipe.DisposeLocalCopyOfClientHandle();
                authenticationClient.Dispose();
                authenticationClient = null;
                return new SupervisorProcess(process, invocationPipe, controlPipe, resultPipe, authenticationServer,
                    authenticationHeader, startInfo.FileName);
            }
            catch (Exception exception)
            {
                process?.Dispose();
                invocationPipe?.Dispose();
                controlPipe?.Dispose();
                resultPipe?.Dispose();
                authenticationServer?.Dispose();
                authenticationClient?.Dispose();
                if (authenticationHeader is not null)
                {
                    CryptographicOperations.ZeroMemory(authenticationHeader);
                }
                if (IsNativeClosureFailure(exception))
                {
                    throw NativeElfReader.Failure();
                }
                throw new ExternalProcessFailureException(
                    new ExternalProcessFailure(ExternalProcessFailureKind.LaunchFailed),
                    exception);
            }
        }
    }

    private static void AddSupervisorHostArguments(ProcessStartInfo startInfo)
    {
        foreach (string argument in SelectSupervisorHostArguments(typeof(ExternalProcessRunner).Assembly.Location))
        {
            startInfo.ArgumentList.Add(argument);
        }
    }

    private static IReadOnlyList<string> SelectSupervisorHostArguments(string assemblyPath)
    {
        Assembly assembly = typeof(ExternalProcessRunner).Assembly;
        string? currentHost = Environment.ProcessPath;
        if (Assembly.GetEntryAssembly() == assembly && currentHost is not null
            && !string.Equals(Path.GetFileNameWithoutExtension(currentHost), "dotnet", StringComparison.Ordinal))
        {
            return [currentHost];
        }

        string appHostPath = Path.ChangeExtension(assemblyPath, null);
        if (File.Exists(appHostPath) && HasExecuteAccess(appHostPath))
        {
            return [appHostPath];
        }

        string? dotnetHost = currentHost is not null
            && string.Equals(Path.GetFileNameWithoutExtension(currentHost), "dotnet", StringComparison.Ordinal)
                ? currentHost
                : Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrEmpty(dotnetHost) || !File.Exists(dotnetHost) || !HasExecuteAccess(dotnetHost))
        {
            dotnetHost = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet"));
        }

        if (!File.Exists(dotnetHost) || !HasExecuteAccess(dotnetHost) || !File.Exists(assemblyPath))
        {
            throw new IOException("The managed process supervisor host is unavailable.");
        }

        var arguments = new List<string> { Path.GetFullPath(dotnetHost) };
        arguments.Add("exec");
        string runtimeConfig = Path.ChangeExtension(assemblyPath, ".runtimeconfig.json");
        if (!File.Exists(runtimeConfig))
        {
            runtimeConfig = FindCurrentHostConfiguration("--runtimeconfig", ".runtimeconfig.json");
        }

        arguments.Add("--runtimeconfig");
        arguments.Add(Path.GetFullPath(runtimeConfig));
        string depsFile = Path.ChangeExtension(assemblyPath, ".deps.json");
        if (!File.Exists(depsFile))
        {
            string? activeDeps = AppContext.GetData("APP_CONTEXT_DEPS_FILES") as string;
            depsFile = activeDeps?.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(path => File.Exists(path)
                    && (string.Equals(Path.GetDirectoryName(path), Path.GetDirectoryName(assemblyPath), StringComparison.Ordinal)
                        || string.Equals(Path.GetDirectoryName(path), Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), StringComparison.Ordinal)))
                ?? FindCurrentHostConfiguration("--depsfile", ".deps.json");
        }

        arguments.Add("--depsfile");
        arguments.Add(Path.GetFullPath(depsFile));
        arguments.Add(assemblyPath);
        return arguments;
    }

    private static string FindCurrentHostConfiguration(string option, string extension)
    {
        string parentWorkingDirectory = Environment.CurrentDirectory;
        string[] commandLine = Encoding.UTF8.GetString(File.ReadAllBytes("/proc/self/cmdline"))
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index + 1 < commandLine.Length; index++)
        {
            if (string.Equals(commandLine[index], option, StringComparison.Ordinal))
            {
                string absolutePath = ResolveHostConfigurationPath(commandLine[index + 1], parentWorkingDirectory);
                if (File.Exists(absolutePath))
                {
                    return absolutePath;
                }
            }
        }

        string? entryPath = Assembly.GetEntryAssembly()?.Location;
        if (!string.IsNullOrEmpty(entryPath))
        {
            string path = Path.ChangeExtension(entryPath, extension);
            if (File.Exists(path))
            {
                return Path.GetFullPath(path);
            }
        }

        throw new IOException("The managed process supervisor configuration is unavailable.");
    }

    private static string ResolveHostConfigurationPath(string path, string parentWorkingDirectory)
    {
        // Freeze host-relative references before ProcessStartInfo applies the invocation's different cwd.
        return Path.GetFullPath(path, parentWorkingDirectory);
    }

    private static Process StartContainedProcess(ExternalProcessInvocation invocation)
    {
        try
        {
            EnsureExecutableCanStart(invocation);
            var startInfo = new ProcessStartInfo
            {
                FileName = invocation.TrustedSupervisorLaunch?.ProcessGroupLauncher ?? GetProcessGroupLauncher(),
                WorkingDirectory = invocation.WorkingDirectory,
                UseShellExecute = false,
                // Standard descriptors are inherited unchanged. Only the parent runner captures/writes them.
                RedirectStandardInput = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
            };
            startInfo.ArgumentList.Add("--wait");
            startInfo.ArgumentList.Add("--");
            startInfo.ArgumentList.Add(invocation.ExecutablePath);
            foreach (string argument in invocation.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            ApplyEnvironmentUpdates(startInfo, invocation.EnvironmentUpdates);
            ApplyTrustedLaunchProtection(startInfo, invocation);
            return Process.Start(startInfo)
                ?? throw new InvalidOperationException("The external process could not be started.");
        }
        catch (OperationFailureException exception) when (IsNativeClosureFailure(exception))
        {
            throw NativeElfReader.Failure();
        }
        catch (Exception exception)
        {
            throw new ExternalProcessFailureException(new ExternalProcessFailure(ExternalProcessFailureKind.LaunchFailed), exception);
        }
    }

    private static void ApplyEnvironmentUpdates(ProcessStartInfo startInfo, IReadOnlyDictionary<string, string?> updates)
    {
        foreach (KeyValuePair<string, string?> update in updates)
        {
            if (update.Value is null)
            {
                startInfo.Environment.Remove(update.Key);
            }
            else
            {
                startInfo.Environment[update.Key] = update.Value;
            }
        }
    }

    private static void ApplyTrustedLaunchProtection(ProcessStartInfo startInfo, ExternalProcessInvocation invocation)
    {
        NativeDependencyClosure? native = RequireTrustedNativeClosure(invocation);
        if (native is null)
        {
            return;
        }

        ExternalProcessSupervisorLaunch launch = invocation.TrustedSupervisorLaunch!;
        native.ApplyClosedEnvironment(startInfo);
        launch.ApplyClosedRuntimeEnvironment(startInfo);
        RevalidateTrustedInvocation(invocation, native, launch);
    }

    private static NativeDependencyClosure? RequireTrustedNativeClosure(ExternalProcessInvocation invocation)
    {
        if ((invocation.TrustedSupervisorLaunch is null) != (invocation.TrustedNativeClosure is null))
        {
            throw NativeElfReader.Failure();
        }
        invocation.TrustedNativeClosure?.EnsureProduction();
        return invocation.TrustedNativeClosure;
    }

    private static void RevalidateTrustedInvocation(
        ExternalProcessInvocation invocation, NativeDependencyClosure native, ExternalProcessSupervisorLaunch launch)
    {
        native.ValidateInputs(launch.NativeInputs);
        native.Revalidate();
        launch.Revalidate();
        native.ValidateExecutable(launch.ProcessGroupLauncher);
        native.ValidateExecutable(launch.HostArguments[0]);
        native.ValidateExecutable(invocation.ExecutablePath);
    }

    private static bool IsNativeClosureFailure(Exception exception) =>
        exception is OperationFailureException { Code: ExitCode.MissingPrerequisite, Kind: "native-closure-unavailable" };

    private static (Socket Server, Socket Client) CreateAuthenticationSockets()
    {
        const int unixDomain = 1;
        const int streamWithCloseOnExec = 1 | 0x80000;
        if (CreateSocketPair(unixDomain, streamWithCloseOnExec, 0, out SocketPairDescriptors descriptors) != 0)
        {
            throw new IOException("The private process capability is unavailable.");
        }

        var serverHandle = new SafeSocketHandle((nint)descriptors.Server, ownsHandle: true);
        var clientHandle = new SafeSocketHandle((nint)descriptors.Client, ownsHandle: true);
        Socket? server = null;
        Socket? client = null;
        try
        {
            server = new Socket(serverHandle);
            client = new Socket(clientHandle);
            int flags = ControlDescriptor(descriptors.Client, 1, 0);
            if (flags < 0 || ControlDescriptor(descriptors.Client, 2, flags & ~1) < 0)
            {
                throw new IOException("The private process capability is unavailable.");
            }

            return (server, client);
        }
        catch
        {
            server?.Dispose();
            client?.Dispose();
            serverHandle.Dispose();
            clientHandle.Dispose();
            throw;
        }
    }

    private static byte[] CreateAuthenticationHeader(AnonymousPipeServerStream invocationPipe,
        AnonymousPipeServerStream controlPipe, AnonymousPipeServerStream resultPipe)
    {
        if (!TryReadProcessInfo(Environment.ProcessId, out LinuxProcessInfo parent))
        {
            throw new IOException("The private process capability is unavailable.");
        }

        byte[] header = new byte[AuthenticationHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, AuthenticationMagic);
        RandomNumberGenerator.Fill(header.AsSpan(AuthenticationNonceOffset, AuthenticationNonceSize));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(36), parent.ProcessId);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(40), parent.StartTime);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(48), invocationPipe.SafePipeHandle.DangerousGetHandle().ToInt32());
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(52), controlPipe.SafePipeHandle.DangerousGetHandle().ToInt32());
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(56), resultPipe.SafePipeHandle.DangerousGetHandle().ToInt32());
        return header;
    }

    private static async Task AuthenticateSupervisorAsync(SupervisorProcess supervisor, CancellationToken cancellationToken)
    {
        using var authenticationLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        authenticationLifetime.CancelAfter(SupervisorResponseTimeout);
        byte[] acknowledgement = new byte[36];
        byte[]? expected = null;
        try
        {
            using var stream = new NetworkStream(supervisor.AuthenticationSocket, ownsSocket: false);
            await stream.WriteAsync(supervisor.AuthenticationHeader, authenticationLifetime.Token).ConfigureAwait(false);
            await stream.ReadExactlyAsync(acknowledgement, authenticationLifetime.Token).ConfigureAwait(false);
            int childId = BinaryPrimitives.ReadInt32LittleEndian(acknowledgement);
            if (!IsExpectedSupervisorDescendant(childId, supervisor.Process.Id, supervisor.ProcessGroupLauncher))
            {
                throw new IOException("The private process capability is unavailable.");
            }

            expected = CreateAuthenticationAcknowledgement(supervisor.AuthenticationHeader, childId);
            if (!CryptographicOperations.FixedTimeEquals(acknowledgement, expected))
            {
                throw new IOException("The private process capability is unavailable.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(acknowledgement);
            if (expected is not null)
            {
                CryptographicOperations.ZeroMemory(expected);
            }

            CryptographicOperations.ZeroMemory(supervisor.AuthenticationHeader);
            supervisor.AuthenticationSocket.Dispose();
        }
    }

    private static async Task AuthenticateParentAsync(Socket capability, int invocationDescriptor,
        int controlDescriptor, int resultDescriptor, string processGroupLauncher)
    {
        if (capability.AddressFamily != AddressFamily.Unix || capability.SocketType != SocketType.Stream)
        {
            throw new IOException("The private process capability is unavailable.");
        }

        const int socketLevel = 1;
        const int peerCredentialsOption = 17;
        uint credentialSize = 12;
        if (GetSocketPeerCredentials(capability.SafeHandle.DangerousGetHandle().ToInt32(), socketLevel,
                peerCredentialsOption, out SocketPeerCredentials peer, ref credentialSize) != 0
            || credentialSize != 12 || peer.UserId != GetEffectiveUserId()
            || peer.ProcessId == Environment.ProcessId
            || !IsAuthorizedParentAncestor(peer.ProcessId, processGroupLauncher)
            || !IsTrustedParentHost(peer.ProcessId))
        {
            throw new IOException("The private process capability is unavailable.");
        }

        using var authenticationLifetime = new CancellationTokenSource(SupervisorResponseTimeout);
        byte[] header = new byte[AuthenticationHeaderSize];
        byte[]? acknowledgement = null;
        try
        {
            using var stream = new NetworkStream(capability, ownsSocket: false);
            await stream.ReadExactlyAsync(header, authenticationLifetime.Token).ConfigureAwait(false);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != AuthenticationMagic
                || BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(36)) != peer.ProcessId
                || !TryReadProcessInfo(peer.ProcessId, out LinuxProcessInfo parent)
                || parent.StartTime != BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(40)))
            {
                throw new IOException("The private process capability is unavailable.");
            }

            ValidateInheritedPipe(peer.ProcessId, BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(48)), invocationDescriptor, childReads: true);
            ValidateInheritedPipe(peer.ProcessId, BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(52)), controlDescriptor, childReads: true);
            ValidateInheritedPipe(peer.ProcessId, BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(56)), resultDescriptor, childReads: false);
            if (!TryReadProcessInfo(peer.ProcessId, out LinuxProcessInfo currentParent) || currentParent.StartTime != parent.StartTime)
            {
                throw new IOException("The private process capability is unavailable.");
            }

            acknowledgement = CreateAuthenticationAcknowledgement(header, Environment.ProcessId);
            await stream.WriteAsync(acknowledgement, authenticationLifetime.Token).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(header);
            if (acknowledgement is not null)
            {
                CryptographicOperations.ZeroMemory(acknowledgement);
            }
        }
    }

    private static byte[] CreateAuthenticationAcknowledgement(ReadOnlySpan<byte> header, int childId)
    {
        Span<byte> binding = stackalloc byte[28];
        header[36..].CopyTo(binding);
        BinaryPrimitives.WriteInt32LittleEndian(binding[24..], childId);
        byte[] acknowledgement = new byte[36];
        BinaryPrimitives.WriteInt32LittleEndian(acknowledgement, childId);
        HMACSHA256.HashData(header.Slice(AuthenticationNonceOffset, AuthenticationNonceSize), binding, acknowledgement.AsSpan(4));
        return acknowledgement;
    }

    private static void ValidateInheritedPipe(int parentId, int serverDescriptor, int childDescriptor, bool childReads)
    {
        string? childIdentity = new FileInfo($"/proc/self/fd/{childDescriptor}").LinkTarget;
        string? parentIdentity = new FileInfo($"/proc/{parentId}/fd/{serverDescriptor}").LinkTarget;
        int childFlags = ControlDescriptor(childDescriptor, 3, 0);
        ulong? parentFlags = ReadDescriptorFlags(parentId, serverDescriptor);
        if (serverDescriptor <= 2 || childIdentity is null || !childIdentity.StartsWith("pipe:[", StringComparison.Ordinal)
            || !string.Equals(childIdentity, parentIdentity, StringComparison.Ordinal)
            || childFlags < 0 || (childFlags & 3) != (childReads ? 0 : 1)
            || !parentFlags.HasValue || (parentFlags.Value & 3) != (childReads ? 1UL : 0UL))
        {
            throw new IOException("The private process capability is unavailable.");
        }
    }

    private static ulong? ReadDescriptorFlags(int processId, int descriptor)
    {
        foreach (string line in File.ReadLines($"/proc/{processId}/fdinfo/{descriptor}"))
        {
            if (line.StartsWith("flags:", StringComparison.Ordinal))
            {
                return Convert.ToUInt64(line.AsSpan(6).Trim().ToString(), 8);
            }
        }

        return null;
    }

    private static bool IsAuthorizedParentAncestor(int parentId, string processGroupLauncher)
    {
        int currentId = Environment.ProcessId;
        for (int depth = 0; depth < 8; depth++)
        {
            if (!TryReadProcessInfo(currentId, out LinuxProcessInfo current))
            {
                return false;
            }

            if (current.ParentProcessId == parentId)
            {
                return true;
            }

            currentId = current.ParentProcessId;
            if (currentId <= 1 || !SameCanonicalFile($"/proc/{currentId}/exe", processGroupLauncher))
            {
                return false;
            }
        }

        return false;
    }

    private static bool IsExpectedSupervisorDescendant(int childId, int launcherId, string processGroupLauncher)
    {
        for (int depth = 0; depth < 8; depth++)
        {
            if (childId == launcherId)
            {
                return true;
            }

            if (!TryReadProcessInfo(childId, out LinuxProcessInfo child)
                || !SameCanonicalFile($"/proc/{child.ParentProcessId}/exe", processGroupLauncher))
            {
                return false;
            }

            childId = child.ParentProcessId;
        }

        return false;
    }

    /// <summary>
    /// Trust is the actually executed CLI image, or the installed CLR executing this CLI's identical
    /// mapped assembly. Testhost additionally needs the real deployed testhost image alongside that
    /// mapped CLI and an identical image beside this helper. This is not a privilege boundary against
    /// code injection or replacement of an authorized same-user CLR/CLI/testhost deployment.
    /// </summary>
    private static bool IsTrustedParentHost(int parentId)
    {
        Assembly cli = typeof(ExternalProcessRunner).Assembly;
        string selfImage = $"/proc/{Environment.ProcessId}/exe";
        string parentImage = $"/proc/{parentId}/exe";
        if (Assembly.GetEntryAssembly() == cli && Environment.ProcessPath is string currentHost
            && !string.Equals(Path.GetFileNameWithoutExtension(currentHost), "dotnet", StringComparison.Ordinal)
            && SameCanonicalFile(parentImage, selfImage))
        {
            return true;
        }

        string installedHost = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet"));
        bool actualClrHost = File.Exists(installedHost) && SameCanonicalFile(parentImage, installedHost);
        if (!actualClrHost && Environment.ProcessPath is string helperHost
            && string.Equals(Path.GetFileNameWithoutExtension(helperHost), "dotnet", StringComparison.Ordinal))
        {
            actualClrHost = SameCanonicalFile(parentImage, selfImage);
        }

        if (!actualClrHost || string.IsNullOrEmpty(cli.Location))
        {
            return false;
        }

        string? entryPath = ReadManagedEntryPath(parentId);
        if (entryPath is null)
        {
            return false;
        }

        byte[] cliHash = HashFile(cli.Location);
        var inspected = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in File.ReadLines($"/proc/{parentId}/maps"))
        {
            ReadOnlySpan<char> fields = line.AsSpan();
            for (int field = 0; field < 5; field++)
            {
                ReadField(ref fields);
            }

            string mappedPath = fields.Trim().ToString();
            if (!string.Equals(Path.GetFileName(mappedPath), Path.GetFileName(cli.Location), StringComparison.Ordinal)
                || !inspected.Add(mappedPath) || !File.Exists(mappedPath)
                || !CryptographicOperations.FixedTimeEquals(cliHash, HashFile(mappedPath)))
            {
                continue;
            }

            if (SameCanonicalFile(entryPath, mappedPath))
            {
                return true;
            }

            string trustedTesthost = Path.Combine(Path.GetDirectoryName(cli.Location)!, "testhost.dll");
            string peerTesthost = Path.Combine(Path.GetDirectoryName(mappedPath)!, "testhost.dll");
            if (File.Exists(trustedTesthost) && SameCanonicalFile(entryPath, peerTesthost)
                && CryptographicOperations.FixedTimeEquals(HashFile(entryPath), HashFile(trustedTesthost)))
            {
                return true;
            }
        }

        return false;
    }

    private static string? ReadManagedEntryPath(int processId)
    {
        string[] arguments = Encoding.UTF8.GetString(File.ReadAllBytes($"/proc/{processId}/cmdline"))
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        int index = arguments.Length > 1 && string.Equals(arguments[1], "exec", StringComparison.Ordinal) ? 2 : 1;
        while (index < arguments.Length && arguments[index].StartsWith("--", StringComparison.Ordinal))
        {
            switch (arguments[index])
            {
                case "--runtimeconfig":
                case "--depsfile":
                case "--additionalprobingpath":
                case "--additional-deps":
                case "--fx-version":
                case "--roll-forward":
                    index += 2;
                    break;
                default:
                    return null;
            }
        }

        if (index >= arguments.Length || !arguments[index].EndsWith(".dll", StringComparison.Ordinal))
        {
            return null;
        }

        string? workingDirectory = File.ResolveLinkTarget($"/proc/{processId}/cwd", returnFinalTarget: true)?.FullName;
        return workingDirectory is null ? null : Path.GetFullPath(arguments[index], workingDirectory);
    }

    private static bool SameCanonicalFile(string left, string right)
    {
        string leftPath = File.ResolveLinkTarget(left, returnFinalTarget: true)?.FullName ?? Path.GetFullPath(left);
        string rightPath = File.ResolveLinkTarget(right, returnFinalTarget: true)?.FullName ?? Path.GetFullPath(right);
        return string.Equals(leftPath, rightPath, StringComparison.Ordinal);
    }

    private static byte[] HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return SHA256.HashData(stream);
    }

    private static async Task WriteInvocationAsync(
        Stream pipe,
        ExternalProcessInvocation invocation,
        Task authenticationTask,
        CancellationToken cancellationToken)
    {
        try
        {
            await authenticationTask.ConfigureAwait(false);
            NativeDependencyClosure? native = RequireTrustedNativeClosure(invocation);
            ExternalProcessSupervisorLaunch? launch = invocation.TrustedSupervisorLaunch;
            if (native is not null)
            {
                RevalidateTrustedInvocation(invocation, native, launch!);
            }
            using var boundedPipe = native is null ? null : new BoundedInvocationStream(pipe);
            await JsonSerializer.SerializeAsync(
                boundedPipe ?? pipe,
                new SupervisorInvocation(SupervisorProtocol, invocation.ExecutablePath, invocation.Arguments,
                    invocation.WorkingDirectory, invocation.EnvironmentUpdates, launch is not null,
                    launch is not null
                        ? new SupervisorTrust(launch.ProcessGroupLauncher, launch.HostArguments, launch.AssemblyPath,
                            launch.RuntimeDirectory, launch.RuntimeRoot, launch.HostTargetPath)
                        : null,
                    native?.ToBinding()),
                native is null ? null : InvocationJsonOptions, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            pipe.Dispose();
        }
    }

    /// <summary>Reads the authenticated frozen graph and separately bound production-launch marker.</summary>
    internal static async Task<ExternalProcessInvocation> ReadInvocationAsync(
        Stream pipe,
        string? trustedProcessGroupLauncher,
        CancellationToken cancellationToken = default)
    {
        SupervisorInvocation? request;
        try
        {
            using var boundedPipe = trustedProcessGroupLauncher is null ? null : new BoundedInvocationStream(pipe);
            request = await JsonSerializer.DeserializeAsync<SupervisorInvocation>(
                boundedPipe ?? pipe, trustedProcessGroupLauncher is null ? null : InvocationJsonOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception) when (trustedProcessGroupLauncher is not null)
        {
            throw NativeElfReader.Failure();
        }
        if (request is null || !string.Equals(request.Protocol, SupervisorProtocol, StringComparison.Ordinal)
            || request.RequiresTrustedSupervisor != (trustedProcessGroupLauncher is not null)
            || request.RequiresTrustedSupervisor != (request.TrustedSupervisorLaunch is not null)
            || request.RequiresTrustedSupervisor != (request.TrustedNativeClosure is not null))
        {
            if (trustedProcessGroupLauncher is not null || request?.RequiresTrustedSupervisor == true
                || request?.TrustedSupervisorLaunch is not null || request?.TrustedNativeClosure is not null)
            {
                throw NativeElfReader.Failure();
            }
            throw new IOException("The private process protocol is unavailable.");
        }

        ExternalProcessSupervisorLaunch? launch = null;
        NativeDependencyClosure? native = null;
        if (request.TrustedSupervisorLaunch is SupervisorTrust trust)
        {
            try
            {
                if (!string.Equals(trust.ProcessGroupLauncher, trustedProcessGroupLauncher, StringComparison.Ordinal))
                {
                    throw NativeElfReader.Failure();
                }
                // Reconstruct the original manifest first. Preparing a new graph here would silently
                // bless a changed library, interpreter, module root, or configuration.
                native = NativeDependencyClosure.FromBinding(request.TrustedNativeClosure!);
                launch = ExternalProcessSupervisorLaunch.Prepare(
                    trust.ProcessGroupLauncher, trust.HostArguments, trust.AssemblyPath, trust.RuntimeDirectory);
                if (!string.Equals(launch.RuntimeRoot, trust.RuntimeRoot, StringComparison.Ordinal)
                    || !string.Equals(launch.HostTargetPath, trust.HostTargetPath, StringComparison.Ordinal))
                {
                    throw NativeElfReader.Failure();
                }
                native.ValidateInputs(launch.NativeInputs);
                native.ValidateExecutable(launch.ProcessGroupLauncher);
                native.ValidateExecutable(launch.HostArguments[0]);
                native.ValidateExecutable(request.ExecutablePath);
            }
            catch (Exception)
            {
                throw NativeElfReader.Failure();
            }
        }

        try
        {
            return new ExternalProcessInvocation(
                request.ExecutablePath, request.Arguments, request.WorkingDirectory, request.EnvironmentUpdates)
            {
                TrustedSupervisorLaunch = launch,
                TrustedNativeClosure = native,
            };
        }
        catch (Exception) when (native is not null)
        {
            throw NativeElfReader.Failure();
        }
    }

    private static async Task<SupervisorOutcome> ReadOutcomeAsync(Stream pipe, CancellationToken cancellationToken)
    {
        SupervisorOutcome? outcome = await JsonSerializer.DeserializeAsync<SupervisorOutcome>(pipe, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (outcome is null || (outcome.FailureKind.HasValue
            && (!Enum.IsDefined(outcome.FailureKind.Value) || outcome.FailureKind == ExternalProcessFailureKind.ProcessExited)))
        {
            throw new IOException("The private process result is unavailable.");
        }
        if (outcome.NativeClosureUnavailable && (!outcome.ScopeReaped || outcome.Cancelled
            || outcome.ExitCode.HasValue || outcome.FailureKind != ExternalProcessFailureKind.LaunchFailed))
        {
            throw new IOException("The private process result is unavailable.");
        }

        return outcome;
    }

    private static void EnsureSuccessfulSupervision(SupervisorOutcome outcome)
    {
        if (outcome.NativeClosureUnavailable)
        {
            throw NativeElfReader.Failure();
        }
        if (!outcome.ScopeReaped || outcome.Cancelled || (!outcome.FailureKind.HasValue && !outcome.ExitCode.HasValue))
        {
            throw new ExternalProcessFailureException(new ExternalProcessFailure(ExternalProcessFailureKind.ExecutionFailed));
        }

        if (outcome.FailureKind.HasValue)
        {
            throw new ExternalProcessFailureException(new ExternalProcessFailure(outcome.FailureKind.Value));
        }
    }

    private static async Task<SupervisorOutcome> CancelSupervisorAsync(
        SupervisorProcess supervisor,
        Task<SupervisorOutcome>? outcomeTask)
    {
        if (!supervisor.Process.HasExited)
        {
            try
            {
                await supervisor.ControlPipe.WriteAsync(CancellationMessage)
                    .AsTask().WaitAsync(StreamCompletionTimeout).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The helper may already have closed control after reaping. Its result is still required.
            }
            catch (ObjectDisposedException)
            {
                // A completed helper can race the request. Do not kill it or bypass its reap acknowledgement.
            }
        }

        await supervisor.Process.WaitForExitAsync(CancellationToken.None)
            .WaitAsync(SupervisorResponseTimeout).ConfigureAwait(false);
        if (outcomeTask is null)
        {
            throw new IOException("The private process result is unavailable.");
        }

        return await outcomeTask.WaitAsync(StreamCompletionTimeout).ConfigureAwait(false);
    }

    private static async Task ListenForCancellationAsync(
        Stream pipe,
        CancellationTokenSource cancellation,
        CancellationToken cancellationToken)
    {
        try
        {
            byte[] command = new byte[1];
            // EOF also means the parent disappeared. The supervisor still owns cleanup before it exits.
            await pipe.ReadAsync(command, cancellationToken).ConfigureAwait(false);
            cancellation.Cancel();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
            cancellation.Cancel();
        }
    }

    private static bool TryReadDescriptor(string text, out int descriptor)
    {
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out descriptor) && descriptor > 2;
    }

    private static void MakePrivateDescriptor(int descriptor)
    {
        const int getDescriptorFlags = 1;
        const int setDescriptorFlags = 2;
        const int closeOnExec = 1;
        int flags = ControlDescriptor(descriptor, getDescriptorFlags, 0);
        if (flags < 0 || ControlDescriptor(descriptor, setDescriptorFlags, flags | closeOnExec) < 0)
        {
            throw new IOException("The private process protocol is unavailable.");
        }
    }

    private static void EnsureLinuxExecution()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("External process-tree containment requires Linux process groups and a child subreaper.");
        }
    }

    private static void EnsureExecutableCanStart(ExternalProcessInvocation invocation)
    {
        NativeDependencyClosure? native = RequireTrustedNativeClosure(invocation);
        if (native is not null)
        {
            native.ValidateInputs(invocation.TrustedSupervisorLaunch!.NativeInputs);
            native.ValidateExecutable(invocation.ExecutablePath);
            return;
        }

        bool foundWithoutExecuteAccess = false;
        foreach (string candidate in EnumerateExecutableCandidates(invocation))
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            if (HasExecuteAccess(candidate))
            {
                return;
            }

            foundWithoutExecuteAccess = true;
        }

        if (foundWithoutExecuteAccess)
        {
            throw new UnauthorizedAccessException("The configured external executable is not executable.");
        }

        throw new FileNotFoundException("The configured external executable was not found.");
    }

    private static IEnumerable<string> EnumerateExecutableCandidates(ExternalProcessInvocation invocation)
    {
        string executablePath = invocation.ExecutablePath;
        if (executablePath.IndexOf(Path.DirectorySeparatorChar) >= 0)
        {
            yield return GetPathRelativeToWorkingDirectory(executablePath, invocation.WorkingDirectory);
            yield break;
        }

        string path = GetChildPathEnvironment(invocation) ?? "/bin:/usr/bin";
        foreach (string entry in path.Split(Path.PathSeparator, StringSplitOptions.None))
        {
            string directory = entry.Length == 0
                ? invocation.WorkingDirectory
                : GetPathRelativeToWorkingDirectory(entry, invocation.WorkingDirectory);
            yield return Path.Combine(directory, executablePath);
        }
    }

    private static string GetPathRelativeToWorkingDirectory(string path, string workingDirectory)
    {
        return Path.IsPathFullyQualified(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(workingDirectory, path));
    }

    private static string? GetChildPathEnvironment(ExternalProcessInvocation invocation)
    {
        return invocation.EnvironmentUpdates.TryGetValue("PATH", out string? updatedPath)
            ? updatedPath
            : Environment.GetEnvironmentVariable("PATH");
    }

    private static bool HasExecuteAccess(string path)
    {
        if (CheckAccess(path, ExecuteAccessMode) == 0)
        {
            return true;
        }

        int error = Marshal.GetLastPInvokeError();
        if (error is NoSuchFileOrDirectoryError or AccessDeniedError)
        {
            return false;
        }

        throw new IOException("Unable to inspect an external executable.");
    }

    private static string GetProcessGroupLauncher()
    {
        foreach (string candidate in ProcessGroupLaunchers)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("The Linux process-group launcher was not found.");
    }

    private static async Task<CapturedProcessStream> CaptureAsync(Stream source, int maximumCapturedBytes, CancellationToken cancellationToken)
    {
        byte[] captureBuffer = ArrayPool<byte>.Shared.Rent(maximumCapturedBytes);
        byte[] readBuffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        var capturedByteCount = 0;
        var truncated = false;
        try
        {
            while (true)
            {
                int read = await source.ReadAsync(readBuffer.AsMemory(0, ReadBufferSize), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                int bytesToRetain = Math.Min(read, maximumCapturedBytes - capturedByteCount);
                if (bytesToRetain > 0)
                {
                    readBuffer.AsSpan(0, bytesToRetain).CopyTo(captureBuffer.AsSpan(capturedByteCount));
                    capturedByteCount += bytesToRetain;
                }

                truncated |= bytesToRetain != read;
            }

            return new CapturedProcessStream(Encoding.UTF8.GetString(captureBuffer, 0, capturedByteCount), truncated, capturedByteCount);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(captureBuffer, clearArray: true);
            ArrayPool<byte>.Shared.Return(readBuffer, clearArray: true);
        }
    }

    private static async Task WriteStandardInputAsync(StreamWriter standardInput, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        try
        {
            await standardInput.BaseStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await standardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            standardInput.Dispose();
        }
    }

    private static void CloseRedirectedStreams(Process process, bool hasStandardInput, CancellationTokenSource streamLifetime)
    {
        streamLifetime.Cancel();
        CloseStream(process.StandardOutput.BaseStream);
        CloseStream(process.StandardError.BaseStream);
        if (hasStandardInput)
        {
            CloseStream(process.StandardInput.BaseStream);
        }
    }

    private static void CloseStream(Stream stream)
    {
        try
        {
            stream.Dispose();
        }
        catch (IOException)
        {
            // Closing diagnostics must not bypass process cleanup or its acknowledgement.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task ObserveTasksAsync(params Task?[] tasks)
    {
        Task completion = Task.WhenAll(tasks.OfType<Task>());
        try
        {
            await completion.WaitAsync(StreamCompletionTimeout).ConfigureAwait(false);
        }
        catch (Exception)
        {
            _ = completion.ContinueWith(
                static completed => { _ = completed.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private sealed class SupervisorProcess : IDisposable
    {
        internal SupervisorProcess(Process process, AnonymousPipeServerStream invocationPipe,
            AnonymousPipeServerStream controlPipe, AnonymousPipeServerStream resultPipe, Socket authenticationSocket,
            byte[] authenticationHeader, string processGroupLauncher)
        {
            Process = process;
            InvocationPipe = invocationPipe;
            ControlPipe = controlPipe;
            ResultPipe = resultPipe;
            AuthenticationSocket = authenticationSocket;
            AuthenticationHeader = authenticationHeader;
            ProcessGroupLauncher = processGroupLauncher;
        }

        internal Process Process { get; }
        internal AnonymousPipeServerStream InvocationPipe { get; }
        internal AnonymousPipeServerStream ControlPipe { get; }
        internal AnonymousPipeServerStream ResultPipe { get; }
        internal Socket AuthenticationSocket { get; }
        internal byte[] AuthenticationHeader { get; }
        internal string ProcessGroupLauncher { get; }

        internal void ClosePipes()
        {
            CloseStream(InvocationPipe);
            CloseStream(ControlPipe);
            CloseStream(ResultPipe);
            AuthenticationSocket.Dispose();
            CryptographicOperations.ZeroMemory(AuthenticationHeader);
        }

        public void Dispose()
        {
            ClosePipes();
            Process.Dispose();
        }
    }

    /// <summary>
    /// Exists only inside the private single-invocation supervisor. Every native child there belongs to
    /// this scope. The managed launcher is reaped by Process; adopted descendants are reaped by PID.
    /// After the launcher is reaped, waitpid(-1) is safe here and provides an authoritative ECHILD boundary.
    /// No process-wide subreaper or wildcard wait is ever installed in the caller/testhost.
    /// </summary>
    private sealed class LinuxProcessTree
    {
        private const int WaitWithoutBlocking = 1;
        private const int InterruptedSystemCallError = 4;
        private const int NoChildProcessError = 10;
        private readonly Process _launcher;
        private readonly int _launcherId;
        private readonly int _supervisorGroupId = GetCurrentProcessGroup();
        private readonly Dictionary<int, LinuxProcessInfo> _members = new();
        private readonly Dictionary<int, LinuxProcessInfo> _snapshot = new();
        private readonly Queue<LinuxProcessInfo> _pending = new();
        private readonly HashSet<(int ProcessId, ulong StartTime)> _visited = new();
        private readonly HashSet<int> _signaledGroups = new();
        private readonly List<int> _reaped = new();

        internal LinuxProcessTree(Process launcher)
        {
            _launcher = launcher;
            _launcherId = launcher.Id;
        }

        internal async Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                RefreshAndReap(cleanupStarted: null, cancellationToken);
                if (HasExited(cleanupStarted: null, cancellationToken))
                {
                    await _launcher.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                await Task.Delay(ProcessGroupPollInterval, cancellationToken).ConfigureAwait(false);
            }
        }

        internal async Task TerminateAndReapAsync(bool gracefully)
        {
            long cleanupStarted = Stopwatch.GetTimestamp();
            RefreshAndReap(cleanupStarted);
            if (gracefully && !HasExited(cleanupStarted))
            {
                var signaled = new HashSet<(int ProcessId, ulong StartTime)>();
                SignalMembers(SignalTerminate, signaled, signalGroups: true, cleanupStarted);
                while (!HasExited(cleanupStarted))
                {
                    TimeSpan remaining = GracefulTerminationTimeout - Stopwatch.GetElapsedTime(cleanupStarted);
                    if (remaining <= TimeSpan.Zero)
                    {
                        break;
                    }

                    await DelayWithinCleanupAsync(remaining, cleanupStarted).ConfigureAwait(false);
                    RefreshAndReap(cleanupStarted);
                    SignalMembers(SignalTerminate, signaled, signalGroups: false, cleanupStarted);
                }
            }

            while (!HasExited(cleanupStarted))
            {
                SignalMembers(SignalKill, signaled: null, signalGroups: true, cleanupStarted);
                await DelayWithinCleanupAsync(CleanupTimeout - Stopwatch.GetElapsedTime(cleanupStarted), cleanupStarted)
                    .ConfigureAwait(false);
                RefreshAndReap(cleanupStarted);
            }

            CheckCleanupDeadline(cleanupStarted);
            await _launcher.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(CleanupTimeout - Stopwatch.GetElapsedTime(cleanupStarted)).ConfigureAwait(false);
            CheckCleanupDeadline(cleanupStarted);
        }

        private static async Task DelayWithinCleanupAsync(TimeSpan remaining, long cleanupStarted)
        {
            CheckCleanupDeadline(cleanupStarted);
            if (remaining <= TimeSpan.Zero)
            {
                throw new IOException("The external process tree could not be terminated and reaped.");
            }

            await Task.Delay(remaining < ProcessGroupPollInterval ? remaining : ProcessGroupPollInterval).ConfigureAwait(false);
            CheckCleanupDeadline(cleanupStarted);
        }

        private bool HasExited(long? cleanupStarted, CancellationToken cancellationToken = default)
        {
            CheckCleanupDeadline(cleanupStarted, cancellationToken);
            bool launcherExited = _launcher.HasExited;
            CheckCleanupDeadline(cleanupStarted, cancellationToken);
            if (!launcherExited)
            {
                return false;
            }

            // The managed root is already reaped; every other native child belongs to this private helper.
            while (true)
            {
                CheckCleanupDeadline(cleanupStarted, cancellationToken);
                int waited = WaitForProcess(-1, out _, WaitWithoutBlocking);
                CheckCleanupDeadline(cleanupStarted, cancellationToken);
                if (waited > 0)
                {
                    _members.Remove(waited);
                    continue;
                }

                if (waited == 0)
                {
                    return false;
                }

                int error = Marshal.GetLastPInvokeError();
                if (error == NoChildProcessError)
                {
                    _members.Clear();
                    return true;
                }

                if (error != InterruptedSystemCallError)
                {
                    throw new IOException("Unable to reap an external descendant.");
                }
            }
        }

        private void RefreshAndReap(long? cleanupStarted, CancellationToken cancellationToken = default)
        {
            CheckCleanupDeadline(cleanupStarted, cancellationToken);
            _snapshot.Clear();
            _pending.Clear();
            _visited.Clear();
            if (!TryReadProcessInfo(Environment.ProcessId, out LinuxProcessInfo supervisor, cleanupStarted, cancellationToken))
            {
                throw new IOException("Unable to inspect the external process scope.");
            }

            QueueProcess(supervisor);
            foreach (LinuxProcessInfo member in _members.Values)
            {
                CheckCleanupDeadline(cleanupStarted, cancellationToken);
                if (TryReadProcessInfo(member.ProcessId, out LinuxProcessInfo current, cleanupStarted, cancellationToken)
                    && current.StartTime == member.StartTime)
                {
                    _snapshot[member.ProcessId] = current;
                    QueueProcess(current);
                }
            }

            while (_pending.TryDequeue(out LinuxProcessInfo parent))
            {
                CheckCleanupDeadline(cleanupStarted, cancellationToken);
                ReadChildren(parent, cleanupStarted, cancellationToken);
            }

            CheckCleanupDeadline(cleanupStarted, cancellationToken);
            bool launcherExited = _launcher.HasExited;
            CheckCleanupDeadline(cleanupStarted, cancellationToken);
            _members.Clear();
            foreach (LinuxProcessInfo info in _snapshot.Values)
            {
                CheckCleanupDeadline(cleanupStarted, cancellationToken);
                if (info.ProcessId != _launcherId || !launcherExited)
                {
                    _members.Add(info.ProcessId, info);
                }
            }

            _reaped.Clear();
            foreach (LinuxProcessInfo member in _members.Values)
            {
                CheckCleanupDeadline(cleanupStarted, cancellationToken);
                if (member.ProcessId == _launcherId
                    || !TryReadProcessInfo(member.ProcessId, out LinuxProcessInfo current, cleanupStarted, cancellationToken)
                    || current.StartTime != member.StartTime || current.ParentProcessId != Environment.ProcessId || current.State != 'Z')
                {
                    continue;
                }

                int waited;
                do
                {
                    CheckCleanupDeadline(cleanupStarted, cancellationToken);
                    waited = WaitForProcess(member.ProcessId, out _, WaitWithoutBlocking);
                    CheckCleanupDeadline(cleanupStarted, cancellationToken);
                }
                while (waited < 0 && Marshal.GetLastPInvokeError() == InterruptedSystemCallError);
                if (waited > 0)
                {
                    _reaped.Add(member.ProcessId);
                }
                else if (waited < 0 && Marshal.GetLastPInvokeError() != NoChildProcessError)
                {
                    throw new IOException("Unable to reap an external descendant.");
                }
            }

            foreach (int processId in _reaped)
            {
                CheckCleanupDeadline(cleanupStarted, cancellationToken);
                _members.Remove(processId);
            }
        }

        private void QueueProcess(LinuxProcessInfo process)
        {
            if (_visited.Add((process.ProcessId, process.StartTime)))
            {
                _pending.Enqueue(process);
            }
        }

        private void ReadChildren(LinuxProcessInfo parent, long? cleanupStarted, CancellationToken cancellationToken)
        {
            string processDirectory = $"/proc/{parent.ProcessId}";
            CheckCleanupDeadline(cleanupStarted, cancellationToken);
            if (!TryReadProcessInfo(parent.ProcessId, out LinuxProcessInfo currentParent, cleanupStarted, cancellationToken)
                || currentParent.StartTime != parent.StartTime)
            {
                return;
            }

            try
            {
                using IEnumerator<string> tasks = Directory.EnumerateDirectories(Path.Combine(processDirectory, "task")).GetEnumerator();
                while (true)
                {
                    CheckCleanupDeadline(cleanupStarted, cancellationToken);
                    bool hasTask = tasks.MoveNext();
                    CheckCleanupDeadline(cleanupStarted, cancellationToken);
                    if (!hasTask)
                    {
                        break;
                    }

                    if (!TryReadProcessInfo(parent.ProcessId, out currentParent, cleanupStarted, cancellationToken)
                        || currentParent.StartTime != parent.StartTime)
                    {
                        return;
                    }

                    string taskDirectory = tasks.Current;
                    string children;
                    try
                    {
                        CheckCleanupDeadline(cleanupStarted, cancellationToken);
                        children = File.ReadAllText(Path.Combine(taskDirectory, "children"));
                        CheckCleanupDeadline(cleanupStarted, cancellationToken);
                    }
                    catch (FileNotFoundException)
                    {
                        CheckCleanupDeadline(cleanupStarted, cancellationToken);
                        continue;
                    }
                    catch (DirectoryNotFoundException)
                    {
                        CheckCleanupDeadline(cleanupStarted, cancellationToken);
                        continue;
                    }
                    catch (IOException exception) when (IsVanishedProcEntry(exception, taskDirectory))
                    {
                        CheckCleanupDeadline(cleanupStarted, cancellationToken);
                        // A procfs descriptor remains bound to the sampled dead task. ESRCH means
                        // that task vanished, even if a later lookup of its numeric pathname succeeds.
                        continue;
                    }

                    ReadOnlySpan<char> fields = children.AsSpan().Trim();
                    while (!fields.IsEmpty)
                    {
                        CheckCleanupDeadline(cleanupStarted, cancellationToken);
                        if (!int.TryParse(ReadField(ref fields), NumberStyles.None, CultureInfo.InvariantCulture, out int childId))
                        {
                            throw new IOException("Unable to inspect an external descendant.");
                        }

                        if (TryReadProcessInfo(childId, out LinuxProcessInfo child, cleanupStarted, cancellationToken)
                            && TryReadProcessInfo(parent.ProcessId, out currentParent, cleanupStarted, cancellationToken)
                            && HasExpectedProcessParent(parent, currentParent, child))
                        {
                            _snapshot[childId] = child;
                            QueueProcess(child);
                        }
                    }
                }
            }
            catch (DirectoryNotFoundException)
            {
                CheckCleanupDeadline(cleanupStarted, cancellationToken);
                // Adoption is rediscovered from the supervisor, never inferred from a stale child-list PID.
            }
            catch (IOException exception) when (IsVanishedProcEntry(exception, processDirectory))
            {
                CheckCleanupDeadline(cleanupStarted, cancellationToken);
            }
        }

        private void SignalMembers(int signal, HashSet<(int ProcessId, ulong StartTime)>? signaled,
            bool signalGroups, long cleanupStarted)
        {
            CheckCleanupDeadline(cleanupStarted);
            _signaledGroups.Clear();
            if (signalGroups)
            {
                foreach (LinuxProcessInfo member in _members.Values)
                {
                    CheckCleanupDeadline(cleanupStarted);
                    if (!TryReadProcessInfo(member.ProcessId, out LinuxProcessInfo current, cleanupStarted)
                        || current.StartTime != member.StartTime || current.State == 'Z')
                    {
                        continue;
                    }

                    if (current.ProcessGroupId > 0 && current.ProcessGroupId != _supervisorGroupId
                        && _signaledGroups.Add(current.ProcessGroupId))
                    {
                        Signal(-current.ProcessGroupId, signal, cleanupStarted);
                    }
                }
            }

            foreach (LinuxProcessInfo member in _members.Values)
            {
                CheckCleanupDeadline(cleanupStarted);
                if ((signaled is not null && !signaled.Add((member.ProcessId, member.StartTime)))
                    || !TryReadProcessInfo(member.ProcessId, out LinuxProcessInfo current, cleanupStarted)
                    || current.StartTime != member.StartTime || current.State == 'Z'
                    || _signaledGroups.Contains(current.ProcessGroupId))
                {
                    continue;
                }

                Signal(current.ProcessId, signal, cleanupStarted);
            }
        }

        private static void Signal(int processOrGroupId, int signal, long cleanupStarted)
        {
            CheckCleanupDeadline(cleanupStarted);
            if (SendSignal(processOrGroupId, signal) != 0 && Marshal.GetLastPInvokeError() != NoSuchProcessError)
            {
                throw new IOException("Unable to signal the external process tree.");
            }

            CheckCleanupDeadline(cleanupStarted);
        }
    }

    private static void CheckCleanupDeadline(long? cleanupStarted, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (cleanupStarted.HasValue && Stopwatch.GetElapsedTime(cleanupStarted.Value) >= CleanupTimeout)
        {
            throw new IOException("The external process tree could not be terminated and reaped.");
        }
    }

    private static bool HasExpectedProcessParent(LinuxProcessInfo sampledParent, LinuxProcessInfo currentParent, LinuxProcessInfo child)
    {
        return sampledParent.ProcessId == currentParent.ProcessId && sampledParent.StartTime == currentParent.StartTime
            && child.ParentProcessId == currentParent.ProcessId;
    }

    private static bool IsVanishedProcEntry(IOException exception, string directory) =>
        exception.HResult == NoSuchProcessError || !Directory.Exists(directory);

    private static bool TryReadProcessInfo(int processId, out LinuxProcessInfo info,
        long? cleanupStarted = null, CancellationToken cancellationToken = default)
    {
        CheckCleanupDeadline(cleanupStarted, cancellationToken);
        info = default;
        string directory = $"/proc/{processId}";
        string stat;
        try
        {
            stat = File.ReadAllText(Path.Combine(directory, "stat"));
            CheckCleanupDeadline(cleanupStarted, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            CheckCleanupDeadline(cleanupStarted, cancellationToken);
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            CheckCleanupDeadline(cleanupStarted, cancellationToken);
            return false;
        }
        catch (IOException exception) when (IsVanishedProcEntry(exception, directory))
        {
            CheckCleanupDeadline(cleanupStarted, cancellationToken);
            return false;
        }

        return TryParseProcessInfo(processId, stat, out info, cleanupStarted, cancellationToken);
    }

    private static bool TryParseProcessInfo(int processId, string stat, out LinuxProcessInfo info,
        long? cleanupStarted = null, CancellationToken cancellationToken = default)
    {
        info = default;
        int nameEnd = stat.LastIndexOf(')');
        if (nameEnd < 0)
        {
            throw new IOException("Unable to inspect an external process identity.");
        }

        ReadOnlySpan<char> fields = stat.AsSpan(nameEnd + 1).TrimStart();
        ReadOnlySpan<char> state = ReadField(ref fields);
        if (state.Length != 1
            || !int.TryParse(ReadField(ref fields), NumberStyles.None, CultureInfo.InvariantCulture, out int parentId)
            || !int.TryParse(ReadField(ref fields), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int groupId)
            || groupId < -1 || (groupId == -1 && parentId != 0))
        {
            throw new IOException("Unable to inspect an external process identity.");
        }

        for (int field = 6; field < 22; field++)
        {
            CheckCleanupDeadline(cleanupStarted, cancellationToken);
            ReadField(ref fields);
        }

        if (!ulong.TryParse(ReadField(ref fields), NumberStyles.None, CultureInfo.InvariantCulture, out ulong startTime))
        {
            throw new IOException("Unable to inspect an external process identity.");
        }

        // Linux samples state before locking sighand. If the task exits in between,
        // it emits ppid=0 and pgrp=-1, not a malformed record. Do not use that
        // sample for discovery or signaling; rescan and waitpid's ECHILD boundary
        // must still prove that the private process scope has exited.
        if (groupId == -1)
        {
            return false;
        }

        info = new LinuxProcessInfo(processId, parentId, groupId, startTime, state[0]);
        return true;
    }

    private static ReadOnlySpan<char> ReadField(ref ReadOnlySpan<char> fields)
    {
        int separator = fields.IndexOf(' ');
        ReadOnlySpan<char> value = separator < 0 ? fields : fields[..separator];
        fields = separator < 0 ? [] : fields[(separator + 1)..].TrimStart();
        return value;
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SendSignal(int processOrGroupId, int signal);

    [DllImport("libc", EntryPoint = "access", SetLastError = true)]
    private static extern int CheckAccess(string path, int mode);

    [DllImport("libc", EntryPoint = "prctl", SetLastError = true)]
    private static extern int SetProcessControl(int option, nuint value, nuint argument3, nuint argument4, nuint argument5);

    [DllImport("libc", EntryPoint = "waitpid", SetLastError = true)]
    private static extern int WaitForProcess(int processId, out int status, int options);

    [DllImport("libc", EntryPoint = "getpgrp")]
    private static extern int GetCurrentProcessGroup();

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int ControlDescriptor(int descriptor, int command, int value);

    [DllImport("libc", EntryPoint = "socketpair", SetLastError = true)]
    private static extern int CreateSocketPair(int domain, int type, int protocol, out SocketPairDescriptors descriptors);

    [DllImport("libc", EntryPoint = "getsockopt", SetLastError = true)]
    private static extern int GetSocketPeerCredentials(int descriptor, int level, int option, out SocketPeerCredentials credentials, ref uint length);

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    [DllImport("libc", EntryPoint = "umask")]
    private static extern uint SetFileCreationMask(uint mask);

    [StructLayout(LayoutKind.Sequential)]
    private struct SocketPairDescriptors
    {
        internal int Server;
        internal int Client;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SocketPeerCredentials
    {
        internal int ProcessId;
        internal uint UserId;
        internal uint GroupId;
    }

    /// <summary>Bounds the authenticated JSON without buffering an entire native manifest.</summary>
    private sealed class BoundedInvocationStream(Stream inner) : Stream
    {
        private int _bytes;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int count = inner.Read(buffer);
            Consume(count);
            return count;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int count = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Consume(count);
            return count;
        }

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Consume(buffer.Length);
            inner.Write(buffer);
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Consume(buffer.Length);
            return inner.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private void Consume(int count)
        {
            if (count > MaximumSupervisorInvocationBytes - _bytes)
            {
                throw NativeElfReader.Failure();
            }
            _bytes += count;
        }
    }

    private sealed record SupervisorInvocation(string Protocol, string ExecutablePath, IReadOnlyList<string> Arguments,
        string WorkingDirectory, IReadOnlyDictionary<string, string?> EnvironmentUpdates,
        bool RequiresTrustedSupervisor = false, SupervisorTrust? TrustedSupervisorLaunch = null,
        NativeDependencyClosureBinding? TrustedNativeClosure = null);

    private sealed record SupervisorTrust(string ProcessGroupLauncher, IReadOnlyList<string> HostArguments,
        string AssemblyPath, string RuntimeDirectory, string RuntimeRoot, string HostTargetPath);

    private sealed record SupervisorOutcome(ExternalProcessFailureKind? FailureKind, int? ExitCode, bool Cancelled,
        bool ScopeReaped, bool NativeClosureUnavailable = false);

    private readonly record struct LinuxProcessInfo(int ProcessId, int ParentProcessId, int ProcessGroupId, ulong StartTime, char State);

    private readonly record struct CapturedProcessStream(string Text, bool Truncated, int ByteCount);
}
