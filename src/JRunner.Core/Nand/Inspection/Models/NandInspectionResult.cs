using System.Collections.Immutable;
using JRunner.Core.Nand.Inspection;
using JRunner.Core.Nand.Comparison;
using JRunner.Core.Nand.Hacks;
using JRunner.Core.Nand.Models;
using JRunner.Core.Patching;
using JRunner.Core.Patching.Inspection;

namespace JRunner.Core.Nand.Inspection.Models;


/// <summary>
/// Immutable, safe public result of inspecting a NAND image.
/// </summary>
/// <remarks>
/// The result composes scalar metadata and safe downstream inspection contracts only. It deliberately does not
/// expose raw image bytes, decrypted bootloader or SMC bytes, keyvault bytes, DVD keys, CPU keys, or derived keys.
/// </remarks>
public sealed record NandInspectionResult
{
    /// <summary>
    /// Creates a complete NAND-inspection result when virtual-fuse marker inspection is unavailable.
    /// </summary>
    /// <remarks>
    /// Existing callers that construct a result from previously available inspection facts cannot
    /// assert an image family: direct decrypted-payload matching is performed only by the inspection pipeline.
    /// </remarks>
    public NandInspectionResult(
        NandCanonicalImageSummary canonicalImage,
        NandHeaderInspection header,
        ImmutableArray<NandBootloaderStage> bootloaderStages,
        NandSmcInspection smc,
        NandKeyvaultInspection keyvault,
        ConsoleIdentificationResult consoleIdentification,
        NandHackEvidence hackEvidence,
        PatchInspectionResult? patchInspection)
        : this(
            canonicalImage,
            header,
            bootloaderStages,
            smc,
            keyvault,
            consoleIdentification,
            hackEvidence,
            patchInspection,
            new NandVirtualFuseEvidence(NandVirtualFuseEvidenceStatus.Unavailable))
    {
    }

    /// <summary>
    /// Creates a complete NAND-inspection result.
    /// </summary>
    /// <param name="canonicalImage">Canonical raw/logical image metadata, including physical format and remaps.</param>
    /// <param name="header">Safe header facts and logical ranges.</param>
    /// <param name="bootloaderStages">Recognized bootloader stages in logical-image order.</param>
    /// <param name="smc">Safe SMC location and evidence.</param>
    /// <param name="keyvault">Safe keyvault range, CRC-32, and verification metadata.</param>
    /// <param name="consoleIdentification">The ranked console-identification result.</param>
    /// <param name="hackEvidence">The safe NAND hack-identification evidence.</param>
    /// <param name="patchInspection">Optional structured patch-section inspection.</param>
    /// <param name="virtualFuses">The safely inspected legacy virtual-fuse marker status.</param>
    /// <remarks>
    /// Structural stage metadata alone proves neither an installed dashboard nor an output family.
    /// Exact CF targets and reviewed decrypted-payload signatures come only from the inspection pipeline.
    /// </remarks>
    public NandInspectionResult(
        NandCanonicalImageSummary canonicalImage,
        NandHeaderInspection header,
        ImmutableArray<NandBootloaderStage> bootloaderStages,
        NandSmcInspection smc,
        NandKeyvaultInspection keyvault,
        ConsoleIdentificationResult consoleIdentification,
        NandHackEvidence hackEvidence,
        PatchInspectionResult? patchInspection,
        NandVirtualFuseEvidence virtualFuses)
        : this(
            canonicalImage,
            header,
            bootloaderStages,
            smc,
            keyvault,
            consoleIdentification,
            hackEvidence,
            patchInspection,
            virtualFuses,
            dashboardBuild: default)
    {
    }

    internal NandInspectionResult(
        NandCanonicalImageSummary canonicalImage,
        NandHeaderInspection header,
        ImmutableArray<NandBootloaderStage> bootloaderStages,
        NandSmcInspection smc,
        NandKeyvaultInspection keyvault,
        ConsoleIdentificationResult consoleIdentification,
        NandHackEvidence hackEvidence,
        PatchInspectionResult? patchInspection,
        NandVirtualFuseEvidence virtualFuses,
        NandDashboardBuildEvidence dashboardBuild,
        NandImageFamilyEvidence? imageFamily = null)
    {
        ArgumentNullException.ThrowIfNull(canonicalImage);
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(smc);
        ArgumentNullException.ThrowIfNull(keyvault);
        ArgumentNullException.ThrowIfNull(consoleIdentification);
        ArgumentNullException.ThrowIfNull(hackEvidence);
        ArgumentNullException.ThrowIfNull(virtualFuses);

        if (bootloaderStages.IsDefault)
        {
            bootloaderStages = ImmutableArray<NandBootloaderStage>.Empty;
        }

        long logicalImageLength = canonicalImage.CanonicalLogicalByteLength;
        if (header.FirstStageLogicalOffset >= logicalImageLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(header),
                "The first bootloader-stage offset must lie within the canonical logical image.");
        }

        header.SmcLogicalRange.EnsureFitsWithin(logicalImageLength, nameof(header));
        header.KeyvaultLogicalRange.EnsureFitsWithin(logicalImageLength, nameof(header));

        if (smc.LogicalRange != header.SmcLogicalRange)
        {
            throw new ArgumentException(
                "The SMC inspection range must match the SMC range declared by the NAND header.",
                nameof(smc));
        }

        smc.LogicalRange.EnsureFitsWithin(logicalImageLength, nameof(smc));

        if (keyvault.LogicalRange != header.KeyvaultLogicalRange)
        {
            throw new ArgumentException(
                "The keyvault CRC range must match the keyvault range declared by the NAND header.",
                nameof(keyvault));
        }

        keyvault.LogicalRange.EnsureFitsWithin(logicalImageLength, nameof(keyvault));

        foreach (NandBootloaderStage stage in bootloaderStages)
        {
            ArgumentNullException.ThrowIfNull(stage);
            stage.LogicalRange.EnsureFitsWithin(logicalImageLength, nameof(bootloaderStages));
        }


        CanonicalImage = canonicalImage;
        Header = header;
        BootloaderStages = bootloaderStages;
        Smc = smc;
        Keyvault = keyvault;
        ConsoleIdentification = consoleIdentification;
        HackEvidence = hackEvidence;
        PatchInspection = patchInspection;
        SemanticEvidence = NandSemanticEvidenceService.Create(
            bootloaderStages,
            consoleIdentification,
            hackEvidence,
            virtualFuses,
            dashboardBuild,
            imageFamily);
    }

    /// <summary>
    /// Gets canonical raw/logical image metadata, including detected format, selected layout, bad blocks, and remaps.
    /// </summary>
    public NandCanonicalImageSummary CanonicalImage { get; }

    /// <summary>
    /// Gets safe NAND-header inspection facts.
    /// </summary>
    public NandHeaderInspection Header { get; }

    /// <summary>
    /// Gets recognized bootloader stages in logical-image order.
    /// </summary>
    public ImmutableArray<NandBootloaderStage> BootloaderStages { get; }

    /// <summary>
    /// Gets safe SMC location, type, version, and hacked-SMC evidence.
    /// </summary>
    public NandSmcInspection Smc { get; }

    /// <summary>
    /// Gets the keyvault CRC-32 and safe keyvault verification metadata.
    /// </summary>
    public NandKeyvaultInspection Keyvault { get; }

    /// <summary>
    /// Gets the ranked, non-arbitrating console-identification result.
    /// </summary>
    public ConsoleIdentificationResult ConsoleIdentification { get; }

    /// <summary>
    /// Gets safe hack-identification evidence derived from bootloader metadata.
    /// </summary>
    public NandHackEvidence HackEvidence { get; }

    /// <summary>
    /// Gets the structured patch-section inspection, or <see langword="null"/> when no patch section was available.
    /// </summary>
    public PatchInspectionResult? PatchInspection { get; }

    /// <summary>
    /// Gets recognized legacy patch evidence, or an empty normalized array when no patch section was available.
    /// </summary>
    public ImmutableArray<LegacyPatchEvidence> Patches =>
        PatchInspection?.RecognizedLegacyPatches ?? ImmutableArray<LegacyPatchEvidence>.Empty;

    /// <summary>
    /// Gets structural patch diagnostics, or an empty normalized array when no patch section was available.
    /// </summary>
    public ImmutableArray<PatchSectionDiagnostic> PatchDiagnostics =>
        PatchInspection?.StructuralDiagnostics ?? ImmutableArray<PatchSectionDiagnostic>.Empty;

    /// <summary>
    /// Gets fail-closed console, exact dashboard, and direct image-family evidence alongside the retained
    /// virtual-fuse and non-exhaustive legacy hack-compatibility diagnostics.
    /// </summary>
    public NandSemanticEvidence SemanticEvidence { get; }
}
