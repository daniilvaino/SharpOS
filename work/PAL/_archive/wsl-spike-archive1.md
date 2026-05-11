# WSL spike archive 1 — Phase 2 spike full data dump

Полный архив raw данных Phase 2 spike в WSL2 environment. Не writeup в формате step (это не finished step — это **measurement run**), а dump всего что узнали на путь от "WSL setup" → "Branch A SharpOSHost.a validates C-ABI" → "libcoreclr.so не линкуется" → "libcoreclr.so dlopens + первый PAL trap fire'ит".

Step066 (`done/step066.md`) зафиксировал checkpoint **на линке** (Iter 1-5 ниже). Этот архив покрывает **весь** spike с самого начала включая pre-step066 setup и post-step066 work которая дошла до first runtime trap.

Forward-looking план (что делать с этими данными) — `done/phase2-sage-round4-synthesis.md`.

---

## Environment

- **Host**: Windows 11, project at `c:\work\OS\`
- **Build env**: WSL2 (Ubuntu) с .NET 10 SDK
- **CoreCLR fork**: `c:\work\OS\dotnet-runtime-sharpos\` (gitignored из SharpOS repo per `04e7864`)
- **Branch**: based на `release/10.0`
- **Compiler**: clang++-14
- **Build command**: `./build.sh -c Debug -s clr+libs+host -cmakeargs "-DSHARPOS_PAL=ON -DPython_EXECUTABLE=/usr/bin/python3"`

Linux paths используют `/mnt/c/work/OS/dotnet-runtime-sharpos/...` через 9P from WSL.

---

## Stage 0 — WSL2 + dotnet fork setup

User cloned `dotnet/runtime` release/10.0 branch как `dotnet-runtime-sharpos/` directly inside SharpOS working tree (`c:\work\OS\dotnet-runtime-sharpos\`).

**Why inside SharpOS dir**: convenience (single workspace), but требует gitignore. Commit `04e7864` добавил `dotnet-runtime-sharpos/` в .gitignore чтобы fork не загрязнял SharpOS repo.

**.NET 10 SDK install** через ppa + apt в WSL Ubuntu:
```bash
sudo apt install dotnet-sdk-10.0
```

User мог сам run build commands в WSL terminal — assistant edit'ил файлы через Windows-side Edit/Write (через 9P) и user copy-paste'ил commands.

First build attempt — `./build.sh -c Debug -s clr` — пошёл скачивать .NET runtime locally (build.sh prefers self-contained dotnet). Это ожидалось.

---

## Stage 1 — Branch A: SharpOSHost.a + C smoke test (validates C-ABI line)

**Goal**: validate что NativeAOT C# code компилируется в static `.a` archive callable из native C.

**SharpOS-side**: `OS/src/PAL/SharpOSHost/` создан с csproj:
```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <PublishAot>true</PublishAot>
  <NativeLib>Static</NativeLib>
  <SelfContained>true</SelfContained>
  <InvariantGlobalization>true</InvariantGlobalization>
  <RuntimeIdentifier>linux-x64</RuntimeIdentifier>
  <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
</PropertyGroup>
```

`HostExports.cs` — 4 smoke functions:
```csharp
[UnmanagedCallersOnly(EntryPoint = "SharpOSHost_GetVersion")]
public static uint GetVersion() => 0x0001_0000;

[UnmanagedCallersOnly(EntryPoint = "SharpOSHost_GetPageSize")]
public static nuint GetPageSize() => 4096;

[UnmanagedCallersOnly(EntryPoint = "SharpOSHost_AddInts")]
public static int AddInts(int a, int b) => a + b;

[UnmanagedCallersOnly(EntryPoint = "SharpOSHost_FillBuffer")]
public static void FillBuffer(byte* buf, nuint count, byte value) {
    for (nuint i = 0; i < count; i++) buf[i] = value;
}
```

`dotnet publish -r linux-x64 -c Release` produced `libsharposhost.a` + headers.

**Native test**: `dotnet-runtime-sharpos/spike-tests/test_sharposhost.c`:
```c
extern unsigned int SharpOSHost_GetVersion(void);
extern size_t SharpOSHost_GetPageSize(void);
extern int SharpOSHost_AddInts(int a, int b);
extern void SharpOSHost_FillBuffer(unsigned char* buf, size_t count, unsigned char value);

int main() {
    printf("Version: %x\n", SharpOSHost_GetVersion());
    printf("PageSize: %zu\n", SharpOSHost_GetPageSize());
    printf("Add: %d\n", SharpOSHost_AddInts(2, 3));
    unsigned char buf[16] = {0};
    SharpOSHost_FillBuffer(buf, 16, 0xAB);
    printf("Filled: %02x...\n", buf[0]);
    return 0;
}
```

`build.sh` link script собирает test program с `-Wl,--start-group/--end-group` поверх NativeAOT runtime libs:
- `libRuntime.WorkstationGC.a`
- `libbootstrapperdll.o`
- `libstdc++compat.a`
- `libaotminipal.a`
- `libeventpipe-disabled.a`
- `libstandalonegc-disabled.a`
- `libSystem.Native.a`
- `libSystem.Globalization.Native.a`
- `libRuntime.VxsortDisabled.a`

`--start-group/--end-group` обязателен — `minipal_get_*_random_bytes` из `libaotminipal.a` нужны `libSystem.Native.a` и наоборот, ld один-проход иначе теряет.

**Result**: smoke output:
```
Version: 10000
PageSize: 4096
Add: 5
Filled: ab...
```

**Branch A validated:** NativeAOT static archive linkable из native code. C-ABI line works.

---

## Stage 2 — pal/sharpos/ skeleton design

**Goal**: измерить какие PAL functions vm/gc/jit реально требуют через link errors.

**CMake substitution mechanism** в `src/coreclr/pal/CMakeLists.txt`:
```cmake
option(SHARPOS_PAL "Use SharpOS PAL backend (pal/sharpos/) instead of pal/src/" OFF)

if(SHARPOS_PAL)
  message(STATUS "PAL backend: SharpOS (pal/sharpos/)")
  add_subdirectory(sharpos)
else()
  message(STATUS "PAL backend: standard (pal/src/)")
  add_subdirectory(src)
endif()
```

**`pal/sharpos/CMakeLists.txt`** — defines `coreclrpal` STATIC target из `stub.cpp` + `stubs.cpp`. Plus placeholder targets для libraries которые pal/src/ defines но мы не реализуем:
- `coreclrpal_dac` — DAC PAL helper (skipped DAC-based debugging)
- `coreclrtraceptprovider` — LTTng-style tracepoints
- `tracepointprovider` — tracepoint backend
- `eventprovider` — ETW provider (Win32 events)

Все placeholder'ы — `add_library(... STATIC stub.cpp)` (empty TU). Это needed чтобы dependent targets могли link-resolve без ошибок.

**stub.cpp** — empty compilation unit:
```cpp
// Phase 2 spike — pal/sharpos/stub.cpp.
// Empty translation unit для dependent cmake targets.
```

**stubs.cpp** — trap pattern:
```cpp
[[noreturn]] static void sharpos_pal_trap(const char* name) {
    fprintf(stderr, "\n[sharpos-pal-trap] called: %s — not implemented\n", name);
    abort();
}

#define TRAP_VOID(name) void name(void) { sharpos_pal_trap(#name); }
#define TRAP_PTR(name) void* name(void) { sharpos_pal_trap(#name); }
#define TRAP_INT(name) int name(void) { sharpos_pal_trap(#name); }
#define TRAP_UINT(name) unsigned int name(void) { sharpos_pal_trap(#name); }
#define TRAP_ULONGLONG(name) unsigned long long name(void) { sharpos_pal_trap(#name); }
```

**Real impls** (не trap'ы):
```cpp
static thread_local unsigned int s_lastError = 0;
unsigned int GetLastError(void) { return s_lastError; }
void SetLastError(unsigned int err) { s_lastError = err; }
```

`thread_local` работает на Linux WSL потому что pthread TLS setup'нута. Для bare-metal SharpOS это нужно migrate'нуть к real TLS infrastructure.

**COM GUID constants** — declared as `extern "C" const sharpos_guid` per-declaration:
```cpp
struct sharpos_guid {
    unsigned int Data1;
    unsigned short Data2;
    unsigned short Data3;
    unsigned char Data4[8];
};

extern "C" const sharpos_guid IID_IUnknown = { 0, 0, 0, {0xC0,...,0x46} };
extern "C" const sharpos_guid IID_IStream = { 0xC, 0, 0, {0xC0,...,0x46} };
extern "C" const sharpos_guid IID_ISequentialStream = { 0x0c733a30, ..., {...} };
extern "C" const sharpos_guid GUID_NULL = { 0, 0, 0, {0,...,0} };
```

C++ default `const X` имеет internal linkage. **Per-declaration** `extern "C" const` обязателен. Не достаточно `extern "C" {...}` block — это даёт extern linkage по C-нотации, но `const` всё равно overrides обратно к internal в C++.

---

## Iteration log — закрытие линкера

Каждая iteration = build attempt → identify next pack of unresolved symbols → add stubs → rebuild.

### Iter 1-2 — initial 41 traps (covered in step066.md)

`pal/sharpos/stubs.cpp` начало с пустого compilation unit. Build failed at libcoreclr.so link с тысячами unresolved references. Систематически добавили 41 traps:
- Virtual memory: `VirtualAlloc/Free/Protect/Query`
- File I/O: `CreateFile{A,W}`, mappings
- Module loading: `LoadLibraryEx*`, `GetProcAddress`, `FreeLibrary`
- Sync primitives: `CreateEventW`, `CreateSemaphoreExW`, `WaitForSingleObjectEx`, `WaitForMultipleObjects`
- Charset: `MultiByteToWideChar`, `WideCharToMultiByte`, `_wcs*`
- System info: `GetSystemInfo`, `GetSystemTime{,AsFileTime}`
- Format/CRT: `_snprintf_s`, `sprintf_s`, `FormatMessageW`, `OutputDebugStringA`
- Exception/debug: `RaiseException`, `RaiseFailFastException`, `DebugBreak`
- PAL-specific: `PAL_Initialize`, `PAL_GetCurrentThread`, `PAL_GetTotalCpuCount`
- Real impls: `GetLastError`/`SetLastError` через `thread_local unsigned int s_lastError`

**Result**: link продвинулся через libcoreclr.so + dependent targets, упёрся на mscordbi с `--undefined-version` LLD-specific flag.

### Iter 3 — skip mscordbi via SHARPOS_PAL=ON conditional

`src/coreclr/dlls/mscordbi/CMakeLists.txt`:
```cmake
if(SHARPOS_PAL)
  message(STATUS "mscordbi: skipped (SHARPOS_PAL=ON)")
  add_library(mscordbi SHARED ${CMAKE_CURRENT_SOURCE_DIR}/../../pal/sharpos/stub.cpp)
  set_target_properties(mscordbi PROPERTIES LINKER_LANGUAGE CXX)
  return()
endif()
```

**Result**: hit singlefilehost host-toolchain target.

### Iter 4 — skip singlefilehost

`src/native/corehost/apphost/static/CMakeLists.txt`:
```cmake
if(SHARPOS_PAL)
  message(STATUS "singlefilehost: skipped (SHARPOS_PAL=ON)")
  set(SHARPOS_STUB_CPP "${CMAKE_CURRENT_BINARY_DIR}/sharpos_stub.cpp")
  file(WRITE "${SHARPOS_STUB_CPP}" "int main(int argc, char** argv) { return 0; }\n")
  add_executable(singlefilehost "${SHARPOS_STUB_CPP}")
  set_target_properties(singlefilehost PROPERTIES LINKER_LANGUAGE CXX)
  return()
endif()
```

**Result**: hit mscordbi.so.dbg install error (stub'нутый mscordbi не проходит post-build symbol-stripping pipeline, .dbg файл не генерится).

### Iter 5 — strip install_clr from mscordbi stub

Removed `install_clr(...)` call из SHARPOS_PAL=ON branch. Stub только для linker dependency satisfaction, install не нужен.

**Result**: build complete до crossgen-corelib post-step. crossgen2 пытается AOT-precompile System.Private.CoreLib.dll в R2R image. Loads `libclrjit_unix_x64_x64.so` (cross-targeting JIT), которая тоже built против trap stubs → trap'ает на `PAL_InitializeDLL`. **Expected** — это host-side toolchain, не runtime. **libcoreclr.so installed.**

### Step066 checkpoint

```
-- Installing: artifacts/bin/coreclr/linux.x64.Debug/./libcoreclr.so
-- Installing: artifacts/bin/coreclr/linux.x64.Debug/./libcoreclr.so.dbg
```

14 MB stripped, 60 MB+ debug. Это main spike achievement step066 зафиксировал.

---

## Iteration log — закрытие dlopen

Линкер **позволил** undefined symbols в libcoreclr.so (because `--allow-shlib-undefined` либо implicit для shared lib). Dynamic linker при `dlopen` отказывается резолвить → `corerun Hello.dll` failed на dlopen с `undefined symbol: PAL_getenv`.

### Iter 6 — enumerate ALL missing symbols

```bash
nm -D --undefined-only $BIN/libcoreclr.so | awk '{print $2}' | sort -u > /tmp/missing.txt
wc -l /tmp/missing.txt   # 475
```

475 total. Из них:
- ~40 наших PAL/Win32 (pattern: PAL_*, Win32 names)
- ~200 eventing (`EventXplatEnabled*`, `FireEtXplat*`)
- ~150+ libc / libstdc++ / libgcc symbols (резолвятся через system .so)
- 1 libc `getenv@GLIBC_2.2.5` (нормально, libc-side)

### Iter 7 — add 41 round-2 PAL traps

Добавили в `stubs.cpp`:

```cpp
// Win32-shape
TRAP_PTR(CreateEventExW)
TRAP_PTR(GetCurrentProcess)
TRAP_VOID(OutputDebugStringW)
TRAP_UINT(WaitForSingleObject)
TRAP_UINT(WaitForMultipleObjectsEx)

// PAL initialization / shutdown
TRAP_INT(PAL_InitializeCoreCLR)
TRAP_VOID(PAL_NotifyRuntimeStarted)
TRAP_VOID(PAL_Shutdown)
TRAP_VOID(PAL_SetShutdownCallback)

// Module loading
TRAP_PTR(PAL_LoadLibraryDirect), TRAP_INT(PAL_FreeLibraryDirect)
TRAP_PTR(PAL_GetProcAddressDirect), TRAP_PTR(PAL_GetLoadLibraryError)
TRAP_PTR(PAL_LOADLoadPEFile), TRAP_INT(PAL_LOADUnloadPEFile)
TRAP_INT(PAL_LOADMarkSectionAsNotNeeded)

// Threading / sync
TRAP_PTR(PAL_CreateThread64), TRAP_PTR(PAL_CreateMutexW), TRAP_PTR(PAL_OpenMutexW)
TRAP_UINT(PAL_WaitForSingleObjectPrioritized), TRAP_UINT(PAL_GetCurrentOSThreadId)
TRAP_INT(PAL_nanosleep)

// Stack / activations / exceptions
TRAP_PTR(PAL_GetStackBase), TRAP_PTR(PAL_GetStackLimit), TRAP_INT(PAL_VirtualUnwind)
TRAP_VOID(PAL_InjectActivation), TRAP_VOID(PAL_SetActivationFunction)
TRAP_VOID(PAL_SetHardwareExceptionHandler), TRAP_VOID(PAL_SetGetGcMarkerExceptionCode)
TRAP_VOID(PAL_FreeExceptionRecords), TRAP_INT(PAL_ProbeMemory)

// Memory ranges, diagnostic, perf JIT dump, environment
TRAP_VOID(PAL_GetExecutableMemoryAllocatorPreferredRange)
TRAP_VOID(PAL_IgnoreProfileSignal), TRAP_INT(PAL_GenerateCoreDump)
TRAP_PTR(PAL_GetTransportName), TRAP_PTR(PAL_GetTransportPipeName)
TRAP_VOID(PAL_PerfJitDump_Start), TRAP_INT(PAL_PerfJitDump_IsStarted)
TRAP_VOID(PAL_PerfJitDump_LogMethod), TRAP_VOID(PAL_PerfJitDump_Finish)
TRAP_PTR(PAL_getenv)
```

**Build issue**: cmake mtime detection через 9P /mnt/c filesystem не reliable. `touch stubs.cpp` from WSL side обязательно перед `cmake --build`. Иначе rebuild не triggered.

**Result**: hit `_ZN8_CONTEXTaSERKS_` mangled symbol — `_CONTEXT::operator=(_CONTEXT const&)`.

### Iter 8 — _CONTEXT::operator= out-of-line definition

`pal.h:1273` declares member function:
```cpp
typedef struct DECLSPEC_ALIGN(16) _CONTEXT {
    ...
    _CONTEXT& operator=(const _CONTEXT& ctx);
    ...
};
```

Implementation в upstream `pal/src/thread/context.cpp:2204` — variable-size memcpy для XSTATE/AVX-512/APX. У нас pal/src/ skipped. Создали `pal/sharpos/context_ops.cpp`:

```cpp
#define PAL_IMPLEMENTATION
#include "pal.h"

#ifdef HOST_AMD64
CONTEXT& CONTEXT::operator=(const CONTEXT& ctx) {
    memcpy(this, &ctx, sizeof(CONTEXT));  // simplified — full struct copy
    return *this;
}
#endif
```

Реальный impl делает variable-size copy в зависимости от ContextFlags + XStateFeaturesMask. Для spike достаточно полного memcpy.

### Iter 9 — eventing + libstdc++

Hit ~26 Win32 functions (Sleep, ExitProcess, etc.) + ~200 ETW functions + libstdc++ symbols (`__cxa_*`, `__gxx_personality_v0`).

**Win32 пачка** добавлена в stubs.cpp (CreateThread, ResumeThread, SetThreadPriority, GetThreadContext, SetThreadDescription, Sleep, QueueUserAPC, CreateMutexExW, OpenMutexW, ReleaseMutex, OpenEventW, ResetEvent, OpenSemaphoreW, SignalObjectAndWait, DuplicateHandle, FlushInstructionCache, FlushProcessWriteBuffers, RtlCaptureContext, RtlRestoreContext, SetEnvironmentVariableW, _wtoi, ExitProcess, ExitThread, TerminateProcess, GetCurrentProcess).

**ETW пачка** через **upstream dummyprovider**:
```cmake
if(NOT TARGET eventprovider)
  set(EVENT_MANIFEST ${VM_DIR}/ClrEtwAll.man)
  add_subdirectory(${COREPAL_SOURCE_DIR}/src/eventprovider/dummyprovider eventprovider_dummy)
endif()
```

`pal/src/eventprovider/dummyprovider/CMakeLists.txt` запускает `genDummyProvider.py` который generates ~200 no-op stubs из `ClrEtwAll.man` manifest. Reuse upstream — manual work не требовался.

**libstdc++ link** explicit:
```cmake
target_link_libraries(coreclrpal PUBLIC stdc++)
```

C++ ABI symbols (`__cxa_throw`, `__cxa_begin_catch`, `__gxx_personality_v0`, `__cxa_guard_*`, `__cxa_pure_virtual`, etc.) приходят отсюда. CoreCLR vm/ структурно зависит от C++ EH — 363 EX_TRY sites + 28 PAL_TRY (per sage round 4 measurement).

**Result**: libcoreclr.so dlopens успешно. Все link-time + dlopen-time symbols resolved.

---

## First runtime trap fire — measurement

```bash
LD_LIBRARY_PATH=$BIN $BIN/corerun Hello.dll
[sharpos-pal-trap] called: MultiByteToWideChar — not implemented
Aborted (core dumped)
```

**Это main measurement** spike'a. После этого spike достиг своих data goals.

### Final symbol counts

| Source | Count |
|---|---|
| Trap stubs в `pal/sharpos/stubs.cpp` | ~165 |
| Eventing no-ops (через dummyprovider) | ~200 |
| **Total link-time PAL/eventing surface** | **~365** |

После sage round 4 review (через verified .NET 10 source paths): real PAL surface = **144 functions** declared в `pal/inc/pal.h` (87 Win32-shape + 57 PAL_-prefix). Spike's 165 включал eventprovider-internal symbols + palprivate.h + rt/-namespace items — non-canonical PAL surface count.

### What we did NOT achieve (spike pass criteria from phase6-architecture.md §11)

| # | Criterion | Status |
|---|---|---|
| 1 | `SharpOSHost_Initialize` returns success | not written |
| 2 | `coreclr_initialize` returns S_OK | not reached |
| 3 | JIT compiles Program.Main (visible via COMPlus_JitDisasm) | not reached |
| 4 | "hello" appears in stdout | not reached |
| 5 | Process exit code = 42 | not reached |
| 6 | PAL summary без fatal stub hits | trapped on first |

**0/6.** Spike validated **link & dlopen**, не **execution**.

---

## Files touched (all in dotnet-runtime-sharpos/, gitignored)

### New

- `src/coreclr/pal/sharpos/CMakeLists.txt` — pal/sharpos/ skeleton
- `src/coreclr/pal/sharpos/stub.cpp` — empty compilation unit
- `src/coreclr/pal/sharpos/stubs.cpp` — ~165 trap stubs + GUID constants + GetLastError
- `src/coreclr/pal/sharpos/context_ops.cpp` — `_CONTEXT::operator=` definition

### Modified

- `src/coreclr/pal/CMakeLists.txt` — `SHARPOS_PAL` option + conditional add_subdirectory
- `src/coreclr/dlls/mscordbi/CMakeLists.txt` — stub target если SHARPOS_PAL=ON, install skipped
- `src/native/corehost/apphost/static/CMakeLists.txt` — stub executable если SHARPOS_PAL=ON

### Build artifacts (artifacts/bin/coreclr/linux.x64.Debug/)

- libcoreclr.so (14 MB stripped)
- libcoreclr.so.dbg (60 MB+ debug info)
- libcoreclrpal.a (наш static с trap stubs)
- corerun executable
- libclrjit.so + variants
- libclrgcexp.so, libmscordaccore.so, etc.

---

## Build pipeline gotchas (concrete failures + fixes)

### G1 — pyenv shim breaking CMake Python detection

First build attempt failed с misleading Python error. Initially diagnosed как "pyenv broken" — wrong. Real issue: CMake had cached wrong `Python_EXECUTABLE` path в `CMakeCache.txt`. Fix: pass explicit `-DPython_EXECUTABLE=/usr/bin/python3` к bypass CMake auto-detection.

### G2 — libSystem.Native.so missing for corerun

Built с `-s clr` only — produced libcoreclr.so but corerun couldn't load `libSystem.Native.so`. Fix: build `-s clr+libs+host` (default). `libs` сборка включает native interop layer.

### G3 — CMake cache reset обязателен при изменении option-conditional CMakeLists

CMake кэширует output predicates в `CMakeCache.txt`. Changes к conditional блокам в CMakeLists.txt не подхватываются на incremental build. **Workaround**: `rm artifacts/obj/coreclr/linux.x64.Debug/CMakeCache.txt` перед каждым изменением conditional кода.

### G4 — /mnt/c filesystem mtime через 9P не reliable

CMake checks file mtime для определения staleness. Changes от Windows-side Edit/Write tool (через 9P mount в WSL) могут не propagate'нуть mtime как WSL/cmake ожидает. **Workaround**: `touch <file>` from WSL side обязательно после редактирования с Windows side.

Обнаружили empirically: edited stubs.cpp, ran `cmake --build`, **никаких изменений** в output (libcoreclr.so timestamp не bumped). После `touch stubs.cpp` from WSL — rebuild triggered корректно.

### G5 — `--start-group/--end-group` для circular C deps

`minipal_get_*_random_bytes` из `libaotminipal.a` нужны `libSystem.Native.a` и наоборот. Без `-Wl,--start-group ... -Wl,--end-group` — ld один-проход, теряет symbols. Branch A test build script использует это; Stage 2 libcoreclr.so link автоматически (cmake handles).

### G6 — mscordbi `--undefined-version` LLD-specific flag

Linker flag `--undefined-version` поддерживается только LLD (since 16). На Ubuntu `/usr/bin/ld` — gnu bfd, не recognize. mscordbi библиотека использует это для версионирования exports — без нашего intervention build failed на этом. Fix: skip mscordbi через SHARPOS_PAL=ON conditional (Iter 3).

### G7 — mscordbi.so.dbg install missing (post-build symbol-stripping pipeline)

Stub'нутый mscordbi (наш `add_library(mscordbi SHARED stub.cpp)`) не проходит post-build pipeline который генерит `.dbg` файл (через `objcopy --only-keep-debug`). `install_clr` expectation требует `.dbg` файл — install fails. Fix: remove `install_clr(...)` call из SHARPOS_PAL=ON branch (Iter 5). Stub library само сидит в build dir, dependent targets могут link, install не нужен для линкера.

### G8 — singlefilehost target dependency hazard

Original conditional skip — `return()` early до создания target — сломал сторонние `add_dependencies(... singlefilehost)` calls в runtime. Fix: создать dummy executable named `singlefilehost` first, потом return.

### G9 — `tail -100` буферизует output на длинных pipes

User initially запустил build командой с `| tail -100`. tail держит buffer до EOF. На длинном build (10+ min) выглядит как hang. Fix: `| tee /tmp/sharpos-build.log` стримит + сохраняет.

### G10 — libcoreclr.so target builds в obj/, install копирует в bin/

`cmake --build ... --target coreclr` собирает stripped libcoreclr.so в `artifacts/obj/coreclr/linux.x64.Debug/dlls/mscoree/coreclr/`. Чтобы попасть в `artifacts/bin/coreclr/linux.x64.Debug/`, нужен install step или manual copy. Workflow during iter testing: `cp $OBJ/libcoreclr.so $BIN/libcoreclr.so` after each `cmake --build`.

### G11 — crossgen-corelib post-step expects working JIT

После libcoreclr.so install, build pipeline пытается AOT-precompile System.Private.CoreLib.dll в R2R image (через `crossgen-corelib.proj`). Loads `libclrjit_unix_x64_x64.so` (cross-targeting JIT), которая sequence trap'нется на `PAL_InitializeDLL`. **Не блокирует** spike — libcoreclr.so уже built, R2R precompile optional. Можно ignore failure.

---

## Lessons (raw, не synthesis)

1. **/mnt/c filesystem mtime через 9P не reliable.** `touch <file>` from WSL side обязательно после редактирования с Windows side иначе cmake decides nothing changed.

2. **Linker permissive по default for `-shared` libs.** `--allow-shlib-undefined` implicit. Symbols **могут** проскользнуть на link step и обнаружиться только при dlopen. Поэтому full undefined enumeration через `nm -D --undefined-only` **обязателен** до runtime testing.

3. **CoreCLR PAL surface ≠ "тонкий syscall shim".** ~144 functions w/ Win32 semantics + RESERVE/COMMIT bookkeeping (1845 LOC в `pal/src/map/virtual.cpp` alone). "cp + sed" подход (phase6-arch §15) был misleading.

4. **C++ runtime hard dependency.** 363 EX_TRY в vm/ + 28 PAL_TRY. `-fno-exceptions` нереально. libstdc++ link обязателен. NativeAOT runtime сам построен с `-fno-exceptions -nostdlib` и обходит это, но CoreCLR vm/ — нет.

5. **`extern "C" const` для GUIDs**. C++ default `const X` имеет internal linkage. Per-declaration `extern "C" const` обязателен чтобы COM identity GUIDs (IID_IUnknown, IID_IStream, GUID_NULL) появились как external symbols для DAC export tables.

6. **Eventing — ~200 functions, но reuse dummyprovider 100% covers.** Upstream `pal/src/eventprovider/dummyprovider/` генерит no-op stubs из ClrEtwAll.man. Manual work zero после правильного `add_subdirectory`.

7. **mscordbi + singlefilehost — host-toolchain dependencies, не runtime.** Skipping через cmake conditional работает. Без них runtime functional равно как и с ними.

8. **crossgen-corelib post-step ломается, но libcoreclr.so уже installed.** R2R precompile корелиба optional — runtime работает без него (JIT компилит at runtime). Для spike не блокирует.

9. **Trap diagnostics — single line per call, no aggregation, no callsite info.** Текущий `printf("called: %s") + abort()` дёшевый, но не масштабируется. Sage round 4 specced proper tracer (BSS ring buffer + phase tracking + caller address) — TODO для дальнейшего spike.

10. **First hot-path PAL = MultiByteToWideChar.** Это не iconv-coupled (sage 1+2 verified) — delegates через `minipal_convert_utf8_to_utf16` (~20 LOC wrapper достаточно). Mой spike concern что "первый trap = locale stuff = unfriendly" был неверным.

---

## Forward — что делать с этими данными

См. `done/phase2-sage-round4-synthesis.md`. Краткая суммаризация:

- Не продолжать ad-hoc trap-by-trap.
- Step 1: tracer infrastructure (preinit BSS ring buffer + phase markers).
- Step 2: replace abort() с policy stubs (LOG_AND_FAIL/FAKE/FORWARD/FATAL).
- Step 3: implement local leaf functions без host (UTF, IDs, time, sysinfo, CRT-safe).
- Step 4: collect trace, generate first-seen + per-phase + count report.
- Step 5: write trace-backed HOST API spec для observed surface (~30-60 funcs).
- Step 6: GO/NO-GO decision на Phase 6 commit.

Total estimate: 2-3 weeks (sage 2 bound). После — Phase 6 implementation на основе actual data.

---

## Pointers

- `done/step066.md` — checkpoint на link-step (что зафиксировано раньше)
- `done/phase2-sage-queries-round4.md` — questions заданные мудрецам после spike
- `done/phase2-sage-round4-synthesis.md` — answers + plan forward
- `done/phase6-architecture.md` — original architecture spec (нуждается в revision на основе actual data)
- `plan.md` Phase 2 / Phase 6 — высокоуровневый план
