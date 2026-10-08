using System.Collections.Immutable;

namespace JRunner.Core.XeBuild.Preparation;

/// <summary>
/// Identifies the XeBuild image target supplied through its <c>-t</c> argument.
/// Numeric values intentionally preserve <c>variables.hacktypes</c> from the legacy oracle.
/// </summary>
public enum XeBuildHackType
{
    /// <summary>Builds an unmodified retail image.</summary>
    Retail = 1,

    /// <summary>Builds an RGH1 image.</summary>
    Glitch = 2,

    /// <summary>Builds a JTAG image.</summary>
    Jtag = 3,

    /// <summary>Builds an RGH2 image.</summary>
    Glitch2 = 4,

    /// <summary>Builds an RGH2M image.</summary>
    Glitch2m = 5,

    /// <summary>Builds a DevGL image.</summary>
    DevGl = 6,

    /// <summary>Builds a 16 MB DevGL image.</summary>
    DevGl16 = 7,

    /// <summary>Builds a devkit image.</summary>
    Devkit = 8,

    /// <summary>Builds a 16 MB devkit image.</summary>
    Devkit16 = 9,

    /// <summary>Builds a testkit image.</summary>
    Testkit = 10,

    /// <summary>Builds a 16 MB testkit image.</summary>
    Testkit16 = 11,
}

/// <summary>
/// Provides the canonical public names accepted for XeBuild target selection.
/// </summary>
public static class XeBuildHackTypeCatalog
{
    /// <summary>
    /// Gets every target in deterministic legacy numeric order.
    /// </summary>
    public static ImmutableArray<XeBuildHackType> All { get; } = ImmutableArray.Create(
        XeBuildHackType.Retail,
        XeBuildHackType.Glitch,
        XeBuildHackType.Jtag,
        XeBuildHackType.Glitch2,
        XeBuildHackType.Glitch2m,
        XeBuildHackType.DevGl,
        XeBuildHackType.DevGl16,
        XeBuildHackType.Devkit,
        XeBuildHackType.Devkit16,
        XeBuildHackType.Testkit,
        XeBuildHackType.Testkit16);

    /// <summary>
    /// Attempts to resolve a canonical target name. Matching is case-insensitive, but legacy aliases are not accepted.
    /// </summary>
    public static bool TryGetByCanonicalName(string? value, out XeBuildHackType hackType)
    {
        if (value is not null)
        {
            foreach (XeBuildHackType candidate in All)
            {
                if (string.Equals(GetCanonicalName(candidate), value, StringComparison.OrdinalIgnoreCase))
                {
                    hackType = candidate;
                    return true;
                }
            }
        }

        hackType = default;
        return false;
    }

    /// <summary>
    /// Gets the canonical public name for a target.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The target is unknown.</exception>
    public static string GetCanonicalName(XeBuildHackType hackType)
    {
        return hackType switch
        {
            XeBuildHackType.Retail => "retail",
            XeBuildHackType.Glitch => "glitch",
            XeBuildHackType.Jtag => "jtag",
            XeBuildHackType.Glitch2 => "glitch2",
            XeBuildHackType.Glitch2m => "glitch2m",
            XeBuildHackType.DevGl => "devgl",
            XeBuildHackType.DevGl16 => "devgl16",
            XeBuildHackType.Devkit => "devkit",
            XeBuildHackType.Devkit16 => "devkit16",
            XeBuildHackType.Testkit => "testkit",
            XeBuildHackType.Testkit16 => "testkit16",
            _ => throw new ArgumentOutOfRangeException(nameof(hackType), hackType, "The XeBuild hack type is not supported."),
        };
    }
}

/// <summary>
/// Selects an optional XL drive patch supplied by the dashboard's <c>bin</c> directory.
/// </summary>
public enum XeBuildDrivePatch
{
    /// <summary>Does not apply an XL drive patch.</summary>
    None = 0,

    /// <summary>Applies the XL USB patch.</summary>
    Usb = 1,

    /// <summary>Applies the XL HDD patch.</summary>
    Hdd = 2,

    /// <summary>Applies the combined XL USB and HDD patch.</summary>
    Both = 3,
}
