using System.Collections.Immutable;
using JRunner.Core.Contracts;
using JRunner.Core.Nand.Inspection;
using JRunner.Core.Nand.Inspection.Models;
using JRunner.Core.XeBuild.Preparation;

namespace JRunner.Core.XeBuild;

/// <summary>
/// Describes the direct output evidence required for one XeBuild target.
/// </summary>
public sealed record XeBuildOutputEvidenceCapability
{
    internal XeBuildOutputEvidenceCapability(
        XeBuildHackType hackType,
        NandImageFamily requiredFamily,
        NandDirectOutputEvidenceManifest? reviewedManifest)
    {
        HackType = hackType;
        RequiredFamily = requiredFamily;
        ReviewedManifest = reviewedManifest;
    }

    /// <summary>Gets the requested XeBuild target type.</summary>
    public XeBuildHackType HackType { get; }

    /// <summary>Gets the family that must be proven from the completed image, not from the request.</summary>
    public NandImageFamily RequiredFamily { get; }

    /// <summary>Gets the source-reviewed output evidence policy, when one exists.</summary>
    public NandDirectOutputEvidenceManifest? ReviewedManifest { get; }

    /// <summary>Gets whether a reviewed output evidence policy is available for this target.</summary>
    public bool IsAvailable => ReviewedManifest is not null;
}

/// <summary>
/// Separates canonical target syntax from the availability of reviewed direct output evidence.
/// </summary>
/// <remarks>
/// Reviewed direct output evidence is supplied only for the RGH3 conversion of Glitch2 and Glitch2m targets.
/// Other selections remain unavailable regardless of support payloads, Wine, or compatibility metadata.
/// </remarks>
public static class XeBuildOutputEvidenceCatalog
{
    private static readonly ImmutableArray<(XeBuildOutputEvidenceCapability Default, XeBuildOutputEvidenceCapability Rgh3)> Capabilities =
        ImmutableArray.Create(
            CreateCapabilities(XeBuildHackType.Retail, NandImageFamily.Retail),
            CreateCapabilities(XeBuildHackType.Glitch, NandImageFamily.Glitch),
            CreateCapabilities(XeBuildHackType.Jtag, NandImageFamily.Jtag),
            CreateCapabilities(XeBuildHackType.Glitch2, NandImageFamily.Glitch2),
            CreateCapabilities(XeBuildHackType.Glitch2m, NandImageFamily.Glitch2m),
            CreateCapabilities(XeBuildHackType.DevGl, NandImageFamily.DevGl),
            CreateCapabilities(XeBuildHackType.DevGl16, NandImageFamily.DevGl),
            CreateCapabilities(XeBuildHackType.Devkit, NandImageFamily.Devkit),
            CreateCapabilities(XeBuildHackType.Devkit16, NandImageFamily.Devkit),
            CreateCapabilities(XeBuildHackType.Testkit, NandImageFamily.Testkit),
            CreateCapabilities(XeBuildHackType.Testkit16, NandImageFamily.Testkit));

    /// <summary>
    /// Gets the required family and reviewed evidence availability for a canonical target.
    /// RGH3 selects a required family only; it does not prove the family of an observed image.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The target type is unknown.</exception>
    public static XeBuildOutputEvidenceCapability Get(XeBuildHackType hackType, bool rgh3 = false)
    {
        int index = (int)hackType - (int)XeBuildHackType.Retail;
        if ((uint)index >= (uint)Capabilities.Length || Capabilities[index].Default.HackType != hackType)
        {
            throw new ArgumentOutOfRangeException(nameof(hackType), hackType, "The XeBuild hack type is not supported.");
        }

        return rgh3 ? Capabilities[index].Rgh3 : Capabilities[index].Default;
    }

    /// <summary>
    /// Requires reviewed direct output evidence before any XeBuild execution preflight or mutation.
    /// </summary>
    /// <exception cref="OperationFailureException">No reviewed output evidence corpus is available.</exception>
    public static void EnsureAvailable(XeBuildBuildTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        XeBuildOutputEvidenceCapability capability = Get(target.HackType, target.Options.Rgh3);
        if (!capability.IsAvailable)
        {
            string rgh3Selection = target.Options.Rgh3 ? " with RGH3" : string.Empty;
            throw new OperationFailureException(
                ExitCode.MissingPrerequisite,
                "xebuild-output-evidence-unavailable",
                $"XeBuild output evidence is unavailable for '{target.TypeCanonicalName}'{rgh3Selection}. " +
                "A reviewed decrypted-stage output fingerprint/signature corpus must be supplied by a reviewed JRunnerEx source update before this target can run. " +
                "Installing support payloads or Wine alone cannot resolve this prerequisite.");
        }
    }

    private static (XeBuildOutputEvidenceCapability Default, XeBuildOutputEvidenceCapability Rgh3) CreateCapabilities(
        XeBuildHackType hackType,
        NandImageFamily requiredFamily) =>
        (new XeBuildOutputEvidenceCapability(hackType, requiredFamily, reviewedManifest: null),
         new XeBuildOutputEvidenceCapability(
             hackType,
             NandImageFamily.Rgh3,
             reviewedManifest: hackType is XeBuildHackType.Glitch2 or XeBuildHackType.Glitch2m
                 ? NandRgh3OutputEvidence.ReviewedManifest
                 : null));
}
