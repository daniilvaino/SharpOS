# Phase 6.1 — 3-way build comparison & port surface overview

**Audience:** Sage 2 + кто будет принимать decisions on kernel-port surface scope.
**Status:** Compile/link milestone done across 3 builds. Cross-build audit collected. Ready for direction on next implementation pass.

**Sources of truth:**
- `c:/work/OS/plan.md` — Phase 6 architecture, 6.1/6.2 split, criticality.
- `c:/work/OS/done/step067.md` — full cascade writeup (OPEN status).
- `c:/work/OS/work/PAL/D1-D20 FINALIZED/` — D5 threading off / D9 forward / D11 firewall / D13 EH.
- `c:/work/OS/work/PAL/symbol-audit/` — raw audit data referenced throughout.
- Fork: `dotnet-runtime-sharpos`, branch `sharpos/coreclr-port`, commit `2919ba34cbe`.

---

## Что собрано

Три build'а на одном Windows host, одной WSL2 distro, одной кодовой базе (release/10.0 от dotnet/runtime):

| Build | Location | TARGET | HOST | Configuration |
|---|---|---|---|---|
| **SharpOS Phase 6.1** | `dotnet-runtime-sharpos/` (fork) | UNIX + SHARPOS | WINDOWS | minimal profile: SVR_GC + STANDALONE_GC + SOFTWARE_WRITE_WATCH off |
| **Vanilla Win** | `dotnet-runtime-vanilla-win/` | WINDOWS | WINDOWS | full vanilla defaults |
| **Vanilla WSL** | `dotnet-runtime-vanilla-wsl/` | UNIX | UNIX | full vanilla defaults (Linux PAL active) |

Компиляторы:
- SharpOS Phase 6.1: **clang-cl** (MSVC ABI + GNU predefines) + lld-link
- Vanilla Win: **MSVC** (cl.exe) + link.exe
- Vanilla WSL: **clang** + ld

Артефакты для audit (одинаковая семантика):
- `coreclr.dll` / `libcoreclr.so` — shared object с runtime imports
- `coreclr_static.lib` / `libcoreclr_static.a` — static archive (kernel image input в нашем случае)

---

## Audit numbers

### Dynamic-load surface (DLL / SO imports)

| Метрика | SharpOS 6.1 | Vanilla Win | Vanilla WSL |
|---|---|---|---|
| Loaded libs | 4 | 7 | 5 (libc, libdl, libstdc++, libgcc, libpthread) |
| Total symbols | 212 | 290 | 323 (dynamic undefined) |
| KERNEL32 / libc-equivalent | 196 | 218 | (mostly libc + libpthread) |
| ADVAPI32 | 12 | 20 | — |
| ole32 / OLEAUT32 | 3 | 46 (включая winrt) | — |

### Static archive truly-external (после subtraction of `defined` from `undefined`)

| Build | Total | `__*` (builtins/imports) | C++ mangled `?*` | extern C |
|---|---|---|---|---|
| **SharpOS 6.1** | 585 | 145 | 89 | 351 |
| **Vanilla Win** | 818 | 238 | 332 | 248 |
| **Vanilla WSL** | 627 | (не categorized — другая ABI) | — | 627 |

### 3-way set decomposition (truly-external)

| Set | Count | Что в нём |
|---|---|---|
| **all 3 (∩)** | **128** | Core CoreCLR external — CRT (memset/memcpy/abort/qsort), math (acos/sin/log/...), COM IIDs (GUID_NULL, IID_IUnknown), GlobalizationResolveDllImport, WaitHandle_*, Compression_*, dn_simdhash_assert_fail |
| **SharpOS only** | 10 | Win-side stubs из нашего winapi_shim.cpp + 1 thunk: `?CLRLoadLibraryEx`, `?ClrGetProcessExecutableHeap`, `?GetTlsIndexObjectAddress`, `?InsertThreadIntoAsyncSafeMap`, `?RemoveThreadFromAsyncSafeMap`, `FireEtXplatCodeSymbols`, `WaitHandle_WaitOnePrioritized`, `__CxxFrameHandler3`, `__atomic_compare_exchange_16`, `ldiv` |
| **SharpOS ∩ Win not WSL** | 264 | MSVC-mangled C++ surface (`?GCToOSInterface::*`, `?VMToOSInterface::*`, `?GCEvent::*`, `?TGcInfoEncoder<...>`, `??2`/`??3` new/delete). На Linux эти ровно те же классы но с Itanium ABI mangling — поэтому не появляются в WSL set. |
| **SharpOS ∩ WSL not Win** | 183 | Dummyprovider ETW stubs (`FireEtXplat*`, `EventXplatEnabled*`). На Vanilla Win они auto-resolved внутри (lttng/etw provider compiled in). |
| **Win only** | 421 | Pure Windows: COM interop (OLEAUT32 / winrt), `??_R0?AV...` RTTI, MSVC EH personality + extras |
| **Win ∩ WSL not SharpOS** | 5 | `_wtoi`, `fma`, `fmaf`, `round`, `roundf` — наша SharpOS gets их internally через что-то |
| **WSL only** | 311 | Linux PAL surface: 46 `PAL_*` direct + 11 `pthread_*` + 96 `_*` (libc) + 28 `__*` (compiler-RT) + many Win32-shape names (`CreateThread`, `CloseHandle`, `FormatMessageW`, etc.) — **provided by libcoreclrpal.a на Linux** |

Math check: SharpOS = 128 + 10 + 264 + 183 = 585 ✓; Win = 128 + 421 + 264 + 5 = 818 ✓; WSL = 128 + 311 + 183 + 5 = 627 ✓

Raw data: `c:/work/OS/work/PAL/symbol-audit/three_way/{intersection_all3,sharpos_only,win_only,wsl_only,sharpos_win_not_wsl,sharpos_wsl_not_win,win_wsl_not_sharpos}.txt`

---

## Key insights

### 1. PAL pattern empirically confirmed

WSL static archive ссылается на **Win32-shaped names** (`CreateThread`, `CloseHandle`, `CreateEventW`, `ExitProcess`, `FormatMessageW`, ~120+ названий) — это **не Win32 API на Linux**, а PAL functions provided by separate `libcoreclrpal.a` archive.

Vanilla CoreCLR pattern на Linux: `vm/` calls Win32-shape API → links против libcoreclrpal.a → PAL routes на pthread / mmap / signals.

Vanilla CoreCLR pattern на Windows: `vm/` calls Win32 API → resolved directly через `__declspec(dllimport)` → kernel32.dll/etc.

**SharpOS Phase 6.1 hybrid**: TARGET_UNIX preprocessor (vm/ uses PAL-shape calls), HOST_WINDOWS substrate (PAL functions implemented как thin wrappers вокруг native Win32 через `pal/sharpos/winapi_shim.cpp` + linking native kernel32/advapi32 import libs).

### 2. minipal — confirmed cross-platform layer (НЕ Linux-only)

| Build | minipal_* count |
|---|---|
| SharpOS 6.1 (static external) | 27 |
| Vanilla WSL (static external) | 28 |
| Vanilla Win (static external) | (similar, both shape) |

minipal — **cross-platform shim layer** (utf16↔utf8 convert, hires/lowres ticks, guid, log, mutex, xoshiro PRNG). Идёт **независимо от TARGET / HOST**. На kernel side нужно provide implementation either via SharpOSHost_* shim или через C# `[UnmanagedCallersOnly]` directly.

### 3. Dummyprovider ETW correctly stubs out 183 FireEt/EventXplat refs

Vanilla Win has все 183 FireEt/EventXplat symbols **defined inside the static archive** (ETW provider compiled in). SharpOS + WSL have them **as external** — resolved at final-link via dummyprovider obj.

Sage's concern (re: dummyprovider): **confirmed working as designed**. На kernel link мы линкуем cee_wks_core + dummyprovider — все 180 ETW stubs становятся no-op.

### 4. Real kernel-port surface для SharpOS — НЕ 418 как я писал в первом sage query

Уточнённая декомпозиция:

| Layer | Count | What |
|---|---|---|
| **Auto-resolved at link** (dummyprovider stubs) | ~180 | FireEt/EventXplat — no kernel-side work |
| **128 cross-platform CRT** | core CoreCLR (math, mem, string, COM IIDs, etc.) — SharpOS std нужно provide или minimal CRT |
| **27-28 minipal_** | cross-platform shim — implement via SharpOSHost_* |
| **89 C++ mangled** в SharpOS | mostly GCToOSInterface / VMToOSInterface / GCEvent classes — already abstracted, route via `gc/sharpos/gcenv.sharpos.cpp` (Phase 6.1: routes на gc/windows/; Phase 6.2: SharpOS mm) |
| **127 `__imp_*` Win32 imports** | На bare metal — replace через `SharpOSHost_*` (CreateThread → SharpOSHost_CreateThread → ABORT_FATAL per D5) |
| **18 compiler-RT** (`__C_specific_handler`, `__CxxFrameHandler3`, `__chkstk`, `__security_cookie`, `__guard_dispatch_icall_fptr`, `__atomic_compare_exchange_16`) | EH personality + CFG + stack probe + 16-byte CAS — Phase 1 unwinder satisfies некоторые? Open question. |

**Honest "must implement" estimate для SharpOS kernel-port**: ~50-80 functions реальной работы (Win32-shape PAL via SharpOSHost), + minimal CRT (~40 functions через SharpOS `std/no-runtime` thunks или minimal C lib).

### 5. Phase 6.1 minimal feature profile delivers minimal surface reduction

| Profile | static_truly_external |
|---|---|
| SharpOS all features ON | 598 |
| SharpOS 3 flags off (SVR_GC, STANDALONE_GC, SOFTWARE_WRITE_WATCH) | 585 |
| Δ | **-13** |

13 symbols ушло (11 NUMA/multi-thread GC API + 2 math). **Phase 6.1 profile not a meaningful surface reducer**. Sage's recommendation "leave features ON until evidence" — empirically confirmed correct.

---

## Open questions для sage

### Q1: CRT strategy (146 functions cross-platform required)

Across all 3 builds, 128 + extra ~50 CRT functions truly external. Reality на SharpOS bare metal:

- **(a) Minimal CRT inject** — `msvcrt-stub.lib` или similar, compile собственный C subset.
  - Pro: standard practice, isolation good.
  - Con: requires .c files (Invariant 1 conflict, unless treat as third-party submodule).

- **(b) Route via SharpOS `std/no-runtime/shared/`** managed thunks.
  - `memset/memcpy` уже existуют в SharpOS std (managed implementation).
  - `math.h` (sin/cos/log/...) — частично есть в SharpOS std, частично — нет.
  - `fopen/fread/...` (file I/O) — Phase 6.1 не нужно, ABORT_FATAL.
  - Pro: Invariant 1 clean, single std layer.
  - Con: ~50 thunks через `[UnmanagedCallersOnly]` wrappers — additional code.

- **(c) Hybrid**: math via `[UnmanagedCallersOnly]` C# wrappers (BCL `MathF.Sin` etc.); mem/str via existing SharpOS std; file ops ABORT_FATAL.

**Sage preference?**

### Q2: Phase 1 .pdata unwinder & MSVC EH personality compatibility

Compiler-RT bucket (18 symbols) includes `__C_specific_handler` + `__CxxFrameHandler3` (MSVC SEH personality routines). Per D13: SharpOS Phase 1 has its own .pdata unwinder.

Question: **does Phase 1 unwinder satisfy these symbols by exposing same ABI**, or are they different contracts (MSVC SEH vs SharpOS managed EH)?

If compatible → 2 of 18 trivially satisfied by aliasing.
If not — need separate MSVC SEH personality stub layer (small, but extra work).

### Q3: Phase 6.1 honest scope re-check based on data

Plan.md Phase 6.1 says "demo-grade Hello World + basic JIT".

Empirical reality: ~50-80 functions to implement minimally for kernel-link to close. EVEN если эти все ABORT_FATAL stub'ы (per D5/D6 threading off), JIT'у нужны живые: memory ops (~10), atomics (~5), Phase 1 EH unwinder integration (~5).

Honest minimal target proposal:
- **Phase 6.1.a**: `coreclr_initialize()` returns S_OK (живой PAL init path). ~20-30 implemented functions, остальное ABORT_FATAL.
- **Phase 6.1.b**: `coreclr_execute_assembly` на trivial method (no Thread API, no `new` post-init, no exceptions). ~50-60 implemented.
- **Phase 6.1.c**: try/finally без throw (EH unwinder runs). ~70-80 implemented.

**Sage agree с этим decomposition?** Or scope tighter / wider?

### Q4: На какой level smoke-test'а exit Phase 6.1 → Phase 6.2 transition

Phase 6.2 = threading on + concurrent GC + full Roslyn. Это масштабный jump. Между 6.1 и 6.2 нужен intermediate?

Например:
- **6.1**: bare-metal kernel, JIT работает, single-thread, no GC alloc (live data fits in initial heap)
- **6.1.5**: bare-metal kernel, JIT работает, single-thread, **ZeroGC mode** (allocations leak — accept)
- **6.2**: full threading + concurrent GC

Or 6.1 = 6.1.5 (ZeroGC allowed)?

### Q5: Что делать с win-only 421 symbols

Vanilla Win has 421 truly-external symbols that SharpOS does NOT have. Mostly COM interop (FEATURE_COMINTEROP off на TARGET_UNIX). Sane?

Spot check: COM interop OFF — нет `?CCWHolder::*`, `?ComCallWrapperTemplate::*`, OLEAUT32 imports (29 symbols) — correct for SharpOS (no COM in kernel).

Но: FEATURE_IJW (C++/CLI native interop) off — correct.
FEATURE_TYPEEQUIVALENCE off — correct (debug-time feature mostly).
FEATURE_DBGIPC off — correct (named-pipe debugger IPC).

**Validate that these "off на TARGET_UNIX" features тоже correct для SharpOS** — there shouldn't be surprises.

---

## What we need

Per sage's prior process correction — direction после controlled audit, не speculative gates:

1. **Validate Q1 strategy** (CRT via SharpOS std vs minimal CRT inject vs hybrid).
2. **Resolve Q2** (Phase 1 unwinder vs `__C_specific_handler` compat).
3. **Confirm Q3** scope decomposition (6.1.a/b/c) или предложи alternative.
4. **Decide Q4** (intermediate 6.1.5 или 6.1 единое).
5. **Sanity-check Q5** (no surprises в Win-only features off на TARGET_UNIX).

If different framing entirely — propose с cost estimate.

---

---

# ADDENDUM — Post-sage investigation: full-bundle audit

**Status:** done after sage 1 + sage 2 responses. Resolves L2 "lost C++ template instances" question conclusively.

## What we discovered

Sage 2 was right в more refined sense than initially framed. The 264 "missing C++ template instances" weren't lost from CMake — они **defined in OTHER static libs** of our build. Original audit looked **only at `coreclr_static.lib` в изоляции**, missing the multi-lib bundle perspective.

When DLL link assembles `coreclr.dll`, it pulls **multiple static libs together**:
- `coreclr_static.lib` (main runtime cee_wks_core, vm/, debug/, md/, dlls/)
- `gc_pal.lib` (our gc/sharpos/ — GCEvent, GCToOSInterface)
- `gcinfo_win_x64.lib` (TGcInfoEncoder)
- `utilcodestaticnohost.lib` (VMToOSInterface — though some moved to coreclr_static)
- `minipal.lib` + `coreclrminipal.lib` (minipal_* implementations)
- `coreclrpal.lib` (our pal/sharpos/)
- `clrjit.lib`
- `eventprovider.lib` (dummyprovider — FireEt/EventXplat resolution)
- `coreclrtraceptprovider.lib`

Все эти libs **уже сборены** нашим Phase 6.1 минимальным profile build'ом.

## Full-bundle truly-external = 341 (vs 585 в isolation)

| Source | Truly external | Δ from isolation |
|---|---|---|
| `coreclr_static.lib` alone | 585 | baseline |
| Full 10-lib bundle | **341** | -244 (-41%) |

Breakdown of 341:

| Layer (sage 2's terms) | Count | Examples |
|---|---|---|
| **L3 — `__imp_*` Win32 syscalls** (true kernel surface) | **154** | CreateThread, CloseHandle, VirtualAlloc, RtlVirtualUnwind, FlushInstructionCache, etc. (all kernel32/advapi32/ole32) |
| **L1 — CRT extern C** (math, mem, str, file, stdio, _Init_thread_*) | 157 | sin/cos/log/sqrt (28 math), memcpy via builtin, fopen/fread, _atoi64, _Init_thread_epoch, abort, _CrtDbgReportW |
| **L1 — C++ runtime primitives** | 13 | operator new/delete (`??2`/`??3`), `type_info` vtable, `std::nothrow_t`, `std::uncaught_exception` |
| **L1 — stdio CRT internals** | 7 | `__acrt_iob_func`, `__stdio_common_*` family |
| **L5 — EH personality + security** | 9 | `__C_specific_handler`, `__CxxFrameHandler3`, `__security_cookie`, `__security_check_cookie`, `__chkstk`, `__guard_dispatch_icall_fptr`, `__tls_guard`, `__tlregdtor`, `__dyn_tls_on_demand_init` |
| **L1 — misc** | 1 | `__ImageBase` (PE base relative, linker-injected) |

Notable: **0 FireEt/EventXplat** (dummyprovider lib bundled — all 180 ETW stubs resolved). **0 minipal_*** (minipal.lib bundled — все 27 resolved). **0 `GCToOSInterface::*` / `GCEvent::*`** (gc_pal.lib bundled — все 30+ resolved). 13 `std::*` and operator new/delete remaining — C++ runtime nucleus.

## Refined kernel-port estimate

| Sub-surface | Count | Strategy per sage 2's 5 layers |
|---|---|---|
| **Pure L3** (Win32 syscalls) | 154 | `SharpOSHost_*` shim layer on bare metal. For Phase 6.1: most ABORT_FATAL per D5/D6; ~30-40 real (memory, atomics, time, EH unwinder hooks) |
| **L1 mechanical CRT** (math + string/mem + C++ primitives) | ~100 | Steal libm subset + compiler builtins. Lives в CoreCLR fork as C/C++ (Invariant 1 OK for submodule) |
| **L1 stdio/runtime CRT** (printf family, _Init_thread_*) | ~20 | Some via ucrtbase (Windows host phase); kernel-tier — minimal stdio stubs |
| **L4 file/process** (fopen, _dup, _fileno, _wfopen) | ~15 | ABORT_FATAL — Phase 6.1 no file I/O |
| **L5 EH + security** | 9 | Real implementations on top of Phase 1 unwinder (D13). Some stubs (__security_cookie = static value) |

**Honest must-implement для Phase 6.1.a-c**: ~50-60 functions реальной работы. Остальное либо bundled libs (resolved), либо trivial stubs (security cookie), либо ABORT_FATAL (cold paths), либо stolen libm.

Это **в 10 раз меньше** чем "418 disaster" из первой формулировки first sage query.

## Confirmation of sage 2's reframe

5-layer mental model **empirically validated**:
- **L1 lives в CoreCLR fork support lib** — yes, our minipal.lib + coreclrminipal.lib + (future libm subset) cover это
- **L2 "fix archive composition"** — already correct in our build! Just needed to audit с правильным lib set
- **L3 = only SharpOSHost surface** — yes, 154 Win32 imports = true kernel ABI
- **L4 fatal stubs** — file I/O part of L1 actually overlaps; clear stubs candidates
- **L5 EH separate** — yes, 9 distinct symbols, clearly own work stream

## Raw data location

```
work/PAL/symbol-audit/phase6_1_min/full_bundle/
├── truly_external.txt            341 syms (after multi-lib bundle)
├── cat_builtins.txt              171 (154 __imp_* + 17 compiler-RT)
├── cat_cxx.txt                    13 (C++ runtime primitives)
├── cat_extc.txt                  157 (CRT + misc)
└── L3_win32_imports.txt          154 (Win32 syscall names, no __imp_ prefix)
```

---

## Appendix: data inventory

```
work/PAL/symbol-audit/
├── phase6_1_min/                — SharpOS Phase 6.1 build audit
│   ├── coreclr_dll_all_imports.txt
│   ├── coreclr_dll_kernel32.txt          196 syms
│   ├── static_truly_external.txt         585 syms
│   ├── cat_builtins.txt                  145
│   ├── cat_cxx.txt                        89
│   └── cat_extc.txt                      351
├── vanilla_win/                 — Vanilla MSVC build audit
│   ├── coreclr_dll_all_imports.txt
│   ├── coreclr_dll_kernel32.txt          218 syms
│   ├── static_truly_external.txt         818 syms
│   ├── cat_builtins.txt                  238
│   ├── cat_cxx.txt                       332
│   └── cat_extc.txt                      248
├── vanilla_wsl/                 — Vanilla Linux PAL build audit
│   ├── libcoreclr_so_ldd.txt              6 lines
│   ├── libcoreclr_so_needed.txt           5 NEEDED libs
│   ├── libcoreclr_so_undef_dyn.txt       323 dynamic externals
│   └── static_truly_external.txt         627 syms (28 minipal_, 46 PAL_, 11 pthread_)
└── three_way/                   — Cross-build set decomposition
    ├── intersection_all3.txt             128 syms (core CRT/runtime)
    ├── sharpos_only.txt                   10 syms (our patches)
    ├── win_only.txt                      421 syms (Win COM + extras)
    ├── wsl_only.txt                      311 syms (Linux PAL surface)
    ├── sharpos_win_not_wsl.txt           264 syms (MSVC-mangled C++)
    ├── sharpos_wsl_not_win.txt           183 syms (dummyprovider ETW)
    └── win_wsl_not_sharpos.txt             5 syms (math: fma, round)
```
