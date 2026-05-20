# Step 84 — Phase E5 LANDED: TimerQueue + Sleep + Event + Semaphore

**Status:** Phase E5 closed (deadlined wait + scheduler-aware
blocking primitives). All three E5 probes green:

- **SleepProbe**: 3 × `Sleep(50ms)` — all iterations elapsed within
  `[45..80]` ms band; `pass=3/3 fail=0 -- ok`.
- **EventProbe**: manual-reset event, waiter blocks, main `Sleep(30ms)`
  + `Set()`; wake latency `0 ms` (well under 30ms bound) `-- ok`.
- **SemaphoreProbe**: 3 waiters on 0-count semaphore; `Release(2)`
  wakes 2; `Release(1)` wakes 1; `residualCount=0 -- ok`.

Probe totals after E5: `OK 50, VALUE 3, FAIL 0, HALT 0`. Zero
regression elsewhere (EH L1..L17 + Phase E4 ping-pong + drivers +
launcher + CoreCLR PAL/OS census all unchanged).

## Архитектура

`docs/threading-architecture.md §7` уже описывает дизайн (TimerQueue
+ Event/Semaphore). Этот шаг реализует minimum-viable вариант с
двумя сознательными отложениями:

1. **IRQ-driven HPET wake не делаем** в E5. Cooperative single-CPU
   обходится polling-yield внутри `Scheduler.Yield`: `DrainExpired`
   запускается **первой** строкой каждого Yield, поэтому любой
   цикл yield'ов (main waiting on a worker) обнаруживает истекший
   deadline на следующем заходе. Sage-2 lost-wakeup hazard
   (`xchg(&deadlineDue, 0)`) понадобится только когда введём
   реальный IRQ; пока флаг отсутствует. Документировано в §7 после
   sage-2 review.
2. **Spin-when-Waiting** в Yield. Если current.State=Waiting
   и нет других Runnable, Yield входит в tight loop:
   `DrainExpired + DequeueRunnable` до пока что-то не появится.
   Без HLT — `HLT` без IRQ не разбудится. Заменится на HLT+IRQ
   в E6/F. Сейчас приемлемо — single-CPU, никто другой не страдает.

## Data layout

`Thread.cs`:
```csharp
public Thread? TimerNext;     // TimerQueue link
public Thread? WaitNext;      // Event/Semaphore wait-list link
public ulong DeadlineTicks;   // HPET tick value
```

Поток находится **at most one wait list** в каждый момент
(TimerQueue или конкретный Event/Semaphore wait queue), поэтому
двух одно-связных pointer'ов достаточно.

`TimerQueue`:
- Singly-linked sorted by `DeadlineTicks` ascending.
- `Schedule(t, deadline)` — O(N) insert (acceptable при low thread
  count — пересмотрим heap'ом в E10 ThreadPool).
- `DrainExpired(now)` — pops все expired entries, для каждого
  зовёт `Scheduler.WakeFromWait` (direct call, не callback — нет
  Action<T> в std/no-runtime).
- `Cancel(t)` — для будущего timeout-aware Event.Wait.

`Event`:
- Manual или auto reset.
- Manual: `Set()` будит ВСЕХ waiters, `IsSet` остаётся true.
- Auto: `Set()` будит ОДНОГО (LIFO); если ноль waiters, флаг
  latch'ится в IsSet для следующего Wait.
- Wait list через `Thread.WaitNext`.

`Semaphore`:
- `Count` + `Max` + wait list через тот же `WaitNext`.
- `Wait()` — если Count>0, decrement и return. Иначе block.
- `Release(n)` — пробуждает до n waiters (каждый "consume" один
  permit), surplus идёт в Count.

`Scheduler` дополнения:
- `Sleep(uint ms)` — convert через `Hpet.FrequencyHz / 1000`,
  `TimerQueue.Schedule(curr, now + ms * ticksPerMs)`,
  `curr.State = Waiting`, `Yield()`. После Yield возврата =
  deadline истёк, мы снова Running.
- `WakeFromWait(t)` — guards against double-wake (idempotent если
  t.State != Waiting); переход в Runnable + enqueue.
- `Yield` переработан:
  - `DrainExpiredTimers()` перед dequeue.
  - Если `next == null` и `curr.State != Waiting` → early
    return (main yield loop = no-op fast path).
  - Если `next == null` и `curr.State == Waiting` → spin
    (drain + dequeue) до пробуждения.
  - **Self-switch elision**: если `next == curr` (мы единственные
    Runnable и сами себя re-enqueued), пропускаем CoopSwitch
    shellcode и просто возвращаемся.

## Что чуть не сработало (и фикс)

### Bug 1: SleepProbe FAIL pass=1/3

Первый прогон: `sleep iter 0 elapsed=50 ms` потом тишина и
"`sleep probe: pass=1/3 fail=0 -- FAIL`". Iter 1+2 не запускались.

Корень: `safety = 100_000` yield counter в main probe loop кончался
ДО того как worker мог сделать вторую и третью итерацию. Main
yield (fast path, `next == null`, return immediately) сравнительно
быстрый (1-3 μs на этой системе), 50ms сна = ~25-50k yields per
iteration. На 3 итерации не хватало бюджета.

Фикс: безразмерный yield-counter → **HPET-based deadline**.
1-секундный бюджет покрывает 3×50ms сна с большим запасом, и
тайминг не зависит от yield-speed:

```csharp
ulong freq = Hpet.FrequencyHz;
ulong timeoutDeadline = Hpet.ReadCounter() + freq;   // 1 second
while (s_doneFlag == 0 && Hpet.ReadCounter() < timeoutDeadline)
    Scheduler.Yield();
```

Та же замена применена в `EventProbe` для consistency (даже хотя
он прошёл раньше — там сценарий быстрее, успел уложиться в 100k).

### Bug 2: CS0128 duplicate `freq`

После добавления HPET-timeout в `EventProbe.Run`, конфликт с
существующим `ulong freq` в блоке latency-calc. Удалил дубль.

## Files

### New (6)

- `OS/src/Kernel/Threading/TimerQueue.cs`
- `OS/src/Kernel/Threading/Event.cs`
- `OS/src/Kernel/Threading/Semaphore.cs`
- `OS/src/Kernel/Threading/SleepProbe.cs`
- `OS/src/Kernel/Threading/EventProbe.cs`
- `OS/src/Kernel/Threading/SemaphoreProbe.cs`

### Modified (5)

- `OS/src/Kernel/Threading/Thread.cs` — `Waiting` state +
  TimerNext/WaitNext/DeadlineTicks
- `OS/src/Kernel/Threading/Scheduler.cs` — Sleep/WakeFromWait/
  DrainExpiredTimers, Yield reworked (drain, spin, self-switch)
- `OS/src/Kernel/Diagnostics/Probes.cs` — 3 new gates
- `OS/src/Boot/BootSequence.cs` — 3 new probe calls
- `tools/probe_report.ps1` — recognize new probe lines

## Result

```
[INFO] sleep probe start
[INFO]   sleep iter 0 elapsed=50 ms
[INFO]   sleep iter 1 elapsed=50 ms
[INFO]   sleep iter 2 elapsed=50 ms
[INFO] sleep probe: pass=3/3 fail=0 -- ok
[INFO] event probe start
[INFO] event probe: latency=0 ms (set->wake) -- ok
[INFO] semaphore probe start
[INFO] semaphore probe: release(2)->wake=2 release(1)->wake=3 residualCount=0 -- ok
```

probe_report.ps1: **OK 50, VALUE 3, FAIL 0, HALT 0**. PAL/OS census
OK=20 DEG=2 FAIL=22 идентично предыдущим этапам — нулевая регрессия.

## Что дальше

**Phase E6** — allocator/page/VM locks + reentrancy R1-R7. Сейчас
KernelHeap.Alloc, GcHeap.AllocateRaw, VirtualMemory.MapFixed, Pager.Map
работают **without lock** (single-thread invariant из pre-E кода).
Multi-thread alloc stress (4 threads × 10000 allocs) гарантированно
порушит state. Lock'и должны быть scheduler-aware (Wait/Event на
busy) — теперь у нас есть инфраструктура.

R-list по `threading-architecture.md §13` Pass 1:

- R1: `Interlocked.CompareExchange` non-atomic → `lock cmpxchg`
  shellcode (E3 уже даёт `X64Asm.CmpXchg64`)
- R2: `ClassConstructorRunner` CAS-spin early-return → restore
  full CAS-loop
- R3: `ExInfo.s_pExInfoHead` static → per-thread via TEB
- R5: `KernelHeap.Alloc` без lock → scheduler-aware blocking lock
- R6: `GcHeap.AllocateRaw` без lock → same
- R7: `pal/sharpos/crt_imp_stubs.cpp` `GetLastError` placeholder →
  real per-thread via TEB+0x68

Acceptance E6: 4 worker threads × 10000 allocations без corruption.
