# PAL design (current state)

**Purpose**: self-contained reference document для project state после Phase 2 finalization + 7 rounds sage 2 analysis. Single entry-point для understanding architecture, decisions, and what's known/deferred prior to Phase 2A spike implementation.

**Status**: FINALIZED, ready for Phase 2A Windows-hosted spike implementation.

**Sources**:
- `D1-D20 FINALIZED/INDEX.md` — entry point для individual decisions
- `D1-D20 FINALIZED/D*_FINALIZED.md` — authoritative individual decisions
- `D1-D20 FINALIZED/D*_closure.md` — closed/deferred decisions
- `D1-D20 FINALIZED/Phase_2_Redesign___FINALIZED.md` — Phase 2 strategy
- `D1-D20 FINALIZED/TARGET_SHARPOS_Build_Configuration___FINALIZED.md` — build details
- `D1-D20_OLD_BASELINE.md` — historical pre-spike state (archived)

---

## 1. Context

### 1.1 Project
**SharpOS** — experimental unikernel в C#, NativeAOT + NoStdLib + UEFI / x64. Phase 1 (managed EH, ACPI, timers) закрыта. Cell goal — host CoreCLR (для Roslyn + PowerShell hosted-tier) внутри SharpOS.

### 1.2 Phase position
- **Phase 1** ✅ Closed — kernel-tier C# works, managed EH через `.pdata` format, 17/17 EH gates passed
- **Phase 2** 🔄 Current — Windows-hosted TARGET_SHARPOS replacement PAL spike (after redesign from WSL)
- **Phase 3** ⏸ Pending — Full threading session (scheduler, Thread.Start, sync primitives)
- **Phase 5** ⏸ Pending — Storage drivers, filesystem
- **Phase 5.5** ⏸ Pending — Native TLS bring-up (target-format-dependent)
- **Phase 6** ⏸ Pending — CoreCLR PAL implementation + integration
- **Phase 6.1** — initial bare metal CoreCLR (no threading, no GC threads)
- **Phase 6.2** — full production (Roslyn, PowerShell ready)

### 1.3 Architectural Invariants (CLAUDE.md, non-negotiable)

**Invariant 1** — C# is only source language. Никаких `.c`/`.cpp`/`.h`/`.s`/`.asm` файлов в SharpOS repo. Допустимое исключение — external fork (CoreCLR fork лежит в **отдельной репе**, там C/C++ allowed).

**Invariant 2** — Naming discipline. Канонические `System.*` namespaces ТОЛЬКО для fully BCL-compat реализаций. Partial / OS-specific → `SharpOS.Std.*`, `OS.Kernel.*`, `OS.Boot.*`.

---

## 2. Architecture (current — post-revised D10/D11)

### 2.1 Call flow (identical в обеих фазах)

```
Phase 2A:  CoreCLR → pal/sharpos/ → SharpOSHost_* → Windows shim (stub) → Win32
Phase 2B:  CoreCLR → pal/sharpos/ → SharpOSHost_* → Windows shim (full) → Win32
Phase 6:   CoreCLR → pal/sharpos/ → SharpOSHost_* → bare-metal provider → kernel
```

**`pal/sharpos/` код identical** во всех трёх. Меняется только **provider** behind SharpOSHost_*.

### 2.2 Linkage model (per revised D10)

**Static linking всё в один artifact**:
- `coreclr_sharpos_static.lib` — CoreCLR fork + pal/sharpos/ archive
- + `sharpos_host_windows_shim.lib` (Phase 2) or bare-metal provider (Phase 6)
- + `spike-host.obj` (Phase 2) or SharpOS kernel image (Phase 6)
- = single final artifact via `link.exe` (Phase 2) or cross-compile toolchain (Phase 6)

**No dlopen**, **no separate NativeAOT static library** (`libsharposhost.a` rejected per revised D10/D11), **no function table indirection** (per revised D11).

### 2.3 Provider model (per revised D11)

Provider environment-specific:

**Phase 2 — sharpos_host_windows_shim/** (C++):
- Win32 backends for SharpOSHost_* exports
- Temporary, replaced for Phase 6
- WinAPI разрешён (НЕ в pal/sharpos/, per D11 firewall)

**Phase 6 — bare-metal provider** (form decided at Phase 6.2):
- C/C++ glue к kernel internal ABI, OR
- generated veneer, OR
- direct kernel symbol, OR
- C# UCO export from kernel-tier (one of options, not default)

### 2.4 Compile-time firewall (per revised D11 + Round 7)

pal/sharpos/ физически предотвращён от вызова WinAPI:
- Forced include `sharpos_no_winapi.h`
- `#pragma GCC poison` для WinAPI identifiers
- Fake `windows.h` в `forbidden_headers/`
- Object-level audit через `nm -u` / `dumpbin /SYMBOLS`
- Link trace via `--trace-symbol` / `link /VERBOSE:LIB`

### 2.5 Architectural precedent

Rump kernels (NetBSD anykernel, Antti Kantee PhD 2012). MirageOS/Solo5, Genode, Microsoft Drawbridge. **Not** embedded language runtime pattern (JVM/Lua/V8).

---

## 3. Empirical data from WSL spike (locked-in facts)

**Note**: gathered during WSL spike (Phase 2 pre-redesign). Numbers may shift after Windows-hosted spike measurement. Treat as **baseline estimates**, не canonical для Windows target.

### 3.1 Numbers
- **PAL surface от pal.h proper**: 144 functions declared (87 Win32-shape + 57 PAL_-prefix)
- **Spike's link-time surface**: ~165 trap symbols (включал eventprovider + palprivate.h + rt/)
- **Eventing surface**: ~200 functions covered through upstream `pal/src/eventprovider/dummyprovider/` (no manual work)
- **C++ EH usage**: 363 EX_TRY sites в vm/ + 28 PAL_TRY (across 13 files). `-fno-exceptions` НЕРЕАЛИСТИЧНО
- **NativeAOT runtime EH**: 0 EX_TRY occurrences. Built с `-fno-exceptions -fno-asynchronous-unwind-tables -nostdlib`
- **NativeAOT Pal surface**: ~35 functions (`nativeaot/Runtime/Pal.h`)
- **Build artifacts (WSL)**: libcoreclr.so 14 MB stripped, 60 MB+ debug

### 3.2 First hot-path PAL function
`MultiByteToWideChar`. Called из `dlls/mscoree/exports.cpp:80` `StringToUnicode()` ДО `PAL_InitializeCoreCLR`. Charset infrastructure требуется **очень рано** в startup.

NOT iconv-coupled — delegates через `minipal_convert_utf8_to_utf16` (`src/native/minipal/utf8.c`, pure C, freestanding-friendly). Per D20: implementation routes к `System.Text.Encoding.UTF8` через SharpOSHost_* (Phase 6) или через Win32 `MultiByteToWideChar` (Phase 2 shim).

### 3.3 Unwind format reality (per D13)
- JIT эмитит Windows-style `UNWIND_INFO` на Linux **and** Windows
- CoreCLR имеет portable Windows-style `RtlVirtualUnwind_Unsafe` (`unwinder/amd64/unwinder.cpp`, 1847 LOC, borrowed from Windows minkernel) — **fallback** для D13
- SharpOS Phase 1 имеет **own** unwinder (`OS/src/Boot/EH/StackFrameIteratorOps.UnwindOneFrame`) — format-compatible с managed unwinder, supports 4 UWOPs currently (PUSH_NONVOL, ALLOC_LARGE, ALLOC_SMALL, SET_FPREG)
- **D13 finalization**: reuse + extend Phase 1 unwinder. Windows `RtlVirtualUnwind` — diagnostic oracle only.

### 3.4 libstdc++ / libunwind status
**Per D13 finalization**: libunwind **forbidden** in production path. Phase 1 unwinder uses no libunwind, no libstdc++ EH ABI. Только portable Microsoft unwinder (1847 LOC) as optional fallback if Phase 1 extension insufficient.

Baseline's libstdc++ subset estimate (15-20 KLOC) — **no longer relevant**.

### 3.5 Symbol conflicts CoreCLR ↔ NativeAOT runtime
**Per revised D10/D11**: only one NativeAOT runtime in system (kernel-tier in Phase 6). Phase 2 spike has no NativeAOT runtime at all (provider is C++ shim).

Baseline's "0-3 conflicts" estimate — **no longer applicable** (revised architecture removes the scenario).

---

## 4. Decisions D1-D20 — authoritative locations

For each decision, see corresponding file in `D1-D20 FINALIZED/`. Summary:

| D# | Topic | Status | Key choice |
|---|---|---|---|
| D1 | Error code lingua franca | FINALIZED | Microsoft Interop.Error values, C ABI primary |
| D2 | LastError storage | FINALIZED | C++11 thread_local, Phase 5.5 target-format-dependent |
| D3 | Policy unimplemented PAL | FINALIZED | 5 categories, default ABORT_FATAL, trace-driven |
| D4 | Catch-all SharpOSHost exports | FINALIZED | Environment-specific (managed + native contexts) |
| D5 | Thread creation | FINALIZED | ABORT_FATAL stub, threading deferred к Phase 6.2 |
| D6 | Thread state ownership | DEFERRED | Reopen Phase 6.2 |
| D7 | TLS implementation | COVERED via D2 | C++11 thread_local |
| D8 | GC thread suspension | DEFERRED | Reopen Phase 6.2 (GC disabled) |
| D9 | pal/sharpos structure | FINALIZED | Flat domain split, no preinit/forward |
| D10 | Linker model | REVISED | Static linking, CoreCLR guest archive, no dlopen |
| D11 | Host API table | REVISED | Direct extern "C", SharpOSHost_* ABI namespace, provider env-specific |
| D12 | NativeAOT Pal middle layer | CLOSED | Not applicable |
| D13 | EH path | FINALIZED | .pdata canonical, Phase 1 unwinder extension |
| D14 | Cross-runtime exception rule | CLOSED via D4 | Hard prohibition |
| D15 | Tracer location | CLOSED via D3+D9 | pal/sharpos/trace.cpp |
| D16 | Phase markers | CLOSED via D3 | Add when needed |
| D17 | Trace dump trigger | FINALIZED | Crash + clean exit + explicit DumpNow, crash-safe |
| D18 | Spec scope | CLOSED via D3 | Observed functions only |
| D19 | Spec format | CLOSED | Per-function detailed |
| D20 | Local leaf implementations | FINALIZED | C++ pal/sharpos vs C# kernel-tier per natural fit |

**Plus** (not in original D1-D20 list):
- **Phase 2 Redesign** — WSL retired → Windows-hosted TARGET_SHARPOS primary
- **TARGET_SHARPOS Build Configuration** — CMake patches, HOST_WIN32 axis split, build steps

---

## 5. Implementation plan (Phase 2A Windows-hosted spike)

Total estimate: **3-5 дней** до первого pal/sharpos trace (per Phase 2 Redesign timebox).

### Step 1 — Setup (1 день)
- Visual Studio Build Tools на Windows machine
- Git for Windows, long paths enabled
- Vanilla CoreCLR Windows build proof (`build.cmd -subset clr -configuration Debug`)

### Step 2 — Configure proof gate (CRITICAL FIRST GATE)
- Apply 4 TARGET_SHARPOS patches (additive, per TARGET_SHARPOS Build Configuration)
- Verify CMake configure proof:
  ```
  HOST_WIN32 + TARGET_SHARPOS + !TARGET_WIN32
  coreclrpal included
  Windows system libs не auto-added
  unwinder_wks explicitly included (manual link для D13)
  ```
- If this gate fails — нужны дополнительные CMake patches beyond initial 4

### Step 3 — Build coreclr_sharpos_static.lib
- D11 firewall enforced (forced include, GCC poison, fake windows.h)
- D10 audits prepared (Tier A initial = kernel32.lib only)

### Step 4 — Build sharpos_host_windows_shim.lib
- C++ project с Win32 backends для SharpOSHost_* exports
- Minimal scope — just enough для Hello World
- WinAPI разрешён здесь (NOT в pal/sharpos/)

### Step 5 — First spike-host.exe link
- `link.exe spike-host.obj coreclr_sharpos_static.lib sharpos_host_windows_shim.lib /OUT:spike-host.exe`
- Link audit (per D10):
  - dumpbin /DEPENDENTS, /IMPORTS
  - link.exe /VERBOSE:LIB
  - System lib evidence-based expansion (Tier A → A-extended)
- D10 acceptance gates:
  - C++ static initialization audit (CRT$XCU sections retained)
  - .pdata/.xdata retention audit (CoreCLR RUNTIME_FUNCTION records in final image)

### Step 6 — Run + collect trace
- Hello World scenario
- First pal/sharpos trace dump
- Identify policy bucket for each observed function (per D3)

### Step 7 — UNWIND_INFO dumper (для D13 coverage measurement)
- Dump UNWIND_INFO для всех frames что встречаются
- Сравнить с Phase 1 supported set (PUSH_NONVOL, ALLOC_LARGE, ALLOC_SMALL, SET_FPREG)
- Identify gap → decide scope расширения (or fallback к Microsoft portable unwinder)

**Timebox**: 3-5 дней до Step 6 first trace. Если не получается — re-evaluate (revisit к WSL or narrow scope к D13-only oracle).

---

## 6. Build configuration target

### 6.1 Differences Windows-hosted spike vs SharpOS bare-metal

| Aspect | Windows-hosted spike (Phase 2) | SharpOS bare-metal (Phase 6 target) |
|---|---|---|
| Build driver | Ninja (default) | Same Ninja, cross-compile target |
| CRT | libcmt (MSVC) or ucrt | None or minimal kernel runtime |
| Object format | COFF/PE | TBD (likely PE, открыто per D2) |
| TLS | TEB-based (PE convention) | Per chosen format (Phase 5.5) |
| Threading | None (single thread per D5) | None в Phase 6.1, scheduler в Phase 6.2 (Phase 3 dep) |
| Memory paging | VirtualAlloc через shim | SharpOS Pager (KernelHeap, exists) |
| Signal delivery | SetUnhandledExceptionFilter (diagnostic) | Phase 1 EH integration via HwFaultBridge |
| File I/O | CreateFile через shim | TBD (depends on Phase 5 storage drivers) |
| Module loading | Static link (no dlopen) | Same static link |
| GC stack suspend | N/A (Zero GC per D5/D8) | Same N/A в Phase 6.1 |
| Unwind | Phase 1 unwinder (per D13) | Same |

### 6.2 What spike validates regardless of target

- C-ABI line works (pal/sharpos/ → SharpOSHost_* → provider)
- Compile-time firewall enforces D11 (no WinAPI leak в pal/sharpos/)
- CMake TARGET_SHARPOS axis split works (HOST_WIN32 + !TARGET_WIN32)
- Static linking model (no dlopen)
- Win32-shape PAL surface scope (~144 funcs)
- Link audit infrastructure (Tier A allowlist evidence-based expansion)
- C++ static initialization safe path
- .pdata/.xdata retention для D13

### 6.3 What spike CANNOT validate (deferred к Phase 6)

- Real PAL hot path through coreclr_initialize → JIT → managed Main (partial — Hello World goes through, complex scenarios deferred)
- Bare-metal provider mechanism (shim form for Phase 6 — open per D11)
- Cross-runtime threading behavior (deferred per D5/D6/D8 к Phase 6.2)
- bare-metal substrate dependencies (CRT-less, kernel-provided primitives)

---

## 7. Deferred decisions (explicit TBD list)

Эти decisions cannot be made до соответствующих gates:

1. **HOST primitive count** — actual derivation из observed surface (Phase 2A trace)
2. **PAL_Callbacks shape** — `on_fault`, `on_async_suspend`, `on_thread_start/exit`, `on_low_memory`. Deferred к Phase 6.2.
3. **CreateProcess policy** — kill process или FAIL? Per D3 trace-driven classification.
4. **Cold-path fakeification policy** — per D3 trace-driven.
5. **`_CONTEXT::operator=`** — simple memcpy now, XSTATE-aware copy when AVX-512 hits hot path.
6. **EH spike scope** — 7 EH scenarios identified в baseline. Phase 2A trace will measure coverage gap.
7. **Phase 3 sequencing gate** — when must Phase 3 (scheduler) complete relative к Phase 6 implementation? Affects D5/D6/D8 reopen timing.
8. **Phase 6 provider mechanism** — D11 revised lists 4 options (C/C++ glue, generated veneer, direct kernel symbol, C# UCO export). Choice deferred к Phase 6.2 design.
9. **Final CoreCLR archive format** — PE/COFF (expected, Windows-hosted spike alignment) или ELF/SysV? Determines D2 Phase 5.5 substrate. Open до empirical confirmation from spike.

---

## 8. Anti-list (explicit NOT-doing)

Что **не делается** в текущем architecture:

- ❌ Trap-by-trap implementation loop (replaced by trace-backed per D3)
- ❌ Cabinet spec на all 144 functions без trace data (per D18)
- ❌ NativeAOT PAL as full replacement of CoreCLR PAL (per D12 closure)
- ❌ Linux WSL primary spike (per Phase 2 Redesign — superseded by Windows-hosted)
- ❌ libunwind as production EH path (per D13 — diagnostic oracle only)
- ❌ libstdc++-free CoreCLR в Phase 2 spike (per Windows-hosted reality)
- ❌ Crossing exceptions across SharpOSHost C-ABI boundary (per D4/D14)
- ❌ Calling Win32 APIs directly from pal/sharpos/ (per D11 firewall, compile-time enforced)
- ❌ Windows `RtlVirtualUnwind` as production implementation (per D13 — oracle only)
- ❌ Phase 2A direct Win32 shortcut в pal/sharpos/ (per Phase 2 Redesign Round 7)
- ❌ `[ThreadStatic]` в kernel-tier SharpOS code без Phase 5.5 native TLS (per D2)
- ❌ Separate NativeAOT `libsharposhost.a` / `SharpOSHost.lib` as boundary mechanism (per revised D10/D11)
- ❌ Function table injection / `SharpOSPal_SetHostApiTable` pattern (per revised D11)
- ❌ dlopen / dynamic loading (per revised D10 — static linking only)
- ❌ preinit/forward subdirectory split в pal/sharpos/ (per D9 — flat structure)
- ❌ scaffolding/ subdirectory (per D9 + D3 principle 8 REMOVED)
- ❌ `cp pal/src/ + sed substitute` as primary implementation strategy
- ❌ Threading в Phase 2/6.1 (per D5 — ABORT_FATAL stub, deferred к Phase 6.2)
- ❌ GC threads / standard GC в Phase 2/6.1 (per D8 — Zero GC, deferred к Phase 6.2)

---

## 9. Open questions (post-finalization, will be resolved by Phase 2A measurements)

1. **D13 unwinder coverage scope** — Phase 2A trace will measure UWOP gap. Decision rule: extend Phase 1 unwinder (incremental) или switch к Microsoft portable unwinder (1847 LOC) if gap too large?

2. **System lib allowlist final shape** — Tier A starts kernel32.lib only. Evidence-based expansion through Phase 2A iteration. Final shape documented at Phase 2A completion.

3. **JIT-only PAL surface** — JIT может invoke functions which не appear before managed Main. Phase 2A trace во время JIT compile (force `COMPlus_JitDisasm`) покажет.

4. **HOST primitive count** — actual derivation from observed surface (Phase 2A trace data).

Items 5-9 from Section 7 (Phase 3 sequencing, Phase 6 provider, CoreCLR format, etc.) — deferred beyond Phase 2A scope.

---

## 10. Compatibility / migration notes

**From baseline state**: this document supersedes `pal-design-baseline.md` (pre-spike, pre-revision). Old baseline preserved as `D1-D20_OLD_BASELINE.md` для historical reference.

**Major changes from baseline**:
- D10/D11 architectural reversal (static linking, ABI namespace, no separate NativeAOT lib)
- Phase 2 target change (WSL → Windows-hosted)
- D13 EH model change (libunwind path → Phase 1 .pdata reuse)
- D9 structure simplification (preinit/forward → flat domain)
- D5/D6/D8 scope reduction (threading deferred к Phase 6.2)
- Compile-time firewall added (round 7 — D11 enforcement)

**Architectural invariants maintained**:
- Invariant 1 — C# only в SharpOS repo (C++ stays в CoreCLR fork)
- Invariant 2 — naming discipline (no canonical System.* для partial impl)
- Hard prohibition exceptions cross C-ABI (D4/D14 — same since baseline)

---

## Quick reference

**Authoritative decisions**: `D1-D20 FINALIZED/D*_FINALIZED.md` + `D*_closure.md`
**Architecture entry point**: `D1-D20 FINALIZED/INDEX.md`
**Phase 2 strategy**: `D1-D20 FINALIZED/Phase_2_Redesign___FINALIZED.md`
**Build configuration**: `D1-D20 FINALIZED/TARGET_SHARPOS_Build_Configuration___FINALIZED.md`
**Historical baseline**: `D1-D20_OLD_BASELINE.md` (obsolete)
