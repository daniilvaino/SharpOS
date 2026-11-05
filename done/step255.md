# step255 — гибель hosted-программы: проба и причина в журнале

Релиз 0.1, §11, пункт «FailFast hosted-процесса вешает ОС». Проверено на
управляемой программе, а не на pwsh.

## Проба

`apps_managed/HostedFault` — один проект, три сборки по `AssemblyName`
(`HostedExit`, `HostedThrow`, `HostedFailFast`): ветка выбирается по имени
сборки, так `.DLL` в оболочке запускается без аргументов. `run_build.ps1`
собирает их (кроме `-NoTests`), кладёт в `\sharpos` и в TPA.

## Что происходит

Все три останавливают машину — CoreCLR один на систему, процесс рантайма — вся
машина:

- `Environment.Exit(3)` → `SafeExitProcess` → `EEShutDown` → `ExitProcess`
  (CRT-заглушка форка) → `SharpOSHost_Panic`.
- необработанное исключение → `[unhandled] InvalidOperationException` в
  журнале приложения, ядро — `[PANIC] unhandled exception`.
- `FailFast` → `EEPolicy::HandleFatalError` → `SafeExitProcess(0x80131623)`;
  заглушка `TerminateProcess` возвращает («успех»), дальше `int3` → `#BP`
  (rva 0x3821D, сразу после `call SafeExitProcess`), дамп, halt.

## Тишина — отдельная ошибка, исправлена

Прогон 348: `HostedExit` повис без единой строки. QMP — `hlt;ret` при IF=0,
символизация — `Panic.Fail` ← `ExitProcess` ← `EEShutDown`. `CoreClrHost` на
время программы ставит `Console.Quiet` (`Probes.HostedAppQuietConsole`), и
причина паники глоталась. `Panic.Fail`, `SharpOSHost_Panic` и
`PanicDump.Print` теперь первым делом включают консоль. Прогоны 349/350 — причина
видна во всех трёх случаях.

## Решение для 0.1

Остаёмся так: смерть внятная, ограничение записано как свойство яруса (README,
limits, coreclr-hosted-limits). Изоляция без нарушения инварианта возможна —
образ не процесс: код CoreCLR общий, `.data/.bss` и кучи свои у каждого
hosted-процесса (свой корень таблиц страниц), дальше зигота — копия при записи
прогретого рантайма. Расписано в donext, первое действие — замер старта.

## Файлы

- Ядро: `Kernel/Panic.cs`, `PAL/SharpOSHost/Diagnostics.cs`, `Hal/Idt/PanicDump.cs`.
- Проба: `apps_managed/HostedFault/` (csproj, `Program.cs`), `run_build.ps1`.
- Документы: README, limits, `docs/coreclr-hosted-limits.md`, release.md, donext.
  В README снята устаревшая оговорка «программе не сообщается смена размера»
  (SIGWINCH — step254).

## Дальше

- Остальное §11: Ctrl+C в pwsh, `BreakHandler` после выхода, `.DLL` с `>`/`|`,
  файловые операции PAL, недостающие API, пути.
