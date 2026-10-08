using System.Buffers.Binary;
using JRunner.Cli.Infrastructure;
using JRunner.Core.Contracts;
using JRunner.Core.Devices.PicoFlasher;
using Xunit;

namespace JRunner.Cli.Tests;

public sealed class PicoFlasherServiceTests
{
    private const string TestDevicePath = "/dev/ttyACM0";
    private const string TestSerialNumber = "test-serial";
    private const uint KnownSmallBlockConfiguration = 0x0119_8010U;
    private const int SmallBlockEraseRecordCount = 0x20;
    private const string TransportTimeoutKind = "pico-transport-timeout";
    private const string TransportClosedKind = "pico-transport-closed";
    private const string TransportIoFailureKind = "pico-transport-io-failed";
    private static readonly string[] TransportFailureKinds =
        [TransportTimeoutKind, TransportClosedKind, TransportIoFailureKind];

    [Fact]
    public async Task Probe_uses_the_safe_smc_sequence_and_classifies_known_nand()
    {
        var transport = new ScriptedTransport(Status(KnownSmallBlockConfiguration));
        PicoFlasherService service = CreateService(transport);

        PicoFlasherProbeResult result = await service.ProbeAsync();

        Assert.Equal(PicoFlasherProtocol.MinimumSupportedFirmwareVersion, result.FirmwareVersion);
        Assert.Equal(TestDevicePath, result.DevicePath);
        Assert.Equal(TestSerialNumber, result.SerialNumber);
        Assert.Equal(KnownSmallBlockConfiguration, result.FlashConfiguration);
        Assert.Equal("nand", result.StorageKind);

        PicoFlasherNandGeometry geometry = Assert.IsType<PicoFlasherNandGeometry>(result.NandGeometry);
        Assert.Equal(KnownSmallBlockConfiguration, geometry.FlashConfiguration);
        Assert.Equal(0x8000U, geometry.RecordCount);
        Assert.Equal((uint)SmallBlockEraseRecordCount, geometry.EraseBlockRecordCount);
        AssertPreflightAndRestart(transport);
    }

    [Theory]
    [InlineData(TransportTimeoutKind, false)]
    [InlineData(TransportClosedKind, false)]
    [InlineData(TransportIoFailureKind, false)]
    [InlineData(TransportTimeoutKind, true)]
    [InlineData(TransportClosedKind, true)]
    [InlineData(TransportIoFailureKind, true)]
    public async Task Classified_flash_configuration_response_failure_restarts_retires_and_preserves_transport_failure(
        string failureKind,
        bool failRestart)
    {
        OperationFailureException transportFailure = CreateTransportFailure(failureKind);
        var transport = new ScriptedTransport(
            Status(KnownSmallBlockConfiguration),
            readFailure: readCall => readCall == 1 ? transportFailure : null,
            partialReadByteCount: readCall => readCall == 1 ? 2 : 0,
            writeFailure: frame => failRestart && frame[0] == (byte)PicoFlasherCommand.StartSmc
                ? CreateTransportFailure(TransportIoFailureKind)
                : null);
        PicoFlasherService service = CreateService(transport);

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => service.ProbeAsync());

        AssertTransportFailure(transportFailure, failure);
        Assert.True(transport.Disposed);
        Assert.Equal(PicoFlasherProtocol.StatusSize - 2, transport.RemainingResponseByteCount);
        AssertFrames(
            transport,
            [
                Command(PicoFlasherCommand.SetSmcWorkaround, 0),
                Command(PicoFlasherCommand.StopSmc, 0),
                Command(PicoFlasherCommand.GetFlashConfiguration, 0),
                Command(PicoFlasherCommand.StartSmc, 0),
            ]);
        await AssertRetiredConnectionRejectsFollowUpAsync(service, transport);
    }

    [Theory]
    [InlineData((byte)PicoFlasherCommand.StopSmc)]
    [InlineData((byte)PicoFlasherCommand.StartSmc)]
    [InlineData((byte)PicoFlasherCommand.RebootToBootloader)]
    public async Task Explicit_controls_send_only_their_post_gate_command(byte opcode)
    {
        PicoFlasherCommand command = (PicoFlasherCommand)opcode;
        var transport = new ScriptedTransport([]);
        PicoFlasherService service = CreateService(transport);

        Task operation = command switch
        {
            PicoFlasherCommand.StopSmc => service.StopSmcAsync(),
            PicoFlasherCommand.StartSmc => service.StartSmcAsync(),
            PicoFlasherCommand.RebootToBootloader => service.RebootToBootloaderAsync(),
            _ => throw new ArgumentOutOfRangeException(nameof(opcode), opcode, "Unknown explicit control command."),
        };

        await operation;

        AssertFrames(transport, [Command(command, 0)]);
        Assert.Empty(transport.ReadLengths);
        Assert.False(transport.Disposed);
    }

    [Fact]
    public void Direct_service_firmware_guard_uses_missing_prerequisite()
    {
        var transport = new ScriptedTransport([]);

        OperationFailureException failure = Assert.Throws<OperationFailureException>(
            () => CreateService(
                transport,
                PicoFlasherProtocol.MinimumSupportedFirmwareVersion - 1U));

        Assert.Equal(ExitCode.MissingPrerequisite, failure.Code);
        Assert.Equal("pico-firmware-unsupported", failure.Kind);
        Assert.Empty(transport.WrittenFrames);
        Assert.Empty(transport.ReadLengths);
    }

    [Theory]
    [InlineData((byte)PicoFlasherCommand.StopSmc, TransportTimeoutKind)]
    [InlineData((byte)PicoFlasherCommand.StopSmc, TransportClosedKind)]
    [InlineData((byte)PicoFlasherCommand.StopSmc, TransportIoFailureKind)]
    [InlineData((byte)PicoFlasherCommand.StartSmc, TransportTimeoutKind)]
    [InlineData((byte)PicoFlasherCommand.StartSmc, TransportClosedKind)]
    [InlineData((byte)PicoFlasherCommand.StartSmc, TransportIoFailureKind)]
    [InlineData((byte)PicoFlasherCommand.RebootToBootloader, TransportTimeoutKind)]
    [InlineData((byte)PicoFlasherCommand.RebootToBootloader, TransportClosedKind)]
    [InlineData((byte)PicoFlasherCommand.RebootToBootloader, TransportIoFailureKind)]
    public async Task Classified_explicit_control_write_retires_without_a_preflight_or_retry(
        byte opcode,
        string failureKind)
    {
        PicoFlasherCommand command = (PicoFlasherCommand)opcode;
        OperationFailureException transportFailure = CreateTransportFailure(failureKind);
        var transport = new ScriptedTransport(
            [],
            writeFailure: frame => frame[0] == opcode ? transportFailure : null);
        PicoFlasherService service = CreateService(transport);

        Task invocation = command switch
        {
            PicoFlasherCommand.StopSmc => service.StopSmcAsync(),
            PicoFlasherCommand.StartSmc => service.StartSmcAsync(),
            PicoFlasherCommand.RebootToBootloader => service.RebootToBootloaderAsync(),
            _ => throw new ArgumentOutOfRangeException(nameof(opcode), opcode, "Unknown explicit control command."),
        };
        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(() => invocation);

        AssertTransportFailure(transportFailure, failure);
        Assert.True(transport.Disposed);
        AssertFrames(transport, [Command(command, 0)]);
        Assert.Empty(transport.ReadLengths);
        await AssertRetiredConnectionRejectsFollowUpAsync(service, transport);
    }

    [Theory]
    [InlineData(0U, "none")]
    [InlineData(0xC123_4567U, "emmc")]
    [InlineData(0x0119_8011U, "unsupported-nand")]
    public async Task Probe_safely_classifies_non_nand_configurations(uint flashConfiguration, string expectedStorageKind)
    {
        var transport = new ScriptedTransport(Status(flashConfiguration));
        PicoFlasherService service = CreateService(transport);

        PicoFlasherProbeResult result = await service.ProbeAsync();

        Assert.Equal(flashConfiguration, result.FlashConfiguration);
        Assert.Equal(expectedStorageKind, result.StorageKind);
        Assert.Null(result.NandGeometry);
        AssertPreflightAndRestart(transport);
        Assert.DoesNotContain(
            transport.WrittenFrames,
            frame => frame[0] == (byte)PicoFlasherCommand.ReadFlash ||
                frame[0] == (byte)PicoFlasherCommand.ReadFlashStream ||
                frame[0] == (byte)PicoFlasherCommand.WriteFlash ||
                frame[0] == (byte)PicoFlasherCommand.EraseFlash);
    }

    [Fact]
    public async Task Cancelled_stream_read_resets_drains_and_fences_before_requested_tail()
    {
        using var cancellation = new CancellationTokenSource();
        byte[] firstRecord = CreateRecord(0x11);
        byte[] queuedRecord = CreateRecord(0xA2);
        var transport = new ScriptedTransport(
            Combine(
                Status(KnownSmallBlockConfiguration),
                Status(0),
                firstRecord,
                Status(0),
                queuedRecord),
            afterRead: readCall =>
            {
                if (readCall == 3)
                {
                    cancellation.Cancel();
                }
            },
            awaitCancellationWhenResponsesExhausted: true);
        PicoFlasherService service = CreateService(transport);
        using var output = new MemoryStream();

        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ReadNandAsync(
                new PicoFlasherNandReadRequest(output, StartRecord: 0, RecordCount: 0x8000),
                cancellationToken: cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(transport.Disposed);
        Assert.Equal(
            3 + PicoFlasherProtocol.StatusSize + PicoFlasherProtocol.NandWireRecordSize + 1,
            transport.ReadLengths.Count);
        Assert.Equal<int>(
            [
                PicoFlasherProtocol.StatusSize,
                PicoFlasherProtocol.StatusSize,
                PicoFlasherProtocol.NandWireRecordSize,
            ],
            transport.ReadLengths.Take(3));
        Assert.All(transport.ReadLengths.Skip(3), length => Assert.Equal(1, length));
        Assert.Equal(0, transport.RemainingResponseByteCount);
        AssertFrames(
            transport,
            [
                Command(PicoFlasherCommand.SetSmcWorkaround, 0),
                Command(PicoFlasherCommand.StopSmc, 0),
                Command(PicoFlasherCommand.GetFlashConfiguration, 0),
                Command(PicoFlasherCommand.ReadFlashStream, 0x8000),
                Command(PicoFlasherCommand.ReadFlashStream, 0),
                Command(PicoFlasherCommand.StartSmc, 0),
            ]);

        OperationFailureException followUpFailure = await Assert.ThrowsAsync<OperationFailureException>(
            () => service.ProbeAsync());

        Assert.Equal("pico-stream-recovery-required", followUpFailure.Kind);
        Assert.Equal(ExitCode.InputOutput, followUpFailure.Code);
        Assert.Equal(6, transport.WrittenFrames.Count);
    }

    [Fact]
    public async Task Cancelled_stream_that_cannot_be_reset_retires_connection_before_rethrowing_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var transport = new ScriptedTransport(
            Combine(
                Status(KnownSmallBlockConfiguration),
                Status(0),
                CreateRecord(0xE2)),
            writeFailure: FailNandStreamReset,
            afterRead: readCall =>
            {
                if (readCall == 3)
                {
                    cancellation.Cancel();
                }
            });
        PicoFlasherService service = CreateService(transport);
        using var output = new MemoryStream();

        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ReadNandAsync(
                new PicoFlasherNandReadRequest(output, StartRecord: 0, RecordCount: 2),
                cancellationToken: cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(transport.Disposed);
        AssertFrames(
            transport,
            [
                Command(PicoFlasherCommand.SetSmcWorkaround, 0),
                Command(PicoFlasherCommand.StopSmc, 0),
                Command(PicoFlasherCommand.GetFlashConfiguration, 0),
                Command(PicoFlasherCommand.ReadFlashStream, 2),
                Command(PicoFlasherCommand.ReadFlashStream, 0),
            ]);

        OperationFailureException followUpFailure = await Assert.ThrowsAsync<OperationFailureException>(
            () => service.ProbeAsync());

        Assert.Equal("pico-stream-recovery-required", followUpFailure.Kind);
        Assert.Equal(ExitCode.InputOutput, followUpFailure.Code);
        Assert.Equal(5, transport.WrittenFrames.Count);
    }

    [Theory]
    [InlineData(2, null)]
    [InlineData(2, TransportTimeoutKind)]
    [InlineData(2, TransportClosedKind)]
    [InlineData(2, TransportIoFailureKind)]
    [InlineData(3, null)]
    [InlineData(3, TransportTimeoutKind)]
    [InlineData(3, TransportClosedKind)]
    [InlineData(3, TransportIoFailureKind)]
    public async Task Partial_stream_response_resets_drains_restarts_and_fences_connection(
        int failingReadCall,
        string? failureKind)
    {
        Exception transportFailure = failureKind is null
            ? new IOException("The stream response timed out after an indeterminate partial response.")
            : CreateTransportFailure(failureKind);
        byte[] responseSequence = failingReadCall == 2
            ? Combine(
                Status(KnownSmallBlockConfiguration),
                Status(0))
            : Combine(
                Status(KnownSmallBlockConfiguration),
                Status(0),
                CreateRecord(0x5C));
        var transport = new ScriptedTransport(
            responseSequence,
            readFailure: readCall => readCall == failingReadCall
                ? transportFailure
                : null,
            partialReadByteCount: readCall => readCall == failingReadCall ? 2 : 0,
            exhaustedReadFailure: CreateTransportFailure(TransportTimeoutKind));
        PicoFlasherService service = CreateService(transport);
        using var output = new MemoryStream();

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => service.ReadNandAsync(
                new PicoFlasherNandReadRequest(output, StartRecord: 0, RecordCount: 1)));

        Assert.Equal(ExitCode.InputOutput, failure.Code);
        Assert.Equal(failureKind ?? "pico-nand-read-response-failed", failure.Kind);
        const string expectedContext = "The PicoFlasher NAND read response for record 0 was incomplete or stalled.";
        Assert.Equal(
            failureKind is null ? expectedContext : $"{transportFailure.Message} {expectedContext}",
            failure.Message);
        Assert.Empty(output.ToArray());

        Assert.True(transport.Disposed);
        Assert.Equal(0, transport.RemainingResponseByteCount);
        Assert.All(transport.ReadLengths.Skip(failingReadCall), length => Assert.Equal(1, length));
        AssertFrames(
            transport,
            [
                Command(PicoFlasherCommand.SetSmcWorkaround, 0),
                Command(PicoFlasherCommand.StopSmc, 0),
                Command(PicoFlasherCommand.GetFlashConfiguration, 0),
                Command(PicoFlasherCommand.ReadFlashStream, 1),
                Command(PicoFlasherCommand.ReadFlashStream, 0),
                Command(PicoFlasherCommand.StartSmc, 0),
            ]);

        OperationFailureException followUpFailure = await Assert.ThrowsAsync<OperationFailureException>(
            () => service.ProbeAsync());

        Assert.Equal("pico-stream-recovery-required", followUpFailure.Kind);
        Assert.Equal(ExitCode.InputOutput, followUpFailure.Code);
        Assert.Equal(6, transport.WrittenFrames.Count);
    }

    [Theory]
    [InlineData(false, "reset")]
    [InlineData(false, "drain")]
    [InlineData(false, "restart")]
    [InlineData(true, "reset")]
    [InlineData(true, "drain")]
    [InlineData(true, "restart")]
    public async Task Classified_read_timeout_survives_failed_stream_reset_drain_or_smc_restart(
        bool emmc,
        string cleanupFailure)
    {
        OperationFailureException transportFailure = CreateTransportFailure(TransportTimeoutKind);
        OperationFailureException cleanupTransportFailure = CreateTransportFailure(TransportIoFailureKind);
        PicoFlasherCommand streamCommand = emmc
            ? PicoFlasherCommand.EmmcReadStream
            : PicoFlasherCommand.ReadFlashStream;
        byte[] firstPayload = emmc
            ? CreateBytes(PicoFlasherProtocol.EmmcSectorSize, 0x32)
            : CreateRecord(0x32);
        byte[] secondPayload = emmc
            ? CreateBytes(PicoFlasherProtocol.EmmcSectorSize, 0x89)
            : CreateRecord(0x89);
        int failingReadCall = emmc ? 10 : 5;
        var transport = new ScriptedTransport(
            Combine(
                emmc ? EmmcMetadataResponses() : Status(KnownSmallBlockConfiguration),
                Status(0),
                firstPayload,
                Status(0),
                secondPayload),
            readFailure: readCall => readCall == failingReadCall ? transportFailure : null,
            partialReadByteCount: readCall => readCall == failingReadCall ? 2 : 0,
            writeFailure: frame =>
                (cleanupFailure == "reset" &&
                    frame[0] == (byte)streamCommand &&
                    BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(1)) == 0) ||
                (cleanupFailure == "restart" && frame[0] == (byte)PicoFlasherCommand.StartSmc)
                    ? cleanupTransportFailure
                    : null,
            exhaustedReadFailure: cleanupFailure == "drain"
                ? cleanupTransportFailure
                : CreateTransportFailure(TransportTimeoutKind));
        PicoFlasherService service = CreateService(transport);
        using var output = new MemoryStream();

        Task invocation = emmc
            ? service.ReadEmmcAsync(new PicoFlasherEmmcReadRequest(output, StartSector: 0, SectorCount: 3))
            : service.ReadNandAsync(new PicoFlasherNandReadRequest(output, StartRecord: 0, RecordCount: 3));
        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(() => invocation);

        string expectedContext = emmc
            ? "The PicoFlasher eMMC read response for sector 1 was incomplete or stalled."
            : "The PicoFlasher NAND read response for record 1 was incomplete or stalled.";
        Assert.Equal(ExitCode.InputOutput, failure.Code);
        Assert.Equal(TransportTimeoutKind, failure.Kind);
        Assert.Equal($"{transportFailure.Message} {expectedContext}", failure.Message);
        Assert.Equal<byte>(firstPayload, output.ToArray());
        Assert.True(transport.Disposed);
        List<byte[]> expectedFrames = emmc
            ? EmmcMetadataCommands()
            :
            [
                Command(PicoFlasherCommand.SetSmcWorkaround, 0),
                Command(PicoFlasherCommand.StopSmc, 0),
                Command(PicoFlasherCommand.GetFlashConfiguration, 0),
            ];
        expectedFrames.Add(Command(streamCommand, 3));
        expectedFrames.Add(Command(streamCommand, 0));
        if (cleanupFailure == "reset")
        {
            Assert.Equal(failingReadCall, transport.ReadLengths.Count);
            Assert.NotEqual(0, transport.RemainingResponseByteCount);
        }
        else
        {
            expectedFrames.Add(Command(PicoFlasherCommand.StartSmc, 0));
            Assert.All(transport.ReadLengths.Skip(failingReadCall), length => Assert.Equal(1, length));
            Assert.Equal(0, transport.RemainingResponseByteCount);
        }

        AssertFrames(transport, expectedFrames);
        await AssertRetiredConnectionRejectsFollowUpAsync(service, transport);
    }

    [Theory]
    [InlineData(null, "pico-nand-read-failed", ExitCode.DeviceUnavailable)]
    [InlineData(TransportTimeoutKind, "pico-nand-read-failed", ExitCode.DeviceUnavailable)]
    [InlineData(TransportClosedKind, "pico-stream-recovery-required", ExitCode.InputOutput)]
    [InlineData(TransportIoFailureKind, "pico-stream-recovery-required", ExitCode.InputOutput)]
    public async Task Nand_stream_status_failure_only_accepts_cleanup_timeout_as_quiescence(
        string? cleanupFailureKind,
        string expectedKind,
        ExitCode expectedCode)
    {
        using var cancellation = new CancellationTokenSource();
        byte[] queuedRecord = CreateRecord(0xA4);
        var transport = new ScriptedTransport(
            Combine(
                Status(KnownSmallBlockConfiguration),
                Status(0xBADC_0DEU),
                Status(0),
                queuedRecord),
            awaitCancellationWhenResponsesExhausted: cleanupFailureKind is null,
            exhaustedReadFailure: cleanupFailureKind is null ? null : CreateTransportFailure(cleanupFailureKind));
        PicoFlasherService service = CreateService(transport);
        using var output = new MemoryStream();

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => service.ReadNandAsync(
                new PicoFlasherNandReadRequest(output, StartRecord: 0, RecordCount: 2),
                cancellationToken: cancellation.Token));

        Assert.Equal(expectedCode, failure.Code);
        Assert.Equal(expectedKind, failure.Kind);
        if (expectedKind == "pico-nand-read-failed")
        {
            Assert.Contains("record 0", failure.Message, StringComparison.Ordinal);
        }
        Assert.True(transport.Disposed);
        Assert.Equal(0, transport.RemainingResponseByteCount);
        Assert.All(transport.ReadLengths.Skip(2), length => Assert.Equal(1, length));
        Assert.Equal(2 + PicoFlasherProtocol.StatusSize + queuedRecord.Length + 1, transport.ReadLengths.Count);
        Assert.Empty(output.ToArray());
        AssertCleanupTokens(transport, normalReadCount: 2, normalWriteCount: 4, cancellation.Token);
        Assert.Equal(cleanupFailureKind is null, transport.ReadCancellationTokens[^1].IsCancellationRequested);
        AssertFrames(
            transport,
            [
                Command(PicoFlasherCommand.SetSmcWorkaround, 0),
                Command(PicoFlasherCommand.StopSmc, 0),
                Command(PicoFlasherCommand.GetFlashConfiguration, 0),
                Command(PicoFlasherCommand.ReadFlashStream, 2),
                Command(PicoFlasherCommand.ReadFlashStream, 0),
                Command(PicoFlasherCommand.StartSmc, 0),
            ]);

        await AssertRetiredConnectionRejectsFollowUpAsync(service, transport);
    }

    [Theory]
    [InlineData(0, "pico-nand-read-failed")]
    [InlineData(1, "pico-stream-recovery-required")]
    public async Task Nand_stream_cleanup_probes_quiescence_after_the_queued_byte_bound(
        int excessQueuedByteCount,
        string expectedKind)
    {
        const int maximumQueuedByteCount =
            0x40 * (PicoFlasherProtocol.StatusSize + PicoFlasherProtocol.NandWireRecordSize);
        var transport = new ScriptedTransport(
            Combine(
                Status(KnownSmallBlockConfiguration),
                Status(0xDEAD_BEEFU),
                new byte[maximumQueuedByteCount + excessQueuedByteCount]),
            awaitCancellationWhenResponsesExhausted: true);
        PicoFlasherService service = CreateService(transport);
        using var output = new MemoryStream();

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => service.ReadNandAsync(
                new PicoFlasherNandReadRequest(output, StartRecord: 0, RecordCount: 1)));

        Assert.Equal(expectedKind, failure.Kind);
        Assert.Equal(
            excessQueuedByteCount == 0 ? ExitCode.DeviceUnavailable : ExitCode.InputOutput,
            failure.Code);
        Assert.True(transport.Disposed);
        Assert.Equal(2 + maximumQueuedByteCount + 1, transport.ReadLengths.Count);
        Assert.All(transport.ReadLengths.Skip(2), length => Assert.Equal(1, length));
        Assert.Equal(0, transport.RemainingResponseByteCount);
        AssertFrames(
            transport,
            [
                Command(PicoFlasherCommand.SetSmcWorkaround, 0),
                Command(PicoFlasherCommand.StopSmc, 0),
                Command(PicoFlasherCommand.GetFlashConfiguration, 0),
                Command(PicoFlasherCommand.ReadFlashStream, 1),
                Command(PicoFlasherCommand.ReadFlashStream, 0),
                Command(PicoFlasherCommand.StartSmc, 0),
            ]);
    }

    [Fact]
    public async Task Partial_stream_response_that_cannot_quiesce_retires_and_preserves_located_io_failure()
    {
        var transport = new ScriptedTransport(
            Combine(
                Status(KnownSmallBlockConfiguration),
                Status(0),
                CreateRecord(0x5C)),
            readFailure: readCall => readCall == 3
                ? new IOException("The stream response timed out after an indeterminate partial response.")
                : null,
            partialReadByteCount: readCall => readCall == 3 ? 2 : 0);
        PicoFlasherService service = CreateService(transport);
        using var output = new MemoryStream();

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => service.ReadNandAsync(
                new PicoFlasherNandReadRequest(output, StartRecord: 0, RecordCount: 1)));

        Assert.Equal(ExitCode.InputOutput, failure.Code);
        Assert.Equal("pico-nand-read-response-failed", failure.Kind);
        Assert.Equal("The PicoFlasher NAND read response for record 0 was incomplete or stalled.", failure.Message);
        Assert.True(transport.Disposed);
        Assert.Equal(0, transport.RemainingResponseByteCount);
        AssertFrames(
            transport,
            [
                Command(PicoFlasherCommand.SetSmcWorkaround, 0),
                Command(PicoFlasherCommand.StopSmc, 0),
                Command(PicoFlasherCommand.GetFlashConfiguration, 0),
                Command(PicoFlasherCommand.ReadFlashStream, 1),
                Command(PicoFlasherCommand.ReadFlashStream, 0),
                Command(PicoFlasherCommand.StartSmc, 0),
            ]);

        OperationFailureException followUpFailure = await Assert.ThrowsAsync<OperationFailureException>(
            () => service.ProbeAsync());

        Assert.Equal("pico-stream-recovery-required", followUpFailure.Kind);
        Assert.Equal(ExitCode.InputOutput, followUpFailure.Code);
        Assert.Equal(6, transport.WrittenFrames.Count);
    }

    [Theory]
    [InlineData(TransportTimeoutKind)]
    [InlineData(TransportClosedKind)]
    [InlineData(TransportIoFailureKind)]
    public async Task Classified_stream_start_write_retires_without_reset_or_restart(string failureKind)
    {
        OperationFailureException transportFailure = CreateTransportFailure(failureKind);
        var transport = new ScriptedTransport(
            Status(KnownSmallBlockConfiguration),
            writeFailure: frame => frame[0] == (byte)PicoFlasherCommand.ReadFlashStream
                ? transportFailure
                : null);
        PicoFlasherService service = CreateService(transport);
        using var output = new MemoryStream();

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => service.ReadNandAsync(
                new PicoFlasherNandReadRequest(output, StartRecord: 0, RecordCount: 1)));

        AssertTransportFailure(transportFailure, failure);
        Assert.True(transport.Disposed);
        AssertFrames(
            transport,
            [
                Command(PicoFlasherCommand.SetSmcWorkaround, 0),
                Command(PicoFlasherCommand.StopSmc, 0),
                Command(PicoFlasherCommand.GetFlashConfiguration, 0),
                Command(PicoFlasherCommand.ReadFlashStream, 1),
            ]);

        OperationFailureException followUpFailure = await Assert.ThrowsAsync<OperationFailureException>(
            () => service.ProbeAsync());

        Assert.Equal("pico-stream-recovery-required", followUpFailure.Kind);
        Assert.Equal(ExitCode.InputOutput, followUpFailure.Code);
        Assert.Equal(4, transport.WrittenFrames.Count);
    }

    [Theory]
    [InlineData(false, TransportTimeoutKind)]
    [InlineData(false, TransportClosedKind)]
    [InlineData(false, TransportIoFailureKind)]
    [InlineData(true, TransportTimeoutKind)]
    [InlineData(true, TransportClosedKind)]
    [InlineData(true, TransportIoFailureKind)]
    public async Task Classified_final_smc_restart_write_retires_without_retrying(
        bool emmc,
        string failureKind)
    {
        OperationFailureException transportFailure = CreateTransportFailure(failureKind);
        byte[] payload = emmc
            ? CreateBytes(PicoFlasherProtocol.EmmcSectorSize, 0x93)
            : CreateRecord(0x93);
        var transport = new ScriptedTransport(
            Combine(
                emmc ? EmmcMetadataResponses() : Status(KnownSmallBlockConfiguration),
                Status(0),
                payload),
            writeFailure: frame => frame[0] == (byte)PicoFlasherCommand.StartSmc ? transportFailure : null);
        PicoFlasherService service = CreateService(transport);
        using var output = new MemoryStream();

        Task invocation = emmc
            ? service.ReadEmmcAsync(new PicoFlasherEmmcReadRequest(output, StartSector: 0, SectorCount: 1))
            : service.ReadNandAsync(new PicoFlasherNandReadRequest(output, StartRecord: 0, RecordCount: 1));
        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(() => invocation);

        AssertTransportFailure(transportFailure, failure);
        Assert.True(transport.Disposed);
        Assert.Equal<byte>(payload, output.ToArray());
        Assert.Equal(0, transport.RemainingResponseByteCount);
        List<byte[]> expectedFrames = emmc
            ? EmmcMetadataCommands()
            :
            [
                Command(PicoFlasherCommand.SetSmcWorkaround, 0),
                Command(PicoFlasherCommand.StopSmc, 0),
                Command(PicoFlasherCommand.GetFlashConfiguration, 0),
            ];
        expectedFrames.Add(Command(
            emmc ? PicoFlasherCommand.EmmcReadStream : PicoFlasherCommand.ReadFlashStream,
            1));
        expectedFrames.Add(Command(PicoFlasherCommand.StartSmc, 0));
        AssertFrames(transport, expectedFrames);
        await AssertRetiredConnectionRejectsFollowUpAsync(service, transport);
    }

    [Theory]
    [InlineData((byte)PicoFlasherCommand.SetSmcWorkaround, TransportTimeoutKind)]
    [InlineData((byte)PicoFlasherCommand.SetSmcWorkaround, TransportClosedKind)]
    [InlineData((byte)PicoFlasherCommand.SetSmcWorkaround, TransportIoFailureKind)]
    [InlineData((byte)PicoFlasherCommand.StopSmc, TransportTimeoutKind)]
    [InlineData((byte)PicoFlasherCommand.StopSmc, TransportClosedKind)]
    [InlineData((byte)PicoFlasherCommand.StopSmc, TransportIoFailureKind)]
    public async Task Classified_smc_preflight_write_retires_without_later_commands(
        byte failingOpcode,
        string failureKind)
    {
        OperationFailureException transportFailure = CreateTransportFailure(failureKind);
        var transport = new ScriptedTransport(
            [],
            writeFailure: frame => frame[0] == failingOpcode
                ? transportFailure
                : null);
        PicoFlasherService service = CreateService(transport);

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => service.ProbeAsync());

        AssertTransportFailure(transportFailure, failure);
        Assert.True(transport.Disposed);

        var expectedFrames = new List<byte[]>
        {
            Command(PicoFlasherCommand.SetSmcWorkaround, 0),
        };
        if (failingOpcode == (byte)PicoFlasherCommand.StopSmc)
        {
            expectedFrames.Add(Command(PicoFlasherCommand.StopSmc, 0));
        }

        AssertFrames(transport, expectedFrames);

        OperationFailureException followUpFailure = await Assert.ThrowsAsync<OperationFailureException>(
            () => service.ProbeAsync());

        Assert.Equal("pico-stream-recovery-required", followUpFailure.Kind);
        Assert.Equal(ExitCode.InputOutput, followUpFailure.Code);
        Assert.Equal(expectedFrames.Count, transport.WrittenFrames.Count);
    }

    [Theory]
    [InlineData("probe", TransportTimeoutKind)]
    [InlineData("probe", TransportClosedKind)]
    [InlineData("probe", TransportIoFailureKind)]
    [InlineData("read", TransportTimeoutKind)]
    [InlineData("read", TransportClosedKind)]
    [InlineData("read", TransportIoFailureKind)]
    [InlineData("write", TransportTimeoutKind)]
    [InlineData("write", TransportClosedKind)]
    [InlineData("write", TransportIoFailureKind)]
    [InlineData("erase", TransportTimeoutKind)]
    [InlineData("erase", TransportClosedKind)]
    [InlineData("erase", TransportIoFailureKind)]
    public async Task Classified_flash_configuration_write_retires_without_later_commands(
        string operation,
        string failureKind)
    {
        OperationFailureException transportFailure = CreateTransportFailure(failureKind);
        var transport = new ScriptedTransport(
            [],
            writeFailure: frame => frame[0] == (byte)PicoFlasherCommand.GetFlashConfiguration
                ? transportFailure
                : null);
        PicoFlasherService service = CreateService(transport);
        using var output = new MemoryStream();
        using var input = CreateNandInput(SmallBlockEraseRecordCount);

        Task invocation = operation switch
        {
            "probe" => service.ProbeAsync(),
            "read" => service.ReadNandAsync(
                new PicoFlasherNandReadRequest(output, StartRecord: 1, RecordCount: 1)),
            "write" => service.WriteNandAsync(
                new PicoFlasherNandWriteRequest(input, StartRecord: 0)),
            "erase" => service.EraseNandAsync(
                new PicoFlasherNandEraseRequest(StartEraseBlock: 0, EraseBlockCount: 1)),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown operation."),
        };

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(() => invocation);

        AssertTransportFailure(transportFailure, failure);
        Assert.True(transport.Disposed);
        AssertFrames(
            transport,
            [
                Command(PicoFlasherCommand.SetSmcWorkaround, 0),
                Command(PicoFlasherCommand.StopSmc, 0),
                Command(PicoFlasherCommand.GetFlashConfiguration, 0),
            ]);

        OperationFailureException followUpFailure = await Assert.ThrowsAsync<OperationFailureException>(
            () => service.ProbeAsync());

        Assert.Equal("pico-stream-recovery-required", followUpFailure.Kind);
        Assert.Equal(ExitCode.InputOutput, followUpFailure.Code);
        Assert.Equal(3, transport.WrittenFrames.Count);
    }

    [Theory]
    [InlineData("range-read", TransportTimeoutKind)]
    [InlineData("range-read", TransportClosedKind)]
    [InlineData("range-read", TransportIoFailureKind)]
    [InlineData("write", TransportTimeoutKind)]
    [InlineData("write", TransportClosedKind)]
    [InlineData("write", TransportIoFailureKind)]
    [InlineData("erase", TransportTimeoutKind)]
    [InlineData("erase", TransportClosedKind)]
    [InlineData("erase", TransportIoFailureKind)]
    public async Task Classified_nand_command_write_retires_without_later_commands(
        string operation,
        string failureKind)
    {
        OperationFailureException transportFailure = CreateTransportFailure(failureKind);
        PicoFlasherCommand failingCommand = operation switch
        {
            "range-read" => PicoFlasherCommand.ReadFlash,
            "write" => PicoFlasherCommand.WriteFlash,
            "erase" => PicoFlasherCommand.EraseFlash,
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown operation."),
        };
        var transport = new ScriptedTransport(
            Status(KnownSmallBlockConfiguration),
            writeFailure: frame => frame[0] == (byte)failingCommand
                ? transportFailure
                : null);
        PicoFlasherService service = CreateService(transport);
        using var output = new MemoryStream();
        using var input = CreateNandInput(SmallBlockEraseRecordCount);

        Task invocation = operation switch
        {
            "range-read" => service.ReadNandAsync(
                new PicoFlasherNandReadRequest(output, StartRecord: 1, RecordCount: 1)),
            "write" => service.WriteNandAsync(
                new PicoFlasherNandWriteRequest(input, StartRecord: 0)),
            "erase" => service.EraseNandAsync(
                new PicoFlasherNandEraseRequest(StartEraseBlock: 0, EraseBlockCount: 1)),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown operation."),
        };

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(() => invocation);

        AssertTransportFailure(transportFailure, failure);
        Assert.True(transport.Disposed);
        var expectedFrames = new List<byte[]>
        {
            Command(PicoFlasherCommand.SetSmcWorkaround, 0),
            Command(PicoFlasherCommand.StopSmc, 0),
            Command(PicoFlasherCommand.GetFlashConfiguration, 0),
        };
        expectedFrames.Add(
            failingCommand == PicoFlasherCommand.WriteFlash
                ? WriteRecordFrame(0, new byte[PicoFlasherProtocol.NandWireRecordSize])
                : Command(failingCommand, failingCommand == PicoFlasherCommand.ReadFlash ? 1U : 0U));
        AssertFrames(transport, expectedFrames);

        OperationFailureException followUpFailure = await Assert.ThrowsAsync<OperationFailureException>(
            () => service.ProbeAsync());

        Assert.Equal("pico-stream-recovery-required", followUpFailure.Kind);
        Assert.Equal(ExitCode.InputOutput, followUpFailure.Code);
        Assert.Equal(4, transport.WrittenFrames.Count);
    }

    [Fact]
    public async Task Emmc_probe_reads_full_metadata_and_restarts_smc()
    {
        const uint flashConfiguration = 0xC046_2002U;
        byte[] cid = CreateBytes(PicoFlasherProtocol.EmmcCidSize, 0x13);
        byte[] csd = CreateBytes(PicoFlasherProtocol.EmmcCsdSize, 0x37);
        byte[] extendedCsd = CreateEmmcExtendedCsd(PicoFlasherProtocol.EmmcLegacyReadWindowSectorCount, 0x5B);
        var transport = new ScriptedTransport(
            Combine(
                Status(flashConfiguration),
                [1],
                Status(0),
                cid,
                csd,
                extendedCsd));
        PicoFlasherService service = CreateService(transport);

        PicoFlasherEmmcProbeResult result = await service.ProbeEmmcAsync();

        Assert.Equal(PicoFlasherProtocol.MinimumSupportedFirmwareVersion, result.FirmwareVersion);
        Assert.Equal(TestDevicePath, result.DevicePath);
        Assert.Equal(TestSerialNumber, result.SerialNumber);
        Assert.Equal(flashConfiguration, result.FlashConfiguration);
        Assert.Equal(PicoFlasherProtocol.EmmcLegacyReadWindowSectorCount, result.CapacitySectorCount);
        Assert.Equal<byte>(cid, result.Cid);
        Assert.Equal<byte>(csd, result.Csd);
        Assert.Equal<byte>(extendedCsd, result.ExtendedCsd);
        Assert.Equal<int>(
            [
                PicoFlasherProtocol.StatusSize,
                1,
                PicoFlasherProtocol.StatusSize,
                PicoFlasherProtocol.EmmcCidSize,
                PicoFlasherProtocol.EmmcCsdSize,
                PicoFlasherProtocol.EmmcExtendedCsdSize,
            ],
            transport.ReadLengths);
        AssertFrames(
            transport,
            [
                Command(PicoFlasherCommand.SetSmcWorkaround, 0),
                Command(PicoFlasherCommand.StopSmc, 0),
                Command(PicoFlasherCommand.GetFlashConfiguration, 0),
                Command(PicoFlasherCommand.EmmcDetect, 0),
                Command(PicoFlasherCommand.EmmcInitialize, 0),
                Command(PicoFlasherCommand.EmmcGetCid, 0),
                Command(PicoFlasherCommand.EmmcGetCsd, 0),
                Command(PicoFlasherCommand.EmmcGetExtendedCsd, 0),
                Command(PicoFlasherCommand.StartSmc, 0),
            ]);
        Assert.Equal(0, transport.RemainingResponseByteCount);
    }

    [Fact]
    public async Task Emmc_origin_read_uses_one_stream_and_writes_exact_sector_data()
    {
        byte[] firstSector = CreateBytes(PicoFlasherProtocol.EmmcSectorSize, 0x21);
        byte[] secondSector = CreateBytes(PicoFlasherProtocol.EmmcSectorSize, 0x72);
        var transport = new ScriptedTransport(
            Combine(
                EmmcMetadataResponses(),
                Status(0),
                firstSector,
                Status(0),
                secondSector));
        PicoFlasherService service = CreateService(transport);
        using var output = new MemoryStream();

        PicoFlasherEmmcReadResult result = await service.ReadEmmcAsync(
            new PicoFlasherEmmcReadRequest(output, StartSector: 0, SectorCount: 2));

        Assert.Equal(0xC046_2002U, result.FlashConfiguration);
        Assert.Equal(PicoFlasherProtocol.EmmcLegacyReadWindowSectorCount, result.CapacitySectorCount);
        Assert.Equal(0U, result.StartSector);
        Assert.Equal(2U, result.SectorCount);
        Assert.Equal((long)(2 * PicoFlasherProtocol.EmmcSectorSize), result.LogicalByteLength);
        Assert.Equal<byte>(Combine(firstSector, secondSector), output.ToArray());
        AssertFrames(
            transport,
            [
                Command(PicoFlasherCommand.SetSmcWorkaround, 0),
                Command(PicoFlasherCommand.StopSmc, 0),
                Command(PicoFlasherCommand.GetFlashConfiguration, 0),
                Command(PicoFlasherCommand.EmmcDetect, 0),
                Command(PicoFlasherCommand.EmmcInitialize, 0),
                Command(PicoFlasherCommand.EmmcGetCid, 0),
                Command(PicoFlasherCommand.EmmcGetCsd, 0),
                Command(PicoFlasherCommand.EmmcGetExtendedCsd, 0),
                Command(PicoFlasherCommand.EmmcReadStream, 2),
                Command(PicoFlasherCommand.StartSmc, 0),
            ]);
        Assert.DoesNotContain(
            transport.WrittenFrames,
            frame => frame[0] == (byte)PicoFlasherCommand.EmmcRead);
        Assert.Equal(0, transport.RemainingResponseByteCount);
    }

    [Fact]
    public async Task Emmc_nonzero_read_uses_individual_absolute_sector_addresses()
    {
        using var cancellation = new CancellationTokenSource();
        byte[] firstSector = CreateBytes(PicoFlasherProtocol.EmmcSectorSize, 0x39);
        byte[] secondSector = CreateBytes(PicoFlasherProtocol.EmmcSectorSize, 0x8A);
        var transport = new ScriptedTransport(
            Combine(
                EmmcMetadataResponses(),
                Status(0),
                firstSector,
                Status(0),
                secondSector));
        PicoFlasherService service = CreateService(transport);
        using var output = new MemoryStream();

        PicoFlasherEmmcReadResult result = await service.ReadEmmcAsync(
            new PicoFlasherEmmcReadRequest(output, StartSector: 0x20, SectorCount: 2),
            cancellationToken: cancellation.Token);

        Assert.Equal(0x20U, result.StartSector);
        Assert.Equal(2U, result.SectorCount);
        Assert.Equal<byte>(Combine(firstSector, secondSector), output.ToArray());
        Assert.Equal(10, transport.ReadCancellationTokens.Count);
        Assert.All(
            transport.ReadCancellationTokens,
            token => Assert.Equal(cancellation.Token, token));
        AssertFrames(
            transport,
            [
                Command(PicoFlasherCommand.SetSmcWorkaround, 0),
                Command(PicoFlasherCommand.StopSmc, 0),
                Command(PicoFlasherCommand.GetFlashConfiguration, 0),
                Command(PicoFlasherCommand.EmmcDetect, 0),
                Command(PicoFlasherCommand.EmmcInitialize, 0),
                Command(PicoFlasherCommand.EmmcGetCid, 0),
                Command(PicoFlasherCommand.EmmcGetCsd, 0),
                Command(PicoFlasherCommand.EmmcGetExtendedCsd, 0),
                Command(PicoFlasherCommand.EmmcRead, 0x20),
                Command(PicoFlasherCommand.EmmcRead, 0x21),
                Command(PicoFlasherCommand.StartSmc, 0),
            ]);
        Assert.DoesNotContain(
            transport.WrittenFrames,
            frame => frame[0] == (byte)PicoFlasherCommand.EmmcReadStream);
        Assert.Equal(0, transport.RemainingResponseByteCount);
    }

    [Theory]
    [InlineData(0U, "pico-emmc-not-detected")]
    [InlineData(1U, "pico-emmc-initialize-failed")]
    public async Task Emmc_detect_or_initialize_failure_stops_before_metadata(
        uint failureValue,
        string expectedKind)
    {
        byte[] responses = expectedKind == "pico-emmc-not-detected"
            ? Combine(Status(0xC046_2002U), [(byte)failureValue])
            : Combine(Status(0xC046_2002U), [1], Status(failureValue));
        var transport = new ScriptedTransport(responses);
        PicoFlasherService service = CreateService(transport);

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => service.ProbeEmmcAsync());

        Assert.Equal(ExitCode.DeviceUnavailable, failure.Code);
        Assert.Equal(expectedKind, failure.Kind);
        Assert.DoesNotContain(
            transport.WrittenFrames,
            frame => frame[0] is (byte)PicoFlasherCommand.EmmcGetCid or
                (byte)PicoFlasherCommand.EmmcGetCsd or
                (byte)PicoFlasherCommand.EmmcGetExtendedCsd or
                (byte)PicoFlasherCommand.EmmcRead or
                (byte)PicoFlasherCommand.EmmcReadStream);
        Assert.Equal<byte>(Command(PicoFlasherCommand.StartSmc, 0), transport.WrittenFrames[^1]);
    }

    [Theory]
    [InlineData("cid", "pico-emmc-cid-invalid")]
    [InlineData("csd", "pico-emmc-csd-invalid")]
    public async Task All_zero_emmc_identity_stops_before_later_metadata_or_data_commands(
        string identity,
        string expectedKind)
    {
        byte[] cid = identity == "cid"
            ? new byte[PicoFlasherProtocol.EmmcCidSize]
            : CreateBytes(PicoFlasherProtocol.EmmcCidSize, 0x14);
        byte[] csd = identity == "csd"
            ? new byte[PicoFlasherProtocol.EmmcCsdSize]
            : CreateBytes(PicoFlasherProtocol.EmmcCsdSize, 0x48);
        byte[] responses = identity == "cid"
            ? Combine(Status(0xC046_2002U), [1], Status(0), cid)
            : Combine(Status(0xC046_2002U), [1], Status(0), cid, csd);
        var transport = new ScriptedTransport(responses);
        PicoFlasherService service = CreateService(transport);
        using var output = new MemoryStream();

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => service.ReadEmmcAsync(
                new PicoFlasherEmmcReadRequest(output, StartSector: 0, SectorCount: 1)));

        Assert.Equal(ExitCode.InvalidData, failure.Code);
        Assert.Equal(expectedKind, failure.Kind);
        Assert.Empty(output.ToArray());
        Assert.DoesNotContain(
            transport.WrittenFrames,
            frame => frame[0] is (byte)PicoFlasherCommand.EmmcGetExtendedCsd or
                (byte)PicoFlasherCommand.EmmcRead or
                (byte)PicoFlasherCommand.EmmcReadStream);
        var expectedFrames = new List<byte[]>
        {
            Command(PicoFlasherCommand.SetSmcWorkaround, 0),
            Command(PicoFlasherCommand.StopSmc, 0),
            Command(PicoFlasherCommand.GetFlashConfiguration, 0),
            Command(PicoFlasherCommand.EmmcDetect, 0),
            Command(PicoFlasherCommand.EmmcInitialize, 0),
            Command(PicoFlasherCommand.EmmcGetCid, 0),
        };
        if (identity == "csd")
        {
            expectedFrames.Add(Command(PicoFlasherCommand.EmmcGetCsd, 0));
        }

        expectedFrames.Add(Command(PicoFlasherCommand.StartSmc, 0));
        AssertFrames(transport, expectedFrames);
        Assert.Equal(0, transport.RemainingResponseByteCount);
    }

    [Theory]
    [InlineData(0U)]
    [InlineData(0x17FFFU)]
    [InlineData(0x800000U)]
    public async Task Invalid_emmc_capacity_stops_before_data_commands(uint capacitySectorCount)
    {
        var transport = new ScriptedTransport(EmmcMetadataResponses(capacitySectorCount));
        PicoFlasherService service = CreateService(transport);
        using var output = new MemoryStream();

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => service.ReadEmmcAsync(
                new PicoFlasherEmmcReadRequest(output, StartSector: 0, SectorCount: 1)));

        Assert.Equal(ExitCode.InvalidData, failure.Code);
        Assert.Equal("pico-emmc-capacity-invalid", failure.Kind);
        Assert.DoesNotContain(
            transport.WrittenFrames,
            frame => frame[0] is (byte)PicoFlasherCommand.EmmcRead or
                (byte)PicoFlasherCommand.EmmcReadStream);
        Assert.Equal<byte>(Command(PicoFlasherCommand.StartSmc, 0), transport.WrittenFrames[^1]);
    }

    [Fact]
    public async Task Emmc_partial_metadata_response_restarts_retires_and_requires_recovery()
    {
        byte[] cid = CreateBytes(PicoFlasherProtocol.EmmcCidSize, 0x12);
        byte[] csd = CreateBytes(PicoFlasherProtocol.EmmcCsdSize, 0x47);
        var transport = new ScriptedTransport(
            Combine(
                Status(0xC046_2002U),
                [1],
                Status(0),
                cid,
                csd),
            readFailure: readCall => readCall == 5
                ? new IOException("The CSD response ended after a partial frame.")
                : null,
            partialReadByteCount: readCall => readCall == 5 ? 7 : 0);
        PicoFlasherService service = CreateService(transport);

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => service.ProbeEmmcAsync());

        Assert.Equal("pico-stream-recovery-required", failure.Kind);
        Assert.Equal(ExitCode.InputOutput, failure.Code);
        Assert.True(transport.Disposed);
        AssertFrames(
            transport,
            [
                Command(PicoFlasherCommand.SetSmcWorkaround, 0),
                Command(PicoFlasherCommand.StopSmc, 0),
                Command(PicoFlasherCommand.GetFlashConfiguration, 0),
                Command(PicoFlasherCommand.EmmcDetect, 0),
                Command(PicoFlasherCommand.EmmcInitialize, 0),
                Command(PicoFlasherCommand.EmmcGetCid, 0),
                Command(PicoFlasherCommand.EmmcGetCsd, 0),
                Command(PicoFlasherCommand.StartSmc, 0),
            ]);
    }

    [Theory]
    [MemberData(nameof(EmmcMetadataTransportFailureCases))]
    public async Task Classified_emmc_metadata_response_restarts_retires_and_preserves_transport_failure(
        bool readOperation,
        int failingReadCall,
        string failureKind)
    {
        OperationFailureException transportFailure = CreateTransportFailure(failureKind);
        var transport = new ScriptedTransport(
            EmmcMetadataResponses(),
            readFailure: readCall => readCall == failingReadCall ? transportFailure : null,
            partialReadByteCount: readCall => readCall == failingReadCall && failingReadCall != 2 ? 2 : 0);
        PicoFlasherService service = CreateService(transport);
        using var output = new MemoryStream();

        Task invocation = readOperation
            ? service.ReadEmmcAsync(new PicoFlasherEmmcReadRequest(output, StartSector: 0, SectorCount: 1))
            : service.ProbeEmmcAsync();
        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(() => invocation);

        AssertTransportFailure(transportFailure, failure);
        Assert.True(transport.Disposed);
        Assert.Empty(output.ToArray());
        Assert.Equal(failingReadCall, transport.ReadLengths.Count);
        Assert.NotEqual(0, transport.RemainingResponseByteCount);
        List<byte[]> expectedFrames = EmmcMetadataCommands().Take(failingReadCall + 2).ToList();
        expectedFrames.Add(Command(PicoFlasherCommand.StartSmc, 0));
        AssertFrames(transport, expectedFrames);
        await AssertRetiredConnectionRejectsFollowUpAsync(service, transport);
    }

    [Theory]
    [MemberData(nameof(EmmcReadResponseFailureCases))]
    public async Task Emmc_read_response_framing_reports_the_exact_incomplete_sector_and_retires(
        bool streamed,
        bool payloadFailure,
        int partialByteCount,
        string? failureKind)
    {
        uint startSector = streamed ? 0U : 34U;
        uint failedSector = startSector + 1;
        int failingReadCall = payloadFailure ? 10 : 9;
        byte[] firstSector = CreateBytes(PicoFlasherProtocol.EmmcSectorSize, 0x31);
        byte[] secondSector = CreateBytes(PicoFlasherProtocol.EmmcSectorSize, 0x86);
        Exception transportFailure = failureKind is null
            ? new IOException("The eMMC read response stopped before the requested frame was complete.")
            : CreateTransportFailure(failureKind);
        var transport = new ScriptedTransport(
            Combine(
                EmmcMetadataResponses(),
                Status(0),
                firstSector,
                Status(0),
                secondSector),
            readFailure: readCall => readCall == failingReadCall ? transportFailure : null,
            partialReadByteCount: readCall => readCall == failingReadCall ? partialByteCount : 0,
            exhaustedReadFailure: CreateTransportFailure(TransportTimeoutKind));
        PicoFlasherService service = CreateService(transport);
        using var output = new MemoryStream();

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => service.ReadEmmcAsync(
                new PicoFlasherEmmcReadRequest(output, startSector, SectorCount: 3)));

        string expectedContext =
            $"The PicoFlasher eMMC read response for sector {failedSector} was incomplete or stalled.";
        Assert.Equal(ExitCode.InputOutput, failure.Code);
        Assert.Equal(failureKind ?? "pico-emmc-read-response-failed", failure.Kind);
        Assert.Equal(
            failureKind is null ? expectedContext : $"{transportFailure.Message} {expectedContext}",
            failure.Message);
        Assert.Equal<byte>(firstSector, output.ToArray());
        Assert.True(transport.Disposed);
        Assert.Equal(
            payloadFailure ? PicoFlasherProtocol.EmmcSectorSize : PicoFlasherProtocol.StatusSize,
            transport.ReadLengths[failingReadCall - 1]);
        List<byte[]> expectedFrames = EmmcMetadataCommands();
        if (streamed)
        {
            expectedFrames.Add(Command(PicoFlasherCommand.EmmcReadStream, 3));
            expectedFrames.Add(Command(PicoFlasherCommand.EmmcReadStream, 0));
            Assert.All(transport.ReadLengths.Skip(failingReadCall), length => Assert.Equal(1, length));
            Assert.Equal(0, transport.RemainingResponseByteCount);
            Assert.False(transport.ReadCancellationTokens[^1].IsCancellationRequested);
        }
        else
        {
            expectedFrames.Add(Command(PicoFlasherCommand.EmmcRead, startSector));
            expectedFrames.Add(Command(PicoFlasherCommand.EmmcRead, failedSector));
            Assert.Equal(failingReadCall, transport.ReadLengths.Count);
            Assert.NotEqual(0, transport.RemainingResponseByteCount);
        }

        expectedFrames.Add(Command(PicoFlasherCommand.StartSmc, 0));
        AssertFrames(transport, expectedFrames);
        await AssertRetiredConnectionRejectsFollowUpAsync(service, transport);
    }

    [Fact]
    public async Task Cancelled_emmc_stream_resets_drains_restarts_and_retires_connection()
    {
        using var cancellation = new CancellationTokenSource();
        byte[] firstSector = CreateBytes(PicoFlasherProtocol.EmmcSectorSize, 0x28);
        byte[] queuedSector = CreateBytes(PicoFlasherProtocol.EmmcSectorSize, 0xC3);
        var transport = new ScriptedTransport(
            Combine(
                EmmcMetadataResponses(),
                Status(0),
                firstSector,
                Status(0),
                queuedSector),
            afterRead: readCall =>
            {
                if (readCall == 8)
                {
                    cancellation.Cancel();
                }
            },
            awaitCancellationWhenResponsesExhausted: true);
        PicoFlasherService service = CreateService(transport);
        using var output = new MemoryStream();

        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ReadEmmcAsync(
                new PicoFlasherEmmcReadRequest(output, StartSector: 0, SectorCount: 2),
                cancellationToken: cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(transport.Disposed);
        Assert.All(transport.ReadLengths.Skip(8), length => Assert.Equal(1, length));
        Assert.Equal(0, transport.RemainingResponseByteCount);
        AssertFrames(
            transport,
            [
                Command(PicoFlasherCommand.SetSmcWorkaround, 0),
                Command(PicoFlasherCommand.StopSmc, 0),
                Command(PicoFlasherCommand.GetFlashConfiguration, 0),
                Command(PicoFlasherCommand.EmmcDetect, 0),
                Command(PicoFlasherCommand.EmmcInitialize, 0),
                Command(PicoFlasherCommand.EmmcGetCid, 0),
                Command(PicoFlasherCommand.EmmcGetCsd, 0),
                Command(PicoFlasherCommand.EmmcGetExtendedCsd, 0),
                Command(PicoFlasherCommand.EmmcReadStream, 2),
                Command(PicoFlasherCommand.EmmcReadStream, 0),
                Command(PicoFlasherCommand.StartSmc, 0),
            ]);
    }

    [Theory]
    [InlineData(null, "pico-emmc-read-failed", ExitCode.DeviceUnavailable)]
    [InlineData(TransportTimeoutKind, "pico-emmc-read-failed", ExitCode.DeviceUnavailable)]
    [InlineData(TransportClosedKind, "pico-stream-recovery-required", ExitCode.InputOutput)]
    [InlineData(TransportIoFailureKind, "pico-stream-recovery-required", ExitCode.InputOutput)]
    public async Task Emmc_stream_status_failure_only_accepts_cleanup_timeout_as_quiescence(
        string? cleanupFailureKind,
        string expectedKind,
        ExitCode expectedCode)
    {
        using var cancellation = new CancellationTokenSource();
        byte[] queuedSector = CreateBytes(PicoFlasherProtocol.EmmcSectorSize, 0xC7);
        var transport = new ScriptedTransport(
            Combine(
                EmmcMetadataResponses(),
                Status(0xCAFE_BABEU),
                Status(0),
                queuedSector),
            awaitCancellationWhenResponsesExhausted: cleanupFailureKind is null,
            exhaustedReadFailure: cleanupFailureKind is null ? null : CreateTransportFailure(cleanupFailureKind));
        PicoFlasherService service = CreateService(transport);
        using var output = new MemoryStream();

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => service.ReadEmmcAsync(
                new PicoFlasherEmmcReadRequest(output, StartSector: 0, SectorCount: 2),
                cancellationToken: cancellation.Token));

        Assert.Equal(expectedCode, failure.Code);
        Assert.Equal(expectedKind, failure.Kind);
        if (expectedKind == "pico-emmc-read-failed")
        {
            Assert.Contains("sector 0", failure.Message, StringComparison.Ordinal);
        }
        Assert.True(transport.Disposed);
        Assert.Equal(0, transport.RemainingResponseByteCount);
        Assert.All(transport.ReadLengths.Skip(7), length => Assert.Equal(1, length));
        Assert.Equal(7 + PicoFlasherProtocol.StatusSize + queuedSector.Length + 1, transport.ReadLengths.Count);
        Assert.Empty(output.ToArray());
        AssertCleanupTokens(transport, normalReadCount: 7, normalWriteCount: 9, cancellation.Token);
        Assert.Equal(cleanupFailureKind is null, transport.ReadCancellationTokens[^1].IsCancellationRequested);
        AssertFrames(
            transport,
            [
                Command(PicoFlasherCommand.SetSmcWorkaround, 0),
                Command(PicoFlasherCommand.StopSmc, 0),
                Command(PicoFlasherCommand.GetFlashConfiguration, 0),
                Command(PicoFlasherCommand.EmmcDetect, 0),
                Command(PicoFlasherCommand.EmmcInitialize, 0),
                Command(PicoFlasherCommand.EmmcGetCid, 0),
                Command(PicoFlasherCommand.EmmcGetCsd, 0),
                Command(PicoFlasherCommand.EmmcGetExtendedCsd, 0),
                Command(PicoFlasherCommand.EmmcReadStream, 2),
                Command(PicoFlasherCommand.EmmcReadStream, 0),
                Command(PicoFlasherCommand.StartSmc, 0),
            ]);
        await AssertRetiredConnectionRejectsFollowUpAsync(service, transport);
    }

    [Theory]
    [InlineData(0U, 0U)]
    [InlineData(0x18000U, 1U)]
    [InlineData(0x17FFFU, 2U)]
    [InlineData(uint.MaxValue, 1U)]
    public async Task Invalid_emmc_read_range_stops_before_data_commands(uint startSector, uint sectorCount)
    {
        var transport = new ScriptedTransport(EmmcMetadataResponses());
        PicoFlasherService service = CreateService(transport);
        using var output = new MemoryStream();

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => service.ReadEmmcAsync(
                new PicoFlasherEmmcReadRequest(output, startSector, sectorCount)));

        Assert.Equal(ExitCode.Usage, failure.Code);
        Assert.Equal("pico-emmc-range-invalid", failure.Kind);
        Assert.DoesNotContain(
            transport.WrittenFrames,
            frame => frame[0] is (byte)PicoFlasherCommand.EmmcRead or
                (byte)PicoFlasherCommand.EmmcReadStream);
        Assert.Equal<byte>(Command(PicoFlasherCommand.StartSmc, 0), transport.WrittenFrames[^1]);
    }

    [Fact]
    public async Task Emmc_individual_read_nonzero_status_does_not_consume_a_sector_payload()
    {
        var transport = new ScriptedTransport(
            Combine(
                EmmcMetadataResponses(),
                Status(0xFFFF_FFFFU)));
        PicoFlasherService service = CreateService(transport);
        using var output = new MemoryStream();

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => service.ReadEmmcAsync(
                new PicoFlasherEmmcReadRequest(output, StartSector: 0x22, SectorCount: 1)));

        Assert.Equal("pico-emmc-read-failed", failure.Kind);
        Assert.Contains("sector 34", failure.Message, StringComparison.Ordinal);
        Assert.Equal(PicoFlasherProtocol.StatusSize, transport.ReadLengths[^1]);
        AssertFrames(
            transport,
            [
                Command(PicoFlasherCommand.SetSmcWorkaround, 0),
                Command(PicoFlasherCommand.StopSmc, 0),
                Command(PicoFlasherCommand.GetFlashConfiguration, 0),
                Command(PicoFlasherCommand.EmmcDetect, 0),
                Command(PicoFlasherCommand.EmmcInitialize, 0),
                Command(PicoFlasherCommand.EmmcGetCid, 0),
                Command(PicoFlasherCommand.EmmcGetCsd, 0),
                Command(PicoFlasherCommand.EmmcGetExtendedCsd, 0),
                Command(PicoFlasherCommand.EmmcRead, 0x22),
                Command(PicoFlasherCommand.StartSmc, 0),
            ]);
    }

    [Theory]
    [MemberData(nameof(EmmcMetadataTransportFailureCases))]
    public async Task Classified_emmc_metadata_write_retires_without_later_commands(
        bool readOperation,
        int failingReadCall,
        string failureKind)
    {
        OperationFailureException transportFailure = CreateTransportFailure(failureKind);
        List<byte[]> expectedFrames = EmmcMetadataCommands().Take(failingReadCall + 2).ToList();
        byte failingOpcode = expectedFrames[^1][0];
        var transport = new ScriptedTransport(
            EmmcMetadataResponses(),
            writeFailure: frame => frame[0] == failingOpcode ? transportFailure : null);
        PicoFlasherService service = CreateService(transport);
        using var output = new MemoryStream();

        Task invocation = readOperation
            ? service.ReadEmmcAsync(new PicoFlasherEmmcReadRequest(output, StartSector: 0, SectorCount: 1))
            : service.ProbeEmmcAsync();
        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(() => invocation);

        AssertTransportFailure(transportFailure, failure);
        Assert.True(transport.Disposed);
        Assert.Empty(output.ToArray());
        Assert.Equal(failingReadCall - 1, transport.ReadLengths.Count);
        AssertFrames(transport, expectedFrames);
        await AssertRetiredConnectionRejectsFollowUpAsync(service, transport);
    }

    [Theory]
    [InlineData(true, TransportTimeoutKind)]
    [InlineData(true, TransportClosedKind)]
    [InlineData(true, TransportIoFailureKind)]
    [InlineData(false, TransportTimeoutKind)]
    [InlineData(false, TransportClosedKind)]
    [InlineData(false, TransportIoFailureKind)]
    public async Task Classified_emmc_read_command_write_retires_without_reset_or_restart(
        bool streamed,
        string failureKind)
    {
        OperationFailureException transportFailure = CreateTransportFailure(failureKind);
        PicoFlasherCommand failingCommand = streamed
            ? PicoFlasherCommand.EmmcReadStream
            : PicoFlasherCommand.EmmcRead;
        var transport = new ScriptedTransport(
            EmmcMetadataResponses(),
            writeFailure: frame => frame[0] == (byte)failingCommand ? transportFailure : null);
        PicoFlasherService service = CreateService(transport);
        using var output = new MemoryStream();
        uint startSector = streamed ? 0U : 34U;

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => service.ReadEmmcAsync(
                new PicoFlasherEmmcReadRequest(output, startSector, SectorCount: 1)));

        AssertTransportFailure(transportFailure, failure);
        Assert.True(transport.Disposed);
        Assert.Empty(output.ToArray());
        List<byte[]> expectedFrames = EmmcMetadataCommands();
        expectedFrames.Add(Command(failingCommand, streamed ? 1U : startSector));
        AssertFrames(transport, expectedFrames);
        await AssertRetiredConnectionRejectsFollowUpAsync(service, transport);
    }

    [Fact]
    public async Task Full_origin_read_streams_exact_records_writes_output_and_restarts_smc()
    {
        byte[] firstRecord = CreateRecord(0x34);
        byte[] secondRecord = CreateRecord(0x67);
        var transport = new ScriptedTransport(
            Combine(
                Status(KnownSmallBlockConfiguration),
                Status(0),
                firstRecord,
                Status(0),
                secondRecord));
        PicoFlasherService service = CreateService(transport);
        using var output = new MemoryStream();

        PicoFlasherNandReadResult result = await service.ReadNandAsync(
            new PicoFlasherNandReadRequest(output, StartRecord: 0, RecordCount: 2));

        Assert.Equal(KnownSmallBlockConfiguration, result.FlashConfiguration);
        Assert.Equal(0U, result.StartRecord);
        Assert.Equal(1U, result.EndRecordInclusive);
        Assert.Equal(2U, result.RecordCount);
        Assert.Equal((long)(2 * PicoFlasherProtocol.NandDataSize), result.LogicalByteLength);
        Assert.Equal((long)(2 * PicoFlasherProtocol.NandWireRecordSize), result.RawByteLength);
        Assert.Equal<byte>(Combine(firstRecord, secondRecord), output.ToArray());
        Assert.Equal<int>(
            [
                PicoFlasherProtocol.StatusSize,
                PicoFlasherProtocol.StatusSize,
                PicoFlasherProtocol.NandWireRecordSize,
                PicoFlasherProtocol.StatusSize,
                PicoFlasherProtocol.NandWireRecordSize,
            ],
            transport.ReadLengths);
        Assert.Equal(0, transport.RemainingResponseByteCount);
        AssertFrames(
            transport,
            [
                Command(PicoFlasherCommand.SetSmcWorkaround, 0),
                Command(PicoFlasherCommand.StopSmc, 0),
                Command(PicoFlasherCommand.GetFlashConfiguration, 0),
                Command(PicoFlasherCommand.ReadFlashStream, 2),
                Command(PicoFlasherCommand.StartSmc, 0),
            ]);
    }

    [Fact]
    public async Task Nonzero_origin_read_uses_individual_read_lbas_instead_of_a_stream()
    {
        byte[] firstRecord = CreateRecord(0x21);
        byte[] secondRecord = CreateRecord(0x43);
        var transport = new ScriptedTransport(
            Combine(
                Status(KnownSmallBlockConfiguration),
                Status(0),
                firstRecord,
                Status(0),
                secondRecord));
        PicoFlasherService service = CreateService(transport);
        using var output = new MemoryStream();

        PicoFlasherNandReadResult result = await service.ReadNandAsync(
            new PicoFlasherNandReadRequest(output, StartRecord: 0x20, RecordCount: 2));

        Assert.Equal(0x20U, result.StartRecord);
        Assert.Equal(0x21U, result.EndRecordInclusive);
        Assert.Equal(2U, result.RecordCount);
        Assert.Equal((long)(2 * PicoFlasherProtocol.NandDataSize), result.LogicalByteLength);
        Assert.Equal((long)(2 * PicoFlasherProtocol.NandWireRecordSize), result.RawByteLength);
        Assert.Equal<byte>(Combine(firstRecord, secondRecord), output.ToArray());
        Assert.Equal(0, transport.RemainingResponseByteCount);
        AssertFrames(
            transport,
            [
                Command(PicoFlasherCommand.SetSmcWorkaround, 0),
                Command(PicoFlasherCommand.StopSmc, 0),
                Command(PicoFlasherCommand.GetFlashConfiguration, 0),
                Command(PicoFlasherCommand.ReadFlash, 0x20),
                Command(PicoFlasherCommand.ReadFlash, 0x21),
                Command(PicoFlasherCommand.StartSmc, 0),
            ]);
    }

    [Theory]
    [InlineData(2, null)]
    [InlineData(2, TransportTimeoutKind)]
    [InlineData(2, TransportClosedKind)]
    [InlineData(2, TransportIoFailureKind)]
    [InlineData(3, null)]
    [InlineData(3, TransportTimeoutKind)]
    [InlineData(3, TransportClosedKind)]
    [InlineData(3, TransportIoFailureKind)]
    public async Task Individual_nand_response_framing_reports_the_absolute_record_and_retires(
        int failingReadCall,
        string? failureKind)
    {
        Exception transportFailure = failureKind is null
            ? new IOException("The individual NAND response stalled.")
            : CreateTransportFailure(failureKind);
        byte[] responseSequence = failingReadCall == 2
            ? Combine(
                Status(KnownSmallBlockConfiguration),
                Status(0))
            : Combine(
                Status(KnownSmallBlockConfiguration),
                Status(0),
                CreateRecord(0x5E));
        var transport = new ScriptedTransport(
            responseSequence,
            readFailure: readCall => readCall == failingReadCall
                ? transportFailure
                : null,
            partialReadByteCount: readCall => readCall == failingReadCall ? 2 : 0);
        PicoFlasherService service = CreateService(transport);
        using var output = new MemoryStream();

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => service.ReadNandAsync(
                new PicoFlasherNandReadRequest(output, StartRecord: 0x22, RecordCount: 1)));

        Assert.Equal(ExitCode.InputOutput, failure.Code);
        Assert.Equal(failureKind ?? "pico-nand-read-response-failed", failure.Kind);
        const string expectedContext = "The PicoFlasher NAND read response for record 34 was incomplete or stalled.";
        Assert.Equal(
            failureKind is null ? expectedContext : $"{transportFailure.Message} {expectedContext}",
            failure.Message);
        Assert.Empty(output.ToArray());
        Assert.Equal(failingReadCall, transport.ReadLengths.Count);
        Assert.True(transport.Disposed);
        Assert.NotEqual(0, transport.RemainingResponseByteCount);
        AssertFrames(
            transport,
            [
                Command(PicoFlasherCommand.SetSmcWorkaround, 0),
                Command(PicoFlasherCommand.StopSmc, 0),
                Command(PicoFlasherCommand.GetFlashConfiguration, 0),
                Command(PicoFlasherCommand.ReadFlash, 0x22),
                Command(PicoFlasherCommand.StartSmc, 0),
            ]);

        OperationFailureException followUpFailure = await Assert.ThrowsAsync<OperationFailureException>(
            () => service.ProbeAsync());

        Assert.Equal("pico-stream-recovery-required", followUpFailure.Kind);
        Assert.Equal(ExitCode.InputOutput, followUpFailure.Code);
    }

    [Theory]
    [InlineData("write", null, "record 32")]
    [InlineData("write", TransportTimeoutKind, "record 32")]
    [InlineData("write", TransportClosedKind, "record 32")]
    [InlineData("write", TransportIoFailureKind, "record 32")]
    [InlineData("erase", null, "erase block 1 (records 32 through 63)")]
    [InlineData("erase", TransportTimeoutKind, "erase block 1 (records 32 through 63)")]
    [InlineData("erase", TransportClosedKind, "erase block 1 (records 32 through 63)")]
    [InlineData("erase", TransportIoFailureKind, "erase block 1 (records 32 through 63)")]
    public async Task Nand_write_and_erase_response_framing_report_the_absolute_target_and_retire(
        string operation,
        string? failureKind,
        string expectedContext)
    {
        Exception transportFailure = failureKind is null
            ? new IOException("The NAND status response stalled.")
            : CreateTransportFailure(failureKind);
        var transport = new ScriptedTransport(
            Combine(
                Status(KnownSmallBlockConfiguration),
                Status(0)),
            readFailure: readCall => readCall == 2
                ? transportFailure
                : null,
            partialReadByteCount: readCall => readCall == 2 ? 2 : 0);
        PicoFlasherService service = CreateService(transport);
        using MemoryStream input = CreateNandInput(SmallBlockEraseRecordCount);

        Task invocation = operation switch
        {
            "write" => service.WriteNandAsync(
                new PicoFlasherNandWriteRequest(input, StartRecord: SmallBlockEraseRecordCount)),
            "erase" => service.EraseNandAsync(
                new PicoFlasherNandEraseRequest(StartEraseBlock: 1, EraseBlockCount: 1)),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown NAND operation."),
        };

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(() => invocation);

        Assert.Equal(ExitCode.InputOutput, failure.Code);
        Assert.Equal(failureKind ?? $"pico-nand-{operation}-response-failed", failure.Kind);
        string expectedMessage =
            $"The PicoFlasher NAND {operation} response for {expectedContext} was incomplete or stalled.";
        Assert.Equal(
            failureKind is null ? expectedMessage : $"{transportFailure.Message} {expectedMessage}",
            failure.Message);
        Assert.True(transport.Disposed);
        var expectedFrames = new List<byte[]>
        {
            Command(PicoFlasherCommand.SetSmcWorkaround, 0),
            Command(PicoFlasherCommand.StopSmc, 0),
            Command(PicoFlasherCommand.GetFlashConfiguration, 0),
        };
        expectedFrames.Add(
            operation == "write"
                ? WriteRecordFrame(SmallBlockEraseRecordCount, new byte[PicoFlasherProtocol.NandWireRecordSize])
                : Command(PicoFlasherCommand.EraseFlash, SmallBlockEraseRecordCount));
        expectedFrames.Add(Command(PicoFlasherCommand.StartSmc, 0));
        AssertFrames(transport, expectedFrames);
        await AssertRetiredConnectionRejectsFollowUpAsync(service, transport);
    }

    [Fact]
    public async Task Normal_nand_status_and_data_responses_observe_the_caller_cancellation_token()
    {
        using var cancellation = new CancellationTokenSource();
        var transport = new ScriptedTransport(
            Combine(
                Status(KnownSmallBlockConfiguration),
                Status(0),
                CreateRecord(0x2F)));
        PicoFlasherService service = CreateService(transport);
        using var output = new MemoryStream();

        await service.ReadNandAsync(
            new PicoFlasherNandReadRequest(output, StartRecord: 1, RecordCount: 1),
            cancellationToken: cancellation.Token);

        Assert.Equal(3, transport.ReadCancellationTokens.Count);
        Assert.All(
            transport.ReadCancellationTokens,
            token => Assert.Equal(cancellation.Token, token));
    }

    [Fact]
    public async Task Cancelled_individual_nand_response_restarts_retires_and_preserves_the_caller_token()
    {
        using var cancellation = new CancellationTokenSource();
        var transport = new ScriptedTransport(
            Combine(
                Status(KnownSmallBlockConfiguration),
                Status(0)),
            beforeRead: readCall =>
            {
                if (readCall == 3)
                {
                    cancellation.Cancel();
                }
            });
        PicoFlasherService service = CreateService(transport);
        using var output = new MemoryStream();

        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ReadNandAsync(
                new PicoFlasherNandReadRequest(output, StartRecord: 1, RecordCount: 1),
                cancellationToken: cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(transport.Disposed);
        AssertFrames(
            transport,
            [
                Command(PicoFlasherCommand.SetSmcWorkaround, 0),
                Command(PicoFlasherCommand.StopSmc, 0),
                Command(PicoFlasherCommand.GetFlashConfiguration, 0),
                Command(PicoFlasherCommand.ReadFlash, 1),
                Command(PicoFlasherCommand.StartSmc, 0),
            ]);
    }

    [Theory]
    [InlineData(0U, ExitCode.DeviceUnavailable, "pico-console-not-detected")]
    [InlineData(0xC001_0000U, ExitCode.InvalidData, "pico-nand-unavailable")]
    public async Task Nand_operations_reject_unavailable_geometries_before_data_commands(
        uint flashConfiguration,
        ExitCode expectedCode,
        string expectedKind)
    {
        var readTransport = new ScriptedTransport(Status(flashConfiguration));
        PicoFlasherService readService = CreateService(readTransport);
        using var readOutput = new MemoryStream();

        OperationFailureException readFailure = await Assert.ThrowsAsync<OperationFailureException>(
            () => readService.ReadNandAsync(
                new PicoFlasherNandReadRequest(readOutput, StartRecord: 0, RecordCount: 1)));

        Assert.Equal(expectedCode, readFailure.Code);
        Assert.Equal(expectedKind, readFailure.Kind);
        AssertPreflightAndRestart(readTransport);

        var writeTransport = new ScriptedTransport(Status(flashConfiguration));
        PicoFlasherService writeService = CreateService(writeTransport);
        using var writeInput = CreateNandInput(SmallBlockEraseRecordCount);

        OperationFailureException writeFailure = await Assert.ThrowsAsync<OperationFailureException>(
            () => writeService.WriteNandAsync(
                new PicoFlasherNandWriteRequest(writeInput, StartRecord: 0)));

        Assert.Equal(expectedCode, writeFailure.Code);
        Assert.Equal(expectedKind, writeFailure.Kind);
        AssertPreflightAndRestart(writeTransport);

        var eraseTransport = new ScriptedTransport(Status(flashConfiguration));
        PicoFlasherService eraseService = CreateService(eraseTransport);

        OperationFailureException eraseFailure = await Assert.ThrowsAsync<OperationFailureException>(
            () => eraseService.EraseNandAsync(
                new PicoFlasherNandEraseRequest(StartEraseBlock: 0, EraseBlockCount: 1)));

        Assert.Equal(expectedCode, eraseFailure.Code);
        Assert.Equal(expectedKind, eraseFailure.Kind);
        AssertPreflightAndRestart(eraseTransport);
    }

    [Fact]
    public async Task Unknown_nand_requires_an_explicit_bounded_read_range_and_remains_non_writable()
    {
        const uint unknownConfiguration = 0x0119_8011U;
        byte[] record = CreateRecord(0x6A);
        var boundedReadTransport = new ScriptedTransport(
            Combine(
                Status(unknownConfiguration),
                Status(0),
                record));
        PicoFlasherService boundedReadService = CreateService(boundedReadTransport);
        using var output = new MemoryStream();

        PicoFlasherNandReadResult result = await boundedReadService.ReadNandAsync(
            new PicoFlasherNandReadRequest(output, StartRecord: 1, RecordCount: 1));

        Assert.Equal(unknownConfiguration, result.FlashConfiguration);
        Assert.Equal(1U, result.StartRecord);
        Assert.Equal(1U, result.RecordCount);
        Assert.Equal<byte>(record, output.ToArray());
        AssertFrames(
            boundedReadTransport,
            [
                Command(PicoFlasherCommand.SetSmcWorkaround, 0),
                Command(PicoFlasherCommand.StopSmc, 0),
                Command(PicoFlasherCommand.GetFlashConfiguration, 0),
                Command(PicoFlasherCommand.ReadFlash, 1),
                Command(PicoFlasherCommand.StartSmc, 0),
            ]);

        var defaultReadTransport = new ScriptedTransport(Status(unknownConfiguration));
        PicoFlasherService defaultReadService = CreateService(defaultReadTransport);
        using var defaultOutput = new MemoryStream();

        OperationFailureException defaultReadFailure = await Assert.ThrowsAsync<OperationFailureException>(
            () => defaultReadService.ReadNandAsync(
                new PicoFlasherNandReadRequest(defaultOutput, StartRecord: 0, RecordCount: null)));

        Assert.Equal(ExitCode.InvalidData, defaultReadFailure.Code);
        Assert.Equal("pico-nand-geometry-unsupported", defaultReadFailure.Kind);
        AssertPreflightAndRestart(defaultReadTransport);

        var writeTransport = new ScriptedTransport(Status(unknownConfiguration));
        PicoFlasherService writeService = CreateService(writeTransport);
        using var writeInput = CreateNandInput(SmallBlockEraseRecordCount);

        OperationFailureException writeFailure = await Assert.ThrowsAsync<OperationFailureException>(
            () => writeService.WriteNandAsync(new PicoFlasherNandWriteRequest(writeInput, StartRecord: 0)));

        Assert.Equal(ExitCode.InvalidData, writeFailure.Code);
        Assert.Equal("pico-nand-geometry-unsupported", writeFailure.Kind);
        AssertPreflightAndRestart(writeTransport);

        var eraseTransport = new ScriptedTransport(Status(unknownConfiguration));
        PicoFlasherService eraseService = CreateService(eraseTransport);

        OperationFailureException eraseFailure = await Assert.ThrowsAsync<OperationFailureException>(
            () => eraseService.EraseNandAsync(
                new PicoFlasherNandEraseRequest(StartEraseBlock: 0, EraseBlockCount: 1)));

        Assert.Equal(ExitCode.InvalidData, eraseFailure.Code);
        Assert.Equal("pico-nand-geometry-unsupported", eraseFailure.Kind);
        AssertPreflightAndRestart(eraseTransport);
    }

    [Fact]
    public async Task Write_rejects_an_incomplete_wire_record_before_stopping_smc()
    {
        var transport = new ScriptedTransport([]);
        PicoFlasherService service = CreateService(transport);
        using var input = new MemoryStream(new byte[PicoFlasherProtocol.NandWireRecordSize - 1], writable: false);

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => service.WriteNandAsync(new PicoFlasherNandWriteRequest(input, StartRecord: 0)));

        Assert.Equal(ExitCode.InvalidData, failure.Code);
        Assert.Equal("pico-nand-input-length-invalid", failure.Kind);
        Assert.Empty(transport.WrittenFrames);
    }

    [Fact]
    public async Task Write_requires_an_erase_block_aligned_range()
    {
        var transport = new ScriptedTransport(Status(KnownSmallBlockConfiguration));
        PicoFlasherService service = CreateService(transport);
        using var input = CreateNandInput(recordCount: 1);

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => service.WriteNandAsync(new PicoFlasherNandWriteRequest(input, StartRecord: 0)));

        Assert.Equal(ExitCode.Usage, failure.Code);
        Assert.Equal("pico-nand-erase-alignment-required", failure.Kind);
        AssertPreflightAndRestart(transport);
    }

    [Fact]
    public async Task Write_sends_one_combined_record_frame_per_lba_without_an_explicit_erase()
    {
        using var cancellation = new CancellationTokenSource();
        byte[][] records = CreateRecords(SmallBlockEraseRecordCount);
        byte[] inputBytes = Combine(records);
        var transport = new ScriptedTransport(StatusSequence(KnownSmallBlockConfiguration, SmallBlockEraseRecordCount));
        PicoFlasherService service = CreateService(transport);
        using var input = new MemoryStream(inputBytes, writable: false);

        PicoFlasherNandWriteResult result = await service.WriteNandAsync(
            new PicoFlasherNandWriteRequest(input, StartRecord: SmallBlockEraseRecordCount),
            cancellationToken: cancellation.Token);

        Assert.Equal(KnownSmallBlockConfiguration, result.FlashConfiguration);
        Assert.Equal((uint)SmallBlockEraseRecordCount, result.StartRecord);
        Assert.Equal((uint)((SmallBlockEraseRecordCount * 2) - 1), result.EndRecordInclusive);
        Assert.Equal((uint)SmallBlockEraseRecordCount, result.RecordCount);
        Assert.Equal(
            (long)SmallBlockEraseRecordCount * PicoFlasherProtocol.NandDataSize,
            result.LogicalByteLength);
        Assert.Equal(
            (long)SmallBlockEraseRecordCount * PicoFlasherProtocol.NandWireRecordSize,
            result.RawByteLength);
        Assert.Equal(SmallBlockEraseRecordCount + 1, transport.ReadCancellationTokens.Count);
        Assert.All(
            transport.ReadCancellationTokens,
            token => Assert.Equal(cancellation.Token, token));
        Assert.All(
            transport.WriteCancellationTokens.Take(3 + records.Length),
            token => Assert.Equal(cancellation.Token, token));

        var expectedFrames = new List<byte[]>
        {
            Command(PicoFlasherCommand.SetSmcWorkaround, 0),
            Command(PicoFlasherCommand.StopSmc, 0),
            Command(PicoFlasherCommand.GetFlashConfiguration, 0),
        };
        for (int recordIndex = 0; recordIndex < records.Length; recordIndex++)
        {
            expectedFrames.Add(
                WriteRecordFrame((uint)SmallBlockEraseRecordCount + (uint)recordIndex, records[recordIndex]));
        }

        expectedFrames.Add(Command(PicoFlasherCommand.StartSmc, 0));

        AssertFrames(transport, expectedFrames);
        Assert.DoesNotContain(
            transport.WrittenFrames,
            frame => frame[0] == (byte)PicoFlasherCommand.EraseFlash);
        Assert.Equal(0, transport.RemainingResponseByteCount);
    }

    [Fact]
    public async Task Erase_rejects_an_empty_erase_block_range()
    {
        var transport = new ScriptedTransport(Status(KnownSmallBlockConfiguration));
        PicoFlasherService service = CreateService(transport);

        OperationFailureException failure = await Assert.ThrowsAsync<OperationFailureException>(
            () => service.EraseNandAsync(new PicoFlasherNandEraseRequest(StartEraseBlock: 0, EraseBlockCount: 0)));

        Assert.Equal(ExitCode.Usage, failure.Code);
        Assert.Equal("pico-nand-range-invalid", failure.Kind);
        AssertPreflightAndRestart(transport);
    }

    [Fact]
    public async Task Erase_sends_one_command_per_aligned_erase_unit()
    {
        const int eraseBlockCount = 2;
        var transport = new ScriptedTransport(StatusSequence(KnownSmallBlockConfiguration, eraseBlockCount));
        PicoFlasherService service = CreateService(transport);

        PicoFlasherNandEraseResult result = await service.EraseNandAsync(
            new PicoFlasherNandEraseRequest(StartEraseBlock: 1, EraseBlockCount: eraseBlockCount));

        Assert.Equal(KnownSmallBlockConfiguration, result.FlashConfiguration);
        Assert.Equal(1U, result.StartEraseBlock);
        Assert.Equal(2U, result.EndEraseBlockInclusive);
        Assert.Equal((uint)eraseBlockCount, result.EraseBlockCount);
        Assert.Equal((uint)SmallBlockEraseRecordCount, result.StartRecord);
        Assert.Equal((uint)((SmallBlockEraseRecordCount * eraseBlockCount) + SmallBlockEraseRecordCount - 1), result.EndRecordInclusive);
        Assert.Equal((uint)(SmallBlockEraseRecordCount * eraseBlockCount), result.RecordCount);
        Assert.Equal((uint)SmallBlockEraseRecordCount, result.EraseBlockRecordCount);
        Assert.Equal(
            (long)SmallBlockEraseRecordCount * PicoFlasherProtocol.NandDataSize,
            result.StartLogicalByteOffset);
        Assert.Equal(
            ((long)SmallBlockEraseRecordCount * PicoFlasherProtocol.NandDataSize) +
                ((long)SmallBlockEraseRecordCount * eraseBlockCount * PicoFlasherProtocol.NandDataSize) - 1,
            result.EndLogicalByteOffsetInclusive);
        Assert.Equal(
            (long)SmallBlockEraseRecordCount * eraseBlockCount * PicoFlasherProtocol.NandDataSize,
            result.LogicalByteLength);
        Assert.Equal(
            (long)SmallBlockEraseRecordCount * PicoFlasherProtocol.NandWireRecordSize,
            result.StartRawByteOffset);
        Assert.Equal(
            ((long)SmallBlockEraseRecordCount * PicoFlasherProtocol.NandWireRecordSize) +
                ((long)SmallBlockEraseRecordCount * eraseBlockCount * PicoFlasherProtocol.NandWireRecordSize) - 1,
            result.EndRawByteOffsetInclusive);
        Assert.Equal(
            (long)SmallBlockEraseRecordCount * eraseBlockCount * PicoFlasherProtocol.NandWireRecordSize,
            result.RawByteLength);
        AssertFrames(
            transport,
            [
                Command(PicoFlasherCommand.SetSmcWorkaround, 0),
                Command(PicoFlasherCommand.StopSmc, 0),
                Command(PicoFlasherCommand.GetFlashConfiguration, 0),
                Command(PicoFlasherCommand.EraseFlash, SmallBlockEraseRecordCount),
                Command(PicoFlasherCommand.EraseFlash, SmallBlockEraseRecordCount * 2),
                Command(PicoFlasherCommand.StartSmc, 0),
            ]);
        Assert.Equal(0, transport.RemainingResponseByteCount);
    }

    [Theory]
    [InlineData("read", "pico-nand-read-failed")]
    [InlineData("write", "pico-nand-write-failed")]
    [InlineData("erase", "pico-nand-erase-failed")]
    public async Task Nonzero_firmware_status_maps_to_a_stable_operation_failure_and_restarts_smc(
        string operation,
        string expectedKind)
    {
        var transport = new ScriptedTransport(
            StatusSequence(KnownSmallBlockConfiguration, operationStatusCount: 1, operationStatus: 0xA5A5_5A5AU));
        PicoFlasherService service = CreateService(transport);

        OperationFailureException failure = await InvokeNonzeroStatusOperationAsync(operation, service);

        Assert.Equal(ExitCode.DeviceUnavailable, failure.Code);
        Assert.Equal(expectedKind, failure.Kind);
        string expectedContext = operation switch
        {
            "read" => "record 1",
            "write" => "record 0",
            "erase" => $"erase block 0 (records 0 through {SmallBlockEraseRecordCount - 1})",
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown NAND operation."),
        };
        Assert.Contains(expectedContext, failure.Message, StringComparison.Ordinal);
        Assert.Equal(5, transport.WrittenFrames.Count);
        Assert.Equal<byte>(Command(PicoFlasherCommand.SetSmcWorkaround, 0), transport.WrittenFrames[0]);
        Assert.Equal<byte>(Command(PicoFlasherCommand.StopSmc, 0), transport.WrittenFrames[1]);
        Assert.Equal<byte>(Command(PicoFlasherCommand.GetFlashConfiguration, 0), transport.WrittenFrames[2]);

        PicoFlasherCommand expectedCommand = GetFailureCommand(operation);
        uint expectedLogicalBlockAddress = operation == "read" ? 1U : 0U;
        byte[] dataFrame = transport.WrittenFrames[3];
        Assert.Equal<byte>(Command(expectedCommand, expectedLogicalBlockAddress), dataFrame[..PicoFlasherProtocol.CommandSize]);
        Assert.Equal(
            expectedCommand == PicoFlasherCommand.WriteFlash
                ? PicoFlasherProtocol.CommandSize + PicoFlasherProtocol.NandWireRecordSize
                : PicoFlasherProtocol.CommandSize,
            dataFrame.Length);
        Assert.Equal<byte>(Command(PicoFlasherCommand.StartSmc, 0), transport.WrittenFrames[4]);
        Assert.Equal(0, transport.RemainingResponseByteCount);
    }

    public static TheoryData<bool, int, string> EmmcMetadataTransportFailureCases()
    {
        var cases = new TheoryData<bool, int, string>();
        for (int operation = 0; operation < 2; operation++)
        {
            for (int readCall = 1; readCall <= 6; readCall++)
            {
                foreach (string failureKind in TransportFailureKinds)
                {
                    cases.Add(operation == 1, readCall, failureKind);
                }
            }
        }

        return cases;
    }

    public static TheoryData<bool, bool, int, string?> EmmcReadResponseFailureCases()
    {
        var cases = new TheoryData<bool, bool, int, string?>();
        for (int streamed = 0; streamed < 2; streamed++)
        {
            for (int payloadFailure = 0; payloadFailure < 2; payloadFailure++)
            {
                for (int partialByteCount = 0; partialByteCount <= 2; partialByteCount += 2)
                {
                    cases.Add(streamed == 1, payloadFailure == 1, partialByteCount, null);
                    foreach (string failureKind in TransportFailureKinds)
                    {
                        cases.Add(streamed == 1, payloadFailure == 1, partialByteCount, failureKind);
                    }
                }
            }
        }

        return cases;
    }

    private static OperationFailureException CreateTransportFailure(string kind)
    {
        string message = kind switch
        {
            TransportTimeoutKind => "The PicoFlasher transport did not make progress before the I/O timeout.",
            TransportClosedKind => "The PicoFlasher transport closed before the requested response was received.",
            TransportIoFailureKind => "The PicoFlasher transport could not complete the requested I/O.",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown transport failure kind."),
        };
        return new OperationFailureException(ExitCode.InputOutput, kind, message);
    }

    private static void AssertTransportFailure(
        OperationFailureException transportFailure,
        OperationFailureException failure)
    {
        Assert.Equal(ExitCode.InputOutput, failure.Code);
        Assert.Same(transportFailure, failure);
        Assert.Equal(transportFailure.Kind, failure.Kind);
        Assert.Equal(transportFailure.Message, failure.Message);
    }

    private static async Task AssertRetiredConnectionRejectsFollowUpAsync(
        PicoFlasherService service,
        ScriptedTransport transport)
    {
        int writtenFrameCount = transport.WrittenFrames.Count;
        int readCallCount = transport.ReadLengths.Count;
        OperationFailureException followUpFailure = await Assert.ThrowsAsync<OperationFailureException>(
            () => service.ProbeAsync());

        Assert.Equal(ExitCode.InputOutput, followUpFailure.Code);
        Assert.Equal("pico-stream-recovery-required", followUpFailure.Kind);
        Assert.Equal(writtenFrameCount, transport.WrittenFrames.Count);
        Assert.Equal(readCallCount, transport.ReadLengths.Count);
    }

    private static void AssertCleanupTokens(
        ScriptedTransport transport,
        int normalReadCount,
        int normalWriteCount,
        CancellationToken callerToken)
    {
        Assert.All(
            transport.ReadCancellationTokens.Take(normalReadCount),
            token => Assert.Equal(callerToken, token));
        CancellationToken drainToken = transport.ReadCancellationTokens[normalReadCount];
        Assert.True(drainToken.CanBeCanceled);
        Assert.NotEqual(callerToken, drainToken);
        Assert.All(
            transport.ReadCancellationTokens.Skip(normalReadCount),
            token => Assert.Equal(drainToken, token));
        Assert.All(
            transport.WriteCancellationTokens.Take(normalWriteCount),
            token => Assert.Equal(callerToken, token));
        Assert.Equal(normalWriteCount + 2, transport.WriteCancellationTokens.Count);
        Assert.All(
            transport.WriteCancellationTokens.Skip(normalWriteCount),
            token =>
            {
                Assert.True(token.CanBeCanceled);
                Assert.NotEqual(callerToken, token);
                Assert.NotEqual(drainToken, token);
                Assert.False(token.IsCancellationRequested);
            });
        Assert.NotEqual(transport.WriteCancellationTokens[^2], transport.WriteCancellationTokens[^1]);
    }

    private static List<byte[]> EmmcMetadataCommands()
    {
        return
        [
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

    private static PicoFlasherService CreateService(
        ScriptedTransport transport,
        uint firmwareVersion = PicoFlasherProtocol.MinimumSupportedFirmwareVersion)
    {
        return new PicoFlasherService(
            new PicoFlasherConnection(
                new PicoFlasherDeviceEndpoint(TestDevicePath, TestSerialNumber, interfaceNumber: 0),
                firmwareVersion,
                transport),
            TimeSpan.Zero);
    }

    private static async Task<OperationFailureException> InvokeNonzeroStatusOperationAsync(
        string operation,
        PicoFlasherService service)
    {
        switch (operation)
        {
            case "read":
            {
                using var output = new MemoryStream();
                return await Assert.ThrowsAsync<OperationFailureException>(
                    () => service.ReadNandAsync(
                        new PicoFlasherNandReadRequest(output, StartRecord: 1, RecordCount: 1)));
            }

            case "write":
            {
                using MemoryStream input = CreateNandInput(SmallBlockEraseRecordCount);
                return await Assert.ThrowsAsync<OperationFailureException>(
                    () => service.WriteNandAsync(new PicoFlasherNandWriteRequest(input, StartRecord: 0)));
            }

            case "erase":
                return await Assert.ThrowsAsync<OperationFailureException>(
                    () => service.EraseNandAsync(
                        new PicoFlasherNandEraseRequest(StartEraseBlock: 0, EraseBlockCount: 1)));

            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown NAND operation.");
        }
    }

    private static PicoFlasherCommand GetFailureCommand(string operation)
    {
        return operation switch
        {
            "read" => PicoFlasherCommand.ReadFlash,
            "write" => PicoFlasherCommand.WriteFlash,
            "erase" => PicoFlasherCommand.EraseFlash,
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown NAND operation."),
        };
    }

    private static void AssertPreflightAndRestart(ScriptedTransport transport)
    {
        AssertFrames(
            transport,
            [
                Command(PicoFlasherCommand.SetSmcWorkaround, 0),
                Command(PicoFlasherCommand.StopSmc, 0),
                Command(PicoFlasherCommand.GetFlashConfiguration, 0),
                Command(PicoFlasherCommand.StartSmc, 0),
            ]);
        Assert.Equal(0, transport.RemainingResponseByteCount);
    }

    private static void AssertFrames(ScriptedTransport transport, IReadOnlyList<byte[]> expectedFrames)
    {
        Assert.Equal(expectedFrames.Count, transport.WrittenFrames.Count);
        for (int index = 0; index < expectedFrames.Count; index++)
        {
            Assert.Equal<byte>(expectedFrames[index], transport.WrittenFrames[index]);
        }
    }

    private static byte[] Command(PicoFlasherCommand command, uint logicalBlockAddress)
    {
        byte[] frame = new byte[PicoFlasherProtocol.CommandSize];
        PicoFlasherProtocol.WriteCommand(frame, command, logicalBlockAddress);
        return frame;
    }

    private static byte[] WriteRecordFrame(uint logicalBlockAddress, byte[] record)
    {
        byte[] frame = new byte[PicoFlasherProtocol.CommandSize + PicoFlasherProtocol.NandWireRecordSize];
        PicoFlasherProtocol.WriteCommand(
            frame.AsSpan(0, PicoFlasherProtocol.CommandSize),
            PicoFlasherCommand.WriteFlash,
            logicalBlockAddress);
        record.AsSpan().CopyTo(frame.AsSpan(PicoFlasherProtocol.CommandSize));
        return frame;
    }

    private static byte[] Status(uint value)
    {
        byte[] status = new byte[PicoFlasherProtocol.StatusSize];
        BinaryPrimitives.WriteUInt32LittleEndian(status, value);
        return status;
    }

    private static byte[] StatusSequence(uint flashConfiguration, int operationStatusCount, uint operationStatus = 0)
    {
        byte[] sequence = new byte[checked((operationStatusCount + 1) * PicoFlasherProtocol.StatusSize)];
        BinaryPrimitives.WriteUInt32LittleEndian(sequence, flashConfiguration);
        for (int index = 0; index < operationStatusCount; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                sequence.AsSpan((index + 1) * PicoFlasherProtocol.StatusSize, PicoFlasherProtocol.StatusSize),
                operationStatus);
        }

        return sequence;
    }

    private static byte[] Combine(params byte[][] chunks)
    {
        int length = 0;
        foreach (byte[] chunk in chunks)
        {
            length = checked(length + chunk.Length);
        }

        byte[] combined = new byte[length];
        int offset = 0;
        foreach (byte[] chunk in chunks)
        {
            chunk.CopyTo(combined, offset);
            offset += chunk.Length;
        }

        return combined;
    }

    private static byte[] EmmcMetadataResponses(
        uint capacitySectorCount = PicoFlasherProtocol.EmmcLegacyReadWindowSectorCount)
    {
        return Combine(
            Status(0xC046_2002U),
            [1],
            Status(0),
            CreateBytes(PicoFlasherProtocol.EmmcCidSize, 0x14),
            CreateBytes(PicoFlasherProtocol.EmmcCsdSize, 0x48),
            CreateEmmcExtendedCsd(capacitySectorCount, 0x7B));
    }

    private static byte[] CreateEmmcExtendedCsd(uint capacitySectorCount, byte seed)
    {
        byte[] extendedCsd = CreateBytes(PicoFlasherProtocol.EmmcExtendedCsdSize, seed);
        BinaryPrimitives.WriteUInt32LittleEndian(
            extendedCsd.AsSpan(
                PicoFlasherProtocol.EmmcExtendedCsdSectorCountOffset,
                PicoFlasherProtocol.StatusSize),
            capacitySectorCount);
        return extendedCsd;
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

    private static byte[] CreateRecord(byte seed)
    {
        byte[] record = new byte[PicoFlasherProtocol.NandWireRecordSize];
        for (int index = 0; index < record.Length; index++)
        {
            record[index] = unchecked((byte)(seed + index));
        }

        return record;
    }

    private static byte[][] CreateRecords(int recordCount)
    {
        var records = new byte[recordCount][];
        for (int index = 0; index < records.Length; index++)
        {
            records[index] = CreateRecord(unchecked((byte)index));
        }

        return records;
    }

    private static MemoryStream CreateNandInput(int recordCount)
    {
        return new MemoryStream(
            new byte[checked(recordCount * PicoFlasherProtocol.NandWireRecordSize)],
            writable: false);
    }

    private static Exception? FailNandStreamReset(byte[] frame)
    {
        return frame.Length == PicoFlasherProtocol.CommandSize &&
            frame[0] == (byte)PicoFlasherCommand.ReadFlashStream &&
            BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(1)) == 0
            ? new IOException("The test transport rejects the stream reset.")
            : null;
    }


    private sealed class ScriptedTransport : IPicoFlasherTransport
    {
        private readonly Queue<byte> _responses;
        private readonly Func<byte[], Exception?>? _writeFailure;
        private readonly Action<int>? _afterRead;
        private readonly Action<int>? _beforeRead;
        private readonly Func<int, Exception?>? _readFailure;
        private readonly Func<int, int>? _partialReadByteCount;
        private readonly bool _awaitCancellationWhenResponsesExhausted;
        private readonly Exception? _exhaustedReadFailure;

        internal ScriptedTransport(
            byte[] responseSequence,
            Func<byte[], Exception?>? writeFailure = null,
            Action<int>? afterRead = null,
            Action<int>? beforeRead = null,
            Func<int, Exception?>? readFailure = null,
            Func<int, int>? partialReadByteCount = null,
            bool awaitCancellationWhenResponsesExhausted = false,
            Exception? exhaustedReadFailure = null)
        {
            ArgumentNullException.ThrowIfNull(responseSequence);
            _responses = new Queue<byte>(responseSequence);
            _writeFailure = writeFailure;
            _afterRead = afterRead;
            _beforeRead = beforeRead;
            _readFailure = readFailure;
            _partialReadByteCount = partialReadByteCount;
            _awaitCancellationWhenResponsesExhausted = awaitCancellationWhenResponsesExhausted;
            _exhaustedReadFailure = exhaustedReadFailure;
        }

        internal List<byte[]> WrittenFrames { get; } = [];

        internal List<CancellationToken> WriteCancellationTokens { get; } = [];

        internal List<int> ReadLengths { get; } = [];

        internal List<CancellationToken> ReadCancellationTokens { get; } = [];

        internal int RemainingResponseByteCount => _responses.Count;

        internal bool Disposed { get; private set; }

        public ValueTask WriteAsync(
            ReadOnlyMemory<byte> source,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteCancellationTokens.Add(cancellationToken);

            byte[] frame = source.ToArray();
            WrittenFrames.Add(frame);
            Exception? failure = _writeFailure?.Invoke(frame);
            return failure is null
                ? ValueTask.CompletedTask
                : new ValueTask(Task.FromException(failure));
        }

        public ValueTask ReadExactlyAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int readCall = ReadLengths.Count + 1;
            ReadLengths.Add(destination.Length);
            ReadCancellationTokens.Add(cancellationToken);
            _beforeRead?.Invoke(readCall);
            cancellationToken.ThrowIfCancellationRequested();
            Exception? failure = _readFailure?.Invoke(readCall);
            if (failure is not null)
            {
                int partialByteCount = _partialReadByteCount?.Invoke(readCall) ?? 0;
                if (partialByteCount < 0 ||
                    partialByteCount > destination.Length ||
                    _responses.Count < partialByteCount)
                {
                    throw new InvalidOperationException("The scripted transport cannot produce the requested partial read failure.");
                }

                for (int index = 0; index < partialByteCount; index++)
                {
                    destination.Span[index] = _responses.Dequeue();
                }

                return new ValueTask(Task.FromException(failure));
            }

            if (_responses.Count < destination.Length)
            {
                if (_responses.Count == 0 && _exhaustedReadFailure is not null)
                {
                    return new ValueTask(Task.FromException(_exhaustedReadFailure));
                }

                if (_awaitCancellationWhenResponsesExhausted &&
                    _responses.Count == 0 &&
                    cancellationToken.CanBeCanceled)
                {
                    return new ValueTask(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
                }

                throw new InvalidOperationException("The scripted transport has insufficient response bytes.");
            }

            for (int index = 0; index < destination.Length; index++)
            {
                destination.Span[index] = _responses.Dequeue();
            }

            _afterRead?.Invoke(readCall);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
