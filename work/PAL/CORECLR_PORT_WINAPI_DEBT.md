# CoreCLR Port: WinAPI Debt Inventory

**Purpose.** Phase 6.1 CoreCLR static-lib build relies на HOST_WINDOWS Win32 API
для прохождения compile/link. На bare-metal SharpOS Win32 не доступен — нужно
перенаправить каждую такую зависимость на наш own pal/sharpos implementation.

Этот файл — single source of truth для **WHERE Win32 API leaks into the static lib**,
чтобы при kernel integration легко swap'нуть.

## Strategy

Желаемая архитектура:

```
coreclr_static.lib ──► winapi_shim.h ──► default: link kernel32.lib (Windows host)
                            │
                            └──► swap: link pal/sharpos/winapi_kernel.cpp (SharpOS)
```

Один точечный shim header → одна single point swap. Сейчас depend-cy раскиданы
по vm/, utilcode/, debug/. Нужно **collect** их в shim.

## Categorization

### A. Direct Win32 import libs (linker level)

В `src/coreclr/dlls/mscoree/coreclr/CMakeLists.txt` мы расширили
`CLR_CMAKE_TARGET_WIN32` gate на TARGET_SHARPOS, что добавило:

| Lib | Purpose | Kernel replacement |
|-----|---------|--------------------|
| `kernel32.lib` | Process/thread/memory/file API | pal/sharpos thread/mem/file shims |
| `advapi32.lib` | Registry, security | stub (kernel не нужен) |
| `ole32.lib`, `oleaut32.lib`, `uuid.lib` | COM | stub (нет COM в kernel) |
| `user32.lib` | Window/message API | stub (нет UI) |
| `version.lib` | Version info | stub |
| `shlwapi.lib` | Path API | pal/sharpos path utils |
| `bcrypt.lib` | Crypto | stub or kernel-side crypto |
| `RuntimeObject.lib` | WinRT | stub |
| `delayimp.lib` | Delay-load helper (`__delayLoadHelper2`) | stub (no delay-load в kernel) |
| `libcmtd.lib`, `libvcruntimed.lib` | MSVC CRT | naш std/no-runtime/ |

### B. Code paths assuming HOST_WINDOWS Win32 calls

Patches которые route TARGET_SHARPOS на Windows-side branch (HOST_WINDOWS provides API).
При kernel build эти branches вызывают real Win32 → нужен swap.

#### B.1 PE/COFF image loading

- **`vm/peimagelayout.cpp:546`** — `LoadedImageLayout` ctor берёт `!TARGET_UNIX||TARGET_SHARPOS`
  branch: `CLRLoadLibraryEx(path, NULL, ...)` (Windows `LoadLibraryExW`).
  Kernel: PE статически линкован в kernel image, эта функция не reachable.
- **`vm/peimagelayout.cpp:625`** — `~LoadedImageLayout` calls `CLRFreeLibrary` (Windows `FreeLibrary`).
  Kernel: no-op stub.
- **`vm/peimagelayout.cpp:1229`** — `NativeImageLayout` ctor: `WszCreateFile` + `CLRLoadLibraryEx`.
  Kernel: not reachable.
- **`vm/method.cpp:3411`** — `FindEntryPoint` берёт Windows branch с `GetProcAddress(hMod, ...)`.
  Kernel: PInvoke resolve через static link table.
- **`vm/method.hpp:3236`**, **`vm/method.cpp:3338`** — `FindEntryPointWithMangling`/`Suffix`
  helpers взяты для Windows path. Same as above.
- **`vm/nativelibrary.cpp:167,184,273,296,326,649,664,680`** — `CLRLoadLibraryEx`,
  `FreeLibrary`, `GetProcAddress`, `IsWindowsAPISet` (`api-`/`ext-` prefix check)
  все берут Windows branch.

#### B.2 Native lib loader / hostpolicy

- **`vm/exports.cpp:264`** — skipped `PAL_InitializeCoreCLR`. На kernel: own
  bootstrap (Phase 6.2).
- **`vm/exports.cpp:373,405`** — skipped `PAL_Shutdown`. Same.
- **`vm/nativelibrary.cpp:664`** — `GetModuleHandle(NULL)` для embedded hostpolicy.

#### B.3 Threading

- **`vm/threads.cpp:2170`** — `Thread::CreateNewThread` use Windows
  `CreateThread()`. Kernel: own thread scheduler (D5, deferred to Phase 6.2).
- **`vm/threads.cpp:4504`** — `GetCurrentThreadId()` instead of PAL. Windows-only.
- **`vm/threads.cpp:6039`** — `GetStackLowerBound` via `ClrVirtualQuery` (`VirtualQuery`).
- **`vm/threadsuspend.cpp:5979`** — `ThreadSuspend::Initialize` Windows branch
  (`WszLoadLibrary("ntdll.dll")`, `GetProcAddress("RtlGetReturnAddressHijackTarget")`).
- **`vm/threadsuspend.cpp:5950`** — thread activation injection: explicit
  `return false;` stub. Phase 6.2: own IPI/APC mechanism.
- **`vm/threadstatics.cpp:1075,974`** — TLS index access (`_tls_index`,
  `NtCurrentTeb()->ThreadLocalStoragePointer`). Phase 5.5 native TLS bring-up.
- **`vm/comsynchronizable.cpp:371`** — `GetCurrentThreadId()`.
- **`vm/eepolicy.cpp:349`** — `GetCurrentThreadId()`.

#### B.4 Memory / GC OS interface

- **`gc/sharpos/gcenv.sharpos.cpp`** — placeholder. Нужно реальные impl для
  `GCToOSInterface::VirtualCommit/Release/Reserve/Decommit` (Phase 6.2 high-priority
  blocker). Сейчас undefined → blocked static-lib link.
- **`utilcode/executableallocator.cpp:200`** — `InitPreferredRange` no-op stub.

#### B.5 Eventing / IPC

- **`debug-pal/CMakeLists.txt`** — взяли Windows named-pipe IPC + DiagnosticServer.
  Файлы `win/twowaypipe.cpp`, `win/processdescriptor.cpp` используют:
  - `CreateNamedPipe`, `ConnectNamedPipe`, `ReadFile`, `WriteFile`, `OVERLAPPED`
  - `OpenProcess`, `GetExitCodeProcess`
  Kernel replacement: own IPC mechanism (probably skip — diagnostics not needed
  в kernel mode), либо stub returns failure.
- **`native/eventpipe/ds-ipc-pal-namedpipe.c`** — full named-pipe IPC PAL,
  использует HOST_WIN32 path. См. `ds_ipc_poll` etc.
- **`vm/eventing/eventpipe/ep-rt-coreclr.h:935`** — `ClrSleepEx` (Windows `SleepEx`).
- **`vm/eventing/eventpipe/ep-rt-coreclr.h:989`** — `GetCurrentThreadId`.

#### B.6 Exception handling / unwind

- **`vm/excep.cpp:11430`** — `Thread::VirtualUnwindCallFrame` (Windows
  `RtlVirtualUnwind`-based). D13 plan: own .pdata unwinder works on Windows
  side, will reuse on SharpOS.
- **`vm/exceptionhandling.cpp:35-44`** — `ClrUnwindEx` + `CallRtlUnwind` Windows
  branch (`RtlUnwind`). 
- **`vm/exceptionhandling.cpp:1683`** — `ClrUnwindEx::RtlUnwind` direct.
- **`vm/exceptionhandling.cpp:1569`** — `STATUS_LONGJUMP` handling using
  `m_pLongJmpBuf`/`m_longJmpReturnValue`. Windows `setjmp/longjmp` ABI.
- **`vm/frames.cpp:644`**, **`vm/stackwalk.cpp:693`**, **`vm/eetwain.cpp:2245`** —
  все routed на `Thread::VirtualUnwindCallFrame` (Windows path).
- **`vm/debughelp.cpp:25`** — `isMemoryReadable` через `ReadProcessMemory` (Windows).
  Kernel: page-table walk shim.

#### B.7 Library loading helpers

- **`vm/gcheaputilities.cpp:308`** — `pGcModuleBase = (PTR_VOID)hMod;` instead
  of `PAL_GetSymbolModuleBase`. На kernel: статически линкованный GC,
  base addr через linker symbol.

### C. Compiler runtime intrinsics

- **`__atomic_compare_exchange_16`** — 16-byte CAS, нужна `clang_rt.builtins-x86_64.lib`
  на link. Kernel: будем включать в наш build pipeline.

### D. Format-level / metadata

- **`mscorwks_ntdef.src`** — `g_CLREngineMetrics @2 DATA`, `CLRJitAttachState @3 DATA`
  — `extern "C"` exports. Kernel: эти globals не exposed external'но, можно
  skip .def механизм entirely.
- **`mscordbi.src`** — убран `private` keyword (lld-link не support). Это
  для mscordbi.dll который мы не build на TARGET_SHARPOS.

### E. Compiler/linker workarounds (не WinAPI, но dirty)

- **`-Wno-invalid-token-paste`** — MSVC vtable mangling макросы (`??_7X@@6B@`)
- **`-Wno-c++11-narrowing`** — clang strict default-error
- **`__declspec(selectany)` в genEventing.py/genDummyProvider.py** — weak symbol
  conflict workaround
- **`debug/ee/dactable.cpp:59`** — route clang-cl через non-MSVC dynamic init
  branch (vs `_Pragma("comment(linker, /ALTERNATENAME:...)")` which lld-link reject)
- **`debug/inc/daccess.h:631`** — соответствующий dac structure swap
- **`native/eventpipe/ds-ipc-pal-namedpipe.c:204`** — clang LLVM 22 internal crash
  workaround в `ds_ipc_poll` (hoisted var decls)
- **`scripts/genDummyProvider.py:122`** — `#include <windows.h>` для type defs
  (BOOL/ULONG/ERROR_SUCCESS) в auto-generated stubs

### F. Stubs / no-ops (clean, no WinAPI but deferred work)

- **`vm/exinfo.cpp:381`** — `ReleaseResources` — оба branch'а skipped на SHARPOS
- **`vm/exceptionhandling.cpp:884`** — entire TARGET_UNIX EH block (1522 lines)
  skipped — PAL_SEHException-based propagation
- **`vm/excep.cpp:3919`** — `PAL_GenerateCoreDump` skipped (no crash dump in kernel)

## Remaining Unresolved Symbols (last link attempt before .dll skip)

20 unresolved symbols, организованы по группам:

### Group I — In my hands (gate too aggressive, ungate):

| Symbol | Source | Fix |
|--------|--------|-----|
| `EECodeManager::UpdateSSP` | `vm/eetwain.cpp:2139` | Add `SSP` field в `REGDISPLAY` для SHARPOS → un-gate |
| `InterpreterCodeManager::UpdateSSP` | `vm/eetwain.cpp:2264` | Same |
| `GetSSPForFrameOnCurrentStack` | `vm/eetwain.cpp:2123` | Same |
| `Thread::RestoreGuardPage` | gated `!TARGET_UNIX` | Расширить на SHARPOS |
| `Module::ExpandAll` | gated similarly | Same |
| `Compiler::mapRegNumToDwarfReg` | JIT gated | Same |

### Group II — Need pal/sharpos stub (extern "C" / standalone):

| Symbol | Stub implementation |
|--------|---------------------|
| `ClrGetProcessExecutableHeap` | `return NULL;` |
| `CLRLoadLibraryEx` | route to `LoadLibraryExW` (Windows host) |
| `RtlVirtualUnwind_Unsafe` | function ptr, `NULL` init |
| `GlobalizationResolveDllImport` | `return NULL;` |
| `WaitHandle_WaitOnePrioritized` | route to `WaitForSingleObject` |
| `GetTlsIndexObjectAddress` | `return NULL;` |
| `InsertThreadIntoAsyncSafeMap` | no-op |
| `RemoveThreadFromAsyncSafeMap` | no-op |
| `FuncEvalHijack` | empty `__declspec(noreturn) void FuncEvalHijack() {}` |
| `GCToOSInterface::VirtualCommit/Release/Reserve/Decommit` | route to `VirtualAlloc`/`VirtualFree` (или прямо в kernel mm на SharpOS) |

### Group III — Runtime library:

| Symbol | Fix |
|--------|-----|
| `__atomic_compare_exchange_16` | Link `clang_rt.builtins-x86_64.lib` |

## Action Items для kernel integration (Phase 6.2)

1. **Создать `pal/sharpos/winapi_shim.cpp`** — single point с stubs для всех
   Group II symbols выше (~15 functions)
2. **Patch SSP gates** — un-gate, add `SSP` к `REGDISPLAY` struct (Group I)
3. **Implement `gc/sharpos/gcenv.sharpos.cpp`** — real `VirtualCommit/Reserve`
   используя kernel mm subsystem (high-priority)
4. **Switch link против `clang_rt.builtins-x86_64.lib`** (Group III)
5. **kernel-link mode** — добавить cmake flag `CLR_KERNEL_LINK=1` который routes
   все Win32 import libs через `pal/sharpos/winapi_shim` вместо `kernel32.lib` etc.

## Maintenance note

Файл живой. При каждом новом dirty fix — добавить запись сюда. Pre-commit
hook recommendation: grep for `SharpOS port: HOST_WINDOWS` или `Windows path`
в diff → require entry в этом файле.
