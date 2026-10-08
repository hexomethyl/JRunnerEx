using Microsoft.Win32.SafeHandles;

namespace JRunner.Cli.Infrastructure;

/// <summary>
/// Owns a uniquely named sibling temporary file and publishes it only after explicit completion.
/// </summary>
public sealed class AtomicOutputFile : IDisposable, IAsyncDisposable
{
    private readonly AtomicOutputDestination _destination;
    private readonly AtomicOutputDirectory _directory;
    private readonly AtomicOutputFileIdentity _identity;
    private SafeFileHandle? _identityHandle;

    private FileStream? _stream;
    private bool _completionStarted;
    private bool _completed;
    private bool _disposed;
    private bool _resourcesReleased;

    private AtomicOutputFile(AtomicOutputDestination destination, AtomicOutputDirectory directory,
        string temporaryPath, FileStream stream, SafeFileHandle identityHandle, AtomicOutputFileIdentity identity)
    {
        _destination = destination;
        _directory = directory;
        _identity = identity;
        _identityHandle = identityHandle;
        DestinationPath = destination.DestinationPath;
        TemporaryPath = temporaryPath;
        Force = destination.Force;
        _stream = stream;
    }

    public string DestinationPath { get; }

    public string TemporaryPath { get; }

    public bool Force { get; }

    /// <summary>
    /// Gets the temporary output stream. This object closes the stream during completion or disposal.
    /// </summary>
    public Stream Stream
    {
        get
        {
            ThrowIfDisposed();
            return _stream ?? throw new InvalidOperationException("The temporary output stream is no longer available.");
        }
    }

    /// <summary>
    /// Preflights a destination and opens a uniquely named temporary sibling file for writing.
    /// </summary>
    public static AtomicOutputFile Create(string destinationPath, bool force)
    {
        AtomicOutputDestination destination = AtomicOutputPath.Preflight(destinationPath, force);
        return CreateTemporaryFile(destination);
    }

    /// <summary>
    /// Flushes and closes the temporary file, then atomically publishes it to the destination.
    /// </summary>
    public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_completionStarted || _completed)
        {
            throw new InvalidOperationException("The temporary output has already been completed or failed.");
        }

        _completionStarted = true;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var stream = _stream ?? throw new InvalidOperationException("The temporary output stream is no longer available.");
            try
            {
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // A caller may have already closed a completed writer before publishing it.
            }

            CloseStream();
            cancellationToken.ThrowIfCancellationRequested();

            SafeFileHandle handle = _identityHandle
                ?? throw new InvalidOperationException("The temporary output identity is no longer available.");
            AtomicOutputFileState state = _directory.GetFileState(handle, externalOutput: false);
            if (state.Identity != _identity)
            {
                throw AtomicOutputPath.OutputChanged(externalOutput: false);
            }
            AtomicOutputPath.Publish(_destination, _directory, TemporaryPath, handle, state, externalOutput: false);
            _completed = true;

            // Publication is irreversible. Releasing handles must not turn success into failure.
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
            GC.SuppressFinalize(this);
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private static AtomicOutputFile CreateTemporaryFile(AtomicOutputDestination destination)
    {
        AtomicOutputDirectory directory = AtomicOutputPath.OpenDirectory(destination);
        FileStream? stream = null;
        SafeFileHandle? identityHandle = null;
        string temporaryPath = string.Empty;
        try
        {
            stream = AtomicOutputPath.CreateTemporaryFile(
                destination,
                directory,
                FileShare.ReadWrite | FileShare.Delete,
                out temporaryPath);
            identityHandle = AtomicOutputDirectory.DuplicateHandle(stream.SafeFileHandle);
            AtomicOutputFileState state = directory.GetFileState(identityHandle, externalOutput: false);
            return new AtomicOutputFile(destination, directory, temporaryPath, stream, identityHandle, state.Identity);
        }
        catch
        {
            try
            {
                try
                {
                    stream?.Dispose();
                }
                finally
                {
                    if (temporaryPath.Length > 0)
                    {
                        AtomicOutputPath.DeleteTemporaryFile(directory, temporaryPath);
                    }
                }
            }
            finally
            {
                identityHandle?.Dispose();
                directory.Dispose();
            }
            throw;
        }
    }

    private void CloseStream()
    {
        var stream = _stream;
        _stream = null;
        stream?.Dispose();
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
                CloseStream();
            }
            finally
            {
                try
                {
                    if (!_completed)
                    {
                        AtomicOutputPath.DeleteTemporaryFile(_directory, TemporaryPath);
                    }
                }
                finally
                {
                    _identityHandle?.Dispose();
                    _identityHandle = null;
                    _directory.Dispose();
                }
            }
        }
        catch when (suppressErrors)
        {
            // Preserve the primary failure, or the already committed success marker.
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
