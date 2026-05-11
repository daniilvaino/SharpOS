# Phase 6 — CoreCLR integration architecture

Synthesized from three rounds of sage queries (rounds 1-2-3 в
`done/phase2-sage-queries*.md`). Captures architectural decisions made
до start of Phase 2 spike. Living document — refined post-spike as
real data comes in.

## Core architectural decision: host/guest split

```
┌─────────────────────────────────────────────────────────────────┐
│ SharpOS repo (this one)                  Invariant 1: C# only    │
│                                                                  │
│   kernel-tier (managed C#)                                       │
│   native-tier apps (managed C#)                                  │
│                                                                  │
│   ┌────────────────────────────────────────────────┐             │
│   │ HOST                                            │             │
│   │  C# C-ABI exports via [UnmanagedCallersOnly]    │             │
│   │  Function table pattern (SharpOSHost_GetApiTable) │           │
│   │  Wraps existing kernel APIs                      │            │
│   │  Published as libsharposhost.a (NativeAOT lib)   │            │
│   └─────────────────┬──────────────────────────────┘             │
│                     │ C-ABI boundary                              │
└─────────────────────┼────────────────────────────────────────────┘
                      │ POD only, status codes only,
                      │ no exceptions, no C++ objects,
                      │ no managed refs crossing
┌─────────────────────┼────────────────────────────────────────────┐
│ dotnet-runtime-sharpos fork    C/C++ allowed natively            │
│                     │                                            │
│   ┌─────────────────┴─────────────────────────────┐              │
│   │ pal/sharpos/                                   │              │
│   │  C++ implementation                            │              │
│   │  Reuses Linux PAL bookkeeping logic            │              │
│   │  Bottom calls swapped to SharpOSHost_*         │              │
│   │  Owns CoreCLR object bookkeeping (handles, etc)│              │
│   └────────────────────────────────────────────────┘              │
│                                                                  │
│   pal/sharposhost-backend/                                       │
│      linux.c        ← Phase 2 spike POSIX backend (~500 LOC)     │
│                                                                  │
│   vm/, gc/, jit/    ← upstream CoreCLR с минимальными patches    │
└──────────────────────────────────────────────────────────────────┘
```

**Architectural precedent**: rump kernels (NetBSD anykernel research,
Antti Kantee PhD thesis 2012). Same pattern — runtime kernel code
running on minimal C-ABI surface (rumpuser ≈ 30 functions ≈ our HOST
API). Other precedents: MirageOS/Solo5, Genode framework, Microsoft
Drawbridge (defunct).

**This is NOT** embedded language runtime pattern (JVM/Lua/V8/Python C
API). Those are host-driver→guest-worker. We are guest-calls-down-to-host
для services. Different pattern, different precedents.

## HOST API surface estimate

Target: 30-50 stable primitives, NOT 1:1 mapping to PAL (~150).

Decomposition by area:

| Area | Functions | Count |
|---|---|---|
| Memory | `ReservePages`, `CommitPages`, `DecommitPages`, `ReleasePages`, `ProtectPages`, `QueryPages`, `GetPageSize`, `FlushICache` | 6-8 |
| Threading | `CreateThread`, `ExitThread`, `GetCurrentThreadId`, `YieldThread`, `Sleep`, `SetThreadName`, `JoinThread`, `SuspendThread`, `ResumeThread` | 8-9 |
| TLS | `TlsAlloc`, `TlsFree`, `TlsGet`, `TlsSet` | 4 |
| Sync (futex-shape) | `WaitOnAddress`, `WakeAddress`, `WakeAddressAll` | 3 |
| Time | `QueryPerformanceCounter`, `QueryPerformanceFrequency`, `GetTickCount64`, `GetSystemTimeUtc`, `Sleep` | 4-5 |
| File/Console I/O | `Open`, `Close`, `Read`, `Write`, `Seek`, `GetFileInfo`, `StatPath`, `ConsoleWrite`, `EnumerateDirectory` | 8-10 |
| Module loading | `LoadModule`, `GetExport`, `UnloadModule` (или skip if static-link clrjit) | 0-3 |
| Process info | `GetSystemInfo`, `GetProcessId`, `Abort` | 3 |
| **Fault delivery callbacks** | `RegisterFaultHandler`, `UnregisterFaultHandler`, `GetCurrentCpuContext`, `RegisterThreadStartCallback`, `RegisterThreadExitCallback` | 4-5 |
| **Total Phase 2 spike** | | **30-45** |
| Phase 6 minimum hosted | | 50-70 |
| Phase 7 Roslyn comfort | | 70-90 |

Convergence: both Sage 1 (39-46) and Sage 2 (30-45) agree on this size.

## C-ABI boundary rules

**Crossing allowed**:
- POD types (pointers, integers)
- Plain structs с fixed layout
- Enum-like flag values
- Function pointers for callbacks
- Status codes (HRESULT-style)

**Crossing NOT allowed**:
- C++ exceptions
- C++ objects (pass through opaque pointers OR convert to POD)
- Templates
- RAII ownership semantics
- TLS destructors as C++ objects
- Managed object references (GC heap separation)

**Status code enum**:
```c
typedef enum SharpOSHost_Status {
    SHARPOSHOST_OK = 0,
    SHARPOSHOST_INVALID_ARGUMENT = 1,
    SHARPOSHOST_NO_MEMORY = 2,
    SHARPOSHOST_NOT_SUPPORTED = 3,
    SHARPOSHOST_TIMEOUT = 4,
    SHARPOSHOST_INTERRUPTED = 5,
    SHARPOSHOST_FAULT = 6
} SharpOSHost_Status;
```

## Bidirectional callbacks pattern

GUEST registers function pointer table on init. HOST stores. HOST invokes
specific functions из table at well-defined points. Stable ABI, no
dynamic symbol resolution overhead.

```c
typedef struct PAL_Callbacks {
    uint32_t size;
    uint32_t version;

    // CPU fault → managed exception delivery
    int (*on_fault)(const SharpOSHost_ExceptionContext* ctx, void* user);

    // GC stack scan
    void (*on_async_suspend)(uint64_t thread_id, const CpuContext* ctx);

    // Thread lifecycle
    void (*on_thread_start)(uint64_t thread_id);
    void (*on_thread_exit)(uint64_t thread_id);

    // Optional: low-memory notification
    void (*on_low_memory)(int severity);
} PAL_Callbacks;

extern "C" int SharpOSHost_Initialize(const PAL_Callbacks* cb);
```

`SharpOSHost_Initialize` — first call, **MUST** complete before any other
HOST primitive used.

## C-ABI line for memory (concrete spec)

```c
// Address-space reservation, no physical backing yet
void* SharpOSHost_ReservePages(size_t size, size_t alignment, int* err);

// Allocate physical frames + map с protection
int SharpOSHost_CommitPages(void* addr, size_t size, int prot);

// Release physical frames, keep address-space reservation
int SharpOSHost_DecommitPages(void* addr, size_t size);

// Release address-space reservation entirely
int SharpOSHost_ReleasePages(void* addr, size_t size);

// Change protection of committed pages
int SharpOSHost_ProtectPages(void* addr, size_t size, int prot);

// Query mapping status (reserved/committed/protection bits)
int SharpOSHost_QueryPages(void* addr, SharpOSHost_PageInfo* info);
```

PAL keeps all Win32 RESERVE/COMMIT/RELEASE bookkeeping (~10 KLOC mature
logic in `pal/src/map/virtual.cpp`). HOST gives raw paging.

`pal/sharpos/map/virtual.cpp` ≈ `cp pal/src/map/virtual.cpp` + sed
substitution `mmap` → `SharpOSHost_ReservePages` etc. Tractable diff.

## Static init order — critical engineering decision

**Highest technical risk** in this architecture (per Sage 2).

Problem flow в Linux spike:
```
dlopen(libcoreclr.so)
  → CoreCLR's C++ static initializers
    → may call PAL functions before PAL_Initialize
      → pal/sharpos/ forwards к SharpOSHost_*
        → SharpOSHost = NativeAOT static archive
          → NativeAOT runtime НЕ initialized yet → undefined behavior
```

**Mitigation strategy**:

1. **Tiny C/C++ bootstrap shim в `pal/sharpos/`**:
   - Implements `GetLastError`/`SetLastError` storage natively
   - Implements basic spin mutex для preinit
   - Implements `GetCurrentThreadId` fallback
   - Forwards к SharpOSHost only после `g_host_ready = 1`

2. **Explicit init order в Linux spike host**:
   ```
   1. Link or load libsharposhost.a/so
   2. Call SharpOSHost_Initialize EXPLICITLY
   3. Set g_host_ready = 1 в pal/sharpos
   4. dlopen libcoreclr.so AFTER step 3
   5. coreclr_initialize
   ```

3. **Preinit guard** в каждом PAL→HOST call:
   ```c
   if (!g_host_ready)
       return SharpPal_PreInitFallbackOrFatal("VirtualAlloc");
   ```

For final SharpOS bare-metal: kernel guarantees init order
(SharpOS runtime → Host C-ABI table published → CoreCLR guest image
initialized).

**Preinit-safe functions** (implement in C++ inside pal/sharpos/, NOT
through SharpOSHost):
- GetLastError / SetLastError
- Interlocked* (intrinsics)
- Memory barriers
- Basic logging to stderr (Linux spike only)
- Spin lock primitives

## Iteration speed — function table pattern

**Function pointer table** instead of direct symbol exports:

```c
typedef struct SharpOSHost_Api {
    uint32_t size;       // sizeof(SharpOSHost_Api) at compile time
    uint32_t version;    // bumped on incompatible changes

    void* (*ReserveMemory)(...);
    int   (*CommitMemory)(...);
    // ... 30-50 function pointers ...

    // Padding / version 2 additions go here
} SharpOSHost_Api;

extern "C" const SharpOSHost_Api* SharpOSHost_GetApiTable(void);
```

**Why**: добавление primitive = bump table size + add pointer at end.
Не triggers full CoreCLR relink.

Iteration cost expected:
- libsharposhost.a rebuild: seconds-to-minutes
- pal/sharpos relink: seconds-to-minutes
- Full CoreCLR rebuild: 20-60+ min (only when shared headers touched)

## EH integration — DEFERRED to post-spike

Sage 1 explicitly **revised** Round 1 EH inversion advice. Win-x64 build
не композится cleanly с host/guest split (Windows build doesn't compile
PAL — `HOST_WIN32` hardcoded → no PAL files).

Three honest paths after spike:

| Path | Approach | Pros | Cons |
|---|---|---|---|
| A | linux-x64 + pal/sharpos/ + libunwind для JIT | Min risk, max PAL leverage | Two unwinders (Phase 1 для AOT, libunwind для JIT) |
| B | win-x64, NO PAL, SharpOSHost = Win32-shape | Single Phase 1 EH unwinder | SharpOSHost полностью другой design |
| C | linux-x64 + JIT patch для .pdata emit | Single unwinder, less SharpOSHost change | Frankenstein, не recommended |

**Decision postponed** until spike data shows:
- libunwind port effort (relevant for Path A)
- PAL emulation logic re-use ratio (relevant for Path B)

Spike uses Path A (linux-x64 standard) — minimum risk, focuses on
validating HOST contract.

## Migration plan для external dependencies

Per plan.md exception, external C/C++ allowed in CoreCLR fork. SharpOS
repo stays C# (Invariant 1).

| Component | Origin | Use case | Migration target |
|---|---|---|---|
| **CoreCLR** (vm/gc/jit) | external fork, minimal patches | Phase 6+ permanent | Stays C++ — out of scope to rewrite |
| **PAL implementation** (pal/sharpos/) | C++ in fork repo | Phase 6+ permanent | Stays C++ — это GUEST code |
| **libc** (musl) | submodule in fork | Phase 6 bootstrap | Replace incrementally; uж std/no-runtime/shared/ covers strings/math |
| **libstdc++/libc++ subset** | submodule in fork | Phase 6 bootstrap | Replace incrementally; rewrite using patterns в C-style alternatives |
| **libunwind** | submodule in fork OR skip | Phase 6 if needed | Stays as third-party; never migrate (specialized, low ROI) |
| **HOST primitives** | C# in SharpOS repo | Phase 6+ permanent | Pure C# from day 1 |

## Spike approach — concrete plan

Sage's recommended structure:

1. **Create `pal/sharpos/`** в CoreCLR fork — paralleling `pal/linux/`.
   Не modify `pal/linux/` — это упрощает upstream tracking.

2. **Source files** (mostly cp from pal/linux/ с bottom-call sed
   substitution):
   - `virtual.cpp` — based on Linux, raw mmap → SharpOSHost
   - `thread.cpp` — based on Linux, raw pthread → SharpOSHost
   - `synch*.cpp` — reuse logic, replace wait backend
   - `file.cpp` — simpler custom version
   - `time.cpp` — thin custom
   - `signal.cpp` — custom CPU fault integration

3. **Header**: handwritten `sharposhost.h` declaring HOST API surface
   (~40 declarations).

4. **Backend**: `pal/sharposhost-backend/linux.c` (~500 LOC POSIX
   implementation для spike).

5. **CMake glue**: new target/option `SHARPOS_PAL=ON`. Sage 2 gave
   concrete CMake patches (см. `done/phase2-sage-queries-followup.md`
   Query 4.1).

6. **Hello.dll**: minimal .NET 10 console app + direct host C wrapper
   (~80 lines). Force JIT через `COMPlus_*` env vars.

**Spike pass criteria** (all must trigger):
1. `SharpOSHost_Initialize` returns success
2. `coreclr_initialize` returns S_OK
3. JIT compiled `Program.Main` (visible через `COMPlus_JitDisasm`)
4. "hello" appears в stdout
5. Process exit code = 42
6. PAL summary shows no fatal stub hits

**Spike anti-criteria** (false success):
- "hello" but no JIT compile log → AOT path accidentally taken
- "hello" but `coreclr_initialize` not called → wrong entry point
- VirtualAlloc count > 10000 before Main → broken reserve/commit
- Repeat probe failures → wrong TPA list

## Open questions для post-spike resolution

1. EH integration path (A/B/C above) — based on actual measurements
2. Personality routine compat между Phase 1 unwinder и CoreCLR-emitted
3. C++ stdlib subset realistic size (~5-10 KLOC estimate)
4. NativeAOT static archive PIC compatibility (Linux first, SharpOS later)
5. Two managed runtimes coexistence — symbol conflicts (5-15 expected)
6. Function table iteration speed — real measurement
7. libcoreclr.so size — manageable? — first build will tell

## References

- `done/phase2-sage-queries.md` — Round 1 (initial PAL extraction)
- `done/phase2-sage-queries-followup.md` — Round 2 (technical drill-down)
- `done/phase2-sage-queries-round3.md` — Round 3 (host/guest validation)
- Antti Kantee, "Flexible Operating System Internals", PhD thesis 2012
  — rump kernel architectural twin
- CoreCLR source paths (in dotnet-runtime fork):
  - `src/coreclr/pal/inc/pal.h` — full API surface (will not be ours)
  - `src/coreclr/pal/src/map/virtual.cpp` — RESERVE/COMMIT bookkeeping
  - `src/coreclr/pal/src/thread/thread.cpp` — pthread mapping
  - `src/coreclr/vm/ceemain.cpp` — `EEStartupHelper` init order
  - `src/coreclr/dlls/mscoree/exports.cpp` — hosting API impl
  - `src/coreclr/hosts/inc/coreclrhost.h` — direct hosting declarations
