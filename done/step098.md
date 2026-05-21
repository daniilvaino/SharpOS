# Step 98 — Phase E9.a: CoreCLR PAL routing LANDED (`new Thread().Start()/Join()` on bare metal)

**Date:** 2026-05-21
**Status:** ✅ Acceptance hit. `Thread.Start`/`Join` round-trips through forked CoreCLR on bare-metal SharpOS.

## Result

```
PAL/OS census..................... OK  (OK=22  DEG=2  FAIL=22)
--- totals ---
  OK       52
  VALUE    3
```

`OK=22 (+2)` vs. prior E1-E4 baseline (`OK=20`). The two new passes are `new Thread + Join` and `Thread.Sleep(1)` from the `normal-hello` Threading section. Zero regressions in the other sections.

## Acceptance shape

```csharp
Probe("new Thread + Join", () =>
{
    int flag = 0;
    var t = new Thread(() => { flag = 42; });
    t.Start();
    t.Join();
    if (flag != 42) throw new Exception(...);
});
```

Executed end-to-end through stock BCL `System.Threading.Thread` → CoreCLR `ThreadNative_Start` → kernel scheduler → managed user delegate JIT'd and ran on the new thread → `JoinEvent` set on exit → main returns from `Join()` with `flag == 42`.

## Architecture

```
                 boot thread                     hosted thread
  ┌─────────────────────────────────┐     ┌────────────────────────────┐
  │ new Thread(λ) .Start() .Join()  │     │                            │
  │   │                             │     │                            │
  │   ├─ CoreCLR Thread::CreateNew  │     │                            │
  │   │   → ::CreateThread(         │     │                            │
  │   │       flags=                │     │                            │
  │   │       CREATE_SUSPENDED |    │     │                            │
  │   │       STACK_RESERVATION)    │     │                            │
  │   │                             │     │                            │
  │   ├─ kernel:                    │     │                            │
  │   │   SharpOSHost_CreateThread  │     │                            │
  │   │   → SpawnHosted             │     │                            │
  │   │     - Spawn(startRunnable   │     │                            │
  │   │             = !suspended)   │     │                            │
  │   │     - CoreClrTeb.Allocate   │     │                            │
  │   │     - ContextBlock+8 = TEB  │     │                            │
  │   │   thread in `New` state     │     │                            │
  │   │                             │     │                            │
  │   ├─ ::ResumeThread             │     │                            │
  │   │   → SharpOSHost_ResumeThread│     │                            │
  │   │   → MakeRunnable            │     │                            │
  │   │     state → Runnable        │     │                            │
  │   │     enqueue                 │     │                            │
  │   │                             │     │                            │
  │   ├─ ::WaitForSingleObject      │     │                            │
  │   │   (thread handle, INFINITE) │     │                            │
  │   │   → kernel HandleTable      │     │                            │
  │   │   → JoinEvent.Wait()        │     │                            │
  │   │     transition Waiting,     │     │                            │
  │   │     yield                   │     │                            │
  │   │     ◇━━━━━━━━━━ Scheduler ━━━━▶  HostedTrampoline (first run)  │
  │   │                             │     │   - gs:base = thread TEB    │
  │   │                             │     │     (CoopSwitch shellcode)  │
  │   │                             │     │   - call entry              │
  │   │                             │     │     = KickOffThread         │
  │   │                             │     │                            │
  │   │                             │     │   KickOffThread:           │
  │   │                             │     │   - HasStarted():          │
  │   │                             │     │     - SetStackLimits       │
  │   │                             │     │       (per-thread bounds)  │
  │   │                             │     │     - SetThreadStackGuar.  │
  │   │                             │     │       (no-op stub)         │
  │   │                             │     │     - SetupTLSForThread    │
  │   │                             │     │     - InitThread           │
  │   │                             │     │     - SetThread            │
  │   │                             │     │     - TransferStartedTh.   │
  │   │                             │     │   - ManagedThreadBase::    │
  │   │                             │     │       KickOff(             │
  │   │                             │     │       KickOffThread_Worker)│
  │   │                             │     │     - JIT λ (b__50)        │
  │   │                             │     │     - prestub patches      │
  │   │                             │     │       call site            │
  │   │                             │     │     - jump to JIT'd code   │
  │   │                             │     │     - λ body: flag = 42    │
  │   │                             │     │   - PulseAllHelper SKIP    │
  │   │                             │     │     (SHARPOS bypass)       │
  │   │                             │     │   - DestroyThread          │
  │   │                             │     │   - return                 │
  │   │                             │     │ HostedTrampoline:          │
  │   │                             │     │   - JoinEvent.Set() ━━━━▶  │
  │   │                             │     │   - Scheduler.Exit()       │
  │   │                             │     │                            │
  │   ◀━━━━ Scheduler wakes main ━━━━│     │                            │
  │   from JoinEvent.Wait()         │     │                            │
  │                                 │     │                            │
  │ assert flag == 42 ✓             │     │                            │
  └─────────────────────────────────┘     └────────────────────────────┘
```

## Roots fixed (each was its own halt)

Phase E9.a was a sequence of seven distinct halt points, each closed by a specific surgical fix. The diagnostic strategy was checkpoint-style `SharpOSHost_DebugPrint` markers inside CoreCLR's thread-init chain — once one halt was fixed the next was reached and the next marker became the diagnostic target.

### 1. `CREATE_SUSPENDED` ignored — race with post-init wiring

`vm/threads.cpp:2145`: `dwCreationFlags = CREATE_SUSPENDED | STACK_SIZE_PARAM_IS_A_RESERVATION` always. CoreCLR expects to create thread, complete `SetThreadHandle` / `m_OSThreadId` / `AllocHandles` / `Thread::ApartmentState` post-init on the creator, then `ResumeThread`.

Our pre-fix `SharpOSHost_CreateThread` dropped the flags arg entirely and always enqueued the new thread immediately. Race: trampoline ran before `SetThreadHandle` set `m_ThreadHandle`, so `HasValidThreadHandle()` in `HasStarted` aborted.

**Fix:** `SharpOSHost_CreateThread` now accepts `dwCreationFlags`. When `CREATE_SUSPENDED` bit (0x4) is set, `Scheduler.SpawnHosted(suspended: true)` puts the thread in `ThreadState.New` and does NOT enqueue it. New `SharpOSHost_ResumeThread` calls `Scheduler.MakeRunnable(t)` which transitions `New → Runnable` and enqueues. Fork-side `ResumeThread` now routes to it instead of returning 0.

### 2. `DebuggerRCThread::ThreadProcStatic` — silent #PF in Win32-event poll

`debug/ee/rcthread.cpp:1368`: the only CoreCLR callsite using `flags = CREATE_SUSPENDED` alone (no stack reservation). The debugger helper thread polls `DebuggerIPCControlBlock::m_leftSideUnmanagedWaitEvent` — but on SharpOS there's no debugger transport, the IPC block is unbacked, and the helper #PFed inside the wait loop.

In a single-CPU cooperative scheduler, ANY thread #PFing halts the entire system (no other CPU to keep going). So even though the helper isn't critical, it MUST not exist.

**Fix:** `DebuggerRCThread::Start()` short-circuits with `return S_OK` under `TARGET_SHARPOS`. The helper is debugger-attach-only; never needed for normal execution.

### 3. Fake-handle `WaitForSingleObject` busy-spin

The finalizer thread (E9.a brings it online too) calls `WaitForFinalizerEvent` → `WaitForSingleObject(hEventFinalizer)`. Pre-E9 the fake-handle path returned `WAIT_OBJECT_0` unconditionally (lie: "signaled"). On the cooperative scheduler this becomes a tight spin: wait→return→drain (nothing)→wait→return→drain...

**Fix:** `ThreadStubs.WaitForSingleObject` now yields once before returning `WAIT_OBJECT_0` for fake handles when `timeoutMs != 0`. Polling case (`ms == 0`) preserved as immediate non-yielding return.

### 4. `SharpOSHost_GetStackBounds` returned the whole 419 MB GcHeap region

`Thread::GetStackUpperBound/LowerBound` call `SharpOSHost_GetStackBounds(&base, &limit)`. Pre-fix it read current SP and walked the UEFI memmap to find the containing region. For hosted threads, the 1 MiB stack was carved out of the conventional-RAM GcHeap region (~419 MB), so the function returned `[GcHeap_start, GcHeap_end)` — `m_CacheStackLimit` ended up hundreds of MB below the real stack.

Subsequent `SetStackLimits` → `CLRSetThreadStackGuarantee` probed bytes inside the bogus huge "stack" but outside the real 1 MiB mapping → #PF on unmapped page.

**Fix:** `StackBounds.GetStackBounds` adds a per-thread fast path BEFORE the BigStack/UEFI fallback. If `Scheduler.Current.StackBase/StackTop` are set and the current SP falls inside `[lo, hi)`, return that exact range. Falls through to the prior behavior only for boot thread.

### 5. `::SetThreadStackGuarantee` unresolved → jump to NULL

`Thread::CLRSetThreadStackGuarantee` calls `::SetThreadStackGuarantee(&uGuardSize)` unconditionally in Debug builds (`IsSetThreadStackGuaranteeInUse` returns TRUE under `#ifdef DEBUG`). Our PAL had no stub, the fork builds with `CMAKE_CXX_STANDARD_LIBRARIES ""` (no kernel32.lib), and the import was left unresolved — the IAT slot held a null pointer, the call jumped to 0 → instruction-fetch #PF → silent halt.

**Fix:** added a `SetThreadStackGuarantee(uint32_t*)` no-op stub in `crt_imp_stubs.cpp` returning success without touching anything (we have no guard pages today; clean SO behavior is a Phase-E follow-up). Resolver entry added too.

### 6. Shared TEB → `t_ThreadType` polluted across hosted threads (closed in E9.b)

Pre-E9.b all hosted threads shared the single global TEB installed by `CoreClrProbe.SetupTebFacade` at boot. CoreCLR uses C++ `thread_local` for variables like `t_ThreadType` (compiles to `gs:[0x58][_tls_index]`), so any thread writing those polluted ALL threads — wrong identity → hot `_ASSERTE` chain at `debugger.cpp:15558`.

**Fix (already landed via E9.b CoreClrTeb):** every hosted thread gets a fresh TEB (4 KiB) + 64-slot TLS pointer array + per-thread `tls_block` initialized from the PE TLS template. `CoopSwitch` shellcode loads `ContextBlock+0x08` (Teb pointer) and `wrmsr`s it into `IA32_GS_BASE` on every switch-in. Skip the swap if `Teb == 0` (kernel-only threads with no need for CoreCLR TLS isolation).

### 7. `PulseAllHelper` — managed-monitor PulseAll needs syncblock contended path

After the user delegate returns inside `ManagedThreadBase::KickOff`, `KickOffThread` calls `PulseAllHelper(pThread)` which acquires the managed Thread object's monitor and `PulseAll`s. This wakes any `Monitor.Wait` on the Thread object (rare, used by some `Thread.Join` impls historically).

The contended-monitor fallback uses `AwareLock` which needs Win32 events / Mutex PAL surface we haven't bridged. On hosted thread this silently #PFs.

Our `Join()` does NOT use managed-monitor wakeup — the boot thread blocks in `WaitForSingleObject(thread_handle)` which routes to `JoinEvent.Wait()`, and the trampoline sets `JoinEvent` on thread exit. So `PulseAllHelper` is pure overhead for our scenario.

**Fix:** `PulseAllHelper(pThread)` skipped under `TARGET_SHARPOS` in `comsynchronizable.cpp`. When managed `Monitor.Wait/Pulse/PulseAll` on arbitrary objects becomes a requirement (Phase E10+), the syncblock contended path will need real Mutex/Event PAL surface and PulseAllHelper can be reinstated.

## Files changed

### Kernel (`OS/`)

- `OS/src/PAL/SharpOSHost/ThreadStubs.cs` *(new)* — `SharpOSHost_CreateThread` (honors `CREATE_SUSPENDED`), `SharpOSHost_ResumeThread`, `SharpOSHost_ExitThread`, `SharpOSHost_GetCurrentThread{Id}`, `SharpOSHost_WaitForSingleObject` (yields on fake-handle non-poll), `SharpOSHost_CloseHandle`, `SharpOSHost_Sleep`, `SharpOSHost_SwitchToThread`.
- `OS/src/PAL/SharpOSHost/HandleTable.cs` *(new)* — 256-slot Win32-style handle table. Explicit `Init()` (avoids ClassConstructorRunner trap). `Alloc(obj)` / `Lookup(handle)` / `LookupAs<T>(handle)` / `Free(handle)` / `LiveCount()`.
- `OS/src/Kernel/Threading/CoreClrTeb.cs` *(new)* — per-thread TEB+TLS allocator. `EnsureTemplate()` walks the PE TLS directory once for `_tls_index` and the template data. `Allocate(stackBase, stackLimit)` returns a fresh 4 KiB TEB with the NT_TIB header populated + 64-slot TLS pointer array + per-thread tls_block cloned from template.
- `OS/src/PAL/SharpOSHost/StackBounds.cs` — per-thread fast path before BigStack/UEFI fallback. Returns the kernel's `[StackBase, StackTop)` range for hosted threads.
- `OS/src/Kernel/Threading/Scheduler.cs` — `Spawn(entry, stackBytes, owner, startRunnable)` with `startRunnable=false` leaving thread in `New`. New `MakeRunnable(t)` for resume. `SpawnHosted` takes `suspended` parameter, allocates per-thread TEB via `CoreClrTeb.Allocate`, writes Teb pointer to `ContextBlock+0x08`. `HostedTrampoline` reads `HostedEntry`/`HostedParam` from `s_current`, invokes the entry, captures exit code, signals `JoinEvent`, calls `Scheduler.Exit()`.
- `OS/src/Kernel/Threading/Thread.cs` — added `HostedEntry` (`delegate* unmanaged<void*, uint>`), `HostedParam` (`void*`), `HostedExitCode` (`uint`), `JoinEvent` (`Event?`), `HasExited` (`bool`).
- `OS/src/Hal/X64Asm.cs` — `CoopSwitch` shellcode (62 B, was 39 B) adds gs-base swap: `mov rax,[rdx+8]` / `test rax,rax` / `jz .skip_gs` / `mov rdx,rax` / `shr rdx,32` / `mov ecx,0xC0000101` / `wrmsr`.
- `OS/src/Kernel/Diagnostics/CoreClrProbe.cs` — `SetupTebFacade` refactored to use `CoreClrTeb.Allocate` for the boot thread; writes the TEB pointer into `Scheduler.Current.Teb` and `ContextBlock+0x08` so a swap back to main works.
- `OS/src/Boot/BootSequence.cs` — `OS.PAL.SharpOSHost.HandleTable.Init();` before CoreCLR session brings any `SharpOSHost_*` call online.
- `OS/OS.csproj` — `<Nullable>annotations</Nullable>` (silences CS8632 in new SharpOSHost code).

### Fork (`dotnet-runtime-sharpos/`)

- `src/coreclr/pal/sharpos/crt_imp_stubs.cpp` — `SharpOSHost_CreateThread` weak fallback signature updated to include `uint32_t creationFlags`. New `SharpOSHost_ResumeThread` weak fallback. `CreateThread` forwards `dwCreationFlags` instead of dropping it. `ResumeThread` routes to `SharpOSHost_ResumeThread`. New `SetThreadStackGuarantee` no-op stub + resolver entry. `DebugBreak` changed from CRT_STUB (panic) to log-and-continue (so non-fatal `_ASSERTE` paths can survive).
- `src/coreclr/debug/ee/rcthread.cpp` — `DebuggerRCThread::Start()` returns `S_OK` immediately under `TARGET_SHARPOS`.
- `src/coreclr/vm/comsynchronizable.cpp` — `PulseAllHelper(pThread)` skipped under `TARGET_SHARPOS` (managed-monitor PulseAll needs syncblock contended PAL we haven't bridged; our Join uses kernel JoinEvent directly).
- `src/coreclr/vm/threads.cpp`, `src/coreclr/vm/prestub.cpp` — Verbose-gated diagnostic markers (`[KOT]/[HS]/[JCCLEW]/[JCCL]/[PIBC]/[DoPrestub]/[PSW]/[KOT_Worker]`) wired through the fork's existing `SharpOSHost_DebugPrint`. No-op when `SharpOSHostDiagnostics.Verbose = false` (default). Flip Verbose=true in `Diagnostics.cs` (no fork rebuild) when future thread-init paths halt.

## Diagnostic methodology

This step's halt-chasing technique generalizes: when a hosted-thread init path halts silently, flip `SharpOSHostDiagnostics.Verbose = true` (kernel-side, no fork rebuild) and inspect the last `[*]` marker that printed. The marker chain `[KOT] entry → pre-HasStarted → [HS] entry → preemp → SetStackLimits → SetupTLS → InitThread → PrepareApartment → SetThread → TransferStarted → [KOT] HasStarted ok → pre-ManagedThreadBase::KickOff → [KOT_Worker] entry → pre-CALL_MANAGED → post-CALL_MANAGED → [KOT] post-ManagedThreadBase::KickOff → [PulseAllHelper skipped (SHARPOS)] → post-GCX_PREEMP_NO_DTOR → post-ClearThreadCPUGroupAffinity → post-DestroyThread → returning 0` covers the full hosted-thread bring-up.

Adjacent JIT chain markers (`[prestub] -> JCCLEW → [JCCLEW] entry → pre-JitCompileCodeLocked → [JCCL] entry → pre-UnsafeJitFunction → UnsafeJitFunction returned → JitCompileCodeLocked returned → [PIBC] JitCompileCode returned → returning pCode → [DoPrestub] PublishVersionableCode returned → pre-DoBackpatch → DoBackpatch returned → returning pCode → [PSW] DoPrestub returned → post-UNINSTALL_UNWIND_AND_CONTINUE → post-UNINSTALL_MANAGED_EXCEPTION → post-ThePreStubPatch → post-pPFrame->Pop, returning`) cover prestub→JIT→backpatch→user-code transit.

## What's deferred

These remain `[Skip]` in the census and block Phase E10+:

- **`ThreadPool.QueueUserWorkItem`** — needs ThreadPool worker queue + Task machinery.
- **`Task.Run + .Result`** — async/await JIT helpers (`AsyncTaskMethodBuilder` etc.), Task continuation chains.
- **`Timer (1ms)`** — needs IOCP-style timer wheel or dedicated timer thread.
- **Managed `Monitor.Wait`/`Pulse`/`PulseAll` on arbitrary objects** — syncblock contended fallback (`AwareLock`) needs Mutex/Event PAL surface and `WaitOnAddress`.
- **`SuspendThread` / cooperative GC suspension** — needs precise thread-suspend mechanism (currently we have only cooperative yield, no preemption).

## Next step

Phase E9.b/c/d: more PAL synchronization surface (Event/Semaphore/Mutex via HandleTable, CriticalSection, SRW locks, `WaitOnAddress`, IOCP), then Phase E10 ThreadPool implementation. The HandleTable + Scheduler primitives from this step are the foundation.
