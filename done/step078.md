# Step 78 — MILESTONE: forked CoreCLR runs firmware-free (own FAT/AHCI/exec, post-EBS)

**Status:** Capability proven & committed (c17de5a). The §1 substrate
— the forked CoreCLR that hosts Roslyn/PowerShell — now boots with
UEFI **physically gone**, on a wholly own hardware/software stack,
passing the entire PAL/OS census byte-for-byte against the UEFI
baseline. The only remaining step is C4: flip this from a verified
gated experiment to the default boot.

## What was done (C-FS3 → bridge → own-exec → reorder)

- **OS.Hal.Fat32** (new): clean-room read-only FAT16/32 over
  `OS.Hal.Disk`. MBR *or* superfloppy mount with BPB validation
  (VVFAT presents either), FAT chain, 8.3 **and VFAT LFN** (assembly
  names are long), 16-sector bulk reads (single-PRDT proven path,
  ~16× fewer AHCI commands — the 23 MB CoreLib completes in-boot).
- **OS.Hal.Fs / OS.Hal.Vfs** (new): FS abstraction + mount/detection
  chain (FAT today; ext2/tar later = one probe). `Fat32Fs` adapter.
  Named `Fs` to stay distinct from the existing UEFI-backed
  `OS.Kernel.File.FileSystem`.
- **Platform.TryReadFile / FileExists bridge**: when a volume is
  mounted, serve from `Fs.Current`; else UEFI. Inert until mount, so
  zero default-boot change — the seam that replaces UEFI SimpleFS.
- **SharpOSHost.AllocExecutable**: post-EBS path = own RWX allocator
  (`PhysicalMemory` + pager identity-map) instead of UEFI
  `AllocatePages` — the JIT's last firmware dependency removed.
- **BootSequence reorder**: AHCI/FAT pulled off the pre-EBS Phase 4
  (issuing AHCI commands corrupts the UEFI FS firmware still uses —
  the step-86 launcher regression; fixed). CoreCLR session extracted
  to `RunCoreClrSession`; the pre-EBS run is skipped when the
  post-EBS path is on. `ExitBootServicesProbe`: post-EBS → mount FAT
  → AHCI/FAT/bridge oracles → CoreCLR via FAT → native shell.
- **Ahci** hardening (from C-FS2 bring-up): HPET-deadline polls +
  non-inlinable `Rd()` MMIO (defeats AOT loop-invariant hoist) +
  idempotent `Initialize`. `UefiFile.TryOpenRoot` guarded post-EBS.
- **Post-EBS end-state**: the native shell, not a halt dead-end —
  SharpOS *is* a usable firmware-free OS at that point.

## Verified (gate ON, headless)

Pre-EBS: `[fbtext]/[ps2]/[lined]/[shell]/[pci] PASS`,
EH L15=1501/L16=1616/L17=1704.
`[ebs] ExitBootServices OK` — UEFI gone.
Post-EBS: `[ebsx] uart=Y fb=PASS ps2=0x1C hpet=adv PASS`,
`[ahci] PASS`, `[fat] mount=Y FAT16 8.3+LFN PASS`,
`[fatbridge] Platform.TryReadFile via FAT sz=23576576 mz=Y PASS`,
`coreclr_execute_assembly(\sharpos\NormalHello.dll)`,
`=== PAL/OS census end: OK=19 DEG=2 FAIL=20 ===` — **every line
after ExitBootServices, no UEFI**. Identical census to the
UEFI-backed baseline → zero regression.

## Lessons learned

- **A driver that seizes a controller the live firmware owns must
  run post-EBS only.** AHCI bring-up pre-EBS corrupted the UEFI FS
  CoreCLR + the ELF launcher load through (step-86 regression I
  missed because the battery didn't include the launcher/census-
  presence — codified). PS/2 stayed non-destructive; AHCI can't, so
  it moved post-EBS. PCI config-space *read* is safe pre-EBS.
- **AOT (not JIT) hoists non-volatile MMIO out of poll loops.** ILC/
  RyuJIT LICM cached a port register across a spin; fixed with a
  NoInlining `Rd()` barrier — correctness by design, not an
  accidental call in the loop.
- **"Replace UEFI FS" is mostly one seam, not a rewrite.** The host
  contract is just `Platform.TryReadFile` (path→bytes). The real work
  was LFN + boot ordering + the own exec allocator, not API surface.

## Files

`OS/src/Hal/{Fat32,Fs,Vfs,Ahci,Platform}.cs`,
`OS/src/Boot/{BootSequence,ExitBootServicesProbe,UefiFile}.cs`,
`OS/src/Kernel/Diagnostics/{FatProbe,AhciProbe,Probes}.cs`,
`OS/src/PAL/SharpOSHost/Memory.cs`. Commit c17de5a (gate false).

## Deferred / next

- **C4 (final):** flip `ExitBootServicesExperiment` to the default
  boot — post-EBS becomes the normal path, the UEFI ELF launcher
  retires, SharpOS boots firmware-free by default. Capability is
  proven; this is the deliberate switch + cleanup.
- Perf: assemblies stream via 16-sector chunks; a larger
  multi-PRDT bulk read would speed big loads (optimization, not a
  blocker — the run completes).
- The single SehUnwind §11 root still gates the 💥 census cases
  (sockets/OpenSSL/threads) — unchanged, independent of firmware.
