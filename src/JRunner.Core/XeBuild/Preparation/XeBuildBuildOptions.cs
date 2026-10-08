using System.Collections.Immutable;

namespace JRunner.Core.XeBuild.Preparation;

/// <summary>
/// Selects typed optional XeBuild image behaviors.
/// </summary>
/// <remarks>
/// Legacy one-off public flags are represented as explicit named patches. XDKBuild selection remains
/// an internal compatibility decision derived from the selected dashboard assets.
/// </remarks>
public sealed record XeBuildBuildOptions
{
    /// <summary>
    /// Creates typed optional XeBuild behaviors.
    /// </summary>
    /// <param name="bigFfs">Whether to select a supported BigFFS board configuration.</param>
    /// <param name="rgh3">Whether to run the compatible RGH3 post-build flow.</param>
    /// <param name="dashLaunch">Whether to stage and inject the DashLaunch dependency set.</param>
    /// <param name="drivePatch">The optional typed XL drive patch.</param>
    /// <param name="namedPatches">Ordered canonical XeBuild patch names, without <c>-a</c> tokens.</param>
    public XeBuildBuildOptions(
        bool bigFfs = false,
        bool rgh3 = false,
        bool dashLaunch = false,
        XeBuildDrivePatch drivePatch = XeBuildDrivePatch.None,
        IEnumerable<string>? namedPatches = null)
    {
        if (!Enum.IsDefined(drivePatch))
        {
            throw new ArgumentOutOfRangeException(nameof(drivePatch), drivePatch, "The XeBuild drive patch is not supported.");
        }

        var patches = ImmutableArray.CreateBuilder<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (namedPatches is not null)
        {
            foreach (string patch in namedPatches)
            {
                if (string.IsNullOrWhiteSpace(patch) || !IsCanonicalPatchName(patch))
                {
                    throw new ArgumentException(
                        "XeBuild patch names must contain only ASCII letters, digits, or underscores.",
                        nameof(namedPatches));
                }

                if (!seen.Add(patch))
                {
                    throw new ArgumentException("XeBuild patch names must not be repeated.", nameof(namedPatches));
                }

                patches.Add(patch);
            }
        }

        BigFfs = bigFfs;
        Rgh3 = rgh3;
        DashLaunch = dashLaunch;
        DrivePatch = drivePatch;
        NamedPatches = patches.ToImmutable();
    }

    /// <summary>
    /// Gets whether the target requests the BigFFS board configuration.
    /// </summary>
    public bool BigFfs { get; }

    /// <summary>
    /// Gets whether the target requires the RGH3 post-build flow.
    /// </summary>
    public bool Rgh3 { get; }

    /// <summary>
    /// Gets whether the target must stage and inject DashLaunch assets.
    /// </summary>
    public bool DashLaunch { get; }

    /// <summary>
    /// Gets the selected XL drive patch.
    /// </summary>
    public XeBuildDrivePatch DrivePatch { get; }

    /// <summary>
    /// Gets ordered, caller-requested XeBuild patch names.
    /// </summary>
    public ImmutableArray<string> NamedPatches { get; }

    private static bool IsCanonicalPatchName(string patch)
    {
        foreach (char character in patch)
        {
            if ((character is < 'A' or > 'Z') &&
                (character is < 'a' or > 'z') &&
                (character is < '0' or > '9') &&
                character != '_')
            {
                return false;
            }
        }

        return true;
    }
}
