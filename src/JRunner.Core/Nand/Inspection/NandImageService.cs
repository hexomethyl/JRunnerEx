using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using JRunner.Core.Binary;
using JRunner.Core.Contracts;
using JRunner.Core.Nand.Comparison;
using JRunner.Core.Nand.Hacks;
using JRunner.Core.Nand.Inspection.Models;
using JRunner.Core.Nand.Models;
using JRunner.Core.Nand.Physical;
using JRunner.Core.Nand.Security;
using JRunner.Core.Patching.Inspection;

namespace JRunner.Core.Nand.Inspection;

/// <summary>
/// Inspects legacy logical or ECC-bearing NAND images through the canonical logical projection.
/// </summary>
/// <remarks>
/// This service returns only immutable, non-secret evidence. Caller-owned input streams remain open,
/// and the service never retains raw image, decrypted payload, keyvault, DVD-key, or CPU-key bytes.
/// Direct family matching is limited to the reviewed zero-key decrypted RGH3 CB_X output signature.
/// </remarks>
public static class NandImageService
{
    private const int HeaderReadLength = 0x80;
    private const int StageHeaderLength = 0x10;
    private const int MaximumMainBootloaderStages = 30;
    private const int CfHeaderLength = 0x354;
    private const int CgHeaderLength = 0x50;
    private const int UpdateSlotScanChunkLength = 0x1000;
    private const long KeyvaultLogicalOffset = 0x4000;
    private const int PatchSectionLength = 0x4000;
    private const long Layout2PatchLogicalOffset = 0xE0010;
    private const long OtherPatchLogicalOffset = 0xC0010;
    private const long LegacyPatchLogicalOffset = 0x913F0;
    private const uint S2Magic = 0x5332;
    private const uint CfMagic = 0x4346;
    private const uint CgMagic = 0x4347;
    private const int HackedSmcMarkerOffset = 0x2DB0;
    private const int HackedSmcMarkerLength = 0x10;
    private const int CbPairingDataOffset = 0x20;
    private const int CbPairingDataLength = 3;
    private const int CbBEncryptedMarkerFirstOffset = 0xA0;
    private const int CbBEncryptedMarkerSecondOffset = 0xA7;
    private const int CbBEncryptedMarkerThirdOffset = 0xAF;
    private const int CbLdvOffset = 0x3B1;
    private const int PatchSlotLogicalOffsetFieldOffset = 0x64;
    private const int PatchSlotCountFieldOffset = 0x68;
    private const int PatchSlotSizeFieldOffset = 0x70;
    private const int MaximumVirtualFusePatchSlots = 2;
    private const long DefaultVirtualFusePatchSlotSize = 0x10000;
    private const int VirtualFuseMarkerLength = 8;
    private const long LegacyJtagVirtualFuseLogicalOffset = 0x95000;
    private const long KnownEmmcLogicalImageLength = 0xE0400000;
    private const int MaximumSupportedSecondBootloaderStages = 3;
    private static readonly byte[] VirtualFuseLine0 =
    [
        0xC0, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
    ];
    // These binaries are measured in KiB on supported hardware. Caps keep malformed
    // in-range headers from turning stream inspection into unbounded allocation.
    private const int MaximumSmcLength = 0x100000;
    private const int MaximumBootloaderStageLength = 0x1000000;
    private const int MaximumMainBootloaderChainLength = 0x02000000;

    /// <summary>
    /// Canonicalizes and inspects a NAND image from its current stream position.
    /// </summary>
    /// <param name="image">The caller-owned readable image stream. It remains open.</param>
    /// <param name="cpuKey">An optional initialized CPU key used only for applicable keyvault and bootloader paths.</param>
    /// <param name="progress">Optional phase progress emitted without raw image or secret material.</param>
    /// <param name="cancellationToken">Cancels canonicalization, reads, checksums, and cryptographic work.</param>
    /// <returns>Safe, immutable NAND-inspection evidence.</returns>
    /// <exception cref="OperationFailureException">The NAND data, supplied key, or declared ranges are invalid.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static async Task<NandInspectionResult> InspectAsync(
        Stream image,
        CpuKey? cpuKey = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!image.CanRead)
        {
            throw new ArgumentException("The NAND image stream must be readable.", nameof(image));
        }

        EnsureInitializedCpuKey(cpuKey);
        cancellationToken.ThrowIfCancellationRequested();

        Report(progress, "canonicalizing-nand-image", "Preparing the canonical logical NAND image.", 0, 7);
        await using NandCanonicalImage canonicalImage = await NandCanonicalImagePreparer.PrepareAsync(
            new NandCanonicalInput(image),
            progress,
            cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        Report(progress, "inspecting-nand-header", "Validating the canonical NAND header.", 1, 7);
        ParsedNandHeader parsedHeader = await ReadHeaderAsync(canonicalImage, cancellationToken).ConfigureAwait(false);
        NandHeaderInspection header = parsedHeader.Inspection;

        cancellationToken.ThrowIfCancellationRequested();
        Report(progress, "inspecting-nand-smc", "Inspecting the encrypted SMC range.", 2, 7);
        NandSmcInspection smc = await InspectSmcAsync(canonicalImage, header.SmcLogicalRange, cancellationToken)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        Report(progress, "inspecting-nand-keyvault", "Inspecting the keyvault verification range.", 3, 7);
        NandKeyvaultInspection keyvault = await InspectKeyvaultAsync(canonicalImage, cpuKey, cancellationToken)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        Report(progress, "inspecting-nand-bootloaders", "Inspecting the main bootloader chain.", 4, 7);
        MainBootloaderInspection mainBootloaders = await InspectMainBootloadersAsync(
            canonicalImage,
            header.FirstStageLogicalOffset,
            cpuKey,
            cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        Report(progress, "inspecting-update-bootloaders", "Inspecting structurally complete update bootloader evidence.", 5, 7);
        UpdateBootloaderInspection updateBootloaders = await InspectUpdateBootloadersAsync(
            canonicalImage,
            parsedHeader.UpdateBootloaderScanPlan,
            cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        Report(progress, "inspecting-nand-patches", "Inspecting the NAND patch section.", 6, 7);
        PatchInspectionResult? patchInspection = await InspectPatchSectionAsync(canonicalImage, cancellationToken)
            .ConfigureAwait(false);
        NandVirtualFuseEvidence virtualFuses = await InspectVirtualFusesAsync(
            canonicalImage,
            parsedHeader.VirtualFuseScanPlan,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        ImmutableArray<NandBootloaderStage> bootloaders = CombineBootloaders(mainBootloaders.Stages, updateBootloaders.Stages);
        int? cbABuild = FindBuild(bootloaders, NandBootloaderStageKind.CB_A);
        int? cbBBuild = FindBuild(bootloaders, NandBootloaderStageKind.CB_B);
        int? cbXBuild = FindBuild(bootloaders, NandBootloaderStageKind.CB_X);
        var storageEvidence = GetConsoleStorageEvidence(canonicalImage.Summary);
        var consoleEvidence = new ConsoleIdentificationEvidence(
            cbBuild: cbXBuild is > 0 ? cbBBuild : cbABuild,
            cbBBuild: cbBBuild,
            smcType: smc.MotherboardType,
            rawNandLength: storageEvidence.RawNandLength,
            layout: storageEvidence.Layout,
            flashConfiguration: null,
            hasSpareData: storageEvidence.HasSpareData);
        ConsoleIdentificationResult consoleIdentification = ConsoleIdentifier.Identify(consoleEvidence);
        NandHackEvidence hackEvidence = NandHackEvidenceService.Identify(cbABuild, cbBBuild, cbXBuild);

        Report(progress, "completed-nand-inspection", "Completed safe NAND inspection.", 7, 7);
        return new NandInspectionResult(
            canonicalImage.Summary,
            header,
            bootloaders,
            smc,
            keyvault,
            consoleIdentification,
            hackEvidence,
            patchInspection,
            virtualFuses,
            updateBootloaders.DashboardBuild,
            mainBootloaders.ImageFamily);
    }

    private static async Task<ParsedNandHeader> ReadHeaderAsync(
        NandCanonicalImage image,
        CancellationToken cancellationToken)
    {
        long logicalLength = image.Summary.CanonicalLogicalByteLength;
        if (!FitsWithinImage(offset: 0, length: HeaderReadLength, logicalLength))
        {
            throw Failure(
                "truncated-image",
                "The canonical NAND image is too short to contain the required header fields.");
        }

        byte[] header = GC.AllocateUninitializedArray<byte>(HeaderReadLength);
        try
        {
            await ReadExactlyAsync(image, 0, header, cancellationToken, "NAND header").ConfigureAwait(false);
            uint magicValue = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, sizeof(ushort)));
            if (!IsSupportedHeaderMagic(magicValue))
            {
                throw Failure(
                    "invalid-nand-header",
                    "The canonical NAND header does not contain a supported magic value.");
            }

            int headerBuild = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(sizeof(ushort), sizeof(ushort)));

            if (headerBuild == 0)
            {
                throw Failure(
                    "invalid-nand-header",
                    "The canonical NAND header does not contain a positive build number.");
            }

            long firstStageOffset = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x08, sizeof(uint)));
            long smcLength = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x78, sizeof(uint)));
            long smcOffset = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x7C, sizeof(uint)));
            if (smcLength == 0 ||
                smcLength > MaximumSmcLength ||
                !FitsWithinImage(smcOffset, smcLength, logicalLength))
            {
                throw Failure(
                    "invalid-smc-range",
                    "The NAND header declares an unsupported or out-of-range SMC range.");
            }

            if (!FitsWithinImage(KeyvaultLogicalOffset, KeyvaultService.KeyvaultLength, logicalLength))
            {
                throw Failure(
                    "invalid-keyvault-size",
                    "The canonical NAND image does not contain one complete legacy keyvault range.");
            }

            if (firstStageOffset == 0 || firstStageOffset >= logicalLength)
            {
                throw Failure(
                    "missing-bootloader",
                    "The NAND header does not point to a bootloader stage within the canonical image.");
            }

            long patchSlotOffset = BinaryPrimitives.ReadUInt32BigEndian(
                header.AsSpan(PatchSlotLogicalOffsetFieldOffset, sizeof(uint)));
            int patchSlotCount = BinaryPrimitives.ReadUInt16BigEndian(
                header.AsSpan(PatchSlotCountFieldOffset, sizeof(ushort)));
            long declaredPatchSlotSize = BinaryPrimitives.ReadUInt32BigEndian(
                header.AsSpan(PatchSlotSizeFieldOffset, sizeof(uint)));
            long patchSlotSize = declaredPatchSlotSize == 0
                ? DefaultVirtualFusePatchSlotSize
                : declaredPatchSlotSize;

            return new ParsedNandHeader(
                new NandHeaderInspection(
                    new NandMagic(magicValue, byteLength: 2),
                    headerBuild,
                    firstStageOffset,
                    new NandLogicalRange(smcOffset, smcLength),
                    new NandLogicalRange(KeyvaultLogicalOffset, KeyvaultService.KeyvaultLength)),
                new UpdateBootloaderScanPlan(patchSlotOffset, patchSlotCount, declaredPatchSlotSize),
                new VirtualFuseScanPlan(patchSlotOffset, patchSlotCount, patchSlotSize));
        }
        finally
        {
            ZeroMemory(header);
        }
    }

    private static async Task<NandSmcInspection> InspectSmcAsync(
        NandCanonicalImage image,
        NandLogicalRange smcRange,
        CancellationToken cancellationToken)
    {
        if (smcRange.Length > MaximumSmcLength)
        {
            throw Failure(
                "invalid-smc-range",
                "The NAND header declares an SMC range too large to inspect safely.");
        }

        byte[] encryptedSmc = await ReadArrayAsync(
            image,
            smcRange.Offset,
            checked((int)smcRange.Length),
            cancellationToken,
            "SMC").ConfigureAwait(false);
        try
        {
            byte[] decryptedSmc = SmcCrypto.Decrypt(encryptedSmc, cancellationToken);
            try
            {
                SmcVersion version = SmcCrypto.GetVersion(decryptedSmc);
                NandHackedSmcEvidence? hackedSmcEvidence = null;
                if (decryptedSmc.Length >= HackedSmcMarkerOffset + HackedSmcMarkerLength)
                {
                    hackedSmcEvidence = new NandHackedSmcEvidence(
                        HackedSmcMarkerOffset,
                        HackedSmcMarkerLength,
                        IsAllZero(decryptedSmc.AsSpan(HackedSmcMarkerOffset, HackedSmcMarkerLength)));
                }

                return new NandSmcInspection(
                    smcRange,
                    version.MotherboardType,
                    version,
                    hackedSmcEvidence);
            }
            finally
            {
                ZeroMemory(decryptedSmc);
            }
        }
        finally
        {
            ZeroMemory(encryptedSmc);
        }
    }

    private static async Task<NandKeyvaultInspection> InspectKeyvaultAsync(
        NandCanonicalImage image,
        CpuKey? cpuKey,
        CancellationToken cancellationToken)
    {
        byte[] rawKeyvault = await ReadArrayAsync(
            image,
            KeyvaultLogicalOffset,
            KeyvaultService.KeyvaultLength,
            cancellationToken,
            "keyvault").ConfigureAwait(false);
        try
        {
            uint crc32 = Crc32.Compute(rawKeyvault, cancellationToken);
            KeyvaultInspection inspection = KeyvaultService.Inspect(rawKeyvault, cpuKey, cancellationToken);
            if (cpuKey.HasValue && inspection.CpuKeyVerification == KeyvaultCpuKeyVerificationStatus.Failed)
            {
                throw Failure(
                    "cpu-key-verification-failed",
                    "The CPU key does not verify this keyvault.");
            }

            return new NandKeyvaultInspection(
                new NandLogicalRange(KeyvaultLogicalOffset, KeyvaultService.KeyvaultLength),
                crc32,
                inspection);
        }
        finally
        {
            ZeroMemory(rawKeyvault);
        }
    }

    private static async Task<MainBootloaderInspection> InspectMainBootloadersAsync(
        NandCanonicalImage image,
        long firstStageOffset,
        CpuKey? cpuKey,
        CancellationToken cancellationToken)
    {
        var parsedStages = new List<ParsedBootloaderStage>(MaximumMainBootloaderStages);
        byte[] stageHeader = GC.AllocateUninitializedArray<byte>(StageHeaderLength);
        try
        {
            long currentOffset = firstStageOffset;
            long totalStageLength = 0;
            var secondBootloaderCount = 0;
            for (int stageIndex = 0; stageIndex < MaximumMainBootloaderStages; stageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!FitsWithinImage(currentOffset, StageHeaderLength, image.Summary.CanonicalLogicalByteLength))
                {
                    if (parsedStages.Count == 0)
                    {
                        throw Failure(
                            "missing-bootloader",
                            "The NAND header points to no complete bootloader header.");
                    }

                    throw Failure(
                        "truncated-bootloader",
                        "The bootloader chain ends before a complete stage header.");
                }

                await ReadExactlyAsync(image, currentOffset, stageHeader, cancellationToken, "bootloader header")
                    .ConfigureAwait(false);

                if (IsAllErased(stageHeader))
                {
                    break;
                }

                byte numericId = (byte)(stageHeader[1] & 0x0F);
                long declaredLength = BinaryPrimitives.ReadUInt32BigEndian(stageHeader.AsSpan(0x0C, sizeof(uint)));
                if (numericId == 0 && declaredLength == 0)
                {
                    break;
                }

                if (numericId == 0 || declaredLength < StageHeaderLength)
                {
                    throw Failure(
                        "invalid-stage-length",
                        "A bootloader stage has an invalid identifier or declared length.");
                }

                if (numericId == 2 && ++secondBootloaderCount > MaximumSupportedSecondBootloaderStages)
                {
                    throw Failure(
                        "unsupported-bootloader-topology",
                        "The NAND image contains more than three second-stage bootloaders.");
                }

                long roundedLength = RoundStageLength(declaredLength);
                if (roundedLength > MaximumBootloaderStageLength ||
                    totalStageLength > MaximumMainBootloaderChainLength - roundedLength)
                {
                    throw Failure(
                        "invalid-stage-length",
                        "A bootloader stage length exceeds the supported inspection limit.");
                }

                totalStageLength += roundedLength;
                if (!FitsWithinImage(currentOffset, roundedLength, image.Summary.CanonicalLogicalByteLength))
                {
                    throw Failure(
                        "truncated-bootloader",
                        "A declared bootloader stage extends beyond the canonical logical image.");
                }

                parsedStages.Add(new ParsedBootloaderStage(
                    new NandMagic(BinaryPrimitives.ReadUInt16BigEndian(stageHeader.AsSpan(0, sizeof(ushort))), byteLength: 2),
                    numericId,
                    BinaryPrimitives.ReadUInt16BigEndian(stageHeader.AsSpan(0x02, sizeof(ushort))),
                    currentOffset,
                    declaredLength,
                    roundedLength));

                currentOffset = checked(currentOffset + roundedLength);
                if (numericId == 5)
                {
                    break;
                }
            }

            if (parsedStages.Count == 0)
            {
                throw Failure("missing-bootloader", "The NAND image does not contain a bootloader stage.");
            }

            AssignMainBootloaderKinds(parsedStages);
            await DecryptMainBootloadersAsync(parsedStages, image, cpuKey, cancellationToken).ConfigureAwait(false);
            NandImageFamilyEvidence imageFamily = await InspectImageFamilyAsync(parsedStages, image, cancellationToken)
                .ConfigureAwait(false);

            var result = ImmutableArray.CreateBuilder<NandBootloaderStage>(parsedStages.Count);
            foreach (ParsedBootloaderStage parsedStage in parsedStages)
            {
                result.Add(parsedStage.Result ?? CreateUndecryptedStage(parsedStage));
            }

            return new MainBootloaderInspection(result.MoveToImmutable(), imageFamily);
        }
        finally
        {
            ZeroMemory(stageHeader);
            foreach (ParsedBootloaderStage parsedStage in parsedStages)
            {
                parsedStage.ZeroTemporaryBuffers();
            }
        }
    }

    private static async Task<NandImageFamilyEvidence> InspectImageFamilyAsync(
        List<ParsedBootloaderStage> stages,
        NandCanonicalImage image,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (stages.Count < 3 ||
            stages[0].Kind != NandBootloaderStageKind.CB_A ||
            stages[1].Kind != NandBootloaderStageKind.CB_X ||
            stages[2].Kind != NandBootloaderStageKind.CB_B ||
            stages[0].Magic.Value != 0x4342 ||
            stages[1].Magic.Value != 0x4342 ||
            stages[2].Magic.Value is not 0x4342 and not 0x5342 ||
            !stages[0].HasDecodedData)
        {
            return NandRgh3OutputEvidence.Absent;
        }

        ParsedBootloaderStage cbX = stages[1];
        if (cbX.HasDecodedData &&
            cbX.Result is
            {
                DecryptionPath: BootloaderDecryptionPath.CbWithManufacturingZeroKey,
                DecryptionEvidence: { UsesNewCbCrypto: false },
            })
        {
            return NandRgh3OutputEvidence.InspectDecoded(
                cbX.DecodedData.Span[..checked((int)cbX.DeclaredLength)],
                cbX.Build,
                cancellationToken);
        }

        byte[] encryptedCbX = await LoadStageAsync(cbX, image, cancellationToken).ConfigureAwait(false);
        return NandRgh3OutputEvidence.Inspect(
            stages[0].DecodedData.Span,
            encryptedCbX.AsSpan(0, checked((int)cbX.DeclaredLength)),
            cbX.Build,
            cancellationToken);
    }

    private static async Task DecryptMainBootloadersAsync(
        List<ParsedBootloaderStage> parsedStages,
        NandCanonicalImage image,
        CpuKey? cpuKey,
        CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte>? decodedCbA = null;
        ReadOnlyMemory<byte>? decodedCbB = null;
        ReadOnlyMemory<byte>? decodedSc = null;
        ReadOnlyMemory<byte>? decodedCd = null;
        bool sawCbX = false;

        foreach (ParsedBootloaderStage parsedStage in parsedStages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (parsedStage.Kind)
            {
                case NandBootloaderStageKind.CB_A:
                {
                    byte[] encryptedStage = await LoadStageAsync(parsedStage, image, cancellationToken).ConfigureAwait(false);
                    BootloaderDecryptionResult decryption = parsedStage.Magic.Value == S2Magic
                        ? BootloaderCrypto.DecryptS2(encryptedStage, cancellationToken)
                        : BootloaderCrypto.DecryptCb(encryptedStage, cancellationToken);
                    parsedStage.SetDecodedData(decryption.Output);
                    decodedCbA = decryption.Output;
                    BootloaderSafeEvidence evidence = ExtractCbAEvidence(decryption.Output.Span);
                    parsedStage.Result = CreateDecryptedStage(parsedStage, decryption, evidence);
                    break;
                }

                case NandBootloaderStageKind.CB_X:
                {
                    sawCbX = true;
                    if (!decodedCbA.HasValue || (!cpuKey.HasValue && !UsesManufacturingCbKey(decodedCbA.Value.Span)))
                    {
                        parsedStage.Result = CreateUndecryptedStage(parsedStage);
                        break;
                    }

                    byte[] encryptedStage = await LoadStageAsync(parsedStage, image, cancellationToken).ConfigureAwait(false);
                    BootloaderDecryptionResult decryption = BootloaderCrypto.DecryptCbWithCpuKey(
                        encryptedStage,
                        decodedCbA.Value.Span,
                        cpuKey,
                        cancellationToken);
                    parsedStage.SetDecodedData(decryption.Output);
                    BootloaderSafeEvidence evidence = ExtractCbBEvidence(decryption.Output.Span);
                    parsedStage.Result = CreateDecryptedStage(parsedStage, decryption, evidence);
                    break;
                }

                case NandBootloaderStageKind.CB_B:
                {
                    if (sawCbX)
                    {
                        byte[] plaintextStage = await LoadStageAsync(parsedStage, image, cancellationToken).ConfigureAwait(false);
                        parsedStage.SetDecodedData(plaintextStage);
                        decodedCbB = plaintextStage;
                        parsedStage.Result = CreateUndecryptedStage(
                            parsedStage,
                            NandBootloaderDecryptionStatus.NotRequired,
                            ExtractCbBEvidence(plaintextStage));
                        break;
                    }

                    if (!decodedCbA.HasValue || (!cpuKey.HasValue && !UsesManufacturingCbKey(decodedCbA.Value.Span)))
                    {
                        parsedStage.Result = CreateUndecryptedStage(parsedStage);
                        break;
                    }

                    byte[] encryptedStage = await LoadStageAsync(parsedStage, image, cancellationToken).ConfigureAwait(false);
                    BootloaderDecryptionResult decryption = BootloaderCrypto.DecryptCbWithCpuKey(
                        encryptedStage,
                        decodedCbA.Value.Span,
                        cpuKey,
                        cancellationToken);
                    parsedStage.SetDecodedData(decryption.Output);
                    decodedCbB = decryption.Output;
                    BootloaderSafeEvidence evidence = ExtractCbBEvidence(decryption.Output.Span);
                    parsedStage.Result = CreateDecryptedStage(parsedStage, decryption, evidence);
                    break;
                }

                case NandBootloaderStageKind.SC:
                {
                    byte[] encryptedStage = await LoadStageAsync(parsedStage, image, cancellationToken).ConfigureAwait(false);
                    BootloaderDecryptionResult decryption = BootloaderCrypto.DecryptSc(encryptedStage, cancellationToken);
                    parsedStage.SetDecodedData(decryption.Output);
                    decodedSc = decryption.Output;
                    parsedStage.Result = CreateDecryptedStage(parsedStage, decryption, default);
                    break;
                }

                case NandBootloaderStageKind.CD:
                {
                    ReadOnlyMemory<byte>? previousStage = decodedSc ?? decodedCbB;
                    if (!previousStage.HasValue)
                    {
                        parsedStage.Result = CreateUndecryptedStage(parsedStage);
                        break;
                    }

                    byte[] encryptedStage = await LoadStageAsync(parsedStage, image, cancellationToken).ConfigureAwait(false);
                    BootloaderDecryptionResult decryption = decodedSc.HasValue
                        ? BootloaderCrypto.DecryptCd(encryptedStage, decodedSc.Value.Span, cancellationToken)
                        : cpuKey.HasValue
                            ? BootloaderCrypto.DecryptCdWithCpuKey(
                                encryptedStage,
                                decodedCbB!.Value.Span,
                                cpuKey.Value,
                                cancellationToken)
                            : BootloaderCrypto.DecryptCd(encryptedStage, previousStage.Value.Span, cancellationToken);
                    parsedStage.SetDecodedData(decryption.Output);
                    decodedCd = decryption.Output;
                    parsedStage.Result = CreateDecryptedStage(parsedStage, decryption, default);
                    break;
                }

                case NandBootloaderStageKind.CE:
                {
                    if (!decodedCd.HasValue)
                    {
                        parsedStage.Result = CreateUndecryptedStage(parsedStage);
                        break;
                    }

                    byte[] encryptedStage = await LoadStageAsync(parsedStage, image, cancellationToken).ConfigureAwait(false);
                    BootloaderDecryptionResult decryption = BootloaderCrypto.DecryptCe(
                        encryptedStage,
                        decodedCd.Value.Span,
                        cancellationToken);
                    parsedStage.SetDecodedData(decryption.Output);
                    parsedStage.Result = CreateDecryptedStage(parsedStage, decryption, default);
                    break;
                }

                default:
                    parsedStage.Result = CreateUndecryptedStage(parsedStage);
                    break;
            }
        }
    }

    private static async Task<UpdateBootloaderInspection> InspectUpdateBootloadersAsync(
        NandCanonicalImage image,
        UpdateBootloaderScanPlan scanPlan,
        CancellationToken cancellationToken)
    {
        if (scanPlan.PatchSlotLogicalOffset == 0 ||
            scanPlan.PatchSlotCount == 0 ||
            scanPlan.DeclaredPatchSlotSize == 0)
        {
            return new UpdateBootloaderInspection(ImmutableArray<NandBootloaderStage>.Empty, default);
        }

        long declaredRegionLength;
        try
        {
            declaredRegionLength = checked(scanPlan.DeclaredPatchSlotSize * scanPlan.PatchSlotCount);
        }
        catch (OverflowException)
        {
            return new UpdateBootloaderInspection(ImmutableArray<NandBootloaderStage>.Empty, default);
        }

        if (!FitsWithinImage(
            scanPlan.PatchSlotLogicalOffset,
            declaredRegionLength,
            image.Summary.CanonicalLogicalByteLength))
        {
            return new UpdateBootloaderInspection(ImmutableArray<NandBootloaderStage>.Empty, default);
        }

        var stages = ImmutableArray.CreateBuilder<NandBootloaderStage>();
        byte[] buffer = GC.AllocateUninitializedArray<byte>(UpdateSlotScanChunkLength);
        try
        {
            int? completeTarget = null;
            bool conflictingTargets = false;
            bool allActiveSlotsComplete = true;
            for (int slotIndex = 0; slotIndex < scanPlan.PatchSlotCount; slotIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long slotOffset = checked(
                    scanPlan.PatchSlotLogicalOffset +
                    checked(scanPlan.DeclaredPatchSlotSize * slotIndex));
                int initialHeaderLength = (int)Math.Min(StageHeaderLength, scanPlan.DeclaredPatchSlotSize);
                await ReadExactlyAsync(
                    image,
                    slotOffset,
                    buffer.AsMemory(0, initialHeaderLength),
                    cancellationToken,
                    "update slot header").ConfigureAwait(false);

                if (IsBlankSlotFiller(buffer.AsSpan(0, initialHeaderLength)))
                {
                    if (!await IsBlankUpdateSlotRangeAsync(
                        image,
                        checked(slotOffset + initialHeaderLength),
                        scanPlan.DeclaredPatchSlotSize - initialHeaderLength,
                        buffer,
                        cancellationToken).ConfigureAwait(false))
                    {
                        allActiveSlotsComplete = false;
                    }

                    continue;
                }

                if (initialHeaderLength != StageHeaderLength ||
                    BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(0, sizeof(ushort))) != CfMagic)
                {
                    allActiveSlotsComplete = false;
                    continue;
                }

                int cfBuild = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(0x02, sizeof(ushort)));
                long cfDeclaredLength = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(0x0C, sizeof(uint)));
                long cfRoundedLength = RoundStageLength(cfDeclaredLength);
                if (cfDeclaredLength < CfHeaderLength ||
                    !FitsWithinImage(0, cfRoundedLength, scanPlan.DeclaredPatchSlotSize))
                {
                    allActiveSlotsComplete = false;
                    continue;
                }

                await ReadExactlyAsync(
                    image,
                    checked(slotOffset + StageHeaderLength),
                    buffer.AsMemory(StageHeaderLength, CfHeaderLength - StageHeaderLength),
                    cancellationToken,
                    "complete CF header").ConfigureAwait(false);

                // CF target_ver is separate from the generic bootloader build and adjacent flags.
                int targetVersion = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(0x14, sizeof(ushort)));
                long cfDeclaredCgLength = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(0x1C, sizeof(uint)));
                long cgRoundedLength = RoundStageLength(cfDeclaredCgLength);
                if (cfDeclaredCgLength < CgHeaderLength ||
                    !FitsWithinImage(cfRoundedLength, cgRoundedLength, scanPlan.DeclaredPatchSlotSize))
                {
                    allActiveSlotsComplete = false;
                    continue;
                }

                long cgOffset = checked(slotOffset + cfRoundedLength);
                await ReadExactlyAsync(
                    image,
                    cgOffset,
                    buffer.AsMemory(0, CgHeaderLength),
                    cancellationToken,
                    "complete CG header").ConfigureAwait(false);

                long cgDeclaredLength = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(0x0C, sizeof(uint)));
                if (BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(0, sizeof(ushort))) != CgMagic ||
                    cgDeclaredLength != cfDeclaredCgLength)
                {
                    allActiveSlotsComplete = false;
                    continue;
                }

                NandBootloaderStageKind cfKind = slotIndex switch
                {
                    0 => NandBootloaderStageKind.CF0,
                    1 => NandBootloaderStageKind.CF1,
                    _ => NandBootloaderStageKind.Other,
                };
                NandBootloaderStageKind cgKind = slotIndex switch
                {
                    0 => NandBootloaderStageKind.CG0,
                    1 => NandBootloaderStageKind.CG1,
                    _ => NandBootloaderStageKind.Other,
                };
                stages.Add(new NandBootloaderStage(
                    cfKind,
                    numericId: 6,
                    new NandMagic(CfMagic, byteLength: 2),
                    cfBuild,
                    slotOffset,
                    cfDeclaredLength,
                    cfRoundedLength,
                    NandBootloaderDecryptionStatus.NotAttempted));
                stages.Add(new NandBootloaderStage(
                    cgKind,
                    numericId: 7,
                    new NandMagic(CgMagic, byteLength: 2),
                    BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(0x02, sizeof(ushort))),
                    cgOffset,
                    cgDeclaredLength,
                    cgRoundedLength,
                    NandBootloaderDecryptionStatus.NotAttempted));

                if (targetVersion is 0 or ushort.MaxValue)
                {
                    allActiveSlotsComplete = false;
                }
                else if (completeTarget is { } priorTarget && priorTarget != targetVersion)
                {
                    conflictingTargets = true;
                }
                else
                {
                    completeTarget = targetVersion;
                }
            }

            NandDashboardBuildEvidence dashboardBuild = conflictingTargets
                ? new NandDashboardBuildEvidence(NandEvidenceResolution.Conflicting, null)
                : allActiveSlotsComplete && completeTarget.HasValue
                    ? new NandDashboardBuildEvidence(NandEvidenceResolution.Confirmed, completeTarget)
                    : default;
            return new UpdateBootloaderInspection(stages.ToImmutable(), dashboardBuild);
        }
        finally
        {
            ZeroMemory(buffer);
        }
    }

    private static async Task<bool> IsBlankUpdateSlotRangeAsync(
        NandCanonicalImage image,
        long logicalOffset,
        long length,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        long scannedLength = 0;
        while (scannedLength < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int chunkLength = (int)Math.Min(buffer.Length, length - scannedLength);
            await ReadExactlyAsync(
                image,
                checked(logicalOffset + scannedLength),
                buffer.AsMemory(0, chunkLength),
                cancellationToken,
                "inactive update slot").ConfigureAwait(false);
            if (!IsBlankSlotFiller(buffer.AsSpan(0, chunkLength)))
            {
                return false;
            }

            scannedLength = checked(scannedLength + chunkLength);
        }

        return true;
    }

    private static bool IsBlankSlotFiller(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            if (value is not (0 or byte.MaxValue))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<PatchInspectionResult?> InspectPatchSectionAsync(
        NandCanonicalImage image,
        CancellationToken cancellationToken)
    {
        long primaryOffset = image.Summary.SelectedLayout == NandLegacyLayout.Layout2
            ? Layout2PatchLogicalOffset
            : OtherPatchLogicalOffset;
        long logicalLength = image.Summary.CanonicalLogicalByteLength;
        if (primaryOffset >= logicalLength)
        {
            return null;
        }

        if (!FitsWithinImage(primaryOffset, PatchSectionLength, logicalLength))
        {
            throw Failure(
                "truncated-patch-section",
                "The canonical NAND image contains only part of the required primary patch section.");
        }

        PatchInspectionResult primary = await InspectPatchAtAsync(image, primaryOffset, cancellationToken).ConfigureAwait(false);
        if (primary.IsComplete)
        {
            return primary;
        }

        if (FitsWithinImage(LegacyPatchLogicalOffset, PatchSectionLength, logicalLength))
        {
            PatchInspectionResult fallback = await InspectPatchAtAsync(image, LegacyPatchLogicalOffset, cancellationToken)
                .ConfigureAwait(false);
            if (fallback.IsComplete)
            {
                return fallback;
            }
        }

        return primary;
    }

    private static async Task<NandVirtualFuseEvidence> InspectVirtualFusesAsync(
        NandCanonicalImage image,
        VirtualFuseScanPlan scanPlan,
        CancellationToken cancellationToken)
    {
        if (scanPlan.PatchSlotCount > MaximumVirtualFusePatchSlots ||
            scanPlan.PatchSlotSize < VirtualFuseMarkerLength)
        {
            return new NandVirtualFuseEvidence(NandVirtualFuseEvidenceStatus.Unavailable);
        }

        byte[] marker = GC.AllocateUninitializedArray<byte>(VirtualFuseMarkerLength);
        try
        {
            long logicalLength = image.Summary.CanonicalLogicalByteLength;
            for (int slotIndex = 0; slotIndex < scanPlan.PatchSlotCount; slotIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long patchSlotOffset;
                try
                {
                    patchSlotOffset = checked(
                        scanPlan.PatchSlotLogicalOffset +
                        checked(scanPlan.PatchSlotSize * slotIndex));
                }
                catch (OverflowException)
                {
                    return new NandVirtualFuseEvidence(NandVirtualFuseEvidenceStatus.Unavailable);
                }

                if (!FitsWithinImage(patchSlotOffset, VirtualFuseMarkerLength, logicalLength))
                {
                    return new NandVirtualFuseEvidence(NandVirtualFuseEvidenceStatus.Unavailable);
                }

                await ReadExactlyAsync(
                    image,
                    patchSlotOffset,
                    marker,
                    cancellationToken,
                    "virtual-fuse marker").ConfigureAwait(false);
                if (VirtualFuseLine0.AsSpan().SequenceEqual(marker))
                {
                    return new NandVirtualFuseEvidence(NandVirtualFuseEvidenceStatus.Present);
                }
            }

            if (!FitsWithinImage(LegacyJtagVirtualFuseLogicalOffset, VirtualFuseMarkerLength, logicalLength))
            {
                return new NandVirtualFuseEvidence(NandVirtualFuseEvidenceStatus.Unavailable);
            }

            await ReadExactlyAsync(
                image,
                LegacyJtagVirtualFuseLogicalOffset,
                marker,
                cancellationToken,
                "virtual-fuse marker").ConfigureAwait(false);
            return new NandVirtualFuseEvidence(
                VirtualFuseLine0.AsSpan().SequenceEqual(marker)
                    ? NandVirtualFuseEvidenceStatus.Present
                    : NandVirtualFuseEvidenceStatus.Absent);
        }
        finally
        {
            ZeroMemory(marker);
        }
    }

    private static async Task<PatchInspectionResult> InspectPatchAtAsync(
        NandCanonicalImage image,
        long logicalOffset,
        CancellationToken cancellationToken)
    {
        byte[] patchSection = await ReadArrayAsync(
            image,
            logicalOffset,
            PatchSectionLength,
            cancellationToken,
            "patch section").ConfigureAwait(false);
        try
        {
            return PatchInspectionService.Inspect(patchSection, cancellationToken: cancellationToken);
        }
        finally
        {
            ZeroMemory(patchSection);
        }
    }

    private static async Task<byte[]> LoadStageAsync(
        ParsedBootloaderStage stage,
        NandCanonicalImage image,
        CancellationToken cancellationToken)
    {
        if (stage.RawData is not null)
        {
            return stage.RawData;
        }
        if (stage.RoundedLength > MaximumBootloaderStageLength)
        {
            throw Failure(
                "invalid-stage-length",
                "A bootloader stage cannot be safely read from its declared length.");
        }

        byte[] rawData = await ReadArrayAsync(
            image,
            stage.LogicalOffset,
            checked((int)stage.RoundedLength),
            cancellationToken,
            "bootloader stage").ConfigureAwait(false);
        stage.RawData = rawData;
        return rawData;
    }

    private static async Task<byte[]> ReadArrayAsync(
        NandCanonicalImage image,
        long logicalOffset,
        int length,
        CancellationToken cancellationToken,
        string rangeName)
    {
        byte[] result = GC.AllocateUninitializedArray<byte>(length);
        try
        {
            await ReadExactlyAsync(image, logicalOffset, result, cancellationToken, rangeName).ConfigureAwait(false);
            return result;
        }
        catch
        {
            ZeroMemory(result);
            throw;
        }
    }

    private static async Task ReadExactlyAsync(
        NandCanonicalImage image,
        long logicalOffset,
        Memory<byte> destination,
        CancellationToken cancellationToken,
        string rangeName)
    {
        if (!FitsWithinImage(logicalOffset, destination.Length, image.Summary.CanonicalLogicalByteLength))
        {
            throw Failure(
                "truncated-image",
                $"The canonical NAND image is too short to read the required {rangeName} range.");
        }

        int copied = 0;
        while (copied < destination.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = await image.ReadLogicalAsync(
                checked(logicalOffset + copied),
                destination.Slice(copied),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw Failure(
                    "truncated-image",
                    $"The canonical NAND image ended while reading the required {rangeName} range.");
            }

            copied = checked(copied + read);
        }
    }

    private static void AssignMainBootloaderKinds(List<ParsedBootloaderStage> stages)
    {
        ParsedBootloaderStage? priorCbB = null;
        int secondBootloaderCount = 0;
        foreach (ParsedBootloaderStage stage in stages)
        {
            stage.Kind = stage.NumericId switch
            {
                3 => NandBootloaderStageKind.SC,
                4 => NandBootloaderStageKind.CD,
                5 => NandBootloaderStageKind.CE,
                _ => NandBootloaderStageKind.Other,
            };

            if (stage.NumericId != 2)
            {
                continue;
            }

            switch (secondBootloaderCount)
            {
                case 0:
                    stage.Kind = NandBootloaderStageKind.CB_A;
                    break;
                case 1:
                    stage.Kind = NandBootloaderStageKind.CB_B;
                    priorCbB = stage;
                    break;
                case 2:
                    if (priorCbB is not null)
                    {
                        priorCbB.Kind = NandBootloaderStageKind.CB_X;
                    }

                    stage.Kind = NandBootloaderStageKind.CB_B;
                    priorCbB = stage;
                    break;
            }

            secondBootloaderCount++;
        }
    }

    private static NandBootloaderStage CreateDecryptedStage(
        ParsedBootloaderStage stage,
        BootloaderDecryptionResult decryption,
        BootloaderSafeEvidence evidence)
    {
        return new NandBootloaderStage(
            stage.Kind,
            stage.NumericId,
            stage.Magic,
            stage.Build,
            stage.LogicalOffset,
            stage.DeclaredLength,
            stage.RoundedLength,
            NandBootloaderDecryptionStatus.Decrypted,
            decryption.Path,
            new NandBootloaderDecryptionEvidence(
                decryption.UsesNewCbCrypto,
                decryption.HasLegacyCdZeroRangeEvidence),
            evidence.Ldv,
            evidence.PairingData);
    }

    private static NandBootloaderStage CreateUndecryptedStage(
        ParsedBootloaderStage stage,
        NandBootloaderDecryptionStatus decryptionStatus = NandBootloaderDecryptionStatus.NotAttempted,
        BootloaderSafeEvidence evidence = default)
    {
        return new NandBootloaderStage(
            stage.Kind,
            stage.NumericId,
            stage.Magic,
            stage.Build,
            stage.LogicalOffset,
            stage.DeclaredLength,
            stage.RoundedLength,
            decryptionStatus,
            ldv: evidence.Ldv,
            pairingData: evidence.PairingData);
    }

    private static BootloaderSafeEvidence ExtractCbAEvidence(ReadOnlySpan<byte> decodedStage)
    {
        byte? ldv = decodedStage.Length > CbLdvOffset && decodedStage[CbLdvOffset] <= 16
            ? decodedStage[CbLdvOffset]
            : null;
        return new BootloaderSafeEvidence(ldv, ExtractPairingData(decodedStage));
    }

    private static BootloaderSafeEvidence ExtractCbBEvidence(ReadOnlySpan<byte> decodedStage)
    {
        if (decodedStage.Length <= CbBEncryptedMarkerThirdOffset ||
            decodedStage[CbBEncryptedMarkerFirstOffset] != 0 ||
            decodedStage[CbBEncryptedMarkerSecondOffset] != 0 ||
            decodedStage[CbBEncryptedMarkerThirdOffset] != 0)
        {
            return default;
        }

        byte? ldv = decodedStage.Length > CbLdvOffset
            ? decodedStage[CbLdvOffset] <= 16
                ? decodedStage[CbLdvOffset]
                : null
            : null;
        if (decodedStage.Length > 3 && decodedStage[2] == 0x3C && decodedStage[3] == 0x48)
        {
            ldv = 0;
        }

        return new BootloaderSafeEvidence(ldv, ExtractPairingData(decodedStage));
    }

    private static uint? ExtractPairingData(ReadOnlySpan<byte> decodedStage)
    {
        if (decodedStage.Length < CbPairingDataOffset + CbPairingDataLength)
        {
            return null;
        }

        return (uint)(decodedStage[CbPairingDataOffset] |
            (decodedStage[CbPairingDataOffset + 1] << 8) |
            (decodedStage[CbPairingDataOffset + 2] << 16));
    }

    private static bool UsesManufacturingCbKey(ReadOnlySpan<byte> decodedCbA) =>
        decodedCbA.Length > 7 && decodedCbA[7] != 0;

    private static ImmutableArray<NandBootloaderStage> CombineBootloaders(
        ImmutableArray<NandBootloaderStage> mainBootloaders,
        ImmutableArray<NandBootloaderStage> updateBootloaders)
    {
        if (updateBootloaders.IsDefaultOrEmpty)
        {
            return mainBootloaders;
        }

        var combined = new List<NandBootloaderStage>(mainBootloaders.Length + updateBootloaders.Length);
        combined.AddRange(mainBootloaders);
        combined.AddRange(updateBootloaders);
        combined.Sort(static (left, right) => left.LogicalOffset.CompareTo(right.LogicalOffset));
        return ImmutableArray.CreateRange(combined);
    }

    private static (
        long? RawNandLength,
        NandLegacyLayout? Layout,
        bool? HasSpareData) GetConsoleStorageEvidence(NandCanonicalImageSummary summary)
    {
        if (summary.DetectedFormat == NandPhysicalFormat.InterleavedEcc)
        {
            return (summary.RawByteLength, summary.SelectedLayout, true);
        }

        if (summary.RawByteLength == KnownEmmcLogicalImageLength)
        {
            return (summary.RawByteLength, Layout: null, HasSpareData: false);
        }

        return (RawNandLength: null, Layout: null, HasSpareData: null);
    }

    private static int? FindBuild(ImmutableArray<NandBootloaderStage> stages, NandBootloaderStageKind kind)
    {
        foreach (NandBootloaderStage stage in stages)
        {
            if (stage.Kind == kind)
            {
                return stage.Build;
            }
        }

        return null;
    }

    private static bool IsSupportedHeaderMagic(uint magic) =>
        magic is 0xFF4F or 0xFF3F or 0x0F3F or 0x0F4F;

    private static long RoundStageLength(long declaredLength)
    {
        try
        {
            return checked((declaredLength + 0x0F) & ~0x0FL);
        }
        catch (OverflowException)
        {
            throw Failure(
                "invalid-stage-length",
                "A bootloader stage length exceeds the supported range.");
        }
    }

    private static bool FitsWithinImage(long offset, long length, long imageLength) =>
        offset >= 0 && length >= 0 && imageLength >= 0 && offset <= imageLength && length <= imageLength - offset;

    private static bool IsAllZero(ReadOnlySpan<byte> bytes)
    {
        byte aggregate = 0;
        foreach (byte value in bytes)
        {
            aggregate |= value;
        }

        return aggregate == 0;
    }

    private static bool IsAllErased(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            if (value != byte.MaxValue)
            {
                return false;
            }
        }

        return true;
    }

    private static void EnsureInitializedCpuKey(CpuKey? cpuKey)
    {
        if (cpuKey is { } suppliedCpuKey && !suppliedCpuKey.IsInitialized)
        {
            throw Failure("invalid-cpu-key", "The supplied CPU key is not an initialized parsed CPU key.");
        }
    }

    private static void ZeroMemory(byte[]? bytes)
    {
        if (bytes is not null)
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void ZeroMemory(ReadOnlyMemory<byte> bytes)
    {
        if (MemoryMarshal.TryGetArray(bytes, out ArraySegment<byte> segment) && segment.Array is { } array)
        {
            CryptographicOperations.ZeroMemory(array.AsSpan(segment.Offset, segment.Count));
        }
    }

    private static OperationFailureException Failure(string kind, string message) =>
        new(ExitCode.InvalidData, kind, message);

    private static void Report(IProgress<OperationProgress>? progress, string kind, string message, long completed, long total)
    {
        progress?.Report(new OperationProgress(kind, message, completed: completed, total: total));
    }

    private readonly record struct BootloaderSafeEvidence(byte? Ldv, uint? PairingData);

    private readonly record struct ParsedNandHeader(
        NandHeaderInspection Inspection,
        UpdateBootloaderScanPlan UpdateBootloaderScanPlan,
        VirtualFuseScanPlan VirtualFuseScanPlan);

    private readonly record struct VirtualFuseScanPlan(
        long PatchSlotLogicalOffset,
        int PatchSlotCount,
        long PatchSlotSize);

    private readonly record struct UpdateBootloaderScanPlan(
        long PatchSlotLogicalOffset,
        int PatchSlotCount,
        long DeclaredPatchSlotSize);

    private readonly record struct MainBootloaderInspection(
        ImmutableArray<NandBootloaderStage> Stages,
        NandImageFamilyEvidence ImageFamily);

    private readonly record struct UpdateBootloaderInspection(
        ImmutableArray<NandBootloaderStage> Stages,
        NandDashboardBuildEvidence DashboardBuild);

    private sealed class ParsedBootloaderStage
    {
        internal ParsedBootloaderStage(
            NandMagic magic,
            byte numericId,
            int build,
            long logicalOffset,
            long declaredLength,
            long roundedLength)
        {
            Magic = magic;
            NumericId = numericId;
            Build = build;
            LogicalOffset = logicalOffset;
            DeclaredLength = declaredLength;
            RoundedLength = roundedLength;
            Kind = NandBootloaderStageKind.Other;
        }

        internal NandMagic Magic { get; }

        internal byte NumericId { get; }

        internal int Build { get; }

        internal long LogicalOffset { get; }

        internal long DeclaredLength { get; }

        internal long RoundedLength { get; }

        internal NandBootloaderStageKind Kind { get; set; }

        internal byte[]? RawData { get; set; }

        internal ReadOnlyMemory<byte> DecodedData { get; private set; }

        internal bool HasDecodedData { get; private set; }

        internal NandBootloaderStage? Result { get; set; }

        internal void SetDecodedData(ReadOnlyMemory<byte> data)
        {
            DecodedData = data;
            HasDecodedData = true;
        }

        internal void ZeroTemporaryBuffers()
        {
            NandImageService.ZeroMemory(RawData);
            if (HasDecodedData)
            {
                NandImageService.ZeroMemory(DecodedData);
            }
        }
    }
}
