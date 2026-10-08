using System.Runtime.Versioning;
using System.Text;
using JRunner.Cli.Infrastructure;
using Xunit;
using ProtectedSupervisorDeployment = JRunner.Cli.Tests.ExternalProcessSupervisorLaunchTests.ProtectedRunnableSupervisorDeployment;

namespace JRunner.Cli.Tests;

public sealed class ExternalProcessInvocationTests
{
    [Fact]
    public void Unmarked_invocation_retains_synthetic_executable_and_no_supervisor_trust_requirement()
    {
        var invocation = new ExternalProcessInvocation("synthetic-tool", ["synthetic-argument"], "synthetic-directory");

        Assert.Null(invocation.TrustedSupervisorLaunch);
        Assert.Null(invocation.TrustedNativeClosure);
        Assert.Equal("synthetic-tool", invocation.ExecutablePath);
        Assert.Equal("synthetic-directory", invocation.WorkingDirectory);
    }

    [SupportedOSPlatform("linux")]
    [Fact]
    public void Marked_invocation_carries_the_same_immutable_prepared_supervisor_chain()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var deployment = new ProtectedSupervisorDeployment();
        ExternalProcessSupervisorLaunch launch = deployment.Prepare();
        NativeDependencyClosure native = deployment.PrepareNativeClosure(launch, "/usr/bin/printf");
        var invocation = new ExternalProcessInvocation("/usr/bin/printf", ["closed-token"], deployment.Root)
        {
            TrustedSupervisorLaunch = launch,
            TrustedNativeClosure = native,
        };

        Assert.Same(launch, invocation.TrustedSupervisorLaunch);
        Assert.Same(native, invocation.TrustedNativeClosure);
        ExternalProcessSupervisorLaunch markedLaunch = Assert.IsType<ExternalProcessSupervisorLaunch>(invocation.TrustedSupervisorLaunch);
        Assert.Equal(deployment.LauncherPath, markedLaunch.ProcessGroupLauncher);
        Assert.Equal([deployment.HostPath], markedLaunch.HostArguments);
        Assert.Equal("/usr/bin/printf", invocation.ExecutablePath);
        Assert.Equal(["closed-token"], invocation.Arguments);
    }

    [Fact]
    public void Constructor_snapshots_argument_tokens_and_environment_updates()
    {
        var arguments = new List<string> { "first" };
        var environmentUpdates = new List<KeyValuePair<string, string?>>
        {
            new("JRUNNER_TEST_VALUE", "original"),
            new("JRUNNER_TEST_REMOVE", null),
        };

        var invocation = new ExternalProcessInvocation(
            "/usr/bin/printf",
            arguments,
            "/tmp",
            environmentUpdates);

        arguments[0] = "changed";
        arguments.Add("second");
        environmentUpdates[0] = new KeyValuePair<string, string?>("JRUNNER_TEST_VALUE", "changed");
        environmentUpdates.Add(new KeyValuePair<string, string?>("JRUNNER_TEST_ADDED", "value"));

        Assert.Equal(["first"], invocation.Arguments);
        Assert.Equal("original", invocation.EnvironmentUpdates["JRUNNER_TEST_VALUE"]);
        Assert.Null(invocation.EnvironmentUpdates["JRUNNER_TEST_REMOVE"]);
        Assert.False(invocation.EnvironmentUpdates.ContainsKey("JRUNNER_TEST_ADDED"));

        var immutableArguments = Assert.IsAssignableFrom<IList<string>>(invocation.Arguments);
        Assert.Throws<NotSupportedException>(() => immutableArguments[0] = "replacement");

        var immutableEnvironment = Assert.IsAssignableFrom<IDictionary<string, string?>>(invocation.EnvironmentUpdates);
        Assert.Throws<NotSupportedException>(() => immutableEnvironment["JRUNNER_TEST_VALUE"] = "replacement");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_rejects_an_empty_executable_path(string executablePath)
    {
        Assert.Throws<ArgumentException>(
            () => new ExternalProcessInvocation(executablePath, Array.Empty<string>(), "/tmp"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_rejects_an_empty_working_directory(string workingDirectory)
    {
        Assert.Throws<ArgumentException>(
            () => new ExternalProcessInvocation("/usr/bin/printf", Array.Empty<string>(), workingDirectory));
    }

    [Fact]
    public void Constructor_rejects_an_empty_argument_token()
    {
        Assert.Throws<ArgumentException>(
            () => new ExternalProcessInvocation("/usr/bin/printf", [string.Empty], "/tmp"));
    }

    [Fact]
    public void Constructor_snapshots_bounded_standard_input_bytes()
    {
        byte[] standardInput = "enter\n"u8.ToArray();
        var invocation = new ExternalProcessInvocation(
            "/usr/bin/printf",
            Array.Empty<string>(),
            "/tmp",
            standardInput: standardInput);

        standardInput[0] = (byte)'x';

        Assert.True(invocation.HasStandardInput);
        Assert.Equal("enter\n", Encoding.UTF8.GetString(invocation.StandardInputBytes.Span));
    }

    [Fact]
    public void Constructor_encodes_bounded_standard_input_text()
    {
        var invocation = new ExternalProcessInvocation(
            "/usr/bin/printf",
            Array.Empty<string>(),
            "/tmp",
            standardInput: "enter\n");

        Assert.True(invocation.HasStandardInput);
        Assert.Equal("enter\n", Encoding.UTF8.GetString(invocation.StandardInputBytes.Span));
    }

    [Fact]
    public void Constructor_rejects_an_oversized_standard_input_payload()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ExternalProcessInvocation(
                "/usr/bin/printf",
                Array.Empty<string>(),
                "/tmp",
                standardInput: new byte[ExternalProcessInvocation.MaximumStandardInputBytes + 1]));
    }

    [Fact]
    public void Constructor_rejects_invalid_environment_variable_names()
    {
        Assert.Throws<ArgumentException>(
            () => new ExternalProcessInvocation(
                "/usr/bin/printf",
                Array.Empty<string>(),
                "/tmp",
                [new KeyValuePair<string, string?>("invalid=name", "value")]));
    }
}
