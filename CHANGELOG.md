# Changelog

## Unreleased

BUG FIXES:

 * gui: closing the app on Linux no longer crashes with an unhandled
   TaskCanceledException. Avalonia's DBus teardown could race dispatcher
   shutdown (AvaloniaUI/Avalonia#19523); the shared DBus connection is now
   disposed on the UI thread as soon as the application lifetime returns,
   before process teardown can race it.

## 1.9.2 (July 20, 2026)

IMPROVEMENTS:

 * core: code-quality pass. The full .NET analyzer set (latest-recommended,
   with code style enforced in build) now runs on every build with warnings
   as errors; a cart without save RAM now raises a dedicated exception type
   (the error message is unchanged); transaction timestamps format
   culture-invariantly regardless of system locale.
 * release: build settings are shared via Directory.Build.props and NuGet
   versions managed centrally via Directory.Packages.props; the test stack
   updated to current stable (xunit 2.9.3, runner 3.1.5, Test.Sdk 18.8.1,
   coverlet.collector 10.0.1). No change to the shipped binaries' behavior.

## 1.9.1 (July 20, 2026)

IMPROVEMENTS:

 * gui: accessibility. The colour-only device/cartridge status dots are
   hidden from screen readers (their meaning is in the adjacent text), the
   status line is a live region so connect/disconnect is announced, the
   per-operation progress bar and result line are labelled, and the action
   buttons gain access-key mnemonics (Alt+R read ROM, Alt+W write ROM,
   etc.). Long auto-dump/-write/patch paths get a hover tooltip with the
   full value.

## 1.9.0 (July 20, 2026)

IMPROVEMENTS:

 * release: the published binaries are now trimmed, cutting download size
   by ~60% (the CLI from ~39 MB to ~13 MB, the GUI from ~48 MB to ~21 MB)
   while staying fully self-contained single files.
 * gui: XAML bindings are compiled rather than reflection-resolved —
   faster, and safe under trimming.
 * cli: `write-rom` now prints the flashed image's CRC32/MD5/SHA-1, matching
   the other read/write commands and the GUI/TUI, so you can confirm what
   was written against a known-good checksum.
 * cli: `--help`/`-h` print usage to stdout and exit 0, instead of only
   showing usage on stderr with an error exit code when no command is given.
 * tui: transaction cards are now only as tall as their status needs — a
   running or failed entry is compact, and a completed dump/write grows to
   fit its CRC32/MD5/SHA-1 lines, instead of every card reserving room for
   the hash block.
 * tui: the left control column scrolls inside its own pane and shows a
   scrollbar when the terminal is too short to hold every panel, so the
   status bar (and a new key-hint line: Tab / scroll / Enter / Ctrl+Q) is
   always visible instead of being pushed off-screen on ~24-row terminals.
   Tabbing to a control below the fold scrolls it into view.

BUG FIXES:

 * tui: long file paths in transaction cards are middle-ellipsized so the
   filename stays visible, instead of being truncated on the right and
   losing the name.

## 1.8.1 (July 20, 2026)

BUG FIXES:

 * cli: `--help`/usage descriptions now line up in a single column;
   flags that take an argument (`--apply-patch <ips>`, `--create-patch
   <base>`, `--no-flash-check`) put their description on the next line
   instead of pushing it out of the column.

## 1.8.0 (July 20, 2026)

FEATURES:

 * core, cli, gui, tui: IPS patching. Apply a patch when flashing
   (`write-rom --patch <ips>`, or the GUI/TUI "IPS patch" panel's Apply
   toggle) and when dumping (`read-rom --apply-patch <ips>`, same toggle);
   create a patch by diffing a dump against a base ROM
   (`read-rom --create-patch <base>`, or the panel's "Create patch"). The
   IpsPatch core (apply + create, literal/RLE records, the truncation
   extension) is fully unit-tested including a create/apply round-trip
   over random fuzzing, and validated on hardware against Ghostbusters +
   the Special Edition patch. Build scripts moved to eng/.

IMPROVEMENTS:

 * cli, gui, tui: dumps and writes now report CRC32 and SHA-1 alongside
   MD5, in the compact uppercase hex form No-Intro / romhacking.net
   quote, so a dump can be checked against a database.

## 1.7.0 (July 20, 2026)

FEATURES:

 * core, cli, gui, tui: cartridges are now identified as Sega 32X vs
   Mega Drive/Genesis from the header's 0x100 system field (a 32X cart
   contains "32X" there — verified on real DOOM and Kolibri carts). The
   `info` command and both UI cart panels show a System line, and ROM
   dumps of a 32X cart are named `.32x` instead of `.bin` (direct dumps
   and auto-dumps alike). Note: the header does not reliably distinguish
   "Mega Drive" from "Genesis" branding — both strings appear on the
   same games — so they are reported as one system.
 * core, cli, gui, tui: cart info now includes the Region (World / USA /
   Japan / Europe) parsed from the header's 0x1F0 field, shown as a line
   in `info` and a row in both UI cart panels.

## 1.6.1 (July 20, 2026)

BUG FIXES:

 * tui: a direct Read ROM/RAM dump failed with "Access to the path … is
   denied" when the save location was chosen by navigating into a folder
   — the dialog then hands back the directory, and the dump was written
   to it. The suggested filename is now saved into the chosen directory
   (auto-dumps, which build the path themselves, were never affected).
 * tui: opening the file picker for Read ROM/RAM corrupted the screen
   (garbled title bar, stray borders). The picker's operation reaches
   the dialog through a background-thread continuation, and running the
   dialog's nested event loop off the main-loop thread mangled drawing;
   dialogs now run through Application.Invoke on the main loop.
 * tui: the cartridge status in the bottom bar sat at a fixed column and
   overlapped the device status when the port name was long (macOS
   `/dev/cu.usbserial-…`); it now flows after the device text with
   padding, like the GUI status bar.
 * tui: buttons no longer cast Terminal.Gui's default drop shadow, which
   rendered as stray black cells after every button and bled onto the
   labels beneath the chooser buttons; Read/Write button pairs are
   padded to a uniform width so their brackets line up.

IMPROVEMENTS:

 * tui: the device/cartridge status line at the bottom now sits in a
   bordered bar with left padding, matching the GUI's status bar.
 * tui: Tab now walks every interactive element across all panels,
   including the transaction list on the right — Terminal.Gui's frames
   default to group navigation, which trapped Tab inside the current
   panel (the F6 group-hop key being the only way out).
 * tui: status indicators are now colored with the GUI's palette —
   green/gray device and cartridge dots, and per-card outcome bubbles
   (amber running, green succeeded, red failed, gray cancelled) with
   failed status text in red. The palette lives in the shared
   presentation layer, so the two front-ends cannot drift apart.
 * tui: transactions are now bordered cards like the GUI's log entries —
   time, outcome glyph, and title in the border, the file path, the
   entry's OWN progress bar, and the full status line (complete MD5s,
   no more one-line truncation). The global bottom progress bar is
   gone; the newest card scrolls into view when an operation starts,
   and arrow/page keys scroll the card list.

## 1.6.0 (July 20, 2026)

BUG FIXES:

 * gui/tui: a permission-denied serial port no longer masquerades as
   "No programmer detected" — the status bar now names the denied port
   and points at the serial group ('dialout', 'uucp' on Arch) fix. The
   CLI always reported this; the interactive front-ends swallowed it.

FEATURES:

 * tui: new `flashkit-md-tui` terminal front-end (Terminal.Gui) — the
   GUI's keyboard-first sibling for SSH sessions, with the same live
   device/cart status, cart info panel, transaction log with progress,
   auto-dump/auto-write panels, and destructive-action warning. Ships in
   the release tarballs, the Windows zip, and the Flatpak (run it with
   `flatpak run --command=flashkit-md-tui io.github.jfryman.FlashKitMD`)
   alongside the CLI and GUI.

IMPROVEMENTS:

 * core: interactive behavior (held-session lifetime, poll state machine,
   auto actions, transaction log) extracted from the GUI into a shared
   FlashKit.Presentation library; the GUI and TUI are now thin adapters
   over the same ProgrammerModel, so they behave and word things
   identically.
 * release: toolchain moved to .NET 10 LTS (net8 support ends November
   2026); release binaries remain self-contained so nothing changes for
   users.
 * gui: Avalonia updated 11.3.2 -> 11.3.18, clearing a known
   vulnerability (GHSA-xrw6-gwf8-vvr9) in the transitive
   Tmds.DBus.Protocol dependency.
 * release: publish.sh now cleans each RID's output directory first, so
   stale files from older publishes cannot trip the stray-native-lib
   check or ship by accident.

## 1.5.4 (July 18, 2026)

IMPROVEMENTS:

 * release: Flatpak assets are named by RID like every other Linux asset
   (`flashkit-md-vX.Y.Z-linux-{x64,arm64}.flatpak` instead of
   `-{x86_64,aarch64}.flatpak`), so they sort next to the tarballs on the
   release page.

## 1.5.3 (July 18, 2026)

IMPROVEMENTS:

 * release: the Flatpak now installs on the conventional `stable` branch
   instead of flatpak-builder's default `master`, so `flatpak update`
   tracks future releases; the version itself is reported by the
   AppStream metadata (`flatpak info`).

BUG FIXES:

 * release: the Flatpak failed to start on Wayland desktops
   ("XOpenDisplay failed"): the sandbox granted X11 only as a fallback
   when Wayland was absent, but Avalonia is X11-only and needs XWayland.
   The manifest now grants the X11 socket unconditionally.

## 1.5.2 (July 18, 2026)

FEATURES:

 * release: the Flatpak bundle is now also built for aarch64
   (`flashkit-md-vX.Y.Z-aarch64.flatpak`), natively on GitHub's arm64
   runners.

## 1.5.1 (July 18, 2026)

FEATURES:

 * release: releases now ship a Linux Flatpak bundle
   (`flashkit-md-vX.Y.Z-x86_64.flatpak`, app ID
   `io.github.jfryman.FlashKitMD`). The manifest is Flathub-ready and CI
   builds the bundle on every push so it cannot rot between releases.
 * release: macOS binaries and app bundles are Developer ID-signed with
   the hardened runtime, notarized, and stapled, and Windows executables
   are Authenticode-signed, when the corresponding repository secrets are
   configured (see docs/RELEASING.md). Without the secrets, releases ship
   ad-hoc-signed/unsigned exactly as before.

IMPROVEMENTS:

 * docs: README.md now targets users — install from release artifacts
   per platform, GUI and CLI usage, screenshot — while building,
   architecture, and release process moved to DEVELOPING.md.

## 1.5.0 (July 17, 2026)

FEATURES:

 * gui: the window now tracks device and cartridge state live — a status
   bar shows whether the programmer is detected (and on which port) and
   whether a cartridge is seated, and cart details (name, ROM/RAM/header
   sizes) auto-refresh every 2 seconds into a structured info panel, so
   the manual "Cart info" button is gone. An empty slot reads
   "No cartridge" instead of "Unknown (X) / 0K".
 * gui: auto-dump — tick "Dump ROM" (and optionally "Dump RAM") and pick
   a folder, and every newly inserted cartridge is dumped there
   automatically, named after the cart header. Existing files are never
   overwritten (a " (2)" suffix is added); reinserting or swapping carts
   triggers a fresh dump, and RAM dumping skips carts without save RAM.
 * gui: auto-write for development loops — tick "Write ROM", acknowledge
   the destructive-action warning (suppressible via "Don't show this
   warning again"), pick an image, and every flash cartridge inserted
   while enabled is erased and reprogrammed with it. Retail (mask ROM)
   carts are detected and skipped by the flash-chip check. Auto-write
   and auto-dump are mutually exclusive; enabling one disables the
   other's checkboxes.
 * gui: the console text dump is replaced by a transaction log: every
   ROM/RAM read/write becomes a log entry with timestamp, file path, its
   own inline progress bar, a status bubble (amber running, green
   success, red failure, gray cancelled), and the outcome (size, MD5, or
   the error) — cancelled pickers are logged too. The global bottom
   progress bar is gone.

IMPROVEMENTS:

 * gui: the status bar now sits along the bottom of the window and reports
   cartridge presence ("Cartridge inserted" / "No cartridge") instead of
   repeating the cart name shown in the info panel.
 * cli, gui: suggested filenames for read-rom/read-ram no longer carry the
   ROM header's internal space padding — runs of spaces collapse to one
   (Sonic 3 now suggests "SONIC THE HEDGEHOG 3 (U).bin" instead of
   "SONIC THE               HEDGEHOG 3 (U).bin"). The cart-info display
   still shows the header name verbatim.

BUG FIXES:

 * gui: on macOS the programmer appeared permanently disconnected after
   writing a ROM, until the adapter was physically replugged. Closing the
   port after a multi-MB flash write wedges in the FTDI driver's drain;
   the guarded close abandons it, which is fine for the CLI (process exit
   reclaims the descriptor) but left the long-lived GUI process holding
   the port, so every reconnect failed. The GUI now keeps one session
   open for as long as the programmer is reachable instead of
   reconnecting per operation, deferring the close to window close or
   device loss.

## 1.4.1 (July 17, 2026)

BUG FIXES:

 * release: `publish.sh` exited nonzero after a successful single-RID
   publish (as in the CI publish matrix) because the final `ls` listing
   included an `.app` glob that only matches when the macOS targets were
   built in the same run. This failed every CI publish job since v1.3.0;
   release builds were unaffected because they publish all targets
   together.

FEATURES:

 * cli: `--version` prints the version stamped into the build.

IMPROVEMENTS:

 * release: binaries, the GUI window title, and the .app bundle are now
   stamped with the version being built — the bare tag on releases,
   `git describe` output (`tag-N-gSHA[-dirty]`) on branch and local
   builds, `dev` when built outside publish.sh.
 * gui: the macOS menu bar and Dock now say "FlashKit MD" instead of
   Avalonia's default "Avalonia Application".

## 1.3.0 (July 17, 2026)

FEATURES:

 * release: macOS releases now ship the GUI as a signed `FlashKit MD.app`
   bundle (`FlashKit-MD.app-vX.Y.Z-osx-{x64,arm64}.zip`) with the
   original client's icon, assembled by `packaging/macos/make-app.sh`.

BUG FIXES:

 * release: the macOS GUI binary in the v1.2.0 tarballs crashed on
   launch anywhere but the build tree — the macOS single-file bundler
   silently leaves the pre-signed Skia/HarfBuzz/AvaloniaNative dylibs
   next to the binary, and the tarballs dropped them (same class of bug
   as v0.9.0's serial lib). The GUI now embeds all content on macOS,
   and `publish.sh` fails if any native library is left beside a binary
   so this cannot ship silently again. Linux and Windows GUI binaries
   were unaffected.

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
