using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using JRunner.Cli;
using JRunner.Core;
using JRunner.Core.Binary;
using JRunner.Core.Contracts;
using JRunner.Core.Nand.Security;
using Xunit;

namespace JRunner.Cli.Tests;

public sealed class CliNandPatchCommandTests
{
    private const string CpuKeyText = "00112233445566778899AABBCCDDEEFF";

    [Theory]
    [InlineData("file")]
    [InlineData("environment")]
    [InlineData("stdin")]
    public async Task Nand_inspect_json_accepts_each_CPU_key_source_without_serializing_its_contents(string source)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string imagePath = temporaryDirectory.File("nand.bin");
        await File.WriteAllBytesAsync(imagePath, CreateInspectableNandInput());

        CliResult result = await RunWithCpuKeySourceAsync(
            ["nand", "inspect", "--input", imagePath, "--json"],
            source,
            CpuKeyText + "\r\n",
            temporaryDirectory);

        AssertInspectableNandResult(result);
    }

    [Fact]
    public async Task Nand_inspect_without_a_CPU_key_source_does_not_read_standard_input()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string imagePath = temporaryDirectory.File("nand.bin");
        await File.WriteAllBytesAsync(imagePath, CreateInspectableNandInput());
        using var standardInput = new UnreadableInputReader();

        CliResult result = await RunInProcessAsync(
            ["nand", "inspect", "--input", imagePath, "--json"],
            standardInput);

        AssertInspectableNandResult(result);
        Assert.Equal(0, standardInput.ReadCount);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task Nand_inspect_rejects_every_multiple_source_combination_before_reading_missing_inputs(int sourceMask)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var standardInput = new UnreadableInputReader();
        string[] arguments = AddCpuKeySources(
            CreateMissingInputArguments("inspect", temporaryDirectory, temporaryDirectory.File("converted.bin")),
            sourceMask,
            temporaryDirectory.File($"missing-key-{CpuKeyText}.txt"),
            $"JRUNNER_MISSING_KEY_{CpuKeyText}_{Guid.NewGuid():N}");

        CliResult result = await RunInProcessAsync(arguments, standardInput);

        AssertFailure(result, ExitCode.Usage, "cpu-key-source-conflict");
        Assert.Equal(0, standardInput.ReadCount);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temporaryDirectory.Path));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    [InlineData(5, false)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    [InlineData(6, true)]
    [InlineData(7, false)]
    [InlineData(7, true)]
    public async Task Rgh3_convert_requires_exactly_one_CPU_key_source_before_input_or_output_access(
        int sourceMask,
        bool outputExists)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string outputDirectory = temporaryDirectory.File("output");
        string outputPath = Path.Combine(outputDirectory, "converted.bin");
        if (outputExists)
        {
            Directory.CreateDirectory(outputDirectory);
            await File.WriteAllTextAsync(outputPath, "original-output");
        }

        using var standardInput = new UnreadableInputReader();
        string[] arguments = AddCpuKeySources(
            CreateMissingInputArguments("rgh3-convert", temporaryDirectory, outputPath),
            sourceMask,
            temporaryDirectory.File($"missing-key-{CpuKeyText}.txt"),
            $"JRUNNER_MISSING_KEY_{CpuKeyText}_{Guid.NewGuid():N}");

        CliResult result = await RunInProcessAsync(arguments, standardInput);

        AssertFailure(
            result,
            ExitCode.Usage,
            sourceMask == 0 ? "cpu-key-source-required" : "cpu-key-source-conflict");
        Assert.Equal(0, standardInput.ReadCount);
        if (outputExists)
        {
            Assert.Equal("original-output", await File.ReadAllTextAsync(outputPath));
        }
        else
        {
            Assert.False(Directory.Exists(outputDirectory));
        }

        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path, ".*.tmp", SearchOption.AllDirectories));
    }

    public static IEnumerable<object[]> MalformedCpuKeySources()
    {
        foreach (string command in new[] { "inspect", "rgh3-convert" })
        {
            foreach (string source in new[] { "file", "environment", "stdin" })
            {
                foreach (string value in new[] { " " + CpuKeyText, CpuKeyText + "\r", "G" + CpuKeyText[1..], CpuKeyText + "\n" + CpuKeyText })
                {
                    string kind = source == "stdin" && value.Contains('\n')
                        ? "cpu-key-stdin-extra-data"
                        : "invalid-cpu-key";
                    yield return new object[] { command, source, value, kind };
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(MalformedCpuKeySources))]
    public async Task Key_commands_reject_malformed_source_values_before_missing_input_access_without_leaking_keys(
        string command,
        string source,
        string sourceValue,
        string expectedKind)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string outputDirectory = temporaryDirectory.File("uncreated-output");
        string[] arguments = CreateMissingInputArguments(
            command,
            temporaryDirectory,
            Path.Combine(outputDirectory, "converted.bin"));

        // Extra-data detection has redirected-stream semantics, not interactive-terminal semantics.
        CliResult result = source == "stdin" && sourceValue.Contains('\n')
            ? await RunAppHostAsync(
                [.. arguments, "--cpu-key-stdin"], variableName: null, value: null, standardInput: sourceValue)
            : await RunWithCpuKeySourceAsync(arguments, source, sourceValue, temporaryDirectory);

        AssertFailure(result, ExitCode.Usage, expectedKind);
        AssertRedacted(result, sourceValue);
        Assert.False(Directory.Exists(outputDirectory));
        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path, ".*.tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("inspect", "--cpu-key-file", " ", "invalid-cpu-key-file")]
    [InlineData("rgh3-convert", "--cpu-key-file", " ", "invalid-cpu-key-file")]
    [InlineData("inspect", "--cpu-key-env", "CPU-KEY_" + CpuKeyText, "invalid-cpu-key-env")]
    [InlineData("rgh3-convert", "--cpu-key-env", "CPU-KEY_" + CpuKeyText, "invalid-cpu-key-env")]
    public async Task Key_commands_reject_invalid_source_selectors_before_missing_input_access(
        string command,
        string option,
        string selector,
        string expectedKind)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string outputDirectory = temporaryDirectory.File("uncreated-output");
        using var standardInput = new UnreadableInputReader();
        string[] arguments =
        [
            .. CreateMissingInputArguments(command, temporaryDirectory, Path.Combine(outputDirectory, "converted.bin")),
            option, selector,
        ];

        CliResult result = await RunInProcessAsync(arguments, standardInput);

        AssertFailure(result, ExitCode.Usage, expectedKind);
        Assert.Equal(0, standardInput.ReadCount);
        Assert.False(Directory.Exists(outputDirectory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(temporaryDirectory.Path));
    }

    [Theory]
    [InlineData("inspect")]
    [InlineData("rgh3-convert")]
    public async Task Key_commands_report_a_missing_environment_source_without_serializing_its_name(string command)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string variableName = $"JRUNNER_MISSING_KEY_{CpuKeyText}_{Guid.NewGuid():N}";
        string outputDirectory = temporaryDirectory.File("uncreated-output");
        string[] arguments =
        [
            .. CreateMissingInputArguments(command, temporaryDirectory, Path.Combine(outputDirectory, "converted.bin")),
            "--cpu-key-env", variableName,
        ];

        CliResult result = await RunAppHostAsync(arguments, variableName, value: null);

        AssertFailure(result, ExitCode.Usage, "cpu-key-env-not-found");
        AssertRedacted(result, variableName);
        Assert.False(Directory.Exists(outputDirectory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(temporaryDirectory.Path));
    }

    [Theory]
    [InlineData("environment", false)]
    [InlineData("environment", true)]
    [InlineData("stdin", false)]
    [InlineData("stdin", true)]
    public async Task Rgh3_convert_resolves_environment_and_stdin_keys_then_preserves_atomic_output_after_invalid_data(
        string source,
        bool outputExists)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string eccPath = temporaryDirectory.File("template.ecc");
        string flashPath = temporaryDirectory.File("flash.bin");
        string outputPath = temporaryDirectory.File("converted.bin");
        await File.WriteAllBytesAsync(eccPath, [0x00]);
        await File.WriteAllBytesAsync(flashPath, [0x00]);
        if (outputExists)
        {
            await File.WriteAllTextAsync(outputPath, "original-output");
        }

        string[] arguments =
        [
            "nand", "rgh3-convert", "--ecc", eccPath, "--flash", flashPath,
            "--output", outputPath, "--json",
        ];
        if (outputExists)
        {
            CliResult refusedOverwrite = await RunWithCpuKeySourceAsync(
                arguments, source, CpuKeyText + "\n", temporaryDirectory);
            AssertFailure(refusedOverwrite, ExitCode.Usage, "destination-exists");
            Assert.Equal("original-output", await File.ReadAllTextAsync(outputPath));
            Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path, ".*.tmp"));
        }

        CliResult result = await RunWithCpuKeySourceAsync(
            [.. arguments, "--force"], source, CpuKeyText + "\n", temporaryDirectory);

        AssertFailure(result, ExitCode.InvalidData, "invalid-rgh3-ecc-size");
        if (outputExists)
        {
            Assert.Equal("original-output", await File.ReadAllTextAsync(outputPath));
        }
        else
        {
            Assert.False(File.Exists(outputPath));
        }

        Assert.Equal(new byte[] { 0x00 }, await File.ReadAllBytesAsync(eccPath));
        Assert.Equal(new byte[] { 0x00 }, await File.ReadAllBytesAsync(flashPath));
        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path, ".*.tmp"));
    }

    [Fact]
    public async Task Nand_compare_json_reports_an_unequal_canonical_image_as_a_negative_result()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string leftPath = temporaryDirectory.File("left.bin");
        string rightPath = temporaryDirectory.File("right.bin");
        var left = new byte[0x200];
        var right = new byte[0x200];
        right[0x7B] = 0xA5;
        await File.WriteAllBytesAsync(leftPath, left);
        await File.WriteAllBytesAsync(rightPath, right);
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        int exitCode = await CliApplication.RunAsync(
            ["--json", "nand", "compare", leftPath, rightPath],
            standardOutput,
            standardError);

        Assert.Equal(1, exitCode);
        using var document = JsonDocument.Parse(standardOutput.ToString());
        JsonElement result = document.RootElement.GetProperty("result");
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(result.GetProperty("equal").GetBoolean());
        Assert.Equal(1, result.GetProperty("differingByteCount").GetInt64());
        Assert.Equal(0x7B, result.GetProperty("firstDifferingLogicalOffset").GetInt64());
        Assert.Contains("Comparing canonical NAND logical data.", standardError.ToString());
    }

    [Fact]
    public async Task Patch_inspect_json_reports_structural_diagnostics_as_an_inspection_result()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string patchPath = temporaryDirectory.File("patches.bin");
        await File.WriteAllBytesAsync(patchPath, CreateFuseBlowPatch());
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        int exitCode = await CliApplication.RunAsync(
            ["patch", "inspect", "--input", patchPath, "--json"],
            standardOutput,
            standardError);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, standardError.ToString());
        using var document = JsonDocument.Parse(standardOutput.ToString());
        JsonElement result = document.RootElement.GetProperty("result");
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("complete", result.GetProperty("completionStatus").GetString());
        Assert.True(result.GetProperty("isComplete").GetBoolean());
        Assert.Equal("FuseBlow", result.GetProperty("recognizedLegacyPatches")[0].GetProperty("patchName").GetString());
    }

    [Fact]
    public async Task Patch_inspect_returns_a_malformed_section_as_structured_evidence()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string patchPath = temporaryDirectory.File("truncated.bin");
        await File.WriteAllBytesAsync(patchPath, [0x00]);
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        int exitCode = await CliApplication.RunAsync(
            ["patch", "inspect", "--input", patchPath, "--json"],
            standardOutput,
            standardError);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, standardError.ToString());
        using var document = JsonDocument.Parse(standardOutput.ToString());
        JsonElement result = document.RootElement.GetProperty("result");
        Assert.Equal("malformed", result.GetProperty("completionStatus").GetString());
        Assert.True(result.GetProperty("isMalformed").GetBoolean());
        Assert.Equal("truncated-header", result.GetProperty("structuralDiagnostics")[0].GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Rgh3_convert_enforces_atomic_overwrite_and_preserves_existing_output_after_failure()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string eccPath = temporaryDirectory.File("template.ecc");
        string flashPath = temporaryDirectory.File("flash.bin");
        string cpuKeyPath = temporaryDirectory.File("cpu-key.txt");
        string outputPath = temporaryDirectory.File("converted.bin");
        await File.WriteAllBytesAsync(eccPath, [0x00]);
        await File.WriteAllBytesAsync(flashPath, [0x00]);
        await File.WriteAllTextAsync(cpuKeyPath, CpuKeyText + "\n");
        await File.WriteAllTextAsync(outputPath, "original-output");

        using (var standardOutput = new StringWriter())
        using (var standardError = new StringWriter())
        {
            int exitCode = await CliApplication.RunAsync(
                [
                    "nand", "rgh3-convert", "--ecc", eccPath, "--flash", flashPath,
                    "--cpu-key-file", cpuKeyPath, "--output", outputPath, "--json",
                ],
                standardOutput,
                standardError);

            Assert.Equal(2, exitCode);
            Assert.Equal("destination-exists", ReadErrorKind(standardOutput));
            Assert.Equal("original-output", await File.ReadAllTextAsync(outputPath));
        }

        using (var standardOutput = new StringWriter())
        using (var standardError = new StringWriter())
        {
            int exitCode = await CliApplication.RunAsync(
                [
                    "nand", "rgh3-convert", "--ecc", eccPath, "--flash", flashPath,
                    "--cpu-key-file", cpuKeyPath, "--output", outputPath, "--force", "--json",
                ],
                standardOutput,
                standardError);

            Assert.Equal(3, exitCode);
            Assert.Equal("invalid-rgh3-ecc-size", ReadErrorKind(standardOutput));
            Assert.Equal("original-output", await File.ReadAllTextAsync(outputPath));
            Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path, ".*.tmp"));
            Assert.DoesNotContain(CpuKeyText, standardOutput.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(CpuKeyText, standardError.ToString(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rgh3_convert_accepts_direct_tmp_output_and_preserves_destination_after_invalid_data(bool outputExists)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        string eccPath = temporaryDirectory.File("template.ecc");
        string flashPath = temporaryDirectory.File("flash.bin");
        await File.WriteAllBytesAsync(eccPath, [0x00]);
        await File.WriteAllBytesAsync(flashPath, [0x00]);
        string outputName = $"jrunner-cli-rgh3-output-{Guid.NewGuid():N}.bin";
        string outputPath = Path.Combine("/tmp", outputName);

        try
        {
            if (outputExists)
            {
                await File.WriteAllTextAsync(outputPath, "original-output");
            }

            string[] arguments =
            [
                "nand", "rgh3-convert", "--ecc", eccPath, "--flash", flashPath,
                "--output", outputPath, "--json",
            ];
            if (outputExists)
            {
                arguments = [.. arguments, "--force"];
            }

            CliResult result = await RunWithCpuKeySourceAsync(
                arguments, "file", CpuKeyText + "\n", temporaryDirectory);

            // Invalid ECC is reached only after the handler creates its managed temporary output.
            AssertFailure(result, ExitCode.InvalidData, "invalid-rgh3-ecc-size");
            if (outputExists)
            {
                Assert.Equal("original-output", await File.ReadAllTextAsync(outputPath));
            }
            else
            {
                Assert.False(File.Exists(outputPath));
                Assert.False(Directory.Exists(outputPath));
            }

            Assert.Equal(new byte[] { 0x00 }, await File.ReadAllBytesAsync(eccPath));
            Assert.Equal(new byte[] { 0x00 }, await File.ReadAllBytesAsync(flashPath));
            Assert.Empty(Directory.EnumerateFileSystemEntries("/tmp", $".{outputName}.*.tmp"));
        }
        finally
        {
            DeleteDirectTmpOutputArtifacts(outputPath);
        }
    }

    [Theory]
    [InlineData(false, false, "destination-exists")]
    [InlineData(true, false, "invalid-destination")]
    [InlineData(true, true, "invalid-destination")]
    public async Task Rgh3_convert_direct_tmp_existing_destinations_return_nonwriting_errors_without_mutation(
        bool destinationIsDirectory,
        bool force,
        string expectedKind)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        string eccPath = temporaryDirectory.File("template.ecc");
        string flashPath = temporaryDirectory.File("flash.bin");
        await File.WriteAllBytesAsync(eccPath, [0x00]);
        await File.WriteAllBytesAsync(flashPath, [0x00]);
        string outputName = $"jrunner-cli-rgh3-preflight-{Guid.NewGuid():N}.bin";
        string outputPath = Path.Combine("/tmp", outputName);
        string originalPath = destinationIsDirectory ? Path.Combine(outputPath, "sentinel.bin") : outputPath;

        try
        {
            if (destinationIsDirectory)
            {
                Directory.CreateDirectory(outputPath);
            }

            await File.WriteAllTextAsync(originalPath, "original-output");
            string[] arguments =
            [
                "nand", "rgh3-convert", "--ecc", eccPath, "--flash", flashPath,
                "--output", outputPath, "--json",
            ];
            if (force)
            {
                arguments = [.. arguments, "--force"];
            }

            CliResult result = await RunWithCpuKeySourceAsync(
                arguments, "file", CpuKeyText + "\n", temporaryDirectory);

            AssertFailure(result, ExitCode.Usage, expectedKind);
            Assert.Equal("original-output", await File.ReadAllTextAsync(originalPath));
            if (destinationIsDirectory)
            {
                Assert.True(Directory.Exists(outputPath));
                Assert.Equal(new[] { originalPath }, Directory.EnumerateFileSystemEntries(outputPath).ToArray());
            }

            Assert.Equal(new byte[] { 0x00 }, await File.ReadAllBytesAsync(eccPath));
            Assert.Equal(new byte[] { 0x00 }, await File.ReadAllBytesAsync(flashPath));
            Assert.Empty(Directory.EnumerateFileSystemEntries("/tmp", $".{outputName}.*.tmp"));
        }
        finally
        {
            DeleteDirectTmpOutputArtifacts(outputPath);
        }
    }

    [Fact]
    public async Task Rgh3_convert_rejects_cpu_key_and_symlinked_source_output_aliases()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string inputsDirectory = temporaryDirectory.File("inputs");
        string linkedInputsDirectory = temporaryDirectory.File("linked-inputs");
        Directory.CreateDirectory(inputsDirectory);
        Directory.CreateSymbolicLink(linkedInputsDirectory, inputsDirectory);
        string eccPath = Path.Combine(inputsDirectory, "template.ecc");
        string flashPath = Path.Combine(inputsDirectory, "flash.bin");
        string cpuKeyPath = Path.Combine(inputsDirectory, "cpu-key.txt");
        await File.WriteAllBytesAsync(eccPath, [0x00]);
        await File.WriteAllBytesAsync(flashPath, [0x00]);
        await File.WriteAllTextAsync(cpuKeyPath, CpuKeyText + "\n");

        foreach (string outputPath in new[] { cpuKeyPath, Path.Combine(linkedInputsDirectory, "template.ecc") })
        {
            using var standardOutput = new StringWriter();
            using var standardError = new StringWriter();
            int exitCode = await CliApplication.RunAsync(
                [
                    "nand", "rgh3-convert", "--ecc", eccPath, "--flash", flashPath,
                    "--cpu-key-file", cpuKeyPath, "--output", outputPath, "--force", "--json",
                ],
                standardOutput,
                standardError);

            Assert.Equal(2, exitCode);
            Assert.Equal("output-matches-input", ReadErrorKind(standardOutput));
            Assert.Equal(CpuKeyText + "\n", await File.ReadAllTextAsync(cpuKeyPath));
            Assert.Equal(new byte[] { 0x00 }, await File.ReadAllBytesAsync(eccPath));
            Assert.Empty(Directory.EnumerateFiles(inputsDirectory, ".*.tmp"));
        }
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("/h")]
    [InlineData("-?")]
    [InlineData("/?")]
    public async Task Rgh3_convert_help_short_circuits_before_opening_or_replacing_files(string helpOption)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string outputPath = temporaryDirectory.File("converted.bin");
        await File.WriteAllTextAsync(outputPath, "original-output");
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        int exitCode = await CliApplication.RunAsync(
            [
                "nand", "rgh3-convert", "--ecc", temporaryDirectory.File("missing.ecc"),
                "--flash", temporaryDirectory.File("missing-flash.bin"),
                "--cpu-key-file", temporaryDirectory.File("missing-key.txt"),
                "--output", outputPath, "--force", helpOption, "--json",
            ],
            standardOutput,
            standardError);

        Assert.Equal(0, exitCode);
        Assert.Equal("original-output", await File.ReadAllTextAsync(outputPath));
        Assert.Equal(string.Empty, standardError.ToString());
        using var document = JsonDocument.Parse(standardOutput.ToString());
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("Usage: jrunner nand rgh3-convert", document.RootElement.GetProperty("result").GetString());
    }

    [Fact]
    public async Task Version_option_short_circuits_before_conversion_file_access()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string outputPath = temporaryDirectory.File("converted.bin");
        await File.WriteAllTextAsync(outputPath, "original-output");
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        int exitCode = await CliApplication.RunAsync(
            ["nand", "rgh3-convert", "--output", outputPath, "--force", "--version", "--json"],
            standardOutput,
            standardError);

        Assert.Equal(0, exitCode);
        Assert.Equal("original-output", await File.ReadAllTextAsync(outputPath));
        Assert.Equal(string.Empty, standardError.ToString());
        using var document = JsonDocument.Parse(standardOutput.ToString());
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(JRunnerVersion.Display, document.RootElement.GetProperty("result").GetString());
    }

    private static void DeleteDirectTmpOutputArtifacts(string outputPath)
    {
        if (Directory.Exists(outputPath))
        {
            Directory.Delete(outputPath, recursive: true);
        }
        else
        {
            File.Delete(outputPath);
        }

        foreach (string temporaryPath in Directory.EnumerateFileSystemEntries(
                     "/tmp", $".{Path.GetFileName(outputPath)}.*.tmp"))
        {
            if (Directory.Exists(temporaryPath))
            {
                Directory.Delete(temporaryPath, recursive: true);
            }
            else
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void AssertInspectableNandResult(CliResult result)
    {
        Assert.Equal((int)ExitCode.Success, result.ExitCode);
        using JsonDocument document = ReadSingleEnvelope(result.StandardOutput);
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        JsonElement inspection = document.RootElement.GetProperty("result");
        Assert.Equal("logical", inspection.GetProperty("canonicalImage").GetProperty("detectedFormat").GetString());
        Assert.Equal("0xFF4F", inspection.GetProperty("header").GetProperty("magic").GetProperty("hexadecimal").GetString());
        AssertRedacted(result, CpuKeyText);
    }

    private static void AssertFailure(CliResult result, ExitCode expectedCode, string expectedKind)
    {
        Assert.Equal((int)expectedCode, result.ExitCode);
        using JsonDocument document = ReadSingleEnvelope(result.StandardOutput);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        JsonElement error = document.RootElement.GetProperty("error");
        Assert.Equal((int)expectedCode, error.GetProperty("code").GetInt32());
        Assert.Equal(expectedKind, error.GetProperty("kind").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
        Assert.NotEmpty(result.StandardError);
        AssertRedacted(result, CpuKeyText);
    }

    private static JsonDocument ReadSingleEnvelope(string standardOutput)
    {
        // Parsing the entire stream rejects extra JSON envelopes and non-JSON stdout.
        JsonDocument document = JsonDocument.Parse(standardOutput);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        return document;
    }

    private static void AssertRedacted(CliResult result, string secret)
    {
        Assert.DoesNotContain(secret, result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secret, result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    private static string[] CreateMissingInputArguments(
        string command,
        TemporaryDirectory temporaryDirectory,
        string outputPath)
    {
        return command switch
        {
            "inspect" =>
            [
                "nand", "inspect", "--input", temporaryDirectory.File("missing-nand.bin"), "--json",
            ],
            "rgh3-convert" =>
            [
                "nand", "rgh3-convert", "--ecc", temporaryDirectory.File("missing.ecc"),
                "--flash", temporaryDirectory.File("missing-flash.bin"), "--output", outputPath, "--json",
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };
    }

    private static string[] AddCpuKeySources(
        IReadOnlyList<string> arguments,
        int sourceMask,
        string keyFilePath,
        string environmentVariable)
    {
        // The theory masks select file (1), environment (2), and stdin (4).
        var result = new List<string>(arguments);
        if ((sourceMask & 1) != 0)
        {
            result.AddRange(["--cpu-key-file", keyFilePath]);
        }

        if ((sourceMask & 2) != 0)
        {
            result.AddRange(["--cpu-key-env", environmentVariable]);
        }

        if ((sourceMask & 4) != 0)
        {
            result.Add("--cpu-key-stdin");
        }

        return result.ToArray();
    }

    private static async Task<CliResult> RunWithCpuKeySourceAsync(
        IReadOnlyList<string> arguments,
        string source,
        string sourceValue,
        TemporaryDirectory temporaryDirectory)
    {
        string[] commandArguments;
        switch (source)
        {
            case "file":
                string keyFilePath = temporaryDirectory.File("cpu-key.txt");
                await File.WriteAllTextAsync(keyFilePath, sourceValue);
                commandArguments = [.. arguments, "--cpu-key-file", keyFilePath];
                break;
            case "environment":
                string variableName = $"JRUNNER_CLI_TEST_CPU_KEY_{Guid.NewGuid():N}";
                return await RunAppHostAsync(
                    [.. arguments, "--cpu-key-env", variableName], variableName, sourceValue);
            case "stdin":
                commandArguments = [.. arguments, "--cpu-key-stdin"];
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(source));
        }

        using var standardInput = new StringReader(source == "stdin" ? sourceValue : string.Empty);
        return await RunInProcessAsync(commandArguments, standardInput);
    }

    private static async Task<CliResult> RunInProcessAsync(
        IReadOnlyList<string> arguments,
        TextReader standardInput)
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        int exitCode = await CliApplication.RunAsync(arguments, standardInput, standardOutput, standardError);
        return new CliResult(exitCode, standardOutput.ToString(), standardError.ToString());
    }

    private static async Task<CliResult> RunAppHostAsync(
        IReadOnlyList<string> arguments,
        string? variableName,
        string? value,
        string standardInput = "")
    {
        // Keep synthetic key values in the child environment, never the parallel testhost's.
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

        if (variableName is not null)
        {
            if (value is null)
            {
                startInfo.Environment.Remove(variableName);
            }
            else
            {
                startInfo.Environment[variableName] = value;
            }
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the jrunner apphost.");
        Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> standardErrorTask = process.StandardError.ReadToEndAsync();
        Task processExitTask = process.WaitForExitAsync();
        await process.StandardInput.WriteAsync(standardInput);
        process.StandardInput.Close();
        await Task.WhenAll(standardOutputTask, standardErrorTask, processExitTask);

        return new CliResult(process.ExitCode, await standardOutputTask, await standardErrorTask);
    }

    private sealed record CliResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class UnreadableInputReader : TextReader
    {
        internal int ReadCount { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            throw new IOException(CpuKeyText);
        }
    }

    private static string ReadErrorKind(StringWriter standardOutput)
    {
        using JsonDocument document = ReadSingleEnvelope(standardOutput.ToString());
        return document.RootElement.GetProperty("error").GetProperty("kind").GetString()
            ?? throw new InvalidOperationException("The CLI error did not include an error kind.");
    }

    private static byte[] CreateInspectableNandInput()
    {
        const int imageLength = 0xD0000;
        const int firstStageOffset = 0x8000;
        const int smcOffset = 0x1000;
        const int smcLength = 0x2DC0;
        const int patchOffset = 0xC0010;
        var image = new byte[imageLength];
        image[0] = 0xFF;
        image[1] = 0x4F;
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(0x02, sizeof(ushort)), 1888);
        WriteUInt32BigEndian(image, 0x08, firstStageOffset);
        WriteUInt32BigEndian(image, 0x78, smcLength);
        WriteUInt32BigEndian(image, 0x7C, smcOffset);

        var decryptedSmc = new byte[smcLength];
        decryptedSmc[0x100] = 0x60;
        decryptedSmc[0x101] = 1;
        decryptedSmc[0x102] = 2;
        byte[] encryptedSmc = SmcCrypto.Encrypt(decryptedSmc);
        encryptedSmc.CopyTo(image, smcOffset);

        byte[] encryptedCbA = CreateEncryptedCbA();
        encryptedCbA.CopyTo(image, firstStageOffset);
        WriteUInt32BigEndian(image, patchOffset, 0x0000C000);
        WriteUInt32BigEndian(image, patchOffset + sizeof(uint), 1);
        WriteUInt32BigEndian(image, patchOffset + (2 * sizeof(uint)), 0x38800000);
        WriteUInt32BigEndian(image, patchOffset + (3 * sizeof(uint)), uint.MaxValue);

        CryptographicOperations.ZeroMemory(decryptedSmc);
        CryptographicOperations.ZeroMemory(encryptedSmc);
        CryptographicOperations.ZeroMemory(encryptedCbA);
        return image;
    }

    private static byte[] CreateEncryptedCbA()
    {
        var stage = new byte[0x3C0];
        stage[0] = (byte)'C';
        stage[1] = (byte)'B';
        BinaryPrimitives.WriteUInt16BigEndian(stage.AsSpan(2, sizeof(ushort)), 9188);
        BinaryPrimitives.WriteUInt32BigEndian(stage.AsSpan(0x0C, sizeof(uint)), checked((uint)stage.Length));
        for (int index = 0; index < 0x10; index++)
        {
            stage[0x10 + index] = checked((byte)(0x20 + index));
        }

        stage[0x20] = 0x11;
        stage[0x21] = 0x22;
        stage[0x22] = 0x33;
        stage[0x3B1] = 9;
        byte[] firstBootLoaderKey =
        [
            0xDD, 0x88, 0xAD, 0x0C, 0x9E, 0xD6, 0x69, 0xE7,
            0xB5, 0x67, 0x94, 0xFB, 0x68, 0x56, 0x3E, 0xFA,
        ];
        byte[] rc4Key = XeCrypt.HmacSha1Truncated(firstBootLoaderKey, stage.AsSpan(0x10, 0x10));
        try
        {
            Rc4.TransformInPlace(rc4Key, stage.AsSpan(0x20));
            return stage;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(firstBootLoaderKey);
            CryptographicOperations.ZeroMemory(rc4Key);
        }
    }

    private static byte[] CreateFuseBlowPatch()
    {
        var patch = new byte[4 * sizeof(uint)];
        WriteUInt32BigEndian(patch, 0, 0x0000C000);
        WriteUInt32BigEndian(patch, sizeof(uint), 1);
        WriteUInt32BigEndian(patch, 2 * sizeof(uint), 0x38800000);
        WriteUInt32BigEndian(patch, 3 * sizeof(uint), uint.MaxValue);
        return patch;
    }

    private static void WriteUInt32BigEndian(byte[] bytes, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset, sizeof(uint)), value);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"jrunner-cli-tests-{Guid.NewGuid():N}");
            if (OperatingSystem.IsLinux())
            {
                const UnixFileMode privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
                Directory.CreateDirectory(Path, privateMode);
                System.IO.File.SetUnixFileMode(Path, privateMode);
            }
            else
            {
                Directory.CreateDirectory(Path);
            }
        }

        public string Path { get; }

        public string File(string name)
        {
            return System.IO.Path.Combine(Path, name);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
