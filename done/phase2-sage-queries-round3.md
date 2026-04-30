# Phase 2 sage queries — Round 3 (host/guest architectural clarification)

## Context update — important

В rounds 1-2 я (asking) drift'ил под вашими ответами в направлении
"PAL = our SharpOS code". Это было ошибкой. После team discussion
у нас зафиксировано следующее разделение, **и оно не negotiable**:

```
SharpOS repo (this one) — strict C# (Invariant 1):
   kernel-tier, native-tier apps
   HOST: small set of C-ABI exports through [UnmanagedCallersOnly]
       (SharpOSHost_AllocPages, _MapMemory, _CreateThread, etc.)
       Wraps existing kernel APIs (KernelHeap, Pager, future Scheduler)

CoreCLR-fork repo (separate) — C/C++ allowed:
   vm/, gc/, jit/ — upstream с minimal patches
   pal/sharpos/ — C++ PAL implementation
       Calls into SharpOSHost_* functions через C-ABI boundary
```

**PAL is GUEST code.** It lives in CoreCLR-fork repo, written in C++.
Sits parallel to existing pal/linux/, pal/macos/. Like a new platform target.

**HOST is OUR code.** Strict C# in SharpOS repo. Surface is the C-ABI line
between SharpOS kernel and embedded CoreCLR.

Goal of project: maximize re-use of CoreCLR code (no rewrite). Goal of
Invariant 1: SharpOS source tree stays C#. These reconcile через clear
host/guest split.

This re-frames Phase 2 questions completely.

---

## Query 5 (Sage 1 — architectural validation of host/guest split)

### 1. Does this two-repo architecture validate as "right"?

The split:
- HOST = small C# surface (kernel integration points)
- GUEST = full PAL implementation в CoreCLR fork (C++)
- Граница = C-ABI named exports

Is this a sane embedded runtime hosting pattern? Are there precedents
(JVM as embedded в larger apps, Lua hosts, Python C extensions reverse
direction, etc.) что мы можем reference? Or is это unusual для CoreCLR
specifically?

### 2. HOST surface granularity — wrap PAL 1:1 or higher abstractions?

Two extreme options:

**Option A: 1:1 mapping**. Every PAL function (`VirtualAlloc`,
`VirtualFree`, `CreateThread`, `WaitForSingleObject`, ...) has direct
HOST equivalent (`SharpOSHost_VirtualAlloc`, `_VirtualFree`, ...).
HOST count ≈ PAL count (~150).

**Option B: Smaller HOST primitive set**. PAL implements Win32-shape
semantics on top of fewer HOST primitives. E.g.:
- `SharpOSHost_AllocPages(size, flags)` — supports both VirtualAlloc
  (reserve+commit + protect) и сам KernelHeap.Alloc paths
- `SharpOSHost_CreateThread` → maps Win32 CreateThread + pthread_create
  shape
- `SharpOSHost_WaitObject(handle, timeout)` — generic primitive over
  Win32 WaitForSingleObject + pthread_cond_wait shapes

Estimated HOST count ≈ 30-50 primitives.

**Question**: based on existing PAL implementations (Linux pal/, macOS pal/),
how much shared infrastructure already exists below the public PAL surface?
Можем ли мы leverage that — making HOST surface небольшим — а PAL'у дать
implement Win32 semantics as wrapper logic on top?

If так — что **точно** нужно как HOST primitive (cannot abstract)?

### 3. Where exactly draws the C-ABI line?

PAL function `VirtualAlloc(addr, size, type, protect)`:
- Win32 semantics (RESERVE | COMMIT, protect bits)
- Linux PAL: emulates через `mmap(PROT_NONE)` + later `mprotect(actual)`
  + bookkeeping в side table
- SharpOS PAL: would call something в HOST. **Что именно**?

Is it: HOST gives raw page allocation (`SharpOSHost_AllocPages`) и PAL
implements RESERVE/COMMIT discipline by itself? Or does HOST provide
"reserve_then_commit" higher-level primitive?

The answer affects HOW MUCH of CoreCLR's existing pal/linux/ logic we
re-use vs reimplement в pal/sharpos/.

### 4. Bidirectional callbacks — что HOST needs from GUEST?

PAL forward direction is clear: PAL calls HOST functions.

But are there cases where HOST needs to **call GUEST** (CoreCLR side)?
Examples:
- Managed exception delivery после CPU fault (kernel catches PF/GP, calls
  managed handler. But that handler is in CoreCLR-side managed code.)
- GC suspend/resume callbacks during stack scan
- Thread state notifications
- Diagnostic events

Если есть — это часть HOST API design, потому что HOST имeет publish
function pointer table к GUEST для callback'ов.

### 5. Spike validates что именно?

Re-formulating spike goal for new architecture:

Old goal: "PAL surface is sane, Hello.dll runs through JIT"

New goal: "**HOST surface is sufficient for CoreCLR-style PAL implementation
on top of it**, validated by Hello.dll running через JIT through that
synthesized PAL"

Concretely: для spike, нам не нужно writing pal/sharpos/. We can:
1. Patch existing pal/linux/ к route через intermediate layer that simulates
   our HOST API (e.g., `pal/linux/` calls into `simulated_sharpos_host.c`
   which forwards to actual Linux equivalents)
2. Validate that simulation pattern is workable
3. Catalog actual HOST primitives needed (subset of Linux PAL backing
   functions)

Is this approach sound? Or should spike write actual pal/sharpos/ from
beginning?

### 6. Does win-x64 EH inversion (Q3.2) still apply with this architecture?

In Round 1 you suggested build CoreCLR for win-x64 ABI to reuse our
Phase 1 .pdata-based EH walker.

Does this still hold in host/guest setup? Specifically:
- Win-x64 PAL surface (smaller, since Windows handles much of it natively)
- vs. Linux-style PAL surface (much larger, handles Win32 emulation)

Is win-x64 build target compatible with custom pal/sharpos/ as a third
target ("not Windows, not Unix-style PAL — use SharpOS PAL")?

---

## Query 6 (Sage 2 — technical feasibility of host/guest split)

### 1. Existing PAL split в CoreCLR — already host/guest-shaped?

In CoreCLR's Linux PAL, is там internal layering between:
- "platform abstraction logic" (Win32 semantics emulation, бookkeeping)
- "raw OS calls" (mmap, pthread_*, etc.)

If so — the second layer is effectively what HOST API maps к. The first
layer stays in pal/sharpos/ as is. This minimizes work.

Concrete: in `pal/src/map/virtual.cpp`, look for the actual `mmap` call
sites. How many are там? How many internal helper functions sit between
PAL public API and раw mmap call?

If structure is "thin" (PAL function → mmap directly), we need to add
HOST layer ourselves. If structure is "thick" (PAL function → bookkeeping
→ helper → mmap), we can swap helper для SharpOSHost equivalent.

### 2. C-ABI boundary — what crosses, what stays in C++?

Standard C-ABI primitives:
- pointers, integers, structs (POD)
- callbacks via function pointers
- error returns (HRESULT, bool, etc.)

**Cannot cross C-ABI cleanly**:
- C++ exceptions
- C++ templates
- RAII objects passed by value
- thread-local storage с C++ destructors

Does CoreCLR PAL'у потребуется передавать что-либо из последнего списка
across HOST boundary? Or all PAL→HOST traffic уже primitive types?

### 3. SharpOSHost C# exports — feasibility

Plan: SharpOS publishes static archive containing C-ABI exports through
`[UnmanagedCallersOnly]`. CoreCLR's pal/sharpos/ links to this archive
during CoreCLR fork build.

Build sequence:
1. SharpOS publishes `libsharposhost.a` (C# → NativeAOT static archive)
2. SharpOS publishes `sharposhost.h` (handwritten C header declaring
   exports)
3. CoreCLR fork's pal/sharpos/ uses `#include "sharposhost.h"` and links
   `libsharposhost.a` at libcoreclr build time
4. Final libcoreclr.so includes our C# code statically linked

Does this work? Or are there practical blockers (NativeAOT static archive
not consumable from C++ link step, etc.)?

### 4. Static initialization order in this setup

Order at process start (Linux spike scenario):
1. CoreCLR loaded via dlopen by host process
2. CoreCLR's static initializers run (libcoreclr's CRT init)
3. Some of those initializers call PAL functions (per your Round 2 warning)
4. PAL forwards to HOST functions
5. HOST functions are inside libsharposhost.a, which is C# / NativeAOT code
6. NativeAOT runtime needs to initialize before its functions run

Question: how does NativeAOT-emitted static archive handle the case where
its first call comes от static C++ initializer in another module's CRT
init? Is there a way to force NativeAOT runtime init at known point
(e.g., explicitly call `Initialize()` before dlopen-ing libcoreclr)?

If нет — фactор risk / mitigation strategies?

### 5. HOST API revision frequency

PAL surface stable (Win32-shape, decades old).

HOST API freshly designed by us. Will iterate many times during Phase 6
implementation:
- Find PAL implementation needs primitive HOST didn't expose
- Add new HOST function
- Re-publish libsharposhost.a
- Re-link CoreCLR fork

This is rapid iteration. Build time для libsharposhost.a vs libcoreclr.so?
Per-iteration cost?

If libcoreclr.so re-link is 30+ minutes, iteration is too painful. Что
есть chance скорости up?

### 6. What about libc/libstdc++/libunwind?

These existed в Round 2 как "под-PAL stack". В new architecture:
- pal/sharpos/ in CoreCLR fork uses libc/libstdc++/libunwind как needs
- These come from CoreCLR fork repo's third-party deps (like upstream)
- HOST не зависит от них (HOST = pure C# в SharpOS)

Is this понимание correct? Or do libc/libstdc++/libunwind functions
**directly reach** SharpOS HOST somehow (e.g., libc printf calling write
который routed к SharpOS console)?

If first model is correct — это significantly reduces scope of Phase 6
beyond what previous round suggested.

---

## What we want from this round

Per-question concise answers (numbered list match'инг). Particularly
valuable:

- **Validation** that host/guest split with C-ABI line is sane embedded
  runtime architecture
- **Concrete HOST API surface** estimate (size, decomposition by area)
- **CoreCLR PAL existing structure analysis** — is layering already
  host/guest-shaped or is it "thin"?
- **Static archive interop concerns** между NativeAOT-emitted .a and
  CMake-built libcoreclr.so
- **Iteration speed** practical estimate

If you have **specific file:line refs** в pal/src/ that show existing
internal layering — please cite.

If your answer differs от Rounds 1-2 (because architecture changed),
please flag explicitly.
