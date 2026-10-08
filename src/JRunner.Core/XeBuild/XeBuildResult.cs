namespace JRunner.Core.XeBuild;

/// <summary>
/// Safe metadata from a successfully validated and atomically published XeBuild image.
/// </summary>
public sealed record XeBuildResult
{
    /// <summary>
    /// Creates result metadata for an atomically published image.
    /// </summary>
    public XeBuildResult(string outputPath, long outputByteLength, XeBuildBackendKind backend)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (outputByteLength <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outputByteLength),
                outputByteLength,
                "A published XeBuild image must be nonempty.");
        }

        if (!Enum.IsDefined(backend))
        {
            throw new ArgumentOutOfRangeException(nameof(backend), backend, "The XeBuild backend is not supported.");
        }

        OutputPath = outputPath;
        OutputByteLength = outputByteLength;
        Backend = backend;
    }

    /// <summary>
    /// Gets the published output path.
    /// </summary>
    public string OutputPath { get; }

    /// <summary>
    /// Gets the published output length in bytes.
    /// </summary>
    public long OutputByteLength { get; }

    /// <summary>
    /// Gets the backend that completed the operation.
    /// </summary>
    public XeBuildBackendKind Backend { get; }
}
