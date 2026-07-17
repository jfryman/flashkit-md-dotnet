# Hardware validation checklist (Stage 5)

Manual validation against the real FlashKit MD programmer — never a CI gate.
Run in this order (least- to most-destructive) and record results below.
Compare against dumps produced by the original Windows client where possible.

| # | Test | Linux | macOS | Windows |
|---|------|-------|-------|---------|
| 1 | `info` on a known cart — name/size/RAM match the original client | ✅ | ☐ | ☐ |
| 2 | `read-rom` — MD5 identical to a dump from the original client | ✅* | ☐ | ☐ |
| 3 | `read-ram` on a save cart, then `write-ram` round-trip | ✅ | ☐ | ☐ |
| 4 | `write-rom` to a FlashKit cart — verify passes, cart boots on console | ☐ | ☐ | ☐ |

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
