# Step 77 — Phase C1–C3: post-EBS hardened into a usable OS without UEFI

**Status:** C1/C2/C3 CLOSED. Step 76 proved SharpOS can physically
ExitBootServices and survive; this turns that experiment into a
self-checking, production-shaped path — culminating in a live
interactive shell with no firmware underneath. C4 (flip post-EBS to
the default boot) and the FS track remain.

## What was done

### C1 — self-checking [ebsx] oracle (commit 1c58daf)

Made the post-EBS proof headless-deterministic (prerequisite for
default-on). `FbRenderProbe` split into `RenderAndChecksum()` (paint
the deterministic frame, return region FNV-1a — no Console) and
`Verify()` (== golden, pure); `Run()` calls them, pre-EBS `[fbtext]`
unchanged. `ExitBootServicesProbe` post-EBS now a self-checked
`[ebsx]` line: own-UART loopback re-init, `FbRenderProbe.Verify()`
(**same golden 0x7A1D4075 ⇒ the own GOP path is bit-identical without
UEFI**), PS/2 STATUS present, HPET counter advances. Verified gate-off
(refactor safe) and gate-on (`[ebsx] uart=Y fb=PASS ps2=0x1C
hpet=adv PASS`).

### C2 — EBS-boundary hardening (commit 98ba2cb)

`Platform.BootServicesGone` (true once the console is rerouted off
UEFI = the EBS boundary). Guards: `UefiFile.TryOpenRoot` and
`SharpOSHost.AllocExecutable` refuse when set, rather than calling
into freed firmware. Inert on normal boot (flag false until the
Phase-4-tail reroute) — defense-in-depth for the post-EBS path.

### C3 — interactive OS without UEFI (commit 98ba2cb)

- `ShellOut.FbHere = ToFb && !BootServicesGone`. Post-EBS, Console
  itself routes to own UART+FbTty (the reroute), so the ShellOut FB
  mirror would draw every glyph twice (cursor double-advance →
  garbled). Suppressing it post-EBS gives one clean copy in both
  eras; pre-EBS behaviour unchanged.
- `FbTty.Clear()` = wipe + home cursor; the `clear` command uses it
  (bare `FbConsole.Clear` left the cursor mid-screen, output
  continued with a blank top).
- After `[ebsx] PASS`, if `ShellInteractive`, hand control to
  `Shell.RunInteractive()` on the own substrate (PS/2 + FbTty + own
  16550) instead of halting.

Eyeball-verified under SHARPOS_GUI=1: post-EBS, a live native shell
with UEFI gone — `mem` / `devices` (serial/fb/ps2/acpi all own) /
`echo` typed by hand, rendered by the own GOP renderer, single clean
copy. SharpOS self-hosts an interactive session on bare metal with no
firmware.

## Lessons learned

- **Verify the step's own deliverable, not just regression-safety.**
  The C1 gate-off run only proved the refactor didn't break the
  default path; the `[ebsx]` deliverable required a gate-on run.
  Committing on regression-safety alone was rejected — codified as
  feedback. Gate-off + gate-on are two distinct verifications for
  gated never-returning features.
- **Reroute composition trap.** Once Console is itself on the own
  substrate, a second mirror (ShellOut→FbTty) double-renders. When
  layering output sinks, gate each on whether a lower layer already
  owns the target.

## Files

`OS/src/Kernel/Diagnostics/FbRenderProbe.cs`,
`OS/src/Boot/{ExitBootServicesProbe,UefiFile}.cs`,
`OS/src/Hal/{Platform,ShellOut,FbTty,Shell}.cs`,
`OS/src/PAL/SharpOSHost/Memory.cs`.

## Recon for the FS track (next)

`gc-experiment/MOOS/` is **Unlicense / public domain** (compatible
with our CC0) — a rich C# kernel to adapt: `Kernel/Driver/PCIExpress.cs`
(ECAM, pure MMIO-pointer, ~0 native deps), `Kernel/FS/`
`Disk.cs`/`FATFS.cs`/`FAT32.cs`/`FileSystem.cs` (FAT RO over an
abstract `Disk.Read(sector,count,byte*)`). Take **.cs only** —
Invariant 1 still forbids its `NativeLib/` (io.cpp/asm) and `Tools/`
bootloader asm; every `Native.*` call remaps to our C# HAL
(`OS.Hal.PortIo`, pointer MMIO, `Mcfg` ECAM base, KernelHeap). Cosmos
(`gc-experiment/Cosmos/`) is NOT public domain — do not adapt from it;
the block backend (q35 = AHCI, no legacy IDE) will be an own
virtio-blk driver written from scratch.

## Regression battery (current, gates default-off)

own-UART PRESENT · `[fb] px0=0x123456` · `[fbtext] crc=0x7A1D4075
PASS` · `[ps2] PASS` · `[lined] PASS` · `[shell] PASS` · EH-gates
L15=1501/L16=1616/L17=1704 · hosted census OK=19 DEG=2 FAIL=20.
Gate-on (manual): `[ebsx] … PASS` + live post-EBS shell.

## Next

C-FS0..FS4 — own-substrate read-only FAT32 (virtio-blk + stolen MOOS
PCI/FAT), then C4 (post-EBS becomes the default boot path). Phase C
closes with a real filesystem, not just console+keyboard.
