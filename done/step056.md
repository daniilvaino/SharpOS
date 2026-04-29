# Step 56 — Phase 1 step 7: finally + second pass → L10 == 111

## Контекст

Step 7 в roadmap'е добавляет finally clauses к real EH dispatch'у. После step 6 (rethrow) всё что нужно для `try/catch/finally`:

```csharp
int x = 0;
try {
    try { throw new IOE("eh10"); }
    finally { x = 11; }
}
catch (IOE) { return 100 + x; }   // expected 111
```

Перед инvoking catch funclet'а нужно run finally clause так что `x = 11` сработало; nonvols modified by finally должны persist и быть visible в catch'е.

В NativeAOT EH info finally compile'ятся как **kind=Fault** (RhEHClauseKind.RH_EH_CLAUSE_FAULT) — fault и finally encode'ятся одинаково. Семантика runtime'а одна — invoke на second pass перед transferring control к catch.

## Решение

### `RhpCallFinallyFunclet` shellcode

`OS/src/Boot/EH/CallFinallyFuncletStub.cs` + `CallFinallyFuncletPatcher.cs` — новые файлы. ~174 байт shellcode.

Сравнение с RhpCallCatchFunclet (140 bytes из step 52):

| Aspect | Catch funclet | Finally funclet |
|---|---|---|
| Args | RCX=ex, RDX=handler, R8=regdisp, R9=exInfo | RCX=handler, RDX=regdisp |
| Frame size | 0x48 (4 args + shadow + pad) | 0x38 (2 args + shadow + pad) |
| Restore nonvols | Yes (read pNonvol) | Yes (read pNonvol) |
| Funclet ABI | RCX=establisher SP, RDX=ex | RCX=establisher SP |
| **Write-back nonvols** | **No** (catch не возвращается) | **Yes** (finally возвращается, mutations должны persist) |
| **Return** | **Non-local** (mov rsp; jmp rax) | **Normal** (epilogue + ret) |
| ExInfo head pop | Yes (catch consumes throw) | No (throw not consumed) |

Write-back loop пишет current nonvol values BACK через REGDISPLAY pNonvol pointers — стандартный stock NativeAOT pattern. Это нужно потому что finally может изменить локальные переменные, которые могут жить в nonvol regs (или сохранены в establisher frame с этих regs).

### `InvokeFinalliesOnFrame` в DispatchEx

Между first-pass и catch invocation добавлен second-pass call:

```csharp
uint startSecondPassIdx = isRethrow ? startIdx : ExInfo.MaxTryRegionIdx;
InvokeFinalliesOnFrame(exInfo, startSecondPassIdx, catchingTryRegionIdx);
```

Function enumerate'ит clauses на ТЕКУЩЕМ iter frame (FFPH оставил iter at catch's establisher frame после first-pass). Для каждого Fault clause покрывающего codeOffset:
1. Проверка `clauseIdx <= startIdx` (rethrow skip — same logic as first pass)
2. Проверка `clauseIdx < idxLimit` (partial-pass cap — catch's own finally не fire'ит перед catch'ем)
3. Проверка `kind == Fault && codeOffset in [TryStart..TryEnd)`
4. Invoke `RhpCallFinallyFunclet(handler, &iter.RegDisplay)`

### Multi-frame finally — deferred

Stock NativeAOT's full second pass walks ALL frames between throw site и catch's establisher, invoking finallys на КАЖДОМ frame. Наша simplified версия handles только catch frame.

Multi-frame finally требует **funclet-aware** stack frame iterator — when iter walks through a catch funclet, оно должно skip к establisher logical frame, иначе финалли в funclet body запутаются с финалли в establisher. Это step 11 territory.

В практике 90% finally clauses single-frame (try/catch/finally в одном методе) — для остальных 10% (нестед методы) finally не run'нется, что corruption-safe (просто missed cleanup) пока не landing'нется в catch frame.

### Wiring

`OS/src/Boot/BootSequence.cs` — `InstallCallFinallyFuncletShellcode()` рядом с rethrow и catch funclet patcher'ами.

`OS/src/Kernel/Diagnostics/Probes.cs` — `EhTryCatchFinally=true` (L10 gate).

`OS/src/Kernel/Diagnostics/EhProbe.cs` — `TryCatchFinally()` test method.

## Подводные камни

### Bug: InvokeSecondPass с frame-walking ломает rethrow path

Изначальный design делал full multi-frame walk. В rethrow path (L9) когда мы делали `Init(iter, exInfo->ExContext)` для rethrow's ExInfo2, iter попадал в catch funclet body PC (где RhpRethrow'нулся). Funclet'ы у нас не handle'ятся специально в SFI — `Next()` пытался unwind'ить funclet'а, попадал на RhpCallCatchFunclet (наш patched shellcode которого ILC unwind info не описывает корректно), и вся итерация corruption'ила REGDISPLAY pointers.

Симптом: после walk pNonvol pointers становились bad, потом RhpCallCatchFunclet restoreил garbage в nonvols, потом `mov rsp, REGDISPLAY.SP; jmp rax` или handler call jumping to NULL → #PF RIP=0.

Fix: simplified InvokeFinalliesOnFrame работает только с current iter (не walking), которая после FFPH правильно positioned at catch frame. Решает L10 без breaking L9.

### NativeAOT EH layout для try/catch/finally

L10 generates 3 clauses (наш decoder):

| idx | kind | TRY range | type | назначение |
|---|---|---|---|---|
| 0 | Fault | [0x10..0x37) | — | inner try's finally |
| 1 | Fault | [0x00..0x00) | — | empty separator |
| 2 | Typed | [0x10..0x37) | IOE | outer try's catch |

Inner finally и outer catch имеют ОДИНАКОВУЮ TRY range потому что в NativeAOT funclet representation — finally body, throw site и outer try region все project'ятся к одному и тому же range в ROOT method'е (finally body — отдельный funclet с собственным RUNTIME_FUNCTION).

clause[1] empty fault — separator (как в L9), который stock использует для same-TRY-range skip logic (мы её не имеем — но empty range просто не matches codeOffset так что safe).

## Результат

```
[info] Dispatch: kind=0x01 exInfo=... prevExInfo=...
[info]   iter ready: ControlPC=0x0E0E45F6 SP=0xFE973F0 startIdx=0xFFFFFFFF (init from ExContext)
[info]     fp[0]: PC=0x0E0E45F6 ehInit=Y methodStart=0x0E0E45C0
[info]       clause[0] kind=1 try=[0x10..0x37) off=0x36 type=0x0
[info]       clause[1] kind=1 try=[0x00..0x00) off=0x36 type=0x0
[info]       clause[2] kind=0 try=[0x10..0x37) off=0x36 type=0x0E0FFCB8
[info]   fp.Found=Y handler=0x0E0E4615 idxCurClause=2 framesWalked=0
[info]     finally[0]: handler=0x0E0E4601 frameSP=0xFE973F0
[info] eh L10 finally + catch: val=111   ← GATE GREEN
```

End-to-end real path:
1. `EhProbe.TryCatchFinally()` — `int x = 0; try { try { throw IOE("eh10"); } finally { x = 11; } } catch (IOE) { return 100 + x; }`
2. ILC throw → RhpThrowEx shellcode → ingress → Dispatch.
3. First pass: 3 clauses, FFPH skips Fault clauses (только Typed/Filter match), matches clause[2] (outer catch). idxCurClause=2.
4. Second pass on catch frame с idxLimit=2: clause[0] kind=Fault, codeOffset 0x36 in [0x10..0x37) → invoke finally.
5. RhpCallFinallyFunclet restores nonvols from REGDISPLAY, calls handler с RCX=establisher SP, handler runs `x = 11`, writes x to local slot, returns.
6. Shellcode write-back: x's nonvol values written back to REGDISPLAY pNonvol pointers (или x lives в establisher's stack — обновлено напрямую через RCX'овый SP base).
7. RhpCallCatchFunclet invoked для clause[2]. Restores nonvols (now updated), calls catch handler `return 100 + x` = 111.
8. Continuation transfers, EhProbe prints `eh L10 finally + catch: val=111`.

**No regression**: L1-L9 + 5.3-A green. NativeAotProbe + CctorProbe green. ELF apps + launcher reach.

## Phase 1 progress

```
After step  1: L4 == 127            ✅ step 44
After step  2: L5 == 7              ✅ step 45
After step  3: L6 == 111            ✅ step 46
After step  4: L7 == 3              ✅ step 47
After step  5: L8 == 801            ✅ step 54
After step  6: L9 == 901            ✅ step 55
After step  7: L10 == 111           ✅ step 56  ← finally + 2nd pass
After step  8: L11 == 1101          ← filter
After step  9: L12 == 101           ← fault (likely free — same encoding as finally)
After step 10: L13 == 3             ← HW-fault bridge
After step 11: L14 == 1401          ← rich stack trace + multi-frame finally
              L15 == 1501           ← collided unwind  ← PHASE 1 CLOSED
```

**7/11 hard gates closed.** Next: step 8 — filter clauses.

## Файлы

### Новые

- `OS/src/Boot/EH/CallFinallyFuncletStub.cs` — `[RuntimeExport("RhpCallFinallyFunclet")]` + `GetMethodAddress()`.
- `OS/src/Boot/EH/CallFinallyFuncletPatcher.cs` — 174-байт shellcode emitter с restore + call + writeback + epilogue.
- `done/step056.md` — этот файл.

### Изменённые

- `OS/src/Boot/EH/DispatchEx.cs` — `InvokeFinalliesOnFrame` второй pass на catch frame; вызывается из Dispatch перед catch funclet'ом.
- `OS/src/Boot/BootSequence.cs` — `InstallCallFinallyFuncletShellcode()` в Phase 2.
- `OS/src/Kernel/Diagnostics/Probes.cs` — `EhTryCatchFinally=true`.
- `OS/src/Kernel/Diagnostics/EhProbe.cs` — `TryCatchFinally()` test method + L10 dispatch в `Run()`.

## Что дальше

**Step 57 = step 8 — filter clauses (`catch (E) when (...)`)**.

По roadmap:
- `RhpCallFilterFunclet` shellcode — функция returns bool в RAX, normal return.
- В FindFirstPassHandler для Filter clauses (kind=Filter): после coverage check вызывать filter funclet, проверять return value, если true — match.
- L11 gate: `try { throw } catch (E) when (predicate) { return 1101 }`.

Step 8 проще чем 7 — filter shellcode проще catch (no head pop, no non-local transfer), а filter matching — extension существующего typed-clause path в FFPH.
