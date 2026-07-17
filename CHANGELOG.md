# Changelog

## 1.2.0 (July 17, 2026)

FEATURES:

 * gui: new cross-platform desktop front-end (`src/FlashKit.Gui`,
   Avalonia) mirroring the original WinForms client: Read/Write ROM,
   Read/Write RAM, and Cart info buttons, a console log, and a progress
   bar. Built on `FlashKitSession`; operations run off the UI thread so
   the window stays responsive during multi-MB transfers.
 * release: `publish.sh` and the release workflow now build, sign, and
   package the GUI (`flashkit-md-gui`) alongside the CLI in every
   platform archive.

## 1.1.0 (July 17, 2026)

FEATURES:

 * cli: `read-rom --trust-header` dumps the extent declared in the ROM
   header instead of the mirror-probed size — useful on flash carts,
   where partially programmed flash can make probing misjudge the ROM's
   extent. `info` now prints the header size whenever it disagrees with
   the probed size. (Inspired by joeyparrish/flashkit-md-py.)
 * cli: `write-rom` and `bake-save` now verify a CFI-capable flash chip
   answers on the cart bus before erasing anything, failing fast on a
   mask ROM game cart or an unseated cart instead of reporting a verify
   error after a full "write". `--no-flash-check` skips the check.
   (Inspired by joeyparrish/flashkit-md-py.)
 * core: `FlashKitSession` gains `ReadHeaderRomSize()`, a size override
   on `ReadRom`, `CheckFlash()`, and `skipFlashCheck` parameters on
   `WriteRom`/`BakeSave`; `CartInfo` gains `HeaderRomBytes`.

## 1.0.1 (July 17, 2026)

IMPROVEMENTS:

 * release: the project is now explicitly MIT-licensed — a LICENSE file
   was added retaining krikzz's copyright, matching the license on the
   original sources at github.com/krikzz/flashkit. The README credits
   the original author prominently.

## 1.0.0 (July 17, 2026)

1.0 milestone: hardware validation is complete on all three platforms
(Linux, macOS, Windows) — see docs/hardware-validation.md. Dumps are
byte-identical across platforms and to the original krikzz Windows
client (ROM and SRAM), and write-rom round-trips were verified on real
flash carts including console boot tests.

IMPROVEMENTS:

 * release: GitHub release notes are now extracted from this changelog's
   section for the tagged version; the release fails if the section is
   missing.

## 0.9.2 (July 17, 2026)

BUG FIXES:

 * serial: `write-rom` no longer hangs the process on exit on macOS. The
   FTDI driver's output drain (`tcdrain`, run inside the serial close)
   can wedge forever after a flash write's multi-megabyte output; the
   port close now discards the already-acked output queue first and
   abandons a close that still wedges, releasing the port.

## 0.9.1 (July 17, 2026)

BUG FIXES:

 * release: archives now work standalone — the serial-port native
   library (`libSystem.IO.Ports.Native`) is embedded into the
   single-file binary. v0.9.0 archives shipped without it, so every
   serial open failed with a dlopen error.
 * release: macOS binaries are ad-hoc code-signed. macOS kills unsigned
   arm64 executables on launch, which made the v0.9.0 osx-arm64 binary
   unrunnable without a manual `codesign -s -`.

## 0.9.0 (July 17, 2026)

Initial release: a cross-platform .NET 8 port of krikzz's Windows-only
FlashKit MD programmer client, hardware-validated on Linux.

FEATURES:

 * cli: `info`, `read-rom`, `write-rom`, `read-ram`, `write-ram`
   commands mirroring the original client's five buttons, with a
   `--port` override, progress display, MD5 printout, and read-back
   verification on writes.
 * cli: `write-rom --full-erase` wipes the whole flash chip before
   writing, preventing stale data above the image from appearing as
   corrupted "ghost" save slots on console.
 * cli: `bake-save` programs a save snapshot into flash at the save
   window for SRAM-less flash carts — read-only saves that survive
   power cycles.
 * core: `FlashKitSession` library API (GetInfo, ReadRom, WriteRom,
   ReadRam, WriteRam, BakeSave) so TUI/GUI front-ends build on the same
   tested code as the CLI.
 * core: per-OS serial port auto-detection (Linux `ttyACM*`/`ttyUSB*`,
   macOS `cu.usbmodem*`/`cu.usbserial*`, Windows `COM*`) with
   open/permission errors surfaced instead of swallowed.
 * release: self-contained single-file binaries for linux-x64,
   linux-arm64, osx-x64, osx-arm64, and win-x64, published automatically
   on `v*` tags with a `SHA256SUMS` file.
