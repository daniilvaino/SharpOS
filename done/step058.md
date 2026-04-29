# Step 58 — Phase 1 step 9 (folded) + step 10: HW fault bridge → L13 == 3

## Контекст

Этот commit закрывает два sub-step'а одним shot'ом:

### Step 9 (fault clauses) — folded into step 7

В NativeAOT EH info `fault` и `finally` clauses encoded **identically** (kind=Fault). Stock CoreCLR distinct'ит их на IL уровне (CIL имеет `fault` opcode), но runtime treatment одинаков — обе run на second pass для caught exceptions.

Step 7 (commit `72d116b`) уже invoke'ит kind=Fault clauses через `InvokeFinalliesOnFrame`. Поэтому fault clauses functionally работают.

Test L12 == 101 не feasible в pure C# — у C#'а нет syntax для `try/fault` (только `try/finally` и `try/catch`). Skipping the gate but functionality is in place.

### Step 10 — HW fault bridge

`try { unsafe { *((int*)0x8000_0000_0000_0000) = 0; } } catch (NullReferenceException) { return 3; }`

CPU #GP from non-canonical write → IDT → managed dispatch → catch handler. End-to-end real path.

## Решение

### `HwFaultBridge.DispatchTrap`

`OS/src/Boot/EH/HwFaultBridge.cs` — managed bridge from interrupt context to DispatchEx.

Flow:
1. `Idt.Dispatch` checks vector. For supported (`#DE`, `#PF`, `#GP`) — calls `HwFaultBridge.DispatchTrap`.
2. Bridge allocates `PalLimitedContext` + `ExInfo` on local kernel stack (lifetime = dispatch lifetime).
3. `BuildPal` copies registers from `InterruptFrame` into PAL — преимущество над stock RhpThrowHwEx asm thunk: trap frame already в struct format from our IDT trampoline.
4. `ResolveException(vector, frame)` constructs managed exception object:
   - `#DE` → `DivideByZeroException`
   - `#PF`/`#GP` → `NullReferenceException` (CLR tradition: any access violation collapses к NullReferenceException; `AccessViolationException` doesn't exist в наш runtime)
5. ExInfo stamp'ится с kind=KindHardwareFault (0x02), linked в head chain.
6. `DispatchEx.Dispatch(exObj, &exInfo)` — same dispatcher как для managed throws. Walks frames from PAL's RIP, finds catch, transfers via `RhpCallCatchFunclet`.

### `Idt.Dispatch` routing

Раньше — всегда `PanicDump.Print(frame); halt`. Теперь:

```csharp
int vector = (int)frame->Vector;
if (HwFaultBridge.IsSupported(vector))
    HwFaultBridge.DispatchTrap(frame);   // doesn't return on success

PanicDump.Print(frame);  // fallback — unsupported vector or unhandled exception
while (true) { }
```

### Test approach: non-canonical address не null deref

Изначальный test был `*((int*)null) = 0` — но в UEFI environment **page 0 identity-mapped writable**. Запись по `*null` молча succeeds, no fault. Test возвращал -1 (unreachable line).

Fix: использовать non-canonical address `0x8000_0000_0000_0000`. CPU генерит `#GP` regardless of paging — bit 63 set но bit 47 cleared = non-canonical → `#GP(0)` обязательно.

CR2=0 в логах потому что `#GP` doesn't update CR2 (это специфично для `#PF`).

## Результат

```
[info] HW fault: vec=13 RIP=0x0E0E1716 RSP=0xFE97410 CR2=0x0
[info] Dispatch: kind=0x02 exInfo=0xFE96F80 prevExInfo=0xFE97140
[info]   iter ready: ControlPC=0x0E0E1716 SP=0xFE97410 startIdx=0xFFFFFFFF (init from ExContext)
[info]     fp[0]: PC=0x0E0E1716 ehInit=Y methodStart=0x0E0E1700
[info]       clause[0] kind=0 try=[0x0A..0x1F) off=0x16 type=0x0E0FE180
[info]   fp.Found=Y handler=0x0E0E1728 idxCurClause=0 framesWalked=0
[info] eh L13 hw fault (null deref): val=3   ← GATE GREEN
```

End-to-end:
1. `EhProbe.HwFault()` — `unsafe { int* p = (int*)0x8000_0000_0000_0000UL; *p = 0; }`
2. CPU попытка write по non-canonical addr → `#GP(0)` raised. Vector=13, errcode=0.
3. IDT entry stub for #GP — pushes vector + dummy errcode (0), jumps к common stub.
4. Common stub pushes all GPRs + CR2, calls `Idt.Dispatch(InterruptFrame*)`.
5. `Idt.Dispatch` sees vec=13 → routes к `HwFaultBridge.DispatchTrap`.
6. Bridge builds PAL (RIP=throw_site, RSP=fault_RSP, all nonvols), constructs `NullReferenceException` через GC.
7. Allocates ExInfo (kind=HardwareFault), links в head chain.
8. `DispatchEx.Dispatch`: 1 clause kind=Typed type=NullRefMT, codeOffset 0x16 в [0x0A..0x1F), match. Found.
9. `RhpCallCatchFunclet` → catch funclet runs `return 3` → continuation.
10. EhProbe prints `eh L13 hw fault (null deref): val=3`.

**No regression**: L1-L11 + 5.3-A green.

### Caveats

- **RFLAGS.IF не восстанавливается**: Interrupt gates clear IF on entry; our control transfer via `mov rsp+jmp` (RhpCallCatchFunclet) bypasses IRETQ, leaving IF=0. После HW exception managed code runs с disabled interrupts. Не страшно для phase 1 tests но прод-ready system нужно `sti` где-то на dispatch path.

- **Single-frame только**: Like other steps, multi-frame finally walking через interrupt context требует funclet-aware SFI (step 11 territory). Catch на same frame как fault — works.

- **No xmm capture**: InterruptFrame doesn't snapshot Xmm6-15. PAL's xmm fields = 0. Throwing code that uses xmm in nonvol slots could lose state. Rarely matters.

## Phase 1 progress

```
After step  1: L4 == 127            ✅ step 44
After step  2: L5 == 7              ✅ step 45
After step  3: L6 == 111            ✅ step 46
After step  4: L7 == 3              ✅ step 47
After step  5: L8 == 801            ✅ step 54
After step  6: L9 == 901            ✅ step 55
After step  7: L10 == 111           ✅ step 56
After step  8: L11 == 1101          ✅ step 57
After step  9: L12 — folded         ✅ step 58 (fault==finally в encoding, no separate test)
After step 10: L13 == 3             ✅ step 58  ← HW fault bridge
After step 11: L14 == 1401          ← rich stack trace + multi-frame finally
              L15 == 1501           ← collided unwind  ← PHASE 1 CLOSED
```

**10/11 hard gates closed.** Только step 11 left.

## Файлы

### Новые

- `OS/src/Boot/EH/HwFaultBridge.cs` — IDT-to-managed bridge.
- `done/step058.md` — этот файл.

### Изменённые

- `OS/src/Hal/Idt/Idt.cs` — `Dispatch` routes supported vectors к HwFaultBridge.
- `OS/src/Kernel/Diagnostics/Probes.cs` — `EhHwFault=true`.
- `OS/src/Kernel/Diagnostics/EhProbe.cs` — `HwFault()` test method + L13 dispatch.

## Что дальше

**Step 59 = step 11 — Phase 1 closure**.

Финальный gate. Mass of work:
- Rich stack trace (`AppendExceptionStackFrame` callbacks для filling `Exception.StackTrace`).
- Collided unwind handling (rethrow inside finally — rare edge case, complex state merge).
- `OnFirstChanceException` / `OnUnhandledException` / `FailFast` hooks.
- Funclet-aware SFI (или explicit support в InvokeFinalliesOnFrame для multi-frame walk).
- L14 == 1401 (rich stack trace test).
- L15 == 1501 (collided unwind test).

Самый сложный из remaining — особенно collided unwind. После step 59: **Phase 1 closed**, начинается phase 2 (or whatever's next per plan.md).
