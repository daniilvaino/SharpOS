# Step 72 — Frontier-B closed: reflection-mode System.Text.Json byte-for-byte on bare metal (CLOSED)

**Status:** CLOSED. The frontier step 71 explicitly deferred
("Что дальше — Frontier B": reflection-mode `System.Text.Json` hard-#GP)
is root-caused and fixed. Two independent roots (B: GC root-reporting;
C: stack size), plus three supporting PAL fixes. Result: a **stock
`dotnet build` program doing reflection-mode `JsonSerializer`
Serialize/Deserialize/JsonDocument byte-for-byte on SharpOS bare metal**:

```
[B] all ops OK
[t] OK    System.Text.Json roundtrip
=== coverage battery end: pass=21 fail=0 ===
execute_assembly hr=0x0 exitCode=42
=== NORMAL .NET PROGRAM EXECUTED (byte-for-byte) ===
```

No HW fault, no panic, clean kernel finish (only the pre-existing
intentional boot EH-filter `#GP` probe, expected, handled).

## Symptom

Post-step-71 the reflection-mode `System.Text.Json roundtrip` test
hard-faulted deep in `JsonSerializer.Serialize<Person>` (managed stack:
`NullabilityInfoContext` → `RuntimeType.GetCustomAttributesData` →
`RuntimeCustomAttributeData..ctor` → `RuntimeType.GetFields` →
`MemberInfoCache.PopulateFields` → `RuntimeType+ListBuilder\`1.ToArray()`).
A bad `OBJECTREF` reached `Object::Validate` → `object.cpp:255`
`pMT && pMT->Validate()` → `FailFast` (`TerminateProcess 0x80131506` =
`COR_E_EXECUTIONENGINE`, uncatchable). The bad ref was
**non-deterministic** across runs (`0x8000E32` token-shaped one run,
`0x1FE94788` stack-band another) — false leads (mdtParamDef-token,
`dn_simdhash` cache (a *phantom symbol* from symbolizing the wrong
binary — coreclr is statically linked into the kernel image, must
symbolize `OS/.qemu/esp/EFI/BOOT/BOOTX64.EFI`), stale-heap/GC-collected,
PE-layout) were each falsified by direct one-run discriminators.

## Root B — GC reports a value-type-local int slot as OBJECTREF

A scaffold discriminator (`StackStructProbe<T>` mirroring
`RuntimeType.ListBuilder<T>`'s `{T[]? ; T? ; int ; int}` layout, mutated
+ alloc-pressure + `GC.Collect()`, **no reflection, no JSON**)
reproduced it → a `#PF`, call chain (symbolized vs the real
BOOTX64.EFI image):

```
Object::GetGCSafeMethodTable()                       object.h:472   ← #PF deref garbage
Object::ValidateInner()                              object.cpp:593
Object::Validate()                                   object.cpp:573
TGcInfoDecoder<AMD64GcInfoEncoding>::ReportStackSlotToGC(…)   gcinfodecoder.cpp
```

A throttled, signature-gated probe in `ReportStackSlotToGC` then proved
the mechanism in one run:

```
[GCSLOT] spBase=GC_FRAMEREG_REL spOff=-72 SP=0x1FE95C10 sbReg=5(RBP)
         FR=0x1FE95C00  ctxRbp=0x1FE95C80  (Δ0x80)  *slot=0x383<<32
```

`TGcInfoDecoder::GetStackSlot` for `GC_FRAMEREG_REL` computes the slot as
`*GetRegisterSlot(m_StackBaseRegister=RBP) + spOffset`.
`GetRegisterSlot` dereferences `pRD->pCurrentContextPointers->Rbp` —
which SharpOS's C#-side `SehUnwind` sets to a **wrong saved-RBP slot**
for unwound frames (`FR=0x..C00` vs the true `pCurrentContext->Rbp =
0x..C80`, Δ0x80). Every frame-pointer-relative GC slot is then computed
off the bad base → a non-objref local (an `int`, `0x383<<32`) is
reported to the GC as an `OBJECTREF` → `Object::Validate` → `#PF`. This
is the **same upstream defect step 71 deferred** ("native-origin
unwind's bad `pCurrentContextPointers->Rbp`") — there it bit EH
catch-resume (fixed locally in `CallCatchFunclet`); here it bites the
**GC root scan**.

## Root C — 128 KiB UEFI boot stack overflows under stock reflection

With B fixed the run got far deeper, then died **silently** (no fault
banner — a triple fault: the #PF handler itself has no stack; QEMU
`-no-reboot` exits). A one-time `SharpOSHost_GetStackBounds` size print
showed the true mapped stack = the **128 KiB UEFI boot stack** the
unikernel runs on. Unlike the GC heap (demand-mapped, grows on #PF), the
stack region is fixed and the pager does not auto-grow it. Stock .NET
reserves ~1 MB/thread; reflection-mode `System.Text.Json` (nested
`DoRunClassInit` → JIT-on-first-call → typeload → cctor recursion)
overruns 128 KiB. Progress did **not** scale with stack size between
4 MiB and 16 MiB runs (≈same death line) confirming the limiter is the
stack, with run-to-run jitter (the earlier "truncated" Verbose logs were
this same silent triple fault, not premature stops).

## The fixes

Fork (`dotnet-runtime-sharpos`, `sharpos/coreclr-port`):

- **`vm/gcinfodecoder.cpp`** (Root B): `GetStackSlot` `GC_FRAMEREG_REL`
  branch — under `TARGET_SHARPOS && TARGET_AMD64`, when the frame-base
  register is RBP, base the slot on the trustworthy
  `pRD->pCurrentContext->Rbp` instead of the SehUnwind-corrupted
  `*GetRegisterSlot(...)` (= `pCurrentContextPointers->Rbp`). Local
  guarantee in the GC consumer — same accepted pattern as step-71's
  `CallCatchFunclet`. Shared upstream root (SehUnwind's
  `pCurrentContextPointers`) remains the future hardening target.
- **`vm/threads.cpp`**: `GetStackLowerBound`/`GetStackUpperBound` —
  SharpOS `VirtualQuery` is a fake stub (`AllocationBase = the queried
  address`), so the Windows-style branch returned `&local` (near stack
  TOP) as `m_CacheStackLimit` → chronic `stackwalk.cpp:974` storm and a
  collapsed GC/unwind range. Now query the kernel via
  `SharpOSHost_GetStackBounds`.
- **`pal/sharpos/winapi_shim.cpp`**: weak `SharpOSHost_GetStackBounds`
  fallback (link infra; absent → original path).
- **`pal/sharpos/crt_imp_stubs.cpp`**: real `FlushProcessWriteBuffers`
  (no-op single-core) + `NtQuerySystemInformation` (leap-second probe)
  + resolver entries — peeled the P/Invoke wall / SEHException mask that
  hid Root B. `TerminateProcess(0x80131506)` now halts cleanly (sage:
  stop masking the real result with post-fatal stackwalk spam).
  `GetModuleFileNameW` synthetic path renamed `\sharpos\kernel.exe` →
  `\sharpos\fake_kernel_path.exe` (was misleading disassembly).

Kernel (`c:/work/OS`, `main`):

- **`OS/src/Kernel/Memory/BigStack.cs`** (Root C, new): Win64
  shellcode rsp-switch trampoline (invariant-1 byte-array, like
  `GcStackSpill`/`JumpStub`); publishes the active buffer bounds.
- **`OS/src/PAL/SharpOSHost/StackBounds.cs`** (new):
  `SharpOSHost_GetStackBounds` — true stack bounds; returns the active
  BigStack buffer extent when a switch is in flight (so CoreCLR's
  `m_CacheStackLimit` is the real buffer bottom), else the UEFI region
  containing SP.
- **`OS/src/Boot/BootSequence.cs`** + **`CoreClrProbe.cs`**: route
  `CoreClrProbe.Run` through `BigStack.RunOn` on a 16 MiB
  `GcHeap.AllocateRaw` buffer (zero-filled → fully mapped) via a
  `[UnmanagedCallersOnly]` thunk; fall back to the boot stack on any
  failure (= prior behavior).

## Lessons (recorded in agent memory)

- CoreCLR is statically linked into the kernel image — symbolize
  runtime addresses against `OS/.qemu/esp/EFI/BOOT/BOOTX64.EFI`, never
  the standalone `artifacts/.../coreclr.dll` (a phantom-symbol day-waster
  — `dn_simdhash` was Mono-only, zero CoreCLR callers; the tell was
  ignored). `_ReturnAddress()` of a tiny helper only yields its
  immediate caller — not the culprit.
- Twice declared "root fixed" prematurely off a truncated/`Verbose`-slow
  log before the run reached the fault point. Do not call a root fixed
  without a clean pass *through* the failure site; the user's "are you
  sure it's the same crash?" was the correct check.
- Stack ≠ heap: VM demand-grows the heap, not the stack. A unikernel
  stack is fixed; overflow → triple fault (no-stack handler) → silent
  QEMU exit, non-deterministic by depth/timing. Give CoreCLR a real
  pre-mapped stack; don't expect the pager to save it.
- One-run signature-gated discriminators (scaffold repro, then targeted
  fork probe) beat speculation — every false framing died to one.

## Файлы

Fork: `vm/gcinfodecoder.cpp`, `vm/threads.cpp`,
`pal/sharpos/winapi_shim.cpp`, `pal/sharpos/crt_imp_stubs.cpp`.
All step-72 diagnostic probes (`[GCSLOT]`, `[BADOBJ]`, DoRunClassInit
`sp=`, `[PIL]`) reverted; `object.cpp`/`methodtable.cpp`/`dllimport.cpp`
back to upstream.

Kernel: `OS/src/Kernel/Memory/BigStack.cs` (new),
`OS/src/PAL/SharpOSHost/StackBounds.cs` (new),
`OS/src/Boot/BootSequence.cs`, `OS/src/Kernel/Diagnostics/CoreClrProbe.cs`,
`OS/src/PAL/SharpOSHost/Diagnostics.cs` (Verbose back to default OFF).
`done/step072.md` (this writeup).

Consult thread: `work/sage-step72.md` (the Sage dialogue + every
falsified framing → the proven GcInfoDecoder-RBP root).

Scaffold `work/normal-hello/` (gitignored): now the step-73 self-ID/IO
program (see below).

## Что дальше — step-73 self-ID / IO gap map

A stock .NET self-identification + file-I/O program was run on the same
base (battery completed on BigStack, `pass=5 fail=11`, **all failures
catchable `SEHException`/`OOM` — no panics**). Stock .NET correctly
self-identifies on bare metal: `FrameworkDescription = .NET 10.0.7-dev`,
`Environment.Version 10.0.7`, `TargetFramework .NETCoreApp,Version=v10.0`,
assembly identity, `ProcessPath \sharpos\fake_kernel_path.exe`,
ProcessorCount/PID, GC info. Concrete deferred frontiers (priority
order):

1. **Clock/RTC bridge** — `DateTime.UtcNow` = `1601-01-01` (FILETIME 0;
   CoreCLR time-PAL not wired to the kernel CMOS the `RtcSnapshot` probe
   already reads). Narrow, high-visibility.
2. **File-I/O PAL bridge** — `File.*`/`FileStream`/`Directory`/cwd →
   `SEHException` (read-oriented `SharpOSHost_FileOpen` not bridged into
   System.IO/System.Native).
3. **OS identification** — `RuntimeInformation.OSDescription` /
   `Environment.OSVersion` trap (OS-detect P/Invokes are trap-stubs).
4. **`Environment.GetEnvironmentVariables()` → `OutOfMemoryException`**
   (narrow env-enum PAL bug).

Also benign/known: the kernel launcher reads keystrokes as raw bytes
over `-serial mon:stdio`; a lone `0x1B` from any ANSI/VT escape
sequence (arrow keys, terminal responses), amplified by a large
`Verbose` dump, can be misread as the Esc-exit key — non-deterministic,
not a regression, outside step-72/73 scope.
