using System.Buffers.Binary;
using System.Text.Json;
using JRunner.Cli;
using JRunner.Cli.Infrastructure;
using JRunner.Core.Contracts;
using JRunner.Core.Devices.PicoFlasher;
using Xunit;

namespace JRunner.Cli.Tests;

public sealed class CliPicoDeviceCommandTests
{
    private static readonly string[] PicoCommands =
    [
        "probe", "smc-stop", "smc-start", "reboot-bootloader",
        "nand-read", "nand-write", "nand-erase", "emmc-probe", "emmc-read",
    ];

    private static readonly string?[] PicoInformationFlags =
    [
        null, "--help", "-h", "/h", "-?", "/?", "--version",
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Device_list_reports_candidate_fields_without_opening_or_firmware_access(bool json)
    {
        PicoFlasherDeviceEndpoint[] endpoints =
        [
            new("/dev/ttyACM2", "serial-a", 0, "/sys/devices/usb/pico-a"),
            new("/dev/ttyACM0", null, 0, "/sys/devices/usb/pico-b"),
        ];
        var enumerator = new FixedEnumerator(endpoints);
        var transportFactory = new RecordingTransportFactory();
        var factory = new PicoFlasherConnectionFactory(enumerator, transportFactory);
        Invocation result = await RunAsync(json ? ["device", "list", "--json"] : ["device", "list"], factory);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        Assert.Equal(1, enumerator.EnumerateCallCount);
        Assert.Equal(0, transportFactory.OpenCallCount);
        if (json)
        {
            using JsonDocument document = JsonDocument.Parse(result.Output);
            JsonElement envelope = document.RootElement;
            Assert.Equal(1, envelope.GetProperty("schemaVersion").GetInt32());
            Assert.True(envelope.GetProperty("ok").GetBoolean());
            JsonElement candidates = envelope.GetProperty("result");
            Assert.Equal(JsonValueKind.Array, candidates.ValueKind);
            Assert.Equal(endpoints.Length, candidates.GetArrayLength());
            for (int index = 0; index < endpoints.Length; index++)
            {
                JsonElement candidate = candidates[index];
                Assert.Equal(
                    new[] { "devicePath", "interfaceNumber", "physicalDevicePath", "serialNumber" },
                    candidate.EnumerateObject().Select(static property => property.Name)
                        .OrderBy(static name => name, StringComparer.Ordinal));
                Assert.Equal(endpoints[index].DevicePath, candidate.GetProperty("devicePath").GetString());
                Assert.Equal(endpoints[index].SerialNumber, candidate.GetProperty("serialNumber").GetString());
                Assert.Equal(endpoints[index].PhysicalDevicePath, candidate.GetProperty("physicalDevicePath").GetString());
                Assert.Equal(0, candidate.GetProperty("interfaceNumber").GetInt32());
            }
        }
        else
        {
            string[] lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(endpoints.Length, lines.Length);
            for (int index = 0; index < endpoints.Length; index++)
            {
                Assert.Contains($"Device path: {endpoints[index].DevicePath}", lines[index], StringComparison.Ordinal);
                Assert.Contains($"serial: {endpoints[index].SerialNumber ?? "(none)"}", lines[index], StringComparison.Ordinal);
                Assert.Contains($"physical path: {endpoints[index].PhysicalDevicePath}", lines[index], StringComparison.Ordinal);
                Assert.Contains("interface: 0", lines[index], StringComparison.Ordinal);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Device_list_with_no_candidates_succeeds_without_opening(bool json)
    {
        var enumerator = new FixedEnumerator([]);
        var transportFactory = new RecordingTransportFactory();
        Invocation result = await RunAsync(
            json ? ["device", "list", "--json"] : ["device", "list"],
            new PicoFlasherConnectionFactory(enumerator, transportFactory));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        Assert.Equal(1, enumerator.EnumerateCallCount);
        Assert.Equal(0, transportFactory.OpenCallCount);
        if (json)
        {
            using JsonDocument document = JsonDocument.Parse(result.Output);
            Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(0, document.RootElement.GetProperty("result").GetArrayLength());
        }
        else
        {
            Assert.Contains("No PicoFlasher command interfaces found.", result.Output, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("--device", "/dev/ttyACM0")]
    [InlineData("--serial", "serial-a")]
    [InlineData("--timeout", "1")]
    public async Task Device_list_does_not_accept_connection_options(string option, string value)
    {
        await AssertRejectedBeforeEnumerationAsync(["device", "list", option, value, "--json"]);
    }

    [Theory]
    [InlineData("--device", "", "pico-device-invalid")]
    [InlineData("--device", " ", "pico-device-invalid")]
    [InlineData("--serial", "", "pico-serial-invalid")]
    [InlineData("--serial", " ", "pico-serial-invalid")]
    [InlineData("--timeout", "0", "pico-timeout-invalid")]
    [InlineData("--timeout", "-1", "pico-timeout-invalid")]
    [InlineData("--timeout", "2147484", "pico-timeout-invalid")]
    [InlineData("--timeout", "0.000000001", "pico-timeout-invalid")]
    [InlineData("--timeout", "NaN", null)]
    [InlineData("--timeout", "Infinity", null)]
    [InlineData("--timeout", "1e309", null)]
    [InlineData("--timeout", "not-a-number", null)]
    [InlineData("--timeout", "", null)]
    public async Task Every_pico_command_validates_connection_options_before_enumeration(
        string option,
        string value,
        string? expectedKind)
    {
        foreach (string operation in PicoCommands)
        {
            List<string> arguments = ValidArguments(operation);
            arguments.AddRange([option, value, "--json"]);
            await AssertRejectedBeforeEnumerationAsync(arguments, expectedKind);
            arguments.Add("--help");
            await AssertRejectedBeforeEnumerationAsync(arguments, expectedKind);
        }
    }

    [Fact]
    public async Task Every_pico_command_rejects_combined_device_and_serial_selectors_before_enumeration()
    {
        foreach (string operation in PicoCommands)
        {
            List<string> arguments = ValidArguments(operation);
            arguments.AddRange(["--device", "/dev/ttyACM0", "--serial", "serial-a", "--json"]);
            await AssertRejectedBeforeEnumerationAsync(arguments, "pico-device-selector-conflict");
            arguments.Add("--help");
            await AssertRejectedBeforeEnumerationAsync(arguments, "pico-device-selector-conflict");
        }
    }

    [Theory]
    [InlineData("--device")]
    [InlineData("--serial")]
    [InlineData("--timeout")]
    public async Task Connection_options_require_an_explicit_value_before_enumeration(string option)
    {
        foreach (string operation in PicoCommands)
        {
            List<string> arguments = ValidArguments(operation);
            arguments.AddRange([option, "--json"]);
            await AssertRejectedBeforeEnumerationAsync(arguments);
            arguments.Add("--help");
            await AssertRejectedBeforeEnumerationAsync(arguments);
        }
    }

    [Theory]
    [InlineData("--start-record")]
    [InlineData("--record-count")]
    [InlineData("--start-sector")]
    [InlineData("--sector-count")]
    public async Task Obsolete_range_options_are_unknown_on_every_flat_pico_command(string option)
    {
        foreach (string operation in PicoCommands)
        {
            List<string> arguments = ValidArguments(operation);
            arguments.AddRange([option, "1", "--json"]);
            await AssertRejectedBeforeEnumerationAsync(arguments);
            arguments.Add("--help");
            await AssertRejectedBeforeEnumerationAsync(arguments);
        }
    }

    [Theory]
    [InlineData("smc-stop", PicoFlasherCommand.StopSmc, false)]
    [InlineData("smc-stop", PicoFlasherCommand.StopSmc, true)]
    [InlineData("smc-start", PicoFlasherCommand.StartSmc, false)]
    [InlineData("smc-start", PicoFlasherCommand.StartSmc, true)]
    [InlineData("reboot-bootloader", PicoFlasherCommand.RebootToBootloader, false)]
    [InlineData("reboot-bootloader", PicoFlasherCommand.RebootToBootloader, true)]
    public async Task Controls_send_only_their_service_command_after_the_firmware_gate(
        string operation,
        PicoFlasherCommand command,
        bool json)
    {
        PicoFlasherDeviceEndpoint endpoint = Endpoint("a", 0);
        var enumerator = new FixedEnumerator([endpoint]);
        var transport = new ScriptedTransport(Status(4));
        var transportFactory = new RecordingTransportFactory(transport);
        var factory = new PicoFlasherConnectionFactory(enumerator, transportFactory);
        Invocation result = await RunAsync(json ? ["pico", operation, "--json"] : ["pico", operation], factory);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        Assert.Equal(1, enumerator.EnumerateCallCount);
        Assert.Equal(1, transportFactory.OpenCallCount);
        Assert.Same(endpoint, transportFactory.OpenedEndpoint);
        Assert.Equal(PicoFlasherProtocol.DefaultNoProgressTimeout, transportFactory.OpenedNoProgressTimeout);
        Assert.True(transport.Disposed);
        AssertFrames(transport, Command(PicoFlasherCommand.GetVersion, 0), Command(command, 0));
        if (json)
        {
            using JsonDocument document = JsonDocument.Parse(result.Output);
            JsonElement envelope = document.RootElement;
            Assert.Equal(1, envelope.GetProperty("schemaVersion").GetInt32());
            Assert.True(envelope.GetProperty("ok").GetBoolean());
            JsonElement control = envelope.GetProperty("result");
            Assert.Equal(operation, control.GetProperty("operation").GetString());
            Assert.Equal(4U, control.GetProperty("firmwareVersion").GetUInt32());
            Assert.Equal(endpoint.DevicePath, control.GetProperty("devicePath").GetString());
            Assert.Equal(endpoint.SerialNumber, control.GetProperty("serialNumber").GetString());
        }
        else
        {
            Assert.Contains($"PicoFlasher {operation} completed", result.Output, StringComparison.Ordinal);
            Assert.Contains("firmware v4", result.Output, StringComparison.Ordinal);
            Assert.Contains(endpoint.DevicePath, result.Output, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("--device", "/dev/ttyACM1")]
    [InlineData("--serial", "serial-b")]
    public async Task Typed_selectors_and_explicit_fractional_timeout_reach_the_factory(string option, string value)
    {
        PicoFlasherDeviceEndpoint first = Endpoint("a", 0);
        PicoFlasherDeviceEndpoint selected = Endpoint("b", 1);
        var enumerator = new FixedEnumerator([first, selected]);
        var transport = new ScriptedTransport(Status(4));
        var transportFactory = new RecordingTransportFactory(transport);
        Invocation result = await RunAsync(
            ["pico", "smc-start", option, value, "--timeout", "1.25", "--json"],
            new PicoFlasherConnectionFactory(enumerator, transportFactory));

        Assert.Equal(0, result.ExitCode);
        Assert.Same(selected, transportFactory.OpenedEndpoint);
        Assert.Equal(TimeSpan.FromSeconds(1.25), transportFactory.OpenedNoProgressTimeout);
        AssertFrames(transport, Command(PicoFlasherCommand.GetVersion, 0), Command(PicoFlasherCommand.StartSmc, 0));
        Assert.True(transport.Disposed);
    }

    [Theory]
    [InlineData(1U)]
    [InlineData(2U)]
    [InlineData(3U)]
    public async Task Legacy_firmware_prevents_all_explicit_controls(uint firmwareVersion)
    {
        foreach (string operation in new[] { "smc-stop", "smc-start", "reboot-bootloader" })
        {
            var enumerator = new FixedEnumerator([Endpoint("a", 0)]);
            var transport = new ScriptedTransport(Status(firmwareVersion));
            var transportFactory = new RecordingTransportFactory(transport);
            Invocation result = await RunAsync(
                ["pico", operation, "--json"],
                new PicoFlasherConnectionFactory(enumerator, transportFactory));

            Assert.Equal((int)ExitCode.MissingPrerequisite, result.ExitCode);
            AssertFailure(result, ExitCode.MissingPrerequisite, "pico-firmware-unsupported");
            Assert.Equal(1, enumerator.EnumerateCallCount);
            Assert.Equal(1, transportFactory.OpenCallCount);
            AssertFrames(transport, Command(PicoFlasherCommand.GetVersion, 0));
            Assert.True(transport.Disposed);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Probe_runs_its_smc_preflight_and_reports_raw_and_decoded_configuration(bool json)
    {
        PicoFlasherDeviceEndpoint endpoint = Endpoint("a", 0);
        var enumerator = new FixedEnumerator([endpoint]);
        var transport = new ScriptedTransport(Combine(Status(4), Status(0x0119_8010U)));
        var transportFactory = new RecordingTransportFactory(transport);
        Invocation result = await RunAsync(
            json ? ["pico", "probe", "--json"] : ["pico", "probe"],
            new PicoFlasherConnectionFactory(enumerator, transportFactory));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        Assert.Equal(PicoFlasherProtocol.DefaultNoProgressTimeout, transportFactory.OpenedNoProgressTimeout);
        AssertFrames(
            transport,
            Command(PicoFlasherCommand.GetVersion, 0),
            Command(PicoFlasherCommand.SetSmcWorkaround, 0),
            Command(PicoFlasherCommand.StopSmc, 0),
            Command(PicoFlasherCommand.GetFlashConfiguration, 0),
            Command(PicoFlasherCommand.StartSmc, 0));
        Assert.True(transport.Disposed);
        if (json)
        {
            using JsonDocument document = JsonDocument.Parse(result.Output);
            Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
            JsonElement probe = document.RootElement.GetProperty("result");
            Assert.Equal(4U, probe.GetProperty("firmwareVersion").GetUInt32());
            Assert.Equal(endpoint.DevicePath, probe.GetProperty("devicePath").GetString());
            Assert.Equal(endpoint.SerialNumber, probe.GetProperty("serialNumber").GetString());
            Assert.Equal(0x0119_8010U, probe.GetProperty("flashConfiguration").GetUInt32());
            Assert.False(string.IsNullOrWhiteSpace(probe.GetProperty("storageKind").GetString()));
            Assert.Equal(JsonValueKind.Object, probe.GetProperty("nandGeometry").ValueKind);
        }
        else
        {
            Assert.Contains("PicoFlasher probe completed", result.Output, StringComparison.Ordinal);
            Assert.Contains("firmware v4", result.Output, StringComparison.Ordinal);
            Assert.Contains("flash configuration 0x01198010", result.Output, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("probe")]
    [InlineData("smc-stop")]
    [InlineData("smc-start")]
    [InlineData("reboot-bootloader")]
    [InlineData("nand-read")]
    [InlineData("nand-write")]
    [InlineData("nand-erase")]
    [InlineData("emmc-probe")]
    [InlineData("emmc-read")]
    public async Task Every_pico_command_help_documents_selectors_timeout_and_json_without_device_access(string operation)
    {
        var enumerator = new FixedEnumerator([Endpoint("a", 0)]);
        var transportFactory = new RecordingTransportFactory();
        Invocation result = await RunAsync(
            ["pico", operation, "--help"],
            new PicoFlasherConnectionFactory(enumerator, transportFactory));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        Assert.Contains($"Usage: jrunner pico {operation} ", result.Output, StringComparison.Ordinal);
        Assert.Contains("[--device <path>|--serial <value>]", result.Output, StringComparison.Ordinal);
        Assert.Contains("[--timeout <seconds>]", result.Output, StringComparison.Ordinal);
        Assert.Contains("[--json]", result.Output, StringComparison.Ordinal);
        Assert.Contains("default: 10", result.Output, StringComparison.Ordinal);
        foreach (string obsoleteOption in new[] { "--start-record", "--record-count", "--start-sector", "--sector-count" })
        {
            Assert.DoesNotContain(obsoleteOption, result.Output, StringComparison.Ordinal);
        }

        Assert.Equal(0, enumerator.EnumerateCallCount);
        Assert.Equal(0, transportFactory.OpenCallCount);
    }

    [Theory]
    [InlineData("nand", "read")]
    [InlineData("nand", "write")]
    [InlineData("nand", "erase")]
    [InlineData("emmc", "probe")]
    [InlineData("emmc", "read")]
    [InlineData("emmc", "write")]
    [InlineData("emmc", "erase")]
    [InlineData(null, "emmc-write")]
    [InlineData(null, "emmc-erase")]
    public async Task Removed_commands_remain_usage_errors_with_every_help_alias(string? group, string operation)
    {
        foreach (string helpFlag in new[] { "--help", "-h", "/h", "-?", "/?" })
        {
            foreach (bool json in new[] { false, true })
            {
                List<string> arguments = ["pico"];
                if (group is not null)
                {
                    arguments.Add(group);
                }

                arguments.AddRange([operation, helpFlag]);
                if (json)
                {
                    arguments.Add("--json");
                }

                var enumerator = new FixedEnumerator([Endpoint("a", 0)]);
                var transportFactory = new RecordingTransportFactory();
                Invocation result = await RunAsync(arguments, new PicoFlasherConnectionFactory(enumerator, transportFactory));
                Assert.Equal((int)ExitCode.Usage, result.ExitCode);
                Assert.NotEmpty(result.Error);
                if (json)
                {
                    AssertFailure(result, ExitCode.Usage, "unsupported-command");
                }
                else
                {
                    Assert.Equal(string.Empty, result.Output);
                }

                Assert.Equal(0, enumerator.EnumerateCallCount);
                Assert.Equal(0, transportFactory.OpenCallCount);
            }
        }
    }

    [Theory]
    [InlineData("unrecognized-family", false)]
    [InlineData("unrecognized-family", true)]
    [InlineData("unsupported/path", false)]
    [InlineData("unsupported/path", true)]
    public async Task Unknown_path_prefixes_before_flat_pico_commands_remain_unsupported(
        string prefix,
        bool includeRequiredOptions)
    {
        foreach (string operation in PicoCommands)
        {
            foreach (string? informationFlag in PicoInformationFlags)
            {
                List<string> arguments = includeRequiredOptions ? ValidArguments(operation) : ["pico", operation];
                arguments.Insert(1, prefix);
                arguments.Add("--json");
                if (informationFlag is not null)
                {
                    arguments.Add(informationFlag);
                }

                await AssertRejectedBeforeEnumerationAsync(arguments, "unsupported-command");
            }
        }
    }

    [Theory]
    [InlineData("--output", "not-opened.bin")]
    [InlineData("--blocks", "1")]
    public async Task Unknown_path_prefix_takes_precedence_over_missing_emmc_output_or_count(
        string suppliedOption,
        string suppliedValue)
    {
        foreach (string? informationFlag in PicoInformationFlags)
        {
            List<string> arguments = ["pico", "unrecognized-family", "emmc-read", suppliedOption, suppliedValue, "--json"];
            if (informationFlag is not null)
            {
                arguments.Add(informationFlag);
            }

            await AssertRejectedBeforeEnumerationAsync(arguments, "unsupported-command");
        }
    }

    [Theory]
    [InlineData("--unknown")]
    [InlineData("-x")]
    public async Task Unknown_options_before_flat_pico_commands_remain_invalid_arguments(string option)
    {
        foreach (string operation in PicoCommands)
        {
            foreach (string? informationFlag in PicoInformationFlags)
            {
                List<string> arguments = ValidArguments(operation);
                arguments.InsertRange(1, [option, "unrecognized-family"]);
                arguments.Add("--json");
                if (informationFlag is not null)
                {
                    arguments.Add(informationFlag);
                }

                await AssertRejectedBeforeEnumerationAsync(arguments, "invalid-command-arguments");
            }
        }
    }

    [Theory]
    [InlineData("unrecognized-family", null)]
    [InlineData("--unknown", null)]
    [InlineData("--unknown", "value")]
    [InlineData("--timeout", null)]
    [InlineData("--timeout", "not-a-number")]
    public async Task Valid_flat_pico_commands_keep_malformed_arguments_invalid(string argument, string? value)
    {
        foreach (string operation in PicoCommands)
        {
            foreach (string? informationFlag in PicoInformationFlags)
            {
                List<string> arguments = ValidArguments(operation);
                arguments.Add(argument);
                if (value is not null)
                {
                    arguments.Add(value);
                }

                arguments.Add("--json");
                if (informationFlag is not null)
                {
                    arguments.Add(informationFlag);
                }

                await AssertRejectedBeforeEnumerationAsync(arguments, "invalid-command-arguments");
            }
        }
    }

    [Theory]
    [InlineData("unrecognized-family/path", false)]
    [InlineData("unrecognized-family/path", true)]
    [InlineData("unrecognized-family", false)]
    [InlineData("unrecognized-family", true)]
    public async Task Option_values_are_distinguished_from_identical_unmatched_pico_words(
        string value,
        bool beforeLeaf)
    {
        foreach (string? informationFlag in PicoInformationFlags)
        {
            List<string> arguments = ["pico", "--support-root", value];
            arguments.AddRange(beforeLeaf ? [value, "probe"] : ["probe", value]);
            arguments.Add("--json");
            if (informationFlag is not null)
            {
                arguments.Add(informationFlag);
            }

            await AssertRejectedBeforeEnumerationAsync(
                arguments,
                beforeLeaf ? "unsupported-command" : "invalid-command-arguments");
        }
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("/h")]
    [InlineData("-?")]
    [InlineData("/?")]
    public async Task Help_aliases_allow_missing_required_operands_without_opening(string helpFlag)
    {
        foreach (string operation in PicoCommands)
        {
            var enumerator = new FixedEnumerator([]);
            var transportFactory = new RecordingTransportFactory();
            Invocation result = await RunAsync(
                ["pico", operation, helpFlag, "--json"],
                new PicoFlasherConnectionFactory(enumerator, transportFactory));
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(string.Empty, result.Error);
            using JsonDocument document = JsonDocument.Parse(result.Output);
            Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
            Assert.Contains(
                $"Usage: jrunner pico {operation} ",
                document.RootElement.GetProperty("result").GetString()!,
                StringComparison.Ordinal);
            Assert.Equal(0, enumerator.EnumerateCallCount);
            Assert.Equal(0, transportFactory.OpenCallCount);
        }
    }

    [Theory]
    [InlineData("device", null)]
    [InlineData("pico", null)]
    [InlineData("pico", "nand")]
    [InlineData("pico", "emmc")]
    public async Task Groups_without_a_public_subcommand_report_usage_without_device_access(string group, string? subgroup)
    {
        List<string> arguments = [group];
        if (subgroup is not null)
        {
            arguments.Add(subgroup);
        }

        arguments.Add("--json");
        await AssertRejectedBeforeEnumerationAsync(arguments, "unsupported-command");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Root_and_pico_help_list_only_the_flat_public_commands(bool picoGroup)
    {
        var enumerator = new FixedEnumerator([]);
        var transportFactory = new RecordingTransportFactory();
        Invocation result = await RunAsync(
            picoGroup ? ["pico", "--help"] : ["--help"],
            new PicoFlasherConnectionFactory(enumerator, transportFactory));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        foreach (string operation in PicoCommands)
        {
            Assert.Contains($"pico {operation}", result.Output, StringComparison.Ordinal);
        }

        if (!picoGroup)
        {
            Assert.Contains("device list", result.Output, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("pico nand ", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("pico emmc ", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("emmc-write", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("emmc-erase", result.Output, StringComparison.Ordinal);
        Assert.Equal(0, enumerator.EnumerateCallCount);
        Assert.Equal(0, transportFactory.OpenCallCount);
    }

    private static List<string> ValidArguments(string operation)
    {
        List<string> arguments = ["pico", operation];
        switch (operation)
        {
            case "nand-read":
            case "emmc-read":
                arguments.AddRange(["--blocks", "1", "--output", "not-opened.bin"]);
                break;
            case "nand-write":
                arguments.AddRange(["--start-block", "0", "--input", "not-opened.bin", "--yes"]);
                break;
            case "nand-erase":
                arguments.AddRange(["--start-erase-block", "0", "--erase-blocks", "1", "--yes"]);
                break;
        }

        return arguments;
    }

    private static async Task AssertRejectedBeforeEnumerationAsync(IReadOnlyList<string> arguments, string? expectedKind = null)
    {
        var enumerator = new FixedEnumerator([Endpoint("a", 0)]);
        var transportFactory = new RecordingTransportFactory();
        Invocation result = await RunAsync(arguments, new PicoFlasherConnectionFactory(enumerator, transportFactory));

        Assert.Equal((int)ExitCode.Usage, result.ExitCode);
        AssertFailure(result, ExitCode.Usage, expectedKind);
        Assert.NotEmpty(result.Error);
        Assert.Equal(0, enumerator.EnumerateCallCount);
        Assert.Equal(0, transportFactory.OpenCallCount);
    }

    private static void AssertFailure(Invocation result, ExitCode expectedCode, string? expectedKind)
    {
        using JsonDocument document = JsonDocument.Parse(result.Output);
        JsonElement envelope = document.RootElement;
        Assert.Equal(1, envelope.GetProperty("schemaVersion").GetInt32());
        Assert.False(envelope.GetProperty("ok").GetBoolean());
        JsonElement error = envelope.GetProperty("error");
        Assert.Equal((int)expectedCode, error.GetProperty("code").GetInt32());
        if (expectedKind is not null)
        {
            Assert.Equal(expectedKind, error.GetProperty("kind").GetString());
        }
    }

    private static async Task<Invocation> RunAsync(IReadOnlyList<string> arguments, PicoFlasherConnectionFactory factory)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int exitCode = await CliCommandRouter.RunAsync(arguments, output, error, CancellationToken.None, factory);
        return new Invocation(exitCode, output.ToString(), error.ToString());
    }

    private static PicoFlasherDeviceEndpoint Endpoint(string suffix, int deviceNumber)
    {
        return new PicoFlasherDeviceEndpoint(
            $"/dev/ttyACM{deviceNumber}", $"serial-{suffix}", 0, $"/sys/devices/usb/pico-{suffix}");
    }

    private static byte[] Status(uint value)
    {
        byte[] bytes = new byte[PicoFlasherProtocol.StatusSize];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Command(PicoFlasherCommand command, uint lba)
    {
        byte[] bytes = new byte[PicoFlasherProtocol.CommandSize];
        PicoFlasherProtocol.WriteCommand(bytes, command, lba);
        return bytes;
    }

    private static byte[] Combine(params byte[][] chunks)
    {
        byte[] bytes = new byte[chunks.Sum(static chunk => chunk.Length)];
        int offset = 0;
        foreach (byte[] chunk in chunks)
        {
            chunk.CopyTo(bytes, offset);
            offset += chunk.Length;
        }

        return bytes;
    }

    private static void AssertFrames(ScriptedTransport transport, params byte[][] expectedFrames)
    {
        Assert.Equal(expectedFrames.Length, transport.WrittenFrames.Count);
        for (int index = 0; index < expectedFrames.Length; index++)
        {
            Assert.Equal<byte>(expectedFrames[index], transport.WrittenFrames[index]);
        }
    }

    private sealed record Invocation(int ExitCode, string Output, string Error);

    private sealed class FixedEnumerator : IPicoFlasherDeviceEnumerator
    {
        private readonly IReadOnlyList<PicoFlasherDeviceEndpoint> _endpoints;

        internal FixedEnumerator(IReadOnlyList<PicoFlasherDeviceEndpoint> endpoints)
        {
            _endpoints = endpoints;
        }

        internal int EnumerateCallCount { get; private set; }

        public ValueTask<IReadOnlyList<PicoFlasherDeviceEndpoint>> EnumerateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnumerateCallCount++;
            return ValueTask.FromResult(_endpoints);
        }
    }

    private sealed class RecordingTransportFactory : IPicoFlasherTransportFactory
    {
        private readonly ScriptedTransport? _transport;

        internal RecordingTransportFactory(ScriptedTransport? transport = null)
        {
            _transport = transport;
        }

        internal int OpenCallCount { get; private set; }

        internal PicoFlasherDeviceEndpoint? OpenedEndpoint { get; private set; }

        internal TimeSpan? OpenedNoProgressTimeout { get; private set; }

        public ValueTask<IPicoFlasherTransport> OpenAsync(
            PicoFlasherDeviceEndpoint endpoint,
            TimeSpan noProgressTimeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenCallCount++;
            OpenedEndpoint = endpoint;
            OpenedNoProgressTimeout = noProgressTimeout;
            return ValueTask.FromResult<IPicoFlasherTransport>(
                _transport ?? throw new InvalidOperationException("This invocation must not open a transport."));
        }
    }

    private sealed class ScriptedTransport : IPicoFlasherTransport
    {
        private readonly Queue<byte> _responses;

        internal ScriptedTransport(byte[] responses)
        {
            _responses = new Queue<byte>(responses);
        }

        internal List<byte[]> WrittenFrames { get; } = [];

        internal bool Disposed { get; private set; }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WrittenFrames.Add(source.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask ReadExactlyAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_responses.Count < destination.Length)
            {
                throw new InvalidOperationException("The scripted transport has insufficient response bytes.");
            }

            for (int index = 0; index < destination.Length; index++)
            {
                destination.Span[index] = _responses.Dequeue();
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
