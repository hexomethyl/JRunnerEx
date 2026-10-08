using JRunner.Core.Devices.PicoFlasher;
using Xunit;

namespace JRunner.Core.Tests.Devices.PicoFlasher;

public sealed class PicoFlasherNandGeometryTests
{
    [Theory]
    [InlineData(0x0119_8010U, 0x0100_0000L, 0x0000_8000U, 0x0108_0000L, 0x4000, 0x20U)]
    [InlineData(0x0119_8030U, 0x0400_0000L, 0x0002_0000U, 0x0420_0000L, 0x4000, 0x20U)]
    [InlineData(0x0002_3010U, 0x0100_0000L, 0x0000_8000U, 0x0108_0000L, 0x4000, 0x20U)]
    [InlineData(0x0004_3000U, 0x0100_0000L, 0x0000_8000U, 0x0108_0000L, 0x4000, 0x20U)]
    [InlineData(0x008A_3020U, 0x1000_0000L, 0x0008_0000U, 0x1080_0000L, 0x20000, 0x100U)]
    [InlineData(0x00AA_3020U, 0x2000_0000L, 0x0010_0000U, 0x2100_0000L, 0x20000, 0x100U)]
    [InlineData(0x008C_3020U, 0x1000_0000L, 0x0008_0000U, 0x1080_0000L, 0x20000, 0x100U)]
    [InlineData(0x00AC_3020U, 0x2000_0000L, 0x0010_0000U, 0x2100_0000L, 0x20000, 0x100U)]
    public void TryFromFlashConfiguration_returns_the_exact_geometry_for_every_known_configuration(
        uint flashConfiguration,
        long expectedLogicalByteLength,
        uint expectedRecordCount,
        long expectedWireByteLength,
        int expectedEraseBlockLogicalByteLength,
        uint expectedEraseBlockRecordCount)
    {
        var recognized = PicoFlasherNandGeometry.TryFromFlashConfiguration(flashConfiguration, out var geometry);

        Assert.True(recognized);
        var actual = Assert.IsType<PicoFlasherNandGeometry>(geometry);
        Assert.Equal(flashConfiguration, actual.FlashConfiguration);
        Assert.Equal(expectedLogicalByteLength, actual.LogicalByteLength);
        Assert.Equal(expectedRecordCount, actual.RecordCount);
        Assert.Equal(expectedWireByteLength, actual.WireByteLength);
        Assert.Equal(expectedEraseBlockLogicalByteLength, actual.EraseBlockLogicalByteLength);
        Assert.Equal(expectedEraseBlockRecordCount, actual.EraseBlockRecordCount);
        Assert.Equal(
            expectedLogicalByteLength,
            checked((long)actual.RecordCount * PicoFlasherProtocol.NandDataSize));
        Assert.Equal(
            expectedWireByteLength,
            checked((long)actual.RecordCount * PicoFlasherProtocol.NandWireRecordSize));
        Assert.Equal(
            (long)expectedEraseBlockLogicalByteLength,
            checked((long)actual.EraseBlockRecordCount * PicoFlasherProtocol.NandDataSize));
        Assert.Equal(0L, actual.LogicalByteLength % actual.EraseBlockLogicalByteLength);

        Assert.True(PicoFlasherNandGeometry.TryGetEraseBlockGeometry(
            flashConfiguration,
            out var eraseBlockLogicalByteLength,
            out var eraseBlockRecordCount));
        Assert.Equal(expectedEraseBlockLogicalByteLength, eraseBlockLogicalByteLength);
        Assert.Equal(expectedEraseBlockRecordCount, eraseBlockRecordCount);
    }

    [Theory]
    [InlineData(0U, 0U, 0x4000, 0x20U)]
    [InlineData(0U, 1U, 0x4000, 0x20U)]
    [InlineData(0U, 2U, 0x4000, 0x20U)]
    [InlineData(0U, 3U, 0x4000, 0x20U)]
    [InlineData(1U, 0U, 0x4000, 0x20U)]
    [InlineData(1U, 1U, 0x4000, 0x20U)]
    [InlineData(1U, 2U, 0x20000, 0x100U)]
    [InlineData(1U, 3U, 0x40000, 0x200U)]
    [InlineData(2U, 0U, 0x4000, 0x20U)]
    [InlineData(2U, 1U, 0x4000, 0x20U)]
    [InlineData(2U, 2U, 0x20000, 0x100U)]
    [InlineData(2U, 3U, 0x40000, 0x200U)]
    [InlineData(3U, 0U, 0x4000, 0x20U)]
    [InlineData(3U, 1U, 0x4000, 0x20U)]
    [InlineData(3U, 2U, 0x20000, 0x100U)]
    [InlineData(3U, 3U, 0x40000, 0x200U)]
    public void Erase_geometry_derivation_ignores_unrelated_bits_for_every_major_and_minor_combination(
        uint major,
        uint minor,
        int expectedEraseBlockLogicalByteLength,
        uint expectedEraseBlockRecordCount)
    {
        // Keep the all-zero field combination distinct from an absent console.
        var flashConfiguration = (major << 17) | (minor << 4) | 1U;

        Assert.True(PicoFlasherNandGeometry.TryGetEraseBlockGeometry(
            flashConfiguration,
            out var eraseBlockLogicalByteLength,
            out var eraseBlockRecordCount));
        Assert.Equal(expectedEraseBlockLogicalByteLength, eraseBlockLogicalByteLength);
        Assert.Equal(expectedEraseBlockRecordCount, eraseBlockRecordCount);
        Assert.False(PicoFlasherNandGeometry.TryFromFlashConfiguration(flashConfiguration, out var geometry));
        Assert.Null(geometry);

        // Set unrelated bits without changing either erase field or producing an absent/eMMC word.
        var maskedFlashConfiguration = flashConfiguration | 0xBFF9_FFCFU;

        Assert.True(PicoFlasherNandGeometry.TryGetEraseBlockGeometry(
            maskedFlashConfiguration,
            out var maskedEraseBlockLogicalByteLength,
            out var maskedEraseBlockRecordCount));
        Assert.Equal(expectedEraseBlockLogicalByteLength, maskedEraseBlockLogicalByteLength);
        Assert.Equal(expectedEraseBlockRecordCount, maskedEraseBlockRecordCount);
        Assert.False(PicoFlasherNandGeometry.TryFromFlashConfiguration(maskedFlashConfiguration, out var maskedGeometry));
        Assert.Null(maskedGeometry);
    }

    [Theory]
    [InlineData(0U)]
    [InlineData(0xFFFF_FFFFU)]
    public void Absent_console_configurations_have_no_geometry(uint flashConfiguration)
    {
        var recognized = PicoFlasherNandGeometry.TryFromFlashConfiguration(flashConfiguration, out var geometry);

        Assert.True(PicoFlasherNandGeometry.IsConsoleAbsent(flashConfiguration));
        Assert.False(recognized);
        Assert.Null(geometry);
        Assert.False(PicoFlasherNandGeometry.TryGetEraseBlockGeometry(
            flashConfiguration,
            out var eraseBlockLogicalByteLength,
            out var eraseBlockRecordCount));
        Assert.Equal(0, eraseBlockLogicalByteLength);
        Assert.Equal(0U, eraseBlockRecordCount);
    }

    [Theory]
    [InlineData(0xC000_0000U, true)]
    [InlineData(0xC123_4567U, true)]
    [InlineData(0xCFFF_FFFFU, true)]
    [InlineData(0xBFFF_FFFFU, false)]
    [InlineData(0xD000_0000U, false)]
    [InlineData(0x0000_0000U, false)]
    public void IsEmmcFlashConfiguration_matches_only_the_high_nibble(uint flashConfiguration, bool expected)
    {
        Assert.Equal(expected, PicoFlasherNandGeometry.IsEmmcFlashConfiguration(flashConfiguration));
    }

    [Theory]
    [InlineData(0xC000_0000U)]
    [InlineData(0xC123_4567U)]
    [InlineData(0xC002_0020U)]
    [InlineData(0xC002_0030U)]
    [InlineData(0xC004_0020U)]
    [InlineData(0xC004_0030U)]
    [InlineData(0xC006_0020U)]
    [InlineData(0xC006_0030U)]
    [InlineData(0xCFFF_FFFFU)]
    public void Emmc_configurations_have_no_nand_geometry(uint flashConfiguration)
    {
        var recognized = PicoFlasherNandGeometry.TryFromFlashConfiguration(flashConfiguration, out var geometry);

        Assert.True(PicoFlasherNandGeometry.IsEmmcFlashConfiguration(flashConfiguration));
        Assert.False(recognized);
        Assert.Null(geometry);
        Assert.False(PicoFlasherNandGeometry.TryGetEraseBlockGeometry(
            flashConfiguration,
            out var eraseBlockLogicalByteLength,
            out var eraseBlockRecordCount));
        Assert.Equal(0, eraseBlockLogicalByteLength);
        Assert.Equal(0U, eraseBlockRecordCount);
    }

    [Theory]
    [InlineData(0x0000_0001U, 0x4000, 0x20U)]
    [InlineData(0x0119_8020U, 0x4000, 0x20U)]
    [InlineData(0x008A_3030U, 0x40000, 0x200U)]
    [InlineData(0x00AC_3021U, 0x20000, 0x100U)]
    public void Unsupported_nand_configurations_have_erase_geometry_but_no_capacity_geometry(
        uint flashConfiguration,
        int expectedEraseBlockLogicalByteLength,
        uint expectedEraseBlockRecordCount)
    {
        var recognized = PicoFlasherNandGeometry.TryFromFlashConfiguration(flashConfiguration, out var geometry);

        Assert.False(PicoFlasherNandGeometry.IsConsoleAbsent(flashConfiguration));
        Assert.False(PicoFlasherNandGeometry.IsEmmcFlashConfiguration(flashConfiguration));
        Assert.False(recognized);
        Assert.Null(geometry);

        Assert.True(PicoFlasherNandGeometry.TryGetEraseBlockGeometry(
            flashConfiguration,
            out var eraseBlockLogicalByteLength,
            out var eraseBlockRecordCount));
        Assert.Equal(expectedEraseBlockLogicalByteLength, eraseBlockLogicalByteLength);
        Assert.Equal(expectedEraseBlockRecordCount, eraseBlockRecordCount);
    }
}
