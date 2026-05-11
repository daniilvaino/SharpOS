## D5 — FINALIZED

### Решение

**ABORT_FATAL stub в pal/sharpos/'s CreateThread (и других thread-related PAL функциях) для Phase 2 spike + Phase 6.1.**

Threading реализация откладывается до Phase 6.2 (после Phase 3 — scheduler session). В коде через комментарии явно фиксируется что это **temporary стейт**, не «потоков не будет никогда».

В TARGET_SHARPOS mode CoreCLR конфигурируется так чтобы CreateThread физически не вызывался во время spike Hello World. Если вызывается — это unexpected code path который требует investigation.

### Архитектура трёх стадий

```
┌──────────────────────────────────────────────────────────────────┐
│ Phase 2 — Spike (Windows-hosted TARGET_SHARPOS) + Phase 6.1     │
│ (initial bare metal)                                            │
│                                                                  │
│ TARGET_SHARPOS mode:                                            │
│ • Custom narrow conditional (форкнут от TARGET_WASM concept)    │
│ • Только finalizer thread skip в vm/ceemain.cpp:934             │
│ • Stack unwinding РАБОТАЕТ (не WASM)                            │
│ • Funclets / EH РАБОТАЮТ (не WASM)                              │
│ • VirtualProtect / page protection РАБОТАЕТ (не WASM)           │
│ • JIT executable memory РАБОТАЕТ (не WASM)                      │
│                                                                  │
│ CoreCLR configuration:                                          │
│ • DOTNET_GCName=zerogc.dll → 0 GC threads                       │
│ • DOTNET_gcServer=0 → Workstation mode (mandatory для Zero GC)  │
│ • DOTNET_gcConcurrent=0 → no background GC thread               │
│ • Hello World scenario не triggers ThreadPool (lazy init)       │
│                                                                  │
│ Threading reality:                                              │
│ • 0 GC threads (Zero GC)                                        │
│ • 0 finalizer thread (TARGET_SHARPOS skip)                      │
│ • 0 ThreadPool threads (lazy)                                   │
│ • CreateThread физически NEVER called                           │
│                                                                  │
│ pal/sharpos/'s CreateThread = ABORT_FATAL                       │
│ • Если когда-то вызывается → громкий crash                      │
│ • Validates что TARGET_SHARPOS mode реально устраняет threading │
└──────────────────────────────────────────────────────────────────┘
                              ↓
┌──────────────────────────────────────────────────────────────────┐
│ Phase 3 — Full threading session (отдельный этап)               │
│                                                                  │
│ • Scheduler implementation                                      │
│ • Thread struct (registers + FXSAVE + stack + guard page)       │
│ • Context switch routine                                        │
│ • Ready queue, Local APIC timer для preemption                  │
│ • Thread.Start, async/await, ThreadPool, Monitor, lock keyword  │
│ • Synchronization primitives (mutex, condvar, semaphore)        │
│                                                                  │
│ pal/sharpos/'s CreateThread всё ещё ABORT_FATAL                 │
│ (D5 пока не переоткрыт — это случится в Phase 6.2)              │
└──────────────────────────────────────────────────────────────────┘
                              ↓
┌──────────────────────────────────────────────────────────────────┐
│ Phase 6.2 — Production (Roslyn/PowerShell ready)                │
│                                                                  │
│ • TARGET_SHARPOS mode disabled                                  │
│ • Standard CoreCLR configuration (нормальный GC + finalizer)    │
│ • D5 переоткрывается с реальной implementation                  │
│ • Likely Variant B: pal/sharpos/'s CreateThread →               │
│   SharpOSHost_CreateThread (C-ABI hop) → provider (form decided  │
│   в момент Phase 6.2 — может быть C/C++ glue, generated veneer, │
│   direct kernel symbol, или C# UCO export per revised D11) →    │
│   SharpOS scheduler capability из Phase 3                       │
│ • Host thread API дизайнится в момент Phase 6.2 с актуальным    │
│   контекстом готового scheduler'а                               │
│                                                                  │
│ Roslyn / PowerShell работают полноценно                         │
└──────────────────────────────────────────────────────────────────┘
```

### Реализация

#### pal/sharpos/thread.cpp

cpp

```cpp
// pal/sharpos/thread.cpp
//
// THREADING NOT YET IMPLEMENTED
// =============================
//
// Threading is a planned future capability, not a removed/abandoned one.
// SharpOS Phase 3 will introduce:
//   - Scheduler with preemption (Local APIC timer)
//   - Thread struct (registers + FXSAVE + stack + guard page)
//   - Context switch routine
//   - Synchronization primitives (mutex, condvar, semaphore)
//   - Thread.Start / Task / async-await / ThreadPool support
//
// Phase 6.2 will then provide real CreateThread implementation that
// routes through SharpOSHost — provider invokes SharpOS scheduler capability
// (provider form decided at Phase 6.2 per revised D11).
//
// Currently in TARGET_SHARPOS mode, CoreCLR is configured to not require
// threads:
//   - Workstation GC + non-concurrent + Custom Zero GC → 0 GC threads
//   - TARGET_SHARPOS conditional skips finalizer thread creation
//     (vm/ceemain.cpp:934)
//   - ThreadPool is lazy and not triggered by Hello World scenarios
//
// Therefore CreateThread (and related thread-management PAL functions)
// should NEVER be called during Phase 2 spike or Phase 6.1.
// If it does — that indicates an unexpected code path that must be
// investigated. ABORT_FATAL provides a loud signal for diagnosis.

HANDLE WINAPI CreateThread(
    LPSECURITY_ATTRIBUTES lpThreadAttributes,
    SIZE_T dwStackSize,
    LPTHREAD_START_ROUTINE lpStartAddress,
    LPVOID lpParameter,
    DWORD dwCreationFlags,
    LPDWORD lpThreadId)
{
    SHARPOS_PAL_ABORT(
        "CreateThread called — not expected in TARGET_SHARPOS mode. "
        "CoreCLR should be configured to not require threads at this "
        "phase (Zero GC + finalizer skipped). Investigate which code "
        "path triggered this call. "
        "Threading implementation is planned for Phase 6.2 after "
        "Phase 3 scheduler session."
    );
}

// Same pattern for other thread-management PAL functions:
// - WaitForSingleObject / WaitForMultipleObjects
// - CreateMutex / CreateEvent / CreateSemaphore (если CoreCLR их зовёт
//   до того как threading needed)
// - SuspendThread / ResumeThread / GetThreadContext / SetThreadContext
// - CloseHandle для thread handles
//
// All ABORT_FATAL с честным narrative «temporary, реализация придёт».
```

#### TARGET_SHARPOS conditional patch

В нашем CoreCLR fork один patch в `vm/ceemain.cpp:934`:

cpp

```cpp
// vm/ceemain.cpp:934
#if !defined(TARGET_WINDOWS) && !defined(TARGET_WASM) && !defined(TARGET_SHARPOS)
    FinalizerThread::FinalizerThreadCreate();
#else
    FinalizerThread::EnableFinalization();
#endif
```

Только это одно изменение upstream. Минимальный merge cost.

#### CoreCLR configuration (env vars для spike)

bash

```bash
DOTNET_GCName=zerogc.dll       # Custom Zero GC implementation
DOTNET_gcServer=0              # Workstation mode (mandatory для Zero GC)
DOTNET_gcConcurrent=0          # No background GC thread
```

Это standard production-supported pattern (Standalone GC API stable since .NET Core 2.1, .NET 10 production).

### Phase split что появился

**Phase 6 теперь split на 6.1 и 6.2**:

**Phase 6.1 — Initial bootstrap on bare metal**:

- TARGET_SHARPOS mode active
- Zero GC + non-concurrent + Workstation
- ABORT_FATAL stubs для thread-management PAL functions
- Validates что архитектура host/guest split работает на реальном железе
- Demo-grade managed scenarios (Hello World, basic JIT, console output)
- **НЕ продакшн** — финализация не работает, long-running scenarios accumulate leaks

**Phase 6.2 — Production**:

- После Phase 3 done (full scheduler)
- TARGET_SHARPOS mode disabled
- Standard CoreCLR configuration
- D5 переоткрыт с реальной implementation
- Roslyn / PowerShell работают полноценно

### Tradeoffs TARGET_SHARPOS mode

**Что работает в этом mode**:

- Memory allocation (VirtualAlloc through mmap mapping)
- JIT executable memory (VirtualProtect functional)
- Stack unwinding (portable Windows unwinder, per D13)
- Exception handling (funclets functional)
- Console output

**Что не работает**:

- Финализаторы (resource leaks acceptable для shortlived demos)
- Long-running scenarios (memory accumulates, leaks накапливаются)
- Roslyn / PowerShell production scenarios (нужна Phase 6.2)
- Debugging integration (TARGET_WASM-style skip but для нашего custom mode мы НЕ skip'аем debugger init — это узкий patch только для finalizer)

**Важно**: TARGET_SHARPOS уже **отличается** от TARGET_WASM — мы не trigger'им WASM-specific behaviors что ломают spike. Только finalizer skip.

### Variants which were rejected

|Variant|Почему отклонён|
|---|---|
|**2. ERROR_NOT_SUPPORTED stub**|Маскирует problems — CoreCLR молча обходит failure, дальше странности невидимо. Лучше громкий crash чем тихий bug.|
|**3. Direct pthread в pal/sharpos/**|Нарушает host/guest split principle — pal/sharpos/ напрямую трогает OS. Не валидирует C-ABI threading границу. На bare metal не транслируется.|
|**4. Real implementation через SharpOSHost сейчас**|Premature work — если TARGET_SHARPOS mode реально устраняет CreateThread, эта работа не нужна сейчас. Host thread API лучше дизайнить в момент Phase 6.2 с готовым scheduler'ом контекстом.|
|**TARGET_WASM mode целиком**|Слишком инвазивен — ломает stack unwinding, funclets, VirtualProtect, executable memory. Spike fundamentally сломается.|

### Что украдено

|Артефакт|Источник|Используется для|
|---|---|---|
|Standalone GC API|`dotnet/runtime` GC infrastructure (production since .NET Core 2.1)|Zero GC через `DOTNET_GCName` env var|
|Custom Zero GC implementation reference|`kevingosse/CoreCLR.ZeroGC` GitHub repo|Starting point для нашей Zero GC implementation|
|TARGET_WASM single-threaded pattern|`vm/ceemain.cpp:932-942` (`#if !defined(TARGET_WASM)`)|Inspiration для нашего узкого TARGET_SHARPOS conditional|
|ABORT_FATAL pattern с информативным сообщением|D3 framework (Critical_NotYetImplemented категория)|pal/sharpos/'s CreateThread implementation|

### Что своё

- **TARGET_SHARPOS conditional** — наш custom narrow target. Именно один patch в одном месте (line 934 в ceemain.cpp), не trigger'ит остальные WASM behaviors
- **Phase 6.1 / 6.2 split** — наша концептуальная организация bootstrap vs production
- **Narrative pattern в коде** — комментарии что temporary, реализация придёт

### Связь с другими decisions

- **D2** (TLS): Phase 5.5 (Native TLS bring-up) делает infrastructure для main thread. После Phase 3 — расширяется на multi-thread. D5's TARGET_SHARPOS не зависит от D2 — TLS нужна independent даже для single thread.
- **D3** (policy для не-реализованных): CreateThread под категорию Critical_NotYetImplemented. ABORT_FATAL pattern из D3 framework.
- **D6** (thread state ownership): откладывается, нет threads на spike. Переоткрывается в Phase 6.2 одновременно с D5.
- **D7** (TLS implementation): covered через D2's Phase 5.5.
- **D8** (GC suspension mechanism): полностью отпадает в spike (нет GC threads — нет suspension). Переоткрывается в Phase 6.2 если standard GC configuration требует.

### Принципы установленные D5

(дополнение к D1-D4)

15. **Custom narrow conditionals лучше invasive mode switches.** Когда нужно skip конкретное поведение upstream — добавить узкий conditional (один `||` в одном месте), а не активировать весь mode что меняет десятки behaviors. Минимизирует upstream merge cost и предотвращает unintended side effects.
16. **Narrative в коде важен для temporary states.** Stub'ы ABORT_FATAL должны explicit'но объяснять что они **временные**, а не «навсегда не будет». Будущий разработчик (или ты сам через год) должен сразу понять что это deliberate placeholder pending Phase X, а не abandoned feature.
17. **Откладывание design до момента когда контекст готов.** Дизайн host thread API делается в Phase 6.2, когда scheduler уже существует и контекст известен. Дизайнить сейчас upfront — premature work что вероятно нужно будет переделать когда реальные constraints станут видны.
18. **Phase split на bootstrap (X.1) и production (X.2).** Когда Phase достигает реальную deliverable но не full production capability — оформляется split. X.1 = validation что architecture работает (demo-grade), X.2 = production полноценный после dependencies готовы. Honest scope, не overclaiming.