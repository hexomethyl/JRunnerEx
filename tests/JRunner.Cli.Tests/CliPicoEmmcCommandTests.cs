using System.Buffers.Binary;
using System.Text.Json;
using JRunner.Cli;
using JRunner.Cli.Infrastructure;
using JRunner.Core.Contracts;
using JRunner.Core.Devices.PicoFlasher;
using Xunit;

namespace JRunner.Cli.Tests;

public sealed class CliPicoEmmcCommandTests
{
    private const string TestDevicePath = "/dev/ttyACM0";
    private const string TestSerialNumber = "test-serial";
    private const uint TestFlashConfiguration = 0xC046_2002U;

    [Theory]
    [InlineData("0", "0")]
    [InlineData("98304", "1")]
    [InlineData("98303", "2")]
    [InlineData("4294967295", "1")]
    [InlineData("1", "4294967295")]
    public async Task Emmc_read_rejects_static_invalid_ranges_before_device_enumeration(
        string startBlock,
        string blocks)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string outputPath = Path.Combine(temporaryDirectory.Path, "output.bin");
        var enumerator = new CountingEmptyEnumerator();
        var transportFactory = new NeverOpenedTransportFactory();
        var connectionFactory = new PicoFlasherConnectionFactory(enumerator, transportFactory);
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        int exitCode = await CliCommandRouter.RunAsync(
            [
                "pico", "emmc-read",
                "--output", outputPath,
                "--start-block", startBlock,
                "--blocks", blocks,
                "--json",
            ],
            standardOutput,
            standardError,
            CancellationToken.None,
            connectionFactory);

        Assert.Equal((int)ExitCode.Usage, exitCode);
        AssertFailure(standardOutput, ExitCode.Usage, "pico-emmc-range-invalid");
        Assert.NotEmpty(standardError.ToString());
        Assert.Equal(0, enumerator.EnumerateCallCount);
        Assert.Equal(0, transportFactory.OpenCallCount);
        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path));
    }

    [Theory]
    [InlineData("--blocks")]
    [InlineData("--output")]
    public async Task Emmc_read_requires_blocks_and_output_before_device_enumeration(string omittedOption)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var arguments = new List<string> { "pico", "emmc-read", "--json" };
        if (!string.Equals(omittedOption, "--blocks", StringComparison.Ordinal))
        {
            arguments.AddRange(["--blocks", "1"]);
        }

        if (!string.Equals(omittedOption, "--output", StringComparison.Ordinal))
        {
            arguments.AddRange(["--output", Path.Combine(temporaryDirectory.Path, "output.bin")]);
        }

        var enumerator = new CountingEmptyEnumerator();
        var transportFactory = new NeverOpenedTransportFactory();
        var connectionFactory = new PicoFlasherConnectionFactory(enumerator, transportFactory);
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        int exitCode = await CliCommandRouter.RunAsync(
            arguments,
            standardOutput,
            standardError,
            CancellationToken.None,
            connectionFactory);

        Assert.Equal((int)ExitCode.Usage, exitCode);
        AssertFailure(standardOutput, ExitCode.Usage, "invalid-command-arguments");
        Assert.NotEmpty(standardError.ToString());
        Assert.Equal(0, enumerator.EnumerateCallCount);
        Assert.Equal(0, transportFactory.OpenCallCount);
        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("pico")]
    [InlineData("emmc-probe")]
    [InlineData("emmc-read")]
    public async Task Help_exposes_only_flat_probe_and_read_for_emmc(string scope)
    {
        IReadOnlyList<string> arguments = scope switch
        {
            "root" => ["--help"],
            "pico" => ["pico", "--help"],
            _ => ["pico", scope, "--help"],
        };
        var enumerator = new CountingEmptyEnumerator();
        var transportFactory = new NeverOpenedTransportFactory();
        var connectionFactory = new PicoFlasherConnectionFactory(enumerator, transportFactory);
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        int exitCode = await CliCommandRouter.RunAsync(
            arguments,
            standardOutput,
            standardError,
            CancellationToken.None,
            connectionFactory);

        string help = standardOutput.ToString();
        Assert.Equal(0, exitCode);
        if (scope is "root" or "pico")
        {
            Assert.Contains("pico emmc-probe", help, StringComparison.Ordinal);
            Assert.Contains("pico emmc-read", help, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains($"Usage: jrunner pico {scope} ", help, StringComparison.Ordinal);
            Assert.Contains("--device", help, StringComparison.Ordinal);
            Assert.Contains("--serial", help, StringComparison.Ordinal);
            Assert.Contains("--timeout", help, StringComparison.Ordinal);
            Assert.Contains("--json", help, StringComparison.Ordinal);
            if (scope == "emmc-read")
            {
                Assert.Contains("--start-block", help, StringComparison.Ordinal);
                Assert.Contains("--blocks", help, StringComparison.Ordinal);
                Assert.Contains("--output", help, StringComparison.Ordinal);
                Assert.Contains("--force", help, StringComparison.Ordinal);
            }
        }

        Assert.DoesNotContain("pico emmc ", help, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("emmc-write", help, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("emmc-erase", help, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--start-sector", help, StringComparison.Ordinal);
        Assert.DoesNotContain("--sector-count", help, StringComparison.Ordinal);
        Assert.Equal(string.Empty, standardError.ToString());
        Assert.Equal(0, enumerator.EnumerateCallCount);
        Assert.Equal(0, transportFactory.OpenCallCount);
    }

    [Theory]
    [InlineData("probe")]
    [InlineData("read")]
    public async Task Legacy_nested_emmc_commands_are_rejected_before_device_enumeration(string operation)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var arguments = new List<string> { "pico", "emmc", operation, "--json" };
        if (operation == "read")
        {
            arguments.AddRange(
                ["--output", Path.Combine(temporaryDirectory.Path, "output.bin"), "--start-sector", "0", "--sector-count", "1"]);
        }

        var transport = new ScriptedTransport([]);
        var enumerator = new SingleEndpointEnumerator();
        var transportFactory = new ScriptedTransportFactory(transport);
        var connectionFactory = new PicoFlasherConnectionFactory(enumerator, transportFactory);
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        int exitCode = await CliCommandRouter.RunAsync(
            arguments,
            standardOutput,
            standardError,
            CancellationToken.None,
            connectionFactory);

        Assert.Equal((int)ExitCode.Usage, exitCode);
        AssertFailure(standardOutput, ExitCode.Usage, "unsupported-command");
        Assert.NotEmpty(standardError.ToString());
        Assert.Equal(0, enumerator.EnumerateCallCount);
        Assert.Equal(0, transportFactory.OpenCallCount);
        AssertFrames(transport, []);
        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path));
    }

    [Theory]
    [InlineData("--start-sector", "0")]
    [InlineData("--sector-count", "1")]
    public async Task Legacy_emmc_range_options_are_rejected_before_device_enumeration(
        string option,
        string value)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var transport = new ScriptedTransport([]);
        var enumerator = new SingleEndpointEnumerator();
        var transportFactory = new ScriptedTransportFactory(transport);
        var connectionFactory = new PicoFlasherConnectionFactory(enumerator, transportFactory);
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        int exitCode = await CliCommandRouter.RunAsync(
            [
                "pico", "emmc-read",
                "--output", Path.Combine(temporaryDirectory.Path, "output.bin"),
                "--blocks", "1",
                option, value,
                "--json",
            ],
            standardOutput,
            standardError,
            CancellationToken.None,
            connectionFactory);

        Assert.Equal((int)ExitCode.Usage, exitCode);
        AssertFailure(standardOutput, ExitCode.Usage, "invalid-command-arguments");
        Assert.NotEmpty(standardError.ToString());
        Assert.Equal(0, enumerator.EnumerateCallCount);
        Assert.Equal(0, transportFactory.OpenCallCount);
        AssertFrames(transport, []);
        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path));
    }

    [Theory]
    [InlineData("emmc-write", false)]
    [InlineData("emmc-erase", false)]
    [InlineData("write", true)]
    [InlineData("erase", true)]
    public async Task Emmc_destructive_commands_are_absent_and_emit_no_write_opcode(
        string command,
        bool nested)
    {
        IReadOnlyList<string> arguments = nested
            ? ["pico", "emmc", command, "--json"]
            : ["pico", command, "--json"];
        var transport = new ScriptedTransport([]);
        var enumerator = new SingleEndpointEnumerator();
        var transportFactory = new ScriptedTransportFactory(transport);
        var connectionFactory = new PicoFlasherConnectionFactory(enumerator, transportFactory);
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        int exitCode = await CliCommandRouter.RunAsync(
            arguments,
            standardOutput,
            standardError,
            CancellationToken.None,
            connectionFactory);

        Assert.Equal((int)ExitCode.Usage, exitCode);
        AssertFailure(standardOutput, ExitCode.Usage, "unsupported-command");
        Assert.NotEmpty(standardError.ToString());
        Assert.Equal(0, enumerator.EnumerateCallCount);
        Assert.Equal(0, transportFactory.OpenCallCount);
        AssertFrames(transport, []);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Emmc_probe_reads_exact_metadata_and_renders_result(bool json)
    {
        var transport = new ScriptedTransport(
            Combine(Status(PicoFlasherProtocol.MinimumSupportedFirmwareVersion), EmmcMetadataResponses()));
        var enumerator = new SingleEndpointEnumerator();
        var transportFactory = new ScriptedTransportFactory(transport);
        var connectionFactory = new PicoFlasherConnectionFactory(enumerator, transportFactory);
        var arguments = new List<string>
        {
            "pico", "emmc-probe",
            "--serial", TestSerialNumber,
            "--timeout", "7",
        };
        if (json)
        {
            arguments.Add("--json");
        }

        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        int exitCode = await CliCommandRouter.RunAsync(
            arguments,
            standardOutput,
            standardError,
            CancellationToken.None,
            connectionFactory);

        Assert.Equal(0, exitCode);
        Assert.True(transport.Disposed);
        Assert.Equal(1, enumerator.EnumerateCallCount);
        Assert.Equal(1, transportFactory.OpenCallCount);
        Assert.Equal(TestDevicePath, transportFactory.OpenedEndpoint?.DevicePath);
        Assert.Equal(TimeSpan.FromSeconds(7), transportFactory.OpenedNoProgressTimeout);
        AssertFrames(transport, [.. MetadataCommands(), Command(PicoFlasherCommand.StartSmc, 0)]);
        Assert.Equal<int>(MetadataReadSizes(), transport.ReadRequests);
        Assert.Equal(0, transport.RemainingResponseByteCount);
        if (json)
        {
            using JsonDocument document = JsonDocument.Parse(standardOutput.ToString());
            JsonElement envelope = document.RootElement;
            Assert.Equal(1, envelope.GetProperty("schemaVersion").GetInt32());
            Assert.True(envelope.GetProperty("ok").GetBoolean());
            JsonElement result = envelope.GetProperty("result");
            Assert.Equal(PicoFlasherProtocol.MinimumSupportedFirmwareVersion, result.GetProperty("firmwareVersion").GetUInt32());
            Assert.Equal(TestDevicePath, result.GetProperty("devicePath").GetString());
            Assert.Equal(TestSerialNumber, result.GetProperty("serialNumber").GetString());
            Assert.Equal(TestFlashConfiguration, result.GetProperty("flashConfiguration").GetUInt32());
            Assert.Equal(PicoFlasherProtocol.EmmcLegacyReadWindowSectorCount, result.GetProperty("capacitySectorCount").GetUInt32());
            Assert.Equal(CreateBytes(PicoFlasherProtocol.EmmcCidSize, 0x14), result.GetProperty("cid").GetBytesFromBase64());
            Assert.Equal(CreateBytes(PicoFlasherProtocol.EmmcCsdSize, 0x48), result.GetProperty("csd").GetBytesFromBase64());
            Assert.Equal(CreateExtendedCsd(), result.GetProperty("extendedCsd").GetBytesFromBase64());
        }
        else
        {
            Assert.Equal(
                $"PicoFlasher eMMC probe completed: firmware v{PicoFlasherProtocol.MinimumSupportedFirmwareVersion}, " +
                $"{PicoFlasherProtocol.EmmcLegacyReadWindowSectorCount} 512-byte sectors, " +
                $"flash configuration 0x{TestFlashConfiguration:X8}.{Environment.NewLine}",
                standardOutput.ToString());
        }

        Assert.Equal(string.Empty, standardError.ToString());
    }

    [Theory]
    [InlineData(0U, true, false)]
    [InlineData(0U, false, true)]
    [InlineData(7U, true, true)]
    [InlineData(7U, false, false)]
    public async Task Emmc_read_publishes_exact_blocks_atomically_and_renders_result(
        uint startBlock,
        bool json,
        bool replaceExisting)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string outputPath = Path.Combine(temporaryDirectory.Path, "output.bin");
        byte[] original = CreateBytes(23, 0x67);
        if (replaceExisting)
        {
            await File.WriteAllBytesAsync(outputPath, original);
        }

        byte[] firstBlock = CreateBytes(PicoFlasherProtocol.EmmcSectorSize, 0xA1);
        byte[] secondBlock = CreateBytes(PicoFlasherProtocol.EmmcSectorSize, 0xD3);
        bool observedBeforePublication = false;
        var transport = new ScriptedTransport(
            Combine(
                Status(PicoFlasherProtocol.MinimumSupportedFirmwareVersion),
                EmmcMetadataResponses(),
                Status(0),
                firstBlock,
                Status(0),
                secondBlock),
            frame =>
            {
                if (frame[0] != (byte)PicoFlasherCommand.StartSmc)
                {
                    return;
                }

                observedBeforePublication = true;
                if (replaceExisting)
                {
                    Assert.Equal<byte>(original, File.ReadAllBytes(outputPath));
                }
                else
                {
                    Assert.False(File.Exists(outputPath));
                }

                Assert.Equal(replaceExisting ? 2 : 1, Directory.EnumerateFiles(temporaryDirectory.Path).Count());
            });
        var enumerator = new SingleEndpointEnumerator();
        var transportFactory = new ScriptedTransportFactory(transport);
        var connectionFactory = new PicoFlasherConnectionFactory(enumerator, transportFactory);
        var arguments = new List<string>
        {
            "pico", "emmc-read",
            "--device", TestDevicePath,
            "--output", outputPath,
            "--blocks", "2",
        };
        if (startBlock != 0)
        {
            arguments.AddRange(["--start-block", startBlock.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }

        if (replaceExisting)
        {
            arguments.Add("--force");
        }

        if (json)
        {
            arguments.Add("--json");
        }

        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        int exitCode = await CliCommandRouter.RunAsync(
            arguments,
            standardOutput,
            standardError,
            CancellationToken.None,
            connectionFactory);

        Assert.Equal(0, exitCode);
        Assert.True(observedBeforePublication);
        Assert.Equal<byte>(Combine(firstBlock, secondBlock), await File.ReadAllBytesAsync(outputPath));
        Assert.Equal(outputPath, Assert.Single(Directory.EnumerateFiles(temporaryDirectory.Path)));
        Assert.True(transport.Disposed);
        Assert.Equal(1, enumerator.EnumerateCallCount);
        Assert.Equal(1, transportFactory.OpenCallCount);
        Assert.Equal(TestDevicePath, transportFactory.OpenedEndpoint?.DevicePath);
        Assert.Equal(PicoFlasherProtocol.DefaultNoProgressTimeout, transportFactory.OpenedNoProgressTimeout);
        var expectedFrames = new List<byte[]>(MetadataCommands());
        if (startBlock == 0)
        {
            expectedFrames.Add(Command(PicoFlasherCommand.EmmcReadStream, 2));
        }
        else
        {
            expectedFrames.Add(Command(PicoFlasherCommand.EmmcRead, startBlock));
            expectedFrames.Add(Command(PicoFlasherCommand.EmmcRead, startBlock + 1));
        }

        expectedFrames.Add(Command(PicoFlasherCommand.StartSmc, 0));
        AssertFrames(transport, expectedFrames);
        Assert.Equal<int>(
            [.. MetadataReadSizes(), PicoFlasherProtocol.StatusSize, PicoFlasherProtocol.EmmcSectorSize,
                PicoFlasherProtocol.StatusSize, PicoFlasherProtocol.EmmcSectorSize],
            transport.ReadRequests);
        Assert.Equal(0, transport.RemainingResponseByteCount);
        if (json)
        {
            using JsonDocument document = JsonDocument.Parse(standardOutput.ToString());
            JsonElement envelope = document.RootElement;
            Assert.Equal(1, envelope.GetProperty("schemaVersion").GetInt32());
            Assert.True(envelope.GetProperty("ok").GetBoolean());
            JsonElement result = envelope.GetProperty("result");
            Assert.Equal(TestFlashConfiguration, result.GetProperty("flashConfiguration").GetUInt32());
            Assert.Equal(PicoFlasherProtocol.EmmcLegacyReadWindowSectorCount, result.GetProperty("capacitySectorCount").GetUInt32());
            Assert.Equal(startBlock, result.GetProperty("startSector").GetUInt32());
            Assert.Equal(2U, result.GetProperty("sectorCount").GetUInt32());
            Assert.Equal(2L * PicoFlasherProtocol.EmmcSectorSize, result.GetProperty("logicalByteLength").GetInt64());
        }
        else
        {
            Assert.Equal(
                $"PicoFlasher eMMC read completed: {2 * PicoFlasherProtocol.EmmcSectorSize} bytes from 2 sectors.{Environment.NewLine}",
                standardOutput.ToString());
        }

        Assert.NotEmpty(standardError.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Emmc_read_failure_does_not_publish_partial_blocks_or_leave_temporary_files(bool replaceExisting)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string outputPath = Path.Combine(temporaryDirectory.Path, "output.bin");
        byte[] original = CreateBytes(23, 0x67);
        if (replaceExisting)
        {
            await File.WriteAllBytesAsync(outputPath, original);
        }

        var transport = new ScriptedTransport(
            Combine(
                Status(PicoFlasherProtocol.MinimumSupportedFirmwareVersion),
                EmmcMetadataResponses(),
                Status(0),
                CreateBytes(PicoFlasherProtocol.EmmcSectorSize, 0xA1),
                Status(1)));
        var enumerator = new SingleEndpointEnumerator();
        var transportFactory = new ScriptedTransportFactory(transport);
        var connectionFactory = new PicoFlasherConnectionFactory(enumerator, transportFactory);
        var arguments = new List<string>
        {
            "pico", "emmc-read",
            "--output", outputPath,
            "--start-block", "7",
            "--blocks", "2",
            "--json",
        };
        if (replaceExisting)
        {
            arguments.Add("--force");
        }

        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        int exitCode = await CliCommandRouter.RunAsync(
            arguments,
            standardOutput,
            standardError,
            CancellationToken.None,
            connectionFactory);

        Assert.Equal((int)ExitCode.DeviceUnavailable, exitCode);
        AssertFailure(standardOutput, ExitCode.DeviceUnavailable, "pico-emmc-read-failed");
        Assert.NotEmpty(standardError.ToString());
        Assert.True(transport.Disposed);
        Assert.Equal(1, enumerator.EnumerateCallCount);
        Assert.Equal(1, transportFactory.OpenCallCount);
        AssertFrames(
            transport,
            [
                .. MetadataCommands(),
                Command(PicoFlasherCommand.EmmcRead, 7),
                Command(PicoFlasherCommand.EmmcRead, 8),
                Command(PicoFlasherCommand.StartSmc, 0),
            ]);
        Assert.Equal<int>(
            [.. MetadataReadSizes(), PicoFlasherProtocol.StatusSize, PicoFlasherProtocol.EmmcSectorSize, PicoFlasherProtocol.StatusSize],
            transport.ReadRequests);
        Assert.Equal(0, transport.RemainingResponseByteCount);
        if (replaceExisting)
        {
            Assert.Equal<byte>(original, await File.ReadAllBytesAsync(outputPath));
            Assert.Equal(outputPath, Assert.Single(Directory.EnumerateFiles(temporaryDirectory.Path)));
        }
        else
        {
            Assert.False(File.Exists(outputPath));
            Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path));
        }
    }

    [Fact]
    public async Task Emmc_read_requires_force_before_enumeration_for_an_existing_destination()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string outputPath = Path.Combine(temporaryDirectory.Path, "output.bin");
        byte[] original = CreateBytes(23, 0x67);
        await File.WriteAllBytesAsync(outputPath, original);
        var enumerator = new CountingEmptyEnumerator();
        var transportFactory = new NeverOpenedTransportFactory();
        var connectionFactory = new PicoFlasherConnectionFactory(enumerator, transportFactory);
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        int exitCode = await CliCommandRouter.RunAsync(
            ["pico", "emmc-read", "--output", outputPath, "--blocks", "1", "--json"],
            standardOutput,
            standardError,
            CancellationToken.None,
            connectionFactory);

        Assert.Equal((int)ExitCode.Usage, exitCode);
        AssertFailure(standardOutput, ExitCode.Usage, "destination-exists");
        Assert.NotEmpty(standardError.ToString());
        Assert.Equal(0, enumerator.EnumerateCallCount);
        Assert.Equal(0, transportFactory.OpenCallCount);
        Assert.Equal<byte>(original, await File.ReadAllBytesAsync(outputPath));
        Assert.Equal(outputPath, Assert.Single(Directory.EnumerateFiles(temporaryDirectory.Path)));
    }

    [Theory]
    [InlineData("emmc-probe", "--device", "/dev/ttyACM9")]
    [InlineData("emmc-probe", "--serial", "missing-serial")]
    [InlineData("emmc-read", "--device", "/dev/ttyACM9")]
    [InlineData("emmc-read", "--serial", "missing-serial")]
    public async Task Emmc_commands_honor_device_and_serial_selectors_before_opening(
        string command,
        string selector,
        string value)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var arguments = new List<string> { "pico", command, selector, value, "--json" };
        if (command == "emmc-read")
        {
            arguments.AddRange(["--output", Path.Combine(temporaryDirectory.Path, "output.bin"), "--blocks", "1"]);
        }

        var transport = new ScriptedTransport([]);
        var enumerator = new SingleEndpointEnumerator();
        var transportFactory = new ScriptedTransportFactory(transport);
        var connectionFactory = new PicoFlasherConnectionFactory(enumerator, transportFactory);
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        int exitCode = await CliCommandRouter.RunAsync(
            arguments,
            standardOutput,
            standardError,
            CancellationToken.None,
            connectionFactory);

        Assert.Equal((int)ExitCode.DeviceUnavailable, exitCode);
        AssertFailure(standardOutput, ExitCode.DeviceUnavailable, "pico-device-not-found");
        Assert.NotEmpty(standardError.ToString());
        Assert.Equal(1, enumerator.EnumerateCallCount);
        Assert.Equal(0, transportFactory.OpenCallCount);
        AssertFrames(transport, []);
        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path));
    }

    [Theory]
    [InlineData("emmc-probe", 1U)]
    [InlineData("emmc-probe", 2U)]
    [InlineData("emmc-probe", 3U)]
    [InlineData("emmc-read", 1U)]
    [InlineData("emmc-read", 2U)]
    [InlineData("emmc-read", 3U)]
    public async Task Emmc_commands_gate_firmware_one_through_three_before_metadata_or_smc_commands(
        string command,
        uint firmwareVersion)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var arguments = new List<string> { "pico", command, "--json" };
        if (command == "emmc-read")
        {
            arguments.AddRange(["--output", Path.Combine(temporaryDirectory.Path, "output.bin"), "--blocks", "1"]);
        }

        var transport = new ScriptedTransport(Status(firmwareVersion));
        var enumerator = new SingleEndpointEnumerator();
        var transportFactory = new ScriptedTransportFactory(transport);
        var connectionFactory = new PicoFlasherConnectionFactory(enumerator, transportFactory);
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        int exitCode = await CliCommandRouter.RunAsync(
            arguments,
            standardOutput,
            standardError,
            CancellationToken.None,
            connectionFactory);

        Assert.Equal((int)ExitCode.MissingPrerequisite, exitCode);
        AssertFailure(standardOutput, ExitCode.MissingPrerequisite, "pico-firmware-unsupported");
        Assert.NotEmpty(standardError.ToString());
        Assert.True(transport.Disposed);
        Assert.Equal(1, enumerator.EnumerateCallCount);
        Assert.Equal(1, transportFactory.OpenCallCount);
        AssertFrames(transport, [Command(PicoFlasherCommand.GetVersion, 0)]);
        Assert.Equal<int>([PicoFlasherProtocol.StatusSize], transport.ReadRequests);
        Assert.Equal(0, transport.RemainingResponseByteCount);
        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path));
    }

    private static void AssertFailure(StringWriter standardOutput, ExitCode expectedCode, string expectedKind)
    {
        using JsonDocument document = JsonDocument.Parse(standardOutput.ToString());
        JsonElement envelope = document.RootElement;
        Assert.Equal(1, envelope.GetProperty("schemaVersion").GetInt32());
        Assert.False(envelope.GetProperty("ok").GetBoolean());
        JsonElement error = envelope.GetProperty("error");
        Assert.Equal((int)expectedCode, error.GetProperty("code").GetInt32());
        Assert.Equal(expectedKind, error.GetProperty("kind").GetString());
    }

    private static void AssertFrames(ScriptedTransport transport, IReadOnlyList<byte[]> expectedFrames)
    {
        Assert.Equal(expectedFrames.Count, transport.WrittenFrames.Count);
        for (int index = 0; index < expectedFrames.Count; index++)
        {
            Assert.Equal(expectedFrames[index], transport.WrittenFrames[index]);
        }

        Assert.DoesNotContain(transport.WrittenFrames, static frame => frame[0] == 0x57);
    }

    private static byte[][] MetadataCommands()
    {
        return
        [
            Command(PicoFlasherCommand.GetVersion, 0),
            Command(PicoFlasherCommand.SetSmcWorkaround, 0),
            Command(PicoFlasherCommand.StopSmc, 0),
            Command(PicoFlasherCommand.GetFlashConfiguration, 0),
            Command(PicoFlasherCommand.EmmcDetect, 0),
            Command(PicoFlasherCommand.EmmcInitialize, 0),
            Command(PicoFlasherCommand.EmmcGetCid, 0),
            Command(PicoFlasherCommand.EmmcGetCsd, 0),
            Command(PicoFlasherCommand.EmmcGetExtendedCsd, 0),
        ];
    }

    private static int[] MetadataReadSizes()
    {
        return
        [
            PicoFlasherProtocol.StatusSize,
            PicoFlasherProtocol.StatusSize,
            1,
            PicoFlasherProtocol.StatusSize,
            PicoFlasherProtocol.EmmcCidSize,
            PicoFlasherProtocol.EmmcCsdSize,
            PicoFlasherProtocol.EmmcExtendedCsdSize,
        ];
    }

    private static byte[] EmmcMetadataResponses()
    {
        return Combine(
            Status(TestFlashConfiguration),
            [1],
            Status(0),
            CreateBytes(PicoFlasherProtocol.EmmcCidSize, 0x14),
            CreateBytes(PicoFlasherProtocol.EmmcCsdSize, 0x48),
            CreateExtendedCsd());
    }

    private static byte[] CreateExtendedCsd()
    {
        byte[] extendedCsd = CreateBytes(PicoFlasherProtocol.EmmcExtendedCsdSize, 0x7B);
        BinaryPrimitives.WriteUInt32LittleEndian(
            extendedCsd.AsSpan(
                PicoFlasherProtocol.EmmcExtendedCsdSectorCountOffset,
                PicoFlasherProtocol.StatusSize),
            PicoFlasherProtocol.EmmcLegacyReadWindowSectorCount);
        return extendedCsd;
    }

    private static byte[] Status(uint value)
    {
        byte[] status = new byte[PicoFlasherProtocol.StatusSize];
        BinaryPrimitives.WriteUInt32LittleEndian(status, value);
        return status;
    }

    private static byte[] Command(PicoFlasherCommand command, uint lba)
    {
        byte[] frame = new byte[PicoFlasherProtocol.CommandSize];
        PicoFlasherProtocol.WriteCommand(frame, command, lba);
        return frame;
    }

    private static byte[] CreateBytes(int length, byte seed)
    {
        byte[] bytes = new byte[length];
        for (int index = 0; index < bytes.Length; index++)
        {
            bytes[index] = unchecked((byte)(seed + index));
        }

        return bytes;
    }

    private static byte[] Combine(params byte[][] chunks)
    {
        int length = chunks.Sum(static chunk => chunk.Length);
        byte[] combined = new byte[length];
        int offset = 0;
        foreach (byte[] chunk in chunks)
        {
            chunk.CopyTo(combined, offset);
            offset += chunk.Length;
        }

        return combined;
    }

    private sealed class CountingEmptyEnumerator : IPicoFlasherDeviceEnumerator
    {
        internal int EnumerateCallCount { get; private set; }

        public ValueTask<IReadOnlyList<PicoFlasherDeviceEndpoint>> EnumerateAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnumerateCallCount++;
            return new ValueTask<IReadOnlyList<PicoFlasherDeviceEndpoint>>(
                Array.Empty<PicoFlasherDeviceEndpoint>());
        }
    }

    private sealed class SingleEndpointEnumerator : IPicoFlasherDeviceEnumerator
    {
        public int EnumerateCallCount { get; private set; }

        public ValueTask<IReadOnlyList<PicoFlasherDeviceEndpoint>> EnumerateAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnumerateCallCount++;
            return new ValueTask<IReadOnlyList<PicoFlasherDeviceEndpoint>>(
                [new PicoFlasherDeviceEndpoint(TestDevicePath, TestSerialNumber, interfaceNumber: 0)]);
        }
    }

    private sealed class NeverOpenedTransportFactory : IPicoFlasherTransportFactory
    {
        internal int OpenCallCount { get; private set; }

        public ValueTask<IPicoFlasherTransport> OpenAsync(
            PicoFlasherDeviceEndpoint endpoint,
            TimeSpan noProgressTimeout,
            CancellationToken cancellationToken = default)
        {
            OpenCallCount++;
            throw new InvalidOperationException("The command must not open a transport without an eligible endpoint.");
        }
    }

    private sealed class ScriptedTransportFactory : IPicoFlasherTransportFactory
    {
        private readonly ScriptedTransport _transport;

        internal ScriptedTransportFactory(ScriptedTransport transport)
        {
            _transport = transport;
        }

        internal int OpenCallCount { get; private set; }

        internal PicoFlasherDeviceEndpoint? OpenedEndpoint { get; private set; }

        internal TimeSpan OpenedNoProgressTimeout { get; private set; }

        public ValueTask<IPicoFlasherTransport> OpenAsync(
            PicoFlasherDeviceEndpoint endpoint,
            TimeSpan noProgressTimeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenCallCount++;
            OpenedEndpoint = endpoint;
            OpenedNoProgressTimeout = noProgressTimeout;
            return new ValueTask<IPicoFlasherTransport>(_transport);
        }
    }

    private sealed class ScriptedTransport : IPicoFlasherTransport
    {
        private readonly Queue<byte> _responses;
        private readonly Action<byte[]>? _writeObserver;

        internal ScriptedTransport(byte[] responseSequence, Action<byte[]>? writeObserver = null)
        {
            _responses = new Queue<byte>(responseSequence);
            _writeObserver = writeObserver;
        }

        internal List<byte[]> WrittenFrames { get; } = [];

        internal List<int> ReadRequests { get; } = [];

        internal int RemainingResponseByteCount => _responses.Count;

        internal bool Disposed { get; private set; }

        public ValueTask WriteAsync(
            ReadOnlyMemory<byte> source,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] frame = source.ToArray();
            WrittenFrames.Add(frame);
            _writeObserver?.Invoke(frame);
            return ValueTask.CompletedTask;
        }

        public ValueTask ReadExactlyAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadRequests.Add(destination.Length);
            if (_responses.Count < destination.Length)
            {
                throw new InvalidOperationException("The scripted transport has insufficient response bytes.");
            }

            for (int index = 0; index < destination.Length; index++)
            {
                destination.Span[index] = _responses.Dequeue();
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"jrunner-emmc-tests-{Guid.NewGuid():N}");
            if (OperatingSystem.IsLinux())
            {
                Directory.CreateDirectory(
                    Path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            else
            {
                Directory.CreateDirectory(Path);
            }
        }

        internal string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
