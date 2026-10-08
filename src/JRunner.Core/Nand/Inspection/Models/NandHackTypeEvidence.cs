using System.Collections.Immutable;
using JRunner.Core.Nand.Hacks;

namespace JRunner.Core.Nand.Inspection.Models;

/// <summary>
/// Legacy hack-compatibility diagnostics retained for existing inspection consumers.
/// </summary>
/// <remarks>
/// Candidate labels are non-exhaustive legacy compatibility preferences, not observed output-family
/// alternatives. Even one unreviewed label remains ambiguous and cannot prove an image family.
/// Output validation uses <see cref="NandSemanticEvidence.ImageFamily"/>, never this diagnostic.
/// </remarks>
public sealed record NandHackTypeEvidence
{
    internal NandHackTypeEvidence(
        NandEvidenceResolution resolution,
        NandHackType? hackType,
        ImmutableArray<NandHackType> candidates)
    {
        if (candidates.IsDefault)
        {
            candidates = ImmutableArray<NandHackType>.Empty;
        }

        for (int index = 0; index < candidates.Length; index++)
        {
            NandHackType candidate = candidates[index];
            if (candidate == NandHackType.Unknown || !Enum.IsDefined(candidate))
            {
                throw new ArgumentOutOfRangeException(nameof(candidates), candidate, "Compatibility labels must be recognized hack types.");
            }

            for (int previous = 0; previous < index; previous++)
            {
                if (candidates[previous] == candidate)
                {
                    throw new ArgumentException("Hack compatibility evidence cannot contain duplicate labels.", nameof(candidates));
                }
            }
        }

        switch (resolution)
        {
            case NandEvidenceResolution.Absent:
            case NandEvidenceResolution.Conflicting:
                if (hackType.HasValue || !candidates.IsEmpty)
                {
                    throw new ArgumentException(
                        "Absent or conflicting compatibility evidence cannot expose a selected type or labels.",
                        nameof(candidates));
                }

                break;

            case NandEvidenceResolution.Ambiguous:
                if (hackType.HasValue || candidates.IsEmpty)
                {
                    throw new ArgumentException(
                        "Ambiguous compatibility evidence must retain at least one label and no selected type.",
                        nameof(candidates));
                }

                break;

            case NandEvidenceResolution.Confirmed:
                if (hackType is null or NandHackType.Unknown || candidates.Length != 1 || candidates[0] != hackType.Value)
                {
                    throw new ArgumentException(
                        "Confirmed compatibility evidence must contain exactly the recognized selected legacy type.",
                        nameof(candidates));
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(resolution), resolution, "The compatibility evidence resolution is not supported.");
        }

        Resolution = resolution;
        HackType = hackType;
        Candidates = candidates;
    }

    internal static NandHackTypeEvidence Absent { get; } = new(NandEvidenceResolution.Absent, null, default);

    /// <summary>Gets the resolution of the legacy compatibility diagnostics, not direct output-family proof.</summary>
    public NandEvidenceResolution Resolution { get; }

    /// <summary>Gets a legacy selected type only for confirmed compatibility evidence, never output-family proof.</summary>
    public NandHackType? HackType { get; }

    /// <summary>Gets non-exhaustive legacy compatibility labels, not observed output-family alternatives.</summary>
    public ImmutableArray<NandHackType> Candidates { get; }

    /// <summary>Gets whether the legacy diagnostic selected one type. This does not confirm an image family.</summary>
    public bool IsConfirmed => Resolution == NandEvidenceResolution.Confirmed;
}
