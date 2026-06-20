# EH subsystem audit — 2026-06

Reviewer's 13 questions, answered against current tree state. All citations are `file:line`. No builds were run; this is a code-read audit.

## Block 0 — fact-check

### Q1. CONTEXT layout + RestoreContextAsm FP/XMM scope

**Verdict:** CONFIRMED — CONTEXT struct ends at `Rip @ 0xF8`, no Xmm fields are declared; `MxCsr` IS declared at `0x34` but is NOT restored by `RestoreContextAsm`.

**Evidence:**

- `Context` struct declared `Size = 1232` but only fields through `Rip` at offset `0xF8` are mapped. Final field is at [`SehStructs.cs:106`](../OS/src/PAL/SharpOSHost/SehStructs.cs#L106). Explicit comment at [`SehStructs.cs:108-109`](../OS/src/PAL/SharpOSHost/SehStructs.cs#L108) acknowledges: "FP/XMM state lives at offset 0x100. We don't unwind those for the C++ EH path (volatile across calls); skip exact layout."
- `MxCsr` field declared at `FieldOffset(0x34)` in [`SehStructs.cs:69`](../OS/src/PAL/SharpOSHost/SehStructs.cs#L69).
- `RestoreContextAsm` shellcode emitter is `EmitRestore` at [`SehDispatch.cs:996-1039`](../OS/src/PAL/SharpOSHost/SehDispatch.cs#L996). It restores ONLY:
  - EFlags (via `pushfq`/`popfq`) [`SehDispatch.cs:1007-1011`](../OS/src/PAL/SharpOSHost/SehDispatch.cs#L1007)
  - GP regs Rbx, Rbp, Rsi, Rdi, R8..R15, Rdx, Rax, Rsp, Rcx [`SehDispatch.cs:1013-1036`](../OS/src/PAL/SharpOSHost/SehDispatch.cs#L1013)
  - No `ldmxcsr`, no `movaps`/`movups`, no XMM, no segment regs, no debug regs touched.
- Likewise the symmetric `EmitCapture` at [`SehDispatch.cs:922-960`](../OS/src/PAL/SharpOSHost/SehDispatch.cs#L922) does NOT capture MxCsr nor XMM; it sets `ContextFlags = 0x100003` (CONTROL|INTEGER, no FLOATING_POINT bit) at [`SehDispatch.cs:925-927`](../OS/src/PAL/SharpOSHost/SehDispatch.cs#L925).

Note: `Context.CONTEXT_FULL = CONTEXT_CONTROL | CONTEXT_INTEGER | CONTEXT_FLOATING_POINT` ([`SehStructs.cs:56`](../OS/src/PAL/SharpOSHost/SehStructs.cs#L56)) is used by `BuildContextFromInterruptFrame` at [`HwFaultBridge.cs:332`](../OS/src/Boot/EH/HwFaultBridge.cs#L332) — i.e. the constant claims FP coverage but the struct (1232 bytes raw, zeroed) carries no FP fields and no restorer reads them. ContextFlags is a label, no behaviour follows from it.

### Q2. Personality return value checks

**Verdict:** CONFIRMED — first pass checks ONLY `ExecuteHandlerMarker`; second pass discards the return value entirely.

**Evidence:**

- First-pass dispatcher loop at [`SehDispatch.cs:490-520`](../OS/src/PAL/SharpOSHost/SehDispatch.cs#L490): `int disp = fn(rec, (void*)newFrame, searchCtx, dc);` then the only test is `if (disp == ExceptionDispositionExt.ExceptionExecuteHandlerMarker)` at [`SehDispatch.cs:503`](../OS/src/PAL/SharpOSHost/SehDispatch.cs#L503). All other values (`ExceptionContinueExecution=0`, `ExceptionContinueSearch=1`, `ExceptionNestedException=2`, `ExceptionCollidedUnwind=3`) fall into the "no match, advance frame" path. `ExceptionCollidedUnwind` is NEVER handled.
- Second pass (`RtlUnwind`) at [`SehDispatch.cs:842-845`](../OS/src/PAL/SharpOSHost/SehDispatch.cs#L842):
  ```
  delegate* unmanaged<...> fn = (delegate* unmanaged<...>)personality;
  fn(rec, (void*)newFrame, uc, dc);
  ```
  The return value is not assigned, not compared, not logged. `ExceptionCollidedUnwind`, `ExceptionNestedException` etc are silently dropped. Implications for collided unwind discussed in Q11.

## Block 1 — XMM

### Q3. UWOP_SAVE_XMM128 frequency in image

**Verdict:** UNKNOWN — needs a built image scan. No `.efi`/`.dll` was inspected. However, RyuJIT codegen WILL emit them (see Q4) when xmm6+ is used; in stock CoreCLR for amd64 Windows this occurs in every native VM frame that spills xmm6-xmm15 (e.g. compiled helpers, hand-written asm). Static-link of the fork CoreCLR (per memory `project_firmware_free_coreclr`) means SharpOS image inherits all of CoreCLR's MSVC-generated `.pdata`/`.xdata`, which includes XMM saves wherever the host C++ compiler used non-volatile XMM.

**To close:** dump `.pdata`/`.xdata` of `OS/.qemu/esp/EFI/BOOT/BOOTX64.EFI` and grep the UNWIND_INFO codes for opcode 8/9. Cheap script: walk RUNTIME_FUNCTION table → for each UNWIND_INFO, iterate 2-byte codes, count opcodes 8 and 9.

What can be asserted from source:
- Our decoder treats both opcodes as "consume slots, do not restore" at [`SehUnwind.cs:651-655`](../OS/src/PAL/SharpOSHost/SehUnwind.cs#L651) and [`SehUnwind.cs:680-681`](../OS/src/PAL/SharpOSHost/SehUnwind.cs#L680). So even if they are common in the image, today they are dead w.r.t. register restoration.

### Q4. ILC/RyuJIT xmm6+ save emission

**Verdict:** CONFIRMED — RyuJIT DOES emit `UWOP_SAVE_XMM128` / `UWOP_SAVE_XMM128_FAR` whenever it spills a callee-saved float register (XMM6..XMM15).

**Evidence:** [`unwindamd64.cpp:466-484`](../dotnet-runtime-sharpos/src/coreclr/jit/unwindamd64.cpp#L466):
```
if (genIsValidFloatReg(reg)) {
    *codedSize     = (USHORT)(offset / 16);
    code->UnwindOp = UWOP_SAVE_XMM128;
}
...
code->UnwindOp = (genIsValidFloatReg(reg)) ? UWOP_SAVE_XMM128_FAR : UWOP_SAVE_NONVOL_FAR;
```
Reg-number encoding at [`unwindamd64.cpp:486-498`](../dotnet-runtime-sharpos/src/coreclr/jit/unwindamd64.cpp#L486) takes `reg - XMMBASE`. RyuJIT is the codegen behind both JIT and AOT (ILC composes RyuJIT for object emission); same emitter, same opcodes.

**Minimal repro outline:**
- Method `A()`: declare `double d = Math.Sqrt(arg);` capturing a value into a callee-saved xmm reg (force via long live range crossing a call). Implementation needs JIT to choose XMM6+ (use enough live floats to exhaust XMM0-5: ~6 simultaneously-live `double` locals across a call).
- Inside the live range, call `B()` which throws `InvalidOperationException`.
- `A()` has `try { B(); ... } catch { return (int)d; }`. After catch, the value held in XMM6 was never restored (Q1). Expected: `d` reads garbage / pre-call value depending on whether catch funclet itself touched XMM6.
- Caveat: in practice CoreCLR Windows EH path normally relies on the OS unwinder which DOES honour XMM saves; our path replaces the OS unwinder and silently drops them, so this repro should be reliably wrong on SharpOS.

### Q5. KNCP xmm consumers in fork

**Verdict:** REFUTED for GcInfoDecoder (no XMM consumption). PARTIAL for the broader fork (other consumers exist but are inactive in our scenarios today).

**Evidence:**
- Grep over [`dotnet-runtime-sharpos/src/coreclr/vm/gcinfodecoder.cpp`](../dotnet-runtime-sharpos/src/coreclr/vm/gcinfodecoder.cpp) for `Xmm` returns zero matches. GcInfoDecoder reads only GP register pointers via `pCurrentContextPointers->Rax..R15` (the integer half of KNCP at offset 0x80+).
- Our `RecordSpill` writes only the GP pointer half at [`SehUnwind.cs:462-467`](../OS/src/PAL/SharpOSHost/SehUnwind.cs#L462), explicitly skipping the leading 0x80 bytes of XMM pointers. Comment at [`SehUnwind.cs:456-461`](../OS/src/PAL/SharpOSHost/SehUnwind.cs#L456) documents the layout intentionally.
- Therefore XMM-pointer fill is NOT required for current GC root reporting; required only once we (a) have GC tracking of XMM-spilled OBJECTREFs (not done) or (b) need actual XMM register restoration on resume (separate concern; Q1).

## Block 2 — EstablisherFrame

### Q6. Linked personality routines + EstablisherFrame use

**Routines actually present in the SharpOS image:**

| Personality | Location | Source/dispatch |
|---|---|---|
| `__C_specific_handler` | [`CrtAndEhStubs.cs:84-176`](../OS/src/PAL/SharpOSHost/CrtAndEhStubs.cs#L84) | Our C# `[RuntimeExport]` |
| `__CxxFrameHandler3` | [`CxxFrameHandler.cs:151-295`](../OS/src/PAL/SharpOSHost/CxxFrameHandler.cs#L151) | Our C# `[RuntimeExport]` |
| `__CxxFrameHandler4` | [`CxxFrameHandler4.cs:200-335`](../OS/src/PAL/SharpOSHost/CxxFrameHandler4.cs#L200) | Our C# `[RuntimeExport]` |
| `ProcessCLRException` | [`exceptionhandling.cpp:569`](../dotnet-runtime-sharpos/src/coreclr/vm/exceptionhandling.cpp#L569) | Linked from fork CoreCLR static lib |
| `__GSHandlerCheck` | — | **NOT linked** (zero matches in tree) |

**EstablisherFrame usage:**

- `__C_specific_handler`: USES `establisherFrame` as the base passed to `__finally` funclet ([`CrtAndEhStubs.cs:132`](../OS/src/PAL/SharpOSHost/CrtAndEhStubs.cs#L132)) and to filter funclet ([`CrtAndEhStubs.cs:157`](../OS/src/PAL/SharpOSHost/CrtAndEhStubs.cs#L157)). Scope-table records are FUNCTION-RVA-based (`ripRva` compared at [`CrtAndEhStubs.cs:120`](../OS/src/PAL/SharpOSHost/CrtAndEhStubs.cs#L120)), but funclet ABI uses establisherFrame. **CONFIRMED most-vulnerable handler** if our `EstablisherFrame` computation diverges from MSVC convention.
- `__CxxFrameHandler3`: USES `pEstablisherFrame` only as the argument forwarded to dtor funclets ([`CxxFrameHandler.cs:243`](../OS/src/PAL/SharpOSHost/CxxFrameHandler.cs#L243) → [`CxxFrameHandler.cs:323`](../OS/src/PAL/SharpOSHost/CxxFrameHandler.cs#L323) `action(establisherFrame);`). State lookup is RVA-based, not establisher-relative.
- `__CxxFrameHandler4`: USES `pEstablisherFrame` for dtor funclet ABI AND as base for catch-object slot computation at [`CxxFrameHandler4.cs:489`](../OS/src/PAL/SharpOSHost/CxxFrameHandler4.cs#L489): `byte* objSlot = (byte*)establisherFrame + objOffset;`. **Most semantically demanding consumer** — divergence in EstablisherFrame here corrupts the C++ exception object pointer for `DtorWithObj`/`DtorWithPtrToObj`.
- `ProcessCLRException`: reads `pDispatcherContext->EstablisherFrame` (e.g. [`exceptionhandling.cpp:1919`](../dotnet-runtime-sharpos/src/coreclr/vm/exceptionhandling.cpp#L1919)). Used as the canonical frame identifier for managed funclet dispatch — divergence here would mis-identify which frame is "this" funclet's parent (affects GC reporting and finally targeting).

Frames using `__C_specific_handler` in our image — present and reachable (memory `project_step106_av_via_pal_seh`: `Object::ValidateInner`'s `AVInRuntimeImplOkayHolder` uses C-style EX_TRY which compiles to `__C_specific_handler`). So Q6's vulnerability is not theoretical.

### Q7. UWOP_SET_FPREG + EHANDLER candidates

**Verdict:** UNKNOWN — needs image scan. Code expectations:

- RyuJIT emits `UWOP_SET_FPREG` (and the CLR-only `UWOP_SET_FPREG_LARGE` on Unix) at [`unwindamd64.cpp:420`](../dotnet-runtime-sharpos/src/coreclr/jit/unwindamd64.cpp#L420). All managed methods with frame-pointer (debug builds, methods with localloc / sufficiently complex frames) emit it.
- Our decoder honours it at [`SehUnwind.cs:622-627`](../OS/src/PAL/SharpOSHost/SehUnwind.cs#L622):
  ```
  ulong fpVal = ReadReg(ctx, frameReg);
  ctx->Rsp = fpVal - (ulong)(frameOffset * 16);
  ```
  Correct per AMD64 ABI.
- Any function emitted by MSVC with `__try`/`__except`/`__finally` AND a frame pointer (alloca / `__chkstk` past 4 KiB / debug) will combine `SET_FPREG` with `EHANDLER`. CoreCLR has many such functions (`Object::ValidateInner` chain, JIT helpers, threadpool worker entry).

**To close:** scan image, list functions whose UNWIND_INFO has both `UNW_FLAG_EHANDLER` set AND a `UWOP_SET_FPREG` code. These are the precise repro targets for semantic-divergence testing of EstablisherFrame.

### Q8. Our CxxFrameHandler establisherFrame base

**Verdict:** CONFIRMED self-consistent — both handlers take the `establisherFrame` parameter from the dispatcher AS GIVEN; they do not rebuild it from CONTEXT internally.

**Evidence:**
- FH3 [`CxxFrameHandler.cs:155`](../OS/src/PAL/SharpOSHost/CxxFrameHandler.cs#L155): `void* pEstablisherFrame` parameter is the only frame-base used; it is forwarded unchanged at [`CxxFrameHandler.cs:243`](../OS/src/PAL/SharpOSHost/CxxFrameHandler.cs#L243) → [`CxxFrameHandler.cs:323`](../OS/src/PAL/SharpOSHost/CxxFrameHandler.cs#L323).
- FH4 [`CxxFrameHandler4.cs:204`](../OS/src/PAL/SharpOSHost/CxxFrameHandler4.cs#L204): same; forwarded at [`CxxFrameHandler4.cs:482`](../OS/src/PAL/SharpOSHost/CxxFrameHandler4.cs#L482), [`CxxFrameHandler4.cs:489`](../OS/src/PAL/SharpOSHost/CxxFrameHandler4.cs#L489), [`CxxFrameHandler4.cs:495`](../OS/src/PAL/SharpOSHost/CxxFrameHandler4.cs#L495).
- The dispatcher itself populates `dc->EstablisherFrame = newFrame` ([`SehDispatch.cs:477`](../OS/src/PAL/SharpOSHost/SehDispatch.cs#L477)) where `newFrame` is the unwinder's output for the parent SP (NOT the canonical "frame base" per MSVC PROC-info — it is simply `Rsp` AFTER unwinding the prolog).
- Risk classification: since OUR FH3/FH4 are both fed our OWN `newFrame` AND OUR own funclets, the contract is internally consistent — they agree by mutual convention. Risk is "only foreign handlers" — i.e. ProcessCLRException (foreign — fork CoreCLR), and the `__C_specific_handler` clients inside fork CoreCLR (which expect EstablisherFrame to be MSVC-canonical "frame base of the function").

The reviewer's concern downgrades: divergence ONLY matters where ntdll-compiled C++ frames are walked. ProcessCLRException's establisher use (e.g. [`exceptionhandling.cpp:1917`](../dotnet-runtime-sharpos/src/coreclr/vm/exceptionhandling.cpp#L1917) FixupDispatcherContext) is the primary exposed surface.

## Block 3-4 — async

### Q9. HW exception CONTEXT build

**Verdict:** CONFIRMED — CONTEXT is built manually in HAL C#, no asm trampoline with its own unwind info.

**Evidence:**
- IDT trampoline saves regs into `InterruptFrame*` on kernel stack; entry dispatched to managed via `Idt.Dispatch` → `HwFaultBridge.DispatchTrap` ([`HwFaultBridge.cs:61`](../OS/src/Boot/EH/HwFaultBridge.cs#L61)).
- `BuildContextFromInterruptFrame` at [`HwFaultBridge.cs:326`](../OS/src/Boot/EH/HwFaultBridge.cs#L326) zeros 1232 bytes then assigns each GP/segment/EFlags field from `InterruptFrame*` — all pure C#, no `__asm`.
- The HW fault path therefore does not extend the unwind chain with a private asm-trampoline frame; the SEH walker starts from the interrupted `Rip`/`Rsp` directly (modulo our manual setup). Points 3-4 confirmed as "not bugs today" for the HW-fault-origin case.

### Q10. UNWIND_INFO version=2 / UWOP_EPILOG presence

**Verdict:** REFUTED — RyuJIT emits `Version = 1` only; no `UWOP_EPILOG` codes generated by our codegen.

**Evidence:**
- [`unwindamd64.cpp:168`](../dotnet-runtime-sharpos/src/coreclr/jit/unwindamd64.cpp#L168): `func->unwindHeader.Version = 1;`
- Multiple asserts confirm Version == 1 invariant at [`unwindamd64.cpp:255,327,388,454,770`](../dotnet-runtime-sharpos/src/coreclr/jit/unwindamd64.cpp#L255).
- Grep for `UWOP_EPILOG` over [`jit/unwindamd64.cpp`](../dotnet-runtime-sharpos/src/coreclr/jit/unwindamd64.cpp): only the print-debug case ([`unwindamd64.cpp:678`](../dotnet-runtime-sharpos/src/coreclr/jit/unwindamd64.cpp#L678)). No emit site.
- MSVC-compiled CoreCLR C++ object code MAY contain v2 records (cl.exe is free to emit them). To be safe our decoder reserves a conservative `return 2;` for UWOP_EPILOG at [`SehUnwind.cs:646-650`](../OS/src/PAL/SharpOSHost/SehUnwind.cs#L646) (1- vs 2-slot ambiguity unresolved).

**Reviewer's point 6 reduced to:** add `assert(version == 1)` at the decoder top, then the conservative `+2` becomes dead code. If MSVC emits v2 we will hit the assert on first walk-through such a function — turning a silent semantic risk into a noisy diagnostic.

## Block 5 — collided unwind

### Q11. ProcessCLRException → ExceptionCollidedUnwind reachability

**Verdict:** CONFIRMED reachable; **CONFIRMED currently silently broken** in SharpOS (Q2 returns are dropped).

**Evidence:**
- Two return sites in fork:
  1. `HijackHandler` returning `ExceptionCollidedUnwind` at [`exceptionhandling.cpp:1929`](../dotnet-runtime-sharpos/src/coreclr/vm/exceptionhandling.cpp#L1929) — fires when a thread was hijacked for ThreadAbort/GCSuspend and the original ip was whacked.
  2. `FixRedirectContextHandler` returning it at [`exceptionhandling.cpp:2275`](../dotnet-runtime-sharpos/src/coreclr/vm/exceptionhandling.cpp#L2275) — fires when context was redirected (typically GC-mode stub).
- Reachability today:
  - HijackHandler: requires Thread::Hijack path. Cooperative scheduler (memory `project_phase_e1_e4_first_pingpong`) doesn't use OS hijack, but ThreadAbort and reflection-driven `Thread.Interrupt` could trigger it. Live cohort: low-but-nonzero.
  - FixRedirectContextHandler: requires "redirected stub stack frame" (`GetCONTEXTFromRedirectedStubStackFrame` [`exceptionhandling.cpp:2267`](../dotnet-runtime-sharpos/src/coreclr/vm/exceptionhandling.cpp#L2267)) — used during GC suspension that interrupted managed code. Likely unreached unless we run GC-on-cooperative-suspend (we don't yet for hosted CoreCLR).
- Throw-from-finally is the most common managed-side trigger. CoreCLR's funclet machinery would issue a nested raise through ProcessCLRException; that path uses `FixupDispatcherContext` to chain through — and the SAME `ExceptionCollidedUnwind` mechanism is how the OS unwinder is told to restart with a new context.
- **Our dispatcher drops the disposition**: Q2 shows neither first nor second pass interprets `ExceptionCollidedUnwind`. The CONTEXT that `FixupDispatcherContext` produced into `pDispatcherContext->ContextRecord` IS observable (we pass our own `searchCtx`/`uc` as the ContextRecord, so mutations land in our buffer), but the FRAME-RESTART semantic (Cutler's "OS takes new context, restarts dispatching on this call frame") is NOT implemented. We just advance.

**Bug class:** silent — the unwind continues from a partially-fixed-up context, and any nested raise that depended on collided-unwind to back up and re-search will instead skip frames. This combines with Q12's scenario.

### Q12. SehProbe design: try/finally rethrow over C++ frame

**Existing probe:** `CollidedUnwind()` at [`EhProbe.cs:710-727`](../OS/src/Kernel/Diagnostics/EhProbe.cs#L710) — managed-only throw inside `finally` inside `try/catch`. Currently gated `Probes.EhCollidedUnwind = true` at [`Probes.cs:53`](../OS/src/Kernel/Diagnostics/Probes.cs#L53). This expects `1501` if the inner replacement exception ("b") wins.

**Status of existing probe:** memory record claims it passes (`step 11 GATE: L15 == 1501`). But this is the all-NativeAOT, in-kernel path through `DispatchEx` — it does NOT route through `ProcessCLRException` / the PAL SEH dispatcher's collided-unwind logic.

**Proposed reviewer-requested probe (NEW):**
- Setup: hosted CoreCLR managed method `A()` with `try { B(); } finally { throw new Exception("b"); }`, where `B()` is a P/Invoke whose native side is a C++ function `void N()` with an MSVC `try { ManagedCallback(); } catch (...) { ... }` block (compiles to `__CxxFrameHandler3` or FH4 personality).
- Throw chain: inner managed throw inside `B()` triggers second-pass unwind through C++ frame; on the way past, `A`'s finally runs and re-throws.
- Expected by Windows OS semantics: ProcessCLRException returns `ExceptionCollidedUnwind`, OS restarts dispatch with new exception record, C++ catch in `N()` catches the replacement "b".
- Expected on SharpOS today:
  1. Q2 dispatcher does not honour `ExceptionCollidedUnwind` → original "a" continues, "b" is lost.
  2. OR: 64-frame loop limit at [`SehDispatch.cs:400`](../OS/src/PAL/SharpOSHost/SehDispatch.cs#L400) / [`SehDispatch.cs:792`](../OS/src/PAL/SharpOSHost/SehDispatch.cs#L792) trips after re-walking from the bottom if FixupDispatcherContext rewinds the searchCtx.
  3. OR: `RtlUnwind: target not found` HALT at [`SehDispatch.cs:860-861`](../OS/src/PAL/SharpOSHost/SehDispatch.cs#L860) if the original `targetFrame` is now below the new context's SP.
- Most likely first observed symptom: **HALT in RtlUnwind** ("target not found") because the rewound context's SP no longer matches the originally-chosen target frame.

## Cross-cutting

### Q13. EH probe inventory + source × catcher matrix

**Probes defined in [`EhProbe.cs`](../OS/src/Kernel/Diagnostics/EhProbe.cs):**

| Probe | Method | Gate | Source world | Catcher world | Status |
|---|---|---|---|---|---|
| L1 TryFinally no-throw | (compiled implicit) | `EhTryFinallyNoThrow` | — | managed | green (no-throw) |
| L2 TryCatch no-throw | (implicit) | `EhTryCatchNoThrow` | — | managed | green (no-throw) |
| L8 TryCatchWithThrow | inline at [`EhProbe.cs:87`](../OS/src/Kernel/Diagnostics/EhProbe.cs#L87) | `EhTryCatchWithThrow` | managed-throw | managed | green |
| L9 RethrowChain | [`EhProbe.cs:498`](../OS/src/Kernel/Diagnostics/EhProbe.cs#L498) | `EhRethrowChain` | managed-throw | managed | green |
| L10 TryCatchFinally | [`EhProbe.cs:527`](../OS/src/Kernel/Diagnostics/EhProbe.cs#L527) | `EhTryCatchFinally` | managed-throw | managed (+finally) | green |
| L11 FilterClause | [`EhProbe.cs:559`](../OS/src/Kernel/Diagnostics/EhProbe.cs#L559) | `EhFilter` | managed-throw | managed (filter) | green |
| L13 HwFault | [`EhProbe.cs:111`](../OS/src/Kernel/Diagnostics/EhProbe.cs#L111) | `EhHwFault` | HW-fault | managed | green |
| L14 StackTraceCheck | [`EhProbe.cs:690`](../OS/src/Kernel/Diagnostics/EhProbe.cs#L690) | `EhStackTrace` | managed-throw | managed | green |
| L15 CollidedUnwind | [`EhProbe.cs:710`](../OS/src/Kernel/Diagnostics/EhProbe.cs#L710) | `EhCollidedUnwind` | managed-throw-in-finally | managed | green (kernel path only — Q12) |
| L16 MultiFrameFinally | [`EhProbe.cs:606`](../OS/src/Kernel/Diagnostics/EhProbe.cs#L606) | `EhMultiFrameFinally` | managed-throw | managed (cross-frame finally) | green |
| L17 MultiFrameStackTrace | [`EhProbe.cs:641`](../OS/src/Kernel/Diagnostics/EhProbe.cs#L641) | `EhMultiFrameStackTrace` | managed-throw | managed | green |

**Source × catcher matrix (kernel + hosted CoreCLR):**

| ↓ source / catcher → | managed (kernel-AOT) | managed (hosted CoreCLR) | native C++ (CoreCLR) | uncaught (panic) |
|---|---|---|---|---|
| managed-throw (kernel-AOT) | L8/L9/L10/L11/L14/L15/L16/L17 ✅ | — *not tested* | — *not tested* | implicit |
| managed-throw (hosted CoreCLR) | — | implied by census `Regex` etc (step107) ✅ | implied by step103 msc-throws-as-managed ✅ | implicit |
| HW-fault (#PF/#GP) | L13 ✅ | step106 census ✅ | step106 (Object::ValidateInner catches AV) ✅ via __C_specific_handler | falls through to Idt panic |
| P/Invoke boundary (managed→native→throw) | — *NOT tested* | — *NOT tested* | — *NOT tested* | unknown |
| finally-rethrow (managed) | L15 ✅ kernel | — *NOT tested* via hosted CoreCLR | — *NOT tested* over C++ frame (Q12) | unknown |
| C++ throw (`throw 42`) from native | n/a | — *NOT tested* | implied by FH3/FH4 implementations existing but no direct probe | unknown |

**Visibility verdict — cells "green by fact":**
- Kernel managed→managed paths (whole L8–L17 column 1).
- HW-fault → managed kernel catch (L13).
- HW-fault → managed hosted CoreCLR (step106 milestone).
- HW-fault → native C++ catch via `__C_specific_handler` inside CoreCLR (step106).
- Managed throw → managed catch inside hosted CoreCLR (step107 Regex / msc-throw, ConcurrentDictionary, etc).

**Cells "green by absence-of-failure" (not actively probed):**
- P/Invoke boundary in ALL directions — there's no probe like "managed throws across a P/Invoke unmanaged → managed transition".
- finally-rethrow OVER a C++ frame (Q12) — kernel-only path tested, hosted path untested.
- Pure native `throw 42` (no managed in stack) — no probe exists.
- managed-throw originating in hosted CoreCLR caught by kernel-AOT code — cross-tier catching is not exercised.

## Summary of risk levels after audit

**Confirmed bugs (latent, can be triggered today):**
- **Q2 + Q11**: dispatcher silently discards `ExceptionCollidedUnwind` in BOTH passes. Any path that exercises `HijackHandler` / `FixRedirectContextHandler` / nested-raise-from-finally over a CoreCLR frame will skip the OS-mandated dispatch restart. Expected symptom: HALT in `RtlUnwind` ("target not found") OR silently-lost replacement exception.
- **Q1 + Q4**: XMM6+ is NOT restored on resume despite RyuJIT emitting `UWOP_SAVE_XMM128`. Any catch handler whose parent function holds a `double`/SIMD value in a callee-saved XMM across the throw will read stale data. Repro target: float-heavy method with try/catch (Q4 outline).

**Confirmed gaps (no bug today, latent):**
- **Q6 + Q8**: `__C_specific_handler` and `__CxxFrameHandler4` are the only handlers using `EstablisherFrame` semantically; our own FH3/FH4 are self-consistent; ProcessCLRException + foreign `__C_specific_handler` clients are the exposed surface if our `newFrame` ever diverges from MSVC's canonical frame base.
- **Q12**: no probe covers throw-in-finally OVER a C++ frame via the hosted CoreCLR personality chain — risk surfaced as Q11.
- **Q13**: P/Invoke-boundary cells and cross-tier (kernel↔hosted CoreCLR) catching are untested.

**Refuted concerns:**
- **Q5**: GcInfoDecoder does NOT consume KNCP.Xmm pointers — leaving the first 0x80 bytes of KNCP unfilled is safe for current GC.
- **Q9**: HW-fault CONTEXT is built in pure C# without an asm trampoline; no hidden frame with private unwind info to worry about.
- **Q10**: RyuJIT only emits `UNWIND_INFO Version=1` and zero `UWOP_EPILOG`. Conservative `+2` slot accounting is dead code for our own AOT/JIT output; only relevant if MSVC-emitted CoreCLR object code uses v2. Replace with `assert(version != 2)` at decoder top to convert silent-risk → loud-diagnostic.

**Open questions needing runtime probing / image scan:**
- **Q3**: actual count of `UWOP_SAVE_XMM128` / `UWOP_SAVE_XMM128_FAR` in built `BOOTX64.EFI`. Needs a small `.pdata` walker script (RUNTIME_FUNCTION → UNWIND_INFO → opcode count).
- **Q7**: enumerate functions in the image with both `UWOP_SET_FPREG` AND `EHANDLER` for EstablisherFrame divergence repros.
- **Q12**: implement the proposed throw-in-finally-over-C++-frame probe to verify the Q11 collided-unwind drop empirically.
