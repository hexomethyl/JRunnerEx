using System.CommandLine;
using System.Text;

namespace JRunner.Cli;

internal static partial class CliCommandRouter
{
    private const string CpuKeySourceHelp = "--cpu-key-file <file>|--cpu-key-env <name>|--cpu-key-stdin";
    private const string HelpAliasesLabel = "-h, --help, /h, -?, /?";

    private static string GetHelpText(CommandDefinitions commands, Command selectedCommand)
    {
        var text = new StringBuilder();
        text.Append(GetUsageText(commands, selectedCommand))
            .Append("\n\n")
            .Append(selectedCommand.Description)
            .Append('\n');

        AppendHelpCommands(text, commands, selectedCommand);
        AppendHelpArguments(text, selectedCommand);
        AppendHelpOptions(text, commands, selectedCommand);
        AppendGlobalHelpOptions(text, commands);
        AppendHelpNotes(text, commands, selectedCommand);
        text.Append("\nExamples:\n  ")
            .Append(GetExampleCommand(commands, selectedCommand))
            .Append('\n');
        return text.ToString();
    }

    private static string GetUsageText(CommandDefinitions commands, Command selectedCommand)
    {
        string path = GetCommandPath(commands, selectedCommand);
        if (ReferenceEquals(selectedCommand, commands.Root))
        {
            return "Usage: jrunner [--support-root <path>] <command> [options]";
        }

        if (selectedCommand.Subcommands.Count != 0)
        {
            return $"Usage: {path} <command> [options]";
        }

        if (ReferenceEquals(selectedCommand, commands.NandInspect))
        {
            return $"Usage: {path} --input <file> [{CpuKeySourceHelp}] [--json]";
        }

        if (ReferenceEquals(selectedCommand, commands.NandCompare))
        {
            return $"Usage: {path} <left> <right> [--json]";
        }

        if (ReferenceEquals(selectedCommand, commands.NandRgh3Convert))
        {
            return $"Usage: {path} --ecc <file> --flash <file> ({CpuKeySourceHelp}) --output <file> [--no-smc-patch] [--force] [--json]";
        }

        if (ReferenceEquals(selectedCommand, commands.PatchInspect))
        {
            return $"Usage: {path} --input <file> [--json]";
        }

        if (ReferenceEquals(selectedCommand, commands.ConsoleList) ||
            ReferenceEquals(selectedCommand, commands.DeviceList))
        {
            return $"Usage: {path} [--json]";
        }

        if (ReferenceEquals(selectedCommand, commands.PicoProbe) ||
            ReferenceEquals(selectedCommand, commands.PicoSmcStop) ||
            ReferenceEquals(selectedCommand, commands.PicoSmcStart) ||
            ReferenceEquals(selectedCommand, commands.PicoRebootBootloader) ||
            ReferenceEquals(selectedCommand, commands.PicoEmmcProbe))
        {
            return $"Usage: {path} [--device <path>|--serial <value>] [--timeout <seconds>] [--json]";
        }

        if (ReferenceEquals(selectedCommand, commands.PicoNandRead))
        {
            return $"Usage: {path} [--device <path>|--serial <value>] [--start-block <record>] [--blocks <count>] --output <file> [--timeout <seconds>] [--force] [--json]";
        }

        if (ReferenceEquals(selectedCommand, commands.PicoNandWrite))
        {
            return $"Usage: {path} [--device <path>|--serial <value>] --start-block <record> --input <file> --yes [--timeout <seconds>] [--json]";
        }

        if (ReferenceEquals(selectedCommand, commands.PicoNandErase))
        {
            return $"Usage: {path} [--device <path>|--serial <value>] --start-erase-block <index> --erase-blocks <count> --yes [--timeout <seconds>] [--json]";
        }

        if (ReferenceEquals(selectedCommand, commands.PicoEmmcRead))
        {
            return $"Usage: {path} [--device <path>|--serial <value>] [--start-block <record>] --blocks <count> --output <file> [--timeout <seconds>] [--force] [--json]";
        }

        if (ReferenceEquals(selectedCommand, commands.SupportStatus))
        {
            return $"Usage: {path} [--support-root <path>] [--json]";
        }

        if (ReferenceEquals(selectedCommand, commands.SupportInstall))
        {
            return $"Usage: {path} [--archive <local-file>] [--support-root <path>] [--json]";
        }

        if (ReferenceEquals(selectedCommand, commands.XeBuildBuild))
        {
            return $"Usage: {path} --input <nand> ({CpuKeySourceHelp}) --dashboard <number> --type <canonical> --output <file> " +
                "[--console <canonical-name>] [--patch <name> ...] [--bigffs] [--rgh3] [--dashlaunch] [--drive-patch usb|hdd|both] " +
                "[--system-partition-only|--full-4gb-data] [--backend wine|native] [--keep-workspace] " +
                "[--force] [--support-root <path>] [--json]";
        }

        throw new InvalidOperationException("The registered command has no usage synopsis.");
    }

    private static string GetCommandPath(CommandDefinitions commands, Command selectedCommand)
    {
        if (ReferenceEquals(selectedCommand, commands.Root))
        {
            return "jrunner";
        }

        foreach (Command group in commands.Root.Subcommands)
        {
            if (ReferenceEquals(selectedCommand, group))
            {
                return "jrunner " + group.Name;
            }

            foreach (Command child in group.Subcommands)
            {
                if (ReferenceEquals(selectedCommand, child))
                {
                    return "jrunner " + group.Name + " " + child.Name;
                }
            }
        }

        throw new ArgumentException("The selected command is not registered.", nameof(selectedCommand));
    }

    private static string GetExampleCommand(CommandDefinitions commands, Command selectedCommand)
    {
        if (ReferenceEquals(selectedCommand, commands.Root))
        {
            return GetCommandPath(commands, commands.NandInspect) + " --help";
        }

        foreach (Command child in selectedCommand.Subcommands)
        {
            return GetCommandPath(commands, child) + " --help";
        }

        string path = GetCommandPath(commands, selectedCommand);
        if (ReferenceEquals(selectedCommand, commands.NandInspect))
        {
            return path + " --input nanddump.bin";
        }

        if (ReferenceEquals(selectedCommand, commands.NandCompare))
        {
            return path + " nanddump1.bin nanddump2.bin";
        }

        if (ReferenceEquals(selectedCommand, commands.NandRgh3Convert))
        {
            return path + " --ecc rgh3-template.bin --flash nanddump.bin --cpu-key-file cpu-key.txt --output rgh3.bin";
        }

        if (ReferenceEquals(selectedCommand, commands.PatchInspect))
        {
            return path + " --input patches.bin --json";
        }

        if (ReferenceEquals(selectedCommand, commands.ConsoleList) ||
            ReferenceEquals(selectedCommand, commands.DeviceList) ||
            ReferenceEquals(selectedCommand, commands.SupportStatus))
        {
            return path + " --json";
        }

        if (ReferenceEquals(selectedCommand, commands.PicoProbe) ||
            ReferenceEquals(selectedCommand, commands.PicoSmcStop) ||
            ReferenceEquals(selectedCommand, commands.PicoSmcStart) ||
            ReferenceEquals(selectedCommand, commands.PicoRebootBootloader) ||
            ReferenceEquals(selectedCommand, commands.PicoEmmcProbe))
        {
            return path + " --device /dev/ttyACM0";
        }

        if (ReferenceEquals(selectedCommand, commands.PicoNandRead))
        {
            return path + " --device /dev/ttyACM0 --start-block 0 --blocks 32 --output nand-range.bin";
        }

        if (ReferenceEquals(selectedCommand, commands.PicoNandWrite))
        {
            return path + " --device /dev/ttyACM0 --start-block 0 --input nand-range.bin --yes";
        }

        if (ReferenceEquals(selectedCommand, commands.PicoNandErase))
        {
            return path + " --device /dev/ttyACM0 --start-erase-block 0 --erase-blocks 1 --yes";
        }

        if (ReferenceEquals(selectedCommand, commands.PicoEmmcRead))
        {
            return path + " --device /dev/ttyACM0 --start-block 0 --blocks 32 --output emmc-range.bin";
        }

        if (ReferenceEquals(selectedCommand, commands.SupportInstall))
        {
            return path;
        }

        if (ReferenceEquals(selectedCommand, commands.XeBuildBuild))
        {
            return path + " --input nanddump.bin --cpu-key-file cpu-key.txt --dashboard 17559 --type glitch2 --rgh3 --console \"Falcon 16MB\" --output updflash.bin --backend wine";
        }

        throw new InvalidOperationException("The registered command has no example.");
    }

    private static void AppendHelpCommands(
        StringBuilder text,
        CommandDefinitions commands,
        Command selectedCommand)
    {
        if (selectedCommand.Subcommands.Count == 0)
        {
            return;
        }

        bool isRoot = ReferenceEquals(selectedCommand, commands.Root);
        int labelWidth = 0;
        if (isRoot)
        {
            foreach (Command group in commands.Root.Subcommands)
            {
                labelWidth = Math.Max(labelWidth, GetHelpCommandLabelWidth(group));
            }
        }
        else
        {
            labelWidth = GetHelpCommandLabelWidth(selectedCommand);
        }

        text.Append("\nCommands:\n");
        if (isRoot)
        {
            foreach (Command group in commands.Root.Subcommands)
            {
                AppendHelpCommandRows(text, group, labelWidth);
            }
        }
        else
        {
            AppendHelpCommandRows(text, selectedCommand, labelWidth);
        }
    }

    private static int GetHelpCommandLabelWidth(Command group)
    {
        int width = 0;
        foreach (Command child in group.Subcommands)
        {
            width = Math.Max(width, "jrunner ".Length + group.Name.Length + 1 + child.Name.Length);
        }

        return width;
    }

    private static void AppendHelpCommandRows(StringBuilder text, Command group, int labelWidth)
    {
        foreach (Command child in group.Subcommands)
        {
            int labelLength = "jrunner ".Length + group.Name.Length + 1 + child.Name.Length;
            text.Append("  jrunner ").Append(group.Name).Append(' ').Append(child.Name);
            AppendHelpColumnGap(text, labelWidth, labelLength);
            text.Append(child.Description).Append('\n');
        }
    }

    private static void AppendHelpArguments(StringBuilder text, Command selectedCommand)
    {
        if (selectedCommand.Arguments.Count == 0)
        {
            return;
        }

        int labelWidth = 0;
        foreach (Argument argument in selectedCommand.Arguments)
        {
            labelWidth = Math.Max(labelWidth, argument.Name.Length + 2);
        }

        text.Append("\nArguments:\n");
        foreach (Argument argument in selectedCommand.Arguments)
        {
            text.Append("  <").Append(argument.Name).Append('>');
            AppendHelpColumnGap(text, labelWidth, argument.Name.Length + 2);
            text.Append(argument.Arity.MinimumNumberOfValues > 0 ? "[required] " : "[optional] ")
                .Append(argument.Description)
                .Append('\n');
        }
    }

    private static void AppendHelpOptions(
        StringBuilder text,
        CommandDefinitions commands,
        Command selectedCommand)
    {
        int labelWidth = 0;
        bool hasLocalOptions = false;
        foreach (Option option in selectedCommand.Options)
        {
            if (ReferenceEquals(option, commands.SupportRoot))
            {
                continue;
            }

            hasLocalOptions = true;
            labelWidth = Math.Max(labelWidth, GetHelpOptionLabelLength(option));
        }

        if (!hasLocalOptions)
        {
            return;
        }

        text.Append("\nOptions:\n");
        foreach (Option option in selectedCommand.Options)
        {
            if (ReferenceEquals(option, commands.SupportRoot))
            {
                continue;
            }

            AppendHelpOptionLabel(text, option, labelWidth);
            bool required = option.Required ||
                ReferenceEquals(option, commands.PicoNandWriteYes) ||
                ReferenceEquals(option, commands.PicoNandEraseYes);
            text.Append(required ? "[required] " : "[optional] ")
                .Append(option.Description)
                .Append('\n');
        }
    }

    private static bool HasHelpOptionValue(Option option)
    {
        return option.Arity.MaximumNumberOfValues > 0 && option.ValueType != typeof(bool);
    }

    private static int GetHelpOptionLabelLength(Option option)
    {
        return option.Name.Length + (HasHelpOptionValue(option) ? (option.HelpName ?? "value").Length + 3 : 0);
    }

    private static void AppendHelpOptionLabel(StringBuilder text, Option option, int labelWidth)
    {
        text.Append("  ").Append(option.Name);
        if (HasHelpOptionValue(option))
        {
            text.Append(" <").Append(option.HelpName ?? "value").Append('>');
        }

        AppendHelpColumnGap(text, labelWidth, GetHelpOptionLabelLength(option));
    }

    private static void AppendGlobalHelpOptions(StringBuilder text, CommandDefinitions commands)
    {
        int labelWidth = Math.Max(HelpAliasesLabel.Length, GetHelpOptionLabelLength(commands.SupportRoot));
        labelWidth = Math.Max(labelWidth, "--version".Length);
        labelWidth = Math.Max(labelWidth, "--json".Length);
        text.Append("\nGlobal options:\n");
        AppendHelpTextRow(text, HelpAliasesLabel, "Show help without executing the command.", labelWidth);
        AppendHelpTextRow(text, "--version", "Show the version.", labelWidth);
        AppendHelpTextRow(text, "--json", "Write one structured result on stdout; diagnostics on stderr.", labelWidth);
        AppendHelpOptionLabel(text, commands.SupportRoot, labelWidth);
        text.Append(commands.SupportRoot.Description)
            .Append(" Only used by support and XeBuild command families.\n");
    }

    private static void AppendHelpTextRow(StringBuilder text, string label, string description, int labelWidth)
    {
        text.Append("  ").Append(label);
        AppendHelpColumnGap(text, labelWidth, label.Length);
        text.Append(description).Append('\n');
    }

    private static void AppendHelpColumnGap(StringBuilder text, int labelWidth, int labelLength)
    {
        text.Append(' ', labelWidth - labelLength + 2);
    }

    private static void AppendHelpNotes(
        StringBuilder text,
        CommandDefinitions commands,
        Command selectedCommand)
    {
        text.Append("\nNotes:\n");
        if (ReferenceEquals(selectedCommand, commands.Root))
        {
            text.Append("  Core NAND/patch operations and Pico do not require support payloads or Wine.\n");
        }
        else if (ReferenceEquals(selectedCommand, commands.Nand))
        {
            text.Append("  Inspection and comparison are read-only; RGH3 conversion creates a separate output image.\n")
                .Append("  Select the inspection input with --input; only nand compare takes two positional file operands.\n");
        }
        else if (ReferenceEquals(selectedCommand, commands.NandInspect))
        {
            text.Append("  Read-only inspection. Select the input with --input, not an unnamed operand.\n");
        }
        else if (ReferenceEquals(selectedCommand, commands.NandCompare))
        {
            text.Append("  Two positional file operands are compared using canonical NAND contents; neither file is modified.\n")
                .Append("  Exit 0 means equivalent contents; exit 1 means a negative comparison result.\n");
        }
        else if (ReferenceEquals(selectedCommand, commands.NandRgh3Convert))
        {
            text.Append("  The RGH3 ECC template (--ecc) and the RGH2 image (--flash) are distinct inputs.\n")
                .Append("  --no-smc-patch preserves the source SMC instead of replacing it with the template SMC.\n");
        }
        else if (IsHelpCommandInGroup(commands.Patch, selectedCommand))
        {
            text.Append("  Patch-file inspection is read-only.\n");
        }
        else if (IsHelpCommandInGroup(commands.Console, selectedCommand))
        {
            text.Append("  Lists the canonical console identities used by xebuild build --console.\n");
        }
        else if (IsHelpCommandInGroup(commands.Device, selectedCommand))
        {
            text.Append("  Enumerates supported command interfaces without opening a transport or sending GET_VERSION.\n");
        }
        else if (ReferenceEquals(selectedCommand, commands.PicoProbe))
        {
            text.Append("  Read-only flash-configuration detection; the SMC is running after a successful probe.\n");
        }
        else if (ReferenceEquals(selectedCommand, commands.PicoSmcStop))
        {
            text.Append("  Stops the SMC and deliberately leaves it stopped.\n");
        }
        else if (ReferenceEquals(selectedCommand, commands.PicoSmcStart))
        {
            text.Append("  Starts the SMC.\n");
        }
        else if (ReferenceEquals(selectedCommand, commands.PicoRebootBootloader))
        {
            text.Append("  Requests reboot into the firmware bootloader.\n");
        }
        else if (ReferenceEquals(selectedCommand, commands.PicoNandRead))
        {
            text.Append("  Read-only NAND access. --start-block and --blocks count 512-byte logical-data records.\n")
                .Append("  Each raw output record includes 16 spare bytes, for 528 bytes total.\n")
                .Append("  Start defaults to 0; omitted count reads through detected geometry. Unrecognized geometry requires an explicit bounded count.\n");
        }
        else if (ReferenceEquals(selectedCommand, commands.PicoNandWrite))
        {
            text.Append("  Destructive, non-rollback operation requiring --yes, explicit --start-block, and known detected geometry.\n")
                .Append("  Start and nonempty input must cover complete erase units; input contains complete 528-byte raw records.\n")
                .Append("  Firmware automatically erases at erase-unit boundaries; a separate erase is unnecessary.\n");
        }
        else if (ReferenceEquals(selectedCommand, commands.PicoNandErase))
        {
            text.Append("  Destructive, non-rollback operation requiring --yes and known detected geometry.\n")
                .Append("  --start-erase-block and --erase-blocks are erase-unit indices/counts, not logical records.\n");
        }
        else if (ReferenceEquals(selectedCommand, commands.PicoEmmcProbe))
        {
            text.Append("  Read-only eMMC device metadata and capacity inspection.\n");
        }
        else if (ReferenceEquals(selectedCommand, commands.PicoEmmcRead))
        {
            text.Append("  Read-only 512-byte eMMC records with no spare bytes. Start defaults to 0; a positive count is required.\n")
                .Append("  Restricted to the first 48 MiB system partition, records [0, 0x18000).\n");
        }
        else if (ReferenceEquals(selectedCommand, commands.Support))
        {
            text.Append("  Inspect or install the pinned V3.4.0-r7 support release used by XeBuild.\n");
        }
        else if (ReferenceEquals(selectedCommand, commands.SupportStatus))
        {
            text.Append("  Checks the pinned support installation. Missing, incomplete, or corrupt support returns exit 4.\n");
        }
        else if (ReferenceEquals(selectedCommand, commands.SupportInstall))
        {
            text.Append("  Installs pinned release V3.4.0-r7. --archive selects a local archive matching that release.\n")
                .Append("  An invalid local archive has no download fallback.\n");
        }

        if (ReferenceEquals(selectedCommand, commands.NandInspect))
        {
            AppendCpuKeyHelpNotes(text, optional: true);
        }
        else if (ReferenceEquals(selectedCommand, commands.NandRgh3Convert) ||
            IsHelpCommandInGroup(commands.XeBuild, selectedCommand))
        {
            AppendCpuKeyHelpNotes(text, optional: false);
        }

        if (IsHelpCommandInGroup(commands.Pico, selectedCommand))
        {
            text.Append("  Pico uses flat command names, such as pico nand-read, not nested NAND/eMMC subcommands.\n")
                .Append("  --device and --serial are mutually exclusive. Automatic selection requires exactly one eligible command-interface candidate.\n")
                .Append("  Firmware version 4 or newer is required. --timeout is a positive no-progress timeout in seconds (default: 10).\n")
                .Append("  NAND reads and eMMC inspection/reads are read-only; NAND writes/erases are destructive and cannot be rolled back.\n")
                .Append("  No eMMC write or erase commands exist.\n");
        }

        if (IsHelpCommandInGroup(commands.XeBuild, selectedCommand))
        {
            text.Append("  Installed pinned support and trusted Wine are required. --backend defaults to wine; native is unavailable, with no fallback.\n")
                .Append("  Canonical --type names are retail, glitch, jtag, glitch2, glitch2m, devgl, devgl16, devkit, devkit16, testkit, and testkit16.\n")
                .Append("  A syntactically canonical name is not a promise that its build path is available.\n")
                .Append("  Only the currently evidenced glitch2/glitch2m plus --rgh3 paths are available; declared --dashlaunch is unavailable.\n")
                .Append("  --console must use a canonical console identity from jrunner console list; if omitted, source inspection selects the console.\n")
                .Append("  Repeat --patch once per patch name; patches retain the requested order.\n")
                .Append("  Full 4 GB inputs require exactly one staging policy: --system-partition-only or --full-4gb-data. Neither flag is valid on smaller inputs.\n")
                .Append("  --keep-workspace retains only the existing non-secret diagnostics; staged source NAND and CPU-key data are removed.\n");
        }

        if (IsHelpCommandInGroup(commands.Support, selectedCommand) ||
            IsHelpCommandInGroup(commands.XeBuild, selectedCommand))
        {
            text.Append("  Support-root precedence: --support-root, JRUNNER_SUPPORT_ROOT, absolute XDG_DATA_HOME plus jrunner/support, then absolute HOME plus .local/share/jrunner/support.\n")
                .Append("  Core NAND/patch operations and Pico do not require support payloads or Wine.\n");
        }

        if (ReferenceEquals(selectedCommand, commands.NandRgh3Convert) ||
            ReferenceEquals(selectedCommand, commands.PicoNandRead) ||
            ReferenceEquals(selectedCommand, commands.PicoEmmcRead) ||
            ReferenceEquals(selectedCommand, commands.XeBuildBuild))
        {
            text.Append("  Existing output files are not replaced unless --force is supplied.\n")
                .Append("  --force permits replacement subject to existing atomic-output/path protections; it does not bypass safety checks.\n");
        }
    }

    private static void AppendCpuKeyHelpNotes(StringBuilder text, bool optional)
    {
        text.Append(optional
                ? "  A CPU-key source is optional; select at most one.\n"
                : "  Exactly one CPU-key source is required.\n")
            .Append("  Accepted sources are --cpu-key-file (file), --cpu-key-env (named environment variable), or --cpu-key-stdin (stdin).\n")
            .Append("  Source contents must be 32 hex digits with an optional trailing line terminator. Never pass a CPU key on the command line.\n");
    }

    private static bool IsHelpCommandInGroup(Command group, Command selectedCommand)
    {
        if (ReferenceEquals(selectedCommand, group))
        {
            return true;
        }

        foreach (Command child in group.Subcommands)
        {
            if (ReferenceEquals(selectedCommand, child))
            {
                return true;
            }
        }

        return false;
    }
}
