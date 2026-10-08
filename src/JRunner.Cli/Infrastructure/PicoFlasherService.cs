using System.Buffers;
using System.Runtime.ExceptionServices;

using JRunner.Core.Contracts;
using JRunner.Core.Devices.PicoFlasher;

namespace JRunner.Cli.Infrastructure;

/// <summary>
/// Executes v4 PicoFlasher NAND and read-only eMMC operations over one firmware-gated command transport.
/// </summary>
internal sealed class PicoFlasherService
{
    private const int TransferBufferSize = PicoFlasherProtocol.CommandSize + PicoFlasherProtocol.NandWireRecordSize;
    private const int MaximumQueuedNandStreamCleanupByteCount =
        0x40 * (PicoFlasherProtocol.StatusSize + PicoFlasherProtocol.NandWireRecordSize);
    private const int MaximumQueuedEmmcStreamCleanupByteCount =
        0x40 * (PicoFlasherProtocol.StatusSize + PicoFlasherProtocol.EmmcSectorSize);
    private const uint MaximumKnownNandRecordCount = 0x2000_0000U / PicoFlasherProtocol.NandDataSize;
    private const uint DisabledSmcWorkaround = 0;
    private const uint ProgressRecordInterval = 0x100;
    private static readonly TimeSpan StreamResetTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SmcRestartTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StreamQuiescenceTimeout = TimeSpan.FromMilliseconds(100);
    private readonly PicoFlasherConnection _connection;
    private readonly TimeSpan _smcStopWait;

    /// <summary>
    /// Creates a service that uses the firmware-defined SMC stop delay.
    /// </summary>
    internal PicoFlasherService(PicoFlasherConnection connection)
        : this(connection, PicoFlasherProtocol.SmcStopWait)
    {
    }

    /// <summary>
    /// Creates a service with an explicit SMC stop delay for deterministic transport tests.
    /// </summary>
    internal PicoFlasherService(PicoFlasherConnection connection, TimeSpan smcStopWait)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (smcStopWait < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(smcStopWait), "The SMC stop delay cannot be negative.");
        }

        if (connection.IsRetired || connection.FirmwareVersion < PicoFlasherProtocol.MinimumSupportedFirmwareVersion)
        {
            throw connection.IsRetired ? StreamRecoveryRequired() : UnsupportedFirmware();
        }

        _connection = connection;
        _smcStopWait = smcStopWait;
    }

    /// <summary>
    /// Sends only the firmware-gated command that intentionally leaves the console SMC stopped.
    /// </summary>
    internal Task StopSmcAsync(CancellationToken cancellationToken = default)
    {
        return SendExplicitControlCommandAsync(PicoFlasherCommand.StopSmc, cancellationToken);
    }

    /// <summary>
    /// Sends only the firmware-gated command that starts the console SMC.
    /// </summary>
    internal Task StartSmcAsync(CancellationToken cancellationToken = default)
    {
        return SendExplicitControlCommandAsync(PicoFlasherCommand.StartSmc, cancellationToken);
    }

    /// <summary>
    /// Sends only the firmware-gated command that reboots the flasher into its bootloader.
    /// </summary>
    internal Task RebootToBootloaderAsync(CancellationToken cancellationToken = default)
    {
        return SendExplicitControlCommandAsync(PicoFlasherCommand.RebootToBootloader, cancellationToken);
    }

    private async Task SendExplicitControlCommandAsync(
        PicoFlasherCommand command,
        CancellationToken cancellationToken)
    {
        ThrowIfConnectionRetired();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(PicoFlasherProtocol.CommandSize);
        try
        {
            var writeFence = new PicoFlasherCommandWriteFence();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await SendCommandAsync(
                        _connection.Transport,
                        buffer,
                        command,
                        0,
                        writeFence,
                        cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception exception)
            {
                if (!writeFence.IsIndeterminate)
                {
                    throw;
                }

                await RetireDesynchronizedConnectionAsync().ConfigureAwait(false);
                throw IndeterminateFailure(exception, cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reads and classifies the console flash configuration while always restoring the SMC after a stop.
    /// </summary>
    internal async Task<PicoFlasherProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfConnectionRetired();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(PicoFlasherProtocol.CommandSize);
        try
        {
            var writeFence = new PicoFlasherCommandWriteFence();
            PicoFlasherSmcLease smcLease = await AcquireSmcLeaseAsync(buffer, writeFence, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                uint flashConfiguration = await ReadFlashConfigurationAsync(
                        _connection.Transport,
                        buffer,
                        writeFence,
                        cancellationToken)
                    .ConfigureAwait(false);
                await smcLease.RestartAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                PicoFlasherNandGeometry.TryFromFlashConfiguration(flashConfiguration, out PicoFlasherNandGeometry? nandGeometry);
                return new PicoFlasherProbeResult(
                    _connection.FirmwareVersion,
                    _connection.Endpoint.DevicePath,
                    _connection.Endpoint.SerialNumber,
                    flashConfiguration,
                    GetStorageKind(flashConfiguration, nandGeometry),
                    nandGeometry);
            }
            catch (PicoFlasherResponseFramingException exception)
            {
                await RestartSmcAfterFailureOrFenceAsync(
                        smcLease,
                        writeFence,
                        exception,
                        cancellationToken)
                    .ConfigureAwait(false);
                await RetireDesynchronizedConnectionAsync().ConfigureAwait(false);
                throw IndeterminateFailure(exception, cancellationToken);
            }
            catch (Exception exception)
            {
                await RestartSmcAfterFailureOrFenceAsync(
                        smcLease,
                        writeFence,
                        exception,
                        cancellationToken)
                    .ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Verifies eMMC access and reads its immutable identification metadata.
    /// </summary>
    internal async Task<PicoFlasherEmmcProbeResult> ProbeEmmcAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfConnectionRetired();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(TransferBufferSize);
        try
        {
            var writeFence = new PicoFlasherCommandWriteFence();
            PicoFlasherSmcLease smcLease = await AcquireSmcLeaseAsync(buffer, writeFence, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                PicoFlasherEmmcMetadata metadata = await ReadEmmcMetadataAsync(
                        _connection.Transport,
                        buffer,
                        writeFence,
                        cancellationToken)
                    .ConfigureAwait(false);
                await smcLease.RestartAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return new PicoFlasherEmmcProbeResult(
                    _connection.FirmwareVersion,
                    _connection.Endpoint.DevicePath,
                    _connection.Endpoint.SerialNumber,
                    metadata.FlashConfiguration,
                    metadata.CapacitySectorCount,
                    metadata.Cid,
                    metadata.Csd,
                    metadata.ExtendedCsd);
            }
            catch (PicoFlasherResponseFramingException exception)
            {
                await RestartSmcAfterFailureOrFenceAsync(
                        smcLease,
                        writeFence,
                        exception,
                        cancellationToken)
                    .ConfigureAwait(false);
                await RetireDesynchronizedConnectionAsync().ConfigureAwait(false);
                throw IndeterminateFailure(exception, cancellationToken);
            }
            catch (Exception exception)
            {
                await RestartSmcAfterFailureOrFenceAsync(
                        smcLease,
                        writeFence,
                        exception,
                        cancellationToken)
                    .ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reads a bounded eMMC sector range into <paramref name="request"/>'s output stream.
    /// </summary>
    internal async Task<PicoFlasherEmmcReadResult> ReadEmmcAsync(
        PicoFlasherEmmcReadRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateWritableEmmcOutput(request.Output);
        ThrowIfConnectionRetired();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(TransferBufferSize);
        try
        {
            var writeFence = new PicoFlasherCommandWriteFence();
            PicoFlasherSmcLease smcLease = await AcquireSmcLeaseAsync(buffer, writeFence, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                PicoFlasherEmmcMetadata metadata = await ReadEmmcMetadataAsync(
                        _connection.Transport,
                        buffer,
                        writeFence,
                        cancellationToken)
                    .ConfigureAwait(false);
                ValidateEmmcReadRange(
                    request.StartSector,
                    request.SectorCount,
                    metadata.CapacitySectorCount);
                long byteLength = checked((long)request.SectorCount * PicoFlasherProtocol.EmmcSectorSize);

                ReportProgress(
                    progress,
                    "pico-emmc-read",
                    "Reading eMMC sectors.",
                    0,
                    request.SectorCount);
                if (request.StartSector == 0)
                {
                    await ReadEmmcStreamAsync(
                            _connection.Transport,
                            buffer,
                            request.Output,
                            request.SectorCount,
                            progress,
                            writeFence,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await ReadEmmcRangeAsync(
                            _connection.Transport,
                            buffer,
                            request.Output,
                            request.StartSector,
                            request.SectorCount,
                            progress,
                            writeFence,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                await smcLease.RestartAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return new PicoFlasherEmmcReadResult(
                    metadata.FlashConfiguration,
                    metadata.CapacitySectorCount,
                    request.StartSector,
                    request.SectorCount,
                    byteLength);
            }
            catch (PicoFlasherStreamDesynchronizedException exception)
            {
                if (exception.CanRestartSmc)
                {
                    await RestartSmcAfterFailureOrFenceAsync(
                            smcLease,
                            writeFence,
                            exception.OriginalException,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                if (exception.MustRetire)
                {
                    await RetireDesynchronizedConnectionAsync().ConfigureAwait(false);
                }

                if (exception.RecoveryRequired &&
                    !IsCallerCancellation(exception.OriginalException, cancellationToken))
                {
                    throw IndeterminateFailure(exception.OriginalException, cancellationToken);
                }

                throw RecoveredStreamFailure(exception.OriginalException, cancellationToken);
            }
            catch (PicoFlasherLocatedResponseFramingException exception)
            {
                await RestartSmcAfterFailureOrFenceAsync(
                        smcLease,
                        writeFence,
                        exception,
                        cancellationToken)
                    .ConfigureAwait(false);
                await RetireDesynchronizedConnectionAsync().ConfigureAwait(false);
                throw IndeterminateFailure(exception, cancellationToken);
            }
            catch (PicoFlasherResponseFramingException exception)
            {
                await RestartSmcAfterFailureOrFenceAsync(
                        smcLease,
                        writeFence,
                        exception,
                        cancellationToken)
                    .ConfigureAwait(false);
                await RetireDesynchronizedConnectionAsync().ConfigureAwait(false);
                throw IndeterminateFailure(exception, cancellationToken);
            }
            catch (Exception exception)
            {
                await RestartSmcAfterFailureOrFenceAsync(
                        smcLease,
                        writeFence,
                        exception,
                        cancellationToken)
                    .ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reads a validated NAND record range into <paramref name="request"/>'s output stream.
    /// </summary>
    internal async Task<PicoFlasherNandReadResult> ReadNandAsync(
        PicoFlasherNandReadRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateWritableOutput(request.Output);
        ThrowIfConnectionRetired();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(TransferBufferSize);
        try
        {
            var writeFence = new PicoFlasherCommandWriteFence();
            PicoFlasherSmcLease smcLease = await AcquireSmcLeaseAsync(buffer, writeFence, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                uint flashConfiguration = await ReadFlashConfigurationAsync(
                        _connection.Transport,
                        buffer,
                        writeFence,
                        cancellationToken)
                    .ConfigureAwait(false);
                uint recordCount = ResolveReadRecordCount(request, flashConfiguration);
                PicoFlasherNandTransferRange transferRange = CreateNandTransferRange(
                    request.StartRecord,
                    recordCount);

                ReportProgress(progress, "pico-nand-read", "Reading NAND records.", 0, recordCount);
                if (request.StartRecord == 0)
                {
                    await ReadNandStreamAsync(
                            _connection.Transport,
                            buffer,
                            request.Output,
                            recordCount,
                            progress,
                            writeFence,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await ReadNandRangeAsync(
                            _connection.Transport,
                            buffer,
                            request.Output,
                            request.StartRecord,
                            recordCount,
                            progress,
                            writeFence,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                await smcLease.RestartAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return new PicoFlasherNandReadResult(
                    flashConfiguration,
                    transferRange.StartRecord,
                    transferRange.EndRecordInclusive,
                    transferRange.RecordCount,
                    transferRange.LogicalByteLength,
                    transferRange.RawByteLength);
            }
            catch (PicoFlasherStreamDesynchronizedException exception)
            {
                if (exception.CanRestartSmc)
                {
                    await RestartSmcAfterFailureOrFenceAsync(
                            smcLease,
                            writeFence,
                            exception.OriginalException,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                if (exception.MustRetire)
                {
                    await RetireDesynchronizedConnectionAsync().ConfigureAwait(false);
                }

                if (exception.RecoveryRequired &&
                    !IsCallerCancellation(exception.OriginalException, cancellationToken))
                {
                    throw IndeterminateFailure(exception.OriginalException, cancellationToken);
                }

                throw RecoveredStreamFailure(exception.OriginalException, cancellationToken);
            }
            catch (PicoFlasherLocatedResponseFramingException exception)
            {
                await RestartSmcAfterFailureOrFenceAsync(
                        smcLease,
                        writeFence,
                        exception,
                        cancellationToken)
                    .ConfigureAwait(false);
                await RetireDesynchronizedConnectionAsync().ConfigureAwait(false);
                throw IndeterminateFailure(exception, cancellationToken);
            }
            catch (PicoFlasherResponseFramingException exception)
            {
                await RestartSmcAfterFailureOrFenceAsync(
                        smcLease,
                        writeFence,
                        exception,
                        cancellationToken)
                    .ConfigureAwait(false);
                await RetireDesynchronizedConnectionAsync().ConfigureAwait(false);
                throw IndeterminateFailure(exception, cancellationToken);
            }
            catch (Exception exception)
            {
                await RestartSmcAfterFailureOrFenceAsync(
                        smcLease,
                        writeFence,
                        exception,
                        cancellationToken)
                    .ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Writes an erase-block-aligned raw NAND record range from <paramref name="request"/>'s input stream.
    /// </summary>
    internal async Task<PicoFlasherNandWriteResult> WriteNandAsync(
        PicoFlasherNandWriteRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        uint recordCount = GetInputRecordCount(request.Input);
        ThrowIfConnectionRetired();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(TransferBufferSize);
        try
        {
            var writeFence = new PicoFlasherCommandWriteFence();
            PicoFlasherSmcLease smcLease = await AcquireSmcLeaseAsync(buffer, writeFence, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                uint flashConfiguration = await ReadFlashConfigurationAsync(
                        _connection.Transport,
                        buffer,
                        writeFence,
                        cancellationToken)
                    .ConfigureAwait(false);
                PicoFlasherNandGeometry geometry = RequireNandGeometry(flashConfiguration);
                ValidateNandRange(request.StartRecord, recordCount, geometry, requireEraseAlignment: true);
                PicoFlasherNandTransferRange transferRange = CreateNandTransferRange(
                    request.StartRecord,
                    recordCount);

                ReportProgress(progress, "pico-nand-write", "Writing NAND records.", 0, recordCount);
                for (uint offset = 0; offset < recordCount; offset++)
                {
                    uint record = checked(request.StartRecord + offset);
                    await ReadInputRecordAsync(
                            request.Input,
                            buffer.AsMemory(PicoFlasherProtocol.CommandSize, PicoFlasherProtocol.NandWireRecordSize),
                            cancellationToken)
                        .ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    PicoFlasherProtocol.WriteCommand(
                        buffer.AsSpan(0, PicoFlasherProtocol.CommandSize),
                        PicoFlasherCommand.WriteFlash,
                        record);
                    await writeFence.WriteAsync(
                            _connection.Transport,
                            buffer.AsMemory(0, TransferBufferSize),
                            cancellationToken)
                        .ConfigureAwait(false);
                    uint status = await ReadNandStatusAtRecordAsync(
                            _connection.Transport,
                            buffer,
                            "write",
                            record,
                            cancellationToken)
                        .ConfigureAwait(false);
                    ThrowForNandFirmwareStatus(status, "pico-nand-write-failed", "write", record);
                    ReportTransferProgress(
                        progress,
                        "pico-nand-write",
                        "Writing NAND records.",
                        offset + 1,
                        recordCount);
                }

                await smcLease.RestartAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return new PicoFlasherNandWriteResult(
                    flashConfiguration,
                    transferRange.StartRecord,
                    transferRange.EndRecordInclusive,
                    transferRange.RecordCount,
                    transferRange.LogicalByteLength,
                    transferRange.RawByteLength);
            }
            catch (PicoFlasherLocatedResponseFramingException exception)
            {
                await RestartSmcAfterFailureOrFenceAsync(
                        smcLease,
                        writeFence,
                        exception,
                        cancellationToken)
                    .ConfigureAwait(false);
                await RetireDesynchronizedConnectionAsync().ConfigureAwait(false);
                throw IndeterminateFailure(exception, cancellationToken);
            }
            catch (PicoFlasherResponseFramingException exception)
            {
                await RestartSmcAfterFailureOrFenceAsync(
                        smcLease,
                        writeFence,
                        exception,
                        cancellationToken)
                    .ConfigureAwait(false);
                await RetireDesynchronizedConnectionAsync().ConfigureAwait(false);
                throw IndeterminateFailure(exception, cancellationToken);
            }
            catch (Exception exception)
            {
                await RestartSmcAfterFailureOrFenceAsync(
                        smcLease,
                        writeFence,
                        exception,
                        cancellationToken)
                    .ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Erases a requested count of complete NAND erase blocks.
    /// </summary>
    internal async Task<PicoFlasherNandEraseResult> EraseNandAsync(
        PicoFlasherNandEraseRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfConnectionRetired();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(PicoFlasherProtocol.CommandSize);
        try
        {
            var writeFence = new PicoFlasherCommandWriteFence();
            PicoFlasherSmcLease smcLease = await AcquireSmcLeaseAsync(buffer, writeFence, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                uint flashConfiguration = await ReadFlashConfigurationAsync(
                        _connection.Transport,
                        buffer,
                        writeFence,
                        cancellationToken)
                    .ConfigureAwait(false);
                PicoFlasherNandGeometry geometry = RequireNandGeometry(flashConfiguration);
                PicoFlasherNandTransferRange eraseRange = ResolveNandEraseRange(request, geometry);

                ReportProgress(
                    progress,
                    "pico-nand-erase",
                    "Erasing NAND blocks.",
                    0,
                    request.EraseBlockCount);
                for (uint eraseBlockOffset = 0; eraseBlockOffset < request.EraseBlockCount; eraseBlockOffset++)
                {
                    uint eraseBlock = checked(request.StartEraseBlock + eraseBlockOffset);
                    uint startRecord = checked(
                        eraseRange.StartRecord + (eraseBlockOffset * geometry.EraseBlockRecordCount));
                    uint endRecordInclusive = checked(startRecord + geometry.EraseBlockRecordCount - 1);
                    uint status = await SendNandEraseStatusCommandAsync(
                            _connection.Transport,
                            buffer,
                            eraseBlock,
                            startRecord,
                            endRecordInclusive,
                            writeFence,
                            cancellationToken)
                        .ConfigureAwait(false);
                    ThrowForNandEraseFirmwareStatus(
                        status,
                        eraseBlock,
                        startRecord,
                        endRecordInclusive);
                    ReportTransferProgress(
                        progress,
                        "pico-nand-erase",
                        "Erasing NAND blocks.",
                        eraseBlockOffset + 1,
                        request.EraseBlockCount);
                }

                await smcLease.RestartAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return new PicoFlasherNandEraseResult(
                    flashConfiguration,
                    request.StartEraseBlock,
                    checked(request.StartEraseBlock + request.EraseBlockCount - 1),
                    request.EraseBlockCount,
                    eraseRange.StartRecord,
                    eraseRange.EndRecordInclusive,
                    eraseRange.RecordCount,
                    geometry.EraseBlockRecordCount,
                    eraseRange.StartLogicalByteOffset,
                    eraseRange.EndLogicalByteOffsetInclusive,
                    eraseRange.LogicalByteLength,
                    eraseRange.StartRawByteOffset,
                    eraseRange.EndRawByteOffsetInclusive,
                    eraseRange.RawByteLength);
            }
            catch (PicoFlasherLocatedResponseFramingException exception)
            {
                await RestartSmcAfterFailureOrFenceAsync(
                        smcLease,
                        writeFence,
                        exception,
                        cancellationToken)
                    .ConfigureAwait(false);
                await RetireDesynchronizedConnectionAsync().ConfigureAwait(false);
                throw IndeterminateFailure(exception, cancellationToken);
            }
            catch (PicoFlasherResponseFramingException exception)
            {
                await RestartSmcAfterFailureOrFenceAsync(
                        smcLease,
                        writeFence,
                        exception,
                        cancellationToken)
                    .ConfigureAwait(false);
                await RetireDesynchronizedConnectionAsync().ConfigureAwait(false);
                throw IndeterminateFailure(exception, cancellationToken);
            }
            catch (Exception exception)
            {
                await RestartSmcAfterFailureOrFenceAsync(
                        smcLease,
                        writeFence,
                        exception,
                        cancellationToken)
                    .ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task ReadNandStreamAsync(
        IPicoFlasherTransport transport,
        byte[] buffer,
        Stream output,
        uint recordCount,
        IProgress<OperationProgress>? progress,
        PicoFlasherCommandWriteFence writeFence,
        CancellationToken cancellationToken)
    {
        uint receivedRecordCount = 0;
        bool streamCommandWriteInFlight = false;
        bool streamActive = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            streamCommandWriteInFlight = true;
            await SendCommandAsync(
                    transport,
                    buffer,
                    PicoFlasherCommand.ReadFlashStream,
                    recordCount,
                    writeFence,
                    cancellationToken)
                .ConfigureAwait(false);
            streamCommandWriteInFlight = false;
            streamActive = true;

            while (receivedRecordCount < recordCount)
            {
                uint status = await ReadNandStatusAtRecordAsync(
                        transport,
                        buffer,
                        "read",
                        receivedRecordCount,
                        cancellationToken)
                    .ConfigureAwait(false);
                ThrowForNandFirmwareStatus(
                    status,
                    "pico-nand-read-failed",
                    "read",
                    receivedRecordCount);
                await ReadNandRecordAtRecordAsync(
                        transport,
                        buffer.AsMemory(0, PicoFlasherProtocol.NandWireRecordSize),
                        "read",
                        receivedRecordCount,
                        cancellationToken)
                    .ConfigureAwait(false);
                receivedRecordCount++;
                await WriteOutputRecordAsync(
                        output,
                        buffer,
                        cancellationToken)
                    .ConfigureAwait(false);
                ReportTransferProgress(
                    progress,
                    "pico-nand-read",
                    "Reading NAND records.",
                    receivedRecordCount,
                    recordCount);
            }

            streamActive = false;
        }
        catch (Exception exception)
        {
            if (streamCommandWriteInFlight)
            {
                throw new PicoFlasherStreamDesynchronizedException(
                    exception,
                    canRestartSmc: false,
                    mustRetire: true,
                    recoveryRequired: true);
            }

            if (streamActive)
            {
                StreamCleanupOutcome cleanupOutcome = await StopAndQuiesceStreamAfterFailureAsync(
                        transport,
                        buffer,
                        PicoFlasherCommand.ReadFlashStream,
                        MaximumQueuedNandStreamCleanupByteCount,
                        writeFence)
                    .ConfigureAwait(false);

                // The firmware does not acknowledge a stream reset, so a quiet drain cannot prove
                // that a late stream frame will not arrive after the next command.
                throw new PicoFlasherStreamDesynchronizedException(
                    exception,
                    canRestartSmc: cleanupOutcome is not StreamCleanupOutcome.ResetWriteIndeterminate,
                    mustRetire: true,
                    recoveryRequired: cleanupOutcome is not StreamCleanupOutcome.Quiesced);
            }

            throw;
        }
    }

    private static async Task ReadNandRangeAsync(
        IPicoFlasherTransport transport,
        byte[] buffer,
        Stream output,
        uint startRecord,
        uint recordCount,
        IProgress<OperationProgress>? progress,
        PicoFlasherCommandWriteFence writeFence,
        CancellationToken cancellationToken)
    {
        for (uint offset = 0; offset < recordCount; offset++)
        {
            uint record = checked(startRecord + offset);
            uint status = await SendNandStatusCommandAsync(
                    transport,
                    buffer,
                    PicoFlasherCommand.ReadFlash,
                    record,
                    "read",
                    writeFence,
                    cancellationToken)
                .ConfigureAwait(false);
            ThrowForNandFirmwareStatus(status, "pico-nand-read-failed", "read", record);
            await ReadNandRecordAtRecordAsync(
                    transport,
                    buffer.AsMemory(0, PicoFlasherProtocol.NandWireRecordSize),
                    "read",
                    record,
                    cancellationToken)
                .ConfigureAwait(false);
            await WriteOutputRecordAsync(
                    output,
                    buffer,
                    cancellationToken)
                .ConfigureAwait(false);
            ReportTransferProgress(
                progress,
                "pico-nand-read",
                "Reading NAND records.",
                offset + 1,
                recordCount);
        }
    }

    private static async Task ReadEmmcStreamAsync(
        IPicoFlasherTransport transport,
        byte[] buffer,
        Stream output,
        uint sectorCount,
        IProgress<OperationProgress>? progress,
        PicoFlasherCommandWriteFence writeFence,
        CancellationToken cancellationToken)
    {
        uint receivedSectorCount = 0;
        bool streamCommandWriteInFlight = false;
        bool streamActive = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            streamCommandWriteInFlight = true;
            await SendCommandAsync(
                    transport,
                    buffer,
                    PicoFlasherCommand.EmmcReadStream,
                    sectorCount,
                    writeFence,
                    cancellationToken)
                .ConfigureAwait(false);
            streamCommandWriteInFlight = false;
            streamActive = true;

            while (receivedSectorCount < sectorCount)
            {
                uint status = await ReadEmmcStatusAsync(
                        transport,
                        buffer,
                        cancellationToken,
                        sector: receivedSectorCount)
                    .ConfigureAwait(false);
                ThrowForEmmcFirmwareStatus(
                    status,
                    "pico-emmc-read-failed",
                    "read",
                    receivedSectorCount);
                await ReadEmmcResponseExactlyAsync(
                        transport,
                        buffer.AsMemory(0, PicoFlasherProtocol.EmmcSectorSize),
                        cancellationToken,
                        sector: receivedSectorCount)
                    .ConfigureAwait(false);
                receivedSectorCount++;
                await WriteEmmcOutputSectorAsync(output, buffer, cancellationToken).ConfigureAwait(false);
                ReportTransferProgress(
                    progress,
                    "pico-emmc-read",
                    "Reading eMMC sectors.",
                    receivedSectorCount,
                    sectorCount);
            }

            streamActive = false;
        }
        catch (Exception exception)
        {
            if (streamCommandWriteInFlight)
            {
                throw new PicoFlasherStreamDesynchronizedException(
                    exception,
                    canRestartSmc: false,
                    mustRetire: true,
                    recoveryRequired: true);
            }

            if (streamActive)
            {
                StreamCleanupOutcome cleanupOutcome = await StopAndQuiesceStreamAfterFailureAsync(
                        transport,
                        buffer,
                        PicoFlasherCommand.EmmcReadStream,
                        MaximumQueuedEmmcStreamCleanupByteCount,
                        writeFence)
                    .ConfigureAwait(false);

                throw new PicoFlasherStreamDesynchronizedException(
                    exception,
                    canRestartSmc: cleanupOutcome is not StreamCleanupOutcome.ResetWriteIndeterminate,
                    mustRetire: true,
                    recoveryRequired: cleanupOutcome is not StreamCleanupOutcome.Quiesced ||
                        exception is PicoFlasherResponseFramingException);
            }

            throw;
        }
    }

    private static async Task ReadEmmcRangeAsync(
        IPicoFlasherTransport transport,
        byte[] buffer,
        Stream output,
        uint startSector,
        uint sectorCount,
        IProgress<OperationProgress>? progress,
        PicoFlasherCommandWriteFence writeFence,
        CancellationToken cancellationToken)
    {
        for (uint offset = 0; offset < sectorCount; offset++)
        {
            uint sector = checked(startSector + offset);
            uint status = await SendEmmcStatusCommandAsync(
                    transport,
                    buffer,
                    PicoFlasherCommand.EmmcRead,
                    sector,
                    writeFence,
                    cancellationToken,
                    responseSector: sector)
                .ConfigureAwait(false);
            ThrowForEmmcFirmwareStatus(status, "pico-emmc-read-failed", "read", sector);
            await ReadEmmcResponseExactlyAsync(
                    transport,
                    buffer.AsMemory(0, PicoFlasherProtocol.EmmcSectorSize),
                    cancellationToken,
                    sector: sector)
                .ConfigureAwait(false);
            await WriteEmmcOutputSectorAsync(output, buffer, cancellationToken).ConfigureAwait(false);
            ReportTransferProgress(
                progress,
                "pico-emmc-read",
                "Reading eMMC sectors.",
                offset + 1,
                sectorCount);
        }
    }

    private static async Task<PicoFlasherEmmcMetadata> ReadEmmcMetadataAsync(
        IPicoFlasherTransport transport,
        byte[] buffer,
        PicoFlasherCommandWriteFence writeFence,
        CancellationToken cancellationToken)
    {
        uint flashConfiguration = await ReadEmmcFlashConfigurationAsync(
                transport,
                buffer,
                writeFence,
                cancellationToken)
            .ConfigureAwait(false);
        bool detected = await DetectEmmcAsync(
                transport,
                buffer,
                writeFence,
                cancellationToken)
            .ConfigureAwait(false);
        if (!detected)
        {
            throw new OperationFailureException(
                ExitCode.DeviceUnavailable,
                "pico-emmc-not-detected",
                "The PicoFlasher did not detect eMMC storage.");
        }

        uint initializeStatus = await SendEmmcStatusCommandAsync(
                transport,
                buffer,
                PicoFlasherCommand.EmmcInitialize,
                0,
                writeFence,
                cancellationToken)
            .ConfigureAwait(false);
        ThrowForEmmcFirmwareStatus(
            initializeStatus,
            "pico-emmc-initialize-failed",
            "initialize",
            null);

        byte[] cid = await ReadEmmcRawResponseAsync(
                transport,
                buffer,
                PicoFlasherCommand.EmmcGetCid,
                PicoFlasherProtocol.EmmcCidSize,
                writeFence,
                cancellationToken)
            .ConfigureAwait(false);
        ValidateEmmcIdentity(cid, "cid", "CID");
        byte[] csd = await ReadEmmcRawResponseAsync(
                transport,
                buffer,
                PicoFlasherCommand.EmmcGetCsd,
                PicoFlasherProtocol.EmmcCsdSize,
                writeFence,
                cancellationToken)
            .ConfigureAwait(false);
        ValidateEmmcIdentity(csd, "csd", "CSD");
        byte[] extendedCsd = await ReadEmmcRawResponseAsync(
                transport,
                buffer,
                PicoFlasherCommand.EmmcGetExtendedCsd,
                PicoFlasherProtocol.EmmcExtendedCsdSize,
                writeFence,
                cancellationToken)
            .ConfigureAwait(false);
        uint capacitySectorCount = PicoFlasherProtocol.ReadEmmcExtendedCsdSectorCount(extendedCsd);
        ValidateEmmcCapacity(capacitySectorCount);
        return new PicoFlasherEmmcMetadata(
            flashConfiguration,
            capacitySectorCount,
            cid,
            csd,
            extendedCsd);
    }

    private static async ValueTask<uint> ReadEmmcFlashConfigurationAsync(
        IPicoFlasherTransport transport,
        byte[] buffer,
        PicoFlasherCommandWriteFence writeFence,
        CancellationToken cancellationToken)
    {
        return await SendEmmcStatusCommandAsync(
                transport,
                buffer,
                PicoFlasherCommand.GetFlashConfiguration,
                0,
                writeFence,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask<bool> DetectEmmcAsync(
        IPicoFlasherTransport transport,
        byte[] buffer,
        PicoFlasherCommandWriteFence writeFence,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await SendCommandAsync(
                transport,
                buffer,
                PicoFlasherCommand.EmmcDetect,
                0,
                writeFence,
                cancellationToken)
            .ConfigureAwait(false);
        await ReadEmmcResponseExactlyAsync(transport, buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        return buffer[0] != 0;
    }

    private static async ValueTask<byte[]> ReadEmmcRawResponseAsync(
        IPicoFlasherTransport transport,
        byte[] buffer,
        PicoFlasherCommand command,
        int responseSize,
        PicoFlasherCommandWriteFence writeFence,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await SendCommandAsync(
                transport,
                buffer,
                command,
                0,
                writeFence,
                cancellationToken)
            .ConfigureAwait(false);
        await ReadEmmcResponseExactlyAsync(
                transport,
                buffer.AsMemory(0, responseSize),
                cancellationToken)
            .ConfigureAwait(false);
        return buffer.AsSpan(0, responseSize).ToArray();
    }

    private static async ValueTask<uint> SendEmmcStatusCommandAsync(
        IPicoFlasherTransport transport,
        byte[] buffer,
        PicoFlasherCommand command,
        uint logicalBlockAddress,
        PicoFlasherCommandWriteFence writeFence,
        CancellationToken cancellationToken,
        uint? responseSector = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await SendCommandAsync(
                transport,
                buffer,
                command,
                logicalBlockAddress,
                writeFence,
                cancellationToken)
            .ConfigureAwait(false);
        return await ReadEmmcStatusAsync(transport, buffer, cancellationToken, responseSector).ConfigureAwait(false);
    }

    private static async ValueTask<uint> ReadEmmcStatusAsync(
        IPicoFlasherTransport transport,
        byte[] buffer,
        CancellationToken cancellationToken,
        uint? sector = null)
    {
        await ReadEmmcResponseExactlyAsync(
                transport,
                buffer.AsMemory(0, PicoFlasherProtocol.StatusSize),
                cancellationToken,
                sector)
            .ConfigureAwait(false);
        return PicoFlasherProtocol.ReadStatus(buffer.AsSpan(0, PicoFlasherProtocol.StatusSize));
    }

    private static async ValueTask ReadEmmcResponseExactlyAsync(
        IPicoFlasherTransport transport,
        Memory<byte> destination,
        CancellationToken cancellationToken,
        uint? sector = null)
    {
        try
        {
            await ReadResponseExactlyAsync(transport, destination, cancellationToken).ConfigureAwait(false);
        }
        catch (PicoFlasherResponseFramingException exception) when (sector is uint value)
        {
            throw CreateEmmcReadResponseFramingException(value, exception);
        }
    }

    private static async ValueTask<uint> SendNandStatusCommandAsync(
        IPicoFlasherTransport transport,
        byte[] buffer,
        PicoFlasherCommand command,
        uint record,
        string action,
        PicoFlasherCommandWriteFence writeFence,
        CancellationToken cancellationToken)
    {
        try
        {
            return await SendStatusCommandAsync(
                    transport,
                    buffer,
                    command,
                    record,
                    writeFence,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PicoFlasherResponseFramingException exception)
        {
            throw CreateNandRecordResponseFramingException(action, record, exception);
        }
    }

    private static async ValueTask<uint> ReadNandStatusAtRecordAsync(
        IPicoFlasherTransport transport,
        byte[] buffer,
        string action,
        uint record,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadStatusAsync(transport, buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (PicoFlasherResponseFramingException exception)
        {
            throw CreateNandRecordResponseFramingException(action, record, exception);
        }
    }

    private static async ValueTask ReadNandRecordAtRecordAsync(
        IPicoFlasherTransport transport,
        Memory<byte> destination,
        string action,
        uint record,
        CancellationToken cancellationToken)
    {
        try
        {
            await ReadResponseExactlyAsync(transport, destination, cancellationToken).ConfigureAwait(false);
        }
        catch (PicoFlasherResponseFramingException exception)
        {
            throw CreateNandRecordResponseFramingException(action, record, exception);
        }
    }

    private static async ValueTask<uint> SendNandEraseStatusCommandAsync(
        IPicoFlasherTransport transport,
        byte[] buffer,
        uint eraseBlock,
        uint startRecord,
        uint endRecordInclusive,
        PicoFlasherCommandWriteFence writeFence,
        CancellationToken cancellationToken)
    {
        try
        {
            return await SendStatusCommandAsync(
                    transport,
                    buffer,
                    PicoFlasherCommand.EraseFlash,
                    startRecord,
                    writeFence,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PicoFlasherResponseFramingException exception)
        {
            throw CreateNandEraseResponseFramingException(
                eraseBlock,
                startRecord,
                endRecordInclusive,
                exception);
        }
    }

    private static async ValueTask ReadResponseExactlyAsync(
        IPicoFlasherTransport transport,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        try
        {
            await transport.ReadExactlyAsync(destination, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw new PicoFlasherResponseFramingException(exception);
        }
    }

    private static async ValueTask<StreamCleanupOutcome> StopAndQuiesceStreamAfterFailureAsync(
        IPicoFlasherTransport transport,
        byte[] buffer,
        PicoFlasherCommand streamCommand,
        int maximumQueuedByteCount,
        PicoFlasherCommandWriteFence writeFence)
    {
        try
        {
            using var resetTimeout = new CancellationTokenSource(StreamResetTimeout);
            await SendCommandAsync(
                    transport,
                    buffer,
                    streamCommand,
                    0,
                    writeFence,
                    resetTimeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return StreamCleanupOutcome.ResetWriteIndeterminate;
        }

        using var quiescenceTimeout = new CancellationTokenSource();
        try
        {
            int drainedByteCount = 0;
            while (true)
            {
                quiescenceTimeout.CancelAfter(StreamQuiescenceTimeout);
                try
                {
                    // A bytewise drain safely handles queued partial status or payload frames.
                    await transport.ReadExactlyAsync(
                            buffer.AsMemory(0, 1),
                            quiescenceTimeout.Token)
                        .ConfigureAwait(false);
                    if (!quiescenceTimeout.TryReset())
                    {
                        return StreamCleanupOutcome.DrainFailed;
                    }

                    if (drainedByteCount == maximumQueuedByteCount)
                    {
                        return StreamCleanupOutcome.DrainFailed;
                    }

                    drainedByteCount++;
                }
                catch (OperationCanceledException) when (quiescenceTimeout.IsCancellationRequested)
                {
                    return StreamCleanupOutcome.Quiesced;
                }
                catch (OperationFailureException exception) when (
                    exception.Code == ExitCode.InputOutput &&
                    exception.Kind == "pico-transport-timeout")
                {
                    // A short configured no-progress timeout also proves an empty bytewise drain.
                    return StreamCleanupOutcome.Quiesced;
                }
            }
        }
        catch (Exception)
        {
            return StreamCleanupOutcome.DrainFailed;
        }
    }

    private static async ValueTask WriteOutputRecordAsync(
        Stream output,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        try
        {
            await output.WriteAsync(
                    buffer.AsMemory(0, PicoFlasherProtocol.NandWireRecordSize),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IOException)
        {
            throw new OperationFailureException(
                ExitCode.InputOutput,
                "pico-nand-output-write-failed",
                "The NAND output file could not be written.");
        }
    }

    private static async ValueTask WriteEmmcOutputSectorAsync(
        Stream output,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        try
        {
            await output.WriteAsync(
                    buffer.AsMemory(0, PicoFlasherProtocol.EmmcSectorSize),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IOException)
        {
            throw new OperationFailureException(
                ExitCode.InputOutput,
                "pico-emmc-output-write-failed",
                "The eMMC output file could not be written.");
        }
    }

    private static async ValueTask ReadInputRecordAsync(
        Stream input,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        try
        {
            await input.ReadExactlyAsync(destination, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (EndOfStreamException)
        {
            throw new OperationFailureException(
                ExitCode.InputOutput,
                "pico-nand-input-truncated",
                "The NAND input file became shorter while it was being written.");
        }
        catch (IOException)
        {
            throw new OperationFailureException(
                ExitCode.InputOutput,
                "pico-nand-input-read-failed",
                "The NAND input file could not be read.");
        }
    }

    private static async ValueTask<uint> ReadFlashConfigurationAsync(
        IPicoFlasherTransport transport,
        byte[] buffer,
        PicoFlasherCommandWriteFence writeFence,
        CancellationToken cancellationToken)
    {
        return await SendStatusCommandAsync(
                transport,
                buffer,
                PicoFlasherCommand.GetFlashConfiguration,
                0,
                writeFence,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask<uint> SendStatusCommandAsync(
        IPicoFlasherTransport transport,
        byte[] buffer,
        PicoFlasherCommand command,
        uint logicalBlockAddress,
        PicoFlasherCommandWriteFence writeFence,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await SendCommandAsync(
                transport,
                buffer,
                command,
                logicalBlockAddress,
                writeFence,
                cancellationToken)
            .ConfigureAwait(false);
        return await ReadStatusAsync(transport, buffer, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask SendCommandAsync(
        IPicoFlasherTransport transport,
        byte[] buffer,
        PicoFlasherCommand command,
        uint logicalBlockAddress,
        PicoFlasherCommandWriteFence writeFence,
        CancellationToken cancellationToken)
    {
        PicoFlasherProtocol.WriteCommand(
            buffer.AsSpan(0, PicoFlasherProtocol.CommandSize),
            command,
            logicalBlockAddress);
        await writeFence.WriteAsync(
                transport,
                buffer.AsMemory(0, PicoFlasherProtocol.CommandSize),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask<uint> ReadStatusAsync(
        IPicoFlasherTransport transport,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        await ReadResponseExactlyAsync(
                transport,
                buffer.AsMemory(0, PicoFlasherProtocol.StatusSize),
                cancellationToken)
            .ConfigureAwait(false);
        return PicoFlasherProtocol.ReadStatus(buffer.AsSpan(0, PicoFlasherProtocol.StatusSize));
    }

    private static void ThrowForNandFirmwareStatus(
        uint status,
        string kind,
        string action,
        uint record)
    {
        if (status == 0)
        {
            return;
        }

        throw new OperationFailureException(
            ExitCode.DeviceUnavailable,
            kind,
            $"The PicoFlasher NAND {action} operation failed at record {record} with firmware status 0x{status:X8}.");
    }

    private static void ThrowForNandEraseFirmwareStatus(
        uint status,
        uint eraseBlock,
        uint startRecord,
        uint endRecordInclusive)
    {
        if (status == 0)
        {
            return;
        }

        throw new OperationFailureException(
            ExitCode.DeviceUnavailable,
            "pico-nand-erase-failed",
            $"The PicoFlasher NAND erase operation failed at erase block {eraseBlock} (records {startRecord} through {endRecordInclusive}) with firmware status 0x{status:X8}.");
    }

    private static void ThrowForEmmcFirmwareStatus(
        uint status,
        string kind,
        string action,
        uint? sector)
    {
        if (status == 0)
        {
            return;
        }

        string sectorDescription = sector is uint value
            ? $" at sector {value}"
            : string.Empty;
        throw new OperationFailureException(
            ExitCode.DeviceUnavailable,
            kind,
            $"The PicoFlasher eMMC {action} operation failed{sectorDescription} with firmware status 0x{status:X8}.");
    }

    private static PicoFlasherNandGeometry RequireNandGeometry(uint flashConfiguration)
    {
        if (PicoFlasherNandGeometry.IsConsoleAbsent(flashConfiguration))
        {
            throw new OperationFailureException(
                ExitCode.DeviceUnavailable,
                "pico-console-not-detected",
                "No console flash was detected by the PicoFlasher.");
        }

        if (PicoFlasherNandGeometry.IsEmmcFlashConfiguration(flashConfiguration))
        {
            throw new OperationFailureException(
                ExitCode.InvalidData,
                "pico-nand-unavailable",
                "The detected console uses eMMC instead of NAND.");
        }

        if (!PicoFlasherNandGeometry.TryFromFlashConfiguration(flashConfiguration, out PicoFlasherNandGeometry? geometry) ||
            geometry is null)
        {
            throw new OperationFailureException(
                ExitCode.InvalidData,
                "pico-nand-geometry-unsupported",
                "The console NAND flash configuration is unsupported and cannot be modified safely.");
        }

        return geometry;
    }

    private static uint ResolveReadRecordCount(
        PicoFlasherNandReadRequest request,
        uint flashConfiguration)
    {
        if (PicoFlasherNandGeometry.IsConsoleAbsent(flashConfiguration))
        {
            throw new OperationFailureException(
                ExitCode.DeviceUnavailable,
                "pico-console-not-detected",
                "No console flash was detected by the PicoFlasher.");
        }

        if (PicoFlasherNandGeometry.IsEmmcFlashConfiguration(flashConfiguration))
        {
            throw new OperationFailureException(
                ExitCode.InvalidData,
                "pico-nand-unavailable",
                "The detected console uses eMMC instead of NAND.");
        }

        if (PicoFlasherNandGeometry.TryFromFlashConfiguration(
                flashConfiguration,
                out PicoFlasherNandGeometry? geometry) &&
            geometry is not null)
        {
            uint knownGeometryRecordCount = request.RecordCount ?? (request.StartRecord < geometry.RecordCount
                ? geometry.RecordCount - request.StartRecord
                : 0);
            ValidateNandRange(request.StartRecord, knownGeometryRecordCount, geometry, requireEraseAlignment: false);
            return knownGeometryRecordCount;
        }

        if (request.RecordCount is not uint explicitRecordCount)
        {
            throw new OperationFailureException(
                ExitCode.InvalidData,
                "pico-nand-geometry-unsupported",
                "The console NAND flash configuration is unsupported. Supply an explicit bounded read range.");
        }

        ValidateUnknownNandReadRange(request.StartRecord, explicitRecordCount);
        return explicitRecordCount;
    }

    private static void ValidateUnknownNandReadRange(uint startRecord, uint recordCount)
    {
        if (recordCount == 0 ||
            startRecord >= MaximumKnownNandRecordCount ||
            recordCount > MaximumKnownNandRecordCount - startRecord)
        {
            throw new OperationFailureException(
                ExitCode.Usage,
                "pico-nand-range-invalid",
                "The requested NAND record range is empty or exceeds the supported maximum read range.");
        }
    }

    private static void ValidateEmmcIdentity(
        ReadOnlySpan<byte> identity,
        string kind,
        string label)
    {
        foreach (byte value in identity)
        {
            if (value != 0)
            {
                return;
            }
        }

        throw new OperationFailureException(
            ExitCode.InvalidData,
            $"pico-emmc-{kind}-invalid",
            $"The PicoFlasher returned an all-zero eMMC {label}.");
    }

    private static void ValidateEmmcCapacity(uint capacitySectorCount)
    {
        if (capacitySectorCount < PicoFlasherProtocol.EmmcLegacyReadWindowSectorCount ||
            capacitySectorCount >= PicoFlasherProtocol.EmmcMaximumSupportedSectorCountExclusive)
        {
            throw new OperationFailureException(
                ExitCode.InvalidData,
                "pico-emmc-capacity-invalid",
                "The eMMC EXT_CSD reports an unsupported sector capacity.");
        }
    }

    private static void ValidateEmmcReadRange(
        uint startSector,
        uint sectorCount,
        uint capacitySectorCount)
    {
        ulong endExclusive = (ulong)startSector + sectorCount;
        if (sectorCount == 0 ||
            startSector >= PicoFlasherProtocol.EmmcLegacyReadWindowSectorCount ||
            endExclusive > PicoFlasherProtocol.EmmcLegacyReadWindowSectorCount ||
            endExclusive > capacitySectorCount ||
            endExclusive >= PicoFlasherProtocol.EmmcMaximumSupportedSectorCountExclusive)
        {
            throw new OperationFailureException(
                ExitCode.Usage,
                "pico-emmc-range-invalid",
                "The requested eMMC sector range is empty or outside the supported 48 MiB read window.");
        }
    }

    private static void ValidateNandRange(
        uint startRecord,
        uint recordCount,
        PicoFlasherNandGeometry geometry,
        bool requireEraseAlignment)
    {
        if (recordCount == 0 ||
            startRecord >= geometry.RecordCount ||
            recordCount > geometry.RecordCount - startRecord)
        {
            throw new OperationFailureException(
                ExitCode.Usage,
                "pico-nand-range-invalid",
                "The requested NAND record range is empty or outside the detected flash geometry.");
        }

        if (requireEraseAlignment &&
            ((startRecord % geometry.EraseBlockRecordCount) != 0 ||
             (recordCount % geometry.EraseBlockRecordCount) != 0))
        {
            throw new OperationFailureException(
                ExitCode.Usage,
                "pico-nand-erase-alignment-required",
                "NAND writes must start and end on detected erase-block boundaries.");
        }
    }

    private static PicoFlasherNandTransferRange ResolveNandEraseRange(
        PicoFlasherNandEraseRequest request,
        PicoFlasherNandGeometry geometry)
    {
        if (request.EraseBlockCount == 0)
        {
            throw new OperationFailureException(
                ExitCode.Usage,
                "pico-nand-range-invalid",
                "The requested NAND erase-block range is empty or outside the detected flash geometry.");
        }

        try
        {
            uint startRecord = checked(request.StartEraseBlock * geometry.EraseBlockRecordCount);
            uint recordCount = checked(request.EraseBlockCount * geometry.EraseBlockRecordCount);
            ValidateNandRange(startRecord, recordCount, geometry, requireEraseAlignment: true);
            return CreateNandTransferRange(startRecord, recordCount);
        }
        catch (OverflowException)
        {
            throw new OperationFailureException(
                ExitCode.Usage,
                "pico-nand-range-invalid",
                "The requested NAND erase-block range is outside the detected flash geometry.");
        }
    }

    private static PicoFlasherNandTransferRange CreateNandTransferRange(
        uint startRecord,
        uint recordCount)
    {
        uint endRecordInclusive = checked(startRecord + recordCount - 1);
        long logicalByteLength = checked((long)recordCount * PicoFlasherProtocol.NandDataSize);
        long rawByteLength = checked((long)recordCount * PicoFlasherProtocol.NandWireRecordSize);
        long startLogicalByteOffset = checked((long)startRecord * PicoFlasherProtocol.NandDataSize);
        long startRawByteOffset = checked((long)startRecord * PicoFlasherProtocol.NandWireRecordSize);
        return new PicoFlasherNandTransferRange(
            startRecord,
            endRecordInclusive,
            recordCount,
            startLogicalByteOffset,
            checked(startLogicalByteOffset + logicalByteLength - 1),
            logicalByteLength,
            startRawByteOffset,
            checked(startRawByteOffset + rawByteLength - 1),
            rawByteLength);
    }

    private static uint GetInputRecordCount(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead)
        {
            throw new OperationFailureException(
                ExitCode.Usage,
                "pico-nand-input-unreadable",
                "The NAND input stream must be readable.");
        }

        if (!input.CanSeek)
        {
            throw new OperationFailureException(
                ExitCode.Usage,
                "pico-nand-input-unseekable",
                "The NAND input stream must have a known length.");
        }

        long remainingLength;
        try
        {
            remainingLength = checked(input.Length - input.Position);
        }
        catch (NotSupportedException)
        {
            throw new OperationFailureException(
                ExitCode.Usage,
                "pico-nand-input-unseekable",
                "The NAND input stream must have a known length.");
        }

        if (remainingLength <= 0 ||
            (remainingLength % PicoFlasherProtocol.NandWireRecordSize) != 0 ||
            (remainingLength / PicoFlasherProtocol.NandWireRecordSize) > uint.MaxValue)
        {
            throw new OperationFailureException(
                ExitCode.InvalidData,
                "pico-nand-input-length-invalid",
                "The NAND input length must contain one or more complete data-and-spare records.");
        }

        return checked((uint)(remainingLength / PicoFlasherProtocol.NandWireRecordSize));
    }

    private static void ValidateWritableOutput(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
        {
            throw new OperationFailureException(
                ExitCode.Usage,
                "pico-nand-output-unwritable",
                "The NAND output stream must be writable.");
        }
    }

    private static void ValidateWritableEmmcOutput(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
        {
            throw new OperationFailureException(
                ExitCode.Usage,
                "pico-emmc-output-unwritable",
                "The eMMC output stream must be writable.");
        }
    }

    private static string GetStorageKind(uint flashConfiguration, PicoFlasherNandGeometry? nandGeometry)
    {
        if (PicoFlasherNandGeometry.IsConsoleAbsent(flashConfiguration))
        {
            return "none";
        }

        if (PicoFlasherNandGeometry.IsEmmcFlashConfiguration(flashConfiguration))
        {
            return "emmc";
        }

        return nandGeometry is null ? "unsupported-nand" : "nand";
    }

    private static void ReportTransferProgress(
        IProgress<OperationProgress>? progress,
        string kind,
        string message,
        uint completed,
        uint total)
    {
        if (completed == total || (completed % ProgressRecordInterval) == 0)
        {
            ReportProgress(progress, kind, message, completed, total);
        }
    }

    private static void ReportProgress(
        IProgress<OperationProgress>? progress,
        string kind,
        string message,
        uint completed,
        uint total)
    {
        progress?.Report(new OperationProgress(kind, message, completed: completed, total: total));
    }

    private static OperationFailureException UnsupportedFirmware()
    {
        return new OperationFailureException(
            ExitCode.MissingPrerequisite,
            "pico-firmware-unsupported",
            "The connected PicoFlasher firmware is unsupported. Update it to version 4 or later.");
    }

    private void ThrowIfConnectionRetired()
    {
        if (_connection.IsRetired)
        {
            throw StreamRecoveryRequired();
        }
    }

    private async Task<PicoFlasherSmcLease> AcquireSmcLeaseAsync(
        byte[] buffer,
        PicoFlasherCommandWriteFence writeFence,
        CancellationToken cancellationToken)
    {
        try
        {
            return await PicoFlasherSmcLease
                .AcquireAsync(_connection.Transport, buffer, writeFence, _smcStopWait, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PicoFlasherSmcLeaseFramingException exception)
        {
            await RetireDesynchronizedConnectionAsync().ConfigureAwait(false);
            throw IndeterminateFailure(exception.OriginalException, cancellationToken);
        }
    }

    private async ValueTask RestartSmcAfterFailureOrFenceAsync(
        PicoFlasherSmcLease smcLease,
        PicoFlasherCommandWriteFence writeFence,
        Exception? originalException = null,
        CancellationToken cancellationToken = default)
    {
        if (!writeFence.IsIndeterminate &&
            await smcLease.RestartAfterFailureAsync().ConfigureAwait(false))
        {
            return;
        }

        await RetireDesynchronizedConnectionAsync().ConfigureAwait(false);
        if (originalException is not null)
        {
            throw IndeterminateFailure(originalException, cancellationToken);
        }

        throw StreamRecoveryRequired();
    }

    private async ValueTask RetireDesynchronizedConnectionAsync()
    {
        try
        {
            await _connection.RetireAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The mandatory transport retirement must not hide the original operation failure.
        }
    }

    private static OperationFailureException StreamRecoveryRequired()
    {
        return new OperationFailureException(
            ExitCode.InputOutput,
            "pico-stream-recovery-required",
            "The PicoFlasher command stream could not be proven synchronized safely. The device connection was closed to prevent protocol desynchronization; physically reset or power-cycle the flasher before trying again.");
    }

    private static bool IsCallerCancellation(Exception exception, CancellationToken cancellationToken)
    {
        return exception switch
        {
            OperationCanceledException when cancellationToken.IsCancellationRequested => true,
            PicoFlasherLocatedResponseFramingException response =>
                IsCallerCancellation(response.ResponseFramingException, cancellationToken),
            PicoFlasherResponseFramingException response =>
                IsCallerCancellation(response.OriginalException, cancellationToken),
            _ => false,
        };
    }

    private static Exception RecoveredStreamFailure(
        Exception exception,
        CancellationToken cancellationToken)
    {
        RethrowCallerCancellation(exception, cancellationToken);
        return exception is PicoFlasherLocatedResponseFramingException or PicoFlasherResponseFramingException
            ? IndeterminateFailure(exception, cancellationToken)
            : exception;
    }

    private static void RethrowCallerCancellation(
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is PicoFlasherLocatedResponseFramingException locatedResponse)
        {
            RethrowCallerCancellation(locatedResponse.ResponseFramingException, cancellationToken);
            return;
        }

        if (exception is PicoFlasherResponseFramingException response)
        {
            RethrowCallerCancellation(response.OriginalException, cancellationToken);
            return;
        }

        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }

    private static OperationFailureException IndeterminateFailure(
        Exception exception,
        CancellationToken cancellationToken)
    {
        RethrowCallerCancellation(exception, cancellationToken);
        return GetInputOutputFailure(exception) ?? StreamRecoveryRequired();
    }

    private static OperationFailureException? GetInputOutputFailure(Exception exception)
    {
        return exception switch
        {
            OperationFailureException failure when failure.Code == ExitCode.InputOutput => failure,
            PicoFlasherLocatedResponseFramingException response =>
                GetInputOutputFailure(response.ResponseFramingException) is OperationFailureException failure
                    ? new OperationFailureException(
                        failure.Code,
                        failure.Kind,
                        $"{failure.Message} {response.Failure.Message}")
                    : response.Failure,
            PicoFlasherResponseFramingException response => GetInputOutputFailure(response.OriginalException),
            _ => null,
        };
    }

    private static PicoFlasherLocatedResponseFramingException CreateNandRecordResponseFramingException(
        string action,
        uint record,
        PicoFlasherResponseFramingException responseFramingException)
    {
        return new PicoFlasherLocatedResponseFramingException(
            responseFramingException,
            new OperationFailureException(
                ExitCode.InputOutput,
                $"pico-nand-{action}-response-failed",
                $"The PicoFlasher NAND {action} response for record {record} was incomplete or stalled."));
    }

    private static PicoFlasherLocatedResponseFramingException CreateNandEraseResponseFramingException(
        uint eraseBlock,
        uint startRecord,
        uint endRecordInclusive,
        PicoFlasherResponseFramingException responseFramingException)
    {
        return new PicoFlasherLocatedResponseFramingException(
            responseFramingException,
            new OperationFailureException(
                ExitCode.InputOutput,
                "pico-nand-erase-response-failed",
                $"The PicoFlasher NAND erase response for erase block {eraseBlock} (records {startRecord} through {endRecordInclusive}) was incomplete or stalled."));
    }

    private static PicoFlasherLocatedResponseFramingException CreateEmmcReadResponseFramingException(
        uint sector,
        PicoFlasherResponseFramingException responseFramingException)
    {
        return new PicoFlasherLocatedResponseFramingException(
            responseFramingException,
            new OperationFailureException(
                ExitCode.InputOutput,
                "pico-emmc-read-response-failed",
                $"The PicoFlasher eMMC read response for sector {sector} was incomplete or stalled."));
    }

    private enum StreamCleanupOutcome
    {
        Quiesced,
        ResetWriteIndeterminate,
        DrainFailed,
    }

    private sealed class PicoFlasherStreamDesynchronizedException : Exception
    {
        internal PicoFlasherStreamDesynchronizedException(
            Exception originalException,
            bool canRestartSmc,
            bool mustRetire,
            bool recoveryRequired)
            : base("The PicoFlasher stream response could not be framed safely.", originalException)
        {
            OriginalException = originalException;
            CanRestartSmc = canRestartSmc;
            MustRetire = mustRetire;
            RecoveryRequired = recoveryRequired;
        }

        internal Exception OriginalException { get; }

        internal bool CanRestartSmc { get; }

        internal bool MustRetire { get; }

        internal bool RecoveryRequired { get; }
    }

    private sealed class PicoFlasherResponseFramingException : Exception
    {
        internal PicoFlasherResponseFramingException(Exception innerException)
            : base("The PicoFlasher response could not be framed safely.", innerException)
        {
            OriginalException = innerException;
        }

        internal Exception OriginalException { get; }
    }

    private sealed class PicoFlasherLocatedResponseFramingException : Exception
    {
        internal PicoFlasherLocatedResponseFramingException(
            PicoFlasherResponseFramingException responseFramingException,
            OperationFailureException failure)
            : base("The PicoFlasher response could not be framed safely at the requested index.", responseFramingException)
        {
            ResponseFramingException = responseFramingException;
            Failure = failure;
        }

        internal PicoFlasherResponseFramingException ResponseFramingException { get; }

        internal OperationFailureException Failure { get; }
    }

    private sealed record PicoFlasherEmmcMetadata(
        uint FlashConfiguration,
        uint CapacitySectorCount,
        byte[] Cid,
        byte[] Csd,
        byte[] ExtendedCsd);

    private sealed record PicoFlasherNandTransferRange(
        uint StartRecord,
        uint EndRecordInclusive,
        uint RecordCount,
        long StartLogicalByteOffset,
        long EndLogicalByteOffsetInclusive,
        long LogicalByteLength,
        long StartRawByteOffset,
        long EndRawByteOffsetInclusive,
        long RawByteLength);

    private sealed class PicoFlasherSmcLeaseFramingException : Exception
    {
        internal PicoFlasherSmcLeaseFramingException(Exception innerException)
            : base("The PicoFlasher SMC preflight command could not be framed safely.", innerException)
        {
            OriginalException = innerException;
        }

        internal Exception OriginalException { get; }
    }

    private sealed class PicoFlasherCommandWriteFence
    {
        internal bool IsIndeterminate { get; private set; }

        internal async ValueTask WriteAsync(
            IPicoFlasherTransport transport,
            ReadOnlyMemory<byte> source,
            CancellationToken cancellationToken)
        {
            if (IsIndeterminate)
            {
                throw new InvalidOperationException("The PicoFlasher command stream has an indeterminate transport state.");
            }

            try
            {
                await transport.WriteAsync(source, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                IsIndeterminate = true;
                throw;
            }
        }
    }

    private sealed class PicoFlasherSmcLease
    {
        private readonly IPicoFlasherTransport _transport;
        private readonly byte[] _buffer;
        private readonly PicoFlasherCommandWriteFence _writeFence;
        private bool _restartRequired;

        private PicoFlasherSmcLease(
            IPicoFlasherTransport transport,
            byte[] buffer,
            PicoFlasherCommandWriteFence writeFence)
        {
            _transport = transport;
            _buffer = buffer;
            _writeFence = writeFence;
        }

        internal static async Task<PicoFlasherSmcLease> AcquireAsync(
            IPicoFlasherTransport transport,
            byte[] buffer,
            PicoFlasherCommandWriteFence writeFence,
            TimeSpan smcStopWait,
            CancellationToken cancellationToken)
        {
            var lease = new PicoFlasherSmcLease(transport, buffer, writeFence);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await SendCommandAsync(
                        transport,
                        buffer,
                        PicoFlasherCommand.SetSmcWorkaround,
                        DisabledSmcWorkaround,
                        writeFence,
                        cancellationToken)
                    .ConfigureAwait(false);
                lease._restartRequired = true;
                await SendCommandAsync(
                        transport,
                        buffer,
                        PicoFlasherCommand.StopSmc,
                        0,
                        writeFence,
                        cancellationToken)
                    .ConfigureAwait(false);
                await Task.Delay(smcStopWait, cancellationToken).ConfigureAwait(false);
                return lease;
            }
            catch (Exception exception)
            {
                if (writeFence.IsIndeterminate || !await lease.RestartAfterFailureAsync().ConfigureAwait(false))
                {
                    throw new PicoFlasherSmcLeaseFramingException(exception);
                }

                throw;
            }
        }

        internal async ValueTask RestartAsync()
        {
            if (!_restartRequired)
            {
                return;
            }

            if (_writeFence.IsIndeterminate)
            {
                throw new InvalidOperationException("The SMC restart command has an indeterminate transport state.");
            }

            using var restartTimeout = new CancellationTokenSource(SmcRestartTimeout);
            await SendCommandAsync(
                    _transport,
                    _buffer,
                    PicoFlasherCommand.StartSmc,
                    0,
                    _writeFence,
                    restartTimeout.Token)
                .ConfigureAwait(false);

            _restartRequired = false;
        }

        internal async ValueTask<bool> RestartAfterFailureAsync()
        {
            if (_writeFence.IsIndeterminate)
            {
                return false;
            }

            if (!_restartRequired)
            {
                return true;
            }

            try
            {
                await RestartAsync().ConfigureAwait(false);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}

/// <summary>
/// Selects a NAND record range to read. A null count reads through the end of the detected NAND.
/// </summary>
internal sealed record PicoFlasherNandReadRequest(Stream Output, uint StartRecord, uint? RecordCount);

/// <summary>
/// Selects a raw NAND input stream and its destination record.
/// </summary>
internal sealed record PicoFlasherNandWriteRequest(Stream Input, uint StartRecord);

/// <summary>
/// Selects complete NAND erase blocks by their absolute erase-block indices.
/// </summary>
internal sealed record PicoFlasherNandEraseRequest(uint StartEraseBlock, uint EraseBlockCount);

/// <summary>
/// Selects a bounded raw eMMC sector range to read.
/// </summary>
internal sealed record PicoFlasherEmmcReadRequest(Stream Output, uint StartSector, uint SectorCount);

/// <summary>
/// Describes a firmware-gated flash probe.
/// </summary>
internal sealed record PicoFlasherProbeResult(
    uint FirmwareVersion,
    string DevicePath,
    string? SerialNumber,
    uint FlashConfiguration,
    string StorageKind,
    PicoFlasherNandGeometry? NandGeometry);

/// <summary>
/// Describes a firmware-gated eMMC metadata probe.
/// </summary>
internal sealed record PicoFlasherEmmcProbeResult(
    uint FirmwareVersion,
    string DevicePath,
    string? SerialNumber,
    uint FlashConfiguration,
    uint CapacitySectorCount,
    byte[] Cid,
    byte[] Csd,
    byte[] ExtendedCsd);

/// <summary>
/// Describes a raw NAND read with distinct logical-data and data-and-spare byte counts.
/// </summary>
internal sealed record PicoFlasherNandReadResult(
    uint FlashConfiguration,
    uint StartRecord,
    uint EndRecordInclusive,
    uint RecordCount,
    long LogicalByteLength,
    long RawByteLength);

/// <summary>
/// Describes a raw eMMC sector read.
/// </summary>
internal sealed record PicoFlasherEmmcReadResult(
    uint FlashConfiguration,
    uint CapacitySectorCount,
    uint StartSector,
    uint SectorCount,
    long LogicalByteLength);

/// <summary>
/// Describes a raw NAND write with distinct logical-data and data-and-spare byte counts.
/// </summary>
internal sealed record PicoFlasherNandWriteResult(
    uint FlashConfiguration,
    uint StartRecord,
    uint EndRecordInclusive,
    uint RecordCount,
    long LogicalByteLength,
    long RawByteLength);

/// <summary>
/// Describes a NAND erase range with checked inclusive record and byte endpoints.
/// </summary>
internal sealed record PicoFlasherNandEraseResult(
    uint FlashConfiguration,
    uint StartEraseBlock,
    uint EndEraseBlockInclusive,
    uint EraseBlockCount,
    uint StartRecord,
    uint EndRecordInclusive,
    uint RecordCount,
    uint EraseBlockRecordCount,
    long StartLogicalByteOffset,
    long EndLogicalByteOffsetInclusive,
    long LogicalByteLength,
    long StartRawByteOffset,
    long EndRawByteOffsetInclusive,
    long RawByteLength);
