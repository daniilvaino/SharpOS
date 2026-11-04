# Famidash — NES homebrew game

**Upstream:** https://github.com/tfdsoft/famidash
**Snapshot:** "Famidash - Huge Man 1.3.0 beta 6-20-26" (build of 2026-06-20), the
file as published on the project's itch.io page and downloaded 2026-11-04 —
newer than the last GitHub release, Demo5 (2024-07-14), which this replaced
(step249).
**License:** MIT (see [LICENSE](LICENSE), from the repository) — © 2023-2024 Team
Famidash
**Taken:** the ROM, as `famidash.nes` (2359312 bytes, sha256
`8ebd9b7ee378a5ce41fc4d25fdddc0d3ed98dc4e4da8da9ed2570454cf94fff8`), NES 2.0 header,
mapper 4 (MMC3), 2 MiB PRG (beyond the original MMC3's 512 KiB: both emulators
take the full 8-bit bank register), 256 KiB CHR, 8 KiB battery-backed PRG RAM.
**Not taken:** the source tree — the ROM is built upstream; nothing here rebuilds it.

A demake of Geometry Dash for the NES; the "Huge" build carries every level and
song. Its credits name the music's original composers (ForeverBound, DJVI,
Waterflame, DJ-Nate, F-777, …). Staged as `\games\FAMIDASH.NES`; both emulators
open it (`TRICNES.EXE`/`FAMI.EXE` with the path, or from its tile in the launcher).

Known: in Fami a tap of ~100 ms on the first screen ("Save file safety — press
any button") is not taken; a held button is. TriCNES takes the tap. The cause is
in Fami's emulation, not found yet.
