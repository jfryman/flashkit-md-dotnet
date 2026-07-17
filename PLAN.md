# Goal: Cross-platform port of FlashKit MD (Linux + macOS + Windows)

> **Status (2026-07-17):** Stages 0-4 complete. Stage 5: Linux and macOS
> columns fully validated on real hardware; Windows is next — see the
> Windows runbook in docs/hardware-validation.md. v0.9.1 fixed the
> v0.9.0 packaging bugs found during macOS validation (native libs
> embedded in the single file; osx binaries ad-hoc signed). v0.9.2 fixed
> the macOS write-rom exit hang (guarded serial close; hardware
> re-validated). Extras beyond the original plan: write-rom --full-erase,
> bake-save, and a library-first refactor (FlashKitSession) enabling
> future TUI/GUI front-ends (Stage 6).

Port krikzz's FlashKit MD programmer client (C# WinForms, .NET Framework 4.0)
to a cross-platform .NET 8 CLI that runs on Linux, macOS (Intel + Apple
Silicon), and Windows, preserving the original's serial protocol behavior.

The original source lives in `flashkit-md-src.zip` (unzip when needed for
reference and diffing; the pristine import is also in git history). The
three files that matter:

- `Device.cs` — serial protocol to the programmer (6 commands: addr, len,
  read, write, ready-wait, delay). Portable logic; only dependency is
  `System.IO.Ports`.
- `Cart.cs` — cart introspection: ROM name/region from header, ROM size via
  mirror probing, SRAM detect/size via the `0xA13000` bank register.
- `Form1.cs` — five-button WinForms UI (Cart info, Read/Write ROM, Read/Write
  RAM) + console log + progress bar. This is the only part that gets replaced.

## Ground rules

1. **Always green.** Every stage ends with local CI passing. No stage merges
   work that requires a later stage to compile or pass.
2. **Fast local CI.** One command (`./ci.sh`) runs restore + build
   (warnings-as-errors) + all tests. Target: < 60s cold, < 10s warm. No
   network beyond NuGet restore, no sleeps, no hardware.
3. **No hardware in CI.** All automated tests run against an in-memory fake
   of the programmer firmware. Real-hardware validation is a documented
   manual checklist (Stage 5), never a CI gate.
4. **Fidelity first.** `Device`/`Cart` logic is ported verbatim where
   possible. Behavior changes (bug fixes) are isolated commits with tests,
   so any hardware regression can be bisected to a single change.

## Stage 0 — Scaffolding and CI harness

- `git init`; commit the pristine extracted original source first so every
  later change is diffable against upstream.
- Solution layout:
  - `src/FlashKit.Core/` — protocol + cart logic (netstandard-free, net8.0)
  - `src/flashkit-md/` — CLI front-end (net8.0)
  - `tests/FlashKit.Core.Tests/` — xunit
- `.gitignore`, `.editorconfig`, `ci.sh` (restore, build `-warnaserror`,
  test). Optional GitHub Actions workflow that just calls `ci.sh` on
  ubuntu + macos + windows runners.
- **Exit criteria:** `./ci.sh` green with a placeholder test; runs in
  seconds.

## Stage 1 — Port the core behind a testability seam

- Introduce `ISerialPort` (open/close, read, write, timeouts) and a thin
  `SystemSerialPort` adapter over `System.IO.Ports` (NuGet package — works
  on Linux and macOS).
- Port `Device.cs` and `Cart.cs` into `FlashKit.Core` with logic verbatim,
  but taking `ISerialPort` instead of a static `SerialPort` field.
- Build `FakeFlashKitDevice : ISerialPort` — an in-memory emulation of the
  programmer firmware protocol: device-ID query, address/length registers
  with auto-increment, 8/16-bit reads/writes against a synthetic cart image
  (ROM with configurable size/mirroring, SRAM mapped at `0x200000` behind
  the `0xA13000` bank register, AMD flash command sequences for
  erase/program).
- Tests (all in-memory, no timing):
  - Exact command byte sequences emitted for `readWord`, `writeWord`,
    `read`, `write`, `flashErase`, `flashWrite`, unlock/bypass sequences —
    locked to the original's wire format.
  - `Cart` header parsing: name extraction (both `0x120`/`0x150` offsets,
    illegal-char fallback), region mapping.
  - ROM size probing against synthetic images (mirrored 512K, 1M, 2M, 4M,
    2M+SRAM, split-bank cases).
  - SRAM detect/size on images with and without RAM.
- **Exit criteria:** CI green; protocol layer fully covered by tests; core
  compiles with zero references to WinForms or `System.Windows`.

## Stage 2 — Cross-platform port discovery

- Replace the "open every port and probe" loop with per-OS candidate
  filtering before probing:
  - Linux: `/dev/ttyACM*`, `/dev/ttyUSB*`
  - macOS: `/dev/cu.usbmodem*`, `/dev/cu.usbserial*` (never `tty.*` —
    those block on open)
  - Windows: `COM*` (existing behavior)
- Injectable port enumerator so filtering logic is unit-testable with
  synthetic device lists per platform.
- Surface open/permission errors instead of swallowing them (Linux
  `dialout` failures must not read as "device not detected").
- **Exit criteria:** CI green; filter + error-surfacing tests pass on all
  three CI OSes.

## Stage 3 — CLI front-end

- Commands mirroring the five buttons: `info`, `read-rom <file>`,
  `write-rom <file>`, `read-ram <file>`, `write-ram <file>`; global
  `--port` override. Terminal progress bar, MD5 printout, console output
  matching the original's messages where sensible.
- Fix the file-handling bugs as isolated commits with tests:
  - truncate on dump (`File.Create`, not `File.OpenWrite`)
  - loop on `Stream.Read` (no silent short reads)
- In-process end-to-end tests: run each CLI command against
  `FakeFlashKitDevice`, assert dumped bytes/MD5, RAM round-trip, flash
  write + verify, and the failure paths (no device, verify mismatch).
- **Exit criteria:** CI green; e2e suite covers all five commands; still
  inside the CI time budget.

## Stage 4 — Packaging

- `dotnet publish` self-contained single-file for `linux-x64`,
  `linux-arm64`, `osx-x64`, `osx-arm64`, `win-x64`; wired into CI as an
  artifact step (build only — publishing releases stays manual).
- Linux: udev rule + `dialout` documentation.
- macOS: Gatekeeper notes — `xattr -d com.apple.quarantine` workaround and
  a Homebrew tap as the preferred distribution for the CLI.
- README covering install, permissions, usage per OS.
- **Exit criteria:** CI green and producing runnable artifacts for every
  RID.

## Stage 5 — Hardware validation (manual, out of CI)

Ordered least- to most-destructive; results recorded in
`docs/hardware-validation.md`:

1. `info` on a known cart — name/size/RAM match the Windows client.
2. `read-rom` — MD5 identical to a dump made with the original Windows
   build.
3. `read-ram` / `write-ram` round-trip on a save-cart.
4. `write-rom` to a FlashKit cart, verify pass, cart boots on console.
5. Repeat 1–2 on macOS (Intel or Apple Silicon) and Windows.

- **Exit criteria:** checklist recorded for Linux + at least one other OS;
  any discrepancy vs. the Windows client triaged (bisectable thanks to
  Stage 1's verbatim-port discipline).

## Stage 6 (optional, later) — Avalonia GUI

Thin Avalonia front-end over `FlashKit.Core` reproducing the original
window. Only start once Stage 5 is signed off; raises the macOS
signing/notarization question, so treat as a separate goal.

## Out of scope

- Mono compatibility for the original WinForms binary.
- Firmware changes; supporting other programmers (fkmd, flashkit-md-py
  hardware variants) beyond what the original supports.

## Prior art (reference only)

- https://github.com/joeyparrish/flashkit-md-py — Python CLI for the same
  hardware; useful cross-check for protocol details and Linux serial
  handling.
- https://github.com/grantek/fkmd — Go implementation.
