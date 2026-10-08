using System.Collections.Immutable;
using JRunner.Core.Nand.Hacks;
using JRunner.Core.Nand.Inspection.Models;
using JRunner.Core.Nand.Models;

namespace JRunner.Core.Nand.Inspection;

/// <summary>
/// Resolves non-arbitrating semantic NAND evidence from facts already parsed by the inspection pipeline.
/// </summary>
/// <remarks>
/// This resolver retains inspected CF target-version evidence, legacy console scoring, CB/SB dashboard
/// compatibility ranges, and compatibility diagnostics. Direct image-family evidence is supplied only
/// by the inspection pipeline's reviewed decrypted-payload matcher; structural metadata and legacy
/// preferences cannot infer a family. Callers with no matching observation remain unavailable.
/// </remarks>
internal static class NandSemanticEvidenceService
{
    internal static NandSemanticEvidence Create(
        ImmutableArray<NandBootloaderStage> bootloaderStages,
        ConsoleIdentificationResult consoleIdentification,
        NandHackEvidence hackEvidence,
        NandVirtualFuseEvidence virtualFuses,
        NandDashboardBuildEvidence dashboardBuild,
        NandImageFamilyEvidence? imageFamily = null)
    {
        ArgumentNullException.ThrowIfNull(consoleIdentification);
        ArgumentNullException.ThrowIfNull(hackEvidence);
        ArgumentNullException.ThrowIfNull(virtualFuses);

        if (bootloaderStages.IsDefault)
        {
            bootloaderStages = ImmutableArray<NandBootloaderStage>.Empty;
        }

        int? kernelBootloaderBuild = FindKernelBootloaderBuild(bootloaderStages);
        NandConsoleEvidence console = ResolveConsole(consoleIdentification);
        NandKernelDashboardEvidence kernelDashboard = ResolveKernelDashboard(
            console,
            hackEvidence,
            kernelBootloaderBuild,
            dashboardBuild);
        return new NandSemanticEvidence(
            console,
            kernelDashboard,
            virtualFuses,
            imageFamily ?? NandImageFamilyEvidence.Unavailable,
            ResolveHackCompatibility(hackEvidence));
    }

    private static NandConsoleEvidence ResolveConsole(ConsoleIdentificationResult consoleIdentification)
    {
        var intersectedCandidateIds = new HashSet<ConsoleId>();
        var hasPositiveRule = false;
        foreach (ConsoleScoreEvidence scoreEvidence in consoleIdentification.Evidence)
        {
            if (scoreEvidence.PointsPerAffectedConsole <= 0 || scoreEvidence.AffectedConsoleIds.IsDefaultOrEmpty)
            {
                continue;
            }

            if (!hasPositiveRule)
            {
                foreach (ConsoleId candidate in scoreEvidence.AffectedConsoleIds)
                {
                    intersectedCandidateIds.Add(candidate);
                }

                hasPositiveRule = true;
                continue;
            }

            intersectedCandidateIds.IntersectWith(scoreEvidence.AffectedConsoleIds);
        }

        if (!hasPositiveRule)
        {
            return new NandConsoleEvidence(
                NandEvidenceResolution.Absent,
                console: null,
                ImmutableArray<ConsoleDefinition>.Empty);
        }

        if (intersectedCandidateIds.Count == 0)
        {
            return new NandConsoleEvidence(
                NandEvidenceResolution.Conflicting,
                console: null,
                ImmutableArray<ConsoleDefinition>.Empty);
        }

        var candidates = new List<ConsoleDefinition>(intersectedCandidateIds.Count);
        foreach (ConsoleId candidateId in intersectedCandidateIds)
        {
            candidates.Add(ConsoleCatalog.Get(candidateId));
        }

        candidates.Sort(static (left, right) => left.LegacyId.CompareTo(right.LegacyId));
        ImmutableArray<ConsoleDefinition> immutableCandidates = ImmutableArray.CreateRange(candidates);
        if (immutableCandidates.Length == 1)
        {
            return new NandConsoleEvidence(
                NandEvidenceResolution.Confirmed,
                immutableCandidates[0],
                immutableCandidates);
        }

        return new NandConsoleEvidence(
            NandEvidenceResolution.Ambiguous,
            console: null,
            immutableCandidates);
    }

    private static NandKernelDashboardEvidence ResolveKernelDashboard(
        NandConsoleEvidence console,
        NandHackEvidence hackEvidence,
        int? kernelBootloaderBuild,
        NandDashboardBuildEvidence dashboardBuild)
    {
        int? selectedCbBuild = hackEvidence.CbXBuild is > 0
            ? hackEvidence.CbBBuild
            : hackEvidence.CbABuild;
        if (selectedCbBuild is not > 0)
        {
            return WithoutCompatibilityEvidence(kernelBootloaderBuild, dashboardBuild);
        }

        ImmutableArray<LegacyBootloaderMapping> allMappings = LegacyBootloaderCatalog.FindAll(selectedCbBuild.Value);
        if (allMappings.IsDefaultOrEmpty)
        {
            return WithoutCompatibilityEvidence(kernelBootloaderBuild, dashboardBuild);
        }

        if (console.Resolution == NandEvidenceResolution.Conflicting)
        {
            return WithoutCompatibilityEvidence(kernelBootloaderBuild, dashboardBuild);
        }

        var matchingMappings = new List<LegacyBootloaderMapping>(allMappings.Length);
        var everyConsoleCandidateHasMapping = true;
        switch (console.Resolution)
        {
            case NandEvidenceResolution.Confirmed:
                AddMappingsForConsole(allMappings, console.Console!, matchingMappings);
                if (matchingMappings.Count == 0)
                {
                    return WithoutCompatibilityEvidence(kernelBootloaderBuild, dashboardBuild);
                }

                break;

            case NandEvidenceResolution.Ambiguous:
                foreach (ConsoleDefinition candidate in console.Candidates)
                {
                    var candidateMappings = new List<LegacyBootloaderMapping>();
                    AddMappingsForConsole(allMappings, candidate, candidateMappings);
                    if (candidateMappings.Count == 0)
                    {
                        everyConsoleCandidateHasMapping = false;
                        continue;
                    }

                    foreach (LegacyBootloaderMapping mapping in candidateMappings)
                    {
                        AddDistinctMapping(matchingMappings, mapping);
                    }
                }

                if (matchingMappings.Count == 0)
                {
                    return WithoutCompatibilityEvidence(kernelBootloaderBuild, dashboardBuild);
                }

                break;

            case NandEvidenceResolution.Absent:
                matchingMappings.AddRange(allMappings);
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(console),
                    console.Resolution,
                    "The console evidence resolution is not supported.");
        }

        ImmutableArray<NandDashboardVersionRange> ranges = BuildDistinctRanges(matchingMappings);
        if (dashboardBuild.Resolution == NandEvidenceResolution.Conflicting)
        {
            return ConflictingDashboardEvidence(kernelBootloaderBuild);
        }

        if (dashboardBuild.Build.HasValue)
        {
            return new NandKernelDashboardEvidence(
                NandEvidenceResolution.Confirmed,
                ranges,
                kernelBootloaderBuild,
                dashboardBuild.Build);
        }

        NandEvidenceResolution resolution =
            console.Resolution == NandEvidenceResolution.Ambiguous && !everyConsoleCandidateHasMapping
                ? NandEvidenceResolution.Ambiguous
                : ranges.Length == 1
                    ? NandEvidenceResolution.Confirmed
                    : NandEvidenceResolution.Ambiguous;
        return new NandKernelDashboardEvidence(resolution, ranges, kernelBootloaderBuild);
    }

    private static NandHackTypeEvidence ResolveHackCompatibility(NandHackEvidence hackEvidence)
    {
        NandHackType preferredHack = hackEvidence.PreferredHack;
        if (preferredHack == NandHackType.Unknown)
        {
            return NandHackTypeEvidence.Absent;
        }

        ImmutableArray<NandHackType> labels = preferredHack == NandHackType.Glitch2
            ? ImmutableArray.Create(NandHackType.Glitch2, NandHackType.Glitch2m)
            : ImmutableArray.Create(preferredHack);
        return new NandHackTypeEvidence(NandEvidenceResolution.Ambiguous, hackType: null, labels);
    }

    private static NandKernelDashboardEvidence WithoutCompatibilityEvidence(
        int? kernelBootloaderBuild,
        NandDashboardBuildEvidence dashboardBuild)
    {
        return dashboardBuild.Resolution switch
        {
            NandEvidenceResolution.Confirmed => new NandKernelDashboardEvidence(
                NandEvidenceResolution.Confirmed,
                ImmutableArray<NandDashboardVersionRange>.Empty,
                kernelBootloaderBuild,
                dashboardBuild.Build),
            NandEvidenceResolution.Conflicting => ConflictingDashboardEvidence(kernelBootloaderBuild),
            NandEvidenceResolution.Absent => AbsentDashboardEvidence(kernelBootloaderBuild),
            _ => throw new ArgumentOutOfRangeException(
                nameof(dashboardBuild),
                dashboardBuild.Resolution,
                "The dashboard-build evidence resolution is not supported."),
        };
    }

    private static NandKernelDashboardEvidence AbsentDashboardEvidence(int? kernelBootloaderBuild) =>
        new(
            NandEvidenceResolution.Absent,
            ImmutableArray<NandDashboardVersionRange>.Empty,
            kernelBootloaderBuild);

    private static NandKernelDashboardEvidence ConflictingDashboardEvidence(int? kernelBootloaderBuild) =>
        new(
            NandEvidenceResolution.Conflicting,
            ImmutableArray<NandDashboardVersionRange>.Empty,
            kernelBootloaderBuild);

    private static int? FindKernelBootloaderBuild(ImmutableArray<NandBootloaderStage> bootloaderStages)
    {
        foreach (NandBootloaderStage stage in bootloaderStages)
        {
            if (stage.Kind == NandBootloaderStageKind.CE)
            {
                return stage.Build;
            }
        }

        return null;
    }

    private static void AddMappingsForConsole(
        ImmutableArray<LegacyBootloaderMapping> mappings,
        ConsoleDefinition console,
        List<LegacyBootloaderMapping> destination)
    {
        foreach (LegacyBootloaderMapping mapping in mappings)
        {
            ConsoleDefinition mappedConsole = ConsoleCatalog.Get(mapping.ConsoleId);
            if (string.Equals(mappedConsole.IniName, console.IniName, StringComparison.Ordinal))
            {
                AddDistinctMapping(destination, mapping);
            }
        }
    }

    private static void AddDistinctMapping(List<LegacyBootloaderMapping> destination, LegacyBootloaderMapping mapping)
    {
        if (!destination.Contains(mapping))
        {
            destination.Add(mapping);
        }
    }

    private static ImmutableArray<NandDashboardVersionRange> BuildDistinctRanges(
        List<LegacyBootloaderMapping> mappings)
    {
        var ranges = new List<NandDashboardVersionRange>(mappings.Count);
        foreach (LegacyBootloaderMapping mapping in mappings)
        {
            var range = new NandDashboardVersionRange(
                mapping.MinimumDashboardVersion,
                mapping.MaximumDashboardVersion);
            if (!ranges.Contains(range))
            {
                ranges.Add(range);
            }
        }

        ranges.Sort(static (left, right) =>
        {
            int minimumComparison = left.MinimumVersion.CompareTo(right.MinimumVersion);
            return minimumComparison != 0
                ? minimumComparison
                : left.MaximumVersion.CompareTo(right.MaximumVersion);
        });
        return ImmutableArray.CreateRange(ranges);
    }

}

/// <summary>
/// Carries only the resolved target version from bounded, structurally inspected CF/CG update slots.
/// The default value denotes absent exact-dashboard evidence.
/// </summary>
internal readonly record struct NandDashboardBuildEvidence(
    NandEvidenceResolution Resolution,
    int? Build);
