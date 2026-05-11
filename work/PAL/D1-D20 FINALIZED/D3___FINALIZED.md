## D3 — FINALIZED

### Решение

**Scaffolding для process'а реализации PAL** — минимальная форма с возможностью расширения по реальной необходимости.

Per-function classification по 5 категориям. Default = ABORT_FATAL для unclassified. Sequential plain text logging. Без upfront over-engineering — features добавляются только когда реально упрёмся в проблему.

### Граница production/scaffolding

**Scaffolding** (живёт во время разработки, разбирается после Phase 6):

- Classification table
- Logging hooks при stub call
- ABORT_FATAL handler для unclassified
- POLICY_TABLE.md документ с rationale

**Production** (остаётся в готовом PAL):

- Реализации функций (категория 1: forward to host)
- Возможно нормальное logging для observability — но это уже не «PAL stub trace», а general runtime logging

**Файловая организация** (per D9 flat structure): scaffolding files (`trace.cpp`, `policy_table.cpp`) живут как обычные domain files в `pal/sharpos/`, рядом с production files (`memory.cpp`, `thread.cpp`, и т.д.). Никакого `scaffolding/` или `forward/` subdirectory split. После завершения Phase 6 scaffolding files идентифицируются через header comment marker (`// SCAFFOLDING — to be removed after Phase 6 stable`) и удаляются manual. POLICY_TABLE.md остаётся как historical document.

### 5 категорий policy

|Категория|Поведение|Когда применять|
|---|---|---|
|**Critical_ImplementedViaHost**|Forward to SharpOSHost (реальная реализация)|Функции без которых CoreCLR не работает (VirtualAlloc, CreateThread, WriteFile)|
|**Critical_NotYetImplemented**|ABORT_FATAL с диагностикой|Critical функции которые ещё не реализованы — нужны но пока stub|
|**Optional_ReturnError**|LOG + return ERROR_NOT_SUPPORTED|Функции у которых CoreCLR имеет fallback path|
|**Cosmetic_FakeSuccess**|LOG + return success без действий|Функции с некритичной семантикой (thread name, profiling hooks)|
|**Unknown_AbortFatal**|ABORT_FATAL — default для unclassified|Функция ещё не investigated — падаем громко|

### Organic discovery loop

```
Stage 1: все 144 функции стартуют в категории Unknown_AbortFatal

Stage 2: запуск spike
        ↓
        CoreCLR упирается в unknown функцию
        ↓
        ABORT_FATAL с информативной диагностикой:
          - имя функции
          - аргументы (если просто читаются)
          - последовательность предыдущих PAL calls в логе
        ↓
Stage 3: агент классифицирует функцию
        - читает signature в pal.h
        - смотрит use cases в CoreCLR vm/
        - решает категорию
        - обновляет classification table (compile-time)
        - дописывает rationale в POLICY_TABLE.md
        ↓
Stage 4: пересборка spike (быстро, секунды)
        ↓
Stage 5: повторить с Stage 2
```

Это **trace-driven progressive classification**. Не нужно классифицировать все 144 функции заранее — это была бы guesswork. Investigation происходит только для функций которые CoreCLR реально вызывает.

### Минимальная реализация

**Core (нужно с самого начала):**

cpp

```cpp
// pal/sharpos/policy_table.h
enum class StubPolicy {
    Critical_ImplementedViaHost,
    Critical_NotYetImplemented,
    Optional_ReturnError,
    Cosmetic_FakeSuccess,
    Unknown_AbortFatal,
};

// Compile-time policy lookup для each function
StubPolicy GetPolicyFor(const char* funcName);
```

**Logging:**

Sequential plain text format, читаемый глазами:

```
VirtualAlloc(0, 65536, MEM_COMMIT, PAGE_READWRITE) = 0x7f1234567000
VirtualAlloc(0, 1048576, MEM_COMMIT, PAGE_READWRITE) = 0x7f1234600000
GetSystemInfo(...) = OK
QueryPerformanceFrequency(...) = 10000000
LoadLibraryExW("libclrjit.so") = 0x7f1234700000
WriteFile(stdout, "Hello\n", 6) = TRUE
```

**Sink:**

- Spike (Linux): stdout
- Bare metal (потом): serial port

**ABORT_FATAL output**:

```
============================================================
PAL ABORT: Unclassified function called
Function: CreateIoCompletionPort
Args: (likely: handle, existingPort, completionKey, threads)
Previous PAL calls in this run:
  1. VirtualAlloc(0, 65536, ...) = 0x7f...
  2. GetSystemInfo(...) = OK
  3. CreateFileW("foo.txt", ...) = 0x100
  4. CreateIoCompletionPort(...) ← we are here
Action: classify this function in policy_table.cpp
============================================================
```

**Documentation:**

`POLICY_TABLE.md` — для каждой classified функции запись:

- что функция делает
- почему такая категория
- что произойдёт если она не реализована
- если cosmetic_fake_success — почему safe возвращать success без действий

### Что НЕ делаем upfront

- ❌ JSONL и structured log formats
- ❌ Persisted state между runs (каждый run from scratch)
- ❌ Replay/diff between runs
- ❌ Breakpoints (используется обычный gdb если надо)
- ❌ Runtime override через env vars (compile-time table, пересборка быстрая)
- ❌ Multiple default modes (только ABORT_FATAL единственно правильный)
- ❌ Granular log levels (один log level)
- ❌ Stats summary

### Что добавляем **только если упрёмся в реальную проблему**

|Feature|Когда добавлять|Если не нужно — не делать|
|---|---|---|
|**Phase markers в логе**|Если контекст вызова реально путается ("это preinit или после init?")|Лог даёт достаточно контекста через последовательность|
|**Counter/stats summary**|Если агент хочет планировать "что реализовывать следующим" по частоте вызовов|Не делать|
|**Granular log levels**|Если объём лога становится unmanageable|Не делать|
|**Runtime override без пересборки**|Если пересборка медленная и итерации частые|Пересборка быстрая — не делать|

Принцип: **add when actually needed**, не upfront just in case.

### Что украдено

|Артефакт|Источник|
|---|---|
|ABORT_FATAL pattern для unimplemented|`dotnet/runtimelab/.../wasm/PalWasm.cpp` (`RhFailFast()`)|
|Stub return ERROR_NOT_SUPPORTED pattern|`dotnet/runtime/src/coreclr/pal/src/` различные функции|
|PORTABILITY_ASSERT концепция|NativeAOT runtime|

### Что своё

- 5 категорий classification (категоризация наша, паттерны Microsoft'а в каждой категории отдельно украдены)
- Organic discovery workflow
- POLICY_TABLE.md формат

### Принципы установленные D3

(дополнение к D1-D2)

8. **REMOVED** (superseded by D9 flat structure).
9. **Add when actually needed, не upfront just in case.** Базовый минимум сейчас, расширения по мере реальной потребности. "Может понадобится" — самое плохое обоснование для добавления feature.
10. **Trace-driven progressive classification.** Когда нужно категоризовать большую surface (144 функции PAL) — не делаем upfront guesswork, а organic discovery: упёрся → классифицировал → продолжил.
11. **Plain text там где машинная обработка не нужна.** JSONL и structured formats — over-engineering для scaffolding которое читается глазами агента. Текст в текстовом формате.