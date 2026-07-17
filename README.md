# flashkit-md

Cross-platform (Linux / macOS / Windows) command-line client for
[krikzz's FlashKit MD programmer](https://krikzz.com/our-products/accessories/flashkitmd.html)
— dump and flash Sega Mega Drive / Genesis cartridges.

This is a port of the original Windows-only C# WinForms client (v1.0.0.0,
preserved under `src-extracted/`) to .NET 8. The serial protocol and
cartridge logic are ported verbatim from the original; the GUI is replaced
by a CLI. See `PLAN.md` for the porting approach and current status.

## Usage

```
flashkit-md [--port <serial-port>] <command> [file]

  info               print cart ROM name/size and save-RAM size
  read-rom [file]    dump cart ROM (default file: <ROM name>.bin)
  write-rom <file>   erase flash cart and write ROM image
  read-ram [file]    dump save RAM (default file: <ROM name>.srm)
  write-ram <file>   write save RAM from file
```

The programmer is auto-detected by probing likely USB serial ports
(`/dev/ttyACM*`/`/dev/ttyUSB*` on Linux, `/dev/cu.usbmodem*`/`/dev/cu.usbserial*`
on macOS, `COM*` on Windows). Use `--port` to pin a specific port.

Dumps print an MD5 you can compare against the original Windows client's
output; `write-rom` and `write-ram` verify by reading back.

## Install

Build from source (needs the .NET 8 SDK):

```
dotnet publish src/flashkit-md -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -o artifacts/linux-x64
```

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
```

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

Layout:

- `src/FlashKit.Core/` — serial protocol (`Device`), cart logic (`Cart`),
  port discovery. Ported verbatim from the original client behind an
  `ISerialPort` seam.
- `src/flashkit-md/` — the CLI.
- `tests/FlashKit.Core.Tests/` — wire-format tests locked to the original
  protocol, plus behavior/e2e tests against `FakeFlashKitDevice`, an
  in-memory emulation of the programmer firmware. CI never needs hardware;
  real-hardware validation is the manual checklist in
  `docs/hardware-validation.md`.

## Credits

The FlashKit MD hardware and the original client are by
[krikzz](https://krikzz.com/). Original source is included unmodified under
`src-extracted/`.
