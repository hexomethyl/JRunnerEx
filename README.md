# JRunnerEx

JRunnerEx is a native Ubuntu command-line fork of
[Octal450/J-Runner-with-Extras](https://github.com/Octal450/J-Runner-with-Extras).
It preserves the upstream history and keeps the original Windows solution as a
behavior oracle while moving reusable NAND, KV, ECC, patch, XeBuild-preparation,
and PicoFlasher protocol logic into a modern, headless .NET implementation.

The supported product surface is the `jrunner` CLI on Ubuntu. PicoFlasher and
BlackPill CDC operations require firmware version 4 or later; earlier firmware
is rejected before any flash, SMC, or eMMC operation is sent.

## Native build and fixtures

From this repository root, with the .NET 10 SDK installed:

```sh
dotnet restore JRunner.Native.sln --locked-mode
dotnet build JRunner.Native.sln -c Release --no-restore
dotnet test JRunner.Native.sln -c Release --no-build
dotnet publish src/JRunner.Cli/JRunner.Cli.csproj -c Release -r linux-x64 --self-contained false -o artifacts/linux-x64
```

The checked-in synthetic fixtures can then be exercised through the published
CLI:

```sh
./artifacts/linux-x64/jrunner nand inspect --input tests/fixtures/nand/small-block.bin --cpu-key-file tests/fixtures/keys/small-block.cpukey --json
./artifacts/linux-x64/jrunner nand compare tests/fixtures/nand/small-block.bin tests/fixtures/nand/small-block-remapped-equivalent.bin --json
./artifacts/linux-x64/jrunner patch inspect --input tests/fixtures/patches/patches.bin --json
```

## XeBuild support payload

XeBuild support files are acquired separately with `jrunner support install`.
They are verified against a pinned release archive and manifest, installed below
the configured XDG data root, and are never committed to this repository.
Those payload files—including XeBuild and associated support assets—are not
covered by this repository's source license. XeBuild builds currently use the
official Windows executable through Wine; the CLI and Pico transport remain
native.

## Legacy Windows oracle

`JRunner.sln` and `J-Runner/` remain the legacy .NET Framework/WinForms
application. They are retained as a compatibility oracle only; the Ubuntu CLI
does not start WinForms, use registry storage, invoke the updater, or depend on
Windows device-notification code.

## License and external firmware

The [MIT License](LICENSE) applies to JRunnerEx and the inherited J-Runner
source in this repository. The PicoFlasher and BlackPillFlasher firmware
projects are external, read-only protocol oracles: they are not copied into
JRunnerEx, and their respective repositories retain their own license notices.