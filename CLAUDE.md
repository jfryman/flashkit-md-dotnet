# CLAUDE.md

Cross-platform .NET 8 port of krikzz's FlashKit MD programmer client
(Sega Mega Drive / Genesis cart dumper/flasher). See docs/porting-plan.md
for the (completed, archived) staged porting plan and
docs/hardware-validation.md for real-hardware results.

## Docs map (keep the audiences separate)

- README.md — end users only: install from release artifacts, GUI/CLI
  usage. No build instructions there.
- DEVELOPING.md — contributors: build/test/publish, architecture,
  release process. Human-oriented prose.
- CLAUDE.md (this file) — agent working rules, condensed.
- docs/RELEASING.md — release signing/notarization secrets and behavior.

A user-visible change usually needs README.md updated; a workflow or
build change usually needs DEVELOPING.md. Update this file only for
rules agents must follow.

## Build and test

```
./ci.sh        # restore + build (-warnaserror) + all tests; must stay under ~10s
./publish.sh   # self-contained single-file binaries into artifacts/<rid>/
```

The .NET 8 SDK is user-installed at `~/.dotnet` and NOT on the default PATH.
Both scripts handle that; for ad-hoc commands use
`export PATH="$HOME/.dotnet:$PATH"`.

## Changelog (permanent repo preference)

CHANGELOG.md follows the mitchellh/HashiCorp style: one `## X.Y.Z
(Month D, YYYY)` section per release with FEATURES / IMPROVEMENTS /
BUG FIXES headings and `component:`-prefixed entries (cli, gui, core,
serial, release). Every user-visible change adds an entry under `## Unreleased`
in the same commit as the change. To cut a release: rename Unreleased to
the version + date, commit, then tag `vX.Y.Z` — the release workflow
extracts that section for the GitHub release notes and fails the release
if the section is missing.

## Architecture (library-first)

- `src/FlashKit.Core/` — everything; front-ends only render.
  - `Device.cs` / `Cart.cs`: serial protocol + cart logic, ported VERBATIM
    from the original client (lowercase method names and all). Keep them
    diffable against the original source in `flashkit-md-src.zip` (unzip
    to compare); behavior changes belong in separate commits with tests,
    or in FlashKitSession.
  - `DeviceConnector` / `PortDiscovery`: per-OS port scanning with
    surfaced errors (Linux: ttyACM*/ttyUSB*; macOS: cu.usbmodem*/
    cu.usbserial*, never tty.* which block on open; Windows: COM*).
  - `FlashKitSession`: the front-end API — GetInfo/ReadRom/WriteRom/
    ReadRam/WriteRam/BakeSave. Synchronous, progress via
    `Action<OperationProgress>` (each phase starts with Done=0),
    `VerifyException` on read-back mismatch, no console/file I/O.
- `src/FlashKit.Presentation/` — shared presentation model for interactive
  front-ends (GUI now, TUI next). `ProgrammerModel` owns ALL interactive
  behavior: held-session lifetime (the macOS tcdrain-wedge fix), the
  device gate, the poll state machine, auto-dump/auto-write (including the
  deliberately-not-identity-keyed insertion logic), the transaction log,
  and display strings. INotifyPropertyChanged + `IUserPrompts` for user
  decisions; must be called on the UI thread (see the class comment).
  New interactive behavior goes HERE, not in a front-end.
- `src/flashkit-md/` — CLI: arg parsing, file I/O, rendering over
  FlashKitSession directly (one-shot commands need no ProgrammerModel).
- `src/FlashKit.Gui/` — Avalonia adapter over ProgrammerModel: renders
  model properties into controls, implements IUserPrompts with
  StorageProvider pickers, drives the poll timer. Headless tests in
  `tests/FlashKit.Gui.Tests` drive the window against the fake device via
  the connector/file-picker seams in MainWindow.
- `flashkit-md-src.zip` — pristine original Windows source (WinForms,
  .NET 4) as distributed by krikzz. Never modify; unzip elsewhere when a
  diff against the original is needed.

## Testing rules

- CI never touches hardware. `FakeFlashKitDevice` (in tests) emulates the
  programmer firmware + a synthetic cart: mirrored ROM, banked SRAM at
  0x200000 behind the 0xA13000 register, AMD flash commands with AND
  semantics (programming can only clear bits — a skipped erase fails tests).
- `DeviceWireFormatTests` hardcode the original client's exact byte
  sequences. Do not "fix" them to match changed code; they lock the wire
  format.
- No sleeps, no timing dependencies, temp files only via the CliTests
  pattern. Keep the whole suite in-memory and fast.

## Hardware testing (manual only)

The programmer shows up as `/dev/ttyUSB0` on the Linux dev machine and
`/dev/cu.usbserial-*` on the Mac (no group membership needed there). If a
Linux shell session lacks the `uucp` group, wrap commands:
`sg uucp -c "DOTNET_ROOT=$HOME/.dotnet <binary> ..."`.
Order operations least- to most-destructive (info → read-rom → read-ram →
write-ram → write-rom) and record results in docs/hardware-validation.md.
`dumps/` is gitignored — cart dumps and saves must never be committed.
An `Unknown (X) / 0K` info result usually means the cart is unseated, not
a code bug — re-check after reseating before debugging.

macOS FTDI gotcha (fixed 2026-07-17): SerialPort.Close() drains via
tcdrain, which wedged forever after write-rom's multi-MB writes, hanging
the process with the port held. SystemSerialPort now discards the output
queue and abandons a stuck close (guarded-close, covered by
SystemSerialPortTests) — keep that behavior if the adapter is touched.
Corollary: an abandoned close only frees the descriptor at process exit,
so long-lived front-ends must not close per operation — the GUI holds one
session while the programmer is reachable (closing after write-rom left
the port unopenable until the adapter was replugged).

## Domain gotchas

- Save RAM is 8-bit on odd bytes; even bytes read as open bus (0xFF).
  Verifies compare odd bytes only.
- write-rom erases only the image's extent by default (original parity);
  stale flash above the image appears as ghost saves in games. --full-erase
  wipes the whole 4 MB chip but would corrupt ROMs on smaller mirrored
  chips — that's why it is opt-in.
- The FlashKit flash cart validated here has no SRAM; bake-save programs a
  read-only save snapshot into flash at 0x200000 as a workaround.
- ROM size detection relies on mirror probing, so a partially erased flash
  cart can report sizes a real mask ROM cart would not.
