# Step 71 — native-origin managed-EH: catch-funclet RBP-clobber fix (CLOSED)

**Status:** CLOSED. The post-step-70 coverage-battery failures
(`checked (overflow throw)` hard #GP panic; reflection-mode
`System.Text.Json` hard #GP panic) are root-caused and fixed with a
single targeted fork change. Battery now runs to completion:
**`[t] OK checked (overflow throw)`, pass=16 fail=1, no HW fault, no
unhandled, `return 42`**. The one remaining `[t] FAIL` (reflection-mode
`System.Text.Json roundtrip`) was downgraded by the **same** fix from a
fatal panic to a **catchable, non-fatal** `SEHException` — a separate,
narrow frontier deferred to its own step.

## Symptom

`checked { int.MaxValue + 1 }` throws `OverflowException`; the test's
`catch (OverflowException) { ovf = true; }` runs but `ovf` reads `false`
in the continuation → spurious `throw new Exception("no overflow")` → a
deterministic `#GP` (`RAX=RCX` = ASCII "…System.G…", a coreclr.dll rodata
GCConfig-knob-name) → unhandled → kernel panic, battery aborts. The green
`try/catch/finally/throw` test (managed `throw new InvalidOperationException`)
worked. Differentiator: **exception origin** — managed `IL_Throw` (green)
vs **native** `COMPlusThrow(kOverflowException)` from the JITed overflow
helper (red).

## Root

`ExInfo::UpdateNonvolatileRegisters` (exceptionhandling.cpp, AMD64
`UPDATEREG(Rbp)`) overwrites the catch-resume `CONTEXT->Rbp` with
`*pRegDisplay->pCurrentContextPointers->Rbp`. For a **native-origin**
exception the C#-side SEH unwind produces a **wrong** saved-Rbp pointer
for the catching frame — observed shift **0x50** vs the RBP the catch
funclet actually ran with. `CallEHFunclet` runs the funclet with
`rbp = pCurrentContext->Rbp` (proven correct: a managed `fixed`-`&local`
address splitter showed the funclet's `&local` == the pre-try main-body
`&local`). The catch funclet and the post-catch continuation **share the
catching method's frame**; the continuation then resumes with the
clobbered RBP → every frame-relative parent local (`ovf`) reads garbage
while static-field writes survive (absolute address). Managed-origin is
unaffected — its `pCurrentContextPointers->Rbp` is correct.

Decisive evidence (one run): `&probe` before-try == catch == `0x1FE93558`
(funclet wrote `0x3333…` there correctly), after-try == `0x1FE93508`
(Δ 0x50, garbage); native `CONTEXT.Rbp` `0x1FE95D20` pre-
`UpdateNonvolatileRegisters` → `0x1FE95CD0` post (Δ 0x50, exact match).

## The fix

`src/coreclr/vm/exceptionhandling.cpp`, `CallCatchFunclet`, guarded
`#if defined(TARGET_SHARPOS) && !defined(DACCESS_COMPILE)`:

```cpp
bool   __sosRbpFix   = (pHandlerIP != NULL && pvRegDisplay && pvRegDisplay->pCurrentContext);
uint64_t __sosSavedRbp = __sosRbpFix ? (uint64_t)pvRegDisplay->pCurrentContext->Rbp : 0;
ExInfo::UpdateNonvolatileRegisters(pvRegDisplay->pCurrentContext, pvRegDisplay, FALSE);
if (__sosRbpFix)
    pvRegDisplay->pCurrentContext->Rbp = (DWORD64)__sosSavedRbp;
```

Preserve the catch-funclet's (proven-correct) RBP across
`UpdateNonvolatileRegisters` for the catch-resume path. Managed-origin =
no-op (`saved == restored`). One site; behavior-preserving elsewhere.
**Upstream-true-root for future hardening:** the native-origin unwind's
bad `pCurrentContextPointers->Rbp` in `StackFrameIterator`/C# `SehUnwind`
(step-69/70 family) — the present fix is the correct local guarantee
(funclet & continuation must share one frame) and is sufficient.

## Lessons (recorded in agent memory)

- A native-origin (`COMPlusThrow`) catch losing parent-**local** writes
  while **static** writes survive == funclet/continuation frame mismatch,
  not allocator/newobj/jump-stub/type. The decisive cheap probes:
  managed `unsafe fixed (T* p=&local)` printing `&local` at before-try /
  in catch / after-try (before==catch≠after ⇒ continuation frame shifted,
  NOT funclet-write-target); native `CONTEXT.Rbp` logged **pre and post**
  `UpdateNonvolatileRegisters` (the entry value is not enough — catch the
  FINAL resume context). static-vs-frame-local splits the two classes.
- ~20 probes; every surface hypothesis (newobj / allocator / RhpNew /
  jump-stub reloc / nonvolatile-R12-R15 / establisher / FixContext-RBP)
  was falsified by **green==red byte-identical** data. Demote a
  hypothesis the moment the discriminating signal contradicts it; "the
  probe didn't fire" / "green==red" is itself strong evidence. Validate
  imageBase per run before symbolizing; never theorize on raw stack-slot
  residue (≠ live call frame).
- The earlier "ASCII-string-used-as-MethodTable / R2R import fixup /
  jump-stub" framings were downstream symptoms, honestly retracted.

## Файлы

Fork (`dotnet-runtime-sharpos`, `sharpos/coreclr-port`):
- `src/coreclr/vm/exceptionhandling.cpp` — the fix (one guarded block in
  `CallCatchFunclet`).
- `src/coreclr/pal/sharpos/winapi_shim.cpp` — weak `SharpOSHost_DebugWrite`
  fallback retained (link infra; also referenced by pre-existing
  `crt_imp_stubs.cpp`; same pattern as the weak `DebugPrint`/
  `DebugPrintHex` defs). No diagnostic code remains; method.cpp /
  methodtable.cpp / object.cpp / jithelpers.cpp / gchelpers.cpp /
  jitinterface.cpp fully reverted to upstream.

Kernel (`c:/work/OS`, `main`):
- `done/step071.md` (this writeup).
- `OS/src/Boot/EH/HwFaultBridge.cs` — `FaultClassify` retained as the
  permanent regression oracle for this signature (#GP, RAX=RCX ASCII,
  RSP-not-in-GC-heap, spray run-length). Fault-time only, ungated, no
  alloc; behaviorally inert on the happy path.

Scaffold `work/normal-hello/` (gitignored): restored to canonical;
`System.Text.Json roundtrip` left enabled to document the now-non-fatal
frontier B.

Consult thread: `work/sage2-request.md` (§0–16) — the Sage-2 dialogue
that converged on the variant-B (continuation frame) diagnosis and the
RBP-clobber root.

## Что дальше (отдельный фронт — Frontier B)

Reflection-mode `System.Text.Json` (`ReflectionEmitCachingMemberAccessor`)
raises a native structured exception (`[seh] RaiseException code=
0xE06D7363` C++ EH / `0xE0434352` CLR-exception) that — thanks to this
fix — is now caught and surfaces to managed code as a generic
`System.Runtime.InteropServices.SEHException` ("External component has
thrown an exception."), `[t] FAIL` non-fatal, battery survives (16/17).
Open question for the next step: why the reflection-emit path's exception
is not unwrapped to its real managed type/message under `TARGET_SHARPOS`
(capture `SEHException.ErrorCode`/`HResult`; decide if it's an
incomplete `0xE0434352`→managed unwrap or a genuine native C++ throw
where a managed exception is expected). NOT a regression of step 71 —
the managed-EH-for-native-origin pillar is closed.
