using System.Collections.Immutable;
using JRunner.Core.Nand.Models;

namespace JRunner.Core.Nand.Hacks;

/// <summary>
/// Stores the bootloader compatibility rows ported verbatim from legacy <c>Nand.ntable.Table</c>.
/// </summary>
/// <remarks>
/// The rows are intentionally internal because they express legacy bootloader compatibility, not a
/// statement that an image contains an exact dashboard version. Callers consume the normalized,
/// safe evidence produced by NAND inspection instead.
/// </remarks>
internal static class LegacyBootloaderCatalog
{
    internal const int LatestLegacyDashboardVersion = 17559;

    private static readonly ImmutableArray<LegacyBootloaderMapping> Mappings =
    [
        // CBs: JTAG.
        new(1888, NandHackType.Jtag, ConsoleId.Xenon16Mb, 0, 7371),
        new(1897, NandHackType.Jtag, ConsoleId.Xenon16Mb, 0, 7371),
        new(1902, NandHackType.Jtag, ConsoleId.Xenon16Mb, 0, 7371),
        new(1903, NandHackType.Jtag, ConsoleId.Xenon16Mb, 0, 7371),
        new(1920, NandHackType.Jtag, ConsoleId.Xenon16Mb, 0, 7371),
        new(1921, NandHackType.Jtag, ConsoleId.Xenon16Mb, 0, 7371),
        new(8192, NandHackType.Jtag, ConsoleId.Xenon16Mb, 0, 7371),
        new(4540, NandHackType.Jtag, ConsoleId.Zephyr16Mb, 0, 7371),
        new(4558, NandHackType.Jtag, ConsoleId.Zephyr16Mb, 0, 7371),
        new(4570, NandHackType.Jtag, ConsoleId.Zephyr16Mb, 0, 7371),
        new(4580, NandHackType.Jtag, ConsoleId.Zephyr16Mb, 0, 7371),
        new(5760, NandHackType.Jtag, ConsoleId.Falcon16Mb, 0, 7371),
        new(5761, NandHackType.Jtag, ConsoleId.Falcon16Mb, 0, 7371),
        new(5766, NandHackType.Jtag, ConsoleId.Falcon16Mb, 0, 7371),
        new(5770, NandHackType.Jtag, ConsoleId.Falcon16Mb, 0, 7371),
        new(6712, NandHackType.Jtag, ConsoleId.Jasper16Mb, 0, 7371),
        new(6723, NandHackType.Jtag, ConsoleId.Jasper16Mb, 0, 7371),

        // CBs: legacy glitch and glitch2 rows.
        new(1922, NandHackType.Glitch2, ConsoleId.Xenon16Mb, 8498, 14699),
        new(1940, NandHackType.Glitch2, ConsoleId.Xenon16Mb, 8498, 14699),
        new(1923, NandHackType.Glitch2, ConsoleId.Xenon16Mb, 8498, 14699),
        new(7373, NandHackType.Glitch2, ConsoleId.Xenon16Mb, 8498, 14699),
        new(7375, NandHackType.Glitch2, ConsoleId.Xenon16Mb, 8498, 14699),
        new(4571, NandHackType.Glitch, ConsoleId.Zephyr16Mb, 8498, 14699),
        new(4579, NandHackType.Glitch, ConsoleId.Zephyr16Mb, 8498, 14699),
        new(4572, NandHackType.Glitch, ConsoleId.Zephyr16Mb, 8498, 14699),
        new(4578, NandHackType.Glitch, ConsoleId.Zephyr16Mb, 8498, 14699),
        new(5771, NandHackType.Glitch, ConsoleId.Falcon16Mb, 8498, 14699),
        new(6750, NandHackType.Glitch, ConsoleId.Jasper16Mb, 8498, 14699),
        new(6751, NandHackType.Glitch, ConsoleId.Jasper16Mb, 8498, 14699),
        new(9188, NandHackType.Glitch2, ConsoleId.Trinity16Mb, 8498, 14699),
        new(10918, NandHackType.Glitch2, ConsoleId.Corona16Mb, 8498, 14699),
        new(13121, NandHackType.Glitch2, ConsoleId.Corona16Mb, 8498, 14699),
        new(1925, NandHackType.Glitch2, ConsoleId.Xenon16Mb, 14717, 14719),
        new(1941, NandHackType.Glitch2, ConsoleId.Xenon16Mb, 14717, 14719),
        new(1926, NandHackType.Glitch2, ConsoleId.Xenon16Mb, 14717, 14719),
        new(7377, NandHackType.Glitch2, ConsoleId.Xenon16Mb, 14717, 14719),
        new(4577, NandHackType.Glitch2, ConsoleId.Zephyr16Mb, 14717, 14719),
        new(4559, NandHackType.Glitch2, ConsoleId.Zephyr16Mb, 14717, 14719),
        new(4576, NandHackType.Glitch2, ConsoleId.Zephyr16Mb, 14717, 14719),
        new(4560, NandHackType.Glitch2, ConsoleId.Zephyr16Mb, 14717, 14719),
        new(4575, NandHackType.Glitch2, ConsoleId.Zephyr16Mb, 14717, 14719),
        new(5772, NandHackType.Glitch2, ConsoleId.Falcon16Mb, 14717, 14719),
        new(5773, NandHackType.Glitch2, ConsoleId.Falcon16Mb, 14717, 14719),
        new(6752, NandHackType.Glitch2, ConsoleId.Jasper16Mb, 14717, 14719),
        new(6753, NandHackType.Glitch2, ConsoleId.Jasper16Mb, 14717, 14719),
        new(9230, NandHackType.Glitch2, ConsoleId.Trinity16Mb, 14717, 14719),
        new(13180, NandHackType.Glitch2, ConsoleId.Corona16Mb, 14717, 14719),
        new(1927, NandHackType.Glitch2, ConsoleId.Xenon16Mb, 15572, LatestLegacyDashboardVersion),
        new(1942, NandHackType.Glitch2, ConsoleId.Xenon16Mb, 15572, LatestLegacyDashboardVersion),
        new(1928, NandHackType.Glitch2, ConsoleId.Xenon16Mb, 15572, LatestLegacyDashboardVersion),
        new(7378, NandHackType.Glitch2, ConsoleId.Xenon16Mb, 15572, LatestLegacyDashboardVersion),
        new(4561, NandHackType.Glitch2, ConsoleId.Zephyr16Mb, 15572, LatestLegacyDashboardVersion),
        new(4574, NandHackType.Glitch2, ConsoleId.Zephyr16Mb, 15572, LatestLegacyDashboardVersion),
        new(4562, NandHackType.Glitch2, ConsoleId.Zephyr16Mb, 15572, LatestLegacyDashboardVersion),
        new(4569, NandHackType.Glitch2, ConsoleId.Zephyr16Mb, 15572, LatestLegacyDashboardVersion),
        new(5774, NandHackType.Glitch2, ConsoleId.Falcon16Mb, 15572, LatestLegacyDashboardVersion),
        new(6754, NandHackType.Glitch2, ConsoleId.Jasper16Mb, 15572, LatestLegacyDashboardVersion),
        new(9231, NandHackType.Glitch2, ConsoleId.Trinity16Mb, 15572, LatestLegacyDashboardVersion),
        new(13181, NandHackType.Glitch2, ConsoleId.Corona16Mb, 15572, LatestLegacyDashboardVersion),
        new(13182, NandHackType.Glitch2, ConsoleId.Corona16Mb, 15572, LatestLegacyDashboardVersion),
        new(16128, NandHackType.Glitch2, ConsoleId.Winchester16Mb, 15572, LatestLegacyDashboardVersion),

        // SBs: preserve repeated console-specific DevGL rows and their distinct minimum dashboards.
        new(10375, NandHackType.DevGl, ConsoleId.Xenon16Mb, 4532, LatestLegacyDashboardVersion),
        new(14352, NandHackType.DevGl, ConsoleId.Xenon16Mb, 4532, LatestLegacyDashboardVersion),
        new(10375, NandHackType.DevGl, ConsoleId.Zephyr16Mb, 4532, LatestLegacyDashboardVersion),
        new(14352, NandHackType.DevGl, ConsoleId.Zephyr16Mb, 4532, LatestLegacyDashboardVersion),
        new(10375, NandHackType.DevGl, ConsoleId.Falcon16Mb, 4532, LatestLegacyDashboardVersion),
        new(14352, NandHackType.DevGl, ConsoleId.Falcon16Mb, 4532, LatestLegacyDashboardVersion),
        new(10375, NandHackType.DevGl, ConsoleId.Jasper16Mb, 4532, LatestLegacyDashboardVersion),
        new(14352, NandHackType.DevGl, ConsoleId.Jasper16Mb, 4532, LatestLegacyDashboardVersion),
        new(10375, NandHackType.DevGl, ConsoleId.Trinity16Mb, 8498, LatestLegacyDashboardVersion),
        new(14352, NandHackType.DevGl, ConsoleId.Trinity16Mb, 8498, LatestLegacyDashboardVersion),
        new(14352, NandHackType.DevGl, ConsoleId.Corona16Mb, 8498, LatestLegacyDashboardVersion),
        new(14352, NandHackType.DevGl, ConsoleId.Winchester16Mb, 8498, LatestLegacyDashboardVersion),
    ];

    internal static LegacyBootloaderMapping? FindFirst(int build)
    {
        foreach (LegacyBootloaderMapping mapping in Mappings)
        {
            if (mapping.Build == build)
            {
                return mapping;
            }
        }

        return null;
    }

    internal static ImmutableArray<LegacyBootloaderMapping> FindAll(int build)
    {
        var count = 0;
        foreach (LegacyBootloaderMapping mapping in Mappings)
        {
            if (mapping.Build == build)
            {
                count++;
            }
        }

        if (count == 0)
        {
            return ImmutableArray<LegacyBootloaderMapping>.Empty;
        }

        var matches = ImmutableArray.CreateBuilder<LegacyBootloaderMapping>(count);
        foreach (LegacyBootloaderMapping mapping in Mappings)
        {
            if (mapping.Build == build)
            {
                matches.Add(mapping);
            }
        }

        return matches.MoveToImmutable();
    }
}

/// <summary>
/// One immutable row from legacy <c>Nand.ntable.Table</c>.
/// </summary>
internal readonly record struct LegacyBootloaderMapping(
    int Build,
    NandHackType PreferredHack,
    ConsoleId ConsoleId,
    int MinimumDashboardVersion,
    int MaximumDashboardVersion);
