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

From this repository root, with SDK `10.0.401` from `global.json` installed:

```sh
dotnet restore JRunner.Native.sln --locked-mode
dotnet build JRunner.Native.sln -c Release --no-restore
dotnet test JRunner.Native.sln -c Release --no-build
dotnet publish src/JRunner.Cli/JRunner.Cli.csproj -c Release --no-restore -r linux-x64 --self-contained false -o artifacts/linux-x64
```

The published Linux x64 apphost is framework-dependent (`--self-contained false`)
and requires the .NET 10 runtime. The local publish writes it to
`artifacts/linux-x64/jrunner`; confirm it and discover the implemented command
surface with:

```sh
./artifacts/linux-x64/jrunner --version
./artifacts/linux-x64/jrunner --help
```

The `jrunner-linux-x64.tar.gz` CI archive contains the published directory beneath
`linux-x64/`. Extract the complete directory, keep its companion files alongside
the apphost, and run `./linux-x64/jrunner`; the archive does not bundle the .NET
runtime or the separately installed XeBuild support payload.

The native Ubuntu 24.04 GitHub Actions release gate runs in a digest-pinned
.NET 10.0.401 Ubuntu container as an unprivileged user. It runs the locked
restore/build/test/publish sequence above, smokes the published apphost with
`--help`, and uploads the tar archive with executable mode preserved. The
separate Windows legacy-oracle job is advisory: it restores and builds the
original solution in a disposable staging directory, but never launches its GUI.

The checked-in synthetic fixtures can then be exercised through the published
CLI:

```sh
./artifacts/linux-x64/jrunner nand inspect --input tests/fixtures/nand/small-block.bin --cpu-key-file tests/fixtures/keys/small-block.cpukey --json
./artifacts/linux-x64/jrunner nand compare tests/fixtures/nand/small-block.bin tests/fixtures/nand/small-block-remapped-equivalent.bin --json
./artifacts/linux-x64/jrunner patch inspect --input tests/fixtures/patches/patches.bin --json
```

These files contain only deterministic synthetic data. Their byte lengths and
SHA-256 digests are locked in `tests/fixtures/manifest.v1.json`. Generate a
fresh fixture tree when that contract changes:

```sh
dotnet run --project tests/JRunner.FixtureBuilder/JRunner.FixtureBuilder.csproj -- /tmp/jrunner-fixtures
```

The builder requires the output path to be missing. It atomically publishes a
private staging tree and never replaces an existing fixture tree. Review a
fresh output through normal source-control workflow before updating the
checked-in fixtures.

## CLI results and CPU keys

Human-readable results go to stdout; diagnostics and progress go to stderr.
With `--json`, stdout contains exactly one compact UTF-8 object with
`schemaVersion: 1`: success has `ok: true` and `result`; failure has `ok: false`
and `error` containing numeric `code`, stable `kind`, and human `message`.
A completed unequal `nand compare` is still a success envelope with
`result.equal: false`, but exits `1`.

| Exit | Meaning |
| --- | --- |
| `0` | Success |
| `1` | Completed negative result (unequal NANDs) |
| `2` | Usage or validation failure |
| `3` | Invalid image/data or failed CPU-key verification |
| `4` | Missing prerequisite or unsupported backend/state |
| `5` | Device absent or permission denied |
| `6` | Transport or filesystem I/O failure |
| `7` | XeBuild/external-process failure |
| `130` | Cancellation |

CPU keys are accepted only through `--cpu-key-file <file>`,
`--cpu-key-env <name>`, or `--cpu-key-stdin`. RGH3 conversion and XeBuild require
exactly one source; `nand inspect` permits none or one. Multiple sources are
rejected before reading any of them. Values must contain exactly 32 hexadecimal
characters, optionally followed by one LF or CRLF. Stdin accepts one value and
rejects extra redirected data; interactive Linux input disables echo and
restores the original terminal attributes on success, failure, or cancellation.
TTY input uses cancellable Linux fd-0 `poll`/`read`. An overlong paste drains
only the rest of its current line before invalid-key or cancellation handling,
never leaving that line's tail to the shell. Cancellation discards pending
terminal input while echo is still disabled, before exact attribute restoration,
so partial key text cannot reach the resumed shell.

The environment source is consumed: once a selected value is read, `jrunner`
removes that variable from its own process environment before parsing or
observing cancellation, and never restores it. This prevents later child
processes from inheriting the selected value even when key validation fails;
it does not clear the caller's shell or unrelated environment variables.
Keys are never emitted in results, diagnostics, logs, or child-process
arguments. Use a protected key file or stdin rather than placing a key on the
command line.

## Output safety

Managed file outputs use descriptor-bound atomic publication: only complete,
validated output reaches the requested destination. Existing outputs without
`--force` and directory destinations are rejected before writing; failure or
cancellation leaves the destination unchanged.

On Linux, output ancestry is traversed without following symlinks and must be
owned by the current UID or root, with no group/other write permission. Trusted
sticky directories such as `/tmp` are allowed because they protect newly
created owned entries from replacement by other UIDs. These path protections
address cross-UID replacement, not hostile processes running as the same UID.

## Core RGH conversion

`jrunner nand rgh3-convert` performs a cancellable RGH2-to-RGH3 conversion
for the legacy 16 MB/64 MB interleaved-ECC and 48 MB logical eMMC image
shapes. It requires a caller-supplied RGH3 template and one CPU-key source:

```sh
./artifacts/linux-x64/jrunner nand rgh3-convert --ecc rgh3-template.ecc --flash rgh2-flash.bin --cpu-key-file cpu-key.txt --output rgh3-flash.bin --json
```

Conversion uses the atomic-output safeguards above and publishes only on
success. `--force` permits replacement; `--no-smc-patch` preserves the source
SMC instead of applying the template SMC.

## PicoFlasher and BlackPill storage

Linux discovery selects the CDC command interface (interface 0) of USB
`600d:7001` PicoFlasher or BlackPill devices, not their UART interfaces.
List candidates without opening a transport, then probe a selected device:

```sh
./artifacts/linux-x64/jrunner device list --json
./artifacts/linux-x64/jrunner pico probe --device /dev/ttyACM0 --timeout 10 --json
```

Every Pico command accepts either `--device <path>` naming a discovered command
interface or `--serial <value>` matching an exact USB serial, never both.
Without a selector, exactly one physical command candidate is required.
Duplicate serials are ambiguous and require `--device`. A positive
`--timeout <seconds>` defaults to `10`: it is a no-progress deadline reset by
transferred bytes, not a total-operation limit, so a progressing dump can take
longer.

Every command that opens a device first checks firmware version 4 or later;
older firmware is rejected before SMC, flash, or eMMC opcodes. `pico probe`
reports the raw/decoded NAND configuration and leaves the SMC running.
The flat control commands `pico smc-stop`, `pico smc-start`, and
`pico reboot-bootloader` share selector/timeout options but do not run the
flash-operation wrapper; `smc-stop` intentionally leaves the SMC stopped.
The transport stays at 115200 baud, never 1200 (BlackPill's DFU trigger).

### Linux device permissions

Install the packaged rule before opening a physical flasher as an unprivileged
user:

```sh
sudo install -D -m 0644 packaging/60-jrunner-pico.rules /etc/udev/rules.d/60-jrunner-pico.rules
sudo udevadm control --reload-rules
# Unplug and reconnect the flasher.
```

The rule grants active desktop users access with `uaccess` and assigns the
matching tty node to `dialout` with mode `0660`. For headless or SSH sessions,
add the intended user to `dialout` (`sudo usermod -aG dialout "$USER"`) and
start a new login session. Do not run `jrunner` as root; verify the selected
node with `stat -c '%A %G %n' /dev/ttyACM0` (or its actual path).

### NAND access

```sh
./artifacts/linux-x64/jrunner pico nand-read --device /dev/ttyACM0 --start-block 0 --blocks 1 --output nand-record.bin
./artifacts/linux-x64/jrunner pico nand-read --serial <serial> --output nanddump.bin
./artifacts/linux-x64/jrunner pico nand-write --device /dev/ttyACM0 --start-block 0 --input nand-image.bin --yes
./artifacts/linux-x64/jrunner pico nand-erase --device /dev/ttyACM0 --start-erase-block 0 --erase-blocks 1 --yes
```

`--start-block` and `--blocks` count 512-byte logical-data records, not physical
erase blocks. `nand-read` defaults to start `0`; omitting `--blocks` reads
through the end of recognized geometry. Unknown nonzero NAND configurations
are read-only and require an explicit positive, bounded `--blocks` count.
Absent and eMMC configurations are rejected by NAND commands.

The output contains 528 bytes per NAND record (512 data + 16 spare); results
report logical and raw byte lengths separately. Reads starting at zero stream
the requested count; nonzero starts use individual record reads. NAND/eMMC
reads publish only complete output using the atomic-output safeguards above.
An existing destination is rejected unless `--force` is supplied, and failed
or cancelled reads leave it unchanged.

**NAND write and erase are destructive.** Both require `--yes` before opening
the device, recognized safe geometry, and an in-range request. `nand-write`
requires explicit `--start-block`, a nonempty input divisible by 528 bytes,
an erase-unit-aligned start, and complete erase units. Depending on detected
geometry, an erase unit is 32, 256, or 512 logical records. Firmware
`WRITE_FLASH` automatically erases at those boundaries; the CLI does not
precede writes with a separate erase.

`nand-erase` instead requires `--start-erase-block` and positive `--erase-blocks`,
which address complete erase units. Its result reports inclusive logical-record
and byte ranges. Cancellation cannot undo NAND writes or erases already sent
to the hardware.

### Read-only eMMC access

```sh
./artifacts/linux-x64/jrunner pico emmc-probe --serial <serial> --json
./artifacts/linux-x64/jrunner pico emmc-read --device /dev/ttyACM0 --start-block 0 --blocks 1 --output emmc-record.bin
```

`emmc-probe` reads raw CID, CSD, EXT_CSD, and reported capacity after successful
detect/init. `emmc-read` outputs 512 bytes per record (no spare bytes), requires
positive `--blocks`, and defaults `--start-block` to zero. It accepts the same
selectors, timeout, and atomic-output/`--force` behavior as `nand-read`.
All-zero CID/CSD or invalid EXT_CSD capacity reject the operation before data
reads. Accepted capacity must cover at least `0x18000` records and be below the
firmware's `0x800000`-record addressing limit.

Every range is checked against both accepted capacity and the independent
first-48-MiB window `[0, 0x18000)` in 512-byte records, not a discovered partition
map. Zero-origin reads stream exactly the requested count; nonzero starts read
individual records. No eMMC write or erase command is registered: firmware v4
does not distinguish the revisions needed to prove safe write readiness.

Each flash operation disables the firmware's legacy SMC fallback, explicitly
stops the SMC, and waits for the firmware-required 500 ms. It restarts the SMC
before a successful result is returned. When failure or cancellation leaves
command framing determinate, it attempts an SMC restart. If any device-command
write fails before completion, the CLI treats the command framing as
indeterminate, sends no later device command, and retires the connection. When
an active NAND or eMMC stream needs cleanup, the CLI sends that stream's
zero-count reset and drains only queued input to a bounded quiescence point. It
then retires the connection even after a quiet drain, because firmware does not
acknowledge that reset. For stream cleanup, the CLI attempts an SMC restart only
when the reset command completed. If it reports `pico-stream-recovery-required`,
physically reset or power-cycle the flasher before issuing any further command.

## XeBuild support payload

XeBuild support files are external payload, acquired separately:

```sh
./artifacts/linux-x64/jrunner support install
./artifacts/linux-x64/jrunner support install --archive /path/to/J-Runner-with-Extras.zip
./artifacts/linux-x64/jrunner support status --json
```

The global `--support-root <directory>` option may appear before or after the
command. Resolution precedence is:

1. Explicit `--support-root`.
2. `JRUNNER_SUPPORT_ROOT`.
3. `$XDG_DATA_HOME/jrunner/support`, when `XDG_DATA_HOME` is absolute.
4. `$HOME/.local/share/jrunner/support`, when `HOME` is absolute.

Invalid explicit or environment overrides are rejected rather than ignored.
Without an absolute XDG or HOME fallback, resolution fails. Unix installation
also refuses roots whose symlink, ownership, or write permissions permit
untrusted replacement.

Installation downloads the official
[`V3.4.0-r7` release archive](https://github.com/J-Runner-With-Extras/J-Runner-with-Extras/releases/download/V3.4.0-r7/J-Runner-with-Extras.zip),
or uses `--archive` without a network fallback. Either source must match the
pinned archive SHA-256
`c92a31d21d7dc617b3dae47e44b3ae9988acf8b94c0da390f8a69e6805bc45b6`.
The reviewed canonical file list, sizes, and hashes live in
[`packaging/support-manifests/v3.4.0-r7.json`](packaging/support-manifests/v3.4.0-r7.json).
Core embeds that file as `JRunner.Core.Support.SupportManifest.v1.json` and
verifies its pinned digest; install metadata is not the trusted expected-tree
manifest.

Installation rejects unsafe archive entries and verifies a fresh generation
before atomic activation, preserving the previous generation on failure.
Activation metadata records the release, source URL, archive/manifest digests,
timestamp, and active generation. `support status` checks that provenance and
the exact expected tree: only `valid` succeeds; `absent`, `incomplete`, and
`corrupt` return exit `4` with error kinds `support-absent`, `support-incomplete`,
and `support-corrupt`. These payload files are not covered by the repository's
source license. Core NAND/patch and Pico commands need neither support nor Wine.

## XeBuild images

XeBuild runs the official Windows executable in an isolated Wine workspace.
Wine is the only implemented/default backend. `--backend native` returns
`xebuild-backend-unavailable` (exit `4`), never a fallback: the
[upstream XeBuild tree](https://github.com/cOzInABox/XeBuild) still lacks the
`source/` required by its CMake build.

**Current output-evidence limit:** only `--type glitch2` or `--type glitch2m`
with `--rgh3` has a reviewed direct RGH3 output signature. Other selections,
including Glitch2/Glitch2m without `--rgh3`, fail closed with
`xebuild-output-evidence-unavailable` (exit `4`) after CPU-key validation and the
native-backend availability check, but before Wine preflight or workspace
creation. They require an audited direct-output fingerprint/signature corpus
in a reviewed source update; installing support or Wine does not unlock
them. Canonical type parsing is not a claim that every family can build.

Discover canonical console names before overriding source-image detection:

```sh
./artifacts/linux-x64/jrunner console list --json
```

`--type` is required and accepts only `retail`, `glitch`, `jtag`, `glitch2`,
`glitch2m`, `devgl`, `devgl16`, `devkit`, `devkit16`, `testkit`, or `testkit16`.
Optional `--console` accepts a canonical name from that list, such as
`"Falcon 16MB"`, not a numeric ID or legacy alias. The names are case-insensitive.
For an otherwise compatible Falcon RGH3 request:

```sh
./artifacts/linux-x64/jrunner xebuild build \
  --input nanddump.bin \
  --cpu-key-file cpu-key.txt \
  --dashboard 17559 \
  --type glitch2 \
  --rgh3 \
  --console "Falcon 16MB" \
  --output updflash.bin \
  --backend wine
```

This path still requires a source NAND and CPU key, a valid pinned support
generation, selected dashboard assets, and a trusted Wine installation, not
merely `wine`/`winepath` names on `PATH`.
The source and its ancestry are opened through no-follow descriptor traversal
and checked for root/current-UID ownership and no group/other write permission.
Staging must match the full SHA-256 snapshot recorded during inspection; a
same-length replacement does not bypass this check. Unsupported platforms
fail closed.

Default Wine commands are discovered on `PATH`, then accepted only after
protected no-follow checks of aliases, targets, ancestry, and launcher helpers.
Commands retain their protected absolute aliases and command-name/`argv[0]`
behavior for bare and `-development` vendor packages. Shell wrappers must match
recognized installed upstream/Debian/Ubuntu forms; unsupported, generated,
build-tree, or uninspectable wrappers fail with `wine-wrapper-unsupported`
(exit `4`) before CPU-key staging. Children use a private curated helper `PATH`,
not inherited or general system search directories.
Recognized Wine wrapper utilities must resolve to root-owned `/usr/bin/<command>`;
`/bin/sh` must resolve to root-owned `/usr/bin/dash`, `/usr/bin/bash`, or
`/usr/bin/sh`. Protected current-UID aliases remain usable, but private copies
of these native helpers do not satisfy the vendor prerequisite.

Trust also covers native code loaded after startup, not just the Wine launchers.
Before Wine preflight or CPU-key staging, bounded static ELF/cache readers close
native interpreters and direct loader candidates in cache/default/multiarch and
embedded search roots, plus complete hardware-capability subtrees and recursive
Wine, CLR, `gconv`, and explicit module roots. Discovery never executes candidate
tools, `ldd`, or loader diagnostics. Every reachable root/candidate, alias,
target, and ancestor must be root/current-UID owned with no group/other write;
every reachable ELF is checked, not just the current library winner. Unrelated
`/usr/lib` data subtrees and general executable directories are not recursively
scanned.

Launch roots and their transitively required ELF dependencies must resolve fully.
Audited optional module candidates may retain missing `DT_NEEDED` dependencies
only when every loader search/candidate root is protected and frozen, with absence
recorded and revalidated. A later optional load can then use only protected code
or fail normally. The actual CLR tracing provider remains audited even without
its matching optional LTTng library; no shim, deleted module, or replacement
fallback is used.

Native Wine support requires official, root-installed Ubuntu/Debian package code
in recognized protected `/usr` layouts: `/usr/bin` or `/usr/lib/wine` launchers,
`/usr/lib/x86_64-linux-gnu/wine` and the i386 counterpart for modules, and
`/usr/share/wine` data (bare or `-development` suffix only). Vendor native files and
module/data trees must be root-owned with no group/other write. Protected
current-UID aliases and exactly recognized copied shell wrappers remain allowed
only when their native Wine targets satisfy this package boundary. Custom,
bundled, home, `/usr/local`, or `/opt` native Wine layouts fail
`wine-wrapper-unsupported` (exit `4`); separately modeled
`/opt/wine/mono`/`gecko` add-on probes do not authorize an `/opt` Wine installation.

Vendor package code and its standard compiled root choices are an explicit
prerequisite, not generic attestation of arbitrary ELF binaries or configuration
strings. Install the required packaged Wine architecture modules and a protected
.NET runtime. Runnable native ABIs are little-endian x86-64/i386 glibc. Existing
glibc 1.1 caches and protected absolute loader-configuration entries/includes are
parsed, not bypassed. `/etc/ld.so.preload` must be absent, empty, or comments-only.

Native search directories must be literal absolute paths or exact `$ORIGIN`
with an optional literal suffix. The main executable uses its canonical parent;
native shared objects use every bound protected load-alias origin, plus the
canonical target origin as an over-approximation. A symlinked target elsewhere
must include alias-relative sibling trees, not just the target's trees. Literal
`.`/`..` suffixes require descriptor/no-follow resolution anchored at each
protected origin, binding aliases, canonical roots, targets, and ancestry. Other
relative/traversal paths,
malformed paths, `$LIB`, `$PLATFORM`, and unknown tokens are unsupported. An
entirely empty `RPATH`/`RUNPATH` is ignored, but a nonempty value with leading,
trailing, or doubled colons has unsupported empty search components. Native
audit/filter/auxiliary/configuration loader features are unsupported.
Unsafe dependencies or unsupported ELF/loader/cache/module state fail
with `native-closure-unavailable` (exit `4`) before CPU-key staging. Restore a
protected supported package layout and remove unsupported loader configuration;
putting different command names on `PATH` does not repair the native closure.

Wine and supervision use a sealed minimal environment with private `HOME`/XDG
directories and the internally selected managed prefix. Inherited Wine, shell,
dynamic-loader, and .NET selectors cannot redirect code loading; display/GPU/audio
selectors are disabled for headless execution. These protections target
replacement by other unprivileged UIDs, not root or hostile same-UID processes;
they are not a general-purpose sandbox.

Wine's sealed `USER` is derived without NSS fallback from an unambiguous protected
local `/etc/passwd` entry matching the real/effective UID in descriptor-bound
`/proc/self/status`. Missing/unsafe local identity or unequal UIDs fail
`wine-native-prefix-unsupported` (exit `4`); use a normal local account rather
than inherited username overrides.

Typed options are repeated `--patch <name>`, `--bigffs`, `--rgh3`,
`--dashlaunch`, and `--drive-patch usb|hdd|both`; board, target, and dependency
compatibility checks apply. The pinned payload lacks `xeBuild/launch.ini` and
`xeBuild/launch_default.ini`, so `--dashlaunch` is currently unavailable and
fails closed rather than producing an image without the requested feature.

A full 4 GB eMMC source requires exactly one of `--system-partition-only`
(stage the first 48 MiB) or `--full-4gb-data` (stage the full source). Neither
policy is accepted for a non-full-4-GB source; there is no implicit truncation.
`--force` permits replacing an existing output.

Each Wine build uses an isolated support snapshot and a newly created private
managed prefix, never `~/.wine` or a caller-managed prefix. `--wine-prefix` has
been removed; passing it is an unknown CLI option (exit `2`), not a supported
external-prefix path or compatibility mode. Workspace ancestry is opened through
no-follow protected descriptors; unsafe ownership/write permissions fail
`xebuild-workspace-path-unsafe` (exit `2`).
Every newly created workspace component, including intermediate support/template
directories, uses owner-only `0700` creation permissions; existing protected
ancestor permissions are not rewritten.

The fresh prefix comes only from trusted vendor Wine initialization. Its bounded
protected code/module and mapping inventory is bound to the native proof, and
standard user/common startup directories must remain empty. Unsafe fresh-prefix
or startup state fails `wine-native-prefix-unsupported` (exit `4`). This does not
certify arbitrary caller-prefix registry state.
`c:` targets the managed `drive_c`; default `z:` -> `/` is DOS data-path
translation only, never a host DLL/native/module search root or filesystem
isolation. Data publication still uses the atomic output protections above.
Output and managed-prefix paths must stay outside the support root.

The fresh prefix is initialized by the fixed, proved `wine wineboot.exe -i -u`
operation after version preflight but before CPU-key staging; initialization is
not executable discovery. Generated code inventory is protected and bound. The
closure revalidates every loader candidate's identity and selectors; executable,
transitively reachable, and recursive-module code is additionally content-frozen
before any key-bearing launch.

Wine/XeBuild always writes output images and sidecars in an automatically
created private `0700` staging directory, even when the final output is beneath
`/tmp`; it does not write directly in the shared final directory.
`--keep-workspace` retains non-secret failure diagnostics, not the staged key
or source dump. Process success alone is insufficient: independent inspection
must match the console, dashboard, and reviewed image-family evidence before
atomic publication. Failure/cancellation removes temporary image/log artifacts
and reaps any running Wine process tree.
Each invocation's private supervisor owns process discovery, signaling, and
reaping. The launch/supervision chain and a bounded native closure are bound before
CPU-key staging. Each marked launch carries that proof through the authenticated
supervisor protocol and revalidates it immediately before both outer and inner
`setsid` starts, including post-key `winepath` and XeBuild calls. It never accepts
a replacement graph after final binding.
Cancellation is reported only after that scope is fully reaped; an unconfirmed
cleanup remains an external-process failure, not cancellation.

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