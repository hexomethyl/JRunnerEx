using System.Collections.Immutable;
using JRunner.Core.Support;

namespace JRunner.Core.XeBuild.Preparation;

/// <summary>
/// Immutable support-file facts used by xeBuild preparation without reading or mutating a support root.
/// </summary>
public sealed class XeBuildSupportIndex
{
    private readonly ImmutableHashSet<string> _files;

    /// <summary>
    /// Creates an index from normalized relative support-file paths.
    /// </summary>
    public XeBuildSupportIndex(IEnumerable<string> filePaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        var files = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (string filePath in filePaths)
        {
            if (!IsNormalizedSupportPath(filePath))
            {
                throw new ArgumentException("Support file paths must be normalized relative descendants.", nameof(filePaths));
            }

            files.Add(filePath);
        }

        _files = files.ToImmutable();
    }

    /// <summary>
    /// Builds an index from an already validated immutable support manifest.
    /// </summary>
    public static XeBuildSupportIndex FromManifest(SupportManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return new XeBuildSupportIndex(manifest.Files.Select(file => file.Path));
    }

    /// <summary>
    /// Gets whether the indexed payload contains an exact normalized relative file path.
    /// </summary>
    public bool ContainsFile(string relativePath)
    {
        return IsNormalizedSupportPath(relativePath) && _files.Contains(relativePath);
    }

    /// <summary>
    /// Gets every indexed file beneath one normalized support-relative directory in deterministic ordinal order.
    /// </summary>
    /// <param name="relativeDirectory">A normalized directory such as <c>xeBuild/common/</c>.</param>
    /// <exception cref="ArgumentException">The directory is not a normalized support descendant.</exception>
    public ImmutableArray<string> GetFilesUnder(string relativeDirectory)
    {
        if (!IsNormalizedSupportDirectory(relativeDirectory))
        {
            throw new ArgumentException(
                "Support directories must be normalized relative descendants ending with '/'.",
                nameof(relativeDirectory));
        }

        return _files
            .Where(file => file.StartsWith(relativeDirectory, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static bool IsNormalizedSupportDirectory(string? value)
    {
        return value is { Length: > 1 } &&
            value.EndsWith("/", StringComparison.Ordinal) &&
            IsNormalizedSupportPath(value[..^1]);
    }

    private static bool IsNormalizedSupportPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            Path.IsPathRooted(value) ||
            value.Contains(':', StringComparison.Ordinal) ||
            value.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> path = value.AsSpan();
        if (!path.StartsWith("common/", StringComparison.Ordinal) &&
            !path.StartsWith("xeBuild/", StringComparison.Ordinal))
        {
            return false;
        }

        int segmentStart = 0;
        while (segmentStart < path.Length)
        {
            int separator = path[segmentStart..].IndexOf('/');
            ReadOnlySpan<char> segment = separator < 0
                ? path[segmentStart..]
                : path.Slice(segmentStart, separator);
            if (segment.IsEmpty ||
                segment.SequenceEqual(".") ||
                segment.SequenceEqual(".."))
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
