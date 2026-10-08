using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using JRunner.Core.Binary;
using JRunner.Core.Contracts;
using JRunner.Core.Nand.Conversion;
using JRunner.Core.Nand.Physical;
using JRunner.Core.Nand.Security;
using Xunit;

namespace JRunner.Core.Tests.Nand.Conversion;

public sealed class Rgh2ToRgh3ConversionServiceTests
{
    private const int LogicalTemplateLength = 0x140000;
    private const int PhysicalFlashLength = 0x1080000;
    private const int LogicalPrefixLength = 0x70000;
    private const int PhysicalPrefixLength = 0x73800;
    private const int SmcOffset = 0x1000;
    private const int SmcLength = 0x20;
    private const int FirstStageOffset = 0x8000;
    private const int CbLength = 0x400;

    [Theory]
    [InlineData(NandLegacyLayout.Layout0)]
    [InlineData(NandLegacyLayout.Layout1)]
    [InlineData(NandLegacyLayout.Layout2)]
    public async Task Converts_a_physical_RGH3_template_and_physical_RGH2_flash_without_exposing_key_material(
        NandLegacyLayout expectedFlashLayout)
    {
        using var fixture = CreateFixture(expectedFlashLayout, templatePayloadLength: 0x420);
        byte[] physicalTemplate = NandEccCodec.AddEcc(fixture.Template, NandPhysicalLayout.Layout1).ToArray();
        using var template = new MemoryStream(physicalTemplate, writable: false);
        using var flash = new MemoryStream(fixture.Flash, writable: false);
        using var output = new MemoryStream();

        Rgh2ToRgh3ConversionResult result = await Rgh2ToRgh3ConversionService.ConvertAsync(
            new Rgh2ToRgh3ConversionRequest(template, flash, output, fixture.CpuKey));

        Assert.Equal(0, template.Position);
        Assert.Equal(0, flash.Position);
        Assert.Equal(NandPhysicalFormat.InterleavedEcc, result.Rgh3EccFormat);
        Assert.Equal(NandPhysicalFormat.InterleavedEcc, result.FlashFormat);
        Assert.Equal(expectedFlashLayout, result.FlashLayout);
        Assert.Equal((long)PhysicalFlashLength, result.OutputByteLength);
        Assert.Equal((long)PhysicalPrefixLength, result.RewrittenPrefixByteLength);
        Assert.True(result.SmcPatched);
        Assert.True(result.Rgh3PayloadPatched);

        byte[] convertedFlash = output.ToArray();
        Assert.Equal(fixture.Flash.Length, convertedFlash.Length);
        Assert.Equal(
            fixture.Flash.AsSpan(PhysicalPrefixLength, 0x80).ToArray(),
            convertedFlash.AsSpan(PhysicalPrefixLength, 0x80).ToArray());

        var convertedPrefix = new byte[LogicalPrefixLength];
        NandEccCodec.RemoveEcc(convertedFlash.AsSpan(0, PhysicalPrefixLength), convertedPrefix);
        Assert.Equal(
            fixture.Template.AsSpan(SmcOffset, SmcLength).ToArray(),
            convertedPrefix.AsSpan(SmcOffset, SmcLength).ToArray());
        Assert.Equal(
            fixture.Template.AsSpan(FirstStageOffset, CbLength).ToArray(),
            convertedPrefix.AsSpan(FirstStageOffset, CbLength).ToArray());

        byte[] decodedTemplateCba = DecodeCba(fixture.Template.AsSpan(FirstStageOffset, CbLength));
        int payloadOffset = FirstStageOffset + CbLength;
        byte[] decodedPayload = DecryptCbb(
            convertedPrefix.AsSpan(payloadOffset, fixture.TemplatePayloadLength),
            decodedTemplateCba,
            fixture.ZeroCpuKey);
        try
        {
            Assert.Equal(0x64690002u, ReadUInt32BigEndian(decodedPayload, 0x354));
            Assert.Equal(0x7D8C482Au, ReadUInt32BigEndian(decodedPayload, 0x368));
            Assert.Equal(0x64690006u, ReadUInt32BigEndian(decodedPayload, 0x370));
            Assert.Equal(0xF8491010u, ReadUInt32BigEndian(decodedPayload, 0x37C));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decodedTemplateCba);
            CryptographicOperations.ZeroMemory(decodedPayload);
        }

        int plaintextCbbOffset = payloadOffset + fixture.TemplatePayloadLength;
        Assert.Equal("XBOX_ROM"u8.ToArray(), convertedPrefix.AsSpan(plaintextCbbOffset + 0x392, 8).ToArray());
        string json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("00112233445566778899AABBCCDDEEFF", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            typeof(Rgh2ToRgh3ConversionResult).GetProperties(),
            property => property.PropertyType == typeof(CpuKey) ||
                property.PropertyType == typeof(byte[]) ||
                property.PropertyType == typeof(Memory<byte>) ||
                property.PropertyType == typeof(ReadOnlyMemory<byte>));

        CryptographicOperations.ZeroMemory(physicalTemplate);
        CryptographicOperations.ZeroMemory(convertedFlash);
        CryptographicOperations.ZeroMemory(convertedPrefix);
    }

    [Fact]
    public async Task Pads_a_shorter_physical_bootloader_replacement_to_a_complete_ECC_page()
    {
        using var fixture = CreateFixture(templatePayloadLength: 0x3E0);
        using var template = new MemoryStream(fixture.Template, writable: false);
        using var flash = new MemoryStream(fixture.Flash, writable: false);
        using var output = new MemoryStream();

        Rgh2ToRgh3ConversionResult result = await Rgh2ToRgh3ConversionService.ConvertAsync(
            new Rgh2ToRgh3ConversionRequest(template, flash, output, fixture.CpuKey));

        Assert.Equal((long)PhysicalPrefixLength, result.RewrittenPrefixByteLength);
        byte[] convertedFlash = output.ToArray();
        var convertedPrefix = new byte[LogicalPrefixLength];
        NandEccCodec.RemoveEcc(convertedFlash.AsSpan(0, PhysicalPrefixLength), convertedPrefix);
        Assert.All(convertedPrefix.AsSpan(LogicalPrefixLength - 0x20, 0x20).ToArray(), value => Assert.Equal((byte)0, value));

        CryptographicOperations.ZeroMemory(convertedFlash);
        CryptographicOperations.ZeroMemory(convertedPrefix);
    }

    [Fact]
    public async Task Preserves_the_source_SMC_when_patch_SMC_is_disabled_for_a_logical_template()
    {
        using var fixture = CreateFixture();
        using var template = new MemoryStream(fixture.Template, writable: false);
        using var flash = new MemoryStream(fixture.Flash, writable: false);
        using var output = new MemoryStream();

        Rgh2ToRgh3ConversionResult result = await Rgh2ToRgh3ConversionService.ConvertAsync(
            new Rgh2ToRgh3ConversionRequest(template, flash, output, fixture.CpuKey, patchSmc: false));

        Assert.Equal(NandPhysicalFormat.Logical, result.Rgh3EccFormat);
        Assert.False(result.SmcPatched);
        byte[] convertedFlash = output.ToArray();
        var convertedPrefix = new byte[LogicalPrefixLength];
        NandEccCodec.RemoveEcc(convertedFlash.AsSpan(0, PhysicalPrefixLength), convertedPrefix);
        Assert.Equal(
            fixture.SourceLogicalPrefix.AsSpan(SmcOffset, SmcLength).ToArray(),
            convertedPrefix.AsSpan(SmcOffset, SmcLength).ToArray());

        CryptographicOperations.ZeroMemory(convertedFlash);
        CryptographicOperations.ZeroMemory(convertedPrefix);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Converts_supported_XDK_and_RGL_bootloader_chains_when_XeLL_is_absent(bool usesXdkChain)
    {
        using var fixture = CreateFixture(includeXell: false, sourceSecondStageIsSb: usesXdkChain);
        using var template = new MemoryStream(fixture.Template, writable: false);
        using var flash = new MemoryStream(fixture.Flash, writable: false);
        using var output = new MemoryStream();

        Rgh2ToRgh3ConversionResult result = await Rgh2ToRgh3ConversionService.ConvertAsync(
            new Rgh2ToRgh3ConversionRequest(template, flash, output, fixture.CpuKey));

        const int expectedLogicalPrefixLength = 0x9200;
        int expectedPhysicalPrefixLength = NandEccCodec.GetPhysicalLengthForLogicalLength(expectedLogicalPrefixLength);
        Assert.Equal((long)expectedPhysicalPrefixLength, result.RewrittenPrefixByteLength);
        Assert.True(result.RewrittenPrefixByteLength < PhysicalPrefixLength);

        byte[] convertedFlash = output.ToArray();
        Assert.Equal(fixture.Flash.Length, convertedFlash.Length);
        Assert.Equal(
            fixture.Flash.AsSpan(expectedPhysicalPrefixLength, 0x80).ToArray(),
            convertedFlash.AsSpan(expectedPhysicalPrefixLength, 0x80).ToArray());

        CryptographicOperations.ZeroMemory(convertedFlash);
    }

    [Fact]
    public async Task Converts_flagged_new_crypto_source_bootloaders()
    {
        using var fixture = CreateFixture(sourceUsesNewCrypto: true);
        using var template = new MemoryStream(fixture.Template, writable: false);
        using var flash = new MemoryStream(fixture.Flash, writable: false);
        using var output = new MemoryStream();

        Rgh2ToRgh3ConversionResult result = await Rgh2ToRgh3ConversionService.ConvertAsync(
            new Rgh2ToRgh3ConversionRequest(template, flash, output, fixture.CpuKey));

        Assert.True(result.Rgh3PayloadPatched);
        Assert.Equal((long)PhysicalFlashLength, result.OutputByteLength);
    }

    [Fact]
    public async Task Converts_manufacturing_zero_key_source_bootloaders()
    {
        using var fixture = CreateFixture(sourceUsesManufacturingZeroKey: true);
        using var template = new MemoryStream(fixture.Template, writable: false);
        using var flash = new MemoryStream(fixture.Flash, writable: false);
        using var output = new MemoryStream();

        Rgh2ToRgh3ConversionResult result = await Rgh2ToRgh3ConversionService.ConvertAsync(
            new Rgh2ToRgh3ConversionRequest(template, flash, output, fixture.CpuKey));

        Assert.True(result.Rgh3PayloadPatched);
        Assert.Equal((long)PhysicalFlashLength, result.OutputByteLength);
    }

    [Fact]
    public async Task Maps_invalid_sizes_layouts_CPU_keys_and_cancellation_to_stable_failures()
    {
        CpuKey cpuKey = CpuKey.Parse("00112233445566778899AABBCCDDEEFF");
        using var invalidTemplate = new MemoryStream(new byte[1], writable: false);
        using var validSizeFlash = new MemoryStream(new byte[PhysicalFlashLength], writable: false);
        using var invalidTemplateOutput = new MemoryStream();
        OperationFailureException templateFailure = await Assert.ThrowsAsync<OperationFailureException>(
            () => Rgh2ToRgh3ConversionService.ConvertAsync(
                new Rgh2ToRgh3ConversionRequest(invalidTemplate, validSizeFlash, invalidTemplateOutput, cpuKey)));
        Assert.Equal("invalid-rgh3-ecc-size", templateFailure.Kind);

        using var validSizeTemplate = new MemoryStream(new byte[LogicalTemplateLength], writable: false);
        using var invalidFlash = new MemoryStream(new byte[1], writable: false);
        using var invalidFlashOutput = new MemoryStream();
        OperationFailureException flashFailure = await Assert.ThrowsAsync<OperationFailureException>(
            () => Rgh2ToRgh3ConversionService.ConvertAsync(
                new Rgh2ToRgh3ConversionRequest(validSizeTemplate, invalidFlash, invalidFlashOutput, cpuKey)));
        Assert.Equal("invalid-rgh2-flash-length", flashFailure.Kind);

        using var fixture = CreateFixture();
        byte[] malformedLayout = fixture.Flash.ToArray();
        malformedLayout.AsSpan(0x4400, NandPhysicalGeometry.SpareSize).Clear();
        using var malformedTemplate = new MemoryStream(fixture.Template, writable: false);
        using var malformedFlash = new MemoryStream(malformedLayout, writable: false);
        using var malformedOutput = new MemoryStream();
        OperationFailureException layoutFailure = await Assert.ThrowsAsync<OperationFailureException>(
            () => Rgh2ToRgh3ConversionService.ConvertAsync(
                new Rgh2ToRgh3ConversionRequest(malformedTemplate, malformedFlash, malformedOutput, fixture.CpuKey)));
        Assert.Equal("invalid-rgh2-flash-layout", layoutFailure.Kind);

        byte[] outOfRangeSmcTemplate = fixture.Template.ToArray();
        WriteUInt32BigEndian(outOfRangeSmcTemplate, 0x78, 0x20);
        WriteUInt32BigEndian(outOfRangeSmcTemplate, 0x7C, LogicalPrefixLength);
        using var outOfRangeSmcStream = new MemoryStream(outOfRangeSmcTemplate, writable: false);
        using var outOfRangeSmcFlash = new MemoryStream(fixture.Flash, writable: false);
        using var outOfRangeSmcOutput = new MemoryStream();
        OperationFailureException smcFailure = await Assert.ThrowsAsync<OperationFailureException>(
            () => Rgh2ToRgh3ConversionService.ConvertAsync(
                new Rgh2ToRgh3ConversionRequest(
                    outOfRangeSmcStream,
                    outOfRangeSmcFlash,
                    outOfRangeSmcOutput,
                    fixture.CpuKey)));
        Assert.Equal("invalid-rgh3-ecc-bootloaders", smcFailure.Kind);
        Assert.Equal(0, outOfRangeSmcOutput.Length);

        using var wrongKeyTemplate = new MemoryStream(fixture.Template, writable: false);
        using var wrongKeyFlash = new MemoryStream(fixture.Flash, writable: false);
        using var wrongKeyOutput = new MemoryStream();
        OperationFailureException keyFailure = await Assert.ThrowsAsync<OperationFailureException>(
            () => Rgh2ToRgh3ConversionService.ConvertAsync(
                new Rgh2ToRgh3ConversionRequest(
                    wrongKeyTemplate,
                    wrongKeyFlash,
                    wrongKeyOutput,
                    CpuKey.Parse("FFEEDDCCBBAA99887766554433221100"))));
        Assert.Equal("flash-cbb-decryption-failed", keyFailure.Kind);

        using var cancelledTemplate = new MemoryStream(new byte[LogicalTemplateLength], writable: false);
        using var cancelledFlash = new MemoryStream(new byte[PhysicalFlashLength], writable: false);
        using var cancelledOutput = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => Rgh2ToRgh3ConversionService.ConvertAsync(
                new Rgh2ToRgh3ConversionRequest(cancelledTemplate, cancelledFlash, cancelledOutput, cpuKey),
                cancellationToken: cancellation.Token));

        CryptographicOperations.ZeroMemory(malformedLayout);
        CryptographicOperations.ZeroMemory(outOfRangeSmcTemplate);
    }

    private static ConversionFixture CreateFixture(
        NandLegacyLayout flashLayout = NandLegacyLayout.Layout1,
        int templatePayloadLength = CbLength,
        bool includeXell = true,
        bool sourceSecondStageIsSb = false,
        bool sourceUsesNewCrypto = false,
        bool sourceUsesManufacturingZeroKey = false)
    {
        CpuKey cpuKey = CpuKey.Parse("00112233445566778899AABBCCDDEEFF");
        CpuKey zeroCpuKey = CpuKey.Parse("00000000000000000000000000000000");
        var template = new byte[LogicalTemplateLength];
        template[0] = 0xFF;
        template[1] = 0x4F;
        WriteUInt32BigEndian(template, 0x08, FirstStageOffset);
        WriteUInt32BigEndian(template, 0x78, SmcLength);
        WriteUInt32BigEndian(template, 0x7C, SmcOffset);
        for (int index = 0; index < SmcLength; index++)
        {
            template[SmcOffset + index] = checked((byte)(0xA0 + index));
        }

        byte[] templateCba = CreateEncryptedCba(build: 10918);
        byte[] templatePayloadPlain = CreateCbbPlain(build: 15432, templatePayloadLength);
        WriteUInt32BigEndian(templatePayloadPlain, 0x354, 0x646A0002);
        WriteUInt32BigEndian(templatePayloadPlain, 0x368, 0x01020304);
        WriteUInt32BigEndian(templatePayloadPlain, 0x370, 0x05060708);
        WriteUInt32BigEndian(templatePayloadPlain, 0x37C, 0x090A0B0C);
        byte[] templatePayload = EncryptCbb(templatePayloadPlain, templateCba, zeroCpuKey);
        templateCba.CopyTo(template, FirstStageOffset);
        templatePayload.CopyTo(template, FirstStageOffset + CbLength);

        var sourceLogicalPrefix = new byte[LogicalPrefixLength];
        sourceLogicalPrefix[0] = 0xFF;
        sourceLogicalPrefix[1] = 0x4F;
        WriteUInt32BigEndian(sourceLogicalPrefix, 0x08, FirstStageOffset);
        WriteUInt32BigEndian(sourceLogicalPrefix, 0x78, SmcLength);
        WriteUInt32BigEndian(sourceLogicalPrefix, 0x7C, SmcOffset);
        for (int index = 0; index < SmcLength; index++)
        {
            sourceLogicalPrefix[SmcOffset + index] = checked((byte)(0x30 + index));
        }

        byte[] sourceCba = CreateEncryptedCba(
            build: 9188,
            usesNewCrypto: sourceUsesNewCrypto,
            usesManufacturingZeroKey: sourceUsesManufacturingZeroKey);
        byte[] sourceCbbPlain = CreateCbbPlain(
            build: 9188,
            magic: sourceSecondStageIsSb ? "SB" : "CB");
        "XBOX_ROM"u8.CopyTo(sourceCbbPlain.AsSpan(0x392, 8));
        byte[] sourceCbb = EncryptCbb(
            sourceCbbPlain,
            sourceCba,
            cpuKey,
            usesNewCrypto: sourceUsesNewCrypto,
            usesManufacturingZeroKey: sourceUsesManufacturingZeroKey);
        sourceCba.CopyTo(sourceLogicalPrefix, FirstStageOffset);
        sourceCbb.CopyTo(sourceLogicalPrefix, FirstStageOffset + CbLength);

        if (!includeXell)
        {
            int thirdStageOffset = FirstStageOffset + (2 * CbLength);
            if (sourceSecondStageIsSb)
            {
                WriteBootloaderHeader(sourceLogicalPrefix, thirdStageOffset, "SC", 0x20);
                WriteBootloaderHeader(sourceLogicalPrefix, thirdStageOffset + 0x20, "SD", 0x20);
                WriteBootloaderHeader(sourceLogicalPrefix, thirdStageOffset + 0x40, "SE", 0x20);
            }
            else
            {
                WriteBootloaderHeader(sourceLogicalPrefix, thirdStageOffset, "CD", 0x20);
                WriteBootloaderHeader(sourceLogicalPrefix, thirdStageOffset + 0x20, "SE", 0x20);
            }
        }

        var flash = new byte[PhysicalFlashLength];
        flash.AsSpan().Fill(0x5A);
        NandEccCodec.AddEcc(
            sourceLogicalPrefix,
            flash.AsSpan(0, PhysicalPrefixLength),
            NandPhysicalLayout.FromLegacyLayout(flashLayout));
        byte[] xellSignature =
        {
            0x48, 0x00, 0x00, 0x20, 0x48, 0x00, 0x00, 0xEC,
            0x48, 0x00, 0x00, 0x00, 0x48, 0x00, 0x00, 0x00,
        };
        if (includeXell)
        {
            xellSignature.CopyTo(flash, PhysicalPrefixLength);
        }

        CryptographicOperations.ZeroMemory(templateCba);
        CryptographicOperations.ZeroMemory(templatePayloadPlain);
        CryptographicOperations.ZeroMemory(templatePayload);
        CryptographicOperations.ZeroMemory(sourceCba);
        CryptographicOperations.ZeroMemory(sourceCbbPlain);
        CryptographicOperations.ZeroMemory(sourceCbb);
        return new ConversionFixture(template, sourceLogicalPrefix, flash, cpuKey, zeroCpuKey, templatePayloadLength);
    }

    private static byte[] CreateEncryptedCba(
        int build,
        bool usesNewCrypto = false,
        bool usesManufacturingZeroKey = false)
    {
        byte[] stage = CreateCbbPlain(build);
        if (usesNewCrypto)
        {
            stage[0x06] = 0x10;
        }

        if (usesManufacturingZeroKey)
        {
            stage[0x07] = 0x01;
        }

        for (int index = 0; index < 0x10; index++)
        {
            stage[0x10 + index] = checked((byte)(0x60 + index));
        }

        byte[] firstBootLoaderKey =
        {
            0xDD, 0x88, 0xAD, 0x0C, 0x9E, 0xD6, 0x69, 0xE7,
            0xB5, 0x67, 0x94, 0xFB, 0x68, 0x56, 0x3E, 0xFA,
        };
        byte[] rc4Key = XeCrypt.HmacSha1Truncated(firstBootLoaderKey, stage.AsSpan(0x10, 0x10));
        try
        {
            Rc4.TransformInPlace(rc4Key, stage.AsSpan(0x20));
            return stage;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(firstBootLoaderKey);
            CryptographicOperations.ZeroMemory(rc4Key);
        }
    }

    private static byte[] CreateCbbPlain(int build, int length = CbLength, string magic = "CB")
    {
        var stage = new byte[length];
        stage[0] = checked((byte)magic[0]);
        stage[1] = checked((byte)magic[1]);
        BinaryPrimitives.WriteUInt16BigEndian(stage.AsSpan(2, sizeof(ushort)), checked((ushort)build));
        WriteUInt32BigEndian(stage, 0x0C, length);
        for (int index = 0x20; index < stage.Length; index++)
        {
            stage[index] = unchecked((byte)(index * 13));
        }

        return stage;
    }

    private static byte[] EncryptCbb(
        ReadOnlySpan<byte> plainCbb,
        ReadOnlySpan<byte> encryptedCba,
        CpuKey cpuKey,
        bool usesNewCrypto = false,
        bool usesManufacturingZeroKey = false)
    {
        byte[] decodedCba = DecodeCba(encryptedCba);
        byte[] encrypted = plainCbb.ToArray();
        Span<byte> cpuKeyBytes = stackalloc byte[CpuKey.ByteLength];
        Span<byte> message = stackalloc byte[0x30];
        Span<byte> rc4Key = stackalloc byte[XeCrypt.HmacSha1TagLength];
        try
        {
            cpuKey.CopyTo(cpuKeyBytes);
            message.Clear();
            encrypted.AsSpan(0x10, XeCrypt.HmacSha1TagLength).CopyTo(message);
            if (!usesManufacturingZeroKey)
            {
                cpuKeyBytes.CopyTo(message.Slice(CpuKey.ByteLength, CpuKey.ByteLength));
            }
            int messageLength = 0x20;
            if (usesNewCrypto)
            {
                decodedCba.AsSpan(0, 0x10).CopyTo(message.Slice(0x20, 0x10));
                message[0x26] = 0;
                message[0x27] = 0;
                messageLength = 0x30;
            }

            XeCrypt.HmacSha1Truncated(
                decodedCba.AsSpan(0x10, XeCrypt.HmacSha1TagLength),
                message.Slice(0, messageLength),
                rc4Key);
            Rc4.TransformInPlace(rc4Key, encrypted.AsSpan(0x20));
            return encrypted;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decodedCba);
            CryptographicOperations.ZeroMemory(cpuKeyBytes);
            CryptographicOperations.ZeroMemory(message);
            CryptographicOperations.ZeroMemory(rc4Key);
        }
    }

    private static byte[] DecodeCba(ReadOnlySpan<byte> encryptedCba)
    {
        return BootloaderCrypto.DecryptCb(encryptedCba).Output.ToArray();
    }

    private static byte[] DecryptCbb(ReadOnlySpan<byte> encryptedCbb, ReadOnlySpan<byte> decodedCba, CpuKey cpuKey)
    {
        var decoded = new byte[encryptedCbb.Length];
        Span<byte> cpuKeyBytes = stackalloc byte[CpuKey.ByteLength];
        Span<byte> message = stackalloc byte[CpuKey.ByteLength * 2];
        Span<byte> rc4Key = stackalloc byte[XeCrypt.HmacSha1TagLength];
        try
        {
            cpuKey.CopyTo(cpuKeyBytes);
            encryptedCbb.Slice(0x10, XeCrypt.HmacSha1TagLength).CopyTo(message);
            cpuKeyBytes.CopyTo(message.Slice(CpuKey.ByteLength, CpuKey.ByteLength));
            XeCrypt.HmacSha1Truncated(decodedCba.Slice(0x10, XeCrypt.HmacSha1TagLength), message, rc4Key);
            encryptedCbb.Slice(0, 0x10).CopyTo(decoded);
            rc4Key.CopyTo(decoded.AsSpan(0x10, XeCrypt.HmacSha1TagLength));
            encryptedCbb.Slice(0x20).CopyTo(decoded.AsSpan(0x20));
            Rc4.TransformInPlace(rc4Key, decoded.AsSpan(0x20));
            return decoded;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(decoded);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cpuKeyBytes);
            CryptographicOperations.ZeroMemory(message);
            CryptographicOperations.ZeroMemory(rc4Key);
        }
    }

    private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> source, int offset)
    {
        return BinaryPrimitives.ReadUInt32BigEndian(source.Slice(offset, sizeof(uint)));
    }

    private static void WriteUInt32BigEndian(byte[] destination, int offset, int value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(destination.AsSpan(offset, sizeof(uint)), checked((uint)value));
    }

    private static void WriteUInt32BigEndian(byte[] destination, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(destination.AsSpan(offset, sizeof(uint)), value);
    }

    private static void WriteBootloaderHeader(byte[] destination, int offset, string magic, int length)
    {
        destination[offset] = checked((byte)magic[0]);
        destination[offset + 1] = checked((byte)magic[1]);
        WriteUInt32BigEndian(destination, offset + 0x0C, length);
    }

    private sealed class ConversionFixture : IDisposable
    {
        private bool disposed;

        internal ConversionFixture(
            byte[] template,
            byte[] sourceLogicalPrefix,
            byte[] flash,
            CpuKey cpuKey,
            CpuKey zeroCpuKey,
            int templatePayloadLength)
        {
            Template = template;
            SourceLogicalPrefix = sourceLogicalPrefix;
            Flash = flash;
            CpuKey = cpuKey;
            ZeroCpuKey = zeroCpuKey;
            TemplatePayloadLength = templatePayloadLength;
        }

        internal byte[] Template { get; }

        internal byte[] SourceLogicalPrefix { get; }

        internal byte[] Flash { get; }

        internal CpuKey CpuKey { get; }

        internal CpuKey ZeroCpuKey { get; }

        internal int TemplatePayloadLength { get; }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            CryptographicOperations.ZeroMemory(Template);
            CryptographicOperations.ZeroMemory(SourceLogicalPrefix);
            CryptographicOperations.ZeroMemory(Flash);
        }
    }
}
