# Step 61 — Phase 1 EH polish: multi-frame finally + multi-frame stack trace

## Контекст

Phase 1 EH ещё не "полноценное" per plan.md пока работает только single-
frame случай. Если throw в callee и try/finally — в caller'е, finally
не fires. Stack trace содержит только throw site, не chain caller'ов.

Этот step закрывает обе дыры через multi-frame second-pass walk +
trace appending per visited frame.

## Решение

### Multi-frame stack trace (L17)

`FindFirstPassHandler` теперь принимает `traceTarget` (Exception object).
В loop'е walk frames, на каждой iteration appends `iter->ControlPC` к
exception's `_corDbgStackTrace`. Каждый visited frame даёт one entry —
включая frames без EH info (просто walked through).

`Dispatch` resolves exception ref before FFPH call и passes through. Для
rethrow path не appends (trace уже populated в первом dispatch'е).

Test L17: 3-deep helper chain (`HelperLevel3 → 2 → 1 → MultiFrameStackTrace`).
After catch, `ex.GetStackIPs().Length >= 4` → return 1700 + length.
Got `val=1704` (4 frames).

### Multi-frame second pass (L16)

Раньше `InvokeFinalliesOnFrame` operates только on iter's CURRENT frame.
Теперь wrapped в `InvokeSecondPass` который walks frames от throw site
до handlingFrameSP:

```csharp
private static void InvokeSecondPass(ExInfo* exInfo, ulong handlingFrameSP,
    uint catchIdx, uint startIdx)
{
    StackFrameIteratorOps.Init(&exInfo->FrameIter, exInfo->ExContext);

    // Collided unwind detection (mirror Dispatch entry)
    if (exInfo->PrevExInfo != null && /* throw inside funclet */) {
        exInfo->FrameIter = exInfo->PrevExInfo->FrameIter;
        exInfo->FrameIter.ControlPC = funcletBodyPC;
    }

    while (frames < MaxFrames)
    {
        bool atCatchFrame = iter.SP == handlingFrameSP;
        bool past = iter.SP > handlingFrameSP;
        if (past) break;

        uint idxLimit = atCatchFrame ? catchIdx : MaxTryRegionIdx;
        uint frameStartIdx = atCatchFrame ? startIdx : MaxTryRegionIdx;
        InvokeFinalliesOnFrame(exInfo, frameStartIdx, idxLimit);

        if (atCatchFrame) break;
        if (!Next(iter)) break;
    }
}
```

На каждом intermediate frame: full pass (all Fault clauses covering
codeOffset run). На catch frame: partial pass (clauses < catchIdx run).
Funclet-aware codeOffset + funcletClauseIdx skip preserved для
collided unwind.

Test L16:
```
MultiFrameFinally
  try { HelperWithFinally16(); }      // outer try, catch
  catch (IOE) { return 1600 + count; }

HelperWithFinally16
  try { HelperThrow16(); }            // inner try, finally
  finally { count = 16; }

HelperThrow16
  throw new IOE("eh16");              // bare throw
```

Flow:
1. Throw in HelperThrow16. iter at HelperThrow16's frame.
2. FFPH walks: HelperThrow16 (no clauses) → HelperWithFinally16 (Fault no
   match) → MultiFrameFinally (Typed match). framesWalked=2.
3. handlingFrameSP = MultiFrameFinally's establisher SP.
4. Second pass re-init from throw site PAL. Walk:
   - HelperThrow16 frame: no clauses, skip.
   - HelperWithFinally16 frame: clause[0] Fault, codeOffset in TRY range,
     invoke RhpCallFinallyFunclet → `count = 16`.
   - MultiFrameFinally frame (= handlingFrameSP): atCatchFrame, idxLimit=0
     (catch is idx=0), no clauses to invoke before catch.
5. RhpCallCatchFunclet → outer catch returns 1600 + 16 = 1616. ✓

## Результат

```
[info] eh L16 multi-frame finally: val=1616
[info] eh L17 multi-frame stack trace: val=1704
```

L17 trace shows full chain:
```
fp[0]: PC=0x...0AAB ehInit=N           ← HelperLevel3_17
fp[1]: PC=0x...0AB9 ehInit=N           ← HelperLevel2_17
fp[2]: PC=0x...0AC9 ehInit=N           ← HelperLevel1_17
fp[3]: PC=0x...0ADF ehInit=Y handler=…0AF4   ← MultiFrameStackTrace
```

L16 trace shows finally fired on intermediate frame:
```
fp[0]: PC=…09EB ehInit=N                ← HelperThrow16
fp[1]: PC=…09FF ehInit=Y try=[0xA..0x10) ← HelperWithFinally16 finally clause
fp[2]: PC=…0A49 ehInit=Y try=[0x14..0x1A) ← MultiFrameFinally catch
finally[0]: handler=…0A12 frameSP=0x…73C0    ← invoked на HelperWithFinally16's frame
```

`frameSP=0xFE973C0` ОТЛИЧНЫЙ от `0xFE973F0` (catch frame's SP) — финально
действительно invoked на DIFFERENT frame than catch. Multi-frame walking
works.

**No regression**: L1-L15 все green.

## Phase 1 EH полностью closed

Все scenarios работают end-to-end:
- ✅ throw + catch (single + multi-frame)
- ✅ rethrow chain
- ✅ try/finally + catch (single + multi-frame)
- ✅ catch when filter
- ✅ HW fault (#GP, #PF, #DE) → managed
- ✅ Stack trace populated (multi-frame)
- ✅ Collided unwind (rethrow inside finally)

## Файлы

### Изменённые

- `OS/src/Boot/EH/DispatchEx.cs` — `FindFirstPassHandler` accepts
  `traceTarget` Exception ref + appends per frame; `InvokeSecondPass`
  multi-frame walk loop wrapping `InvokeFinalliesOnFrame`.
- `OS/src/Kernel/Diagnostics/Probes.cs` — `EhMultiFrameFinally`,
  `EhMultiFrameStackTrace` toggles.
- `OS/src/Kernel/Diagnostics/EhProbe.cs` — L16/L17 test methods + helper
  chains.
- `done/step061.md` — этот файл.

## Что дальше

Per plan.md Phase 1 critical-path остаётся:
1. **Drop `--resilient` mode** — eager cctors на module init; разблокирует
   lazy static patterns (`if (s == null) s = new T()`) и array initializers
   (`static readonly T[] xs = new T[]{...}`). Сейчас обходим через
   `static readonly T x = new T()` (TypePreinit) + factory properties.
2. **EH polish**: AccessViolationException, OnFirstChance/Unhandled/FailFast
   hooks, RFLAGS.IF restoration после HW fault dispatch.
