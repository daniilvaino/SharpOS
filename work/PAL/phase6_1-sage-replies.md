# Phase 6.1 sage replies — 3-way audit overview consultation

**Context:** [phase6_1-3way-audit-overview.md](phase6_1-3way-audit-overview.md) was sent to both sages. Both responded substantively.

**Synthesis status:** User reviewed both, accepted 5-layer reframe (sage 2's main contribution), made calls on disagreements. Internal plan updated accordingly.

---

## Sage 1 reply (summary)

Validation of 3-way audit as epistemic upgrade. Numbers confirm previous estimates (~50-80 must-implement is right order). Empirical Phase 6.1 profile reduction of -13 symbols confirms feature trimming не главный инструмент bounding.

Refined positions:

### Q1 CRT — (c) hybrid with bootstrap order discipline

Pure managed (b) has bootstrap chicken-egg: CoreCLR calls `memset` before managed runtime ready. Split:

- **Bootstrap-critical** (memset/memcpy/memmove): compiler intrinsics (`__builtin_*`), not managed
- **Math/string/COM IIDs**: managed wrappers via `[UnmanagedCallersOnly]`, OK after kernel-tier init
- **File/process/CRT internals**: ABORT_FATAL

### Q2 EH personality — different contracts, not aliases

Phase 1 .pdata unwinder ≠ `__C_specific_handler` / `__CxxFrameHandler3`. Last two are MSVC SEH / C++ EH personality routines на top of unwinder.

Recommendation:
- 6.1.a/b: ABORT_FATAL stubs OK if unreachable
- **Verifiable concern**: `__CxxFrameHandler3` may fire в static init (vm/ has TU-level constructors с try). Dedicated investigation step needed before stubbing.
- 6.1.c+: real personality needed

### Q3 6.1.a/b/c decomposition agreed

Plus refinement: between 6.1.b (Hello World) and 6.1.c (try/finally), add throw-without-catch substep (6.1.c-pre).

### Q4 6.1.5 intermediate layer — yes

ZeroGC for 6.1, single-thread compatible Workstation GC for 6.1.5, full threading for 6.2.

### Q5 Win-only sanity — additional watchpoints

- Verify dummyprovider covers both ETW AND EventPipe
- Check FEATURE_BASICFREEZE intentionally on/off
- FEATURE_PORTABLE_SHUFFLE_THUNKS correct path
- PROFILING_SUPPORTED off for kernel deployment

### Sage 1 added new questions

**Q6**: Phase 1 unwinder extension timing relative to 6.1.c
**Q7**: GC heap init under ZeroGC — verified functional?
**Q8**: cost-benefit curve for additional config tightening

---

## Sage 2 reply (summary)

Strategic reframe: stop thinking "585 functions" — think **5 layers**, each with different strategy. This changes mental model significantly.

### The 5-layer decomposition

| Layer | Content | Strategy | Where lives |
|---|---|---|---|
| **L1: Guest support** | CRT mechanical (memset/strcpy), minipal, math, operator new/delete | C/C++ allowed (Invariant 1 exception для submodule). Steal libm subset, не managed wrappers. | CoreCLR fork support lib |
| **L2: CoreCLR internal link** | Template instances (TGcInfoEncoder), VMToOSInterface implementations, GcInfoEncoder | **Fix archive composition** — these missing because we accidentally excluded subdirectories. Не stub. | CoreCLR fork build setup |
| **L3: Platform capability** | Memory (VirtualAlloc), TLS, time, sync primitives | **Only this layer** becomes `SharpOSHost_*`. True kernel surface. | SharpOS host C# |
| **L4: Disabled / cold** | Debugger IPC, profiler, COM interop, registry, Watson, named pipes, EventPipe | Fatal stubs или no-op. **Never implement.** | CoreCLR fork |
| **L5: EH personality** | `__C_specific_handler`, `__CxxFrameHandler3`, `_CxxThrowException` | Separate D13 work stream. NOT aliasing Phase 1 unwinder — different levels. | New code on top of Phase 1 |

### Refined positions per question

**Q1 CRT**: Hybrid **guest-support-first**. Mechanical CRT в CoreCLR fork support lib (C/C++ OK there), NOT через C# wrappers wholesale. OS-shaped CRT (file I/O) ABORT_FATAL для 6.1.

**Q2 EH**: `__C_specific_handler` / `__CxxFrameHandler3` are personality routines, не aliases для Phase 1 unwinder. For 6.1.a/b — fatal stubs OK if unreachable. For 6.1.c — real personality story нужна.

**Q3 scope**: Agree с 6.1.a/b/c. Add **6.1.0 — link/image integration gate** before initialize.

**Q4 transition**: Yes, **6.1.5 stabilization milestone**:
- 6.1.0 — final link, no unresolveds, .pdata retained
- 6.1.a — coreclr_initialize → S_OK
- 6.1.b — JIT trivial method
- 6.1.c — managed EH smoke (try/finally)
- 6.1.5 — single-thread hosted-tier с ZeroGC, multiple managed calls, stable trace
- 6.2 — scheduler/threading/concurrent GC/Roslyn

**Q5 Win-only**: Mostly sane to exclude. Watchpoints:
1. Не потерять coreclr_initialize / JIT / GC required paths
2. COM IIDs (GUID_NULL, IID_IUnknown) в intersection — это constants, не COM interop. Корректно.

### Sage 2 additions

**minipal**: Treat as guest support layer. Если unresolved — link/port `libaotminipal.a`, не писать 27 replacements.

**C++ template instances**: Fix archive composition. Не stub.

---

## User decisions (synthesis)

After review:

1. **Math** ⟶ steal libm subset (agree с sage 2 — bootstrap order avoid managed). Either compiler builtins или small C subset. Not C# wrappers.

2. **minipal** ⟶ pragmatic: check `libaotminipal.a` coverage first. If covers 27 symbols ⟶ link. If gaps ⟶ document, implement what's missing.

3. **Template instances** ⟶ investigation step: find which CMake targets we accidentally excluded that contain these symbols. Likely в `nativeaot/Runtime/` или `libs-native/` subdir which we skipped wholesale на TARGET_SHARPOS.

4. **5-layer mental model** ⟶ agreed.

5. **EH** ⟶ "красиво прокинуть через нашу машинарию" — sage 2 не отрицает что Phase 1 unwinder primitives используются ALSO для `__CxxFrameHandler3`. Это не aliasing (разные contracts), но `__CxxFrameHandler3` сидит **сверху** Phase 1 unwinder. Архитектура:
   ```
   __CxxFrameHandler3 (our impl, personality)
   ├── decode MSVC C++ EH funclet metadata
   ├── match catch handlers by type
   └── call SharpOS Phase 1 unwinder primitives
       ├── RUNTIME_FUNCTION lookup
       ├── UNWIND_INFO decode
       └── context restoration
   ```

---

## Next steps (internal plan)

1. **L2 investigation**: demangle C++ symbols в `sharpos_win_not_wsl.txt`, find where defined в vanilla Win build, identify which target/subdir мы skipped. Concrete output: list of CMakeLists changes to recover lost targets.

2. **L1 minipal verify**: dump `libaotminipal.a` symbols, compare с our 27 `minipal_*` externals. If full coverage ⟶ link recipe. If partial ⟶ list gaps.

3. **Re-build + re-audit after L2 fix**: see if 264 mangled / 89 C++ surface reduces когда archive composition fixed.

4. **L1 math**: identify which math functions actually called (28 в intersection_all3 + extras), steal subset.

5. **L4 fatal stubs**: enumerate cold-path symbols, batch ABORT_FATAL stubs in pal/sharpos/ shim layer.

6. **L3 design**: from remaining surface, enumerate true platform capabilities → `SharpOSHost_*` API spec.

7. **L5 design**: `__CxxFrameHandler3` personality routine on Phase 1 unwinder (D13 extension).

Each step is concrete + measurable. No more speculation, only empirical investigation + data-driven decisions.
