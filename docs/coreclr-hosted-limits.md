# CoreCLR-hosted (stock .NET on bare metal): карта ограничений

Живой документ. Перечень того, что **работает / не работает** когда
стоковый `dotnet build` DLL исполняется **байт-в-байт** на форк-CoreCLR
(`dotnet-runtime-sharpos`, статически слинкован в kernel-образ;
steps 68–73, 98, 99). Это **третий tier** SharpOS:

- ядро (`OS/`) — kernel NativeAOT+NoStdLib → [`nativeaot-nostd-kernel-limits.md`](nativeaot-nostd-kernel-limits.md);
- ELF apps (`apps/`) — NativeAOT через AppSDK → [`nativeaot-nostd-elf-limits.md`](nativeaot-nostd-elf-limits.md);
- **этот файл** — гостевой стоковый .NET поверх нашего PAL.

Общая компаративная таблица — в [`README.md`](../README.md).

Все пункты проверены практикой через scaffold `work/normal-hello/`
(gitignored) — там живут repro-пробы (coverage-батарея step-72 +
PAL/OS census step-73). Хелпер `Probe(name, body)` печатает имя ДО тела
(некатчабл trap/hang → висячая строка = виновник), затем
классифицированный вердикт.

**Конвенция:**
- ✅ **Работает** — доказано прогоном.
- 🟡 **Деградирует** — не кидает, но значение неверное/частичное (ловимо).
- ❌ **PAL-STUB** — ловимый `SEHException` («External component has thrown an exception»): trap-stub в PAL поднял native SEH, он дошёл до managed как generic-исключение.
- ❌ **MANAGED-EXC** — чистое BCL-исключение (`FileNotFound`/`CultureNotFound`/`ZLibException`): managed-слой **сбриджен**, отсутствует только нативный backend.
- ❌ **OOM** — `OutOfMemoryException`.
- ⏭️ **SKIPPED** — в пробах отключено (доказанный hard-panic-класс).
- 💥 **HARD-PANIC** — **некатчабл**: нативный C-SEH (`__C_specific_handler`) → размотчик `SehUnwind` не находит handler (`invalid Rip` в стеке) → `[SehDispatch] no handler matched → HALT`. Это **единый отложенный фронтир** (см. §11).

**Терминология.** «PAL-STUB» ловится (`try/catch` приложения работает —
заслуга managed-EH-фиксов step-69…72). «HARD-PANIC» **не** ловится:
размотчик ломается раньше, чем дойдёт до CLR-кадра, который превратил
бы SEH в managed-исключение. Корень HARD-PANIC — upstream-дефект
`SehUnwind`/frame-chain, который step-71/72 чинили **локально для своих
консьюмеров** (`CallCatchFunclet`, `GetStackSlot`), а сам размотчик
отложили. Подробности — `done/step071.md`, `done/step072.md`,
agent-memory `project_string_as_methodtable_shared_root`,
`project_reflection_json_baremetal_step72`.

---

## 0. Что РАБОТАЕТ (фундамент — steps 68–73)

Доказано coverage-батареей 21/21 + census'ом:

- **Исполнение**: RyuJIT (JIT-on-first-call), R2R, type loader, generics
  + shared-generics, cctor (`DoRunClassInit`), interface/virtual dispatch.
- **GC/память**: CoreCLR mark-sweep, `new`/массивы/строки, `GC.Collect`,
  precise+conservative stack-root scan, 16 MiB BigStack, `GetTotalMemory`.
- **Язык/BCL**: arith+`Math`-интринсики, `string`/`StringBuilder`,
  `Span`/`stackalloc`, `List`/LINQ, `Dictionary`, generics/tuple/pattern,
  nullable, `DateTime`/`TimeSpan` арифметика, `Guid`, `record`/`with`,
  рекурсия, `checked`-overflow, **`yield`/итераторы** (state-machine
  codegen — не зависит от threading-PAL).
  > ✅ **Generic `as T` / `(T)x` с `where T : class` работает с step 103**
  > (`reference_msc_throw_becomes_sehexception` РЕШЕНО). Корень был не в
  > RhTypeCast, а в трансляции C++ msc throws (code 0xE06D7363) в
  > managed: на TARGET_UNIX/SHARPOS `PAL_TRY/PAL_CATCH` оборачивал C++
  > throw как `SEHException` без сохранения оригинального типа. Фикс
  > step 103: двойной deref `args[1]` в `clrex.cpp:619` +
  > `excep.cpp:5585` для извлечения `EEException*` — типизированные
  > catches (`InvalidCastException` и т.п.) теперь срабатывают как
  > положено. Verified пробой `constrained where T : class` в
  > `normal-hello` Sec 8.
  > **Историческое предупреждение** (актуально только pre-step-103,
  > сохранено как контекст для понимания корня позднее
  > `TerminateProcess(COR_E_EXECUTIONENGINE)` на Regex/Culture probes —
  > их корень был **тот же** msc-throw-trans path).
  > **Полный fix:** уже в `clrex.cpp:619`, см. step 103 done writeup —
  > case `0xE06D7363` парсит ThrowInfo и возвращает оригинальный
  > CLRException-derived throwable.
- **Exceptions**: `try/catch/finally/throw`, фильтры, rethrow,
  multi-frame, native-origin (`COMPlusThrow`), HW-fault→`catch`,
  `StackTrace` — сквозь JIT-кадры.
- **Reflection**: type/assembly metadata, `GetConstructors`/
  `GetParameters`, generic instantiation, `GetCustomAttributesData`,
  `NullabilityInfoContext`, `DynamicMethod`/LCG, **reflection-mode
  `System.Text.Json`** (Serialize/Deserialize/JsonDocument) + source-gen.
- **Текст/числа**: полный **UTF-8/Unicode** (кириллица+эмодзи),
  `String.Normalize`, `Regex`, `Convert.ToBase64String`,
  `Random.Shared` (managed PRNG), `Stopwatch`/`Environment.TickCount64`
  (монотонны), `Path.GetFullPath`.
- **Время**: `DateTime.UtcNow` — реальное CMOS-время (step-73 мост; §1).
- **Self-ID**: `FrameworkDescription=.NET 10.0.7-dev`,
  `Environment.Version 10.0.7`, target `.NETCoreApp v10`, assembly
  identity, `ProcessPath`, `ProcessorCount`, `PID`, GC settings.

Веха: `done/step072.md` — байт-в-байт reflection-mode System.Text.Json.

---

## 1. Время / часы

- ✅ `DateTime.UtcNow` — **РАБОТАЕТ (step 73 quick-win)**. Реальное
  CMOS-время (проверено: `Year=2026`). Мост:
  `OS/src/PAL/SharpOSHost/Clock.cs` (`SharpOSHost_GetUtcFileTime` /
  `SharpOSHost_GetSystemTime` ← `Hal.Rtc`, days_from_civil → FILETIME с
  эпохи 1601) ← fork `GetSystemTimeAsFileTime`/`GetSystemTime`. CMOS
  трактуется как UTC (bare metal без tz DB). RTC-fail → 0 (старое 1601,
  без фолта).
- ❌ PAL-STUB `DateTime.Now` — local/timezone-конвертация (нет tz DB;
  отдельный фронт, не «быстрый»).
- ✅ `Stopwatch.GetTimestamp`, `Environment.TickCount64` — монотонны
  (fake-счётчики, не привязаны к wall-clock, но не кидают).

---

## 2. Файловая система (System.IO)

Managed-слой **сбриджен** (отдаёт чистые BCL-исключения, не trap).
После step-99 базовые stat-getters работают, write/enum-with-results
ждут писемой FS.

- ✅ `Path.GetFullPath` (чистый managed).
- ✅ `Directory.GetCurrentDirectory` → `\sharpos` (step 99: forwards
  to `SharpOSHost_GetSystemString(KindCurrentDir)`).
- ✅ `Path.GetTempPath` → `\sharpos\tmp\` (step 99: KindTempPath).
- ✅ `Directory.Exists("." | "\sharpos")` → `true`. Другие пути
  возвращают `false` без trap'а (step 99: `SharpOSHost_GetFileAttributes`).
- ✅ `Directory.EnumerateFiles(.)` → пустая итерация (step 99:
  `FindFirstFileW` stub возвращает `INVALID_HANDLE_VALUE` +
  `ERROR_FILE_NOT_FOUND`, BCL отдаёт `IEnumerable<>` без элементов).
- ❌ MANAGED-EXC `File.WriteAllText` → `UnauthorizedAccessException`
  (host-шим **read-only**, FAT writer не реализован).
- ❌ MANAGED-EXC `File.ReadAllText` → `FileNotFoundException`
  (плумбинг функционален; файла нет т.к. write denied).
- ❌ MANAGED-EXC `File.Delete` → `FileNotFoundException`
  (`DeleteFileW` отдаёт `ERROR_FILE_NOT_FOUND`).

Важно: host-шим **открывает существующие `\sharpos\*`** (доказано
загрузкой сборок: `[host] FileOpen "\sharpos\fx\*.dll" → ok`). Значит
`File.ReadAllText`/`FileStream` **реального `\sharpos\`-файла должны
работать** — IO-плумбинг функционален. Следующий тапль: read-only мост
`System.IO` → `SharpOSHost_FileOpen/Read/Size` + writable FAT для
полного rw-цикла.

---

## 3. OS-идентификация / machine / user

После step 99 — все ✅. Реализованы через
`SharpOSHost_GetSystemString(kind)` (`OS/src/PAL/SharpOSHost/SystemIdentity.cs`)
с kind-таблицей `MachineName`/`UserName`/`SystemDir`/`HostName`/`OsName`/
`TimeZoneName`. PAL — только zero-extension ASCII → UTF-16 + Win32
length-protocol. Версия → `SharpOSHost_GetOSVersion`.

- ✅ `RuntimeInformation.OSDescription` (через `RtlGetVersion`).
- ✅ `RuntimeInformation.RuntimeIdentifier` → `unknown` (managed).
- ✅ `Environment.OSVersion` → 10.0.26100.
- ✅ `Environment.MachineName` → `SHARPOS` (`GetComputerNameW`/`Ex`).
- ✅ `Environment.UserName` → `local` (через secur32 sentinel +
  `GetUserNameExW`).
- ✅ `Environment.SystemDirectory` → `\sharpos\system32`.
- ✅ `Dns.GetHostName` → `sharpos` (через
  `SystemNative_GetHostName` → `KindHostName`).

Замечание: `Process.GetCurrentProcess().ProcessName`/`WorkingSet64` → 🟡
`InvalidOperationException: Process has exited` — CoreCLR думает что
Unix, лезет в `\proc\self\stat`. По-прежнему deferred (`Process` модели
у нас нет).

---

## 4. Environment-переменные

- ✅ `Environment.GetEnvironmentVariable("PATH")` — одиночная.
- ✅ `Environment.GetEnvironmentVariables()` — пустой словарь (step 73
  fix `GetEnvironmentStringsW` → double-NUL block, переподтверждено
  в census step 99).

---

## 5. Threading

После step 98 (Phase E9.a) — все core примитивы ✅, остаются только
ThreadPool/Task/Timer на E10-E11.

- ✅ `Thread.CurrentThread.ManagedThreadId`, `Interlocked.*`,
  `lock`/`Monitor` (uncontended).
- ✅ `new Thread(λ).Start()` / `.Join()` (step 98). Маршрут:
  `Thread::CreateNewOSThread` → `SharpOSHost_CreateThread`
  (с `CREATE_SUSPENDED`) → `Scheduler.SpawnHosted` (per-thread TEB +
  TLS, gs-base swap в CoopSwitch) → `SharpOSHost_ResumeThread`
  (`MakeRunnable`). `Join` → `WaitForSingleObject` → `JoinEvent.Wait`.
- ✅ `Thread.Sleep(1)` — через `SharpOSHost_Sleep` →
  `Scheduler.Sleep` (TimerQueue + HPET).
- ✅ `ThreadPool.QueueUserWorkItem` — step103+ (LocalAlloc + CondVar +
  GetSystemTimes + IOCP shim landed).
- ✅ `Task.Run + .Result` — step103+ через ThreadPool + Task continuation.
- ✅ `Task.Delay(1).Wait(2s)` — step111+step112 закрыли:
  1. **QPC/Stopwatch tied to HPET** (FILETIME 100ns + HPET sub-second
     mix) — `ProcessorIdCache.ProcessorNumberSpeedCheck` (System.
     Threading.Lock cctor) больше не deadlock'ает в managed-spin без
     yield (раньше QPC инкрементил `static int64_t` на 1 за вызов,
     `t < oneMicrosecond` навсегда true).
  2. `WaitForMultipleObjects/Ex` n=1 forward к `WaitForSingleObject`
     (раньше no-op `return 0` = WAIT_OBJECT_0 → MRES.Wait busy-spin).
  3. `WaitForSingleObject` finite timeout (Event/Semaphore/Mutex/
     Thread): HPET-deadline yield-poll вместо degrade в `Wait()` (inf).
  4. `AddressWait.WaitOnAddress` finite timeout: same HPET-deadline
     pattern (без bucket park; WakeByAddress не cancel'ит timer).
- ✅ `Task.Delay(3s).Wait(1s)` — Wait timeout honoured **+** long-task
  shutdown safety: `GetThreadIOPendingFlag` stub (step112) даёт
  `PortableThreadPool.WorkerThread.IsIOPending` чисто завершиться;
  раньше worker exit path кидал `EntryPointNotFoundException` без
  catch выше → uncaught propagation → HALT.
- ✅ `System.Threading.Timer (50ms)` — step114 probe green. Earlier
  step103 Timer skip is obsolete.

NB: `yield`/итераторы — **НЕ** threading (см. §0, ✅). Не путать.

---

## 6. Crypto / RNG

После step 99 — все census-probe'ы ✅. SHA-256 алгоритм в managed C#
(`OS.Kernel.Crypto.Sha256`, FIPS 180-4), PAL forwarders в обоих ABI
(BCrypt + OpenSSL EVP).

- ✅ `Guid.NewGuid`, `Convert.ToBase64String`, `Random.Shared.Next`
  (managed).
- ✅ `RandomNumberGenerator.Fill` — через
  `SystemNative_GetCryptographicallySecureRandomBytes` или
  `CryptoNative_GetRandomBytes` → `SharpOSHost_FillRandom`
  (kernel-side RNG).
- ✅ `SHA256.HashData` — через `CryptoNative_EvpDigestOneShot` или
  `BCryptCreateHash/HashData/FinishHash` → `SharpOSHost_Sha256_*` →
  `OS.Kernel.Crypto.Sha256`.

---

## 7. Globalization / encoding / regex / compression

- ✅ `Encoding.UTF8` roundtrip (кириллица + эмодзи), `String.Normalize(FormD)`,
  `CultureInfo.InvariantCulture`, `Regex.IsMatch` — **в памяти**
  (bytes↔string) корректно.
- ⚠️ **Отображение** не-ASCII в консоли — мусор (`â`/`ð¡`). НЕ баг
  рантайма: `SharpOSHost_DebugWrite(byte*)` кастит UTF-8-байты по-байтно
  в `char` и шлёт в UEFI ConOut, а firmware-шрифт эмодзи-глифов не
  имеет. На UEFI-текст-консоли не лечится (правильный i18n — Phase B
  own-console / позже). Вывод программ держать ASCII; не-ASCII только
  если действительно нужен и есть свой рендерер.
- ❌ MANAGED-EXC `CultureInfo.GetCultureInfo("ru-RU")` →
  `CultureNotFoundException` — **активен globalization-invariant mode**
  (ICU нет; именованные культуры не поддерживаются **by design**,
  invariant работает; не баг).
- ❌ MANAGED-EXC `GZipStream` → `ZLibException` («underlying
  compression routine could not be loaded») — нативный zlib отсутствует
  (чисто, ловимо).

---

## 8. Сеть / создание процессов

- ❌ PAL-STUB `new Socket(...)` — ловимый `SEHException` (§11 закрыт
  step 81); ждёт winsock-поверхность (`WSAStartup`/`socket`/`bind`/...).
- ✅ `Dns.GetHostName` → `sharpos` (step 99: через
  `SystemNative_GetHostName` → `KindHostName`).
- ❌ PAL-STUB `Process.Start(dummy)` — нет процессной модели на SharpOS;
  ловимо без halt'а.

Сетевого стека (winsock) и спавна процессов нет (ожидаемо).

---

## 11. Единый некатчабл корень — ЗАКРЫТ (step 90) ✅

**Статус:** закрыт. `Socket` / OpenSSL `RNG` / `SHA256` и прочие
P/Invoke-через-stub trap'ы теперь ловятся как catchable `SEHException`
— верифицировано полным census-прогоном (`OK=20 DEG=2 FAIL=22`, ноль
HALT'ов; см. `done/step081.md`). Threading-cohort (`new Thread()`,
`ThreadPool`, `Task.Run`, `Timer`, `Thread.Sleep`) **остаётся**
гарантированным HALT — это другой класс (direct `SharpOSHost_Panic`
в `SleepEx`/`SwitchToThread`-стабах, C-SEH не поднимается, walker
не запускается). Threading-PAL — отдельный фронт (Phase E).

**Что было** (до step 90). Симптом во всех 💥-случаях:

```
[__C_specific_handler] ... filter returned 0
[seh] invalid Rip=0x<стек-адрес> — stop walk
[SehDispatch] no handler matched — HALT
[PANIC] unhandled exception
```

**Root cause (step-89 диагностика, см. `done/step080.md`).** Walker
**корректно** размотает все стандартные кадры по UNWIND_INFO — это
проверено per-opcode трейсом и сверкой с источником `dotnet-runtime-
sharpos`. Точка отказа — кадр, чья начальная сигнатура (`53 56 55 48
8B EC 48 8B D9 8B 4B 08`…) и custom personality `CallDescr-
WorkerUnwindFrameChainHandler` однозначно идентифицируют его как
**`CallDescrWorkerInternal`** (handwritten asm trampoline в
`src/coreclr/vm/amd64/CallDescrWorkerAMD64.asm`), через который
CoreCLR проводит managed-to-unmanaged transitions.

CoreCLR'овский **stub-mechanism** (P/Invoke, reflection invoke,
helper-method transitions) НЕ использует обычный `call`/`ret` —
вместо retaddr-слота на стеке пушится указатель на `Frame*`
структуру (per-thread linked list, head в `Thread::m_pFrame`).
Кадр между «callee» и «caller» физически разорван — настоящий
continuation context живёт **в `Frame*`-объекте**, не в стеке.

Наш walker наивно читает `*rsp` как return-address и попадает на
этот self-referential `Frame*` указатель (= стековый адрес) → fails
IsValidIp → HALT. Personality routine этого кадра
(`CallDescrWorkerUnwindFrameChainHandler` — см. `vm/exception-
handling.cpp:2114`) в **search-pass — no-op**, потому что в реальном
Windows OS-dispatcher сам подхватывает FrameChain через
`Thread::m_pFrame` после возврата personality `ExceptionContinue-
Search`. Наш SehDispatch этого механизма НЕ имеет.

**Не «port RtlVirtualUnwind» и не «PE/Win64 dispatcher protocol»** —
эти теории были опровергнуты в step-89. Реальный fix — **интеграция
walker'а с CoreCLR's per-thread FrameChain**:

1. При обнаружении stub-frame'а (по personality address —
   `CallDescrWorkerUnwindFrameChainHandler`, `FixContextHandler`,
   `ReverseComUnwindFrameChainHandler`, etc.) обратиться к
   `GetThread()->m_pFrame`.
2. Дёрнуть текущий top `Frame*`, вызвать его виртуал
   `UpdateRegDisplay(REGDISPLAY*)` — `Frame*` сам знает, где лежит
   настоящий caller's Rip/Rsp/Rbp/saved-regs.
3. Продолжить раскрутку с восстановленного контекста, поп'нуть
   `Frame*` с FrameChain.

**Что сделано** (step 90). Реализовано ровно три части — без
порта `RtlVirtualUnwind`, без threading-PAL:

1. Форк: два `SharpOSHost_*` helper'а в `vm/exceptionhandling.cpp`,
   возвращают/обновляют `Thread::m_pFrame` (read и pop).
2. Kernel C# `SehDispatch.TryActivateFrameChain(Context*)`:
   читает `m_pFrame`, для активного `InlinedCallFrame` (frameId=1,
   `m_pCallerReturnAddress != 0`) overrid'ит `ctx->{Rip,Rsp,Rbp}`
   из полей `CallerRA`/`CallSiteSP`/`CalleeSavedFP`. Анти-
   реактивация через `CallSiteSP > ctx->Rsp` guard (не нужно
   поп'ать в search pass — pop делает personality во время
   unwind через `CleanUpForSecondPass`).
3. Hook'и в **обоих** walker'ах SehDispatch — search pass
   (`DispatchException`) и unwind pass (`RtlUnwind`) — при
   `IsValidIp(controlPc) == false` пробуем FrameChain skip
   до bail-а.

Frame layout в форке **без vtable** (ID-based dispatch — см.
`vm/frames.h`):
```
+0:  FrameIdentifier _frameIdentifier   (1 = InlinedCallFrame)
+8:  PTR_Frame       m_Next              (~0 = FRAME_TOP)
+24: m_pCallSiteSP                       (caller's RSP)
+32: m_pCallerReturnAddress              (caller's RIP — managed JIT)
+40: m_pCalleeSavedFP                    (caller's RBP)
```

Step-71 (`CallCatchFunclet` RBP) и step-72 (`GetStackSlot` GC RBP)
остаются локальными пластырями для **двух других** классов кадров
(native-origin throw + GC stack-slot RBP). step-89 диагностический
trace-scaffolding (gated `const bool TraceUnwind/Trace = false` в
`SehUnwind`/`SehDispatch`) живёт в коде на случай повторных
раскопок (ILC дед-кодит когда `false`).

**Не покрыто** (если когда-то потребуется): другие Frame-типы
(`HelperMethodFrame`, `TransitionFrame`, `PInvokeCalliFrame`,
`UMThunkUnwindFrameChainHandler`'ные кадры и т.д.). В текущей
census-батарее они не триггерятся; если будущий probe их встретит,
расширение TryActivateFrameChain тривиально (добавить ветку по
`frameId`).

---

## 12. Runtime mechanics / EH gaps (step 119)

Открыты при расширении пробной батареи `normal-hello` секциями 8–11
(RUNTIME MECHANICS / ADVANCED / STRING.FORMAT / REGEX LADDER). Три
новых класса limit'ов, общий корень — **CLR-internal C++ exception
path** (`0xE06D7363 .PEAVEEMessageException@@`). Стоковый CoreCLR
оборачивает это в managed exception на ранней стадии EH dispatch;
наш hosted runtime пробрасывает дальше, не материализуя managed-layer
wrap'ы на inner frames.

### 🟡 LIMIT-12.1 — `Exception.StackTrace` пустой для EE-internal exceptions

Symptom: ловим exception, читаем `.StackTrace` — `string.Empty` или `null`
там где стоковый CoreCLR заполнил бы full trace с frame'ами.

Repro: `normal-hello` Sec 9 (`throw; rethrow caught` / `throw ex; rethrow
caught`). До правки тесты asserted на content; теперь только на сам
факт catch. Внутренние exception'ы брошенные через C++ EH path
показывают `[seh] throw code=0xE06D7363 type=.PEAVEEMessageException@@`.

Корень: `SehUnwind` не заполняет `Exception._stackTrace` поле когда
exception доезжает managed-frame'а через EE-internal путь (не через
`RhpThrowEx`).

Workaround: для tests — не assert'ить на StackTrace content; в prod
exception сам по себе catchable, type/message работают, только trace
пустой.

### 🟡 LIMIT-12.2 — EE-internal exceptions пробивают managed catch на inner frames

Symptom: `try { thingThatThrowsEEInternal(); } catch (Exception) { }` —
catch **не срабатывает**, exception летит в top-level handler как
если бы inner try-catch отсутствовал.

Repro: `normal-hello` Sec 9 `absurd-size alloc -> deterministic
exception` — `new int[int.MaxValue]` бросает через EE C++ path,
inner `catch (OutOfMemoryException)`/`catch (OverflowException)`/
`catch (Exception)` не ловят, exception летит в outer Probe()
handler где CoreCLR translates как OOM. Probe помечено `Skip()` с
явной пометкой.

Корень: тот же frame-walker — managed catch frame не обнаруживается
SehDispatch'ем для EE-internal C++ exception. Возможно `FrameChain`
walker не активирует `InlinedCallFrame` skip-through для этого
конкретного call-site (как было с Socket до Phase D / step 90).

Workaround: catchable на top-level handler. Внутренние exception-typed
catches вокруг alloc-heavy code не надёжны.

### 🟡 LIMIT-12.3 — Cctor exception → raw, не оборачивается в `TypeInitializationException`

Symptom: stock CoreCLR оборачивает любой exception из cctor body в
`TypeInitializationException` с `InnerException = original`. Наш hosted
runtime пробрасывает **raw original** exception без обёртки.

Repro: `normal-hello` Sec 8 `cctor throws -> TIE (or raw on SharpOS
hosted)` — выкидывает `InvalidOperationException("boom in cctor")` из
cctor body. Stock ожидал бы `catch (TypeInitializationException)`; на
нас catch'ает `catch (InvalidOperationException)` напрямую. Probe был
расширен для приёма обоих вариантов.

Корень: ClassConstructorRunner / cctor dispatch в нашем форке не
делает explicit wrap exception'а перед throw. В стоке это уровень
`EEFileLoad` wrap в CorHost'е.

Workaround: catch по конкретному типу — работает.
`catch (TypeInitializationException)` — нет.

### Скан-точка

Эти три (плюс related XMM-lost P0-1 и CollidedUnwind P0-2 из
`donext.md`) — связаны общим SehUnwind / FrameChain walker'ом. Скорее
всего после P0-1 / P0-2 фиксов LIMIT-12.1 / 12.2 закроются попутно.

---

## Сводка census

| Step | OK | DEG | FAIL | HALT | Δ |
|------|----|-----|------|------|---|
| 73 | 19 | 2 | 20 | 1 | baseline (пост-FrameChain) |
| 81 (D) | 20 | 2 | 22 | 0 | §11 закрыт; HALT'ов больше нет |
| 87 | 19 | 2 | 20 | 0 | firmware-free CoreCLR (свой FAT/AHCI/EBS) |
| 94 (E1-E4) | 20 | 2 | 22 | 0 | scheduler online, ping-pong |
| 98 (E9.a) | 22 | 2 | 22 | 0 | `new Thread()/.Start()/.Join()` + `Thread.Sleep` |
| 99 (PAL cleanup) | 37 | 2 | 7 | 0 | identity-strings + version + TZ + SHA-256 (kernel C#) + FS-attr stubs + RNG |
| **100 (E9.b)** | **37** | **2** | **7** | **0** | Event/Semaphore/Mutex реально работают (был fake-handle WAIT_OBJECT_0 lie); census без сдвига -- нет специальной пробы под Monitor.Wait/Pulse |
| 107 | 42 | 2 | 7 | 0 | Regex.IsMatch + 4 другие пробы закрыты via Object::Validate non-heap guard (interpreter GC info false-positive) |
| **108** | **42** | **2** | **7** | **0** | VirtualBox first-class: GPT FAT-mount + AHCI multi-PRDT corruption fix (FIS->Count vs PRDT-loop counter split) + FAT read perf (BulkBytes 64K, FAT-sector cache, contig-run coalesce, CopyBlock) + BigStack out of GcHeap. Census без сдвига -- та же поверхность что step107, теперь работает и под QEMU и под VBox |
| **112** | **44** | **2** | **7** | **0** | SehUnwind заполняет `KNONVOLATILE_CONTEXT_POINTERS` (real root для phantom-OBJECTREF класса); +2 OK: `Task.Delay(1).Wait(2s)` + `Task.Delay(3s).Wait(1s)` (Wait timeout honoured + long-task shutdown safety через `GetThreadIOPendingFlag` stub). `DynamicMethod.GetILGenerator` теперь Heisenbug-free (раньше fluctuated между ✅ и `TerminateProcess(COR_E_EXECUTIONENGINE)` через layout shifts). n=5 stable (QEMU 3x + VirtualBox 2x). |
| **113** | **54** | **2** | **7** | **0** | ThreadPool stress 1000x20 + hill-climbing green; silent qemu-exit root = `sqrt`↔`lm_sqrt` infinite recursion fixed by direct `sqrtsd`; step72 RBP override removed after clean validation. |
| **114** | **51** | **2** | **7** | **0** | Release CoreCLR (`/Ox`) on bare metal, FH4 C++ EH personality, weak `SharpOSHost_HeapAlloc` fallback fix for Release `malloc` strong-fold. QEMU+VirtualBox green. |
| 109 | 42 | 2 | 7 | 0 | NativeArena: 14 GcHeap.AllocateRaw call-sites переехали (CRT malloc, TEB/TLS, SEH structs, Sha256State, TPA buf, PE file buffers). M1-M4 из memory-ownership §9 закрыты. PhaseReport capacity baseline. |
| **110** | **42** | **2** | **7** | **0** | Precise GC walker: PE .pdata→UNWIND_INFO→gcInfo decoder (header + slot table + transitions + per-PC live state). GcContextSpill shellcode + SehUnwind frame iteration + slot address resolver. KernelGC.Collect использует precise по дефолту, GC.ReclamationDisabled = false (sweep реально освобождает). M5 закрыт. Pioneered BinaryPrimitives full surface в std. |
| **119** | **113** | **2** | **8** | **0** | Comprehensive runtime mechanics probe battery: Sec 8 RUNTIME MECHANICS (27 sub-probes: boxing × 6, array covariance × 4, interface dispatch × 4, virtual × 3, generic sharing × 4, cctor × 4, module init × 1, write barrier × 3), Sec 9 ADVANCED (11: GC roots through EH, finally ordering nested, exception filter, throw/throw ex rethrow, GC+Span, Array.Copy overlap, thread handoff+GC, XMM6+ across throw, OOM, nested PAL), Sec 10 STRING.FORMAT (8), Sec 11 REGEX LADDER (9: L0-L8 + Compiled). +59 проб vs step 114. Открыты §12 limit'ы: StackTrace empty for EE-internal, EE exceptions bypass inner catches, cctor exception not wrapped in TIE. |

Оставшиеся FAIL после step 100:
- `Socket ctor (TCP)` — нужна winsock-поверхность (ws2_32).
- `File.WriteAllText` / `File.Delete` — нужна writable FAT.
- `File.ReadAllText` — нет пробного файла (вытекает из `File.WriteAllText`).
- `CultureInfo.GetCultureInfo("ru-RU")` — by design (globalization-invariant
  mode CoreLib).
- `GZipStream` — нужна zlib P/Invoke поверхность.
- `Process.Start(dummy)` — нет процессной модели.
- `Process.GetCurrentProcess().*` — 🟡 (DEG): CoreCLR думает что Unix,
  лезет в `\proc\self\stat`.

**Однострочно:** вычислительное ядро .NET (JIT/GC/generics/LINQ/
exceptions/reflection/System.Text.Json/yield/Unicode/Regex) — рабочее
на голом железе; PAL-пробелы в OS-сервисы в основном **деградируют
управляемо** (catchable SEH / чистые BCL-исключения). Некатчабл-корень
§11 закрыт step112. Приоритет фронтов теперь: Release/D10-D11 hardening
→ IST для #PF/#DF → finalizers/GC-production или Roslyn resolver+IO.

---

*Repro: `work/normal-hello/Program.cs` (gitignored scaffold). Пересборка
только тест-DLL; форк/ядро без изменений; вывод чистый при
`Verbose=false` + boot-probes off + EH-trace gated. Обновлять при каждом
новом census-прогоне.*
