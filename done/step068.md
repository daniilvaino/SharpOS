# Step 68 — Byte-for-byte normal .NET program on bare metal via stock CoreCLR (CLOSED)

**Status:** CLOSED. A completely-normal `dotnet build` assembly (NormalHello.dll —
NOT the `-nostdlib`/forkSPC hack) executed on SharpOS bare metal, hosted by the
stock CoreCLR fork. Log proof: `execute_assembly hr=0x0 exitCode=42`,
`=== NORMAL .NET PROGRAM EXECUTED (byte-for-byte) ===`, and the literal
`Hello from a normal .NET program running on SharpOS bare metal!` printed to
COM1 → QEMU `-serial stdio` → host terminal. Same exit 42 as on stock Windows.

This is the key intermediate milestone of the project (the user's stated goal:
"тот же файл байт в байт, полностью обычная программа из обычного dotnet").

## Контекст

Continuation of Phase 6.1 (CoreCLR в kernel image). Step 67 closed compile/link;
managed Hello on bare metal worked via the constrained path. Step 68 makes a
**stock** `dotnet build` DLL run unmodified. Two repos: kernel `c:/work/OS`
(branch `main`), fork `dotnet-runtime-sharpos` (branch `sharpos/coreclr-port`).

The user-supplied "B + all consumers" plan (7 points). This step lands S1–S3 +
the de-collide (point 6) + kernel32 P/Invoke surface.

## Что сделано

### S1 — boot/config (kernel)
- `run_build.ps1`: QEMU `-m 256→512`; ship 171 fork fx dlls + tpa.txt; build
  NormalHello via stock dotnet; exec buffer.
- `CoreClrProbe.cs`: 8 `coreclr_initialize` properties (TPA, APP path, GC
  Server/Concurrent=false, HeapHardLimit=64M, RegionRange=128M, RegionSize=1M,
  RetainVM=true); `coreclr_execute_assembly` path.
- fork `crt_imp_stubs.cpp`: real `_wcstoui64`/`strtoull` (GC INT config parse).

### S2 — unified VM manager (kernel)
- NEW `OS/src/Kernel/Memory/VirtualMemory.cs`: true reserve≠commit window;
  `Reserve` = VA bump (no backing); `Commit` = PhysicalMemory.AllocPage +
  X64PageTable.MapKernel into the ACTIVE firmware PML4.
- NEW `OS/src/PAL/SharpOSHost/VirtualMemoryHost.cs`: C-ABI SharpOSHost_VM*.
- `X64PageTable.cs`: `MapKernel` (roots at active s_kernelRootTable).
- `BootSequence.cs`: VM self-test in Phase3.

### S3 — reserve≠commit wiring + lower-half + demand-fault
- fork `crt_imp_stubs.cpp`: `VirtualAlloc`/`VirtualFree` → SharpOSHost_VM*
  (replaces commit-on-alloc kernel GcHeap path).
- `VirtualMemory.cs`: WindowBase moved higher-half→**lower canonical half**
  `0x0000500000000000` (CoreCLR write-barrier/JIT 32-bit-truncate higher-half →
  #GP). Added `TryDemandCommit`/`InWindow`: a not-present #PF inside the window
  is a lazy commit.
- `X64Asm.cs`: byte-emitted IRETQ-resume stub (`TryResumeFrame`/`EmitResume`)
  restoring GPRs from the InterruptFrame; `Idt.cs` demand-fault hook ahead of
  the panic path.

### BUG A (root cause + fix) — the breakthrough
Symptom: real `AppContext.s_dataStore` (and string comparers, UTF8 encoding)
allocated into kernel mark-sweep GcHeap (Heap A, `0x37E5B40`) instead of the
CoreCLR GC window (Heap B) → `IsHeapPointer=false` → `[VH]` spin / freeze.
Confirmed it was the real object (twin-test: `[s_dataStore@]==0x37E5B40`), not
an orphan.

Root cause (fork `vm/jitinterfacegen.cpp` `InitJITAllocationHelpers`): CoreCLR
**overrides** the default collision-free helpers with fast ones named
`RhpNewFast` (CORINFO_HELP_NEWSFAST) and `RhNewString` (FastAllocateString) —
**the exact names the kernel `[RuntimeExport]`s** (GcRuntimeExports.cs →
kernel GcHeap). In the linked kernel+CoreCLR image those symbols collide, so
hosted `newobj`/string bound to the kernel allocator → Heap A. Arrays were fine
(`RhpNewArrayFast` ≠ kernel `RhpNewArray`; default `RhpNew` collision-free).

Fix: `#if !defined(TARGET_SHARPOS)` around exactly the two colliding installs;
keep CoreCLR's collision-free defaults (`RhpNew`/framed string) → Alloc →
gc_heap → VM window. Validated: `[s_dataStore@]=0x500000B00208` (in window),
all `[VH]`/`[VHdump]`/`[VHdict]` gone, ran far past prior freeze. Plan point 6
closed. See memory `reference-rhpnew-symbol-collision`.

### kernel32 P/Invoke surface (fork crt_imp_stubs.cpp)
Windows-flavored framework P/Invokes `kernel32.dll`; loader file-loaded it,
failed, threw DllNotFound → PANIC. Added `SHARPOS_KERNEL32_HMODULE` sentinel +
`sharpos_is_kernel32` (kernel32/kernelbase/ntdll) + `LoadLibraryExW` branch +
comprehensive `sharpos_resolve_kernel32` (~90 in-image symbols). Mirrors the
existing advapi32 / libSystem.Native sentinel pattern.

### Diagnostics + noise (fork)
- Silenced `[sprintf]/[sprintf_s]/[snprintf_s]/[fprintf]` spam (kept buffer/
  return semantics).
- Investigation scaffolding still in tree (object.cpp/object.h `[VH]`/
  `[VHdump]`/`[VHdict]`/`SharpOS_OnObjMTStamp`; corelib.h
  `FIELD__APPCONTEXT__S_DATA_STORE`; corhost.cpp `[s_dataStore@]`;
  CrtHeapStubs.cs `[PEIMG]`). **Kept intentionally** — still useful for the
  pwsh push; removed in the 5b cleanup step.

### kernel GC guard
`GC.ReclamationDisabled` (GC.cs/GcSweep.cs) set before `coreclr_initialize`
(CoreClrProbe.cs). Originally chasing a misdiagnosed "corruption"; kept because
residual CoreCLR native `operator new`/`HeapAlloc` (the deferred 5b half) still
lands in kernel GcHeap and must not be reclaimed by the kernel mark phase
(which can't see CoreCLR roots). Reverted once 5b routes native heap off Heap A.

## Lessons learned

- **Don't dismiss invariant violations as "cosmetic."** The object-in-Heap-A
  was a real architectural bug even though it wasn't the immediate freeze
  cause; the user's insistence ("это явный баг, смотри сорцы") drove us to the
  real root. See memory `feedback-dont-dismiss-invariant-violations`.
- **Don't over-predict the next wall from a stale state.** "Bug B = finalizer
  deadlock" was extrapolated from a pre-fix freeze; after Bug A the run blew
  past it (single-thread GC + non-blocking WaitForSingleObject sufficed). The
  user correctly called this premature.
- **Look at the object, not around it.** The twin-test (`[s_dataStore@]`) and
  `[VHdict]` key/value dump cut a multi-day ambiguity in one run — the user's
  "посмотри в сами словари" was the decisive turn.
- Symbol collision in a single linked image (kernel `[RuntimeExport]` vs
  CoreCLR JIT-helper names) is silent and lethal — audit new exports.

## Файлы

Kernel (`c:/work/OS`, main): `OS/src/Kernel/Memory/VirtualMemory.cs` (new),
`OS/src/PAL/SharpOSHost/VirtualMemoryHost.cs` (new), `OS/src/Kernel/Paging/
X64PageTable.cs`, `OS/src/Hal/X64Asm.cs`, `OS/src/Hal/Idt/Idt.cs`,
`OS/src/Boot/BootSequence.cs`, `OS/src/Kernel/Diagnostics/CoreClrProbe.cs`,
`OS/src/PAL/SharpOSHost/CrtHeapStubs.cs`, `std/no-runtime/shared/GC/GC.cs`,
`std/no-runtime/shared/GC/GcSweep.cs`, `run_build.ps1`.

Fork (`dotnet-runtime-sharpos`, sharpos/coreclr-port):
`src/coreclr/vm/jitinterfacegen.cpp` (Bug A fix),
`src/coreclr/pal/sharpos/crt_imp_stubs.cpp` (VM shim, kernel32, sprintf,
_wcstoui64), `src/coreclr/vm/object.cpp`, `src/coreclr/vm/object.h`,
`src/coreclr/vm/corelib.h`, `src/coreclr/vm/corhost.cpp`.

## Что откладываем (5b + next)

- **5b cleanup:** route residual CoreCLR native `[crt] HeapAlloc` off kernel
  GcHeap onto a VM-window arena; then revert `GC.ReclamationDisabled`; strip
  the `[VH]/[VHdump]/[VHdict]/[MTSTAMP]/[s_dataStore@]/[PEIMG]` scaffolding.
- Optional optimization: wire CoreCLR `PInvokeOverride` for libSystem.Native/
  kernel32 to drop the LoadLibrary/GetProcAddress indirection (one-time cost;
  low ROI — the managed→native transition/marshalling is irreducible anyway).
- Framework flavor wart: System.Console is Unix-flavored while the rest is
  Windows-flavored — rebuild fx single-flavor to drop the dual sentinel.
- Threads: handle real thread dependencies empirically only when one actually
  blocks (no preemptive cooperative scheduler until proven needed).

## Next

S6 → the final goal: PowerShell (pwsh) on SharpOS. Foundation proven — stock
CoreCLR executes ordinary .NET programs on bare metal. Next: richer console
app (Stage B), then pwsh (Stage C).
