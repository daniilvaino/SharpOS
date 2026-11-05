# step253 — Ctrl+C для всех

Релиз 0.1, §10. До этого шага Ctrl+C в приложении был байтом 0x03: программу,
которая его не читает, остановить было нечем, кроме `kill` из другой консоли,
которой нет.

## Модель

Как в POSIX и в .NET, два слоя:

1. **Ядро — по умолчанию завершает.** Ctrl+C — сигнал группе, владеющей
   терминалом (`TerminalOwner.Foreground`, step234). Процесс без обработчика
   завершается с 130 (128 + SIGINT) тем же путём, что `Kill`: поток уходит из
   кода приложения на тике или на выходе из службы, ожидания в ядре
   (`AddressWait`, трубы, сон) прерываются — всё это было с step194.
2. **Программа может обработать.** `Console.CancelKeyPress` и
   `PosixSignalRegistration` ставят поток-слушатель, который ждёт в ядре.
   Обработчики идут в нём; `Cancel = true` отменяет завершение, иначе — 130.

Чего нет: отмены через `CancellationToken`, протащенный «везде» (чужой код его
не смотрит), и `Thread.Abort`-подобного исключения в чужом потоке (прилетает
посреди `finally` и под замком; из .NET Core его убрали).

## Ядро

- `TerminalInterrupt` (новый): `Raise` из потока опроса клавиатуры
  (`InputPump`) — для каждого живого процесса группы: слушает → счётчик
  `InterruptCount`++ и пробуждение по адресу; не слушает → `RequestEnd(130)`.
  Исход: `Signal` — клавиша съедена (и её отпускание), `Key` — в группе есть
  процесс с `TreatControlCAsInput`, клавиша идёт ему, `NoProcess` — старый путь
  hosted-рантайма (`RaiseCtrlEvent`).
- Повисший обработчик: если слушатель ещё не вернулся за следующим (счётчик ≠
  обработанному), Ctrl+C завершает процесс. Без этого программа с
  `CancelKeyPress`, где обработчик завис, была бы неостановима.
- Свободный терминал (`Foreground == 0`) — группе процесса, запущенного ядром
  (оболочка, лаунчер), как терминал принадлежит login-оболочке, пока его не
  взяло задание.
- Служба `ProcessOpInterrupt` (26): прочитать/задать «Ctrl+C — клавиша»,
  «слушаю», ждать (`AddressWait` на `InterruptCount`, прерывается при
  завершении), «завершить как Ctrl+C».
- `AppProcess.InHostedRun` — процесс ждёт hosted-программу (`RunManagedApp`):
  Ctrl+C её рантайму (pwsh, step159), а не ждущему.
- Строка журнала на каждый Ctrl+C: `[ctrlc] group=… ended=… signalled=… key=…`.

## SDK (CoreLib приложений)

- `PosixSignalRegistration`, `PosixSignal`, `PosixSignalContext` — порт BCL 8 как
  есть; платформенная половина (`PosixSignalRegistration.AppHost.cs`) по форме
  `.Windows.cs`: те же сигналы (SIGINT, SIGQUIT, SIGTERM, SIGHUP; прочие —
  `PlatformNotSupportedException`), набор токенов, обработчики в своём потоке.
  Слушатель запускается первой регистрацией и включает потоки приложения
  (`TaskBackendInstaller`), если их не было.
- `Console.CancelKeyPress` — как в BCL на Unix, поверх SIGINT/SIGQUIT;
  `ConsoleCancelEventArgs`, `ConsoleSpecialKey` — порт как есть.
- `Console.TreatControlCAsInput` — служба ядра.

Ошибка по ходу: потоки включались внутри `lock` — после включения у потока
другой идентификатор, `Monitor.Exit` его не узнаёт
(`SynchronizationLockException`). Включаются до первого замка
(`EnsureThreads`).

## Оболочка, лаунчер

- Оболочка у приглашения — `TreatControlCAsInput = true`: Ctrl+C сбрасывает
  строку, как раньше.
- `RunProgram` (`expect`, программа без вывода на экран, `.DLL`) открывает
  задание, как конвейер: раньше программа шла в группе оболочки, терминал никто
  не брал.
- `SharpOSDriver` (Terminal.Gui) — `TreatControlCAsInput = true`, как стоковый
  `NetDriver`. Программа, запущенная лаунчером, сигнал получает, сам лаунчер — нет.

## Проверка

- AotTests `--ctrl-c CASE`, драйвер жмёт Ctrl+C через QMP по строке готовности
  (прогон 344): `spin`/`sleep`/`read` → 130; `handled` → 7; `uncancelled` → 130
  после обработчика; `hung` → второй Ctrl+C → 130; `posix` → обработан, после
  `Dispose` второй → 130; `input` → `ReadKey` получил 0x03 `C Control`. 9/9.
- Вручную у приглашения: `echo nope^C` — строка сброшена, оболочка жива;
  `AOTTESTS.EXE --ctrl-c spin | PIPECNT` — оба завершены, приглашение `[130]`.
- Малые тесты (прогон 345): JSON 72, Regex 93, Xml 20, KQL 7+9, Data 366,
  AotTests 369 — без сбоев.
- По ходу: 340/341 — `group=0` (оболочка запускала `expect` без задания);
  342/343 — 134 у обработчиков (нет потоков, потом замок).

## Файлы

- Ядро: `Process/TerminalInterrupt.cs` (новый), `AppProcess.cs`,
  `AppServiceBuilder.Processes.cs` (служба 26), `AppServiceBuilder.cs`
  (`InHostedRun`), `Input/InputPump.cs`.
- SDK: `PosixSignalRegistration.cs`, `PosixSignalRegistration.AppHost.cs`,
  `ConsoleCancelEventArgs.cs` (новые), `SystemConsole.cs`, `Process.cs`,
  `SdkRuntime.props`.
- Оболочка: `Program.cs`, `Executor.cs`; Terminal.Gui `SharpOS/SharpOSDriver.cs`.
- Тест: `AotTests/CtrlCTests.cs`, `Program.cs`, `AotTests.csproj`.
- Документы: README, limits, release.md, donext.

## Дальше

- §10: SIGWINCH при смене размера панели — тот же путь (слушатель, счётчик).
- §10: потерянное отпускание Ctrl → ложный Ctrl+C — теперь это не байт, а
  завершение программы; прогон с журналом скан-кодов важнее, чем был.
- Встроенные команды и циклы скрипта у приглашения Ctrl+C не прерывает.
- Драйвер `ctrlc_driver.py` — в батарею (сейчас из черновиков).
