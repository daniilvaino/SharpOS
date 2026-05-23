# Step 101 — Phase E9.c kernel-side: WaitOnAddress / WakeByAddress*

**Date:** 2026-05-21
**Commit:** `256331aec7df503e1fe10852bae132364ef2cb7f`
**Status:** ✅ Census `OK=37 DEG=2 FAIL=7` unchanged (no probe specifically exercises WaitOnAddress, but BCL's modern sync paths now have a real backing implementation instead of a CRT trap).

## Result

Address-keyed wait queue for the modern .NET fast-path sync surface.
Foundation for:
- `ManualResetEventSlim`
- `SemaphoreSlim`
- `Monitor` syncblock fast path
- `SpinWait` yields
- `ConcurrentDictionary` slow path

All of which spin briefly then fall back to `WaitOnAddress` before
touching a real `Event`/`Mutex`.

Paired with fork-side step 101 (`sharpos/coreclr-port 4c88778c2a1`).

## What landed

### `OS/src/Kernel/Threading/AddressWait.cs` (new)

64-bucket hash table (`key = (addr >> 3) & 63`) of
`Thread.WaitNext`-linked wait lists. Each thread can be on **at most
one** wait list at a time (TimerQueue / Event-Semaphore-Mutex /
WaitOnAddress); the new `Thread.WaitAddress` field identifies which
bucket the parked thread belongs to, so `WakeByAddress*` can unlink
without scanning every bucket.

`WaitOnAddress(addr, cmpAddr, size, timeoutMs)`:
- **No-wait fast path**: `memcmp(addr, cmpAddr, size) != 0` → return `true`.
- Otherwise park `curr` on the bucket, `Scheduler.Yield()`; on wake
  `WakeByAddress*` has already unlinked and nulled
  `WaitAddress`/`Next`.
- Finite timeouts degrade to infinite (no `TimerQueue` cancel-on-wake
  plumbing yet — matches `WaitForSingleObject` policy).

`WakeByAddressSingle(addr)` — first matching waiter on the bucket.
`WakeByAddressAll(addr)` — every matching waiter.

Single-CPU cooperative — any Wait/Wake sequence is by construction
serialised across threads on this CPU. SMP-ready surface awaits
per-bucket spinlocks in E13+.

Lazy-init via explicit `Init()` (boot wires it; field initializer
would trip `ClassConstructorRunner` per CLAUDE.md).

### `OS/src/Kernel/Threading/Thread.cs`

Added `void* WaitAddress` — non-null only while this thread is parked
in a `WaitOnAddress` wait. `WakeByAddress*` matches on it to identify
the bucket entry.

### `OS/src/PAL/SharpOSHost/WaitOnAddressBridge.cs` (new)

`[RuntimeExport]` wrappers:
- `SharpOSHost_WaitOnAddress`
- `SharpOSHost_WakeByAddressSingle`
- `SharpOSHost_WakeByAddressAll`

### `OS/src/Boot/BootSequence.cs`

`AddressWait.Init()` called alongside `HandleTable.Init()` before the
CoreCLR session brings any `SharpOSHost_WaitOnAddress` call online.

## Files (4 changed, +199/-0)

- `OS/src/Boot/BootSequence.cs` (+4)
- `OS/src/Kernel/Threading/AddressWait.cs` (new, +142)
- `OS/src/Kernel/Threading/Thread.cs` (+8)
- `OS/src/PAL/SharpOSHost/WaitOnAddressBridge.cs` (new, +45)

## Acceptance

Census `OK=37 DEG=2 FAIL=7` unchanged.

## What's deferred

**E9.d (IOCP / GetQueuedCompletionStatus)** — our cooperative
single-CPU ThreadPool design (E10) won't need IOCP as its work-item
dispatch (no async I/O surface to drive completions). If the BCL
ThreadPool insists on IOCP it'll surface as a CRT trap at that time
— it eventually did (step 103 added `IocpBridge.cs` as
LowLevelLifoSemaphore backing).

## Next

SMP-prep cleanup of §3 spec deviations — step 102.
