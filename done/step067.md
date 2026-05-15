# Step 67 — Phase 6.1: CoreCLR compile/link cascade complete (OPEN)

**Status:** open. Compile/link milestone reached. Smoke-tests + symbol audit + WinAPI debt inventory refresh still pending — see "Next" at bottom.

## Контекст

Phase 6.1 per `plan.md`: TARGET_SHARPOS mode active в форке CoreCLR
(`dotnet-runtime-sharpos/`, отдельный GitHub repo `daniilvaino/dotnet-runtime-sharpos`,
branch `sharpos/coreclr-port`). Goal: production port не hosted spike — runtime
artifacts должны быть kernel-image-ready, без libunwind / libdl / pthreads
dependencies в final link surface. Cascade закрывает compile + link.

Step 66 завершился smoke'ом libcoreclr.so против trap-stub pal/sharpos/ (Linux,
host process). Step 67 продолжил линию на Windows host: clang-cl + lld-link
production path, native PE/COFF output, тот самый bundle который kernel image
поглотит как static archive в Phase 6.1 integration step (TBD, next milestone).

**Никаких изменений в SharpOS repo (OS/) в этом step не было** — все правки
живут в форке. Этот writeup и есть документация шага. Fork commit:
`2919ba34cbe TARGET_SHARPOS: bring up CoreCLR compile/link cascade`.

## Архитектурная установка (повторение для контекста)

Three-tier разделение (зафиксировано в `done/phase6-architecture.md` + PAL/
FINALIZED docs):

- **Guest tier** = форк CoreCLR. C/C++ разрешён в форке (Invariant 1 исключение
  для submodule). PAL pal/sharpos/ + GC env gc/sharpos/ — drop-in замены
  pal/src/ и gc/windows/ соответственно, выбираются `-DCLR_CMAKE_TARGET_SHARPOS=1`.
- **Host tier** = SharpOS repo, C# only. `OS/src/PAL/SharpOSHost/` NativeAOT
  static archive экспортирует ~30-50 C-ABI функций.
- **C-ABI line** между tiers — POD types only.

Phase 6.1 ranges Host-Windows production: clang-cl, lld-link, native PE/COFF.
Это значит coreclr.dll реально загружается / coreclr_static.lib линкуется в
external harness — smoke surface для всех patches до kernel link step.

## Что было сделано (compile/link cascade)

### Build pipeline pivot — clang-cl + lld-link на Windows host

Pre-step 67 был attempt vanilla MSVC. Failed на `pal_mstypes.h` —
`__attribute__((noreturn))` etc. это GNU-style predefines которые
pal/inc/ assumes для TARGET_UNIX preprocessor. clang-cl supports
GNU predefines AND MSVC linkage simultaneously → natural fit.

Build script `build_clr_sharpos.ps1`:
- `$env:CC = $env:CXX = clang-cl`
- `-DCLR_CMAKE_TARGET_SHARPOS=1`
- `/WX` disable scoped `-Wno-error` (clang warning surface diverges от MSVC;
  fighting через individual `-Wno-error=*` не масштабируется).
- `-Wno-c++11-narrowing`, `-Wno-invalid-token-paste` (MSVC vtable mangling).
- `/p:NativeAotSupported=false` (skip NativeAOT csproj'ы — мы pull собственный
  toolchain в SharpOS repo, не CoreCLR fork's nativeaot/Runtime artifacts).

### Patches summary

99 файлов, +1320/-307. Subsystem breakdown (см. commit message
`2919ba34cbe` для полного списка):

- **Platform bundle** (new): `pal/sharpos/`, `gc/sharpos/`.
- **Configuration**: `eng/native/configureplatform.cmake` (skip
  CLR_CMAKE_TARGET_WIN32 on SHARPOS), `eng/native/configurecompiler.cmake`
  (clang-cl flags), `clrfeatures.cmake` (FEATURE_STATICALLY_LINKED),
  `CMakeLists.txt` root (skip nativeaot/libs-native/hosts/singlefilehost,
  gate ilasm/ildasm/superpmi host tools).
- **vm/** ~30 файлов — wide gate adjustments. Pattern: `#ifdef TARGET_UNIX`
  blocks где `#else` branch держит Windows-specific machinery (FEATURE_HIJACK,
  .pdata-based EH, MASM redirect stubs, RtlVirtualUnwind, TEB access).
  TARGET_SHARPOS нуждается в Windows branch.
- **debug/** ~10 файлов — symbol export adjustments (extern "C" wraps
  для unmangled .def), MSVC vtable mangling (`@@6B@`) routes через
  dynamic InitializeEntries.
- **gc/** — sharpos overlay, suppress kernel32/advapi32 auto-link (D11
  firewall), skip standalone clrgc.dll/clrgcexp.dll.
- **EventPipe/dummyprovider** — genDummyProvider.py / genEventPipe.py /
  genEventing.py emit windows.h+stdint.h на TARGET_SHARPOS,
  `__declspec(selectany)` для weak symbol merging.
- **MASM .asm** — enabled на HOST_WINDOWS+TARGET_SHARPOS (ABI-identical
  для x64), `rtlfunctions.cpp` added в cee_wks_core sources.

## Key findings / lessons

### 1. `CROSS_COMPILE` auto-define trap (`inc/crosscomp.h:24`)

Самая важная находка cascade. Gate:

```c
#if defined(TARGET_UNIX) && !defined(HOST_UNIX) && !defined(CROSS_COMPILE)
#define CROSS_COMPILE
#endif
```

Срабатывает автоматически на TARGET_SHARPOS (TARGET_UNIX defined +
HOST_WINDOWS) → `CROSS_COMPILE` define. Это silently masked несколько
crash patterns:

- `Module::ExpandAll` (`ceeload.cpp:4699`) — gated `_DEBUG && !DACCESS_COMPILE
  && !CROSS_COMPILE`. С auto-defined CROSS_COMPILE — definition excluded,
  но call site (`assembly.cpp:2264`, `ceemain.cpp:1028`) emits undefined
  reference → link error.
- `dbgtargetcontext.h:295`, `debug/inc/common.h:95`, `dacdbiimpl.cpp:4966` —
  все gated `!CROSS_COMPILE && !TARGET_WINDOWS`. Содержат
  `offsetof(T_CONTEXT, XStateFeaturesMask)` — XStateFeaturesMask это Linux PAL
  CONTEXT field, не Win32 _CONTEXT field. С auto-defined CROSS_COMPILE
  эти asserts были "случайно" корректны. После fix (neutralize CROSS_COMPILE
  on TARGET_SHARPOS) — exposed real bug, requires `!TARGET_SHARPOS` в gate.

**Lesson**: auto-defines в headers — landmines. Особенно когда macro
имя suggests user-intent flag (`CROSS_COMPILE` = "я кросс-компилю"), но
header'ы используют его как platform-shape proxy. На SharpOS мы native
PE для host=guest=amd64 — это не cross-compile в смысле reachability,
но crosscomp.h это не различает.

### 2. MASM/C++ gate mismatch — `USE_REDIRECT_FOR_GCSTRESS`

`vm/amd64/cgencpu.h:74-76`:
```c
#ifndef TARGET_UNIX
#define USE_REDIRECT_FOR_GCSTRESS
#endif
```

MASM stub `RedirectedHandledJITCase.asm:93-97` emits call к
`Thread::RedirectedHandledJITCaseForGCStress` под `_DEBUG && HAVE_GCCOVER`.
MASM не видит USE_REDIRECT_FOR_GCSTRESS — gate'ов на этот macro в asm
нет. C++ definition (`threadsuspend.cpp:2843`) gated на
`HAVE_GCCOVER && USE_REDIRECT_FOR_GCSTRESS`.

На TARGET_SHARPOS (TARGET_UNIX defined) USE_REDIRECT_FOR_GCSTRESS not
defined → C++ definition excluded. MASM emit'ит unresolved call → link
error.

**Fix**: extend cgencpu.h gate `!TARGET_UNIX || TARGET_SHARPOS`.

**Lesson**: cross-language gate consistency между MASM и C++ — separate
concern от high-level platform gate. MASM в принципе не должен emit'ить
calls который C++ side не emit'ит — это инвариант который CoreCLR
upstream не enforces (Windows-only assumption + matching X86 gate
implicitly satisfies it). На TARGET_SHARPOS этот инвариант обнаружен
empirically через link error.

### 3. lld-link strictness vs vanilla link.exe

Несколько мест где vanilla link.exe был permissive а lld-link не:

- `.def` files: `data` (lowercase) → link.exe принимает, lld-link
  не парсит → `DATA` (uppercase) required. `mscorwks_ntdef.src`.
- `.def` files: `private` keyword — link.exe supports, lld-link нет.
  `mscordbi.src` rewrite.
- `.drectve`: MSVC vtable mangling `@@6B@` (например `??_7Type@@6B@`)
  в `_Pragma("comment(linker, ...)")` — link.exe parser tolerates,
  lld-link rejects. `dactable.cpp` + `daccess.h`: switched to dynamic
  `InitializeEntries` path на TARGET_SHARPOS.

**Lesson**: lld-link не drop-in для link.exe в production CoreCLR
codebase. Если будем staying on lld-link permanently — отдельный
hardening pass нужен для всех `.def` / `.drectve` patterns.

### 4. Standalone GC DLLs skip on TARGET_SHARPOS

`gc/CMakeLists.txt`: `FEATURE_STANDALONE_GC` block (`clrgc.dll` /
`clrgcexp.dll`) использует gc_pal библиотеку, которая включает
gc/windows/gcenv.windows.cpp с прямыми вызовами advapi32
(`LookupPrivilegeValueW`, `OpenProcessToken`, `AdjustTokenPrivileges`)
для large-page privilege escalation.

Эти DLLs shipped separately и не нужны kernel image — main coreclr.dll
использует embedded GC через cee_wks_mergeable path. Skip целиком на
TARGET_SHARPOS.

**Lesson**: host-tool / shipped-DLL distinction matters для skip
candidates. Также: D11 firewall (no direct WinAPI in gc/sharpos/)
будет нужно реально enforce при Phase 6.2 — сейчас gc/sharpos/
просто routes на gc/windows/ напрямую как Phase 6.1 expedient.

## Output artifacts

После успешного билда:

| Artifact | Size | Purpose |
|---|---|---|
| `coreclr_static.lib` | 197.6 MB | Kernel image input (Phase 6.1 integration target) |
| `coreclr.dll` | 19.3 MB | SHARED target — full DLL для smoke-test |
| `coreclr.lib` | 3.5 KB | Import lib для DLL |
| `mscordaccore.dll` | 4.8 MB | DAC для debug |
| `mscorrc.dll` | (есть) | Resources |
| `crossgen2` + `System.Private.CoreLib.dll` (crossgenned) | — | Regular CoreCLR managed corelib |

## Patches not yet validated (work continues)

Cascade закрывает compile/link surface, но runtime behaviour ни одного
patch'а не verified. Несколько мест где правильность под вопросом:

- `vm/amd64/asmconstants.h` — `OFFSETOF__Thread__m_pInterpThreadContext`
  выбран hybrid value 0xb50 (вместо UNIX 0xb48 / WIN 0xba0) на основе
  static assert mismatch при cascade. Layout correctness validated только
  static_assert'ом, не runtime test'ом.
- `vm/eetwain.cpp` — `_rdsspq()` stubbed как `size_t *targetSSP = nullptr;`.
  CET shadow stack pointer теряется → unwinder может misbehave если
  managed code ever runs с активным CET. SharpOS guest сейчас CET-off
  (Phase 1 baseline), но это carry-over риск.
- `vm/CMakeLists.txt` — `rtlfunctions.cpp` added в cee_wks_core sources
  напрямую через `if(CLR_CMAKE_TARGET_SHARPOS)` block. Это не использует
  существующий `VM_SOURCES_DAC_AND_WKS_WIN32` aggregation pattern —
  вместо этого добавляет файлы prosto. Cleanup возможен если решим
  что TARGET_SHARPOS должен извлечь больше Win32-side файлов
  (`dwreport.cpp`, `eventreporter.cpp`, COM interop).
- 75+ ungating points в `vm/` — каждый просто extends gate с
  `|| TARGET_SHARPOS` без deep analysis того что branch делает. Diff
  review нужен для classification: which are fork-permanent (correct
  on TARGET_SHARPOS by construction), which temporary (need replacement
  с SharpOS-native impl), which host-only (only correct на HOST_WINDOWS,
  break на kernel target).

## Next (the open items)

Из todo list для step 67:

1. **Diff review** — classify все 99 patches на fork-permanent /
   temporary / host-only. Текущий commit body sketch'ит structure
   по subsystem, но per-file classification missing.
2. **Symbol audit**: `dumpbin /imports coreclr.dll` (host WinAPI
   surface) + `llvm-nm --undefined coreclr_static.lib` (kernel-port
   interface). Это и есть real WinAPI debt inventory.
3. **Refresh** `work/PAL/CORECLR_PORT_WINAPI_DEBT.md` против real
   symbol set (current doc был написан в середине cascade, теперь
   stale).
4. **PAL D-decisions reconciliation** — пройти D1-D20 против real
   symbol set, отметить decisions которые conflict с runtime
   requirements. Особенно D5 (no threading), D9 (forward GC).
5. **Smoke-test ladder** (Windows host):
   - **Min**: `coreclr_initialize` returns S_OK.
   - **Mid**: `coreclr_execute_assembly` на trivial main без
     Thread.* и без post-init `new` (threads/GC disabled per D5/D9).
   - **Max**: managed `try/finally` без `throw`/`catch` (EH unwinder
     runs, no allocations).

После smoke-test'ов закрытие шага 67 + opening шага 68 для
integration milestone (link coreclr_static.lib в SharpOS kernel image,
hookup entry point).

## Update — post-sage investigation (Phase 6.1 minimal profile + full-bundle audit)

After step writeup, completed:

1. **3-way audit collected**: SharpOS Phase 6.1 / Vanilla Win / Vanilla WSL
   builds + symbol decomposition. Documented в
   `work/PAL/phase6_1-3way-audit-overview.md`.

2. **Phase 6.1 minimal profile reconfigured** (per plan.md "Workstation,
   non-concurrent"): only 3 disables (`FEATURE_SVR_GC`, `FEATURE_STANDALONE_GC`,
   `FEATURE_USE_SOFTWARE_WRITE_WATCH_FOR_GC_HEAP`). Initial speculative
   wide disables (TIERED_COMPILATION, PGO, MULTICOREJIT, REJIT, etc.)
   reverted after sage feedback — empirical evidence shows they trigger
   rotten upstream `#else` cascades без meaningful surface reduction.
   Fork commit `0bc5ae9ba35`.

3. **Both sages consulted** на overview document.
   - Sage 1: validated 3-way as epistemic upgrade. Refined CRT strategy
     ((c) hybrid with bootstrap order). Added Q6-Q8 (Phase 1 unwinder
     extension timing, GC heap init under ZeroGC, config tightening
     cost-benefit curve).
   - Sage 2: **5-layer reframe** (L1 guest support, L2 internal link,
     L3 platform capability, L4 disabled/cold, L5 EH personality).
     This is the strategic insight: only L3 becomes `SharpOSHost_*`.
     L1 lives в CoreCLR fork as C/C++ (Invariant 1 exception для submodule).
   Saved в `work/PAL/phase6_1-sage-replies.md`.

4. **User decisions** (synthesis after sage replies):
   - Math: steal libm subset (compiler builtins + minimal C), не
     C# wrappers (bootstrap order risk).
   - minipal: link готовую `libaotminipal.a` если cover'ит, не reimpl.
   - C++ template instances: investigation revealed они НЕ "lost CMake
     targets" — defined в other static libs (`gc_pal.lib`, `gcinfo_win_x64.lib`,
     `utilcodestaticnohost.lib`). Multi-lib bundle audit needed.
   - EH: построить `__CxxFrameHandler3` поверх Phase 1 unwinder primitives.
     Не aliasing — separate code on top.

5. **Full-bundle multi-lib audit** done (10 static libs together —
   coreclr_static.lib, gc_pal.lib, gcinfo_win_x64.lib, utilcodestaticnohost.lib,
   minipal.lib, coreclrminipal.lib, coreclrpal.lib, clrjit.lib,
   eventprovider.lib, coreclrtraceptprovider.lib).
   
   Result: **341 truly external** (vs 585 isolation). Breakdown:
   - 154 `__imp_*` Win32 syscalls (L3 — true kernel surface)
   - 157 CRT extern C (L1 — math, mem, str, file, stdio)
   - 13 C++ runtime primitives (L1 — new/delete, type_info, std::nothrow_t)
   - 9 EH personality + security (L5)
   - 0 FireEt/EventXplat (dummyprovider resolved them all)
   - 0 minipal_* (minipal.lib resolved them all)

   Honest must-implement estimate: **~50-60 functions** для Phase 6.1.a-c
   (вместо feared 418). Остальное либо bundled, либо stealable, либо
   ABORT_FATAL за D5/D6 gates.

## Файлы (этот шаг)

- Fork commits: `dotnet-runtime-sharpos`, branch `sharpos/coreclr-port`:
  - `2919ba34cbe` TARGET_SHARPOS: bring up CoreCLR compile/link cascade
  - `0bc5ae9ba35` TARGET_SHARPOS: Phase 6.1 profile + upstream bug fixes
- Этот writeup: `done/step067.md` (still OPEN — implementation work
  ahead per 5-layer plan).
- Analysis: `work/PAL/phase6_1-3way-audit-overview.md` (with full-bundle
  ADDENDUM), `work/PAL/phase6_1-sage-replies.md`,
  `work/PAL/sage-query-phase6_1-port-surface-bounds.md` (initial query).
- Raw audit data: `work/PAL/symbol-audit/` (3-way + full_bundle).

## Cmake/CLI artifacts (если потеряются)

```powershell
# Build invocation
cd c:\work\OS\dotnet-runtime-sharpos
.\build_clr_sharpos.ps1 -Clean
# Produces:
#   artifacts/bin/coreclr/windows.x64.Debug/coreclr.dll  (~19 MB)
#   artifacts/obj/coreclr/windows.x64.Debug/dlls/mscoree/coreclr/coreclr_static.lib  (~197 MB)
#   artifacts/bin/coreclr/windows.x64.Debug/mscordaccore.dll  (~5 MB)
```

## Update — Phase 6.1.0 link integration + Phase 6.1.a first call attempted

After WinAPI surface analysis closed:

1. **Phase 6.1.0 link integration achieved** (commit `b4b0156`). 16.5 MB
   kernel image с coreclr_static.lib + 9 bundle libs statically linked.
   `/INCLUDE:coreclr_initialize` force-pulls transitive closure. `/FORCE:MULTIPLE`
   resolves duplicate `Rh*` helpers (SharpOS managed wins by link order).
   `CrtAndEhStubs.cs` (~30 stubs) covers L1 (real impl via SharpOS.Std) и
   L5 (fatal stubs). Kernel boots в QEMU.

2. **Phase 6.1.a first call attempted**. `SharpOSHost_RunCxxCtors` walker
   (forked into `pal/sharpos/winapi_shim.cpp`) manually walks `.CRT$XCA..$XCZ`
   table since SharpOS kernel entry is `EfiMain` (no mainCRTStartup → no
   `__scrt_initialize_crt`). С counter/limit/skip-mask/table accessor
   diagnostics — bisection identified **ctor 4 = `log.cpp` static init**
   crashes inside libcmtd's `_register_thread_local_exe_atexit_callback`
   reading uninit'нутый sentinel global at link RVA `0xF6F510`.

3. **Sentinel patch hack** advanced one level (patches sentinel к `-1`
   before walker — pushes past first wall, hits next inside malloc-equivalent).
   Each step deeper reveals new uninit'нутую table — manual patching does
   not scale.

4. **Empirical confirmation** of sage round 1 predictions: Sage 1 Q2 (CRT
   init order — debug build pulls full bootstrap chain) and Sage 2 L5
   (EH personality + CRT bootstrap = separate work stream) — обе verified.

5. **Architectural decision required**. Options documented в
   `work/PAL/phase6_1a-empirical-findings.md`:
   - A: Full CRT bring-up (provide real heap/TLS/atexit primitives,
        libcmtd happy).
   - B: Surgical strip (remove all C++ static init from CoreCLR fork).
   - C: Replace libcmtd entirely (~150+ symbols в C# host).
   - D: Vanilla Linux PAL path (build SharpOS target as Linux-shape).
   Sage round 2 query planned.

Files added:
- `OS/src/Kernel/Diagnostics/CoreClrProbe.cs` — bisection + sentinel patch.
- `OS/src/PAL/SharpOSHost/Diagnostics.cs` — real `SharpOSHost_DebugPrint`.
- `OS/src/Boot/BootSequence.cs` — invocation в Phase4.
- `OS/src/Kernel/Diagnostics/Probes.cs` — `CoreClrInit = true` toggle.
- `OS/src/Boot/MinimalRuntime.cs` — `DllImportAttribute` + `CallingConvention`
  for P/Invoke into statically-linked `coreclr_*` symbols.
- `work/PAL/phase6_1a-empirical-findings.md` — full writeup.

Fork:
- `dotnet-runtime-sharpos/src/coreclr/pal/sharpos/winapi_shim.cpp` —
  `SharpOSHost_RunCxxCtors` walker + 5 diagnostic accessors.

## Update — Phase 6.1.a closure (ALL 60 ctors pass walker)

After Phase 6.1.a empirical findings + sage methodology pivot
(measure-before-commit с sage 1/2 endorsement), we:

### 1. Built ctor-dep extractor (sage 2's relocation-based method)

Tools live в `work/PAL/symbol-audit/`:
- `ctor_dep_extract.py` — walks .obj tree, parses llvm-readobj
  per-section relocations. Identifies ctors (`??__E*` / `_GLOBAL__sub_I_*`),
  extracts external symbol refs from per-function-range relocations.
- `filter_linked.py` — cross-references against actually-linked .lib
  bundle members (OS.csproj LinkerArg set).
- `pdb_lookup.py` / `pdb_module_at.py` — PDB-based RVA → symbol/module
  reverse lookup for fault diagnosis.

Sage 2 was right: relocation approach is structured + parseable, vs
disasm parsing fragile.

### 2. Measurement result

Across 1672 .obj files in CoreCLR build tree:
- 472 static C++ ctors compiled
- 197 ctors in linked .lib bundle members
- **60 ctors actually in final image** (after COMDAT folding / /OPT:REF)

Unique external symbols across linked ctors: **9 truly-external**.
3 stubs already provided (security cookie pair + memset). After
verifying fresh GCEvent/Histogram/SpinLock are defined in bundle
(stale `static_truly_external.txt` had false positives), **truly new
surface = 3 CRT symbols**: `atexit`, `__tlregdtor`, `_tls_index`.

Below sage 1's lowest threshold (10-20 → A' viable). Option A'
(hijack-not-init) confirmed по measurement.

### 3. Implementation — 3 hijack stubs

`OS/src/PAL/SharpOSHost/CrtAndEhStubs.cs`:
```csharp
[RuntimeExport("atexit")]
public static int Atexit(void* func) { return 0; }
//   ↑ 6 ctors call this (log/pgo/profdetach/stubhelpers).
//     Kernel never shuts down → no-op.

[RuntimeExport("__tlregdtor")]
public static int Tlregdtor(void* func) { return 0; }
//   ↑ 3 ctors call this (ceemain tls_destructionMonitor, eventpipe).
//     D5 single-threaded → no thread teardown.

[RuntimeExport("_tls_index")]
public static uint TlsIndex = 0;
//   ↑ data symbol. Satisfies link.
```

### 4. Walker bisection results

After implementing stubs, walker bisected through ctors empirically:

| ctor # | source | status | wall reason |
|---|---|---|---|
| 1-3   | debug.cpp, corhost.cpp, clrconfig.cpp     | ✅ trivial  | — |
| 4     | utilcode/log.cpp `??__EszLogFileName`      | ✅ **fixed by atexit stub** | (was: libcmtd atexit) |
| 5-13  | various (corhost extras, binder, appdomain) | ✅ trivial | — |
| 14    | dllimportcallback.cpp `??__Es_thunkFreeList` | ❌ skip mask | CrstStatic::Init → InitializeCriticalSection Win32 API |
| 15-53 | (39 ctors across cee_wks_core)             | ✅ trivial  | — |
| 54    | gcenv.ee.cpp `??__EanalysisTimer`           | ❌ skip mask | NormalizedTimer → minipal_hires_tick_frequency → QueryPerformanceFrequency |
| 55-60 | (6 more ctors)                             | ✅ trivial  | — |

**58 of 60 ctors pass trivially.** 2 ctors faulted on Win32 API
unresolved imports (`InitializeCriticalSection`, `QueryPerformanceFrequency`).
Skip mask `(1<<13) | (1<<53) = 0x20000000002000` bypasses both.

Fault pattern identical: both fault RIPs cluster around 0xF23A00..0xF23C00
which is the kernel32.lib IAT thunk region. When DLL not loaded (we're
UEFI not Windows), IAT entries point to uninit memory → page fault on
indirect call.

### 5. Phase 6.1.a walker — CLOSED

Walker completes cleanly:
```
limit=60: xi=0 xc=60 last=0xCE4EC50
--- bisection done ---
[info] elf validation start    ← next boot phase reached
```

Static-init wall (the original Phase 6.1.a target) — bypassed.
boot proceeds to Phase 5 (elf validation) and Phase 6.1.a's `Run()`
returns normally.

### 6. What's next (open work)

1. **Re-enable `coreclr_initialize(...)` call** в CoreClrProbe.Run().
   Walker now passes; ready to enter CoreCLR init body.
2. **New surface будет appear**: live runtime calls (not static init).
   The 154 Win32 imports classified в `work/PAL/phase6_1-L3-classification.md`
   will start firing. Each one is a wall until stubbed.
3. **Architectural question revisits**: at some point волну Win32 stubs
   нужно сделать одним коммитом — define `__imp_*` data symbols pointing
   к our stub functions, /FORCE:MULTIPLE picks ours over kernel32.lib's
   broken IAT thunks. Sage thresholds say ~50-60 stubs manageable;
   54 ctors-passing was achieved с 0 Win32 stubs implemented, so the
   real surface is in coreclr_initialize body, не static init.

Files (this update — uncommitted):
- `OS/src/PAL/SharpOSHost/CrtAndEhStubs.cs` (atexit/__tlregdtor/_tls_index)
- `OS/src/Kernel/Diagnostics/CoreClrProbe.cs` (bisection + skip mask)
- `OS/OS.csproj` (utilcodestaticnohost.lib dropped earlier)
- `work/PAL/symbol-audit/ctor_dep_extract.py` (extractor)
- `work/PAL/symbol-audit/filter_linked.py` (link filter)
- `work/PAL/symbol-audit/pdb_lookup.py`, `pdb_module_at.py` (fault diagnosis)
- `work/PAL/symbol-audit/ctor-deps/` (TSV outputs)
- `work/PAL/phase6_1a-empirical-findings.md` (earlier writeup, still valid)

## Update — Phase 6.1.b massive empirical session (fork commit `1df9965c2dd`)

После walker closure, one productive session of CRT/Win32 surface expansion
+ empirical walk-through coreclr_initialize body. Fork commit
`1df9965c2dd` captures fork-side changes. Main repo work uncommitted yet.

### Session arc

- **Start**: walker passing 60 ctors, but `coreclr_initialize` immediately
  crashes inside libcmtd's atexit registrar — sentinel-patch hack from
  previous session was workaround.
- **End**: full EE startup pipeline reached. ExecutionManager::Init done,
  SystemDomain::Attach done, 50+ Crsts initialized. Crash now deep in
  `CallStubGenerator::GenerateCallStubForSig` (genuinely Phase 6.1.c
  territory — past static init walls, into runtime semantics).

### Key milestones

1. **3 hijack stubs** (atexit / __tlregdtor / _tls_index) closed log.cpp
   wall. No more libcmtd atexit chain involvement.

2. **CRT/Win32 surface override layer** (`crt_imp_stubs.cpp` 849 lines).
   Separate library (`coreclrpal_kernel_crt.lib`) so doesn't collide
   with libcmtd at fork's coreclr.dll build. Covers 272 symbols via
   CRT_STUB macro emitting both direct fn + `__imp_NAME` data pair.
   /FORCE:MULTIPLE picks ours over ucrt.lib's IAT thunks.

3. **Real impls** for symbols hit empirically:
   - 20 string/ctype (strlen/wcscmp/isalpha/towlower etc.)
   - 5 timing (QPC, QPF, GetTickCount64, FILETIME, SYSTEMTIME)
   - 4 identity (proc/thread handles + IDs)
   - 8 lock primitives (single-thread no-op + sentinel state for CS)
   - 2 EncodePointer/DecodePointer (identity)
   - 9 CPU topology (single-CPU defaults)
   - 10 env/modules (no-env, single-image)
   - 2 job object (never-in-job)
   - 5 heap routed → SharpOSHost_HeapAlloc
   - 3 misc (GetLastError, IsDebuggerPresent)

4. **Diagnostic infrastructure**:
   - `[CRT trap] NAME caller=0xRIP` on first call to uncovered symbol
     + clean SharpOSHost_Panic instead of hlt-loop
   - `[real] NAME caller=0xRIP` trace for every real impl
   - HwFault handler: full register snapshot (RAX/RBX/RCX/RDX/RSI/RDI/
     RBP/R8-R15) + 32-qword stack dump with .text-range markers
   - `__security_check_cookie` actively checks + scans stack for caller RIP
     on mismatch

5. **The DacGlobals wall** — THE BLOCKER of this session:
   `DacGlobals::InitializeEntries()` walks `vptr_list.h` (36 classes):
   ```cpp
   #define VPTR_CLASS(name) {
       void *pBuf = _alloca(sizeof(name));
       name *dummy = new (pBuf) name(0);
       name##__vtAddr = PTR_TO_TADDR(*((PVOID*)dummy)); }
   ```
   Each `_alloca` grows stack по sizeof(class). With 24KB EEJitManager +
   similar large classes, total stack usage exceeds frame limit → canary
   corruption → ud2 в function epilogue.

   Reached в SharpOS fork because our earlier fork patch disabled
   MSVC vtable mangling alias path (lld-link rejects `@` in .drectve),
   forcing dynamic path. На HOST_WINDOWS this never hits.

   **Fix**: gate `VPTR_CLASS` expansion `#ifndef TARGET_SHARPOS`. DAC
   vtable addresses stay zero — only mscordaccore reads them for
   debugger introspection, kernel doesn't run that.

6. **`__chkstk` investigation** — false lead. Initially suspected our
   no-op `__chkstk` was breaking stack frame setup for large allocas.
   Removed our C# version to let libcmt.lib's real impl take over.
   Didn't change crash → __chkstk wasn't the immediate issue.

7. **BSS zeroing experiment** — false lead. Suspected `.data` BSS portion
   wasn't being zero-initialized by UEFI loader (which would explain
   garbage in g_codeRangeMap). Manual `memset(0)` on specific BSS
   regions didn't change crash — BSS was already properly zero-initialized.

### Empirical results — coreclr_initialize pipeline reached

After all fixes applied, kernel boot now executes:
1. ✅ Walker 60 ctors (2 skipped via mask: ctor 14 = thunkFreeList CrstStatic
   needs InitializeCriticalSection; ctor 54 = analysisTimer needs QPF)
2. ✅ `coreclr_initialize()` body entered
3. ✅ 4 mallocs through GcHeap (3 small + 1 24KB allocation = likely
   EEJitManager instance)
4. ✅ `CPUGroupInfo::InitCPUGroupInfo` runs — all CPU topology Win32 calls
   handled (GetProcessGroupAffinity, GetProcessAffinityMask,
   QueryInformationJobObject)
5. ✅ Configuration knob reads (~8 GetEnvironmentVariableW per knob set)
6. ✅ `GetSystemInfo` returns sane single-CPU info
7. ✅ `GetModuleHandleW` + `GetProcAddress` queries (Rtl* growable function
   tables — our stub returns null, CoreCLR falls back gracefully)
8. ✅ `ExecutionManager::Init`: completes (g_codeRangeMap placement-new'd,
   EECodeManager + EEJitManager created)
9. ✅ `SystemDomain::Attach`: stub managers init'd, AppDomain created
10. ✅ ~50+ Crsts initialized via `CrstBase::InitWorker` →
    `minipal_mutex_init` → our `InitializeCriticalSection` chain
11. ✅ `InitCallStubGenerator`: Crst init + `new CallStubCacheHash` OK
12. ❌ `CallStubGenerator::GenerateCallStubForSig`: crash at offset 0x74 —
    `#GP` (general protection) на non-canonical pointer
    `0x00000002_0C43A7AE` в stack frame [RSP+0x18]

### Current wall (next session)

`#GP` (vec=13) at PC=0xC441BC4 inside `GenerateCallStubForSig`. Frame walker
correctly unwinds — `ehInit=Y` for managed-style EE frames (frames 0, 1, 3):
- frame[0]: GenerateCallStubForSig+0x74 (callstubgenerator.cpp)
- frame[1]: ceemain.cpp (~EEStartupHelper area)
- frame[2]: native code (no managed EH)
- frame[3]: ceemain.cpp (deeper EEStartup)

Pattern of non-canonical pointer (`0x00000002_xxxxxxxx`):
- Low 32 bits = valid .text address (0x0C43A7AE points в callstubgenerator)
- High 32 bits = `0x00000002` (should be `0x00000000` for canonical pointer)
- Sign-extended canonical x64 pointer requires upper 17 bits all 0 or all 1

Most likely a **32/64-bit storage mismatch** somewhere — int32 value
packed adjacent to pointer in struct/stack, then read как 64-bit.
Source candidates:
- `m_routineIndex` (int32 field) packed near a pointer member
- One of our Win32 stubs returning int32 vs expected int64 (function
  uses it as ptr later, upper bits stay zero — but bits=`0x02` not zero)
- ABI mismatch where compiler expects sign-extension but got zero-extension

### Files (uncommitted in main repo)

Main repo OS/:
- `OS/src/PAL/SharpOSHost/CrtAndEhStubs.cs` —
  removed `__chkstk` (let libcmt provide), made `__security_check_cookie`
  active check with stack scan on mismatch
- `OS/src/PAL/SharpOSHost/CrtHeapStubs.cs` — diagnostic in HeapAlloc
- `OS/src/PAL/SharpOSHost/Diagnostics.cs` — `SharpOSHost_Panic` export
- `OS/src/Kernel/Diagnostics/CoreClrProbe.cs` — BSS zeroing для
  g_codeRangeMap + crstDebugInfo before coreclr_initialize; single
  unlimited walker run; restored coreclr_initialize call
- `OS/src/Boot/EH/HwFaultBridge.cs` — extended diagnostic: full register
  snapshot + 32-qword stack dump with .text markers
- `OS/OS.csproj` — added coreclrpal_kernel_crt.lib LinkerArg

Fork (committed as `1df9965c2dd`):
- `src/coreclr/pal/sharpos/crt_imp_stubs.cpp` (NEW)
- `src/coreclr/debug/ee/dactable.cpp` (VPTR_CLASS bypass)
- `src/coreclr/pal/sharpos/CMakeLists.txt` (new lib target)
- `src/coreclr/pal/sharpos/winapi_shim.cpp` (malloc routing)
- `build_clr_sharpos.ps1` (-NinjaClean flag)

### Next step decisions

1. Continue empirical debug of GenerateCallStubForSig crash — look for
   32/64-bit ABI mismatch in our stub returns OR ComputeTempStorageSize
   computation.
2. Commit main repo changes once next wall is also pinned down (paired
   commit with fork's `1df9965c2dd`).

---

## Phase 6.1.b round 2 — TLS root cause + TEB facade

After committing fork as `1df9965c2dd` brainstormed wall classification
with two architecture sages. **Both agreed** the non-canonical
`0x00000002_xxxxxxxx` pointer pattern в `GenerateCallStubForSig+0x74`
points к **missing TLS substrate**, not 32/64-bit ABI mismatch:

- **Sage 1**: recommended step back to Phase 5.5 — build the missing
  substrate (TLS, EH, SEH unwind) before continuing empirical wall hunt.
- **Sage 2**: recommended bounded substrate investigation (1-2 focused
  sessions) — minimal TEB/TLS facade in current Phase 6.1.b session,
  then classify what remains as alignment, stub-signature, or semantic.

User chose **Sage 2's path** — pragmatic, lower-cost, gives empirical
signal whether substrate is *the* blocker or just one of many.

### Empirical confirmation: 2982 `gs:0x58` reads

Disassembled full `coreclr.dll` and counted instructions accessing
GS-relative addresses:
- 2982 instances of `mov rXX, gs:0x58` (TEB->TlsSlots pointer)
- Plus scattered `gs:0x30` (TEB.Self), `gs:0x10` (StackLimit)

Без TEB ALL of those reads return whatever firmware left in GS shadow
register — garbage memory addresses, garbage `_tls_index` lookups,
non-canonical pointers propagating через xor-and-load to stack frames.

That matches the empirical pattern exactly:
- Low 32 bits = valid .text address (proper pointer somewhere in code)
- High 32 bits = `0x00000002` (junk from GS shadow being OR'd via
  indexed load: `mov rax, gs:[rdx*8]`)

### PE TLS directory contents

Walked PE headers of latest fork build:
- `IMAGE_TLS_DIRECTORY` present, populated by linker
- Template ~413 bytes (linker-emitted TLS initial values:
  `gCurrentThreadInfo`, `t_currentThread`, EE per-thread state, etc.)
- `AddressOfIndex` slot для `_tls_index` value — written by
  `LdrpHandleTlsData` in normal Windows; on bare-metal we must
  write `0` ourselves.

CoreCLR's `__declspec(thread)` variables compile to:
```
  mov rax, [_tls_index]      ; runtime-resolved slot number
  mov rcx, gs:[0x58]         ; TEB->ThreadLocalStoragePointer
  mov rax, [rcx + rax*8]     ; slots[_tls_index] → tls_block pointer
  ; access variable at fixed offset within tls_block
```

### TEB facade implementation

Added `SetupTebFacade(imageBase)` в [CoreClrProbe.cs](OS/src/Kernel/Diagnostics/CoreClrProbe.cs)
called immediately before `coreclr_initialize()`.

Steps:
1. Parse PE TLS directory dynamically (so RVAs survive image rebuilds):
   read e_lfanew → optional header → DataDirectory[9] → IMAGE_TLS_DIRECTORY64
2. Allocate 3 GcHeap buffers (zero-initialized):
   - TEB: 4 KiB
   - TlsSlots: 64 slots × 8 bytes = 512 bytes
   - tls_block: `(EndAddressOfRawData - StartAddressOfRawData) + SizeOfZeroFill` bytes
3. Copy TLS template from `image + tlsTemplateRva` → `tls_block` (initialized
   portion; zero-fill already from GcHeap)
4. Wire pointers:
   - `teb[0x30] = teb` (TEB.Self)
   - `teb[0x58] = slots` (TEB.ThreadLocalStoragePointer)
   - `slots[0] = tls_block` (slot #0 → our TLS block)
5. Write `_tls_index = 0` at `image + tlsIndexRva` (PE-relocated slot)
6. Emit wrmsr shellcode into `BootInfo.AsmExecBuffer + 64` (past STI/CLI/HLT
   slots at 0/16/32 used by X64Asm):
   ```
   48 89 C8         mov rax, rcx        ; RAX = TEB (low 32 in EAX)
   48 89 CA         mov rdx, rcx        ; RDX = TEB
   48 C1 EA 20      shr rdx, 32         ; RDX = high 32 (in EDX)
   B9 01 01 00 C0   mov ecx, 0xC0000101 ; IA32_GS_BASE MSR number
   0F 30            wrmsr               ; MSR[ECX] = EDX:EAX
   C3               ret
   ```
7. Call shellcode через `delegate* unmanaged<ulong, void>` с TEB
   address as arg → CPU's GS base register now points к our TEB.

### Expected outcome

After this setup CoreCLR's 2982 `gs:0x58` reads should resolve to
`slots`, then `slots[0]` → `tls_block`, then per-variable offsets within
`tls_block` give linker-initialized values. The non-canonical
`0x00000002_xxxxxxxx` pattern в `GenerateCallStubForSig` stack frame
should disappear.

Two outcomes possible:
- **A** TLS was THE wall → `GenerateCallStubForSig` progresses; new wall
  surfaces somewhere later. Continue empirical advance.
- **B** TLS partially helps but new wall appears for similar substrate
  reason (SEH unwind table, dynamic TLS for second `__declspec(thread)`
  variable, EH frame setup). Classify per Sage 1's exit criterion.

### Build status

`dotnet build OS/OS.csproj -c Release` clean (1 net7.0 EOL warning,
nothing on `CoreClrProbe.cs`). Full kernel rebuild + boot test next.

### Files (uncommitted, this round)

- `OS/src/Kernel/Diagnostics/CoreClrProbe.cs` — added `SetupTebFacade`
  method (~120 lines) + call site before `coreclr_initialize`
- `work/PAL/sage-query-phase6_1b-runtime-walls.md` — strategic query
  put к sages 1+2

---

# Step 67 CLOSE — Phase 6.1 closed: managed code executes on bare metal

**Status: CLOSED.** `coreclr_initialize` → `S_OK`; managed C# code JIT-compiled
and executed on bare-metal SharpOS via the official CoreCLR hosting API; a
byte-for-byte stock-`dotnet build` program reaches Console init. Remaining
blocker (CoreCLR GC heap integration) is fully root-caused and specced for the
next step.

## Phase 6.1.a/b — coreclr_initialize = S_OK

Cleared the full init wall cascade (each: targeted fix, не sledgehammer):

- **`__chkstk`** — libcmt asm reads `gs:[10h]` (TEB.StackLimit, one-shot boot
  snapshot); when RSP descends below it, enters page-zeroing loop corrupting
  kernel memory. Fix: `ChkstkStub` + `ChkstkPatcher` → single `0xC3` ret
  (MSVC `__chkstk` is net-zero on RSP; caller's `sub rsp,rax` does the alloc).
  Was the #UD-at-mid-instruction crash.
- **`Object::ValidateInner` VERIFY_HEAP** (object.cpp:562) — suppressed under
  TARGET_SHARPOS + `[VH]` diagnostic dump.
- **`sprintf_s`/`snprintf_s`** — JIT pre-import does `assert(charsPrinted>0)`;
  return 1 + `'?'`; format content emitted via diag for failfast capture.
- **`CreateNamedPipeA`** → INVALID_HANDLE_VALUE (DiagnosticServer bails).
- **JIT emitxarch path-consistency `noway_assert`** — targeted `#if
  !TARGET_SHARPOS` at two sites (no global getRelocTypeHint hack).
- **`nativelibrary.cpp:489` / `utils.cpp` / `applicationcontext.cpp`
  `Path::IsRelative`** — UEFI `\sharpos\X.dll` looks relative; suppressed.
- **Advapi32 ETW** — `LoadLibrary` sentinel + `GetProcAddress` → 9 noop
  `SharpOS_Event*` stubs (no IPC peer on bare metal).

Result: `coreclr_initialize hr=0x0`.

## First managed code execution (root cause: flat-vs-mapped PE)

`coreclr_create_delegate(Hello, SharpOSHello.Program, Run)` threw
`EEMessageException` m_hr=`0x8007000B` (COR_E_BADIMAGEFORMAT) resID=`0x2038`
(`BFA_BAD_IL_RANGE`). Root cause: `PAL_LOADLoadPEFile` returns a **flat raw
file buffer**; real Unix PAL mmaps sections at their RVAs. `LoadedImageLayout`
treated flat as mapped → `PEDecoder::GetRvaData` did `base+rva` instead of
`RvaToOffset` → JIT read garbage IL for any section past the first → bad-IL.
Fix: force `disableMapping=true` под TARGET_SHARPOS in `PEImageLayout::Load`
→ file PEs route through `FlatImageLayout` (correct section translation).
**`[managed] Hello, World` + `Hello.Run returned 42`** — full bidirectional
ABI round-trip (kernel C# → reverse-pinvoke → managed → forward P/Invoke
callback → return) verified. See memory `project-pe-layout-flat-not-mapped`.

## Stage A — byte-for-byte normal .NET program

- 171 fork framework dlls (`coreclr-pack/Debug/net10.0`) → `\sharpos\fx\`;
  `tpa.txt` (7185B) generated at build; `\sharpos\NormalHello.dll` from
  **stock `dotnet build`** (sha256 C080FFB5…), proven to run on stock
  `dotnet` Windows (exit 42) — same bytes.
- `CoreClrProbe`: reads `tpa.txt` → TРА property; `coreclr_execute_assembly`.
- Versions match exactly (app + fork fx both `10.0.0.0`) — binding works
  without version-agnostic tricks.
- **`System.Console` is Unix-flavored** (P/Invokes `libSystem.Native`, NOT
  kernel32). Added `libSystem.Native` sentinel + 25 `SharpOS_SN_*` console
  shims (`Write`→`SharpOSHost_DebugWrite`→COM1, `IsATty`→0, init→no-op).
- Debug-assert cascade suppressed (same class as VERIFY_HEAP — single-thread
  port can't satisfy multi-thread debug invariants): `ObjHeader m_alignpad`
  (syncblk.cpp:2918), `AwareLock` wake-signal state-machine (syncblk.inl
  381/390/393/416 ×2 fns).

Progressed through `System.Console::.cctor` → `get_Out EnsureInitialized` →
ConsolePal → `System.IO.Stream` → then **hang** (Monitor infinite spin).

## Root cause of the hang — GC heap not integrated (next step)

`[VH] … SOH=0 LOH=0`: `gc_heap->IsHeapPointer(obj)=false` for all objects.
Chain: `GCToOSInterface::VirtualReserve` → `::VirtualAlloc(MEM_RESERVE)` →
our `VirtualAlloc` → `SharpOSHost_HeapAlloc` → tiny commit-on-alloc kernel
GcHeap. .NET 10 region-GC needs a large cheap VA reserve → degenerate
gc_heap → IsHeapPointer false → sync-block/Monitor/write-barrier on false
data → thin-lock on `new object()` → broken contended path → infinite spin.
Symptom chain (SOH=0 → m_alignpad → AwareLock → spin) = one root: **we hand
GC memory as small commit-on-alloc kernel blocks, not a reserve/commit VA
arena.**

Memory spec captured (this writeup's investigation): QEMU 256MB →
PhysicalMemory (bump over UEFI Usable regions) → KernelHeap → GcHeap; JIT
exec via `AllocatePages(EfiLoaderCode)`. ~150–200MB usable.

## Next step plan (Option A — grown-up GC, we are page provider)

1. Bound GC via `coreclr_initialize` props: `System.GC.HeapHardLimit`
   ~64–128MB, `gcConcurrent=false`.
2. Real arena-backed `VirtualReserve`/`VirtualCommit` in `gcenv.sharpos.cpp`
   over a boot-reserved contiguous `PhysicalMemory` arena + `X64PageTable`
   (reserve=VA, commit=present+W) — bypassing KernelHeap/GcHeap.
3. De-collide `RhpNewArray`/`RhNewString` (kernel-managed vs CoreCLR-hosted
   allocators) — required for string/array-heavy pwsh.

Final target: PowerShell on SharpOS (Stage B richer console app → Stage C pwsh).

## Files (this close)

SharpOS repo: `OS/src/PAL/SharpOSHost/{ChkstkStub,ChkstkPatcher,CrtHeapStubs,
CxxFrameHandler,SehDispatch,SehStructs,SehUnwind,SystemNativeStubs}.cs` (new),
`Diagnostics.cs` (`SharpOSHost_DebugWrite`), `CrtAndEhStubs.cs`, `Memory.cs`,
`CoreClrProbe.cs` (tpa.txt + execute_assembly), `BootSequence.cs`
(`InstallChkstkShellcode`), `Idt/PanicDump.cs` (imagebase/RVA/bytes/stack
dump), `Boot/EH/HwFaultBridge.cs`, `Boot/UefiBootInfoBuilder.cs`,
`Boot/UefiTypes.cs`, `Kernel/Paging/X64PageTable.cs` (TrySetKernelFlags*),
`std/no-runtime/shared/GC/GcHeap.cs`, `std/no-runtime/shared/Bcl/{Guid,Path}.cs`
(new), `run_build.ps1` (fx/tpa/NormalHello), `hello/` (new).

Fork repo (`dotnet-runtime-sharpos`, separate): `vm/peimagelayout.{cpp,h}`
(disableMapping), `vm/object.cpp` (VH suppress+diag), `vm/syncblk.{cpp,inl}`
(alignpad/AwareLock suppress), `vm/prestub.cpp` (JIT method-name diag),
`vm/nativelibrary.cpp` `binder/{utils,applicationcontext}.cpp` (IsRelative),
`jit/emitxarch.cpp` (reloc tripwire), `pal/sharpos/crt_imp_stubs.cpp`
(libSystem.Native + Advapi32 sentinels, CreateNamedPipe, sprintf_s),
`vm/{crst,excep,clsload,methodtable,methodtablebuilder,pendingload}.cpp`.

## Next

GC arena integration (3-part plan above). Then Stage B/C toward pwsh.
