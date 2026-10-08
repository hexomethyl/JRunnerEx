namespace JRunner.Core.Devices.PicoFlasher;

/// <summary>
/// Opens transport connections to PicoFlasher command CDC endpoints.
/// </summary>
public interface IPicoFlasherTransportFactory
{
    /// <summary>
    /// Opens a transport connection for the specified command CDC endpoint.
    /// </summary>
    /// <param name="endpoint">The endpoint to open.</param>
    /// <param name="noProgressTimeout">
    /// The positive maximum time the transport may receive no response progress before failing the operation. It must
    /// satisfy <see cref="PicoFlasherProtocol.IsValidNoProgressTimeout"/>.
    /// </param>
    /// <param name="cancellationToken">A token that can cancel opening the endpoint.</param>
    /// <returns>An asynchronously disposable transport for <paramref name="endpoint"/>.</returns>
    ValueTask<IPicoFlasherTransport> OpenAsync(
        PicoFlasherDeviceEndpoint endpoint,
        TimeSpan noProgressTimeout,
        CancellationToken cancellationToken = default);
}
