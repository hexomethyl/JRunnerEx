using JRunner.Core.Contracts;
using Microsoft.Win32.SafeHandles;

namespace JRunner.Cli.Infrastructure;

/// <summary>
/// Shares destination preflight, descriptor-bound temporary creation, and atomic publication rules
/// between managed and externally written outputs.
/// </summary>
internal static class AtomicOutputPath
{
    private const int TemporaryFileAttempts = 10;

    internal static AtomicOutputDestination Preflight(string destinationPath, bool force)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new OperationFailureException(
                ExitCode.Usage,
                "invalid-destination",
                "An output destination is required.");
        }

        string fullDestinationPath = GetFullDestinationPath(destinationPath);
        string? destinationDirectory = Path.GetDirectoryName(fullDestinationPath);
        if (destinationDirectory is null || string.IsNullOrEmpty(Path.GetFileName(fullDestinationPath)))
        {
            throw InvalidDestinationPath();
        }

        var destination = new AtomicOutputDestination(fullDestinationPath, destinationDirectory, force);
        using AtomicOutputDirectory directory = OpenDirectory(destination);
        return destination;
    }

    internal static AtomicOutputDirectory OpenDirectory(AtomicOutputDestination destination)
    {
        AtomicOutputDirectory directory = AtomicOutputDirectory.Open(destination.DestinationDirectory);
        try
        {
            directory.EnsureDestinationAvailable(destination);
            // Nonwriting destination failures take precedence over directory trust failures.
            directory.EnsurePathSafe();
            return directory;
        }
        catch
        {
            directory.Dispose();
            throw;
        }
    }

    internal static FileStream CreateTemporaryFile(
        AtomicOutputDestination destination,
        AtomicOutputDirectory directory,
        FileShare fileShare,
        out string temporaryPath)
    {
        string destinationName = Path.GetFileName(destination.DestinationPath);
        temporaryPath = string.Empty;

        for (int attempt = 0; attempt < TemporaryFileAttempts; attempt++)
        {
            string candidateName = $".{destinationName}.{Guid.NewGuid():N}.tmp";
            FileStream? stream = directory.TryCreateTemporaryFile(candidateName, fileShare);
            if (stream is null)
            {
                continue;
            }

            try
            {
                directory.EnsurePathSafe();
                temporaryPath = Path.Combine(directory.DirectoryPath, candidateName);
                return stream;
            }
            catch
            {
                try
                {
                    stream.Dispose();
                }
                finally
                {
                    directory.DeleteTemporaryFile(candidateName);
                }
                throw;
            }
        }

        throw new IOException("A unique temporary output file could not be created.");
    }

    internal static void Publish(AtomicOutputDestination destination, AtomicOutputDirectory directory,
        string temporaryPath, SafeFileHandle handle, AtomicOutputFileState expected, bool externalOutput)
    {
        directory.Publish(destination, Path.GetFileName(temporaryPath), handle, expected, externalOutput);
    }

    internal static void DeleteTemporaryFile(AtomicOutputDirectory directory, string temporaryPath)
    {
        directory.DeleteTemporaryFile(Path.GetFileName(temporaryPath));
    }

    private static string GetFullDestinationPath(string destinationPath)
    {
        try
        {
            return Path.GetFullPath(destinationPath);
        }
        catch (ArgumentException)
        {
            throw InvalidDestinationPath();
        }
        catch (NotSupportedException)
        {
            throw InvalidDestinationPath();
        }
        catch (PathTooLongException)
        {
            throw InvalidDestinationPath();
        }
    }

    internal static OperationFailureException InvalidDestinationPath()
    {
        return new OperationFailureException(
            ExitCode.Usage,
            "invalid-destination",
            "The output destination is invalid.");
    }

    internal static OperationFailureException DestinationAlreadyExists()
    {
        return new OperationFailureException(
            ExitCode.Usage,
            "destination-exists",
            "The output destination already exists. Use force to replace it.");
    }

    internal static OperationFailureException MissingDirectory()
    {
        return new OperationFailureException(
            ExitCode.Usage,
            "destination-directory-missing",
            "The output destination directory does not exist.");
    }

    internal static OperationFailureException UnsafePath()
    {
        return new OperationFailureException(
            ExitCode.InvalidData,
            "workspace-path-unsafe",
            "The output path must not contain symbolic links.");
    }

    internal static OperationFailureException UnsafeDirectory()
    {
        return new OperationFailureException(
            ExitCode.Usage,
            "output-directory-unsafe",
            "Use an output directory owned by you or root; group/other-writable directories must have the sticky bit. " +
            "A private directory is recommended, for example mkdir -m 700 \"$HOME/jrunner-output\".");
    }

    internal static OperationFailureException OutputChanged(bool externalOutput)
    {
        return externalOutput
            ? new OperationFailureException(
                ExitCode.ExternalProcess,
                "external-output-changed",
                "The external process output changed during validation or before publication.")
            : new OperationFailureException(
                ExitCode.InputOutput,
                "output-path-changed",
                "The output directory or temporary file changed before publication.");
    }

    internal static OperationFailureException ExternalOutputMissing()
    {
        return new OperationFailureException(
            ExitCode.ExternalProcess,
            "external-output-missing",
            "The external process did not produce an output file.");
    }

    internal static OperationFailureException ExternalOutputEmpty()
    {
        return new OperationFailureException(
            ExitCode.ExternalProcess,
            "external-output-empty",
            "The external process produced an empty output file.");
    }

    internal static OperationFailureException ExternalOutputInvalid()
    {
        return new OperationFailureException(
            ExitCode.ExternalProcess,
            "external-output-invalid",
            "The external process output must be a regular file owned by you, without hard links or group/other write permission.");
    }
}

internal readonly record struct AtomicOutputDestination(
    string DestinationPath,
    string DestinationDirectory,
    bool Force);
