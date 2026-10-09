using System.Buffers.Binary;
using System.Text.Json;
using JRunner.Cli;
using JRunner.Cli.Infrastructure;
using JRunner.Core.Contracts;
using JRunner.Core.Devices.PicoFlasher;
using Xunit;

namespace JRunner.Cli.Tests;

public sealed class CliPicoNandCommandTests
{
    private const string TestDevicePath = "/dev/ttyACM0";
    private const string TestSerialNumber = "test-serial";
    private const uint KnownSmallBlockConfiguration = 0x0119_8010U;
    private const uint SmallBlockEraseRecordCount = 32;

    [Theory]
    [InlineData("write")]
    [InlineData("erase")]
    public async Task Destructive_commands_require_yes_before_device_enumeration(string operation)
    {
        var enumerator = new CountingEmptyEnumerator();
        var transportFactory = new NeverOpenedTransportFactory();
        var connectionFactory = new PicoFlasherConnectionFactory(enumerator, transportFactory);
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        int exitCode = await CliCommandRouter.RunAsync(
            BuildDestructiveArguments(operation, acknowledge: false, inputPath: "not-opened.bin"),
            standardOutput,
            standardError,
            CancellationToken.None,
            connectionFactory);

        Assert.Equal((int)ExitCode.Usage, exitCode);
        AssertFailure(standardOutput, ExitCode.Usage, "pico-nand-confirmation-required");
        Assert.NotEmpty(standardError.ToString());
        Assert.Equal(0, enumerator.EnumerateCallCount);
        Assert.Equal(0, transportFactory.OpenCallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Nand_write_missing_input_is_redacted_and_fails_before_any_device_access(
        bool jsonRequested)
    {
        const string inputSentinel = "PICO_INPUT_SECRET_2C1DA78F";
        using var temporaryDirectory = new TemporaryDirectory();
        string inputPath = Path.Combine(temporaryDirectory.Path, $"{inputSentinel}.bin");
        var arguments = new List<string>(BuildDestructiveArguments("write", acknowledge: true, inputPath));
        if (!jsonRequested)
        {
            arguments.Remove("--json");
        }

        var enumerator = new SingleEndpointEnumerator();
        var transportFactory = new NeverOpenedTransportFactory();
        var connectionFactory = new PicoFlasherConnectionFactory(enumerator, transportFactory);
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        int exitCode = await CliCommandRouter.RunAsync(
            arguments, standardOutput, standardError, CancellationToken.None, connectionFactory);

        Assert.Equal((int)ExitCode.InputOutput, exitCode);
        string message;
        if (jsonRequested)
        {
            AssertFailure(standardOutput, ExitCode.InputOutput, "io-error");
            using JsonDocument document = JsonDocument.Parse(standardOutput.ToString());
            message = Assert.IsType<string>(
                document.RootElement.GetProperty("error").GetProperty("message").GetString());
        }
        else
        {
            Assert.Equal(string.Empty, standardOutput.ToString());
            message = standardError.ToString().TrimEnd('\r', '\n');
        }

        Assert.Contains("--input", message, StringComparison.Ordinal);
        Assert.Contains("does not exist", message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(message + Environment.NewLine, standardError.ToString());
        foreach (string supplied in new[] { inputPath, inputSentinel, nameof(FileNotFoundException) })
        {
            Assert.DoesNotContain(supplied, standardOutput.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(supplied, standardError.ToString(), StringComparison.Ordinal);
        }

        Assert.Equal(0, enumerator.EnumerateCallCount);
        Assert.Equal(0, transportFactory.OpenCallCount);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temporaryDirectory.Path));
    }

    [Theory]
    [InlineData("write")]
    [InlineData("erase")]
    public async Task Acknowledged_destructive_commands_reach_device_selection(string operation)
    {
        string inputPath = Path.GetTempFileName();
        try
        {
            var enumerator = new CountingEmptyEnumerator();
            var transportFactory = new NeverOpenedTransportFactory();
            var connectionFactory = new PicoFlasherConnectionFactory(enumerator, transportFactory);
            using var standardOutput = new StringWriter();
            using var standardError = new StringWriter();

            int exitCode = await CliCommandRouter.RunAsync(
                BuildDestructiveArguments(operation, acknowledge: true, inputPath: inputPath),
                standardOutput,
                standardError,
                CancellationToken.None,
                connectionFactory);

            Assert.Equal((int)ExitCode.DeviceUnavailable, exitCode);
            AssertFailure(standardOutput, ExitCode.DeviceUnavailable, "pico-device-not-found");
            Assert.NotEmpty(standardError.ToString());
            Assert.Equal(1, enumerator.EnumerateCallCount);
            Assert.Equal(0, transportFactory.OpenCallCount);
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Theory]
    [InlineData("write")]
    [InlineData("erase")]
    public async Task Destructive_command_help_marks_yes_as_required(string operation)
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        int exitCode = await CliCommandRouter.RunAsync(
            ["pico", $"nand-{operation}", "--help"],
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("--yes", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("[--yes]", standardOutput.ToString(), StringComparison.Ordinal);
        if (string.Equals(operation, "write", StringComparison.Ordinal))
        {
            Assert.Contains("--start-block <record>", standardOutput.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("[--start-block", standardOutput.ToString(), StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("--start-erase-block", standardOutput.ToString(), StringComparison.Ordinal);
            Assert.Contains("--erase-blocks", standardOutput.ToString(), StringComparison.Ordinal);
        }
        Assert.Equal(string.Empty, standardError.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Nand_read_uses_fake_transport_protocol_and_publishes_raw_record(bool jsonRequested)
    {
        string outputPath = Path.Combine(Path.GetTempPath(), $"jrunner-nand-{Guid.NewGuid():N}.bin");
        byte[] record = CreateRecord(0xA1);
        var transport = new ScriptedTransport(
            Combine(
                Status(PicoFlasherProtocol.MinimumSupportedFirmwareVersion),
                Status(KnownSmallBlockConfiguration),
                Status(0),
                record));
        var enumerator = new SingleEndpointEnumerator();
        var transportFactory = new ScriptedTransportFactory(transport);
        var connectionFactory = new PicoFlasherConnectionFactory(enumerator, transportFactory);
        try
        {
            using var standardOutput = new StringWriter();
            using var standardError = new StringWriter();

            var arguments = new List<string>
            {
                "pico", "nand-read",
                "--output", outputPath,
                "--blocks", "1",
            };
            if (jsonRequested)
            {
                arguments.Add("--json");
            }

            int exitCode = await CliCommandRouter.RunAsync(
                arguments,
                standardOutput,
                standardError,
                CancellationToken.None,
                connectionFactory);

            Assert.Equal(0, exitCode);
            Assert.Equal<byte>(record, await File.ReadAllBytesAsync(outputPath));
            Assert.True(transport.Disposed);
            Assert.Equal(1, enumerator.EnumerateCallCount);
            Assert.Equal(1, transportFactory.OpenCallCount);
            Assert.Equal(TimeSpan.FromSeconds(10), transportFactory.LastNoProgressTimeout);
            Assert.Equal(0, transport.RemainingResponseByteCount);
            byte[][] expectedFrames =
            [
                Command(PicoFlasherCommand.GetVersion, 0),
                Command(PicoFlasherCommand.SetSmcWorkaround, 0),
                Command(PicoFlasherCommand.StopSmc, 0),
                Command(PicoFlasherCommand.GetFlashConfiguration, 0),
                Command(PicoFlasherCommand.ReadFlashStream, 1),
                Command(PicoFlasherCommand.StartSmc, 0),
            ];
            Assert.Equal(expectedFrames.Length, transport.WrittenFrames.Count);
            for (int index = 0; index < expectedFrames.Length; index++)
            {
                Assert.Equal(expectedFrames[index], transport.WrittenFrames[index]);
            }

            if (jsonRequested)
            {
                using JsonDocument document = JsonDocument.Parse(standardOutput.ToString());
                JsonElement envelope = document.RootElement;
                Assert.True(envelope.GetProperty("ok").GetBoolean());
                JsonElement result = envelope.GetProperty("result");
                AssertNandTransferResult(result, startRecord: 0, recordCount: 1);
            }
            else
            {
                Assert.Contains(
                    $"{PicoFlasherProtocol.NandDataSize} logical bytes",
                    standardOutput.ToString(),
                    StringComparison.Ordinal);
                Assert.Contains(
                    $"{PicoFlasherProtocol.NandWireRecordSize} raw bytes",
                    standardOutput.ToString(),
                    StringComparison.Ordinal);
                Assert.Contains("1 records", standardOutput.ToString(), StringComparison.Ordinal);
            }
            Assert.NotEmpty(standardError.ToString());
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("-1")]
    [InlineData("not-a-block")]
    public async Task Nand_write_requires_an_explicit_valid_start_block_before_opening(string? startBlock)
    {
        var arguments = new List<string>
        {
            "pico", "nand-write",
            "--input", "not-opened.bin",
            "--yes",
            "--json",
        };
        if (startBlock is not null)
        {
            arguments.Add("--start-block");
            arguments.Add(startBlock);
        }

        await AssertUsageBeforeOpeningAsync(arguments, "invalid-command-arguments");
    }

    [Theory]
    [InlineData("0", "pico-nand-range-invalid")]
    [InlineData("-1", "invalid-command-arguments")]
    [InlineData("not-a-count", "invalid-command-arguments")]
    public async Task Nand_read_requires_a_positive_explicit_blocks_count_before_opening(
        string blocks,
        string expectedKind)
    {
        await AssertUsageBeforeOpeningAsync(
            [
                "pico", "nand-read",
                "--output", "not-opened.bin",
                "--blocks", blocks,
                "--json",
            ],
            expectedKind);
    }

    [Theory]
    [InlineData("--start-erase-block")]
    [InlineData("--erase-blocks")]
    public async Task Nand_erase_requires_both_erase_range_options_before_opening(string missingOption)
    {
        var arguments = new List<string> { "pico", "nand-erase", "--yes", "--json" };
        if (!string.Equals(missingOption, "--start-erase-block", StringComparison.Ordinal))
        {
            arguments.Add("--start-erase-block");
            arguments.Add("0");
        }

        if (!string.Equals(missingOption, "--erase-blocks", StringComparison.Ordinal))
        {
            arguments.Add("--erase-blocks");
            arguments.Add("1");
        }

        await AssertUsageBeforeOpeningAsync(arguments, "invalid-command-arguments");
    }

    [Theory]
    [InlineData("0", "pico-nand-range-invalid")]
    [InlineData("-1", "invalid-command-arguments")]
    [InlineData("not-a-count", "invalid-command-arguments")]
    public async Task Nand_erase_requires_a_positive_erase_blocks_count_before_opening(
        string eraseBlocks,
        string expectedKind)
    {
        await AssertUsageBeforeOpeningAsync(
            [
                "pico", "nand-erase",
                "--start-erase-block", "0",
                "--erase-blocks", eraseBlocks,
                "--yes",
                "--json",
            ],
            expectedKind);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("erase")]
    public async Task Legacy_nested_nand_commands_are_rejected_before_opening(string operation)
    {
        List<string> arguments = BuildNandArgumentsForRejection(operation);
        arguments[1] = "nand";
        arguments.Insert(2, operation);

        await AssertUsageBeforeOpeningAsync(arguments, "unsupported-command");
    }

    [Theory]
    [InlineData("read", "--start-record", "0")]
    [InlineData("read", "--record-count", "1")]
    [InlineData("write", "--start-record", "0")]
    [InlineData("write", "--record-count", "32")]
    [InlineData("erase", "--start-record", "0")]
    [InlineData("erase", "--record-count", "32")]
    public async Task Legacy_nand_range_options_are_rejected_before_opening(
        string operation,
        string legacyOption,
        string value)
    {
        List<string> arguments = BuildNandArgumentsForRejection(operation);
        arguments.Add(legacyOption);
        arguments.Add(value);

        await AssertUsageBeforeOpeningAsync(arguments, "invalid-command-arguments");
    }

    [Theory]
    [InlineData(7U, true, "--serial", TestSerialNumber)]
    [InlineData(32766U, false, "--device", TestDevicePath)]
    public async Task Nand_read_maps_start_block_and_optional_blocks_to_record_lbas(
        uint startBlock,
        bool explicitBlocks,
        string selectorOption,
        string selectorValue)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string outputPath = Path.Combine(temporaryDirectory.Path, "output.bin");
        byte[] firstRecord = CreateRecord(0x63);
        byte[] secondRecord = CreateRecord(0x87);
        var transport = new ScriptedTransport(
            Combine(
                Status(PicoFlasherProtocol.MinimumSupportedFirmwareVersion),
                Status(KnownSmallBlockConfiguration),
                Status(0),
                firstRecord,
                Status(0),
                secondRecord));
        var enumerator = new SingleEndpointEnumerator();
        var transportFactory = new ScriptedTransportFactory(transport);
        var connectionFactory = new PicoFlasherConnectionFactory(enumerator, transportFactory);
        var arguments = new List<string>
        {
            "pico", "nand-read",
            "--output", outputPath,
            "--start-block", startBlock.ToString(System.Globalization.CultureInfo.InvariantCulture),
            selectorOption, selectorValue,
            "--timeout", "3",
            "--json",
        };
        if (explicitBlocks)
        {
            arguments.Add("--blocks");
            arguments.Add("2");
        }

        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        int exitCode = await CliCommandRouter.RunAsync(
            arguments,
            standardOutput,
            standardError,
            CancellationToken.None,
            connectionFactory);

        Assert.Equal((int)ExitCode.Success, exitCode);
        Assert.Equal<byte>(Combine(firstRecord, secondRecord), await File.ReadAllBytesAsync(outputPath));
        Assert.True(transport.Disposed);
        Assert.Equal(0, transport.RemainingResponseByteCount);
        Assert.Equal(1, enumerator.EnumerateCallCount);
        Assert.Equal(1, transportFactory.OpenCallCount);
        Assert.Equal(TimeSpan.FromSeconds(3), transportFactory.LastNoProgressTimeout);
        PicoFlasherDeviceEndpoint endpoint = Assert.IsType<PicoFlasherDeviceEndpoint>(transportFactory.LastEndpoint);
        Assert.Equal(TestDevicePath, endpoint.DevicePath);
        Assert.Equal(TestSerialNumber, endpoint.SerialNumber);
        AssertFrames(
            transport,
            [
                Command(PicoFlasherCommand.GetVersion, 0),
                Command(PicoFlasherCommand.SetSmcWorkaround, 0),
                Command(PicoFlasherCommand.StopSmc, 0),
                Command(PicoFlasherCommand.GetFlashConfiguration, 0),
                Command(PicoFlasherCommand.ReadFlash, startBlock),
                Command(PicoFlasherCommand.ReadFlash, startBlock + 1),
                Command(PicoFlasherCommand.StartSmc, 0),
            ]);
        using JsonDocument document = JsonDocument.Parse(standardOutput.ToString());
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        AssertNandTransferResult(
            document.RootElement.GetProperty("result"),
            startRecord: startBlock,
            recordCount: 2);
        Assert.Equal(new[] { outputPath }, Directory.GetFiles(temporaryDirectory.Path));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Nand_write_sends_a_full_erase_unit_relying_on_firmware_automatic_erase(bool jsonRequested)
    {
        const uint startBlock = 64;
        using var temporaryDirectory = new TemporaryDirectory();
        string inputPath = Path.Combine(temporaryDirectory.Path, "input.bin");
        byte[] rawRecords = CreateRawRecords(SmallBlockEraseRecordCount);
        await File.WriteAllBytesAsync(inputPath, rawRecords);
        var transport = new ScriptedTransport(
            Combine(
                Status(PicoFlasherProtocol.MinimumSupportedFirmwareVersion),
                Status(KnownSmallBlockConfiguration),
                new byte[checked((int)SmallBlockEraseRecordCount * PicoFlasherProtocol.StatusSize)]));
        var enumerator = new SingleEndpointEnumerator();
        var transportFactory = new ScriptedTransportFactory(transport);
        var connectionFactory = new PicoFlasherConnectionFactory(enumerator, transportFactory);
        var arguments = new List<string>
        {
            "pico", "nand-write",
            "--input", inputPath,
            "--start-block", "64",
            "--yes",
        };
        if (jsonRequested)
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

        Assert.Equal((int)ExitCode.Success, exitCode);
        Assert.True(transport.Disposed);
        Assert.Equal(0, transport.RemainingResponseByteCount);
        Assert.Equal(1, enumerator.EnumerateCallCount);
        Assert.Equal(1, transportFactory.OpenCallCount);
        Assert.Equal(TimeSpan.FromSeconds(10), transportFactory.LastNoProgressTimeout);
        var expectedFrames = new List<byte[]>
        {
            Command(PicoFlasherCommand.GetVersion, 0),
            Command(PicoFlasherCommand.SetSmcWorkaround, 0),
            Command(PicoFlasherCommand.StopSmc, 0),
            Command(PicoFlasherCommand.GetFlashConfiguration, 0),
        };
        for (uint offset = 0; offset < SmallBlockEraseRecordCount; offset++)
        {
            expectedFrames.Add(
                WriteRecordFrame(
                    startBlock + offset,
                    rawRecords.AsSpan(
                        checked((int)offset * PicoFlasherProtocol.NandWireRecordSize),
                        PicoFlasherProtocol.NandWireRecordSize)));
        }

        expectedFrames.Add(Command(PicoFlasherCommand.StartSmc, 0));
        AssertFrames(transport, expectedFrames);
        Assert.DoesNotContain(
            transport.WrittenFrames,
            static frame => frame[0] == (byte)PicoFlasherCommand.EraseFlash);
        if (jsonRequested)
        {
            using JsonDocument document = JsonDocument.Parse(standardOutput.ToString());
            Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
            AssertNandTransferResult(
                document.RootElement.GetProperty("result"),
                startRecord: startBlock,
                recordCount: SmallBlockEraseRecordCount);
        }
        else
        {
            Assert.Contains(
                $"{SmallBlockEraseRecordCount * PicoFlasherProtocol.NandDataSize} logical bytes",
                standardOutput.ToString(),
                StringComparison.Ordinal);
            Assert.Contains(
                $"{rawRecords.Length} raw bytes",
                standardOutput.ToString(),
                StringComparison.Ordinal);
            Assert.Contains(
                $"{SmallBlockEraseRecordCount} records",
                standardOutput.ToString(),
                StringComparison.Ordinal);
        }

        Assert.NotEmpty(standardError.ToString());
        Assert.Equal<byte>(rawRecords, await File.ReadAllBytesAsync(inputPath));
    }

    [Theory]
    [InlineData(KnownSmallBlockConfiguration, 32U, true)]
    [InlineData(KnownSmallBlockConfiguration, 32U, false)]
    [InlineData(0x008A_3020U, 256U, true)]
    [InlineData(0x008A_3020U, 256U, false)]
    public async Task Nand_erase_maps_erase_block_indices_to_lbas_and_reports_inclusive_ranges(
        uint flashConfiguration,
        uint eraseBlockRecordCount,
        bool jsonRequested)
    {
        const uint startEraseBlock = 3;
        const uint eraseBlockCount = 2;
        uint startRecord = startEraseBlock * eraseBlockRecordCount;
        uint recordCount = eraseBlockCount * eraseBlockRecordCount;
        uint endRecordInclusive = startRecord + recordCount - 1;
        long startLogicalByteOffset = (long)startRecord * PicoFlasherProtocol.NandDataSize;
        long logicalByteLength = (long)recordCount * PicoFlasherProtocol.NandDataSize;
        long endLogicalByteOffsetInclusive = startLogicalByteOffset + logicalByteLength - 1;
        long startRawByteOffset = (long)startRecord * PicoFlasherProtocol.NandWireRecordSize;
        long rawByteLength = (long)recordCount * PicoFlasherProtocol.NandWireRecordSize;
        var transport = new ScriptedTransport(
            Combine(
                Status(PicoFlasherProtocol.MinimumSupportedFirmwareVersion),
                Status(flashConfiguration),
                Status(0),
                Status(0)));
        var enumerator = new SingleEndpointEnumerator();
        var transportFactory = new ScriptedTransportFactory(transport);
        var connectionFactory = new PicoFlasherConnectionFactory(enumerator, transportFactory);
        var arguments = new List<string>
        {
            "pico", "nand-erase",
            "--start-erase-block", "3",
            "--erase-blocks", "2",
            "--yes",
        };
        if (jsonRequested)
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

        Assert.Equal((int)ExitCode.Success, exitCode);
        Assert.True(transport.Disposed);
        Assert.Equal(0, transport.RemainingResponseByteCount);
        Assert.Equal(1, enumerator.EnumerateCallCount);
        Assert.Equal(1, transportFactory.OpenCallCount);
        AssertFrames(
            transport,
            [
                Command(PicoFlasherCommand.GetVersion, 0),
                Command(PicoFlasherCommand.SetSmcWorkaround, 0),
                Command(PicoFlasherCommand.StopSmc, 0),
                Command(PicoFlasherCommand.GetFlashConfiguration, 0),
                Command(PicoFlasherCommand.EraseFlash, startRecord),
                Command(PicoFlasherCommand.EraseFlash, startRecord + eraseBlockRecordCount),
                Command(PicoFlasherCommand.StartSmc, 0),
            ]);
        if (jsonRequested)
        {
            using JsonDocument document = JsonDocument.Parse(standardOutput.ToString());
            Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
            JsonElement result = document.RootElement.GetProperty("result");
            Assert.Equal(flashConfiguration, result.GetProperty("flashConfiguration").GetUInt32());
            Assert.Equal(startEraseBlock, result.GetProperty("startEraseBlock").GetUInt32());
            Assert.Equal(startEraseBlock + eraseBlockCount - 1, result.GetProperty("endEraseBlockInclusive").GetUInt32());
            Assert.Equal(eraseBlockCount, result.GetProperty("eraseBlockCount").GetUInt32());
            Assert.Equal(eraseBlockRecordCount, result.GetProperty("eraseBlockRecordCount").GetUInt32());
            Assert.Equal(startRecord, result.GetProperty("startRecord").GetUInt32());
            Assert.Equal(endRecordInclusive, result.GetProperty("endRecordInclusive").GetUInt32());
            Assert.Equal(recordCount, result.GetProperty("recordCount").GetUInt32());
            Assert.Equal(startLogicalByteOffset, result.GetProperty("startLogicalByteOffset").GetInt64());
            Assert.Equal(endLogicalByteOffsetInclusive, result.GetProperty("endLogicalByteOffsetInclusive").GetInt64());
            Assert.Equal(logicalByteLength, result.GetProperty("logicalByteLength").GetInt64());
            Assert.Equal(startRawByteOffset, result.GetProperty("startRawByteOffset").GetInt64());
            Assert.Equal(startRawByteOffset + rawByteLength - 1, result.GetProperty("endRawByteOffsetInclusive").GetInt64());
            Assert.Equal(rawByteLength, result.GetProperty("rawByteLength").GetInt64());
        }
        else
        {
            Assert.Contains("2 erase blocks", standardOutput.ToString(), StringComparison.Ordinal);
            Assert.Contains(
                $"logical records {startRecord}-{endRecordInclusive}",
                standardOutput.ToString(),
                StringComparison.Ordinal);
            Assert.Contains(
                $"logical bytes {startLogicalByteOffset}-{endLogicalByteOffsetInclusive}",
                standardOutput.ToString(),
                StringComparison.Ordinal);
            Assert.Contains(
                $"{logicalByteLength} logical bytes",
                standardOutput.ToString(),
                StringComparison.Ordinal);
            Assert.Contains(
                $"{rawByteLength} raw bytes",
                standardOutput.ToString(),
                StringComparison.Ordinal);
        }

        Assert.NotEmpty(standardError.ToString());
    }

    [Fact]
    public async Task Nand_read_existing_destination_requires_force_before_opening()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string outputPath = Path.Combine(temporaryDirectory.Path, "output.bin");
        byte[] original = [0x12, 0x34, 0x56];
        await File.WriteAllBytesAsync(outputPath, original);

        await AssertUsageBeforeOpeningAsync(BuildReadArguments(outputPath), "destination-exists");

        Assert.Equal<byte>(original, await File.ReadAllBytesAsync(outputPath));
        Assert.Equal(new[] { outputPath }, Directory.GetFiles(temporaryDirectory.Path));
    }

    [Fact]
    public async Task Nand_read_force_replaces_the_destination_only_after_successful_transfer()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string outputPath = Path.Combine(temporaryDirectory.Path, "output.bin");
        byte[] original = [0x12, 0x34, 0x56];
        byte[] record = CreateRecord(0xB3);
        await File.WriteAllBytesAsync(outputPath, original);
        var transport = new ScriptedTransport(
            Combine(
                Status(PicoFlasherProtocol.MinimumSupportedFirmwareVersion),
                Status(KnownSmallBlockConfiguration),
                Status(0),
                record),
            writeObserved: _ => Assert.Equal<byte>(original, File.ReadAllBytes(outputPath)));
        var connectionFactory = new PicoFlasherConnectionFactory(
            new SingleEndpointEnumerator(),
            new ScriptedTransportFactory(transport));
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        int exitCode = await CliCommandRouter.RunAsync(
            BuildReadArguments(outputPath, force: true),
            standardOutput,
            standardError,
            CancellationToken.None,
            connectionFactory);

        Assert.Equal((int)ExitCode.Success, exitCode);
        Assert.Equal<byte>(record, await File.ReadAllBytesAsync(outputPath));
        Assert.True(transport.Disposed);
        Assert.Equal(0, transport.RemainingResponseByteCount);
        using JsonDocument document = JsonDocument.Parse(standardOutput.ToString());
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(new[] { outputPath }, Directory.GetFiles(temporaryDirectory.Path));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Nand_read_failure_removes_partial_output_without_publishing(bool destinationExists)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string outputPath = Path.Combine(temporaryDirectory.Path, "output.bin");
        byte[] original = [0x12, 0x34, 0x56];
        if (destinationExists)
        {
            await File.WriteAllBytesAsync(outputPath, original);
        }

        var transport = new ScriptedTransport(
            Combine(
                Status(PicoFlasherProtocol.MinimumSupportedFirmwareVersion),
                Status(KnownSmallBlockConfiguration),
                Status(0),
                CreateRecord(0xB3),
                Status(1)));
        var connectionFactory = new PicoFlasherConnectionFactory(
            new SingleEndpointEnumerator(),
            new ScriptedTransportFactory(transport));
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        int exitCode = await CliCommandRouter.RunAsync(
            [
                "pico", "nand-read",
                "--output", outputPath,
                "--start-block", "1",
                "--blocks", "2",
                "--force",
                "--json",
            ],
            standardOutput,
            standardError,
            CancellationToken.None,
            connectionFactory);

        Assert.Equal((int)ExitCode.DeviceUnavailable, exitCode);
        AssertFailure(standardOutput, ExitCode.DeviceUnavailable, "pico-nand-read-failed");
        Assert.True(transport.Disposed);
        Assert.Equal(0, transport.RemainingResponseByteCount);
        AssertFrames(
            transport,
            [
                Command(PicoFlasherCommand.GetVersion, 0),
                Command(PicoFlasherCommand.SetSmcWorkaround, 0),
                Command(PicoFlasherCommand.StopSmc, 0),
                Command(PicoFlasherCommand.GetFlashConfiguration, 0),
                Command(PicoFlasherCommand.ReadFlash, 1),
                Command(PicoFlasherCommand.ReadFlash, 2),
                Command(PicoFlasherCommand.StartSmc, 0),
            ]);
        if (destinationExists)
        {
            Assert.Equal<byte>(original, await File.ReadAllBytesAsync(outputPath));
            Assert.Equal(new[] { outputPath }, Directory.GetFiles(temporaryDirectory.Path));
        }
        else
        {
            Assert.False(File.Exists(outputPath));
            Assert.Empty(Directory.GetFiles(temporaryDirectory.Path));
        }
    }

    [Fact]
    public async Task Nand_read_publication_failure_preserves_a_competing_destination_and_cleans_temporary_output()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string outputPath = Path.Combine(temporaryDirectory.Path, "output.bin");
        var transport = new ScriptedTransport(
            Combine(
                Status(PicoFlasherProtocol.MinimumSupportedFirmwareVersion),
                Status(KnownSmallBlockConfiguration),
                Status(0),
                CreateRecord(0xB3)),
            writeObserved: frame =>
            {
                if (frame.Span[0] == (byte)PicoFlasherCommand.StartSmc)
                {
                    File.WriteAllText(outputPath, "competing-output");
                }
            });
        var connectionFactory = new PicoFlasherConnectionFactory(
            new SingleEndpointEnumerator(),
            new ScriptedTransportFactory(transport));
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        int exitCode = await CliCommandRouter.RunAsync(
            BuildReadArguments(outputPath),
            standardOutput,
            standardError,
            CancellationToken.None,
            connectionFactory);

        Assert.Equal((int)ExitCode.Usage, exitCode);
        AssertFailure(standardOutput, ExitCode.Usage, "destination-exists");
        Assert.Equal("competing-output", await File.ReadAllTextAsync(outputPath));
        Assert.True(transport.Disposed);
        Assert.Equal(0, transport.RemainingResponseByteCount);
        Assert.Equal(new[] { outputPath }, Directory.GetFiles(temporaryDirectory.Path));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Nand_read_committed_success_survives_cancellation_during_connection_disposal(bool jsonRequested)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var cancellationSource = new CancellationTokenSource();
        string outputPath = Path.Combine(temporaryDirectory.Path, "output.bin");
        byte[] record = CreateRecord(0xB3);
        var transport = new ScriptedTransport(
            Combine(
                Status(PicoFlasherProtocol.MinimumSupportedFirmwareVersion),
                Status(KnownSmallBlockConfiguration),
                Status(0),
                record),
            disposedObserved: () =>
            {
                Assert.True(File.Exists(outputPath));
                cancellationSource.Cancel();
            });
        var connectionFactory = new PicoFlasherConnectionFactory(
            new SingleEndpointEnumerator(),
            new ScriptedTransportFactory(transport));
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        int exitCode = await CliCommandRouter.RunAsync(
            BuildReadArguments(outputPath, jsonRequested: jsonRequested),
            standardOutput,
            standardError,
            cancellationSource.Token,
            connectionFactory);

        Assert.Equal((int)ExitCode.Success, exitCode);
        Assert.True(cancellationSource.IsCancellationRequested);
        Assert.True(transport.Disposed);
        Assert.Equal<byte>(record, await File.ReadAllBytesAsync(outputPath));
        Assert.Equal(new[] { outputPath }, Directory.GetFiles(temporaryDirectory.Path));
        if (jsonRequested)
        {
            using JsonDocument document = JsonDocument.Parse(standardOutput.ToString());
            Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
            AssertNandTransferResult(document.RootElement.GetProperty("result"), startRecord: 0, recordCount: 1);
        }
        else
        {
            Assert.Contains("logical bytes", standardOutput.ToString(), StringComparison.Ordinal);
            Assert.Contains("raw bytes", standardOutput.ToString(), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Nand_read_standard_output_failure_does_not_remove_committed_file_or_append_a_failure_envelope(
        bool jsonRequested)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string outputPath = Path.Combine(temporaryDirectory.Path, "output.bin");
        byte[] record = CreateRecord(0xB3);
        var transport = new ScriptedTransport(
            Combine(
                Status(PicoFlasherProtocol.MinimumSupportedFirmwareVersion),
                Status(KnownSmallBlockConfiguration),
                Status(0),
                record));
        var connectionFactory = new PicoFlasherConnectionFactory(
            new SingleEndpointEnumerator(),
            new ScriptedTransportFactory(transport));
        using var standardOutput = new FailingOutputWriter();
        using var standardError = new StringWriter();

        int exitCode = await CliCommandRouter.RunAsync(
            BuildReadArguments(outputPath, jsonRequested: jsonRequested),
            standardOutput,
            standardError,
            CancellationToken.None,
            connectionFactory);

        Assert.Equal((int)ExitCode.InputOutput, exitCode);
        Assert.Equal<byte>(record, await File.ReadAllBytesAsync(outputPath));
        Assert.True(transport.Disposed);
        Assert.Equal(1, standardOutput.WriteCallCount);
        Assert.Equal(1, standardOutput.ToString().Length);
        Assert.DoesNotContain("\"error\"", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.NotEmpty(standardError.ToString());
        Assert.Equal(new[] { outputPath }, Directory.GetFiles(temporaryDirectory.Path));
    }

    private static IReadOnlyList<string> BuildDestructiveArguments(
        string operation,
        bool acknowledge,
        string inputPath)
    {
        var arguments = new List<string> { "pico", $"nand-{operation}" };
        if (string.Equals(operation, "write", StringComparison.Ordinal))
        {
            arguments.Add("--input");
            arguments.Add(inputPath);
            arguments.Add("--start-block");
            arguments.Add("0");
        }
        else
        {
            arguments.Add("--start-erase-block");
            arguments.Add("0");
            arguments.Add("--erase-blocks");
            arguments.Add("1");
        }

        if (acknowledge)
        {
            arguments.Add("--yes");
        }

        arguments.Add("--json");
        return arguments;
    }

    private static List<string> BuildReadArguments(
        string outputPath,
        bool force = false,
        bool jsonRequested = true)
    {
        var arguments = new List<string>
        {
            "pico", "nand-read",
            "--output", outputPath,
            "--blocks", "1",
        };
        if (force)
        {
            arguments.Add("--force");
        }

        if (jsonRequested)
        {
            arguments.Add("--json");
        }

        return arguments;
    }

    private static List<string> BuildNandArgumentsForRejection(string operation)
    {
        return string.Equals(operation, "read", StringComparison.Ordinal)
            ? BuildReadArguments("not-opened.bin")
            : new List<string>(BuildDestructiveArguments(operation, acknowledge: true, inputPath: "not-opened.bin"));
    }

    private static async Task AssertUsageBeforeOpeningAsync(IReadOnlyList<string> arguments, string expectedKind)
    {
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
        AssertFailure(standardOutput, ExitCode.Usage, expectedKind);
        Assert.NotEmpty(standardError.ToString());
        Assert.Equal(0, enumerator.EnumerateCallCount);
        Assert.Equal(0, transportFactory.OpenCallCount);
    }

    private static void AssertNandTransferResult(JsonElement result, uint startRecord, uint recordCount)
    {
        Assert.Equal(KnownSmallBlockConfiguration, result.GetProperty("flashConfiguration").GetUInt32());
        Assert.Equal(startRecord, result.GetProperty("startRecord").GetUInt32());
        Assert.Equal(startRecord + recordCount - 1, result.GetProperty("endRecordInclusive").GetUInt32());
        Assert.Equal(recordCount, result.GetProperty("recordCount").GetUInt32());
        Assert.Equal(
            (long)recordCount * PicoFlasherProtocol.NandDataSize,
            result.GetProperty("logicalByteLength").GetInt64());
        Assert.Equal(
            (long)recordCount * PicoFlasherProtocol.NandWireRecordSize,
            result.GetProperty("rawByteLength").GetInt64());
    }

    private static byte[] CreateRawRecords(uint recordCount)
    {
        byte[] records = new byte[checked((int)recordCount * PicoFlasherProtocol.NandWireRecordSize)];
        for (uint offset = 0; offset < recordCount; offset++)
        {
            CreateRecord(unchecked((byte)(0x31 + offset))).CopyTo(
                records,
                checked((int)offset * PicoFlasherProtocol.NandWireRecordSize));
        }

        return records;
    }

    private static byte[] WriteRecordFrame(uint record, ReadOnlySpan<byte> rawRecord)
    {
        byte[] frame = new byte[PicoFlasherProtocol.CommandSize + PicoFlasherProtocol.NandWireRecordSize];
        PicoFlasherProtocol.WriteCommand(frame.AsSpan(0, PicoFlasherProtocol.CommandSize), PicoFlasherCommand.WriteFlash, record);
        rawRecord.CopyTo(frame.AsSpan(PicoFlasherProtocol.CommandSize));
        return frame;
    }

    private static void AssertFrames(ScriptedTransport transport, IReadOnlyList<byte[]> expectedFrames)
    {
        Assert.Equal(expectedFrames.Count, transport.WrittenFrames.Count);
        for (int index = 0; index < expectedFrames.Count; index++)
        {
            Assert.Equal(expectedFrames[index], transport.WrittenFrames[index]);
        }
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

    private static byte[] Status(uint value)
    {
        byte[] status = new byte[PicoFlasherProtocol.StatusSize];
        BinaryPrimitives.WriteUInt32LittleEndian(status, value);
        return status;
    }

    private static byte[] CreateRecord(byte seed)
    {
        byte[] record = new byte[PicoFlasherProtocol.NandWireRecordSize];
        for (int index = 0; index < record.Length; index++)
        {
            record[index] = unchecked((byte)(seed + index));
        }

        return record;
    }

    private static byte[] Command(PicoFlasherCommand command, uint lba)
    {
        byte[] frame = new byte[PicoFlasherProtocol.CommandSize];
        PicoFlasherProtocol.WriteCommand(frame, command, lba);
        return frame;
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

    private sealed class SingleEndpointEnumerator : IPicoFlasherDeviceEnumerator
    {
        internal int EnumerateCallCount { get; private set; }

        public ValueTask<IReadOnlyList<PicoFlasherDeviceEndpoint>> EnumerateAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnumerateCallCount++;
            return new ValueTask<IReadOnlyList<PicoFlasherDeviceEndpoint>>(
                [new PicoFlasherDeviceEndpoint(TestDevicePath, TestSerialNumber, interfaceNumber: 0)]);
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

        internal PicoFlasherDeviceEndpoint? LastEndpoint { get; private set; }

        internal TimeSpan? LastNoProgressTimeout { get; private set; }

        public ValueTask<IPicoFlasherTransport> OpenAsync(
            PicoFlasherDeviceEndpoint endpoint,
            TimeSpan noProgressTimeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenCallCount++;
            LastEndpoint = endpoint;
            LastNoProgressTimeout = noProgressTimeout;
            return new ValueTask<IPicoFlasherTransport>(_transport);
        }
    }

    private sealed class ScriptedTransport : IPicoFlasherTransport
    {
        private readonly Queue<byte> _responses;

        private readonly Action<ReadOnlyMemory<byte>>? _writeObserved;
        private readonly Action? _disposedObserved;
        internal ScriptedTransport(
            byte[] responseSequence,
            Action<ReadOnlyMemory<byte>>? writeObserved = null,
            Action? disposedObserved = null)
        {
            _responses = new Queue<byte>(responseSequence);
            _writeObserved = writeObserved;
            _disposedObserved = disposedObserved;
        }

        internal List<byte[]> WrittenFrames { get; } = [];

        internal int RemainingResponseByteCount => _responses.Count;

        internal bool Disposed { get; private set; }

        public ValueTask WriteAsync(
            ReadOnlyMemory<byte> source,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WrittenFrames.Add(source.ToArray());
            _writeObserved?.Invoke(source);
            return ValueTask.CompletedTask;
        }

        public ValueTask ReadExactlyAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            _disposedObserved?.Invoke();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingOutputWriter : StringWriter
    {
        internal int WriteCallCount { get; private set; }

        public override Task WriteAsync(string? value)
        {
            return FailWrite(value);
        }

        public override Task WriteLineAsync(string? value)
        {
            return FailWrite(value);
        }

        private Task FailWrite(string? value)
        {
            WriteCallCount++;
            if (!string.IsNullOrEmpty(value))
            {
                Write(value[0]);
            }

            return Task.FromException(new IOException("simulated command output failure"));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"jrunner-cli-nand-tests-{Guid.NewGuid():N}");
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
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
