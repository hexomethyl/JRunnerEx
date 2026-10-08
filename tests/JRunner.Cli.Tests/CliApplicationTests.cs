using System.Text.Json;
using JRunner.Cli;
using JRunner.Core;
using Xunit;

namespace JRunner.Cli.Tests;

public sealed class CliApplicationTests
{
    [Fact]
    public async Task Version_command_writes_the_version_only_to_standard_output()
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await CliApplication.RunAsync(
            new[] { "--version" },
            TextReader.Null,
            standardOutput,
            standardError);

        Assert.Equal(0, exitCode);
        Assert.Equal(JRunnerVersion.Display + Environment.NewLine, standardOutput.ToString());
        Assert.Equal(string.Empty, standardError.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Version_json_writes_one_success_envelope_to_standard_output(bool jsonFirst)
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await CliApplication.RunAsync(
            jsonFirst ? ["--json", "--version"] : ["--version", "--json"],
            TextReader.Null,
            standardOutput,
            standardError);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, standardError.ToString());

        using var document = JsonDocument.Parse(standardOutput.ToString());
        var envelope = document.RootElement;
        Assert.Equal(JsonValueKind.Object, envelope.ValueKind);
        Assert.Equal(1, envelope.GetProperty("schemaVersion").GetInt32());
        Assert.True(envelope.GetProperty("ok").GetBoolean());
        Assert.Equal(JRunnerVersion.Display, envelope.GetProperty("result").GetString());
    }

    [Theory]
    [InlineData("nand")]
    [InlineData("patch")]
    [InlineData("console")]
    [InlineData("device")]
    [InlineData("pico")]
    [InlineData("support")]
    [InlineData("xebuild")]
    public async Task Unsupported_command_groups_json_use_the_shared_usage_error_envelope(string group)
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await CliApplication.RunAsync(
            [group, "--json"],
            TextReader.Null,
            standardOutput,
            standardError);

        AssertUsageFailure(
            exitCode,
            standardOutput.ToString(),
            standardError.ToString(),
            jsonRequested: true,
            "unsupported-command");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("unknown-command")]
    public async Task Unsupported_root_requests_json_retain_the_unsupported_command_classification(string? command)
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        string[] arguments = command is null ? ["--json"] : [command, "--json"];

        int exitCode = await CliApplication.RunAsync(
            arguments,
            TextReader.Null,
            standardOutput,
            standardError);

        AssertUsageFailure(
            exitCode,
            standardOutput.ToString(),
            standardError.ToString(),
            jsonRequested: true,
            "unsupported-command");
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("--help")]
    public async Task Root_information_flags_do_not_hide_parse_errors(string informationFlag)
    {
        const string secret = "00112233445566778899AABBCCDDEEFF";
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        int exitCode = await CliApplication.RunAsync(
            [informationFlag, "--unknown", secret, "--json"],
            TextReader.Null,
            standardOutput,
            standardError);

        AssertUsageFailure(
            exitCode,
            standardOutput.ToString(),
            standardError.ToString(),
            jsonRequested: true,
            "unsupported-command");
        Assert.DoesNotContain(secret, standardOutput.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secret, standardError.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Root_help_writes_human_readable_help_only_to_standard_output()
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        int exitCode = await CliApplication.RunAsync(
            ["--help"],
            TextReader.Null,
            standardOutput,
            standardError);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, standardError.ToString());
        AssertRootHelp(standardOutput.ToString());
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("/h")]
    [InlineData("-?")]
    [InlineData("/?")]
    public async Task Root_help_aliases_json_write_exactly_one_success_object(string helpFlag)
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        int exitCode = await CliApplication.RunAsync(
            [helpFlag, "--json"],
            TextReader.Null,
            standardOutput,
            standardError);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, standardError.ToString());
        using JsonDocument document = JsonDocument.Parse(standardOutput.ToString());
        AssertEnvelope(document.RootElement, success: true);
        AssertRootHelp(Assert.IsType<string>(document.RootElement.GetProperty("result").GetString()));
    }

    [Theory]
    [InlineData("nand", "inspect")]
    [InlineData("nand", "compare")]
    [InlineData("nand", "rgh3-convert")]
    [InlineData("patch", "inspect")]
    [InlineData("console", "list")]
    [InlineData("device", "list")]
    [InlineData("pico", "probe")]
    [InlineData("pico", "smc-stop")]
    [InlineData("pico", "smc-start")]
    [InlineData("pico", "reboot-bootloader")]
    [InlineData("pico", "emmc-probe")]
    [InlineData("pico", "emmc-read")]
    [InlineData("pico", "nand-read")]
    [InlineData("pico", "nand-write")]
    [InlineData("pico", "nand-erase")]
    [InlineData("support", "status")]
    [InlineData("support", "install")]
    [InlineData("xebuild", "build")]
    public async Task Canonical_public_command_help_json_writes_exactly_one_success_object(
        string group,
        string command)
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        int exitCode = await CliApplication.RunAsync(
            [group, command, "--help", "--json"],
            TextReader.Null,
            standardOutput,
            standardError);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, standardError.ToString());
        using JsonDocument document = JsonDocument.Parse(standardOutput.ToString());
        AssertEnvelope(document.RootElement, success: true);
        string help = Assert.IsType<string>(document.RootElement.GetProperty("result").GetString());
        Assert.Contains($"Usage: jrunner {group} {command}", help);
    }

    [Fact]
    public async Task Console_list_json_writes_one_success_object_instead_of_human_output()
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        int exitCode = await CliApplication.RunAsync(
            ["console", "list", "--json"],
            TextReader.Null,
            standardOutput,
            standardError);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, standardError.ToString());
        using JsonDocument document = JsonDocument.Parse(standardOutput.ToString());
        AssertEnvelope(document.RootElement, success: true);
        Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("result").ValueKind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task XeBuild_help_describes_only_the_canonical_public_build_contract(bool json)
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        int exitCode = await CliApplication.RunAsync(
            json ? ["xebuild", "build", "--help", "--json"] : ["xebuild", "build", "--help"],
            TextReader.Null,
            standardOutput,
            standardError);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, standardError.ToString());
        string help;
        if (json)
        {
            using JsonDocument document = JsonDocument.Parse(standardOutput.ToString());
            AssertEnvelope(document.RootElement, success: true);
            help = Assert.IsType<string>(document.RootElement.GetProperty("result").GetString());
        }
        else
        {
            help = standardOutput.ToString();
        }
        Assert.Contains("--type <canonical>", help);
        Assert.Contains("[--console <canonical-name>]", help);
        Assert.Contains("[--drive-patch usb|hdd|both]", help);
        foreach (string option in new[]
        {
            "--cpu-key-file", "--cpu-key-env", "--cpu-key-stdin",
            "--patch", "--system-partition-only", "--full-4gb-data",
            "--backend", "--support-root", "--keep-workspace", "--dashlaunch",
            "--bigffs", "--rgh3", "--drive-patch",
        })
        {
            Assert.Contains(option, help);
        }

        foreach (string oldOption in new[]
        {
            "--hack", "--rgh1-capable", "--xdkbuild",
            "--usbdsec", "--corona-key-fix", "--workspace-root", "--wine-prefix",
        })
        {
            Assert.DoesNotContain(oldOption, help);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_required_leaf_operands_use_a_generic_redacted_usage_error(bool jsonRequested)
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        string[] arguments = jsonRequested ? ["nand", "inspect", "--json"] : ["nand", "inspect"];

        int exitCode = await CliApplication.RunAsync(
            arguments,
            TextReader.Null,
            standardOutput,
            standardError);

        AssertUsageFailure(
            exitCode,
            standardOutput.ToString(),
            standardError.ToString(),
            jsonRequested,
            "invalid-command-arguments");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_global_support_root_requires_a_value_even_when_help_is_requested(bool helpRequested)
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        List<string> arguments = ["console", "list", "--support-root", "--json"];
        if (helpRequested)
        {
            arguments.Add("--help");
        }

        int exitCode = await CliApplication.RunAsync(
            arguments,
            TextReader.Null,
            standardOutput,
            standardError);

        AssertUsageFailure(
            exitCode,
            standardOutput.ToString(),
            standardError.ToString(),
            jsonRequested: true,
            "invalid-command-arguments");
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("", true)]
    [InlineData("   ", false)]
    [InlineData("   ", true)]
    [InlineData("invalid\0support-root", false)]
    [InlineData("invalid\0support-root", true)]
    public async Task Invalid_global_support_root_values_are_rejected_before_dispatch_or_help(
        string supportRoot,
        bool helpRequested)
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        List<string> arguments = ["--support-root", supportRoot, "console", "list", "--json"];
        if (helpRequested)
        {
            arguments.Add("--help");
        }

        int exitCode = await CliApplication.RunAsync(
            arguments,
            TextReader.Null,
            standardOutput,
            standardError);

        AssertUsageFailure(
            exitCode,
            standardOutput.ToString(),
            standardError.ToString(),
            jsonRequested: true,
            "invalid-command-arguments");
    }

    [Fact]
    public async Task A_filesystem_root_is_not_a_valid_global_support_root_even_for_help()
    {
        string supportRoot = Assert.IsType<string>(Path.GetPathRoot(Path.GetFullPath(".")));
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        int exitCode = await CliApplication.RunAsync(
            ["--support-root", supportRoot, "support", "status", "--help", "--json"],
            TextReader.Null,
            standardOutput,
            standardError);

        AssertUsageFailure(
            exitCode,
            standardOutput.ToString(),
            standardError.ToString(),
            jsonRequested: true,
            "invalid-command-arguments");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task An_old_XeBuild_option_is_rejected_with_a_generic_redacted_error_even_with_help(
        bool helpRequested,
        bool jsonRequested)
    {
        const string secret = "00112233445566778899AABBCCDDEEFF";
        string root = Path.Combine(Path.GetTempPath(), $"jrunner-rejected-command-{Guid.NewGuid():N}");
        List<string> arguments =
        [
            "xebuild", "build",
            "--input", Path.Combine(root, "nand.bin"),
            "--cpu-key-file", Path.Combine(root, $"{secret}.txt"),
            "--output", Path.Combine(root, "updflash.bin"),
            "--dashboard", "17559",
            "--type", "glitch2",
            "--hack", secret,
        ];
        if (helpRequested)
        {
            arguments.Add("--help");
        }

        if (jsonRequested)
        {
            arguments.Add("--json");
        }

        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        int exitCode = await CliApplication.RunAsync(
            arguments,
            TextReader.Null,
            standardOutput,
            standardError);

        AssertUsageFailure(
            exitCode,
            standardOutput.ToString(),
            standardError.ToString(),
            jsonRequested,
            "invalid-command-arguments");
        Assert.DoesNotContain(secret, standardOutput.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secret, standardError.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(root));
    }

    private static void AssertRootHelp(string help)
    {
        Assert.Contains("Usage: jrunner [--support-root <path>] <command> [options]", help);
        Assert.DoesNotContain("--wine-prefix", help);
        foreach (string command in new[]
        {
            "nand inspect", "nand compare", "nand rgh3-convert", "patch inspect",
            "console list", "device list", "pico probe", "support status", "support install", "xebuild build",
        })
        {
            Assert.Contains(command, help);
        }
    }

    private static void AssertEnvelope(JsonElement envelope, bool success)
    {
        Assert.Equal(JsonValueKind.Object, envelope.ValueKind);
        Assert.Equal(1, envelope.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(success, envelope.GetProperty("ok").GetBoolean());
    }

    private static void AssertUsageFailure(
        int exitCode,
        string standardOutput,
        string standardError,
        bool jsonRequested,
        string expectedKind)
    {
        Assert.Equal(2, exitCode);
        string message;
        if (jsonRequested)
        {
            using JsonDocument document = JsonDocument.Parse(standardOutput);
            AssertEnvelope(document.RootElement, success: false);
            JsonElement error = document.RootElement.GetProperty("error");
            Assert.Equal(2, error.GetProperty("code").GetInt32());
            Assert.Equal(expectedKind, error.GetProperty("kind").GetString());
            message = Assert.IsType<string>(error.GetProperty("message").GetString());
            Assert.False(string.IsNullOrWhiteSpace(message));
        }
        else
        {
            Assert.Equal(string.Empty, standardOutput);
            message = standardError.TrimEnd('\r', '\n');
        }

        if (expectedKind == "invalid-command-arguments")
        {
            Assert.Equal("The command arguments are invalid.", message);
        }

        Assert.Equal(message + Environment.NewLine, standardError);
    }
}
