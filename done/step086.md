# Step 86 — Phase E7: Process abstraction landed

**Status:** Phase E7 closes. Logical Process container with PID,
lifecycle state, exit code, and thread ownership. Two concurrent
Processes acceptance per `docs/threading-architecture.md §15` runs
green: both Launch, alternate via cooperative Yield, both call
`Process.Exit(code)`, main `WaitForExit`s both, exit codes propagate
intact (0x1A and 0x2B), distinct PIDs.

`probe_report.ps1` totals after E7: **OK 52, VALUE 3, FAIL 0, HALT 0**.
PAL/OS census OK=20 DEG=2 FAIL=22 unchanged from pre-E1 — zero
cumulative regression across E1..E7.

## Архитектурное замечание

Per `§10` Process is **purely logical** — no MMU isolation, no
per-process CR3. The structure tracks:

- PID (assigned by `ProcessTable.s_nextPid++`)
- Name (diagnostic only)
- Lifecycle state (Active / Exiting / Zombie)
- Exit code
- Owned kernel.Threads (singly-linked via `Thread.NextInProcess`)
- Registry link (`Process.Next`)

The concurrent-execution requirement of §15 is met at the **logical
level**: both Processes coexist in the table, both make progress before
either finishes, both report Zombie. Real concurrency at the same VA
(e.g., two ELF apps both linked at `0x400000`) requires per-process CR3
— deferred to Phase F if/when ELF concurrency becomes mainline. User
context: ELF / NativeAOT apps may not be in production at all (CoreCLR
hosted code is the prod target), so the abstraction matters more than
the ELF concurrency demo.

The probe uses pure kernel threads with two distinct entry functions
(`WorkerAEntry`, `WorkerBEntry`) — both share the kernel address
space but logically belong to different Processes via
`Thread.OwnerProcess`.

## Data layout

`Process` (managed class):

```csharp
public uint Id;
public string? Name;
public ProcessLifecycle State;   // Active | Exiting | Zombie
public int ExitCode;
public Thread? FirstThread;       // singly-linked via Thread.NextInProcess
public uint ThreadCount;
public Process? Next;             // ProcessTable registry link
```

`ProcessLifecycle` enum is intentionally distinct from the existing
`OS.Kernel.Process.ProcessState` (None/Ready/Running/Exited/Failed) —
that one is ELF-launcher bookkeeping; this is the §10 lifecycle. Both
coexist until the ELF launcher refactor (Phase E8 if it lands).

`Thread.cs` adds:

```csharp
public Process? OwnerProcess;
public Thread? NextInProcess;
```

`Scheduler.Spawn(entry, stackBytes, Process? owner = null)` — new
optional 3rd arg. Non-null links the new thread into
`owner.FirstThread` (head insert; cooperative single-CPU so no lock).

`Process.Launch(name, entry, stackBytes=0) -> Process?`:
1. `ProcessTable.Allocate(name)` → new Process with fresh PID.
2. `Scheduler.Spawn(entry, stackBytes, p)` → primary kernel.Thread.
3. Return Process (caller can WaitForExit or fire-and-forget).

`Process.Exit(int code)`:
1. Sets `p.ExitCode = code`, `p.State = Zombie`.
2. Calls `Scheduler.Exit()` — current thread terminated, never returns.

`Process.WaitForExit(uint timeoutMs = 5000)`:
- Yield-loops until `this.State == Zombie` or HPET timeout expires.

`ProcessTable` registry:
- `Allocate(name)` — bumps PID, prepends to singly-linked list.
- `Unregister(p)` — O(N) remove.
- `FindById(pid)` — O(N) lookup.
- `LiveCount()` — diagnostic count of non-Zombie processes.

## R-list note

Section 13's reentrancy audit lists no E7-specific items beyond R3
(`ExInfo.s_pExInfoHead` static → per-thread via TEB). E7 doesn't touch
that path — kernel threads in this probe don't throw. R3 carries over
to Phase F.

## Files

### New (3)
- `OS/src/Kernel/Threading/Process.cs`
- `OS/src/Kernel/Threading/ProcessTable.cs`
- `OS/src/Kernel/Threading/ProcessProbe.cs`

### Modified (4)
- `OS/src/Kernel/Threading/Thread.cs` (+ `OwnerProcess`, `NextInProcess`)
- `OS/src/Kernel/Threading/Scheduler.cs` (Spawn accepts optional Process owner)
- `OS/src/Kernel/Diagnostics/Probes.cs` (+ `ProcessSpawn` gate)
- `OS/src/Boot/BootSequence.cs` (probe call after AllocStress)
- `tools/probe_report.ps1` (process probe matcher)

## Result

```
[INFO] process probe start
[INFO]   proc A iter 0
[INFO]   proc B iter 0
[INFO]   proc A iter 1
[INFO]   proc B iter 1
[INFO]   proc A iter 2
[INFO]   proc B iter 2
[INFO] process probe: pidA=N exitA=0x1A pidB=M exitB=0x2B iters=3/3 -- ok
```

probe_report.ps1: 52 OK / 3 VALUE / 0 FAIL / 0 HALT. PAL/OS census
OK=20 DEG=2 FAIL=22. EH L1..L17 + Phase E1..E6 + drivers + launcher
4/4 OK + CoreCLR all green.

## Что дальше

Per `docs/threading-architecture.md §15`:

- **E8**: ELF thread API in AppSDK (`SharpOSHost_CreateThread`,
  `SharpOSHost_JoinThread`, etc.). **Status given user direction**:
  ELF / NativeAOT apps may not be in production. We have the kernel-
  side primitives (Phase E4-E7). The AppSDK glue is a thin shim. Skip
  unless / until a specific scenario demands it.

- **E9** (priority for prod): CoreCLR PAL routing — full §12 surface.
  Reopen D5 = ABORT_FATAL stubs in `pal/sharpos/crt_imp_stubs.cpp`,
  replace with real implementations using our E4-E7 primitives.
  Acceptance: `new Thread(() => Console.WriteLine("hi")).Start(); Join()`
  works. This is the bigger lift but aligned with the prod scenario
  (CoreCLR hosted code).

Suggesting E9 next; E8 deferred / on-demand.
