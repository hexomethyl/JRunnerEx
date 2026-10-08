namespace JRunner.Core.Devices.PicoFlasher;

/// <summary>
/// Enumerates deterministic PicoFlasher physical-device candidates available on the host.
/// </summary>
public interface IPicoFlasherDeviceEnumerator
{
    /// <summary>
    /// Enumerates the currently available command CDC candidates without opening a transport.
    /// </summary>
    /// <param name="cancellationToken">A token that can cancel enumeration.</param>
    /// <returns>
    /// Immutable, physical-device-deduplicated command endpoint descriptions sorted by physical-device identity.
    /// </returns>
    ValueTask<IReadOnlyList<PicoFlasherDeviceEndpoint>> EnumerateAsync(
        CancellationToken cancellationToken = default);
}
