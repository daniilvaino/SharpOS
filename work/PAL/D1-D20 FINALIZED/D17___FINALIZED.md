# D17 — FINALIZED

## Решение

**Trace buffer дампится на:**

- Normal process/runtime shutdown (clean exit)
- PAL `ABORT_FATAL`
- NativeAOT `FailFast` hook (where available)
- Unhandled platform crash hook (best-effort)
- Kernel panic handler (на bare metal)
- Explicit `SharpOSTrace_DumpNow(reason)` internal call

**Trace buffer НЕ дампится:**

- Periodically (нет threading в Phase 2/6.1 per D5)
- By env var trigger (потеряем при crash до trigger)

## Минимальный API

```cpp
// pal/sharpos/trace.cpp

enum class TraceDumpReason : uint32_t {
    CleanExit,
    PalAbortFatal,
    NativeAotFailFast,
    UnhandledException,
    KernelPanic,
    Manual,
};

void SharpOSTrace_Record(...);
void SharpOSTrace_DumpNow(TraceDumpReason reason);
void SharpOSTrace_InstallDumpHooks();  // platform-specific
```

## ABORT_FATAL pattern

```cpp
#define SHARPOS_PAL_ABORT(msg)                              \
    do {                                                    \
        SharpOSTrace_RecordAbort(__FILE__, __LINE__, msg);  \
        SharpOSTrace_DumpNow(TraceDumpReason::PalAbortFatal); \
        abort();                                            \
    } while (0)
```

ABORT_FATAL **обязан** дампить trace перед abort. Иначе главный диагностический сигнал неполный — D5's CreateThread ABORT_FATAL теряет контекст событий что привели к нему.

## Platform mapping

### Windows-hosted spike

|Trigger|Hook|
|---|---|
|Clean exit|`atexit(SharpOSTrace_DumpAtExit)`|
|Fatal PAL path|`SHARPOS_PAL_ABORT` macro → `DumpNow(PalAbortFatal)` → abort|
|Unhandled native crash|`SetUnhandledExceptionFilter` → `DumpNow(UnhandledException)`|
|Console Ctrl+C (optional)|`SetConsoleCtrlHandler` → `DumpNow(Manual)`|

**Sink**: stderr (hardcoded в первом commit). Файловый sink — future option.

### Bare metal SharpOS

|Trigger|Hook|
|---|---|
|Kernel panic|`KernelPanicHandler` → `DumpNow(KernelPanic)`|
|NativeAOT FailFast|FailFast hook → `DumpNow(NativeAotFailFast)`|
|Normal shutdown|`KernelShutdown` → `DumpNow(CleanExit)`|

**Sink**: serial port (hardcoded в первом commit). Crash dump buffer / file system — future option когда они появятся.

## Crash-safe implementation discipline

`SharpOSTrace_DumpNow()` **должен** быть best-effort crash-safe:

- **No malloc** — не аллоцируем в crash path
- **No locks** что могут deadlock (если уже в crash — буфер может быть corrupted, lock может не быть released)
- **No std::string / heap formatting** — printf-style буферы на стеке только
- **No PAL calls** — иначе recursive crash
- **Raw platform write only** — `WriteFile` на Windows, serial port out на bare metal
- **Reentrancy guard** — flag что dump уже идёт, second call returns immediately
- **Tolerate corrupted partial record** — если ring buffer partially overwritten, продолжаем со следующего

Без этой discipline dump сам упадёт в crash path и потеряем весь буфер.

## Что украдено

|Артефакт|Источник|Использование|
|---|---|---|
|atexit handler pattern|C standard library|Clean exit dump на Windows|
|SetUnhandledExceptionFilter|Win32 API|Crash dump на Windows|
|Ring buffer crash-safe pattern|Standard kernel diagnostic infrastructure|BSS-allocated trace storage|
|TraceDumpReason enum approach|Linux kernel oops/panic reason codes|Structured reason vs string|

## Что наше

- `SharpOSTrace_DumpNow()` core function
- `TraceDumpReason` enum для нашего set причин
- `SHARPOS_PAL_ABORT` macro что integrates trace dump
- Platform adapter functions
- Crash-safe implementation discipline rules

## Отклонённые варианты

|Вариант|Почему отклонён|
|---|---|
|**Periodic flush**|Требует threading (D5 = ABORT_FATAL для CreateThread на spike). Над-engineering для scaffolding (D3). Шум в timing low-level scenarios.|
|**On-demand only via env var**|Buffer теряется при crash до того как trigger'нуть. D3 уже отверг runtime override toggles.|
|**Env var trigger в первом commit**|Premature — D3 принцип «add when needed». Hardcoded sinks per platform достаточны для bring-up. Env var sink configuration — future option если понадобится.|

## Future options (НЕ в первом commit)

Если конкретные проблемы появятся:

- `SHARPOS_TRACE_SINK=stderr|file|none` для sink selection (только если hardcoded стало мешать)
- `SHARPOS_TRACE_FILE=...` для file path
- Watchdog/periodic dump — только если hangs/deadlocks станут реальный blocker
- Manual trigger via console — только если debugging required

Эти добавляются **по реальной потребности**, не upfront.

## Связь с другими decisions

- **D3** (scaffolding policy): trace dump это часть scaffolding, добавляем минимум features
- **D4** (NativeAOT FailFast): hook в FailFast routine для crash path coverage
- **D5** (CreateThread = ABORT_FATAL): ABORT_FATAL сначала дампит trace через макрос
- **D9** (структура pal/sharpos/): trace.cpp в pal/sharpos/ как domain file
- **D15** (tracer location): dump function живёт в том же trace.cpp module

## Принципы установленные D17

(в дополнение к D1-D16 + Phase 2 Redesign + TARGET_SHARPOS Build Configuration)

38. **Diagnostic infrastructure покрывает оба важных пути.** Clean exit + fatal exit + explicit manual call. Без любого из трёх — теряем data в важном случае.
    
39. **Crash-safe discipline для diagnostic dump.** No malloc, no locks, no heap formatting, raw platform write, reentrancy guard. Иначе dump сам падает и теряем буфер целиком.
    
40. **ABORT_FATAL обязан дампить trace перед abort.** Главный диагностический сигнал — события что привели к ABORT_FATAL. Без dump в этом пути диагностика неполная.
    
41. **Hardcoded defaults в первом commit, configurability — by demand.** Env var sink configuration не upfront. Hardcoded stderr/serial достаточны. Configurability добавляется когда конкретная потребность появилась.
    

---
