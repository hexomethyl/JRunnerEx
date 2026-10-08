namespace JRunner.Core.XeBuild;

/// <summary>
/// Identifies a XeBuild execution backend.
/// </summary>
public enum XeBuildBackendKind
{
    /// <summary>
    /// Runs the official Windows XeBuild executable through Wine.
    /// </summary>
    Wine = 1,

    /// <summary>
    /// Reserves the native backend selection for a future verified implementation.
    /// </summary>
    Native = 2,
}

/// <summary>
/// Parses and formats the canonical public backend names.
/// </summary>
public static class XeBuildBackendCatalog
{
    /// <summary>
    /// Attempts to resolve a canonical backend name. Matching is case-insensitive, but aliases are not accepted.
    /// </summary>
    public static bool TryGetByCanonicalName(string? value, out XeBuildBackendKind backend)
    {
        if (string.Equals(value, "wine", StringComparison.OrdinalIgnoreCase))
        {
            backend = XeBuildBackendKind.Wine;
            return true;
        }

        if (string.Equals(value, "native", StringComparison.OrdinalIgnoreCase))
        {
            backend = XeBuildBackendKind.Native;
            return true;
        }

        backend = default;
        return false;
    }

    /// <summary>
    /// Gets the canonical public name for a supported backend.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The backend is unknown.</exception>
    public static string GetCanonicalName(XeBuildBackendKind backend)
    {
        return backend switch
        {
            XeBuildBackendKind.Wine => "wine",
            XeBuildBackendKind.Native => "native",
            _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, "The XeBuild backend is not supported."),
        };
    }
}
