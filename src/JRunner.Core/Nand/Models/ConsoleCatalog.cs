using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using JRunner.Core.Nand.Physical;

namespace JRunner.Core.Nand.Models;

/// <summary>
/// Provides the immutable canonical console catalog ported from legacy <c>variables.ctypes</c>.
/// </summary>
public static class ConsoleCatalog
{
    /// <summary>
    /// Gets the first supported legacy console identifier.
    /// </summary>
    public const int FirstLegacyId = 1;

    /// <summary>
    /// Gets the last supported legacy console identifier.
    /// </summary>
    public const int LastLegacyId = 17;

    /// <summary>
    /// Gets every supported console in ascending legacy identifier order.
    /// </summary>
    public static ImmutableArray<ConsoleDefinition> All { get; } = ImmutableArray.Create(
        new ConsoleDefinition(ConsoleId.Trinity16Mb, "Trinity 16MB", "trinity", "trinity", 16, NandLogicalSize.S16, NandLegacyLayout.Layout1),
        new ConsoleDefinition(ConsoleId.Falcon16Mb, "Falcon 16MB", "falcon", "falcon", 16, NandLogicalSize.S16, NandLegacyLayout.Layout0),
        new ConsoleDefinition(ConsoleId.Zephyr16Mb, "Zephyr 16MB", "zephyr", "zephyr", 16, NandLogicalSize.S16, NandLegacyLayout.Layout0),
        new ConsoleDefinition(ConsoleId.Jasper16Mb, "Jasper 16MB", "jasper", "jasper", 16, NandLogicalSize.S16, NandLegacyLayout.Layout1),
        new ConsoleDefinition(ConsoleId.JasperXsb, "Jasper XSB", "jaspersb", "jasper", 16, NandLogicalSize.S16, NandLegacyLayout.Layout0),
        new ConsoleDefinition(ConsoleId.JasperBigBlock, "Jasper BB", "jasperbb", "jasper", 64, NandLogicalSize.S64, NandLegacyLayout.Layout2),
        new ConsoleDefinition(ConsoleId.Xenon64Mb, "Xenon 64MB", "xenon", "xenon", 64, NandLogicalSize.S64, NandLegacyLayout.Layout0),
        new ConsoleDefinition(ConsoleId.Xenon16Mb, "Xenon 16MB", "xenon", "xenon", 16, NandLogicalSize.S16, NandLegacyLayout.Layout0),
        new ConsoleDefinition(ConsoleId.CoronaBigBlock, "Corona BB", "coronabb", "corona", 64, NandLogicalSize.S64, NandLegacyLayout.Layout2),
        new ConsoleDefinition(ConsoleId.Corona16Mb, "Corona 16MB", "corona", "corona", 16, NandLogicalSize.S16, NandLegacyLayout.Layout1),
        new ConsoleDefinition(ConsoleId.Corona4Gb, "Corona 4GB", "corona4g", "corona", 0, NandLogicalSize.S0, layout: null),
        new ConsoleDefinition(ConsoleId.TrinityBigBlock, "Trinity BB", "trinitybb", "trinity", 64, NandLogicalSize.S64, NandLegacyLayout.Layout2),
        new ConsoleDefinition(ConsoleId.Zephyr64Mb, "Zephyr 64MB", "zephyr", "zephyr", 64, NandLogicalSize.S64, NandLegacyLayout.Layout0),
        new ConsoleDefinition(ConsoleId.Falcon64Mb, "Falcon 64MB", "falcon", "falcon", 64, NandLogicalSize.S64, NandLegacyLayout.Layout0),
        new ConsoleDefinition(ConsoleId.Winchester16Mb, "Winchester 16MB", "winchester", "winchester", 64, NandLogicalSize.S16, NandLegacyLayout.Layout1),
        new ConsoleDefinition(ConsoleId.Winchester4Gb, "Winchester 4GB", "winchester4g", "winchester", 64, NandLogicalSize.S0, layout: null),
        new ConsoleDefinition(ConsoleId.WinchesterBigBlock, "Winchester BB", "winchesterbb", "winchester", 64, NandLogicalSize.S64, NandLegacyLayout.Layout2));

    /// <summary>
    /// Gets the number of supported canonical consoles.
    /// </summary>
    public static int Count => All.Length;

    /// <summary>
    /// Resolves a canonical console by its typed legacy identifier.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="id"/> is unsupported.</exception>
    public static ConsoleDefinition Get(ConsoleId id) => Get((int)id);

    /// <summary>
    /// Resolves a canonical console by its numeric legacy identifier.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="legacyId"/> is unsupported.</exception>
    public static ConsoleDefinition Get(int legacyId)
    {
        if (TryGet(legacyId, out var console))
        {
            return console!;
        }

        throw new ArgumentOutOfRangeException(nameof(legacyId), legacyId, "The console identifier is not supported.");
    }

    /// <summary>
    /// Attempts to resolve a canonical console by its typed legacy identifier.
    /// </summary>
    public static bool TryGet(ConsoleId id, [NotNullWhen(true)] out ConsoleDefinition? console) => TryGet((int)id, out console);

    /// <summary>
    /// Attempts to resolve a canonical console by its numeric legacy identifier.
    /// </summary>
    public static bool TryGet(int legacyId, [NotNullWhen(true)] out ConsoleDefinition? console)
    {
        if (legacyId is < FirstLegacyId or > LastLegacyId)
        {
            console = null;
            return false;
        }

        console = All[legacyId - FirstLegacyId];
        return true;
    }

    /// <summary>
    /// Attempts to resolve a canonical console by its human-readable canonical name, ignoring case.
    /// </summary>
    public static bool TryGetByCanonicalName(string? canonicalName, [NotNullWhen(true)] out ConsoleDefinition? console)
    {
        if (string.IsNullOrWhiteSpace(canonicalName))
        {
            console = null;
            return false;
        }

        foreach (var candidate in All)
        {
            if (string.Equals(candidate.CanonicalName, canonicalName, StringComparison.OrdinalIgnoreCase))
            {
                console = candidate;
                return true;
            }
        }

        console = null;
        return false;
    }
}
