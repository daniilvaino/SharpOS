### Решение

**TARGET_SHARPOS — третий target в CoreCLR build system, отличный от TARGET_WINDOWS/TARGET_UNIX/TARGET_WASM.**

Использует PAL pattern (как TARGET_UNIX), но НЕ наследует Unix substrate. Та же конфигурация работает на Windows-hosted spike и на bare metal kernel image.

### CMake structure

cmake

```cmake
if (CLR_CMAKE_HOST_UNIX)
    # Standard Unix path — pthread, dlopen, libunwind, signals
elseif (CLR_CMAKE_TARGET_SHARPOS)
    # SharpOS path:
    # - Use PAL pattern (vm/ calls Win32-shape APIs through pal/sharpos/)
    # - NO Unix substrate inheritance
    # - NO signals, pthread, dlopen, libunwind, .eh_frame, ELF assumptions
    # - Static linking enforced (FEATURE_STATICALLY_LINKED=1)
    # - Custom narrow conditionals where needed (TARGET_WASM-style narrow patches)
elseif (WIN32)
    # Standard Windows path — direct Win32 API
endif()
```

### Что не наследуется от TARGET_UNIX

- POSIX signals → нет на bare metal
- pthread → CreateThread = ABORT_FATAL stub в Phase 2/6.1 (per D5)
- dlopen → static linking only (per D10)
- libunwind / .eh_frame → .pdata canonical (per D13)
- ELF assumptions → kernel image format может отличаться
- Linux-specific filesystem semantics → kernel-backed filesystem

### Что используется от TARGET_UNIX pattern

- PAL pattern (vm/ → Win32-shape APIs → pal/sharpos/ → SharpOSHost)
- FEATURE_STATICALLY_LINKED activation
- coreclr_static target вместо coreclr SHARED

### Patches required в upstream

**Минимальные additive patches**:

1. `clrfeatures.cmake:5-7` — расширение условия для FEATURE_STATICALLY_LINKED:

cmake

```cmake
if (CLR_CMAKE_TARGET_ARCH_WASM 
    OR CLR_CMAKE_TARGET_MACCATALYST 
    OR CLR_CMAKE_TARGET_IOS 
    OR CLR_CMAKE_TARGET_TVOS
    OR CLR_CMAKE_TARGET_SHARPOS)   # ← наше дополнение
  set(FEATURE_STATICALLY_LINKED 1)
endif()
```

2. `vm/ceemain.cpp:934` — TARGET_SHARPOS conditional skip finalizer thread (per D5):

cpp

```cpp
#if !defined(TARGET_WINDOWS) && !defined(TARGET_WASM) && !defined(TARGET_SHARPOS)
    FinalizerThread::FinalizerThreadCreate();
#else
    FinalizerThread::EnableFinalization();
#endif
```

3. `pal/src/exception/seh-unwind.cpp` — TARGET_SHARPOS branch на virtual unwind boundary (per D13). Узкое изменение, не rewrite.

4. Новая директория `pal/sharpos/` — additive только, никаких изменений в `pal/src/`.

**Initialization order — open risk, не upstream patch**:

Старая редакция включала patch для `nativeaot/Bootstrap/main.cpp` с `__attribute__((constructor(101)))` для high-priority initialization. Этот patch был частью **старой** D10 mental model где SharpOSHost это отдельная NativeAOT static library со своими static initializers что race'ились с CoreCLR.

Per revised D10/D11 — отдельной NativeAOT static library нет. Phase 2 provider это C++ shim без NativeAOT initialization quirks. Patch удалён.

Initialization order остаётся **open risk** что measured при первой реальной сборке:
- **Phase 2 Windows spike**: должен работать out-of-the-box (нет NativeAOT static lib в build)
- **Phase 6 bare metal**: kernel image NativeAOT-compiled с CoreCLR static archive — initialization order measured при Phase 6 link dry-run gate (per revised D10). Если issue появится — solved kernel-side, не CoreCLR-side. Mechanism (priority constructor / init segments / explicit init function) decided based on actual symptom.

### Build steps

**Phase 1: HOST_WIN32 + TARGET_SHARPOS + !TARGET_WIN32 configure proof gate** (PRIMARY FIRST GATE):

Прежде чем строить Hello World — verify что CMake configure проходит при правильном axis split:

```cmd
build.cmd -subset clr -configuration Debug -configureonly ^
  -cmakeargs "-DCLR_CMAKE_TARGET_SHARPOS=1 -DFEATURE_STATICALLY_LINKED=1"
```

Acceptance:
- Configure completes без errors
- `coreclrpal` library добавлена (поскольку target != TARGET_WIN32)
- Windows system libs **не** включены автоматически (CoreCLR не считает себя на Windows target)
- `unwinder_wks` присутствует в build (manual linkage если CLR_CMAKE_HOST_UNIX false)

Если configure ломается — нужно разделять оси "Windows toolchain/COFF" (HOST_WIN32) и "SharpOS CoreCLR target behavior" (TARGET_SHARPOS, !TARGET_WIN32). Это может потребовать дополнительные CMake patches что **не** входят в текущие 4 patches.

**Phase 2: CoreCLR fork build**:

```cmd
build.cmd -subset clr -configuration Debug ^
  -cmakeargs "-DCLR_CMAKE_TARGET_SHARPOS=1 -DFEATURE_STATICALLY_LINKED=1"
```

Output: `coreclr_sharpos_static.lib` (per revised D10), включающий pal/sharpos/ + clrjit + unwinder_wks.

**Phase 3: Windows shim build** (C++ provider, per revised D10/D11):

Отдельный CMake mini-project для `sharpos_host_windows_shim`:

```cmd
cmake -B build-shim -S sharpos_host_windows_shim
cmake --build build-shim --config Debug
```

Output: `sharpos_host_windows_shim.lib` — C/C++ implementations of SharpOSHost_* exports через Win32 APIs.

**NO dotnet publish SharpOSHost.csproj.** Per revised D10/D11 — отдельная NativeAOT static library НЕ создаётся.

**Phase 4: Final spike-host link**:

```cmd
link.exe spike-host.obj ^
  coreclr_sharpos_static.lib ^
  sharpos_host_windows_shim.lib ^
  /SUBSYSTEM:CONSOLE ^
  /OUT:spike-host.exe
```

System libs подтянутся через DEFAULTLIB директивы из embedded `.obj` файлов. Audit обязателен (per Phase 2 Redesign + D10 audit gates).

**На bare metal**: те же команды на cross-compile toolchain, target = SharpOS kernel image format. Provider это bare-metal shim/glue/kernel-provided symbols (per revised D11), не Windows shim.

### Связь с другими decisions

- **D5** (CreateThread = ABORT_FATAL): TARGET_SHARPOS conditional в ceemain.cpp:934
- **D9** (структура pal/sharpos/): new directory, additive только
- **D10 revised** (CoreCLR guest archive): FEATURE_STATICALLY_LINKED enforced for TARGET_SHARPOS, generates `coreclr_sharpos_static.lib`. Initialization order — Phase 6 link dry-run gate concern, не TARGET_SHARPOS upstream patch.
- **D11 revised** (SharpOSHost_* как ABI namespace): standard linker resolution через статическую линковку. Provider environment-specific.
- **D13** (.pdata canonical): TARGET_SHARPOS branch в pal/src/exception/seh-unwind.cpp. `unwinder_wks` linked manually для D13 portable amd64 unwinder.

### Принцип установленный

(в дополнение к D1-D13 + Phase 2 Redesign)

37. **Custom target — не Unix не Windows.** Когда production environment отличается от существующих CoreCLR targets — создаём свой target вместо wholesale наследования. Похищаем pattern (PAL), не substrate (POSIX).