# Phase 2 sage queries — Round 4 synthesis

Sage 1 (verified .NET 10 source paths via local clone) и Sage 2 (.NET 10 cross-checked through docs) ответили независимо. Convergence сильная — расхождение только в Q4, и при ближайшем рассмотрении оно сводится к engineering choice, не fundamental disagreement.

---

## Where sages converged (decisive)

### Q1 — Path 2.5: bounded measurement spike

Не "trap → implement → trap → implement" (текущий режим — слепой). Не full spec-first (167 функций без trace data — 60-80% мёртвого текста).

**Правильно**: 2-3 недели **trace-backed bring-up + design-back**.

Сделать commit series:
1. `pal/sharpos/trace.{h,cpp}` — preinit-safe ring buffer in BSS, no malloc, raw syscall write.
2. `func_id` enum для всех stubs.
3. **Phase markers** — `DSO_LOAD / CTOR / BEFORE_PAL_INIT / IN_PAL_INIT / AFTER_PAL_INIT / CORECLR_INITIALIZE / JIT / MANAGED_MAIN / SHUTDOWN`.
4. **Policy stubs** вместо unconditional abort:
   - `LOG_AND_FAIL` (return error)
   - `LOG_AND_FAKE_SUCCESS` (return S_OK / 0 / NULL)
   - `LOG_AND_FORWARD_TO_HOST` (real impl)
   - `LOG_AND_ABORT_FATAL` (process invalid, can't continue)
5. Реализовать **trivial leaf functions локально в pal/sharpos/** (no host needed):
   - `MultiByteToWideChar` / `WideCharToMultiByte` — wrapper над `minipal_convert_utf8_to_utf16` (минипал уже vendored, ~20 LOC)
   - `GetCurrentProcessId` / `GetCurrentThreadId`
   - `GetSystemInfo` (minimal/fake)
   - `GetTickCount` / `GetTickCount64`
   - `_snprintf_s` / `sprintf_s` etc. (CRT wrappers)
6. Run corerun Hello.dll, collect trace.
7. По trace написать **первую версию HOST API spec** только для observed surface (~30-60 funcs).

После этого — Phase 6 implementation на основе spec.

### Q2 — Trace-first spec, не spec-first и не spike-first

Pure spec-first на 165 функций → много мёртвого текста.  
Pure spike → локальные хаки без понимания фаз.

Spec классифицирует функции по 4 buckets:
- **A. Local C++ impl** (UTF, IDs, GetLastError, simple CRT) — leafs without host
- **B. SharpOSHost-forwarded** (memory, threads, sync, file, module loading)
- **C. Bring-up disabled/fake** (createdump, debugger transport, profiler/perfjitdump)
- **D. Fatal unsupported** (CreateProcess, remote process memory)

### Q3 — EH Path A для bring-up. Запретить crossing exceptions

B (win-x64 без PAL) — соблазнительно из-за единого .pdata мира, но цена = эмулировать существенный кусок Win32 process/thread/sync semantics. Может быть больше чем Unix PAL path.

C (JIT patch для .pdata) — Frankenstein. CoreCLR JIT уже эмитит .pdata на Linux (`generateCFIUnwindCodes` true только для NativeAOT ABI; regular CoreCLR JIT эмитит UNWIND_INFO). Patching не нужен.

**Жёсткое правило**: 
```
SharpOSHost_* C ABI:
  - no managed exception escapes into CoreCLR
  - catch-all at boundary
  - return HRESULT / BOOL / errno-like
  - fatal host exception → explicit failfast, not unwind across CoreCLR
```

EH spike (позже): managed try/catch, try/finally, filter, NullRef/AV path, PAL call returns error, SharpOSHost throws internally + catches at boundary + returns error code.

### Q5 — Custom PAL tracer, не LD_DEBUG

LD_DEBUG показывает symbol resolution/load order, **не actual call frequency**. Static `__libc_init_array` call graph — too noisy, false positives.

**Tracer requirements:**
- No malloc, no std::string
- Static BSS ring buffer
- Raw `write(2)` syscall only for emergency dump
- No locks requiring PAL
- Dump on abort / process exit
- Offline symbolize via addr2line

**Phase change manually** в bootstrap.cpp constructor → CTOR; entry/exit обёртки `PAL_InitializeCoreCLR` → IN/AFTER_PAL_INIT; etc.

Все calls до `PAL_InitializeCoreCLR` = **preinit surface** = scope для preinit-safe shim.

### Q6 — Table injection, NOT static lookup

`SharpOSHost_GetApiTable()` **не должен быть callable из static init** — NativeAOT PAL требует `PalInit()` before exports.

**Разделение pal/sharpos/:**
```
pal/sharpos/preinit/  — C++ only, no NativeAOT, no SharpOSHost, no heap, no managed
  trace.cpp
  errno.cpp (Get/SetLastError storage)
  spinlock.cpp
  tls_min.cpp
  utf.cpp (UTF-8/UTF-16 via minipal)
  bootstrap.cpp

pal/sharpos/forward/  — calls host API table (only after explicit init)
  memory.cpp
  thread.cpp
  file.cpp
  sync.cpp
  module.cpp
```

**Init order** (explicit, not static):
```
1. process/image starts
2. CoreCLR PAL preinit layer may run (no host calls)
3. SharpOSHost_Initialize → initializes NativeAOT side
4. SharpOSHost prepares API table
5. host calls SharpOSPal_SetHostApiTable(table, version)
6. NOW PAL forwarders may call SharpOSHost_*
7. coreclr_initialize
```

**Для WSL spike**: НЕ вшивать libsharposhost.a в libcoreclr.so. Проще:
- `corerun/sharpos-runner` executable links/dlopens libsharposhost.so
- Then dlopens libcoreclr.so
- Injects API table explicitly via `SharpOSPal_SetHostApiTable`

Static archive внутрь libcoreclr.so — **позже**, когда есть linker map + duplicate-symbol audit.

### Q7 — libstdc++ subset обязателен (15-20 KLOC, 3-6 months)

Sage 1 revision: было "5-10 KLOC" в Round 3, сейчас **15-20 KLOC**.

Components:
- libunwind subset (x86_64 only, vendored уже в `src/native/external/libunwind/`): ~10 KLOC
- libsupc++ EH ABI (`__cxa_*`): 3-5 KLOC (LLVM libcxxabi extractable)
- `__gxx_personality_v0`: 1-2 KLOC
- type_info runtime, operator new/delete: <1 KLOC

**363 EX_TRY sites в vm/ + 28 PAL_TRY** — `-fno-exceptions` не реалистично. C++ EH ABI = hard dependency.

**Стратегия**: import минимальный libc++/libc++abi/compiler-rt subset как **external dependency CoreCLR fork**, не writing from scratch.

**Measurement command для SharpOS**:
```bash
nm -uC libcoreclr.so | grep -E 'std::|__cxa|__gxx|typeinfo|vtable|operator new|operator delete|_Unwind' | sort -u
```

Build с `-nostdlib++ -Wl,--no-undefined` против собственного `libcxxabi_shim.a` — measure что недостаёт.

### Q8 — Custom PAL tracer, ICorProfiler не поможет

Profiling API видит JIT/managed/native transitions, не PAL-call frequency.

**Event format**:
```c
struct PalTraceEvent {
    uint64_t seq;
    uint64_t tsc;
    uint32_t tid;
    uint16_t func_id;
    uint16_t phase;
    void* caller;     // __builtin_return_address(0)
    uintptr_t arg0;
    uintptr_t arg1;
    int32_t result;
    int32_t last_error;
};
```

Сигналы: first-seen order, count_by_func, count_by_phase, preinit_calls, fatal_policy_hits, fake_success_tolerated, unique_callers, JIT-only calls, shutdown-only calls, host-forwarded vs local-only ratio.

---

## Where sages disagreed — Q4 (NativeAOT PAL reuse)

Это единственное реальное расхождение. Stack diagrams показывают суть:

### Sage 1 — VIABLE (intermediate-layer reuse)

```
CoreCLR vm/gc/jit
  ↓ pal.h surface (Win32-shaped, 144 funcs)
pal/sharpos/  (in CoreCLR fork)
  ↓ thin wrappers + Win32 RESERVE/COMMIT bookkeeping
NativeAOT Pal* surface (~35 funcs, libRuntime.WorkstationGC.a)
  ↓ already implemented for Linux
SharpOSHost primitives (long-term: bare-metal)
```

Аргумент: NativeAOT runtime уже работает на Linux. Spike Branch A проверила link-time interop. ~35 NativeAOT Pal* — правильный размер для HOST primitives. Bootstrap: pal/sharpos/'s `PAL_Initialize` calls `RhInitialize(true)` first, потом normal flow.

### Sage 2 — DEAD-END (parallel paths)

```
CoreCLR vm/gc/jit
  ↓ pal.h surface
pal/sharpos/  (in CoreCLR fork)
  ↓ direct
SharpOSHost primitives ←──────────┐
                                  │
NativeAOT runtime ────────────────┘
   (independent path to host)
```

Аргументы:
1. NativeAOT PAL surface ≠ CoreCLR PAL surface (Pal*-prefix не совместим с Win32-shape).
2. NativeAOT PAL сам требует `PalInit()` before exports — adds second runtime initialization sequence that may deadlock with CoreCLR's static init.
3. Adapter "CoreCLR PAL → NativeAOT PAL" фактически = re-implement CoreCLR PAL semantics, no real saving.

### Resolution

При внимательном чтении расхождение меньше чем кажется:
- Оба согласны что **CoreCLR pal.h surface должен быть реализован** в pal/sharpos/. Не can be skipped.
- Оба согласны что **SharpOSHost primitives** (C# layer) — финальная точка для bare-metal.
- Оба согласны что **NativeAOT PAL functions don't replace CoreCLR PAL surface**.

Расхождение: **где сидит middle layer** между pal/sharpos/ и SharpOSHost.

- Sage 1: NativeAOT Pal* служит middle layer (для Linux spike — saving impl effort).
- Sage 2: pal/sharpos/ → SharpOSHost напрямую (cleaner, no double init order risk).

Sage 2 right в долгосрочной перспективе. Sage 1's approach даёт быстрый Linux spike. **Можно начать Sage 1 path для bring-up** (использовать NativeAOT Pal* для leaf calls во время Linux spike), и **в Phase 6 проре-route на Sage 2 architecture** (parallel paths). Это compatible с trace-first spec strategy — в spec явно отметим какие funcs идут "host-forwarded" сразу, а какие "NativeAOT-Pal-via-bootstrap".

---

## Factual corrections от sages

1. **MultiByteToWideChar НЕ iconv-coupled.** В .NET 10 CP_UTF8/CP_ACP делегируют в `minipal_convert_utf8_to_utf16` (`src/native/minipal/utf8.c`, 2151 LOC pure C, deps только `errno.h, limits.h, string.h, assert.h`). Реализация для pal/sharpos/ — **~20 LOC wrapper**. Мой spike concern был неверным.

2. **PAL surface — 144, не 165.** `pal/inc/pal.h` declares 144 functions (87 Win32-shape + 57 PAL_-prefix). Spike's 165 включал eventprovider + palprivate.h + rt/. Reality slightly меньше.

3. **JIT unwind format — Windows-style на Linux.** `jit/compiler.h:8467 generateCFIUnwindCodes` returns true только для NativeAOT ABI. Regular CoreCLR JIT эмитит `UNWIND_INFO` (Windows format) даже на Linux. Path C ("JIT patch для .pdata") **уже сделан upstream** — это not custom work.

4. **Two unwinders на Linux build** (CoreCLR baseline, до нашей Phase 1):
   - Managed JIT'd code → `unwinder/amd64/unwinder.cpp` portable Windows-style (1847 LOC, "borrowed from Windows minkernel")
   - Native libcoreclr.so internals → libunwind via `pal/src/exception/seh-unwind.cpp::PAL_VirtualUnwind` (DWARF CFI)
   
   Phase 1 SharpOS adds **third** unwinder для AOT-emitted SharpOS code, format-compatible с managed unwinder. Не дополнительная fragility — already part of CoreCLR's reality.

5. **libunwind already vendored** в `src/native/external/libunwind/` (3.2 MB, 612 .c files, 52 KLOC total). x86_64-relevant subset: ~10 KLOC. Не нужно искать external source.

6. **NativeAOT runtime — bare-metal-clean.** `nativeaot/CMakeLists.txt:16-19` adds `-fno-exceptions -fno-asynchronous-unwind-tables -nostdlib`. 0 EX_TRY occurrences в `nativeaot/Runtime/`. Custom EH protocol (`RhpThrowEx` assembly → ExInfo chain). Uses libc + pthread, не libstdc++.

7. **Symbol conflict prediction revised**: Sage 1 measured **0-3 realistic conflicts**, не "5-15 expected" из phase6-arch §13. `__cxa_*` only от libstdc++ (NativeAOT does not define), NativeAOT's `operator new` — placement-new overload signature, no conflict. memcpy/memset — single libc source.

8. **`-fno-exceptions` ИСКЛЮЧЕНО**. 363 EX_TRY sites в vm/ + 28 PAL_TRY. C++ EH ABI = hard dependency.

---

## Concrete action plan (post-sage)

Заменить текущий ad-hoc trap loop на следующий commit series в pal/sharpos/:

### Step 1 — Tracer infrastructure (1-2 дня)

- `pal/sharpos/preinit/trace.h` — public macros `PAL_TRACE(func_id, arg0, arg1)`
- `pal/sharpos/preinit/trace.cpp` — BSS ring buffer (no malloc), raw `write(2)` dump on signal/exit
- Phase enum + `extern std::atomic<uint16_t> g_pal_phase`
- Phase setters wrap `PAL_Initialize`, `coreclr_initialize`

### Step 2 — Generate func_id enum + policy stubs (1 день)

- Replace abort-only stubs с policy-tagged stubs:
  ```cpp
  TRAP_POLICY(MultiByteToWideChar, FORWARD)
  TRAP_POLICY(SetEnvironmentVariableW, FAKE_SUCCESS)
  TRAP_POLICY(CreateProcessW, FAIL)
  TRAP_POLICY(DebugBreak, FATAL)
  ```
- Each stub emits `PAL_TRACE` + applies policy

### Step 3 — Local leaf implementations (2-3 дня)

В `pal/sharpos/preinit/`:
- `errno.cpp` — `GetLastError`/`SetLastError` (уже есть, переехать в новую структуру)
- `utf.cpp` — `MultiByteToWideChar` / `WideCharToMultiByte` через minipal
- `procthread_id.cpp` — `GetCurrentProcessId` (`getpid()`), `GetCurrentThreadId` (`gettid()`)
- `time.cpp` — `GetTickCount` / `GetTickCount64` через `CLOCK_MONOTONIC`
- `sysinfo.cpp` — `GetSystemInfo` (minimal: page size, processor count, x64 arch)
- `crt_safe.cpp` — `_snprintf_s`/`sprintf_s` etc. via standard C functions

### Step 4 — Run + collect trace (1 день)

- `corerun Hello.dll` against fully-traced libcoreclr.so
- Dump trace, generate report:
  ```
  FIRST SEEN
  001 MultiByteToWideChar       phase=BEFORE_PAL_INIT  caller=...
  002 GetCurrentThreadId        phase=BEFORE_PAL_INIT  caller=...
  ...
  
  HOTTEST
  GetCurrentThreadId       N
  SetLastError             N
  ...
  
  PREINIT SURFACE (calls before PAL_InitializeCoreCLR)
  - func1
  - func2
  ...
  ```

### Step 5 — Trace-backed HOST API spec (3-5 дней)

Написать `done/phase2-host-api-spec.md`:
- For each observed function: signature, semantics, policy (LOCAL/FORWARD/FAKE/FAIL/FATAL)
- HOST primitives derivation — group forwarded funcs into ~30-50 underlying primitives
- Mark preinit-safe vs post-init separately

### Step 6 — GO/NO-GO для Phase 6

С trace-backed spec в руках — committed to Phase 6 implementation, OR pivot if data shows red flags.

**Total scope**: 2-3 weeks (per Sage 2 bound).

---

## Risks not yet measured

1. **Static init context** — нужен ли preinit-safe shim вообще, или CoreCLR гарантирует что PAL_InitializeCoreCLR called first. Step 4 trace покажет.
2. **Hot-path concentration** — если top-10 functions = 95% calls (likely), implementation effort смещается с "spec all 144" на "perfect 10".
3. **JIT-only PAL surface** — JIT может вызывать functions which не появятся до managed Main. Trace во время JIT compile (force `COMPlus_JitDisasm`) покажет.
4. **Cross-runtime Static init** — даже без NativeAOT-as-middle-layer (Sage 2 path), если SharpOSHost.a ever linked into libcoreclr.so, NativeAOT runtime initializes during dlopen. Tracer phase markers покажут violation.

---

## What we're NOT doing (explicit)

- ❌ Continuing trap-by-trap implementation (current mode)
- ❌ Writing 144-function spec без trace data
- ❌ NativeAOT PAL as full architecture replacement (Sage 2 killer arguments stand)
- ❌ Win-x64 build (Path B)
- ❌ JIT patching (Path C — already done upstream)
- ❌ libstdc++-free CoreCLR (impossible with 363 EX_TRY)
- ❌ Crossing exceptions across SharpOSHost C-ABI boundary

---

## Open questions still

- Sage 2's preinit/forward split — clean engineering, but где LIVE preinit-safe code? `pal/sharpos/preinit/` это C++ файлы в CoreCLR fork repo (Invariant 1 OK — это GUEST). Подтверждаю.
- libsharposhost.so vs static archive для WSL spike — Sage 2 предлагает .so + RTLD_GLOBAL. Согласен — упрощает linker semantics, делает init order explicit.
- Bare-metal libstdc++ subset — measurement deferred до того как нам нужно (post-spike).
