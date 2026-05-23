# Step 102 — SMP-prep: ThreadKind + Binding + GuardPage + WaitBlock

**Date:** 2026-05-21
**Commit:** `f951b261c17ee1293b289980e0aad1f1a67cd6cf`
**Status:** ✅ Census `OK=37 DEG=2 FAIL=7` unchanged. Zero regression. Guard page didn't trigger on any current census probe (no stack overflow in normal paths). Foundation for E13 SMP work in place.

## Result

Honest correction of the §3 spec deviations called out as SMP-unsafe
shortcuts. Four fixes A/B/C/D from `docs/threading-architecture.md`
§3 + §12.

Paired with fork-side step 102 (`sharpos/coreclr-port 714fad2fc8d`
adds per-thread LastError via `gs:[0x68]`).

## Fix A — Guard page below every Spawn'd thread stack

Stack overflow now **#PFs cleanly** inside an unmapped 4 KiB range,
instead of silently corrupting the adjacent GC heap allocation.

`OS/src/Kernel/Threading/Scheduler.cs`:
- `AllocateStack` now reserves one extra page below the usable stack
  and `Pager.Unmap`'s it — guard page address returned via `out`
  param, stored on `Thread.GuardPage`. Boot-thread skips (UEFI stack,
  no guard owned by us).

`OS/src/Boot/EH/HwFaultBridge.cs`:
- On #PF (vector 14): check if `CR2` is inside the current thread's
  `GuardPage` range. If yes, print `STACK OVERFLOW` with `thread.Id`
  and halt cleanly. Runs **before** the usual managed-exception
  bridge so a guard-page hit doesn't drag us into the EH machinery
  on a by-definition-broken stack.

## Fix B — ThreadKind enum + ManagedThreadBinding separated struct

Kernel scheduler reads only
`Thread.{Id, State, Kind, Context, Stack*, GuardPage, Teb, Entry,
Wait, Owner*}`, CoreCLR-touched code reads only
`Thread.Binding.{HostedEntry, ClrThreadOpaquePtr, HostedExitCode,
JoinEvent, HasExited}`. The inline hosted-fields accessor pattern from
step 98 is gone.

`OS/src/Kernel/Threading/Thread.cs`:
- `ThreadKind` enum `{Kernel, AotApp, CoreClr}`.
- `ManagedThreadBinding` class with `ClrThreadOpaquePtr`,
  `HostedEntry`, `HostedExitCode`, `JoinEvent`, `HasExited`.
- `Thread`: new `Kind`, `GuardPage`, `Wait` (inline), `Binding`
  fields. Removed inline `HostedEntry`/`HostedParam`/`HostedExitCode`/
  `JoinEvent`/`HasExited` (now on `Binding`).

`OS/src/Kernel/Threading/Scheduler.cs`:
- `Spawn` sets `Kind = Kernel`; `SpawnHosted` promotes
  `Kind = CoreClr` and attaches a fresh `ManagedThreadBinding`.
- `HostedTrampoline` reads `HostedEntry` / `ClrThreadOpaquePtr` /
  writes `HostedExitCode` + `HasExited` via `Binding`.

`OS/src/PAL/SharpOSHost/ThreadStubs.cs`:
- `CreateThread` / `ExitThread` / `WaitForSingleObject` thread-branch
  route through `Thread.Binding` instead of the removed inline
  fields.
- `ManagedThreadBinding` null-checks done with explicit
  `if (... != null)` (no `?.` / `??` — those trigger the Roslyn
  nullable-rewriter trap that needs
  `InvalidOperationException(string)` ctor lookup we don't have).

## Fix C — WaitBlock struct grouping wait-state fields

`(Next, TimerNext, Address, Deadline) + WaitKind` tag. Currently
inline-by-value on `Thread`; **at E13 SMP this is the exact field
that switches to `WaitBlock* CurrentWait`** for atomic CAS-swap.
Every consumer (Event/Semaphore/Mutex/Win32Mutex/AddressWait/
TimerQueue) already accesses via `t.Wait.X`; the SMP migration is a
one-line replace per call site.

`OS/src/Kernel/Threading/Thread.cs`:
- `WaitKind` enum `{None, Event, Semaphore, Mutex, Address, Timer}`.
- `WaitBlock` struct (`Next, TimerNext, Address, Deadline, Kind`).
- `Thread.Wait` (inline) replaces `Thread.{WaitNext, TimerNext,
  DeadlineTicks, WaitAddress}`.

Consumers updated:
- `Event.cs` / `Semaphore.cs` / `Mutex.cs` / `Win32Mutex.cs` /
  `AddressWait.cs`: wait sites set
  `curr.Wait.Next = _waitHead + curr.Wait.Kind = <WaitKind>`,
  instead of `curr.WaitNext = _waitHead`. Wake sites read
  `t.Wait.Next`, null `t.Wait.Next + t.Wait.Kind = None` on dequeue.
  `AddressWait` also nulls `Wait.Address`.
- `TimerQueue.cs`: `Schedule` / `DrainExpired` / `Cancel` /
  `NextDeadline` route through `Wait.TimerNext + Wait.Deadline` (was
  direct `TimerNext + DeadlineTicks`). `Wait.Kind = WaitKind.Timer`
  on `Schedule`.

## Fix D — Per-thread LastError via gs:[0x68]

Fork-side step 102 (`sharpos/coreclr-port 714fad2fc8d`).

## Files (10 changed, +283/-112)

- `OS/src/Boot/EH/HwFaultBridge.cs` (+35)
- `OS/src/Kernel/Threading/Thread.cs` (+156/-?)
- `OS/src/Kernel/Threading/Scheduler.cs` (+61/-?)
- `OS/src/Kernel/Threading/AddressWait.cs` (+29/-?)
- `OS/src/Kernel/Threading/TimerQueue.cs` (+46/-?)
- `OS/src/Kernel/Threading/Event.cs` (+15/-?)
- `OS/src/Kernel/Threading/Mutex.cs` (+8/-?)
- `OS/src/Kernel/Threading/Semaphore.cs` (+8/-?)
- `OS/src/Kernel/Threading/Win32Mutex.cs` (+8/-?)
- `OS/src/PAL/SharpOSHost/ThreadStubs.cs` (+29/-?)

## Acceptance

Census `OK=37 DEG=2 FAIL=7` unchanged. Zero regression.

## Next

Phase E11 (ThreadPool / Task / Timer) — step 103.
