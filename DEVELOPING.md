# Developing flashkit-md-dotnet

This is the contributor guide: building, testing, architecture, and how
releases are cut. User-facing docs live in [README.md](README.md);
[CLAUDE.md](CLAUDE.md) carries the condensed rules AI coding agents work
from — keep the three in sync when things change.

## Building and testing

Requires the .NET 10 SDK — but you don't need to install it yourself:
both scripts source `eng/ensure-dotnet.sh`, which installs the SDK pinned in
`global.json` into `~/.dotnet` (via Microsoft's `dotnet-install.sh`) when
no install on the machine satisfies it.

```
./eng/ci.sh        # restore + build (warnings as errors) + all tests, a few seconds
./eng/publish.sh   # self-contained single-file binaries into artifacts/<rid>/
```

`eng/publish.sh` cross-publishes every supported target
(`linux-x64 linux-arm64 osx-x64 osx-arm64 win-x64`) from any host; set
`RIDS` to build a subset and `VERSION` to override the git-derived version
stamp. The macOS targets also get a `FlashKit MD.app` bundle assembled by
`packaging/macos/make-app.sh`.

If you publish by hand instead, keep
`-p:IncludeNativeLibrariesForSelfExtract=true`: without it the serial-port
native library lands next to the binary instead of inside it, and the
binary alone cannot open any port (this bug has shipped twice).

The published binaries are trimmed (`-p:PublishTrimmed=true`,
`-p:InvariantGlobalization=true`), which drops each one by ~60% (the CLI
from ~39 MB to ~13 MB) by removing the runtime/BCL code and ICU data
nothing references. This is only safe because the GUI compiles its XAML
bindings (`AvaloniaUseCompiledBindingsByDefault` in `FlashKit.Gui.csproj`);
a reflection binding would emit an IL2026 trim warning and could be
silently removed, blanking the transaction log. A trimmed publish must
stay at **zero IL2026/IL2xxx warnings** — if one appears, fix it (usually
add `x:DataType`/compiled bindings or a `DynamicDependency`) rather than
suppressing it. The CLI and TUI trim clean and were verified to run and
render trimmed; the only check that needs a real display is confirming the
GUI window and log still render after a trimmed publish.

### Build configuration

Settings shared by every project live in `Directory.Build.props`
(target framework, nullable, and the analyzers: `AnalysisLevel
latest-recommended` plus `EnforceCodeStyleInBuild`); the per-project
`.csproj` files carry only what is distinctive. NuGet versions are
managed centrally in `Directory.Packages.props` — bump a version there,
never in a project file. The three test projects additionally share
their xunit stack via `tests/Directory.Build.props`.

Because `eng/ci.sh` builds with `-warnaserror`, any new analyzer
diagnostic fails CI. Deliberate exceptions are written down where they
apply: `.editorconfig` scopes rule relaxations to the verbatim-ported
`Device.cs`/`Cart.cs` and the `flashkit_md` namespace,
`tests/.editorconfig` relaxes naming/perf rules that fight test
conventions, and the few in-code `[SuppressMessage]` attributes carry
justifications (e.g. MD5/SHA-1 in `RomHash` are ROM database identity
hashes, not cryptography). Suppress narrowly and say why, or fix the
code — never blanket-disable a rule.

## Architecture

The project is library-first: all device workflows live in
`FlashKit.Core` and the front-ends only render them — the CLI, the
Avalonia GUI, and the Terminal.Gui TUI build on the same tested code.
The GUI and TUI additionally share `FlashKit.Presentation`, so the two
interactive front-ends have identical behavior and wording.

- `src/FlashKit.Core/` — the library.
  - `Device`/`Cart`: serial protocol and cart logic, **ported verbatim**
    from the original client (lowercase method names and all) behind an
    `ISerialPort` seam. They are kept diffable against the original
    source at [github.com/krikzz/flashkit](https://github.com/krikzz/flashkit)
    (`flashkit-md/`); behavior changes belong in
    `FlashKitSession` or in separate commits with tests.
  - `DeviceConnector`/`PortDiscovery`: cross-platform port discovery with
    surfaced per-port errors.
  - `FlashKitSession`: the front-end API — `GetInfo`, `ReadRom`,
    `WriteRom`, `ReadRam`, `WriteRam`, `BakeSave`. Operations are
    synchronous, report progress via an `Action<OperationProgress>`
    callback, throw `VerifyException` on read-back mismatches, and do no
    console or file I/O. `GetInfo` returns a `CartInfo` that also
    identifies the system (Mega Drive/Genesis vs. Sega 32X) and the
    header region.
  - `IpsPatch`: IPS patch `Apply`/`Create` operating purely on byte
    arrays (RLE and the Lunar truncation extension supported); the
    front-ends wire the file I/O around it.
  - `RomHash`: CRC32/MD5/SHA-1 of a buffer, rendered as compact
    uppercase hex — every read/write path reports these so a dump or a
    flashed image can be checked against a known-good checksum.
- `src/FlashKit.Presentation/` — shared presentation model for the
  interactive front-ends: `ProgrammerModel` owns device/cart status
  polling, the held serial session (see the FTDI note below), the
  transaction log, and auto-dump/auto-write, exposing
  `INotifyPropertyChanged` state and asking for user decisions through
  `IUserPrompts`. All members must be called on the UI thread; device
  I/O runs on worker threads internally and marshals results back.
- `src/flashkit-md/` — the CLI: argument parsing, file I/O, rendering
  over `FlashKitSession` directly.
- `src/FlashKit.Gui/` — the Avalonia adapter over `ProgrammerModel`:
  renders model properties into controls, implements `IUserPrompts` with
  StorageProvider pickers, drives the poll timer.
- `src/flashkit-md-tui/` — the Terminal.Gui adapter over
  `ProgrammerModel`, same panel roles and prompt seams as the GUI.
- `tests/FlashKit.Core.Tests/` — wire-format tests locked to the original
  protocol, plus behavior/e2e tests against `FakeFlashKitDevice`, an
  in-memory emulation of the programmer firmware and a synthetic cart.
- `tests/FlashKit.Gui.Tests/` — headless Avalonia tests driving the real
  window against the fake device.
- `tests/FlashKit.Tui.Tests/` — the TUI equivalent; Terminal.Gui views
  work without a driver, so these need no main loop at all.

### Testing rules

- CI never touches hardware, sleeps, or the clock; the whole suite runs
  in memory in a few seconds.
- `DeviceWireFormatTests` hardcode the original client's exact byte
  sequences. Do not "fix" them to match changed code — they lock the wire
  format.
- `IpsRealAssetTests` validate IPS apply/create against real cart dumps
  and a real patch when you drop `base.bin`/`patch.ips`/`patched.bin`
  into `dumps/ips-fixtures/` (or point `FLASHKIT_IPS_FIXTURES` at them).
  Those files are real ROM content, are never committed (`dumps/` is
  gitignored), and the tests no-op when absent so CI stays green.
- Real-hardware validation is a manual checklist; record results in
  [docs/hardware-validation.md](docs/hardware-validation.md). Order
  operations least- to most-destructive (info → read-rom → read-ram →
  write-ram → write-rom).

### macOS FTDI close gotcha

`SerialPort.Close()` drains via `tcdrain`, which can wedge forever after
multi-megabyte flash writes on the macOS FTDI driver. `SystemSerialPort`
discards the output queue and abandons a stuck close; an abandoned close
only frees the descriptor at process exit, so long-lived front-ends must
not close per operation — the GUI holds one session while the programmer
is reachable. Keep both behaviors if you touch the serial layer.

## Changelog and releases

`CHANGELOG.md` follows the mitchellh/HashiCorp style: one
`## X.Y.Z (Month D, YYYY)` section per release with FEATURES /
IMPROVEMENTS / BUG FIXES headings and `component:`-prefixed entries
(cli, gui, tui, core, serial, release, docs). Every user-visible change
adds an entry under `## Unreleased` in the same commit as the change.

To cut a release: rename `## Unreleased` to the version + date, commit,
and push a `vX.Y.Z` tag. The release workflow builds and signs all
targets, packages the Flatpak, extracts that changelog section for the
release notes, and fails if the section is missing. Code-signing and
notarization credentials are repository secrets — see
[docs/RELEASING.md](docs/RELEASING.md) for what to configure and what
happens when they are absent.

## Packaging

- `packaging/macos/` — `FlashKit MD.app` assembly (`make-app.sh`,
  `Info.plist`, icon) and the hardened-runtime entitlements used when a
  signing identity is available.
- `packaging/flatpak/` — Flatpak manifest, desktop entry, AppStream
  metainfo, and icon. CI builds the bundle on every push to main; the
  release workflow attaches `flashkit-md-vX.Y.Z-x86_64.flatpak` to the
  release.
- `packaging/99-flashkit-md.rules` — optional udev rule for serial
  access on Linux.
