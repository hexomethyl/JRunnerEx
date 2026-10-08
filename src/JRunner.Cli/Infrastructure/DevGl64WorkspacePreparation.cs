using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using JRunner.Core.Contracts;
using JRunner.Core.Nand.Security;
using JRunner.Core.XeBuild.Algorithms;
using JRunner.Core.XeBuild.Preparation;

namespace JRunner.Cli.Infrastructure;

/// <summary>
/// Owns the short-lived DevGL 64 MB mutations made only inside one private XeBuild workspace.
/// </summary>
internal sealed class DevGl64WorkspacePreparation : IAsyncDisposable
{
    private const string VFuseFileName = "vfuses_khv.bin";
    private const string XellReasonFileName = "xell_reason.bin";
    private static readonly byte[] FuseLine0 = [0xC0, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];
    private static readonly byte[] DevkitFuseLine1 = [0x0F, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F];
    private static readonly byte[] XellStartupReason = [0x00, 0x12];

    private readonly object disposeGate = new();
    private readonly string workspaceRoot;
    private readonly string iniPath;
    private readonly string backupPath;
    private readonly string dashboardDirectory;
    private readonly string vfusePath;
    private readonly string xellReasonPath;
    private Task? disposeTask;
    private byte[]? originalIniBytes;
    private string? patchedSdPath;
    private bool backupCreated;
    private bool patchedSdCreated;
    private bool vfuseCreated;
    private bool xellReasonCreated;

    private DevGl64WorkspacePreparation(
        string workspaceRoot,
        string iniPath,
        string backupPath,
        string dashboardDirectory)
    {
        this.workspaceRoot = workspaceRoot;
        this.iniPath = iniPath;
        this.backupPath = backupPath;
        this.dashboardDirectory = dashboardDirectory;
        vfusePath = WorkspacePathSafety.ResolveDescendant(dashboardDirectory, VFuseFileName);
        xellReasonPath = WorkspacePathSafety.ResolveDescendant(dashboardDirectory, XellReasonFileName);
    }

    /// <summary>
    /// Applies the legacy DevGL 64 MB workspace mutation and returns a lease that restores it.
    /// </summary>
    internal static async Task<DevGl64WorkspacePreparation> ApplyAsync(
        string workspaceRoot,
        XeBuildPreparedPlan plan,
        CpuKey cpuKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();

        if (!plan.RequiresDevGl64Preparation)
        {
            throw InvalidData(
                "devgl64-preparation-not-required",
                "The selected XeBuild plan does not require DevGL 64 MB workspace preparation.");
        }

        if (!cpuKey.IsInitialized)
        {
            throw InvalidData(
                "devgl64-cpu-key-invalid",
                "The DevGL 64 MB workspace preparation requires a CPU key.");
        }

        string normalizedWorkspaceRoot = WorkspacePathSafety.NormalizeDirectoryPath(
            workspaceRoot,
            nameof(workspaceRoot));
        EnsureExistingDirectory(normalizedWorkspaceRoot, "devgl64-workspace-missing");

        string iniPath = WorkspacePathSafety.ResolveDescendant(
            normalizedWorkspaceRoot,
            plan.BuildModeIniPath);
        string backupPath = WorkspacePathSafety.ResolveDescendant(
            normalizedWorkspaceRoot,
            string.Concat(plan.BuildModeIniPath, ".bak"));
        string dashboardDirectory = WorkspacePathSafety.ResolveDescendant(
            normalizedWorkspaceRoot,
            string.Concat("xeBuild/", plan.DashboardVersion.ToString(CultureInfo.InvariantCulture)));

        var preparation = new DevGl64WorkspacePreparation(
            normalizedWorkspaceRoot,
            iniPath,
            backupPath,
            dashboardDirectory);
        try
        {
            await preparation.ApplyCoreAsync(plan, cpuKey, cancellationToken).ConfigureAwait(false);
            return preparation;
        }
        catch
        {
            try
            {
                await preparation.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Preserve the preparation failure after every independent cleanup attempt.
            }

            throw;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (disposeGate)
        {
            disposeTask ??= DisposeCoreAsync();
            return new ValueTask(disposeTask);
        }
    }

    private async Task ApplyCoreAsync(
        XeBuildPreparedPlan plan,
        CpuKey cpuKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureExistingDirectory(dashboardDirectory, "devgl64-dashboard-missing");
        RequireExistingFile(iniPath, "devgl64-ini-missing");
        EnsureTargetAbsent(backupPath, "devgl64-backup-exists");

        string patchPath = WorkspacePathSafety.ResolveDescendant(
            dashboardDirectory,
            string.Concat("bin/patches_dev", plan.Console.IniName, ".bin"));
        byte[] patchPayload = await ReadFileAsync(
            patchPath,
            "devgl64-patch-missing",
            cancellationToken).ConfigureAwait(false);
        XeBuildPatchSets patchSets = XeBuildPatchSetReader.ReadAll(patchPayload, cancellationToken);

        byte[] rawIni = await ReadFileAsync(
            iniPath,
            "devgl64-ini-missing",
            cancellationToken).ConfigureAwait(false);
        originalIniBytes = rawIni;
        RawLine sdRow = FindSdRow(rawIni, plan.Console.IniName);
        SdIniEntry sdEntry = ParseSdEntry(rawIni.AsSpan(sdRow.Start, sdRow.Length));

        string sourceSdPath = ResolveSourceSdPath(sdEntry.FileName);
        byte[] sourceSd = await ReadFileAsync(
            sourceSdPath,
            "devgl64-sd-missing",
            cancellationToken).ConfigureAwait(false);
        uint originalCrc = BootloaderCrcCalculator.Calculate(sourceSd, cancellationToken).Value;
        if (originalCrc != sdEntry.Crc)
        {
            throw InvalidData(
                "devgl64-sd-crc-mismatch",
                "The staged DevGL SD bootloader does not match its configuration checksum.");
        }

        BootloaderPatchResult patchedSd = BootloaderPatchApplier.Apply(
            sourceSd,
            patchSets.FourBl.Data,
            cancellationToken);
        uint patchedCrc = BootloaderCrcCalculator.Calculate(patchedSd.Bootloader, cancellationToken).Value;
        string patchedFileName = string.Concat("PATCH_", sdEntry.FileName);
        patchedSdPath = WorkspacePathSafety.ResolveDescendant(dashboardDirectory, patchedFileName);

        byte[] updatedIni = ReplaceLine(
            rawIni,
            sdRow,
            Encoding.ASCII.GetBytes(string.Concat(
                patchedFileName,
                ",",
                patchedCrc.ToString("x", CultureInfo.InvariantCulture))));

        await WriteNewFileAsync(
            backupPath,
            rawIni,
            () => backupCreated = true,
            cancellationToken).ConfigureAwait(false);
        await WritePatchedSdAsync(patchedSd, cancellationToken).ConfigureAwait(false);
        await WriteReplacementFileAsync(iniPath, updatedIni, cancellationToken).ConfigureAwait(false);

        byte[] fuseAndKernelPatchData = CreateFuseAndKernelPatchData(cpuKey, patchSets.KernelHypervisor.Data);
        try
        {
            await WriteNewFileAsync(
                vfusePath,
                fuseAndKernelPatchData,
                () => vfuseCreated = true,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fuseAndKernelPatchData);
        }

        await WriteNewFileAsync(
            xellReasonPath,
            XellStartupReason,
            () => xellReasonCreated = true,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task DisposeCoreAsync()
    {
        var cleanupFailed = false;

        if (backupCreated)
        {
            bool restoredIni = await TryCleanupAsync(RestoreIniAsync).ConfigureAwait(false);
            if (!restoredIni)
            {
                cleanupFailed = true;
            }
            else if (!await TryCleanupAsync(() => DeleteOwnedFileAsync(backupPath)).ConfigureAwait(false))
            {
                cleanupFailed = true;
            }
        }

        string? createdPatchedSdPath = patchedSdPath;
        if (patchedSdCreated && createdPatchedSdPath is not null &&
            !await TryCleanupAsync(() => DeleteOwnedFileAsync(createdPatchedSdPath)).ConfigureAwait(false))
        {
            cleanupFailed = true;
        }

        if (vfuseCreated &&
            !await TryCleanupAsync(() => DeleteOwnedFileAsync(vfusePath)).ConfigureAwait(false))
        {
            cleanupFailed = true;
        }

        if (xellReasonCreated &&
            !await TryCleanupAsync(() => DeleteOwnedFileAsync(xellReasonPath)).ConfigureAwait(false))
        {
            cleanupFailed = true;
        }

        originalIniBytes = null;
        if (cleanupFailed)
        {
            throw new OperationFailureException(
                ExitCode.InputOutput,
                "devgl64-cleanup-failed",
                "The DevGL 64 MB workspace preparation could not be cleaned up.");
        }
    }

    private async Task RestoreIniAsync()
    {
        if (originalIniBytes is null)
        {
            throw new InvalidOperationException("The original DevGL configuration was not retained.");
        }

        await WriteReplacementFileAsync(iniPath, originalIniBytes, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task WritePatchedSdAsync(
        BootloaderPatchResult patchedSd,
        CancellationToken cancellationToken)
    {
        string destinationPath = patchedSdPath
            ?? throw new InvalidOperationException("The patched DevGL SD path was not initialized.");
        EnsureTargetAbsent(destinationPath, "devgl64-patched-sd-exists");

        try
        {
            await using FileStream output = WineXeBuildWorkspace.OpenPrivateOutput(destinationPath, FileMode.CreateNew);
            patchedSdCreated = true;
            output.Write(patchedSd.Bootloader);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw InputOutputFailure("devgl64-patched-sd-write-failed");
        }
    }

    private static async Task WriteNewFileAsync(
        string path,
        ReadOnlyMemory<byte> data,
        Action markCreated,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(markCreated);
        EnsureTargetAbsent(path, "devgl64-generated-file-exists");

        try
        {
            await using FileStream output = WineXeBuildWorkspace.OpenPrivateOutput(path, FileMode.CreateNew);
            markCreated();
            await output.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw InputOutputFailure("devgl64-generated-file-write-failed");
        }
    }

    private static async Task WriteReplacementFileAsync(
        string path,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        EnsureSafePath(path);

        try
        {
            await using FileStream output = WineXeBuildWorkspace.OpenPrivateOutput(path, FileMode.Create);
            await output.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw InputOutputFailure("devgl64-ini-write-failed");
        }
    }

    private static async Task<byte[]> ReadFileAsync(
        string path,
        string missingKind,
        CancellationToken cancellationToken)
    {
        RequireExistingFile(path, missingKind);

        try
        {
            return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw InputOutputFailure("devgl64-file-read-failed");
        }
    }

    private string ResolveSourceSdPath(string fileName)
    {
        string dashboardCandidate = WorkspacePathSafety.ResolveDescendant(dashboardDirectory, fileName);
        if (IsExistingFile(dashboardCandidate))
        {
            return dashboardCandidate;
        }

        string commonDirectory = WorkspacePathSafety.ResolveDescendant(workspaceRoot, "xeBuild/common");
        EnsureExistingDirectory(commonDirectory, "devgl64-common-directory-missing");
        string commonCandidate = WorkspacePathSafety.ResolveDescendant(commonDirectory, fileName);
        if (IsExistingFile(commonCandidate))
        {
            return commonCandidate;
        }

        throw InvalidData(
            "devgl64-sd-missing",
            "The staged DevGL SD bootloader is missing.");
    }

    private static bool IsExistingFile(string path)
    {
        EnsureSafePath(path);
        try
        {
            if (File.Exists(path))
            {
                return true;
            }

            if (Directory.Exists(path))
            {
                throw InvalidData(
                    "devgl64-sd-invalid",
                    "The staged DevGL SD bootloader is not a regular file.");
            }

            return false;
        }
        catch (OperationFailureException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw InputOutputFailure("devgl64-file-access-failed");
        }
    }

    private static void RequireExistingFile(string path, string missingKind)
    {
        EnsureSafePath(path);
        try
        {
            if (!File.Exists(path))
            {
                throw InvalidData(
                    missingKind,
                    "A required DevGL workspace file is missing.");
            }
        }
        catch (OperationFailureException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw InputOutputFailure("devgl64-file-access-failed");
        }
    }

    private static void EnsureExistingDirectory(string path, string missingKind)
    {
        EnsureSafePath(path);
        try
        {
            if (!Directory.Exists(path))
            {
                throw InvalidData(
                    missingKind,
                    "A required DevGL workspace directory is missing.");
            }
        }
        catch (OperationFailureException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw InputOutputFailure("devgl64-directory-access-failed");
        }
    }

    private static void EnsureTargetAbsent(string path, string existingKind)
    {
        EnsureSafePath(path);
        try
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                throw InvalidData(
                    existingKind,
                    "The DevGL workspace contains an unexpected generated file.");
            }
        }
        catch (OperationFailureException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw InputOutputFailure("devgl64-file-access-failed");
        }
    }

    private static void EnsureSafePath(string path)
    {
        WorkspacePathSafety.EnsureNoLinkAncestors(path);
        WorkspacePathSafety.EnsureNotLink(path);
    }

    private static Task DeleteOwnedFileAsync(string path)
    {
        EnsureSafePath(path);
        try
        {
            if (Directory.Exists(path))
            {
                throw InputOutputFailure("devgl64-cleanup-path-invalid");
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (OperationFailureException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw InputOutputFailure("devgl64-cleanup-delete-failed");
        }

        return Task.CompletedTask;
    }

    private static async Task<bool> TryCleanupAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static RawLine FindSdRow(byte[] rawIni, string iniName)
    {
        ArgumentNullException.ThrowIfNull(rawIni);
        if (string.IsNullOrWhiteSpace(iniName))
        {
            throw InvalidData(
                "devgl64-ini-section-invalid",
                "The staged DevGL configuration has an invalid bootloader section.");
        }
        string expectedSection = string.Concat("[", iniName, "bl]");
        var lines = new List<RawLine>();
        var lineStart = 0;
        for (var index = 0; index < rawIni.Length; index++)
        {
            if (rawIni[index] != (byte)'\r' && rawIni[index] != (byte)'\n')
            {
                continue;
            }

            lines.Add(new RawLine(lineStart, index - lineStart));
            if (rawIni[index] == (byte)'\r' &&
                index + 1 < rawIni.Length &&
                rawIni[index + 1] == (byte)'\n')
            {
                index++;
            }

            lineStart = index + 1;
        }

        if (lineStart < rawIni.Length)
        {
            lines.Add(new RawLine(lineStart, rawIni.Length - lineStart));
        }

        var sectionIndex = -1;
        for (var index = 0; index < lines.Count; index++)
        {
            if (!MatchesExactSection(rawIni, lines[index], expectedSection))
            {
                continue;
            }

            if (sectionIndex >= 0)
            {
                throw InvalidData(
                    "devgl64-ini-section-duplicate",
                    "The staged DevGL configuration contains duplicate bootloader sections.");
            }

            sectionIndex = index;
        }

        if (sectionIndex < 0)
        {
            throw InvalidData(
                "devgl64-ini-section-missing",
                "The staged DevGL configuration is missing its bootloader section.");
        }

        int sdRowIndex = checked(sectionIndex + 3);
        if (sdRowIndex >= lines.Count)
        {
            throw InvalidData(
                "devgl64-ini-sd-row-missing",
                "The staged DevGL configuration is missing its SD bootloader row.");
        }

        return lines[sdRowIndex];
    }

    private static bool MatchesExactSection(byte[] rawIni, RawLine line, string expectedSection)
    {
        var offset = line.Start;
        var length = line.Length;
        if (offset == 0 && length >= 3 &&
            rawIni[0] == 0xEF && rawIni[1] == 0xBB && rawIni[2] == 0xBF)
        {
            offset += 3;
            length -= 3;
        }

        if (length != expectedSection.Length)
        {
            return false;
        }

        for (var index = 0; index < expectedSection.Length; index++)
        {
            char character = expectedSection[index];
            if (character > 0x7F || rawIni[offset + index] != (byte)character)
            {
                return false;
            }
        }

        return true;
    }

    private static SdIniEntry ParseSdEntry(ReadOnlySpan<byte> row)
    {
        int commaIndex = row.IndexOf((byte)',');
        if (commaIndex <= 0 || commaIndex == row.Length - 1 ||
            row[(commaIndex + 1)..].IndexOf((byte)',') >= 0)
        {
            throw InvalidData(
                "devgl64-ini-sd-row-invalid",
                "The staged DevGL configuration has an invalid SD bootloader row.");
        }

        ReadOnlySpan<byte> fileNameBytes = row[..commaIndex];
        ReadOnlySpan<byte> crcBytes = row[(commaIndex + 1)..];
        ValidateSdFileName(fileNameBytes);
        uint crc = ParseHexadecimalCrc(crcBytes);
        string fileName = Encoding.ASCII.GetString(fileNameBytes);
        if (fileName.StartsWith("PATCH", StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidData(
                "devgl64-sd-already-patched",
                "The staged DevGL configuration already references a patched SD bootloader.");
        }

        return new SdIniEntry(fileName, crc);
    }

    private static void ValidateSdFileName(ReadOnlySpan<byte> fileName)
    {
        if (fileName.IsEmpty)
        {
            throw InvalidData(
                "devgl64-ini-sd-row-invalid",
                "The staged DevGL configuration has an invalid SD bootloader row.");
        }

        foreach (byte value in fileName)
        {
            if (value is < 0x21 or > 0x7E ||
                value is (byte)'/' or (byte)'\\' or (byte)':')
            {
                throw InvalidData(
                    "devgl64-ini-sd-row-invalid",
                    "The staged DevGL configuration has an invalid SD bootloader row.");
            }
        }

        string path = Encoding.ASCII.GetString(fileName);
        if (path is "." or ".." ||
            !string.Equals(Path.GetFileName(path), path, StringComparison.Ordinal))
        {
            throw InvalidData(
                "devgl64-ini-sd-row-invalid",
                "The staged DevGL configuration has an invalid SD bootloader row.");
        }
    }

    private static uint ParseHexadecimalCrc(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty || value.Length > sizeof(uint) * 2)
        {
            throw InvalidData(
                "devgl64-ini-sd-row-invalid",
                "The staged DevGL configuration has an invalid SD bootloader row.");
        }

        uint result = 0;
        foreach (byte character in value)
        {
            int digit = character switch
            {
                >= (byte)'0' and <= (byte)'9' => character - (byte)'0',
                >= (byte)'A' and <= (byte)'F' => character - (byte)'A' + 10,
                >= (byte)'a' and <= (byte)'f' => character - (byte)'a' + 10,
                _ => -1,
            };
            if (digit < 0)
            {
                throw InvalidData(
                    "devgl64-ini-sd-row-invalid",
                    "The staged DevGL configuration has an invalid SD bootloader row.");
            }

            result = (result << 4) | (uint)digit;
        }

        return result;
    }

    private static byte[] ReplaceLine(byte[] source, RawLine line, byte[] replacement)
    {
        int newLength = checked(source.Length - line.Length + replacement.Length);
        var result = new byte[newLength];
        source.AsSpan(0, line.Start).CopyTo(result);
        replacement.AsSpan().CopyTo(result.AsSpan(line.Start));
        source.AsSpan(line.Start + line.Length).CopyTo(result.AsSpan(line.Start + replacement.Length));
        return result;
    }

    private static byte[] CreateFuseAndKernelPatchData(CpuKey cpuKey, ReadOnlySpan<byte> kernelHypervisorPatch)
    {
        byte[] output = new byte[checked(0x60 + kernelHypervisorPatch.Length)];
        FuseLine0.CopyTo(output, 0);
        DevkitFuseLine1.CopyTo(output, 0x8);

        Span<byte> keyBytes = stackalloc byte[CpuKey.ByteLength];
        try
        {
            cpuKey.CopyTo(keyBytes);
            keyBytes[..0x8].CopyTo(output.AsSpan(0x18, 0x8));
            keyBytes[..0x8].CopyTo(output.AsSpan(0x20, 0x8));
            keyBytes[0x8..].CopyTo(output.AsSpan(0x28, 0x8));
            keyBytes[0x8..].CopyTo(output.AsSpan(0x30, 0x8));
            kernelHypervisorPatch.CopyTo(output.AsSpan(0x60));
            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }

    private static OperationFailureException InvalidData(string kind, string message)
    {
        return new OperationFailureException(ExitCode.InvalidData, kind, message);
    }

    private static OperationFailureException InputOutputFailure(string kind)
    {
        return new OperationFailureException(
            ExitCode.InputOutput,
            kind,
            "The DevGL 64 MB workspace preparation could not access a required workspace file.");
    }

    private readonly record struct RawLine(int Start, int Length);

    private readonly record struct SdIniEntry(string FileName, uint Crc);
}
