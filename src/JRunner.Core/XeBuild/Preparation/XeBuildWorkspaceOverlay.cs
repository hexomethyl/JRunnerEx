namespace JRunner.Core.XeBuild.Preparation;

/// <summary>
/// Describes one immutable support file copied over a mutable path in an isolated XeBuild workspace.
/// </summary>
public sealed record XeBuildWorkspaceOverlay
{
    internal XeBuildWorkspaceOverlay(string sourceSupportPath, string destinationWorkspacePath)
    {
        if (!IsNormalizedWorkspacePath(sourceSupportPath) ||
            !IsNormalizedWorkspacePath(destinationWorkspacePath))
        {
            throw new ArgumentException("XeBuild workspace overlay paths must be normalized relative xeBuild descendants.");
        }

        SourceSupportPath = sourceSupportPath;
        DestinationWorkspacePath = destinationWorkspacePath;
    }

    /// <summary>
    /// Gets the normalized support-relative source file path.
    /// </summary>
    public string SourceSupportPath { get; }

    /// <summary>
    /// Gets the normalized path to overwrite in the isolated workspace.
    /// </summary>
    public string DestinationWorkspacePath { get; }

    private static bool IsNormalizedWorkspacePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            Path.IsPathRooted(value) ||
            value.Contains(':', StringComparison.Ordinal) ||
            value.Contains('\\', StringComparison.Ordinal) ||
            !value.StartsWith("xeBuild/", StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> path = value.AsSpan();
        int segmentStart = 0;
        while (segmentStart < path.Length)
        {
            int separator = path[segmentStart..].IndexOf('/');
            ReadOnlySpan<char> segment = separator < 0
                ? path[segmentStart..]
                : path.Slice(segmentStart, separator);
            if (segment.IsEmpty || segment.SequenceEqual(".") || segment.SequenceEqual(".."))
            {
                return false;
            }

            if (separator < 0)
            {
                return true;
            }

            segmentStart += separator + 1;
        }

        return false;
    }
}
