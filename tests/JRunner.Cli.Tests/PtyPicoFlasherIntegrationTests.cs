using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using JRunner.Cli;
using JRunner.Cli.Infrastructure;
using JRunner.Core.Devices.PicoFlasher;
using Xunit;

namespace JRunner.Cli.Tests;

public sealed class PtyPicoFlasherIntegrationTests
{
    private const uint NandConfiguration = 0x0119_8010;
    private const uint EmmcConfiguration = 0xC046_2002;
    private const uint EmmcCapacity = 0x20000;
    private const string SerialNumber = "pty-pico";

    [PtyLinuxTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pico_probe_uses_fragmented_real_serial_responses_and_explicit_selection(bool selectBySerial)
    {
        await using var firmware = PtyPicoFirmware.Create();
        firmware.Start(ServeProbeAsync);

        CliResult run = await RunPicoAsync(firmware, CreateConnectionFactory(firmware), "probe", selectBySerial: selectBySerial);
        await firmware.CompleteAsync();

        AssertProbeResult(run, firmware.SlavePath);
        Assert.Equal(string.Empty, run.StandardError);
        AssertFrames(
            firmware,
            (PicoFlasherCommand.GetVersion, 0),
            (PicoFlasherCommand.SetSmcWorkaround, 0),
            (PicoFlasherCommand.StopSmc, 0),
            (PicoFlasherCommand.GetFlashConfiguration, 0),
            (PicoFlasherCommand.StartSmc, 0));
    }

    [PtyLinuxFact]
    public async Task Device_list_does_not_open_or_send_commands_to_the_real_serial_endpoint()
    {
        await using var firmware = PtyPicoFirmware.Create();
        byte[]? initialSettings = CaptureSlaveSettingsIfSupported(firmware);
        firmware.Start(static (terminal, token) => terminal.AssertQuietAsync(TimeSpan.FromMilliseconds(150), token));

        CliResult run = await RunAsync(firmware, CreateConnectionFactory(firmware), ["device", "list", "--json"]);
        await firmware.CompleteAsync();
        AssertSlaveSettingsUnchanged(firmware, initialSettings);

        JsonElement result = AssertSuccess(run);
        Assert.Equal(JsonValueKind.Array, result.ValueKind);
        Assert.Equal(1, result.GetArrayLength());
        Assert.Equal(firmware.SlavePath, result[0].GetProperty("devicePath").GetString());
        Assert.Equal(SerialNumber, result[0].GetProperty("serialNumber").GetString());
        Assert.Empty(firmware.Frames);
        Assert.Equal(string.Empty, run.StandardError);
    }

    [PtyLinuxTheory]
    [InlineData(1U)]
    [InlineData(2U)]
    [InlineData(3U)]
    public async Task Every_opening_command_rejects_old_firmware_after_only_get_version(uint version)
    {
        using var directory = new TemporaryDirectory();
        string input = Path.Combine(directory.Root, "input.bin");
        string output = Path.Combine(directory.Root, "output.bin");
        await File.WriteAllBytesAsync(input, Payload(32 * 528, 0x37));

        string[] commands =
        [
            "probe", "smc-stop", "smc-start", "reboot-bootloader",
            "nand-read", "nand-write", "nand-erase", "emmc-probe", "emmc-read",
        ];
        foreach (string command in commands)
        {
            await using var firmware = PtyPicoFirmware.Create();
            firmware.Start(async (terminal, token) =>
            {
                terminal.ReceiveCommand(PicoFlasherCommand.GetVersion, 0, token);
                AssertSerialLineCoding(terminal);
                await terminal.WriteUInt32Async(version, token);
                await terminal.AssertQuietAsync(TimeSpan.FromMilliseconds(100), token);
            });

            CliResult run = await RunPicoAsync(
                firmware,
                CreateConnectionFactory(firmware),
                command,
                OptionsForOpeningCommand(command, input, output));

            JsonElement error = AssertError(run, 4, "pico-firmware-unsupported");
            Assert.Contains("firmware", error.GetProperty("message").GetString()!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("4", error.GetProperty("message").GetString()!, StringComparison.Ordinal);
            await firmware.CompleteAsync();
            AssertFrames(firmware, (PicoFlasherCommand.GetVersion, 0));
            Assert.False(File.Exists(output));
            Assert.Single(Directory.GetFiles(directory.Root));
        }
    }

    [PtyLinuxTheory]
    [InlineData("smc-stop", PicoFlasherCommand.StopSmc)]
    [InlineData("smc-start", PicoFlasherCommand.StartSmc)]
    [InlineData("reboot-bootloader", PicoFlasherCommand.RebootToBootloader)]
    public async Task Explicit_controls_send_only_their_command_after_the_firmware_gate(
        string command,
        PicoFlasherCommand opcode)
    {
        await using var firmware = PtyPicoFirmware.Create();
        firmware.Start(async (terminal, token) =>
        {
            await ServeVersionAsync(terminal, token);
            terminal.ReceiveCommand(opcode, 0, token);
            await terminal.AssertQuietAsync(TimeSpan.FromMilliseconds(100), token);
        });

        CliResult run = await RunPicoAsync(firmware, CreateConnectionFactory(firmware), command);
        await firmware.CompleteAsync();

        JsonElement result = AssertSuccess(run);
        Assert.Equal(4U, result.GetProperty("firmwareVersion").GetUInt32());
        Assert.Equal(command, result.GetProperty("operation").GetString());
        Assert.Equal(firmware.SlavePath, result.GetProperty("devicePath").GetString());
        Assert.Equal(SerialNumber, result.GetProperty("serialNumber").GetString());
        Assert.Equal(string.Empty, run.StandardError);
        AssertFrames(firmware, (PicoFlasherCommand.GetVersion, 0), (opcode, 0));
    }

    [PtyLinuxTheory]
    [InlineData(0U, false)]
    [InlineData(0U, true)]
    [InlineData(7U, true)]
    public async Task Nand_read_uses_counted_stream_only_at_zero_and_preserves_data_and_spare(
        uint startRecord,
        bool includeStartBlock)
    {
        using var directory = new TemporaryDirectory();
        string output = Path.Combine(directory.Root, "nand.bin");
        byte[] expected = Payload(2 * 528, 0x49);
        await using var firmware = PtyPicoFirmware.Create();
        firmware.Start(async (terminal, token) =>
        {
            await ServePreflightAsync(terminal, NandConfiguration, token);
            if (startRecord == 0)
            {
                terminal.ReceiveCommand(PicoFlasherCommand.ReadFlashStream, 2, token);
            }

            for (uint index = 0; index < 2; index++)
            {
                if (startRecord != 0)
                {
                    terminal.ReceiveCommand(PicoFlasherCommand.ReadFlash, startRecord + index, token);
                }

                await terminal.WriteFragmentedAsync(RecordResponse(0, expected.AsSpan((int)index * 528, 528)), token);
            }

            await ServeRestartAsync(terminal, token);
        });

        string[] options = includeStartBlock
            ? ["--start-block", Number(startRecord), "--blocks", "2", "--output", output]
            : ["--blocks", "2", "--output", output];
        CliResult run = await RunPicoAsync(
            firmware,
            CreateConnectionFactory(firmware),
            "nand-read",
            options);

        AssertNandTransfer(AssertSuccess(run), startRecord, 2);
        await firmware.CompleteAsync();
        Assert.Equal(expected, await File.ReadAllBytesAsync(output));
        Assert.Single(Directory.GetFiles(directory.Root));
        var expectedFrames = PreflightFrames();
        if (startRecord == 0)
        {
            expectedFrames.Add((PicoFlasherCommand.ReadFlashStream, 2));
        }
        else
        {
            expectedFrames.Add((PicoFlasherCommand.ReadFlash, startRecord));
            expectedFrames.Add((PicoFlasherCommand.ReadFlash, startRecord + 1));
        }

        expectedFrames.Add((PicoFlasherCommand.StartSmc, 0));
        AssertFrames(firmware, expectedFrames.ToArray());
    }

    [PtyLinuxFact]
    public async Task Nand_write_sends_complete_533_byte_frames_without_a_separate_erase()
    {
        using var directory = new TemporaryDirectory();
        string input = Path.Combine(directory.Root, "nand.bin");
        byte[] expected = Payload(32 * 528, 0x63);
        await File.WriteAllBytesAsync(input, expected);
        await using var firmware = PtyPicoFirmware.Create();
        firmware.Start(async (terminal, token) =>
        {
            await ServePreflightAsync(terminal, NandConfiguration, token);
            for (uint index = 0; index < 32; index++)
            {
                terminal.ReceiveWrite(32 + index, expected.AsSpan((int)index * 528, 528), token);
                await terminal.WriteUInt32Async(0, token);
            }

            await ServeRestartAsync(terminal, token);
        });

        CliResult run = await RunPicoAsync(
            firmware,
            CreateConnectionFactory(firmware),
            "nand-write",
            ["--start-block", "32", "--input", input, "--yes"]);
        await firmware.CompleteAsync();

        AssertNandTransfer(AssertSuccess(run), 32, 32);
        Assert.Equal(expected, await File.ReadAllBytesAsync(input));
        Assert.Equal(37, firmware.Frames.Count);
        AssertPreflightFrames(firmware);
        for (int index = 0; index < 32; index++)
        {
            byte[] frame = firmware.Frames[index + 4];
            Assert.Equal(533, frame.Length);
            Assert.Equal(PtyPicoFirmware.Command(PicoFlasherCommand.WriteFlash, (uint)(32 + index)), frame[..5]);
            Assert.Equal(expected.AsSpan(index * 528, 528).ToArray(), frame[5..]);
        }

        Assert.Equal(PtyPicoFirmware.Command(PicoFlasherCommand.StartSmc, 0), firmware.Frames[^1]);
        Assert.DoesNotContain(firmware.Frames, static frame => frame[0] == (byte)PicoFlasherCommand.EraseFlash);
        AssertNoEmmcWrite(firmware);
    }

    [PtyLinuxTheory]
    [InlineData(0x0119_8010U, 0x20U)]
    [InlineData(0x0002_3010U, 0x20U)]
    [InlineData(0x0004_3000U, 0x20U)]
    [InlineData(0x0119_8030U, 0x20U)]
    [InlineData(0x008A_3020U, 0x100U)]
    [InlineData(0x008C_3020U, 0x100U)]
    [InlineData(0x00AA_3020U, 0x100U)]
    [InlineData(0x00AC_3020U, 0x100U)]
    public async Task Nand_erase_maps_geometry_to_aligned_record_and_inclusive_byte_ranges(
        uint configuration,
        uint eraseRecords)
    {
        await using var firmware = PtyPicoFirmware.Create();
        firmware.Start(async (terminal, token) =>
        {
            await ServePreflightAsync(terminal, configuration, token);
            terminal.ReceiveCommand(PicoFlasherCommand.EraseFlash, eraseRecords, token);
            await terminal.WriteUInt32Async(0, token);
            terminal.ReceiveCommand(PicoFlasherCommand.EraseFlash, 2 * eraseRecords, token);
            await terminal.WriteUInt32Async(0, token);
            await ServeRestartAsync(terminal, token);
        });

        CliResult run = await RunPicoAsync(
            firmware,
            CreateConnectionFactory(firmware),
            "nand-erase",
            ["--start-erase-block", "1", "--erase-blocks", "2", "--yes"]);
        await firmware.CompleteAsync();

        JsonElement result = AssertSuccess(run);
        Assert.Equal(configuration, result.GetProperty("flashConfiguration").GetUInt32());
        Assert.Equal(1U, result.GetProperty("startEraseBlock").GetUInt32());
        Assert.Equal(2U, result.GetProperty("endEraseBlockInclusive").GetUInt32());
        Assert.Equal(2U, result.GetProperty("eraseBlockCount").GetUInt32());
        Assert.Equal(eraseRecords, result.GetProperty("eraseBlockRecordCount").GetUInt32());
        Assert.Equal(eraseRecords, result.GetProperty("startRecord").GetUInt32());
        Assert.Equal(3 * eraseRecords - 1, result.GetProperty("endRecordInclusive").GetUInt32());
        Assert.Equal(2 * eraseRecords, result.GetProperty("recordCount").GetUInt32());
        Assert.Equal((long)eraseRecords * 512, result.GetProperty("startLogicalByteOffset").GetInt64());
        Assert.Equal((long)3 * eraseRecords * 512 - 1, result.GetProperty("endLogicalByteOffsetInclusive").GetInt64());
        Assert.Equal((long)2 * eraseRecords * 512, result.GetProperty("logicalByteLength").GetInt64());
        Assert.Equal((long)eraseRecords * 528, result.GetProperty("startRawByteOffset").GetInt64());
        Assert.Equal((long)3 * eraseRecords * 528 - 1, result.GetProperty("endRawByteOffsetInclusive").GetInt64());
        Assert.Equal((long)2 * eraseRecords * 528, result.GetProperty("rawByteLength").GetInt64());
        var expectedFrames = PreflightFrames();
        expectedFrames.Add((PicoFlasherCommand.EraseFlash, eraseRecords));
        expectedFrames.Add((PicoFlasherCommand.EraseFlash, 2 * eraseRecords));
        expectedFrames.Add((PicoFlasherCommand.StartSmc, 0));
        AssertFrames(firmware, expectedFrames.ToArray());
    }

    [PtyLinuxTheory]
    [InlineData(0x0119_8020U)]
    [InlineData(0x008A_3030U)]
    public async Task Nand_erase_rejects_unrecognized_major_minor_geometry_and_restarts_without_erasing(uint configuration)
    {
        await using var firmware = PtyPicoFirmware.Create();
        firmware.Start(async (terminal, token) =>
        {
            await ServePreflightAsync(terminal, configuration, token);
            await ServeRestartAsync(terminal, token);
        });

        CliResult run = await RunPicoAsync(
            firmware,
            CreateConnectionFactory(firmware),
            "nand-erase",
            ["--start-erase-block", "1", "--erase-blocks", "1", "--yes"]);
        await firmware.CompleteAsync();

        AssertError(run, 3, "pico-nand-geometry-unsupported");
        var expectedFrames = PreflightFrames();
        expectedFrames.Add((PicoFlasherCommand.StartSmc, 0));
        AssertFrames(firmware, expectedFrames.ToArray());
    }

    [PtyLinuxTheory]
    [InlineData(NandConfiguration, 0x8000U, 0x20U, false)]
    [InlineData(NandConfiguration, 0x8000U, 0x20U, true)]
    [InlineData(0x0119_8030U, 0x20000U, 0x20U, false)]
    [InlineData(0x0119_8030U, 0x20000U, 0x20U, true)]
    [InlineData(0x008A_3020U, 0x80000U, 0x100U, false)]
    [InlineData(0x008A_3020U, 0x80000U, 0x100U, true)]
    [InlineData(0x00AA_3020U, 0x100000U, 0x100U, false)]
    [InlineData(0x00AA_3020U, 0x100000U, 0x100U, true)]
    public async Task Nand_destructive_ranges_reject_the_first_unit_beyond_recognized_capacity_without_data_or_erase_frames(
        uint configuration,
        uint capacityRecords,
        uint eraseRecords,
        bool startAtCapacity)
    {
        using var directory = new TemporaryDirectory();
        string input = Path.Combine(directory.Root, "nand.bin");
        uint startRecord = startAtCapacity ? capacityRecords : capacityRecords - eraseRecords;
        uint recordCount = startAtCapacity ? eraseRecords : 2 * eraseRecords;
        byte[] expected = Payload(checked((int)recordCount * 528), 0x6D);
        await File.WriteAllBytesAsync(input, expected);

        foreach (string command in new[] { "nand-write", "nand-erase" })
        {
            await using var firmware = PtyPicoFirmware.Create();
            firmware.Start(async (terminal, token) =>
            {
                await ServePreflightAsync(terminal, configuration, token);
                await ServeRestartAsync(terminal, token);
            });

            string[] options = command == "nand-write"
                ? ["--start-block", Number(startRecord), "--input", input, "--yes"]
                :
                [
                    "--start-erase-block", Number(startRecord / eraseRecords),
                    "--erase-blocks", Number(recordCount / eraseRecords), "--yes",
                ];
            CliResult run = await RunPicoAsync(firmware, CreateConnectionFactory(firmware), command, options);
            await firmware.CompleteAsync();

            JsonElement error = AssertError(run, 2, "pico-nand-range-invalid");
            Assert.Contains("outside the detected flash geometry", error.GetProperty("message").GetString()!, StringComparison.Ordinal);
            Assert.Equal(expected, await File.ReadAllBytesAsync(input));
            Assert.Single(Directory.GetFiles(directory.Root));
            var expectedFrames = PreflightFrames();
            expectedFrames.Add((PicoFlasherCommand.StartSmc, 0));
            AssertFrames(firmware, expectedFrames.ToArray());
        }
    }

    [PtyLinuxTheory]
    [InlineData(0x18000U)]
    [InlineData(EmmcCapacity)]
    [InlineData(0x7FFFFFU)]
    public async Task Emmc_probe_reads_fragmented_raw_metadata_without_status_prefixes(uint capacity)
    {
        await using var firmware = PtyPicoFirmware.Create();
        firmware.Start(async (terminal, token) =>
        {
            await ServePreflightAsync(terminal, EmmcConfiguration, token);
            await ServeEmmcMetadataAsync(terminal, capacity, token);
            await ServeRestartAsync(terminal, token);
        });

        CliResult run = await RunPicoAsync(firmware, CreateConnectionFactory(firmware), "emmc-probe");
        await firmware.CompleteAsync();

        JsonElement result = AssertSuccess(run);
        Assert.Equal(4U, result.GetProperty("firmwareVersion").GetUInt32());
        Assert.Equal(firmware.SlavePath, result.GetProperty("devicePath").GetString());
        Assert.Equal(SerialNumber, result.GetProperty("serialNumber").GetString());
        Assert.Equal(EmmcConfiguration, result.GetProperty("flashConfiguration").GetUInt32());
        Assert.Equal(capacity, result.GetProperty("capacitySectorCount").GetUInt32());
        Assert.Equal(Payload(16, 0x19), result.GetProperty("cid").GetBytesFromBase64());
        Assert.Equal(Payload(16, 0x73), result.GetProperty("csd").GetBytesFromBase64());
        Assert.Equal(ExtendedCsd(capacity), result.GetProperty("extendedCsd").GetBytesFromBase64());
        var expectedFrames = EmmcPreflightFrames();
        expectedFrames.Add((PicoFlasherCommand.StartSmc, 0));
        AssertFrames(firmware, expectedFrames.ToArray());
    }

    [PtyLinuxTheory]
    [InlineData(0U, false)]
    [InlineData(0U, true)]
    [InlineData(0x17FFEU, true)]
    public async Task Emmc_read_streams_at_zero_or_reads_individually_through_the_system_partition_boundary(
        uint startSector,
        bool includeStartBlock)
    {
        using var directory = new TemporaryDirectory();
        string output = Path.Combine(directory.Root, "emmc.bin");
        byte[] expected = Payload(2 * 512, 0x91);
        await using var firmware = PtyPicoFirmware.Create();
        firmware.Start(async (terminal, token) =>
        {
            await ServePreflightAsync(terminal, EmmcConfiguration, token);
            await ServeEmmcMetadataAsync(terminal, EmmcCapacity, token);
            if (startSector == 0)
            {
                terminal.ReceiveCommand(PicoFlasherCommand.EmmcReadStream, 2, token);
            }

            for (uint index = 0; index < 2; index++)
            {
                if (startSector != 0)
                {
                    terminal.ReceiveCommand(PicoFlasherCommand.EmmcRead, startSector + index, token);
                }

                await terminal.WriteFragmentedAsync(RecordResponse(0, expected.AsSpan((int)index * 512, 512)), token);
            }

            await ServeRestartAsync(terminal, token);
        });

        string[] options = includeStartBlock
            ? ["--start-block", Number(startSector), "--blocks", "2", "--output", output]
            : ["--blocks", "2", "--output", output];
        CliResult run = await RunPicoAsync(
            firmware,
            CreateConnectionFactory(firmware),
            "emmc-read",
            options);

        JsonElement result = AssertSuccess(run);
        Assert.Equal(EmmcConfiguration, result.GetProperty("flashConfiguration").GetUInt32());
        Assert.Equal(EmmcCapacity, result.GetProperty("capacitySectorCount").GetUInt32());
        Assert.Equal(startSector, result.GetProperty("startSector").GetUInt32());
        Assert.Equal(2U, result.GetProperty("sectorCount").GetUInt32());
        Assert.Equal(1024L, result.GetProperty("logicalByteLength").GetInt64());
        await firmware.CompleteAsync();
        Assert.Equal(expected, await File.ReadAllBytesAsync(output));
        Assert.Single(Directory.GetFiles(directory.Root));
        var expectedFrames = EmmcPreflightFrames();
        if (startSector == 0)
        {
            expectedFrames.Add((PicoFlasherCommand.EmmcReadStream, 2));
        }
        else
        {
            expectedFrames.Add((PicoFlasherCommand.EmmcRead, startSector));
            expectedFrames.Add((PicoFlasherCommand.EmmcRead, startSector + 1));
        }

        expectedFrames.Add((PicoFlasherCommand.StartSmc, 0));
        AssertFrames(firmware, expectedFrames.ToArray());
    }

    [PtyLinuxTheory]
    [InlineData(0U)]
    [InlineData(0x17FFFU)]
    [InlineData(0x800000U)]
    [InlineData(uint.MaxValue)]
    public async Task Emmc_read_rejects_invalid_capacity_before_a_data_frame_and_removes_temporary_output(uint capacity)
    {
        using var directory = new TemporaryDirectory();
        string output = Path.Combine(directory.Root, "emmc.bin");
        await using var firmware = PtyPicoFirmware.Create();
        firmware.Start(async (terminal, token) =>
        {
            await ServePreflightAsync(terminal, EmmcConfiguration, token);
            await ServeEmmcMetadataAsync(terminal, capacity, token);
            await ServeRestartAsync(terminal, token);
        });

        CliResult run = await RunPicoAsync(
            firmware,
            CreateConnectionFactory(firmware),
            "emmc-read",
            ["--blocks", "1", "--output", output]);

        AssertError(run, 3, "pico-emmc-capacity-invalid");
        await firmware.CompleteAsync();
        Assert.Empty(Directory.GetFiles(directory.Root));
        var expectedFrames = EmmcPreflightFrames();
        expectedFrames.Add((PicoFlasherCommand.StartSmc, 0));
        AssertFrames(firmware, expectedFrames.ToArray());
    }

    [PtyLinuxTheory]
    [InlineData("0", "0")]
    [InlineData("98304", "1")]
    [InlineData("98303", "2")]
    [InlineData("4294967295", "2")]
    [InlineData("1", "4294967295")]
    public async Task Emmc_read_rejects_zero_overflow_and_out_of_window_ranges_without_opening_serial(
        string start,
        string count)
    {
        using var directory = new TemporaryDirectory();
        string output = Path.Combine(directory.Root, "emmc.bin");
        await using var firmware = PtyPicoFirmware.Create();
        byte[]? initialSettings = CaptureSlaveSettingsIfSupported(firmware);
        firmware.Start(static (terminal, token) => terminal.AssertQuietAsync(TimeSpan.FromMilliseconds(100), token));

        CliResult run = await RunPicoAsync(
            firmware,
            CreateConnectionFactory(firmware),
            "emmc-read",
            ["--start-block", start, "--blocks", count, "--output", output]);
        await firmware.CompleteAsync();
        AssertSlaveSettingsUnchanged(firmware, initialSettings);

        AssertError(run, 2, "pico-emmc-range-invalid");
        Assert.Empty(firmware.Frames);
        Assert.Empty(Directory.GetFiles(directory.Root));
    }

    [PtyLinuxTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Stream_status_failure_or_cancellation_aborts_drains_restarts_and_reopens_without_stale_frames(
        bool emmc,
        bool cancel)
    {
        using var directory = new TemporaryDirectory();
        string output = Path.Combine(directory.Root, "dump.bin");
        int payloadSize = emmc ? 512 : 528;
        PicoFlasherCommand streamOpcode = emmc ? PicoFlasherCommand.EmmcReadStream : PicoFlasherCommand.ReadFlashStream;
        byte[] queuedTail = Payload(payloadSize + 4 + 29, 0x55);
        using var commandCancellation = new CancellationTokenSource();
        await using var firmware = PtyPicoFirmware.Create();
        var transportFactory = new StreamFailureTransportFactory(
            streamOpcode, payloadSize, cancel, commandCancellation, firmware.LifetimeToken);
        PicoFlasherConnectionFactory connectionFactory = CreateConnectionFactory(firmware, transportFactory);
        firmware.Start(async (terminal, token) =>
        {
            await ServePreflightAsync(terminal, emmc ? EmmcConfiguration : NandConfiguration, token);
            if (emmc)
            {
                await ServeEmmcMetadataAsync(terminal, EmmcCapacity, token);
            }

            terminal.ReceiveCommand(streamOpcode, 3, token);
            await terminal.WriteFragmentedAsync(RecordResponse(0, Payload(payloadSize, 0x15)), token);
            if (cancel)
            {
                await terminal.WriteFragmentedAsync(
                    RecordResponse(0, Payload(StreamFailureTransportFactory.PartialPayloadByteCount, 0x33)),
                    token);
            }
            else
            {
                await terminal.WriteUInt32Async(0xBAD, token);
            }

            // Do not let fragment scheduling race the client's 100 ms quiescence
            // interval. The transport exposes the failure only after this entire
            // already-queued tail is available to the real serial bytewise drain.
            await transportFactory.FailureObserved.Task.WaitAsync(token);
            await terminal.WriteFragmentedAsync(queuedTail, token);
            transportFactory.TailQueued.SetResult();
            terminal.ReceiveCommand(streamOpcode, 0, token);
            terminal.ReceiveCommand(PicoFlasherCommand.StartSmc, 0, token);
            await ServeProbeAsync(terminal, token);
        });

        Task<CliResult> read = RunPicoAsync(
            firmware,
            connectionFactory,
            emmc ? "emmc-read" : "nand-read",
            ["--start-block", "0", "--blocks", "3", "--output", output],
            commandCancellation.Token);

        CliResult failedRun = await read;
        JsonElement error = AssertError(
            failedRun,
            cancel ? 130 : 5,
            cancel ? "cancelled" : emmc ? "pico-emmc-read-failed" : "pico-nand-read-failed");
        if (!cancel)
        {
            Assert.Contains(emmc ? "sector 1" : "record 1", error.GetProperty("message").GetString()!, StringComparison.Ordinal);
            Assert.Contains("0x00000BAD", error.GetProperty("message").GetString()!, StringComparison.Ordinal);
        }

        Assert.Empty(Directory.GetFiles(directory.Root));
        Assert.Equal(queuedTail.Length, transportFactory.DrainedByteCount);
        Assert.Equal(
            cancel ? StreamFailureTransportFactory.PartialPayloadByteCount : 0,
            transportFactory.PartialPayloadBytesReceived);
        CliResult reopenedRun = await RunPicoAsync(firmware, connectionFactory, "probe");

        AssertProbeResult(reopenedRun, firmware.SlavePath);
        await firmware.CompleteAsync();
        var expectedFrames = emmc ? EmmcPreflightFrames() : PreflightFrames();
        expectedFrames.Add((streamOpcode, 3));
        expectedFrames.Add((streamOpcode, 0));
        expectedFrames.Add((PicoFlasherCommand.StartSmc, 0));
        expectedFrames.AddRange(PreflightFrames());
        expectedFrames.Add((PicoFlasherCommand.StartSmc, 0));
        AssertFrames(firmware, expectedFrames.ToArray());
    }

    [PtyLinuxFact]
    public async Task Progressing_fragmented_version_response_can_exceed_the_no_progress_timeout()
    {
        await using var firmware = PtyPicoFirmware.Create();
        firmware.Start(async (terminal, token) =>
        {
            terminal.ReceiveCommand(PicoFlasherCommand.GetVersion, 0, token);
            byte[] version = UInt32Response(4);
            AssertSerialLineCoding(terminal);
            for (int index = 0; index < version.Length; index++)
            {
                if (index != 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(800), token);
                }

                await terminal.WriteFragmentedAsync(version.AsMemory(index, 1), token);
            }

            await ServeSmcPreflightAsync(terminal, NandConfiguration, token);
            await ServeRestartAsync(terminal, token);
        });

        var elapsed = Stopwatch.StartNew();
        CliResult run = await RunPicoAsync(firmware, CreateConnectionFactory(firmware), "probe", timeout: "2");
        elapsed.Stop();
        await firmware.CompleteAsync();

        AssertProbeResult(run, firmware.SlavePath);
        Assert.True(elapsed.Elapsed > TimeSpan.FromSeconds(2), "The progressing response must exceed the selected timeout in total.");
        var expectedFrames = PreflightFrames();
        expectedFrames.Add((PicoFlasherCommand.StartSmc, 0));
        AssertFrames(firmware, expectedFrames.ToArray());
    }

    [PtyLinuxFact]
    public async Task Stalled_partial_version_response_fails_with_typed_transport_timeout_and_no_later_opcode()
    {
        await using var firmware = PtyPicoFirmware.Create();
        firmware.Start(async (terminal, token) =>
        {
            terminal.ReceiveCommand(PicoFlasherCommand.GetVersion, 0, token);
            AssertSerialLineCoding(terminal);
            await terminal.WriteFragmentedAsync(UInt32Response(4).AsMemory(0, 2), token);
            await terminal.AssertQuietAsync(TimeSpan.FromMilliseconds(1500), token);
        });

        CliResult run = await RunPicoAsync(firmware, CreateConnectionFactory(firmware), "probe", timeout: "1");
        await firmware.CompleteAsync();

        AssertError(run, 6, "pico-transport-timeout");
        AssertFrames(firmware, (PicoFlasherCommand.GetVersion, 0));
    }

    [PtyLinuxFact]
    public async Task Emmc_write_is_an_unknown_command_and_never_opens_or_sends_opcode_57()
    {
        using var directory = new TemporaryDirectory();
        string input = Path.Combine(directory.Root, "input.bin");
        await File.WriteAllBytesAsync(input, Payload(512, 0x27));
        await using var firmware = PtyPicoFirmware.Create();
        byte[]? initialSettings = CaptureSlaveSettingsIfSupported(firmware);
        firmware.Start(static (terminal, token) => terminal.AssertQuietAsync(TimeSpan.FromMilliseconds(100), token));

        CliResult run = await RunPicoAsync(
            firmware,
            CreateConnectionFactory(firmware),
            "emmc-write",
            ["--start-block", "0", "--input", input, "--yes"]);
        AssertSlaveSettingsUnchanged(firmware, initialSettings);

        AssertError(run, 2, "unsupported-command");
        await firmware.CompleteAsync();
        Assert.Empty(firmware.Frames);
        AssertNoEmmcWrite(firmware);
    }

    private static byte[]? CaptureSlaveSettingsIfSupported(PtyPicoFirmware firmware) =>
        RuntimeInformation.ProcessArchitecture == Architecture.X64 ? firmware.CaptureSlaveSettings() : null;

    private static void AssertSlaveSettingsUnchanged(PtyPicoFirmware firmware, byte[]? initialSettings)
    {
        if (initialSettings is not null)
        {
            Assert.Equal(initialSettings, firmware.CaptureSlaveSettings());
        }
    }

    private static void AssertSerialLineCoding(PtyPicoFirmware firmware)
    {
        // The native termios layout is grounded for Linux x64. Wire coverage still
        // runs on every Linux architecture rather than assuming the same ABI.
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        Assert.Equal(0x1002U, firmware.GetSlaveInputSpeedCode()); // B115200, never B1200.
        Assert.Equal(0x1002U, firmware.GetSlaveOutputSpeedCode());
        uint controlFlags = BitConverter.ToUInt32(firmware.CaptureSlaveSettings(), 8);
        Assert.Equal(0x30U, controlFlags & 0x30U); // CS8.
        Assert.Equal(0U, controlFlags & 0x140U); // No CSTOPB or PARENB: 8N1.
    }

    private static async Task ServeVersionAsync(PtyPicoFirmware firmware, CancellationToken token)
    {
        firmware.ReceiveCommand(PicoFlasherCommand.GetVersion, 0, token);
        AssertSerialLineCoding(firmware);
        await firmware.WriteUInt32Async(4, token);
    }

    private static async Task ServePreflightAsync(PtyPicoFirmware firmware, uint configuration, CancellationToken token)
    {
        await ServeVersionAsync(firmware, token);
        await ServeSmcPreflightAsync(firmware, configuration, token);
    }

    private static async Task ServeSmcPreflightAsync(PtyPicoFirmware firmware, uint configuration, CancellationToken token)
    {
        firmware.ReceiveCommand(PicoFlasherCommand.SetSmcWorkaround, 0, token);
        firmware.ReceiveCommand(PicoFlasherCommand.StopSmc, 0, token);
        // The wire must remain idle during the firmware's 500 ms SMC settle.
        // Allow only 50 ms for PTY observation/poll scheduling jitter in CI.
        // Checking all outgoing bytes also rejects premature NAND/eMMC data opcodes.
        await firmware.AssertQuietAsync(TimeSpan.FromMilliseconds(500 - 50), token);
        firmware.ReceiveCommand(PicoFlasherCommand.GetFlashConfiguration, 0, token);
        await firmware.WriteUInt32Async(configuration, token);
    }

    private static async Task ServeProbeAsync(PtyPicoFirmware firmware, CancellationToken token)
    {
        await ServePreflightAsync(firmware, NandConfiguration, token);
        await ServeRestartAsync(firmware, token);
    }

    private static async Task ServeRestartAsync(PtyPicoFirmware firmware, CancellationToken token)
    {
        firmware.ReceiveCommand(PicoFlasherCommand.StartSmc, 0, token);
        await firmware.AssertQuietAsync(TimeSpan.FromMilliseconds(50), token);
    }

    private static async Task ServeEmmcMetadataAsync(PtyPicoFirmware firmware, uint capacity, CancellationToken token)
    {
        firmware.ReceiveCommand(PicoFlasherCommand.EmmcDetect, 0, token);
        await firmware.WriteFragmentedAsync(new byte[] { 1 }, token);
        firmware.ReceiveCommand(PicoFlasherCommand.EmmcInitialize, 0, token);
        await firmware.WriteUInt32Async(0, token);
        firmware.ReceiveCommand(PicoFlasherCommand.EmmcGetCid, 0, token);
        await firmware.WriteFragmentedAsync(Payload(16, 0x19), token);
        firmware.ReceiveCommand(PicoFlasherCommand.EmmcGetCsd, 0, token);
        await firmware.WriteFragmentedAsync(Payload(16, 0x73), token);
        firmware.ReceiveCommand(PicoFlasherCommand.EmmcGetExtendedCsd, 0, token);
        await firmware.WriteFragmentedAsync(ExtendedCsd(capacity), token);
    }

    private static PicoFlasherConnectionFactory CreateConnectionFactory(
        PtyPicoFirmware firmware,
        IPicoFlasherTransportFactory? transportFactory = null) => new(
        new FixedEndpointEnumerator(new PicoFlasherDeviceEndpoint(firmware.SlavePath, SerialNumber, interfaceNumber: 0)),
        transportFactory ?? new SerialPicoFlasherTransportFactory());

    private static Task<CliResult> RunPicoAsync(
        PtyPicoFirmware firmware,
        PicoFlasherConnectionFactory connectionFactory,
        string command,
        string[]? options = null,
        CancellationToken cancellationToken = default,
        string timeout = "2",
        bool selectBySerial = false)
    {
        var arguments = new List<string>
        {
            "pico", command,
            selectBySerial ? "--serial" : "--device",
            selectBySerial ? SerialNumber : firmware.SlavePath,
            "--timeout", timeout, "--json",
        };
        if (options is not null)
        {
            arguments.AddRange(options);
        }

        return RunAsync(firmware, connectionFactory, arguments.ToArray(), cancellationToken);
    }

    private static async Task<CliResult> RunAsync(
        PtyPicoFirmware firmware,
        PicoFlasherConnectionFactory connectionFactory,
        string[] arguments,
        CancellationToken cancellationToken = default)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(firmware.LifetimeToken, cancellationToken);
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        int exitCode = await CliCommandRouter.RunAsync(
            arguments,
            standardOutput,
            standardError,
            cancellation.Token,
            connectionFactory);
        return new CliResult(exitCode, standardOutput.ToString(), standardError.ToString());
    }

    private static string[] OptionsForOpeningCommand(string command, string input, string output) => command switch
    {
        "nand-read" => ["--start-block", "0", "--blocks", "1", "--output", output],
        "nand-write" => ["--start-block", "0", "--input", input, "--yes"],
        "nand-erase" => ["--start-erase-block", "0", "--erase-blocks", "1", "--yes"],
        "emmc-read" => ["--start-block", "0", "--blocks", "1", "--output", output],
        _ => [],
    };

    private static JsonElement AssertSuccess(CliResult run)
    {
        Assert.Equal(0, run.ExitCode);
        using JsonDocument document = JsonDocument.Parse(run.StandardOutput);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        return document.RootElement.GetProperty("result").Clone();
    }

    private static JsonElement AssertError(CliResult run, int expectedExitCode, string expectedKind)
    {
        Assert.Equal(expectedExitCode, run.ExitCode);
        using JsonDocument document = JsonDocument.Parse(run.StandardOutput);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        JsonElement error = document.RootElement.GetProperty("error");
        Assert.Equal(expectedExitCode, error.GetProperty("code").GetInt32());
        Assert.Equal(expectedKind, error.GetProperty("kind").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
        return error.Clone();
    }

    private static void AssertProbeResult(CliResult run, string devicePath)
    {
        JsonElement result = AssertSuccess(run);
        Assert.Equal(4U, result.GetProperty("firmwareVersion").GetUInt32());
        Assert.Equal(devicePath, result.GetProperty("devicePath").GetString());
        Assert.Equal(SerialNumber, result.GetProperty("serialNumber").GetString());
        Assert.Equal(NandConfiguration, result.GetProperty("flashConfiguration").GetUInt32());
        Assert.Equal("nand", result.GetProperty("storageKind").GetString());
        JsonElement geometry = result.GetProperty("nandGeometry");
        Assert.Equal(0x8000U, geometry.GetProperty("recordCount").GetUInt32());
        Assert.Equal(0x20U, geometry.GetProperty("eraseBlockRecordCount").GetUInt32());
        Assert.Equal(0x4000U, geometry.GetProperty("eraseBlockLogicalByteLength").GetUInt32());
    }

    private static void AssertNandTransfer(JsonElement result, uint start, uint count)
    {
        Assert.Equal(NandConfiguration, result.GetProperty("flashConfiguration").GetUInt32());
        Assert.Equal(start, result.GetProperty("startRecord").GetUInt32());
        Assert.Equal(start + count - 1, result.GetProperty("endRecordInclusive").GetUInt32());
        Assert.Equal(count, result.GetProperty("recordCount").GetUInt32());
        Assert.Equal((long)count * 512, result.GetProperty("logicalByteLength").GetInt64());
        Assert.Equal((long)count * 528, result.GetProperty("rawByteLength").GetInt64());
    }

    private static void AssertFrames(PtyPicoFirmware firmware, params (PicoFlasherCommand Command, uint Lba)[] expected)
    {
        Assert.Equal(expected.Length, firmware.Frames.Count);
        for (int index = 0; index < expected.Length; index++)
        {
            Assert.Equal(PtyPicoFirmware.Command(expected[index].Command, expected[index].Lba), firmware.Frames[index]);
        }

        AssertNoEmmcWrite(firmware);
    }

    private static void AssertPreflightFrames(PtyPicoFirmware firmware)
    {
        var expected = PreflightFrames();
        for (int index = 0; index < expected.Count; index++)
        {
            Assert.Equal(PtyPicoFirmware.Command(expected[index].Command, expected[index].Lba), firmware.Frames[index]);
        }
    }

    private static void AssertNoEmmcWrite(PtyPicoFirmware firmware) =>
        Assert.DoesNotContain(firmware.Frames, static frame => frame[0] == 0x57);

    private static List<(PicoFlasherCommand Command, uint Lba)> PreflightFrames() =>
    [
        (PicoFlasherCommand.GetVersion, 0),
        (PicoFlasherCommand.SetSmcWorkaround, 0),
        (PicoFlasherCommand.StopSmc, 0),
        (PicoFlasherCommand.GetFlashConfiguration, 0),
    ];

    private static List<(PicoFlasherCommand Command, uint Lba)> EmmcPreflightFrames()
    {
        var frames = PreflightFrames();
        frames.AddRange(
        [
            (PicoFlasherCommand.EmmcDetect, 0),
            (PicoFlasherCommand.EmmcInitialize, 0),
            (PicoFlasherCommand.EmmcGetCid, 0),
            (PicoFlasherCommand.EmmcGetCsd, 0),
            (PicoFlasherCommand.EmmcGetExtendedCsd, 0),
        ]);
        return frames;
    }

    private static byte[] Payload(int length, byte seed)
    {
        byte[] bytes = new byte[length];
        for (int index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)(seed + index * 17 + index / 512);
        }

        return bytes;
    }

    private static byte[] ExtendedCsd(uint capacity)
    {
        byte[] bytes = Payload(512, 0xA7);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0xD4, 4), capacity);
        return bytes;
    }

    private static byte[] RecordResponse(uint status, ReadOnlySpan<byte> payload)
    {
        byte[] response = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(response, status);
        payload.CopyTo(response.AsSpan(4));
        return response;
    }

    private static byte[] UInt32Response(uint value)
    {
        byte[] response = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(response, value);
        return response;
    }

    private static string Number(uint value) => value.ToString(CultureInfo.InvariantCulture);

    private sealed record CliResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class FixedEndpointEnumerator(PicoFlasherDeviceEndpoint endpoint) : IPicoFlasherDeviceEnumerator
    {
        public ValueTask<IReadOnlyList<PicoFlasherDeviceEndpoint>> EnumerateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<IReadOnlyList<PicoFlasherDeviceEndpoint>>([endpoint]);
        }
    }

    private sealed class StreamFailureTransportFactory : IPicoFlasherTransportFactory
    {
        internal const int PartialPayloadByteCount = 37;
        private readonly SerialPicoFlasherTransportFactory _serialFactory = new();
        private readonly PicoFlasherCommand _streamOpcode;
        private readonly int _payloadSize;
        private readonly bool _cancel;
        private readonly CancellationTokenSource _commandCancellation;
        private readonly CancellationToken _lifetimeToken;

        internal StreamFailureTransportFactory(
            PicoFlasherCommand streamOpcode,
            int payloadSize,
            bool cancel,
            CancellationTokenSource commandCancellation,
            CancellationToken lifetimeToken)
        {
            _streamOpcode = streamOpcode;
            _payloadSize = payloadSize;
            _cancel = cancel;
            _commandCancellation = commandCancellation;
            _lifetimeToken = lifetimeToken;
        }

        internal TaskCompletionSource FailureObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource TailQueued { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal int DrainedByteCount { get; private set; }

        internal int PartialPayloadBytesReceived { get; private set; }

        public async ValueTask<IPicoFlasherTransport> OpenAsync(
            PicoFlasherDeviceEndpoint endpoint,
            TimeSpan noProgressTimeout,
            CancellationToken cancellationToken = default)
        {
            IPicoFlasherTransport transport = await _serialFactory.OpenAsync(
                endpoint, noProgressTimeout, cancellationToken);
            return new StreamFailureTransport(transport, this);
        }

        private async Task AwaitQueuedTailAsync()
        {
            FailureObserved.TrySetResult();
            await TailQueued.Task.WaitAsync(_lifetimeToken);
        }

        private sealed class StreamFailureTransport(
            IPicoFlasherTransport transport,
            StreamFailureTransportFactory owner) : IPicoFlasherTransport
        {
            private bool _streaming;
            private bool _draining;
            private int _responseReadCount;

            public async ValueTask WriteAsync(
                ReadOnlyMemory<byte> source,
                CancellationToken cancellationToken = default)
            {
                byte opcode = source.Span[0];
                uint lba = BinaryPrimitives.ReadUInt32LittleEndian(source.Span[1..]);
                await transport.WriteAsync(source, cancellationToken);
                _streaming = opcode == (byte)owner._streamOpcode && lba != 0;
                _draining = opcode == (byte)owner._streamOpcode && lba == 0;
                _responseReadCount = 0;
            }

            public async ValueTask ReadExactlyAsync(
                Memory<byte> destination,
                CancellationToken cancellationToken = default)
            {
                if (_draining)
                {
                    Assert.Equal(1, destination.Length);
                    await transport.ReadExactlyAsync(destination, cancellationToken);
                    owner.DrainedByteCount++;
                    return;
                }

                int responseRead = _streaming ? ++_responseReadCount : 0;
                if (owner._cancel && responseRead == 4)
                {
                    Assert.Equal(owner._payloadSize, destination.Length);
                    await transport.ReadExactlyAsync(
                        destination[..PartialPayloadByteCount], cancellationToken);
                    owner.PartialPayloadBytesReceived = PartialPayloadByteCount;

                    // Request cancellation only once the first record and the
                    // second status plus partial payload have really been read.
                    // Start the remaining real serial read before cancelling it.
                    Task remainingPayload = transport.ReadExactlyAsync(
                        destination[PartialPayloadByteCount..], cancellationToken).AsTask();
                    owner._commandCancellation.Cancel();
                    try
                    {
                        await remainingPayload;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        await owner.AwaitQueuedTailAsync();
                        throw;
                    }

                    Assert.Fail("The partial payload read completed without observing caller cancellation.");
                }

                await transport.ReadExactlyAsync(destination, cancellationToken);
                if (!owner._cancel && responseRead == 3)
                {
                    Assert.Equal(PicoFlasherProtocol.StatusSize, destination.Length);
                    Assert.Equal(0xBADU, PicoFlasherProtocol.ReadStatus(destination.Span));
                    await owner.AwaitQueuedTailAsync();
                }
            }

            public ValueTask DisposeAsync() => transport.DisposeAsync();
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), $"jrunner-pico-pty-tests-{Guid.NewGuid():N}");
            if (OperatingSystem.IsLinux())
            {
                Directory.CreateDirectory(
                    Root,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            else
            {
                Directory.CreateDirectory(Root);
            }
        }

        internal string Root { get; }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
