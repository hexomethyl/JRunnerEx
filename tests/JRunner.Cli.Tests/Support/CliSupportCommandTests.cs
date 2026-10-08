using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using JRunner.Cli;
using JRunner.Core.Contracts;
using JRunner.Core.Support;
using Xunit;

namespace JRunner.Cli.Tests.Support;

public sealed class CliSupportCommandTests
{
    [Theory]
    [InlineData("support-absent", 0)]
    [InlineData("support-absent", 1)]
    [InlineData("support-absent", 2)]
    [InlineData("support-incomplete", 0)]
    [InlineData("support-incomplete", 1)]
    [InlineData("support-incomplete", 2)]
    [InlineData("support-corrupt", 0)]
    [InlineData("support-corrupt", 1)]
    [InlineData("support-corrupt", 2)]
    public async Task Support_status_preserves_classification_with_the_global_root_before_between_or_after_commands(
        string expectedKind,
        int supportRootPosition)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string supportRoot = Path.Combine(temporaryDirectory.Path, "support");
        await PrepareStatusRootAsync(supportRoot, expectedKind);
        bool rootExisted = Directory.Exists(supportRoot);
        string[] originalEntries = GetEntries(supportRoot);
        string activationPath = Path.Combine(supportRoot, "active.json");
        string? originalActivation = File.Exists(activationPath)
            ? await File.ReadAllTextAsync(activationPath)
            : null;
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        int exitCode = await CliApplication.RunAsync(
            SupportStatusArguments(supportRoot, supportRootPosition),
            TextReader.Null,
            standardOutput,
            standardError);

        AssertFailureEnvelope(
            exitCode,
            standardOutput.ToString(),
            standardError.ToString(),
            ExitCode.MissingPrerequisite,
            expectedKind);
        Assert.Equal(rootExisted, Directory.Exists(supportRoot));
        Assert.Equal(originalEntries, GetEntries(supportRoot));
        if (originalActivation is not null)
        {
            Assert.Equal(originalActivation, await File.ReadAllTextAsync(activationPath));
        }
    }

    [Fact]
    public async Task Support_status_in_a_clean_home_does_not_create_the_default_root()
    {
        using var homeDirectory = new TemporaryDirectory();
        string expectedSupportRoot = Path.Combine(
            homeDirectory.Path,
            ".local",
            "share",
            "jrunner",
            "support");

        ProcessResult result = await RunInIsolatedProcessAsync(
            CreateStartInfo(["support", "status", "--json"], homeDirectory.Path));

        AssertFailureEnvelope(
            result.ExitCode,
            result.StandardOutput,
            result.StandardError,
            ExitCode.MissingPrerequisite,
            "support-absent");
        Assert.False(Directory.Exists(expectedSupportRoot));
    }

    [Fact]
    public async Task Support_status_prefers_the_environment_override_over_xdg_and_home()
    {
        using var homeDirectory = new TemporaryDirectory();
        string environmentRoot = Path.Combine(homeDirectory.Path, "environment-support");
        string xdgDataHome = Path.Combine(homeDirectory.Path, "xdg");
        string xdgSupportRoot = Path.Combine(xdgDataHome, "jrunner", "support");
        await PrepareStatusRootAsync(environmentRoot, "support-incomplete");
        await PrepareStatusRootAsync(xdgSupportRoot, "support-corrupt");

        ProcessResult result = await RunInIsolatedProcessAsync(
            CreateStartInfo(
                ["support", "status", "--json"],
                homeDirectory.Path,
                environmentRoot,
                xdgDataHome));

        AssertFailureEnvelope(
            result.ExitCode,
            result.StandardOutput,
            result.StandardError,
            ExitCode.MissingPrerequisite,
            "support-incomplete");
        Assert.False(Directory.Exists(Path.Combine(homeDirectory.Path, ".local")));
        Assert.False(File.Exists(Path.Combine(environmentRoot, "active.json")));
        Assert.True(File.Exists(Path.Combine(xdgSupportRoot, "active.json")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Support_status_prefers_an_explicit_global_root_over_environment_and_xdg(
        int supportRootPosition)
    {
        using var homeDirectory = new TemporaryDirectory();
        string explicitRoot = Path.Combine(homeDirectory.Path, "explicit-support");
        string environmentRoot = Path.Combine(homeDirectory.Path, "environment-support");
        string xdgDataHome = Path.Combine(homeDirectory.Path, "xdg");
        string xdgSupportRoot = Path.Combine(xdgDataHome, "jrunner", "support");
        await PrepareStatusRootAsync(environmentRoot, "support-incomplete");
        await PrepareStatusRootAsync(xdgSupportRoot, "support-corrupt");

        ProcessResult result = await RunInIsolatedProcessAsync(
            CreateStartInfo(
                SupportStatusArguments(explicitRoot, supportRootPosition),
                homeDirectory.Path,
                environmentRoot,
                xdgDataHome));

        AssertFailureEnvelope(
            result.ExitCode,
            result.StandardOutput,
            result.StandardError,
            ExitCode.MissingPrerequisite,
            "support-absent");
        Assert.False(Directory.Exists(explicitRoot));
        Assert.False(Directory.Exists(Path.Combine(homeDirectory.Path, ".local")));
        Assert.False(File.Exists(Path.Combine(environmentRoot, "active.json")));
        Assert.True(File.Exists(Path.Combine(xdgSupportRoot, "active.json")));
    }

    [Fact]
    public async Task Support_status_uses_xdg_when_no_environment_override_or_explicit_root_is_supplied()
    {
        using var homeDirectory = new TemporaryDirectory();
        string xdgDataHome = Path.Combine(homeDirectory.Path, "xdg");
        string xdgSupportRoot = Path.Combine(xdgDataHome, "jrunner", "support");
        string defaultRoot = Path.Combine(homeDirectory.Path, ".local", "share", "jrunner", "support");
        await PrepareStatusRootAsync(xdgSupportRoot, "support-corrupt");
        await PrepareStatusRootAsync(defaultRoot, "support-incomplete");

        ProcessResult result = await RunInIsolatedProcessAsync(
            CreateStartInfo(
                ["support", "status", "--json"],
                homeDirectory.Path,
                xdgDataHome: xdgDataHome));

        AssertFailureEnvelope(
            result.ExitCode,
            result.StandardOutput,
            result.StandardError,
            ExitCode.MissingPrerequisite,
            "support-incomplete");
        Assert.False(File.Exists(Path.Combine(defaultRoot, "active.json")));
    }

    [Theory]
    [InlineData(false, 0, ExitCode.InputOutput, "support-local-archive-unavailable")]
    [InlineData(false, 2, ExitCode.InputOutput, "support-local-archive-unavailable")]
    [InlineData(true, 2, ExitCode.InvalidData, "support-archive-digest-mismatch")]
    public async Task Local_archive_failures_are_typed_without_network_fallback_or_activation(
        bool archiveExists,
        int supportRootPosition,
        ExitCode expectedCode,
        string expectedKind)
    {
        using var homeDirectory = new TemporaryDirectory();
        string supportRoot = Path.Combine(homeDirectory.Path, "support");
        string archivePath = Path.Combine(homeDirectory.Path, "local-support.zip");
        if (archiveExists)
        {
            using var archive = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            archive.SetLength(EmbeddedSupportManifest.Current.Archive.ByteLength);
        }

        List<string> arguments = SupportStatusArguments(supportRoot, supportRootPosition);
        arguments[arguments.IndexOf("status")] = "install";
        arguments.AddRange(["--archive", archivePath]);
        var proxy = new TcpListener(IPAddress.Loopback, 0);
        proxy.Start();
        try
        {
            string proxyAddress = $"http://127.0.0.1:{((IPEndPoint)proxy.LocalEndpoint).Port}";
            ProcessStartInfo startInfo = CreateStartInfo(arguments, homeDirectory.Path);
            foreach (string variable in new[]
            {
                "http_proxy", "HTTP_PROXY", "https_proxy", "HTTPS_PROXY", "all_proxy", "ALL_PROXY",
            })
            {
                startInfo.Environment[variable] = proxyAddress;
            }

            startInfo.Environment["no_proxy"] = string.Empty;
            startInfo.Environment["NO_PROXY"] = string.Empty;
            ProcessResult result = await RunInIsolatedProcessAsync(startInfo);

            Assert.False(proxy.Pending(), "A local archive install attempted a network request.");
            AssertFailureEnvelope(
                result.ExitCode,
                result.StandardOutput,
                result.StandardError,
                expectedCode,
                expectedKind);
            Assert.False(File.Exists(Path.Combine(supportRoot, "active.json")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(supportRoot, "installations")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(supportRoot, ".staging")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(supportRoot, ".downloads")));
            Assert.Equal(archiveExists, File.Exists(archivePath));
            if (archiveExists)
            {
                Assert.Equal(EmbeddedSupportManifest.Current.Archive.ByteLength, new FileInfo(archivePath).Length);
            }
        }
        finally
        {
            proxy.Stop();
        }
    }

    [Theory]
    [InlineData("status")]
    [InlineData("install")]
    public async Task Support_commands_are_available_through_the_shared_help_surface(string command)
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        int exitCode = await CliApplication.RunAsync(
            ["support", command, "--help", "--json"],
            TextReader.Null,
            standardOutput,
            standardError);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, standardError.ToString());
        using JsonDocument document = JsonDocument.Parse(standardOutput.ToString());
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Contains(
            $"Usage: jrunner support {command}",
            document.RootElement.GetProperty("result").GetString());
        string help = Assert.IsType<string>(document.RootElement.GetProperty("result").GetString());
        Assert.Contains("--support-root", help);
        if (command == "install")
        {
            Assert.Contains("--archive", help);
        }
    }

    private static List<string> SupportStatusArguments(string supportRoot, int supportRootPosition)
    {
        List<string> arguments = ["support", "status", "--json"];
        arguments.InsertRange(supportRootPosition, ["--support-root", supportRoot]);
        return arguments;
    }

    private static async Task PrepareStatusRootAsync(string supportRoot, string kind)
    {
        if (kind == "support-absent")
        {
            return;
        }

        const string generationId = "current";
        CreatePrivateDirectory(supportRoot);
        string installationsDirectory = Path.Combine(supportRoot, "installations");
        CreatePrivateDirectory(installationsDirectory);
        CreatePrivateDirectory(Path.Combine(installationsDirectory, generationId));
        if (kind == "support-incomplete")
        {
            return;
        }

        Assert.Equal("support-corrupt", kind);
        SupportManifest manifest = EmbeddedSupportManifest.Current;
        string activationPath = Path.Combine(supportRoot, "active.json");
        await File.WriteAllTextAsync(
            activationPath,
            JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                payloadId = manifest.PayloadId,
                releaseTag = manifest.ReleaseTag,
                sourceUrl = manifest.Archive.DownloadUri.AbsoluteUri,
                archiveSha256 = manifest.Archive.Sha256,
                canonicalManifestSha256 = manifest.CanonicalManifestSha256,
                installedAtUtc = DateTimeOffset.UnixEpoch,
                activePayloadPath = $"installations/{generationId}",
                generationId,
            }));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(activationPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static string[] GetEntries(string supportRoot)
    {
        return Directory.Exists(supportRoot)
            ? Directory.GetFileSystemEntries(supportRoot, "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [];
    }

    private static ProcessStartInfo CreateStartInfo(
        IReadOnlyList<string> arguments,
        string homeDirectory,
        string? environmentSupportRoot = null,
        string? xdgDataHome = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(
                AppContext.BaseDirectory,
                OperatingSystem.IsWindows() ? "jrunner.exe" : "jrunner"),
            WorkingDirectory = homeDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment.Remove("JRUNNER_SUPPORT_ROOT");
        startInfo.Environment.Remove("XDG_DATA_HOME");
        startInfo.Environment["HOME"] = homeDirectory;
        if (environmentSupportRoot is not null)
        {
            startInfo.Environment["JRUNNER_SUPPORT_ROOT"] = environmentSupportRoot;
        }

        if (xdgDataHome is not null)
        {
            startInfo.Environment["XDG_DATA_HOME"] = xdgDataHome;
        }

        return startInfo;
    }

    private static async Task<ProcessResult> RunInIsolatedProcessAsync(ProcessStartInfo startInfo)
    {
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the jrunner apphost.");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
            return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private static void AssertFailureEnvelope(
        int exitCode,
        string standardOutput,
        string standardError,
        ExitCode expectedCode,
        string expectedKind)
    {
        Assert.Equal((int)expectedCode, exitCode);
        using JsonDocument document = JsonDocument.Parse(standardOutput);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        JsonElement error = document.RootElement.GetProperty("error");
        Assert.Equal((int)expectedCode, error.GetProperty("code").GetInt32());
        Assert.Equal(expectedKind, error.GetProperty("kind").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
        Assert.NotEmpty(standardError);
    }

    private static void CreatePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"jrunner-cli-support-{Guid.NewGuid():N}");
            CreatePrivateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
