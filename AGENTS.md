# JRunnerEx Agent Guide

## Layout and boundaries

- `JRunner.Native.sln` targets .NET 10. `src/JRunner.Core/` owns platform-neutral
  NAND/patch/support/XeBuild contracts and policy; `src/JRunner.Cli/` owns routing
  and Linux adapters. Keep UI globals and Windows dependencies out.
- `JRunner.sln` and `J-Runner/` are the legacy .NET Framework/WinForms oracle,
  not the Ubuntu runtime. Do not launch its GUI or reuse registry/updater/device
  notification code in the CLI.
- Core/CLI tests are in `tests/JRunner.Core.Tests/` and `tests/JRunner.Cli.Tests/`.
  `tests/TestInfrastructure/FixtureCatalog.cs` validates
  `tests/fixtures/manifest.v1.json`; `tests/JRunner.FixtureBuilder/` generates
  synthetic candidates. Update fixture bytes, manifest, and affected tests
  together; never add real CPU keys, console dumps, or personal identifiers.
- Where available, sibling `../PicoFlasher/protocol.h`, `../PicoFlasher/main.c`,
  `../BlackPillFlasher/src/main.c`, and `../BlackPillFlasher/tools/blackpill_cli.py`
  are read-only wire oracles. Firmware and downloaded support payloads are
  external, separately licensed inputs; do not vendor them into this repository.

## Verification and CI

Use SDK `10.0.401` selected by `global.json`; keep the exact
`rollForward: disable` pin, and bump `sdk.version` and the version inside
`sdk.errorMessage` together. Ubuntu apt ships only `10.0.1xx`, so from the
repository root install the pin under `$HOME/.dotnet` (never `/tmp`) and run
with `DOTNET_ROOT="$HOME/.dotnet"` and that directory first on `PATH` (see
`README.md`):

```sh
curl -fsSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --jsonfile global.json --install-dir "$HOME/.dotnet"
```

Then, from the repository root:

```sh
dotnet restore JRunner.Native.sln --locked-mode
dotnet build JRunner.Native.sln -c Release --no-restore
dotnet test JRunner.Native.sln -c Release --no-build
dotnet publish src/JRunner.Cli/JRunner.Cli.csproj -c Release --no-restore -r linux-x64 --self-contained false -o artifacts/linux-x64
./artifacts/linux-x64/jrunner --help
```

Change `packages.lock.json` only with deliberate dependency updates. Native
Ubuntu CI is the release gate: locked build/test/publish, apphost `--help` smoke,
then `jrunner-linux-x64.tar.gz` with executable mode and the `linux-x64/` layout.
The publish requires the .NET 10 runtime. Windows CI is advisory legacy-oracle
coverage in disposable staging, never GUI execution.

Native Ubuntu CI runs its release gate in the digest-pinned .NET 10.0.401 Ubuntu
24.04 container, then creates and drops to a unique `jrunner` UID/GID `1001`
with no supplementary groups or capabilities for every
restore/build/test/publish/smoke/archive command. Keep that image digest and
the preflight ownership/ancestry assertions synchronized with `global.json`; do
not run the native closure gate directly on a mutable hosted image or weaken
static cache closure rules to accommodate its optional libraries.

Generate candidates into an absent directory, never `tests/fixtures/`:
`dotnet run --project tests/JRunner.FixtureBuilder/JRunner.FixtureBuilder.csproj -- /tmp/jrunner-fixtures`.
Keep public command/safety changes synchronized with `README.md`.

## Public contract and CPU-key hygiene

- Human results use stdout; diagnostics/progress use stderr. `--json` emits one
  schema-versioned envelope. Syntax, DTOs, error kinds, exits, and atomic output
  are public API; unequal `nand compare` remains `ok:true` with exit `1`.
- Managed outputs use descriptor-bound atomic publication. Reject existing
  destinations without `--force` and directory destinations before writing;
  existing files remain unchanged on failure/cancellation. Issued hardware
  mutations cannot be rolled back.
- Linux output ancestry must be no-follow, root/current-UID owned and not
  group/other writable, except trusted sticky directories (e.g. `/tmp`) that
  protect owned new entries. Filesystem safety targets cross-UID replacement,
  not protection from hostile same-UID processes.
- CPU keys use file/env/stdin: one source for conversion/XeBuild, none or one
  for inspection; reject conflicts before reading. Preserve 32-hex validation
  and redacted errors. Never log/serialize keys or put them in argv.
- Preserve cancellable Linux fd-0 `poll`/`read` for interactive CPU-key stdin
  and exact echo-scope attribute restoration. Drain only an overlong terminal
  paste's current line before invalid-key/cancel handling; never leave its tail
  for the shell. Cancellation discards pending current canonical-mode input
  while echo is still disabled, before exact attribute restoration, so partial
  key text cannot reach the resumed shell.
- Use real PTY tests for fd-0 polling, cancellation, exact echo restoration,
  and overlong current-line draining: redirected/memory streams do not exercise
  these terminal behaviors.
- Remove a selected non-null env value from the current process before parsing
  or cancellation observation; never restore it or let later children inherit
  it. This is targeted consumption, not scrubbing unrelated vars or the shell.

## Pico safety

- Preserve flat Pico commands, never nested NAND/eMMC groups. Select CDC command
  interface 0 with `--device` or unique `--serial`; unselected requires one
  candidate. Positive `--timeout` is a no-progress deadline (default 10 seconds).
  Stay at 115200 baud, never BlackPill's 1200-baud DFU trigger; retain packaged
  udev/dialout guidance instead of recommending root.
- Check firmware >=4 before later opcodes. Probe/storage disable the SMC
  workaround, stop/wait 500 ms, and restart safely; `smc-stop` leaves it stopped.
- NAND `--start-block`/`--blocks` count 512-byte logical records; files carry
  528-byte data+spare records. Unknown geometry is bounded read-only. Mutations
  require `--yes`, known geometry, valid range, and complete erase units.
  Erase uses `--start-erase-block`/`--erase-blocks`; writes rely on firmware
  auto-erase, never a separate preceding erase.
- eMMC is read-only with validated detect/init/metadata/capacity and the
  independent `[0, 0x18000)`-record (48 MiB) cap. Writes require a distinguishable
  safe firmware capability and approved product change.
- Keep bounded stream abort/drain and SMC cleanup. Indeterminate framing retires
  the connection; `pico-stream-recovery-required` needs a physical reset.
  Never send speculative cleanup commands through unsafe framing.

## Support and XeBuild

- Support precedence: global `--support-root`, `JRUNNER_SUPPORT_ROOT`, absolute
  XDG data home, absolute HOME fallback. Download and `support install --archive`
  require the same pinned `V3.4.0-r7` archive; preserve exact-tree verification
  and atomic activation, not mutable URL/tag/digest overrides.
- Canonical source `packaging/support-manifests/v3.4.0-r7.json` is embedded by Core
  as `JRunner.Core.Support.SupportManifest.v1.json`. Keep its pinned digest and
  LF attributes synchronized with reviewed provenance updates; activation
  metadata is not the trusted expected-tree manifest.
- XeBuild uses canonical `--type`/optional `--console` from `console list`, typed
  options, and explicit full-4-GB staging policy. Wine is the only backend;
  upstream lacks CMake's `source/`. Native requests fail, never fall back.
- Only Glitch2/Glitch2m with `--rgh3` have reviewed direct output evidence. Other
  selections, including those types without RGH3, fail
  `xebuild-output-evidence-unavailable` after CPU-key/native-backend validation
  but before Wine/workspace preflight pending an audited direct
  fingerprint/signature corpus. Compatibility/requested type is not output
  proof; never weaken this gate or call those families supported.
- Pinned support lacks DashLaunch `launch.ini`/`launch_default.ini`;
  `--dashlaunch` is declared but unavailable and fails closed.
- Open XeBuild source snapshots via no-follow descriptor traversal; require
  root/current-UID ownership and no group/other write on the source and protected
  ancestry. Bind inspection to staging with full-content SHA-256, including
  same-length source swaps; unsupported platforms fail closed.
- External Wine/XeBuild image and sidecar writes always use an automatically
  created private `0700` staging directory, even with final outputs beneath
  `/tmp`; never operate directly in the shared final directory.
- Accept only a newly created owner-private managed prefix; no caller-prefix
  option/property or external lock API remains. `--wine-prefix` is an unknown
  CLI option (Usage, exit `2`), not a missing-prerequisite compatibility gate.
  Do not restore arbitrary caller-managed registry/code certification. Trust
  only fresh vendor-generated prefix state and its bounded protected
  code/module/mapping inventory. Keep standard user/common startup directories
  empty, including hidden entries; unsafe fresh/private/identity/startup state
  fails `wine-native-prefix-unsupported` (exit `4`). `c:` targets managed
  `drive_c`; default `z:` -> `/` is DOS data-path translation only, never a host
  DLL/native/module search root or filesystem isolation. Keep data outputs under
  the existing private-staging/AtomicOutput policy.
- Use `WorkspaceDirectoryProtection.OpenOrCreate` for descriptor-bound no-follow
  workspace ancestry. Require root/current-UID ownership and no shared-writable
  leaf; preserve the helper's trusted sticky-ancestor handling. Unsafe owner/write
  permissions fail `xebuild-workspace-path-unsafe` (Usage, exit `2`), not the
  removed caller-prefix path/lock contract.
- Resolve default `wine`/`winepath` from `PATH` only after source staging and
  before CPU-key staging. Require protected no-follow validation of each alias,
  target, and traversed ancestor: root/current-UID ownership and no group/other
  write. Execute selected Wine commands by protected absolute aliases, preserving
  command-name/`argv[0]` and supported package-suffix behavior; never use bare
  `PATH` names or replace an alias with its canonical target as the command.
- Accept shell launchers only in inspectable, exactly fingerprinted installed
  upstream/Debian/Ubuntu forms. Bind their required Wine server/loader/preloader
  commands and finite utility dependencies; a protected wrapper file alone does
  not make its helper lookups safe. Unsupported/generated/build-tree/uninspectable
  wrappers fail pre-key with `wine-wrapper-unsupported` (exit `4`); do not infer
  blanket distro/version compatibility.
- Wine wrapper utilities must resolve to canonical UID `0` `/usr/bin/<command>`
  targets matching the expected command name; `/bin/sh` must resolve to UID `0` `/usr/bin/dash`,
  `/usr/bin/bash`, or `/usr/bin/sh`. Preserve protected current-UID symlink
  aliases, not arbitrary private copies of native helpers. This is Wine's vendor
  boundary only; do not tighten generic private CLR/`setsid` launcher policy.
  Production vendor suffixes are bare and `-development`; `-stable`/`-staging`
  recognition belongs only to the explicit internal legacy fixture resolver,
  not WineHQ or `/opt` selected-loader support.
- Production native Wine (static and dynamic control binaries) requires
  root-installed, trusted official Ubuntu/Debian vendor package code in approved
  `/usr/bin`, `/usr/lib/wine`, or supported-multiarch `wine` layouts (bare or
  `-development` suffixes only). Vendor module/data trees, including modeled
  `/usr/share/wine` variants and `/opt/wine/mono`/`gecko` add-on probes, must be
  recursively root-owned and protected. Retain protected current-UID aliases and
  exactly recognized copied shell wrappers to those native targets;
  custom/bundled/home/`/usr/local`/`/opt`
  native Wine layouts fail `wine-wrapper-unsupported`, not a fallback. This is
  an explicit vendor/root-package trust prerequisite: placement, version output,
  literal-string scans, and frozen file hashes do not attest arbitrary compiled
  `BINDIR`/`SYSTEMDLLPATH`/module roots. Do not add a generic custom-binary proof or
  per-build package-provenance subsystem; content hashes bind selected executable,
  dependency, and recursive-module code across launches, not vendor provenance.
- Resolve the complete native closure statically before Wine preflight or key
  staging: ELF `PT_INTERP` and path-bearing dynamic metadata, direct candidates in
  cache/default/configuration/RPATH/RUNPATH/absolute-dependency parent roots, all
  `glibc-hwcaps` and x86 legacy-hwcap candidate subtrees, and fully recursive
  Wine/CLR/`gconv`/explicit module roots. Protect every reachable candidate/tree,
  alias, target, and ancestor (root/current UID, no group/other write), and parse
  every ELF, including nonexecuting leaves, not only selected dependency winners.
  Direct/cache-only non-transitive candidates are metadata-bound and reparsed on
  every revalidation; SHA-freeze executable roots, every compatible
  ambiguous/transitive dependency, and recursive native/PE/module objects. Use
  bounded `NativeElfReader`/cache parsing, never `ldd`, candidate execution, or
  loader diagnostics as discovery. Do not recursively scan unrelated `/usr/lib`
  data or general executable directories; keep Wine/helper lookups finite.
  CLR module inputs cover the actual assembly/fxr/shared/runtime/store/probing
  roots, not unrelated SDK/packs/tools. Keep installation ancestry protected and
  the installation root fixed in the serialized environment; do not promote it
  wholesale into recursive native roots. A self-contained runtime directory
  remains recursive.
- Launch roots and their transitively required ELF dependencies must resolve
  fully. Recursively audited optional module candidates may have unresolved
  `DT_NEEDED` only if all loader search/candidate roots are protected and bound,
  and absence is recorded/revalidated; later optional `dlopen` resolves only
  protected code or fails normally. Retain the actual optional CLR tracing provider
  even without its matching LTTng ABI; do not delete it, add a shim, or substitute
  a fallback. Optional status never relaxes code-leaf, ELF, environment, or
  configuration validation.
- Supported Ubuntu package roots are modeled installed layouts, not a blanket
  Wine/version/loader guarantee; runnable native ABIs are little-endian
  x86-64/i386 glibc. Validate existing glibc 1.1 cache data (including a validated
  compatibility prefix), protected loader-configuration absolute directory and
  literal/`/*.conf` include forms, default/multiarch roots, and all reachable
  `glibc-hwcaps` candidates.
  `/etc/ld.so.preload` must be absent/empty/comments-only; active entries fail.
  Unsupported/malformed native metadata, cache, loader, or module state returns
  redacted `native-closure-unavailable` (exit `4`), never a production fallback.
- Bare dependency names may resolve only inside that closure. Native path/search
  metadata permits literal absolute paths and exact `$ORIGIN` with an optional
  literal suffix. Use the main executable's canonical containing directory; for
  native shared objects (DSOs), close/revalidate every bound protected load-alias
  origin plus the canonical target as an over-approximation. A DSO symlink target
  elsewhere must cover alias-relative sibling trees as well as target-relative
  trees; canonical-only origin is insufficient. The interpreter must be a modeled
  absolute glibc loader. Permit literal `.`/`..` suffixes only via descriptor/
  no-follow resolution anchored at each protected origin; protect/register the
  alias, resulting canonical root, targets, and ancestry. Do not lexically
  normalize traversal into trust. Reject other
  relative/traversal paths, malformed paths, `$LIB`, `$PLATFORM`, unknown tokens
  and unmodeled tags/flags, and native audit/filter/auxiliary/configuration
  features. Ignore wholly empty `RPATH`/`RUNPATH` tags as glibc does; reject every
  empty component of a nonempty colon-separated value, including leading,
  trailing, and doubled colons. An initial private working directory does not
  freeze later `chdir` behavior; do not add a working-directory API or claim
  `chdir` prevention. Admit non-path architecture ELF tags only as bounded,
  source-modeled metadata, including the modeled glibc 2.41 PLT records; never
  treat unknown architecture tags as harmless.
- Set Wine children's `PATH` only to the owner-private workspace `wine-toolchain`
  directory, populated with validated command/helper bindings. Never restore
  inherited or general system search directories.
- Seal a minimal native environment after caller updates, then apply the fixed
  managed-runtime environment in both launchers. Use owner-private `0700`
  `HOME`/XDG directories, internally fixed managed prefix and headless settings;
  inherited Wine, shell-startup, loader, desktop/GPU/audio, and .NET selectors
  cannot reopen search roots. Headless selectors do not excuse skipping native
  module trees.
  Only the dedicated marked supervisor sets `umask 0077`, after authentication
  and before the contained launch; keep the CLI parent's and unmarked callers'
  existing file-creation policy unchanged.
- Derive sealed `USER` from bounded descriptor-protected `/etc/passwd` data and
  matching real/effective UID fields in `/proc/self/status`, never inherited
  usernames, `Environment.UserName`, or NSS fallback. Require one safe local name
  (1–64 ASCII letters/digits/underscore/dot/hyphen, not `.` or `..`); identity
  mismatch or missing/ambiguous local entry is `wine-native-prefix-unsupported`.
  Freeze the passwd input, literal `USER`, and private startup roots in the
  authenticated proof, not a PID-specific `/proc/self` inode. Remove inherited
  `LOGNAME`, `USERNAME`, and `WINEUSERNAME`; do not invent replacements.
- Initialize the fresh prefix only through the fixed `wine wineboot.exe -i -u`
  operation on an already proved toolchain after version preflight and before
  key staging; it is not discovery. Bind the protected vendor-generated code
  inventory and extend the final proof only while preserving all original
  native code/cache bindings.
- CPU-key staging must follow Wine/native resolution and binding/revalidation of
  the trusted `setsid`/supervisor/runtime chain. Pair `TrustedSupervisorLaunch`
  with `TrustedNativeClosure` on every production invocation. Carry the bounded
  native proof through the authenticated protocol; validate executable membership,
  reparse metadata-only candidate selectors, and revalidate the original
  code/cache/configuration identities immediately before both outer and inner
  `setsid` starts, including post-key `winepath`/XeBuild calls. Never rediscover a
  replacement graph as trusted after final binding. Preserve the fresh prefix's
  protected code/mapping inventory without claiming mutable non-code data is
  byte-frozen or arbitrary external registry selectors are certified. The boundary
  is cross-UID replacement, not root/same-UID races or a general sandbox.
- Synthetic/fake toolchains and unmarked invocations with both trust markers
  null are internal test seams only; preserve their existing semantics, never
  expose a production bypass or user-selectable synthetic backend.
  `CreateSyntheticNativeFixtureResolver`/`UsesSyntheticNativeInputs` are an
  internal metadata-fixture seam only, never CLI/default production selection;
  they do not skip static native closure discovery or launch protection.
- Successful marked-launch fixtures must copy the genuine CLI/apphost or
  `dotnet`, actual CLR installation, and manifest dependency assets beneath
  private protected parents, as `ProtectedRunnableSupervisorDeployment` does.
  Apphost mode requires the actual CLI apphost, never silent host substitution;
  use explicit `useApphost: false` to exercise the genuine `dotnet` host.
  Do not change source installation permissions, bless fake ELF headers, or
  weaken production trust to accommodate a writable SDK/test checkout. Keep
  genuine parent/testhost authentication; remove the test-parent-only startup
  hook for the real child.
  Metadata-only synthetic ELF graphs are for parsing or rejection-before-launch
  tests, not runnable success fixtures; managed metadata-only deployments cannot
  replace the real native runtime closure in a marked call.
- Preserve support immutability, isolated workspaces, staged key/source removal
  even with `--keep-workspace`, process-tree reaping, image/log cleanup, and
  independent output proof before atomic publication.
