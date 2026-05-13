# Sage query — Phase 6.1 kernel-port surface bounding strategy

**To**: Sage 2 (you did Round 4 / D1-D20 / Phase 2 Redesign / Phase 2A boundary tension).

**Status**: Phase 6.1 compile/link milestone reached (step 067, OPEN — `c665843` в main repo, `2919ba34cbe` в fork). `coreclr_static.lib` собрался, fresh symbol audit done. **Real kernel-port surface size — concrete data на руках.** Now need direction before designing implementation strategy. Concerned that naive "implement every symbol" path is months wasted; want validation of bounding strategy.

---

## What we have now

Build pipeline: clang-cl + lld-link на Windows host, native PE/COFF output. Fork `dotnet-runtime-sharpos` branch `sharpos/coreclr-port` produces:

- `coreclr_static.lib` (~197 MB) — kernel image input
- `coreclr.dll` (~19 MB) — SHARED target for smoke testing on host
- `mscordaccore.dll`, `crossgen2`, regular `System.Private.CoreLib.dll`

99 файлов изменено в форке. Key non-trivial findings during cascade (see step 067):
1. **CROSS_COMPILE auto-define trap** в `inc/crosscomp.h:24` — gated `TARGET_UNIX && !HOST_UNIX` automatically defined `CROSS_COMPILE`, который silently masked несколько Windows-EH gates (Module::ExpandAll, FixupDispatcherContext, XStateFeaturesMask asserts). Neutralized.
2. **MASM/C++ gate mismatch** на `USE_REDIRECT_FOR_GCSTRESS` — MASM emits call под `_DEBUG && HAVE_GCCOVER` (не видит USE_REDIRECT macro), C++ definition gated на дополнительный macro. Extended.
3. **lld-link strictness** vs link.exe — `.def` syntax (`DATA` case, no `private`), `.drectve` rejects MSVC vtable mangling `@@6B@`.

Detailed file/commit: `c:/work/OS/done/step067.md`, fork branch `sharpos/coreclr-port` commit `2919ba34cbe`.

---

## Symbol audit results (the data driving this query)

### `coreclr.dll` direct imports — 212 symbols, 4 DLLs

| DLL | Count | Notes |
|---|---|---|
| KERNEL32 | 196 | Memory/threading/sync/TLS/file/EH/time/locale |
| ADVAPI32 | 12 | Privileges + Registry + impersonation |
| ole32 | 3 | CoCreateGuid + CoTaskMemAlloc/Free |
| USER32 | 1 | LoadStringW |

### `coreclr_static.lib` truly external — 598 symbols

После фильтрации (`comm -23 undef defined`):

| Bucket | Count | Examples |
|---|---|---|
| **Win32 import thunks** `__imp_*` | **127** | Same as DLL imports above + some additional via static-only path |
| **C++ mangled** `?*` | **100** | `GCToOSInterface::*` (~30), `VMToOSInterface::*` (~10), `GCEvent::*` (~10), GcInfoEncoder template instances (~30), ctor/dtor/new/delete/`?CLRLoadLibraryEx`/`?ClrGetProcessExecutableHeap` |
| **Compiler-RT / EH** `__*` (non-imp) | **18** | `__C_specific_handler`, `__CxxFrameHandler3`, `__chkstk`, `__security_cookie`, `__guard_dispatch_icall_fptr`, `__atomic_compare_exchange_16` (clang builtin) |
| **FireEt\* / EventXplat\*** | **180** | Dummyprovider stubs, auto-resolved during DLL link (not real kernel surface) |
| **minipal_\*** | **27** | utf16↔utf8 convert, hires/lowres ticks, guid, log, mutex, xoshiro PRNG |
| **Misc extern C** | **146** | MSVC CRT (memset/strcpy/fopen/math/strlen/abort/qsort), COM IIDs (GUID_NULL, IID_IUnknown), `_Init_thread_*`, `_CrtDbgReportW`, `_CxxThrowException`, `_purecall`, `_wassert`, `_tls_index`, `_fltused`, `_invalid_parameter_noinfo`, `_atoi64`, `_dup`/`_errno`/`_fdopen`/`_fileno`/`_fopen`/etc., `GlobalizationResolveDllImport`, `WaitHandle_WaitOnePrioritized`, `dn_simdhash_assert_fail` |

Subtracting 180 FireEt (auto-resolved): **~418 real external symbols** need addressing для kernel link.

Raw data: `work/PAL/symbol-audit/{coreclr_dll_kernel32.txt, static_truly_external.txt, static_extern_c.txt, static_builtins.txt, static_cxx_mangled.txt}`.

---

## My proposed framing — "configure-then-classify"

Не implement 418 functions. Project Phase 6.1 runtime config tightly enough чтобы most paths become unreachable, then deal с residual ~40-90.

### Category breakdown (proposed)

| Category | Count (estimate) | Action |
|---|---|---|
| **Phase-6.1-CRITICAL** (init-path required) | **~40-60** | Real impl через `SharpOSHost_*` |
| **Phase-6.1-UNREACHABLE** (gated за off features) | **~150** | `ABORT_FATAL` stub (printf name + halt) |
| **No-op acceptable** (events, finalizers per D5) | **~50** | Empty stub, accept degradation |
| **Compiler/CRT** (memset, strcpy, math, abort) | **~150** | Either minimal CRT inject OR route via SharpOS `std/no-runtime` |
| **Auto-resolved** (FireEt dummyprovider) | **180** | Already handled at link time |

### What makes a symbol unreachable in Phase 6.1

- **ZeroGC + Workstation + non-concurrent** (per D9 / plan.md Phase 6.1): kills NUMA APIs (~10), GetWriteWatch/ResetWriteWatch (2), large-page privilege paths (~5 ADVAPI32), most heap stats (3-4).
- **Single thread / no threading** (per D5): kills CreateThread/ResumeThread/ExitThread (~10), SwitchToThread/Sleep (3), thread priority/affinity (~15), FlsAlloc/Free/Get/Set (4), most TLS only базовый Tls* (4 kept).
- **No file I/O** (assembly from memory blob): kills CreateFile/Read/Write/Find/SetEnd/SetFilePointer/Flush (~20), Console* (4), CreateNamedPipe/Connect/Disconnect (4), CreateFileMapping/MapView/UnmapView (3 — keep если для assembly load).
- **No process/job/COM control**: kills CreateProcess/ExitProcess/GetExitCode (3), QueryInformationJobObject/IsProcessInJob (2), CoTaskMemAlloc (kept), most user32/advapi32 (15).
- **No registry config — env vars only**: kills RegOpenKeyEx/Query/Close (4 ADVAPI32).
- **No Watson telemetry**: kills RaiseFailFastException, SetUnhandledExceptionFilter, etc. (~5).

Coarse estimate: ~150 of the 418 become unreachable under tight config.

### Open implementation questions

1. **CRT (146 symbols) is the biggest concrete block.** Three options:
   - (a) Minimal CRT injection (msvcrt-equivalent subset) — maintenance burden, foreign code.
   - (b) Route via SharpOS `std/no-runtime/shared/` (memset/strcpy already exist managed; math floor/ceil/sin/cos partially exist).
   - (c) Hybrid — math through `[UnmanagedCallersOnly]` C# wrappers; mem/string via inline assembly OR existing managed helpers.
   
   Option (b/c) is C# pure (Invariant 1), но requires ~150 thunks. Option (a) пахнет .c/.cpp в repo (violates Invariant 1 unless we treat msvcrt-stub as external lib).

2. **Compiler-RT `__*` (18) — небольшая, но conceptually trickiest.**
   - `__C_specific_handler` + `__CxxFrameHandler3` — MSVC SEH personality routines. SharpOS Phase 1 has its own .pdata unwinder per D13. Question: can Phase 1 unwinder serve as `__C_specific_handler` replacement, or these are different contracts?
   - `__security_cookie` + `__guard_dispatch_icall_fptr` — CFG (Control Flow Guard). Static stub `0x0` likely OK, или disable /guard:cf on TARGET_SHARPOS?
   - `__chkstk` — stack probe. Trivial impl или route on stack growth.
   - `__atomic_compare_exchange_16` — clang builtin для cmpxchg16b. Already stubbed via `_InterlockedCompareExchange128` в winapi_shim.cpp.

3. **GCToOSInterface (~30) + VMToOSInterface (~10)** уже abstracted. Phase 6.1: `gc/sharpos/gcenv.sharpos.cpp` routes via gc/windows/. Phase 6.2: replace в SharpOS mm. Question: should Phase 6.1 already implement these через `SharpOSHost_*` (cleaner separation), или leave Windows routing as expedient (faster path to first smoke-test)?

---

## Alternative framings to consider (challenge mine)

### Option A: Pure config-first (my framing)

Tighten runtime config aggressively. Most syscalls become dead code. ABORT_FATAL stubs catch leakage.

**Pro**: Bounded effort. Predictable scope.
**Con**: ABORT_FATAL surface may surface bugs at runtime — unknown unknowns about what config flag we missed.

### Option B: Smoke-test first, classify by trace

Run `coreclr_initialize` on Windows host (using real Windows DLLs for kernel32/etc.). Trace actual call paths. Implementation surface = what trace shows hit, plus modest margin.

**Pro**: Empirical reachability, no guessing.
**Con**: Windows DLLs may resolve paths that won't exist on bare-metal SharpOS — trace gives upper bound but might still miss some unreachable. Also: smoke-test setup requires harness work upfront.

### Option C: Skip Phase 6.1, jump к Phase 6.2 baseline

Phase 6.1 with ZeroGC + no-threading is intentionally crippled per plan. Maybe scope was wrong — should jump to "real GC + minimal threading" с Phase 3 scheduler hookup. Surface bigger но runtime less artificial.

**Pro**: Less wasted work on stubs that get replaced anyway.
**Con**: Phase 3 (scheduler) is gated на Phase 6.1 done per plan dependency chain. Reverses plan.

### Option D: Single-pass implementation, no classification

Just start implementing top-down, every symbol gets real impl, prioritized by what's hit during init. Treat 418 as just-do-it list.

**Pro**: No upfront analysis cost. Concrete progress every day.
**Con**: Likely 4-8 months grind. Many implementations will be wasted (symbol unreachable after all).

---

## Concrete decision criteria для you

1. **Is configure-then-classify (Option A) the right framing**? Or are unreachable estimates too optimistic — does CoreCLR init touch more than my analysis predicts? Recommend reading first 30 entries of static reachability vs my classification.

2. **For CRT (146 symbols) — which sub-option**? 
   - (a) inject minimal CRT (msvcrt-stub.lib)
   - (b) route via SharpOS std/no-runtime
   - (c) hybrid
   
   Invariant 1 considerations vs maintenance burden.

3. **For Phase 1 .pdata unwinder vs `__C_specific_handler`** — are they compatible contracts? If yes, we satisfy 2 of the 18 compiler-RT entries trivially. If no — we need both Phase 1's managed-EH unwinder AND MSVC EH personality stub layer.

4. **Smoke-test sequencing**: do we benefit from running coreclr_initialize against real Windows DLLs first (Option B preliminary) before designing kernel surface? Or is paper analysis sufficient given the size?

5. **Phase 6.1 honest scope re-check**: per plan, 6.1 = "demo-grade Hello World + basic JIT", finalizers off, leaks accumulate. With 418 symbols on the table, is this scope still realistic, or должна 6.1 stop short at "coreclr_initialize returns S_OK без trying to run managed code"?

---

## What we need

Direction. Specifically:

- Validate or invalidate "configure-then-classify" framing.
- Pick CRT strategy (a/b/c).
- Phase 1 unwinder reuse for `__C_specific_handler` — yes/no.
- Whether Windows-host smoke-test gives useful data before kernel-port work.
- Honest Phase 6.1 scope re-check — should it be narrower than current plan, given empirical surface size?

If different framing entirely (Option E?) — propose with cost estimate.

---

## Sources of truth

- `c:/work/OS/plan.md` — Phase 6 architecture + 6.1/6.2 split + critical path.
- `c:/work/OS/done/step067.md` — full cascade writeup (this open step).
- `c:/work/OS/work/PAL/D1-D20 FINALIZED/INDEX.md` — decision index.
- `c:/work/OS/work/PAL/D1-D20 FINALIZED/D5___FINALIZED.md` — threading off / ABORT_FATAL.
- `c:/work/OS/work/PAL/D1-D20 FINALIZED/D9___FINALIZED.md` — memory forward.
- `c:/work/OS/work/PAL/D1-D20 FINALIZED/D11___FINALIZED.md` — firewall.
- `c:/work/OS/work/PAL/D1-D20 FINALIZED/D13___FINALIZED.md` — EH integration with Phase 1.
- `c:/work/OS/work/PAL/CORECLR_PORT_WINAPI_DEBT.md` — initial inventory (stale, needs refresh after this query is resolved).
- `c:/work/OS/work/PAL/symbol-audit/` — raw audit data (this query's evidence).
- `c:/work/OS/dotnet-runtime-sharpos/` (fork) — actual patches, see commit `2919ba34cbe`.
