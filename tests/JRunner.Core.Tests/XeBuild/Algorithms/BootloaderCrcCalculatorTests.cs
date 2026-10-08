using System.Buffers.Binary;
using JRunner.Core.Contracts;
using JRunner.Core.XeBuild.Algorithms;
using Xunit;

namespace JRunner.Core.Tests.XeBuild.Algorithms;

public sealed class BootloaderCrcCalculatorTests
{
    [Fact]
    public void Calculate_masks_cb_mutable_bytes_and_honors_the_declared_length()
    {
        var bootloader = new byte[0x50];
        for (var index = 0; index < bootloader.Length; index++)
        {
            bootloader[index] = (byte)(index ^ 0xA5);
        }

        bootloader[1] = 0x42;
        BinaryPrimitives.WriteUInt32BigEndian(bootloader.AsSpan(0xC, sizeof(uint)), 0x40);

        var result = BootloaderCrcCalculator.Calculate(bootloader);

        Assert.Equal(0x16CAAEF0U, result.Value);
        Assert.Equal(0x40, result.ProcessedLength);
        Assert.Equal(0x10, result.ClearedOffset);
        Assert.Equal(0x30, result.ClearedLength);
    }

    [Fact]
    public void Calculate_masks_cf_mutable_bytes()
    {
        var bootloader = new byte[0x240];
        for (var index = 0; index < bootloader.Length; index++)
        {
            bootloader[index] = (byte)((index * 37) + 11);
        }

        bootloader[1] = 0x46;
        BinaryPrimitives.WriteUInt32BigEndian(bootloader.AsSpan(0xC, sizeof(uint)), 0x230);

        var result = BootloaderCrcCalculator.Calculate(bootloader);

        Assert.Equal(0xE344D7A7U, result.Value);
        Assert.Equal(0x230, result.ProcessedLength);
        Assert.Equal(0x20, result.ClearedOffset);
        Assert.Equal(0x210, result.ClearedLength);
    }

    [Fact]
    public void Calculate_masks_the_3bl_range_but_hashes_the_remaining_suffix()
    {
        var bootloader = new byte[0x40];
        for (var index = 0; index < bootloader.Length; index++)
        {
            bootloader[index] = (byte)((index * 19) + 7);
        }

        bootloader[1] = 0x44;
        BinaryPrimitives.WriteUInt32BigEndian(bootloader.AsSpan(0xC, sizeof(uint)), 0x31);

        var result = BootloaderCrcCalculator.Calculate(bootloader);
        var changedMaskedByte = bootloader.ToArray();
        changedMaskedByte[0x18] ^= 0xFF;
        var changedSuffixByte = bootloader.ToArray();
        changedSuffixByte[0x25] ^= 0xFF;

        Assert.Equal(0xD969216AU, result.Value);
        Assert.Equal(result.Value, BootloaderCrcCalculator.Calculate(changedMaskedByte).Value);
        Assert.NotEqual(result.Value, BootloaderCrcCalculator.Calculate(changedSuffixByte).Value);
        Assert.Equal(0x10, result.ClearedOffset);
        Assert.Equal(0x10, result.ClearedLength);
    }

    [Fact]
    public void Calculate_rejects_a_truncated_declared_size()
    {
        var bootloader = new byte[0x20];
        bootloader[1] = 0x41;
        BinaryPrimitives.WriteUInt32BigEndian(bootloader.AsSpan(0xC, sizeof(uint)), 0x21);

        var exception = Assert.Throws<OperationFailureException>(
            () => BootloaderCrcCalculator.Calculate(bootloader));

        Assert.Equal("truncated-bootloader-data", exception.Kind);
        Assert.Equal(ExitCode.InvalidData, exception.Code);
    }

    [Fact]
    public void Calculate_rejects_a_truncated_cb_mutable_range()
    {
        var bootloader = new byte[0x20];
        bootloader[1] = 0x42;
        BinaryPrimitives.WriteUInt32BigEndian(bootloader.AsSpan(0xC, sizeof(uint)), 0x20);

        var exception = Assert.Throws<OperationFailureException>(
            () => BootloaderCrcCalculator.Calculate(bootloader));

        Assert.Equal("truncated-bootloader-data", exception.Kind);
    }
}
