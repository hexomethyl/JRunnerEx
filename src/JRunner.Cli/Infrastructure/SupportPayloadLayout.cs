namespace JRunner.Cli.Infrastructure;

/// <summary>
/// Owns the stable on-disk layout for immutable support payload generations.
/// </summary>
internal static class SupportPayloadLayout
{
    internal const int ActivationSchemaVersion = 2;
    internal const string InstallationsDirectoryName = "installations";
    internal const string StagingDirectoryName = ".staging";
    internal const string ActiveRecordFileName = "active.json";

    internal static string GetInstallationsDirectory(SupportRoot supportRoot)
    {
        return Path.Combine(supportRoot.DirectoryPath, InstallationsDirectoryName);
    }

    internal static string GetStagingDirectory(SupportRoot supportRoot)
    {
        return Path.Combine(supportRoot.DirectoryPath, StagingDirectoryName);
    }

    internal static string GetActiveRecordPath(SupportRoot supportRoot)
    {
        return Path.Combine(supportRoot.DirectoryPath, ActiveRecordFileName);
    }

    internal static string CreateGenerationId(string payloadId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadId);
        return $"{payloadId}-{Guid.NewGuid():N}";
    }

    internal static string GetGenerationDirectory(SupportRoot supportRoot, string generationId)
    {
        if (!IsSimpleName(generationId))
        {
            throw new InvalidOperationException("The support activation generation identifier is invalid.");
        }

        return Path.Combine(GetInstallationsDirectory(supportRoot), generationId);
    }

    internal static string GetStagedGenerationDirectory(SupportRoot supportRoot, string generationId)
    {
        if (!IsSimpleName(generationId))
        {
            throw new InvalidOperationException("The support staging generation identifier is invalid.");
        }

        return Path.Combine(GetStagingDirectory(supportRoot), generationId);
    }

    internal static string GetRelativeGenerationPath(string generationId)
    {
        if (!IsSimpleName(generationId))
        {
            throw new InvalidOperationException("The support activation generation identifier is invalid.");
        }

        return string.Concat(InstallationsDirectoryName, "/", generationId);
    }

    internal static bool IsSimpleName(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
            !Path.IsPathRooted(value) &&
            value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/', '\\']) < 0 &&
            value is not "." and not "..";
    }
}

/// <summary>
/// Identifies the complete generation exposed through the atomic active marker.
/// </summary>
internal sealed record SupportActivationRecord(
    int SchemaVersion,
    string PayloadId,
    string ReleaseTag,
    string SourceUrl,
    string ArchiveSha256,
    string CanonicalManifestSha256,
    DateTimeOffset InstalledAtUtc,
    string ActivePayloadPath,
    string GenerationId);
