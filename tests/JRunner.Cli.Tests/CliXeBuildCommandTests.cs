using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JRunner.Cli;
using JRunner.Cli.Infrastructure;
using JRunner.Core.Binary;
using JRunner.Core.Contracts;
using JRunner.Core.Nand.Hacks;
using JRunner.Core.Nand.Inspection;
using JRunner.Core.Nand.Inspection.Models;
using JRunner.Core.Nand.Models;
using JRunner.Core.Nand.Physical;
using JRunner.Core.Nand.Security;
using JRunner.Core.XeBuild;
using JRunner.Core.XeBuild.Preparation;
using Xunit;

namespace JRunner.Cli.Tests;

public sealed class CliXeBuildCommandTests
{
    private const string CpuKeyText = "00112233445566778899AABBCCDDEEFF";
    private const string WrongCpuKeyText = "FFEEDDCCBBAA99887766554433221100";
    private const string CpuKeyEnvironmentVariable = "JRUNNER_TEST_XEBUILD_CPU_KEY";
    private const string DvdKeySentinel = "SYNTHETIC-DVD-KEY";
    private const int LogicalFixtureLength = 0xD0000;
    private const int FirstStageOffset = 0x8000;
    private const int CbLength = 0x3C0;
    private const int SmcOffset = 0x1000;
    private const int SmcLength = 0x2DC0;
    private const int KeyvaultOffset = 0x4000;
    private const int PatchOffset = 0xC0010;
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode PrivateDirectoryMode = PrivateFileMode | UnixFileMode.UserExecute;

    private static readonly string[] CanonicalConsoleNames =
    [
        "Trinity 16MB", "Falcon 16MB", "Zephyr 16MB", "Jasper 16MB", "Jasper XSB",
        "Jasper BB", "Xenon 64MB", "Xenon 16MB", "Corona BB", "Corona 16MB",
        "Corona 4GB", "Trinity BB", "Zephyr 64MB", "Falcon 64MB", "Winchester 16MB",
        "Winchester 4GB", "Winchester BB",
    ];

    private static readonly string[] CanonicalTypeNames =
    [
        "retail", "glitch", "jtag", "glitch2", "glitch2m", "devgl", "devgl16",
        "devkit", "devkit16", "testkit", "testkit16",
    ];

    [Fact]
    public async Task Console_list_json_exposes_the_complete_canonical_console_and_type_catalogs()
    {
        CliCapture capture = await InvokeAsync(["console", "list", "--json"]);

        Assert.Equal(0, capture.ExitCode);
        Assert.Equal(string.Empty, capture.StandardError);
        JsonElement envelope = AssertJsonEnvelope(capture, ok: true);
        JsonElement consoles = envelope.GetProperty("result");
        Assert.Equal(JsonValueKind.Array, consoles.ValueKind);
        Assert.Equal(CanonicalConsoleNames.Length, consoles.GetArrayLength());
        Assert.Equal(
            CanonicalConsoleNames,
            consoles.EnumerateArray().Select(console => console.GetProperty("canonicalName").GetString()).ToArray());
        Assert.Equal(
            Enumerable.Range(1, CanonicalConsoleNames.Length),
            consoles.EnumerateArray().Select(console => console.GetProperty("legacyId").GetInt32()));
        Assert.Equal(CanonicalTypeNames, XeBuildHackTypeCatalog.All.Select(XeBuildHackTypeCatalog.GetCanonicalName).ToArray());

        foreach (JsonElement entry in consoles.EnumerateArray())
        {
            ConsoleDefinition console = ConsoleCatalog.Get(entry.GetProperty("legacyId").GetInt32());
            Assert.Equal(console.XeBuildName, entry.GetProperty("xeBuildName").GetString());
            Assert.Equal(console.IniName, entry.GetProperty("iniName").GetString());
            Assert.Equal(console.NandSizeMegabytes, entry.GetProperty("nandSizeMegabytes").GetInt32());
            Assert.Equal(console.LogicalNandByteLength, entry.GetProperty("logicalNandByteLength").GetInt64());
            Assert.Equal(console.LegacyLayoutId, entry.GetProperty("legacyLayoutId").GetInt32());
            string[] types = entry.GetProperty("supportedHackTargets").EnumerateArray()
                .Select(type => type.GetString() ?? throw new InvalidOperationException("A console target was not a string."))
                .ToArray();
            Assert.Equal(
                XeBuildCompatibilityMatrix.GetSupportedHackTypes(console.Id).Select(XeBuildHackTypeCatalog.GetCanonicalName),
                types);
            Assert.All(types, type => Assert.Contains(type, CanonicalTypeNames));
            Assert.DoesNotContain(types, type => type.Contains('-', StringComparison.Ordinal));
        }

        JsonElement trinity = consoles[0];
        Assert.DoesNotContain("glitch", ReadTypeNames(trinity));
        Assert.DoesNotContain("jtag", ReadTypeNames(trinity));
        Assert.Contains("glitch2", ReadTypeNames(trinity));
        Assert.Contains("glitch", ReadTypeNames(consoles[1]));
        Assert.Contains("jtag", ReadTypeNames(consoles[1]));
    }

    [Fact]
    public async Task Console_list_human_output_uses_the_same_canonical_names_and_types()
    {
        CliCapture capture = await InvokeAsync(["console", "list"]);

        Assert.Equal(0, capture.ExitCode);
        Assert.Equal(string.Empty, capture.StandardError);
        string[] lines = capture.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(CanonicalConsoleNames.Length, lines.Length);
        for (int index = 0; index < lines.Length; index++)
        {
            Assert.Contains(CanonicalConsoleNames[index], lines[index], StringComparison.Ordinal);
            Assert.Contains("retail", lines[index], StringComparison.Ordinal);
            Assert.Contains("glitch2", lines[index], StringComparison.Ordinal);
            Assert.Contains("devgl16", lines[index], StringComparison.Ordinal);
            Assert.DoesNotContain("dev-gl", lines[index], StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("--input")]
    [InlineData("--output")]
    [InlineData("--dashboard")]
    [InlineData("--type")]
    public async Task Build_requires_each_mandatory_non_key_option_before_opening_any_resources(string omittedOption)
    {
        using var temporary = new TemporaryDirectory();
        BuildFiles files = BuildFiles.In(temporary);
        List<string> arguments = BuildArguments(files);
        RemoveOptionAndValue(arguments, omittedOption);
        var backend = new PublishingBackend();
        using var input = new NeverReadTextReader();

        CliCapture capture = await InvokeAsync(arguments, backend, input);

        AssertJsonFailure(capture, ExitCode.Usage);
        Assert.Equal(0, input.ReadCount);
        Assert.Empty(backend.Requests);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temporary.Path));
    }

    [Fact]
    public async Task Build_requires_one_key_source_before_missing_input_output_planner_or_native_backend()
    {
        using var temporary = new TemporaryDirectory();
        BuildFiles files = BuildFiles.In(temporary);
        List<string> arguments = BuildArguments(files);
        RemoveOptionAndValue(arguments, "--cpu-key-file");
        SetOptionValue(arguments, "--output", temporary.File("absent-directory/output.bin"));
        SetOptionValue(arguments, "--dashboard", "1");
        arguments.AddRange(["--backend", "native"]);
        var backend = new PublishingBackend();
        using var input = new NeverReadTextReader();

        CliCapture capture = await InvokeAsync(arguments, backend, input);

        AssertJsonFailure(capture, ExitCode.Usage, "cpu-key-source-required");
        Assert.Equal(0, input.ReadCount);
        Assert.Empty(backend.Requests);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temporary.Path));
    }

    [Theory]
    [MemberData(nameof(ConflictingKeySources))]
    public async Task Build_rejects_every_key_source_pair_and_triple_before_reading_either_source(string[] selectors, bool helpRequested)
    {
        using var temporary = new TemporaryDirectory();
        BuildFiles files = BuildFiles.In(temporary);
        List<string> arguments = BuildArguments(files);
        RemoveOptionAndValue(arguments, "--cpu-key-file");
        arguments.AddRange(selectors);
        arguments.AddRange(["--backend", "native"]);
        SetOptionValue(arguments, "--output", temporary.File("absent-directory/output.bin"));
        if (helpRequested)
        {
            arguments.Add("--help");
        }

        var backend = new PublishingBackend();
        using var input = new NeverReadTextReader();

        CliCapture capture = await InvokeAsync(arguments, backend, input);

        AssertJsonFailure(capture, ExitCode.Usage, "cpu-key-source-conflict");
        Assert.Equal(0, input.ReadCount);
        Assert.Empty(backend.Requests);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temporary.Path));
    }

    public static IEnumerable<object[]> ConflictingKeySources()
    {
        string[][] selections =
        [
            ["--cpu-key-file", CpuKeyText, "--cpu-key-env", CpuKeyText],
            ["--cpu-key-file", CpuKeyText, "--cpu-key-stdin"],
            ["--cpu-key-env", CpuKeyText, "--cpu-key-stdin"],
            ["--cpu-key-file", CpuKeyText, "--cpu-key-env", CpuKeyText, "--cpu-key-stdin"],
        ];
        foreach (string[] selection in selections)
        {
            yield return [selection, false];
            yield return [selection, true];
        }
    }

    [Theory]
    [MemberData(nameof(MalformedOptionCardinalities))]
    public async Task Option_arity_is_checked_before_key_files_input_output_or_backend_even_with_help(string[] malformedOptions, bool helpRequested)
    {
        using var temporary = new TemporaryDirectory();
        List<string> arguments = BuildArguments(BuildFiles.In(temporary));
        string option = malformedOptions[0];
        if (option.StartsWith("--cpu-key-", StringComparison.Ordinal))
        {
            RemoveOptionAndValue(arguments, "--cpu-key-file");
        }
        else if (arguments.Contains(option, StringComparer.Ordinal))
        {
            RemoveOptionAndValue(arguments, option);
        }

        arguments.AddRange(malformedOptions);
        if (helpRequested)
        {
            arguments.Add("--help");
        }

        var backend = new PublishingBackend();
        using var input = new NeverReadTextReader();

        CliCapture capture = await InvokeAsync(arguments, backend, input);

        AssertJsonFailure(capture, ExitCode.Usage);
        Assert.Equal(0, input.ReadCount);
        Assert.Empty(backend.Requests);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temporary.Path));
    }

    public static IEnumerable<object[]> MalformedOptionCardinalities()
    {
        foreach (string option in new[]
        {
            "--input", "--output", "--dashboard", "--type", "--console", "--cpu-key-file",
            "--cpu-key-env", "--patch", "--drive-patch", "--backend", "--support-root",
        })
        {
            yield return [new[] { option }, false];
            yield return [new[] { option }, true];
        }

        string[][] selections =
        [
            ["--patch", "nofcrt", "usbdsec"],
            ["--patch", "nofcrt", "--patch"],
            ["--patch", "--patch", "nofcrt"],
            ["--cpu-key-file", CpuKeyText, WrongCpuKeyText],
            ["--cpu-key-env", CpuKeyEnvironmentVariable, CpuKeyText],
            ["--cpu-key-stdin", CpuKeyText],
        ];
        foreach (string[] selection in selections)
        {
            yield return [selection, false];
            yield return [selection, true];
        }
    }

    [Theory]
    [MemberData(nameof(InvalidSelectionValues))]
    public async Task Invalid_public_values_fail_before_missing_key_input_output_planner_or_backend(
        string option,
        string value,
        string? expectedKind)
    {
        using var temporary = new TemporaryDirectory();
        List<string> arguments = BuildArguments(BuildFiles.In(temporary));
        if (option == "--cpu-key-env")
        {
            RemoveOptionAndValue(arguments, "--cpu-key-file");
        }

        SetOptionValue(arguments, option, value);
        var backend = new PublishingBackend();
        using var input = new NeverReadTextReader();

        CliCapture capture = await InvokeAsync(arguments, backend, input);

        AssertJsonFailure(capture, ExitCode.Usage, expectedKind);
        Assert.Equal(0, input.ReadCount);
        Assert.Empty(backend.Requests);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temporary.Path));
    }

    public static IEnumerable<object[]> InvalidSelectionValues()
    {
        foreach (string dashboard in new[] { "0", "-1", "latest", "17559.0", "2147483648", CpuKeyText })
        {
            yield return ["--dashboard", dashboard, dashboard is "0" or "-1" ? "xebuild-dashboard-invalid" : null!];
        }

        foreach (string type in new[] { "", " ", "glitch2 ", "not_a_type", "4", CpuKeyText })
        {
            yield return ["--type", type, "xebuild-type-invalid"];
        }

        foreach (string console in new[] { "", " ", "Trinity 16MB ", "not-a-console", "0", "999", CpuKeyText })
        {
            yield return ["--console", console, "xebuild-console-unsupported"];
        }

        foreach (string drivePatch in new[] { "", "optical", "0", CpuKeyText })
        {
            yield return ["--drive-patch", drivePatch, "xebuild-drive-patch-invalid"];
        }

        foreach (string backend in new[] { "", "auto", "default", "1", "wine ", CpuKeyText })
        {
            yield return ["--backend", backend, "xebuild-backend-invalid"];
        }

        foreach (string patch in new[] { "", " ", "../escape", "patch.bin", "patch-name", "two names", "nofcrt,usbdsec", "-a" })
        {
            yield return ["--patch", patch, patch == "-a" ? null! : "xebuild-patch-invalid"];
        }

        yield return ["--cpu-key-env", CpuKeyText, "invalid-cpu-key-env"];
        yield return ["--cpu-key-file", " ", "invalid-cpu-key-file"];
    }

    [Theory]
    [MemberData(nameof(RetiredBuildShapes))]
    public async Task Removed_flags_aliases_numeric_consoles_and_explicit_drive_none_are_rejected_even_with_help(
        string[] retiredShape,
        bool helpRequested)
    {
        using var temporary = new TemporaryDirectory();
        List<string> arguments = BuildArguments(BuildFiles.In(temporary));
        if (retiredShape[0] is "--type" or "--console")
        {
            RemoveOptionAndValue(arguments, retiredShape[0]);
        }

        arguments.AddRange(retiredShape);
        if (helpRequested)
        {
            arguments.Add("--help");
        }

        var backend = new PublishingBackend();
        using var input = new NeverReadTextReader();

        CliCapture capture = await InvokeAsync(arguments, backend, input);

        AssertJsonFailure(capture, ExitCode.Usage);
        Assert.Equal(0, input.ReadCount);
        Assert.Empty(backend.Requests);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temporary.Path));
    }

    public static IEnumerable<object[]> RetiredBuildShapes()
    {
        string[][] flags =
        [
            ["--hack", "glitch2"], ["--rgh1-capable"], ["--xdkbuild"], ["--usbdsec"],
            ["--corona-key-fix"], ["--workspace-root", "not-created"], ["--drive-patch", "none"],
            ["--drive-patch", "NONE"],
        ];
        foreach (string[] shape in flags)
        {
            yield return [shape, false];
            yield return [shape, true];
        }

        foreach (string alias in new[]
        {
            "glitch-2", "glitch-2m", "dev-gl", "devgl-16", "dev-gl16", "dev-gl-16",
            "devkit-16", "testkit-16",
        })
        {
            yield return [new[] { "--type", alias }, false];
            yield return [new[] { "--type", alias }, true];
        }

        foreach (string alias in ConsoleCatalog.All.Select(console => console.XeBuildName).Distinct(StringComparer.Ordinal))
        {
            yield return [new[] { "--console", alias }, false];
            yield return [new[] { "--console", alias }, true];
        }

        foreach (string canonicalName in CanonicalConsoleNames)
        {
            yield return [new[] { "--console", canonicalName.Replace(' ', '-') }, false];
            yield return [new[] { "--console", canonicalName.Replace(' ', '-') }, true];
        }

        for (int legacyId = 1; legacyId <= 17; legacyId++)
        {
            yield return [new[] { "--console", legacyId.ToString(CultureInfo.InvariantCulture) }, false];
            yield return [new[] { "--console", legacyId.ToString(CultureInfo.InvariantCulture) }, true];
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Retired_Wine_prefix_is_an_unknown_option_before_resource_access_even_with_help(
        bool hasValue,
        bool helpRequested,
        bool json)
    {
        using var temporary = new TemporaryDirectory();
        List<string> arguments = BuildArguments(BuildFiles.In(temporary));
        arguments.Add("--wine-prefix");
        if (hasValue)
        {
            arguments.Add(temporary.File(CpuKeyText));
        }

        if (helpRequested)
        {
            arguments.Add("--help");
        }

        if (!json)
        {
            arguments.Remove("--json");
        }

        var backend = new PublishingBackend();
        using var input = new NeverReadTextReader();

        CliCapture capture = await InvokeAsync(arguments, backend, input);

        if (json)
        {
            AssertJsonFailure(capture, ExitCode.Usage, "invalid-command-arguments");
        }
        else
        {
            Assert.Equal((int)ExitCode.Usage, capture.ExitCode);
            Assert.Equal(string.Empty, capture.StandardOutput);
        }

        Assert.Equal("The command arguments are invalid." + Environment.NewLine, capture.StandardError);
        Assert.Equal(0, input.ReadCount);
        Assert.Empty(backend.Requests);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temporary.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bare_build_help_succeeds_without_mandatory_operands_or_resource_access(bool json)
    {
        using var temporary = new TemporaryDirectory();
        List<string> arguments = ["xebuild", "build", "--help"];
        if (json)
        {
            arguments.Add("--json");
        }

        var backend = new PublishingBackend();
        using var input = new NeverReadTextReader();

        CliCapture capture = await InvokeAsync(arguments, backend, input);

        Assert.Equal(0, capture.ExitCode);
        Assert.Equal(string.Empty, capture.StandardError);
        string help = json
            ? AssertJsonEnvelope(capture, ok: true).GetProperty("result").GetString()!
            : capture.StandardOutput;
        Assert.Contains("--dashboard", help, StringComparison.Ordinal);
        Assert.Contains("--type", help, StringComparison.Ordinal);
        Assert.DoesNotContain("--wine-prefix", help, StringComparison.Ordinal);
        Assert.Equal(0, input.ReadCount);
        Assert.Empty(backend.Requests);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temporary.Path));
    }

    [Theory]
    [MemberData(nameof(PartialHelpOmissions))]
    public async Task Help_accepts_partial_mandatory_selectors_without_opening_resources(string[] omittedOptions, bool json)
    {
        using var temporary = new TemporaryDirectory();
        BuildFiles files = BuildFiles.In(temporary);
        List<string> arguments = BuildArguments(files);
        foreach (string option in omittedOptions)
        {
            RemoveOptionAndValue(arguments, option);
        }

        RemoveOptionAndValue(arguments, "--cpu-key-file");
        arguments.Remove("--rgh3");
        arguments.AddRange(["--cpu-key-stdin", "--keep-workspace", "--help"]);
        if (!json)
        {
            arguments.Remove("--json");
        }

        var backend = new PublishingBackend();
        using var input = new NeverReadTextReader();

        CliCapture capture = await InvokeAsync(arguments, backend, input);

        Assert.Equal(0, capture.ExitCode);
        Assert.Equal(string.Empty, capture.StandardError);
        string help = json
            ? AssertJsonEnvelope(capture, ok: true).GetProperty("result").GetString()!
            : capture.StandardOutput;
        Assert.Contains("--dashboard", help, StringComparison.Ordinal);
        Assert.Contains("--type", help, StringComparison.Ordinal);
        Assert.Equal(0, input.ReadCount);
        Assert.Empty(backend.Requests);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temporary.Path));
    }

    public static IEnumerable<object[]> PartialHelpOmissions()
    {
        string[][] omissions =
        [
            ["--dashboard"],
            ["--type"],
            ["--dashboard", "--type"],
            ["--input", "--output"],
        ];
        foreach (string[] omittedOptions in omissions)
        {
            yield return [omittedOptions, false];
            yield return [omittedOptions, true];
        }
    }

    [Theory]
    [MemberData(nameof(MalformedPartialHelpSelections))]
    public async Task Help_rejects_malformed_supplied_selections_even_when_other_mandatory_operands_are_absent(
        string[] selections,
        string expectedKind,
        bool json)
    {
        using var temporary = new TemporaryDirectory();
        List<string> arguments = ["xebuild", "build", "--help"];
        arguments.AddRange(selections);
        if (json)
        {
            arguments.Add("--json");
        }

        var backend = new PublishingBackend();
        using var input = new NeverReadTextReader();

        CliCapture capture = await InvokeAsync(arguments, backend, input);

        if (json)
        {
            AssertJsonFailure(capture, ExitCode.Usage, expectedKind);
        }
        else
        {
            Assert.Equal((int)ExitCode.Usage, capture.ExitCode);
            Assert.Equal(string.Empty, capture.StandardOutput);
            Assert.NotEmpty(capture.StandardError);
        }

        Assert.Equal(0, input.ReadCount);
        Assert.Empty(backend.Requests);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temporary.Path));
    }

    public static IEnumerable<object[]> MalformedPartialHelpSelections()
    {
        yield return [new[] { "--dashboard", "0" }, "xebuild-dashboard-invalid", true];
        yield return [new[] { "--dashboard", CpuKeyText }, "invalid-command-arguments", true];
        yield return [new[] { "--dashboard", CpuKeyText }, "invalid-command-arguments", false];
        yield return [new[] { "--type", CpuKeyText }, "xebuild-type-invalid", true];
        yield return [new[] { "--type", CpuKeyText }, "xebuild-type-invalid", false];
        yield return [new[] { "--cpu-key-file", " " }, "invalid-cpu-key-file", true];
        yield return [new[] { "--cpu-key-env", CpuKeyText }, "invalid-cpu-key-env", true];
        yield return [new[] { "--cpu-key-file", CpuKeyText, "--cpu-key-stdin" }, "cpu-key-source-conflict", true];
        yield return [new[] { "--console", CpuKeyText }, "xebuild-console-unsupported", true];
        yield return [new[] { "--drive-patch", CpuKeyText }, "xebuild-drive-patch-invalid", true];
        yield return [new[] { "--patch", "../escape" }, "xebuild-patch-invalid", true];
        yield return [new[] { "--patch", "nofcrt", "--patch", "nofcrt" }, "xebuild-patch-invalid", true];
        yield return [new[] { "--backend", CpuKeyText }, "xebuild-backend-invalid", true];
        yield return [new[] { "--system-partition-only", "--full-4gb-data" }, "xebuild-4gb-staging-policy-conflict", true];

        foreach (string option in new[] { "--dashboard", "--type" })
        {
            yield return [new[] { option }, "invalid-command-arguments", true];
        }

        yield return [new[] { "--cpu-key-stdin", CpuKeyText }, "invalid-command-arguments", true];
    }

    [Theory]
    [InlineData("retail")]
    [InlineData("glitch")]
    [InlineData("jtag")]
    [InlineData("glitch2")]
    [InlineData("glitch2m")]
    [InlineData("devgl")]
    [InlineData("devgl16")]
    [InlineData("devkit")]
    [InlineData("devkit16")]
    [InlineData("testkit")]
    [InlineData("testkit16")]
    public async Task Help_accepts_every_case_insensitive_canonical_type_without_opening_files(string canonicalType)
    {
        using var temporary = new TemporaryDirectory();
        List<string> arguments = BuildArguments(BuildFiles.In(temporary));
        arguments.Remove("--rgh3");
        SetOptionValue(arguments, "--type", canonicalType.ToUpperInvariant());
        SetOptionValue(arguments, "--dashboard", "2147483647");
        arguments.Add("--help");
        var backend = new PublishingBackend();
        using var input = new NeverReadTextReader();

        CliCapture capture = await InvokeAsync(arguments, backend, input);

        Assert.Equal(0, capture.ExitCode);
        Assert.Equal(string.Empty, capture.StandardError);
        JsonElement envelope = AssertJsonEnvelope(capture, ok: true);
        string help = envelope.GetProperty("result").GetString()!;
        Assert.Contains("--type", help, StringComparison.Ordinal);
        Assert.Contains("--cpu-key-env", help, StringComparison.Ordinal);
        Assert.Contains("--cpu-key-stdin", help, StringComparison.Ordinal);
        Assert.Contains("[--console", help, StringComparison.Ordinal);
        Assert.Contains("--patch", help, StringComparison.Ordinal);
        Assert.Contains("--system-partition-only", help, StringComparison.Ordinal);
        Assert.Contains("--full-4gb-data", help, StringComparison.Ordinal);
        Assert.Contains("--keep-workspace", help, StringComparison.Ordinal);
        foreach (string retiredFlag in new[] { "--hack", "--rgh1-capable", "--xdkbuild", "--usbdsec", "--corona-key-fix", "--workspace-root", "--wine-prefix", "legacy-id" })
        {
            Assert.DoesNotContain(retiredFlag, help, StringComparison.Ordinal);
        }

        Assert.Equal(0, input.ReadCount);
        Assert.Empty(backend.Requests);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temporary.Path));
    }

    [Theory]
    [InlineData("--cpu-key-file", " ", "invalid-cpu-key-file")]
    [InlineData("--cpu-key-env", "0CPU_KEY", "invalid-cpu-key-env")]
    public async Task Help_rejects_invalid_cpu_key_selector_syntax_without_resolving_sources(
        string option,
        string value,
        string expectedKind)
    {
        using var temporary = new TemporaryDirectory();
        BuildFiles files = BuildFiles.In(temporary);
        List<string> arguments = BuildArguments(files);
        if (option == "--cpu-key-env")
        {
            RemoveOptionAndValue(arguments, "--cpu-key-file");
        }

        SetOptionValue(arguments, option, value);
        arguments.Add("--help");
        var backend = new PublishingBackend();
        using var input = new NeverReadTextReader();

        CliCapture capture = await InvokeAsync(arguments, backend, input);

        AssertJsonFailure(capture, ExitCode.Usage, expectedKind);
        Assert.Equal(0, input.ReadCount);
        Assert.Empty(backend.Requests);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temporary.Path));
    }

    [Theory]
    [InlineData("--cpu-key-file")]
    [InlineData("--cpu-key-env")]
    public async Task Help_accepts_valid_cpu_key_selectors_without_resolving_sources(string option)
    {
        using var temporary = new TemporaryDirectory();
        BuildFiles files = BuildFiles.In(temporary);
        List<string> arguments = BuildArguments(files);
        string value;
        if (option == "--cpu-key-env")
        {
            RemoveOptionAndValue(arguments, "--cpu-key-file");
            value = $"JRUNNER_HELP_MISSING_KEY_{Guid.NewGuid():N}";
        }
        else
        {
            value = files.CpuKeyPath;
        }

        SetOptionValue(arguments, option, value);
        arguments.Add("--help");
        var backend = new PublishingBackend();
        using var input = new NeverReadTextReader();

        CliCapture capture = await InvokeAsync(arguments, backend, input);

        Assert.Equal(0, capture.ExitCode);
        Assert.Equal(string.Empty, capture.StandardError);
        AssertJsonEnvelope(capture, ok: true);
        Assert.Equal(0, input.ReadCount);
        Assert.Empty(backend.Requests);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temporary.Path));
    }


    [Theory]
    [InlineData("")]
    [InlineData("wine")]
    [InlineData("WiNe")]
    public async Task Default_and_explicit_Wine_dispatch_a_typed_request_from_confirmed_live_source_inspection(string backendValue)
    {
        using var temporary = new TemporaryDirectory();
        BuildFiles files = await CreateBuildFilesAsync(temporary, physicalLayout: NandPhysicalLayout.Layout1);
        List<string> arguments = BuildArguments(files);
        RemoveOptionAndValue(arguments, "--console");
        if (backendValue.Length > 0)
        {
            arguments.AddRange(["--backend", backendValue]);
        }

        var backend = new PublishingBackend();
        CliCapture capture = await InvokeAsync(arguments, backend);

        XeBuildRequest request = await AssertSuccessfulPublicationAsync(capture, files, backend);
        Assert.Equal(17_301_504L, request.Source.ByteLength);
        Assert.Equal(ConsoleId.Trinity16Mb, request.Source.DetectedConsoleId);
        Assert.False(request.Source.SupportsRgh1);
        Assert.False(request.Source.IsFourGigabyteEmmc);
        Assert.Null(request.Target.ConsoleOverride);
        Assert.Equal(17559, request.Target.DashboardVersion);
        Assert.Equal("glitch2", request.Target.TypeCanonicalName);
        Assert.Equal(XeBuildHackType.Glitch2, request.Target.HackType);
        Assert.False(request.Target.Options.BigFfs);
        Assert.True(request.Target.Options.Rgh3);
        Assert.False(request.Target.Options.DashLaunch);
        Assert.Equal(XeBuildDrivePatch.None, request.Target.Options.DrivePatch);
        Assert.Empty(request.Target.Options.NamedPatches);
        Assert.Equal(XeBuildBackendKind.Wine, request.Execution.Backend);
        Assert.Equal(XeBuildFourGigabyteStagingPolicy.None, request.Execution.FourGigabyteStagingPolicy);
        Assert.Null(request.Execution.WorkspaceRootPath);
        Assert.False(request.Execution.KeepWorkspace);
        Assert.False(request.Execution.OverwriteExistingOutput);
    }

    [Fact]
    public async Task Unsupported_canonical_type_fails_before_source_support_planner_backend_or_process_work()
    {
        using var temporary = new TemporaryDirectory();
        BuildFiles files = BuildFiles.In(temporary);
        await File.WriteAllTextAsync(files.CpuKeyPath, CpuKeyText);
        List<string> arguments = BuildArguments(files);
        arguments.Remove("--rgh3");
        SetOptionValue(arguments, "--dashboard", "17559");
        var backend = new PublishingBackend();
        using var input = new NeverReadTextReader();

        CliCapture capture = await InvokeAsync(arguments, backend, input);

        AssertJsonFailure(capture, ExitCode.MissingPrerequisite, "xebuild-output-evidence-unavailable");
        Assert.DoesNotContain("xebuild-support-file-missing", capture.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("xebuild-dashboard-mode-unavailable", capture.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("Completed safe NAND inspection.", capture.StandardError, StringComparison.Ordinal);
        Assert.Equal(0, input.ReadCount);
        Assert.Empty(backend.Requests);
        Assert.False(File.Exists(files.InputPath));
        Assert.False(File.Exists(files.OutputPath));
        Assert.False(Directory.Exists(files.SupportRoot));
        Assert.True(File.Exists(files.CpuKeyPath));
    }

    [Theory]
    [MemberData(nameof(CanonicalConsoleSelections))]
    public async Task Every_RGH3_compatible_canonical_console_can_override_live_source_facts_case_insensitively(string canonicalName, ConsoleId expectedId)
    {
        using var temporary = new TemporaryDirectory();
        BuildFiles files = await CreateBuildFilesAsync(temporary);
        List<string> arguments = BuildArguments(files);
        SetOptionValue(arguments, "--console", canonicalName.ToUpperInvariant());
        var backend = new PublishingBackend();

        CliCapture capture = await InvokeAsync(arguments, backend);

        XeBuildRequest request = await AssertSuccessfulPublicationAsync(capture, files, backend);
        Assert.Equal(expectedId, request.Target.ConsoleOverride!.Id);
        Assert.Equal(canonicalName, request.Target.ConsoleOverride.CanonicalName);
        Assert.Null(request.Source.DetectedConsoleId);
        Assert.False(request.Source.IsFourGigabyteEmmc);
        Assert.Equal(XeBuildFourGigabyteStagingPolicy.None, request.Execution.FourGigabyteStagingPolicy);
    }

    public static IEnumerable<object[]> CanonicalConsoleSelections()
    {
        for (int index = 0; index < CanonicalConsoleNames.Length; index++)
        {
            if (XeBuildCompatibilityMatrix.SupportsRgh3((ConsoleId)(index + 1)))
            {
                yield return [CanonicalConsoleNames[index], (ConsoleId)(index + 1)];
            }
        }
    }

    [Theory]
    [InlineData("retail", 17559, ExitCode.MissingPrerequisite, "xebuild-output-evidence-unavailable")]
    [InlineData("glitch", 17559, ExitCode.MissingPrerequisite, "xebuild-output-evidence-unavailable")]
    [InlineData("jtag", 17559, ExitCode.MissingPrerequisite, "xebuild-output-evidence-unavailable")]
    [InlineData("glitch2", 17559, ExitCode.Success, null)]
    [InlineData("glitch2m", 17559, ExitCode.Success, null)]
    [InlineData("devgl", 17559, ExitCode.MissingPrerequisite, "xebuild-output-evidence-unavailable")]
    [InlineData("devgl16", 17559, ExitCode.MissingPrerequisite, "xebuild-output-evidence-unavailable")]
    [InlineData("devkit", 17489, ExitCode.MissingPrerequisite, "xebuild-output-evidence-unavailable")]
    [InlineData("devkit16", 17559, ExitCode.MissingPrerequisite, "xebuild-output-evidence-unavailable")]
    [InlineData("testkit", 17559, ExitCode.MissingPrerequisite, "xebuild-output-evidence-unavailable")]
    [InlineData("testkit16", 17559, ExitCode.MissingPrerequisite, "xebuild-output-evidence-unavailable")]
    public async Task Canonical_types_gate_direct_output_evidence_before_source_or_planner_work(
        string canonicalType,
        int dashboard,
        ExitCode expectedCode,
        string? expectedKind)
    {
        using var temporary = new TemporaryDirectory();
        BuildFiles files = await CreateBuildFilesAsync(temporary, cbBuild: 5771, smcType: 3);
        List<string> arguments = BuildArguments(files);
        SetOptionValue(arguments, "--console", "Falcon 16MB");
        SetOptionValue(arguments, "--type", canonicalType.ToUpperInvariant());
        SetOptionValue(arguments, "--dashboard", dashboard.ToString(CultureInfo.InvariantCulture));
        var backend = new PublishingBackend();

        CliCapture capture = await InvokeAsync(arguments, backend);

        if (expectedCode is ExitCode.Success)
        {
            XeBuildRequest request = await AssertSuccessfulPublicationAsync(capture, files, backend);
            Assert.Equal(canonicalType, request.Target.TypeCanonicalName);
            Assert.Equal(dashboard, request.Target.DashboardVersion);
            Assert.True(request.Source.SupportsRgh1);
        }
        else
        {
            AssertJsonFailure(capture, expectedCode, expectedKind);
            Assert.DoesNotContain("Completed safe NAND inspection.", capture.StandardError, StringComparison.Ordinal);
            Assert.Empty(backend.Requests);
            Assert.False(File.Exists(files.OutputPath));
        }
    }

    [Fact]
    public async Task Canonical_override_keeps_detected_console_and_maps_supported_typed_options_and_execution_policies()
    {
        using var temporary = new TemporaryDirectory();
        BuildFiles files = await CreateBuildFilesAsync(temporary, physicalLayout: NandPhysicalLayout.Layout1);
        await File.WriteAllTextAsync(files.OutputPath, "original-output");
        List<string> arguments = BuildArguments(files);
        SetOptionValue(arguments, "--console", "cOrOnA bB");
        SetOptionValue(arguments, "--type", "GLITCH2");
        arguments.AddRange(
        [
            "--patch", "corona_key_fix", "--patch", "nofcrt", "--patch", "usbdsec",
            "--bigffs", "--drive-patch", "both",
            "--backend", "wine", "--keep-workspace", "--force",
        ]);
        var backend = new PublishingBackend();

        CliCapture capture = await InvokeAsync(arguments, backend);

        XeBuildRequest request = await AssertSuccessfulPublicationAsync(capture, files, backend);
        Assert.Equal(ConsoleId.Trinity16Mb, request.Source.DetectedConsoleId);
        Assert.Equal(ConsoleId.CoronaBigBlock, request.Target.ConsoleOverride!.Id);
        Assert.Equal("Corona BB", request.Target.ConsoleOverride.CanonicalName);
        Assert.True(request.Target.Options.BigFfs);
        Assert.True(request.Target.Options.Rgh3);
        Assert.False(request.Target.Options.DashLaunch);
        Assert.Equal(XeBuildDrivePatch.Both, request.Target.Options.DrivePatch);
        Assert.Equal(["corona_key_fix", "nofcrt", "usbdsec"], request.Target.Options.NamedPatches.ToArray());
        Assert.True(request.Execution.KeepWorkspace);
        Assert.True(request.Execution.OverwriteExistingOutput);
        Assert.Null(request.Execution.WorkspaceRootPath);
        Assert.False(Directory.Exists(files.SupportRoot));
    }

    [Theory]
    [InlineData("usb", XeBuildDrivePatch.Usb)]
    [InlineData("hdd", XeBuildDrivePatch.Hdd)]
    [InlineData("both", XeBuildDrivePatch.Both)]
    public async Task Drive_patch_values_map_to_typed_policies_without_a_named_patch_alias(string value, XeBuildDrivePatch expected)
    {
        using var temporary = new TemporaryDirectory();
        BuildFiles files = await CreateBuildFilesAsync(temporary);
        List<string> arguments = BuildArguments(files);
        arguments.AddRange(["--drive-patch", value]);
        var backend = new PublishingBackend();

        CliCapture capture = await InvokeAsync(arguments, backend);

        XeBuildRequest request = await AssertSuccessfulPublicationAsync(capture, files, backend);
        Assert.Equal(expected, request.Target.Options.DrivePatch);
        Assert.Empty(request.Target.Options.NamedPatches);
    }

    [Fact]
    public async Task Repeated_patch_flags_retain_exact_manifest_spelling_and_caller_order()
    {
        using var temporary = new TemporaryDirectory();
        BuildFiles files = await CreateBuildFilesAsync(temporary);
        List<string> arguments = BuildArguments(files);
        arguments.AddRange(["--patch", "usbdsec", "--patch", "noSShdd", "--patch", "nofcrt"]);
        var backend = new PublishingBackend();

        CliCapture capture = await InvokeAsync(arguments, backend);

        XeBuildRequest request = await AssertSuccessfulPublicationAsync(capture, files, backend);
        Assert.Equal(["usbdsec", "noSShdd", "nofcrt"], request.Target.Options.NamedPatches.ToArray());
    }

    [Fact]
    public async Task Duplicate_patch_names_are_rejected_before_missing_key_source_input_or_backend()
    {
        using var temporary = new TemporaryDirectory();
        List<string> arguments = BuildArguments(BuildFiles.In(temporary));
        arguments.AddRange(["--patch", "nofcrt", "--patch", "nofcrt", "--backend", "native"]);
        var backend = new PublishingBackend();

        CliCapture capture = await InvokeAsync(arguments, backend);

        AssertJsonFailure(capture, ExitCode.Usage, "xebuild-patch-invalid");
        Assert.Empty(backend.Requests);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temporary.Path));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Recursive_support_root_reaches_the_same_typed_request_at_every_command_level(int placement)
    {
        using var temporary = new TemporaryDirectory();
        BuildFiles files = await CreateBuildFilesAsync(temporary);
        List<string> arguments = BuildArguments(files);
        RemoveOptionAndValue(arguments, "--support-root");
        arguments.InsertRange(placement, ["--support-root", files.SupportRoot]);
        var backend = new PublishingBackend();

        CliCapture capture = await InvokeAsync(arguments, backend);

        XeBuildRequest request = await AssertSuccessfulPublicationAsync(capture, files, backend);
        Assert.Equal(files.SupportRoot, request.SupportRootPath);
        Assert.False(Directory.Exists(files.SupportRoot));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ambiguous_live_source_requires_a_canonical_override_and_never_selects_a_ranked_candidate(bool supplyOverride)
    {
        using var temporary = new TemporaryDirectory();
        BuildFiles files = await CreateBuildFilesAsync(temporary, cbBuild: 10375, smcType: 0, physicalLayout: NandPhysicalLayout.Layout1);
        await AssertSourceEvidenceAsync(files, NandEvidenceResolution.Ambiguous, expectedConsole: null, NandHackType.DevGl);
        List<string> arguments = BuildArguments(files);
        if (!supplyOverride)
        {
            RemoveOptionAndValue(arguments, "--console");
        }
        else
        {
            SetOptionValue(arguments, "--console", "cOrOnA 16mB");
        }

        var backend = new PublishingBackend();
        CliCapture capture = await InvokeAsync(arguments, backend);

        if (supplyOverride)
        {
            XeBuildRequest request = await AssertSuccessfulPublicationAsync(capture, files, backend);
            Assert.Null(request.Source.DetectedConsoleId);
            Assert.Equal(ConsoleId.Corona16Mb, request.Target.ConsoleOverride!.Id);
            Assert.False(request.Source.SupportsRgh1);
        }
        else
        {
            AssertJsonFailure(capture, ExitCode.Usage, "xebuild-console-required");
            Assert.Empty(backend.Requests);
            Assert.False(File.Exists(files.OutputPath));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Conflicting_live_source_is_not_promoted_to_a_detected_console_even_when_it_has_a_top_score(bool supplyOverride)
    {
        using var temporary = new TemporaryDirectory();
        BuildFiles files = await CreateBuildFilesAsync(temporary, cbBuild: 9188, smcType: 3, physicalLayout: NandPhysicalLayout.Layout1);
        await AssertSourceEvidenceAsync(files, NandEvidenceResolution.Conflicting, expectedConsole: null, NandHackType.Glitch2);
        List<string> arguments = BuildArguments(files);
        if (!supplyOverride)
        {
            RemoveOptionAndValue(arguments, "--console");
        }

        var backend = new PublishingBackend();
        CliCapture capture = await InvokeAsync(arguments, backend);

        if (supplyOverride)
        {
            XeBuildRequest request = await AssertSuccessfulPublicationAsync(capture, files, backend);
            Assert.Null(request.Source.DetectedConsoleId);
            Assert.Equal(ConsoleId.Trinity16Mb, request.Target.ConsoleOverride!.Id);
        }
        else
        {
            AssertJsonFailure(capture, ExitCode.Usage, "xebuild-console-required");
            Assert.Empty(backend.Requests);
            Assert.False(File.Exists(files.OutputPath));
        }
    }

    [Theory]
    [InlineData(5771)]
    [InlineData(5772)]
    [InlineData(12345)]
    public async Task Rgh1_source_evidence_is_not_reached_for_an_unsupported_canonical_target(int cbBuild)
    {
        using var temporary = new TemporaryDirectory();
        BuildFiles files = await CreateBuildFilesAsync(temporary, cbBuild: cbBuild, smcType: 3, physicalLayout: NandPhysicalLayout.Layout0);
        List<string> arguments = BuildArguments(files);
        RemoveOptionAndValue(arguments, "--console");
        SetOptionValue(arguments, "--type", "gLiTcH");
        var backend = new PublishingBackend();

        CliCapture capture = await InvokeAsync(arguments, backend);

        AssertJsonFailure(capture, ExitCode.MissingPrerequisite, "xebuild-output-evidence-unavailable");
        Assert.DoesNotContain("Completed safe NAND inspection.", capture.StandardError, StringComparison.Ordinal);
        Assert.Empty(backend.Requests);
        Assert.False(File.Exists(files.OutputPath));
    }

    [Fact]
    public async Task Rgh1_output_evidence_gate_precedes_a_CB_X_source_inspection()
    {
        using var temporary = new TemporaryDirectory();
        BuildFiles files = await CreateBuildFilesAsync(temporary, cbBuild: 5771, smcType: 3, physicalLayout: NandPhysicalLayout.Layout0, includeCbX: true);
        await using (FileStream source = File.OpenRead(files.InputPath))
        {
            NandInspectionResult inspection = await NandImageService.InspectAsync(source, CpuKey.Parse(CpuKeyText));
            Assert.Equal(NandHackType.Glitch, inspection.HackEvidence.TablePreferredHack);
            Assert.Equal(NandHackType.Rgh13, inspection.HackEvidence.PreferredHack);
            Assert.Equal(NandEvidenceResolution.Confirmed, inspection.SemanticEvidence.Console.Resolution);
        }

        List<string> arguments = BuildArguments(files);
        RemoveOptionAndValue(arguments, "--console");
        SetOptionValue(arguments, "--type", "glitch");
        var backend = new PublishingBackend();
        CliCapture capture = await InvokeAsync(arguments, backend);

        AssertJsonFailure(capture, ExitCode.MissingPrerequisite, "xebuild-output-evidence-unavailable");
        Assert.DoesNotContain("Completed safe NAND inspection.", capture.StandardError, StringComparison.Ordinal);
        Assert.Empty(backend.Requests);
        Assert.False(File.Exists(files.OutputPath));
    }

    [Theory]
    [MemberData(nameof(PlannerRejections))]
    public async Task Live_pinned_planner_rejects_unavailable_or_incompatible_typed_requests_before_backend(
        string[] selections,
        ExitCode expectedCode,
        string expectedKind)
    {
        using var temporary = new TemporaryDirectory();
        BuildFiles files = await CreateBuildFilesAsync(temporary);
        List<string> arguments = BuildArguments(files);
        if (selections.Contains("--rgh3", StringComparer.Ordinal))
        {
            arguments.Remove("--rgh3");
        }
        ApplySelections(arguments, selections);
        var backend = new PublishingBackend();

        CliCapture capture = await InvokeAsync(arguments, backend);

        AssertJsonFailure(capture, expectedCode, expectedKind);
        if (expectedKind is "xebuild-output-evidence-unavailable")
        {
            Assert.DoesNotContain("Completed safe NAND inspection.", capture.StandardError, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("Completed safe NAND inspection.", capture.StandardError, StringComparison.Ordinal);
        }
        Assert.Empty(backend.Requests);
        Assert.False(File.Exists(files.OutputPath));
        Assert.False(Directory.Exists(files.SupportRoot));
    }

    public static IEnumerable<object[]> PlannerRejections()
    {
        yield return [new[] { "--dashboard", "1" }, ExitCode.Usage, "xebuild-dashboard-mode-unavailable"];
        yield return [new[] { "--dashboard", "2147483647" }, ExitCode.Usage, "xebuild-dashboard-mode-unavailable"];
        yield return [new[] { "--type", "jtag" }, ExitCode.MissingPrerequisite, "xebuild-output-evidence-unavailable"];
        yield return [new[] { "--type", "glitch" }, ExitCode.MissingPrerequisite, "xebuild-output-evidence-unavailable"];
        yield return [new[] { "--bigffs" }, ExitCode.Usage, "xebuild-bigffs-unsupported"];
        yield return [new[] { "--type", "retail", "--bigffs" }, ExitCode.MissingPrerequisite, "xebuild-output-evidence-unavailable"];
        yield return [new[] { "--type", "retail", "--rgh3" }, ExitCode.MissingPrerequisite, "xebuild-output-evidence-unavailable"];
        yield return [new[] { "--console", "Xenon 16MB", "--rgh3" }, ExitCode.Usage, "xebuild-rgh3-unsupported"];
        yield return [new[] { "--type", "retail", "--dashlaunch" }, ExitCode.MissingPrerequisite, "xebuild-output-evidence-unavailable"];
        yield return [new[] { "--dashlaunch" }, ExitCode.MissingPrerequisite, "xebuild-dashlaunch-asset-missing"];
        yield return [new[] { "--type", "retail", "--drive-patch", "usb" }, ExitCode.MissingPrerequisite, "xebuild-output-evidence-unavailable"];
        yield return [new[] { "--type", "retail", "--patch", "usbdsec" }, ExitCode.MissingPrerequisite, "xebuild-output-evidence-unavailable"];
        yield return [new[] { "--patch", "corona_key_fix" }, ExitCode.Usage, "xebuild-named-patch-unsupported"];
        yield return [new[] { "--patch", "missing_patch" }, ExitCode.Usage, "xebuild-named-patch-unavailable"];
        yield return [new[] { "--patch", "USBDSEC" }, ExitCode.Usage, "xebuild-named-patch-unavailable"];
        yield return [new[] { "--patch", "xl_usb" }, ExitCode.Usage, "xebuild-drive-patch-conflict"];
        yield return [new[] { "--patch", "xl_hdd" }, ExitCode.Usage, "xebuild-drive-patch-conflict"];
        yield return [new[] { "--patch", "xl_both" }, ExitCode.Usage, "xebuild-drive-patch-conflict"];
        yield return [new[] { "--drive-patch", "both", "--patch", "xl_both" }, ExitCode.Usage, "xebuild-drive-patch-conflict"];
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Existing_output_requires_force_and_is_atomically_replaced_only_on_success(bool force)
    {
        using var temporary = new TemporaryDirectory();
        BuildFiles files = await CreateBuildFilesAsync(temporary);
        byte[] original = Encoding.ASCII.GetBytes("original-output");
        await File.WriteAllBytesAsync(files.OutputPath, original);
        List<string> arguments = BuildArguments(files);
        if (force)
        {
            arguments.Add("--force");
        }

        var backend = new PublishingBackend();
        CliCapture capture = await InvokeAsync(arguments, backend);

        if (force)
        {
            XeBuildRequest request = await AssertSuccessfulPublicationAsync(capture, files, backend);
            Assert.True(request.Execution.OverwriteExistingOutput);
        }
        else
        {
            AssertJsonFailure(capture, ExitCode.Usage, "destination-exists");
            Assert.Empty(backend.Requests);
            Assert.Equal(original, await File.ReadAllBytesAsync(files.OutputPath));
            Assert.DoesNotContain("Completed safe NAND inspection.", capture.StandardError, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(Directory.EnumerateFiles(temporary.Path), path => path.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(UnixFileMode.GroupWrite)]
    [InlineData(UnixFileMode.OtherWrite)]
    [InlineData(UnixFileMode.GroupWrite | UnixFileMode.StickyBit)]
    [InlineData(UnixFileMode.OtherWrite | UnixFileMode.StickyBit)]
    public async Task Writable_source_is_rejected_before_inspection_preparation_backend_or_process(
        UnixFileMode unsafeBits)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        BuildFiles files = await CreateBuildFilesAsync(temporary);
        // This well-formed key would fail NAND inspection, so the unsafe-source gate must win first.
        await File.WriteAllTextAsync(files.CpuKeyPath, WrongCpuKeyText);
        byte[] originalSha256 = await HashSourceAsync(files.InputPath);
        UnixFileMode originalMode = File.GetUnixFileMode(files.InputPath);
        UnixFileMode unsafeMode = originalMode | unsafeBits;
        string workspaceRoot = temporary.File("workspaces");
        var runner = new SourceStageRecordingProcessRunner();
        var backend = new SourceStagingBackend(workspaceRoot, runner);
        File.SetUnixFileMode(files.InputPath, unsafeMode);
        try
        {
            CliCapture capture = await InvokeAsync(BuildArguments(files), backend);

            AssertJsonFailure(capture, ExitCode.InvalidData, "xebuild-input-unsafe");
            Assert.DoesNotContain("Completed safe NAND inspection.", capture.StandardError, StringComparison.Ordinal);
            AssertNoSourceBackendArtifacts(capture, temporary, files, backend, runner, workspaceRoot);
            Assert.Equal(originalSha256, await HashSourceAsync(files.InputPath));
            Assert.Equal(unsafeMode, File.GetUnixFileMode(files.InputPath));
        }
        finally
        {
            File.SetUnixFileMode(files.InputPath, originalMode);
        }
    }

    [Theory]
    [InlineData(UnixFileMode.GroupWrite, false)]
    [InlineData(UnixFileMode.OtherWrite, false)]
    [InlineData(UnixFileMode.GroupWrite, true)]
    [InlineData(UnixFileMode.OtherWrite, true)]
    public async Task Writable_non_sticky_source_ancestry_is_rejected_before_inspection_or_backend(
        UnixFileMode unsafeBits,
        bool unsafeAncestor)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        BuildFiles files = await CreateBuildFilesAsync(temporary);
        await File.WriteAllTextAsync(files.CpuKeyPath, WrongCpuKeyText);
        string ancestor = temporary.File("source-parent");
        string parent = Path.Combine(ancestor, "nested");
        Directory.CreateDirectory(parent);
        File.SetUnixFileMode(ancestor, PrivateDirectoryMode);
        File.SetUnixFileMode(parent, PrivateDirectoryMode);
        string inputPath = Path.Combine(parent, Path.GetFileName(files.InputPath));
        File.Move(files.InputPath, inputPath);
        files = files with { InputPath = inputPath };
        byte[] originalSha256 = await HashSourceAsync(inputPath);
        string unsafeDirectory = unsafeAncestor ? ancestor : parent;
        UnixFileMode unsafeMode = PrivateDirectoryMode | unsafeBits;
        string workspaceRoot = temporary.File("workspaces");
        var runner = new SourceStageRecordingProcessRunner();
        var backend = new SourceStagingBackend(workspaceRoot, runner);
        File.SetUnixFileMode(unsafeDirectory, unsafeMode);
        try
        {
            CliCapture capture = await InvokeAsync(BuildArguments(files), backend);

            AssertJsonFailure(capture, ExitCode.InvalidData, "xebuild-input-unsafe");
            Assert.DoesNotContain("Completed safe NAND inspection.", capture.StandardError, StringComparison.Ordinal);
            AssertNoSourceBackendArtifacts(capture, temporary, files, backend, runner, workspaceRoot);
            Assert.Equal(originalSha256, await HashSourceAsync(inputPath));
            Assert.Equal(unsafeMode, File.GetUnixFileMode(unsafeDirectory));
        }
        finally
        {
            File.SetUnixFileMode(unsafeDirectory, PrivateDirectoryMode);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Source_symlink_or_symlink_ancestor_is_rejected_before_inspection_or_backend(bool ancestorLink)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        BuildFiles files = await CreateBuildFilesAsync(temporary);
        await File.WriteAllTextAsync(files.CpuKeyPath, WrongCpuKeyText);
        byte[] originalSha256 = await HashSourceAsync(files.InputPath);
        string originalInputPath = files.InputPath;
        string link = temporary.File("source-link");
        if (ancestorLink)
        {
            string parent = temporary.File("actual-source-parent");
            Directory.CreateDirectory(parent);
            File.SetUnixFileMode(parent, PrivateDirectoryMode);
            originalInputPath = Path.Combine(parent, Path.GetFileName(files.InputPath));
            File.Move(files.InputPath, originalInputPath);
            Directory.CreateSymbolicLink(link, parent);
            files = files with { InputPath = Path.Combine(link, Path.GetFileName(originalInputPath)) };
        }
        else
        {
            File.CreateSymbolicLink(link, originalInputPath);
            files = files with { InputPath = link };
        }

        string workspaceRoot = temporary.File("workspaces");
        var runner = new SourceStageRecordingProcessRunner();
        var backend = new SourceStagingBackend(workspaceRoot, runner);

        CliCapture capture = await InvokeAsync(BuildArguments(files), backend);

        AssertJsonFailure(capture, ExitCode.InvalidData, "xebuild-input-unsafe");
        Assert.DoesNotContain("Completed safe NAND inspection.", capture.StandardError, StringComparison.Ordinal);
        AssertNoSourceBackendArtifacts(capture, temporary, files, backend, runner, workspaceRoot);
        Assert.Equal(originalSha256, await HashSourceAsync(originalInputPath));
    }

    [Fact]
    public async Task Same_length_pathname_replacement_after_inspection_is_rejected_before_backend_or_process()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        BuildFiles files = await CreateBuildFilesAsync(temporary, physicalLayout: NandPhysicalLayout.Layout1);
        long inspectedByteLength = new FileInfo(files.InputPath).Length;
        string replacementPath = temporary.File("replacement.bin");
        File.Copy(files.InputPath, replacementPath);
        File.SetUnixFileMode(replacementPath, PrivateFileMode);
        await using (FileStream replacement = new(replacementPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            replacement.Position = replacement.Length - 1;
            int originalByte = replacement.ReadByte();
            replacement.Position--;
            replacement.WriteByte((byte)(originalByte ^ 0xFF));
        }

        Assert.Equal(new FileInfo(files.InputPath).Length, new FileInfo(replacementPath).Length);
        List<string> arguments = BuildArguments(files);
        RemoveOptionAndValue(arguments, "--console");
        string workspaceRoot = temporary.File("workspaces");
        var runner = new SourceStageRecordingProcessRunner();
        var backend = new SourceStagingBackend(workspaceRoot, runner);
        using var output = new StringWriter();
        using var error = new InspectionCompletedTextWriter(
            () => File.Move(replacementPath, files.InputPath, overwrite: true));

        int exitCode = await CliCommandRouter.RunAsync(
            arguments,
            output,
            error,
            CancellationToken.None,
            standardInput: TextReader.Null,
            wineBackend: backend);
        var capture = new CliCapture(exitCode, output.ToString(), error.ToString());

        Assert.Equal(1, error.CompletedInspectionCount);
        AssertJsonFailure(capture, ExitCode.InvalidData, "xebuild-input-changed");
        Assert.Equal(inspectedByteLength, new FileInfo(files.InputPath).Length);
        Assert.False(File.Exists(replacementPath));
        AssertNoSourceBackendArtifacts(capture, temporary, files, backend, runner, workspaceRoot);
    }

    [Fact]
    public async Task Same_length_in_place_write_after_inspection_is_rejected_before_backend_or_process()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        BuildFiles files = await CreateBuildFilesAsync(temporary, physicalLayout: NandPhysicalLayout.Layout1);
        long inspectedByteLength = new FileInfo(files.InputPath).Length;
        byte[] inspectedSha256 = await HashSourceAsync(files.InputPath);
        List<string> arguments = BuildArguments(files);
        RemoveOptionAndValue(arguments, "--console");
        string workspaceRoot = temporary.File("workspaces");
        var runner = new SourceStageRecordingProcessRunner();
        var backend = new SourceStagingBackend(workspaceRoot, runner);
        using var output = new StringWriter();
        // Same-UID perturbation exercises the descriptor recheck, not a same-UID adversary guarantee.
        using var error = new InspectionCompletedTextWriter(() =>
        {
            using FileStream mutation = new(files.InputPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            mutation.Position = mutation.Length - 1;
            int originalByte = mutation.ReadByte();
            mutation.Position--;
            mutation.WriteByte((byte)(originalByte ^ 0xFF));
            mutation.Flush(flushToDisk: true);
        });

        int exitCode = await CliCommandRouter.RunAsync(
            arguments,
            output,
            error,
            CancellationToken.None,
            standardInput: TextReader.Null,
            wineBackend: backend);
        var capture = new CliCapture(exitCode, output.ToString(), error.ToString());

        Assert.Equal(1, error.CompletedInspectionCount);
        AssertJsonFailure(capture, ExitCode.InvalidData, "xebuild-input-changed");
        Assert.Equal(inspectedByteLength, new FileInfo(files.InputPath).Length);
        byte[] changedSha256 = await HashSourceAsync(files.InputPath);
        Assert.False(inspectedSha256.SequenceEqual(changedSha256));
        AssertNoSourceBackendArtifacts(capture, temporary, files, backend, runner, workspaceRoot);
    }

    [Theory]
    [InlineData("input")]
    [InlineData("key")]
    public async Task Force_cannot_overwrite_the_input_or_CPU_key_file(string collision)
    {
        using var temporary = new TemporaryDirectory();
        BuildFiles files = await CreateBuildFilesAsync(temporary);
        string protectedPath = collision == "input" ? files.InputPath : files.CpuKeyPath;
        byte[] original = await File.ReadAllBytesAsync(protectedPath);
        List<string> arguments = BuildArguments(files);
        SetOptionValue(arguments, "--output", Path.Combine(temporary.Path, ".", Path.GetFileName(protectedPath)));
        arguments.Add("--force");
        var backend = new PublishingBackend();

        CliCapture capture = await InvokeAsync(arguments, backend);

        AssertJsonFailure(capture, ExitCode.Usage, "output-matches-input");
        Assert.Empty(backend.Requests);
        Assert.Equal(original, await File.ReadAllBytesAsync(protectedPath));
        Assert.False(File.Exists(files.OutputPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Output_preflight_rejects_missing_parent_or_directory_before_missing_source_or_backend(bool outputIsDirectory)
    {
        using var temporary = new TemporaryDirectory();
        BuildFiles files = BuildFiles.In(temporary);
        await File.WriteAllTextAsync(files.CpuKeyPath, CpuKeyText);
        List<string> arguments = BuildArguments(files);
        SetOptionValue(arguments, "--output", outputIsDirectory ? temporary.Path : temporary.File("missing/output.bin"));
        var backend = new PublishingBackend();

        CliCapture capture = await InvokeAsync(arguments, backend);

        AssertJsonFailure(capture, ExitCode.Usage, outputIsDirectory ? "invalid-destination" : "destination-directory-missing");
        Assert.Empty(backend.Requests);
        Assert.False(File.Exists(files.InputPath));
        Assert.False(File.Exists(files.OutputPath));
        Assert.False(Directory.Exists(temporary.File("missing")));
    }

    [Fact]
    public async Task Failed_atomic_backend_publication_preserves_an_existing_forced_output_and_redacts_the_request()
    {
        using var temporary = new TemporaryDirectory();
        BuildFiles files = await CreateBuildFilesAsync(temporary);
        await File.WriteAllTextAsync(files.OutputPath, "original-output");
        List<string> arguments = BuildArguments(files);
        arguments.Add("--force");
        var backend = new PublishingBackend(failBeforeCommit: true);

        CliCapture capture = await InvokeAsync(arguments, backend);

        AssertJsonFailure(capture, ExitCode.ExternalProcess, "xebuild-output-invalid");
        Assert.Single(backend.Requests);
        Assert.Equal("original-output", await File.ReadAllTextAsync(files.OutputPath));
        Assert.DoesNotContain(Directory.EnumerateFiles(temporary.Path), path => path.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("native")]
    [InlineData("NaTiVe")]
    public async Task Native_is_unavailable_before_missing_source_output_or_planner_and_never_falls_back_to_Wine(string value)
    {
        using var temporary = new TemporaryDirectory();
        BuildFiles files = BuildFiles.In(temporary);
        await File.WriteAllTextAsync(files.CpuKeyPath, CpuKeyText);
        List<string> arguments = BuildArguments(files);
        arguments.AddRange(["--backend", value]);
        SetOptionValue(arguments, "--output", temporary.File("missing/output.bin"));
        SetOptionValue(arguments, "--dashboard", "1");
        var backend = new PublishingBackend();

        CliCapture capture = await InvokeAsync(arguments, backend);

        AssertJsonFailure(capture, ExitCode.MissingPrerequisite, "xebuild-backend-unavailable");
        Assert.Empty(backend.Requests);
        Assert.False(File.Exists(files.InputPath));
        Assert.False(File.Exists(files.OutputPath));
        Assert.False(Directory.Exists(files.SupportRoot));
        Assert.False(Directory.Exists(temporary.File("missing")));
        Assert.DoesNotContain("Completed safe NAND inspection.", capture.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_provided_backend_of_the_wrong_kind_is_rejected_without_invocation_or_fallback()
    {
        using var temporary = new TemporaryDirectory();
        BuildFiles files = await CreateBuildFilesAsync(temporary);
        var backend = new PublishingBackend(kind: XeBuildBackendKind.Native);

        CliCapture capture = await InvokeAsync(BuildArguments(files), backend);

        AssertJsonFailure(capture, ExitCode.MissingPrerequisite, "xebuild-backend-unavailable");
        Assert.Empty(backend.Requests);
        Assert.False(File.Exists(files.OutputPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Router_output_evidence_gate_precedes_source_inspection_for_default_and_explicit_Wine(bool explicitWine)
    {
        using var temporary = new TemporaryDirectory();
        BuildFiles files = await CreateBuildFilesAsync(temporary);
        List<string> arguments = BuildArguments(files);
        arguments.Remove("--rgh3");
        if (explicitWine)
        {
            arguments.AddRange(["--backend", "wine"]);
        }

        CliCapture capture = await InvokeAsync(arguments);

        AssertJsonFailure(capture, ExitCode.MissingPrerequisite, "xebuild-output-evidence-unavailable");
        Assert.DoesNotContain("Completed safe NAND inspection.", capture.StandardError, StringComparison.Ordinal);
        Assert.False(File.Exists(files.OutputPath));
        Assert.False(Directory.Exists(files.SupportRoot));
        Assert.DoesNotContain(Directory.EnumerateFiles(temporary.Path), path => path.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Full_4GB_length_requires_a_policy_even_with_an_override_and_before_inspection(bool overrideConsole)
    {
        using var temporary = new TemporaryDirectory();
        BuildFiles files = BuildFiles.In(temporary);
        await File.WriteAllTextAsync(files.CpuKeyPath, CpuKeyText);
        await using (FileStream sparse = File.Create(files.InputPath))
        {
            sparse.SetLength(XeBuildSourceContext.FourGigabyteEmmcByteLength);
        }
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(files.InputPath, PrivateFileMode);
        }

        List<string> arguments = BuildArguments(files);
        if (!overrideConsole)
        {
            RemoveOptionAndValue(arguments, "--console");
        }

        var backend = new PublishingBackend();
        CliCapture capture = await InvokeAsync(arguments, backend);

        AssertJsonFailure(capture, ExitCode.Usage, "xebuild-4gb-staging-policy-required");
        Assert.Empty(backend.Requests);
        Assert.False(File.Exists(files.OutputPath));
        Assert.DoesNotContain("Completed safe NAND inspection.", capture.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_4GB_policies_are_rejected_before_missing_files_or_help_execution(bool helpRequested)
    {
        using var temporary = new TemporaryDirectory();
        List<string> arguments = BuildArguments(BuildFiles.In(temporary));
        arguments.AddRange(["--system-partition-only", "--full-4gb-data"]);
        if (helpRequested)
        {
            arguments.Add("--help");
        }

        var backend = new PublishingBackend();
        CliCapture capture = await InvokeAsync(arguments, backend);

        AssertJsonFailure(capture, ExitCode.Usage, "xebuild-4gb-staging-policy-conflict");
        Assert.Empty(backend.Requests);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temporary.Path));
    }

    [Theory]
    [InlineData("--system-partition-only")]
    [InlineData("--full-4gb-data")]
    public async Task Either_4GB_policy_is_invalid_for_non_4GB_source_even_when_the_console_override_says_4GB(string flag)
    {
        using var temporary = new TemporaryDirectory();
        BuildFiles files = BuildFiles.In(temporary);
        await File.WriteAllTextAsync(files.CpuKeyPath, CpuKeyText);
        await File.WriteAllBytesAsync(files.InputPath, [0x00]);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(files.InputPath, PrivateFileMode);
        }
        List<string> arguments = BuildArguments(files);
        SetOptionValue(arguments, "--console", "Corona 4GB");
        arguments.Add(flag);
        var backend = new PublishingBackend();

        CliCapture capture = await InvokeAsync(arguments, backend);

        AssertJsonFailure(capture, ExitCode.Usage, "xebuild-4gb-staging-policy-inapplicable");
        Assert.Empty(backend.Requests);
        Assert.False(File.Exists(files.OutputPath));
    }

    [Theory]
    [InlineData("--system-partition-only", XeBuildFourGigabyteStagingPolicy.SystemPartitionOnly)]
    [InlineData("--full-4gb-data", XeBuildFourGigabyteStagingPolicy.FullData)]
    public async Task Sparse_4GB_source_is_really_inspected_and_preserves_the_explicit_selected_policy(
        string flag,
        XeBuildFourGigabyteStagingPolicy policy)
    {
        using var temporary = new TemporaryDirectory();
        BuildFiles files = await CreateBuildFilesAsync(temporary, cbBuild: 13121, smcType: 6, fourGigabyte: true);
        List<string> arguments = BuildArguments(files);
        RemoveOptionAndValue(arguments, "--console");
        arguments.Add(flag);
        var backend = new PublishingBackend();

        CliCapture capture = await InvokeAsync(arguments, backend);

        XeBuildRequest request = await AssertSuccessfulPublicationAsync(capture, files, backend);
        Assert.Equal(XeBuildSourceContext.FourGigabyteEmmcByteLength, request.Source.ByteLength);
        Assert.Equal(ConsoleId.Corona4Gb, request.Source.DetectedConsoleId);
        Assert.True(request.Source.IsFourGigabyteEmmc);
        Assert.False(request.Source.SupportsRgh1);
        Assert.Null(request.Target.ConsoleOverride);
        Assert.Equal(policy, request.Execution.FourGigabyteStagingPolicy);
        Assert.Equal(XeBuildSourceContext.FourGigabyteEmmcByteLength, new FileInfo(files.InputPath).Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task File_and_stdin_key_sources_produce_the_same_parsed_key_and_safe_JSON_result(bool useStandardInput)
    {
        using var temporary = new TemporaryDirectory();
        BuildFiles files = await CreateBuildFilesAsync(temporary);
        List<string> arguments = BuildArguments(files);
        if (useStandardInput)
        {
            RemoveOptionAndValue(arguments, "--cpu-key-file");
            arguments.Add("--cpu-key-stdin");
        }

        using var input = new StringReader(CpuKeyText.ToLowerInvariant() + "\r\n");
        var backend = new PublishingBackend();
        CliCapture capture = await InvokeAsync(arguments, backend, input);

        XeBuildRequest request = await AssertSuccessfulPublicationAsync(capture, files, backend);
        Assert.Equal(CpuKey.Parse(CpuKeyText), request.CpuKey);
        JsonElement result = AssertJsonEnvelope(capture, ok: true).GetProperty("result");
        Assert.Equal(["backend", "outputByteLength", "outputPath"], result.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Wrong_but_well_formed_CPU_key_fails_live_encrypted_source_inspection_before_backend(bool useStandardInput)
    {
        using var temporary = new TemporaryDirectory();
        BuildFiles files = await CreateBuildFilesAsync(temporary);
        List<string> arguments = BuildArguments(files);
        if (useStandardInput)
        {
            RemoveOptionAndValue(arguments, "--cpu-key-file");
            arguments.Add("--cpu-key-stdin");
        }
        else
        {
            await File.WriteAllTextAsync(files.CpuKeyPath, WrongCpuKeyText);
        }

        using var input = new StringReader(WrongCpuKeyText);
        var backend = new PublishingBackend();
        CliCapture capture = await InvokeAsync(arguments, backend, input);

        AssertJsonFailure(capture, ExitCode.InvalidData, "cpu-key-verification-failed");
        Assert.Empty(backend.Requests);
        Assert.False(File.Exists(files.OutputPath));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("", true)]
    [InlineData("00112233445566778899AABBCCDDEEF", false)]
    [InlineData("00112233445566778899AABBCCDDEEF", true)]
    [InlineData("00112233445566778899AABBCCDDEEFF0", false)]
    [InlineData("00112233445566778899AABBCCDDEEFF0", true)]
    [InlineData("00112233445566778899AABBCCDDEEFG", false)]
    [InlineData("00112233445566778899AABBCCDDEEFG", true)]
    [InlineData(" 00112233445566778899AABBCCDDEEFF", false)]
    [InlineData(" 00112233445566778899AABBCCDDEEFF", true)]
    public async Task Invalid_file_and_stdin_key_text_is_validated_before_missing_source_output_or_native_backend(string keyText, bool useStandardInput)
    {
        using var temporary = new TemporaryDirectory();
        BuildFiles files = BuildFiles.In(temporary);
        List<string> arguments = BuildArguments(files);
        if (useStandardInput)
        {
            RemoveOptionAndValue(arguments, "--cpu-key-file");
            arguments.Add("--cpu-key-stdin");
        }
        else
        {
            await File.WriteAllTextAsync(files.CpuKeyPath, keyText);
        }

        SetOptionValue(arguments, "--output", temporary.File("missing/output.bin"));
        arguments.AddRange(["--backend", "native"]);
        using var input = new StringReader(keyText);
        var backend = new PublishingBackend();

        CliCapture capture = await InvokeAsync(arguments, backend, input);

        AssertJsonFailure(capture, ExitCode.Usage, "invalid-cpu-key");
        if (keyText.Length > 0)
        {
            Assert.DoesNotContain(keyText, capture.StandardOutput, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(keyText, capture.StandardError, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Empty(backend.Requests);
        Assert.False(File.Exists(files.InputPath));
        Assert.False(File.Exists(files.OutputPath));
        Assert.False(Directory.Exists(temporary.File("missing")));
    }

    [Fact]
    public async Task Stdin_rejects_a_second_key_line_without_exposing_either_key_or_touching_backend()
    {
        using var temporary = new TemporaryDirectory();
        List<string> arguments = BuildArguments(BuildFiles.In(temporary));
        RemoveOptionAndValue(arguments, "--cpu-key-file");
        arguments.AddRange(["--cpu-key-stdin", "--backend", "native"]);
        using var input = new StringReader(CpuKeyText + "\n" + WrongCpuKeyText + "\n");
        var backend = new PublishingBackend();

        CliCapture capture = await InvokeAsync(arguments, backend, input);

        AssertJsonFailure(capture, ExitCode.Usage, "cpu-key-stdin-extra-data");
        Assert.Empty(backend.Requests);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temporary.Path));
    }

    [Fact]
    public async Task Public_application_stdin_overload_passes_its_reader_to_the_real_router()
    {
        using var temporary = new TemporaryDirectory();
        BuildFiles files = await CreateBuildFilesAsync(temporary);
        List<string> arguments = BuildArguments(files);
        arguments.Remove("--rgh3");
        RemoveOptionAndValue(arguments, "--cpu-key-file");
        arguments.Add("--cpu-key-stdin");
        using var input = new StringReader(CpuKeyText + "\n");
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await CliApplication.RunAsync(arguments, input, output, error);
        var capture = new CliCapture(exitCode, output.ToString(), error.ToString());

        AssertJsonFailure(capture, ExitCode.MissingPrerequisite, "xebuild-output-evidence-unavailable");
        Assert.False(File.Exists(files.OutputPath));
        Assert.False(Directory.Exists(files.SupportRoot));
    }

    [Theory]
    [InlineData(null, ExitCode.Usage, "cpu-key-env-not-found")]
    [InlineData("", ExitCode.Usage, "invalid-cpu-key")]
    [InlineData("not-a-cpu-key", ExitCode.Usage, "invalid-cpu-key")]
    [InlineData(" 00112233445566778899AABBCCDDEEFF", ExitCode.Usage, "invalid-cpu-key")]
    [InlineData(WrongCpuKeyText, ExitCode.InvalidData, "cpu-key-verification-failed")]
    [InlineData(CpuKeyText, ExitCode.MissingPrerequisite, "xebuild-output-evidence-unavailable")]
    [InlineData("00112233445566778899aabbccddeeff\r\n", ExitCode.MissingPrerequisite, "xebuild-output-evidence-unavailable")]
    public async Task Environment_key_is_resolved_only_in_an_isolated_child_process_and_never_serialized(
        string? keyText,
        ExitCode expectedCode,
        string expectedKind)
    {
        using var temporary = new TemporaryDirectory();
        BuildFiles files = await CreateBuildFilesAsync(temporary);
        List<string> arguments = BuildArguments(files);
        RemoveOptionAndValue(arguments, "--cpu-key-file");
        arguments.AddRange(["--cpu-key-env", CpuKeyEnvironmentVariable]);
        if (expectedKind is "xebuild-output-evidence-unavailable")
        {
            arguments.Remove("--rgh3");
        }

        CliCapture capture = await InvokeChildAsync(arguments, keyText);

        AssertJsonFailure(capture, expectedCode, expectedKind);
        Assert.False(File.Exists(files.OutputPath));
        Assert.False(Directory.Exists(files.SupportRoot));
    }

    [Theory]
    [InlineData(null, ExitCode.Usage, "cpu-key-env-not-found")]
    [InlineData("not-a-cpu-key", ExitCode.Usage, "invalid-cpu-key")]
    [InlineData("00112233445566778899AABBCCDDEEFG", ExitCode.Usage, "invalid-cpu-key")]
    [InlineData(CpuKeyText, ExitCode.MissingPrerequisite, "xebuild-backend-unavailable")]
    public async Task Isolated_environment_key_validation_precedes_missing_source_output_planner_and_native_backend(
        string? keyText,
        ExitCode expectedCode,
        string expectedKind)
    {
        using var temporary = new TemporaryDirectory();
        BuildFiles files = BuildFiles.In(temporary);
        List<string> arguments = BuildArguments(files);
        RemoveOptionAndValue(arguments, "--cpu-key-file");
        SetOptionValue(arguments, "--output", temporary.File("missing/output.bin"));
        SetOptionValue(arguments, "--dashboard", "1");
        arguments.AddRange(["--cpu-key-env", CpuKeyEnvironmentVariable, "--backend", "native"]);

        CliCapture capture = await InvokeChildAsync(arguments, keyText);

        AssertJsonFailure(capture, expectedCode, expectedKind);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temporary.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Environment_and_stdin_conflict_wins_over_valid_or_invalid_environment_text_in_an_isolated_process(bool validEnvironment)
    {
        using var temporary = new TemporaryDirectory();
        List<string> arguments = BuildArguments(BuildFiles.In(temporary));
        RemoveOptionAndValue(arguments, "--cpu-key-file");
        arguments.AddRange(["--cpu-key-env", CpuKeyEnvironmentVariable, "--cpu-key-stdin", "--backend", "native"]);

        CliCapture capture = await InvokeChildAsync(arguments, validEnvironment ? CpuKeyText : "invalid-key");

        AssertJsonFailure(capture, ExitCode.Usage, "cpu-key-source-conflict");
        Assert.Empty(Directory.EnumerateFileSystemEntries(temporary.Path));
    }

    [Fact]
    public async Task Human_success_prints_only_published_output_metadata_and_never_the_typed_request()
    {
        using var temporary = new TemporaryDirectory();
        BuildFiles files = await CreateBuildFilesAsync(temporary);
        List<string> arguments = BuildArguments(files);
        arguments.Remove("--json");
        var backend = new PublishingBackend();

        CliCapture capture = await InvokeAsync(arguments, backend);

        Assert.Equal(0, capture.ExitCode);
        Assert.Single(backend.Requests);
        Assert.Contains(files.OutputPath, capture.StandardOutput, StringComparison.Ordinal);
        Assert.Contains($"({PublishingBackend.Payload.Length} bytes)", capture.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("XeBuildRequest", capture.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("cpuKey", capture.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PublishingBackend.Payload, await File.ReadAllBytesAsync(files.OutputPath));
        AssertSecretsAbsent(capture);
    }

    [Fact]
    public async Task Human_native_failure_leaves_stdout_empty_and_reports_no_CPU_key_or_request()
    {
        using var temporary = new TemporaryDirectory();
        BuildFiles files = BuildFiles.In(temporary);
        await File.WriteAllTextAsync(files.CpuKeyPath, CpuKeyText);
        List<string> arguments = BuildArguments(files);
        arguments.Remove("--json");
        arguments.AddRange(["--backend", "native"]);
        var backend = new PublishingBackend();

        CliCapture capture = await InvokeAsync(arguments, backend);

        Assert.Equal((int)ExitCode.MissingPrerequisite, capture.ExitCode);
        Assert.Equal(string.Empty, capture.StandardOutput);
        Assert.Contains("unavailable", capture.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("XeBuildRequest", capture.StandardError, StringComparison.Ordinal);
        Assert.Empty(backend.Requests);
        Assert.False(File.Exists(files.OutputPath));
    }

    private static string[] ReadTypeNames(JsonElement entry) =>
        entry.GetProperty("supportedHackTargets").EnumerateArray().Select(type => type.GetString()!).ToArray();

    private static List<string> BuildArguments(BuildFiles files) =>
    [
        "xebuild", "build", "--input", files.InputPath, "--cpu-key-file", files.CpuKeyPath,
        "--dashboard", "17559", "--type", "glitch2", "--rgh3", "--output", files.OutputPath,
        "--console", "Trinity 16MB", "--support-root", files.SupportRoot, "--json",
    ];

    private static void RemoveOptionAndValue(List<string> arguments, string option)
    {
        int index = arguments.IndexOf(option);
        Assert.True(index >= 0, $"The argument list did not contain {option}.");
        arguments.RemoveRange(index, 2);
    }

    private static void SetOptionValue(List<string> arguments, string option, string value)
    {
        int index = arguments.IndexOf(option);
        if (index < 0)
        {
            arguments.AddRange([option, value]);
        }
        else
        {
            arguments[index + 1] = value;
        }
    }

    private static void ApplySelections(List<string> arguments, string[] selections)
    {
        for (int index = 0; index < selections.Length; index++)
        {
            string option = selections[index];
            if (index + 1 < selections.Length && !selections[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                SetOptionValue(arguments, option, selections[++index]);
            }
            else
            {
                arguments.Add(option);
            }
        }
    }

    private static async Task<CliCapture> InvokeAsync(
        IReadOnlyList<string> arguments,
        IXeBuildBackend? backend = null,
        TextReader? input = null)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int exitCode = await CliCommandRouter.RunAsync(
            arguments,
            output,
            error,
            CancellationToken.None,
            standardInput: input ?? TextReader.Null,
            wineBackend: backend);
        var capture = new CliCapture(exitCode, output.ToString(), error.ToString());
        AssertSecretsAbsent(capture);
        return capture;
    }

    private static async Task<CliCapture> InvokeChildAsync(IReadOnlyList<string> arguments, string? environmentKeyText)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "jrunner.exe" : "jrunner"),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment.Remove(CpuKeyEnvironmentVariable);
        if (environmentKeyText is not null)
        {
            startInfo.Environment[CpuKeyEnvironmentVariable] = environmentKeyText;
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the jrunner apphost.");
        process.StandardInput.Close();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task<string> output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await Task.WhenAll(output, error, process.WaitForExitAsync(deadline.Token));
            var capture = new CliCapture(process.ExitCode, await output, await error);
            AssertSecretsAbsent(capture);
            if (!string.IsNullOrWhiteSpace(environmentKeyText))
            {
                Assert.DoesNotContain(environmentKeyText, capture.StandardOutput, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(environmentKeyText, capture.StandardError, StringComparison.OrdinalIgnoreCase);
            }

            return capture;
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

    private static JsonElement AssertJsonEnvelope(CliCapture capture, bool ok)
    {
        AssertSecretsAbsent(capture);
        using JsonDocument document = JsonDocument.Parse(capture.StandardOutput);
        JsonElement envelope = document.RootElement;
        Assert.Equal(JsonValueKind.Object, envelope.ValueKind);
        Assert.Equal(1, envelope.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(ok, envelope.GetProperty("ok").GetBoolean());
        Assert.Equal(
            ok ? ["ok", "result", "schemaVersion"] : new[] { "error", "ok", "schemaVersion" },
            envelope.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
        return envelope.Clone();
    }

    private static void AssertJsonFailure(CliCapture capture, ExitCode code, string? kind = null)
    {
        Assert.Equal((int)code, capture.ExitCode);
        JsonElement error = AssertJsonEnvelope(capture, ok: false).GetProperty("error");
        Assert.Equal(["code", "kind", "message"], error.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal((int)code, error.GetProperty("code").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("kind").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
        if (kind is not null)
        {
            Assert.Equal(kind, error.GetProperty("kind").GetString());
        }

        Assert.NotEmpty(capture.StandardError);
    }

    private static async Task<XeBuildRequest> AssertSuccessfulPublicationAsync(CliCapture capture, BuildFiles files, PublishingBackend backend)
    {
        Assert.Equal(0, capture.ExitCode);
        XeBuildRequest request = Assert.Single(backend.Requests);
        Assert.Equal(files.InputPath, request.Source.InputPath);
        Assert.Equal(files.OutputPath, request.OutputPath);
        Assert.Equal(files.SupportRoot, request.SupportRootPath);
        Assert.Equal(CpuKey.Parse(CpuKeyText), request.CpuKey);
        Assert.Equal(PublishingBackend.Payload, await File.ReadAllBytesAsync(files.OutputPath));
        Assert.Contains("Completed safe NAND inspection.", capture.StandardError, StringComparison.Ordinal);
        JsonElement result = AssertJsonEnvelope(capture, ok: true).GetProperty("result");
        Assert.Equal(["backend", "outputByteLength", "outputPath"], result.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(files.OutputPath, result.GetProperty("outputPath").GetString());
        Assert.Equal(PublishingBackend.Payload.LongLength, result.GetProperty("outputByteLength").GetInt64());
        Assert.Equal("wine", result.GetProperty("backend").GetString());
        return request;
    }

    private static void AssertSecretsAbsent(CliCapture capture)
    {
        foreach (string secret in new[] { CpuKeyText, WrongCpuKeyText, DvdKeySentinel })
        {
            Assert.DoesNotContain(secret, capture.StandardOutput, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secret, capture.StandardError, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void AssertNoSourceBackendArtifacts(
        CliCapture capture,
        TemporaryDirectory temporary,
        BuildFiles files,
        SourceStagingBackend backend,
        SourceStageRecordingProcessRunner runner,
        string workspaceRoot)
    {
        Assert.Empty(backend.Requests);
        Assert.Empty(runner.Calls);
        Assert.Null(backend.StagedInputPath);
        Assert.Null(backend.StagedCpuKeyPath);
        Assert.Null(backend.WorkspaceDirectory);
        Assert.False(Directory.Exists(workspaceRoot));
        Assert.False(Directory.Exists(files.SupportRoot));
        Assert.False(File.Exists(files.OutputPath));
        Assert.True(File.Exists(files.InputPath));
        Assert.True(File.Exists(files.CpuKeyPath));
        Assert.DoesNotContain(Directory.EnumerateFiles(temporary.Path), path => path.EndsWith(".tmp", StringComparison.Ordinal));
        AssertSecretsAbsent(capture);
    }

    private static async Task<byte[]> HashSourceAsync(string path)
    {
        await using FileStream input = new(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 0x10000, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(input);
    }

    private static async Task AssertSourceEvidenceAsync(
        BuildFiles files,
        NandEvidenceResolution resolution,
        ConsoleId? expectedConsole,
        NandHackType tableHack)
    {
        await using FileStream input = File.OpenRead(files.InputPath);
        NandInspectionResult inspection = await NandImageService.InspectAsync(input, CpuKey.Parse(CpuKeyText));
        Assert.Equal(resolution, inspection.SemanticEvidence.Console.Resolution);
        Assert.Equal(expectedConsole, inspection.SemanticEvidence.Console.Console?.Id);
        Assert.Equal(tableHack, inspection.HackEvidence.TablePreferredHack);
    }

    private static async Task<BuildFiles> CreateBuildFilesAsync(
        TemporaryDirectory temporary,
        int cbBuild = 9188,
        int smcType = 5,
        NandPhysicalLayout? physicalLayout = null,
        bool fourGigabyte = false,
        bool includeCbX = false)
    {
        BuildFiles files = BuildFiles.In(temporary);
        await File.WriteAllTextAsync(files.CpuKeyPath, CpuKeyText + "\r\n");
        byte[] logical = CreateLogicalImage(physicalLayout is null ? LogicalFixtureLength : 0x1000000, cbBuild, smcType, includeCbX);
        try
        {
            await using var input = new FileStream(files.InputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 0x10000, FileOptions.Asynchronous);
            if (fourGigabyte)
            {
                input.SetLength(XeBuildSourceContext.FourGigabyteEmmcByteLength);
            }

            if (physicalLayout is null)
            {
                await input.WriteAsync(logical);
            }
            else
            {
                byte[] physical = NandEccCodec.AddEcc(logical, physicalLayout).ToArray();
                try
                {
                    await input.WriteAsync(physical);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(physical);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(logical);
        }

        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(files.InputPath, PrivateFileMode);
        }

        return files;
    }

    // Uses the same positive header, SMC encryption, CB nonce/HMAC/RC4, and patch-section layout
    // as the native NandImageService tests. The encrypted keyvault requires the supplied CPU key.
    private static byte[] CreateLogicalImage(int length, int cbBuild, int smcType, bool includeCbX)
    {
        var image = new byte[length];
        image[0] = 0xFF;
        image[1] = 0x4F;
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(2, sizeof(ushort)), 14699);
        WriteUInt32(image, 0x08, FirstStageOffset);
        WriteUInt32(image, 0x78, SmcLength);
        WriteUInt32(image, 0x7C, SmcOffset);
        var decryptedSmc = new byte[SmcLength];
        decryptedSmc[0x100] = checked((byte)(smcType << 4));
        decryptedSmc[0x101] = 1;
        decryptedSmc[0x102] = 2;
        byte[] encryptedSmc = SmcCrypto.Encrypt(decryptedSmc);
        byte[] keyvault = CreateEncryptedKeyvault();
        byte[] cb = CreateEncryptedCb(cbBuild);
        try
        {
            encryptedSmc.CopyTo(image, SmcOffset);
            keyvault.CopyTo(image, KeyvaultOffset);
            cb.CopyTo(image, FirstStageOffset);
            if (includeCbX)
            {
                int cbXOffset = FirstStageOffset + CbLength;
                WriteBootloaderHeader(image, cbXOffset, 42069, 0x20);
                WriteBootloaderHeader(image, cbXOffset + 0x20, cbBuild, CbLength);
            }

            WriteUInt32(image, PatchOffset, 0x0000C000);
            WriteUInt32(image, PatchOffset + sizeof(uint), 1);
            WriteUInt32(image, PatchOffset + (2 * sizeof(uint)), 0x38800000);
            WriteUInt32(image, PatchOffset + (3 * sizeof(uint)), uint.MaxValue);
            return image;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decryptedSmc);
            CryptographicOperations.ZeroMemory(encryptedSmc);
            CryptographicOperations.ZeroMemory(keyvault);
            CryptographicOperations.ZeroMemory(cb);
        }
    }

    private static byte[] CreateEncryptedKeyvault()
    {
        var keyvault = new byte[KeyvaultService.KeyvaultLength];
        for (int index = 0; index < 0x10; index++)
        {
            keyvault[index] = checked((byte)(0x50 + index));
        }

        Encoding.ASCII.GetBytes("TESTSERIAL01").CopyTo(keyvault, 0xB0);
        Encoding.ASCII.GetBytes(DvdKeySentinel).CopyTo(keyvault, 0x100);
        Span<byte> keyBytes = stackalloc byte[CpuKey.ByteLength];
        Span<byte> rc4Key = stackalloc byte[XeCrypt.HmacSha1TagLength];
        try
        {
            CpuKey.Parse(CpuKeyText).CopyTo(keyBytes);
            XeCrypt.HmacSha1Truncated(keyBytes, keyvault.AsSpan(0, 0x10), rc4Key);
            Rc4.TransformInPlace(rc4Key, keyvault.AsSpan(0x10));
            return keyvault;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
            CryptographicOperations.ZeroMemory(rc4Key);
        }
    }

    private static byte[] CreateEncryptedCb(int build)
    {
        var stage = new byte[CbLength];
        WriteBootloaderHeader(stage, 0, build, stage.Length);
        for (int index = 0; index < 0x10; index++)
        {
            stage[0x10 + index] = checked((byte)(0x20 + index));
        }

        stage[0x20] = 0x11;
        stage[0x21] = 0x22;
        stage[0x22] = 0x33;
        stage[0x3B1] = 9;
        byte[] firstBootloaderKey =
        [
            0xDD, 0x88, 0xAD, 0x0C, 0x9E, 0xD6, 0x69, 0xE7,
            0xB5, 0x67, 0x94, 0xFB, 0x68, 0x56, 0x3E, 0xFA,
        ];
        byte[] rc4Key = XeCrypt.HmacSha1Truncated(firstBootloaderKey, stage.AsSpan(0x10, 0x10));
        try
        {
            Rc4.TransformInPlace(rc4Key, stage.AsSpan(0x20));
            return stage;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(firstBootloaderKey);
            CryptographicOperations.ZeroMemory(rc4Key);
        }
    }

    private static void WriteBootloaderHeader(byte[] image, int offset, int build, int length)
    {
        image[offset] = (byte)'C';
        image[offset + 1] = (byte)'B';
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(offset + 2, sizeof(ushort)), checked((ushort)build));
        WriteUInt32(image, offset + 0x0C, checked((uint)length));
    }

    private static void WriteUInt32(byte[] image, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(offset, sizeof(uint)), value);

    private sealed record CliCapture(int ExitCode, string StandardOutput, string StandardError);

    private sealed record BuildFiles(string InputPath, string CpuKeyPath, string OutputPath, string SupportRoot)
    {
        internal static BuildFiles In(TemporaryDirectory temporary) => new(
            temporary.File("source nand.bin"),
            temporary.File("cpu-key.txt"),
            temporary.File("published image.bin"),
            temporary.File("support root"));
    }

    private sealed class PublishingBackend : IXeBuildBackend
    {
        internal static readonly byte[] Payload = Encoding.ASCII.GetBytes("atomic CLI test backend image\n");
        private readonly bool failBeforeCommit;

        internal PublishingBackend(XeBuildBackendKind kind = XeBuildBackendKind.Wine, bool failBeforeCommit = false)
        {
            Kind = kind;
            this.failBeforeCommit = failBeforeCommit;
        }

        public XeBuildBackendKind Kind { get; }
        internal List<XeBuildRequest> Requests { get; } = [];

        public async Task<XeBuildResult> BuildAsync(XeBuildRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            Assert.Equal(XeBuildBackendKind.Wine, Kind);
            Assert.Equal(Kind, request.Execution.Backend);
            await using var output = AtomicOutputFile.Create(request.OutputPath, request.Execution.OverwriteExistingOutput);
            await output.Stream.WriteAsync(Payload, cancellationToken);
            if (failBeforeCommit)
            {
                throw new OperationFailureException(
                    ExitCode.ExternalProcess,
                    "xebuild-output-invalid",
                    "The test backend rejected its unpublished temporary output.");
            }

            await output.CompleteAsync(cancellationToken);
            return new XeBuildResult(request.OutputPath, Payload.LongLength, Kind);
        }
    }

    private sealed class SourceStagingBackend(string workspaceRoot, IExternalProcessRunner runner) : IXeBuildBackend
    {
        public XeBuildBackendKind Kind => XeBuildBackendKind.Wine;
        internal List<XeBuildRequest> Requests { get; } = [];
        internal string? WorkspaceDirectory { get; private set; }
        internal string? StagedInputPath { get; private set; }
        internal string? StagedCpuKeyPath { get; private set; }

        public async Task<XeBuildResult> BuildAsync(XeBuildRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await using WineXeBuildWorkspace workspace = await WineXeBuildWorkspace.CreateAsync(
                workspaceRoot, cancellationToken);
            WorkspaceDirectory = workspace.RootDirectory;
            StagedInputPath = workspace.StagedInputPath;
            StagedCpuKeyPath = workspace.StagedCpuKeyPath;
            await workspace.StageInputAsync(request.Source, request.Execution.FourGigabyteStagingPolicy, cancellationToken);
            await workspace.StageCpuKeyAsync(request.CpuKey, cancellationToken);
            var invocation = new ExternalProcessInvocation("never-invoked-winepath", Array.Empty<string>(), workspace.RootDirectory);
            runner.EnsureExecutableAvailable(invocation);
            await runner.RunAsync(invocation, cancellationToken);
            return await new PublishingBackend().BuildAsync(request, cancellationToken);
        }
    }

    private sealed class SourceStageRecordingProcessRunner : IExternalProcessRunner
    {
        internal List<ExternalProcessInvocation> Calls { get; } = [];

        public void EnsureExecutableAvailable(ExternalProcessInvocation invocation)
        {
            Calls.Add(invocation);
        }

        public Task<ExternalProcessResult> RunAsync(
            ExternalProcessInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(invocation);
            return Task.FromResult(new ExternalProcessResult(0, string.Empty, false, 0, string.Empty, false, 0));
        }
    }

    private sealed class InspectionCompletedTextWriter(Action onCompletedInspection) : StringWriter
    {
        internal int CompletedInspectionCount { get; private set; }

        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (value?.Contains("[completed-nand-inspection]", StringComparison.Ordinal) is true)
            {
                CompletedInspectionCount++;
                onCompletedInspection();
            }
        }
    }

    private sealed class NeverReadTextReader : TextReader
    {
        internal int ReadCount { get; private set; }

        public override int Read(char[] buffer, int index, int count)
        {
            ReadCount++;
            throw new InvalidOperationException("This command must not consume standard input.");
        }

        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            throw new InvalidOperationException("This command must not consume standard input.");
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"jrunner-xebuild-cli-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            if (OperatingSystem.IsLinux())
            {
                System.IO.File.SetUnixFileMode(Path, PrivateDirectoryMode);
            }
        }

        internal string Path { get; }

        internal string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
