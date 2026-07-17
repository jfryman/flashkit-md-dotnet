# Hardware validation checklist (Stage 5)

Manual validation against the real FlashKit MD programmer — never a CI gate.
Run in this order (least- to most-destructive) and record results below.
Compare against dumps produced by the original Windows client where possible.

| # | Test | Linux | macOS | Windows |
|---|------|-------|-------|---------|
| 1 | `info` on a known cart — name/size/RAM match the original client | ✅ | ✅ | ☐ |
| 2 | `read-rom` — MD5 identical to a dump from the original client | ✅* | ✅* | ☐ |
| 3 | `read-ram` on a save cart, then `write-ram` round-trip | ✅ | ✅ | ☐ |
| 4 | `write-rom` to a FlashKit cart — verify passes, cart boots on console | ✅ | ✅* | ☐ |

## macOS validation runbook (for the agent running on the Mac)

Goal: fill the macOS column above. Items 1-2 are required; 3-4 are a bonus
if the right carts are inserted. Linux results below are the reference.

Setup:

1. Download the release binary (pick `osx-arm64` for Apple Silicon,
   `osx-x64` for Intel):
   `https://github.com/jfryman/flashkit-md-mono/releases/tag/v0.9.0`
2. `tar xzf flashkit-md-v0.9.0-osx-<arch>.tar.gz` (tar preserves the
   executable bit). If Gatekeeper blocks it:
   `xattr -d com.apple.quarantine ./flashkit-md`
3. Plug in the programmer. It appears as `/dev/cu.usbmodem*` or
   `/dev/cu.usbserial*`; no group membership or drivers are needed on
   macOS. Auto-detection scans those patterns; `--port <path>` pins one.

Validation steps (use the REAL game carts, not the FlashKit flash cart —
see the warning below):

1. `./flashkit-md info` with a known cart. Expected, matching Linux:
   - Action 52: `ACTION 52 (W)`, `ROM size : 4096K`, `RAM size : 0B`
   - Shining Force 2: `SHINING FORCE 2 (U)`, `ROM size : 2048K`,
     `RAM size : 8K`
2. `./flashkit-md read-rom check.bin` on the same cart. The MD5 must match
   the Linux reference exactly:
   - Action 52: `C4-C1-91-D4-F8-A9-F0-04-50-26-39-E5-CC-B9-54-80`
   - Shining Force 2: `64-73-B1-50-53-34-EF-56-20-D1-31-91-C1-82-51-FE`
3. (bonus) On the SF2 cart: `read-ram backup.srm`, then
   `write-ram backup.srm` (writes its own backup — non-destructive), then
   `read-ram again.srm` and compare MD5s; all three must match.
4. (bonus) `write-rom` a previously dumped image to the FlashKit flash
   cart and confirm the built-in verify passes. Destructive to the flash
   cart's contents — skip unless the user confirms.

Warning: the FlashKit flash cart currently holds SF2 plus a save snapshot
baked at 0x200000. Mirror-based size probing can report odd ROM sizes on
it (partially programmed flash has no mirrors), so use the real game carts
for the info/read-rom comparisons.

When done: fill in the macOS column of the table above, add a dated note
with the macOS version, arch, and port device name, and commit. If any
result diverges from Linux, record the exact output — do not "fix"
anything on the Mac; divergences get investigated on the dev machine.

## Windows validation runbook (for the agent running on the Windows box)

Goal: fill the Windows column above. Items 1-2 are required; 3-4 are a
bonus. Linux and macOS results below are the reference.

Setup:

1. Download `flashkit-md-v0.9.1-win-x64.zip` (NOT v0.9.0 — its archives
   have packaging bugs, see the macOS notes) from
   `https://github.com/jfryman/flashkit-md-mono/releases/tag/v0.9.1`
   and extract (`Expand-Archive`). If SmartScreen/mark-of-the-web blocks
   it: `Unblock-File .\flashkit-md.exe`.
2. Plug in the programmer. It uses an FTDI USB-serial chip; Windows
   installs the VCP driver automatically (Device Manager shows a
   `USB Serial Port (COMn)`). Auto-detection probes COM ports;
   `--port COMn` pins one.

Validation steps (use the REAL game carts, not a FlashKit flash cart —
see the warning below):

1. `.\flashkit-md.exe info` with a known cart. Expected, matching
   Linux/macOS:
   - Action 52: `ACTION 52 (W)`, `ROM size : 4096K`, `RAM size : 0B`
   - Shining Force 2: `SHINING FORCE 2 (U)`, `ROM size : 2048K`,
     `RAM size : 8K`
2. `.\flashkit-md.exe read-rom check.bin` on the same cart. The MD5 the
   CLI prints must match the reference exactly (cross-check with
   `Get-FileHash -Algorithm MD5 check.bin` — same hex, no dashes):
   - Action 52: `C4-C1-91-D4-F8-A9-F0-04-50-26-39-E5-CC-B9-54-80`
   - Shining Force 2: `64-73-B1-50-53-34-EF-56-20-D1-31-91-C1-82-51-FE`
3. (bonus) On the SF2 cart: `read-ram backup.srm`, then
   `write-ram backup.srm` (writes its own backup — non-destructive), then
   `read-ram again.srm` and compare MD5s; all three must match.
4. (bonus) `write-rom` a previously dumped image to a FlashKit flash
   cart and confirm the built-in verify passes plus an independent
   re-dump matches. Destructive to the flash cart's contents — skip
   unless the user confirms. Afterwards, check whether the process exits
   cleanly: on macOS it hangs in SerialPort.Close() after printing OK
   (see the macOS notes) — record whether Windows does the same.

Warning: both FlashKit flash carts currently hold Shining Force 2 and
report RAM 0B (SRAM-less carts). Mirror-based size probing can report odd
ROM sizes on partially programmed flash, so use the real game carts for
the info/read-rom comparisons.

When done: fill in the Windows column of the table above, add a dated
note with the Windows version and COM port name, and commit. If any
result diverges, record the exact output — do not "fix" anything on the
Windows box; divergences get investigated on the dev machine.

## Results

Notes / discrepancies:

- 2026-07-17, macOS 26.5.2 (Apple Silicon, arm64), programmer on
  /dev/cu.usbserial-A10MQJP4, release binary v0.9.0 osx-arm64.
- Release packaging bugs (not port bugs) blocked the binary from running
  at all; both fixed since (publish.sh embeds native libs via
  IncludeNativeLibrariesForSelfExtract; release.yml runs on macos-latest
  and ad-hoc signs the osx binaries) — needs a v0.9.1 tag to take effect:
  1. The binary is unsigned; macOS SIGKILLs unsigned arm64 executables
     (exit 137). Workaround: `codesign -s - ./flashkit-md`. The publish
     should ad-hoc sign osx binaries (or document the workaround).
  2. The tarballs contain only `flashkit-md`, but single-file publish
     leaves native libs beside the exe — `libSystem.IO.Ports.Native.dylib`
     is missing, so every serial open fails with a dlopen error. Affects
     all RIDs' archives, not just macOS (Linux validation below used the
     locally built artifacts dir, which is why it passed). Workaround:
     drop the dylib from the `runtime.osx-arm64.runtime.native.System.IO.Ports`
     NuGet package next to the binary. Fix: tar the whole publish dir or
     set `-p:IncludeNativeLibrariesForSelfExtract=true`.
- Item 1: Shining Force 2 reported `SHINING FORCE 2 (U)` / 2048K / 8K,
  matching Linux exactly. Blaster Master 2 (U) also detected sensibly
  (1024K / 0B).
- Item 2: SF2 dumped 2 MB in ~5 s,
  MD5 64-73-B1-50-53-34-EF-56-20-D1-31-91-C1-82-51-FE — identical to the
  Linux reference. (*) same caveat as Linux: not yet cross-checked against
  an original-client dump.
- Item 3: SF2 8K SRAM backed up, written back (built-in verify passed),
  re-dumped — all three MD5s identical
  (A4-B8-41-A4-2C-45-EC-BE-FC-F9-98-77-C8-98-4B-C8). Even bytes all 0xFF,
  data on odd bytes, as expected for 8-bit RAM.
- Item 4: SF2 dump flashed to a brand-new FlashKit cart (shipped preloaded
  with `Battle City Online (X)`, 2048K — backed up before overwrite).
  Erase + write + built-in verify passed; an independent re-dump is
  byte-identical to the source image (MD5 64-73-B1-50-...-51-FE). Like the
  other FlashKit cart, this one reports RAM 0B (SRAM-less). (*) console
  boot not re-tested from the Mac-flashed cart; the identical image was
  already boot-validated in the Linux run.
- macOS divergence — write-rom hangs on exit: after printing OK, the
  process never exits and keeps the port open. Sampled stack shows the
  main thread stuck in `tcdrain` (ioctl) under `SerialPort.Close()` on the
  FTDI usbserial driver; read-only commands exit cleanly, so it is
  write-volume related. Work completes and verifies fine — kill the
  process and carry on. Needs investigation on the dev machine (candidate
  fix: DiscardOutBuffer/skip drain on close, or close via the session
  layer with a timeout).
- Side note: Blaster Master 2 dumped twice byte-identically
  (MD5 DD-38-02-1F-F9-CB-67-CF-C2-4A-1A-D7-44-7E-4E-3E), but its embedded
  header checksum (0x2137) does not match the computed sum (0xA1F4). No
  reference dump was available to distinguish a shipped-bad checksum from
  a bad dump; the SF2 exact-match makes a dumper fault unlikely.

- 2026-07-17, Arch Linux, programmer on /dev/ttyUSB0.
- Item 1: Action 52 (W) detected as 4096K ROM / 0B RAM; Sonic 3 (U) detected
  as 2048K ROM / 512B RAM (matches its 512-byte FRAM).
- Item 2: Action 52 dumped 4 MB in ~8 s,
  MD5 C4-C1-91-D4-F8-A9-F0-04-50-26-39-E5-CC-B9-54-80.
  (*) not yet cross-checked against an original-client dump of the same cart.
- Item 3: Sonic 3 save RAM backed up (512 words, 0xFF on even bytes as
  expected for 8-bit RAM), written back, re-dumped — all three MD5s identical
  (A8-D0-A3-45-52-A8-DF-FA-CC-FC-1F-4B-A1-F8-EB-3C).
- Item 4: Action 52 dump (4 MB) flashed back to the FlashKit cart in ~72 s;
  built-in verify passed and an independent re-dump matched the source MD5
  exactly. Shining Force 2 (2 MB) then flashed over it in ~36 s, verified,
  and re-dumped byte-identical to the donor cart dump. (*) console boot
  test: game boots and runs; see ghost-save note below.
- FlashKit cart finding #1: this cart has no save RAM. The probe (which
  correctly finds Sonic 3's 512B FRAM and SF2's 8K SRAM) reports nothing
  writable at 0x200000 under any bank-register value (0x0001/0x0003/0xFFFF,
  word at 0xA13000 and byte at 0xA130F1). Games play but cannot save.
- FlashKit cart finding #2 — "ghost saves": write-rom (like the original
  client) erases only as much flash as the image needs. Flashing 2 MB SF2
  over 4 MB Action 52 left stale Action 52 data at 0x200000+, which SF2
  reads as its save area on console and renders as corrupted save slots.
  Confirmed by matching the stale bytes against the Action 52 dump at
  0x200000. Fixed manually by erasing 0x200000-0x400000; the cart then
  dumps byte-identical to the donor cart and reports 2048K again.
- Console boot test: SF2 boots and runs from the FlashKit cart. After
  programming the donor cart's .srm byte layout into flash at 0x200000
  (odd-byte data / 0xFF even bytes — matches erased flash), the console
  shows the real saves from the donor cart, validating dump -> flash ->
  console byte fidelity end to end. Saves are a read-only snapshot on
  this SRAM-less cart: the game's plain writes to the save window carry
  no flash command sequence, so nothing persists across power cycles.
