using System.Collections.ObjectModel;
using System.Text;

namespace JRunner.Cli.Infrastructure;

/// <summary>
/// An immutable, tokenized external-process invocation with explicit environment updates.
/// </summary>
internal sealed class ExternalProcessInvocation
{
    /// <summary>
    /// The maximum byte count accepted for an optional standard-input payload.
    /// </summary>
    public const int MaximumStandardInputBytes = 64 * 1024;

    private readonly byte[]? _standardInput;

    /// <summary>
    /// Initializes a closed invocation. Each argument is passed as one process argument rather than shell text.
    /// A <see langword="null"/> environment value removes that variable from the child environment.
    /// </summary>
    public ExternalProcessInvocation(
        string executablePath,
        IEnumerable<string> arguments,
        string workingDirectory,
        IEnumerable<KeyValuePair<string, string?>>? environmentUpdates = null,
        ReadOnlyMemory<byte>? standardInput = null)
        : this(
            executablePath,
            arguments,
            workingDirectory,
            environmentUpdates,
            CopyStandardInput(standardInput))
    {
    }

    /// <summary>
    /// Initializes a closed invocation with a bounded UTF-8 standard-input payload.
    /// </summary>
    public ExternalProcessInvocation(
        string executablePath,
        IEnumerable<string> arguments,
        string workingDirectory,
        string standardInput,
        IEnumerable<KeyValuePair<string, string?>>? environmentUpdates = null)
        : this(
            executablePath,
            arguments,
            workingDirectory,
            environmentUpdates,
            EncodeStandardInput(standardInput))
    {
    }

    private ExternalProcessInvocation(
        string executablePath,
        IEnumerable<string> arguments,
        string workingDirectory,
        IEnumerable<KeyValuePair<string, string?>>? environmentUpdates,
        byte[]? standardInput)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("An executable path is required.", nameof(executablePath));
        }

        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            throw new ArgumentException("A working directory is required.", nameof(workingDirectory));
        }

        ArgumentNullException.ThrowIfNull(arguments);

        var copiedArguments = new List<string>();
        foreach (string argument in arguments)
        {
            if (string.IsNullOrEmpty(argument))
            {
                throw new ArgumentException("Process argument tokens cannot be empty.", nameof(arguments));
            }

            copiedArguments.Add(argument);
        }

        var copiedEnvironmentUpdates = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (environmentUpdates is not null)
        {
            foreach (KeyValuePair<string, string?> update in environmentUpdates)
            {
                if (string.IsNullOrWhiteSpace(update.Key) || update.Key.Contains('='))
                {
                    throw new ArgumentException(
                        "Environment variable names must be nonempty and cannot contain '='.",
                        nameof(environmentUpdates));
                }

                if (!copiedEnvironmentUpdates.TryAdd(update.Key, update.Value))
                {
                    throw new ArgumentException(
                        "Environment variable updates cannot contain duplicate names.",
                        nameof(environmentUpdates));
                }
            }
        }

        ExecutablePath = executablePath;
        WorkingDirectory = workingDirectory;
        Arguments = new ReadOnlyCollection<string>(copiedArguments);
        EnvironmentUpdates = new ReadOnlyDictionary<string, string?>(copiedEnvironmentUpdates);
        _standardInput = standardInput;
    }

    /// <summary>
    /// Gets the executable path or executable name resolved by the operating system.
    /// </summary>
    public string ExecutablePath { get; }

    /// <summary>
    /// Gets the immutable argument tokens passed through <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>.
    /// </summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>
    /// Gets the working directory supplied to the child process.
    /// </summary>
    public string WorkingDirectory { get; }

    /// <summary>
    /// Gets immutable environment updates. A <see langword="null"/> value removes a variable.
    /// </summary>
    public IReadOnlyDictionary<string, string?> EnvironmentUpdates { get; }

    /// <summary>
    /// Gets the prepared production supervisor chain. Null preserves unrelated and synthetic invocations.
    /// A marked invocation requires a protected absolute executable alias and never repeats host selection.
    /// </summary>
    internal ExternalProcessSupervisorLaunch? TrustedSupervisorLaunch { get; init; }

    /// <summary>
    /// Gets the immutable native loader and module proof for the marked supervisor and invocation.
    /// Production trust requires both closures; an unmarked synthetic invocation leaves both null.
    /// </summary>
    internal NativeDependencyClosure? TrustedNativeClosure { get; init; }

    /// <summary>
    /// Gets whether this invocation explicitly supplies and closes standard input for the child.
    /// </summary>
    public bool HasStandardInput => _standardInput is not null;

    /// <summary>
    /// Gets the immutable bounded standard-input bytes for the process runner.
    /// </summary>
    internal ReadOnlyMemory<byte> StandardInputBytes =>
        _standardInput is null ? ReadOnlyMemory<byte>.Empty : _standardInput;

    private static byte[]? CopyStandardInput(ReadOnlyMemory<byte>? standardInput)
    {
        if (!standardInput.HasValue)
        {
            return null;
        }

        EnsureStandardInputLength(standardInput.Value.Length, nameof(standardInput));
        return standardInput.Value.ToArray();
    }

    private static byte[] EncodeStandardInput(string standardInputText)
    {
        ArgumentNullException.ThrowIfNull(standardInputText);
        int byteCount = Encoding.UTF8.GetByteCount(standardInputText);
        EnsureStandardInputLength(byteCount, nameof(standardInputText));
        return Encoding.UTF8.GetBytes(standardInputText);
    }

    private static void EnsureStandardInputLength(int length, string parameterName)
    {
        if (length > MaximumStandardInputBytes)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                length,
                $"The standard-input payload cannot exceed {MaximumStandardInputBytes} bytes.");
        }
    }
}
