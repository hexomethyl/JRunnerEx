namespace JRunner.Core.Nand.Hacks;

/// <summary>
/// Immutable legacy hack-compatibility diagnostics derived from parsed CB build numbers.
/// </summary>
/// <remarks>
/// Parsed build numbers and derived flags preserve legacy selection behavior, not direct proof of
/// the family contained in an inspected output. This model intentionally retains no bootloader bytes,
/// CPU keys, or other secret image data.
/// </remarks>
public sealed record NandHackEvidence
{
    internal NandHackEvidence(
        int? cbABuild,
        int? cbBBuild,
        int? cbXBuild,
        NandHackType tablePreferredHack,
        NandHackType preferredHack,
        bool hasWinbondEvidence,
        bool hasElpisEvidence)
    {
        CbABuild = cbABuild;
        CbBBuild = cbBBuild;
        CbXBuild = cbXBuild;
        TablePreferredHack = tablePreferredHack;
        PreferredHack = preferredHack;
        HasWinbondEvidence = hasWinbondEvidence;
        HasElpisEvidence = hasElpisEvidence;
    }

    /// <summary>
    /// Gets the parsed CB_A build, or <see langword="null"/> when CB_A was not present.
    /// </summary>
    public int? CbABuild { get; }

    /// <summary>
    /// Gets the parsed CB_B build, or <see langword="null"/> when CB_B was not present.
    /// </summary>
    public int? CbBBuild { get; }

    /// <summary>
    /// Gets the parsed CB_X build, or <see langword="null"/> when CB_X was not present.
    /// </summary>
    public int? CbXBuild { get; }

    /// <summary>
    /// Gets the preferred hack from the ordered legacy CB table using CB_A alone, or
    /// <see cref="NandHackType.Unknown"/> when CB_A did not match an entry.
    /// </summary>
    public NandHackType TablePreferredHack { get; }

    /// <summary>
    /// Gets the legacy compatibility preference. A positive CB_X takes precedence as RGH3 or RGH 1.3;
    /// otherwise this is <see cref="TablePreferredHack"/>. It does not prove an observed image family.
    /// </summary>
    public NandHackType PreferredHack { get; }

    /// <summary>
    /// Gets whether CB_A matched an entry in the ordered legacy CB/SB table.
    /// </summary>
    public bool HasTableMatch => TablePreferredHack != NandHackType.Unknown;

    /// <summary>
    /// Gets whether a positive CB_X supplies the legacy RGH3-family compatibility signal. Legacy parsing
    /// uses zero as its absent-CB_X sentinel. This diagnostic does not prove an observed image family.
    /// </summary>
    public bool HasCbXEvidence => CbXBuild is > 0;

    /// <summary>
    /// Gets whether a positive non-<c>42069</c> CB_X resolved to RGH3.
    /// </summary>
    public bool HasRgh3Evidence => PreferredHack == NandHackType.Rgh3;

    /// <summary>
    /// Gets whether CB_X build <c>42069</c> resolved to RGH 1.3.
    /// </summary>
    public bool HasRgh13Evidence => PreferredHack == NandHackType.Rgh13;

    /// <summary>
    /// Gets whether CB_A and CB_B exactly match the legacy Winbond pair: <c>13121</c> and
    /// <c>13182</c>, respectively.
    /// </summary>
    public bool HasWinbondEvidence { get; }

    /// <summary>
    /// Gets whether CB_A or CB_B is in the legacy MainForm Elpis/Rhea range <c>7373</c> through
    /// <c>7378</c>. This is independent from the hack conclusion.
    /// </summary>
    public bool HasElpisEvidence { get; }
}
