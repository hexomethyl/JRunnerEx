namespace JRunner.Core.XeBuild;

/// <summary>
/// Executes one fully specified XeBuild operation through a selected real backend.
/// </summary>
/// <remarks>
/// The interface intentionally has no native implementation until native XeBuild source and
/// byte-for-byte parity are available. Implementations must reject a request for another backend
/// rather than silently falling back.
/// </remarks>
public interface IXeBuildBackend
{
    /// <summary>
    /// Gets the backend represented by this implementation.
    /// </summary>
    XeBuildBackendKind Kind { get; }

    /// <summary>
    /// Builds and atomically publishes the requested image.
    /// </summary>
    /// <param name="request">The complete, typed execution request.</param>
    /// <param name="cancellationToken">Cancels the operation and requires cleanup before completion.</param>
    /// <returns>Safe metadata for the atomically published output.</returns>
    Task<XeBuildResult> BuildAsync(
        XeBuildRequest request,
        CancellationToken cancellationToken = default);
}
