# Open symptoms (under investigation)

Trail of observations that don't yet justify a step / commit but should
not be lost. Each entry: short title, date, exact log line(s) or repro,
hypothesis (if any), what would close it.

Append-only list. When an entry is closed (root identified + fixed or
explained), move it into the relevant `done/stepNN.md` and delete here.

---

_(SYM-001 retracted and SYM-002 resolved in done/step113.md: the
silent-triple-fault root was an infinite `sqrt`↔`lm_sqrt` recursion in
the Debug fork build, fixed by emitting `sqrtsd` directly.)_

---

## SYM-003 — `GC.WaitForPendingFinalizers()` hangs

**Date:** 2026-05-28 (step113-followup)
**Tier:** CoreCLR-hosted
**Status:** open, not yet investigated

**Observation:** A probe doing
```csharp
GC.Collect();
GC.WaitForPendingFinalizers();   // <-- hangs here
GC.Collect();
```
at the bottom of a deep managed recursion hung the kernel (QEMU stayed
open, not a triple fault). Sampling RIP via QMP `info registers` showed
it MOVING through CoreCLR GC/sync code (`GCHolderBase::EnterInternalCoop`
threads.h:4649 and neighbours) -- a busy/blocking wait, not a hard
deadlock at one instruction. CR2=0 (no fault).

Removing just the `WaitForPendingFinalizers()` line made the same probe
pass cleanly (now `GC FRAMEREG_REL refs across Collect [OK]`, census
OK=50). So the hang is specifically in the finalizer-drain wait, NOT in
GC.Collect / FRAMEREG root reporting (which work fine).

**Hypothesis:** our CoreCLR-hosted finalizer thread either (a) never
starts, (b) starts but never drains the finalizer queue, or (c) drains
but never signals the completion event that `WaitForPendingFinalizers`
blocks on. `EnterInternalCoop` in the RIP sample suggests the calling
thread is parked in a coop-mode GC-safe wait expecting the finalizer
thread to make progress that never comes.

**Important context (confounding cleared):** this was initially
mis-attributed to the step72 RBP override during a "is the override
redundant?" experiment. The probe was confounded -- it tested FRAMEREG
slot reporting AND WaitForPendingFinalizers at once. The hang is the
finalizer wait; the override question remains UNTESTED cleanly (override
left ON, as it has been since step72 -- working, not touched further).

**To investigate (when picked up):**
- Does the finalizer thread get created at CoreCLR startup? Grep for
  `FinalizerThread` / `GCHeap::CreateFinalizerThread` activity in a
  verbose boot log.
- Is the finalizer-done event (`hEventFinalizerDone` /
  `FinalizerThread::FinalizerThreadWait`) signaled? Check the
  fork's finalizer thread wait/wake wiring against our PAL
  Event/WaitForSingleObject.
- Does a plain `new object()` with a finalizer ever get its finalizer
  run at all (separate from the Wait)? If finalizers never run, the
  whole subsystem is the gap; if they run but Wait doesn't return, it's
  the completion-event signaling.

**Why parked:** finalizers are not on the current critical path; most
managed code doesn't call WaitForPendingFinalizers. Logged so the next
finalizer-dependent workload (using/Dispose patterns relying on GC
finalization, IDisposable-heavy code) has a known starting point.
