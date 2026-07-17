# CLAUDE.md

Cross-platform .NET 8 port of krikzz's FlashKit MD programmer client
(Sega Mega Drive / Genesis cart dumper/flasher). See PLAN.md for the staged
porting goal and docs/hardware-validation.md for real-hardware results.

## Build and test

```
./ci.sh        # restore + build (-warnaserror) + all tests; must stay under ~10s
./publish.sh   # self-contained single-file binaries into artifacts/<rid>/
```

The .NET 8 SDK is user-installed at `~/.dotnet` and NOT on the default PATH.
Both scripts handle that; for ad-hoc commands use
`export PATH="$HOME/.dotnet:$PATH"`.

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
- `src/flashkit-md/` — CLI: arg parsing, file I/O, rendering. Reference
  implementation for future TUI/GUI front-ends, which should also build on
  FlashKitSession.
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
