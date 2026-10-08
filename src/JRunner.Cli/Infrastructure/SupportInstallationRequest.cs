namespace JRunner.Cli.Infrastructure;

/// <summary>
/// Identifies the immutable archive source accepted by a support-payload installation.
/// </summary>
public abstract record SupportArchiveSource
{
    protected SupportArchiveSource()
    {
    }

    /// <summary>
    /// Obtains the sole archive pinned by the embedded reviewed manifest.
    /// </summary>
    public sealed record PinnedDownload : SupportArchiveSource;

    /// <summary>
    /// Uses a caller-supplied local archive after it matches the same pinned manifest.
    /// </summary>
    public sealed record LocalFile(string ArchivePath) : SupportArchiveSource;
}

/// <summary>
/// Describes one support-payload installation without coupling the operation to CLI parsing.
/// </summary>
public sealed record SupportInstallationRequest(
    SupportRoot SupportRoot,
    SupportArchiveSource ArchiveSource);
