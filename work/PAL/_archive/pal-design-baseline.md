# PAL design baseline (cross-check reference)

**Назначение**: self-contained reference document для cross-check внешним агентом. Содержит весь необходимый context, architectural decisions, empirical data, и D1-D20 решения чтобы агент мог независимо сравнить с альтернативным design plan и доложить divergences.

**Sources** (для тех кто хочет докопаться до первоисточников):
- `plan.md` — high-level phase plan (Phase 2 / Phase 6)
- `CLAUDE.md` — Architectural Invariants 1 & 2
- `done/phase6-architecture.md` — pre-spike architecture spec
- `done/phase2-sage-queries-round4.md` — post-spike open questions
- `done/phase2-sage-round4-synthesis.md` — sage convergent answers
- `done/wsl-spike-archive1.md` — raw spike measurement data
- `done/step066.md` — link-step checkpoint

---

## 1. Context

### 1.1 Project
**SharpOS** — experimental unikernel в C#, NativeAOT + NoStdLib + UEFI / x64. Phase 1 (managed EH, ACPI, timers) закрыта. Cell goal — host CoreCLR (для Roslyn + PowerShell hosted-tier) внутри SharpOS.

### 1.2 Phase position
Phase 2 (current) per `plan.md`: **PAL разведка + дизайн + Linux spike**. Two deliverables:
- Письменная спека PAL surface (signatures + semantics)
- De-risk spike на Linux: managed Hello World через CoreCLR + stub PAL

Phase 6 (next major commit) per `plan.md`: PAL implementation + CoreCLR fork integration. 9-18 месяцев scope.

### 1.3 Architectural Invariants (CLAUDE.md, non-negotiable)

**Invariant 1** — C# is only source language. Никаких `.c`/`.cpp`/`.h`/`.s`/`.asm` файлов в SharpOS repo. Допустимое исключение — external fork (CoreCLR fork лежит в **отдельной репе**, там C/C++ allowed).

**Invariant 2** — Naming discipline. Канонические `System.*` namespaces ТОЛЬКО для fully BCL-compat реализаций. Partial / OS-specific → `SharpOS.Std.*`, `OS.Kernel.*`, `OS.Boot.*`.

---

## 2. Architecture (host/guest split)

```
┌─────────────────────────────────────────────────────────────────┐
│ SharpOS repo (this one)                  Invariant 1: C# only    │
│                                                                  │
│   kernel-tier (managed C#) + native-tier apps (managed C#)      │
│                                                                  │
│   ┌──────────────────────────────────────────────┐               │
│   │ HOST: libsharposhost.a (NativeAOT static)    │               │
│   │  [UnmanagedCallersOnly] C-ABI exports         │              │
│   │  ~30-50 stable primitives                     │              │
│   └─────────────────┬────────────────────────────┘               │
│                     │ C-ABI boundary (POD only, status codes)    │
└─────────────────────┼────────────────────────────────────────────┘
                      │
┌─────────────────────┼────────────────────────────────────────────┐
│ dotnet-runtime-sharpos fork (separate repo, gitignored locally)  │
│                     │   C/C++ allowed natively                    │
│   ┌─────────────────┴─────────────────────────────┐              │
│   │ pal/sharpos/                                   │              │
│   │   preinit/  — C++ only, no host calls          │              │
│   │   forward/  — wrappers around SharpOSHost_*    │              │
│   │   trace.cpp — measurement infrastructure       │              │
│   └────────────────────────────────────────────────┘             │
│                                                                  │
│   vm/ + gc/ + jit/  — upstream CoreCLR с минимальными patches    │
└──────────────────────────────────────────────────────────────────┘
```

**Architectural precedent**: rump kernels (NetBSD anykernel, Antti Kantee PhD 2012). MirageOS/Solo5, Genode, Microsoft Drawbridge. **Not** embedded language runtime pattern (JVM/Lua/V8).

---

## 3. Empirical data from spike (locked-in facts)

### 3.1 Numbers
- **PAL surface от pal.h proper**: 144 functions declared (87 Win32-shape + 57 PAL_-prefix).
- **Spike's link-time surface**: ~165 trap symbols (включал eventprovider + palprivate.h + rt/).
- **Eventing surface**: ~200 functions covered through upstream `pal/src/eventprovider/dummyprovider/` (no manual work).
- **C++ EH usage**: 363 EX_TRY sites в vm/ + 28 PAL_TRY (across 13 files). `-fno-exceptions` НЕРЕАЛИСТИЧНО.
- **NativeAOT runtime EH**: 0 EX_TRY occurrences. Built с `-fno-exceptions -fno-asynchronous-unwind-tables -nostdlib`.
- **libunwind**: vendored в `src/native/external/libunwind/` (3.2 MB total; x86_64 subset ~10 KLOC).
- **NativeAOT Pal surface**: ~35 functions (`nativeaot/Runtime/Pal.h`).
- **Build artifacts**: libcoreclr.so 14 MB stripped, 60 MB+ debug.

### 3.2 First hot-path PAL function
`MultiByteToWideChar`. Called из `dlls/mscoree/exports.cpp:80` `StringToUnicode()` ДО `PAL_InitializeCoreCLR`. NOT iconv-coupled — delegates через `minipal_convert_utf8_to_utf16` (`src/native/minipal/utf8.c`, 2151 LOC pure C, deps только `errno.h/limits.h/string.h/assert.h`). Implementation для pal/sharpos/ — **~20 LOC wrapper** + link minipal.

### 3.3 Unwind format reality
- JIT эмитит Windows-style `UNWIND_INFO` на Linux (DWARF CFI emit'ится только для NativeAOT ABI per `jit/compiler.h:8467 generateCFIUnwindCodes`).
- CoreCLR имеет **два unwinder'а** на Linux уже:
  - Managed JIT'd code → portable Windows-style `RtlVirtualUnwind_Unsafe` (`unwinder/amd64/unwinder.cpp`, 1847 LOC, borrowed from Windows minkernel)
  - Native libcoreclr.so internals → libunwind через `PAL_VirtualUnwind`
- SharpOS Phase 1 adds **third** unwinder (`OS/src/Boot/EH/StackFrameIteratorOps.UnwindOneFrame`) — format-compatible с managed unwinder.

### 3.4 libstdc++ subset estimate
**15-20 KLOC, 3-6 months solo dev**:
- libunwind x86_64 subset: ~10 KLOC (already vendored)
- libsupc++ EH ABI (`__cxa_*`): 3-5 KLOC (LLVM libcxxabi extractable)
- `__gxx_personality_v0`: 1-2 KLOC
- type_info runtime, `operator new/delete`, `bad_alloc`: <1 KLOC

### 3.5 Symbol conflicts CoreCLR ↔ NativeAOT runtime
**0-3 realistic** (revised от earlier 5-15 estimate):
- `__cxa_*` only from libstdc++ (NativeAOT не defines)
- NativeAOT's `operator new` — placement-new overload, different signature, no conflict
- memcpy/memset — single libc source

---

## 4. Decisions D1-D20

Format каждого D: **Question → Options → Recommendation → Rationale → Source**.

### Error handling

**D1. Error code lingua franca на C-ABI boundary**
- Options:
  - A. Узкий `SharpOSHost_Status` enum (~15 categories)
  - B. Pass-through Win32 codes (`ERROR_*`)
  - C. POSIX errno values
- **Recommendation: A**
- Rationale: B leaks Win32 shape в C# host code. C — Linux bias, not bare-metal friendly. A keeps boundary minimal; translation table в pal/sharpos/ converts к Win32 lasterror codes для CoreCLR consumption.
- Source: phase6-architecture.md §4.

**D2. Где живёт LastError**
- Options:
  - A. `thread_local unsigned int s_lastError` в pal/sharpos/preinit/errno.cpp
  - B. Через SharpOSHost (managed)
- **Recommendation: A**
- Rationale: Preinit-safe storage обязателен (called до `PAL_Initialize`, до initialized NativeAOT runtime). B не работает в preinit phase. На Linux WSL `thread_local` работает через pthread TLS; bare-metal — нужна migration к real TLS infrastructure (Phase 3 dependency).
- Source: sage round 4 synthesis Q5/Q6.
- **Caveat**: in kernel-tier SharpOS code currently NO `[ThreadStatic]` usage и no native TLS setup (FS/GS не initialized). `thread_local` в pal/sharpos/ работает на WSL только. Bare-metal migration — open work.

**D3. Policy для не-реализованных функций**
- Options (not mutually exclusive — per-function classification):
  - `LOG_AND_FAIL` (recoverable, return error, caller deals)
  - `LOG_AND_FAKE_SUCCESS` (pretend OK, observe if caller breaks)
  - `LOG_AND_FORWARD_TO_HOST` (real implementation через SharpOSHost_*)
  - `LOG_AND_ABORT_FATAL` (kill process, invalid state)
- **Recommendation: per-function classification based on trace data**
- Rationale: Sage 2 explicit recommendation. Replace blanket abort() с policy-tagged stubs. Categories driven by observed runtime usage, not guessed ahead.
- Source: sage round 4 synthesis Q1/Q2.

**D4. Catch-all wrapper на каждом SharpOSHost_* export**
- Options:
  - A. `try { ... } catch (Exception e) { return mapToStatus(e); }` mandatory
  - B. Allow exceptions cross C-ABI boundary
- **Recommendation: A**
- Rationale: B = arch broken. Managed exception unwinding через C++ frames corrupts vm/ state. Sage 2 жёсткое правило: "no managed exception escapes into CoreCLR; catch-all at boundary; return HRESULT/BOOL/errno-like".
- Source: sage round 4 synthesis Q3.

### Threading

**D5. Thread creation pathway для Linux spike**
- Options:
  - A. pthread напрямую в pal/sharpos/forward/thread.cpp
  - B. SharpOSHost_CreateThread → pthread_create в C# host
  - C. NativeAOT `PalStartBackgroundGCThread` для special threads
- **Recommendation: B**
- Rationale: B validates the architectural C-ABI line — это main goal of spike. A — fast но skips integration. C — mixing two APIs not coherent. For bare-metal, B's wrapper changes pthread → SharpOS Scheduler, code structure preserved.
- Source: phase6-architecture.md §3 + sage round 4.
- **Caveat**: SharpOS Scheduler не существует (Phase 3, 4-6 months from current). Spike's B = pthread под капотом C# wrapper.

**D6. Thread state ownership**
- Options:
  - A. vm/ владеет (Thread*, TLS slots, GC scan info, exception state)
  - B. SharpOS host владеет
- **Recommendation: A**
- Rationale: Standard CoreCLR pattern. vm/threads.cpp registers threads, owns lifecycle. Host знает только OS-level (TID + stack). Cleaner separation; vm/ не coupled с SharpOS internals.
- Source: vm/threads.cpp upstream behavior.

**D7. TLS implementation**
- Options:
  - A. Linux behavior as-is для spike (`__thread` + pthread_key_t auto-resolved)
  - B. Wrap через SharpOSHost сразу
- **Recommendation: A для spike, defer B к bare-metal Phase 6**
- Rationale: Linux gives free TLS through ELF + pthread. Wrapping в C-ABI raises complexity без spike benefit. Bare-metal SharpOS needs TLS infrastructure (FS/GS register + per-thread image) which is Phase 3+ work.
- Source: spike pragmatism + plan.md Phase 3 dependency.

**D8. GC thread suspension mechanism**
- Options:
  - A. Copy Linux signal-based (SIGUSR1 → ucontext capture в thread-local buffer → GC reads)
  - B. Abstract through SharpOSHost callback (`on_async_suspend`) сразу
- **Recommendation: A для spike, B для bare-metal**
- Rationale: Linux signal mechanism живёт в `pal/src/exception/signal.cpp` — портабельно копируется в pal/sharpos/. Bare-metal SharpOS scheduler-based suspend требует Phase 3 scheduler — defer.
- Source: phase6-architecture.md §6 (bidirectional callbacks).

### Architecture / structure

**D9. preinit/ vs forward/ split в pal/sharpos/**
- Options:
  - A. Two-directory split per Sage 2 (preinit = no host calls; forward = with host)
  - B. Single layer
- **Recommendation: A**
- Rationale: Static init order safety обязательна. NativeAOT runtime requires `PalInit()` before exports (per nativeaot/Pal.h). CoreCLR's C++ static initializers run **before** any explicit init — могут call PAL функции через TU-level constructors. preinit/ surface must not depend on initialized NativeAOT. Concrete split:
  - `preinit/` files: `trace.cpp`, `errno.cpp`, `spinlock.cpp`, `tls_min.cpp`, `utf.cpp`, `bootstrap.cpp`
  - `forward/` files: `memory.cpp`, `thread.cpp`, `file.cpp`, `sync.cpp`, `module.cpp`
- Source: sage round 4 synthesis Q5/Q6.

**D10. Linker model для WSL spike**
- Options:
  - A. libsharposhost.so + RTLD_GLOBAL, explicit dlopen sequence
  - B. libsharposhost.a вшить в libcoreclr.so напрямую
- **Recommendation: A**
- Rationale: A makes init order explicit (load host → init host → set api table → dlopen libcoreclr). B forces NativeAOT runtime initialization to fight CoreCLR's static init. Sage 2 explicit recommendation. Bare-metal final link — позже, когда linker map + duplicate-symbol audit прошли.
- Source: sage round 4 synthesis Q6.

**D11. Host API table acquisition**
- Options:
  - A. Table injection (`extern "C" int SharpOSPal_SetHostApiTable(const SharpOSHostApiTable*, uint32_t version)`) — host pushes
  - B. Static lookup (`SharpOSHost_GetApiTable()`) — pal pulls
- **Recommendation: A**
- Rationale: B requires callable export from static init context. NativeAOT-owned exports требуют initialized runtime → cannot be called from PAL preinit. A makes init order explicit: SharpOSHost_Initialize → prepare table → SharpOSPal_SetHostApiTable(table) → THEN coreclr_initialize.
- Source: sage round 4 synthesis Q6.

**D12. NativeAOT Pal* как middle layer (the disagreement)**
- Options:
  - A. Sage 1 path: NativeAOT Pal* surface (≈35 funcs in `nativeaot/Runtime/Pal.h`) служит middle layer; pal/sharpos/'s `VirtualAlloc` calls `PalVirtualAlloc` etc.
  - B. Sage 2 path: pal/sharpos/ → SharpOSHost напрямую; NativeAOT runtime hangs off SharpOSHost independently (parallel paths)
- **Recommendation: B (long-term correctness over short-term saving)**
- Rationale: Sage 2 killer arguments stand: (1) NativeAOT Pal contract ≠ CoreCLR PAL contract — adapter layer = re-implementing CoreCLR PAL anyway; (2) NativeAOT Pal requires `PalInit()` first — adds second runtime init dependency; (3) two-runtime ownership ambiguity. Sage 1's path saves только Linux spike effort. Hybrid possible: use Sage 1 path during Linux bring-up (faster), переезд на Sage 2 architecture for Phase 6 commit.
- Source: sage round 4 synthesis Q4.
- **Status: post-spike disagreement; decision committed против Sage 1 advice based on Sage 2's killer arguments.**

### Exception handling integration

**D13. EH path A/B/C**
- Options:
  - A. linux-x64 + libunwind для C++ native frames
  - B. win-x64 build без PAL, SharpOSHost = Win32-shape
  - C. linux-x64 + JIT patch для .pdata emit
- **Recommendation: A**
- Rationale: Convergent sage choice. B = эмулировать большой кусок Win32 process/thread/sync — могло быть больше Unix PAL path. C = already done upstream (JIT эмитит UNWIND_INFO on Linux per `generateCFIUnwindCodes`). A = min change to upstream, requires libstdc++/libunwind subset (independent risk per D14/D17).
- Source: sage round 4 synthesis Q3.

**D14. Cross-runtime exception rule**
- Options:
  - A. Hard prohibition: managed exceptions catch-all at SharpOSHost C-ABI; return status codes
  - B. Allow propagation across C-ABI
- **Recommendation: A**
- Rationale: Same as D4 — arch broken otherwise. SharpOSHost C-ABI rule:
  ```
  - no managed exception escapes into CoreCLR
  - catch-all at boundary
  - return HRESULT / BOOL / errno-like
  - fatal host exception → explicit failfast, not unwind across CoreCLR
  ```
- Source: sage convergent both rounds.

### Tracer

**D15. Tracer infrastructure location**
- Options:
  - A. `pal/sharpos/preinit/trace.{h,cpp}` (pure C++, BSS ring buffer, raw write(2))
  - B. SharpOSHost (managed)
- **Recommendation: A**
- Rationale: Tracer must work in preinit context (cataloging static-init PAL calls). Managed tracer requires initialized runtime — too late. No malloc / no std::string / static BSS ring buffer / raw syscall dump.
- Source: sage round 4 synthesis Q5.

**D16. Phase markers — manual vs auto**
- Options:
  - A. Manual setters в bootstrap.cpp constructor + wrappers around `PAL_Initialize` / `coreclr_initialize` / managed Main entry
  - B. Auto-detect через heuristic
- **Recommendation: A**
- Rationale: Explicit > heuristic. Phase enum: `DSO_LOAD, CTOR, BEFORE_PAL_INIT, IN_PAL_INIT, AFTER_PAL_INIT, CORECLR_INITIALIZE, JIT, MANAGED_MAIN, SHUTDOWN`. Setters at known transition points.
- Source: sage round 4 synthesis Q5.

**D17. Trace dump trigger**
- Options:
  - A. atexit handler + signal handler (SIGABRT/SIGTERM)
  - B. Periodic flush
  - C. On-demand via env var
- **Recommendation: A**
- Rationale: Covers crash AND clean exit. Periodic = unnecessary overhead, complicates synchronisation. On-demand = forgettable. atexit + signal = automatic, fits "abort dump" model.
- Source: derived from D15 requirements.

### Spec / planning

**D18. Spec scope для trace-backed spec**
- Options:
  - A. Только observed functions (~30-60 expected)
  - B. All link-time surface (~144)
- **Recommendation: A**
- Rationale: Sage 2 explicit warning против B: "полный spec по 165 функциям без runtime trace будет частично мёртвым". Observed surface даёт hot-path priority + actual semantic constraints. Cold-path = "stub returns failure" generic policy.
- Source: sage round 4 synthesis Q2.

**D19. Spec format**
- Options:
  - A. Per-function record: name, signature, semantics, policy bucket, host primitive mapping, phase
  - B. Categorized list без per-function detail
- **Recommendation: A**
- Rationale: Per-function detail makes spec actionable for implementation. Categorized-only spec leaves implementation decisions to implementer (likely re-design).
- Source: sage round 4 synthesis Q8 (trace event format suggests row format for spec).

### Local leaf implementations

**D20. Что implement локально без host (preinit-safe)**
- Confirmed list:
  - UTF-8 ↔ UTF-16 conversion через `minipal_convert_utf8_to_utf16` (для `MultiByteToWideChar`, `WideCharToMultiByte`)
  - `GetCurrentProcessId` / `GetCurrentThreadId` (`getpid()`, `gettid()` on Linux WSL)
  - `GetTickCount` / `GetTickCount64` via `CLOCK_MONOTONIC` clock_gettime
  - `GetSystemInfo` (minimal: page size from `sysconf`, processor count, x64 arch)
  - `GetLastError` / `SetLastError` (already done — `thread_local unsigned int`)
  - `_snprintf_s` / `sprintf_s` / etc. — CRT-safe wrappers (forward to standard C functions с bounds check)
  - GUID constants (already done as `extern "C" const`)
- **Recommendation: implement all 6 categories**
- Rationale: All leaf functions — no host call needed. Preinit-safe. Removes them from "trap and observe" loop, reduces noise in trace.
- Source: sage round 4 synthesis Step 3.

---

## 5. Implementation plan (6-step trace-backed bring-up)

Total estimate: **2-3 weeks** (sage 2 bound).

### Step 1 — Tracer infrastructure (1-2 days)
- `pal/sharpos/preinit/trace.h` — `PAL_TRACE(func_id, arg0, arg1)` macros
- `pal/sharpos/preinit/trace.cpp` — BSS ring buffer (no malloc), atexit + signal dump via raw write(2)
- Phase enum + `extern std::atomic<uint16_t> g_pal_phase`
- Phase setters wrap `PAL_Initialize`, `coreclr_initialize`

### Step 2 — func_id enum + policy stubs (1 day)
Replace blanket `abort()` с policy-tagged stubs:
```cpp
TRAP_POLICY(MultiByteToWideChar, FORWARD)    // implemented локально (D20)
TRAP_POLICY(SetEnvironmentVariableW, FAKE_SUCCESS)
TRAP_POLICY(CreateProcessW, FAIL)
TRAP_POLICY(DebugBreak, FATAL)
```

### Step 3 — Local leaf implementations (2-3 days)
В `pal/sharpos/preinit/`:
- `utf.cpp` — UTF-8/16 conversion through minipal
- `errno.cpp` — Get/SetLastError (already exists, переехать в новую структуру)
- `procthread_id.cpp` — GetCurrentProcessId/ThreadId через getpid/gettid
- `time.cpp` — GetTickCount/64 через CLOCK_MONOTONIC
- `sysinfo.cpp` — GetSystemInfo minimal
- `crt_safe.cpp` — _snprintf_s etc.

### Step 4 — Run + collect trace (1 day)
- `corerun Hello.dll` against fully-traced libcoreclr.so
- Dump trace, generate report:
  ```
  FIRST SEEN (in chronological order)
  001 MultiByteToWideChar       phase=BEFORE_PAL_INIT  caller=<addr>
  002 GetCurrentThreadId        phase=BEFORE_PAL_INIT  caller=<addr>
  ...
  
  HOTTEST (by total count)
  GetCurrentThreadId       N
  SetLastError             N
  ...
  
  PREINIT SURFACE (called before PAL_InitializeCoreCLR)
  - func1
  - func2
  ```

### Step 5 — Trace-backed HOST API spec (3-5 days)
Write `done/pal-host-api-spec.md`:
- Per-observed-function: signature, semantics, policy bucket, host primitive mapping
- HOST primitives derivation (group forwarded funcs into ~30-50 underlying)
- Preinit-safe vs post-init separately marked

### Step 6 — GO/NO-GO Phase 6 decision (1 day)
С trace-backed spec в руках — committed to Phase 6 OR pivot if data shows red flags.

---

## 6. SharpOS build configuration (TARGET = bare-metal)

Linux WSL spike — это **proxy** target. Final target = SharpOS bare-metal unikernel UEFI/x64.

### 6.1 Differences Linux WSL vs SharpOS bare-metal

| Aspect | Linux WSL spike | SharpOS bare-metal (Phase 6 target) |
|---|---|---|
| libc | glibc available | None. Need musl subset OR replace C functions with kernel equivalents |
| libstdc++ | System -lstdc++ | Port subset (15-20 KLOC: libunwind x86_64 + libsupc++ EH ABI + minimal type_info + new/delete) |
| pthread TLS | Free via pthread + ELF TLS | Need FS/GS register init + per-thread TLS area (Phase 3 dependency) |
| Threading | pthread | SharpOS Scheduler (Phase 3 dependency) |
| Memory paging | mmap/mprotect/munmap | SharpOS Pager (KernelHeap, exists) |
| Signal delivery | POSIX signals | Phase 1 EH integration via HwFaultBridge |
| File I/O | open/read/write на host filesystem | TBD — depends on Phase 5 storage drivers |
| Module loading | dlopen/dlsym | Static link all CoreCLR objects into kernel image |
| GC stack suspend | SIGUSR1 + ucontext capture | Scheduler-based suspend (Phase 3 dependency) |

### 6.2 What spike validates regardless of target

- C-ABI line works (NativeAOT C# → .a → linked from C++)
- CMake substitution mechanism для pal/src/ → pal/sharpos/
- Win32-shape PAL surface scope (~144 funcs)
- libcoreclr.so link feasibility against trap stubs
- Static init order risk identified (NativeAOT runtime must init before exports)

### 6.3 What spike CANNOT validate (deferred к Phase 6)

- Real PAL hot path through coreclr_initialize → JIT → managed Main
- SharpOSHost.a actually consumed by pal/sharpos/ (sage round 4 Q6 still open)
- Cross-runtime symbol coexistence (0-3 conflicts predicted)
- Bare-metal libstdc++ subset realistic size (15-20 KLOC estimate)
- libunwind port feasibility (vendored but не reused yet)

---

## 7. Deferred decisions (explicit TBD list)

Эти decisions нельзя сделать без data which spike trace will produce:

1. **HOST primitive count** — phase6-arch §3 predicted 30-50. Actual derivation from observed surface (Step 5).
2. **Function table layout** — version + size + slots layout. Specced как pattern (phase6-arch §10), не concrete numbers.
3. **PAL_Callbacks shape** — `on_fault`, `on_async_suspend`, `on_thread_start/exit`, `on_low_memory` listed но не finalized.
4. **CreateProcess policy** — kill process или FAIL gracefully? Depends on which CoreCLR features нужны (managed `Process.Start` = fail; debugger transport = fatal).
5. **Cold-path fakeification policy** — какие функции safe to fake-succeed (`SetEnvironmentVariableW`?), какие должны fail loud (`CreateProcessW`)?
6. **musl libc subset** — what's needed после spike removes glibc dependency. Inventory blocked on trace data.
7. **Path for `_CONTEXT::operator=`** — simple memcpy (spike) vs variable-size XSTATE-aware copy (production)? Likely production needs real impl when AVX-512 / APX state hits hot path.
8. **EH spike scope** — sage 2 listed 7 EH scenarios для post-trace validation. Not started.

---

## 8. Anti-list (explicit NOT-doing)

- ❌ Trap-by-trap implementation loop (current mode, abandon)
- ❌ Cabinet spec на all 144 functions без trace data
- ❌ NativeAOT PAL as full replacement of CoreCLR PAL (Sage 2 killer arguments stand)
- ❌ Win-x64 build (Path B rejected)
- ❌ JIT patching (Path C already done upstream)
- ❌ libstdc++-free CoreCLR (363 EX_TRY sites)
- ❌ Crossing exceptions across SharpOSHost C-ABI boundary
- ❌ Calling SharpOSHost from CoreCLR static init context (NativeAOT not initialized)
- ❌ `[ThreadStatic]` в kernel-tier SharpOS code (no native TLS infrastructure; defer to Phase 3)
- ❌ `cp pal/src/ + sed substitute` як основная implementation strategy — это "PAL port with SharpOS backend", не mechanical substitution

---

## 9. Open questions (post-baseline)

1. **Q4 disagreement resolution** — D12 committed against Sage 1 advice. If empirical Linux spike shows NativeAOT Pal middle-layer dramatically saves effort, revisit decision.
2. **Static init context PAL calls** — Step 4 trace покажет actual scope of preinit-safe shim. Currently specced as "GetLastError + spinlock + TLS minimum + UTF" — may be incomplete.
3. **JIT-only PAL surface** — JIT may invoke functions which не appear before managed Main. Trace во время JIT compile (force `COMPlus_JitDisasm`) покажет.
4. **Two NativeAOT runtime instances в process** — if SharpOSHost.a ever linked into libcoreclr.so (production path), two NativeAOT runtimes coexist. Spike Branch A не tested this configuration.
5. **Phase 3 threading completion** — D5/D7/D8 all depend on Phase 3 (Scheduler + Threading). plan.md ordering says Phase 3 → Phase 6. Reality: spike is running Phase 2 work without Phase 3 done. Need explicit gate decision: do we commit Phase 6 implementation before Phase 3 finishes?
6. **libsharposhost.a build pipeline в SharpOS repo** — Branch A produced static archive в `OS/src/PAL/SharpOSHost/`. CoreCLR fork need consumed это. Build integration не designed yet.

---

## 10. Cross-check protocol (для агента)

Compare альтернативного design plan к этому baseline и report:

### Categories of divergence

1. **Decision divergence** — alternative plan picks different option для D1-D20.
   - Report which D, what was chosen, vs baseline's recommendation.
   - Cite alternative's rationale if available.

2. **Factual divergence** — alternative claims different numbers / behaviors.
   - PAL surface count (alternative says X, baseline says 144 pal.h + 200 eventing).
   - Hot-path function (alternative says X, baseline says MultiByteToWideChar).
   - libstdc++ subset size (alternative says X, baseline says 15-20 KLOC).

3. **Scope divergence** — alternative includes / excludes items not in baseline.
   - Missing D's not addressed.
   - Extra D's (новые decision points not previously identified).

4. **Architectural divergence** — alternative violates Invariants 1/2 OR sage convergent rules.
   - Examples: alternative puts C++ code в SharpOS repo (Invariant 1 violation), alternative allows exceptions across C-ABI (D14 violation), alternative calls SharpOSHost from static init (D11 violation).

5. **Phase divergence** — alternative scope crosses Phase 2 → Phase 6 boundary undocumented.

### Output format request

For each divergence: `<DXX>` + `<baseline says>` + `<alternative says>` + `<severity: critical | medium | nit>`.

Critical = violates Invariants or sage convergent rules.  
Medium = differs from baseline recommendation but defensible.  
Nit = stylistic, naming, document organization.

End report with single recommendation: **align with baseline / align with alternative / synthesize / no decision yet** для each major D.
