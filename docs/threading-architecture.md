# Threading architecture (Phase E)

Canonical decision document for SharpOS cooperative threading. **Decisions
locked here are not re-negotiable mid-implementation** — deviations must
be justified by what's actually encountered in code, not by preference.

This consolidates two-sage review (see `work/phase_e_sage_brief.md` + 
`work/phase_e_sage_responses.md` for the deliberation trail). For Phase D
(EH/walker) decisions see `docs/eh-model.md`. For boot ordering see
`docs/boot-order.md`. For PAL-bridge D1-D20 decisions see
`work/PAL/D1-D20 FINALIZED/`.

## Scope

In:  kernel threads, logical processes, ELF/PE app threading, CoreCLR
     threading-PAL routing (D5/D6 reopen), AssemblyLoadContext
     multi-tasking (mostly free from stock CoreCLR).
Out: preemption (APIC-timer-tick), real GC suspend (Phase F / SP1),
     concurrent GC, SMP, MMU process isolation, network, real GC
     unloadable ALCs.

## 1. Five-tier architecture

```
┌──────────────────────────────────────────────────────────────────────┐
│ KERNEL = single OS image, ONE address space                          │
│                                                                      │
│  ONE cooperative scheduler + ready queue                             │
│  Thread carrier: kernel.Thread (regs + FXSAVE + stack + state +       │
│                                  TEB* + process* + ManagedBinding?)  │
│  Wait primitives: Event/Semaphore/TimerQueue (HPET-IRQ-assisted)     │
│  Yield points: explicit + I/O block + Sleep/Wait                     │
│  NO preemption-tick (cooperative-first)                              │
│                                                                      │
│  ┌────────────┬──────────────────────┬───────────────────────────┐   │
│  │ KERNEL     │ AOT-APP PROCESSES    │ CORECLR (one stat-linked  │   │
│  │ THREADS    │                      │   instance)               │   │
│  │            │ Process struct:      │                           │   │
│  │ - boot     │  PID                 │ - GC threads (Zero-GC)    │   │
│  │ - idle     │  thread set          │ - finalizer (deferred)    │   │
│  │ - async-IO │  memory regions      │ - ThreadPool (N=4)        │   │
│  │ - timer cb │  handle table        │ - User Thread.Create      │   │
│  │            │                      │ - ALC #1: REPL            │   │
│  │            │ ELF/PE concurrent OK │ - ALC #2: PS              │   │
│  │            │ thread API via SDK   │ - ALC #N: script          │   │
│  │            │   → kernel sched     │                           │   │
│  │            │                      │ thread API via PAL hook   │   │
│  └────────────┴──────────────────────┴───────────────────────────┘   │
└──────────────────────────────────────────────────────────────────────┘
```

**Invariants:**
- ONE address space (logical isolation only — no MMU process boundary).
- ONE scheduler across all tiers (no nested schedulers).
- ONE CoreCLR instance, statically linked.
- ONE universal carrier (kernel.Thread); CoreCLR Thread* is linked
  state, not carrier.

## 2. Page tables — early clone activation

[`X64PageTable.cs`](../OS/src/Kernel/Paging/X64PageTable.cs) already
clones UEFI's inherited PML4 into `s_rootTable` at boot via
`TryCloneTableRecursive`. **CR3 is never switched** — kernel runs on the
inherited `s_kernelRootTable`. `Map()` writes to the inactive clone, so
its mappings are invisible to the running CPU.

**Decision:** activate the clone (`mov cr3, s_rootTable`).

**Timing constraint (critical):** activation must happen **before** any
`VirtualMemory.MapKernel` or framebuffer/CoreCLR/JIT region mapping —
those write only to the active root. Activate after `InitializePager()`
clones the table, before any subsequent `Map*` call.

If retrofitting that order is infeasible, alternative is dual-write
`Map()` (writes to both `s_kernelRootTable` and `s_rootTable`) until
activation. Pick one before E1.

After activation: `Map()` continues to write `s_rootTable` (now active)
and is visible. Per-thread guard pages get reserved by leaving a slot
unmapped between adjacent stacks.

## 3. Thread carrier + ownership (D6 split)

**`kernel.Thread`** is the physical scheduling carrier (kernel-owned):

```
struct kernel.Thread {
    uint64 id;
    enum   state;                  // Ready | Running | Waiting | Dead
    Context regs;                  // GP + RIP + RFLAGS + RSP
    byte[512] fxsaveArea;          // FXSAVE legacy SSE+x87 (see §5)
    void* stackBase;               // top of allocated stack
    void* stackLimit;              // bottom-most usable byte
    void* guardPage;               // unmapped slot below stackLimit
    void* teb;                     // pointer into our TEB facade (§6)
    Process* process;
    WaitBlock* waitBlock;          // non-null if waiting
    enum   kind;                   // Kernel | AotApp | CoreClr
    ManagedThreadBinding* binding; // non-null iff kind == CoreClr
}
```

**`ManagedThreadBinding`** glues to CoreCLR-owned state:

```
struct ManagedThreadBinding {
    void*  clrThreadOpaquePtr;     // CoreCLR Thread* (vm/threads.h); kernel
                                   //   NEVER dereferences — opaque
    uint64 osThreadHandle;         // handle visible to PAL (TlsGetValue,
                                   //   GetCurrentThread)
    uint64 osThreadId;             // matches TEB.ThreadId
    void** tlsSlots;               // per-thread C++ __declspec(thread)
                                   //   pointer array; TEB+0x58 points here
    // mirror flags only as needed (debug, GC-mode shadow for asserts)
}
```

**Kernel never reads CoreCLR-Thread fields.** Owning structs reciprocally
opaque. CoreCLR `Thread*` lives in `vm/`; CoreCLR-side ThreadStore
continues to own its threads. The PAL bridge constructs a `kernel.Thread`
of `kind=CoreClr` paired with a fresh CoreCLR `Thread*` on
`CreateThread`; thereafter the kernel scheduler iterates `kernel.Thread`
ready queue and never touches managed state.

## 4. Context switch (byte-shellcode)

Single shellcode template emitted into kernel's RWX exec buffer (same
mechanism as `JumpStub`, `CaptureContextStub`). One shellcode call:

```
SwitchTo(kernel.Thread* curr, kernel.Thread* next)
  // Save curr:
  //   GPRs (rax..r15 except volatile per ABI)
  //   RIP (return-addr push), RFLAGS
  //   RSP -> curr.regs.rsp
  //   fxsave64 [curr.fxsaveArea]
  // Switch:
  //   wrmsr IA32_GS_BASE (0xC0000101) = next.teb
  // Load next:
  //   fxrstor64 [next.fxsaveArea]
  //   GPRs from next.regs
  //   ret (transfers to next's RIP)
```

Save area = GPRs + RSP/RIP/RFLAGS + 512-byte FXSAVE + gs base.
Architecturally per-thread struct memory: ~700 bytes for context.

## 5. FXSAVE vs XSAVE — FXSAVE only in Phase E

Phase E uses **FXSAVE/FXRSTOR** (512 bytes per thread). Rationale:
- We control `XCR0` via `xsetbv` at boot. We deliberately set
  `XCR0 = x87 | SSE` only.
- RyuJIT detects AVX through CPUID + `OSXSAVE` bit. With AVX disabled
  in `XCR0`, `OSXSAVE` reads false → JIT emits SSE2 path, no
  VEX-encoded instructions.
- 4-8 threads × 512 bytes vs × ~3 KB (XSAVE+AVX-512) — economy matters
  while we're proving the scheduler.
- Roslyn / PowerShell demos in Phase E don't require AVX.
- Migration to XSAVE in Phase F+ is a single point change: extend
  shellcode emitter to use XSAVE with `xstate_bv` mask covering the
  enabled XCR0 bits, update per-thread save area size.

**Sanity check at E0:** add boot probe that reads CPUID feature flags
+ `XCR0`, asserts AVX bit off. Document that bit will be enabled
explicitly when XSAVE migration happens.

## 6. TLS via TEB facade (gs base swap)

Each thread has a dedicated TEB-shaped struct (minimum ~`0x700` bytes)
that gs base points at. Layout (Windows-compatible where it matters,
ours extended above `0x600`):

| Offset | Field | Notes |
|---|---|---|
| `0x00` | `NtTib.ExceptionList` | NULL on x64 (legacy 32-bit SEH) |
| `0x08` | `NtTib.StackBase` | Top of usable stack |
| `0x10` | `NtTib.StackLimit` | Bottom-most usable byte (above guard) |
| `0x18` | `NtTib.SubSystemTib` | NULL |
| `0x20` | `NtTib.FiberData` / `Version` | Legacy; **NOT Self** |
| `0x30` | `NtTib.Self` | **Pointer to this TEB** (correct Self offset) |
| `0x58` | `ThreadLocalStoragePointer` | `void**` array of TLS-module bases |
| `0x60` | `ProcessEnvironmentBlock` | NULL (no PEB) |
| `0x68` | `LastErrorValue` | DWORD; per-thread `GetLastError`/`SetLastError` |
| `0x88` | `ThreadId` | DWORD; matches `osThreadId` in binding |
| `0x100..0x500` | `TlsSlots[64]` | `TlsAlloc`-style slots (zero-init initially) |
| `0x600` | `kernel.Thread*` | SharpOS-private back-pointer |

MSR: **`IA32_GS_BASE` (0xC0000101)** — used by direct `gs:[N]` reads
without `SWAPGS`. `IA32_KERNEL_GS_BASE` (0xC0000102) is only relevant
when using `SWAPGS`; we don't (single AS, no kernel/user split).

`SwitchTo` does `wrmsr 0xC0000101, next.teb`.

**Why this is critical to verify (G6/PV1) before E1:** if Phase 5.5
only set `IA32_GS_BASE` once at boot and the context switch path
doesn't re-write it, **every multi-thread `gs:[N]` read is broken** —
JIT-emitted direct accesses to `[ThreadStatic]` and CoreCLR's
`t_pCurrentThread` will silently read the wrong thread's slot. First
crash will be obscure (often null-deref or wrong-object); root cause
unobvious from the symptom.

### TLS array reallocation race (G17)

`gs:[0x58]+idx*8` reads are not atomic against grow of the array (new
TLS-using module loads). Phase E mitigation: allocate the array large
enough at thread creation (256 slots? — measure under Roslyn) and
fail-fast if exceeded. Phase F+: implement atomic pointer swap + grace
period.

### JIT-emitted direct gs accesses (G19)

The JIT inlines `mov rax, gs:[t_inlinedThreadLocalStaticBase_offset]`
for `[ThreadStatic]` fields. **These do not go through the PAL.** TLS
layout (specifically the slot offsets emitted by JIT during compile)
is implicitly an ABI contract — we cannot rearrange `gs:[N]` slots
after first JIT compile without invalidating all generated code.

Phase E commitment: lock the TEB layout above. Add new fields only at
the SharpOS-private region `0x600+`. CoreCLR-visible offsets
(`0x00..0x500`) are frozen.

## 7. Wait primitives — event/semaphore + HPET-IRQ-assisted timer

`Event` (set/reset/auto-reset) and `Semaphore` are scheduler-aware
blocking primitives. Waiting thread is moved from ready queue to
event's wait list. `Set()` moves wait-list entries back to ready queue.

`TimerQueue` holds deadlined tasks. **HPET configured with interrupt**
(IRQ vector with IST stack — see §10 G15). IRQ handler does only:

```
HPET_IRQ_handler:
    deadlineDue = 1          // set flag, do NOT walk timer queue
    EOI
    iretq                    // return to interrupted code
```

The actual timer-queue walk happens on the next `yield`/`Sleep`/`Wait`:

```
Scheduler.Yield():
    if deadlineDue:
        deadlineDue = 0
        for each timer in timerQueue with expiry <= now:
            move associated thread from wait list to ready queue
    swap to next ready thread
```

This preserves the cooperative invariant: managed code is never
preempted mid-execution. The IRQ only sets a flag; rescheduling happens
at controlled yield points.

**CPU-bound managed loop without yield points still freezes the world.**
This is accepted Phase E limitation (G2/G16) — addressed via JIT-emitted
loop-back-edge polls in Phase F.

**IRQ handler discipline (Sage 2):**
- No heap allocation (would need allocator lock; could deadlock).
- No logging through `Console.Write` paths that lock.
- No managed callback.
- No context switch.
- No complex wait-queue walks without atomic guards.

## 8. GC suspend — Zero-GC + fail-fast in Phase E

Phase E remains on Zero-GC (no collection, no suspend). Threading
without GC suspend is sound iff GC never tries to suspend.

**Required:** explicit `Panic.Fail("GC suspend not supported in Phase E")`
in any code path that attempts `SuspendRuntime`/`SuspendEE`/equivalent.
Silent corruption from a partial-suspend is far worse than a loud halt.

**Heap-growth risk (SP5):** Roslyn / PowerShell allocate heavily and
the heap is monotonic without GC. Set a hard `KernelHeap` upper bound;
`OutOfMemoryException` becomes the expected end-of-Phase-E demo
runtime. Document for demos: "REPL session is bounded; Phase F brings
real GC."

Phase F reopens D8: choose between cooperative safepoint polls (JIT
emits poll-call on loop back-edges; managed code voluntarily
suspends), APIC-IPI (requires SMP), or some hybrid. Out of scope here.

## 9. ThreadPool / Task / async-await

**Fixed N=4 worker threads** at Phase E. Grow-bound to 8 only if a
specific scenario demonstrates need (Roslyn deadlock on N=4 not yet
observed; PowerShell minimal needs measurement). `Environment.Processor-
Count` reports 2 (not 1) to keep stock BCL heuristics out of
single-core corner cases.

`SynchronizationContext` provided by Phase E: default-flushing context
that posts continuations to the ThreadPool. No UI sync context.

`Task.Run`, `Task.Delay` (TimerQueue-backed), `await` chains depth ≤8
levels — verified working before declaring E10 done.

**Block-on-async deadlock risk:** `task.Wait()` while waiter occupies
the only ThreadPool slot is a classic deadlock. Phase E demos avoid
`.Wait()` / `.Result` on `Task` returned by user code; only use
`await`. Document; do not enforce in PAL.

## 10. Process abstraction (logical, no MMU isolation)

```
struct Process {
    uint64 pid;
    string name;                  // for diagnostics
    List<kernel.Thread> threads;  // owned threads
    HandleTable handles;          // open files, events, etc.
    List<MemoryRegion> regions;   // allocated memory (image, heaps, stacks)
    int exitCode;
    enum state;                   // Active | Exiting | Zombie
}
```

Process lifecycle:
- `Process.Launch(image, args)` — creates Process, allocates regions,
  loads image, creates initial `kernel.Thread`, links thread to
  Process via back-pointer.
- `Process.Exit(int code)` — marks Exiting, scheduler refuses to run
  its threads on next yield, frees regions, closes handles, reports
  exit code to parent (or kernel as root).
- **Panic during managed catch (G18):** if a thread crashes mid-catch,
  kernel detects via Frame chain state inconsistency, marks Process
  Exiting, runs its cleanup. Recovery is process-scoped; one bad
  process doesn't kill the kernel.

Concurrent launching: parent calls `Process.Launch` multiple times;
each returns a `Process*` with own threads, scheduled concurrently
by the kernel.

## 11. ELF/PE app threading via AppSDK

[`apps/sdk/AppHost.cs`](../apps/sdk/AppHost.cs) gets new entries
mirroring kernel threading API. Each becomes a `[RuntimeExport]`
`SharpOSHost_*` from app's POV that delegates to kernel:

```
SharpOSHost_CreateThread(entryPoint, arg, stackSize) -> threadId
SharpOSHost_JoinThread(threadId, timeoutMs) -> exitCode
SharpOSHost_YieldThread()
SharpOSHost_SleepThread(ms)
SharpOSHost_CreateEvent(manualReset) -> eventHandle
SharpOSHost_SetEvent(eventHandle)
SharpOSHost_WaitEvent(eventHandle, timeoutMs) -> waitResult
SharpOSHost_GetCurrentThreadId() -> threadId
```

Migration from ELF to PE doesn't affect API.

## 12. CoreCLR PAL routing (D5 reopen — full surface)

The `D5 = ABORT_FATAL` stubs in `pal/sharpos/crt_imp_stubs.cpp` get
replaced with real implementations. **Not just `CreateThread`** — full
list:

```
Thread lifecycle:
  CreateThread, ExitThread, TerminateThread,
  GetCurrentThread, GetCurrentThreadId

Sync primitives:
  WaitForSingleObject, WaitForMultipleObjects, MsgWaitForMultipleObjects,
  SleepEx, Sleep, SwitchToThread,
  CreateEvent, SetEvent, ResetEvent, PulseEvent,
  CreateSemaphore, ReleaseSemaphore,
  CreateMutex, ReleaseMutex,
  InitializeCriticalSection, EnterCriticalSection, LeaveCriticalSection,
    DeleteCriticalSection,
  TryAcquireSRWLockShared/Exclusive, AcquireSRWLockShared/Exclusive,
    ReleaseSRWLockShared/Exclusive,
  SleepConditionVariableCS, WakeConditionVariable, WakeAllConditionVariable

Handle management:
  CloseHandle, DuplicateHandle

TLS:
  TlsAlloc, TlsFree, TlsGetValue, TlsSetValue,
  FlsAlloc, FlsFree, FlsGetValue, FlsSetValue (rare)

Errors:
  GetLastError, SetLastError    (real per-thread via TEB+0x68)
```

Expect 2-3 weeks of crash-driven debugging here (Sage 1: "first crash on
real CreateThread will be GC thread / finalizer / ThreadPool worker tripping over uninit state").

## 13. Reentrancy audit (R-list)

Two passes:

**Pass 1 (E6, critical-for-multi-thread):**

| ID | Location | Issue | Fix |
|---|---|---|---|
| R1 | `std/no-runtime/shared/Threading.cs` | `Interlocked.CompareExchange` non-atomic | `lock cmpxchg` byte-shellcode |
| R2 | `Runtime/ClassConstructorRunner.cs` | CAS-spin early-return | Restore full CAS-loop |
| R3 | `OS/src/Boot/EH/ExInfo.cs` | `s_pExInfoHead` static | Per-thread via TEB |
| R5 | `KernelHeap.cs` | Alloc without lock | Scheduler-aware blocking lock |
| R6 | `GC/GcHeap.cs` | `AllocateRaw` without lock | Same |
| R7 | `pal/sharpos/crt_imp_stubs.cpp` | `GetLastError` placeholder | Real per-thread via TEB+0x68 |

**Pass 2 (E13, crash-driven):**

| ID | Location | Issue |
|---|---|---|
| R4 | `DebugLog` `s_inLine` | Single-thread reentrancy guard |
| R8 | `StackFrameIterator` walker statics | Per-thread state |
| R9 | `AppDomain.cs` `s_currentDomain` | Per-thread |
| R10 | `g_pConfig` lazy init | Thread-safe |
| R11 | `g_profControlBlock` | Skip if profiler disabled |
| R12 | `Thread::m_pFrame` anchor | Verify per-thread anchor from Phase D walker integration |
| R13 | DAC globals | Re-evaluate `#ifndef TARGET_SHARPOS` skips |
| R14 | `PhysicalMemory` | Add allocation lock |
| R15 | `VirtualMemory` page-table spare list | Lock |
| R16 | Process list / PID allocator | Lock + atomic PID counter |
| R17 | Handle tables | Per-process lock |
| R18 | Console/logging path | Per-thread buffer or real lock |
| R19 | GC root lists | Per-thread root accumulation |
| R20 | PAL critical sections, wait handles, monitor/syncblock | Scheduler-aware |

## 14. Out of scope (deferred to Phase F+)

- Real concurrent GC + suspend (Phase F, **SP1 main risk**)
- Preemption via APIC-timer-tick
- SMP (multi-core)
- MMU process isolation (separate page tables per process)
- AssemblyLoadContext implementation (stock CoreCLR feature; works once
  Phase E threading lands)
- Network stack
- XSAVE/AVX migration (Phase F+ when needed)

## 15. Implementation sequence E0-E13

| # | Goal | Acceptance criterion |
|---|---|---|
| E0 | Pre-flight: PV1 (gs base swap on context switch?), PV2 (XCR0 state at boot?), PV3 (page-table active vs inactive content) | Documented findings; PV1 either confirmed or planned |
| E1 | Page-table clone activated early; `Map()` visible | Probe writes new mapping, CPU sees it post-switch |
| E2 | TEB facade allocated per (currently single) thread; gs base swap on context switch | Probe reads `gs:[0x30]` returns TEB.Self; switch-test ping-pong |
| E3 | Atomic primitives (`lock cmpxchg`, `xchg`, `mfence`) via byte-shellcode | `Interlocked.CompareExchange` byte-equal to real `lock cmpxchg` semantics |
| E4 | `kernel.Thread` + cooperative `SwitchTo`; 2 kernel threads alternating | Two threads ping-pong N times; FXSAVE not corrupted |
| E5 | `TimerQueue` (HPET-IRQ-assisted) + Event/Semaphore + `Sleep`/`Yield` | `Thread.Sleep(100)` returns at correct deadline ±5 ms |
| E6 | Allocator/page/VM locks (scheduler-aware blocking); R1-R3, R5-R7 fixed | Multi-thread alloc stress: 4 threads × 10000 allocs no corruption |
| E7 | `Process` struct + PID + handle table; concurrent ELF launches; exit cleanup (G18) | Two ELF apps run concurrently, both exit cleanly |
| E8 | ELF thread API in AppSDK | ELF app spawns 2 worker threads, joins them, prints results |
| E9 | CoreCLR PAL routing — full §12 surface | `new Thread(() => Console.WriteLine("hi")).Start(); Join()` works |
| E10 | ThreadPool N=4 | `ThreadPool.QueueUserWorkItem(_ => count++)` × 100 — final `count == 100` |
| E11 | Task + async-await + SynchronizationContext | `Task.Run(async () => { await Task.Delay(50); return 42; }).Result == 42` |
| E12 | AssemblyLoadContext smoke — 2 ALCs with isolated assemblies | Two ALCs load same library, types distinct |
| E13 | Pass-2 reentrancy audit (R4, R8-R20); driven by E9-E12 crash signatures | No HALT on `for (i=0; i<10000; i++) Task.Run(() => i)` |

**Realistic timing (per Sage 1):** E1-E10 ≈ 8-12 weeks; E11-E13 ≈ 4-6
weeks debugging surface. Total Phase E ≈ 3-4.5 months solo. Not a
one-day landing like Phase D.

## 16. Cross-references

- `work/phase_e_sage_brief.md` — original questions + sage corrections
  + synthesis tail.
- `work/phase_e_sage_responses.md` — full sage 1 + sage 2 responses
  verbatim.
- `work/PAL/D1-D20 FINALIZED/D5___FINALIZED.md` — D5 (no threading
  in spike) reopen rationale.
- `work/PAL/D1-D20 FINALIZED/Deferred_Decisions.md` — D6/D8 deferred
  status, reopen here.
- `plan.md` — overall DAG + Phase E section + decision-points table.
- `docs/eh-model.md` — Phase D EH architecture (predecessor doc style).
- `docs/coreclr-hosted-limits.md` §5 (threading) — currently-failing
  cohort that Phase E unblocks (Socket/RNG/SHA256 already catchable
  post-Phase D; Thread.Start / ThreadPool / Task / Timer / Sleep
  unblock here).
- `gc-experiment/MOOS/Kernel/Misc/Threading.cs` — preemptive scheduler
  reference (MIT/Unlicense, attribution required if portions adapted).
- `gc-experiment/MOOS/Kernel/Driver/HPET.cs` — direct port candidate.
- `dotnet-runtime-sharpos/src/coreclr/vm/threads.h` — CoreCLR Thread*
  layout (kernel never reads).
