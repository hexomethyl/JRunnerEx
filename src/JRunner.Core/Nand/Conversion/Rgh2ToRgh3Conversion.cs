using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using JRunner.Core.Binary;
using JRunner.Core.Contracts;
using JRunner.Core.Nand.Physical;
using JRunner.Core.Nand.Security;

namespace JRunner.Core.Nand.Conversion;

/// <summary>
/// Immutable streams and options for an RGH2-to-RGH3 NAND conversion.
/// </summary>
public sealed record Rgh2ToRgh3ConversionRequest
{
    /// <summary>
    /// Creates a conversion request.
    /// </summary>
    /// <param name="rgh3Ecc">The caller-owned RGH3 ECC template, either 0x140000 logical bytes or 0x14A000 interleaved-ECC bytes.</param>
    /// <param name="rgh2Flash">The caller-owned RGH2 flash image, either 16/64 MB interleaved ECC or 48 MB logical eMMC data.</param>
    /// <param name="output">The caller-owned writable destination. It must not be the same stream as either source.</param>
    /// <param name="cpuKey">The parsed CPU key used only to decrypt the original CB_B.</param>
    /// <param name="patchSmc">Whether to replace the original SMC bytes with the template SMC bytes.</param>
    public Rgh2ToRgh3ConversionRequest(
        Stream rgh3Ecc,
        Stream rgh2Flash,
        Stream output,
        CpuKey cpuKey,
        bool patchSmc = true)
    {
        ArgumentNullException.ThrowIfNull(rgh3Ecc);
        ArgumentNullException.ThrowIfNull(rgh2Flash);
        ArgumentNullException.ThrowIfNull(output);
        if (!rgh3Ecc.CanRead)
        {
            throw new ArgumentException("The RGH3 ECC template stream must be readable.", nameof(rgh3Ecc));
        }

        if (!rgh2Flash.CanRead)
        {
            throw new ArgumentException("The RGH2 flash stream must be readable.", nameof(rgh2Flash));
        }

        if (!output.CanWrite)
        {
            throw new ArgumentException("The conversion output stream must be writable.", nameof(output));
        }

        if (!rgh3Ecc.CanSeek || !rgh2Flash.CanSeek)
        {
            throw new ArgumentException("RGH conversion input streams must be seekable.");
        }

        if (ReferenceEquals(rgh3Ecc, rgh2Flash) ||
            ReferenceEquals(rgh3Ecc, output) ||
            ReferenceEquals(rgh2Flash, output))
        {
            throw new ArgumentException("RGH conversion requires three distinct source and destination streams.");
        }

        Rgh3Ecc = rgh3Ecc;
        Rgh2Flash = rgh2Flash;
        Output = output;
        CpuKey = cpuKey;
        PatchSmc = patchSmc;
    }

    /// <summary>
    /// Gets the caller-owned RGH3 ECC template stream.
    /// </summary>
    public Stream Rgh3Ecc { get; }

    /// <summary>
    /// Gets the caller-owned RGH2 flash stream.
    /// </summary>
    public Stream Rgh2Flash { get; }

    /// <summary>
    /// Gets the caller-owned conversion output stream.
    /// </summary>
    public Stream Output { get; }

    /// <summary>
    /// Gets the parsed CPU key used for original-CB_B verification and decryption.
    /// </summary>
    public CpuKey CpuKey { get; }

    /// <summary>
    /// Gets whether the template SMC replaces the original SMC.
    /// </summary>
    public bool PatchSmc { get; }
}

/// <summary>
/// Safe metadata returned after a completed RGH2-to-RGH3 conversion.
/// </summary>
public sealed record Rgh2ToRgh3ConversionResult
{
    /// <summary>
    /// Creates completed conversion metadata.
    /// </summary>
    public Rgh2ToRgh3ConversionResult(
        NandPhysicalFormat rgh3EccFormat,
        NandPhysicalFormat flashFormat,
        NandLegacyLayout? flashLayout,
        long outputByteLength,
        long rewrittenPrefixByteLength,
        bool smcPatched,
        bool rgh3PayloadPatched)
    {
        if (rgh3EccFormat is not NandPhysicalFormat.Logical and not NandPhysicalFormat.InterleavedEcc)
        {
            throw new ArgumentOutOfRangeException(nameof(rgh3EccFormat), rgh3EccFormat, "The RGH3 ECC format is not supported.");
        }

        if (flashFormat is not NandPhysicalFormat.Logical and not NandPhysicalFormat.InterleavedEcc)
        {
            throw new ArgumentOutOfRangeException(nameof(flashFormat), flashFormat, "The flash format is not supported.");
        }

        if ((flashFormat == NandPhysicalFormat.InterleavedEcc) != flashLayout.HasValue)
        {
            throw new ArgumentException("A physical flash result must have exactly one spare layout.", nameof(flashLayout));
        }

        if (flashLayout is { } layout && !Enum.IsDefined(layout))
        {
            throw new ArgumentOutOfRangeException(nameof(flashLayout), layout, "The NAND spare layout is not supported.");
        }

        if (outputByteLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputByteLength), outputByteLength, "The output length must be positive.");
        }

        if (rewrittenPrefixByteLength <= 0 || rewrittenPrefixByteLength > outputByteLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rewrittenPrefixByteLength),
                rewrittenPrefixByteLength,
                "The rewritten prefix must be positive and within the output.");
        }

        Rgh3EccFormat = rgh3EccFormat;
        FlashFormat = flashFormat;
        FlashLayout = flashLayout;
        OutputByteLength = outputByteLength;
        RewrittenPrefixByteLength = rewrittenPrefixByteLength;
        SmcPatched = smcPatched;
        Rgh3PayloadPatched = rgh3PayloadPatched;
    }

    /// <summary>
    /// Gets whether the RGH3 template was logical or interleaved ECC.
    /// </summary>
    public NandPhysicalFormat Rgh3EccFormat { get; }

    /// <summary>
    /// Gets whether the converted flash is logical eMMC or interleaved ECC.
    /// </summary>
    public NandPhysicalFormat FlashFormat { get; }

    /// <summary>
    /// Gets the preserved physical spare layout, or <see langword="null"/> for logical eMMC.
    /// </summary>
    public NandLegacyLayout? FlashLayout { get; }

    /// <summary>
    /// Gets the number of output bytes written from the flash input's current position.
    /// </summary>
    public long OutputByteLength { get; }

    /// <summary>
    /// Gets the number of leading flash bytes regenerated by conversion.
    /// </summary>
    public long RewrittenPrefixByteLength { get; }

    /// <summary>
    /// Gets whether the template SMC replaced the source SMC.
    /// </summary>
    public bool SmcPatched { get; }

    /// <summary>
    /// Gets whether the optional legacy RGH3 payload instruction patch was applied.
    /// </summary>
    public bool Rgh3PayloadPatched { get; }
}

/// <summary>
/// Converts supported legacy RGH2 flash images to RGH3 using a supplied RGH3 ECC template.
/// </summary>
/// <remarks>
/// This is intentionally a bounded legacy conversion: it accepts only the historical 16 MB/64 MB
/// spare-bearing flash sizes and the 48 MB logical eMMC size. Source streams remain open and return
/// to their original positions. The output stream remains open and is advanced on success; callers
/// that require all-or-nothing filesystem publication should use an atomic temporary output.
/// </remarks>
public static class Rgh2ToRgh3ConversionService
{
    private const int LogicalEccTemplateLength = 0x140000;
    private const int PhysicalEccTemplateLength = 0x14A000;
    private const int Physical16MbFlashLength = 0x1080000;
    private const int Physical64MbFlashLength = 0x4200000;
    private const int Logical48MbFlashLength = 0x3000000;
    private const int LogicalInitialPatchLength = 0x70000;
    private const int PhysicalInitialPatchLength = 0x73800;
    private const int MaximumLogicalPatchLength = 0x200000;
    private const int XellSignatureLength = 0x10;
    private const int BootloaderHeaderLength = 0x10;
    private const int BootloaderCryptoHeaderLength = 0x20;
    private const int CbbValidationOffset = 0x392;
    private const int CbbValidationLength = 0x08;
    private const int Rgh3PatchProbeOffset = 0x354;
    private const int Rgh3PatchFinalOffset = 0x37C;
    private const int CopyBufferLength = 0x10000;

    /// <summary>
    /// Converts the supplied RGH2 flash to RGH3.
    /// </summary>
    /// <param name="request">Validated streams, CPU key, and SMC option.</param>
    /// <param name="progress">Optional phase progress that never includes secret material.</param>
    /// <param name="cancellationToken">Cancels input reads, cryptography, ECC work, and output writes.</param>
    /// <returns>Safe metadata describing the completed conversion.</returns>
    /// <exception cref="OperationFailureException">The template, source flash, CPU key, or bootloader chain is unsupported or invalid.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static async Task<Rgh2ToRgh3ConversionResult> ConvertAsync(
        Rgh2ToRgh3ConversionRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureInitializedCpuKey(request.CpuKey);

        long rgh3Start = GetCurrentPosition(request.Rgh3Ecc, nameof(request.Rgh3Ecc));
        long flashStart = GetCurrentPosition(request.Rgh2Flash, nameof(request.Rgh2Flash));
        long rgh3Length = GetRemainingLength(request.Rgh3Ecc, rgh3Start, nameof(request.Rgh3Ecc));
        long flashLength = GetRemainingLength(request.Rgh2Flash, flashStart, nameof(request.Rgh2Flash));

        byte[]? rgh3Raw = null;
        byte[]? rgh3Logical = null;
        byte[]? flashRawPrefix = null;
        byte[]? flashLogicalPrefix = null;
        byte[]? convertedLogicalPrefix = null;
        byte[]? decodedRgh3Cba = null;
        byte[]? decodedFlashCba = null;
        byte[]? decodedFlashCbb = null;
        byte[]? xellSignature = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, "converting-rgh2-to-rgh3", "Validating RGH conversion inputs.", completed: 0, total: flashLength);

            NandPhysicalFormat rgh3Format = GetRgh3TemplateFormat(rgh3Length);
            FlashShape flashShape = GetFlashShape(flashLength);
            rgh3Raw = await ReadRangeAsync(request.Rgh3Ecc, rgh3Start, 0, checked((int)rgh3Length), cancellationToken, "RGH3 ECC template")
                .ConfigureAwait(false);
            rgh3Logical = DecodeRgh3Template(rgh3Raw, rgh3Format, cancellationToken);
            if (!ReferenceEquals(rgh3Raw, rgh3Logical))
            {
                ZeroMemory(rgh3Raw);
                rgh3Raw = null;
            }

            TemplateParts template = ParseTemplate(rgh3Logical);
            PatchInput patchInput = await ReadInitialPatchInputAsync(
                request.Rgh2Flash,
                flashStart,
                flashShape,
                cancellationToken).ConfigureAwait(false);
            flashRawPrefix = patchInput.RawPrefix;
            flashLogicalPrefix = patchInput.LogicalPrefix;
            NandPhysicalLayout? flashLayout = patchInput.Layout;
            xellSignature = await ReadRangeAsync(
                request.Rgh2Flash,
                flashStart,
                patchInput.XellOffset,
                XellSignatureLength,
                cancellationToken,
                "XeLL signature").ConfigureAwait(false);

            StageRange sourceCba = ReadCbStage(
                flashLogicalPrefix,
                ReadFirstStageOffset(flashLogicalPrefix, "invalid-rgh2-flash-bootloaders"),
                "invalid-rgh2-flash-bootloaders",
                "RGH2 CB_A");
            StageRange sourceCbb = ReadCbOrSbStage(
                flashLogicalPrefix,
                sourceCba.EndOffset,
                "invalid-rgh2-flash-bootloaders",
                "RGH2 second-stage bootloader");

            if (!HasXellSignature(xellSignature))
            {
                int expandedLogicalLength = GetXellLessPatchLength(
                    flashLogicalPrefix,
                    sourceCbb.EndOffset,
                    "invalid-rgh2-flash-bootloaders");
                PatchInput expandedPatchInput = await ReadPatchInputAsync(
                    request.Rgh2Flash,
                    flashStart,
                    flashShape,
                    expandedLogicalLength,
                    cancellationToken).ConfigureAwait(false);
                ZeroMemory(flashRawPrefix);
                ZeroMemory(flashLogicalPrefix);
                flashRawPrefix = expandedPatchInput.RawPrefix;
                flashLogicalPrefix = expandedPatchInput.LogicalPrefix;
                sourceCba = ReadCbStage(
                    flashLogicalPrefix,
                    ReadFirstStageOffset(flashLogicalPrefix, "invalid-rgh2-flash-bootloaders"),
                    "invalid-rgh2-flash-bootloaders",
                    "RGH2 CB_A");
                sourceCbb = ReadCbOrSbStage(
                    flashLogicalPrefix,
                    sourceCba.EndOffset,
                    "invalid-rgh2-flash-bootloaders",
                    "RGH2 second-stage bootloader");
            }

            if (request.PatchSmc)
            {
                EnsureSmcTargetRange(flashLogicalPrefix, template, sourceCba, sourceCbb);
            }

            decodedRgh3Cba = DecryptCbA(
                rgh3Logical.AsSpan(template.Cba.Offset, template.Cba.Length),
                "invalid-rgh3-ecc-bootloaders",
                cancellationToken);
            bool rgh3PayloadPatched = PatchRgh3Payload(
                rgh3Logical.AsSpan(template.Payload.Offset, template.Payload.Length),
                decodedRgh3Cba,
                cancellationToken);

            decodedFlashCba = DecryptCbA(
                flashLogicalPrefix.AsSpan(sourceCba.Offset, sourceCba.Length),
                "invalid-rgh2-flash-bootloaders",
                cancellationToken);
            decodedFlashCbb = DecryptSourceCbB(
                flashLogicalPrefix.AsSpan(sourceCbb.Offset, sourceCbb.Length),
                decodedFlashCba,
                request.CpuKey,
                "flash-cbb-decryption-failed",
                cancellationToken);
            if (!HasExpectedCbbSignature(decodedFlashCbb))
            {
                throw Failure(
                    "flash-cbb-decryption-failed",
                    "The supplied CPU key does not decrypt the source CB_B into an expected bootloader image.");
            }

            if (request.PatchSmc)
            {
                rgh3Logical.AsSpan(template.SmcOffset, template.SmcLength)
                    .CopyTo(flashLogicalPrefix.AsSpan(template.SmcOffset, template.SmcLength));
            }

            convertedLogicalPrefix = BuildConvertedPrefix(
                flashLogicalPrefix,
                sourceCba,
                sourceCbb,
                rgh3Logical.AsSpan(template.Cba.Offset, template.Cba.Length),
                rgh3Logical.AsSpan(template.Payload.Offset, template.Payload.Length),
                decodedFlashCbb);

            byte[]? outputPrefix = null;
            try
            {
                if (flashShape.Format == NandPhysicalFormat.InterleavedEcc)
                {
                    byte[] paddedLogicalPrefix = PadToLogicalPageBoundary(convertedLogicalPrefix);
                    if (!ReferenceEquals(paddedLogicalPrefix, convertedLogicalPrefix))
                    {
                        ZeroMemory(convertedLogicalPrefix);
                        convertedLogicalPrefix = paddedLogicalPrefix;
                    }

                    outputPrefix = GC.AllocateUninitializedArray<byte>(
                        NandEccCodec.GetPhysicalLengthForLogicalLength(convertedLogicalPrefix.Length));
                    NandEccCodec.AddEcc(
                        convertedLogicalPrefix,
                        outputPrefix,
                        flashLayout!,
                        cancellationToken: cancellationToken);
                }
                else
                {
                    outputPrefix = convertedLogicalPrefix;
                    convertedLogicalPrefix = null;
                }

                byte[] completedOutputPrefix = outputPrefix
                    ?? throw new InvalidOperationException("The conversion output prefix was not initialized.");
                int rewrittenPrefixLength = completedOutputPrefix.Length;
                await request.Output.WriteAsync(completedOutputPrefix, cancellationToken).ConfigureAwait(false);
                await CopyRemainingAsync(
                    request.Rgh2Flash,
                    flashStart,
                    rewrittenPrefixLength,
                    flashLength,
                    request.Output,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                await request.Output.FlushAsync(cancellationToken).ConfigureAwait(false);

                Report(progress, "completed-rgh2-to-rgh3-conversion", "Completed RGH2-to-RGH3 conversion.", flashLength, flashLength);
                return new Rgh2ToRgh3ConversionResult(
                    rgh3Format,
                    flashShape.Format,
                    flashLayout?.LegacyLayout,
                    flashLength,
                    rewrittenPrefixLength,
                    request.PatchSmc,
                    rgh3PayloadPatched);
            }
            finally
            {
                ZeroMemory(outputPrefix);
            }
        }
        finally
        {
            ZeroMemory(rgh3Raw);
            ZeroMemory(rgh3Logical);
            ZeroMemory(flashRawPrefix);
            ZeroMemory(flashLogicalPrefix);
            ZeroMemory(convertedLogicalPrefix);
            ZeroMemory(decodedRgh3Cba);
            ZeroMemory(decodedFlashCba);
            ZeroMemory(decodedFlashCbb);
            ZeroMemory(xellSignature);
            RestorePosition(request.Rgh3Ecc, rgh3Start);
            RestorePosition(request.Rgh2Flash, flashStart);
        }
    }

    private static NandPhysicalFormat GetRgh3TemplateFormat(long length) => length switch
    {
        LogicalEccTemplateLength => NandPhysicalFormat.Logical,
        PhysicalEccTemplateLength => NandPhysicalFormat.InterleavedEcc,
        _ => throw Failure(
            "invalid-rgh3-ecc-size",
            "The RGH3 template must be exactly 0x140000 logical bytes or 0x14A000 interleaved-ECC bytes."),
    };

    private static FlashShape GetFlashShape(long length) => length switch
    {
        Physical16MbFlashLength => new FlashShape(
            NandPhysicalFormat.InterleavedEcc,
            PhysicalInitialPatchLength,
            LogicalInitialPatchLength,
            Layout: null),
        Physical64MbFlashLength => new FlashShape(
            NandPhysicalFormat.InterleavedEcc,
            PhysicalInitialPatchLength,
            LogicalInitialPatchLength,
            Layout: null),
        Logical48MbFlashLength => new FlashShape(
            NandPhysicalFormat.Logical,
            LogicalInitialPatchLength,
            LogicalInitialPatchLength,
            Layout: null),
        _ => throw Failure(
            "invalid-rgh2-flash-length",
            "The RGH2 flash must be 16 MB or 64 MB interleaved ECC data, or 48 MB logical eMMC data."),
    };

    private static byte[] DecodeRgh3Template(
        byte[] rgh3Raw,
        NandPhysicalFormat format,
        CancellationToken cancellationToken)
    {
        if (format == NandPhysicalFormat.Logical)
        {
            return rgh3Raw;
        }

        byte[] logical = GC.AllocateUninitializedArray<byte>(LogicalEccTemplateLength);
        try
        {
            NandEccCodec.RemoveEcc(rgh3Raw, logical, cancellationToken: cancellationToken);
            return logical;
        }
        catch
        {
            ZeroMemory(logical);
            throw;
        }
    }

    private static TemplateParts ParseTemplate(ReadOnlySpan<byte> template)
    {
        EnsureRange(template, 0, 0x80, "invalid-rgh3-ecc-bootloaders", "The RGH3 template header is truncated.");
        int smcLength = ReadLength(template, 0x78, "invalid-rgh3-ecc-bootloaders", "The RGH3 SMC length is invalid.");
        int smcOffset = ReadOffset(template, 0x7C, "invalid-rgh3-ecc-bootloaders", "The RGH3 SMC offset is invalid.");
        EnsureRange(
            template,
            smcOffset,
            smcLength,
            "invalid-rgh3-ecc-bootloaders",
            "The RGH3 template SMC range is truncated.");

        int firstStageOffset = ReadFirstStageOffset(template, "invalid-rgh3-ecc-bootloaders");
        StageRange cba = ReadCbStage(template, firstStageOffset, "invalid-rgh3-ecc-bootloaders", "RGH3 CB_A");
        StageRange payload = ReadCbStage(template, cba.EndOffset, "invalid-rgh3-ecc-bootloaders", "RGH3 payload");
        if (payload.Length < Rgh3PatchFinalOffset + sizeof(uint))
        {
            throw Failure(
                "invalid-rgh3-ecc-bootloaders",
                "The RGH3 payload is too short for its bootloader patch area.");
        }

        if (RangesOverlap(smcOffset, smcLength, cba) || RangesOverlap(smcOffset, smcLength, payload))
        {
            throw Failure(
                "invalid-rgh3-ecc-bootloaders",
                "The RGH3 template SMC range overlaps a bootloader stage.");
        }

        return new TemplateParts(smcOffset, smcLength, cba, payload);
    }

    private static async Task<PatchInput> ReadInitialPatchInputAsync(
        Stream flash,
        long flashStart,
        FlashShape shape,
        CancellationToken cancellationToken)
    {
        PatchInput patchInput = await ReadPatchInputAsync(
            flash,
            flashStart,
            shape,
            shape.LogicalPatchLength,
            cancellationToken).ConfigureAwait(false);
        if (shape.Format != NandPhysicalFormat.InterleavedEcc)
        {
            return patchInput;
        }

        try
        {
            NandPhysicalLayout layout = DetectLegacyLayout(patchInput.RawPrefix);
            return patchInput with { Layout = layout };
        }
        catch
        {
            ZeroMemory(patchInput.RawPrefix);
            ZeroMemory(patchInput.LogicalPrefix);
            throw;
        }
    }

    private static async Task<PatchInput> ReadPatchInputAsync(
        Stream flash,
        long flashStart,
        FlashShape shape,
        int logicalPatchLength,
        CancellationToken cancellationToken)
    {
        if (logicalPatchLength <= 0 || logicalPatchLength > MaximumLogicalPatchLength)
        {
            throw Failure(
                "invalid-rgh2-flash-bootloaders",
                "The source bootloader chain requires an unsupported patch range.");
        }

        int rawPatchLength = shape.Format == NandPhysicalFormat.InterleavedEcc
            ? NandEccCodec.GetPhysicalLengthForLogicalLength(logicalPatchLength)
            : logicalPatchLength;
        byte[] rawPrefix = await ReadRangeAsync(
            flash,
            flashStart,
            0,
            rawPatchLength,
            cancellationToken,
            "RGH2 flash prefix").ConfigureAwait(false);
        if (shape.Format == NandPhysicalFormat.Logical)
        {
            return new PatchInput(rawPrefix, rawPrefix, rawPatchLength, shape.XellOffset, Layout: null);
        }

        byte[] logicalPrefix = GC.AllocateUninitializedArray<byte>(logicalPatchLength);
        try
        {
            NandEccCodec.RemoveEcc(rawPrefix, logicalPrefix, cancellationToken: cancellationToken);
            return new PatchInput(rawPrefix, logicalPrefix, rawPatchLength, shape.XellOffset, Layout: null);
        }
        catch
        {
            ZeroMemory(rawPrefix);
            ZeroMemory(logicalPrefix);
            throw;
        }
    }

    private static NandPhysicalLayout DetectLegacyLayout(ReadOnlySpan<byte> physicalPrefix)
    {
        const int spareSampleOffset = 0x4400;
        EnsureRange(
            physicalPrefix,
            spareSampleOffset,
            NandPhysicalGeometry.SpareSize,
            "invalid-rgh2-flash-layout",
            "The RGH2 flash does not contain the required spare-area sample.");
        ReadOnlySpan<byte> spare = physicalPrefix.Slice(spareSampleOffset, NandPhysicalGeometry.SpareSize);
        if (spare[0] == byte.MaxValue)
        {
            return NandPhysicalLayout.Layout2;
        }

        if (spare[5] == byte.MaxValue)
        {
            if (spare[0] == 0x01 && spare[1] == 0x00)
            {
                return NandPhysicalLayout.Layout0;
            }

            if (spare[0] == 0x00 && spare[1] == 0x01)
            {
                return NandPhysicalLayout.Layout1;
            }
        }

        throw Failure(
            "invalid-rgh2-flash-layout",
            "The RGH2 flash spare-area layout is not one of the supported legacy layouts.");
    }

    private static int GetXellLessPatchLength(
        ReadOnlySpan<byte> source,
        int offset,
        string failureKind)
    {
        StageRange thirdStage = ReadStage(source, offset, failureKind, "post-CB_B stage");
        int chainEnd;
        if (thirdStage.Magic == 0x5343) // SC
        {
            StageRange sd = ReadStage(source, thirdStage.EndOffset, failureKind, "XDK SD stage");
            EnsureMagic(sd, 0x5344, failureKind, "XDK SD stage");
            StageRange se = ReadStage(source, sd.EndOffset, failureKind, "XDK SE stage");
            EnsureMagic(se, 0x5345, failureKind, "XDK SE stage");
            chainEnd = se.EndOffset;
        }
        else if (thirdStage.Magic == 0x4344) // CD
        {
            StageRange se = ReadStage(source, thirdStage.EndOffset, failureKind, "RGL SE stage");
            EnsureMagic(se, 0x5345, failureKind, "RGL SE stage");
            chainEnd = se.EndOffset;
        }
        else
        {
            throw Failure(
                "xell-not-found",
                "XeLL is absent and the source bootloader chain is not a supported XDK or RGL chain.");
        }

        long roundedPages = (chainEnd + NandPhysicalGeometry.LogicalPageSize - 1L) /
            NandPhysicalGeometry.LogicalPageSize;
        long requiredLength = checked((roundedPages + 4) * NandPhysicalGeometry.LogicalPageSize);
        if (requiredLength > MaximumLogicalPatchLength)
        {
            throw Failure(
                "invalid-rgh2-flash-bootloaders",
                "The source bootloader chain requires an unsupported patch range.");
        }

        return checked((int)requiredLength);
    }

    private static StageRange ReadCbStage(
        ReadOnlySpan<byte> data,
        int offset,
        string failureKind,
        string description)
    {
        StageRange stage = ReadStage(data, offset, failureKind, description);
        EnsureMagic(stage, 0x4342, failureKind, description);
        if (stage.Length < BootloaderCryptoHeaderLength)
        {
            throw Failure(failureKind, $"The {description} bootloader is shorter than 0x20 bytes.");
        }

        return stage;
    }

    private static StageRange ReadCbOrSbStage(
        ReadOnlySpan<byte> data,
        int offset,
        string failureKind,
        string description)
    {
        StageRange stage = ReadStage(data, offset, failureKind, description);
        if (stage.Magic is not 0x4342 and not 0x5342) // CB or SB
        {
            throw Failure(failureKind, $"The {description} does not have the expected bootloader magic.");
        }

        if (stage.Length < BootloaderCryptoHeaderLength)
        {
            throw Failure(failureKind, $"The {description} bootloader is shorter than 0x20 bytes.");
        }

        return stage;
    }

    private static StageRange ReadStage(
        ReadOnlySpan<byte> data,
        int offset,
        string failureKind,
        string description)
    {
        EnsureRange(data, offset, BootloaderHeaderLength, failureKind, $"The {description} header is truncated.");
        int length = ReadLength(data, offset + 0x0C, failureKind, $"The {description} length is invalid.");
        if (length < BootloaderHeaderLength)
        {
            throw Failure(failureKind, $"The {description} length is smaller than its header.");
        }

        EnsureRange(data, offset, length, failureKind, $"The {description} extends beyond the available NAND prefix.");
        ushort magic = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, sizeof(ushort)));
        return new StageRange(offset, length, magic);
    }

    private static int ReadFirstStageOffset(ReadOnlySpan<byte> data, string failureKind)
    {
        EnsureRange(data, 0x08, sizeof(uint), failureKind, "The NAND header does not contain a bootloader offset.");
        return ReadOffset(data, 0x08, failureKind, "The NAND bootloader offset is invalid.");
    }

    private static int ReadOffset(ReadOnlySpan<byte> data, int offset, string failureKind, string message)
    {
        uint value = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, sizeof(uint)));
        if (value > int.MaxValue)
        {
            throw Failure(failureKind, message);
        }

        return checked((int)value);
    }

    private static int ReadLength(ReadOnlySpan<byte> data, int offset, string failureKind, string message)
    {
        uint value = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, sizeof(uint)));
        if (value > int.MaxValue)
        {
            throw Failure(failureKind, message);
        }

        return checked((int)value);
    }

    private static void EnsureMagic(StageRange stage, ushort expectedMagic, string failureKind, string description)
    {
        if (stage.Magic != expectedMagic)
        {
            throw Failure(failureKind, $"The {description} does not have the expected bootloader magic.");
        }
    }

    private static void EnsureRange(
        ReadOnlySpan<byte> data,
        int offset,
        int length,
        string failureKind,
        string message)
    {
        if (offset < 0 || length < 0 || offset > data.Length || length > data.Length - offset)
        {
            throw Failure(failureKind, message);
        }
    }

    private static void EnsureSmcTargetRange(
        ReadOnlySpan<byte> flashPrefix,
        TemplateParts template,
        StageRange sourceCba,
        StageRange sourceCbb)
    {
        EnsureRange(
            flashPrefix,
            template.SmcOffset,
            template.SmcLength,
            "invalid-rgh3-ecc-bootloaders",
            "The RGH3 template SMC range does not fit the source bootloader prefix.");
        if (RangesOverlap(template.SmcOffset, template.SmcLength, sourceCba) ||
            RangesOverlap(template.SmcOffset, template.SmcLength, sourceCbb))
        {
            throw Failure(
                "invalid-rgh3-ecc-bootloaders",
                "The RGH3 template SMC range overlaps a source bootloader stage.");
        }
    }

    private static bool RangesOverlap(int offset, int length, StageRange stage) =>
        offset < (long)stage.Offset + stage.Length && stage.Offset < (long)offset + length;

    private static byte[] DecryptCbA(
        ReadOnlySpan<byte> encryptedCba,
        string failureKind,
        CancellationToken cancellationToken)
    {
        try
        {
            BootloaderDecryptionResult result = BootloaderCrypto.DecryptCb(encryptedCba, cancellationToken);
            return TakeOwnedBuffer(result.Output);
        }
        catch (OperationFailureException)
        {
            throw Failure(failureKind, "A CB_A bootloader could not be decrypted.");
        }
    }

    private static bool PatchRgh3Payload(
        Span<byte> encryptedPayload,
        ReadOnlySpan<byte> decodedCba,
        CancellationToken cancellationToken)
    {
        Span<byte> zeroCpuKey = stackalloc byte[CpuKey.ByteLength];
        zeroCpuKey.Clear();
        byte[]? decodedPayload = null;
        byte[]? patchedPayload = null;
        try
        {
            decodedPayload = DecryptLegacyRgh3Payload(
                encryptedPayload,
                decodedCba,
                zeroCpuKey,
                "invalid-rgh3-ecc-bootloaders",
                cancellationToken);
            if (BinaryPrimitives.ReadUInt32BigEndian(decodedPayload.AsSpan(Rgh3PatchProbeOffset, sizeof(uint))) != 0x646A0002)
            {
                return false;
            }

            BinaryPrimitives.WriteUInt32BigEndian(decodedPayload.AsSpan(Rgh3PatchProbeOffset, sizeof(uint)), 0x64690002);
            BinaryPrimitives.WriteUInt32BigEndian(decodedPayload.AsSpan(0x368, sizeof(uint)), 0x7D8C482A);
            BinaryPrimitives.WriteUInt32BigEndian(decodedPayload.AsSpan(0x370, sizeof(uint)), 0x64690006);
            BinaryPrimitives.WriteUInt32BigEndian(decodedPayload.AsSpan(Rgh3PatchFinalOffset, sizeof(uint)), 0xF8491010);
            patchedPayload = EncryptRgh3Payload(decodedPayload, cancellationToken);
            patchedPayload.CopyTo(encryptedPayload);
            return true;
        }
        finally
        {
            ZeroMemory(decodedPayload);
            ZeroMemory(patchedPayload);
            CryptographicOperations.ZeroMemory(zeroCpuKey);
        }
    }

    private static byte[] DecryptSourceCbB(
        ReadOnlySpan<byte> encryptedCbb,
        ReadOnlySpan<byte> decodedCba,
        CpuKey cpuKey,
        string failureKind,
        CancellationToken cancellationToken)
    {
        try
        {
            BootloaderDecryptionResult result = BootloaderCrypto.DecryptCbWithCpuKey(
                encryptedCbb,
                decodedCba,
                cpuKey,
                cancellationToken);
            return TakeOwnedBuffer(result.Output);
        }
        catch (OperationFailureException)
        {
            throw Failure(failureKind, "A CB_B bootloader could not be decrypted.");
        }
    }

    internal static byte[] DecryptLegacyRgh3Payload(
        ReadOnlySpan<byte> encryptedPayload,
        ReadOnlySpan<byte> decodedCba,
        ReadOnlySpan<byte> cpuKey,
        string failureKind,
        CancellationToken cancellationToken)
    {
        if (encryptedPayload.Length < BootloaderCryptoHeaderLength ||
            decodedCba.Length < BootloaderCryptoHeaderLength ||
            cpuKey.Length != CpuKey.ByteLength)
        {
            throw Failure(failureKind, "The RGH3 payload is too short for decryption.");
        }

        Span<byte> message = stackalloc byte[CpuKey.ByteLength * 2];
        Span<byte> rc4Key = stackalloc byte[XeCrypt.HmacSha1TagLength];
        byte[]? decoded = null;
        try
        {
            message.Clear();
            encryptedPayload.Slice(0x10, XeCrypt.HmacSha1TagLength).CopyTo(message);
            cpuKey.CopyTo(message.Slice(CpuKey.ByteLength, CpuKey.ByteLength));
            XeCrypt.HmacSha1Truncated(
                decodedCba.Slice(0x10, XeCrypt.HmacSha1TagLength),
                message,
                rc4Key);

            decoded = GC.AllocateUninitializedArray<byte>(encryptedPayload.Length);
            encryptedPayload.Slice(0, 0x10).CopyTo(decoded);
            rc4Key.CopyTo(decoded.AsSpan(0x10, XeCrypt.HmacSha1TagLength));
            encryptedPayload.Slice(BootloaderCryptoHeaderLength).CopyTo(decoded.AsSpan(BootloaderCryptoHeaderLength));
            Rc4.TransformInPlace(rc4Key, decoded.AsSpan(BootloaderCryptoHeaderLength), cancellationToken);
            byte[] result = decoded;
            decoded = null;
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(message);
            CryptographicOperations.ZeroMemory(rc4Key);
            ZeroMemory(decoded);
        }
    }

    private static byte[] EncryptRgh3Payload(ReadOnlySpan<byte> decodedPayload, CancellationToken cancellationToken)
    {
        if (decodedPayload.Length < BootloaderCryptoHeaderLength)
        {
            throw Failure("invalid-rgh3-ecc-bootloaders", "The RGH3 payload is too short for encryption.");
        }

        byte[] encrypted = GC.AllocateUninitializedArray<byte>(decodedPayload.Length);
        try
        {
            decodedPayload.Slice(0, 0x10).CopyTo(encrypted);
            encrypted.AsSpan(0x10, XeCrypt.HmacSha1TagLength).Clear();
            decodedPayload.Slice(BootloaderCryptoHeaderLength).CopyTo(encrypted.AsSpan(BootloaderCryptoHeaderLength));
            Rc4.TransformInPlace(
                decodedPayload.Slice(0x10, XeCrypt.HmacSha1TagLength),
                encrypted.AsSpan(BootloaderCryptoHeaderLength),
                cancellationToken);
            return encrypted;
        }
        catch
        {
            ZeroMemory(encrypted);
            throw;
        }
    }

    private static bool HasExpectedCbbSignature(ReadOnlySpan<byte> decodedCbb)
    {
        if (decodedCbb.Length < CbbValidationOffset + CbbValidationLength)
        {
            return false;
        }

        ReadOnlySpan<byte> signature = decodedCbb.Slice(CbbValidationOffset, CbbValidationLength);
        return signature.SequenceEqual("XBOX_ROM"u8) || IsAllZero(signature);
    }

    private static byte[] BuildConvertedPrefix(
        ReadOnlySpan<byte> sourcePrefix,
        StageRange sourceCba,
        StageRange sourceCbb,
        ReadOnlySpan<byte> rgh3Cba,
        ReadOnlySpan<byte> rgh3Payload,
        ReadOnlySpan<byte> decodedFlashCbb)
    {
        int replacementLength = checked(rgh3Cba.Length + rgh3Payload.Length + decodedFlashCbb.Length);
        int tailLength = sourcePrefix.Length - sourceCbb.EndOffset;
        int assembledLength = checked(sourceCba.Offset + replacementLength + tailLength);
        int outputLength = Math.Min(sourcePrefix.Length, assembledLength);
        var converted = new byte[outputLength];
        try
        {
            sourcePrefix.Slice(0, sourceCba.Offset).CopyTo(converted);
            int destinationOffset = sourceCba.Offset;
            CopyTruncated(rgh3Cba, converted, ref destinationOffset);
            CopyTruncated(rgh3Payload, converted, ref destinationOffset);
            CopyTruncated(decodedFlashCbb, converted, ref destinationOffset);
            if (destinationOffset < converted.Length)
            {
                CopyTruncated(sourcePrefix.Slice(sourceCbb.EndOffset), converted, ref destinationOffset);
            }

            return converted;
        }
        catch
        {
            ZeroMemory(converted);
            throw;
        }
    }


    private static byte[] PadToLogicalPageBoundary(byte[] logicalPrefix)
    {
        int remainder = logicalPrefix.Length % NandPhysicalGeometry.LogicalPageSize;
        if (remainder == 0)
        {
            return logicalPrefix;
        }

        var padded = new byte[checked(logicalPrefix.Length + NandPhysicalGeometry.LogicalPageSize - remainder)];
        logicalPrefix.CopyTo(padded, 0);
        return padded;
    }

    private static void CopyTruncated(ReadOnlySpan<byte> source, Span<byte> destination, ref int destinationOffset)
    {
        int copyLength = Math.Min(source.Length, destination.Length - destinationOffset);
        source.Slice(0, copyLength).CopyTo(destination.Slice(destinationOffset, copyLength));
        destinationOffset += copyLength;
    }

    private static bool HasXellSignature(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length == XellSignatureLength &&
            BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(0, sizeof(ulong))) == 0x48000020480000ECUL &&
            BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(sizeof(ulong), sizeof(ulong))) == 0x4800000048000000UL;
    }

    private static bool IsAllZero(ReadOnlySpan<byte> bytes)
    {
        byte aggregate = 0;
        foreach (byte value in bytes)
        {
            aggregate |= value;
        }

        return aggregate == 0;
    }

    private static async Task<byte[]> ReadRangeAsync(
        Stream stream,
        long start,
        long relativeOffset,
        int length,
        CancellationToken cancellationToken,
        string description)
    {
        var bytes = GC.AllocateUninitializedArray<byte>(length);
        try
        {
            stream.Position = checked(start + relativeOffset);
            int copied = 0;
            while (copied < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = await stream.ReadAsync(bytes.AsMemory(copied), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw Failure("truncated-rgh-conversion-input", $"The {description} ended before its declared length.");
                }

                copied += read;
            }

            return bytes;
        }
        catch
        {
            ZeroMemory(bytes);
            throw;
        }
    }

    private static async Task CopyRemainingAsync(
        Stream source,
        long sourceStart,
        long prefixLength,
        long sourceLength,
        Stream destination,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        source.Position = checked(sourceStart + prefixLength);
        long remaining = sourceLength - prefixLength;
        long completed = prefixLength;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferLength);
        try
        {
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int wanted = (int)Math.Min(remaining, buffer.Length);
                int read = await source.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw Failure("truncated-rgh-conversion-input", "The RGH2 flash ended while the unchanged tail was being copied.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                remaining -= read;
                completed += read;
                if (remaining == 0 || completed % (1024 * 1024) < read)
                {
                    Report(progress, "writing-rgh2-to-rgh3-output", "Writing converted RGH3 flash bytes.", completed, sourceLength);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static long GetCurrentPosition(Stream stream, string parameterName)
    {
        try
        {
            return stream.Position;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or ObjectDisposedException)
        {
            throw new ArgumentException("The RGH conversion stream must provide a stable position.", parameterName, exception);
        }
    }

    private static long GetRemainingLength(Stream stream, long position, string parameterName)
    {
        try
        {
            long length = stream.Length;
            if (position < 0 || length < position)
            {
                throw new ArgumentException("The RGH conversion stream has an invalid position or length.", parameterName);
            }

            return length - position;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or ObjectDisposedException)
        {
            throw new ArgumentException("The RGH conversion stream must provide a stable length.", parameterName, exception);
        }
    }

    private static void RestorePosition(Stream stream, long position)
    {
        try
        {
            stream.Position = position;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or ObjectDisposedException)
        {
            _ = exception;
        }
    }

    private static byte[] TakeOwnedBuffer(ReadOnlyMemory<byte> memory)
    {
        if (MemoryMarshal.TryGetArray(memory, out ArraySegment<byte> segment) &&
            segment.Array is { } array &&
            segment.Offset == 0 &&
            segment.Count == array.Length)
        {
            return array;
        }

        return memory.ToArray();
    }

    private static void EnsureInitializedCpuKey(CpuKey cpuKey)
    {
        if (!cpuKey.IsInitialized)
        {
            throw Failure("invalid-cpu-key", "A parsed CPU key is required for RGH2-to-RGH3 conversion.");
        }
    }

    private static void Report(
        IProgress<OperationProgress>? progress,
        string kind,
        string message,
        long completed,
        long total)
    {
        progress?.Report(new OperationProgress(kind, message, completed: completed, total: total));
    }

    private static void ZeroMemory(byte[]? bytes)
    {
        if (bytes is not null)
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static OperationFailureException Failure(string kind, string message) =>
        new(ExitCode.InvalidData, kind, message);

    private readonly record struct StageRange(int Offset, int Length, ushort Magic)
    {
        internal int EndOffset => checked(Offset + Length);
    }

    private readonly record struct TemplateParts(int SmcOffset, int SmcLength, StageRange Cba, StageRange Payload);

    private readonly record struct FlashShape(
        NandPhysicalFormat Format,
        int RawPatchLength,
        int LogicalPatchLength,
        NandPhysicalLayout? Layout)
    {
        internal int XellOffset => Format == NandPhysicalFormat.InterleavedEcc
            ? PhysicalInitialPatchLength
            : LogicalInitialPatchLength;
    }

    private readonly record struct PatchInput(
        byte[] RawPrefix,
        byte[] LogicalPrefix,
        int RawPatchLength,
        int XellOffset,
        NandPhysicalLayout? Layout);
}
