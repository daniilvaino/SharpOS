# D10 — REVISED

## Статус

**Supersedes old D10.**

Старый D10 правильно зафиксировал главный принцип — **статическая линковка всего в один конечный артефакт** и отказ от `dlopen` / dynamic runtime loading. Но старый текст ошибочно тащил в critical path отдельную NativeAOT static library `libsharposhost.a` / `SharpOSHost.lib`.

Новая версия D10 убирает эту предпосылку.

---

## Решение

**CoreCLR + `pal/sharpos/` собирается как guest-runtime static archive и линкуется внутрь финального SharpOS image.**

Не создаём отдельную NativeAOT static library `SharpOSHost.lib`. NativeAOT нужен для компиляции самого SharpOS kernel, но не как boundary mechanism между CoreCLR PAL и host.

### Финальная архитектура

```text
SharpOS kernel C# code
  └─ compiled by NativeAOT into kernel native objects / final image

CoreCLR fork
  └─ builds guest-runtime static archive:
       coreclr_sharpos_static.lib / .a
         - CoreCLR VM
         - CoreCLR GC
         - RyuJIT
         - pal/sharpos/
         - optional C/C++ shim / glue

Final SharpOS image link:
  SharpOS NativeAOT kernel objects/libs
  + coreclr_sharpos_static.lib / .a
  + sharpos_host_baremetal_shim.obj / .o, if needed
  → one final SharpOS kernel image
```

### Phase 2 Windows-hosted spike architecture

```text
spike-host.exe
  = spike-host.obj
  + coreclr_sharpos_static.lib
  + sharpos_host_windows_shim.lib
  + explicitly classified Windows/system/runtime libs
```

Windows is a **temporary measurement host**, not the production substrate.

---

## What D10 explicitly rejects

### Rejected: separate NativeAOT host static library

```text
dotnet publish SharpOSHost.csproj → SharpOSHost.lib
CoreCLR static lib + SharpOSHost.lib + runner → executable
```

This is no longer the D10 model.

Reason:

- It creates an unnecessary NativeAOT static-library consumption problem.
- It introduces static initialization, CRT, NativeAOT runtime closure, `/INCLUDE` and duplicate-runtime issues that do not answer the CoreCLR/PAL question.
- It conflates “SharpOS kernel is NativeAOT” with “host API provider must be a NativeAOT static library.” The second does not follow from the first.

### Rejected: stock Windows CoreCLR build as Phase 2 proof

A normal Windows CoreCLR build may bypass `pal/sharpos/` and call Windows paths directly. That does not measure SharpOS PAL demand surface.

### Rejected: TARGET_SHARPOS as TARGET_WIN32

`TARGET_SHARPOS` must not fall into the stock `CLR_CMAKE_TARGET_WIN32` path, because that path pulls Windows system libraries and can bypass `coreclrpal`.

### Rejected: TARGET_SHARPOS as generic TARGET_UNIX

`TARGET_SHARPOS` must not inherit Unix substrate wholesale: no accidental `pthread`, `dlopen`, `libunwind`, `.eh_frame`, POSIX signals as the architectural path.

---

## Core principle

```text
CoreCLR guest runtime is a static archive.
SharpOS final link owns the final image.
Host ABI symbols are resolved by the linker.
```

This preserves the existing D10 principle:

```text
No dlopen.
No separate runtime-loaded CoreCLR.
No runtime API table injection.
No dynamic host discovery.
```

---

## Artifacts

### 1. CoreCLR guest archive

Name examples:

```text
Windows spike:
  coreclr_sharpos_static.lib

Bare metal / ELF toolchain:
  libcoreclr_sharpos_static.a
```

Contains:

```text
- CoreCLR vm/
- CoreCLR gc/
- CoreCLR jit/
- pal/sharpos/
- TARGET_SHARPOS-specific glue
- D13 portable unwinder dependency, if needed
```

Does **not** contain:

```text
- SharpOS kernel implementation
- separate SharpOSHost NativeAOT static library
- stock Windows PAL
- POSIX emulation layer
```

### 2. Windows host shim

Name example:

```text
sharpos_host_windows_shim.lib
```

Provides the same symbols that the real SharpOS image will provide later:

```text
SharpOSHost_AllocPages
SharpOSHost_FreePages
SharpOSHost_WriteConsole
SharpOSHost_GetTime
...
```

Implementation is allowed to use Win32 APIs because it is the temporary Windows-backed host side for the spike.

### 3. Bare-metal host shim / glue

Name example:

```text
sharpos_host_baremetal_shim.o
```

Provides the same `SharpOSHost_*` symbols, but routes them into SharpOS kernel internal ABI / exported kernel functions / generated glue.

This is not a separate NativeAOT static library. It is part of the final kernel image link.

---

## Windows-hosted Phase 2 build pipeline

### Stage 1 — build CoreCLR guest archive

Command shape:

```cmd
build.cmd ^
  -subset clr ^
  -configuration Debug ^
  -arch x64 ^
  -cmakeargs "-DCLR_CMAKE_TARGET_SHARPOS=1 -DFEATURE_STATICALLY_LINKED=1"
```

Then explicitly build or verify the static target:

```cmd
cmake --build <coreclr-obj-dir> --target coreclr_static --config Debug
```

Expected output:

```text
coreclr_sharpos_static.lib
```

The exact path is determined by the CoreCLR build output directory.

### Required CMake properties for TARGET_SHARPOS

```text
HOST_WIN32 = true, because the build runs on Windows.
TARGET_SHARPOS = true.
TARGET_WIN32 = false for CoreCLR target behavior.
TARGET_UNIX = false or narrow/custom, not wholesale Unix.
FEATURE_STATICALLY_LINKED = true.
coreclrpal resolves to pal/sharpos/.
unwinder_wks is explicitly linked for D13 if the portable .pdata unwinder is used.
```

Important: `unwinder_wks` is not automatically linked on Windows host in the stock Unix condition. `TARGET_SHARPOS` must explicitly pull it if D13 uses it.

### Stage 2 — build Windows host shim

Example:

```cmd
cl.exe /nologo /c /Zi /Od ^
  /I path\to\pal\sharpos\include ^
  sharpos_host_windows_shim.cpp ^
  /Fo:artifacts\sharpos_host_windows_shim.obj

lib.exe /nologo ^
  /OUT:artifacts\sharpos_host_windows_shim.lib ^
  artifacts\sharpos_host_windows_shim.obj
```

This shim is temporary spike infrastructure. It may use Win32 APIs.

It must export C ABI names exactly matching `sharpos_host_api.h`.

### Stage 3 — compile runner

```cmd
cl.exe /nologo /c /Zi /Od ^
  /I path\to\coreclr\inc ^
  spike-host.c ^
  /Fo:artifacts\spike-host.obj
```

### Stage 4 — final Windows spike link

Use a response file.

`spike-host.rsp`:

```text
/OUT:artifacts\spike-host.exe
/MAP:artifacts\spike-host.map
/MAPINFO:EXPORTS
/VERBOSE:LIB
/VERBOSE:REF
/INCREMENTAL:NO
/DEBUG:FULL

artifacts\spike-host.obj
artifacts\sharpos_host_windows_shim.lib
path\to\coreclr_sharpos_static.lib

kernel32.lib
ucrt.lib
vcruntime.lib
libvcruntime.lib
libcmt.lib
oldnames.lib
bcrypt.lib
uuid.lib
version.lib
```

Run:

```cmd
link.exe @spike-host.rsp > artifacts\link.verbose.txt 2>&1
```

The system lib list is not final. It is an initial allowlist and must be driven by actual unresolved symbols and audit output.

---

## Phase 6 SharpOS final image link

Phase 6 does **not** build a separate `SharpOSHost.lib`.

Instead, the existing SharpOS NativeAOT kernel build adds the CoreCLR guest archive to the final image link.

Possible integration shapes:

```xml
<ItemGroup>
  <NativeLibrary Include="path\to\coreclr_sharpos_static.lib" />
</ItemGroup>
```

or, when linker ordering / forced retention is needed:

```xml
<ItemGroup>
  <LinkerArg Include="/WHOLEARCHIVE:path\to\coreclr_sharpos_static.lib" />
</ItemGroup>
```

The exact mechanism must be decided after inspecting the generated NativeAOT linker response file for the current SharpOS kernel build.

### Phase 6 link dry-run gate

Before trying to boot CoreCLR, run a link-only / dry integration gate:

```text
Goal:
  prove that SharpOS final NativeAOT link can consume coreclr_sharpos_static archive.

Not goal:
  run PowerShell
  initialize CoreCLR
  pass managed Hello World
```

Gate output:

```text
- generated linker response file
- final linker stdout/stderr
- duplicate-symbol report
- unresolved-symbol report
- map file if available
```

---

## Static archive order

### Windows / COFF

`link.exe` is less order-sensitive than ELF `ld`, but order still matters when:

```text
- duplicate symbols exist
- /DEFAULTLIB directives conflict
- /WHOLEARCHIVE is used
- /INCLUDE is used
- COMDAT folding / /OPT:REF removes unused objects
```

Required audit:

```cmd
link.exe @spike-host.rsp > artifacts\link.verbose.txt 2>&1
```

### Bare metal / SharpOS

The generated NativeAOT linker response file is the source of truth.

Required audit:

```text
Save generated link.rsp.
Verify coreclr_sharpos_static archive is present.
Verify host shim/glue objects are present.
Verify no SharpOSHost_* symbols remain unresolved.
```

---

## Whole-archive / force-include policy

Default: **do not** `/WHOLEARCHIVE` the entire CoreCLR archive.

Start with explicit entrypoint retention.

Examples:

```cmd
/INCLUDE:coreclr_initialize
/INCLUDE:coreclr_execute_assembly
/INCLUDE:coreclr_shutdown
```

Exact decorated names must be verified with:

```cmd
dumpbin /LINKERMEMBER:1 coreclr_sharpos_static.lib > artifacts\coreclr.members.txt
dumpbin /SYMBOLS coreclr_sharpos_static.lib > artifacts\coreclr.symbols.txt
findstr /i "coreclr_initialize coreclr_execute_assembly coreclr_shutdown" artifacts\coreclr.symbols.txt
```

Escalation policy:

```text
1. Use /INCLUDE for known hosting entrypoints.
2. If specific sub-libraries are stripped, use /WHOLEARCHIVE only for those specific libs.
3. Use /WHOLEARCHIVE for the whole CoreCLR archive only as a temporary diagnostic step.
```

---

## Duplicate symbol policy

Duplicate symbols are expected risk because SharpOS already provides low-level runtime helpers and CoreCLR brings its own native runtime dependencies.

Potential conflict classes:

```text
memcpy / memset / memmove
operator new / operator delete
C++ EH helpers
RTTI / type_info
Rhp* NativeAOT runtime exports
GC-related symbols
minipal symbols
eventpipe / diagnostic globals
COM / GUID symbols
```

Policy:

```text
Any duplicate symbol is a blocker until classified.
Do not use /FORCE:MULTIPLE as default policy.
```

Allowed only after explicit classification:

```text
- COMDAT identical duplicate proven harmless
- deliberately selected single definition
- temporary diagnostic link with documented reason
```

Audit:

```cmd
findstr /i "LNK2005 LNK4006 already defined multiply defined" artifacts\link.verbose.txt
```

---

## CRT / libc / C++ EH dependency policy

Windows-hosted spike may use MSVC CRT/VCRuntime as host convenience.

Bare metal cannot.

Therefore Windows spike does not solve final C++ runtime substrate; it only reports it.

Windows audit:

```cmd
dumpbin /DIRECTIVES coreclr_sharpos_static.lib > artifacts\coreclr.directives.txt
dumpbin /SYMBOLS coreclr_sharpos_static.lib > artifacts\coreclr.symbols.txt

findstr /i "defaultlib CxxFrameHandler type_info operator new operator delete" ^
  artifacts\coreclr.directives.txt artifacts\coreclr.symbols.txt
```

ELF/bare-metal audit later:

```bash
nm -uC libcoreclr_sharpos_static.a \
  | grep -E 'std::|__cxa|__gxx|typeinfo|vtable|operator new|operator delete|_Unwind|memcpy|memset|malloc|free' \
  | sort -u
```

Policy:

```text
No silent CRT conflict.
Any LNK4098 is a blocker until classified.
Use /VERBOSE:LIB to find source.
Use targeted /NODEFAULTLIB only after classification.
```

---

## System library allowlist for Windows spike

**Principle**: start with **minimum explicit list**, expand only after unresolved-symbol evidence + `/VERBOSE:LIB` + `dumpbin /DIRECTIVES`. Иначе audit становится "мы сами разрешили слишком много".

### Tier A — initial explicit (start here)

```text
kernel32.lib    — Win32 base APIs (VirtualAlloc, CreateFile, GetLastError, etc.)
```

Only this. Everything else added by evidence, не upfront.

### Tier A-extended — typically required, add when needed

These will likely be needed for spike. Add **only** когда `/VERBOSE:LIB` или unresolved symbol error показывает explicit dependency:

```text
ucrt.lib OR libcmt.lib  — CRT (одна из, не обе — CRT conflict)
oldnames.lib            — POSIX aliases
bcrypt.lib              — likely needed для NativeAOT runtime RNG
uuid.lib                — small, often pulled by COM headers
version.lib             — file version info
```

**CRT decision**: `ucrt.lib` (dynamic) vs `libcmt.lib` (static). NativeAOT Windows targets force `ucrt`. CoreCLR static использует `libcmt`. **Conflict expected** — resolve через `/NODEFAULTLIB` директивы based on actual link errors. Документировать решение в первом working build.

`vcruntime.lib + libvcruntime.lib` together — **NOT both**. Это `LNK4098` conflict. Pick one based on chosen CRT model.

### Tier B — allowed only with explicit symbol reason

```text
advapi32.lib
normaliz.lib
crypt32.lib
ncrypt.lib
secur32.lib
ole32.lib
oleaut32.lib
shlwapi.lib
delayimp.lib
```

### Tier C — forbidden for Hello/JIT spike unless reopened

```text
user32.lib
ws2_32.lib
mswsock.lib
iphlpapi.lib
RuntimeObject.lib
```

### Special: ntdll.lib

`ntdll.lib` is not globally forbidden by name.

Forbidden outside diagnostic builds:

```text
RtlVirtualUnwind
RtlLookupFunctionEntry
RtlAddFunctionTable
RtlDeleteFunctionTable
RtlInstallFunctionTableCallback
```

Allowed only under explicit diagnostic comparison flag:

```text
SHARPOS_DIAGNOSTIC_COMPARE_WITH_WINDOWS_UNWINDER
```

D13 production-shaped path must use SharpOS / portable .pdata unwinder, not Windows `RtlVirtualUnwind`.

### Expansion process

1. Attempt link с `Tier A` only (`kernel32.lib`)
2. Capture unresolved symbol errors
3. Run `dumpbin /DIRECTIVES` on `coreclr_sharpos_static.lib` и `sharpos_host_windows_shim.lib` чтобы видеть embedded `DEFAULTLIB` directives
4. Add libraries из Tier A-extended **with reason** (which symbol, which library resolves)
5. If Tier B library needed — document explicit reason (which CoreCLR feature pulls it)
6. Tier C library needed — reopen decision (might indicate D5/D8 scope creep)
7. Final `Tier A-final` list documented в первом working build's notes

---

## Link audit commands

### Final imports

```cmd
dumpbin /DEPENDENTS artifacts\spike-host.exe > artifacts\dependents.txt
dumpbin /IMPORTS artifacts\spike-host.exe > artifacts\imports.txt
```

### Default library directives

```cmd
dumpbin /DIRECTIVES path\to\coreclr_sharpos_static.lib > artifacts\coreclr.directives.txt
dumpbin /DIRECTIVES artifacts\sharpos_host_windows_shim.lib > artifacts\shim.directives.txt
dumpbin /DIRECTIVES artifacts\spike-host.obj > artifacts\spike-host.directives.txt
```

### Symbol origins

```cmd
dumpbin /SYMBOLS path\to\coreclr_sharpos_static.lib > artifacts\coreclr.symbols.txt
dumpbin /SYMBOLS artifacts\sharpos_host_windows_shim.lib > artifacts\shim.symbols.txt
dumpbin /LINKERMEMBER:1 path\to\coreclr_sharpos_static.lib > artifacts\coreclr.members.txt
```

### Forbidden import checks

```cmd
findstr /i "RtlVirtualUnwind RtlLookupFunctionEntry RtlAddFunctionTable RtlDeleteFunctionTable RtlInstallFunctionTableCallback" artifacts\imports.txt
findstr /i "user32.dll ws2_32.dll mswsock.dll iphlpapi.dll runtimeobject.dll" artifacts\dependents.txt
findstr /i "CreateThread _beginthreadex CreateThreadpool SubmitThreadpool" artifacts\imports.txt
```

### Duplicate / CRT conflict checks

```cmd
findstr /i "LNK2005 LNK4006 LNK4098 already defined multiply defined defaultlib conflict" artifacts\link.verbose.txt
```

---

## Acceptance criteria for Windows-hosted Phase 2 link

PASS only if:

```text
1. coreclr_sharpos_static.lib links into spike-host.exe.
2. pal/sharpos/ symbols are present and retained.
3. sharpos_host_windows_shim.lib provides all required SharpOSHost_* symbols.
4. No unresolved SharpOSHost_* symbols remain.
5. No forbidden imports are present.
6. RtlVirtualUnwind / RtlLookupFunctionEntry are absent outside diagnostic build.
7. No unclassified duplicate symbols.
8. No unclassified CRT/defaultlib conflicts.
9. link.verbose.txt and spike-host.map are saved as artifacts.
10. C++ static initialization audit: .CRT$XCU / init sections from CoreCLR archive 
    are accounted for. Windows CRT startup runs them automatically — verify no init 
    failures. (See "C++ static initialization audit" gate below.)
11. .pdata/.xdata retention audit: CoreCLR RUNTIME_FUNCTION records are present in 
    final image exception directory. (See ".pdata/.xdata retention audit" gate below.)
```

FAIL if:

```text
- stock Windows CoreCLR path bypasses pal/sharpos/
- TARGET_SHARPOS falls into TARGET_WIN32 behavior
- Windows unwind APIs become retained production imports
- CreateThread appears before D5 is reopened
- unknown system library appears without classification
- C++ static initializers from CoreCLR archive не запустились (init failure symptom)
- CoreCLR .pdata records lost during link (managed EH через CoreCLR код не работает)
```

### C++ static initialization audit (NEW gate)

Когда CoreCLR archive статически линкуется — CoreCLR's C++ objects содержат **static initializers** (`.CRT$XCU` секции на MSVC, init_array на ELF). Кто-то должен их запустить **до** `coreclr_initialize()`.

**Windows runner (Phase 2)**: CRT startup автоматически запускает все static constructors при `main()` entry. Должно работать out-of-the-box. Но **verify через**:

```cmd
dumpbin /SECTIONS coreclr_sharpos_static.lib | findstr /i "CRT\$XCU CRT\$XCA"
dumpbin /SECTIONS spike-host.exe | findstr /i "CRT\$XCU CRT\$XCA"
```

PASS: `.CRT$XCU` секции из CoreCLR archive присутствуют в final exe sections (CRT startup найдёт и запустит).

FAIL: секции потеряны при линковке (CRT не сможет вызвать initializers, CoreCLR может падать в init).

**Bare metal (Phase 6)**: NativeAOT entrypoint **может не запускать** `.CRT$XCU` секции из CoreCLR archive автоматически. Это **open risk** что measured при Phase 6 link dry-run gate. Если init failures появятся — solved kernel-side (вызывать `__scrt_initialize_crt` equivalent перед `coreclr_initialize`).

### .pdata/.xdata retention audit (NEW gate)

CoreCLR's native C++ code эмитит `.pdata` (RUNTIME_FUNCTION records) и `.xdata` (UNWIND_INFO) для всех его функций. Эти должны попасть в **final image exception directory** чтобы наш unwinder (Phase 1 StackFrameIterator) их нашёл через `CoffRuntimeFunctionTable`.

**Verify через**:

```cmd
dumpbin /HEADERS spike-host.exe | findstr "Exception Table"
dumpbin /UNWINDINFO spike-host.exe > unwind_records.txt
wc -l unwind_records.txt
```

PASS:
- Exception Table (`.pdata` directory) присутствует в final exe headers
- CoreCLR RUNTIME_FUNCTION records видны в unwind_records.txt
- Counts reasonable (CoreCLR has thousands of functions — expect thousands of records)

FAIL:
- Exception Table empty или missing
- CoreCLR records отсутствуют (linker dropped them)
- Symptom: managed EH через CoreCLR код не работает, hard crash вместо exception propagation

**Bare metal (Phase 6)**: same audit на final SharpOS kernel image. Plus verify что `CoffRuntimeFunctionTable` может iterate CoreCLR ranges (integration test).

---

## Acceptance criteria for Phase 6 link dry-run

PASS only if:

```text
1. SharpOS NativeAOT final link includes coreclr_sharpos_static archive.
2. Host shim/glue symbols are present.
3. No SharpOSHost_* unresolved symbols remain.
4. Duplicate symbols are zero or classified.
5. CoreCLR entrypoints are retained.
6. No accidental Windows/Unix hosted dependencies are present.
```

---

## Updated open risks

These remain real and must be measured:

```text
1. Exact TARGET_SHARPOS CMake patch set.
2. Whether coreclr_static builds cleanly on Windows host with TARGET_SHARPOS.
3. Whether TARGET_SHARPOS can use coreclrpal without inheriting TARGET_UNIX.
4. Whether unwinder_wks must be linked manually for D13.
5. Whether CoreCLR archive can be consumed by the SharpOS NativeAOT final image link.
6. Duplicate symbols between SharpOS NativeAOT runtime helpers and CoreCLR/native deps.
7. CoreCLR C++ runtime / EH ABI requirements on bare metal.
8. Exact /INCLUDE or /WHOLEARCHIVE requirements.
```

---

## Relationship to other decisions

### D9

D10 preserves D9: `pal/sharpos/` is a thin translation layer and functionality lives in kernel/host.

### D11

D10 preserves D11: direct `extern "C"` symbol calls. It only changes who provides those symbols.

### D13

D10 supports D13: `.pdata` / Windows-style unwind is canonical, but Windows `RtlVirtualUnwind` cannot be the production implementation.

### D5

D10 supports D5: thread creation remains `ABORT_FATAL` in early phases; imports / runtime calls that create threads must be audited.

---

## Final wording

**D10 decision:**

> CoreCLR is built as a TARGET_SHARPOS guest-runtime static archive. The archive is linked directly into the final SharpOS image. During Phase 2 the same archive is linked into a Windows-hosted runner with a C/C++ Windows host shim that provides the `SharpOSHost_*` ABI. No separate NativeAOT `SharpOSHost.lib` is produced or consumed. Static linking remains mandatory, and every hosted spike link must produce audit artifacts proving that no forbidden OS/runtime dependency became part of the production-shaped path.
