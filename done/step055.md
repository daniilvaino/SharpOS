# Step 55 — Phase 1 step 6: rethrow baseline → L9 == 901

## Контекст

Step 5 был самый тяжёлый из 11; step 6 — natural follow-up. После того как L8 (`try { throw; } catch { ... }`) green, следующий gate — rethrow chain:

```csharp
try {
    try { throw new IOE("eh9"); }
    catch (IOE) { throw; }   // RhpRethrow path
}
catch (IOE ex) { return 901; }
```

Ожидание (per roadmap): L9 == 901. Inner catch ловит, делает `throw;`, outer catch ловит, возвращает 901.

Stock NativeAOT решает rethrow через отдельный thunk `RhpRethrow` + managed `RhRethrow(ref activeExInfo, ref exInfo)`. Layout RhpRethrow ≈ RhpThrowEx с одной семантической разницей: вместо нового exception объекта он наследует THROWN exception от current active ExInfo и помечает kind=Rethrow.

## Решение

### `RhpRethrow` shellcode

`OS/src/Boot/EH/RethrowStub.cs` + `RethrowPatcher.cs` — новые файлы. ~186 байт, layout полностью идентичен `ThrowExPatcher` (step 48) с двумя отличиями:

1. **kind байт**: `0x05 = KindThrow | KindRethrow` (вместо чистого 0x01 = Throw).
2. **ingress**: тот же `RhpTest_ThrowIngress` — Dispatch detect rethrow flag и handles accordingly.

PAL_LIMITED_CONTEXT захватывается на rethrow-site (то есть внутри inner catch funclet body) — formally это выбрасывается dispatcher'ом для rethrow path (см. ниже про exception sourcing), но capture зеркалит ThrowExPatcher для layout consistency.

ILC's `rethrow` IL компилируется в `call RhpRethrow` без аргументов. RCX волатилен и держит garbage от внутренностей funclet body — это критично для bug найденного позже (см. "Подводные камни").

### `DispatchEx.Dispatch` rethrow branch

`OS/src/Boot/EH/DispatchEx.cs` — добавлено detection и handling:

```csharp
bool isRethrow = (exInfo->Kind & ExInfo.KindRethrow) != 0;

if (isRethrow)
{
    ExInfo* prev = exInfo->PrevExInfo;
    if (prev != null)
    {
        // Source exception from prev — ILC's rethrow IL doesn't pass it в RCX.
        exceptionPtr = (byte*)(nuint)prev->Exception;

        // Reuse prev's iter (positioned at catch frame от первого pass).
        exInfo->FrameIter = prev->FrameIter;
        startIdx = prev->IdxCurClause;
    }
}
```

Два ключевых решения:

1. **Exception sourcing** — берём из `prev->Exception` (= original IOE, stamp'нутый Dispatch'ем во время первого прохода). RCX от ILC недостоверен.
2. **Iter reuse** — копируем `prev->FrameIter` целиком вместо re-init от rethrow-site PAL. Stock инициализирует от exInfo._pExContext (rethrow-site) и walk'ит N frames до catch'а; мы экономим traversal и сразу landing'имся в catch frame.

### `FindFirstPassHandler` startIdx skip

Existing `startIdx` parameter (added в step 51 для будущего rethrow) активируется. На first frame пропускаем clauses 0..startIdx (inclusive). Дальше — full search.

Для нашего теста с iter at RethrowChain frame и startIdx=0:
- clause[0] (inner catch, kind=Typed, try=[0x0B..0x32)): `clauseIdx <= 0` — skip ✓
- clause[1] (kind=Fault, try=[0..0)): empty separator (см. ниже), kind не Typed/Filter — falls through clauseIdx++
- clause[2] (outer catch, kind=Typed, try=[0x0B..0x32)): codeOffset=0x31 in range, type=IOE matches IOE → Found

`fp.IdxCurClause = 2`, `handler = 0x0E0E7568` (outer catch funclet IP).

### NativeAOT EH layout для nested try/catch — three clauses, same TRY range

Сюрприз при дебаге: ILC генерирует **3 EH clauses** для simple nested try/catch (а не 2 как ожидалось из CIL):

| idx | kind | try range | type | назначение |
|---|---|---|---|---|
| 0 | Typed | [0x0B..0x32) | IOE | inner catch |
| 1 | Fault | [0x00..0x00) | — | **empty separator** |
| 2 | Typed | [0x0B..0x32) | IOE | outer catch |

clause[0] и clause[2] имеют **identical TRY range** потому что в NativeAOT catches компилируются как separate funclets — в ROOT method'е inner и outer try regions обе покрывают только сам код inner try (где throw). Inner catch handler — funclet с собственным RUNTIME_FUNCTION entry.

clause[1] (empty fault) — synthesized ILC'ом separator. Stock NativeAOT's `FindFirstPassHandler` имеет explicit "skip clauses with same TRY range as last skipped clause" logic для empty finally markers — separator breaks the streak so outer catch не пропускается ошибочно.

Наша простая логика (skip только `curIdx <= startIdx`) работает в этом случае без separator-tracking, потому что startIdx=0 пропускает только clause[0]. clause[2] обрабатывается нормально, matches.

### Wiring

`OS/src/Boot/BootSequence.cs` — `InstallRethrowShellcode()` добавлен в Phase 2 рядом c throw/catch funclet patchers:

```csharp
if (!RethrowPatcher.TryInstall())
    Panic.Fail("rethrow shellcode patch failed");
```

`OS/src/Boot/ExceptionEngine.cs` — legacy halt-stub `RhpRethrow` удалён. Single definition теперь в `RethrowStub.cs`.

`OS/src/Kernel/Diagnostics/Probes.cs` — `EhRethrowChain=true` (L9 gate runs every boot).

`OS/src/Kernel/Diagnostics/EhProbe.cs` — `RethrowChain()` test method + dispatch вызов в `Run()`.

## Подводные камни

### Bug 1 (red herring): isFirstFrame skip applies to wrong frame

Изначально мы инициализировали iter от `prev->ExContext` (= original throw site, в Helper или внутри inner try). Walk вверх от этого — first frame = throw site, second frame = catch's establisher. Но `isFirstFrame` skip применяется к first frame (throw site, не catch site). Inner catch не пропускался.

В нашем тесте throw — inline в RethrowChain (без helper); first frame = RethrowChain (frame совпадает с catch frame случайно). Тем не менее проблема была реальная и проявилась бы в multi-frame случае. Фикс: reuse `prev->FrameIter` который уже positioned at catch frame.

### Bug 2 (real cause): exception sourcing

Stock `RhpRethrow` (asm в `gc-experiment/.../amd64/ExceptionHandling.asm:196`) — `void FASTCALL RhpRethrow()` без аргументов. ILC's `rethrow` IL компилируется в call без передачи exception в RCX — RCX содержит garbage от внутренностей funclet body.

Stock managed `RhRethrow(ref activeExInfo, ref exInfo)` source'ит exception:
```csharp
object rethrownException = activeExInfo.ThrownException;
exInfo.Init(rethrownException, ref activeExInfo);
```

То есть exception берётся ИЗ active ExInfo (= prev в нашей терминологии), который был stamp'нут Dispatch'ем в первом проходе.

Наш изначальный shellcode передавал RCX как exception в ingress → Dispatch → `*(GcMethodTable**)exceptionPtr` → garbage MT pointer (`0xE8E7894C1074E485`) → `IsAssignableFromClass.GetBaseType()` deref non-canonical address → #GP.

Симптом: RDX=R8=R9=`0xE8E7894C1074E485` (Win64 ABI loading garbage exType into args of GetBaseType helper).

LE bytes `0xE8E7894C1074E485` = `85 E4 74 10 4C 89 E7 E8` = `test esp,esp; je +0x10; mov rdi,r12; call …` — **8 bytes of code memory** прочитанные как pointer. Подсказка что мы dereference'или wrong region (originally accessed code as data through valid-looking но неправильный pointer).

Фикс: в `Dispatch` для rethrow override `exceptionPtr = prev->Exception`.

## Результат

```
[info] eh L8 typed catch (real dispatch): val=801
[info] Dispatch: kind=0x01 exInfo=... prevExInfo=0x0
[info]   iter ready: ControlPC=0x0E0E7551 SP=0xFE973F0 startIdx=0xFFFFFFFF (init from ExContext)
[info]     fp[0]: PC=0x0E0E7551 ehInit=Y methodStart=0x0E0E7520
[info]       clause[0] kind=0 try=[0x0B..0x32) off=0x31 type=0x0E101B20
[info]   fp.Found=Y handler=0x0E0E755C idxCurClause=0 framesWalked=0
[info] Dispatch: kind=0x05 exInfo=0xFE96B70 prevExInfo=0xFE97140
[info]   rethrow: prev->IdxCurClause=0 prev->Exception=0x106758 ...
[info]   iter ready: ControlPC=0x0E0E7551 SP=0xFE973F0 startIdx=0x0 (reused prev iter)
[info]     fp[0]: PC=0x0E0E7551 ehInit=Y methodStart=0x0E0E7520
[info]       clause[0] kind=0 try=[0x0B..0x32) off=0x31 type=0x0E101B20
[info]       clause[1] kind=1 try=[0x00..0x00) off=0x31 type=0x0
[info]       clause[2] kind=0 try=[0x0B..0x32) off=0x31 type=0x0E101B20
[info]   fp.Found=Y handler=0x0E0E7568 idxCurClause=2 framesWalked=0
[info] eh L9 rethrow chain: val=901   ← GATE GREEN
[info] elf validation start
... (boot continues normally)
```

End-to-end real path:
1. `EhProbe.RethrowChain()` — inner `throw new IOE("eh9")`.
2. ILC `call RhpThrowEx` → shellcode → ingress → Dispatch → ExInfo1 (kind=Throw).
3. Dispatch finds clause[0] = inner catch, `idxCurClause=0`, `Exception=ptr(IOE)`.
4. RhpCallCatchFunclet → inner catch funclet body executes `throw;`.
5. ILC `call RhpRethrow` (без exception arg) → shellcode → ExInfo2 (kind=Throw|Rethrow, prev=ExInfo1).
6. Recursive Dispatch detects rethrow: source exception от `prev->Exception`, reuse `prev->FrameIter`, startIdx=0.
7. FindFirstPassHandler skip clause[0], skip clause[1] (fault), match clause[2] (outer catch).
8. RhpCallCatchFunclet → outer catch funclet → `return ex.Message == "eh9" ? 901 : -1`.
9. Continuation transfers, returns 901 → printed.

**No regression**: L1-L8 + 5.3-A все green; NativeAotProbe + CctorProbe green; ELF apps + launcher reach.

## Phase 1 progress

```
After step  1: L4 == 127            ✅ step 44
After step  2: L5 == 7              ✅ step 45
After step  3: L6 == 111            ✅ step 46
After step  4: L7 == 3              ✅ step 47
After step  5: L8 == 801            ✅ step 54
After step  6: L9 == 901            ✅ step 55  ← rethrow chain
After step  7: L10 == 111           ← finally + second pass
After step  8: L11 == 1101          ← filter
After step  9: L12 == 101           ← fault
After step 10: L13 == 3             ← HW-fault bridge
After step 11: L14 == 1401          ← rich stack trace
              L15 == 1501           ← collided unwind  ← PHASE 1 CLOSED
```

**6/11 hard gates closed.** Next: step 7 — finally + second pass.

## Файлы

### Новые

- `OS/src/Boot/EH/RethrowStub.cs` — `[RuntimeExport("RhpRethrow")]` + `GetMethodAddress()`.
- `OS/src/Boot/EH/RethrowPatcher.cs` — 186-байт shellcode emitter.
- `done/step055.md` — этот файл.

### Изменённые

- `OS/src/Boot/EH/DispatchEx.cs` — rethrow branch (exception sourcing + iter reuse + startIdx); per-frame и per-clause diagnostic logging.
- `OS/src/Boot/EH/StackFrameIterator.cs` — без изменений (frame iter reused as-is).
- `OS/src/Boot/ExceptionEngine.cs` — legacy halt-stub `RhpRethrow` удалён.
- `OS/src/Boot/BootSequence.cs` — `RethrowPatcher.TryInstall()` в Phase 2.
- `OS/src/Kernel/Diagnostics/Probes.cs` — `EhRethrowChain=true`.
- `OS/src/Kernel/Diagnostics/EhProbe.cs` — `RethrowChain()` test method + L9 dispatch в `Run()`.

## Что дальше

**Step 56 = step 7 — finally + second pass**.

По roadmap:
- Шаг 7 добавит `RhpCallFinallyFunclet` shellcode (~similar to CatchFunclet но без exception arg + return-not-jump).
- `InvokeSecondPass(exInfo, startIdx, endIdx)` — walks frames на second pass, invokes finally clauses в frame's TRY range.
- DispatchEx после first-pass выполняет second-pass loop перед transferring к catch.
- L10 gate: `try { throw } finally { ++x } catch { return 100 + x }` → 111.

Step 7 — менее тяжёлый чем 5 или 6 потому что mechanisms (StackFrameIterator, EH decoder, Funclet ABI shellcode) на месте. Главное — second-pass walk loop + finally clause kind handling.
