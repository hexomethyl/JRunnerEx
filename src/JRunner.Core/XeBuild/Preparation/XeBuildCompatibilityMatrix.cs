using System.Collections.Immutable;
using JRunner.Core.Nand.Models;

namespace JRunner.Core.XeBuild.Preparation;

/// <summary>
/// Encodes the board-level xeBuild compatibility rules recovered from the legacy UI oracle.
/// Dashboard assets and source-image capabilities are validated separately by preparation.
/// </summary>
public static class XeBuildCompatibilityMatrix
{
    /// <summary>
    /// Gets the typed targets that are board-compatible with <paramref name="consoleId"/>.
    /// </summary>
    public static ImmutableArray<XeBuildHackType> GetSupportedHackTypes(ConsoleId consoleId)
    {
        _ = ConsoleCatalog.Get(consoleId);
        var supported = ImmutableArray.CreateBuilder<XeBuildHackType>();
        foreach (XeBuildHackType hackType in Enum.GetValues<XeBuildHackType>())
        {
            if (SupportsHack(consoleId, hackType))
            {
                supported.Add(hackType);
            }
        }

        return supported.ToImmutable();
    }

    /// <summary>
    /// Gets whether a target is board-compatible before dashboard and source-image checks.
    /// </summary>
    public static bool SupportsHack(ConsoleId consoleId, XeBuildHackType hackType)
    {
        _ = ConsoleCatalog.Get(consoleId);
        if (!Enum.IsDefined(hackType))
        {
            return false;
        }

        return hackType switch
        {
            XeBuildHackType.Jtag => consoleId is
                ConsoleId.Falcon16Mb or
                ConsoleId.Falcon64Mb or
                ConsoleId.Zephyr16Mb or
                ConsoleId.Zephyr64Mb or
                ConsoleId.Jasper16Mb or
                ConsoleId.JasperXsb or
                ConsoleId.JasperBigBlock or
                ConsoleId.Xenon16Mb or
                ConsoleId.Xenon64Mb,
            XeBuildHackType.Glitch => consoleId is
                ConsoleId.Falcon16Mb or
                ConsoleId.Falcon64Mb or
                ConsoleId.Zephyr16Mb or
                ConsoleId.Zephyr64Mb or
                ConsoleId.Jasper16Mb or
                ConsoleId.JasperXsb or
                ConsoleId.JasperBigBlock,
            _ => true,
        };
    }

    /// <summary>
    /// Gets whether a console has a real BigFFS xeBuild board configuration.
    /// </summary>
    public static bool SupportsBigFfs(ConsoleId consoleId)
    {
        _ = ConsoleCatalog.Get(consoleId);
        return consoleId is
            ConsoleId.JasperBigBlock or
            ConsoleId.CoronaBigBlock or
            ConsoleId.TrinityBigBlock or
            ConsoleId.WinchesterBigBlock;
    }

    /// <summary>
    /// Gets whether a console can use the legacy RGH3 post-build flow.
    /// </summary>
    public static bool SupportsRgh3(ConsoleId consoleId)
    {
        _ = ConsoleCatalog.Get(consoleId);
        return consoleId is not
            ConsoleId.Xenon16Mb and not
            ConsoleId.Xenon64Mb and not
            ConsoleId.Zephyr16Mb and not
            ConsoleId.Zephyr64Mb and not
            ConsoleId.Winchester16Mb and not
            ConsoleId.Winchester4Gb and not
            ConsoleId.WinchesterBigBlock;
    }

    /// <summary>
    /// Gets whether a DevGL target needs the legacy 64 MB DevGL preparation flow.
    /// </summary>
    public static bool RequiresDevGl64Preparation(ConsoleId consoleId, XeBuildHackType hackType)
    {
        _ = ConsoleCatalog.Get(consoleId);
        return hackType is XeBuildHackType.DevGl && consoleId is
            ConsoleId.Xenon64Mb or
            ConsoleId.Zephyr64Mb or
            ConsoleId.Falcon64Mb;
    }

    /// <summary>
    /// Gets whether the legacy XeBuild output requires a header repair after a successful build.
    /// </summary>
    public static bool RequiresXeBuildImageRepair(ConsoleId consoleId)
    {
        _ = ConsoleCatalog.Get(consoleId);
        return consoleId is
            ConsoleId.Falcon16Mb or
            ConsoleId.Zephyr16Mb or
            ConsoleId.JasperXsb or
            ConsoleId.Xenon64Mb or
            ConsoleId.Xenon16Mb or
            ConsoleId.Zephyr64Mb or
            ConsoleId.Falcon64Mb;
    }

    /// <summary>
    /// Resolves the board configuration passed through xeBuild's <c>-c</c> argument.
    /// </summary>
    /// <param name="console">The resolved canonical console.</param>
    /// <param name="bigFfs">Whether the caller requested the BigFFS configuration.</param>
    /// <param name="usesXdkBuildConfiguration">Whether the selected dashboard requires the legacy XDKBuild configuration.</param>
    public static string GetConfigurationName(
        ConsoleDefinition console,
        bool bigFfs,
        bool usesXdkBuildConfiguration)
    {
        ArgumentNullException.ThrowIfNull(console);

        if (usesXdkBuildConfiguration)
        {
            return console.Id switch
            {
                ConsoleId.JasperBigBlock => "jasperbigffs",
                ConsoleId.CoronaBigBlock => "coronabigffs",
                ConsoleId.TrinityBigBlock => "trinitybigffs",
                ConsoleId.WinchesterBigBlock => "winchesterbigffs",
                _ => console.XeBuildName,
            };
        }

        if (!bigFfs)
        {
            return console.XeBuildName;
        }

        return console.Id switch
        {
            ConsoleId.JasperBigBlock => "jasperbigffs",
            ConsoleId.CoronaBigBlock => "coronabigffs",
            ConsoleId.TrinityBigBlock => "trinitybigffs",
            ConsoleId.WinchesterBigBlock => "winchesterbigffs",
            _ => throw new ArgumentOutOfRangeException(nameof(console), console.Id, "The console has no BigFFS xeBuild configuration."),
        };
    }

    /// <summary>
    /// Gets whether the legacy XDKBuild branch passes xeBuild's <c>-i flash</c> option.
    /// </summary>
    public static bool RequiresFlashIniForXdkBuild(ConsoleId consoleId)
    {
        _ = ConsoleCatalog.Get(consoleId);
        return consoleId is
            ConsoleId.JasperBigBlock or
            ConsoleId.CoronaBigBlock or
            ConsoleId.Corona4Gb or
            ConsoleId.TrinityBigBlock or
            ConsoleId.WinchesterBigBlock or
            ConsoleId.Winchester4Gb;
    }
}
