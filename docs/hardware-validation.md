# Hardware validation checklist (Stage 5)

Manual validation against the real FlashKit MD programmer — never a CI gate.
Run in this order (least- to most-destructive) and record results below.
Compare against dumps produced by the original Windows client where possible.

| # | Test | Linux | macOS | Windows |
|---|------|-------|-------|---------|
| 1 | `info` on a known cart — name/size/RAM match the original client | ✅ | ☐ | ☐ |
| 2 | `read-rom` — MD5 identical to a dump from the original client | ✅* | ☐ | ☐ |
| 3 | `read-ram` on a save cart, then `write-ram` round-trip | ✅ | ☐ | ☐ |
| 4 | `write-rom` to a FlashKit cart — verify passes, cart boots on console | ✅ | ☐ | ☐ |

Notes / discrepancies:

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
