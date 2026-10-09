using System.Text.Json;
using JRunner.Cli;
using JRunner.Core;
using Xunit;

namespace JRunner.Cli.Tests;

public sealed class CliApplicationTests
{
    private const string SecretSentinel = "CLI_SECRET_SENTINEL_7E53A90B";

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
    [MemberData(nameof(PublicCommandGroups))]
    public async Task Unsupported_command_groups_offer_only_their_registered_children(
        string group,
        string[] children,
        bool jsonRequested)
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await CliApplication.RunAsync(
            jsonRequested ? [group, "--json"] : [group],
            TextReader.Null,
            standardOutput,
            standardError);

        string message = AssertUsageFailure(
            exitCode,
            standardOutput.ToString(),
            standardError.ToString(),
            jsonRequested,
            "unsupported-command");
        AssertCanonicalGuidance(message, group);
        foreach (string child in children)
        {
            Assert.Contains(child, message, StringComparison.Ordinal);
        }

        AssertGroupScope(message, group);
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

        string message = AssertUsageFailure(
            exitCode,
            standardOutput.ToString(),
            standardError.ToString(),
            jsonRequested: true,
            "unsupported-command");
        foreach (string group in new[] { "nand", "patch", "console", "device", "pico", "support", "xebuild" })
        {
            Assert.Contains(group, message, StringComparison.Ordinal);
        }

        Assert.Contains("jrunner --help", message, StringComparison.Ordinal);
        if (command is not null)
        {
            Assert.DoesNotContain(command, message, StringComparison.Ordinal);
        }
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
    public async Task Canonical_public_command_help_is_informative_in_human_and_JSON_modes(
        string group,
        string command)
    {
        foreach (bool jsonRequested in new[] { false, true })
        {
            using var standardOutput = new StringWriter();
            using var standardError = new StringWriter();

            int exitCode = await CliApplication.RunAsync(
                jsonRequested ? [group, command, "--help", "--json"] : [group, command, "--help"],
                TextReader.Null,
                standardOutput,
                standardError);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, standardError.ToString());
            string help = ReadHelp(standardOutput.ToString(), jsonRequested);
            Assert.Contains($"Usage: jrunner {group} {command}", help, StringComparison.Ordinal);
            Assert.Contains($"jrunner {group} {command}", ReadHelpExamples(help), StringComparison.Ordinal);
            Assert.Contains("Notes:", help, StringComparison.Ordinal);
            Assert.Contains("Global options:", help, StringComparison.Ordinal);
            Assert.DoesNotContain("Missing required", help, StringComparison.OrdinalIgnoreCase);
            AssertGlobalHelp(help);
        }
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
    public async Task Missing_required_leaf_operands_identify_the_option_and_canonical_usage(bool jsonRequested)
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        string[] arguments = jsonRequested ? ["nand", "inspect", "--json"] : ["nand", "inspect"];

        int exitCode = await CliApplication.RunAsync(
            arguments,
            TextReader.Null,
            standardOutput,
            standardError);

        string message = AssertUsageFailure(
            exitCode,
            standardOutput.ToString(),
            standardError.ToString(),
            jsonRequested,
            "invalid-command-arguments");
        Assert.Contains("--input", message, StringComparison.Ordinal);
        Assert.Contains("missing", message, StringComparison.OrdinalIgnoreCase);
        AssertCanonicalGuidance(message, "nand inspect");
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
    public async Task An_old_XeBuild_option_is_rejected_with_a_redacted_error_even_with_help(
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

    public static IEnumerable<object[]> PublicCommandGroups()
    {
        (string Group, string[] Children)[] groups =
        [
            ("nand", ["inspect", "compare", "rgh3-convert"]),
            ("patch", ["inspect"]),
            ("console", ["list"]),
            ("device", ["list"]),
            ("pico", ["probe", "smc-stop", "smc-start", "reboot-bootloader",
                "nand-read", "nand-write", "nand-erase", "emmc-probe", "emmc-read"]),
            ("support", ["status", "install"]),
            ("xebuild", ["build"]),
        ];
        foreach (var (group, children) in groups)
        {
            foreach (bool jsonRequested in new[] { false, true })
            {
                yield return [group, children, jsonRequested];
            }
        }
    }

    [Theory]
    [MemberData(nameof(PublicCommandGroups))]
    public async Task Group_help_has_its_own_usage_children_and_example(
        string group,
        string[] children,
        bool jsonRequested)
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        int exitCode = await CliApplication.RunAsync(
            jsonRequested ? [group, "--help", "--json"] : [group, "--help"],
            TextReader.Null,
            standardOutput,
            standardError);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, standardError.ToString());
        string help = ReadHelp(standardOutput.ToString(), jsonRequested);
        Assert.Contains($"Usage: jrunner {group} <command> [options]", help, StringComparison.Ordinal);
        int commandsIndex = help.IndexOf("Commands:", StringComparison.Ordinal);
        int globalOptionsIndex = help.IndexOf("Global options:", StringComparison.Ordinal);
        Assert.True(commandsIndex >= 0 && globalOptionsIndex > commandsIndex);
        string[] commandPaths = help[(commandsIndex + "Commands:".Length)..globalOptionsIndex]
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(row => string.Join(" ", row.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(3)))
            .ToArray();
        Assert.Equal(children.Length, commandPaths.Length);
        foreach (string child in children)
        {
            Assert.Contains($"jrunner {group} {child}", commandPaths);
        }

        Assert.Contains($"jrunner {group} {children[0]} --help", ReadHelpExamples(help), StringComparison.Ordinal);
        AssertGlobalHelp(help);
    }

    public static IEnumerable<object[]> MalformedLeafInvocations()
    {
        (string[] Arguments, string Command, string[] Fragments)[] cases =
        [
            (["nand", "inspect", "--input"], "nand inspect", ["--input", "value", "<file>"]),
            (["nand", "compare", SecretSentinel], "nand compare", ["<right>", "missing"]),
            (["pico", "nand-read", "--start-block", SecretSentinel, "--output", "unused.bin"],
                "pico nand-read", ["--start-block", "whole number", "4294967295"]),
            (["pico", "probe", "--timeout", SecretSentinel],
                "pico probe", ["--timeout", "number"]),
            (["xebuild", "build", "--dashboard", SecretSentinel],
                "xebuild build", ["--dashboard", "whole number", "-2147483648", "2147483647"]),
            (["nand", "inspect", "--input", SecretSentinel, "--input", SecretSentinel + "_SECOND"],
                "nand inspect", ["--input", "one value", "once"]),
            (["nand", "compare", SecretSentinel, SecretSentinel + "_RIGHT", SecretSentinel + "_EXTRA"],
                "nand compare", ["unrecognized", "1"]),
            (["nand", "inspect", "--input", SecretSentinel, "--inptu", SecretSentinel],
                "nand inspect", ["unrecognized", "2"]),
        ];
        foreach (var (arguments, command, fragments) in cases)
        {
            foreach (bool jsonRequested in new[] { false, true })
            {
                yield return [arguments, command, fragments, jsonRequested];
            }
        }
    }

    [Theory]
    [MemberData(nameof(MalformedLeafInvocations))]
    public async Task Malformed_leaf_invocations_explain_safe_operands_and_canonical_repair(
        string[] arguments,
        string command,
        string[] expectedFragments,
        bool jsonRequested)
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        int exitCode = await CliApplication.RunAsync(
            jsonRequested ? [.. arguments, "--json"] : arguments,
            TextReader.Null,
            standardOutput,
            standardError);

        string message = AssertUsageFailure(
            exitCode, standardOutput.ToString(), standardError.ToString(), jsonRequested,
            "invalid-command-arguments");
        string diagnosis = ReadDiagnosis(message);
        foreach (string fragment in expectedFragments)
        {
            Assert.Contains(fragment, diagnosis, StringComparison.OrdinalIgnoreCase);
        }

        AssertCanonicalGuidance(message, command);
        Assert.DoesNotContain(SecretSentinel, standardOutput.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SecretSentinel, standardError.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_input_before_the_command_explains_the_missing_selector_without_echoing_the_path(
        bool jsonRequested)
    {
        string suppliedPath = Path.Combine(Path.GetTempPath(), SecretSentinel, "nanddump.bin");
        string[] arguments = [suppliedPath, "nand", "inspect"];
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        int exitCode = await CliApplication.RunAsync(
            jsonRequested ? [.. arguments, "--json"] : arguments,
            TextReader.Null,
            standardOutput,
            standardError);

        string message = AssertUsageFailure(
            exitCode, standardOutput.ToString(), standardError.ToString(), jsonRequested,
            "invalid-command-arguments");
        string diagnosis = ReadDiagnosis(message);
        Assert.Contains("--input", diagnosis, StringComparison.Ordinal);
        Assert.Contains("missing", diagnosis, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unrecognized", diagnosis, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1", diagnosis, StringComparison.Ordinal);
        AssertCanonicalGuidance(message, "nand inspect");
        Assert.Contains("Example: jrunner nand inspect --input nanddump.bin", message, StringComparison.Ordinal);
        Assert.DoesNotContain(suppliedPath, standardOutput.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(suppliedPath, standardError.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(SecretSentinel, standardOutput.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(SecretSentinel, standardError.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--help", false)]
    [InlineData("--help", true)]
    [InlineData("--version", false)]
    [InlineData("--version", true)]
    public async Task Malformed_information_values_do_not_report_omitted_mandatory_operands(
        string informationFlag,
        bool jsonRequested)
    {
        string[] arguments = ["xebuild", "build", "--dashboard", SecretSentinel, informationFlag];
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        int exitCode = await CliApplication.RunAsync(
            jsonRequested ? [.. arguments, "--json"] : arguments,
            TextReader.Null,
            standardOutput,
            standardError);

        string message = AssertUsageFailure(
            exitCode, standardOutput.ToString(), standardError.ToString(), jsonRequested,
            "invalid-command-arguments");
        string diagnosis = ReadDiagnosis(message);
        Assert.Contains("--dashboard", diagnosis, StringComparison.Ordinal);
        Assert.Contains("whole number", diagnosis, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("missing", diagnosis, StringComparison.OrdinalIgnoreCase);
        foreach (string omittedOption in new[] { "--input", "--output", "--type" })
        {
            Assert.DoesNotContain(omittedOption, diagnosis, StringComparison.Ordinal);
        }

        AssertCanonicalGuidance(message, "xebuild build");
        Assert.DoesNotContain(SecretSentinel, standardOutput.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(SecretSentinel, standardError.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData("--help", false)]
    [InlineData("--help", true)]
    [InlineData("--version", false)]
    [InlineData("--version", true)]
    public async Task Unknown_option_names_values_paths_environment_names_and_operands_are_redacted(
        string? informationFlag,
        bool jsonRequested)
    {
        foreach (bool inlineValue in new[] { false, true })
        {
            string suppliedPath = Path.Combine(Path.GetTempPath(), SecretSentinel, "input.bin");
            string environmentName = "JRUNNER_" + SecretSentinel;
            List<string> arguments =
            [
                "nand", "inspect", "--input", suppliedPath, "--cpu-key-env", environmentName,
                inlineValue ? $"--{SecretSentinel}={SecretSentinel}" : $"--{SecretSentinel}",
            ];
            if (!inlineValue)
            {
                arguments.Add(SecretSentinel + "_VALUE");
            }

            arguments.Add(SecretSentinel + "_OPERAND");
            if (informationFlag is not null)
            {
                arguments.Add(informationFlag);
            }

            if (jsonRequested)
            {
                arguments.Add("--json");
            }

            using var standardOutput = new StringWriter();
            using var standardError = new StringWriter();
            int exitCode = await CliApplication.RunAsync(
                arguments, TextReader.Null, standardOutput, standardError);
            string message = AssertUsageFailure(
                exitCode, standardOutput.ToString(), standardError.ToString(), jsonRequested,
                "invalid-command-arguments");
            Assert.Contains("unrecognized", ReadDiagnosis(message), StringComparison.OrdinalIgnoreCase);
            AssertCanonicalGuidance(message, "nand inspect");
            foreach (string supplied in new[] { SecretSentinel, suppliedPath, environmentName })
            {
                Assert.DoesNotContain(supplied, standardOutput.ToString(), StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(supplied, standardError.ToString(), StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private static string ReadHelp(string standardOutput, bool jsonRequested)
    {
        if (!jsonRequested)
        {
            return standardOutput;
        }

        using JsonDocument document = JsonDocument.Parse(standardOutput);
        AssertEnvelope(document.RootElement, success: true);
        return Assert.IsType<string>(document.RootElement.GetProperty("result").GetString());
    }

    private static void AssertGlobalHelp(string help)
    {
        foreach (string option in new[] { "-h", "--help", "/h", "-?", "/?", "--version", "--json", "--support-root <path>" })
        {
            Assert.Contains(option, help, StringComparison.Ordinal);
        }
    }

    private static void AssertGroupScope(string message, string group)
    {
        foreach (var (owner, distinctiveChild) in new[]
        {
            ("nand", "rgh3-convert"), ("pico", "nand-read"),
            ("support", "support install"), ("xebuild", "xebuild build"),
        })
        {
            if (group != owner)
            {
                Assert.DoesNotContain(distinctiveChild, message, StringComparison.Ordinal);
            }
        }
    }

    private static string ReadDiagnosis(string message)
    {
        int usageIndex = message.IndexOf("Usage:", StringComparison.Ordinal);
        Assert.True(usageIndex > 0);
        return message[..usageIndex];
    }

    private static void AssertCanonicalGuidance(string message, string command)
    {
        Assert.Contains($"Usage: jrunner {command}", message, StringComparison.Ordinal);
        Assert.Contains($"Example: jrunner {command}", message, StringComparison.Ordinal);
        Assert.Contains($"jrunner {command} --help", message, StringComparison.Ordinal);
    }

    private static string ReadHelpExamples(string help)
    {
        const string heading = "Examples:";
        int examplesIndex = help.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(examplesIndex > 0);
        return help[(examplesIndex + heading.Length)..];
    }

    private static void AssertRootHelp(string help)
    {
        Assert.Contains("Usage: jrunner [--support-root <path>] <command> [options]", help);
        Assert.DoesNotContain("--wine-prefix", help);
        foreach (string command in new[]
        {
            "nand inspect", "nand compare", "nand rgh3-convert", "patch inspect",
            "console list", "device list", "pico probe", "pico smc-stop", "pico smc-start",
            "pico reboot-bootloader", "pico nand-read", "pico nand-write", "pico nand-erase",
            "pico emmc-probe", "pico emmc-read", "support status", "support install", "xebuild build",
        })
        {
            Assert.Contains(command, help);
        }
        AssertGlobalHelp(help);
        Assert.Contains("jrunner nand inspect --help", ReadHelpExamples(help), StringComparison.Ordinal);
    }

    private static void AssertEnvelope(JsonElement envelope, bool success)
    {
        Assert.Equal(JsonValueKind.Object, envelope.ValueKind);
        Assert.Equal(1, envelope.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(success, envelope.GetProperty("ok").GetBoolean());
    }

    private static string AssertUsageFailure(
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

        Assert.Equal(message + Environment.NewLine, standardError);
        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.Contains("Usage: jrunner", message, StringComparison.Ordinal);
        Assert.Contains("Example: jrunner", message, StringComparison.Ordinal);
        Assert.Contains("--help", message, StringComparison.Ordinal);
        return message;
    }
}
