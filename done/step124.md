# Step 124 — REGDISPLAY layout fix + catchable managed cascade + Layer 3 magistral decision

Rollup за день: step122 (path/identity) + step123 (PInvoke try/catch wrapper) + step124 (EH cascade closure + Layer 3 commitment).

## Контекст

После step115 (Iced encoder bake-in) и step120/121 (libcmt cut + BootAsm cleanup) основная батарея проб на Windows R2R SPC шла стабильно. Сегодня вскрылись три фронта:

1. **Managed-throw EH через PInvoke/SafeHandle цепочки** не отрабатывал — `Process.ProcessName` (через procfs→FileStream→SafeFileHandle→Sys.FStat→`ObjectDisposedException`) → pass1 search завершался преждевременно, pass2 шёл в `propagate-native` mode, catch funclet в `Program::Probe` никогда не вызывался → HALT.
2. **Catchable HW NullReferenceException** теоретически работал (step106 `[SOS-HHE]`), но resume RSP был на 0xE0 байт выше нужного → catch funclet получал управление с неверным стеком → краш на эпилоге.
3. **Architectural identity question** — переход от Linux SPC к Windows SPC (или dual-track) как магистрали для развития managed-приложений на bare metal.

## Архитектура решения

### 1. REGDISPLAY layout alignment (корень для (1))

Корневой баг для managed-throw cascade оказался **layout mismatch** между C# SPC и C++ runtime. Layer 1 = Windows machinery (REGDISPLAY 0xbf0 байт, SP at 0xbd0), а C# Linux IL SPC под `TARGET_UNIX && !TARGET_SHARPOS` ожидал Linux layout (REGDISPLAY 0x1b80 байт, SP at 0x1b70). C# писал `_handlingFrameSP` на offset 0x278, C++ читал `m_handlingFrameSP` на 0x250 — `pCatchHandler` value падал на правильный offset случайно (одно из соседних полей), `handlingFrameSP` получал мусор → pass2 walker бесконечно искал match, не находил, шёл в native-boundary unhandled.

Дополнительно `SIZEOF__StackFrameIterator` различался: Linux+interp=0x170, Linux-interp=0x150, **Windows runtime sizeof=0x148** (8 байт меньше из-за отсутствующего поля в no-FEATURE_INTERPRETER no-FEATURE_EH_FUNCLETS pre-build ветке).

**Фикс** в `src/coreclr/System.Private.CoreLib/src/System/Runtime/ExceptionServices/AsmOffsets.cs`:
- Все `#if TARGET_UNIX && !TARGET_SHARPOS` → `#if false && TARGET_UNIX && !TARGET_SHARPOS` (9 мест) — принудительно отключить Linux REGDISPLAY branch
- Все варианты теперь падают в Windows-shaped layout, выравненный с Layer 1

Соответственно в `nativeaot/Runtime.Base/src/System/Runtime/ExceptionHandling.cs` — `#if TARGET_UNIX` блок с `_pReversePInvokePropagationCallback`/`_pReversePInvokePropagationContext` тоже отключён (поля больше не имеют offset constants).

### 2. CCF-inv RSP patching (workaround для (2))

`pvRegDisplay->pCurrentContext->Rsp` при входе в `ResumeAfterCatch` оставался на throw-frame SP, не на catching-frame body SP. Linux PAL path использует `FaultingExceptionFrame::InitAndLink` для fixup'а, у нас этого нет.

**Workaround** в `vm/exceptionhandling.cpp::CallCatchFunclet`: перед `ResumeAfterCatch` сканировать первые 64 байта resume PC на pattern `add rsp, imm32; pop rbp; ret` (48 81 C4 ?? ?? ?? ?? 5D C3). Если найден — invariant `ctx.Rsp + imm + 16 == callerTargetSp` даёт expected RSP. Если delta != 0, патчим. Stop-gap до полной унифицированной механики catching-frame RSP computation из unwind metadata.

В прогоне на 4 catchable NRE пробах delta = 0xE0 (одна лямбда) и 0xC0 (другая) — все патчатся корректно, resume в catch funclet, control возвращается к caller'у штатно.

### 3. Layer 3 magistral commitment

После многосторонней дискуссии (включая два независимых мнения мудрецов) **зафиксировано Windows SPC как магистральная Layer 3 identity**.

**Решающие аргументы:**

- **Completion model уже работает.** `CreateIoCompletionPort`/`GetQueuedCompletionStatus`/`PostQueuedCompletionStatus` реальные, backing через `SharpOSHost_IocpPost`. Совпадает с природой кооперативного scheduler'а (операция ставится в очередь → ядро выполнило → completion постится → ждущий поток wake-up).
- **Layer 1 = Windows совпадает с Layer 3 = Windows.** Минимум слоёв несоответствия. AsmOffsets layout matches без ручных патчей (наш `false && TARGET_UNIX` хак под Linux SPC становится не нужен).
- **Layer 2 (Unix-shaped PAL) — implementation interception detail, не identity.** Он внутри думает что Linux потому что PAL — Unix-фича в upstream dotnet, но наружу отдаёт Windows-shaped contract.
- **Уникальные обязательства Windows изолированы; Linux — воюют с архитектурой.** Registry = чистый изолированный subsystem. epoll/eventfd/signals/procfs = эмуляция Unix-kernel internals в системе которая машинно не Unix.
- **Эмпирика сегодня:** PowerShell на Windows SPC доходит до Group Policy lookup в реестре (managed Main + parse args). PowerShell на Linux SPC падает на самом раннем threading init (`SystemNative_LowLevelMonitor_Create` отсутствует) — до Main.

**Формула identity:**
> SharpOS exposes a **Windows-Core managed profile** — managed-side контракт ближе к Windows Core, потому что machinery PE/SEH/Win64/IOCP-shaped. Без обещания full WinAPI/desktop/admin compatibility.

Linux SPC → lab/secondary track. Не удалён, гоняется иногда для сравнения, не блокирует magistral progress.

## Результат

**Probe-battery (Windows R2R SPC):**

- **117 OK / 4 DEG / 11 FAIL** (предыдущая база ~85 OK).
- Из 11 FAIL: 4 catchable HW NullReferenceException (by-design success-criterion), 7 ожидаемых / known PAL gap (RO FS / Invariant globalization / zlib / SigChld / L6 documented).
- 1 регрессия (`cctor throws -> TypeInitializationException`) — закомментирована, упирается в `__C_specific_handler` filter @ rva 0x14890 NPE на field+4. Отдельная задача (не блокер).

**Catchable HW NRE — 4/4 PASS:**
- `Null object.ToString catchability` — `[CCF-inv] patching Rsp delta=0xE0`
- `Null MethodInfo.ToString catchability` — same path
- `Null MethodInfo.Invoke catchability` — same
- `DebugProvider.FailCore NP invoke` — same

**Managed throw cascade PASS:**
- `managed throw bare` — `throw new InvalidOperationException("bare")` из лямбды
- `managed throw deep stack` — 8 уровней nested static local functions
- `Process.ProcessName` — managed `ObjectDisposedException` через SafeHandle→FileStream→procfs цепочку (FAIL по сути — procfs нет, но catchable правильно отрабатывает)

**Lifecycle:**
- step122: APP_PATHS dual-mode для pwsh/fx/root probing, NormalHello identity probes
- step123: `[NoInlining] InvokeCoreClrInitialize` обходит NativeAOT-Release elision try/catch вокруг PInvoke
- step124: REGDISPLAY alignment + CCF-inv RSP patch + Layer 3 commitment

## Layer 3 forward plan (step125+)

Из консенсуса мудрецов:

1. **step125** — Controlled failure для всех linked-but-unimplemented exports. Никаких crash'ей на новых BCL вызовах. (~5 файлов, ~100 строк.)
2. **step126** — Registry empty subsystem (HKLM/HKCU существуют, OpenSubKey/GetValue/Enum → null/empty). Цель: PowerShell проходит GPO lookup. (~6 функций advapi32.)
3. **step127** — Console Win32 facade (GetStdHandle, WriteFile→stdout, WriteConsoleW, GetConsoleMode/SetConsoleMode, GetConsoleScreenBufferInfo). Цель: PowerShell рисует prompt. (~10 функций kernel32.)
4. **step128** — NTSTATUS glue + точечный NT RTL (RtlNtStatusToDosError, NtQueryInformationProcess/Thread).
5. **step129** — FileSystem advanced (WriteFile, FlushFileBuffers, CreateDirectory, MoveFile, GetVolumeInformation).
6. **step130** — BCryptGenRandom, special folders, TimeZone hive (UTC stub).
7. Отдельный milestone — Sockets поверх работающего IOCP.
8. Отдельный milestone — TLS/X509 (managed-fallback decision).

## Изменённые файлы

### Fork (`dotnet-runtime-sharpos/`):
- `src/coreclr/System.Private.CoreLib/src/System/Runtime/ExceptionServices/AsmOffsets.cs` — `#if false && TARGET_UNIX && !TARGET_SHARPOS` (9 мест)
- `src/coreclr/nativeaot/Runtime.Base/src/System/Runtime/ExceptionHandling.cs` — disable `#if TARGET_UNIX` блоки которые используют выкинутые AsmOffsets константы
- `src/coreclr/vm/exceptionhandling.cpp` — diagnostic prints во всех SfiNext exit paths, DispatchExSecondPass `[DESP]` enter/iter/match prints, CCF-inv RSP patching (epilog pattern detection + write `ctx.Rsp = expectedRsp` если delta != 0)
- `src/coreclr/pal/sharpos/crt_imp_stubs.cpp` — `SystemNative_GetPid` стаб (возвращает 1)
- `src/coreclr/vm/ceemain.cpp`, `vm/finalizerthread.cpp`, `vm/qcallentrypoints.cpp` — мелкие точечные правки (FinalizerThread gate, kernel32 QCALL block, env-OOM)
- `src/coreclr/clrfeatures.cmake` — FEATURE_EVENTSOURCE_XPLAT включён для SharpOS

### Kernel (`OS/`):
- `OS/src/Kernel/Diagnostics/CoreClrProbe.cs` — `[NoInlining] InvokeCoreClrInitialize` wrapper, APP_PATHS dual-mode
- `OS/src/Boot/EH/HwFaultBridge.cs` — `SharpOS_CoreCLR_TryHandleHardwareException` DllImport + invoke перед SehDispatch
- `OS/src/Boot/BootSequence.cs` — VA 0 unmap, DumpExecBuffers, register exec-pools в SehUnwind
- `OS/src/PAL/SharpOSHost/SehDispatch.cs` — IsValidIp [ivip] one-shot, IsImageTextGap accept
- `OS/src/PAL/SharpOSHost/SehUnwind.cs` — ImageTextGapLookup synthesizing leaf RF, targeted TraceUnwind constants
- `OS/src/PAL/SharpOSHost/Diagnostics.cs` — `SharpOSHost_DebugPrintForced` (bypass Verbose flag)
- `OS/src/PAL/SharpOSHost/Clock.cs` — мелкие правки
- `OS/src/Kernel/Diagnostics/Probes.cs` — `KernelGcPreciseSmoke = false` (VA 0 guard exposed)
- `work/normal-hello/Program.cs` — 4 catchable NRE пробы + managed throw bare/deep + cctor probe commented (regression)

### Docs:
- `donext.md` — обновлён с Layer 3 magistral decision и forward plan step125+

## Lessons learned

1. **Layout-mismatch class of bugs requires runtime layout assertions.** AsmOffsets static_assert в `#if __cplusplus` блоке нет в нашем build pipeline — мы не валидировали layout до runtime. Несовпадение пряталось часами. Добавить explicit runtime self-test: при `coreclr_initialize` пройти ExInfo offsets, сравнить C# vs C++, fast-fail при mismatch.

2. **SfiNextWorker exit paths нужно покрывать диагностикой ВСЕГДА.** Один path (line 4358 SWA_FAILED на первом `pThis->Next()`) уходил в `goto Exit` без log'а — потеряли часы на "почему pass1 терминируется silently". Все три goto Exit теперь логируют.

3. **`[CCF-inv]` epilog-pattern fix — workaround, не решение.** Правильный путь — извлекать catching-frame body RSP из unwind metadata напрямую, как делает Linux PAL FaultingExceptionFrame. Откладываем до step125+ если catchable HW NRE станет более распространённой.

4. **Layer 3 identity — фундаментальное архитектурное решение, не optimization.** Откладывать его не имеет смысла — оно влияет на каждый последующий шаг PAL surface. Сделать однократно, зафиксировать, dual-track как research не как magistral.

5. **PowerShell на bare metal — feature, не toy.** Доходит до GPO lookup (managed Main + parse args). Реестр-stub разблокирует REPL за ~6 функций advapi32. PowerShell первый flagship Windows-managed apps на bare metal.

## Отложено

- Полная (а не stop-gap) механика catching-frame RSP из unwind metadata
- cctor TIE wrap path debugging (`__C_specific_handler` filter @ rva 0x14890 NPE на field+4)
- Diagnostic print cleanup в `vm/exceptionhandling.cpp` (после стабилизации можно gate'нуть на `TraceCcf=false`)
- AsmOffsets runtime self-test (см. lesson 1)
- Linux SPC как nightly regression target (CI setup отдельно)

## Next step

**step125 — controlled failure для linked-but-unimplemented exports.** Цель: каждый WinAPI entry достижимый из managed BCL должен быть в одном из трёх состояний: implemented / functional stub / controlled error. Никогда — crash.

После этого **step126 — Registry empty subsystem** (~6 функций advapi32). Цель: PowerShell проходит GPO wall.
