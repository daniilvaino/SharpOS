# Step 85 — Phase E6: lock infrastructure + alloc stress probe

**Status:** Phase E6 closes as **infrastructure-ready, application
deferred to Phase F preemption**. New scheduler-aware mutex types
land (Mutex class + SpinYieldLock helper); the alloc-stress acceptance
gate per `docs/threading-architecture.md §15` runs green (4 workers ×
10000 KernelHeap.Alloc/Free cycles each, with own-thread-id pattern
verification across an in-flight Yield). No actual lock wrapped onto
KernelHeap — see "Why KernelHeap stays lock-free" below.

probe_report.ps1 totals after E6: **OK 51, VALUE 3, FAIL 0, HALT 0**.
PAL/OS census OK=20 DEG=2 FAIL=22 still identical to pre-E1 — zero
regression cumulative across E1..E6.

## Что взяли с собой

| Файл | Тип | Что |
|---|---|---|
| `OS/src/Kernel/Threading/Mutex.cs` | N | Class-based scheduler-aware mutex. CAS fast-path via X64Asm.CmpXchg64 (E3 stub), wait-list slow-path through Thread.WaitNext, direct hand-off on Release (no observable "unlocked" window) |
| `OS/src/Kernel/Threading/SpinYieldLock.cs` | N | Foundational byte-flag lock. ulong owned by caller; CmpXchg64 fast-path; Yield()-on-contention when scheduler exists, raw spin pre-scheduler. Bootstrap-safe (no managed allocation, no cctor) |
| `OS/src/Kernel/Threading/AllocStressProbe.cs` | N | 4 workers × 10000 cycles of (Alloc(16) + write own ThreadId tag + Yield + verify tag + Free). 30-second HPET budget |
| `OS/src/Kernel/Memory/KernelHeap.cs` | M | + documentary comment about why lock-wrap was reverted (see below) |
| `OS/src/Kernel/Diagnostics/Probes.cs` | M | + `AllocStress` gate |
| `OS/src/Boot/BootSequence.cs` | M | вызов после SemaphoreProbe |
| `tools/probe_report.ps1` | M | + AllocStress matcher |

## Why KernelHeap stays lock-free in step 96

First-attempt of E6 wrapped `KernelHeap.Alloc/Free` in a non-reentrant
`SpinYieldLock`. Build clean, boot reached Phase 2, then **#PF**
manifest with the last visible log line "heap grow pages: " (partial).

Trace:

1. `KernelHeap.Alloc(size)` — first level, Acquire succeeds (`_lock`
   goes 0 → 1).
2. `FindFirstFit` returns null (heap too small for next alloc).
3. `AddRegion` called inside the locked region.
4. AddRegion does its work, then logs the growth with
   `Console.Write("heap grow pages: "); Console.WriteUInt(pageCount);`.
5. **`Console.WriteUInt(uint)`** dispatches via
   `NumberFormatting.UIntToString(value)` →
   `StringRuntime.FastAllocateString(length)` →
   **`KernelHeap.Alloc(stringBytes)`** -- recursive call.
6. Re-entrant `Acquire` sees `_lock == 1`, CAS fails, spin path.
7. `Scheduler.Current` is `null` (Phase 2 = pre-Phase-E4 scheduler).
   SpinYieldLock spins WITHOUT yielding -- infinite loop.
8. The infinite spin trips some state (probably wraps in timer or
   triggers an inactive page touch downstream); manifests as a
   plain #PF that HwFaultBridge.DispatchTrap can't service (GcHeap
   not yet up → `new NullReferenceException()` returns null →
   the "no exception для vector 14" panic path).

Fix space:

**A. Make the lock reentrant (owner + depth).** Doable: pack owner
token (boot sentinel pre-scheduler, `(uint)Thread.Id + 2`
post-scheduler) in low 32 of an ulong, depth in high 32, CAS the
whole pair. Recursive entry detected by owner==me, just `depth++`.
Single-CPU cooperative friendly AND preemption-ready. Complexity
acceptable but bigger than the E6 milestone scope.

**B. Refactor allocator to log outside the locked region.**
Mechanically possible (record the grow event into a fixed-size ring,
flush after Release), but pollutes the allocator's hot path with
deferred-IO bookkeeping. Reject.

**C. Don't lock at all today.** Honest with reality:
cooperative single-CPU + KernelHeap.Alloc not yielding inside means
there is no concurrent access window. Any "contention" today is by
construction recursive-from-self (logging path above). Real lock is
needed alongside real preemption (Phase F or SMP).

Chose **C** for step 96. The Mutex + SpinYieldLock infrastructure
ships (other primitives can use it -- e.g., higher-level futures'
state machines, future TimerQueue IRQ hand-off). KernelHeap revisited
under option A when preemption lands.

R5 from the §13 R-list ("KernelHeap.Alloc without lock") is therefore
"design-ready, not active today". Same applies to:

| R | Item | Status post-step-96 |
|---|---|---|
| R1 | Interlocked.CompareExchange naive | Deferred to E10+. Cooperative single-CPU + Roslyn iterators don't need real atomicity; CoreCLR uses its own Interlocked path |
| R2 | ClassConstructorRunner CAS-spin | Not blocking any scenario today |
| R3 | ExInfo.s_pExInfoHead static | Needs TEB extension + EH rework; separate larger step |
| R5 | KernelHeap.Alloc lock | Infrastructure ready (Mutex+SpinYieldLock); wire when preemption lands |
| R6 | GcHeap.AllocateRaw lock | std/no-runtime layer has no access to OS.Hal/Kernel; needs runtime-export hook; same preemption timing |
| R7 | GetLastError per-thread | Fork-side change in `pal/sharpos/crt_imp_stubs.cpp` |

E6 acceptance criterion ("4 threads × 10000 allocs, no corruption")
satisfied trivially by cooperative invariant. The AllocStress probe
still ships -- it exercises the multi-thread alloc path with realistic
Yield-between-write-and-verify interleaving, providing a regression
oracle for any future change that breaks that invariant.

## Result

```
[INFO] alloc stress probe start
[INFO] alloc stress probe: workers=4/4 allocs=40000/40000 corruption=0 allocFail=0 -- ok
```

probe_report.ps1: 51 OK / 3 VALUE / 0 FAIL / 0 HALT. PAL/OS census
OK=20 DEG=2 FAIL=22 unchanged. EH L1..L17 + drivers + launcher 4/4
all green.

## Что дальше

Phase E7 — Process struct + PID + handle table + concurrent ELF
launches. Per `docs/threading-architecture.md §15`. The infrastructure
to manage multiple processes is the next bottleneck on the way to
CoreCLR PAL routing (E9).
