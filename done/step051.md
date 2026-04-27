# Step 51 — Phase 1 step 5.4: FindFirstPassHandler + class hierarchy match

## Контекст

Sub-step 5.4 of step 5 (sage 2 breakdown). Заменяет 5.3 ad-hoc walk-up в test seam на production `FindFirstPassHandler` — managed-side dispatcher decision: "какой frame и какая clause обработают exception". `RhpCallCatchFunclet` остаётся halt — мы verifying только first-pass logic без actual unwind.

Stock NativeAOT reference:
- `gc-experiment/dotnet-runtime/src/coreclr/nativeaot/Runtime.Base/src/System/Runtime/ExceptionHandling.cs:738-817`

## Решение

### `OS/src/Boot/EH/DispatchEx.cs`

Растущий dispatcher файл. В 5.4 — только `FindFirstPassHandler`. В 5.6+ добавится `DispatchEx` (orchestrator), `InvokeSecondPass` (5.7), filter handling (5.8).

**`FindFirstPassHandler(GcMethodTable* exceptionType, StackFrameIterator* iter) → FirstPassResult`**:

```csharp
while (framesWalked < MaxFrames) {
    if (CoffEhDecoder.EhEnumInit(iter->ControlPC, ...)) {
        uint codeOffset = ControlPC - methodStart;
        while (CoffEhDecoder.EhEnumNext(...)) {
            if (clause.Kind == Filter) continue;     // 5.5+ territory
            if (clause.Kind == Typed
                && codeOffset in [tryStart, tryEnd)
                && IsAssignableFromClass(clauseType, exceptionType))
            {
                return FirstPassResult { Found=true, HandlerAddress, IdxCurClause, ... };
            }
        }
    }
    if (!StackFrameIteratorOps.Next(iter)) break;
    framesWalked++;
}
return FirstPassResult { Found=false };
```

`FirstPassResult` struct содержит:
- `Found` bool
- `HandlerAddress` byte* (IP of catch funclet)
- `IdxCurClause` uint (index within frame's clause table — for ExInfo state)
- `TryRegionIdx` uint
- `FramesWalked` int (for diagnostics)
- `MethodStart` byte* + `CodeOffset` uint (for diagnostics)

### IsAssignableFromClass

Inlined version of `RhTypeCast_IsInstanceOfClass`:

```csharp
if (objType == targetType) return true;       // identity (most common)
for (int i = 0; i < 32; i++) {
    objType = objType->GetBaseType();
    if (objType == null) return false;
    if (objType == targetType) return true;   // class hierarchy match
}
```

Skipped (compared to stock): generic variance, cloned types, array → object special case. Will extend в Phase 6+ when hosted-tier loads variance-using BCL.

### Test seam wiring

`RhpTest_ThrowIngress` 5.3 ad-hoc walk-up replaced с `FindFirstPassHandler` call:

```csharp
GcMethodTable* exType = *(GcMethodTable**)exceptionPtr;   // first 8 bytes = MT
FirstPassResult fp = DispatchEx.FindFirstPassHandler(exType, &exInfo->FrameIter);

if (fp.Found) {
    Console.Write("MATCH: framesWalked=N handler=0x... idxCurClause=N");
    Console.Write("methodStart=0x... codeOffset=0x...");
} else {
    Console.Write("NO MATCH after framesWalked=N");
}
```

## Результат

С `EhIngressThrow=true`:

```
*** FindFirstPassHandler (5.4) ***
  exType=0x000000000E105708
  MATCH: framesWalked=1 handler=0x000000000E0ED166 idxCurClause=0
  methodStart=0x000000000E0ED150 codeOffset=0x0000000F
*** halting (5.4 first-pass probe) ***
```

**Все совпадает с 5.3** plus добавлены match-specific values:
- `framesWalked=1`, `handler=0x0E0ED166`, `methodStart=0x0E0ED150`, `codeOffset=0x0F` — exact match с 5.3 walk-up output. Same path through frames.
- `exType=0x0E105708` — non-null MT pointer в `.rdata` range (= MT of `InvalidOperationException`).
- `idxCurClause=0` — clause[0] of `IngressThrow`'s EH info (the only typed catch).

`IsAssignableFromClass` сработал на identity check — `clauseType == exceptionType` (catch'ит exact type). Hierarchy walk не triggert'ся в этом тесте, но реализован для derived-from-base catches.

С `EhIngressThrow=false` (default after verification): no regression, all probes L1-L7 + 5.3-A green, ELF apps + launcher работают.

## Файлы

### Новые

- `OS/src/Boot/EH/DispatchEx.cs` — `FindFirstPassHandler` + `IsAssignableFromClass`.
- `done/step051.md` — этот файл.

### Изменённые

- `OS/src/Boot/ExceptionEngine.cs` — replaced 5.3 ad-hoc walk-up в `RhpTest_ThrowIngress` with FindFirstPassHandler call.
- `OS/src/Kernel/Diagnostics/Probes.cs` — `EhIngressThrow` comment updated.

## Что дальше

Phase 1 progress: 4 + 4/6 of step 5.

**Sub-step 5.5** — `RhpCallCatchFunclet` shellcode. По sage 2 breakdown разбит на 5.5a (minimal — без unhijack/abort/validation) + 5.5b (stock-closer тail).

5.5a contract:
- Entry: `RCX = exception, RDX = handler IP, R8 = REGDISPLAY*, R9 = ExInfo*`.
- Restore nonvols + (eventually) XMM6-15 from REGDISPLAY's pNonvol slots.
- `RCX = REGDISPLAY->SP` (establisher frame), `RDX = exception`, call handler.
- Handler returns resume IP в RAX.
- Pop ExInfo head chain.
- `mov rsp, REGDISPLAY->SP; jmp rax` — non-local transfer to resume IP.

Smoke 5.5: standalone test seam (без real throw) — manually populate REGDISPLAY + handler IP, call RhpCallCatchFunclet, tiny catch funclet writes `s_probe = 701` and returns continuation label. Verify continuation reached + s_probe == 701.

После 5.5 — 5.6 (full L8=801 — wires actual DispatchEx → RhpCallCatchFunclet from real throw).
