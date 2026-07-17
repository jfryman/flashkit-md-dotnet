# flashkit-md-dotnet

Cross-platform (Linux / macOS / Windows) client for
[krikzz's FlashKit MD programmer](https://krikzz.com/our-products/accessories/flashkitmd.html)
— dump and flash Sega Mega Drive / Genesis cartridges. Ships as a CLI
(`flashkit-md`) and a desktop GUI (`flashkit-md-gui`).

This is a port of the original Windows-only C# WinForms client (v1.0.0.0,
preserved in `flashkit-md-src.zip`) to .NET 8. The serial protocol and
cartridge logic are ported verbatim from the original; the front-ends are
a CLI and an Avalonia GUI reproducing the original window. See
`docs/porting-plan.md` for how the port was staged.

> **All credit for the hardware and the original client goes to
> [krikzz](https://krikzz.com/)** — this project is only a port and would
> not exist without that work. Original C# client and hardware sources:
> [github.com/krikzz/flashkit](https://github.com/krikzz/flashkit) (MIT).
> Buy the programmer from
> [krikzz.com](https://krikzz.com/our-products/accessories/flashkitmd.html).

## Usage

```
flashkit-md [--port <serial-port>] <command> [file]

  info               print cart ROM name/size and save-RAM size
  read-rom [file]    dump cart ROM (default file: <ROM name>.bin)
      --trust-header dump the size the ROM header declares, not the
                     mirror-probed size (useful on flash carts)
  write-rom <file>   erase flash cart and write ROM image
      --no-flash-check   skip the CFI flash-presence check run before
                     erasing (also applies to bake-save)
  read-ram [file]    dump save RAM (default file: <ROM name>.srm)
  write-ram <file>   write save RAM from file

flashkit-md --version   print the build's version (release tag, or
                        tag-N-gSHA for branch/local builds)
```

The programmer is auto-detected by probing likely USB serial ports
(`/dev/ttyACM*`/`/dev/ttyUSB*` on Linux, `/dev/cu.usbmodem*`/`/dev/cu.usbserial*`
on macOS, `COM*` on Windows). Use `--port` to pin a specific port.

Dumps print an MD5 you can compare against the original Windows client's
output; `write-rom` and `write-ram` verify by reading back.

`flashkit-md-gui` is the same set of operations behind the original
client's five-button window (Cart info, Read/Write ROM, Read/Write RAM,
console log, progress bar). It has no command-line options; `bake-save`
and the erase/size overrides are CLI-only.

## Install

Grab a binary from the GitHub Releases page (published automatically for
each `v*` tag: `.tar.gz` for Linux/macOS, `.zip` for Windows, with a
`SHA256SUMS` file). Each archive holds both the CLI (`flashkit-md`) and
the GUI (`flashkit-md-gui`); macOS releases additionally ship the GUI as
a `FlashKit MD.app` bundle (`FlashKit-MD.app-*-osx-{x64,arm64}.zip`).
Or build from source (needs the .NET 8 SDK):

```
dotnet publish src/flashkit-md -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o artifacts/linux-x64
```

(`IncludeNativeLibrariesForSelfExtract` matters: without it the serial-port
native library lands next to the binary instead of inside it, and the
binary alone cannot open any port.)

or run `./publish.sh` to build all supported targets
(`linux-x64 linux-arm64 osx-x64 osx-arm64 win-x64`). The result is a single
dependency-free binary.

### Linux: serial port permissions

If you get "Access to the port ... is denied", either add yourself to the
serial group and re-login:

```
sudo usermod -aG uucp $USER      # Arch
sudo usermod -aG dialout $USER   # Debian/Ubuntu/Fedora
```

or install the udev rule in `packaging/99-flashkit-md.rules` (fill in your
programmer's VID/PID from `lsusb`).

### macOS: Gatekeeper

Binaries downloaded from the internet are quarantined. Either build from
source (no quarantine), or clear the flag:

```
xattr -d com.apple.quarantine ./flashkit-md
xattr -dr com.apple.quarantine "FlashKit MD.app"
```

(The binaries are ad-hoc signed, not notarized, so the first launch of
the `.app` may also need right-click → Open.)

### macOS: write-rom may hang on exit (fixed in v0.9.2)

In v0.9.1 and earlier, `write-rom` could hang after printing the final
`OK` (the FTDI driver's output drain wedges inside the serial close),
keeping the port locked. The flash write and verify have completed by
then — it is safe to Ctrl-C; if a later command reports the port as
denied, kill the leftover `flashkit-md` process. Upgrade to v0.9.2+ for
the fix.

## Notes on flash carts

`write-rom` erases only as much flash as the image needs (matching the
original client). If the cart previously held a larger ROM, the leftover
data above the new image stays on the chip — and a game with save support
may read it through the save-RAM window at `0x200000` and show "corrupted"
ghost save slots on console. Use `write-rom --full-erase` to wipe the whole
chip first (only on carts with a full-size 4 MB chip — on smaller chips the
upper address space mirrors the ROM and a full erase would corrupt it).

The FlashKit cart plays saves-capable games but cannot persist saves unless
its board actually has SRAM populated; `info` reporting `RAM size : 0B` on
the flash cart tells you saving won't work. As a workaround, `bake-save`
programs a save image (e.g. dumped from a real cart with `read-ram`) into
the flash at the save window: the game then sees those saves — loadable,
surviving every power cycle — but as a read-only snapshot it cannot
overwrite in-game.

## Development

```
./ci.sh    # restore + build (warnings as errors) + all tests, a few seconds
```

The project is a library-first design: all device workflows live in
`FlashKit.Core` and front-ends only render them — the CLI and the
Avalonia GUI build on the same tested code.

Layout:

- `src/FlashKit.Core/` — the library.
  - `Device`/`Cart`: serial protocol and cart logic, ported verbatim from
    the original client behind an `ISerialPort` seam; `DeviceConnector`
    for cross-platform port discovery.
  - `FlashKitSession`: the front-end API — `GetInfo`, `ReadRom`,
    `WriteRom`, `ReadRam`, `WriteRam`, `BakeSave`. Operations are
    synchronous, report progress via an `Action<OperationProgress>`
    callback (phase + done/total), throw `VerifyException` on read-back
    mismatches, and do no console or file I/O.
- `src/flashkit-md/` — the CLI: argument parsing, file I/O, and rendering
  over `FlashKitSession`.
- `src/FlashKit.Gui/` — the Avalonia GUI: the original client's window
  over `FlashKitSession`, operations on a worker thread.
- `tests/FlashKit.Core.Tests/` — wire-format tests locked to the original
  protocol, plus behavior/e2e tests against `FakeFlashKitDevice`, an
  in-memory emulation of the programmer firmware. CI never needs hardware;
  real-hardware validation is the manual checklist in
  `docs/hardware-validation.md`.
- `tests/FlashKit.Gui.Tests/` — headless GUI tests driving the window
  against the same fake device.

## Credits

The FlashKit MD hardware and the original client are by
[krikzz](https://krikzz.com/) and published at
[github.com/krikzz/flashkit](https://github.com/krikzz/flashkit) under the
MIT license. The original client source as distributed is also included
unmodified in `flashkit-md-src.zip`, and `Device.cs`/`Cart.cs` in this
repo are kept diffable against it. This port is likewise MIT-licensed
(see `LICENSE`), retaining krikzz's copyright notice.

Thanks also to [joeyparrish/flashkit-md-py](https://github.com/joeyparrish/flashkit-md-py),
an independent Python port of the same client.
