using System.Buffers.Binary;
using JRunner.Core.Contracts;
using JRunner.Core.XeBuild.Algorithms;
using Xunit;

namespace JRunner.Core.Tests.XeBuild.Algorithms;

public sealed class BootloaderPatchApplierTests
{
    [Fact]
    public void Apply_places_patches_extends_the_bootloader_and_updates_its_size()
    {
        var bootloader = Enumerable.Range(0, 0x20).Select(static index => (byte)index).ToArray();
        var original = bootloader.ToArray();
        byte[] patchSection =
        [
            0x00, 0x00, 0x00, 0x04,
            0x00, 0x00, 0x00, 0x01,
            0xDE, 0xAD, 0xBE, 0xEF,
            0x00, 0x00, 0x00, 0x20,
            0x00, 0x00, 0x00, 0x02,
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
            0xFF, 0xFF, 0xFF, 0xFF,
        ];

        var result = BootloaderPatchApplier.Apply(bootloader, patchSection);

        Assert.Equal(original, bootloader);
        bootloader[0x04] = 0x00;

        Assert.Equal(2, result.PatchCount);
        Assert.Equal(0x30, result.Length);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, result.Bootloader.Slice(0x04, 0x04).ToArray());
        Assert.Equal(
            new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 },
            result.Bootloader.Slice(0x20, 0x08).ToArray());
        Assert.Equal(0x30U, BinaryPrimitives.ReadUInt32BigEndian(result.Bootloader.Slice(0xC, sizeof(uint))));
        Assert.All(result.Bootloader.Slice(0x28, 0x08).ToArray(), static value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public void Apply_rejects_a_patch_payload_truncated_before_its_declared_word_count()
    {
        byte[] patchSection =
        [
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x01,
            0xDE, 0xAD, 0xBE,
        ];

        var exception = Assert.Throws<OperationFailureException>(
            () => BootloaderPatchApplier.Apply(new byte[0x10], patchSection));

        Assert.Equal("truncated-xebuild-patch-section", exception.Kind);
        Assert.Equal(ExitCode.InvalidData, exception.Code);
    }

    [Fact]
    public void Apply_rejects_data_after_the_section_terminator()
    {
        byte[] patchSection = [0xFF, 0xFF, 0xFF, 0xFF, 0x00];

        var exception = Assert.Throws<OperationFailureException>(
            () => BootloaderPatchApplier.Apply(new byte[0x10], patchSection));

        Assert.Equal("invalid-xebuild-patch-section", exception.Kind);
    }

    [Fact]
    public void Apply_rejects_a_patch_address_outside_the_managed_buffer_range()
    {
        byte[] patchSection =
        [
            0x80, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x01,
            0x01, 0x02, 0x03, 0x04,
            0xFF, 0xFF, 0xFF, 0xFF,
        ];

        var exception = Assert.Throws<OperationFailureException>(
            () => BootloaderPatchApplier.Apply(new byte[0x10], patchSection));

        Assert.Equal("invalid-xebuild-patch-range", exception.Kind);
        Assert.Equal(ExitCode.InvalidData, exception.Code);
    }

    [Fact]
    public void Apply_rejects_a_bootloader_without_a_writable_header()
    {
        var exception = Assert.Throws<OperationFailureException>(
            () => BootloaderPatchApplier.Apply(
                new byte[0x0F],
                new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }));

        Assert.Equal("truncated-bootloader-data", exception.Kind);
    }
}
