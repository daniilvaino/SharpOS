# Step 100 — Phase E9.b kernel-side: Event/Semaphore/Mutex via HandleTable

**Date:** 2026-05-21
**Commit:** `4dd25094f80183f640674576eb739bd46fc89198`
**Status:** ✅ Census `OK=37 DEG=2 FAIL=7` (unchanged — no probe specifically exercises Monitor.Wait/Pulse, but CoreCLR init now creates real handles at startup without tripping fake-handle dispatch gaps).

## Result

Pre-E9.b lie ("every `WaitForSingleObject` returns `WAIT_OBJECT_0`")
replaced with `HandleTable`-backed real synchronization. Foundation for
`Monitor.Wait/Pulse` and `Task` continuation chains.

Paired with fork-side step 100 (`sharpos/coreclr-port 35670979d35`).

## What landed

### `OS/src/Kernel/Threading/Win32Mutex.cs` (new)

Reentrant Win32-semantics mutex (`Owner` + `RecursionCount` +
abandoned detection). Distinct from `OS.Kernel.Threading.Mutex`
(Phase E6, non-reentrant, for alloc-stress test infrastructure).

`Wait()`:
- unowned → claim (`Owner = curr, count = 1`)
- owned by current → `count++` (recursion)
- owner exited → abandoned: claim ourselves, return code 2
- otherwise → block on wait list; on wake, ownership is transferred

`Release()`: decrement; if `count` reaches 0, hand to next waiter
(`count = 1`) or clear ownership.

Single-CPU cooperative; SMP-ready semantics for E13+.

### PAL bridges (new)

- `EventBridge.cs` — `SharpOSHost_CreateEvent(manualReset, initialState)`
  + `CreateEventEx(flags)` (maps `CREATE_EVENT_MANUAL_RESET` /
  `CREATE_EVENT_INITIAL_SET`), `SetEvent(h)`, `ResetEvent(h)`. Backed
  by `OS.Kernel.Threading.Event` (Phase E5).
- `SemaphoreBridge.cs` — `CreateSemaphore(initial, max)`,
  `ReleaseSemaphore(h, releaseCount, outPrev*)`. Backed by
  `OS.Kernel.Threading.Semaphore` (Phase E5).
- `MutexBridge.cs` — `CreateMutex(bInitialOwner)`, `ReleaseMutex(h)`.
  Backed by `Win32Mutex` above.

### `OS/src/PAL/SharpOSHost/ThreadStubs.cs`

- `WAIT_ABANDONED` (0x80) constant added.
- `WaitForSingleObject` got a `Win32Mutex` branch:
  - **Poll path** (`ms == 0`): unowned or owned-by-current → signaled
    (claim); owner exited → abandoned-then-claim (returns
    `WAIT_ABANDONED`); otherwise `WAIT_TIMEOUT`.
  - **Blocking path**: `Win32Mutex.Wait()` handles all states
    including ownership transfer on wake; returns `WAIT_ABANDONED` if
    the prior owner exited.

## Files (5 changed, +292/-4)

- `OS/src/Kernel/Threading/Win32Mutex.cs` (new, +109)
- `OS/src/PAL/SharpOSHost/EventBridge.cs` (new, +68)
- `OS/src/PAL/SharpOSHost/SemaphoreBridge.cs` (new, +39)
- `OS/src/PAL/SharpOSHost/MutexBridge.cs` (new, +49)
- `OS/src/PAL/SharpOSHost/ThreadStubs.cs` (+31/-4)

## Acceptance

Census `OK=37 DEG=2 FAIL=7` unchanged.

## Next

E9.c (WaitOnAddress / WakeByAddress*) — step 101.
