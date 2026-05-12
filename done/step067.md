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

## Файлы (этот шаг)

- Fork commit: `dotnet-runtime-sharpos`, branch `sharpos/coreclr-port`,
  commit `2919ba34cbe TARGET_SHARPOS: bring up CoreCLR compile/link cascade`.
- Этот writeup: `done/step067.md` (open).

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
