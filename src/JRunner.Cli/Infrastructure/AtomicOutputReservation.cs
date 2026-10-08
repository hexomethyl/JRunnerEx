using System.Buffers;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace JRunner.Cli.Infrastructure;

/// <summary>
/// Owns a private sibling staging directory and atomically publishes its bound, validated external output.
/// </summary>
internal sealed class AtomicOutputReservation : IDisposable, IAsyncDisposable
{
    private readonly AtomicOutputDestination _destination;
    private readonly AtomicOutputDirectory _directory;
    private readonly AtomicOutputDirectory _stagingDirectory;
    private readonly string _stagingName;
    private readonly string _temporaryName;
    private string? _publicationName;
    private bool _stagingRemoved;
    private FileStream? _outputStream;
    private SafeFileHandle? _outputHandle;
    private AtomicOutputFileState _validatedState;
    private byte[]? _validatedDigest;
    private bool _validationStarted;
    private bool _completionStarted;
    private bool _completed;
    private bool _disposed;
    private bool _resourcesReleased;

    private AtomicOutputReservation(AtomicOutputDestination destination, AtomicOutputDirectory directory,
        AtomicOutputDirectory stagingDirectory, string stagingName, string temporaryPath)
    {
        _destination = destination;
        _directory = directory;
        _stagingDirectory = stagingDirectory;
        _stagingName = stagingName;
        _temporaryName = Path.GetFileName(temporaryPath);
        DestinationPath = destination.DestinationPath;
        TemporaryPath = temporaryPath;
        Force = destination.Force;
    }

    /// <summary>
    /// Gets the normalized requested destination path.
    /// </summary>
    public string DestinationPath { get; }

    /// <summary>
    /// Gets the external writer's temporary path inside an operation-owned private sibling directory.
    /// </summary>
    public string TemporaryPath { get; }

    /// <summary>
    /// Gets whether an existing destination may be atomically replaced.
    /// </summary>
    public bool Force { get; }

    /// <summary>
    /// Preflights a destination and reserves a temporary file in a private sibling staging directory.
    /// </summary>
    public static AtomicOutputReservation Create(string destinationPath, bool force)
    {
        return Create(AtomicOutputPath.Preflight(destinationPath, force));
    }

    /// <summary>
    /// Creates a private sibling staging directory after preflighting immutable destination facts.
    /// </summary>
    internal static AtomicOutputReservation Create(AtomicOutputDestination destination)
    {
        AtomicOutputDirectory directory = AtomicOutputPath.OpenDirectory(destination);
        AtomicOutputDirectory? staging = null;
        string stagingName = $".jrunner-output-{Guid.NewGuid():N}";
        try
        {
            staging = directory.CreatePrivateStagingDirectory(stagingName);
            using FileStream reservationHandle = AtomicOutputPath.CreateTemporaryFile(
                destination,
                staging,
                FileShare.None,
                out string temporaryPath);
            return new AtomicOutputReservation(destination, directory, staging, stagingName, temporaryPath);
        }
        catch
        {
            try
            {
                if (staging is not null)
                {
                    staging.DeleteContents();
                    directory.RemoveStagingDirectory(stagingName, staging.Identity);
                }
            }
            finally
            {
                staging?.Dispose();
                directory.Dispose();
            }
            throw;
        }
    }

    /// <summary>
    /// Revalidates the bound directory before handing an absolute path to an external process.
    /// </summary>
    internal void EnsurePathSafe()
    {
        ThrowIfDisposed();
        if (_stagingRemoved)
        {
            _directory.EnsurePathSafe();
        }
        else
        {
            _stagingDirectory.EnsurePathSafe();
        }
    }

    /// <summary>
    /// Removes a companion leaf, including a directory, without following links in the private stage.
    /// </summary>
    internal void DeleteSidecarFile(string fileName)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (fileName.Contains('/') || fileName.Contains('\\') || fileName is "." or ".." ||
            string.Equals(fileName, _temporaryName, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new ArgumentException("A sidecar must be a leaf name other than the output image.", nameof(fileName));
        }
        if (!_stagingRemoved)
        {
            _stagingDirectory.DeleteEntry(fileName);
        }
    }

    /// <summary>
    /// Opens the current external output through its bound directory for prevalidation work.
    /// </summary>
    internal FileStream OpenOutput(FileAccess access)
    {
        ThrowIfDisposed();
        if (_completionStarted || _validationStarted)
        {
            throw new InvalidOperationException("The temporary output is no longer available for prevalidation work.");
        }
        try
        {
            return _stagingDirectory.OpenExternalOutput(_temporaryName, access);
        }
        catch
        {
            _completionStarted = true;
            Cleanup(suppressErrors: true);
            throw;
        }
    }

    /// <summary>
    /// Hashes a bound output before semantic validation, then retains its descriptor, identity,
    /// change state, and digest until publication. The callback reads that same unbuffered stream.
    /// </summary>
    internal async Task<long> ValidateAsync(
        Func<Stream, CancellationToken, Task> validate,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(validate);
        if (_completionStarted || _validationStarted)
        {
            throw new InvalidOperationException("The temporary output has already been validated, completed, or failed.");
        }
        _validationStarted = true;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _outputStream = _stagingDirectory.OpenExternalOutput(_temporaryName);
            _outputHandle = _outputStream.SafeFileHandle;
            _validatedState = _stagingDirectory.GetFileState(_outputHandle, externalOutput: true);

            // Both digest passes stop at the captured length, even if a writer keeps extending it.
            byte[] digest = new byte[SHA256.HashSizeInBytes];
            await HashOutputAsync(_outputStream, _validatedState.Length, digest, cancellationToken).ConfigureAwait(false);
            _stagingDirectory.EnsureFileUnchanged(_temporaryName, _outputHandle, _validatedState, externalOutput: true);
            cancellationToken.ThrowIfCancellationRequested();
            _outputStream.Position = 0;
            await validate(_outputStream, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _stagingDirectory.EnsureFileUnchanged(_temporaryName, _outputHandle, _validatedState, externalOutput: true);

            _validatedDigest = digest;
            return _validatedState.Length;
        }
        catch (Exception exception)
        {
            _completionStarted = true;
            try
            {
                if (exception is not OperationCanceledException && _outputHandle is not null)
                {
                    // A concurrent mutation must not hide behind a semantic-validation failure.
                    _stagingDirectory.EnsureFileUnchanged(_temporaryName, _outputHandle, _validatedState, externalOutput: true);
                }
            }
            finally
            {
                Cleanup(suppressErrors: true);
            }
            throw;
        }
    }

    /// <summary>
    /// Rechecks validated bytes and atomically publishes the bound output. Callers that do not
    /// request validation retain the original nonempty-output completion contract.
    /// </summary>
    public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_completionStarted || _completed || _validationStarted && _validatedDigest is null)
        {
            throw new InvalidOperationException("The temporary output has already been completed or failed, or validation is in progress.");
        }
        _completionStarted = true;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _directory.EnsureDestinationAvailable(_destination);
            if (_validatedDigest is null)
            {
                _outputStream = _stagingDirectory.OpenExternalOutput(_temporaryName);
                _outputHandle = _outputStream.SafeFileHandle;
                _validatedState = _stagingDirectory.GetFileState(_outputHandle, externalOutput: true);
            }
            FileStream stream = _outputStream
                ?? throw new InvalidOperationException("The output stream is no longer available.");
            SafeFileHandle handle = _outputHandle
                ?? throw new InvalidOperationException("The output descriptor is no longer available.");
            _stagingDirectory.EnsureFileUnchanged(_temporaryName, handle, _validatedState, externalOutput: true);
            AtomicOutputFileState publicationState = _stagingDirectory.SecureExternalOutput(handle, _validatedState);
            string publicationName = $".{Path.GetFileName(DestinationPath)}.{Guid.NewGuid():N}.tmp";
            cancellationToken.ThrowIfCancellationRequested();
            _stagingDirectory.MoveTo(_directory, _temporaryName, publicationName, handle, publicationState);
            _publicationName = publicationName;
            publicationState = _directory.GetStateAfterOwnMetadataChange(handle, publicationState);

            // Finish every fallible stage cleanup before the irreversible destination rename.
            RemoveStagingDirectory();
            if (_validatedDigest is { } validatedDigest)
            {
                _directory.EnsureFileUnchanged(publicationName, handle, publicationState, externalOutput: true);
                stream.Position = 0;
                byte[] digest = ArrayPool<byte>.Shared.Rent(SHA256.HashSizeInBytes);
                try
                {
                    await HashOutputAsync(stream, _validatedState.Length, digest.AsMemory(0, SHA256.HashSizeInBytes), cancellationToken)
                        .ConfigureAwait(false);
                    if (!CryptographicOperations.FixedTimeEquals(digest.AsSpan(0, SHA256.HashSizeInBytes), validatedDigest))
                    {
                        throw AtomicOutputPath.OutputChanged(externalOutput: true);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(digest);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            _directory.Publish(_destination, publicationName, handle, publicationState, externalOutput: true);
            _completed = true;
            Cleanup(suppressErrors: true);
        }
        catch
        {
            Cleanup(suppressErrors: true);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            Cleanup(suppressErrors: _completed);
        }
        finally
        {
            try
            {
                RemoveStagingDirectory();
            }
            finally
            {
                _stagingDirectory.Dispose();
                _directory.Dispose();
                GC.SuppressFinalize(this);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private static async Task HashOutputAsync(Stream stream, long length, Memory<byte> digest, CancellationToken cancellationToken)
    {
        const int BufferSize = 64 * 1024;
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long remaining = length;
            while (remaining > 0)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, BufferSize)), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    throw AtomicOutputPath.OutputChanged(externalOutput: true);
                }
                hash.AppendData(buffer, 0, read);
                remaining -= read;
            }
            if (!hash.TryGetHashAndReset(digest.Span, out _))
            {
                throw new InvalidOperationException("The SHA256 digest buffer is too small.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void Cleanup(bool suppressErrors)
    {
        if (_resourcesReleased)
        {
            return;
        }
        _resourcesReleased = true;

        try
        {
            try
            {
                _outputStream?.Dispose();
                _outputStream = null;
                _outputHandle = null;
                _validatedDigest = null;
            }
            finally
            {
                if (!_completed)
                {
                    if (_publicationName is { } publicationName)
                    {
                        _directory.DeleteTemporaryFile(publicationName);
                    }
                    else
                    {
                        _stagingDirectory.DeleteTemporaryFile(_temporaryName);
                    }
                }
            }
        }
        catch when (suppressErrors)
        {
            // Preserve the primary failure, or the already committed success marker.
        }
    }

    private void RemoveStagingDirectory()
    {
        if (_stagingRemoved)
        {
            return;
        }
        _directory.PrepareStagingCleanup(_stagingName, _stagingDirectory);
        _stagingDirectory.DeleteContents();
        _directory.RemoveStagingDirectory(_stagingName, _stagingDirectory.Identity);
        _stagingRemoved = true;
        _stagingDirectory.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
