using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JRunner.Tests.Fixtures;
using Xunit;

namespace JRunner.Cli.Tests;

public sealed class CpuKeyPtyIntegrationTests
{
    [CpuKeyPtyLinuxTheory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task Real_fd0_accepts_a_synthetic_ASCII_key_and_restores_every_terminal_attribute(string lineEnding)
    {
        NandInspectionFixture fixture = GetInspectionFixture();
        using var input = SyntheticKeyLine.Load(fixture, lineEnding);
        await using var child = CpuKeyPtyProcess.Start(CreateArguments(fixture));

        Assert.NotEqual(0U, CpuKeyPtyProcess.ReadLocalFlags(child.OriginalAttributes) & CpuKeyPtyProcess.EchoFlags);
        byte[] duringInput = await child.WaitForEchoDisabledAsync();
        Assert.Equal(CpuKeyPtyProcess.WithoutEcho(child.OriginalAttributes), duringInput);

        await child.WriteInputAsync(input.Line);
        await child.WaitForExitAsync();

        Assert.Equal(child.OriginalAttributes, child.CaptureAttributes());
        AssertNoKeyLeak(child, input.Key);
        AssertSuccessfulInspection(child, fixture);
    }

    [CpuKeyPtyLinuxFact]
    public async Task Real_fd0_rejects_malformed_ASCII_without_leaking_it_and_restores_every_terminal_attribute()
    {
        NandInspectionFixture fixture = GetInspectionFixture();
        using var input = SyntheticKeyLine.Load(fixture, "\n", malformed: true);
        await using var child = CpuKeyPtyProcess.Start(CreateArguments(fixture));

        Assert.Equal(CpuKeyPtyProcess.WithoutEcho(child.OriginalAttributes), await child.WaitForEchoDisabledAsync());
        await child.WriteInputAsync(input.Line);
        await child.WaitForExitAsync();

        Assert.Equal(child.OriginalAttributes, child.CaptureAttributes());
        AssertNoKeyLeak(child, input.Key);
        AssertFailure(child, 2, "invalid-cpu-key");
    }

    [CpuKeyPtyLinuxTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Program_SIGINT_cancels_idle_or_partial_fd0_input_without_a_later_line_and_restores_all_attributes(bool partialLine)
    {
        NandInspectionFixture fixture = GetInspectionFixture();
        using var input = SyntheticKeyLine.Load(fixture, "\n");
        // The partial case is noncanonical so the prefix is available to native
        // read, rather than merely waiting in the terminal's canonical line buffer.
        await using var child = CpuKeyPtyProcess.Start(CreateArguments(fixture), canonicalInput: !partialLine);
        byte[] expectedDuringInput = CpuKeyPtyProcess.WithoutEcho(child.OriginalAttributes);

        Assert.Equal(expectedDuringInput, await child.WaitForEchoDisabledAsync());
        if (partialLine)
        {
            await child.WriteInputAsync(input.Key[..16]);
        }

        // Give a blocked/partially completed input read time to settle before the
        // signal, and prove its unrelated attrs still have not changed meanwhile.
        await Task.Delay(250);
        Assert.Equal(expectedDuringInput, child.CaptureAttributes());
        child.SendInterrupt();
        // No newline, EOF, terminal close, or subsequent input unblocks this case.
        await child.WaitForExitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(child.OriginalAttributes, child.CaptureAttributes());
        AssertNoKeyLeak(child, input.Key);
        AssertFailure(child, 130, "cancelled");
    }

    [CpuKeyPtyLinuxTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Program_SIGINT_discards_a_canonical_partial_key_before_restoring_echo_and_accepting_a_fresh_line(bool outputStopped)
    {
        NandInspectionFixture fixture = GetInspectionFixture();
        using var input = SyntheticKeyLine.Load(fixture, "\n");
        byte[] freshLine = "post-cancellation-stdin-is-clean\n"u8.ToArray();
        byte[]? observedLine = null;
        try
        {
            await using var child = CpuKeyPtyProcess.Start(CreateArguments(fixture), canonicalInput: true);
            Assert.NotEqual(0U, CpuKeyPtyProcess.ReadLocalFlags(child.OriginalAttributes) & 0x2U); // ICANON
            byte[] expectedDuringInput = CpuKeyPtyProcess.WithoutEcho(child.OriginalAttributes);
            Assert.Equal(expectedDuringInput, await child.WaitForEchoDisabledAsync());
            await child.WriteInputAsync(input.Key[..16], requireSingleWrite: true);
            await Task.Delay(250);
            Assert.Equal(expectedDuringInput, child.CaptureAttributes());
            if (outputStopped)
            {
                child.StopOutputFlow();
                Assert.Equal(expectedDuringInput, child.CaptureAttributes());
            }

            // An externally delivered SIGINT does not itself flush the kernel's
            // canonical partial line, unlike typing the terminal's VINTR character.
            child.SendInterrupt();
            // Nothing supplies LF/EOF or closes the terminal before cancellation.
            await child.WaitForExitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(child.OriginalAttributes, child.CaptureAttributes());
            AssertNoKeyLeak(child, input.Key);
            AssertFailure(child, 130, "cancelled");

            if (outputStopped)
            {
                // Resumption must not be needed for either bounded cancellation
                // or exact restoration; no PTY write occurs while flow is stopped.
                child.ResumeOutputFlow();
                Assert.Equal(child.OriginalAttributes, child.CaptureAttributes());
            }

            // Only after the child is reaped, terminate a fresh observer line.
            // A retained key prefix would be prepended to this line by n_tty.
            await child.WriteAfterExitAsync(freshLine);
            observedLine = await child.ReadRemainingSlaveInputAsync(freshLine.Length);
            AssertNoKeyLeak(child, input.Key);
            Assert.True(freshLine.AsSpan().SequenceEqual(observedLine), "Canonical cancellation retained CPU-key prefix bytes before the fresh observer line.");
            Assert.False(child.HasRemainingSlaveInput(), "Canonical cancellation left extra readable stdin bytes after the fresh observer line.");
            Assert.Equal(child.OriginalAttributes, child.CaptureAttributes());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(freshLine);
            if (observedLine is not null)
            {
                CryptographicOperations.ZeroMemory(observedLine);
            }
        }
    }

    [CpuKeyPtyLinuxFact]
    public async Task Real_fd0_consumes_only_the_key_line_and_leaves_a_prequeued_second_line_on_the_slave()
    {
        NandInspectionFixture fixture = GetInspectionFixture();
        using var input = SyntheticKeyLine.Load(fixture, "\n");
        byte[] secondLine = "cpu-key-pty-next-line-remains-unread\n"u8.ToArray();
        byte[] batch = new byte[input.Line.Length + secondLine.Length];
        byte[]? remaining = null;
        try
        {
            input.Line.Span.CopyTo(batch);
            secondLine.CopyTo(batch, input.Line.Length);
            await using var child = CpuKeyPtyProcess.Start(CreateArguments(fixture));

            Assert.Equal(CpuKeyPtyProcess.WithoutEcho(child.OriginalAttributes), await child.WaitForEchoDisabledAsync());
            // Queue both lines in the same short PTY write. A buffered Console.In
            // reader would steal the second line even if the resolver stopped at LF.
            await child.WriteInputAsync(batch, requireSingleWrite: true);
            await child.WaitForExitAsync();

            Assert.Equal(child.OriginalAttributes, child.CaptureAttributes());
            AssertNoKeyLeak(child, input.Key);
            AssertSuccessfulInspection(child, fixture);
            remaining = await child.ReadRemainingSlaveInputAsync(secondLine.Length);
            Assert.Equal(secondLine, remaining);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(batch);
            CryptographicOperations.ZeroMemory(secondLine);
            if (remaining is not null)
            {
                CryptographicOperations.ZeroMemory(remaining);
            }
        }
    }

    [CpuKeyPtyLinuxTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Overlong_paste_drains_through_only_its_LF_without_leaks_or_shell_leftovers(bool fragmented, bool queueFollowingLine)
    {
        NandInspectionFixture fixture = GetInspectionFixture();
        using var input = SyntheticKeyLine.Load(fixture, "\n");
        byte[] suffix = "overlong-paste-tail-that-must-not-be-returned-to-the-shell"u8.ToArray();
        byte[] followingLine = queueFollowingLine ? "sentinel-after-the-overflowed-key-line\n"u8.ToArray() : [];
        int lineFeedOffset = input.Key.Length + suffix.Length;
        byte[] paste = new byte[lineFeedOffset + 1 + followingLine.Length];
        byte[]? remaining = null;
        try
        {
            input.Key.Span.CopyTo(paste);
            suffix.CopyTo(paste, input.Key.Length);
            paste[lineFeedOffset] = (byte)'\n';
            followingLine.CopyTo(paste, lineFeedOffset + 1);
            // Noncanonical mode makes the fragmented prefix readable before LF,
            // so an early validation failure cannot hide in kernel line buffering.
            await using var child = CpuKeyPtyProcess.Start(CreateArguments(fixture), canonicalInput: !fragmented);
            byte[] expectedDuringInput = CpuKeyPtyProcess.WithoutEcho(child.OriginalAttributes);

            Assert.Equal(expectedDuringInput, await child.WaitForEchoDisabledAsync());
            if (fragmented)
            {
                await child.WriteInputAsync(paste.AsMemory(0, lineFeedOffset), requireSingleWrite: true);
                await Task.Delay(250);
                Assert.Equal(expectedDuringInput, child.CaptureAttributes());
                await child.WriteInputAsync(paste.AsMemory(lineFeedOffset), requireSingleWrite: true);
            }
            else
            {
                await child.WriteInputAsync(paste, requireSingleWrite: true);
            }

            await child.WaitForExitAsync();
            Assert.Equal(child.OriginalAttributes, child.CaptureAttributes());
            AssertNoKeyLeak(child, input.Key);
            AssertFailure(child, 2, "invalid-cpu-key");

            if (queueFollowingLine)
            {
                remaining = await child.ReadRemainingSlaveInputAsync(followingLine.Length);
                Assert.True(followingLine.AsSpan().SequenceEqual(remaining), "Overflow draining did not preserve exactly the following sentinel line.");
            }

            Assert.False(child.HasRemainingSlaveInput(), "The overflowed CPU-key paste left readable stdin bytes for the invoking shell.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(paste);
            CryptographicOperations.ZeroMemory(suffix);
            CryptographicOperations.ZeroMemory(followingLine);
            if (remaining is not null)
            {
                CryptographicOperations.ZeroMemory(remaining);
            }
        }
    }

    [CpuKeyPtyLinuxFact]
    public async Task SIGINT_cancels_overflow_discard_without_LF_and_restores_all_terminal_attributes()
    {
        NandInspectionFixture fixture = GetInspectionFixture();
        using var input = SyntheticKeyLine.Load(fixture, "\n");
        byte[] prefix = new byte[input.Key.Length * 3];
        try
        {
            for (int offset = 0; offset < prefix.Length; offset += input.Key.Length)
            {
                input.Key.Span.CopyTo(prefix.AsSpan(offset));
            }

            await using var child = CpuKeyPtyProcess.Start(CreateArguments(fixture), canonicalInput: false);
            byte[] expectedDuringInput = CpuKeyPtyProcess.WithoutEcho(child.OriginalAttributes);
            Assert.Equal(expectedDuringInput, await child.WaitForEchoDisabledAsync());
            await child.WriteInputAsync(prefix, requireSingleWrite: true);
            await Task.Delay(250);
            Assert.Equal(expectedDuringInput, child.CaptureAttributes());

            child.SendInterrupt();
            // No LF, EOF, or later input is supplied while overflow is discarded.
            await child.WaitForExitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(child.OriginalAttributes, child.CaptureAttributes());
            AssertNoKeyLeak(child, input.Key);
            AssertFailure(child, 130, "cancelled");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(prefix);
        }
    }

    private static NandInspectionFixture GetInspectionFixture() => FixtureCatalog.Manifest.Scenarios?.NandInspection
        ?? throw new InvalidOperationException("The synthetic fixture manifest did not declare NAND inspection.");

    private static string[] CreateArguments(NandInspectionFixture fixture) =>
        ["nand", "inspect", "--input", FixtureCatalog.GetPath(fixture.Input), "--cpu-key-stdin", "--json"];

    private static void AssertSuccessfulInspection(CpuKeyPtyProcess child, NandInspectionFixture fixture)
    {
        Assert.Equal(0, child.ExitCode);
        using JsonDocument document = JsonDocument.Parse(child.StandardOutput);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        JsonElement result = document.RootElement.GetProperty("result");
        Assert.Equal(fixture.Format, result.GetProperty("canonicalImage").GetProperty("detectedFormat").GetString());
        Assert.Equal(fixture.Layout, result.GetProperty("canonicalImage").GetProperty("selectedLayout").GetString());
        Assert.Equal(fixture.CpuKeyVerification, result.GetProperty("keyvault").GetProperty("inspection").GetProperty("cpuKeyVerification").GetString());
    }

    private static void AssertFailure(CpuKeyPtyProcess child, int expectedExitCode, string expectedKind)
    {
        Assert.Equal(expectedExitCode, child.ExitCode);
        using JsonDocument document = JsonDocument.Parse(child.StandardOutput);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        JsonElement error = document.RootElement.GetProperty("error");
        Assert.Equal(expectedExitCode, error.GetProperty("code").GetInt32());
        Assert.Equal(expectedKind, error.GetProperty("kind").GetString());
        string message = Assert.IsType<string>(error.GetProperty("message").GetString());
        Assert.False(string.IsNullOrWhiteSpace(message));
        // JSON errors belong on stdout; their redacted human diagnostic belongs
        // on stderr. Check the entire capture so additional output is not hidden.
        byte[] expectedDiagnostic = Encoding.UTF8.GetBytes(message + Environment.NewLine);
        Assert.True(child.StandardError.Span.SequenceEqual(expectedDiagnostic),
            "CPU-key stderr must contain exactly the JSON error diagnostic and no extra output.");
    }

    private static void AssertNoKeyLeak(CpuKeyPtyProcess child, ReadOnlyMemory<byte> key)
    {
        AssertKeyAbsent(child.StandardOutput.Span, key.Span, "stdout");
        AssertKeyAbsent(child.StandardError.Span, key.Span, "stderr");
        // Console startup can write harmless ANSI sequences through fd 0. Only
        // synthetic key text, not arbitrary terminal noise, is forbidden here.
        AssertKeyAbsent(child.TerminalOutput.Span, key.Span, "PTY echo");
    }

    private static void AssertKeyAbsent(ReadOnlySpan<byte> capture, ReadOnlySpan<byte> key, string channel)
    {
        Assert.False(ContainsAsciiIgnoringCase(capture, key), $"Synthetic CPU-key text appeared in {channel}.");
        // Detect meaningful partial disclosures without constructing immutable
        // key strings or putting expected/actual plaintext in assertion output.
        for (int offset = 0; offset + 8 <= key.Length; offset += 8)
        {
            Assert.False(ContainsAsciiIgnoringCase(capture, key.Slice(offset, 8)), $"Synthetic CPU-key bytes appeared in {channel}.");
        }
    }

    private static bool ContainsAsciiIgnoringCase(ReadOnlySpan<byte> capture, ReadOnlySpan<byte> value)
    {
        for (int start = 0; start <= capture.Length - value.Length; start++)
        {
            int index = 0;
            while (index < value.Length && FoldAscii(capture[start + index]) == FoldAscii(value[index]))
            {
                index++;
            }

            if (index == value.Length)
            {
                return true;
            }
        }

        return false;
    }

    private static byte FoldAscii(byte value) => value is >= (byte)'a' and <= (byte)'z' ? (byte)(value - 32) : value;

    private sealed class SyntheticKeyLine : IDisposable
    {
        private readonly byte[] _line;
        private SyntheticKeyLine(byte[] line) => _line = line;
        internal ReadOnlyMemory<byte> Key => _line.AsMemory(0, 32);
        internal ReadOnlyMemory<byte> Line => _line;

        internal static SyntheticKeyLine Load(NandInspectionFixture fixture, string lineEnding, bool malformed = false)
        {
            byte[] source = File.ReadAllBytes(FixtureCatalog.GetPath(fixture.CpuKey));
            byte[]? line = null;
            try
            {
                int keyLength = source.Length;
                while (keyLength > 0 && source[keyLength - 1] is (byte)'\r' or (byte)'\n')
                {
                    keyLength--;
                }

                if (keyLength != 32 || lineEnding is not "\n" and not "\r\n")
                {
                    throw new InvalidOperationException("The synthetic CPU-key fixture or requested line ending is invalid.");
                }

                foreach (byte value in source.AsSpan(0, keyLength))
                {
                    if (value is not (>= (byte)'0' and <= (byte)'9') and not (>= (byte)'A' and <= (byte)'F') and not (>= (byte)'a' and <= (byte)'f'))
                    {
                        throw new InvalidOperationException("The synthetic CPU-key fixture must be ASCII hexadecimal.");
                    }
                }

                line = new byte[keyLength + lineEnding.Length];
                source.AsSpan(0, keyLength).CopyTo(line);
                if (malformed)
                {
                    line[0] = (byte)'G';
                }

                if (lineEnding.Length == 2)
                {
                    line[keyLength] = (byte)'\r';
                }

                line[^1] = (byte)'\n';
                return new SyntheticKeyLine(line);
            }
            catch
            {
                if (line is not null)
                {
                    CryptographicOperations.ZeroMemory(line);
                }

                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(source);
            }
        }

        public void Dispose() => CryptographicOperations.ZeroMemory(_line);
    }
}
