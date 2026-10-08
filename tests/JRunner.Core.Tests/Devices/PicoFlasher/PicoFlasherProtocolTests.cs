using JRunner.Core.Devices.PicoFlasher;
using JRunner.Core.Contracts;
using Xunit;

namespace JRunner.Core.Tests.Devices.PicoFlasher;

public sealed class PicoFlasherProtocolTests
{
    [Fact]
    public void Protocol_constants_match_the_shared_firmware_contract()
    {
        Assert.Equal((ushort)0x600D, PicoFlasherProtocol.VendorId);
        Assert.Equal((ushort)0x7001, PicoFlasherProtocol.ProductId);
        Assert.Equal(0, PicoFlasherProtocol.CommandCdcInterfaceNumber);
        Assert.Equal(115_200, PicoFlasherProtocol.CommandBaudRate);
        Assert.Equal(4U, PicoFlasherProtocol.MinimumSupportedFirmwareVersion);
        Assert.Equal(5, PicoFlasherProtocol.CommandSize);
        Assert.Equal(4, PicoFlasherProtocol.StatusSize);
        Assert.Equal(0x200, PicoFlasherProtocol.NandDataSize);
        Assert.Equal(0x10, PicoFlasherProtocol.NandSpareSize);
        Assert.Equal(0x210, PicoFlasherProtocol.NandWireRecordSize);
        Assert.Equal(0x200, PicoFlasherProtocol.EmmcSectorSize);
        Assert.Equal(0x10, PicoFlasherProtocol.EmmcCidSize);
        Assert.Equal(0x10, PicoFlasherProtocol.EmmcCsdSize);
        Assert.Equal(0x200, PicoFlasherProtocol.EmmcExtendedCsdSize);
        Assert.Equal(0xD4, PicoFlasherProtocol.EmmcExtendedCsdSectorCountOffset);
        Assert.Equal(0x18000U, PicoFlasherProtocol.EmmcLegacyReadWindowSectorCount);
        Assert.Equal(0x800000U, PicoFlasherProtocol.EmmcMaximumSupportedSectorCountExclusive);
        Assert.Equal(500, PicoFlasherProtocol.SmcStopWaitMilliseconds);
        Assert.Equal(TimeSpan.FromMilliseconds(500), PicoFlasherProtocol.SmcStopWait);
    }

    [Fact]
    public void Commands_match_the_complete_shared_firmware_opcode_map()
    {
        Assert.Equal(
            new[]
            {
                PicoFlasherCommand.GetVersion,
                PicoFlasherCommand.GetFlashConfiguration,
                PicoFlasherCommand.ReadFlash,
                PicoFlasherCommand.WriteFlash,
                PicoFlasherCommand.ReadFlashStream,
                PicoFlasherCommand.EraseFlash,
                PicoFlasherCommand.SetSmcWorkaround,
                PicoFlasherCommand.StopSmc,
                PicoFlasherCommand.StartSmc,
                PicoFlasherCommand.SetDebugUartBaud,
                PicoFlasherCommand.EmmcDetect,
                PicoFlasherCommand.EmmcInitialize,
                PicoFlasherCommand.EmmcGetCid,
                PicoFlasherCommand.EmmcGetCsd,
                PicoFlasherCommand.EmmcGetExtendedCsd,
                PicoFlasherCommand.EmmcRead,
                PicoFlasherCommand.EmmcReadStream,
                PicoFlasherCommand.EmmcWrite,
                PicoFlasherCommand.RebootToBootloader,
            },
            Enum.GetValues<PicoFlasherCommand>());
    }

    [Theory]
    [InlineData(PicoFlasherCommand.GetVersion, (byte)0x00)]
    [InlineData(PicoFlasherCommand.GetFlashConfiguration, (byte)0x01)]
    [InlineData(PicoFlasherCommand.ReadFlash, (byte)0x02)]
    [InlineData(PicoFlasherCommand.WriteFlash, (byte)0x03)]
    [InlineData(PicoFlasherCommand.ReadFlashStream, (byte)0x04)]
    [InlineData(PicoFlasherCommand.EraseFlash, (byte)0x05)]
    [InlineData(PicoFlasherCommand.SetSmcWorkaround, (byte)0x20)]
    [InlineData(PicoFlasherCommand.StopSmc, (byte)0x21)]
    [InlineData(PicoFlasherCommand.StartSmc, (byte)0x22)]
    [InlineData(PicoFlasherCommand.SetDebugUartBaud, (byte)0x23)]
    [InlineData(PicoFlasherCommand.EmmcDetect, (byte)0x50)]
    [InlineData(PicoFlasherCommand.EmmcInitialize, (byte)0x51)]
    [InlineData(PicoFlasherCommand.EmmcGetCid, (byte)0x52)]
    [InlineData(PicoFlasherCommand.EmmcGetCsd, (byte)0x53)]
    [InlineData(PicoFlasherCommand.EmmcGetExtendedCsd, (byte)0x54)]
    [InlineData(PicoFlasherCommand.EmmcRead, (byte)0x55)]
    [InlineData(PicoFlasherCommand.EmmcReadStream, (byte)0x56)]
    [InlineData(PicoFlasherCommand.EmmcWrite, (byte)0x57)]
    [InlineData(PicoFlasherCommand.RebootToBootloader, (byte)0xFE)]
    public void Write_command_encodes_the_exact_opcode_and_little_endian_lba(
        PicoFlasherCommand command,
        byte expectedOpcode)
    {
        var actual = new byte[PicoFlasherProtocol.CommandSize];

        PicoFlasherProtocol.WriteCommand(actual, command, 0x78563412U);

        Assert.Equal(
            new byte[] { expectedOpcode, 0x12, 0x34, 0x56, 0x78 },
            actual);
    }

    [Fact]
    public void Write_command_rejects_nonexact_destination_sizes()
    {
        Assert.Throws<ArgumentException>(() => PicoFlasherProtocol.WriteCommand(
            new byte[PicoFlasherProtocol.CommandSize - 1],
            PicoFlasherCommand.GetVersion,
            0));
        Assert.Throws<ArgumentException>(() => PicoFlasherProtocol.WriteCommand(
            new byte[PicoFlasherProtocol.CommandSize + 1],
            PicoFlasherCommand.GetVersion,
            0));
    }

    [Fact]
    public void Read_status_decodes_little_endian_values()
    {
        uint status = PicoFlasherProtocol.ReadStatus(new byte[] { 0x12, 0x34, 0x56, 0x78 });

        Assert.Equal(0x78563412U, status);
    }

    [Fact]
    public void Read_status_rejects_nonexact_source_sizes()
    {
        Assert.Throws<ArgumentException>(() => PicoFlasherProtocol.ReadStatus(
            new byte[PicoFlasherProtocol.StatusSize - 1]));
        Assert.Throws<ArgumentException>(() => PicoFlasherProtocol.ReadStatus(
            new byte[PicoFlasherProtocol.StatusSize + 1]));
    }

    [Fact]
    public void Read_emmc_extended_csd_sector_count_decodes_little_endian_value()
    {
        var extendedCsd = new byte[PicoFlasherProtocol.EmmcExtendedCsdSize];
        int sectorCountOffset = PicoFlasherProtocol.EmmcExtendedCsdSectorCountOffset;
        extendedCsd[sectorCountOffset] = 0x12;
        extendedCsd[sectorCountOffset + 1] = 0x34;
        extendedCsd[sectorCountOffset + 2] = 0x56;
        extendedCsd[sectorCountOffset + 3] = 0x78;

        uint sectorCount = PicoFlasherProtocol.ReadEmmcExtendedCsdSectorCount(extendedCsd);

        Assert.Equal(0x78563412U, sectorCount);
    }

    [Fact]
    public void Read_emmc_extended_csd_sector_count_uses_sec_count_offset_not_nonzero_tail()
    {
        var extendedCsd = new byte[PicoFlasherProtocol.EmmcExtendedCsdSize];
        int sectorCountOffset = PicoFlasherProtocol.EmmcExtendedCsdSectorCountOffset;
        extendedCsd[sectorCountOffset] = 0x04;
        extendedCsd[sectorCountOffset + 1] = 0x03;
        extendedCsd[sectorCountOffset + 2] = 0x02;
        extendedCsd[sectorCountOffset + 3] = 0x01;
        extendedCsd[^4] = 0x78;
        extendedCsd[^3] = 0x56;
        extendedCsd[^2] = 0x34;
        extendedCsd[^1] = 0x12;

        uint sectorCount = PicoFlasherProtocol.ReadEmmcExtendedCsdSectorCount(extendedCsd);

        Assert.Equal(0x01020304U, sectorCount);
    }

    [Fact]
    public void Read_emmc_extended_csd_sector_count_rejects_nonexact_source_sizes()
    {
        Assert.Throws<ArgumentException>(() => PicoFlasherProtocol.ReadEmmcExtendedCsdSectorCount(
            new byte[PicoFlasherProtocol.EmmcExtendedCsdSize - 1]));
        Assert.Throws<ArgumentException>(() => PicoFlasherProtocol.ReadEmmcExtendedCsdSectorCount(
            new byte[PicoFlasherProtocol.EmmcExtendedCsdSize + 1]));
    }

    [Fact]
    public void Endpoint_preserves_provided_physical_and_command_identity_without_normalization()
    {
        const string devicePath = " /dev/ttyACM0 ";
        const string physicalDevicePath = " /sys/devices/pci0000:00/usb1/1-2 ";
        const string serialNumber = " serial-001 ";

        var endpoint = new PicoFlasherDeviceEndpoint(
            devicePath,
            serialNumber,
            interfaceNumber: 0,
            physicalDevicePath: physicalDevicePath);
        var endpointWithoutExplicitPhysicalPath = new PicoFlasherDeviceEndpoint(
            "/dev/ttyACM1",
            null,
            interfaceNumber: 1);

        Assert.Equal(devicePath, endpoint.DevicePath);
        Assert.Equal(physicalDevicePath, endpoint.PhysicalDevicePath);
        Assert.Equal(serialNumber, endpoint.SerialNumber);
        Assert.Equal(0, endpoint.InterfaceNumber);
        Assert.Equal("/dev/ttyACM1", endpointWithoutExplicitPhysicalPath.DevicePath);
        Assert.Equal("/dev/ttyACM1", endpointWithoutExplicitPhysicalPath.PhysicalDevicePath);
        Assert.Null(endpointWithoutExplicitPhysicalPath.SerialNumber);
        Assert.Equal(1, endpointWithoutExplicitPhysicalPath.InterfaceNumber);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t ")]
    public void Endpoint_rejects_blank_device_paths(string devicePath)
    {
        Assert.Throws<ArgumentException>(() => new PicoFlasherDeviceEndpoint(devicePath, null, interfaceNumber: 0));
    }

    [Fact]
    public void Endpoint_rejects_blank_serial_numbers_physical_paths_and_negative_interfaces()
    {
        Assert.Throws<ArgumentException>(() => new PicoFlasherDeviceEndpoint("/dev/ttyACM0", " \t ", interfaceNumber: 0));
        Assert.Throws<ArgumentException>(
            () => new PicoFlasherDeviceEndpoint("/dev/ttyACM0", null, interfaceNumber: 0, physicalDevicePath: " "));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PicoFlasherDeviceEndpoint("/dev/ttyACM0", null, interfaceNumber: -1));
    }

    [Fact]
    public void Selector_accepts_no_selector_and_one_exact_selector()
    {
        Assert.True(PicoFlasherDeviceSelector.TryCreate(
            null,
            null,
            out PicoFlasherDeviceSelector? unspecified,
            out PicoFlasherDeviceSelectorError unspecifiedError));
        Assert.Same(PicoFlasherDeviceSelector.Unspecified, unspecified);
        Assert.Equal(PicoFlasherDeviceSelectorError.None, unspecifiedError);
        Assert.True(unspecified!.IsUnspecified);

        Assert.True(PicoFlasherDeviceSelector.TryCreate(
            "/dev/ttyACM0",
            null,
            out PicoFlasherDeviceSelector? deviceSelector,
            out PicoFlasherDeviceSelectorError deviceError));
        Assert.Equal("/dev/ttyACM0", deviceSelector!.DevicePath);
        Assert.Null(deviceSelector.SerialNumber);
        Assert.Equal(PicoFlasherDeviceSelectorError.None, deviceError);

        Assert.True(PicoFlasherDeviceSelector.TryCreate(
            null,
            "serial-001",
            out PicoFlasherDeviceSelector? serialSelector,
            out PicoFlasherDeviceSelectorError serialError));
        Assert.Null(serialSelector!.DevicePath);
        Assert.Equal("serial-001", serialSelector.SerialNumber);
        Assert.Equal(PicoFlasherDeviceSelectorError.None, serialError);
    }

    [Theory]
    [InlineData("", null, PicoFlasherDeviceSelectorError.BlankDevicePath)]
    [InlineData(" ", null, PicoFlasherDeviceSelectorError.BlankDevicePath)]
    [InlineData(null, "", PicoFlasherDeviceSelectorError.BlankSerialNumber)]
    [InlineData(null, "\t", PicoFlasherDeviceSelectorError.BlankSerialNumber)]
    [InlineData("/dev/ttyACM0", "serial-001", PicoFlasherDeviceSelectorError.MultipleSelectors)]
    public void Selector_rejects_blank_or_conflicting_values(
        string? devicePath,
        string? serialNumber,
        PicoFlasherDeviceSelectorError expectedError)
    {
        Assert.False(PicoFlasherDeviceSelector.TryCreate(
            devicePath,
            serialNumber,
            out PicoFlasherDeviceSelector? selector,
            out PicoFlasherDeviceSelectorError error));
        Assert.Null(selector);
        Assert.Equal(expectedError, error);
    }

    [Theory]
    [InlineData("", null, "pico-device-invalid")]
    [InlineData(null, "", "pico-serial-invalid")]
    [InlineData("/dev/ttyACM0", "serial-001", "pico-device-selector-conflict")]
    public void Selector_create_maps_invalid_values_to_usage_failures(
        string? devicePath,
        string? serialNumber,
        string expectedKind)
    {
        OperationFailureException exception = Assert.Throws<OperationFailureException>(
            () => PicoFlasherDeviceSelector.Create(devicePath, serialNumber));

        Assert.Equal(ExitCode.Usage, exception.Code);
        Assert.Equal(expectedKind, exception.Kind);
    }

    [Fact]
    public void No_progress_timeout_validation_has_a_positive_serial_representable_range()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), PicoFlasherProtocol.DefaultNoProgressTimeout);
        Assert.True(PicoFlasherProtocol.IsValidNoProgressTimeout(PicoFlasherProtocol.DefaultNoProgressTimeout));
        Assert.True(PicoFlasherProtocol.IsValidNoProgressTimeout(PicoFlasherProtocol.MaximumNoProgressTimeout));
        Assert.False(PicoFlasherProtocol.IsValidNoProgressTimeout(TimeSpan.Zero));
        Assert.False(PicoFlasherProtocol.IsValidNoProgressTimeout(TimeSpan.FromTicks(-1)));
        Assert.False(PicoFlasherProtocol.IsValidNoProgressTimeout(
            PicoFlasherProtocol.MaximumNoProgressTimeout + TimeSpan.FromTicks(1)));
    }
}
