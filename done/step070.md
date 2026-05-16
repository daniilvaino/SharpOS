# Step 70 — JIT→R2R unwind: stale pCodeInfo fix (managed-EH pillar fully closed) (OPEN)

**Status:** OPEN. The **unwinding root** the user repeatedly named ("размотка
не работает") is **found, fixed, and proven by probes** — committed here.
The coverage battery is still not 100%: System.Text.Json (test 11) now fails
on a **separate, new** fault (#GP in JIT'd code), a different domain — next
session.

## Что починено (Fix #10)

Symptom: managed `throw`/`try`/`catch` works (step 69), but System.Text.Json
crashes `#PF RIP=0`. Long chain of FALSE leads (honestly retracted): "GC
region_allocator" and "R2R code-range not registered" — both were
**stale-symbolization artifacts** (the unikernel image base shifts every
build; symbols resolved against a wrong base → phantom functions).

Real root (probe `[VU]` in `Thread::VirtualUnwindCallFrame`, base-validated):
during the **2nd pass** of a **nested** exception (System.Text.Json: a C++
`EEMessageException` unwinds cleanly, then a managed exception thrown by a
**class cctor** invoked via R2R `DelayLoad_Helper`/`DynamicHelperFixup`), the
unwind walks JIT frames and reaches a **JIT→R2R transition**. At that point
`StackFrameIterator` handed `VirtualUnwindCallFrame` a **stale `EECodeInfo`**
— still the *previous JIT frame's* (valid, but for a different method). The
`else` branch (`pCodeInfo` non-NULL & `IsValid()`) used its
`GetFunctionEntry()`/`GetModuleBase()` for the R2R PC `0x500008403672`,
i.e. the **JIT heap's** `RUNTIME_FUNCTION` + base `0x500009D90000` applied to
a CoreLib-R2R frame → `RtlVirtualUnwind` produced **Rip=0** →
`ControlPC=0` → `CallCatchFunclet` → `GetCodeManager()` on `m_pJM=NULL` →
`call 0`.

Proof Fix #10 works (post-fix probes, base-validated 0x1C19D000):
- `[VU] preRip=0x500008403672 imgBase=0x500008090000` (correct CoreLib-R2R
  base, was the JIT heap base) `… postRip=0x500009DC9019` (valid, was 0).
- `[SN] preUnwPC=0x500008403672 … postPC=0x500009DC9019` (valid, was 0).
- `[CCF]/[CCF-ENTER] crawlPC=0x500009DB9B45 ci.valid=0x1 pCodeMgr=0x13AD70`
  (valid catching frame; `call 0` gone). `HW fault: RIP=0` no longer occurs.

### The fix
`src/coreclr/vm/stackwalk.cpp`, `Thread::VirtualUnwindCallFrame`
(extends step-69 Fix #3, TARGET_SHARPOS, the `#if`/`#else` guard):

```cpp
if (pCodeInfo == NULL || !pCodeInfo->IsValid()
    || (UINT_PTR)PCODEToPINSTR(pCodeInfo->GetCodeAddress()) != (UINT_PTR)PCODEToPINSTR(uControlPc))
```

i.e. a `pCodeInfo` whose code address does not match the PC being unwound is
treated as unusable → re-resolve via `RtlLookupFunctionEntry(uControlPc)`
(C# SehUnwind `StaticTableLookup` → the correct CoreLib-R2R RUNTIME_FUNCTION
and image base). One-line condition extension; only `stackwalk.cpp` changes.

## Lessons (recorded in agent memory)

- **imageBase must be re-validated per run** (it shifts every build) BEFORE
  symbolizing or theorizing. Building theories on a stale base produced a
  full day of false leads (GC, R2R-range). Sanity-symbolize 2 known
  addresses first.
- **When the user repeatedly asserts the failure category** ("размотка не
  работает"), treat it as a strong prior; deepen strictly inside it; branch
  to another subsystem only with hard, validly-symbolized evidence.
- A non-fatal-assert (step-69 Fix #6) can **mask** a real correctness
  `_ASSERTE` (`exceptionhandling.cpp:4353 "IP address must not be null"`);
  when "call 0 after an ignored assert", read *which* assert — it points at
  the root.

## Файлы

Fork (`dotnet-runtime-sharpos`, `sharpos/coreclr-port`):
`src/coreclr/vm/stackwalk.cpp` (Fix #10 — one condition extended).
Kernel (`c:/work/OS`, `main`): `done/step070.md` (this writeup). No kernel
code change in step 70 (Fix #10 is fork-only; Diagnostics.cs Verbose back to
committed `false`).

All step-70 diagnostic probes (`[VU]/[SN]/[P2]/[CCF]/[CCF-ENTER]` in
stackwalk.cpp/exceptionhandling.cpp, `[REF]` ceeload, `[R2R JCMI]`/
`[EECodeInfo INVALID]` jitinterface/codeman) reverted in 5b — verified no
probe markers remain.

## Что дальше (отдельный фронт)

System.Text.Json now fails differently: `HW fault: vec=13 (#GP)
RIP=0x500009DCF918` (valid JIT code, NOT 0), `RAX=RCX=0x0074696D694C6472`
(ASCII "…rdLimit") — JIT'd code dispatching through a corrupt pointer that
holds string data. This is a **JIT execution / dispatch** bug in the
reflection-heavy System.Text.Json path, NOT unwinding/EH. New investigation,
new session. The managed-EH pillar (project goal) is closed:
arith/string/Span/LINQ/Dict/generics/try-catch-finally-throw/nullable/
DateTime/Guid all green, plus nested-exception JIT→R2R unwind now correct.
