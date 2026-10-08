namespace JRunner.Core.XeBuild.Preparation;

/// <summary>
/// Identifies one ordered post-build operation that the isolated XeBuild executor must perform only after a successful XeBuild invocation.
/// </summary>
public enum XeBuildPostBuildOperation
{
    /// <summary>
    /// Repairs the legacy XeBuild patch-slot and key-vault header fields for affected console layouts.
    /// </summary>
    RepairXeBuildImage = 1,

    /// <summary>
    /// Converts the generated image with the XDKBuild utility.
    /// </summary>
    RunXdkBuild = 2,

    /// <summary>
    /// Converts the generated image to RGH3 using the all-zero key required by the sequenced legacy flow.
    /// </summary>
    ConvertRgh2ToRgh3WithZeroCpuKey = 3,

    /// <summary>
    /// Converts the generated image to RGH3 using the caller's physical CPU key.
    /// </summary>
    ConvertRgh2ToRgh3WithPhysicalCpuKey = 4,

    /// <summary>
    /// Zero-pairs the Devkit SB after a successful 64 MB DevGL build.
    /// </summary>
    ZeroPairDevkitSb = 5,
}
