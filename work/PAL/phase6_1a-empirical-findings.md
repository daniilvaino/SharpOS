# Phase 6.1.a — empirical findings (CRT init wall)

**Status:** Phase 6.1.0 (link integration) achieved. Phase 6.1.a (first call
к `coreclr_initialize`) blocked at CRT static-init dependency chain. Architectural
decision required before continuing — full CRT bring-up vs surgical removal vs
alternative bootstrap.

## TL;DR

Static linking CoreCLR в kernel image работает (16.5 MB image, boots в QEMU).
Manual walk of `.CRT$XCA..$XCZ` C++ ctor table fires; ctors 1-3 (`debug.cpp`,
`corhost.cpp`, `clrconfig.cpp`) выполняются успешно. Ctor 4 (`log.cpp` static
init) уходит в libcmtd's atexit-registration helper, который ожидает что весь
debug-CRT уже initialized (heap allocator, atexit table head, security cookie,
TLS slots). Stubs returning "success" не достаточны — реальный code path в
libcmtd reads global state, branches на uninit'нутые значения, eventually
mallocs new atexit entry на heap which never was given to it.

Sages предсказали это в round 1:
- **Sage 1 Q2**: "CRT init order — debug build pulls libcmtd full bootstrap,
  including heap init, atexit, TLS. Stubbing this in pieces will reveal new
  uninit'нутое state на каждой итерации until heap allocator pre-init'нут."
- **Sage 2 L5 framing**: "EH personality + CRT bootstrap is a separate work
  stream — это не platform capability (L3), это runtime substrate (L5)."

Empirical evidence теперь confirms обе hypotheses.

## Что сделано

### 1. Link integration (Phase 6.1.0)

`OS/OS.csproj` LinkerArg для 10 (now 9) static libs:

```xml
<LinkerArg Include="coreclr_static.lib" />           <!-- 197 MB -->
<LinkerArg Include="gc_pal.lib" />
<LinkerArg Include="gcinfo_win_x64.lib" />
<!-- DROPPED: utilcodestaticnohost.lib (duplicate of utilcode .obj
     already в coreclr_static.lib — /FORCE:MULTIPLE picked random copy) -->
<LinkerArg Include="minipal.lib" />
<LinkerArg Include="coreclrminipal.lib" />
<LinkerArg Include="coreclrpal.lib" />
<LinkerArg Include="clrjit.lib" />
<LinkerArg Include="eventprovider.lib" />
<LinkerArg Include="coreclrtraceptprovider.lib" />
<LinkerArg Include="/INCLUDE:coreclr_initialize" />
<LinkerArg Include="/FORCE:MULTIPLE" />
```

Без `/INCLUDE:coreclr_initialize` linker не pull'ит coreclr_static.lib closure
(no callsite в kernel managed code yet) → 291 KB image, no functionality.
С force-include → 16.5 MB image.

Image breakdown:
| Section | Size | Content |
|---|---|---|
| .text   | 13.0 MB | CoreCLR runtime + JIT + GC + vm/ |
| .rdata  | 2.4 MB  | const data, vtables, RTTI |
| .pdata  | 0.5 MB  | EH unwind info |
| .managed| 0.14 MB | SharpOS kernel managed code |

### 2. Stub layer (CrtAndEhStubs.cs + SharpOSHost/*)

~50 stubs across two categories:

**Real implementations** (forwarded к existing SharpOS managed code):
- `memcmp`, `strchr`, `strrchr`, `strstr`, `memchr`, `wcsstr`, `wcschr`, `wcsrchr`
  — все через `SharpOS.Std.NoRuntime.MemoryPrimitives`.
- `memset`, `memcpy`, `memmove` — уже provided by `MinimalRuntime.cs` (pre-Phase 6).
- `SharpOSHost_DebugPrint` — writes UTF-8 char-by-char to OS.Console.

**Fatal stubs** (`Panic.Fail` если reached):
- L5 EH personality: `__CxxFrameHandler3/4`, `__C_specific_handler`,
  `_CxxThrowException`, `longjmp`, `__uncaught_exception`.
- L4 CRT init internals: `__vcrt_initialize`, `__acrt_initialize`,
  `__vcrt_thread_attach`/`_detach`, `__acrt_thread_attach`/`_detach`,
  `_is_c_termination_complete` (все return 1 = success).
- L4 misc: `_purecall`, `_CrtDbgReportW`, `_malloc_dbg`, `_free_dbg` (fatal).
- L1 constants: `_fltused = 0x9875`, `__security_cookie = 0x2B992DDFA232`,
  `__security_check_cookie` (no-op), `__chkstk` (no-op).

### 3. CRT walker — `SharpOSHost_RunCxxCtors`

Live в форке `dotnet-runtime-sharpos/src/coreclr/pal/sharpos/winapi_shim.cpp`.
Walks `__xc_a_sentinel..__xc_z_sentinel` (and XIA..XIZ phase first), calling
each non-null pointer.

Required because SharpOS kernel entry is `EfiMain`, не `mainCRTStartup`. Normal
Windows programs reach C++ globals via `__scrt_initialize_crt` called from
mainCRTStartup. Мы skip that path; without manual walk vtables remain
zero-initialized → first virtual call crashes (HW #PF on instruction fetch
from `.rdata`).

Diagnostic infrastructure:
- `g_SharpOSHost_XiPhase` / `XcPhase` — counter how far through tables.
- `g_SharpOSHost_LastCtorAddr` — last ptr value attempted.
- `g_SharpOSHost_CtorLimit` — bisection cap (stop after N ctors).
- `g_SharpOSHost_CtorSkipMask` — bitmask `[0..63]` of indices to skip.
- `SharpOSHost_GetCtorTable` — read sentinel addresses без execution
  (для image base computation, sentinel patching).

### 4. Empirical bisection results

| Limit | Outcome | Last addr |
|---|---|---|
| 1 | ✅ runs ctor 1 (debug.cpp) | clean return |
| 2 | ✅ runs 2 (corhost.cpp) | clean |
| 3 | ✅ runs 3 (clrconfig.cpp) | clean |
| 4 | ❌ HW #PF inside ctor 4 (log.cpp wrapper) | 0x18001D300 |
| skip-4 + limit=N (any N) | ❌ fault still inside ctor 4 chain | varies |

**PDB lookup** (`llvm-pdbutil`) identified ctor 4:
- 0x18001D300 = `??__Eutilcode_log_init@@YAXXZ` (utilcode/log.cpp file-scope ctor)
- Calls helper at 0x18001C000 (log.cpp's init function proper)
- That calls 0x180CB1180 (libcmtd internal — atexit registration thunk)
- Which calls 0x180CB1100 (real `_register_thread_local_exe_atexit_callback`)
- Which reads sentinel global at `0x180F6F510` (link-time RVA `0xF6F510`)
  - If `-1` → "table uninitialized, initialize me"
  - If anything else (including zero-init) → "this is the table head ptr"
- After reading uninit'нутый zero → branches deeper → eventually calls
  malloc-equivalent на heap which никто не initialized → fault.

### 5. Sentinel patch hack — got us one level deeper

`CoreClrProbe.Run()` теперь:
```csharp
// Compute loaded image base from $XCA sentinel address minus its link-time RVA.
ulong imageBase = xcAA - 0xFED000UL;
ulong* atexitSentinel = (ulong*)(imageBase + 0xF6F510UL);
*atexitSentinel = 0xFFFFFFFFFFFFFFFFUL; // -1 = uninitialized
```

Forces libcmtd's atexit registrar в "init me" branch. Crash moves: previous
HW #PF at `0x00F234B2` (in atexit-register's read path) → новый HW #PF at
`0x00F24522` (deeper, inside malloc-equivalent после "init table" decision).

**Reading**: каждый CRT global мы leave uninit'нутый — это next wall. Manual
patching does not scale.

## The dependency chain (concrete)

```
EfiMain (SharpOS kernel)
  → Phase4: CoreClrProbe.Run()
    → SharpOSHost_RunCxxCtors() [our manual walker]
      → ctor 1, 2, 3 [trivial — only zero/const init]
      → ctor 4 wrapper (??__Eutilcode_log_init@@YAXXZ)
        → log.cpp init helper (0x18001C000)
          → init guard check (one-shot) ✅
          → atexit-register via thunk (0x180CB1180)
            → real registrar (0x180CB1100)
              → read sentinel 0xF6F510 — UNINIT (0) ❌
                ← patched к -1 via hack → goes "init me" path ✅
              → tries to malloc new table entry
                → calls heap allocator (libcmtd internal)
                  → heap globals UNINIT ❌
                  → HW #PF at some lookup
```

Mental model: libcmtd is one cohesive bootstrap. Skipping `__scrt_initialize_crt`
leaves ~dozen global tables zero-init'нутыми. Each one becomes a wall as the
first piece of code reads it.

Tables we know about:
- `__acrt_first_block` / `_HEAP_HANDLE` — heap allocator state
- `__scrt_native_startup_lock` — one-shot init guard
- `__onexitend` / `__onexitbegin` — atexit table head/tail
- TLS slot indices — `__scrt_tls_*`
- `__security_cookie` — already provided as constant (but `__security_init_cookie` not called)
- Thread-local exe atexit table head (the 0xF6F510 sentinel мы patched)
- Various locale/IO globals в libcmtd

## Why stubs aren't enough

Initially attractive idea: provide stub for `_register_thread_local_exe_atexit_callback`
returning success. Doesn't work potому что:

1. Symbol is **not exported** by libcmtd — it's статическая helper резолвится
   intra-library. Our stub would only intercept если symbol было externally
   visible.
2. Re-export'нуть его means weak-linking against libcmtd что lld-link does not
   do well (we'd need /FORCE:MULTIPLE и rely on linker picking ours first).
3. Even if intercepted, `log.cpp` ctor likely passes opaque tokens (table
   pointers, callback addresses) which we'd need to track для symmetric
   cleanup later.

Скорее the model has to be: либо мы run libcmtd's full init (and provide
underlying primitives — heap, TLS — that satisfy it), либо нам не нужен
libcmtd и мы strip everything that references it.

## Architectural options

### Option A — Full CRT bring-up

Make libcmtd happy. Real implementations for:
- `HeapAlloc` / `HeapFree` / `HeapCreate` (already partially have via SharpOS heap)
- `TlsAlloc` / `TlsGetValue` / `TlsSetValue`
- Per-thread state (Phase 6 D5 said no threading — conflict)
- Critical sections (real or stubbed-as-no-op carefully)
- Some flavor of `_register_thread_local_exe_atexit_callback`

Surface estimate: ~20-30 functions, mostly L3 platform capability + libcmtd
init primitives. Roughly matches earlier "50-60 must-implement" estimate, но
shifted — больше CRT plumbing, меньше high-level runtime capability.

**Pro**: keeps CoreCLR pristine. No fork patching for this concern.
**Con**: D5 (no threading) пересмотр; threading'е nuance в init order.

### Option B — Surgical strip

Remove all C++ static initializers from CoreCLR. Move `log.cpp` style globals
to lazy-init paths. Fork patching at large scale.

**Pro**: clean target — CoreCLR без CRT bootstrap dependency.
**Con**: deep fork divergence. Every upstream merge becomes harder. И мы не
знаем сколько globals на самом деле необходимо initialize'нуть — some хранят
function pointer tables which lazy-init breaks.

### Option C — Replace libcmtd

Build CoreCLR с `/MD` instead of `/MT` (link против shared msvcrt — won't work
in kernel) OR build с `/NODEFAULTLIB:libcmtd` и provide our own libcmt
equivalent in C# host (via `[RuntimeExport]` for всех `__acrt_*`, `__vcrt_*`,
`_*dbg`, etc.).

**Pro**: complete control over CRT surface.
**Con**: ~~50~~ ~150+ symbols to implement. libcmtd internals are not documented;
behavior reverse-engineered from PDB.

### Option D — vanilla bootstrap reference

Investigate what Linux PAL bare-metal CoreCLR does (vanilla WSL build —
`dotnet-runtime-vanilla-wsl/`). On Linux there's no libcmtd; PAL ld-linux
runs `.init_array`/`.fini_array` natively, and CoreCLR doesn't ever expect
`__scrt_initialize_crt` semantics. Possibly the cleanest path: build SharpOS
target as if it were Linux (set `__GNUC__`, use `.init_array`, switch к
Itanium ABI EH personality).

**Pro**: well-trodden path — Linux CoreCLR is production code.
**Con**: massive build system change. `pal/sharpos/` was built assuming
HOST_WINDOWS shape. Toolchain switch from clang-cl к lld+gcc/clang. Different
EH (libunwind vs SEH).

## Recommendation для round 2 sage query

Pose architectural fork question:

> Empirical evidence от Phase 6.1.0/.a:
> - Static link works.
> - First C++ ctor fails inside libcmtd atexit chain due к uninit globals.
> - Patching sentinels manually scales linearly с CRT internals count.
>
> Options A-D above. Hybrid possible (например A + D shape — bring up subset
> of CRT internals наша way, leave libcmtd compile-time deps stubbed).
>
> Q1: какой path has lowest combined risk × effort?
> Q2: D5 "no threading" decision conflict с A (libcmtd wants TLS) — это
>     surface-only (single dummy thread state) или real (libcmtd uses
>     thread-locals dynamically)?
> Q3: Vanilla WSL Linux path — is fork divergence cost (~~50~~ 200+ patches
>     reverted) worth it for proven CRT semantics?

## Files

### Fork (`dotnet-runtime-sharpos`)
- `src/coreclr/pal/sharpos/winapi_shim.cpp` — added CRT walker + 5 diagnostic
  accessors + sentinel sections.

### Main repo
- `OS/OS.csproj` — utilcodestaticnohost.lib removed (duplicate).
- `OS/src/Boot/MinimalRuntime.cs` — `DllImportAttribute` + `CallingConvention`
  enum + `CharSet` made public.
- `OS/src/PAL/SharpOSHost/CrtAndEhStubs.cs` — memset/memcpy/memmove removed
  (duplicate с `MinimalRuntime.cs NativeMemoryStubs`).
- `OS/src/PAL/SharpOSHost/Diagnostics.cs` — `SharpOSHost_DebugPrint` real
  impl + `SharpOSHost_DebugPrintHex` added.
- `OS/src/Kernel/Diagnostics/CoreClrProbe.cs` — bisection loop + sentinel
  patch hack + GetCtorTable usage.
- `OS/src/Kernel/Diagnostics/Probes.cs` — `CoreClrInit = true` toggle.
- `OS/src/Boot/BootSequence.cs` — `CoreClrProbe.Run()` invocation в Phase4.

## Next

1. Commit current empirical findings (this doc + code).
2. Round 2 sage query c above options + evidence.
3. Make architectural decision.
4. Plan Phase 6.1.b structure (depends on decision).
