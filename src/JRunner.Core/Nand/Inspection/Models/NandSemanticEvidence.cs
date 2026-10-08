using System.Collections.Immutable;
using JRunner.Core.Nand.Models;

namespace JRunner.Core.Nand.Inspection.Models;

/// <summary>
/// Describes whether a semantic fact can be asserted from inspected NAND evidence.
/// </summary>
public enum NandEvidenceResolution
{
    /// <summary>No applicable evidence was present in the inspected image.</summary>
    Absent = 0,

    /// <summary>Exactly one conclusion is supported by the inspected evidence.</summary>
    Confirmed,

    /// <summary>More than one conclusion remains possible from the inspected evidence.</summary>
    Ambiguous,

    /// <summary>Independent inspected evidence sources disagree.</summary>
    Conflicting,

    /// <summary>Image-family evidence cannot be produced because no reviewed matching policy is available.</summary>
    Unavailable,
}

/// <summary>
/// One inclusive dashboard-version range from the legacy bootloader compatibility table.
/// </summary>
/// <remarks>
/// A range records what a parsed bootloader is compatible with. It is not an assertion that the
/// NAND currently contains any particular dashboard version.
/// </remarks>
public sealed record NandDashboardVersionRange
{
    /// <summary>
    /// Creates an inclusive dashboard-version range.
    /// </summary>
    public NandDashboardVersionRange(int minimumVersion, int maximumVersion)
    {
        if (minimumVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumVersion), "The minimum dashboard version cannot be negative.");
        }

        if (maximumVersion < minimumVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumVersion),
                "The maximum dashboard version cannot be less than the minimum dashboard version.");
        }

        MinimumVersion = minimumVersion;
        MaximumVersion = maximumVersion;
    }

    /// <summary>
    /// Gets the inclusive minimum compatible dashboard version.
    /// </summary>
    public int MinimumVersion { get; }

    /// <summary>
    /// Gets the inclusive maximum compatible dashboard version.
    /// </summary>
    public int MaximumVersion { get; }

    /// <summary>
    /// Determines whether a positive dashboard version lies within this range.
    /// </summary>
    public bool Contains(int dashboardVersion) =>
        dashboardVersion > 0 && dashboardVersion >= MinimumVersion && dashboardVersion <= MaximumVersion;
}

/// <summary>
/// A non-arbitrating console conclusion derived from all positive legacy console-identification rules.
/// </summary>
public sealed record NandConsoleEvidence
{
    internal NandConsoleEvidence(
        NandEvidenceResolution resolution,
        ConsoleDefinition? console,
        ImmutableArray<ConsoleDefinition> candidates)
    {
        if (candidates.IsDefault)
        {
            candidates = ImmutableArray<ConsoleDefinition>.Empty;
        }

        EnsureDistinctCandidates(candidates);
        switch (resolution)
        {
            case NandEvidenceResolution.Absent:
            case NandEvidenceResolution.Conflicting:
                if (console is not null || !candidates.IsEmpty)
                {
                    throw new ArgumentException(
                        "Absent or conflicting console evidence cannot expose a selected console or candidates.",
                        nameof(candidates));
                }

                break;

            case NandEvidenceResolution.Confirmed:
                if (console is null || candidates.Length != 1 || candidates[0].Id != console.Id)
                {
                    throw new ArgumentException(
                        "Confirmed console evidence must contain exactly the selected console.",
                        nameof(candidates));
                }

                break;

            case NandEvidenceResolution.Ambiguous:
                if (console is not null || candidates.Length < 2)
                {
                    throw new ArgumentException(
                        "Ambiguous console evidence must retain at least two candidates and no selected console.",
                        nameof(candidates));
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(resolution), resolution, "The evidence resolution is not supported.");
        }

        Resolution = resolution;
        Console = console;
        Candidates = candidates;
    }

    /// <summary>
    /// Gets the resolution of the console evidence.
    /// </summary>
    public NandEvidenceResolution Resolution { get; }

    /// <summary>
    /// Gets the selected canonical console only when <see cref="Resolution"/> is <see cref="NandEvidenceResolution.Confirmed"/>.
    /// </summary>
    public ConsoleDefinition? Console { get; }

    /// <summary>
    /// Gets the remaining candidate consoles. This contains one item for confirmed evidence, multiple
    /// items for ambiguous evidence, and no items when evidence is absent or conflicting.
    /// </summary>
    public ImmutableArray<ConsoleDefinition> Candidates { get; }

    /// <summary>
    /// Gets whether exactly one console is safe to assert.
    /// </summary>
    public bool IsConfirmed => Resolution == NandEvidenceResolution.Confirmed;

    private static void EnsureDistinctCandidates(ImmutableArray<ConsoleDefinition> candidates)
    {
        var ids = new HashSet<ConsoleId>();
        foreach (ConsoleDefinition candidate in candidates)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            if (!ids.Add(candidate.Id))
            {
                throw new ArgumentException("Console evidence cannot contain duplicate candidates.", nameof(candidates));
            }
        }
    }
}

/// <summary>
/// Safe kernel/dashboard evidence extracted from the inspected NAND bootloader metadata.
/// </summary>
/// <remarks>
/// Exact dashboard evidence is available only from the big-endian CF target version at header +0x14
/// in complete, ordered CF/CG pairs reached through the NAND header's declared update slots. Every
/// active slot must have one valid target version and all targets must agree. Generic stage builds,
/// CE builds, CB compatibility ranges, and the flash-header build are never installed-dashboard proof.
/// </remarks>
public sealed record NandKernelDashboardEvidence
{
    internal NandKernelDashboardEvidence(
        NandEvidenceResolution resolution,
        ImmutableArray<NandDashboardVersionRange> compatibleVersionRanges,
        int? kernelBootloaderBuild,
        int? exactDashboardVersion = null)
    {
        if (compatibleVersionRanges.IsDefault)
        {
            compatibleVersionRanges = ImmutableArray<NandDashboardVersionRange>.Empty;
        }

        EnsureDistinctRanges(compatibleVersionRanges);
        if (exactDashboardVersion is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(exactDashboardVersion),
                "An exact dashboard version must be positive when supplied.");
        }

        if (kernelBootloaderBuild is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(kernelBootloaderBuild),
                "A kernel bootloader build cannot be negative.");
        }

        switch (resolution)
        {
            case NandEvidenceResolution.Absent:
            case NandEvidenceResolution.Conflicting:
                if (!compatibleVersionRanges.IsEmpty || exactDashboardVersion.HasValue)
                {
                    throw new ArgumentException(
                        "Absent or conflicting dashboard evidence cannot expose compatibility or exact-version values.",
                        nameof(compatibleVersionRanges));
                }

                break;

            case NandEvidenceResolution.Confirmed:
                if (compatibleVersionRanges.IsEmpty && !exactDashboardVersion.HasValue)
                {
                    throw new ArgumentException(
                        "Confirmed dashboard evidence must contain compatibility or an exact dashboard version.",
                        nameof(compatibleVersionRanges));
                }

                break;

            case NandEvidenceResolution.Ambiguous:
                if (compatibleVersionRanges.IsEmpty || exactDashboardVersion.HasValue)
                {
                    throw new ArgumentException(
                        "Ambiguous dashboard evidence must retain compatibility ranges and no exact version.",
                        nameof(compatibleVersionRanges));
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(resolution), resolution, "The evidence resolution is not supported.");
        }

        Resolution = resolution;
        CompatibleVersionRanges = compatibleVersionRanges;
        KernelBootloaderBuild = kernelBootloaderBuild;
        ExactDashboardVersion = exactDashboardVersion;
    }

    /// <summary>
    /// Gets the resolution of the dashboard evidence.
    /// </summary>
    public NandEvidenceResolution Resolution { get; }

    /// <summary>
    /// Gets the legacy CB-compatible dashboard ranges. These ranges are not proof of the installed
    /// dashboard version and cannot, by themselves, validate a requested XeBuild dashboard.
    /// </summary>
    public ImmutableArray<NandDashboardVersionRange> CompatibleVersionRanges { get; }

    /// <summary>
    /// Gets the parsed CE kernel-bootloader header build, when that stage was present. This is
    /// bootloader metadata and must not be treated as the installed dashboard version.
    /// </summary>
    public int? KernelBootloaderBuild { get; }

    /// <summary>
    /// Gets the exact CF target version when all active declared CF/CG slots are structurally complete
    /// and agree. It is <see langword="null"/> for missing, malformed, incomplete, or conflicting evidence.
    /// </summary>
    public int? ExactDashboardVersion { get; }

    /// <summary>
    /// Gets whether an exact dashboard version is available for fail-closed output validation.
    /// </summary>
    public bool HasExactDashboardVersion => ExactDashboardVersion.HasValue;

    private static void EnsureDistinctRanges(ImmutableArray<NandDashboardVersionRange> ranges)
    {
        var distinct = new HashSet<NandDashboardVersionRange>();
        foreach (NandDashboardVersionRange range in ranges)
        {
            ArgumentNullException.ThrowIfNull(range);
            if (!distinct.Add(range))
            {
                throw new ArgumentException("Dashboard evidence cannot contain duplicate compatibility ranges.", nameof(ranges));
            }
        }
    }
}

/// <summary>
/// A non-arbitrating image-family conclusion with scalar provenance from direct output evidence.
/// </summary>
/// <remarks>
/// Compatibility tables, generic stage metadata, SMC markers, virtual fuses, and request metadata
/// cannot prove an image family. Confirmed conclusions still require validation against a reviewed
/// decrypted-stage fingerprint manifest.
/// </remarks>
public sealed record NandImageFamilyEvidence
{
    internal NandImageFamilyEvidence(
        NandEvidenceResolution resolution,
        NandImageFamily? family,
        ImmutableArray<NandImageFamily> candidates,
        ImmutableArray<NandDirectOutputEvidenceProvenance> directEvidence = default)
    {
        if (candidates.IsDefault)
        {
            candidates = ImmutableArray<NandImageFamily>.Empty;
        }

        if (directEvidence.IsDefault)
        {
            directEvidence = ImmutableArray<NandDirectOutputEvidenceProvenance>.Empty;
        }

        EnsureDistinctCandidates(candidates);
        foreach (NandDirectOutputEvidenceProvenance provenance in directEvidence)
        {
            ArgumentNullException.ThrowIfNull(provenance);
        }

        switch (resolution)
        {
            case NandEvidenceResolution.Unavailable:
                if (!directEvidence.IsEmpty)
                {
                    throw new ArgumentException(
                        "Unavailable image-family evidence cannot expose direct evidence.",
                        nameof(directEvidence));
                }

                goto case NandEvidenceResolution.Absent;

            case NandEvidenceResolution.Absent:
            case NandEvidenceResolution.Conflicting:
                if (family.HasValue || !candidates.IsEmpty)
                {
                    throw new ArgumentException(
                        "Unavailable, absent, or conflicting image-family evidence cannot expose a selected family or candidates.",
                        nameof(candidates));
                }

                break;

            case NandEvidenceResolution.Ambiguous:
                if (family.HasValue || candidates.Length < 2)
                {
                    throw new ArgumentException(
                        "Ambiguous image-family evidence must retain at least two candidate families and no selected family.",
                        nameof(candidates));
                }

                break;

            case NandEvidenceResolution.Confirmed:
                if (!family.HasValue || candidates.Length != 1 || candidates[0] != family.Value)
                {
                    throw new ArgumentException(
                        "Confirmed image-family evidence must contain exactly the recognized selected family.",
                        nameof(candidates));
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(resolution), resolution, "The evidence resolution is not supported.");
        }

        Resolution = resolution;
        Family = family;
        Candidates = candidates;
        DirectEvidence = directEvidence;
    }

    /// <summary>
    /// Gets the unavailable conclusion used when no reviewed direct-output evidence policy exists.
    /// </summary>
    public static NandImageFamilyEvidence Unavailable { get; } =
        new(NandEvidenceResolution.Unavailable, family: null, ImmutableArray<NandImageFamily>.Empty);

    /// <summary>
    /// Gets the resolution of the image-family evidence.
    /// </summary>
    public NandEvidenceResolution Resolution { get; }

    /// <summary>
    /// Gets the selected family only when <see cref="Resolution"/> is <see cref="NandEvidenceResolution.Confirmed"/>.
    /// </summary>
    public NandImageFamily? Family { get; }

    /// <summary>
    /// Gets the remaining image-family candidates, without arbitrating ambiguous or conflicting evidence.
    /// </summary>
    public ImmutableArray<NandImageFamily> Candidates { get; }

    /// <summary>
    /// Gets scalar provenance for direct evidence. These records retain no stage bytes or keys.
    /// </summary>
    public ImmutableArray<NandDirectOutputEvidenceProvenance> DirectEvidence { get; }

    /// <summary>
    /// Gets whether exactly one family was selected. Trusted provenance must be checked separately.
    /// </summary>
    public bool IsConfirmed => Resolution == NandEvidenceResolution.Confirmed;

    private static void EnsureDistinctCandidates(ImmutableArray<NandImageFamily> candidates)
    {
        for (int index = 0; index < candidates.Length; index++)
        {
            NandImageFamily candidate = candidates[index];
            if (!Enum.IsDefined(candidate))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(candidates),
                    candidate,
                    "Image-family candidates must be recognized families.");
            }

            for (int previous = 0; previous < index; previous++)
            {
                if (candidates[previous] == candidate)
                {
                    throw new ArgumentException("Image-family evidence cannot contain duplicate candidates.", nameof(candidates));
                }
            }
        }
    }
}

/// <summary>
/// Aggregates semantic facts that can be used to validate an inspected NAND image against a build request.
/// </summary>
public sealed record NandSemanticEvidence
{
    internal NandSemanticEvidence(
        NandConsoleEvidence console,
        NandKernelDashboardEvidence kernelDashboard,
        NandVirtualFuseEvidence virtualFuses,
        NandImageFamilyEvidence imageFamily,
        NandHackTypeEvidence? hack = null)
    {
        Console = console ?? throw new ArgumentNullException(nameof(console));
        KernelDashboard = kernelDashboard ?? throw new ArgumentNullException(nameof(kernelDashboard));
        VirtualFuses = virtualFuses ?? throw new ArgumentNullException(nameof(virtualFuses));
        ImageFamily = imageFamily ?? throw new ArgumentNullException(nameof(imageFamily));
        Hack = hack ?? NandHackTypeEvidence.Absent;
    }

    /// <summary>
    /// Gets the non-arbitrating console evidence.
    /// </summary>
    public NandConsoleEvidence Console { get; }

    /// <summary>
    /// Gets the kernel/dashboard compatibility and exact-version evidence.
    /// </summary>
    public NandKernelDashboardEvidence KernelDashboard { get; }

    /// <summary>
    /// Gets the legacy virtual-fuse marker diagnostic, which does not prove an image family.
    /// </summary>
    public NandVirtualFuseEvidence VirtualFuses { get; }

    /// <summary>
    /// Gets the non-arbitrating image-family evidence.
    /// </summary>
    public NandImageFamilyEvidence ImageFamily { get; }

    /// <summary>
    /// Gets the retained legacy compatibility diagnostics. These non-exhaustive labels cannot prove an output family.
    /// </summary>
    public NandHackTypeEvidence Hack { get; }
}

/// <summary>
/// Describes semantic facts a caller expects an inspected NAND image to prove.
/// </summary>
public sealed record NandSemanticEvidenceRequirement
{
    /// <summary>
    /// Creates a semantic NAND evidence requirement.
    /// </summary>
    /// <param name="console">The required canonical console.</param>
    /// <param name="dashboardVersion">
    /// The required exact dashboard version, or <see langword="null"/> when a caller intentionally
    /// does not require exact dashboard proof.
    /// </param>
    /// <param name="imageFamily">The required directly proven image family.</param>
    /// <param name="reviewedManifest">The reviewed direct-evidence policy, or <see langword="null"/> when unavailable.</param>
    public NandSemanticEvidenceRequirement(
        ConsoleId console,
        int? dashboardVersion,
        NandImageFamily imageFamily,
        NandDirectOutputEvidenceManifest? reviewedManifest = null)
    {
        if (!ConsoleCatalog.TryGet(console, out _))
        {
            throw new ArgumentOutOfRangeException(nameof(console), console, "The console is not supported.");
        }

        if (dashboardVersion is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dashboardVersion),
                "A required dashboard version must be positive when supplied.");
        }

        if (!Enum.IsDefined(imageFamily))
        {
            throw new ArgumentOutOfRangeException(nameof(imageFamily), imageFamily, "The required image family must be recognized.");
        }

        Console = console;
        DashboardVersion = dashboardVersion;
        ImageFamily = imageFamily;
        ReviewedManifest = reviewedManifest;
    }

    /// <summary>
    /// Gets the required canonical console.
    /// </summary>
    public ConsoleId Console { get; }

    /// <summary>
    /// Gets the required exact dashboard version, if one is required.
    /// </summary>
    public int? DashboardVersion { get; }

    /// <summary>
    /// Gets the required directly proven image family.
    /// </summary>
    public NandImageFamily ImageFamily { get; }

    /// <summary>
    /// Gets the reviewed direct-evidence policy, if one is available.
    /// </summary>
    public NandDirectOutputEvidenceManifest? ReviewedManifest { get; }
}

/// <summary>
/// Identifies why a semantic NAND evidence requirement did not match.
/// </summary>
public enum NandSemanticEvidenceMismatchKind
{
    /// <summary>Console evidence is absent.</summary>
    ConsoleAbsent,

    /// <summary>Console evidence is ambiguous.</summary>
    ConsoleAmbiguous,

    /// <summary>Console evidence conflicts.</summary>
    ConsoleConflicting,

    /// <summary>The confirmed console differs from the requested console.</summary>
    ConsoleMismatch,

    /// <summary>Dashboard evidence is absent.</summary>
    DashboardAbsent,

    /// <summary>Dashboard evidence is ambiguous.</summary>
    DashboardAmbiguous,

    /// <summary>Dashboard evidence conflicts.</summary>
    DashboardConflicting,

    /// <summary>No exact dashboard version was parsed, so compatibility ranges cannot verify the request.</summary>
    DashboardNotExact,

    /// <summary>The exact parsed dashboard differs from the requested dashboard.</summary>
    DashboardMismatch,

    /// <summary>The required reviewed image-family policy or direct matching observation is unavailable.</summary>
    ImageFamilyEvidenceUnavailable,

    /// <summary>A selected image family lacks complete, trusted direct-evidence provenance.</summary>
    ImageFamilyEvidenceUntrusted,

    /// <summary>Image-family evidence is absent.</summary>
    ImageFamilyAbsent,

    /// <summary>Image-family evidence is ambiguous.</summary>
    ImageFamilyAmbiguous,

    /// <summary>Image-family evidence conflicts.</summary>
    ImageFamilyConflicting,

    /// <summary>The trusted confirmed image family differs from the requested family.</summary>
    ImageFamilyMismatch,
}

/// <summary>
/// One fail-closed semantic-evidence mismatch.
/// </summary>
public sealed record NandSemanticEvidenceMismatch
{
    internal NandSemanticEvidenceMismatch(NandSemanticEvidenceMismatchKind kind)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "The evidence mismatch kind is not supported.");
        }

        Kind = kind;
    }

    /// <summary>
    /// Gets the stable mismatch kind.
    /// </summary>
    public NandSemanticEvidenceMismatchKind Kind { get; }
}

/// <summary>
/// The immutable result of checking semantic NAND evidence against a requirement.
/// </summary>
public sealed record NandSemanticEvidenceValidationResult
{
    internal NandSemanticEvidenceValidationResult(ImmutableArray<NandSemanticEvidenceMismatch> mismatches)
    {
        if (mismatches.IsDefault)
        {
            mismatches = ImmutableArray<NandSemanticEvidenceMismatch>.Empty;
        }

        foreach (NandSemanticEvidenceMismatch mismatch in mismatches)
        {
            ArgumentNullException.ThrowIfNull(mismatch);
        }

        Mismatches = mismatches;
    }

    /// <summary>
    /// Gets whether every required fact was exactly verified.
    /// </summary>
    public bool IsMatch => Mismatches.IsEmpty;

    /// <summary>
    /// Gets every independent reason the image cannot satisfy the requirement.
    /// </summary>
    public ImmutableArray<NandSemanticEvidenceMismatch> Mismatches { get; }
}
