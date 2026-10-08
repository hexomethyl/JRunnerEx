using System.Collections.Immutable;
using JRunner.Core.Nand.Physical;

namespace JRunner.Core.Nand.Models;

/// <summary>
/// Identifies a source of evidence used by the legacy console-identification score rules.
/// </summary>
public enum ConsoleEvidenceKind
{
    /// <summary>A CB build number.</summary>
    CbBuild,

    /// <summary>An SMC type nibble.</summary>
    SmcType,

    /// <summary>A NAND flash configuration value.</summary>
    FlashConfiguration,

    /// <summary>The raw source-image byte length.</summary>
    RawNandLength,

    /// <summary>Whether the source image includes NAND spare data.</summary>
    SpareData,

    /// <summary>A detected legacy NAND spare layout.</summary>
    Layout,
}

/// <summary>
/// Immutable facts available while identifying an Xbox 360 console from a NAND image.
/// </summary>
public sealed record ConsoleIdentificationEvidence
{
    /// <summary>
    /// Creates console-identification evidence.
    /// </summary>
    /// <param name="cbBuild">
    /// The CB build selected by legacy <c>Nand.identifyConsole</c>: CB_B when CB_X is present,
    /// otherwise CB_A.
    /// </param>
    /// <param name="cbBBuild">
    /// The CB_B build. It is only needed for the legacy Falcon-versus-Xenon CB disambiguation rule.
    /// </param>
    /// <param name="smcType">The SMC type nibble, from zero through fifteen.</param>
    /// <param name="rawNandLength">The raw source-image length in bytes.</param>
    /// <param name="layout">The detected legacy spare layout, when spare data is available.</param>
    /// <param name="flashConfiguration">The numeric value represented by the legacy eight-digit flash-config string.</param>
    /// <param name="hasSpareData">Whether the image has NAND spare data. A detected <paramref name="layout"/> implies <see langword="true"/>.</param>
    public ConsoleIdentificationEvidence(
        int? cbBuild = null,
        int? cbBBuild = null,
        int? smcType = null,
        long? rawNandLength = null,
        NandLegacyLayout? layout = null,
        uint? flashConfiguration = null,
        bool? hasSpareData = null)
    {
        ValidateNonNegative(cbBuild, nameof(cbBuild));
        ValidateNonNegative(cbBBuild, nameof(cbBBuild));

        if (smcType is < 0 or > 0xF)
        {
            throw new ArgumentOutOfRangeException(nameof(smcType), "The SMC type must be a four-bit value.");
        }

        if (rawNandLength is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rawNandLength), "The raw NAND length cannot be negative.");
        }

        if (layout.HasValue &&
            layout.Value is not (
                NandLegacyLayout.Layout0 or
                NandLegacyLayout.Layout1 or
                NandLegacyLayout.Layout2))
        {
            throw new ArgumentOutOfRangeException(nameof(layout), layout, "The NAND layout is not supported.");
        }

        if (hasSpareData is false && layout.HasValue)
        {
            throw new ArgumentException("A NAND layout cannot be supplied when spare data is absent.", nameof(layout));
        }

        CbBuild = cbBuild;
        CbBBuild = cbBBuild;
        SmcType = smcType;
        RawNandLength = rawNandLength;
        Layout = layout;
        FlashConfiguration = flashConfiguration;
        HasSpareData = hasSpareData;
    }

    /// <summary>
    /// Gets the CB build selected by the legacy identifier.
    /// </summary>
    public int? CbBuild { get; }

    /// <summary>
    /// Gets the CB_B build used by the legacy Falcon-versus-Xenon special case.
    /// </summary>
    public int? CbBBuild { get; }

    /// <summary>
    /// Gets the SMC type nibble.
    /// </summary>
    public int? SmcType { get; }

    /// <summary>
    /// Gets the raw source-image length in bytes.
    /// </summary>
    public long? RawNandLength { get; }

    /// <summary>
    /// Gets the detected legacy spare layout.
    /// </summary>
    public NandLegacyLayout? Layout { get; }

    /// <summary>
    /// Gets the numeric flash configuration value.
    /// </summary>
    public uint? FlashConfiguration { get; }

    /// <summary>
    /// Gets whether the image has NAND spare data.
    /// </summary>
    public bool? HasSpareData { get; }

    /// <summary>
    /// Gets the known spare-data state after treating a detected layout as proof that spare data exists.
    /// </summary>
    public bool? EffectiveHasSpareData
    {
        get
        {
            if (HasSpareData.HasValue)
            {
                return HasSpareData.Value;
            }

            return Layout.HasValue ? true : null;
        }
    }

    /// <summary>
    /// Creates evidence from the CB fields used by legacy <c>Nand.identifyConsole</c>.
    /// </summary>
    /// <remarks>
    /// The returned <see cref="CbBuild"/> is CB_B when <paramref name="cbXBuild"/> is positive;
    /// otherwise it is CB_A, exactly matching the legacy selection rule.
    /// </remarks>
    public static ConsoleIdentificationEvidence FromLegacyBootloaders(
        int cbABuild,
        int cbBBuild,
        int cbXBuild,
        int? smcType = null,
        long? rawNandLength = null,
        NandLegacyLayout? layout = null,
        uint? flashConfiguration = null,
        bool? hasSpareData = null)
    {
        ValidateNonNegative(cbABuild, nameof(cbABuild));
        ValidateNonNegative(cbBBuild, nameof(cbBBuild));
        ValidateNonNegative(cbXBuild, nameof(cbXBuild));

        return new ConsoleIdentificationEvidence(
            cbXBuild > 0 ? cbBBuild : cbABuild,
            cbBBuild,
            smcType,
            rawNandLength,
            layout,
            flashConfiguration,
            hasSpareData);
    }

    private static void ValidateNonNegative(int? value, string parameterName)
    {
        if (value is < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "The bootloader build cannot be negative.");
        }
    }

    private static void ValidateNonNegative(int value, string parameterName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "The bootloader build cannot be negative.");
        }
    }
}

/// <summary>
/// One rule contribution recorded while scoring console candidates.
/// </summary>
public sealed record ConsoleScoreEvidence
{
    internal ConsoleScoreEvidence(
        ConsoleEvidenceKind kind,
        int pointsPerAffectedConsole,
        ImmutableArray<ConsoleId> affectedConsoleIds)
    {
        Kind = kind;
        PointsPerAffectedConsole = pointsPerAffectedConsole;
        AffectedConsoleIds = affectedConsoleIds;
    }

    /// <summary>
    /// Gets the source evidence evaluated by the rule.
    /// </summary>
    public ConsoleEvidenceKind Kind { get; }

    /// <summary>
    /// Gets the score added to each affected console.
    /// A value of zero means that the supplied evidence did not match a legacy rule.
    /// </summary>
    public int PointsPerAffectedConsole { get; }

    /// <summary>
    /// Gets every console affected by the matched rule.
    /// </summary>
    public ImmutableArray<ConsoleId> AffectedConsoleIds { get; }

    /// <summary>
    /// Gets whether the evaluated evidence matched a legacy scoring rule.
    /// </summary>
    public bool HasMatch => !AffectedConsoleIds.IsDefaultOrEmpty;
}

/// <summary>
/// One scored canonical console candidate.
/// </summary>
public sealed record ConsoleIdentificationCandidate
{
    internal ConsoleIdentificationCandidate(ConsoleDefinition console, int score)
    {
        Console = console;
        Score = score;
    }

    /// <summary>
    /// Gets the canonical console definition.
    /// </summary>
    public ConsoleDefinition Console { get; }

    /// <summary>
    /// Gets the aggregate legacy evidence score.
    /// </summary>
    public int Score { get; }
}

/// <summary>
/// The complete, ranked result of applying the legacy console-identification rules.
/// </summary>
public sealed record ConsoleIdentificationResult
{
    internal ConsoleIdentificationResult(
        ConsoleIdentificationEvidence input,
        ImmutableArray<ConsoleIdentificationCandidate> candidates,
        ImmutableArray<ConsoleIdentificationCandidate> highestScoringCandidates,
        ImmutableArray<ConsoleScoreEvidence> evidence)
    {
        Input = input;
        Candidates = candidates;
        HighestScoringCandidates = highestScoringCandidates;
        Evidence = evidence;
    }

    /// <summary>
    /// Gets the source facts evaluated by the scorer.
    /// </summary>
    public ConsoleIdentificationEvidence Input { get; }

    /// <summary>
    /// Gets every canonical console, sorted by descending score and then ascending legacy identifier.
    /// </summary>
    public ImmutableArray<ConsoleIdentificationCandidate> Candidates { get; }

    /// <summary>
    /// Gets every candidate tied for the highest score. This is deliberately plural so callers do not silently choose a console.
    /// </summary>
    public ImmutableArray<ConsoleIdentificationCandidate> HighestScoringCandidates { get; }

    /// <summary>
    /// Gets every evaluated evidence source and its matched rule contribution.
    /// </summary>
    public ImmutableArray<ConsoleScoreEvidence> Evidence { get; }

    /// <summary>
    /// Gets the highest aggregate score, or zero when the catalog is empty.
    /// </summary>
    public int HighestScore => Candidates.IsDefaultOrEmpty ? 0 : Candidates[0].Score;

    /// <summary>
    /// Gets whether at least one legacy rule contributed a positive score.
    /// </summary>
    public bool HasPositiveEvidence => HighestScore > 0;

    /// <summary>
    /// Gets whether more than one console shares the highest score.
    /// </summary>
    public bool IsAmbiguous => HighestScoringCandidates.Length > 1;
}

/// <summary>
/// Applies the pure legacy <c>Nand.identifyConsole</c> scoring rules to available NAND evidence.
/// </summary>
public static class ConsoleIdentifier
{
    private static readonly ImmutableArray<ConsoleId> TrinityCandidates = ImmutableArray.Create(
        ConsoleId.Trinity16Mb,
        ConsoleId.TrinityBigBlock);

    private static readonly ImmutableArray<ConsoleId> FalconCandidates = ImmutableArray.Create(
        ConsoleId.Falcon16Mb,
        ConsoleId.Falcon64Mb);

    private static readonly ImmutableArray<ConsoleId> ZephyrCandidates = ImmutableArray.Create(
        ConsoleId.Zephyr16Mb,
        ConsoleId.Zephyr64Mb);

    private static readonly ImmutableArray<ConsoleId> JasperCandidates = ImmutableArray.Create(
        ConsoleId.Jasper16Mb,
        ConsoleId.JasperXsb,
        ConsoleId.JasperBigBlock);

    private static readonly ImmutableArray<ConsoleId> XenonCandidates = ImmutableArray.Create(
        ConsoleId.Xenon64Mb,
        ConsoleId.Xenon16Mb);

    private static readonly ImmutableArray<ConsoleId> CoronaNandCandidates = ImmutableArray.Create(
        ConsoleId.CoronaBigBlock,
        ConsoleId.Corona16Mb);

    private static readonly ImmutableArray<ConsoleId> CoronaCandidates = ImmutableArray.Create(
        ConsoleId.CoronaBigBlock,
        ConsoleId.Corona16Mb,
        ConsoleId.Corona4Gb);

    private static readonly ImmutableArray<ConsoleId> WinchesterNandCandidates = ImmutableArray.Create(
        ConsoleId.Winchester16Mb,
        ConsoleId.WinchesterBigBlock);

    private static readonly ImmutableArray<ConsoleId> WinchesterCandidates = ImmutableArray.Create(
        ConsoleId.Winchester16Mb,
        ConsoleId.Winchester4Gb,
        ConsoleId.WinchesterBigBlock);

    private static readonly ImmutableArray<ConsoleId> EmmcCandidates = ImmutableArray.Create(
        ConsoleId.Corona4Gb,
        ConsoleId.Winchester4Gb);

    private static readonly ImmutableArray<ConsoleId> Corona4GbCandidate = ImmutableArray.Create(ConsoleId.Corona4Gb);

    private static readonly ImmutableArray<ConsoleId> Winchester4GbCandidate = ImmutableArray.Create(ConsoleId.Winchester4Gb);

    private static readonly ImmutableArray<ConsoleId> JasperBigBlockTrinityBigBlockCandidates = ImmutableArray.Create(
        ConsoleId.JasperBigBlock,
        ConsoleId.TrinityBigBlock);

    private static readonly ImmutableArray<ConsoleId> CoronaBigBlockWinchesterBigBlockCandidates = ImmutableArray.Create(
        ConsoleId.CoronaBigBlock,
        ConsoleId.WinchesterBigBlock);

    private static readonly ImmutableArray<ConsoleId> SmallBlockFlashConfigurationCandidates = ImmutableArray.Create(
        ConsoleId.Falcon16Mb,
        ConsoleId.Zephyr16Mb,
        ConsoleId.JasperXsb,
        ConsoleId.Xenon16Mb);

    private static readonly ImmutableArray<ConsoleId> LargeBlockFlashConfigurationCandidates = ImmutableArray.Create(
        ConsoleId.Xenon64Mb,
        ConsoleId.Zephyr64Mb,
        ConsoleId.Falcon64Mb);

    private static readonly ImmutableArray<ConsoleId> TrinityJasperFlashConfigurationCandidates = ImmutableArray.Create(
        ConsoleId.Trinity16Mb,
        ConsoleId.Jasper16Mb);

    private static readonly ImmutableArray<ConsoleId> CoronaWinchesterFlashConfigurationCandidates = ImmutableArray.Create(
        ConsoleId.Corona16Mb,
        ConsoleId.Winchester16Mb);

    private static readonly ImmutableArray<ConsoleId> SmallNandLengthCandidates = ImmutableArray.Create(
        ConsoleId.Trinity16Mb,
        ConsoleId.Falcon16Mb,
        ConsoleId.Zephyr16Mb,
        ConsoleId.Jasper16Mb,
        ConsoleId.JasperXsb,
        ConsoleId.Xenon16Mb,
        ConsoleId.Corona16Mb,
        ConsoleId.Winchester16Mb);

    private static readonly ImmutableArray<ConsoleId> LargeNandLengthCandidates = ImmutableArray.Create(
        ConsoleId.JasperBigBlock,
        ConsoleId.Xenon64Mb,
        ConsoleId.CoronaBigBlock,
        ConsoleId.TrinityBigBlock,
        ConsoleId.Zephyr64Mb,
        ConsoleId.Falcon64Mb,
        ConsoleId.WinchesterBigBlock);

    private static readonly ImmutableArray<ConsoleId> Layout0Candidates = ImmutableArray.Create(
        ConsoleId.Falcon16Mb,
        ConsoleId.Zephyr16Mb,
        ConsoleId.JasperXsb,
        ConsoleId.Xenon64Mb,
        ConsoleId.Xenon16Mb,
        ConsoleId.Zephyr64Mb,
        ConsoleId.Falcon64Mb);

    private static readonly ImmutableArray<ConsoleId> Layout1Candidates = ImmutableArray.Create(
        ConsoleId.Trinity16Mb,
        ConsoleId.Jasper16Mb,
        ConsoleId.Corona16Mb,
        ConsoleId.Winchester16Mb);

    private static readonly ImmutableArray<ConsoleId> Layout2Candidates = ImmutableArray.Create(
        ConsoleId.JasperBigBlock,
        ConsoleId.CoronaBigBlock,
        ConsoleId.TrinityBigBlock,
        ConsoleId.WinchesterBigBlock);

    /// <summary>
    /// Scores every canonical console using all available evidence.
    /// </summary>
    /// <remarks>
    /// This method preserves the legacy additive score rules but never selects an arbitrary winner.
    /// Callers must handle <see cref="ConsoleIdentificationResult.IsAmbiguous"/> explicitly.
    /// </remarks>
    public static ConsoleIdentificationResult Identify(ConsoleIdentificationEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        var catalog = ConsoleCatalog.All;
        Span<int> scores = stackalloc int[catalog.Length];
        var evidenceMatches = ImmutableArray.CreateBuilder<ConsoleScoreEvidence>(GetEvidenceEntryCount(evidence));

        if (evidence.CbBuild is int cbBuild)
        {
            var cbRule = GetCbRule(cbBuild, evidence.CbBBuild, evidence.EffectiveHasSpareData);
            AddEvidence(scores, evidenceMatches, ConsoleEvidenceKind.CbBuild, cbRule.Points, cbRule.Candidates);
        }

        if (evidence.SmcType is int smcType)
        {
            var smcRule = GetSmcRule(smcType);
            AddEvidence(scores, evidenceMatches, ConsoleEvidenceKind.SmcType, smcRule.Points, smcRule.Candidates);
        }

        if (evidence.FlashConfiguration is uint flashConfiguration)
        {
            AddEvidence(
                scores,
                evidenceMatches,
                ConsoleEvidenceKind.FlashConfiguration,
                pointsPerAffectedConsole: 1,
                affectedConsoleIds: GetFlashConfigurationCandidates(flashConfiguration));
        }

        if (evidence.RawNandLength is long rawNandLength)
        {
            var lengthRule = GetRawLengthRule(rawNandLength);
            AddEvidence(scores, evidenceMatches, ConsoleEvidenceKind.RawNandLength, lengthRule.Points, lengthRule.Candidates);
        }

        if (evidence.HasSpareData is false)
        {
            AddEvidence(
                scores,
                evidenceMatches,
                ConsoleEvidenceKind.SpareData,
                pointsPerAffectedConsole: 1,
                affectedConsoleIds: EmmcCandidates);
        }
        else
        {
            if (evidence.HasSpareData is true)
            {
                AddEvidence(
                    scores,
                    evidenceMatches,
                    ConsoleEvidenceKind.SpareData,
                    pointsPerAffectedConsole: 0,
                    affectedConsoleIds: ImmutableArray<ConsoleId>.Empty);
            }

            if (evidence.Layout is NandLegacyLayout layout)
            {
                AddEvidence(
                    scores,
                    evidenceMatches,
                    ConsoleEvidenceKind.Layout,
                    pointsPerAffectedConsole: 1,
                    affectedConsoleIds: GetLayoutCandidates(layout));
            }
        }

        var candidates = ImmutableArray.CreateBuilder<ConsoleIdentificationCandidate>(catalog.Length);
        for (var index = 0; index < catalog.Length; index++)
        {
            candidates.Add(new ConsoleIdentificationCandidate(catalog[index], scores[index]));
        }

        candidates.Sort(static (left, right) =>
        {
            var scoreComparison = right.Score.CompareTo(left.Score);
            return scoreComparison != 0
                ? scoreComparison
                : left.Console.LegacyId.CompareTo(right.Console.LegacyId);
        });

        var rankedCandidates = candidates.MoveToImmutable();
        var highestScore = rankedCandidates[0].Score;
        var highestCount = 0;
        while (highestCount < rankedCandidates.Length && rankedCandidates[highestCount].Score == highestScore)
        {
            highestCount++;
        }

        var highestCandidates = ImmutableArray.CreateBuilder<ConsoleIdentificationCandidate>(highestCount);
        for (var index = 0; index < highestCount; index++)
        {
            highestCandidates.Add(rankedCandidates[index]);
        }

        return new ConsoleIdentificationResult(
            evidence,
            rankedCandidates,
            highestCandidates.MoveToImmutable(),
            evidenceMatches.MoveToImmutable());
    }


    private static int GetEvidenceEntryCount(ConsoleIdentificationEvidence evidence)
    {
        var count = 0;

        if (evidence.CbBuild.HasValue)
        {
            count++;
        }

        if (evidence.SmcType.HasValue)
        {
            count++;
        }

        if (evidence.FlashConfiguration.HasValue)
        {
            count++;
        }

        if (evidence.RawNandLength.HasValue)
        {
            count++;
        }

        if (evidence.HasSpareData.HasValue)
        {
            count++;
        }

        if (evidence.Layout.HasValue)
        {
            count++;
        }

        return count;
    }

    private static (ImmutableArray<ConsoleId> Candidates, int Points) GetCbRule(
        int cbBuild,
        int? cbBBuild,
        bool? hasSpareData)
    {
        if (cbBuild is >= 9188 and <= 9250)
        {
            return (TrinityCandidates, 3);
        }

        if (cbBuild >= 16000)
        {
            return hasSpareData switch
            {
                false => (Winchester4GbCandidate, 3),
                true => (WinchesterNandCandidates, 3),
                _ => (ImmutableArray<ConsoleId>.Empty, 0),
            };
        }

        if (cbBuild is >= 13121 and <= 13200)
        {
            return hasSpareData switch
            {
                false => (Corona4GbCandidate, 3),
                true => (CoronaNandCandidates, 3),
                _ => (ImmutableArray<ConsoleId>.Empty, 0),
            };
        }

        if (cbBuild is >= 6712 and <= 6780)
        {
            return (JasperCandidates, 3);
        }

        if (cbBuild is >= 4558 and <= 4590)
        {
            return (ZephyrCandidates, 3);
        }

        if ((cbBuild is >= 1888 and <= 1960) ||
            (cbBuild is >= 7373 and <= 7378) ||
            cbBuild == 8192)
        {
            return (XenonCandidates, 3);
        }

        if (cbBuild is >= 5761 and <= 5780)
        {
            return cbBBuild is >= 7373 and <= 7378
                ? (XenonCandidates, 3)
                : (FalconCandidates, 3);
        }

        return (ImmutableArray<ConsoleId>.Empty, 0);
    }

    private static (ImmutableArray<ConsoleId> Candidates, int Points) GetSmcRule(int smcType) => smcType switch
    {
        1 => (XenonCandidates, 5),
        2 => (ZephyrCandidates, 2),
        3 => (FalconCandidates, 2),
        4 => (JasperCandidates, 2),
        5 => (TrinityCandidates, 2),
        6 => (CoronaCandidates, 2),
        7 => (WinchesterCandidates, 2),
        _ => (ImmutableArray<ConsoleId>.Empty, 0),
    };

    private static ImmutableArray<ConsoleId> GetFlashConfigurationCandidates(uint flashConfiguration) => flashConfiguration switch
    {
        0x008A3020u or 0x00AA3020u => JasperBigBlockTrinityBigBlockCandidates,
        0x008C3020u or 0x00AC3020u => CoronaBigBlockWinchesterBigBlockCandidates,
        0xC0462002u => EmmcCandidates,
        0x01198010u => SmallBlockFlashConfigurationCandidates,
        0x01198030u => LargeBlockFlashConfigurationCandidates,
        0x00023010u => TrinityJasperFlashConfigurationCandidates,
        0x00043000u => CoronaWinchesterFlashConfigurationCandidates,
        _ => ImmutableArray<ConsoleId>.Empty,
    };

    private static (ImmutableArray<ConsoleId> Candidates, int Points) GetRawLengthRule(long rawNandLength) => rawNandLength switch
    {
        17_301_504 => (SmallNandLengthCandidates, 1),
        69_206_016 or 276_824_064 or 553_648_128 => (LargeNandLengthCandidates, 2),
        _ => (EmmcCandidates, 1),
    };

    private static ImmutableArray<ConsoleId> GetLayoutCandidates(NandLegacyLayout layout) => layout switch
    {
        NandLegacyLayout.Layout0 => Layout0Candidates,
        NandLegacyLayout.Layout1 => Layout1Candidates,
        NandLegacyLayout.Layout2 => Layout2Candidates,
        _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, "The NAND layout is not supported."),
    };

    private static void AddEvidence(
        Span<int> scores,
        ImmutableArray<ConsoleScoreEvidence>.Builder evidenceMatches,
        ConsoleEvidenceKind kind,
        int pointsPerAffectedConsole,
        ImmutableArray<ConsoleId> affectedConsoleIds)
    {
        if (affectedConsoleIds.IsDefaultOrEmpty)
        {
            evidenceMatches.Add(new ConsoleScoreEvidence(kind, 0, ImmutableArray<ConsoleId>.Empty));
            return;
        }

        foreach (var consoleId in affectedConsoleIds)
        {
            var scoreIndex = checked((int)consoleId - ConsoleCatalog.FirstLegacyId);
            scores[scoreIndex] = checked(scores[scoreIndex] + pointsPerAffectedConsole);
        }

        evidenceMatches.Add(new ConsoleScoreEvidence(kind, pointsPerAffectedConsole, affectedConsoleIds));
    }
}
