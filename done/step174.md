# step 174 — ожидания без опроса, пул задач, потоки приложения умирают с ним

Часть 1 плана «задачи → вытеснение → USB → IOCP». AOT-задачи на ноутбуке
стоили 116 мкс (тик 1000 Гц, step173), hosted — 3.

## Корень

`Task.Wait`, `ManualResetEventSlim.Wait`, `Monitor.Enter` в std опрашивали флаг:

```csharp
while (!_completed) ThreadBackend.Sleep(1);
```

Комментарий объяснял: «пока у приложения нет потоков, блокироваться не на
чем». Потоки давно есть. В BenchAot первый `Wait` пачки из 16 засыпал на
`Sleep(1)` — до тика: 1–2 мс на пачку при 1000 Гц, 5–10 при 100 (отсюда
307 → 116 мкс в step173). Плюс новый поток на каждую задачу; память потока после
выхода не освобождается (~72 КиБ).

## Сделано

**Ядро.**
- `AddressWait.WaitOnAddress`: сравнение, постановка в бакет и `Waiting` —
  под `Preemption.Suppress` (как `Event.Wait`); ожидание с таймаутом — сон на
  бакете и таймере сразу (как `Semaphore.WaitUntil`), не цикл с `Yield`;
  пробуждение помечает `Signalled`. `Forget(thread)` — снять из бакета.
- `Scheduler.Sleep`: `Waiting` + `TimerQueue.Schedule` под `Suppress` — тик
  между ними терял поток.
- Потоки приложений: адрес входа и поколение запуска — в самом потоке
  (`Thread.AppEntry`, `AppGeneration`), кольцо `s_appEntries` на 32 записи
  убрано. `Scheduler.EnterApp`/`LeaveApp` вокруг `JumpStub.Run` (лаунчер,
  `RunExternalApp`): при возврате приложения его оставшиеся потоки снимаются с
  очереди готовых, таймеров и адресов — раньше продолжали жить в выгруженном
  коде или просыпались в следующем приложении по тому же адресу.
- Службы `WaitOnAddressAddress`, `WakeByAddressAllAddress` в хвосте обеих
  `AppServiceTable.cs`: `[UnmanagedCallersOnly]` функции ядра, без
  переходника (как `GcWalkRootsAddress`), только Win64. Версия ABI не менялась.
- Счётчики по прогону: `sched.switches`, `sched.halt.*`, `sched.sleeps`,
  `sched.spawn.*`, `wait.address.*`, `wait.wakes`.

**std.**
- `ThreadBackend.InstallWaits` / `WaitWhile` / `WakeAll`; без служб — прежний
  опрос. Адрес поля отдаётся ядру напрямую: сборщики не двигают объекты.
- `Task`: слово 0/2/1 (идёт / идёт и ждут / готово) — завершение зовёт ядро,
  только если ждут. `ManualResetEventSlim`, `Monitor` (счётчик ждущих на слот,
  `Exit` будит, если есть) — блокирующие. Ожидания с `CancellationToken` —
  ломтями по 10 мс (`Cancel` никого не будит).
- `TaskPool`: потоки ждут работу на слове `s_queued`. Новый поток — только
  если есть очередь, нет свободных и никто не стартует; проверка при постановке
  и при взятии задачи, так что бесконечный цикл Terminal.Gui держит свой поток,
  не задерживая очередь. `s_waking` — будить только ещё не разбуженных
  (иначе пачка из 16 звала ядро 16 раз). Замок — futex-мьютекс (0/1/2).
- `LocalApic.RetuneToDeliveredRate` (из разбора step173): мерить все окна, не
  трогая счётчик; править один раз, только если окна согласны; проверять
  правку, иначе откатить. Раньше потерянные тики QEMU раскручивали таймер до
  47 тыс. прерываний в секунду.

## Замеры

`aot.tasks`, мкс на задачу:

| | QEMU | ноутбук |
|---|---|---|
| step172 (100 Гц, поток на задачу, опрос) | 351–392 | 307 |
| step173 (1000 Гц) | 125 | 116 |
| step174, первые 48 задач приложения | 66–100 | 34–48 |
| **step174, тёплый пул (`tasks.warm`)** | **1.6–1.8** | **0.30** |

Создание потока ядром (`sched.spawn.avg_ns`): 2.5–3.5 мкс на ноутбуке, ~40
под QEMU. Остановов процессора в BenchAot — 0. Hosted `tasks` на ноутбуке —
2.3–2.9 мкс, как было (один прогон — 111, выброс).

Проверка: AOTTESTS 61/61 (+4: блокирующие ожидания опубликованы, событие из
другого потока будит ждущего, ожидание с таймаутом возвращает false, 100 задач
разом), перепись 157/2/18, `[task]`/`[lock]`/`[preempt]` PASS, `app threads
ended with the app: 2` после каждого AOTTESTS и BenchAot — QEMU и ноутбук.
Подстройка частоты при нагруженном хосте: `OUT OF RANGE, count left as
calibrated (ticks per round: 6 9 11 0)`, загрузка без зависания.

## Открыто

- **Первые задачи приложения — ~1.5–2 мс сверху** (ноутбук; под QEMU 3–5).
  Не создание потоков ядром (2 × 3 мкс), не останов, не сборка ядра, не лог
  (внутри замера ядро ничего не пишет). Засечь первую пачку по частям.
- Очереди продолжений нет: `await` незавершённой задачи занимает поток пула.
- Стеки завершённых и снятых потоков не освобождаются.

## Уроки

- Комментарий «пока нет X» переживает появление X — сверять при каждом шаге,
  который X добавляет.
- Прежде чем чинить «медленно», засечь, спит оно или считает: один счётчик
  остановов разделил тиковое ожидание и работу за один прогон.
- Холодное и тёплое — отдельными замерами: 48 задач с единоразовым стартом пула
  выглядели как 34–48 мкс на задачу, пул стоит 0.3.

## Файлы

- Ядро: `OS/src/Kernel/Threading/AddressWait.cs`, `Scheduler.cs`, `Thread.cs`;
  `OS/src/Kernel/Process/AppServiceBuilder.cs`, `AppServiceTable.cs`,
  `LauncherBoot.cs`; `OS/src/Kernel/Diagnostics/PerfCounters.cs`;
  `OS/src/Hal/Apic/LocalApic.cs`, `OS/src/Boot/ExitBootServicesProbe.cs`.
- std: `std/no-runtime/shared/Threading.Tasks.cs`, `Threading.Monitor.cs`,
  `Threading.Tasks.KernelScheduler.cs`, `Threading.Tasks.AppServices.cs`.
- SDK и приложения: `apps_native/sdk/AppServiceTable.cs`, `AppThreads.cs`;
  `apps_native/AotTests/Program.cs`, `apps_native/BenchAot/Program.cs`
  (`tasks.warm`).
- Документы: `docs/nativeaot-nostd-kernel-limits.md` (задачи, §16),
  `docs/coreclr-hosted-limits.md`, `README.md`, `CLAUDE.md`; `donext.md` —
  раздел «Стенд» (ноутбук + рутованный телефон как USB-гаджет).

Сырьё — `work/bench-2026-09-14/sharpos-laptop-pool*.txt` (не в git).

## Следующий шаг

Часть 2: вытеснение для программ из лаунчера (гонки в `Scheduler.Spawn`,
`NativeArena`, `Fat32`, `TerminalConsole.Flush`; `sti` при входе в приложение;
критические секции сборщика приложения; консервативный кадр потока, прерванного
вне точки с GcInfo).
