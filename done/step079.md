# Step 79 — Phase C close-out: collision-proof FAT match, FETCH banner, diagnostics trim, csproj drift fix

**Status:** Phase C end-state landed. Post-EBS now boots into the
native launcher firmware-free, runs apps, and the launcher-self-relaunch
that used to **halt** instead returns a clean `unsupported`. Verified
gate-on by the user ("идеально"); gate flipped back to **false** for the
headless regression battery. Builds on the step-78 milestone (c17de5a);
C4 (make ExitBootServicesExperiment the default, retire UEFI launcher)
is the only remaining Phase-C item.

## 1. FAT name-match — collision-*impossible*, not just fixed

Symptom: launching `HELLOCS.ELF` from inside `HELLOCS.ELF` halted only
post-EBS; other apps fine. Root cause (driven to the bottom, not
checkpointed): `FindIn` fabricated an 8.3 key from the *requested* long
name (`Make83`) and `memcmp`'d it against directory entries. `Make83`
is lossy for non-8.3 names — `HELLOCS.ELF.abi` → `HELLOCS␠ELF`, which
**equals** the real `HELLOCS.ELF`'s stored 8.3 field. So the manifest
read returned the ELF's bytes, `"SABI"` parse failed, the loader fell
back to win64, and the managed launcher relaunch faulted.

The deeper wrong: a *reader* must never synthesize a key under the
writer's `~N` alias protocol — that protocol is the writer's private
business and unknowable from a long name.

Fix (per the user's directive — "нереальность попасться на коллизии,
скорость на втором месте"): drop `Make83`/`Name83Eq`/`Is83` entirely.
Per real directory entry, compare the request **only** against names
the entry actually stores on disk:
1. its reconstructed LFN (if present) — `LongNameEq`;
2. else its verbatim 8.3 short field — `Name83Out` → `LongNameEq`.

Both case-insensitive, length-checked first. No fabricated key, no
lossy prefilter ⇒ a wrong file cannot alias a right request regardless
of the writer's alias choice. The only residual collision would require
the volume itself to store two identically-named entries (corrupt FS).

## 2. FETCH banner — own-substrate console was ASCII-only

The codepoint survives the app ABI intact (`AppHost.WriteString` →
`WriteChar` path → `UiText.WriteChar((char)cp)`); the loss was the
post-EBS sink only (pre-EBS UEFI ConOut already rendered UCS-2):

- `Serial.WriteChar`: was `(byte)c` (one garbage byte) → now UTF-8
  encodes BMP codepoints + `\n`→CRLF; `Platform.WriteChar` post-EBS
  branch routed through it instead of the truncating `WriteByte`.
- `Font8x8`: added an `Ext` table (6 CP437-style box/block/shade
  glyphs: `U+2580/2584/2588/2591/2592/2500`) + `Row`/`ExtIndex`/
  `IsRenderable`. Source is pure ASCII (`(char)0xNNNN` constants,
  codepoints by number) so the build never depends on `.cs` encoding.
- `FbConsole.DrawChar`: indexes via `Font8x8.Row(ch,row)` (ASCII +
  box + `?` fallback). `FbTty.Putc` renders iff `IsRenderable`.
- `FbTty.Scale` 2 → 1 (true 8×8 pixel-for-pixel; 2× more text fits,
  fewer overflow-wipes for the banner + listing).

## 3. Diagnostics trim (per user)

FETCH std-demo block deleted; launcher `WriteResultBlock` silenced
(kernel's `---- child end ----` is the authoritative marker); the
`parent context restored` / `[trace] heap coalesce` / `[abimanifest]`
/ `[runext] enter|@build|@built|@jump` scaffolding removed. **Kept**
as real features/oracles: the `s_runExternalDepth` cap + clean
`nested app launch rejected (max depth 1)` line; FatProbe's
`[fatbridge]`/`[fatdir]` PASS/FAIL oracles.

## 4. Curated-csproj drift (regression surfaced by the rebuild)

Forcing FetchApp's first rebuild in months exposed latent rot in the
hand-maintained `<Compile>` lists of FetchApp/HelloSharpFs: the shared
`std/` had grown deps the app projects never picked up.
- `String(ReadOnlySpan<char>)` (added e462cd54, 2026-04-24) — split
  into `SystemString.Span.cs`, included only by the kernel (has Span);
  apps get a coherent Span-free `String` (verified nothing they
  compile calls the ctor).
- `MethodTable` → `OptionalFieldsReader` → `NativeFormatDecoder`:
  added both files to both app csprojs.

Lesson: `EnableDefaultCompileItems=false` projects bit-rot silently
against shared sources; only a rebuild reveals it. Resolve along the
dependency tree, not by guessing.

## Files

- `OS/src/Hal/Fat32.cs` — collision-proof `FindIn`; deleted
  `Make83`/`Name83Eq`/`Up`/`Is83`.
- `OS/src/Hal/Serial.cs`, `Platform.cs`, `Font8x8.cs`, `FbConsole.cs`,
  `FbTty.cs` — UTF-8 serial + box glyphs + 8×8 1:1.
- `OS/src/Kernel/Process/AppServiceBuilder.cs` — depth cap kept,
  scaffolding stripped.
- `OS/src/Kernel/Memory/KernelHeap.cs`, `Diagnostics/Probes.cs` —
  trace silenced; gate→false + comment honest.
- `apps/FetchApp/Program.cs`, `apps/HelloSharpFs/Program.cs` —
  std-demo / result-block removed.
- `std/no-runtime/shared/SystemString.Span.cs` (new),
  `SystemString.cs`, `OS/OS.csproj`,
  `apps/{FetchApp,HelloSharpFs}/*.csproj` — Span partition + GC deps.
- `OS/src/Hal/FatBootBridge.cs` (new) — FAT-backed BootInfo delegates.

## Deferred

- **C4**: flip `ExitBootServicesExperiment` to the default boot,
  retire the UEFI launcher path, close Phase C.
- App csproj drift is patched reactively; a shared-`std` "app surface"
  props file would prevent recurrence (not now).
