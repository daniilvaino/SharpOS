# Step 80 — §11 root-cause investigation: walker needs CoreCLR FrameChain, not RtlVirtualUnwind port

**Status:** Diagnostic-only step. Identified the actual root cause of
§11 (unique uncatchable HARD-PANIC class in `coreclr-hosted-limits.md`)
and corrected the Phase D scope. Walker code unchanged; documentation
updated; diagnostic scaffolding committed in-place (gated `const bool`
flags). Next step is the actual implementation (Phase D execution).

## Methodology — cheap-detector first, per memory

Added per-opcode UNWIND_INFO trace inside `SehUnwind.ApplyUnwindInfo`
+ per-frame brackets inside `SehDispatch.DispatchException`, both
gated by compile-time `const bool` (ILC dead-codes when false):

- [`SehUnwind.cs:45`](../OS/src/PAL/SharpOSHost/SehUnwind.cs#L45) —
  `TraceUnwind` + helpers `TuHeader/TuCode/TuFinalize` print every
  opcode application with `rsp` delta and **first 48 bytes** of each
  function body (for identification by prolog signature when no
  symbols available).
- [`SehDispatch.cs:40`](../OS/src/PAL/SharpOSHost/SehDispatch.cs#L40) —
  `Trace` prints `[seh] search frame Rip=…`, `[seh] pers bytes:…`
  (first 32 bytes of personality routine), and a `[seh] pers
  MUTATED ctx` detector that fires if personality writes `Rip/Rsp/
  Rbp` through `dc->ContextRecord`.

Both flipped back to `false` after the investigation; infrastructure
stays for the next debugging cycle.

## Three repros, three lessons

### v1 — `Thread.Sleep(0)` — **wrong trigger**

Goes through PAL stub `SleepEx` which is a **direct `SharpOSHost_
Panic`** ([`Diagnostics.cs:80`](../OS/src/PAL/SharpOSHost/Diagnostics.cs#L80)),
not a C-SEH raise. No walker run, no §11. Docs phrase «все упираются
в `SwitchToThread`» was misleading — `Thread.Sleep(0)` shortcircuits
to `SleepEx` and never reaches `SwitchToThread`.

### v2 — `try { throw } catch { throw new… }` without outer catch — **not §11**

Walker walked **15+ frames cleanly** including `__C_specific_handler`
scope matching, then ended at *walked out of image at 0x1E1690A4*.
This is plain **unhandled-exception walk-off** — the second throw has
no enclosing handler by design, walker correctly searches up the
stack until it leaves registered-code regions. Not a walker bug.

Important secondary finding: the supposedly-funclet kill frame at
RVA `0xCA2FB3` (which I initially fingered as `CallEHFunclet`-style
funclet trampoline by hand-asm prolog signature) **walked through
fine** in v2 — confirming walker handles its UNWIND_INFO correctly.

### v3 — `try { new Socket(...) } catch (Exception e) {…}` — **real §11**

Walker reached the same frame at RVA `0xCA2FB3`, applied UNWIND_INFO
correctly, but `*rsp` at the computed entry_rsp slot was a **stack
address** (`0x277C900`, not a code address) — fails `IsValidIp` →
`invalid Rip` → HALT, never reached user catch.

## Kill-frame identification

48-byte signature `53 56 55 48 8B EC 48 8B D9 8B 4B 08 F7 C1 01 00 …
74 01 50 48 8B 33 48 8D 34 CE 48 83 EE 08 FF 36 FF C9 75 F6 48 8B
43 10 48 8B 0C 24 F3 0F` matched **byte-for-byte** to
[`CallDescrWorkerAMD64.asm`](../dotnet-runtime-sharpos/src/coreclr/vm/amd64/CallDescrWorkerAMD64.asm) —
`CallDescrWorkerInternal`, the CoreCLR handwritten variadic-call
trampoline. Custom personality
`CallDescrWorkerUnwindFrameChainHandler` confirmed via
[`exceptionhandling.cpp:2114`](../dotnet-runtime-sharpos/src/coreclr/vm/exceptionhandling.cpp#L2114).

Initial hypothesis ("`CallEHFunclet` body-RBP overwrite, à la
step-71") was **wrong**:

- `CallEHFunclet` has 6 pushes in prolog (`FUNCLET_CALL_PROLOGUE`
  macro), our kill frame has 3 — signatures don't match.
- `CallDescrWorkerInternal`'s body does NOT modify RBP (no `mov rbp,
  X` instruction in the dumped bytes).
- The `ctx->Rbp = 0x277C840` walker sees IS correct end-of-prolog
  RSP, computed by `set_frame rbp, 0` directive (= `mov rbp, rsp`).

## Real root cause

CoreCLR's **stub-mechanism** for managed-to-unmanaged transitions
(P/Invoke, reflection invoke, helper-method transitions, etc.)
does NOT use ordinary `call`/`ret`. Instead, transition stubs push
a `Frame*` structure onto the stack (per-thread linked list, head
in `Thread::m_pFrame`) **in place of** a code return-address. The
real continuation context for the unwind lives **inside the
`Frame*` object**, not on the stack slot the prolog "saved-retaddr"
position points to.

Our walker reads `*rsp` and gets that `Frame*` pointer — looks like
a stack-resident `self-pointer`, fails IsValidIp.

The personality routine `CallDescrWorkerUnwindFrameChainHandler`
is **no-op in search pass** (only does FrameChain cleanup during
the unwind pass), and **does NOT modify the dispatcher context** —
verified by the `[seh] pers MUTATED ctx` detector silent throughout
the trace. In real Windows, the OS dispatcher knows to walk
`Thread::m_pFrame` automatically after `ProcessCLRException` returns
`ExceptionContinueSearch` for stub frames. Our `SehDispatch` does
not.

## Phase D scope — corrected

The fix is **NOT**:
- ❌ "Port `RtlVirtualUnwind` properly" — walker's `RtlVirtualUnwind`
  is correct; UNWIND_INFO is correct; opcode interpretation matches
  CoreCLR reference (`unwinder/amd64/unwinder.cpp:796`).
- ❌ "Implement PE/Win64 DispatcherContext / personality redirect
  protocol" — `CallDescrWorkerUnwindFrameChainHandler` doesn't use
  it; in real Windows the OS dispatcher handles FrameChain natively.

The fix **IS**: walker must read `GetThread()->m_pFrame` and walk
the `Frame*` linked list when it encounters a stub frame. Concrete
steps:

1. Import layout-compatible declarations for `Thread` (just the
   `m_pFrame` field) and the `Frame*` family (`InlinedCallFrame`,
   `HelperMethodFrame`, `TransitionFrame`, `PInvokeCalliFrame`,
   etc.).
2. Detect stub frames by personality address whitelist
   (`CallDescrWorkerUnwindFrameChainHandler`,
   `FixRedirectContextHandler`,
   `ReverseComUnwindFrameChainHandler`).
3. On detection: dereference `GetThread()->m_pFrame`, invoke that
   frame's `UpdateRegDisplay(REGDISPLAY*)` virtual to obtain the
   real caller Rip/Rsp/Rbp, restore context, continue unwinding.

This is the same approach CoreCLR's own
`ExceptionTracker::ProcessOSExceptionNotification` + `StackFrame-
Iterator` use, minus all the thread-coordination (`GCX_COOP_NO_DTOR`,
`Thread::EnterCoopMode`, etc.) which our single-thread substrate
doesn't need.

## Not threading

Critical distinction: this needs the **struct** `Thread*` + `Frame*`
family — NOT a scheduler, NOT real threads, NOT TLS, NOT context-
switching. In single-thread mode there is one singleton `Thread*`
(`GetThread()` already returns it correctly in the fork), and
`m_pFrame` is maintained automatically by CoreCLR's stub macros.
The gap is purely walker-side **reader integration**.

So `plan.md` Phase D → Phase E ordering remains valid, and the size
estimate drops from initial "3-4 weeks" guess to ~**1-2 weeks**
sequential. Phase E (real threads / scheduler / SwitchToThread)
stays independent and unaffected.

## Files

- [`OS/src/PAL/SharpOSHost/SehUnwind.cs`](../OS/src/PAL/SharpOSHost/SehUnwind.cs) —
  `TraceUnwind` flipped back to `false`; `TuHeader/TuCode/TuFinalize`
  scaffolding kept.
- [`OS/src/PAL/SharpOSHost/SehDispatch.cs`](../OS/src/PAL/SharpOSHost/SehDispatch.cs) —
  `Trace` flipped back to `false`; pers-bytes dump + `pers MUTATED
  ctx` detector kept.
- [`docs/coreclr-hosted-limits.md`](../docs/coreclr-hosted-limits.md) —
  §11 description rewritten with root-cause analysis.
- [`docs/eh-model.md`](../docs/eh-model.md) — Tier C §11 section
  updated with same correction.

## Deferred

- **Phase D execution** — actual walker FrameChain integration.
  Concrete first task: import `Thread`/`Frame*` layout-compatible
  declarations from `dotnet-runtime-sharpos/src/coreclr/vm/frames.h`,
  verify they match the fork build, add a no-op detector that prints
  `[seh] stub-frame personality=0x… m_pFrame=0x…` whenever walker
  encounters a stub frame — confirms `GetThread()->m_pFrame`
  actually has the expected linked list. Then layer in the actual
  `UpdateRegDisplay()` call.
- **Phase C4** — flip `ExitBootServicesExperiment` to default boot.
- Document feedback memory: «не «port RtlVirtualUnwind» — Phase D is
  Thread::m_pFrame integration» (correct prior memory
  `project_jit_frame_seh_unwind_pillar`).
