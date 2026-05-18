# Step 74 — Phase A clean-freeze + Phase B#1 UART + Phase B#2 framebuffer pillar (CLOSED)

**Status:** CLOSED for the framebuffer/serial fronts. Post-step-73
(PAL/OS census + the replanned Phase A–I DAG) we executed the head of
the new plan: the clean-milestone freeze (Phase A) and the
native-tier console off-ramp's display/serial half (Phase B#1 own
UART, Phase B#2 own GOP framebuffer). Phase B#3 (PS/2 keyboard →
line editor → native shell) is the remaining Phase B work and the
next front.

## What was done

### 1. Replan committed (Phase A, c35efd3)

`plan.md` rewritten as the Phase A–I DAG synthesising the two sage
replan responses (decision-table, critical path, SP1–SP6 stuck
points, SehUnwind ordering B→D→E). Invariants/architecture/correction
rules preserved.

### 2. Phase A — clean milestone freeze (0e9729e)

Per-frame EH/SEH trace gated behind `private const bool Trace = false`
(`CxxFrameHandler.cs`, `SehDispatch.cs`); comments rewritten from
"experiment comfort" to permanent mainline hygiene. EH pillar
regression preserved via Probes EH-gates + the 21/21 hosted battery.

### 3. Phase B#1 — own 16550 UART (a470ba0)

`OS/src/Hal/Serial.cs`: polled COM1 @0x3F8, 115200-8N1, FIFO, loopback
self-test, via `PortIo`. `SerialProbe` prints a status line (UEFI
ConOut mirror) **and** a proof line through the own driver straight to
the 16550. Pre-EBS both reach the same physical COM1, so both lines
appearing proves the post-EBS serial substrate works. Permanent
regression oracle (`Probes.SerialSmoke`).

### 4. Phase B#2 — own GOP framebuffer pillar (6c8f74b → 3a1a3ac)

End to end, four verify-before-commit sub-steps:

- **72b capture** — `UefiGop.TryCapture` snapshots the GOP linear FB
  while Boot Services are alive (`LocateProtocol`), into `BootInfo`.
  Rejects PixelBltOnly / no-FB. Captured: 1280×800, base 0x80000000,
  size 0x3E8000 (= 1280·800·4 exactly), stride 1280, fmt 1 = BGRX
  (OVMF std-vga). `run_build.ps1` gained `-vga std` in *both* headless
  and `SHARPOS_GUI=1` branches + a window+serial GUI mode.
- **72c map + dual-layer** — `OS.Hal.Framebuffer` identity-maps the
  FB-MMIO range into the pager PML4 (`VirtualMemory.MapFixed`, va==pa
  — above the 512 MiB RAM ceiling, clear of every VA window).
  Dual-layer per the bridge principle: AOT `OS.Hal.Framebuffer`
  (kernel renderer) + hosted export `SharpOSHost_GetFramebuffer`
  (future managed-JIT render engine P/Invokes it). `[fb] map+rw OK
  px0=0x123456` — write+readback through the mapping proven.
- **72d renderer + font** — `OS.Hal.Font8x8` (user-supplied
  public-domain Hepper/IBM 8×8 VGA font; `static readonly byte[]` is
  safe here — Phase 4 runs after GcStaticsMaterializer, same as
  CoreClrProbe.cs) + `OS.Hal.FbConsole` (format-aware Pack, Clear,
  FillRect, DrawChar/DrawString w/ integer scale + transparent bg,
  FNV-1a Checksum; every primitive clipped — the FB mapping has no
  slack). `FbRenderProbe` paints channel-order swatches + banner +
  full printable-ASCII strips. **BGRX eyeball-confirmed by the user**:
  swatches RED GREEN BLUE WHITE under `SHARPOS_GUI=1`.
- **72e self-checking oracle** — `FbRenderProbe` embeds
  `GoldenCrc = 0x7A1D4075` and emits `[fbtext] … crc=0x… PASS|FAIL
  exp=0x…`. The boot log now self-asserts the renderer instead of
  printing a number a human must compare. Region [0,512)×[0,360)
  covers all painted content (everything below y~360 is constant
  navy) → a sharper oracle than a full-frame hash diluted by
  unchanging background. Reported, not fatal (other-probes policy).

No double buffer: a boot console has no animation and the FB is
write-back cached, so direct draw is correct; back-buffer deferred
until tearing matters.

## Lessons learned

- **Build needs the VS dev env, but NOT vcvars.** `run_build.ps1`'s
  link wrapper calls bare `vswhere.exe`; my shell's PATH lacked the
  Installer dir → `link.exe exit 123`. Prepend ONLY
  `…\Microsoft Visual Studio\Installer` to PATH. `vcvars64.bat`
  exports `Platform=x64`, which redirects MSBuild output to
  `bin\x64\Release\…\publish\` while `run_build.ps1` (line 463) stages
  BOOTX64.EFI from the default `bin\Release\…` — a stale EFI boots,
  new code silently absent. Cost one full debug cycle.
- **`<unknown>:0: error: invalid symbol redefinition`** after
  "Generating native code" is **benign ILC objwriter noise** — it
  prints even on fully successful builds (publish + BOOTX64.EFI still
  produced). Proven by reproducing on a clean committed HEAD.
- **Never run the build/QEMU myself.** Headless QEMU sits at the
  LAUNCHER waiting for input forever, holding QMP port 4444; my
  background builds spawned zombies that blocked the user's runs.
  The user builds/runs; I write code and read logs. (Memory:
  feedback-never-run-build-or-qemu.)
- **Stale-log discipline.** A `last_build.log` mtime 3 s after a
  source edit cannot be that edit's build (ILC takes minutes) — it
  was the previous run. Check mtime before reading verification, not
  just content.

## Files

- `plan.md` (replan), `OS/src/PAL/SharpOSHost/{CxxFrameHandler,SehDispatch}.cs`
  (trace gate)
- `OS/src/Hal/Serial.cs`, `OS/src/Kernel/Diagnostics/SerialProbe.cs`
- `OS/src/Boot/{UefiGop,BootInfo,UefiBootInfoBuilder,BootSequence}.cs`
- `OS/src/Hal/{Framebuffer,Font8x8,FbConsole}.cs`,
  `OS/src/PAL/SharpOSHost/Framebuffer.cs`
- `OS/src/Kernel/Diagnostics/{FbRenderProbe,Probes}.cs`
- `run_build.ps1` (display branches)

## Regression battery (current green baseline)

own-UART PRESENT · `[fb] map+rw OK px0=0x123456` · `[fbtext]
1280x800 fmt=1 crc=0x7A1D4075 PASS` · EH-gates L15=1501/L16=1616/
L17=1704 · hosted census OK=19 DEG=2 FAIL=20.

## Deferred

- Framebuffer double buffer (no animation yet — not needed).
- Full-frame hash (region oracle is sharper; revisit only if
  off-region drawing appears).
- The single SehUnwind §11 root (all 💥 census cases) — large
  standalone step, scheduled Phase D after Phase B per the DAG.

## Next

Phase B#3 — PS/2 keyboard (0x60/0x64, scancode→KeyEvent) → line
editor → native-tier command shell (`help`/`mem`/`devices`/
`run-normalhello`/…). First interactive on bare metal; completes the
Phase B native-tier console off-ramp (insurance for the Roslyn path).
