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
                                   //   MUST be 16-byte aligned. Allocator
                                   //   either pads inside struct or returns
                                   //   16-aligned base. assert((uintptr_t)
                                   //   fxsaveArea % 16 == 0) before first
                                   //   switch. (Phase F XSAVE: 64-byte.)
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

**Stack guard #PF policy (sage-2 add).** Each thread reserves one
unmapped page immediately below `stackLimit`. The `#PF` handler
runs on the IST stack (per §10). On `#PF` with `CR2` inside a known
`guardPage`:

- Phase E: mark the process / thread as stack-overflow-fatal and
  halt or kill it. No managed EH recovery yet.
- We do NOT auto-grow the stack — Phase E has no strict stack-region
  model; growth in a single AS is a foot-gun (might collide with the
  next thread's stack or with kernel data).

On `#PF` with `CR2` not in a guard page and not in a VM-demand
region: regular page-fault path → panic dump → halt.

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

Phase E uses **FXSAVE/FXRSTOR** (16-byte aligned 512-byte area per
thread). Misaligned operand is `#GP` — the per-thread save buffer MUST
be 16-byte aligned and the kernel allocator must guarantee it (current
implementation allocates a whole 4 KiB page per `ContextBlock` so the
fxsave area at offset `0x10` is trivially 16-aligned).

How we keep AVX off the JIT codegen path (corrected per sage-2 review):

- `CR4.OSXSAVE` must be set before any `xsetbv`. `xsetbv` on a system
  with `CR4.OSXSAVE = 0` is `#UD`. (Observed empirically on QEMU/OVMF —
  firmware does NOT set OSXSAVE by default.)
- `XCR0 = x87 | SSE` (bits 0, 1) — no YMM bit, no AVX-512 bits.
- RyuJIT does NOT decide AVX availability from `CPUID.OSXSAVE` alone.
  The correct mechanic per Intel SDM: AVX is OS-enabled iff
  `CPUID.OSXSAVE == 1` **and** `XGETBV(0).SSE == 1` **and**
  `XGETBV(0).YMM == 1`. With `XCR0 = 0x3` (YMM bit clear), JIT sees
  AVX as not-OS-enabled and falls back to legacy SSE; FXSAVE then
  covers the full live FP state.

AVX-512 detection uses additional XCR0 bits (`OPMASK`, `ZMM_HI256`,
`HI16_ZMM`); leaving them at 0 keeps AVX-512 off the codegen path too.

Save-area economics:

- FXSAVE: **512 B/thread**.
- XSAVE with AVX (YMM only): **~832 B/thread** (256 B legacy area +
  AVX state area; depends on CPUID.0DH.1.EBX).
- XSAVE with AVX-512: **several KiB/thread** (full ZMM + OPMASK).

We pick FXSAVE in Phase E for the deterministic SSE-only codegen path,
not because XSAVE-AVX is itself unaffordable. Migration to XSAVE in
Phase F+ extends shellcode emitter to use `xsave`/`xrstor` with an
`xstate_bv` mask matching the enabled XCR0 bits and bumps per-thread
save-area size from the CPUID-reported figure (`CPUID.0DH.0.ECX`).

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
| `0x40` | `ClientId.UniqueProcess` | `uintptr` — Windows-compatible process ID |
| `0x48` | `ClientId.UniqueThread`  | `uintptr` — Windows-compatible **ThreadId** offset (per Geoff Chappell's x64 TEB) |
| `0x58` | `ThreadLocalStoragePointer` | `void**` array of TLS-module bases |
| `0x60` | `ProcessEnvironmentBlock` | NULL (no PEB) |
| `0x68` | `LastErrorValue` | DWORD; per-thread `GetLastError`/`SetLastError` |
| `0x6C` | `CountOfOwnedCriticalSections` | optional; pad/zero if not modelled |
| `0x600` | `kernel.Thread*` | SharpOS-private back-pointer (clear of all known Windows fields) |

**Important correction (sage-2):** earlier draft placed `ThreadId`
at `0x88`. That offset is **not** Windows-compatible. The
Windows-compatible thread ID lives in `ClientId.UniqueThread` at
**`0x48`** (a `uintptr`, not DWORD). `0x88` on x64 falls in the gap
between LastError and `GdiTebBatch` (Geoff Chappell places
`GdiTebBatch` near `0x02F0` on x64, not `0x80` — the critic's
"inside GdiTebBatch" assertion was incorrect; but `0x88` is still
wrong as a Windows ABI field). Any SharpOS-private mirror of the
thread ID goes at `0x600+` or is simply read via `kernel.Thread*`.

CoreCLR-visible offsets (`0x00..0x500`) are now frozen: the JIT inlines
`gs:[<offset>]` reads after first compile and a later layout change
would invalidate every cached code page.

MSR: **`IA32_GS_BASE` (0xC0000101)** — used by direct `gs:[N]` reads
without `SWAPGS`. `IA32_KERNEL_GS_BASE` (0xC0000102) is only relevant
when using `SWAPGS`; we don't (single AS, no kernel/user split).

**FS_BASE / SWAPGS invariant (sage-2 add):**

- `FS_BASE` is **not** used by CoreCLR-hosted code in Phase E.
- `SwitchTo` does NOT preserve `FS_BASE`. Any future code that writes
  `FS_BASE` must extend `Context` and the switch shellcode.
- `IA32_KERNEL_GS_BASE` remains untouched.
- `SWAPGS` is forbidden in Phase E — we're single-AS / no ring 3.
- Segment selector state (`CS/DS/ES/FS/GS/SS` regs) is firmware-init'd
  and not modified by `SwitchTo`. Document if a phase ever needs to.

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

The actual timer-queue walk happens on the next `yield`/`Sleep`/`Wait`.

**Lost-wakeup hazard (sage-2):** the naive `if (flag) { flag = 0;
walk; }` pattern loses any IRQ that fires between the test and the
clear. Even on a single core the IRQ is asynchronous. Use an atomic
exchange so consume-the-signal and clear-the-flag are one operation:

```
Scheduler.Yield():
    // Atomic 0-store-and-read-old: if old != 0, signal was raised
    // between two consumes; we own it now.
    if (xchg(&deadlineDue, 0) != 0):
        for each timer in timerQueue with expiry <= now:
            move associated thread from wait list to ready queue
    swap to next ready thread
```

Equivalent shellcode (Win64 ABI, RCX = &deadlineDue):

```
mov eax, 0
xchg [rcx], eax          ; eax = old value, [rcx] = 0
test eax, eax
jz   no_timers
```

`X64Asm.Xchg64` (Phase E3) gives this primitive at managed surface.

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

**Known-lie disclosure (sage-2):** `ProcessorCount = 2` is a BCL
heuristic hack, not hardware truth. Our hardware is effectively
single-core cooperative. Returning 2 changes heuristics inside
`ThreadPool`, `ConcurrentDictionary`, `Parallel`, `Channels`,
`PLINQ`, etc. — generally raising worker counts and contention
without backing CPUs. Re-evaluate when we go SMP or add preemption;
the right answer there is the real core count, not this canned 2.

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
list (expanded per sage-2 review):

```
Thread lifecycle:
  CreateThread, ExitThread, TerminateThread,
  GetCurrentThread, GetCurrentThreadId,
  SuspendThread, ResumeThread,                    (sage-2 add)
  GetThreadContext, SetThreadContext,             (sage-2 add)
  GetThreadTimes,                                 (sage-2 add)
  QueueUserAPC                                    (sage-2 add)

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
  SleepConditionVariableCS, WakeConditionVariable, WakeAllConditionVariable,
  WaitOnAddress, WakeByAddressSingle, WakeByAddressAll  (sage-2 add — Win8+ futex-like)

I/O Completion Port (for current LowLevelLifoSemaphore.Windows):
  CreateIoCompletionPort, GetQueuedCompletionStatus,
  PostQueuedCompletionStatus                      (sage-2 add)

Unwind / EH:
  RtlVirtualUnwind, RtlLookupFunctionEntry,
  RtlAddFunctionTable, RtlDeleteFunctionTable,
  RtlInstallFunctionTableCallback                 (already needed; explicit)

Handle management:
  CloseHandle, DuplicateHandle

TLS:
  TlsAlloc, TlsFree, TlsGetValue, TlsSetValue,
  FlsAlloc, FlsFree, FlsGetValue, FlsSetValue (rare)

Errors:
  GetLastError, SetLastError    (real per-thread via TEB+0x68)
```

**Sage-2 correction on critic claims:**

- The critic's blanket assertion that "every .NET lock primitive
  needs `WaitOnAddress`" was wrong. Current
  `LowLevelLifoSemaphore.Windows` uses I/O Completion Port
  (`CreateIoCompletionPort` / `GetQueuedCompletionStatus` /
  `PostQueuedCompletionStatus`) — not `WaitOnAddress`.
- `System.Threading.Lock` slow path in current corelib uses
  `AutoResetEvent`, not direct `WaitOnAddress`.
- Therefore `WaitOnAddress`/`WakeByAddress*` are likely-needed
  surface (they're the modern futex-like API and the .NET runtime
  reaches for them in newer paths), but they are NOT the universal
  lock backend. Plan covers both: WaitOnAddress family AND IOCP
  trio, with explicit `ABORT_FATAL` + known fallback documented
  for any surface that lands first.

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
| E0 | Pre-flight: PV1 (gs base swap on context switch?), PV2 (XCR0 state at boot?), PV3 (page-table active vs inactive content) | DONE — findings in §17 |
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
  reference. License (sage-2 correction): MOOS `LICENSE` is
  **public-domain / Unlicense-like** ("free and unencumbered software
  released into the public domain"), NOT MIT. No copyright-notice
  retention requirement applies; we still leave an attribution comment
  for provenance when copying code.
- `gc-experiment/MOOS/Kernel/Driver/HPET.cs` — direct port candidate.
- `dotnet-runtime-sharpos/src/coreclr/vm/threads.h` — CoreCLR Thread*
  layout (kernel never reads).

## 17. E0 pre-flight findings (2026-05-19)

Read-only investigation; no code changes. Each PV either confirms an
assumption in §2/§5/§6 or surfaces a delta the implementer must
absorb before E1.

### PV1 — gs base today vs. swap on context switch

`OS/src/Kernel/Diagnostics/CoreClrProbe.cs:600` emits a wrmsr
shellcode at `AsmExecBuffer` offset 64 that writes
`IA32_GS_BASE = 0xC0000101` once at boot to the address of the
single-thread TEB struct. TEB layout matches §6:

| offset | field | use |
|---|---|---|
| `gs:[0x10]` | StackLimit | CoreCLR stack queries |
| `gs:[0x30]` | Self | TEB self-pointer (§6 lock) |
| `gs:[0x58]` | TLS pointer | CoreCLR TLS slot array |
| `gs:[0x60]` | PEB | currently null sentinel |
| `gs:[0x68]` | LastError | mutated by Win32 emulation |

**Status.** Single-thread today, so no swap needed. The wrmsr shellcode
IS reusable for context-switch — caller just hands a different TEB
pointer. **MSR ID is correct** (`0xC0000101`, NOT `0xC0000102` —
the latter is the SWAPGS scratch, irrelevant in our ring-0 unikernel).

**E2 input.** Reuse the existing shellcode; switch path is one
indirect call with the next TEB's address.

### PV2 — XCR0 state at boot (corrected per sage-2 review)

Grep across kernel sources + fork PAL: ZERO occurrences of `xsetbv`,
`XCR0`, `AVX`. We never touch the SSE/AVX enable mask explicitly.

**Empirical follow-up:** QEMU/OVMF leaves `CR4.OSXSAVE = 0` —
observed when the original `xsetbv` preamble `#UD`'d on boot.
With `CR4.OSXSAVE = 0`, `xsetbv` is illegal AND `CPUID.OSXSAVE`
reports 0 → the JIT cannot use any XSAVE-class instruction at all,
which incidentally keeps AVX off the codegen path. So a kernel that
NEVER enables OSXSAVE is safe-by-omission. The current shipped code
takes that path: read CR4 first, skip `xsetbv` if OSXSAVE is 0.

**But sage-2 corrects two things that matter for the long-term spec:**

1. Earlier draft claimed "with AVX disabled in XCR0, OSXSAVE reads
   false." **Wrong mechanic.** `CPUID.OSXSAVE` mirrors `CR4.OSXSAVE`,
   not `XCR0` contents. AVX is OS-enabled iff `CPUID.OSXSAVE == 1`
   **AND** `XGETBV(0).SSE == 1` **AND** `XGETBV(0).YMM == 1`. So the
   correct way to disable AVX while keeping XSAVE infra usable is to
   set `CR4.OSXSAVE = 1` and then `XCR0 = 0x3` (SSE bit set, YMM bit
   cleared).

2. `xsetbv` requires `CR4.OSXSAVE = 1` first, otherwise `#UD`. The
   original preamble (xsetbv with no CR4 set) was unsafe.

**Updated E1 preamble (long-term form):**

```
; require CPUID.01H:ECX.XSAVE = 1 first; if 0, hardware has no XSAVE
; family at all — skip the whole block and rely on FXSAVE legacy.

mov rax, cr4
or  rax, 1 << 18              ; CR4.OSXSAVE = 1
mov cr4, rax

xor ecx, ecx                  ; XCR0 selector
xor edx, edx
mov eax, 3                    ; bits: x87 (0) | SSE (1); YMM bit clear
xsetbv                        ; XCR0 := EDX:EAX
```

Logging recommended:

```
[cpuid] XSAVE=...               (CPUID.01H:ECX.bit26)
[cpuid] OSXSAVE-before-cr4=...  (CPUID.01H:ECX.bit27)
[cpuid] OSXSAVE-after-cr4=...   (should be 1)
[xcr0] before=...
[xcr0] after=0x3
```

**Current code status.** `ActivatePagerRootAndLockCpuFeatures` reads
CR4 and SKIPS `xsetbv` if `OSXSAVE = 0`. That's safe but leaves
`CR4.OSXSAVE` unmanaged — fine while we never want XSAVE/AVX. When
the kernel eventually wants OSXSAVE on (e.g., Phase F XSAVE
migration), it must explicitly take the preamble above.

### PV3 — page-table active vs inactive content

`OS/src/Kernel/Paging/X64PageTable.cs:58-94` (`Init`):

1. Read live CR3 → `s_kernelCr3` / `s_kernelRootTable` (firmware PML4)
2. `TryCloneTableRecursive(s_kernelRootTable, 4, out s_rootTable)` —
   recursive deep-clone produces our own PML4 sharing firmware's
   identity mappings
3. `s_pagerCr3` set; `TryActivatePagerCr3` exists but NEVER called

| API | Writes to | CPU-visible today |
|---|---|---|
| `Pager.Map` → `X64PageTable.Map` | `s_rootTable` (inactive clone) | NO |
| `VirtualMemory.MapFixed/Map` → `X64PageTable.MapKernel` | `s_kernelRootTable` (active firmware) | YES |
| `TrySetKernelFlags*` | `s_kernelRootTable` | YES |

**Caller audit:**

- `Pager.Map` (invisible writes): `JumpStub`, `ElfLoader`,
  `ElfValidation`, `ProcessImageBuilder`, `ProcessManager`,
  `AppServiceBuilder`, `PagingValidation`
- `MapKernel` (visible writes): `VirtualMemory.MapFixed` only,
  used by `Framebuffer`, `Pci`, `Ahci`, `SharpOSHost.Memory`
  (CoreCLR JIT exec regions)

**Why ELF apps run despite `Pager.Map` being invisible.** UEFI
identity-maps the full address space in firmware's PML4. ELF code
and data live at PA == VA in already-mapped firmware ranges, so
loads/stores succeed via the active kernelRootTable. The
`Pager.Map` calls add entries to the clone for our own bookkeeping
but the CPU never consults the clone (CR3 is firmware). The clone
is effectively a write-only ledger today.

**E1 hazard — split-brain on activation.** If we naively
`TryActivatePagerCr3()` at the current call site (post-Init, after
MapKernel has already populated `s_kernelRootTable` with FB/AHCI/PCI/JIT
mappings), the clone is STALE for those ranges. CPU loses FB MMIO,
AHCI MMIO, PCI ECAM window, and every JIT-emitted code page. Boot
fails on first access through one of those.

**E1 fix (locks Sage 2 H3 in §2).** One of:

(a) **Activate clone EARLY**, immediately after `Init()` returns,
    BEFORE any `VirtualMemory.MapFixed` / framebuffer / AHCI /
    PCI / JIT region is mapped. After activation, redirect
    `MapKernel` to write to `s_rootTable` (which is now active).
    Cleanest model: one root table, period.

(b) **Mirror every `MapKernel` to `s_rootTable`**, then activate
    later. Doubles bookkeeping; one write per call goes "wherever"
    isn't active. Higher leak surface.

§2 already records H3 (early activation) as the chosen path. PV3
confirms (a) is feasible — `MapKernel` and `Map` share the same
algorithm (only the root-table field differs), so the refactor is
literal: drop `s_kernelRootTable` writes after activation, route
both APIs to `s_rootTable`.

**Boot-order constraint.** Activation must happen between
`X64PageTable.Init()` returning and the first `VirtualMemory.MapFixed`
call. Today that first call is `Framebuffer.Init` (`OS/src/Hal/Framebuffer.cs:51`)
unless paging-validation pre-empts.

### PV4 — global TLB / CR4.PGE on activation (sage-2 add)

Earlier draft assumed `mov cr3, …` provides "implicit TLB
invalidation." **Half right.** `mov cr3` invalidates non-global
translations only; entries marked with the **global** bit
(`PTE.G`, bit 8) survive CR3 reload when `CR4.PGE = 1`.

This is not academic. The build's `last_build.log` shows PTEs with
the global flag — e.g. `flags=P|W|G|NX`, `flags=Present|Writable|Global|NoExecute`.
So global mappings exist in the firmware PML4, get cloned into our
clone PML4 (memcpy preserves the G bit), and would persist in the
TLB across activation if `CR4.PGE` is on.

**Activation MUST perform a full local TLB flush.** Because CR3
reload does not invalidate global translations when `CR4.PGE = 1`,
activation must either:

(A) Clear `CR4.PGE`, load CR3, restore `CR4.PGE` (recommended).
(B) Prove no active G mappings exist AND keep PGE disabled until
    after activation.

Patch sequence for (A):

```
mov rax, cr4
mov rdx, rax                  ; save original CR4 for restore
and rax, ~(1 << 7)            ; clear CR4.PGE → invalidates global TLB
mov cr4, rax

mov cr3, <s_rootTable | flags>

mov rax, rdx
mov cr4, rax                  ; restore PGE bit
```

**Current code status.** `Pager.TryActivatePagerRoot` →
`Cr3Accessor.TryWrite` writes CR3 without toggling CR4.PGE. The
hazard is real but the observed boot survives because the clone
preserves all firmware mappings PA-for-PA — stale G-cached
translations point to the same physical pages as the cloned
mappings, so semantics are identical for already-cached VAs. We do
NOT modify any firmware-mapped entry post-activation; if we ever do,
the TLB will serve the OLD translation and the change goes invisible
until the next process-wide flush.

**PV4 acceptance check** (later, when implementing): after activation,
the active CR3 equals `s_rootTable`, AND a synthetic global-flag
write through a fresh test VA is visible immediately (proves no
stale G translation survived).

### Summary

| PV | Status | Action absorbed into |
|---|---|---|
| PV1 | gs base wrmsr shellcode exists, reusable for swap | E2 |
| PV2 | XCR0 mechanic corrected (OSXSAVE ≠ XCR0); preamble needs CR4.OSXSAVE first; current code gates xsetbv on CR4.OSXSAVE (safe by omission) | E0 → E1 |
| PV3 | Two-PML4 split-brain closed; single-table model post-activation | E1 (landed) |
| PV4 | CR4.PGE global pages survive `mov cr3`; activation must toggle PGE off/on for full local TLB flush | E1.b (post-landing fix) |

E0 closes with these recorded. E1 starts with the §2 H3 sequence
plus the PV2 xsetbv preamble inserted before any JIT-callable code
is mapped.
