# Step 54 — Phase 1 step 5.6: real DispatchEx + ILC catch funclet → L8 == 801 🎉

## Контекст

Финальный sub-step шага 5. **Step 5 ЗАКРЫТ.** First-green typed catch end-to-end в SharpOS — managed `try { throw } catch (E) { ... }` работает ровно как в stock NativeAOT. Уникально среди managed-OS проектов: Cosmos / MOSA / MOOS все redirect throw в halt; теперь у нас реально работающий unwinder.

## Решение

### `DispatchEx.Dispatch(byte* exceptionPtr, ExInfo* exInfo)`

Top-level orchestrator (`OS/src/Boot/EH/DispatchEx.cs`). Called от `RhpTest_ThrowIngress` после `RhpThrowEx` shellcode построил PAL+ExInfo+head:

1. `StackFrameIteratorOps.Init(&exInfo->FrameIter, exInfo->ExContext)` — bootstrap iterator от throw-site PAL.
2. Resolve `GcMethodTable* exType = *(GcMethodTable**)exceptionPtr` (object header[0] = MT pointer).
3. `FindFirstPassHandler(exType, &iter)` — walk frames, match Typed clause via `IsAssignableFromClass`.
4. If `!fp.Found`: print "unhandled" + halt (full FailFast plumbing — позже).
5. Update ExInfo state: `IdxCurClause = fp.IdxCurClause`, `Exception = exceptionPtr`, `PassNumber = 2`.
6. `RhpCallCatchFunclet(exception, fp.HandlerAddress, &iter, exInfo)` — non-local transfer. Не возвращается.

Single-pass dispatch (без `InvokeSecondPass` finally walk) — sub-step 5.7 territory. Для 5.6 enough: typed catch без finally тестируется напрямую.

### Wiring в `RhpTest_ThrowIngress`

`OS/src/Boot/ExceptionEngine.cs` — добавлен production short-circuit перед 5.1-5.5 logging chain:

```csharp
if (Probes.EhRealDispatch)
{
    OS.Boot.EH.DispatchEx.Dispatch(exceptionPtr, exInfo);
    // never returns
    Console.Write("*** Dispatch returned (BUG) ***");
    while (true) { }
}
```

Когда `EhRealDispatch=true` — ANY throw immediately routes в Dispatch. Мock пути (5.1/5.2/5.4 logging, 5.5b mock dispatch) byproduct'ятся. По-прежнему доступны если `EhRealDispatch=false`.

### L8 probe update

`EhProbe.TryCatchWithThrow` — раньше `return m.Length` (=7), теперь `return ex.Message == "eh8" ? 801 : -1` (per roadmap spec). Probe label: `eh L8 typed catch (real dispatch)`.

Probe order переустроен — L8 теперь **последний** в EhProbe.Run (после L1, L2, L4-L7, 5.3-A).

### Toggle defaults

| Toggle | Значение | Reason |
|---|---|---|
| `EhRealDispatch` | **`true`** | Production path — все throw идут в Dispatch |
| `EhTryCatchWithThrow` | **`true`** | L8 gate runs every boot |
| `EhIngressThrow` | `false` | 5.1-5.5 mock path (доступен for debugging) |
| `EhCatchFuncletReal` | `false` | 5.5b mock |
| `EhCatchFuncletProbe` | `false` | 5.5a mock |

## Результат

```
[info] eh L1 try/finally no-throw: val=211
[info] eh L2 try/catch no-throw: val=4
[info] eh L4 exception shape: val=127
[info]   l5-diag: count=788 selfIp=... selfRecord=... firstFunclet=...
[info] eh L5 .pdata + root walk: val=7
[info]   l6-diag: typed=1 finally=1 filter=1
[info] eh L6 ehInfo varint decode: val=111
[info] eh L7 frame walk: val=3
[info]   5.3-A diag: methodStart=0x... controlPC=0x... codeOffset=0x32 nClauses=1
[info]     clause[0] kind=0 try=[0x18..0x48) handler=0x...
[info] eh 5.3 enum-live: val=15
[info] eh L8 typed catch (real dispatch): val=801    ← FIRST GREEN
[info] elf validation start
[info] fs init ok
... (ELF apps + launcher continue normally)
```

End-to-end real path verified:
1. `EhProbe.TryCatchWithThrow()` — managed body throws `new InvalidOperationException("eh8")`.
2. ILC emits `call RhpThrowEx`.
3. `RhpThrowEx` shellcode (step 48): captures throw-site context, builds PAL+ExInfo, links head chain, tail-calls `RhpTest_ThrowIngress`.
4. `RhpTest_ThrowIngress` sees `EhRealDispatch=true` → routes к `DispatchEx.Dispatch`.
5. `Dispatch` initialises SFI, resolves exception type (MT*), calls `FindFirstPassHandler`.
6. `FindFirstPassHandler` walks via `SfiNext` (step 47), finds typed clause matching `InvalidOperationException`, returns `fp.HandlerAddress` = ILC's catch funclet IP.
7. `RhpCallCatchFunclet` shellcode (step 52, 140 bytes) restores 8 nonvols from REGDISPLAY, sets RCX=establisher SP, RDX=exception, calls handler.
8. ILC catch funclet runs `ex.Message == "eh8" ? 801 : -1`, returns RAX=continuation IP.
9. Shellcode pops ExInfo head (`s_head = exInfo->PrevExInfo`), `mov rsp, REGDISPLAY.SP; jmp rax`.
10. Resume в `TryCatchWithThrow` continuation IP — completes `return 801`.
11. Control returns к `EhProbe.Run`, prints `eh L8 typed catch (real dispatch): val=801`.
12. Boot continues — ELF validation, apps launch, launcher reaches.

**No regression**: все existing probes (L1-L7, 5.3-A) green. NativeAotProbe (50+ tests) green. CctorProbe green. ELF apps run, launcher reaches.

## Phase 1 step 5 closure summary

| Sub-step | Status | Commit |
|---|---|---|
| 5.1 Ingress shellcode | ✅ | step 48 (`e6a365e`) |
| 5.2 SfiInit | ✅ | step 49 (`b60079c`) |
| 5.3 EH enum (probes A + B) | ✅ | step 50 (`7931bb4`) |
| 5.4 FindFirstPassHandler | ✅ | step 51 (`6bbc954`) |
| 5.5a RhpCallCatchFunclet (mock) | ✅ | step 52 (`9b37509`) |
| 5.5b real REGDISPLAY + fake handler | ✅ | step 53 (`a0e9c77`) |
| 5.6 real DispatchEx + ILC funclet | ✅ | **step 54** |

**Step 5 = L8 == 801 GATE GREEN.**

## Файлы

### Изменённые

- `OS/src/Boot/EH/DispatchEx.cs` — `Dispatch` orchestrator added.
- `OS/src/Boot/ExceptionEngine.cs` — `RhpTest_ThrowIngress` short-circuit к Dispatch when `EhRealDispatch=true`.
- `OS/src/Kernel/Diagnostics/EhProbe.cs` — `TryCatchWithThrow` returns 801; reorder probes так что L8 последний.
- `OS/src/Kernel/Diagnostics/Probes.cs` — `EhRealDispatch=true`, `EhTryCatchWithThrow=true` (production defaults).
- `done/step054.md` — этот файл.

## Phase 1 progress

```
After step  1: L4 == 127            ✅ step 44
After step  2: L5 == 7              ✅ step 45
After step  3: L6 == 111            ✅ step 46
After step  4: L7 == 3              ✅ step 47
After step  5: L8 == 801            ✅ step 54  ← FIRST GREEN catch ✨
After step  6: L9 == 901            ← rethrow
After step  7: L10 == 111           ← finally + second pass
After step  8: L11 == 1101          ← filter
After step  9: L12 == 101           ← fault
After step 10: L13 == 3             ← HW-fault bridge
After step 11: L14 == 1401          ← rich stack trace
              L15 == 1501           ← collided unwind  ← PHASE 1 CLOSED
```

**5/11 hard gates closed.** Next: step 6 — rethrow (`throw;`).

## Что дальше

**Step 55 = step 6 — Rethrow baseline**.

По roadmap:
- Shellcode thunk `RhpRethrow` (similar to RhpThrowEx but reuses existing exception).
- Managed `RhRethrow`.
- Use `activeExInfo._idxCurClause` / `startIdx` так что DispatchEx продолжает с правильной clause position.
- НЕ collided unwind (это step 11).

Smoke L9 = 901: nested catch where inner does `throw;`, outer catches и returns 901.

Step 5 был самый сложный из 11. Дальше pace должен ускориться — основные mechanisms на месте (StackFrameIterator, EH decoder, FindFirstPassHandler, RhpCallCatchFunclet). Каждый последующий step добавляет 1-2 новых thunk'а или отдельный path в DispatchEx.
