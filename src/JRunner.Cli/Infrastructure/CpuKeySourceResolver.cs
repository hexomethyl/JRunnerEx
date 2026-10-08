using System.Runtime.InteropServices;
using System.Security.Cryptography;
using JRunner.Core.Contracts;
using JRunner.Core.Nand.Security;

namespace JRunner.Cli.Infrastructure;

/// <summary>
/// Selects and parses one CPU key source without retaining its textual value.
/// Selected environment values are consumed before parsing and are never restored.
/// </summary>
internal sealed class CpuKeySourceResolver
{
    internal const int MaximumFileByteLength = CpuKey.HexadecimalLength + 2;

    private readonly ICpuKeyEnvironment _environment;
    private readonly IStandardInputTerminal _standardInputTerminal;

    internal CpuKeySourceResolver(
        ICpuKeyEnvironment? environment = null,
        IStandardInputTerminal? standardInputTerminal = null)
    {
        _environment = environment ?? ProcessCpuKeyEnvironment.Instance;
        _standardInputTerminal = standardInputTerminal ?? LinuxStandardInputTerminal.Instance;
    }

    /// <summary>
    /// Resolves exactly one selected source, or no source when the caller explicitly permits it.
    /// </summary>
    internal async ValueTask<CpuKey?> ResolveAsync(
        CpuKeySourceSelection selection,
        CpuKeySourcePolicy policy,
        TextReader standardInput,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(standardInput);
        ValidatePolicy(policy);

        int sourceCount = CountSources(selection);
        if (sourceCount == 0)
        {
            if (policy is CpuKeySourcePolicy.Optional)
            {
                return null;
            }

            throw SourceRequired();
        }

        if (sourceCount != 1)
        {
            throw SourceConflict();
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (selection.FilePath is not null)
        {
            return await ReadFileAsync(selection.FilePath, cancellationToken).ConfigureAwait(false);
        }

        if (selection.EnvironmentVariableName is not null)
        {
            return ReadEnvironment(selection.EnvironmentVariableName, cancellationToken);
        }

        return await ReadStandardInputAsync(standardInput, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<CpuKey> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        byte[]? keyBytes = null;
        try
        {
            string normalizedPath = NormalizeFilePath(path);
            await using var input = new FileStream(
                normalizedPath,
                new FileStreamOptions
                {
                    Access = FileAccess.Read,
                    Mode = FileMode.Open,
                    Share = FileShare.Read,
                    // A one-byte buffer selects FileStream's unbuffered path so that keyBytes is the
                    // only managed copy.
                    BufferSize = 1,
                    Options = FileOptions.Asynchronous,
                });

            if (input.Length > MaximumFileByteLength)
            {
                throw InvalidCpuKey();
            }

            keyBytes = GC.AllocateUninitializedArray<byte>(checked((int)input.Length));
            await input.ReadExactlyAsync(keyBytes, cancellationToken).ConfigureAwait(false);
            return ParseAsciiCpuKey(keyBytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (OperationFailureException)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            throw new OperationFailureException(
                ExitCode.DeviceUnavailable,
                "permission-denied",
                "Permission was denied while accessing a required resource.");
        }
        catch (IOException)
        {
            throw SourceReadFailure();
        }
        finally
        {
            if (keyBytes is not null)
            {
                CryptographicOperations.ZeroMemory(keyBytes);
            }
        }
    }

    private CpuKey ReadEnvironment(string environmentVariableName, CancellationToken cancellationToken)
    {
        if (!IsValidEnvironmentVariableName(environmentVariableName))
        {
            throw InvalidEnvironmentVariableName();
        }

        string? value = null;
        try
        {
            value = _environment.GetEnvironmentVariable(environmentVariableName);
        }
        finally
        {
            // Consume the source before parsing or observing cancellation so that any later
            // child process cannot inherit it, including when resolution fails.
            if (value is not null)
            {
                _environment.RemoveEnvironmentVariable(environmentVariableName);
            }
        }

        if (value is null)
        {
            throw MissingEnvironmentVariable();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ParseCpuKey(value.AsSpan());
    }

    private async ValueTask<CpuKey> ReadStandardInputAsync(
        TextReader standardInput,
        CancellationToken cancellationToken)
    {
        IStandardInputEchoScope? echoScope = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool isTerminal = _standardInputTerminal.IsTerminal;
            if (isTerminal)
            {
                echoScope = await _standardInputTerminal
                    .DisableEchoAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            return await ReadStandardInputValueAsync(standardInput, echoScope, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (OperationFailureException)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            throw new OperationFailureException(
                ExitCode.DeviceUnavailable,
                "permission-denied",
                "Permission was denied while accessing a required resource.");
        }
        catch (IOException)
        {
            throw SourceReadFailure();
        }
        finally
        {
            if (echoScope is not null)
            {
                try
                {
                    await echoScope.DisposeAsync().ConfigureAwait(false);
                }
                catch (IOException)
                {
                    throw SourceReadFailure();
                }
            }
        }
    }

    private async ValueTask<CpuKey> ReadStandardInputValueAsync(
        TextReader standardInput,
        IStandardInputEchoScope? echoScope,
        CancellationToken cancellationToken)
    {
        bool isTerminal = echoScope is not null;
        char[] characters = GC.AllocateUninitializedArray<char>(MaximumFileByteLength);
        char[]? readBuffer = isTerminal ? null : GC.AllocateUninitializedArray<char>(1);
        try
        {
            int characterCount = 0;
            bool sawLineFeed = false;
            while (true)
            {
                int value = isTerminal
                    ? await _standardInputTerminal.ReadCharacterAsync(cancellationToken).ConfigureAwait(false)
                    : await ReadRedirectedCharacterAsync(standardInput, readBuffer!, cancellationToken).ConfigureAwait(false);
                if (value < 0)
                {
                    break;
                }

                char character = (char)value;
                if (character is '\n')
                {
                    sawLineFeed = true;
                    break;
                }

                if (characterCount == characters.Length)
                {
                    if (echoScope is not null)
                    {
                        // Do not leave an overlong secret queued for the shell after echo is restored.
                        ZeroCharacters(characters);
                        await DiscardTerminalLineAsync(echoScope, cancellationToken).ConfigureAwait(false);
                    }

                    throw InvalidCpuKey();
                }

                characters[characterCount++] = character;
            }

            echoScope?.CompleteInput();

            // A second read from a terminal waits for a future user line.
            // Redirected input must end here.
            if (sawLineFeed && !isTerminal)
            {
                int trailingCharacter = await ReadRedirectedCharacterAsync(standardInput, readBuffer!, cancellationToken)
                    .ConfigureAwait(false);
                if (trailingCharacter >= 0)
                {
                    throw ExtraStandardInputData();
                }
            }

            return ParseStandardInputCharacters(characters, characterCount, sawLineFeed);
        }
        finally
        {
            ZeroCharacters(characters);
            if (readBuffer is not null)
            {
                ZeroCharacters(readBuffer);
            }
        }
    }

    private async ValueTask DiscardTerminalLineAsync(
        IStandardInputEchoScope echoScope,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            int value = await _standardInputTerminal.ReadCharacterAsync(cancellationToken).ConfigureAwait(false);
            if (value < 0 || value == '\n')
            {
                echoScope.CompleteInput();
                return;
            }
        }
    }

    private static async ValueTask<int> ReadRedirectedCharacterAsync(
        TextReader standardInput,
        char[] readBuffer,
        CancellationToken cancellationToken)
    {
        int read = await standardInput.ReadAsync(readBuffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        return read == 0 ? -1 : readBuffer[0];
    }

    private static CpuKey ParseStandardInputCharacters(char[] characters, int characterCount, bool sawLineFeed)
    {
        ReadOnlySpan<char> valueCharacters = characters.AsSpan(0, characterCount);
        if (sawLineFeed && valueCharacters.Length > 0 && valueCharacters[^1] is '\r')
        {
            valueCharacters = valueCharacters[..^1];
        }

        return ParseCpuKey(valueCharacters);
    }

    private static void ZeroCharacters(char[] characters)
    {
        CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(characters.AsSpan()));
    }

    private static CpuKey ParseAsciiCpuKey(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumFileByteLength)
        {
            throw InvalidCpuKey();
        }

        Span<char> characters = stackalloc char[MaximumFileByteLength];
        try
        {
            for (int index = 0; index < bytes.Length; index++)
            {
                byte value = bytes[index];
                if (value > 0x7F)
                {
                    throw InvalidCpuKey();
                }

                characters[index] = (char)value;
            }

            return ParseCpuKey(characters[..bytes.Length]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(characters));
        }
    }

    private static CpuKey ParseCpuKey(ReadOnlySpan<char> value)
    {
        if (value.Length >= 2 && value[^2] is '\r' && value[^1] is '\n')
        {
            value = value[..^2];
        }
        else if (value.Length >= 1 && value[^1] is '\n')
        {
            value = value[..^1];
        }

        if (!CpuKey.TryParse(value, out CpuKey cpuKey))
        {
            throw InvalidCpuKey();
        }

        return cpuKey;
    }

    private static int CountSources(CpuKeySourceSelection selection)
    {
        int sourceCount = selection.FilePath is null ? 0 : 1;
        sourceCount += selection.EnvironmentVariableName is null ? 0 : 1;
        sourceCount += selection.ReadFromStandardInput ? 1 : 0;
        return sourceCount;
    }

    private static bool IsValidEnvironmentVariableName(string value)
    {
        if (value.Length == 0 || !IsEnvironmentNameStart(value[0]))
        {
            return false;
        }

        for (int index = 1; index < value.Length; index++)
        {
            if (!IsEnvironmentNamePart(value[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsEnvironmentNameStart(char value)
    {
        return value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';
    }

    private static bool IsEnvironmentNamePart(char value)
    {
        return IsEnvironmentNameStart(value) || value is >= '0' and <= '9';
    }

    private static string NormalizeFilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw InvalidCpuKeyFilePath();
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            throw InvalidCpuKeyFilePath();
        }
        catch (NotSupportedException)
        {
            throw InvalidCpuKeyFilePath();
        }
        catch (PathTooLongException)
        {
            throw InvalidCpuKeyFilePath();
        }
    }

    private static void ValidatePolicy(CpuKeySourcePolicy policy)
    {
        if (!Enum.IsDefined<CpuKeySourcePolicy>(policy))
        {
            throw new ArgumentOutOfRangeException(nameof(policy), policy, "The CPU key source policy is invalid.");
        }
    }

    private static OperationFailureException SourceRequired()
    {
        return new OperationFailureException(
            ExitCode.Usage,
            "cpu-key-source-required",
            "Specify exactly one CPU key source: --cpu-key-file, --cpu-key-env, or --cpu-key-stdin.");
    }

    private static OperationFailureException SourceConflict()
    {
        return new OperationFailureException(
            ExitCode.Usage,
            "cpu-key-source-conflict",
            "Specify exactly one CPU key source: --cpu-key-file, --cpu-key-env, or --cpu-key-stdin.");
    }

    private static OperationFailureException InvalidCpuKeyFilePath()
    {
        return new OperationFailureException(
            ExitCode.Usage,
            "invalid-cpu-key-file",
            "The CPU key file path is invalid.");
    }

    private static OperationFailureException InvalidEnvironmentVariableName()
    {
        return new OperationFailureException(
            ExitCode.Usage,
            "invalid-cpu-key-env",
            "The CPU key environment variable name is invalid.");
    }

    private static OperationFailureException MissingEnvironmentVariable()
    {
        return new OperationFailureException(
            ExitCode.Usage,
            "cpu-key-env-not-found",
            "The requested CPU key environment variable is not defined.");
    }

    private static OperationFailureException InvalidCpuKey()
    {
        return new OperationFailureException(
            ExitCode.Usage,
            "invalid-cpu-key",
            "The CPU key source must contain exactly one 32-character hexadecimal CPU key.");
    }

    private static OperationFailureException ExtraStandardInputData()
    {
        return new OperationFailureException(
            ExitCode.Usage,
            "cpu-key-stdin-extra-data",
            "Standard input must contain exactly one CPU key value.");
    }

    private static OperationFailureException SourceReadFailure()
    {
        return new OperationFailureException(
            ExitCode.InputOutput,
            "cpu-key-source-read-failed",
            "The CPU key source could not be read.");
    }
}

/// <summary>
/// Selects the one permitted CPU key source for a command.
/// </summary>
internal sealed class CpuKeySourceSelection
{
    internal CpuKeySourceSelection(
        string? filePath,
        string? environmentVariableName,
        bool readFromStandardInput)
    {
        FilePath = filePath;
        EnvironmentVariableName = environmentVariableName;
        ReadFromStandardInput = readFromStandardInput;
    }

    internal string? FilePath { get; }

    internal string? EnvironmentVariableName { get; }

    internal bool ReadFromStandardInput { get; }
}

/// <summary>
/// Controls whether a command permits no CPU key source.
/// </summary>
internal enum CpuKeySourcePolicy
{
    Required = 0,
    Optional = 1,
}

/// <summary>
/// Reads and removes process environment values without coupling CPU-key parsing to a global process API.
/// </summary>
internal interface ICpuKeyEnvironment
{
    string? GetEnvironmentVariable(string variableName);

    void RemoveEnvironmentVariable(string variableName);
}

internal sealed class ProcessCpuKeyEnvironment : ICpuKeyEnvironment
{
    internal static ProcessCpuKeyEnvironment Instance { get; } = new();

    private ProcessCpuKeyEnvironment()
    {
    }

    public string? GetEnvironmentVariable(string variableName)
    {
        return Environment.GetEnvironmentVariable(variableName);
    }

    public void RemoveEnvironmentVariable(string variableName)
    {
        Environment.SetEnvironmentVariable(variableName, null, EnvironmentVariableTarget.Process);
    }
}

/// <summary>
/// Provides the terminal state associated with standard input.
/// </summary>
internal interface IStandardInputTerminal
{
    bool IsTerminal { get; }

    ValueTask<IStandardInputEchoScope> DisableEchoAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reads one standard-input byte, or returns -1 at end of input, while observing cancellation.
    /// </summary>
    ValueTask<int> ReadCharacterAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Restores terminal attributes after one CPU key input line, discarding pending input if reading aborts.
/// </summary>
internal interface IStandardInputEchoScope : IAsyncDisposable
{
    /// <summary>
    /// Marks the current line as consumed through LF or EOF so cleanup does not discard later input.
    /// </summary>
    void CompleteInput();
}
