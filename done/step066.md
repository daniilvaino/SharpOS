# Step 66 — Phase 2 spike checkpoint: libcoreclr.so links against pal/sharpos/

## Контекст

Phase 2 per `plan.md` — PAL design + Linux spike. Целевое measurement: **что
именно из CoreCLR PAL нужно** vm/gc/jit'у, чтобы libcoreclr.so собрался. Не
implementations — просто link surface. Дальше iteratively replace trap stubs
с реальными вызовами в `SharpOSHost_*` C-ABI primitives (host-tier C# code).

**Никаких изменений в SharpOS repo (OS/) не было** — все правки в
`dotnet-runtime-sharpos/` (форк CoreCLR, в .gitignore SharpOS репозитория).
Поэтому commit пустой; этот writeup и есть документация шага.

## Архитектурная установка (повторение для контекста)

Three-tier разделение, зафиксированное в `done/phase6-architecture.md`:

- **Guest tier** = форк CoreCLR в отдельной репе. C/C++ разрешён. Это PAL,
  vm/, gc/, jit/. Цель — «украсть всё что можно по максимуму», минимум diff
  vs upstream.
- **Host tier** = SharpOS repo, **строго C#** per Invariant 1. Это
  `OS/src/PAL/SharpOSHost/` — NativeAOT static archive, экспортирует ~30-50
  C-ABI функций (`SharpOSHost_VirtualAlloc`, `SharpOSHost_GetPageSize`,
  итд). Внутри использует SharpOS unikernel primitives.
- **C-ABI line** между tiers — POD types only (`uint32_t`, `void*`,
  status codes), function pointer tables, no exceptions, no STL types.

`pal/sharpos/` (новый каталог в форке CoreCLR) — drop-in замена `pal/src/`.
Выбирается через `-DSHARPOS_PAL=ON` cmake flag. В этом spike: только trap
stubs (`abort()` + diagnostic message). Iteration N+1: thin wrappers,
делегирующие в `SharpOSHost_*`.

## Что было сделано в этом spike

### Branch A — SharpOSHost.a smoke (предыдущая сессия, complete)

`OS/src/PAL/SharpOSHost/` — NativeAOT csproj с:
- `<NativeLib>Static</NativeLib>` (производит `.a`)
- `<RuntimeIdentifier>linux-x64</RuntimeIdentifier>`
- `<InvariantGlobalization>true</InvariantGlobalization>`
- `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`

4 smoke функции через `[UnmanagedCallersOnly(EntryPoint = ...)]`:
`SharpOSHost_GetVersion`, `SharpOSHost_GetPageSize`, `SharpOSHost_AddInts`,
`SharpOSHost_FillBuffer`. Слинкованы в C тестовую программу через
`-Wl,--start-group/--end-group` с NativeAOT runtime libs (libRuntime.WorkstationGC.a,
libbootstrapperdll.o, libstdc++compat.a, libaotminipal.a, libeventpipe-disabled.a,
libstandalonegc-disabled.a, libSystem.Native.a, libSystem.Globalization.Native.a,
libRuntime.VxsortDisabled.a). Все 4 функции callable из C — C-ABI line works.

**Результат Branch A:** validated что NativeAOT static archive integrate'ится
в native build pipeline. Runtime helpers всё ещё нужны (managed code не работает
без них), но это implementation detail — наш C# код выглядит как обычный
managed C#, plus `[UnmanagedCallersOnly]` exports.

### Branch B — libcoreclr.so links против pal/sharpos/ (этот spike)

Cmake-side switch (`dotnet-runtime-sharpos/src/coreclr/pal/CMakeLists.txt`):

```cmake
option(SHARPOS_PAL "Use SharpOS PAL backend (pal/sharpos/) instead of pal/src/" OFF)
if(SHARPOS_PAL)
  add_subdirectory(sharpos)
else()
  add_subdirectory(src)
endif()
```

`pal/sharpos/CMakeLists.txt` — defines `coreclrpal` STATIC target из двух
.cpp файлов:
- `stub.cpp` — пустой compilation unit (placeholder для targets типа
  `coreclrpal_dac`, `coreclrtraceptprovider`, `tracepointprovider`,
  `eventprovider`, которые vm/gc/dlls референсят но мы не реализуем).
- `stubs.cpp` — ~87 trap stubs покрывающие весь PAL surface, который
  vm/gc/jit reference'или при линковке. Каждая trap:

```cpp
[[noreturn]] static void sharpos_pal_trap(const char* name) {
    fprintf(stderr, "\n[sharpos-pal-trap] called: %s — not implemented\n", name);
    abort();
}
#define TRAP_VOID(name) void name(void) { sharpos_pal_trap(#name); }
// ... TRAP_PTR / TRAP_INT / TRAP_UINT / TRAP_ULONGLONG аналогично
TRAP_PTR(VirtualAlloc)
// ... ~87 раз
```

Diagnostic message с именем функции — это **the measurement**. Когда
runtime попытается запуститься, первый trap fire скажет нам какая PAL
функция первой нужна на hot path startup'a.

Реальные (не trap) implementations в этом spike — только две:
`GetLastError`/`SetLastError` поверх `thread_local unsigned int s_lastError`.
Без них всё трапается, потому что PAL внутренне calls `SetLastError(0)` на
каждом entry. Бесконечная рекурсия traps.

COM GUID константы (`IID_IUnknown`, `IID_IStream`, `IID_ISequentialStream`,
`GUID_NULL`) — declared as `extern "C" const sharpos_guid` (struct mirror
of GUID layout). C++ raw `const` имеет internal linkage по умолчанию;
explicit `extern "C" const` per-declaration делает symbol externally visible.
Иначе linker не находит при указании в DAC export tables.

### Stub-out auxiliary build targets

Две вещи которые `-DSHARPOS_PAL=ON` build-flow ломал:

**mscordbi** (`dlls/mscordbi/CMakeLists.txt`) — debugger interface DLL.
Использует `--undefined-version` LLD-specific linker flag, который
`/usr/bin/ld` (gnu bfd) на Ubuntu не поддерживает. Не нужен для spike.
Replaced с stub library:

```cmake
if(SHARPOS_PAL)
  message(STATUS "mscordbi: skipped (SHARPOS_PAL=ON)")
  add_library(mscordbi SHARED ${CMAKE_CURRENT_SOURCE_DIR}/../../pal/sharpos/stub.cpp)
  set_target_properties(mscordbi PROPERTIES LINKER_LANGUAGE CXX)
  return()
endif()
```

`install_clr` skipped — пытается install `.dbg` файл, который генерится
post-build symbol-stripping pipeline'ом. Stub этот pipeline не проходит,
.dbg не существует, install fails. Stub library само по себе достаточно,
чтобы dependent targets могли link.

**singlefilehost** (`src/native/corehost/apphost/static/CMakeLists.txt`) —
single-file deployment apphost, требует full PAL для embedded runtime.
Replaced с trivial executable:

```cmake
if(SHARPOS_PAL)
  set(SHARPOS_STUB_CPP "${CMAKE_CURRENT_BINARY_DIR}/sharpos_stub.cpp")
  file(WRITE "${SHARPOS_STUB_CPP}" "int main(int argc, char** argv) { return 0; }\n")
  add_executable(singlefilehost "${SHARPOS_STUB_CPP}")
  set_target_properties(singlefilehost PROPERTIES LINKER_LANGUAGE CXX)
  return()
endif()
```

`return()` early до создания target ломает сторонние `add_dependencies(...
singlefilehost)` calls. Создаём dummy executable named `singlefilehost`,
потом return.

## Результат

Build command:
```bash
./build.sh -c Debug -s clr+libs+host \
  -cmakeargs "-DSHARPOS_PAL=ON -DPython_EXECUTABLE=/usr/bin/python3"
```

После 11:21 elapsed:

```
-- Installing: artifacts/bin/coreclr/linux.x64.Debug/./libmscordaccore.so
-- Installing: artifacts/bin/coreclr/linux.x64.Debug/./libmscordaccore.so.dbg
-- Installing: artifacts/bin/coreclr/linux.x64.Debug/./libcoreclr.so
-- Installing: artifacts/bin/coreclr/linux.x64.Debug/./libcoreclr.so.dbg
-- Installing: artifacts/bin/coreclr/linux.x64.Debug/sharedFramework/libcoreclr.so
...
```

**`libcoreclr.so` слинкован и установлен** против `coreclrpal` target из
`pal/sharpos/`. ~87 trap stubs закрывают всю PAL surface, к которой vm/gc/jit
линкуются. **Главное измерение Phase 2 spike достигнуто.**

Падение build'a после: post-step `crossgen-corelib.proj` — пытается
AOT-precompile System.Private.CoreLib.dll в R2R native image для startup
optimization. Crossgen2 загружает `libclrjit_unix_x64_x64.so`, который
тоже слинкован против наших trap stubs → trap'ает на `PAL_InitializeDLL`
до того как fully load. **Expected**, не блокирует — R2R precompile
optional. Runtime работает без него (JIT компилит corelib at runtime,
просто медленнее startup). Откладываем.

## История измерений (PAL surface enumeration)

Iterative process — каждый round build'a выявлял очередную пачку undefined
references:

- Iter 1 (empty stubs.cpp): ~tousands undefined refs at libcoreclr.so link.
- Iter 2 (initial 41 traps): build progressed through libcoreclr.so link,
  failed at next dependent target.
- Iter 3 (+39 traps): прошли libcoreclr.so, failed at mscordbi (LLD flag).
- Iter 4 (+7 traps + GUID constants): mscordbi link succeeded, hit `--undefined-version`.
- Iter 5 (skip mscordbi via SHARPOS_PAL stub): hit singlefilehost.
- Iter 6 (skip singlefilehost): build complete до install phase.
- Iter 7 (mscordbi install_clr stripped): build complete до crossgen-corelib.
  **libcoreclr.so installed.** GOAL.

Full list trap'ов в `pal/sharpos/stubs.cpp` — categorized:
- Virtual memory: `VirtualAlloc/Free/Protect/Query`
- File I/O: `CreateFileA/W`, `Read/Write/CloseHandle`, mapping
- Module loading: `LoadLibraryExA/W`, `GetProcAddress`, `FreeLibrary`
- Process/thread: `GetCurrentProcessId/ThreadId`, `CreateProcessW`
- Sync: `CreateEventW`, `WaitForSingleObjectEx`, semaphores
- Charset: `MultiByteToWideChar`, `WideCharToMultiByte`, wide-string compare
- Path/Module: `GetModuleFileName`, `GetFullPathName`, `GetTempPath`
- System: `GetSystemInfo`, `GetSystemTime`, `GetSystemTimeAsFileTime`
- Format: `_snprintf_s`, `sprintf_s`, `FormatMessageW`, `OutputDebugStringA`
- Exception/debug: `RaiseException`, `RaiseFailFastException`, `DebugBreak`
- PAL-specific: `PAL_Initialize`, `PAL_GetCurrentThread`, `PAL_GetTotalCpuCount`, `PAL_VirtualReserve...`
- COM constants: `IID_IUnknown`, `IID_IStream`, `IID_ISequentialStream`, `GUID_NULL`

Это enumerated startup-time surface. Hot path measurement (что **первым**
вызывается) — следующий шаг через corerun + Hello.dll.

## Что узнали (lessons)

### CoreCLR PAL surface больше чем кажется на первый взгляд

CoreCLR maintainers говорят что PAL ~30-50 функций. В реальности линковочная
surface ~87 (плюс 4 GUID константы). Разница — это transitive dependencies:
charset routines (`MultiByteToWideChar`), string-safe variants (`_snprintf_s`,
`memcpy_s`), thread-local error state (`GetLastError`/`SetLastError`),
COM-style identity (GUIDs). Это **link-time** count; runtime hot path может
быть гораздо уже (наша гипотеза — ~10-15 функций).

### Build infrastructure tightly coupled

cross-targeting JIT (`libclrjit_unix_x64_x64.so`) сам собирается с PAL.
crossgen2 загружает его, чтобы AOT-precompile corelib. С SHARPOS_PAL=ON
эта цепочка ломается потому что host-side toolchain тоже использует наш
trap PAL. Чистое решение — собирать host-side tooling с pal/src/, target-side
runtime с pal/sharpos/. Это нетривиальный CMake split. Откладываем — для
spike достаточно libcoreclr.so.

### Stub vs skip via cmake conditional

cmake `if(SHARPOS_PAL) return() endif()` early — natural pattern для
"feature switch". Но если cmake target создаётся **после** return,
`add_dependencies(... target_name)` calls в других местах ломаются.
Workaround: создать минимальный stub target с тем же именем, потом return.
Использовали для mscordbi и singlefilehost.

### `extern "C" const` для GUIDs

C++ дефолт для top-level `const X`-объектов — internal linkage. Linker
не видит их как external symbols. PAL DAC export tables expect their
GUID symbols externally. Решение: **per-declaration** `extern "C" const X`
prefix. Не достаточно `extern "C" {...}` block — то даёт extern linkage
по C-нотации, но `const` всё равно overrides обратно к internal в C++.

### Cmake cache reset обязателен при изменении option-conditional CMakeLists

CMake кэширует output predicate'ов в `CMakeCache.txt`. Изменение
`SHARPOS_PAL=ON` кода в условном блоке не подцепляется на incremental
build — cmake не reprocesses CMakeLists для targets, чьи output property
он уже кэшировал. `rm CMakeCache.txt` перед каждым изменением условного
кода обязателен.

### `tail -100` буферизует output на длинных pipes

`./build.sh ... 2>&1 | tail -100` — не show progress live. tail держит
буфер до EOF. Replaced с `| tee /tmp/sharpos-build.log` — стримит +
сохраняет.

## Что в SharpOS repo (OS/) не менялось

Никаких файлов. Spike целиком в guest tier (форк CoreCLR). Поэтому commit
пустой; гитигнор содержит `dotnet-runtime-sharpos/`.

Host tier (`OS/src/PAL/SharpOSHost/`) был добавлен в Branch A раньше — там
4 smoke функции, но **не подключён** к pal/sharpos/. В этом spike pal/sharpos/
не линкуется к SharpOSHost.a — сначала измеряем PAL surface через trap stubs,
потом подключим SharpOSHost для real implementations.

## Следующий шаг

Hello.dll через corerun + libcoreclr.so → **первый PAL trap fire**.

```bash
mkdir /tmp/hello && cd /tmp/hello
echo 'class P { static void Main() { System.Console.WriteLine("hi"); } }' > P.cs
dotnet build -c Debug
ARTIFACTS=/mnt/c/work/OS/dotnet-runtime-sharpos/artifacts/bin/coreclr/linux.x64.Debug
LD_LIBRARY_PATH=$ARTIFACTS $ARTIFACTS/corerun bin/Debug/*/P.dll
```

Expected output:
```
[sharpos-pal-trap] called: <FunctionName> — not implemented
Aborted (core dumped)
```

`<FunctionName>` — first PAL function on runtime startup hot path.
Это и есть answer к question "что нужно сначала". Iteratively replace
trap'ы с thin wrappers calling SharpOSHost_*, repeat.

Goal Phase 2 spike (per `plan.md`) — managed Hello World runs end-to-end
через CoreCLR + naши PAL implementation backed by SharpOSHost_* primitives.
До этого может потребоваться ~10-15 итераций (по гипотезе).

## Файлы (всё в `dotnet-runtime-sharpos/` — gitignored)

### Новые

- `src/coreclr/pal/sharpos/CMakeLists.txt` — pal/sharpos/ skeleton.
- `src/coreclr/pal/sharpos/stub.cpp` — empty compilation unit.
- `src/coreclr/pal/sharpos/stubs.cpp` — 87 trap stubs + GUIDs + GetLastError.

### Изменённые

- `src/coreclr/pal/CMakeLists.txt` — `SHARPOS_PAL` option + conditional add_subdirectory.
- `src/coreclr/dlls/mscordbi/CMakeLists.txt` — stub target если SHARPOS_PAL=ON, install skipped.
- `src/native/corehost/apphost/static/CMakeLists.txt` — stub executable если SHARPOS_PAL=ON.

### В SharpOS repo (OS/)

- `done/step066.md` — этот файл.
- (`.gitignore` уже содержит `dotnet-runtime-sharpos/` с прошлого commit'a.)
