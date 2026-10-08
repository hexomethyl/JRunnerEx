namespace JRunner.Core.Nand.Hacks;

/// <summary>
/// Identifies immutable legacy NAND hack-compatibility diagnostics from parsed CB build numbers.
/// </summary>
/// <remarks>
/// The preferred-hack lookup deliberately scans the legacy <c>Nand.ntable.Table</c> entries in
/// declaration order and uses CB_A only, matching the legacy MainForm caller. CB_B is retained
/// for the separate Winbond and Elpis evidence rules; it does not infer a preferred hack. These
/// compatibility rules and CB_X precedence do not directly prove the family in an inspected image.
/// </remarks>
public static class NandHackEvidenceService
{
    private const int Rgh13CbXBuild = 42069;
    private const int ElpisFirstBuild = 7373;
    private const int ElpisLastBuild = 7378;


    /// <summary>
    /// Identifies hack evidence without retaining NAND bytes or invoking any UI, process, or
    /// filesystem dependency.
    /// </summary>
    /// <param name="cbABuild">The parsed CB_A build, or <see langword="null"/> when absent.</param>
    /// <param name="cbBBuild">The parsed CB_B build, or <see langword="null"/> when absent.</param>
    /// <param name="cbXBuild">The parsed CB_X build, or <see langword="null"/> when absent.</param>
    /// <returns>Immutable preferred-hack, CB_X, Winbond, and Elpis evidence.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Any supplied build number is negative.</exception>
    public static NandHackEvidence Identify(int? cbABuild, int? cbBBuild, int? cbXBuild)
    {
        ValidateNonNegative(cbABuild, nameof(cbABuild));
        ValidateNonNegative(cbBBuild, nameof(cbBBuild));
        ValidateNonNegative(cbXBuild, nameof(cbXBuild));

        var tablePreferredHack = FindTablePreferredHack(cbABuild);
        var preferredHack = ResolvePreferredHack(tablePreferredHack, cbXBuild);

        return new NandHackEvidence(
            cbABuild,
            cbBBuild,
            cbXBuild,
            tablePreferredHack,
            preferredHack,
            cbABuild == 13121 && cbBBuild == 13182,
            IsElpisBuild(cbABuild) || IsElpisBuild(cbBBuild));
    }

    private static NandHackType FindTablePreferredHack(int? cbABuild)
    {
        if (!cbABuild.HasValue)
        {
            return NandHackType.Unknown;
        }

        LegacyBootloaderMapping? mapping = LegacyBootloaderCatalog.FindFirst(cbABuild.Value);
        return mapping?.PreferredHack ?? NandHackType.Unknown;
    }

    private static NandHackType ResolvePreferredHack(NandHackType tablePreferredHack, int? cbXBuild)
    {
        if (cbXBuild is not > 0)
        {
            return tablePreferredHack;
        }

        return cbXBuild == Rgh13CbXBuild
            ? NandHackType.Rgh13
            : NandHackType.Rgh3;
    }

    private static bool IsElpisBuild(int? build)
    {
        return build is >= ElpisFirstBuild and <= ElpisLastBuild;
    }

    private static void ValidateNonNegative(int? build, string parameterName)
    {
        if (build is < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "A bootloader build cannot be negative.");
        }
    }

}
