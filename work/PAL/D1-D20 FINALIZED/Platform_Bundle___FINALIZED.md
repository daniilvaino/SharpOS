# TARGET_SHARPOS as first-class CoreCLR platform bundle — FINALIZED

Refinement to Phase 6.1 implementation approach after empirical Path A
trial revealed structural mismatch: CoreCLR has parallel GC env
implementations (`gc/windows/` Win32-direct, `gc/unix/` POSIX-direct)
selected by **host** platform, but TARGET_SHARPOS preprocessor expected
**target**-level PAL pattern. Hybrid state (vm/ через PAL + GC через
Win32/POSIX) generates type/symbol conflicts that cascade indefinitely.

## Decision

**TARGET_SHARPOS — third CoreCLR platform**, not Unix-masked or
Windows-masked. Implement as **complete platform bundle**:

```
src/coreclr/
  pal/sharpos/        — Win32-shape PAL → SharpOSHost_*
  gc/sharpos/         — GC env → SharpOSHost_*
  unwinder/           — Phase 1 .pdata reuse (per D13, already in tree)
  pal/src/eventprovider/dummyprovider/  — minimal/no-op (per Round 6)
```

NOT used on TARGET_SHARPOS:
- `gc/windows/` (Win32 direct — defeats PAL boundary)
- `gc/unix/` (POSIX direct — requires libunwind/pthread/signals)
- Windows stock unwind (RtlVirtualUnwind — already rejected per D13)
- Stock event provider (ETW/LTTng — replaced by dummyprovider)

## Build axis split (confirmed)

```
HOST_WIN32       = true   (build machine on Windows)
TARGET_SHARPOS   = true   (CoreCLR platform target)
TARGET_WIN32     = false  (not stock Windows target)
TARGET_UNIX      = narrow internal-only, not wholesale Linux
```

Linux host as build machine — fallback only for diagnostics, не
mainline. WSL+MinGW cross-compile rejected as production path:
mismatch between MinGW C++ ABI/libstdc++/EH model and MSVC/NativeAOT
world introduces hidden runtime issues. SharpOS final image expects
MSVC ABI (PE/COFF + Win64 calling convention) — Windows host clang-cl
produces this natively.

## Why first-class bundle, не source surgery

Empirical: Path B-source-surgery approach (TARGET_WINDOWS preprocessor
+ `|| defined(TARGET_SHARPOS)` branches throughout vm/) would require
80-140+ vm/ patches per scope analysis. Fragile against upstream
changes. Hides architectural intent в conditional thickets.

Path bundle (this decision): cmake patches select correct platform
subsystem; vm/ source remains mostly unchanged. Production architecture
honest: "SharpOS is not Windows, SharpOS is not Unix — needs its own
CoreCLR platform layer".

## Implementation principles

### Principle 1: Replace, не patch

If vm/ needs >20 TARGET_SHARPOS branches after platform bundle done →
model wrong, stop and rethink. First attempt = platform bundle ONLY +
minimal vm/ overrides where unavoidable. Iterate from там.

### Principle 2: gc/sharpos/ stolen from reference

Copy structure от gc/unix/ (PAL-like abstractions) или gc/windows/
(Win64/COFF conventions). Replace bottom-level OS calls с
`SharpOSHost_*` ABI. Skeleton minimum:
- `gcenv.sharpos.cpp` — GC OS environment shim
- Routes through SharpOSHost C-ABI primitives

### Principle 3: Early-phase minimal scope (per D5)

Phase 6.1 bootstrap:
- CreateThread paths → ABORT_FATAL
- Thread suspension → ABORT_FATAL or unreachable
- Events/semaphores → minimal stubs / fatal if reached
- Virtual memory → real
- Time/CPU info → minimal
- Write/log → real

No production ThreadPool, no GC suspension. Phase 6.2 expands.

### Principle 4: Firewall extends к gc/sharpos/

D11 firewall (forced include + `#pragma poison` + fake windows.h)
applies к gc/sharpos/ same as pal/sharpos/. Windows APIs only allowed
в `sharpos_host_windows_shim/` (Phase 2A build/debug environment) —
never в pal/sharpos/ или gc/sharpos/.

## Anti-list

- ❌ TARGET_UNIX wholesale (pulls libunwind/pthread/signals)
- ❌ TARGET_WINDOWS wholesale (pulls stock Win32 subsystems, bypasses PAL)
- ❌ gc/windows/ on TARGET_SHARPOS (Win32-direct, defeats boundary)
- ❌ gc/unix/ on TARGET_SHARPOS (POSIX-direct, requires libunwind)
- ❌ WSL+MinGW cross-compile as production path (MinGW ABI mismatch)
- ❌ source surgery in vm/ as primary mechanism (>20 vm/ patches = wrong model)
- ❌ Win32 APIs in pal/sharpos/ or gc/sharpos/ (firewall enforced)

## Decision rule

> "SharpOS is not Windows. SharpOS is not Unix. SharpOS needs its own
> CoreCLR platform layer."

Each new CoreCLR subsystem (gc, pal, debug-pal, eventing, etc.) gets
considered:
- Does it have Win32-direct + POSIX-direct parallel implementations?
- If yes → create `<subsystem>/sharpos/` overlay, patch cmake selection
- Если no (cross-platform with TARGET_* branches) → minimal narrow
  TARGET_SHARPOS branches where overrides genuinely needed

## Status

Established 2026-05-12 after Path A trial empirically confirmed
hybrid (pal/sharpos/ + gc/windows/) generates indefinite cascade.
Replaces ambiguous "Path A as platform port" framing with explicit
"platform bundle". Compatible с sage 2 round 7 finalization.
