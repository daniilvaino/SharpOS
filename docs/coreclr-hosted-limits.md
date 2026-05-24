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
  > ⚠️ **Generic `as T` / `(T)x` с `where T : class` НЕ работает** даже
  > в hosted-tier'е (step103-доказано пробой `generic `as T` cast`):
  > `[FAIL] PAL-STUB/SEH: External component has thrown an exception.`
  > Корень не RhTypeCast (тот вызов работает) — корень в **трансляции
  > C++ msc throws (code 0xE06D7363) в managed**: на TARGET_UNIX/SHARPOS
  > `PAL_TRY/PAL_CATCH` оборачивает C++ throw как `SEHException`, и
  > `GetThrowableFromException` в `clrex.cpp:615` для SEHException
  > branch не имеет case для msc magic → возвращает managed
  > `System.Runtime.InteropServices.SEHException` вместо проброса
  > оригинального `EEMessageException::m_kind`. Inner
  > `catch (InvalidCastException)` мимо, outer `catch (SEHException)`
  > ловит. **Тот же корень — позднее `TerminateProcess(COR_E_EXECUTIONENGINE)`
  > на Regex/Culture probes**: `CreateThrowable.GetThrowableMessage()`
  > пытается грузить `mscorrc.dll` → not found → throw → cascade →
  > fatal. Подробно — `nativeaot-nostd-kernel-limits.md` §2 + agent-memory
  > [[reference_msc_throw_becomes_sehexception]].
  > **Workaround в user-code:** ловить `Exception` (или дополнительно
  > `SEHException` после specific) — managed-side fix.
  > **Полный fix:** добавить в `clrex.cpp:615` SEHException branch
  > case `0xE06D7363` который парсит ThrowInfo и возвращает оригинальный
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
- ⏳ `ThreadPool.QueueUserWorkItem` — Phase E10.
- ⏳ `Task.Run` / `Task.Delay` — Phase E11 (async/await JIT helpers).
- ⏳ `Timer (1ms)` — Phase E10 (нужен dedicated timer thread или
  IOCP-style wheel).

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
| 109 | 42 | 2 | 7 | 0 | NativeArena: 14 GcHeap.AllocateRaw call-sites переехали (CRT malloc, TEB/TLS, SEH structs, Sha256State, TPA buf, PE file buffers). M1-M4 из memory-ownership §9 закрыты. PhaseReport capacity baseline. |
| **110** | **42** | **2** | **7** | **0** | Precise GC walker: PE .pdata→UNWIND_INFO→gcInfo decoder (header + slot table + transitions + per-PC live state). GcContextSpill shellcode + SehUnwind frame iteration + slot address resolver. KernelGC.Collect использует precise по дефолту, GC.ReclamationDisabled = false (sweep реально освобождает). M5 закрыт. Pioneered BinaryPrimitives full surface в std. |

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
управляемо** (catchable SEH / чистые BCL-исключения), кроме одного
некатчабл-корня §11. Приоритет фронтов: RTC-мост → FS-read →
OS-id/env → (крупное) SehUnwind-upstream / threading-PAL.

---

*Repro: `work/normal-hello/Program.cs` (gitignored scaffold). Пересборка
только тест-DLL; форк/ядро без изменений; вывод чистый при
`Verbose=false` + boot-probes off + EH-trace gated. Обновлять при каждом
новом census-прогоне.*
