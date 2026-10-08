namespace JRunner.Core.XeBuild;

/// <summary>
/// Selects the explicit staging treatment for a full 4 GB eMMC source.
/// </summary>
public enum XeBuildFourGigabyteStagingPolicy
{
    /// <summary>
    /// No 4 GB staging policy was selected.
    /// </summary>
    None = 0,

    /// <summary>
    /// Stages only the legacy 48 MiB system partition.
    /// </summary>
    SystemPartitionOnly = 1,

    /// <summary>
    /// Stages the complete 4 GB source as XeBuild data.
    /// </summary>
    FullData = 2,
}

/// <summary>
/// Execution-only policies that do not affect XeBuild image semantics.
/// </summary>
public sealed record XeBuildExecutionOptions
{
    /// <summary>
    /// Creates execution policies for one XeBuild request.
    /// </summary>
    public XeBuildExecutionOptions(
        XeBuildFourGigabyteStagingPolicy fourGigabyteStagingPolicy = XeBuildFourGigabyteStagingPolicy.None,
        XeBuildBackendKind backend = XeBuildBackendKind.Wine,
        string? workspaceRootPath = null,
        bool keepWorkspace = false,
        bool overwriteExistingOutput = false)
    {
        if (!Enum.IsDefined(fourGigabyteStagingPolicy))
        {
            throw new ArgumentOutOfRangeException(
                nameof(fourGigabyteStagingPolicy),
                fourGigabyteStagingPolicy,
                "The 4 GB staging policy is not supported.");
        }

        if (!Enum.IsDefined(backend))
        {
            throw new ArgumentOutOfRangeException(nameof(backend), backend, "The XeBuild backend is not supported.");
        }

        if (workspaceRootPath is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRootPath);
        }

        FourGigabyteStagingPolicy = fourGigabyteStagingPolicy;
        Backend = backend;
        WorkspaceRootPath = workspaceRootPath;
        KeepWorkspace = keepWorkspace;
        OverwriteExistingOutput = overwriteExistingOutput;
    }

    /// <summary>
    /// Gets the requested full-eMMC staging treatment.
    /// </summary>
    public XeBuildFourGigabyteStagingPolicy FourGigabyteStagingPolicy { get; }

    /// <summary>
    /// Gets the backend explicitly requested by the caller.
    /// </summary>
    public XeBuildBackendKind Backend { get; }

    /// <summary>
    /// Gets an optional caller-managed workspace parent path.
    /// </summary>
    public string? WorkspaceRootPath { get; }

    /// <summary>
    /// Gets whether non-secret workspace diagnostics should remain after a failure.
    /// </summary>
    public bool KeepWorkspace { get; }

    /// <summary>
    /// Gets whether an existing destination may be atomically replaced.
    /// </summary>
    public bool OverwriteExistingOutput { get; }
}
