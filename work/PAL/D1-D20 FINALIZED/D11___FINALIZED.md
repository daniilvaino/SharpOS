# D11 — REVISED

## Статус

**Supersedes old D11 provider model.**

Old D11 correctly chose direct `extern "C"` calls and rejected runtime API tables. That part remains.

Old D11 incorrectly assumed that `SharpOSHost_*` symbols must be provided by a separate NativeAOT-produced `libsharposhost.a` / `SharpOSHost.lib`. That assumption is removed.

---

## Решение

`pal/sharpos/` calls host functions through direct `extern "C"` declarations.

The linker resolves the names at build time.

No API table.  
No runtime registration.  
No function pointer indirection.  
No separate NativeAOT host static library requirement.

---

## Core idea

`SharpOSHost_*` is a **stable C ABI symbol namespace**, not a commitment to any specific provider implementation.

```text
pal/sharpos/
  → extern "C" SharpOSHost_* symbol
  → linker resolves provider
```

Provider depends on environment:

```text
Phase 2 Windows spike:
  sharpos_host_windows_shim.lib

Phase 6 bare metal:
  sharpos_host_baremetal_shim.o / generated glue / kernel-provided symbols

Rejected:
  separate NativeAOT SharpOSHost.lib as mandatory boundary mechanism
```

---

## Why the old provider model was wrong

The old model was:

```text
C# SharpOSHost methods
  → [UnmanagedCallersOnly]
  → NativeAOT static library
  → libsharposhost.a / SharpOSHost.lib
  → linked into CoreCLR runner
```

This was unnecessary because:

```text
1. SharpOS kernel is already NativeAOT-compiled as the final OS image.
2. CoreCLR is a guest archive linked into that image.
3. The C ABI boundary can be provided by shim/glue symbols.
4. `pal/sharpos/` only needs stable names and signatures, not a NativeAOT packaging model.
```

NativeAOT remains necessary for compiling the SharpOS kernel. It is not necessary as the CoreCLR PAL boundary mechanism.

---

## Architecture

### Common PAL call shape

```cpp
// pal/sharpos/memory.cpp

#include "sharpos_host_api.h"

LPVOID WINAPI VirtualAlloc(
    LPVOID lpAddress,
    SIZE_T dwSize,
    DWORD flAllocationType,
    DWORD flProtect)
{
    void* result = nullptr;

    SharpOS_SystemError status = SharpOSHost_AllocPages(
        lpAddress,
        dwSize,
        TranslateAllocFlags(flAllocationType, flProtect),
        &result);

    if (status != SHARPOS_SUCCESS) {
        SetLastError(TranslateSharpOSErrorToWin32(status));
        return nullptr;
    }

    return result;
}
```

`pal/sharpos/` does not know who implements `SharpOSHost_AllocPages`.

That is intentional.

---

## Header contract

```cpp
// pal/sharpos/include/sharpos_host_api.h
//
// Stable C ABI declarations consumed by pal/sharpos/.
// Providers differ by environment:
//   - Windows spike: sharpos_host_windows_shim.lib
//   - Phase 6: bare-metal shim/glue/kernel symbols
//
// These declarations are NOT a mirror of NativeAOT [UnmanagedCallersOnly]
// exports. They are the stable ABI consumed by the CoreCLR guest archive.

#ifndef SHARPOS_HOST_API_H
#define SHARPOS_HOST_API_H

#include <stdint.h>
#include <stddef.h>
#include "sharpos_host_status.h"

#ifdef __cplusplus
extern "C" {
#endif

// === Memory ===

SharpOS_SystemError SharpOSHost_AllocPages(
    void* requestedAddress,
    size_t size,
    uint32_t flags,
    void** result);

SharpOS_SystemError SharpOSHost_FreePages(
    void* address,
    size_t size);

SharpOS_SystemError SharpOSHost_ProtectPages(
    void* address,
    size_t size,
    uint32_t protection);

// === Console / diagnostics ===

SharpOS_SystemError SharpOSHost_WriteConsole(
    const void* buffer,
    size_t length);

// === Time ===

SharpOS_SystemError SharpOSHost_GetTickCount64(
    uint64_t* result);

// === Module / file / later capabilities ===
// Added as trace-backed PAL demand requires them.

#ifdef __cplusplus
}
#endif

#endif
```

---

## Provider 1 — Windows-hosted spike shim

```cpp
// sharpos_host_windows_shim/memory.cpp

#include <windows.h>
#include "sharpos_host_api.h"

extern "C" SharpOS_SystemError SharpOSHost_AllocPages(
    void* requestedAddress,
    size_t size,
    uint32_t flags,
    void** result)
{
    DWORD protect = PAGE_READWRITE;
    DWORD allocType = MEM_RESERVE | MEM_COMMIT;

    void* p = ::VirtualAlloc(requestedAddress, size, allocType, protect);
    if (p == nullptr) {
        *result = nullptr;
        return SHARPOS_ENOMEM; // translated from GetLastError if desired
    }

    *result = p;
    return SHARPOS_SUCCESS;
}
```

This is allowed to use Win32 APIs because it is the temporary Windows host backend.

`pal/sharpos/` itself **must not** include `<windows.h>` or call Win32 APIs directly. Этот rule **hard-enforced through compile-time firewall** (см. ниже), не через discipline + grep.

---

## D11 firewall — compile-time enforcement

Per sage 2 round 7 finalization. Цель: physically prevent WinAPI попадание в pal/sharpos/, не полагаться на code review.

### Forced include + #pragma GCC poison

Create header `pal/sharpos/include/sharpos_no_winapi.h`:

```cpp
// pal/sharpos/include/sharpos_no_winapi.h
#pragma once

// Force-included into every pal/sharpos/*.cpp translation unit.
// NOT used for sharpos_host_windows_shim/.

#if defined(SHARPOS_BUILDING_PAL)
#if defined(_WIN32) || defined(__MINGW32__) || defined(__MINGW64__)

#if defined(__GNUC__) || defined(__clang__)

// Memory APIs
#pragma GCC poison VirtualAlloc VirtualFree VirtualProtect VirtualQuery
#pragma GCC poison HeapAlloc HeapFree GetProcessHeap

// File APIs
#pragma GCC poison CreateFileA CreateFileW ReadFile WriteFile CloseHandle

// Module APIs
#pragma GCC poison LoadLibraryA LoadLibraryW LoadLibraryExA LoadLibraryExW
#pragma GCC poison GetProcAddress FreeLibrary

// Threading
#pragma GCC poison CreateThread ExitThread GetCurrentThread GetCurrentThreadId
#pragma GCC poison WaitForSingleObject WaitForMultipleObjects
#pragma GCC poison CreateEventA CreateEventW SetEvent ResetEvent
#pragma GCC poison CreateMutexA CreateMutexW ReleaseMutex
#pragma GCC poison CreateSemaphoreA CreateSemaphoreW ReleaseSemaphore

// Time
#pragma GCC poison QueryPerformanceCounter QueryPerformanceFrequency
#pragma GCC poison GetSystemTimeAsFileTime GetTickCount GetTickCount64

// Unicode (per D20 — must route через SharpOSHost_*)
#pragma GCC poison MultiByteToWideChar WideCharToMultiByte

// D13 — Windows unwinder is oracle-only, never implementation
#pragma GCC poison RtlVirtualUnwind RtlLookupFunctionEntry
#pragma GCC poison RtlAddFunctionTable RtlDeleteFunctionTable
#pragma GCC poison RtlInstallFunctionTableCallback RtlCaptureContext

#endif // __GNUC__ || __clang__
#endif // _WIN32
#endif // SHARPOS_BUILDING_PAL
```

`#pragma GCC poison` делает poisoned identifier **hard compile error** если встречается после pragma. Это официальная GCC preprocessor feature специально для запрета identifiers.

### Build configuration

CMake setup для `coreclrpal` (pal/sharpos/) target:

```cmake
target_compile_definitions(coreclrpal PRIVATE SHARPOS_BUILDING_PAL=1)

# GCC/MinGW: forced include через -include
target_compile_options(coreclrpal PRIVATE
    -include ${CMAKE_CURRENT_SOURCE_DIR}/include/sharpos_no_winapi.h)

# MSVC equivalent: /FI flag
# target_compile_options(coreclrpal PRIVATE
#     /FI${CMAKE_CURRENT_SOURCE_DIR}/include/sharpos_no_winapi.h)
```

`sharpos_host_windows_shim` target **does NOT** get this flag — WinAPI разрешён там.

### Fake windows.h для pal/sharpos/

`#pragma GCC poison` ловит direct identifiers. Additional layer — запретить сам include:

```
pal/sharpos/forbidden_headers/
├── windows.h
├── winnt.h
├── minwindef.h
├── processthreadsapi.h
├── memoryapi.h
└── ...
```

Each fake header содержит only:

```cpp
// pal/sharpos/forbidden_headers/windows.h
#error "<windows.h> is forbidden in pal/sharpos. Use sharpos_host_api.h and SharpOSHost_*."
```

CMake: include directory **first** только для pal/sharpos/:

```cmake
target_include_directories(coreclrpal BEFORE PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/forbidden_headers)
```

Если кто-то в pal/sharpos/ напишет `#include <windows.h>` — сборка падает immediately с explicit error message.

### Source grep — secondary belt

Compile-time firewall — primary defense. Дополнительный CI check для catch'а edge cases:

```bash
grep -RInE \
  '#[[:space:]]*include[[:space:]]*[<"](windows|winnt|memoryapi|processthreadsapi|synchapi|fileapi|handleapi|libloaderapi)\.h[>"]|VirtualAlloc|CreateFileW|RtlVirtualUnwind|LoadLibraryW|CreateThread' \
  src/coreclr/pal/sharpos/
```

Expected: empty output.

### Object-level audit

После сборки pal/sharpos/ archive — verify no WinAPI undefined references:

**MinGW/GCC**:
```bash
x86_64-w64-mingw32-nm -u path/to/coreclrpal.lib \
  | grep -Ei '(__imp_|VirtualAlloc|CreateFile|ReadFile|WriteFile|LoadLibrary|GetProcAddress|RtlVirtualUnwind|RtlLookupFunctionEntry|CreateThread)'
```

For COFF/MinGW, imported WinAPI часто видны как `__imp_FunctionName`, grep по `__imp_` полезен.

**MSVC**:
```cmd
dumpbin /SYMBOLS path\to\coreclrpal.lib > artifacts\coreclrpal.symbols.txt
findstr /i "__imp_ VirtualAlloc CreateFileW RtlVirtualUnwind LoadLibraryW CreateThread" ^
  artifacts\coreclrpal.symbols.txt
```

Expected result: nothing из WinAPI.

### Link-level trace

Для GNU ld / MinGW — `--trace-symbol=symbol` показывает каждый linked file где встречается symbol:

```bash
x86_64-w64-mingw32-g++ \
  ... \
  -Wl,-Map=artifacts/spike-host.map \
  -Wl,--trace-symbol=__imp_VirtualAlloc \
  -Wl,--trace-symbol=__imp_CreateFileW \
  -Wl,--trace-symbol=__imp_RtlVirtualUnwind \
  -o spike-host.exe
```

Если `__imp_VirtualAlloc` появляется только из `sharpos_host_windows_shim.lib(memory.o)` — OK.
Если из `coreclrpal.lib(memory.o)` — FAIL.

Для MSVC аналог: `link.exe /VERBOSE:LIB /MAP:artifacts\spike-host.map`, потом grep по `link.verbose.txt`.

### Three-target build configuration

```
coreclrpal / pal_sharpos
  - SHARPOS_BUILDING_PAL=1
  - forced include sharpos_no_winapi.h
  - fake windows headers в include path
  - no WinAPI imports allowed (compile + link + object audit)

sharpos_host_windows_shim
  - may include windows.h
  - may call VirtualAlloc/CreateFileW/etc
  - no RtlVirtualUnwind except diagnostic file

diagnostics_windows_unwind_compare (optional)
  - may call RtlVirtualUnwind
  - only built если SHARPOS_DIAGNOSTIC_COMPARE_WITH_WINDOWS_UNWINDER=1
```

### Acceptance criterion

```
PASS:
  pal/sharpos builds с forced no-winapi header
  pal/sharpos has no undefined __imp_* WinAPI symbols
  final map shows WinAPI references originate только из sharpos_host_windows_shim
  RtlVirtualUnwind/RtlLookupFunctionEntry absent из production-shaped imports

FAIL:
  pal/sharpos includes <windows.h>
  pal/sharpos references __imp_VirtualAlloc / __imp_CreateFileW / __imp_Rtl*
  final link pulls RtlVirtualUnwind вне diagnostic build
```

---

## Provider 2 — bare-metal shim / glue

Possible shapes:

```text
A. C/C++ shim calls kernel internal ABI.
B. generated glue calls kernel-exported symbols.
C. assembly veneer switches calling convention / environment.
D. final image linker resolves SharpOSHost_* directly to kernel-provided symbols.
```

D11 does not choose the exact bare-metal glue mechanism.

D11 only fixes the contract:

```text
pal/sharpos/ consumes SharpOSHost_* C ABI names.
final image link must provide them.
```

---

## What stays from old D11

Still valid:

```text
- direct function calls
- no API table
- no AppServiceTable
- no runtime registration
- no dynamic lookup
- linker catches missing functions
```

Still valid rationale:

```text
- lower overhead
- simpler debugging
- fewer moving parts
- static image means update independence is not needed
- CoreCLR is rebuilt with the kernel
```

---

## What changes from old D11

Removed:

```text
- SharpOSHost_* must be [UnmanagedCallersOnly] C# exports.
- NativeAOT must produce libsharposhost.a / SharpOSHost.lib.
- pal/sharpos/ header mirrors NativeAOT static library exports.
- NativeAOT initialization order is part of D11.
```

Added:

```text
- SharpOSHost_* is an ABI namespace.
- Provider is environment-specific.
- Windows provider is a C/C++ shim.
- Bare-metal provider is final-image shim/glue/kernel symbol provider.
- Symbol manifest is mandatory.
```

---

## Symbol manifest

D11 introduces a required ABI manifest.

Example:

```text
docs/sharpos_host_api.symbols

SharpOSHost_AllocPages
SharpOSHost_FreePages
SharpOSHost_ProtectPages
SharpOSHost_WriteConsole
SharpOSHost_GetTickCount64
...
```

This manifest is generated or maintained from `sharpos_host_api.h`.

Build gates verify:

```text
1. Every symbol referenced by pal/sharpos/ is in the manifest.
2. Every manifest symbol has a provider in the current environment.
3. No C++-mangled SharpOSHost symbol names exist.
4. No unexpected provider symbol exists without declaration.
```

---

## Windows spike symbol audit

```cmd
dumpbin /SYMBOLS artifacts\sharpos_host_windows_shim.lib > artifacts\shim.symbols.txt
dumpbin /SYMBOLS path\to\coreclr_sharpos_static.lib > artifacts\coreclr.symbols.txt
dumpbin /IMPORTS artifacts\spike-host.exe > artifacts\imports.txt

findstr /i "SharpOSHost_" artifacts\shim.symbols.txt
findstr /i "unresolved external symbol SharpOSHost_" artifacts\link.verbose.txt
```

Acceptance:

```text
PASS:
  all SharpOSHost_* references are resolved by shim or final image provider.

FAIL:
  unresolved SharpOSHost_* symbol
  C++ decorated provider name
  provider implemented through runtime table instead of direct symbol
```

---

## Bare-metal symbol audit

For COFF / MSVC-like pipeline:

```cmd
dumpbin /SYMBOLS final_kernel_intermediate.lib > artifacts\kernel.symbols.txt
dumpbin /SYMBOLS coreclr_sharpos_static.lib > artifacts\coreclr.symbols.txt
```

For ELF-like pipeline:

```bash
nm -g final-kernel-intermediate.o > artifacts/kernel.symbols.txt
nm -u libcoreclr_sharpos_static.a > artifacts/coreclr.undefined.txt
```

Required check:

```text
All SharpOSHost_* undefined references from coreclr_sharpos_static are satisfied by final image link.
```

---

## Relationship to D9

D11 preserves D9:

```text
PAL is thin.
Functionality lives in kernel/host.
PAL translates CoreCLR Win32-shape API into SharpOSHost ABI.
```

If `pal/sharpos/` needs new functionality:

```text
Do not implement full subsystem inside PAL.
Add / expose new host capability.
Then wrap it from PAL.
```

---

## Relationship to D10

D11 depends on revised D10:

```text
CoreCLR guest archive is linked into final SharpOS image.
The linker resolves SharpOSHost_* names at image build time.
```

D10 defines the archive/image structure.

D11 defines the ABI symbol contract.

---

## Relationship to D13

D11 does not allow Windows unwind APIs to become host ABI.

Do not add:

```text
SharpOSHost_RtlVirtualUnwind
SharpOSHost_RtlLookupFunctionEntry
```

unless explicitly decided by D13.

D13 production path should use SharpOS / portable `.pdata` unwinder, not Windows `ntdll` as the host implementation.

Diagnostic-only comparison with Windows `RtlVirtualUnwind` must live outside the stable `SharpOSHost_*` ABI or be guarded by an explicit diagnostic flag.

---

## Relationship to AppServiceTable

SharpOS already has AppServiceTable for separate apps / launcher scenarios.

It is not used for CoreCLR guest.

Reasons:

```text
- CoreCLR is statically linked into the kernel image.
- CoreCLR is rebuilt together with the kernel.
- Hot paths should not pay function table indirection.
- API surface grows too large for convenient versioned table management.
- Linker already solves static binding.
```

---

## Decision rule for new host calls

When adding a new PAL function:

```text
1. Does the required capability already exist in kernel/host?
   yes → add or use SharpOSHost_* wrapper.

2. Does the capability belong in kernel?
   yes → implement kernel capability first, expose ABI, then wrap in PAL.

3. Is it temporary spike-only behavior?
   yes → implement in Windows shim, but do not add to bare-metal ABI until needed.

4. Is it complex runtime logic like unwind/EH/thread scheduling?
   do not hide it behind Windows APIs.
   route through project-owned substrate or defer behind explicit D13/D5 decision.
```

---

## C ABI rules

```text
- extern "C" only.
- fixed-width integer types.
- no C++ classes across boundary.
- no STL types across boundary.
- no exceptions across boundary.
- no ownership ambiguity.
- return SharpOS_SystemError or BOOL/HRESULT-equivalent status.
- output values through pointer parameters.
```

Allowed:

```cpp
extern "C" SharpOS_SystemError SharpOSHost_AllocPages(
    void* requestedAddress,
    size_t size,
    uint32_t flags,
    void** result);
```

Forbidden:

```cpp
std::vector<Region> SharpOSHost_GetRegions();
KernelObject* SharpOSHost_OpenObject(std::string name);
void SharpOSHost_DoThing(); // throws on failure
```

---

## Final wording

**D11 decision:**

> `pal/sharpos/` calls `SharpOSHost_*` host services by direct `extern "C"` declarations. These names form a stable C ABI consumed by the CoreCLR guest archive. The provider is environment-specific: a C/C++ Windows shim during Phase 2, and bare-metal shim/glue/kernel symbols during Phase 6. A separate NativeAOT-produced `SharpOSHost.lib` is not required and is not part of the D11 contract. The linker, not a runtime table, resolves the calls.
