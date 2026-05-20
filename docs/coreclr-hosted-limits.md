# CoreCLR-hosted (stock .NET on bare metal): карта ограничений

Живой документ. Перечень того, что **работает / не работает** когда
стоковый `dotnet build` DLL исполняется **байт-в-байт** на форк-CoreCLR
(`dotnet-runtime-sharpos`, статически слинкован в kernel-образ;
steps 68–73). Это **другой режим**, чем `docs/nativeaot-nostdlib-limits.md`
(тот — про само ядро SharpOS как NativeAOT+NoStdLib). Здесь — про
гостевой стоковый .NET поверх нашего PAL.

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

Managed-слой **сбриджен** (отдаёт чистые BCL-исключения, не trap):

- ✅ `Path.GetFullPath` (чистый managed).
- ❌ MANAGED-EXC `Directory.EnumerateFiles` → `DirectoryNotFoundException`.
- ❌ MANAGED-EXC `File.WriteAllText` → `UnauthorizedAccessException`
  (host-шим **read-only**).
- ❌ MANAGED-EXC `File.ReadAllText` → `FileNotFoundException`
  (плумбинг функционален; файла нет т.к. write denied).
- ❌ PAL-STUB `Directory.GetCurrentDirectory`, `File.Delete`,
  `File.Exists`, `Directory.Exists`, `Path.GetTempPath`.

Важно: host-шим **открывает существующие `\sharpos\*`** (доказано
загрузкой сборок: `[host] FileOpen "\sharpos\fx\*.dll" → ok`). Значит
`File.ReadAllText`/`FileStream` **реального `\sharpos\`-файла должны
работать** — IO-плумбинг функционален. Самый дешёвый следующий выигрыш:
read-only мост `System.IO` → `SharpOSHost_FileOpen/Read/Size`.

---

## 3. OS-идентификация / machine / user

Все — ❌ PAL-STUB (trap-stub, ловимо):
`RuntimeInformation.OSDescription`, `Environment.OSVersion`,
`Environment.MachineName` / `UserName` (ищет `secur32.dll`) /
`SystemDirectory`. Под .NET нет Windows/Linux-PAL → OS-detect
P/Invoke'и = trap-stub. `RuntimeInformation.RuntimeIdentifier` → ✅
(`unknown`).

Замечание: `Process.ProcessName`/`WorkingSet64` → 🟡
`InvalidOperationException: Process has exited` — CoreCLR думает что
Unix, лезет в `\proc\self\stat`.

---

## 4. Environment-переменные

- ✅ `Environment.GetEnvironmentVariable("PATH")` — одиночная.
- ⚙️ `Environment.GetEnvironmentVariables()` — был ❌ OOM
  (`GetEnvironmentStringsW` → `nullptr` → CoreCLR считал длину null-блока
  → `OutOfMemoryException`). **step-73 fix применён** (constant-stub
  класса `GetModuleFileNameW`): возвращаем валидный пустой double-NUL
  блок → ожидается ✅ пустой словарь. **Не переподтверждён прогоном** —
  проверить на следующем census-ране, тогда → ✅.

---

## 5. Threading

- ✅ `Thread.CurrentThread.ManagedThreadId`, `Interlocked.*`,
  `lock`/`Monitor` (uncontended) — примитивы синхронизации работают.
- 💥 HARD-PANIC: `new Thread().Start()`, `ThreadPool`, `Task.Run`,
  `Timer`, `Thread.Sleep` — все упираются в `SwitchToThread`
  (CRT trap-stub). SharpOS одноядерный, **без планировщика потоков**.

NB: `yield`/итераторы — **НЕ** threading (см. §0, ✅). Не путать.
Workaround: реального треда нет; threading-PAL — отдельный крупный
фронт (планировщик + `SwitchToThread`/waits/timers).

---

## 6. Crypto / RNG

- ✅ `Guid.NewGuid`, `Convert.ToBase64String`, `Random.Shared.Next`
  (всё чистый managed).
- 💥 HARD-PANIC: `RandomNumberGenerator.Fill`, `SHA256.HashData` —
  грузят `libSystem.Security.Cryptography.Native.OpenSsl` (отсутствует)
  → §11. Managed-crypto есть, нативного backend'а / entropy-PAL нет.

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

- 💥 HARD-PANIC `new Socket(...)` (грузит native socket-путь → §11).
- ❌ PAL-STUB `Dns.GetHostName`, `Process.Start` (ловимый `SEHException`
  — **не** hard-panic-класс).

Сетевого стека и создания процессов нет (ожидаемо).

---

## 11. Единый некатчабл корень (отложенный фронтир) ⚠️

Симптом во всех 💥-случаях (`Socket`, OpenSSL `RNG`/`SHA256`,
OS-thread spawn) **один и тот же**:

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

Объём — **не threading-PAL**: нужна *структура* `Thread*`+`Frame*`
(singleton, одна инстанция), не scheduler. `GetThread()` в форке
уже работает; `m_pFrame` поддерживается CoreCLR'ом автоматически.
Gap — **только читалка** в нашем SehDispatch. Оценка: **1-2 недели**
sequential работы (импорт layout-совместимых деклараций `Thread` +
`Frame*`-семейства типов + walker integration + регрессии).

Step-71 (`CallCatchFunclet` RBP) и step-72 (`GetStackSlot` GC RBP)
остаются локальными пластырями для **двух уже-решённых** классов
кадров. step-89 диагностический trace-scaffolding (gated `const
bool TraceUnwind = false` в `SehUnwind`, `Trace = false` в
`SehDispatch`) живёт в коде на случай повторных раскопок.

---

## Сводка census (step-73)

`✅ 19 · 🟡 2 · ❌ 20 · ⏭️ 8 · 💥 1` (Socket; RNG/SHA256/thread-spawn —
тот же класс).

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
