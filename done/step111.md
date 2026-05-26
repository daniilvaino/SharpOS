# step 111 — Task.Delay (короткий) + DynamicMethod.GetILGenerator на bare metal

## Веха

CoreCLR-hosted census: **OK=43 → 44**, FAIL=7 same.
- `Task.Delay(1).Wait(2s)` — ✅ green (был 🟡 HALT)
- `DynamicMethod.GetILGenerator` — ✅ green (был 🟡 TerminateProcess(COR_E_EXECUTIONENGINE))
- Side-effect от QPC-fix: всё что зависит от `Stopwatch.GetTimestamp / Environment.TickCount64` теперь корректно: SpinWait, MRES.Wait, lock spin classification, TimerQueue scheduling.

Новая регрессия (не пройдена): `Task.Delay(3s).Wait(1s)` — Wait timeout сработал корректно (✅ probe), но **активный TimerQueueTimer переживает probe** и ломает app shutdown (uncaught managed throw → kernel HALT). Long-running Task.Delay shutdown safety — отдельный фронт.

## Корни закрытых проб

### Корень 1 — `QueryPerformanceCounter` инкрементил `static int64_t` на 1 за вызов

`ProcessorIdCache.ProcessorNumberSpeedCheck` (static init `s_isProcessorNumberReallyFast` через `System.Threading.Lock` cctor) внутреннее do-while: `t = QPC()_end - QPC()_start`; условие `while (t < oneMicrosecond=11)`. С `Stopwatch.Frequency=10_000_000` стабильно `t=2` (наш QPC прибавлял 1 за вызов). `iters *= 2` уходило в бесконечность **без yield** — silent deadlock в managed-spin цикле, no PAL trace.

Fix: `QueryPerformanceCounter / GetTickCount64 / QueryUnbiasedInterruptTime` в `crt_imp_stubs.cpp` теперь привязаны к `SharpOSHost_GetUtcFileTime()` (FILETIME 100ns). `SharpOSHost_GetUtcFileTime` в `OS/src/PAL/SharpOSHost/Clock.cs` подмешивает HPET sub-second offset (RTC 1Гц недостаточно для SpinWait/SpeedCheck — клиенты видят real forward progress между RTC-тиками).

### Корень 2 — `WaitForMultipleObjectsEx` был no-op stub возвращавший 0 (=WAIT_OBJECT_0)

`Monitor.Wait` → `CLREvent.Wait` → `DoAppropriateAptStateWait` → `WaitForMultipleObjectsEx(1, &h, FALSE, ms, TRUE)`. До правки — `return 0` (signaled) → MRES.Wait считал, что событие сразу пришло → busy-spin без реального ожидания.

Fix: `WaitForMultipleObjects` / `WaitForMultipleObjectsEx` форвардят случай `n==1` в `SharpOSHost_WaitForSingleObject`. Multi-handle WaitAll/WaitAny возвращают `WAIT_FAILED` (явный fail вместо silent bogus signal).

### Корень 3 — finite timeouts в `WaitForSingleObject` (Event/Semaphore/Win32Mutex) пути → "infinite or 0", без deadline poll

`ThreadStubs.WaitForSingleObject` для Event с finite ms падал в `e.Wait()` (infinite). Теперь HPET-deadline yield-poll: read `Hpet.ReadCounter()` start + scale ms→ticks, в петле re-check IsSet/Yield пока не deadline или signaled. Та же логика для Semaphore, Win32Mutex, Thread (Join finite).

Параллельно: `AddressWait.WaitOnAddress` (Phase E9.c) — finite timeout не парковали на bucket (WakeByAddress не canceled timer), вместо этого HPET-deadline poll-yield на самой memory.

### Корень 4 — JIT GcInfo репортит stale stack-slot как OBJECTREF в non-interruptible aborted frame (DynamicMethod path)

`DynamicMethod.GetILGenerator` → loads `System.Reflection.Emit.{Lightweight,ILGeneration}.dll` → JIT'ит управляющий код → во время JIT'а GC fires → `WKS::GCHeap::Relocate` → `Object::ValidateInner` (debug-only `CHECK_AND_TEAR_DOWN(pMT && pMT->Validate())`) → assert на `pMT=0` (память по адресу из slot'а — zeroed).

Диагностика поэтапно ([CDTP] → [HFE] → [VI-bad] → [VI-caller] → [RL-zero]) показала: slot=0x027xxxx (стек host-потока), obj=0x500000A13390 (в GC range), память zeroed. Caller — `GcEnumObject` (JIT GcInfo callback). Класс — тот же что step107 (Frontier-D2 corruption), но **decoder-side** на JIT а не interpreter encoder.

Fix: cherry-pick **PR #119403** ([Unconditionally skip GC reporting for non-interruptible aborted methods](https://github.com/dotnet/runtime/pull/119403)) — 1 файл `gcinfodecoder.cpp`, ~40 LOC. Когда `m_NumInterruptibleRanges==0 && countIntersections==0 && executionAborted` — bail без reporting (skip stale slot walking). Apply clean, без merge-conflict. Fork commit `b7c49510629`.

Match с нашим триггером оказался полным (хотя я предполагал partial — `executionAborted=true` гейт): наш DynamicMethod-фрейм всё-таки `executionAborted` (предположительно через EH-unwind в dependency-loading цепочке JIT'а).

## Открытый фронт — long-running Task.Delay teardown

Симптом (verbose=off, после census end):
```
[wfso] h=0x1 ms=INF
[GetProcAddress kernel32] unknown name=GetThreadIOPendingFlag
[seh] throw code=0xE06D7363 type=.PEAVEEMessageException@@
[seh] throw code=0xE0434352
[SehDispatch] no handler matched — HALT
```

`h=0x1` — псевдо-handle (handle-table возвращает реальные ~0x1xxxx). `GetThreadIOPendingFlag` — kernel32 lookup для IO-completion check. Цепочка: shutdown пытается финализировать pending `TimerQueueTimer` → IOCP completion path → kernel32 sym lookup → throw cascade.

**Не блокатор для step111** — известный класс «long-running task survives probe lifetime». Cancellation семантика (`Task.Delay(ms, ct)`, `cts.Cancel`) — known .NET pattern; в bare metal shutdown'е дополнительно нужно: стаб `GetThreadIOPendingFlag` + flush TimerQueue до teardown. Отдельный шаг.

## Lessons learned

1. **Verbose=true тормозит serial-port печать (~87µs/char × тысячи строк) → меняет timing classification ProcessorNumberSpeedCheck**. Heisenbug «работает с verbose=on, падает с verbose=off» = timing-sensitive race в managed code. Найти race по дифференциалу timing'а: что в managed-side зависит от `Stopwatch.Frequency` vs `Stopwatch.GetTimestamp`.

2. **`extern "C"` ВНУТРИ функции — parser error в clang-cl/MSVC**. Память `[[extern_c_only_file_scope]]` существует, я её нарушил 2 раза за сессию (один раз в `excep.cpp`, один раз в `gc.cpp`). Always file-scope between functions.

3. **Логи открывать самому** — пользователь дважды попросил «братишка, это буквально лог qemu»: `last_build.log` в корне репо, UTF-16LE. `iconv -f UTF-16LE -t UTF-8 last_build.log | grep/sed` — не выпрашивать выдержки. Записано в [[feedback_read_logs_yourself]].

4. **Не маскировать непонятый корень** — пользователь зарубил предложение force `s_isProcessorNumberReallyFast=false` как заглушку для скрытого corruption'а. Корректный подход: довести до root cause, а потом расширить **существующий guard того же класса** (step107) если корень = тот же. PR #119403 cherry-pick — это **upstream fix корня**, не workaround.

5. **`Object::Validate` debug-assert — detector, не cause** ([[reference_methodtable_sanitycheck_is_detector]]). Heap corruption already happened upstream. В нашем случае upstream — JIT GcInfo decoder bug, fix в PR #119403.

## Файлы

### Kernel side (OS/)
- `OS/src/PAL/SharpOSHost/Clock.cs` (+15 LOC) — `SharpOSHost_GetUtcFileTime` теперь mixes HPET sub-second offset для sub-second resolution
- `OS/src/PAL/SharpOSHost/ThreadStubs.cs` (+~80 LOC) — HPET-deadline yield-poll для finite timeouts в Event/Semaphore/Win32Mutex/Thread branches
- `OS/src/Kernel/Threading/AddressWait.cs` (+~45 LOC) — finite timeout `WaitOnAddress` через HPET poll (без bucket-park, WakeByAddress не cancel'ит timer)

### Fork side (dotnet-runtime-sharpos/)
- Cherry-pick `b7c49510629` (Jan Kotas, PR #119403) — `gcinfodecoder.cpp` only, decoder bail when aborted-frame has no interruptible ranges
- `src/coreclr/pal/sharpos/crt_imp_stubs.cpp` (+33/-10) — `QueryPerformanceCounter / GetTickCount64 / QueryUnbiasedInterruptTime` → `SharpOSHost_GetUtcFileTime`; `WaitForMultiple{Objects,ObjectsEx}` n=1 forwarder; `WAIT_FAILED` для multi-handle.

### Probe
- `work/normal-hello/Program.cs` (+~15 LOC) — две Task.Delay пробы: `(1).Wait(2s)` для baseline timer infra, `(3s).Wait(1s)` для Wait timeout honoring. Вторая показала открытый фронт shutdown'а.

## Что откладывается

- `Task.Delay(3s).Wait(1s)` shutdown safety: stub `GetThreadIOPendingFlag`, flush TimerQueue до teardown, или корректное unobserved-Task disposal. **Не сейчас**, отдельный step.
- README comparative table / `docs/coreclr-hosted-limits.md` — Task.Delay остаётся 🟡, не переводим в ✅ пока shutdown не починен. Per user explicit: "табличку обновлять не надо".
- Cleanup всех остальных diag artifacts (`[CT]`, `[stub-reg]` и пр.) в ThreadStubs.cs — pre-existing, не сегодняшняя задача.

## Следующий step

Direction options (выбирать пользователю):
- **Phase F shutdown**: TimerQueue cancel/flush, `GetThreadIOPendingFlag` stub, observed-task disposal на teardown
- **Frontier-G**: `System.Threading.Timer (1ms)` (Skip'нут с step103, теперь TimerQueue ожил)
- **Cancellation**: `CancellationTokenSource.CancelAfter`, `Task.Delay(ms, ct)` с отменой
- **Process.Start fix**: `SystemNative_RegisterForSigChld` / Unix-side child reaping
