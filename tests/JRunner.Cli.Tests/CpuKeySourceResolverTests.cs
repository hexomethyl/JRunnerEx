using System.Runtime.InteropServices;
using System.Security.Cryptography;
using JRunner.Cli;
using JRunner.Cli.Infrastructure;
using JRunner.Core.Contracts;
using JRunner.Core.Nand.Security;
using Xunit;

namespace JRunner.Cli.Tests;

public sealed class CpuKeySourceResolverTests
{
    private const string CpuKeyText = "00112233445566778899AABBCCDDEEFF";

    [Fact]
    public async Task Required_policy_rejects_an_absent_source()
    {
        var resolver = new CpuKeySourceResolver(new FakeEnvironment(), new FakeTerminal(isTerminal: false));
        using var standardInput = new StringReader(string.Empty);

        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
            () => resolver.ResolveAsync(
                new CpuKeySourceSelection(null, null, readFromStandardInput: false),
                CpuKeySourcePolicy.Required,
                standardInput).AsTask());

        Assert.Equal(ExitCode.Usage, exception.Code);
        Assert.Equal("cpu-key-source-required", exception.Kind);
    }

    [Fact]
    public async Task Optional_policy_returns_no_key_when_no_source_is_selected()
    {
        var environment = new FakeEnvironment([("UNSELECTED_CPU_KEY", CpuKeyText)]);
        var resolver = new CpuKeySourceResolver(environment, new FakeTerminal(isTerminal: false));
        using var standardInput = new StringReader(string.Empty);

        CpuKey? result = await resolver.ResolveAsync(
            new CpuKeySourceSelection(null, null, readFromStandardInput: false),
            CpuKeySourcePolicy.Optional,
            standardInput);

        Assert.Null(result);
        Assert.Equal(0, environment.ReadCount);
        Assert.Empty(environment.RemovedNames);
        Assert.Equal(CpuKeyText, environment.Values["UNSELECTED_CPU_KEY"]);
    }

    [Fact]
    public async Task Multiple_sources_are_rejected_before_any_source_is_read()
    {
        var environment = new FakeEnvironment([("CPU_KEY", CpuKeyText)]);
        var terminal = new FakeTerminal(isTerminal: true);
        var resolver = new CpuKeySourceResolver(environment, terminal);
        using var standardInput = new ThrowingReader(new IOException(CpuKeyText));

        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
            () => resolver.ResolveAsync(
                new CpuKeySourceSelection(" ", "CPU_KEY", readFromStandardInput: true),
                CpuKeySourcePolicy.Required,
                standardInput).AsTask());

        Assert.Equal(ExitCode.Usage, exception.Code);
        Assert.Equal("cpu-key-source-conflict", exception.Kind);
        Assert.Equal(0, environment.ReadCount);
        Assert.Empty(environment.RemovedNames);
        Assert.Equal(CpuKeyText, environment.Values["CPU_KEY"]);
        Assert.Equal(0, terminal.DisableEchoCount);
    }

    [Fact]
    public async Task File_source_accepts_only_the_allowed_terminal_line_endings()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var resolver = new CpuKeySourceResolver(new FakeEnvironment(), new FakeTerminal(isTerminal: false));

        foreach (string suffix in new[] { string.Empty, "\n", "\r\n" })
        {
            string filePath = temporaryDirectory.File($"key-{suffix.Length}.txt");
            await File.WriteAllTextAsync(filePath, CpuKeyText + suffix);
            using var standardInput = new StringReader(string.Empty);

            CpuKey? result = await resolver.ResolveAsync(
                new CpuKeySourceSelection(filePath, null, readFromStandardInput: false),
                CpuKeySourcePolicy.Required,
                standardInput);

            Assert.True(result.HasValue);
            Assert.Equal(CpuKey.Parse(CpuKeyText), result.Value);
        }

        string bareCarriageReturnPath = temporaryDirectory.File("key-bare-carriage-return.txt");
        await File.WriteAllTextAsync(bareCarriageReturnPath, CpuKeyText + "\r");
        using var invalidInput = new StringReader(string.Empty);

        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
            () => resolver.ResolveAsync(
                new CpuKeySourceSelection(bareCarriageReturnPath, null, readFromStandardInput: false),
                CpuKeySourcePolicy.Required,
                invalidInput).AsTask());

        Assert.Equal("invalid-cpu-key", exception.Kind);
    }

    [Fact]
    public async Task File_source_rejects_data_beyond_the_bounded_key_file_size_without_redaction_leaks()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string filePath = temporaryDirectory.File("key-too-long.txt");
        string sourceValue = CpuKeyText + "\r\nX";
        await File.WriteAllTextAsync(filePath, sourceValue);
        var resolver = new CpuKeySourceResolver(new FakeEnvironment(), new FakeTerminal(isTerminal: false));
        using var standardInput = new StringReader(string.Empty);

        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
            () => resolver.ResolveAsync(
                new CpuKeySourceSelection(filePath, null, readFromStandardInput: false),
                CpuKeySourcePolicy.Required,
                standardInput).AsTask());

        Assert.Equal("invalid-cpu-key", exception.Kind);
        AssertRedacted(exception, sourceValue);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-cpu-key")]
    [InlineData(CpuKeyText + "X")]
    [InlineData(CpuKeyText + "\r")]
    [InlineData(CpuKeyText + "\n\n")]
    public async Task Environment_source_consumes_invalid_content_without_exposing_it(string sourceValue)
    {
        var environment = new FakeEnvironment(
            [("CPU_KEY", sourceValue), ("UNSELECTED_CPU_KEY", CpuKeyText)]);
        var resolver = new CpuKeySourceResolver(environment, new FakeTerminal(isTerminal: false));
        using var standardInput = new StringReader(string.Empty);

        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
            () => resolver.ResolveAsync(
                new CpuKeySourceSelection(null, "CPU_KEY", readFromStandardInput: false),
                CpuKeySourcePolicy.Required,
                standardInput).AsTask());

        Assert.Equal(ExitCode.Usage, exception.Code);
        Assert.Equal("invalid-cpu-key", exception.Kind);
        AssertRedacted(exception, CpuKeyText);
        if (sourceValue.Length > 0)
        {
            AssertRedacted(exception, sourceValue);
        }

        Assert.Equal(1, environment.ReadCount);
        Assert.Equal("CPU_KEY", Assert.Single(environment.RemovedNames));
        Assert.False(environment.Values.ContainsKey("CPU_KEY"));
        Assert.Equal(CpuKeyText, environment.Values["UNSELECTED_CPU_KEY"]);
    }

    [Fact]
    public async Task Environment_source_does_not_remove_a_missing_or_unselected_variable()
    {
        var environment = new FakeEnvironment([("UNSELECTED_CPU_KEY", CpuKeyText)]);
        var resolver = new CpuKeySourceResolver(environment, new FakeTerminal(isTerminal: false));
        using var standardInput = new StringReader(string.Empty);
        string sourceName = "CPU_KEY_" + CpuKeyText;

        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
            () => resolver.ResolveAsync(
                new CpuKeySourceSelection(null, sourceName, readFromStandardInput: false),
                CpuKeySourcePolicy.Required,
                standardInput).AsTask());

        Assert.Equal(ExitCode.Usage, exception.Code);
        Assert.Equal("cpu-key-env-not-found", exception.Kind);
        AssertRedacted(exception, sourceName);
        AssertRedacted(exception, CpuKeyText);
        Assert.Equal(1, environment.ReadCount);
        Assert.Empty(environment.RemovedNames);
        Assert.Equal(CpuKeyText, environment.Values["UNSELECTED_CPU_KEY"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("CPU-KEY")]
    [InlineData("1_CPU_KEY")]
    [InlineData("CPU_KEY=" + CpuKeyText)]
    [InlineData("CPU_KEY\0" + CpuKeyText)]
    public async Task Environment_source_rejects_invalid_names_before_lookup_or_removal(string sourceName)
    {
        var environment = new FakeEnvironment([("UNSELECTED_CPU_KEY", CpuKeyText)]);
        var resolver = new CpuKeySourceResolver(environment, new FakeTerminal(isTerminal: false));
        using var standardInput = new StringReader(string.Empty);

        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
            () => resolver.ResolveAsync(
                new CpuKeySourceSelection(null, sourceName, readFromStandardInput: false),
                CpuKeySourcePolicy.Required,
                standardInput).AsTask());

        Assert.Equal(ExitCode.Usage, exception.Code);
        Assert.Equal("invalid-cpu-key-env", exception.Kind);
        AssertRedacted(exception, CpuKeyText);
        if (sourceName.Length > 0)
        {
            AssertRedacted(exception, sourceName);
        }

        Assert.Equal(0, environment.ReadCount);
        Assert.Empty(environment.RemovedNames);
        Assert.Equal(CpuKeyText, environment.Values["UNSELECTED_CPU_KEY"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task Environment_source_consumes_only_the_selected_value_and_preserves_the_typed_key(string suffix)
    {
        var environment = new FakeEnvironment(
            [("CPU_KEY", CpuKeyText + suffix), ("UNSELECTED_CPU_KEY", CpuKeyText)]);
        var resolver = new CpuKeySourceResolver(environment, new FakeTerminal(isTerminal: false));
        using var standardInput = new StringReader(string.Empty);

        CpuKey? result = await resolver.ResolveAsync(
            new CpuKeySourceSelection(null, "CPU_KEY", readFromStandardInput: false),
            CpuKeySourcePolicy.Required,
            standardInput);

        Assert.True(result.HasValue);
        Assert.Equal(1, environment.ReadCount);
        Assert.Equal("CPU_KEY", Assert.Single(environment.RemovedNames));
        Assert.False(environment.Values.ContainsKey("CPU_KEY"));
        Assert.Equal(CpuKeyText, environment.Values["UNSELECTED_CPU_KEY"]);

        byte[] keyBytes = new byte[CpuKey.ByteLength];
        result.Value.CopyTo(keyBytes);
        Assert.Equal(Convert.FromHexString(CpuKeyText), keyBytes);
    }

    [Fact]
    public async Task Environment_source_consumes_a_value_before_observing_cancellation_after_lookup()
    {
        using var cancellationSource = new CancellationTokenSource();
        var environment = new FakeEnvironment([("CPU_KEY", CpuKeyText)])
        {
            AfterRead = cancellationSource.Cancel,
        };
        var resolver = new CpuKeySourceResolver(environment, new FakeTerminal(isTerminal: false));
        using var standardInput = new StringReader(string.Empty);

        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => resolver.ResolveAsync(
                new CpuKeySourceSelection(null, "CPU_KEY", readFromStandardInput: false),
                CpuKeySourcePolicy.Required,
                standardInput,
                cancellationSource.Token).AsTask());

        Assert.Equal(cancellationSource.Token, exception.CancellationToken);
        Assert.DoesNotContain(CpuKeyText, exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, environment.ReadCount);
        Assert.Equal("CPU_KEY", Assert.Single(environment.RemovedNames));
        Assert.False(environment.Values.ContainsKey("CPU_KEY"));
    }

    [Fact]
    public async Task Environment_source_is_untouched_when_cancellation_precedes_lookup()
    {
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var environment = new FakeEnvironment([("CPU_KEY", CpuKeyText)]);
        var resolver = new CpuKeySourceResolver(environment, new FakeTerminal(isTerminal: false));
        using var standardInput = new StringReader(string.Empty);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => resolver.ResolveAsync(
                new CpuKeySourceSelection(null, "CPU_KEY", readFromStandardInput: false),
                CpuKeySourcePolicy.Required,
                standardInput,
                cancellationSource.Token).AsTask());

        Assert.Equal(0, environment.ReadCount);
        Assert.Empty(environment.RemovedNames);
        Assert.Equal(CpuKeyText, environment.Values["CPU_KEY"]);
    }

    [Fact]
    public async Task Standard_input_accepts_one_value_and_rejects_trailing_data()
    {
        var resolver = new CpuKeySourceResolver(new FakeEnvironment(), new FakeTerminal(isTerminal: false));
        foreach (string suffix in new[] { string.Empty, "\n", "\r\n" })
        {
            using var standardInput = new StringReader(CpuKeyText + suffix);
            CpuKey? result = await resolver.ResolveAsync(
                new CpuKeySourceSelection(null, null, readFromStandardInput: true),
                CpuKeySourcePolicy.Required,
                standardInput);

            Assert.True(result.HasValue);
            Assert.Equal(CpuKey.Parse(CpuKeyText), result.Value);
        }

        string sourceValue = CpuKeyText + "\ntrailing";
        using var inputWithTrailingData = new StringReader(sourceValue);
        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
            () => resolver.ResolveAsync(
                new CpuKeySourceSelection(null, null, readFromStandardInput: true),
                CpuKeySourcePolicy.Required,
                inputWithTrailingData).AsTask());

        Assert.Equal("cpu-key-stdin-extra-data", exception.Kind);
        AssertRedacted(exception, CpuKeyText);

        using var bareCarriageReturn = new StringReader(CpuKeyText + "\r");
        OperationFailureException bareCarriageReturnFailure = await Assert.ThrowsAsync<OperationFailureException>(
            () => resolver.ResolveAsync(
                new CpuKeySourceSelection(null, null, readFromStandardInput: true),
                CpuKeySourcePolicy.Required,
                bareCarriageReturn).AsTask());

        Assert.Equal("invalid-cpu-key", bareCarriageReturnFailure.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task Standard_input_restores_terminal_echo_after_success(string lineEnding)
    {
        var terminal = new FakeTerminal(isTerminal: true);
        var resolver = new CpuKeySourceResolver(new FakeEnvironment(), terminal);
        using var standardInput = new StringReader(CpuKeyText + lineEnding);
        terminal.Input = standardInput;

        CpuKey? result = await resolver.ResolveAsync(
            new CpuKeySourceSelection(null, null, readFromStandardInput: true),
            CpuKeySourcePolicy.Required,
            standardInput);

        Assert.True(result.HasValue);
        Assert.True(terminal.EchoEnabled);
        FakeEchoScope scope = Assert.Single(terminal.Scopes);
        Assert.Equal(1, scope.DisposeCount);
        Assert.Equal(1, scope.CompleteInputCount);
        Assert.Equal(0, scope.InputFlushCount);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task Terminal_standard_input_ignores_the_supplied_reader_and_stops_after_one_line(string lineEnding)
    {
        using var terminalInput = new StringReader(CpuKeyText + lineEnding + "next input\n");
        using var standardInput = new ThrowingReader(new IOException("Unexpected supplied reader access."));
        var terminal = new FakeTerminal(isTerminal: true)
        {
            Input = terminalInput,
        };
        var resolver = new CpuKeySourceResolver(new FakeEnvironment(), terminal);

        CpuKey? result = await resolver.ResolveAsync(
            new CpuKeySourceSelection(null, null, readFromStandardInput: true),
            CpuKeySourcePolicy.Required,
            standardInput);

        Assert.True(result.HasValue);
        Assert.Equal(CpuKey.Parse(CpuKeyText), result.Value);
        Assert.Equal((int)'n', terminalInput.Peek());
        Assert.Equal(1, terminal.DisableEchoCount);
        Assert.True(terminal.EchoEnabled);
        FakeEchoScope scope = Assert.Single(terminal.Scopes);
        Assert.Equal(1, scope.DisposeCount);
        Assert.Equal(1, scope.CompleteInputCount);
        Assert.Equal(0, scope.InputFlushCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task Terminal_standard_input_discards_only_the_current_overlong_line(string lineEnding)
    {
        string nextLine = lineEnding.Length == 0 ? string.Empty : "next input\n";
        using var terminalInput = new StringReader(
            CpuKeyText + new string('X', CpuKeySourceResolver.MaximumFileByteLength + 1) + lineEnding + nextLine);
        using var standardInput = new ThrowingReader(new IOException("Unexpected supplied reader access."));
        var terminal = new FakeTerminal(isTerminal: true)
        {
            Input = terminalInput,
        };
        var resolver = new CpuKeySourceResolver(new FakeEnvironment(), terminal);

        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
            () => resolver.ResolveAsync(
                new CpuKeySourceSelection(null, null, readFromStandardInput: true),
                CpuKeySourcePolicy.Required,
                standardInput).AsTask());

        Assert.Equal(ExitCode.Usage, exception.Code);
        Assert.Equal("invalid-cpu-key", exception.Kind);
        AssertRedacted(exception, CpuKeyText);
        Assert.Equal(lineEnding.Length == 0 ? -1 : (int)'n', terminalInput.Peek());
        Assert.True(terminal.EchoEnabled);
        Assert.Equal(1, terminal.RestoreEchoCount);
        FakeEchoScope scope = Assert.Single(terminal.Scopes);
        Assert.Equal(1, scope.DisposeCount);
        Assert.Equal(1, scope.ReleaseCount);
        Assert.Equal(1, scope.CompleteInputCount);
        Assert.Equal(0, scope.InputFlushCount);
    }

    [Fact]
    public async Task Standard_input_restores_the_exact_prior_terminal_echo_state()
    {
        var terminal = new FakeTerminal(isTerminal: true, echoEnabled: false);
        var resolver = new CpuKeySourceResolver(new FakeEnvironment(), terminal);
        using var standardInput = new StringReader(CpuKeyText + "\n");
        terminal.Input = standardInput;

        await resolver.ResolveAsync(
            new CpuKeySourceSelection(null, null, readFromStandardInput: true),
            CpuKeySourcePolicy.Required,
            standardInput);

        Assert.False(terminal.EchoEnabled);
        FakeEchoScope scope = Assert.Single(terminal.Scopes);
        Assert.Equal(1, scope.DisposeCount);
        Assert.Equal(1, scope.CompleteInputCount);
        Assert.Equal(0, scope.InputFlushCount);
    }

    [Fact]
    public async Task Standard_input_restores_terminal_echo_after_validation_failure()
    {
        var terminal = new FakeTerminal(isTerminal: true);
        var resolver = new CpuKeySourceResolver(new FakeEnvironment(), terminal);
        using var standardInput = new StringReader("not-a-cpu-key\nnext input\n");
        terminal.Input = standardInput;

        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
            () => resolver.ResolveAsync(
                new CpuKeySourceSelection(null, null, readFromStandardInput: true),
                CpuKeySourcePolicy.Required,
                standardInput).AsTask());

        Assert.Equal("invalid-cpu-key", exception.Kind);
        Assert.Equal((int)'n', standardInput.Peek());
        Assert.True(terminal.EchoEnabled);
        FakeEchoScope scope = Assert.Single(terminal.Scopes);
        Assert.Equal(1, scope.DisposeCount);
        Assert.Equal(1, scope.CompleteInputCount);
        Assert.Equal(0, scope.InputFlushCount);
    }

    [Fact]
    public async Task Standard_input_restores_terminal_echo_after_io_failure_without_exposing_the_reader_message()
    {
        var terminal = new FakeTerminal(isTerminal: true);
        var resolver = new CpuKeySourceResolver(new FakeEnvironment(), terminal);
        using var standardInput = new ThrowingReader(new IOException(CpuKeyText));
        terminal.Input = standardInput;

        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
            () => resolver.ResolveAsync(
                new CpuKeySourceSelection(null, null, readFromStandardInput: true),
                CpuKeySourcePolicy.Required,
                standardInput).AsTask());

        Assert.Equal(ExitCode.InputOutput, exception.Code);
        Assert.Equal("cpu-key-source-read-failed", exception.Kind);
        AssertRedacted(exception, CpuKeyText);
        Assert.True(terminal.EchoEnabled);
        FakeEchoScope scope = Assert.Single(terminal.Scopes);
        Assert.Equal(1, scope.DisposeCount);
        Assert.Equal(0, scope.CompleteInputCount);
        Assert.Equal(1, scope.InputFlushCount);
        Assert.Equal(1, scope.ReleaseCount);
        Assert.Equal(1, terminal.RestoreEchoCount);
    }

    [Fact]
    public async Task Standard_input_sanitizes_terminal_restore_io_failure_without_retrying_disposal()
    {
        using var standardInput = new StringReader(CpuKeyText + "\n");
        var terminal = new FakeTerminal(isTerminal: true)
        {
            Input = standardInput,
            EchoRestoreFailure = new IOException(CpuKeyText),
        };
        var resolver = new CpuKeySourceResolver(new FakeEnvironment(), terminal);

        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
            () => resolver.ResolveAsync(
                new CpuKeySourceSelection(null, null, readFromStandardInput: true),
                CpuKeySourcePolicy.Required,
                standardInput).AsTask());

        Assert.Equal(ExitCode.InputOutput, exception.Code);
        Assert.Equal("cpu-key-source-read-failed", exception.Kind);
        AssertRedacted(exception, CpuKeyText);
        Assert.True(terminal.EchoEnabled);
        Assert.Equal(1, terminal.RestoreEchoCount);
        FakeEchoScope scope = Assert.Single(terminal.Scopes);
        Assert.Equal(1, scope.DisposeCount);
        Assert.Equal(1, scope.ReleaseCount);
        Assert.Equal(1, scope.CompleteInputCount);
        Assert.Equal(0, scope.InputFlushCount);
    }

    [Fact]
    public async Task Standard_input_sanitizes_abort_flush_io_failure_and_still_attempts_attribute_restoration()
    {
        using var standardInput = new ThrowingReader(new IOException("Input failed."));
        var terminal = new FakeTerminal(isTerminal: true)
        {
            Input = standardInput,
            InputFlushFailure = new IOException(CpuKeyText),
        };
        var resolver = new CpuKeySourceResolver(new FakeEnvironment(), terminal);

        OperationFailureException exception = await Assert.ThrowsAsync<OperationFailureException>(
            () => resolver.ResolveAsync(
                new CpuKeySourceSelection(null, null, readFromStandardInput: true),
                CpuKeySourcePolicy.Required,
                standardInput).AsTask());

        Assert.Equal(ExitCode.InputOutput, exception.Code);
        Assert.Equal("cpu-key-source-read-failed", exception.Kind);
        AssertRedacted(exception, CpuKeyText);
        Assert.True(terminal.EchoEnabled);
        Assert.Equal(1, terminal.RestoreEchoCount);
        FakeEchoScope scope = Assert.Single(terminal.Scopes);
        Assert.Equal(0, scope.CompleteInputCount);
        Assert.Equal(1, scope.InputFlushCount);
        Assert.Equal(1, scope.DisposeCount);
        Assert.Equal(1, scope.ReleaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Standard_input_restores_terminal_echo_after_cancellation(bool hasPartialInput)
    {
        using var cancellationSource = new CancellationTokenSource();
        var terminal = new FakeTerminal(isTerminal: true);
        var resolver = new CpuKeySourceResolver(new FakeEnvironment(), terminal);
        using var standardInput = new WaitingReader(hasPartialInput ? CpuKeyText[..8] : string.Empty);
        terminal.Input = standardInput;

        Task<CpuKey?> resolution = resolver.ResolveAsync(
            new CpuKeySourceSelection(null, null, readFromStandardInput: true),
            CpuKeySourcePolicy.Required,
            standardInput,
            cancellationSource.Token).AsTask();
        await terminal.EchoDisabled.Task;
        await standardInput.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolution);

        Assert.True(terminal.EchoEnabled);
        FakeEchoScope scope = Assert.Single(terminal.Scopes);
        Assert.Equal(1, scope.DisposeCount);
        Assert.Equal(0, scope.CompleteInputCount);
        Assert.Equal(1, scope.InputFlushCount);
        Assert.Equal(1, scope.ReleaseCount);
        Assert.Equal(1, terminal.RestoreEchoCount);
    }

    [Fact]
    public async Task Terminal_standard_input_cancels_while_discarding_an_overlong_line_without_a_line_feed()
    {
        using var cancellationSource = new CancellationTokenSource();
        using var standardInput = new WaitingReader(
            CpuKeyText + new string('X', CpuKeySourceResolver.MaximumFileByteLength + 1));
        var terminal = new FakeTerminal(isTerminal: true)
        {
            Input = standardInput,
        };
        var resolver = new CpuKeySourceResolver(new FakeEnvironment(), terminal);

        Task<CpuKey?> resolution = resolver.ResolveAsync(
            new CpuKeySourceSelection(null, null, readFromStandardInput: true),
            CpuKeySourcePolicy.Required,
            standardInput,
            cancellationSource.Token).AsTask();
        await standardInput.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(terminal.EchoEnabled);
        Assert.Equal(0, Assert.Single(terminal.Scopes).DisposeCount);
        cancellationSource.Cancel();

        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolution);

        Assert.Equal(cancellationSource.Token, exception.CancellationToken);
        Assert.DoesNotContain(CpuKeyText, exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(terminal.EchoEnabled);
        Assert.Equal(1, terminal.RestoreEchoCount);
        FakeEchoScope scope = Assert.Single(terminal.Scopes);
        Assert.Equal(1, scope.DisposeCount);
        Assert.Equal(1, scope.ReleaseCount);
        Assert.Equal(0, scope.CompleteInputCount);
        Assert.Equal(1, scope.InputFlushCount);
    }

    [Fact]
    public async Task Application_internal_input_overload_accepts_a_caller_owned_reader()
    {
        using var standardInput = new StringReader(CpuKeyText);
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        int exitCode = await CliApplication.RunAsync(
            ["--version"],
            standardInput,
            standardOutput,
            standardError);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, standardError.ToString());
    }

    private static void AssertRedacted(OperationFailureException exception, string secret)
    {
        Assert.DoesNotContain(secret, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeEnvironment : ICpuKeyEnvironment
    {
        private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);

        internal FakeEnvironment(IEnumerable<(string Name, string? Value)>? values = null)
        {
            if (values is null)
            {
                return;
            }

            foreach ((string name, string? value) in values)
            {
                _values.Add(name, value);
            }
        }

        internal int ReadCount { get; private set; }

        internal IReadOnlyDictionary<string, string?> Values => _values;

        internal List<string> RemovedNames { get; } = [];

        internal Action? AfterRead { get; init; }

        public string? GetEnvironmentVariable(string variableName)
        {
            ReadCount++;
            string? value = _values.TryGetValue(variableName, out string? storedValue) ? storedValue : null;
            AfterRead?.Invoke();
            return value;
        }

        public void RemoveEnvironmentVariable(string variableName)
        {
            RemovedNames.Add(variableName);
            _values.Remove(variableName);
        }
    }

    private sealed class FakeTerminal : IStandardInputTerminal
    {
        private readonly bool _isTerminal;
        private readonly char[] _readBuffer = new char[1];

        internal FakeTerminal(bool isTerminal, bool echoEnabled = true)
        {
            _isTerminal = isTerminal;
            EchoEnabled = echoEnabled;
        }

        internal TaskCompletionSource<bool> EchoDisabled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal List<FakeEchoScope> Scopes { get; } = [];

        internal int DisableEchoCount { get; private set; }

        internal int RestoreEchoCount { get; private set; }

        internal bool EchoEnabled { get; private set; }

        internal TextReader? Input { get; set; }

        internal IOException? EchoRestoreFailure { get; init; }

        internal IOException? InputFlushFailure { get; init; }

        public bool IsTerminal => _isTerminal;

        public ValueTask<IStandardInputEchoScope> DisableEchoAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DisableEchoCount++;
            bool originalEchoEnabled = EchoEnabled;
            EchoEnabled = false;
            var scope = new FakeEchoScope(originalEchoEnabled, RestoreEcho, FlushInput);
            Scopes.Add(scope);
            EchoDisabled.TrySetResult(true);
            return ValueTask.FromResult<IStandardInputEchoScope>(scope);
        }

        public async ValueTask<int> ReadCharacterAsync(CancellationToken cancellationToken)
        {
            Assert.True(IsTerminal);
            Assert.False(EchoEnabled);
            Assert.NotEmpty(Scopes);
            Assert.Equal(0, Scopes[^1].DisposeCount);
            Assert.Equal(0, Scopes[^1].CompleteInputCount);
            TextReader input = Input ?? throw new InvalidOperationException("Fake terminal input must be bound.");
            try
            {
                int read = await input.ReadAsync(_readBuffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                return read == 0 ? -1 : _readBuffer[0];
            }
            finally
            {
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(_readBuffer.AsSpan()));
            }
        }

        private void RestoreEcho(bool echoEnabled)
        {
            RestoreEchoCount++;
            EchoEnabled = echoEnabled;
            if (EchoRestoreFailure is { } failure)
            {
                throw failure;
            }
        }

        private void FlushInput()
        {
            Assert.False(EchoEnabled);
            if (InputFlushFailure is { } failure)
            {
                throw failure;
            }
        }
    }

    private sealed class FakeEchoScope : IStandardInputEchoScope
    {
        private readonly bool _originalEchoEnabled;
        private readonly Action<bool> _restoreEcho;
        private readonly Action _flushInput;
        private bool _inputComplete;

        internal FakeEchoScope(bool originalEchoEnabled, Action<bool> restoreEcho, Action flushInput)
        {
            _originalEchoEnabled = originalEchoEnabled;
            _restoreEcho = restoreEcho;
            _flushInput = flushInput;
        }

        internal int DisposeCount { get; private set; }

        internal int ReleaseCount { get; private set; }

        internal int CompleteInputCount { get; private set; }

        internal int InputFlushCount { get; private set; }

        public void CompleteInput()
        {
            Assert.Equal(0, DisposeCount);
            CompleteInputCount++;
            _inputComplete = true;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            bool inputComplete = _inputComplete;
            try
            {
                try
                {
                    if (!inputComplete)
                    {
                        InputFlushCount++;
                        _flushInput();
                    }
                }
                finally
                {
                    _restoreEcho(_originalEchoEnabled);
                }
            }
            finally
            {
                ReleaseCount++;
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingReader : TextReader
    {
        private readonly IOException _exception;

        internal ThrowingReader(IOException exception)
        {
            _exception = exception;
        }

        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            throw _exception;
        }
    }

    private sealed class WaitingReader : TextReader
    {
        private readonly string _prefix;
        private int _offset;

        internal WaitingReader(string prefix = "")
        {
            _prefix = prefix;
        }

        internal TaskCompletionSource<bool> Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_offset < _prefix.Length)
            {
                buffer.Span[0] = _prefix[_offset++];
                return 1;
            }

            Waiting.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"jrunner-cli-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        internal string File(string fileName)
        {
            return System.IO.Path.Combine(Path, fileName);
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
