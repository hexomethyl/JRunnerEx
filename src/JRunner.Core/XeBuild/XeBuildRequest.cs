using System.Text.Json.Serialization;
using JRunner.Core.Nand.Security;
using JRunner.Core.XeBuild.Preparation;

namespace JRunner.Core.XeBuild;

/// <summary>
/// Complete, typed input to one XeBuild backend operation.
/// </summary>
/// <remarks>
/// The CPU key is represented only by <see cref="CpuKey"/>, whose text and JSON representations are
/// redacted or rejected. This request must never be rendered as a CLI result or process argument list.
/// </remarks>
public sealed record XeBuildRequest
{
    /// <summary>
    /// Creates one XeBuild execution request.
    /// </summary>
    public XeBuildRequest(
        string supportRootPath,
        XeBuildSourceContext source,
        CpuKey cpuKey,
        string outputPath,
        XeBuildBuildTarget target,
        XeBuildExecutionOptions? execution = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(supportRootPath);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(target);
        if (!cpuKey.IsInitialized)
        {
            throw new ArgumentException("A parsed CPU key is required for XeBuild.", nameof(cpuKey));
        }

        SupportRootPath = supportRootPath;
        Source = source;
        CpuKey = cpuKey;
        OutputPath = outputPath;
        Target = target;
        Execution = execution ?? new XeBuildExecutionOptions();
    }

    /// <summary>
    /// Gets the resolved support payload root supplied by the CLI.
    /// </summary>
    public string SupportRootPath { get; }

    /// <summary>
    /// Gets safe facts about the source image and its caller-owned path.
    /// </summary>
    public XeBuildSourceContext Source { get; }

    /// <summary>
    /// Gets the parsed CPU key used only by the backend's private staging and validation paths.
    /// </summary>
    [JsonIgnore]
    public CpuKey CpuKey { get; }

    /// <summary>
    /// Gets the requested final output path. Backends must pass only a sibling temporary path to XeBuild.
    /// </summary>
    public string OutputPath { get; }

    /// <summary>
    /// Gets the validated target selection.
    /// </summary>
    public XeBuildBuildTarget Target { get; }

    /// <summary>
    /// Gets execution-only backend, staging, and filesystem policies.
    /// </summary>
    public XeBuildExecutionOptions Execution { get; }
}
