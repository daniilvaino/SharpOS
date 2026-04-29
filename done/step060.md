# Step 60 — Phase 1 step 11 closure: funclet-aware codeOffset → L15 == 1501

## Контекст

Phase 1 step 11 закрыт фактически с одним deferred gate (L15) в step 59.
Этот step фиксит L15 — collided unwind (rethrow inside finally body) — через
**funclet-aware codeOffset transformation** в EH dispatch path. **11/11
hard gates Phase 1 green.**

## Решение

### Проблема

NativeAOT компилирует finally body как **separate funclet** с отдельным
RUNTIME_FUNCTION. Funclet body живёт в области кода parent method'а на
codeOffset >= TryEnd parent's TRY range. Когда throw происходит внутри
finally body:

- iter.ControlPC = funclet body PC (e.g., 0x0E0E0A28)
- methodStart = ROOT method start (0x0E0E09C0)
- codeOffset = 0x68 — **PAST все TRY ranges parent** (типичный inner try
  [0x0B..0x32))

Все clause matching fails — outer catches не видят throw. Stock NativeAOT
solves this via funclet-aware StackFrameIterator, который при walk через
funclet logically maps к parent's protected region.

### Фикс №1 — funclet-aware codeOffset

`OS/src/Boot/EH/CoffEhDecoder.cs` — новый helper:

```csharp
public static bool TryFindFuncletProtectedOffset(
    byte* ip,
    out uint synthOffset,
    out byte* methodStart,
    out uint funcletClauseIdx)
```

Algorithm:
1. `CoffMethodLookup.TryFindMethod(ip)` — resolve to MethodInfo (handles
   funclet→ROOT walk).
2. Check `info.CurrentBlockFlags & UBF_FUNC_KIND_MASK` — если ROOT, return false.
3. Compute funclet's offset = funcletRf.BeginAddress - rootRf.BeginAddress.
4. Iterate parent's EH clauses; find one whose `HandlerAddress == funcletAddr`.
5. Return clause's `TryStartOffset` (synthetic codeOffset inside protected
   region) + clauseIdx.

Used в `FindFirstPassHandler` и `InvokeFinalliesOnFrame` — когда iter в
funclet, codeOffset replaced с synth offset, outer clauses match естественно.

### Фикс №2 — funcletClauseIdx skip

После funclet-aware transformation, `InvokeFinalliesOnFrame` мог re-invoke
the **same finally** that we're currently executing (iter PC inside its
funclet body, codeOffset 0x0B inside its TRY range). Бесконечная рекурсия —
finally throws "b" → recursive Dispatch invokes same finally → throws "b"
→ etc.

`TryFindFuncletProtectedOffset` теперь возвращает `funcletClauseIdx`. В
`InvokeFinalliesOnFrame` это clause skipped:

```csharp
if (clauseIdx == funcletClauseIdx)
{
    clauseIdx++;
    continue;
}
```

### Фикс №3 — collided-unwind iter adoption

After (1) и (2), Dispatch2 (для "b") нашёл outer catch handler но catch
funclet получил **wrong establisher SP**. Recursive Dispatch's iter был
init'нут от rethrow-site PAL (= funclet's local rsp 0xFE96D20). Catch
funclet ABI: `mov rcx, [r8 + REGDISPLAY.SP]` — RCX = wrong establisher,
catch reads `ex` from wrong stack offset → garbage Message → return -1.

Fix: detect "throw inside funclet" (= TryFindFuncletProtectedOffset returns
true on `exInfo->ExContext->IP`), adopt prev's iter (Dispatch1's iter at
correct establisher SP), но preserve funclet body PC в `iter.ControlPC`
для funclet detection в FFPH/InvokeFinalliesOnFrame:

```csharp
if (exInfo->PrevExInfo != null
    && CoffEhDecoder.TryFindFuncletProtectedOffset(
        (byte*)exInfo->ExContext->IP, out _, out _, out _))
{
    ulong funcletBodyPC = exInfo->ExContext->IP;
    exInfo->FrameIter = exInfo->PrevExInfo->FrameIter;  // correct SP + nonvol ptrs
    exInfo->FrameIter.ControlPC = funcletBodyPC;        // keep funclet detection alive
}
```

Mixed iter: SP/nonvol pointers from prev (=parent method's establisher),
ControlPC = funclet body PC. FFPH видит "we're in funclet" via ControlPC,
RhpCallCatchFunclet получает correct establisher SP via RegDisplay.SP.

## Результат

```
[info] Dispatch: kind=0x01 exInfo=0x...96A50 prevExInfo=0x...97140
[info]   collided-unwind: adopted prev iter SP=0x...973F0 kept ControlPC=0x...0A28
[info]   iter ready: ControlPC=0x...0A28 SP=0x...973F0 startIdx=0xFFFFFFFF (init from ExContext)
[info]     fp[0]: PC=0x...0A28 ehInit=Y methodStart=0x...09C0
[info]       funclet-aware: synth codeOffset=0xB funcletClauseIdx=0
[info]       clause[0] kind=1 try=[0xB..0x32) off=0xB
[info]       clause[1] kind=1 try=[0..0) off=0xB
[info]       clause[2] kind=0 try=[0xB..0x32) off=0xB type=0x...FD020
[info]   fp.Found=Y handler=0x...0A29 idxCurClause=2 framesWalked=0
[info] eh L15 collided unwind: val=1501   ← GATE GREEN
```

End-to-end:
1. `EhProbe.CollidedUnwind()` — outer try contains inner try/finally; inner
   finally throws "b" while original "a" exception is being dispatched.
2. Dispatch1 для "a": clauses [Fault, Fault, Typed], finds outer catch idx=2.
3. Dispatch1 second pass: invokes inner finally (clause[0]).
4. Finally body executes `throw new IOE("b")` → RhpThrowEx → ingress → Dispatch2.
5. Dispatch2: detects ExContext.IP inside funclet body → adopts prev->FrameIter
   (correct establisher SP), keeps funclet body PC.
6. Dispatch2 FFPH: funclet-aware synth codeOffset=0xB. clause[2] outer catch
   matches IOE.
7. Dispatch2 second pass: clause[0] skipped (funcletClauseIdx=0), clause[1]
   skipped (separator empty), clause[2] capped by idxLimit=2 → no invocation.
8. Dispatch2 calls catchFn(ptrB, outerHandler, &iter, exInfo). Iter has
   correct establisher SP. Catch funclet receives ex="b". `ex.Message == "b"`
   → returns 1501. ✓

**No regression**: L1-L11, L13, L14 + 5.3-A green.

## Phase 1 closure final

```
After step  1: L4 == 127            ✅ step 44
After step  2: L5 == 7              ✅ step 45
After step  3: L6 == 111            ✅ step 46
After step  4: L7 == 3              ✅ step 47
After step  5: L8 == 801            ✅ step 54
After step  6: L9 == 901            ✅ step 55
After step  7: L10 == 111           ✅ step 56
After step  8: L11 == 1101          ✅ step 57
After step  9: L12 — folded         ✅ step 58
After step 10: L13 == 3             ✅ step 58
After step 11: L14 == 1401          ✅ step 59
              L15 == 1501           ✅ step 60   ← collided unwind закрыт
```

**11/11 hard gates closed.** Phase 1 EH полностью завершена.

Полный набор work in scope:
- ✅ throw + catch (any class hierarchy)
- ✅ rethrow chain (`throw;`)
- ✅ try/finally + catch (single-frame)
- ✅ catch when filter
- ✅ HW fault → managed (#GP, #PF, #DE)
- ✅ Stack trace populated (single-frame marker)
- ✅ Collided unwind (rethrow inside finally)

Single-frame только: multi-frame finally walk all-still в queue (требует
funclet-aware SFI's Next() that skips funclet+dispatch internals — Phase 2
work). Single-frame, который покрывает 90%+ real-world C# patterns,
работает полностью.

## Файлы

### Изменённые

- `OS/src/Boot/EH/CoffEhDecoder.cs` — `TryFindFuncletProtectedOffset` returns
  synthOffset + funcletClauseIdx.
- `OS/src/Boot/EH/DispatchEx.cs` — funclet-aware codeOffset in FFPH +
  InvokeFinalliesOnFrame; collided-unwind iter adoption in Dispatch entry;
  funcletClauseIdx skip в InvokeFinalliesOnFrame.
- `OS/src/Kernel/Diagnostics/Probes.cs` — `EhCollidedUnwind=true` (was false
  pending fix).
- `done/step060.md` — этот файл.

## Что дальше

Phase 1 фактически закрыт по плану. Per `plan.md` Phase 1 critical-path
items beyond EH:
- ✅ ACPI parsing (RSDP/XSDT/MADT/HPET/MCFG) — already done
- ✅ RTC + HPET + Stopwatch — already done
- ⚠️ ClassConstructorRunner полная (canonical pattern works; lazy/array
  patterns ещё нет — остаются заблокированными per CLAUDE.md trap)
- ⚠️ Drop `--resilient` mode (eager cctors at module init)

Open EH polish (deferred — non-blocking):
- Multi-frame finally walk через funclet-aware SFI Next() (extends to
  multi-frame stack trace формирование тоже).
- Rich stack trace formatting (real method names требуют symbol info).
- RFLAGS.IF restoration после HW fault dispatch.
- AccessViolationException type.
- OnFirstChanceException / OnUnhandledException / FailFast hooks.

Per plan.md следующая фаза — **Phase 2: PAL design + Linux spike**.
Эта работа — каталог CoreCLR PAL функций + dummy stub experiment.
Готово к старту.
