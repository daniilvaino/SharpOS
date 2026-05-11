# Phase 2 sage queries — Round 4 (post-spike open questions)

## What changed since Round 3

We executed the Phase 2 spike per plan. **Spike pass not achieved**, but
spike **measurement complete**. We now have concrete data instead of
estimates. New questions emerge from that data + several decisions we
deferred to "post-spike" still need answering.

This round asks sages to help us:
1. Decide whether to continue the current spike trajectory or rethink
2. Sequence spec-writing vs implementation
3. Resolve EH integration path (A/B/C)
4. Validate or kill a new hypothesis (NativeAOT PAL reuse)
5. Address several technical risks now that they're concrete

---

## Spike status — concrete

### What we built (in CoreCLR fork repo, gitignored from SharpOS repo)

- **`pal/sharpos/`** — new directory parallel to `pal/src/`. Activated via
  `-DSHARPOS_PAL=ON` cmake option. Contains:
  - `stubs.cpp` — ~165 PAL/Win32 trap stubs (`abort()` + diagnostic).
  - `context_ops.cpp` — out-of-line `_CONTEXT::operator=` (custom
    assignment for variable-size XSTATE, simplified to full memcpy for
    spike).
  - `stub.cpp` — empty placeholder TU for cmake target dependencies.
  - `CMakeLists.txt` — defines `coreclrpal` STATIC target, links
    libstdc++, reuses upstream `pal/src/eventprovider/dummyprovider`
    for ~200 ETW no-op stubs.

- Auxiliary cmake patches (mscordbi, singlefilehost) — replaced with
  trivial stubs when `SHARPOS_PAL=ON` because they have host-toolchain
  dependencies that don't compose with our trap-only PAL.

### What works

- libcoreclr.so links (~14 MB stripped, ~60 MB with debug info).
- libcoreclr.so dlopens — all link-time symbols resolved.
- `corerun Hello.dll` starts executing.
- First PAL function call traps with diagnostic message.

### What doesn't work

- Hello World output — never reached. Trapped on first PAL call.
- `SharpOSHost_Initialize` — not written.
- Function table pattern (`SharpOSHost_GetApiTable`) — not written.
- `PAL_Callbacks` bidirectional pattern — not written.
- libsharposhost.a → pal/sharpos/ wiring — disconnected. Two binaries
  exist independently; pal/sharpos/ doesn't link against SharpOSHost.

### Key data points

- **PAL link-time surface: ~165 functions.** Concrete list (categorized):
  - Virtual memory: VirtualAlloc/Free/Protect/Query (4)
  - File I/O: CreateFile{A,W}, Read/WriteFile, mappings (10)
  - Module loading: LoadLibrary{ExA,ExW,Direct}, GetProcAddress, Free* (8)
  - Process/thread: GetCurrent{Pid,Tid}, CreateProcess, CreateThread (8)
  - Sync: CreateEvent/Mutex/Semaphore + Wait*, Open*, Release* (~15)
  - Charset: MultiByteToWideChar, WideCharToMultiByte, _wcs* (6)
  - System: GetSystemInfo/Time, GetTickCount (4)
  - PAL_*: PAL_Initialize, PAL_Shutdown, PAL_*Memory, PAL_*Thread (~40)
  - Context: GetThreadContext, SetThreadContext, RtlCaptureContext (5)
  - Format/CRT: _snprintf_s, sprintf_s, FormatMessage (~10)
  - LoadPEFile, ProbeMemory, GenerateCoreDump, etc. (~30)
  - COM constants: IID_IUnknown, IID_IStream, GUID_NULL (4)
  - **Eventing surface ~200 functions** — covered automatically via
    upstream `dummyprovider` (no manual work needed).

- **First hot-path PAL function**: `MultiByteToWideChar`. Runtime
  startup begins with charset conversion — likely command-line / env-var
  / path processing.

- **C++ ABI symbols required**: `__cxa_*`, `__gxx_personality_v0`, etc.
  — link libstdc++ explicitly (PUBLIC on coreclrpal target).

- **Custom impl required for**: `_CONTEXT::operator=` — declared as
  member function in pal.h, implemented out-of-line in
  pal/src/thread/context.cpp:2204.

### Spike pass criteria (from phase6-architecture.md §11) — none met

| # | Criterion | Status |
|---|---|---|
| 1 | `SharpOSHost_Initialize` returns success | not written |
| 2 | `coreclr_initialize` returns S_OK | not reached |
| 3 | JIT compiles Program.Main (visible via COMPlus_JitDisasm) | not reached |
| 4 | "hello" appears in stdout | not reached |
| 5 | Process exit code = 42 | not reached |
| 6 | PAL summary shows no fatal stub hits | trapped on first |

### What spike validated vs what stays abstract

✅ **Validated**:
- C-ABI line works (NativeAOT C# .a callable from C — Branch A test).
- CMake substitution mechanism works.
- ~165 link-time PAL surface count (concrete list, not estimate).
- libcoreclr.so dlopens with trap-only PAL.

❌ **Not validated**:
- "30-50 HOST primitives" estimate vs actual hot-path needs.
- "cp pal/src/ + sed" approach as primary implementation strategy.
- Static init ordering mitigation pattern.
- Function-table iteration speed claim ("seconds-to-minutes").
- Two managed runtimes (NativeAOT host + CoreCLR guest) symbol coexistence.
- libunwind port effort (relevant for EH path A).
- libsharposhost.a actually consumed by pal/sharpos/ (the architecture's
  whole point).

---

## Question 1 — Strategy: continue spike or rethink?

### Background

phase6-architecture.md §15 prescribes this implementation strategy:

> `pal/sharpos/map/virtual.cpp ≈ cp pal/src/map/virtual.cpp + sed
> substitution mmap → SharpOSHost_ReservePages etc. Tractable diff.`

Three cumulative observations make us less confident:

**(a)** Spike trapped immediately on `MultiByteToWideChar` — a charset
conversion routine that's tightly coupled to iconv/glibc locale data.
Not memory, threading, or signals. The "easy first PAL function" is
already unfriendly to host/guest split because the conversion logic
itself is non-trivial (UTF-8 → UTF-16 with codepage handling, surrogate
pairs, etc.) and lives inside libc on POSIX.

**(b)** PAL's pal.h declares `_CONTEXT::operator=` as a struct member
function, not a free function. This means PAL types leak C++ semantics
across what was supposed to be a POD-only C-ABI line. We hit similar
issues with `extern "C" const` GUID linkage and libstdc++ runtime
symbols that PAL emission requires. The "C-ABI clean" boundary in
practice has C++ runtime dependencies.

**(c)** ~165 link-time PAL surface vs 30-50 HOST primitives target gap
(3-5x). PAL composes many Win32-shaped surface functions to fewer
underlying primitives — this is fine in principle. But realizing this
requires us to actually copy and adapt ~165 functions worth of pal/src/
code, replacing only the bottom-level OS calls. That's not "tractable
diff" — that's porting most of pal/src/.

### The question

Given the data, which trajectory is correct?

**Path 1 — Continue the spike.** Implement MultiByteToWideChar
(probably by copying pal/src/locale/unicode.cpp and using glibc iconv
under the hood). See next trap. Repeat. Estimate 10-30 iterations to
reach Hello World stdout. Validates the core architecture as it stands.

**Path 2 — Pause spike, write spec first.** Stop adding implementations
ad-hoc. Instead, read all 165 PAL declarations + their pal/src/ impls,
write a HOST API spec (the Phase 2 deliverable we haven't done), then
restart spike from a designed surface. Risks: month+ of reading without
running code; spec can be wrong without empirical data.

**Path 3 — Rethink architecture.** Reuse NativeAOT runtime's PAL surface
instead of porting CoreCLR's Win32 PAL. NativeAOT already runs
managed C# on Linux/Windows/bare-metal-with-some-effort. Its PAL is
much smaller (no JIT, no AppDomains, no cross-compilation toolchain).
Could we host CoreCLR JIT on top of NativeAOT runtime + small shim,
instead of host/guest split?

**Path 4 — Accept the gap and adjust expectations.** "1-2 weeks spike"
in plan.md was wrong. Reality is "9-18 months Phase 6 IS the spike",
no separate de-risk milestone exists. Continue Phase 6 as-planned but
without the GO/NO-GO gate.

We need help choosing. Decision criteria:
- Project already 1.5 years in. We can afford 1-3 more months of
  reconnaissance, but not 6+.
- Single developer, no FTE budget.
- Goal is Roslyn + PowerShell, not perfectly clean architecture.

### What we want from sage

Honest read on which path is least likely to dead-end. We've already
revised our plan once (Round 3) — willing to revise again if data warrants.
Specifically: is "cp pal/src/ + sed" actually the right strategy after
seeing first hot-path is locale-coupled?

---

## Question 2 — Spec-first or spike-first?

plan.md Phase 2 has two deliverables: written PAL spec + Linux spike.
We executed spike first (incomplete) without spec.

### Pros of spec-first now

- Forces us to actually read pal.h declarations, not just trap them.
- Decouples HOST API design from "what trap fires next" reactive mode.
- Phase 6 prerequisite anyway — Phase 6 implementation needs the spec.

### Cons of spec-first

- 165 functions × 30-60 min each (read declaration, find pal/src/ impl,
  write SharpOSHost_* mapping) = 80-160 hours. 2-4 weeks pure desk work.
- Risks specifying unused functions. Without runtime trace data, can't
  prioritize.
- Specs without running code are 60-80% wrong (anecdotal — happens to
  every protocol/API spec ever written).

### Pros of more spike

- Each implemented function gives empirical "this is on hot path"
  signal — natural priority ordering.
- Faster feedback loop. 30 min to add new trap impl, 2 min build,
  observe next trap.
- Forces architectural decisions to be tested, not designed in vacuum.

### Cons of more spike

- Without spec, we may build implementations that get torn out when we
  realize HOST API surface needs to be different.
- Can't decompose work for parallel effort (spec → multiple implementers,
  spike → one developer at keyboard).
- Risk getting stuck implementing impl-detail PAL functions that have
  no clean HOST equivalent.

### What we want from sage

Recommended sequencing. Are there hybrid patterns that work better
(e.g., "implement first 20 hot-path functions ad-hoc, then pause and
spec-back from observed patterns")?

---

## Question 3 — EH integration path A/B/C — decision now

phase6-architecture.md §8 lists three paths for exception handling
integration, decision deferred until "post-spike data". Spike gave
zero data on this — we trapped before any exception machinery started.

### Path A — linux-x64 + libunwind for JIT

Current spike trajectory. CoreCLR uses libunwind (or PAL's internal
unwinder) for JIT-emitted code. SharpOS kernel-tier uses Phase 1 EH
infrastructure (already built — `.eh_frame` walking, personality,
finally + filter clauses, multi-frame collided unwind). Two unwinders
in process.

- **Pro**: Min change to upstream PAL.
- **Con**: Two unwinders is fragile. Frame ownership ambiguous when
  exception crosses tier boundary (CoreCLR-managed → host-managed).

### Path B — win-x64 build, no PAL, SharpOSHost = Win32-shape

Build CoreCLR for Windows target. No PAL needed (pal/src/ is Linux/Mac
only). SharpOSHost implements Win32 functions directly (CreateFileW,
VirtualAlloc, etc.). Single unwinder — Phase 1 EH (already designed
for win-x64 layout: `.pdata`/`.xdata` + `RUNTIME_FUNCTION`).

- **Pro**: Single unwinder. Aligns with Phase 1 work already done.
- **Con**: SharpOSHost surface much larger (~165 vs 30-50 primitives).
  Loses the "small HOST C# surface" appeal.

### Path C — linux-x64 + JIT patch to emit .pdata

Linux PAL but patch JIT codegen to emit Windows-style .pdata sections.
Phase 1 EH consumes both AOT-emitted (`.pdata`) and JIT-emitted
(`.pdata`) frames uniformly.

- **Pro**: Single unwinder, less SharpOSHost change than B.
- **Con**: Custom JIT patch — Frankenstein, not upstream-friendly,
  unmaintainable.

### What we want from sage

Decision now without waiting for more spike data. Spike won't naturally
produce libunwind-vs-Phase-1 comparison data — exception scenarios are
not on Hello World hot path. We need this decided to scope SharpOSHost
correctly.

If sage answer is "still need data" — what specific spike experiment
generates it?

---

## Question 4 — NativeAOT runtime PAL reuse hypothesis

This is a new hypothesis not addressed in prior rounds.

### Setup

- We already use NativeAOT runtime in SharpOS kernel-tier successfully.
- NativeAOT runtime has its own minimal PAL (runtime/Bootstrap/, etc.)
  — much smaller than CoreCLR's pal/src/ (~10-20 functions vs ~165).
- NativeAOT runtime PAL is what makes our libsharposhost.a work in the
  first place.

### Hypothesis

What if we configure CoreCLR to **use NativeAOT runtime's PAL** instead
of pal/src/? CoreCLR's GC, JIT, vm/ would talk to NativeAOT PAL surface.
NativeAOT PAL talks to SharpOSHost_*. We get one PAL surface to wrap,
not two.

### Concerns

- NativeAOT PAL was designed for AOT-compiled code, not JIT runtime.
  Missing: dynamic module load, certain TLS patterns, signal handling
  that CoreCLR JIT'd code expects.
- NativeAOT PAL doesn't expose CoreCLR-shape Win32 API (CreateFileW,
  VirtualAlloc) — CoreCLR vm/gc/jit code calls these directly.
- We'd have to inject an adapter layer between CoreCLR's PAL
  expectations and NativeAOT PAL's actual surface.

### What we want from sage

Is this a viable alternative architecture, or is it a dead-end because
of fundamental mismatch?

If viable: how does the adapter layer compare in scope to "cp pal/src/
+ sed substitute" approach?

If dead-end: what's the specific killer? (Static init order? Threading
model mismatch? Memory management semantics?)

---

## Question 5 — How to test static init order mitigation

phase6-architecture.md §7 designed mitigation: tiny C++ bootstrap shim
in pal/sharpos/ + explicit init order in host + preinit guards.

We never tested it. Spike used trap-only PAL — no real impls means
no real init order to violate.

### Concrete concern

phase6-arch claims:
> CoreCLR's C++ static initializers могут call PAL до PAL_Initialize.

How do we verify which PAL functions are actually called from static
init context? Adding logging to traps shows runtime calls (which are
inside main()) but not pre-main static init. Need different
instrumentation.

### Strategies considered

1. Replace traps with logging stubs that don't abort. Run Hello.dll,
   collect first 1000 PAL calls. Anything between dlopen and
   `coreclr_initialize` is static-init-context.
2. Use LD_DEBUG=symbols when running corerun. ld.so logs all symbol
   resolutions in init order.
3. Parse __libc_init_array section of libcoreclr.so for initializer
   functions, statically determine PAL call graph from each.

### What we want from sage

Recommended approach to actually generate the static-init-context PAL
call list. We need this data to scope the preinit-safe shim correctly
(currently it's specced as "GetLastError + spinlock + GetCurrentThreadId
fallback" — guesses, not measurements).

---

## Question 6 — libsharposhost.a integration with pal/sharpos/

Current state: two disconnected binaries.

- `OS/src/PAL/SharpOSHost/SharpOSHost.csproj` — produces
  libsharposhost.a (NativeAOT static archive, 4 smoke functions).
- `dotnet-runtime-sharpos/src/coreclr/pal/sharpos/` — produces
  libcoreclrpal.a (trap stubs).

They don't link to each other. The "host C-ABI line" exists only in
docs.

### Open questions

1. **At what build step do they connect?** SharpOS repo publishes
   libsharposhost.a + sharposhost.h. CoreCLR fork imports both. But:
   - libsharposhost.a has its own NativeAOT runtime baked in
     (libRuntime.WorkstationGC.a, libbootstrapperdll.o, etc., per
     Branch A linker line). Linking libsharposhost.a into libcoreclr.so
     means CoreCLR's libstdc++/libgcc + NativeAOT's libstdc++compat
     must coexist.
   - Symbol conflicts expected: 5-15 per phase6-arch §13, but no
     measurement yet.

2. **Preinit-safe spinlock — C# or C++?** phase6-arch §7 says C++
   shim. But Invariant 1 ("no C/C++ in SharpOS repo") means this C++
   code lives in CoreCLR fork repo. Where exactly? In pal/sharpos/
   (yes, but separated from forwarder code so it's clear what's
   preinit-safe vs HOST-forwarded).

3. **`SharpOSHost_GetApiTable` callable from static init?** If table
   acquisition itself requires NativeAOT runtime to be initialized
   (which static init context cannot guarantee), the function-table
   pattern is broken before it starts.

### What we want from sage

Concrete linker-level integration plan. Specifically:
- Where in build pipeline libsharposhost.a is consumed by libcoreclr.so?
- How do we manage the two NativeAOT-runtime + CoreCLR-runtime
  collision?
- Is the function-table pattern actually safe across the
  static-init / NativeAOT-not-initialized boundary?

---

## Question 7 — C++ runtime dependencies leak

Spike showed PAL pulls in C++ standard library symbols (`__cxa_*`,
`__gxx_personality_v0`, `_ITM_*`, `_CONTEXT::operator=` member function).

phase6-arch §9 anticipates "libstdc++/libc++ subset" as Phase 6
external dependency. We assumed "subset" is small. Spike didn't
measure — it just linked against full system libstdc++ via `-lstdc++`.

### Open question

For SharpOS bare-metal target (not Linux WSL spike), libstdc++ doesn't
exist. We need either:
- Port libstdc++ subset into CoreCLR fork (sage estimate: 5-10 KLOC).
- Reduce PAL's C++ usage to compile without libstdc++ (kill
  `_CONTEXT::operator=` member, kill `__cxa_*`-emitting features in
  vm/, etc.). Risky — touches upstream code heavily.

### What we want from sage

Realistic libstdc++ subset for SharpOS bare-metal. Which features are
actually used by CoreCLR? Are there no-libstdc++ build configurations
upstream we can borrow from (e.g., for embedded / kernel-mode
hosting)?

If "no, libstdc++ port is mandatory" — that's a 6-month subproject.
We need to know now to scope Phase 6.

---

## Question 8 — Hot path discovery methodology

Currently we know:
- Link-time PAL surface (from `nm -D --undefined-only`).
- First runtime PAL call (from trap fire on first abort).

Don't know:
- Functions 2-N on startup hot path (until we implement #1 and see #2).
- Per-function call frequency (which are critical for performance vs
  rare).
- Static-init-context calls (separate question 5).
- Calls during JIT compilation phase vs steady-state execution.

### What we want from sage

Recommended instrumentation pattern. Replace `abort()` with structured
log + counter, collect ~5-10 minute trace. What signals does this
extract?

Specifically: is there an existing CoreCLR profiling hook (telemetry,
ICorProfiler, etc.) that gives PAL-call frequency without us building
custom instrumentation?

---

## Summary — what we need from sages

Five concrete decisions to unblock progress:

1. **Continue / pause / pivot** — Question 1.
2. **Sequence of spec vs more implementation** — Question 2.
3. **EH path A vs B vs C** — Question 3.
4. **NativeAOT runtime reuse: viable or dead-end** — Question 4.
5. **libsharposhost.a integration plan** — Question 6.

Three fact-finding asks where we don't know how to measure:

6. **Static init context PAL calls** — Question 5.
7. **libstdc++ subset realistic for bare-metal** — Question 7.
8. **Hot path discovery instrumentation** — Question 8.

---

## Self-context for sages who didn't read prior rounds

- **SharpOS** — experimental unikernel в C#. NativeAOT + NoStdLib.
  Targets UEFI on x64. Single developer, ~1.5 year project so far.
  Phase 1 (managed exception handling, ACPI, timers) closed.
- **Goal** — host CoreCLR (and via it, Roslyn + PowerShell) inside
  SharpOS. Hosted-tier IL execution через JIT.
- **Invariant 1**: SharpOS source tree is 100% C#. No `.c`/`.cpp`/`.h`/
  `.s` files committed. Allowed exception: external dependencies in
  separate fork repo (i.e., CoreCLR fork CAN have C/C++ — just stays
  out of SharpOS repo).
- **Architecture** (per phase6-arch.md): host/guest split. SharpOS repo
  publishes libsharposhost.a (NativeAOT-built, C-ABI exports). CoreCLR
  fork has new pal/sharpos/ that calls into SharpOSHost_* primitives.
- **Spike phase** — Linux WSL2 environment (not bare-metal yet). Goal
  was "Hello World runs through CoreCLR + stub PAL on Linux", as
  de-risk before committing to 9-18 months Phase 6 implementation.

References:
- `done/phase2-sage-queries.md` — Round 1 (initial PAL extraction).
- `done/phase2-sage-queries-followup.md` — Round 2 (technical drill-down).
- `done/phase2-sage-queries-round3.md` — Round 3 (host/guest validation).
- `done/phase6-architecture.md` — synthesized architecture spec.
- `plan.md` — phase plan with Phase 2 / Phase 6 deliverables.
