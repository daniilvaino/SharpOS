# Step 76 — Phase C: physical ExitBootServices, own substrate survives (MILESTONE)

**Status:** Core proven, first try. SharpOS has never left UEFI before
— the entire Phase B off-ramp (own UART / GOP / PS-2 / font / shell)
was built so this would be survivable. It is. UEFI Boot Services were
physically torn down and the kernel kept running on its own substrate.

## What was done

Gated experiment `OS/src/Boot/ExitBootServicesProbe.cs`
(`Probes.ExitBootServicesExperiment`, committed **false** — never-
returning and destroys UEFI, same discipline as IdtPanic).

Load-bearing order:

1. **Reroute Console BEFORE the EBS call.** `Platform.UseOwnConsole()`
   flips `Platform.WriteChar` to the own 16550 (`Serial.WriteByte` —
   pure port I/O, valid without firmware) + `FbTty` (identity-mapped
   GOP MMIO). Without this, every post-EBS `Console.*` would fault on
   the dead UEFI ConOut and a silent triple-fault would look exactly
   like a truncated log.
2. **One last allocation.** Probe map size (`GetMemoryMap` with null),
   `AllocatePool` a generous buffer once — this is the final Boot
   Services allocation; it invalidates the map key, which is why the
   key is re-read *after* it.
3. **GetMemoryMap → ExitBootServices, retry on key change.** Up to 8
   attempts; no allocation between the `GetMemoryMap` and the matching
   `ExitBootServices(ImageHandle, mapKey)`.
4. **Post-EBS proof.** No Boot Services calls. Own-UART line, FB
   banner via the own GOP renderer, PS/2 STATUS read, then halt (the
   UEFI launcher cannot run without UEFI).

Plumbing: `EFI_BOOT_SERVICES.ExitBootServices` exposed from the
private `void*` (`delegate* unmanaged<IntPtr, ulong, ulong>`);
`BootInfo.ImageHandle` carried from `BootContext` via
`UefiBootInfoBuilder`. UEFI Runtime Services left untouched (still
valid post-EBS — ResetSystem etc.).

## Result (SHARPOS_GUI=1, serial + eyeball)

```
[ebs] console rerouted to own UART+FbTty
[ebs] ExitBootServices OK -- POST-EBS substrate LIVE
[ebs] direct own-UART line written after ExitBootServices
[ebs] ps2 status=0x1C present=Y
[ebs] halting (no UEFI launcher post-EBS)
```

Everything after `console rerouted` arrived through the OWN 16550
(not the UEFI ConOut mirror) and kept flowing past `ExitBootServices`.
The green "POST ExitBootServices" banner renders via the own GOP path
with firmware gone. PS/2 controller still answers (0x1C). No map-key
iteration needed, ABI matched the existing UEFI call convention, no
post-EBS faults.

## Lessons learned

- **Reroute the diagnostic channel before tearing down its backend.**
  The own UART is just port I/O — independent of UEFI — so it is the
  ideal post-EBS evidence channel, but only if Console points at it
  *before* EBS. This generalises the step-71/no-premature-claim rule:
  a silent triple-fault is indistinguishable from a truncated log, so
  guarantee the log channel survives the operation under test.
- **The map-key dance worked on the first GetMemoryMap because the
  last mutation was our own single AllocatePool** — ordering the one
  allocation before the key read removed the usual retry loop need
  (the retry is kept anyway, cheap insurance).

## Files

- `OS/src/Boot/{ExitBootServicesProbe,UefiTypes,BootInfo,UefiBootInfoBuilder,BootSequence}.cs`
- `OS/src/Hal/Platform.cs` (UseOwnConsole reroute)
- `OS/src/Kernel/Diagnostics/Probes.cs` (gate, committed false)

## Deferred / next

- Phase C remainder: UEFI calls behind interfaces
  (`IPlatformConsole/FS/Keyboard/Timer`), a memmap snapshot copy, and
  making post-EBS the *default* boot path (currently a gated
  experiment that halts; the launcher still assumes UEFI). The core
  blocker — *can we even survive EBS* — is now answered: yes.
- Per the DAG (B → D → E): Phase D, the SehUnwind §11 root (C#
  RtlVirtualUnwind port — unblocks every deferred 💥 census case), or
  resume the Roslyn path. Decide at the next step.

## Regression battery (unchanged, gate default-off)

own-UART PRESENT · `[fb] px0=0x123456` · `[fbtext] crc=0x7A1D4075
PASS` · `[ps2] PASS` · `[lined] PASS` · `[shell] PASS` · EH-gates
L15=1501/L16=1616/L17=1704 · hosted census OK=19 DEG=2 FAIL=20.
