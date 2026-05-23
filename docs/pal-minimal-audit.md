# Audit: партиальные/фейковые реализации в PAL/sharpos

**Date:** 2026-05-23 (step103 wrap-up)
**Триггер:** диагностический blackout — `__stdio_common_vsprintf_s` молча
вернул `?` для всех format-вызовов CoreCLR, из-за чего unhandled
exception printer выдал `"Undefined resource string ID:0x?"` 25× подряд
без единой полезной цифры. Поверх этого: пользователь верно заметил,
что "минимал" реализаций накопилось много, и они не везде явно
помечены.

## Что считаем "не полная реализация"

Любая из трёх категорий ниже — кроме случаев, когда **в комментарии
ABOVE the function** явно сказано: что именно не реализовано, почему,
и при каких условиях оно сломается.

### 🔴 Falsifying — пишут фейк, возвращают success

Самый опасный класс. Caller получает "успех" с мусором в out-параметре.
Симптом: дальние косвенные проявления (как `0x?` в resource IDs).

| Symbol | Файл / линия | Что делает |
|---|---|---|
| `__stdio_common_vsprintf_s` | `crt_imp_stubs.cpp:1006` | Пишет `"?"` + NUL в buffer, returns 1. **Полностью игнорирует format + args.** |
| `__stdio_common_vsnprintf_s` | `crt_imp_stubs.cpp:1014` | То же |
| `__stdio_common_vsprintf` | `crt_imp_stubs.cpp:995` | Сбрасывает buffer в `""`, returns 0 |
| `__stdio_common_vsscanf` | `crt_imp_stubs.cpp:1028` | Returns 0 (no fields parsed). Не помечено как "не парсит". |
| `puts` / `fputs` / `fputws` | `crt_imp_stubs.cpp:1036/1044/1052` | Только narrow ASCII range. `fputws` молча конвертит wide в narrow зеркально (UTF-16 не валидирован) |
| `_wassert` | `crt_imp_stubs.cpp:1130` | Печатает текст, но потом **не halt'ит** (хотя _wassert по контракту noreturn) — calling code продолжает с битым state |
| `__stdio_common_vfprintf` | `crt_imp_stubs.cpp:1022` | Returns 0. Игнорирует format + args. |

**Чинить:** все vsprintf-семейство должно пользоваться существующим
`diag_format()` engine ([crt_imp_stubs.cpp:904]) и писать в caller's
buffer. Engine уже умеет `%d %i %u %x %X %p %s %S %c %%` + `l`/`ll`
length specifiers — этого хватает для CoreCLR diag паттернов.

### 🟠 Unmarked subset — partial implementation без disclaimer

В коде написана какая-то работа, но не вся; в комментарии не сказано
что именно. Caller думает что full Win32-семантика — а получает
подмножество.

| Symbol | Файл / линия | Что упущено |
|---|---|---|
| `MultiByteToWideChar` | `crt_imp_stubs.cpp:1245` | Только ASCII / UTF-8 fast path. Не валидирует UTF-8, не делает proper BMP → surrogate, не поддерживает CP_ACP. Header comment явно говорит "ASCII identity fast path", но **только в комменте над функцией**, в форме непонятной для caller'а. |
| `WideCharToMultiByte` (если есть) | проверить | Скорее всего тот же partial scope |
| `FormatMessageW` | `crt_imp_stubs.cpp:2353` | Маркировано как `minimal implementation covering the CoreCLR usage`. ✅ OK маркировка. **Но:** игнорирует `FORMAT_MESSAGE_FROM_SYSTEM`, `FROM_HMODULE`, `IGNORE_INSERTS`, `MAX_WIDTH_MASK`. Поведение если эти флаги пришли — **silently** пропускает (return 0). Caller получает 0 без diagnostic. |
| `LocalAlloc` | `crt_imp_stubs.cpp:2316` | Игнорирует `LMEM_FIXED`/`LMEM_MOVABLE` различия (всегда fixed). `LMEM_ZEROINIT` обрабатывается. Не маркировано над функцией. |
| `bsearch` | `crt_imp_stubs.cpp:621` | Real impl, ✅ portable, no caveats. OK. |
| `CoCreateGuid` | `crt_imp_stubs.cpp:1333` | Проверить — возможно используется `RDRAND` без fallback на pseudo-PRNG |

**Чинить:** добавить comment-выше с явным списком unsupported flags +
**runtime assert/halt при попадании в неподдержанный путь** (вместо
silent fallback). Альтернативно — переименовать в `*_Minimal` или
сделать TRAP в неподдержанной ветви.

### 🟡 Marked-minimal but problematic — corner cases

Помечено как partial в comment'е, но silent break при corner cases.

| Symbol | Файл / линия | Когда сломается |
|---|---|---|
| `Sleep` / `SleepEx` | `crt_imp_stubs.cpp:1568/1574` | Не yield'ит scheduler. Single-thread = OK; с E11 ThreadPool — должен через Scheduler.Yield(). |
| `SwitchToThread` | `crt_imp_stubs.cpp:1581` | Возвращает 0 (no thread switched). Pre-E11 OK. Post-E11 должен через Scheduler. |
| `WaitForSingleObject` | `crt_imp_stubs.cpp:1526` | Имеет реальную dispatch logic (через ThreadStubs.cs), но для **некоторых handle types** (fake-handle sentinels) возвращает WAIT_OBJECT_0 сразу. Маркировано. |
| `SetThreadStackGuarantee` | `crt_imp_stubs.cpp:1610` | No-op stub returning success. Comment явный. Но callers иногда полагаются на guarantee — если стек уперся, тихо crash. |
| `FlushInstructionCache` | `crt_imp_stubs.cpp` | No-op (комм. "coherent x86/x64 I-cache"). Это **архитектурно правильно** для x86/x64 — OK. |
| `FlushProcessWriteBuffers` | `crt_imp_stubs.cpp` | No-op (single-core). Правильно для single-CPU. Под SMP в E13 — **нужно**. |
| `ResumeThread` | `crt_imp_stubs.cpp` | Returns prev suspend count = 0 (no-op). Сейчас работает потому что threads не suspendеs in CREATE_SUSPENDED state. Когда поддержим — break. |
| `SetThreadErrorMode` | `crt_imp_stubs.cpp:1590` | No-op success. OK для нашей среды. |
| `SetThreadPriority` | `crt_imp_stubs.cpp` | No-op success. Single-priority scheduler — sensible. |
| `_wassert` | `crt_imp_stubs.cpp:1130` | Печатает текст, но не halt'ит. **Перенесено в 🔴** выше — это falsification, не minimal. |

### 🟢 OK by design — действительно not applicable

| Symbol | Why no-op is correct |
|---|---|
| `FreeLibrary` | Мы не загружаем DLLs (статическая линковка). |
| `UnmapViewOfFile` | GC reclaims; нет mmap'а в смысле POSIX. |
| `FlushViewOfFile` | То же. |
| `VirtualFree` (см. `crt_imp_stubs.cpp:334`) | SharpOS GC reclaims; explicit free — no-op safe. |
| `VirtualProtect` | "SharpOS doesn't enforce page-level W^X" — **выглядит как** дизайн, но **на самом деле** это известная дыра — мы делаем RWX для всех PE-патчей через `[host] FileOpen RWX patch`. Перенести в 🟠 если хотим security tier. |
| `CloseHandle` | Для наших fake handle sentinels — no-op safe. Для real HandleTable — заинвалидирует через HT. |
| `OutputDebugStringA/W` | Дебуггера нет; silent OK. |

## Cross-cutting проблемы

### 1. Diagnostic blackout

`vsprintf_s` family пишет `?` → **никакая** CoreCLR error message не
прочитываема. Это amplifies все остальные баги: вместо
`"InvalidCastException: Cannot cast 'Foo' to 'Bar'"` мы видим
`"Undefined resource string ID:0x?"`.

**Priority 1 fix:** перенаправить vsprintf-семейство на
`diag_format()` engine + caller's buffer.

### 2. Нет convention для маркировки

Текущая практика смешанная:
- Иногда `// no-op` в comment ABOVE.
- Иногда `// (returns success constant)` в forward-declarations table (lines 107-340).
- Иногда нет ни того ни другого (vsprintf_s — claim "real impl below", но real impl это `?`).

**Recommendation:**
- Принять **suffix convention** на уровне реализации: `*_Stub` /
  `*_Partial` / `*_NoOp` — **не** in symbol name (linker не поможет),
  а в **CRT registry table** (раздел `// --- 124 imports from
  /tmp/imports-ucrt.txt ---`, line 107+).
- Перед каждой real impl — обязательный comment с:
  - **Implementation status:** Full / Partial / No-op / Trap.
  - **What's not supported:** список.
  - **What happens at the boundary:** trap / silent / log.

### 3. "Logging suppressed — too noisy" — anti-pattern

`vsprintf_s` была заглушена с motivation `"too noisy"`. Это решило
проблему **диагностики build phase**, но создало **диагностический
blackout runtime phase**. Аргумент в пользу заглушки — engine
печатал format string дословно с `%s`/`%d` markers; читаемее когда args
substituted.

**Решение:** не отключать output, а **подключить format engine** так
чтобы output был prettified. Это **то же количество I/O**, но
полезного. Также — gate через `Verbose` flag (как DebugPrint).

### 4. Phase-зависимые stubs не обновляются

Несколько stubs корректны для текущей фазы, но **сломаются** при
переходе на следующую:
- `Sleep` / `SwitchToThread` не yield'ят — OK pre-E11, не OK post-E11
- `ResumeThread` — OK пока нет CREATE_SUSPENDED, не OK с E11.
- `FlushProcessWriteBuffers` — OK на single-CPU, не OK с E13 SMP.

**Recommendation:** в комменте указывать **последний phase при котором
этот no-op корректен** и **симптом нарушения** ("под E13 SMP это
silently corrupts cross-CPU writes"). Тогда при переходе на след. phase
audit-пробег по таким маркерам подсветит candidates.

## План действий

### Immediate (step104 candidates)

1. **🔴 vsprintf-family на real engine** — `__stdio_common_vsprintf*`
   используют `diag_format()` engine с output в caller's buffer. ~40
   строк wiring.
2. **`_wassert` → halt** — выполнить контракт (noreturn). Альтернативно
   document'нуть violation.
3. **`__stdio_common_vsscanf`** — это **trap**, не silent return 0.
   Caller думает что parsed успешно — silently feed zeros.

### Medium (отдельный step, может быть E11.5 / E12 prep)

4. **🟠 unmarked subsets промаркировать** — за каждой добавить
   structured doc-comment.
5. **Phase-dependent stubs аннотировать** — например
   `// VALID-UNTIL: pre-E13 SMP — under SMP this leaks cross-core writes`.
6. **Conventions** — settled на формат и enforce через grep-CI
   (`./tools/check_pal_stubs.ps1`).

### Long-term

7. **Diagnostic infrastructure** — то о чём пользователь спросил
   "почему до сих пор нет нормального механизма". Кандидаты:
   - Symbolizer вызываемый из Panic handler (RVA → function name через
     ImageBase + .pdata).
   - Structured exception printer (print full type + message +
     stack trace) вместо текущей "Undefined resource string ID" каши.
   - `[FAIL]` test reporter в Probe должен снимать **catch (Exception
     ex) → ex.GetType().Name + ex.Message** unified (уже почти так,
     просто нужен sprintf_s real чтобы Message не была `?`).

## Также — список не в PAL

Audit касается только `pal/sharpos/`. Другие места где partial impls
**могут** прятаться:

- `OS/src/Kernel/Threading/*` — некоторые structures имеют
  `// TODO E13` / `// SMP-unsafe`.
- `OS/src/PAL/SharpOSHost/*` — наши managed bridges (Iocp, Mutex,
  Event) — minimal cooperative semantics, OK на single-CPU.
- `OS/src/Boot/EH/*` — managed EH stack довольно полный, но есть
  каверзы (cross-image walk не реализован, `HwFaultBridge` IF в
  catch funclet).

Отдельный audit для них — step после.
