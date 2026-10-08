namespace JRunner.Core.Devices.PicoFlasher;

/// <summary>
/// Provides byte transport access to one opened PicoFlasher command CDC endpoint.
/// </summary>
/// <remarks>
/// Each write completes the whole supplied buffer, and each read completes only after filling the whole requested buffer.
/// Command exchanges are caller-serialized: callers must not overlap commands or their corresponding reads. The
/// no-progress deadline configured while opening the transport is reset whenever a read receives one or more bytes;
/// it is not a total transfer deadline.
/// </remarks>
public interface IPicoFlasherTransport : IAsyncDisposable
{
    /// <summary>
    /// Writes the complete source buffer to the command endpoint.
    /// </summary>
    /// <param name="source">The bytes to write.</param>
    /// <param name="cancellationToken">A token that can cancel the write.</param>
    /// <returns>A task that completes only after all bytes in <paramref name="source"/> have been written.</returns>
    ValueTask WriteAsync(
        ReadOnlyMemory<byte> source,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads exactly enough bytes to fill the destination buffer from the command endpoint.
    /// </summary>
    /// <param name="destination">The buffer to fill.</param>
    /// <param name="cancellationToken">A token that can cancel the read.</param>
    /// <returns>A task that completes only after all bytes in <paramref name="destination"/> have been read.</returns>
    ValueTask ReadExactlyAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken = default);
}
