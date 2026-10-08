using System.Collections.Immutable;
using JRunner.Core.Nand.Inspection.Models;
using JRunner.Core.Nand.Models;

namespace JRunner.Core.Nand.Inspection;

/// <summary>
/// Performs fail-closed comparison of inspected NAND semantic evidence with a build requirement.
/// </summary>
/// <remarks>
/// Only exact CF target-version evidence from complete declared update-slot pairs satisfies a dashboard
/// requirement. Flash-header builds, generic CF/CG or CE builds, and CB compatibility ranges never do.
/// Image-family requirements additionally need complete decrypted-stage provenance bound to a reviewed
/// manifest. Compatibility metadata, requested types, and untrusted selected families never satisfy
/// that requirement.
/// </remarks>
public static class NandSemanticEvidenceValidator
{
    /// <summary>
    /// Validates semantic evidence attached to an inspected NAND image.
    /// </summary>
    /// <param name="inspection">The completed NAND inspection.</param>
    /// <param name="requirement">The console, optional exact dashboard, and directly proven image family required by the caller.</param>
    /// <returns>A result whose <see cref="NandSemanticEvidenceValidationResult.IsMatch"/> value is true only when every required fact was exactly proven.</returns>
    public static NandSemanticEvidenceValidationResult Validate(
        NandInspectionResult inspection,
        NandSemanticEvidenceRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        return Validate(inspection.SemanticEvidence, requirement);
    }

    /// <summary>
    /// Validates standalone semantic NAND evidence.
    /// </summary>
    /// <param name="evidence">The immutable evidence to evaluate.</param>
    /// <param name="requirement">The console, optional exact dashboard, and directly proven image family required by the caller.</param>
    /// <returns>A result whose <see cref="NandSemanticEvidenceValidationResult.IsMatch"/> value is true only when every required fact was exactly proven.</returns>
    public static NandSemanticEvidenceValidationResult Validate(
        NandSemanticEvidence evidence,
        NandSemanticEvidenceRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(requirement);

        var mismatches = ImmutableArray.CreateBuilder<NandSemanticEvidenceMismatch>(3);
        ValidateConsole(evidence.Console, requirement.Console, mismatches);
        ValidateDashboard(evidence.KernelDashboard, requirement.DashboardVersion, mismatches);
        ValidateImageFamily(evidence.ImageFamily, requirement.ImageFamily, requirement.ReviewedManifest, mismatches);
        return new NandSemanticEvidenceValidationResult(mismatches.ToImmutable());
    }

    private static void ValidateConsole(
        NandConsoleEvidence evidence,
        ConsoleId expectedConsole,
        ImmutableArray<NandSemanticEvidenceMismatch>.Builder mismatches)
    {
        switch (evidence.Resolution)
        {
            case NandEvidenceResolution.Confirmed:
                if (evidence.Console?.Id != expectedConsole)
                {
                    mismatches.Add(new NandSemanticEvidenceMismatch(NandSemanticEvidenceMismatchKind.ConsoleMismatch));
                }

                return;

            case NandEvidenceResolution.Absent:
                mismatches.Add(new NandSemanticEvidenceMismatch(NandSemanticEvidenceMismatchKind.ConsoleAbsent));
                return;

            case NandEvidenceResolution.Ambiguous:
                mismatches.Add(new NandSemanticEvidenceMismatch(NandSemanticEvidenceMismatchKind.ConsoleAmbiguous));
                return;

            case NandEvidenceResolution.Conflicting:
                mismatches.Add(new NandSemanticEvidenceMismatch(NandSemanticEvidenceMismatchKind.ConsoleConflicting));
                return;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(evidence),
                    evidence.Resolution,
                    "The console evidence resolution is not supported.");
        }
    }

    private static void ValidateDashboard(
        NandKernelDashboardEvidence evidence,
        int? expectedDashboardVersion,
        ImmutableArray<NandSemanticEvidenceMismatch>.Builder mismatches)
    {
        if (!expectedDashboardVersion.HasValue)
        {
            return;
        }

        switch (evidence.Resolution)
        {
            case NandEvidenceResolution.Confirmed:
                if (!evidence.ExactDashboardVersion.HasValue)
                {
                    mismatches.Add(new NandSemanticEvidenceMismatch(NandSemanticEvidenceMismatchKind.DashboardNotExact));
                }
                else if (evidence.ExactDashboardVersion.Value != expectedDashboardVersion.Value)
                {
                    mismatches.Add(new NandSemanticEvidenceMismatch(NandSemanticEvidenceMismatchKind.DashboardMismatch));
                }

                return;

            case NandEvidenceResolution.Absent:
                mismatches.Add(new NandSemanticEvidenceMismatch(NandSemanticEvidenceMismatchKind.DashboardAbsent));
                return;

            case NandEvidenceResolution.Ambiguous:
                mismatches.Add(new NandSemanticEvidenceMismatch(NandSemanticEvidenceMismatchKind.DashboardAmbiguous));
                return;

            case NandEvidenceResolution.Conflicting:
                mismatches.Add(new NandSemanticEvidenceMismatch(NandSemanticEvidenceMismatchKind.DashboardConflicting));
                return;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(evidence),
                    evidence.Resolution,
                    "The dashboard evidence resolution is not supported.");
        }
    }

    private static void ValidateImageFamily(
        NandImageFamilyEvidence evidence,
        NandImageFamily expectedFamily,
        NandDirectOutputEvidenceManifest? reviewedManifest,
        ImmutableArray<NandSemanticEvidenceMismatch>.Builder mismatches)
    {
        if (reviewedManifest is null)
        {
            mismatches.Add(new NandSemanticEvidenceMismatch(NandSemanticEvidenceMismatchKind.ImageFamilyEvidenceUnavailable));
            return;
        }

        switch (evidence.Resolution)
        {
            case NandEvidenceResolution.Confirmed:
                if (!reviewedManifest.HasValidProvenance(evidence.Family!.Value, evidence.DirectEvidence))
                {
                    mismatches.Add(new NandSemanticEvidenceMismatch(NandSemanticEvidenceMismatchKind.ImageFamilyEvidenceUntrusted));
                }
                else if (evidence.Family != expectedFamily)
                {
                    mismatches.Add(new NandSemanticEvidenceMismatch(NandSemanticEvidenceMismatchKind.ImageFamilyMismatch));
                }

                return;

            case NandEvidenceResolution.Unavailable:
                mismatches.Add(new NandSemanticEvidenceMismatch(NandSemanticEvidenceMismatchKind.ImageFamilyEvidenceUnavailable));
                return;

            case NandEvidenceResolution.Absent:
                mismatches.Add(new NandSemanticEvidenceMismatch(NandSemanticEvidenceMismatchKind.ImageFamilyAbsent));
                return;

            case NandEvidenceResolution.Ambiguous:
                mismatches.Add(new NandSemanticEvidenceMismatch(NandSemanticEvidenceMismatchKind.ImageFamilyAmbiguous));
                return;

            case NandEvidenceResolution.Conflicting:
                mismatches.Add(new NandSemanticEvidenceMismatch(NandSemanticEvidenceMismatchKind.ImageFamilyConflicting));
                return;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(evidence),
                    evidence.Resolution,
                    "The image-family evidence resolution is not supported.");
        }
    }
}
