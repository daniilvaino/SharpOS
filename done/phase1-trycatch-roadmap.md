# Phase 1 — managed try/catch/finally roadmap

Single source of truth для последнего открытого пункта Phase 1. План построен мудрецом 2 и refined empirical данными от probes (`probe_unwind_codes.ps1`, `probe_eh_trailer.ps1`).

**Цель**: полная реализация managed exception handling в SharpOS — не урезанная до longjmp, не отложенная до Phase 6. Phase 1 закрывается **только после step 11** (collided + filter + fault + HW-bridge + rich stack trace + полный 88-byte Exception shape + все 6 missing derived types).

**Estimate**: 3.5–6 месяцев focused работы.

## Зафиксированные решения

| Item | Decision | Rationale |
|---|---|---|
| Strategy | Option A (full unwinder), не B/C/D | Plan.md: «полноценный, НЕ longjmp». Параллельный setjmp world даёт вторую семантику которую потом выкинем. |
| Sequencing | Managed-dispatcher-first, RhpCallCatchFunclet рано | Convergence быстрее: видим где dispatcher работает, где halt'ит на funclet boundary с диагностикой. |
| Phase 1 closure | После step 11 (НЕ после 1.5b) | Filter handling — production gate из-за 38 implicit filter funclets в нашем binary; 1.5c нельзя оставить «на потом». |
| XMM6-XMM15 spill | Оставлять stock-compatible | Throw path холодный; 160 байт spill дёшево; cut'ит ABI fork bugs. Stock asm helpers (FUNCLET_CALL_PROLOGUE) реально пишут XMM в REGDISPLAY. |
| Unwind decoder MVP | 4 opcode'а: PUSH_NONVOL / ALLOC_SMALL / ALLOC_LARGE / SET_FPREG | Empirical: эти 4 покрывают 100% наших records. Прочие — hard-fail/log. |
| Trailer formula | Stock: `offsetof(UNWIND_INFO, UnwindCode) + 2*N`, +`ALIGN_UP+4` если EHANDLER/UHANDLER | Наша naive формула (`4 + roundUpDword(2*N)`) отсекала 78/83 EH records. |
| ehInfoRVA location | `.rdata` | Empirical confirmed для 5 clean-parsed records. |
| 6 derived exception types | Сразу в step 1 | NullRef, Overflow, DivByZero, InvalidCast, ArrayTypeMismatch, NotImplemented. `RhThrowEx`(`throw null`) превращает в NullRef через `GetRuntimeException` — без них runtime breaks. |
| `kind=3` records | Parser bug (off-by-2), не valid 4th kind | NativeAOT v7.0.20 source defines только ROOT/HANDLER/FILTER. |

## Текущее состояние binary (на момент написания)

- 698 RUNTIME_FUNCTION records, all `Unwind flags: None` (no Windows-personality usage).
- Opcode breakdown: PUSH_NONVOL × 1468, ALLOC_SMALL × 477, ALLOC_LARGE × 32, SET_FPREG × 8.
- Funclet kinds: ROOT × 574, HANDLER × 61, FILTER × 38.
- 83 records с HAS_EHINFO trailer flag.
- Sections: `.text`, `.unbox`, `.managed` (0x1CBBD bytes), `.rdata`, `.data`, `.pdata`, `.modules` (8 bytes), `.rsrc`, `.reloc`.

## 11-step roadmap

### Step 1 — Exception contract + classlib surface

**Что**:
- Полный NativeAOT-compatible `Exception` layout (88 bytes): `_message`, `_data`, `_innerException`, `_helpURL`, `_source`, `_HResult`, `_stackTraceString`, `_remoteStackTraceString`, `_corDbgStackTrace`, `_idxFirstFreeStackTraceEntry`.
- 6 missing derived types: `NullReferenceException`, `OverflowException`, `DivideByZeroException`, `InvalidCastException`, `ArrayTypeMismatchException`, `NotImplementedException`.
- Конструкторы `()` / `(string?)` / `(string?, Exception?)` для базы и каждого derived.
- Переписать `GetRuntimeException(int id)` чтобы возвращал concrete derived types per BCL ID enum.

**Файлы**:
- Новые: `std/no-runtime/shared/System/Exception.cs`, `std/no-runtime/shared/System/Exceptions.Derived.cs`.
- Изменить: `OS/src/Boot/ExceptionEngine.cs` (GetRuntimeException), `std/no-runtime/shared/Threading.cs` (вынести existing stub Exception types в отдельные файлы).
- `OS/src/Kernel/Diagnostics/EhProbe.cs` — добавить L4 probe.

**Smoke**: `EhProbe_L4_ExceptionShape == 127` (bitmask: каждый из 7 types правильно cтroitcя и `is`).

**Зависит от**: ничего.

### Step 2 — Coff method lookup + funclet→ROOT backward walk

**Что**:
- Managed `RUNTIME_FUNCTION` reader по `.pdata`.
- Binary search по `BeginAddress`/`EndAddress` для IP → runtime function.
- Backward walk до `UBF_FUNC_KIND_ROOT` (0x00). Stock `CoffNativeCodeManager.cpp:271-283` literally декрементирует `pRuntimeFunction--` пока trailer kind не станет ROOT.

**Файлы**:
- `OS/src/Boot/EH/CoffRuntimeFunctionTable.cs`
- `OS/src/Boot/EH/CoffMethodLookup.cs`

**Smoke**: `EhProbe_L5_RootWalk == 7`. Возьми метод с try/catch/finally; для main IP, catch handler IP, finally handler IP — все три должны mapping в один и тот же `mainRuntimeFunction.BeginAddress`.

**Зависит от**: step 1.

### Step 3 — EH trailer + ehInfoRVA decoder

**Что**:
- Parser standard UNWIND_INFO trailer:
  - `unwindSize = offsetof(UNWIND_INFO, UnwindCode) + 2 * CountOfUnwindCodes` (= `4 + 2*N`)
  - Если Windows unwind info имеет EHANDLER/UHANDLER: `unwindSize = ALIGN_UP(unwindSize, 4) + 4`
  - NativeAOT trailer byte читается по `unwindBlob + unwindSize`
  - Затем optional `associatedDataRVA` (если HAS_ASSOCIATED_DATA), optional `ehInfoRVA` (если HAS_EHINFO), затем GCInfo varint.
- `EHEnum` state, `RhEHClause` struct, managed `RhpEHEnumInit` / `RhpEHEnumNext`.
- При init: `pMethodStartAddress`, `pEHInfo`, `nClauses = VarInt::ReadUnsigned(...)`.
- При Next: декодировать `(tryStart, tryLen<<2|kind, handlerOff, typeRVA|filterOff)` по stock Coff decoder.

**Файлы**:
- `OS/src/Boot/EH/CoffEhDecoder.cs`
- `OS/src/Boot/EH/VarInt.cs`

**Smoke**: `EhProbe_L6_EhDecode == 111`. Метод A: try/finally, B: try/catch, C: try/catch when. Возвращай `100*filterCount + 10*finallyCount + typedCount`.

**Зависит от**: step 2. После фикса formula в наш `probe_eh_trailer.ps1` `kind=3` records должны исчезнуть, а `<not found>` ehRVAs очиститься почти полностью.

### Step 4 — StackFrameIterator + unwind decoder

**Что**:
- `PAL_LIMITED_CONTEXT`, `REGDISPLAY`, `StackFrameIterator`.
- `RhpSfiInit` / `RhpSfiNext`.
- Unwind decoder для **4 observed opcode'ов**:
  - `PUSH_NONVOL` — recover saved nonvol register from stack offset
  - `ALLOC_SMALL` — sub rsp, ≤128
  - `ALLOC_LARGE` — sub rsp, >128 (variant flag bit determines 16-bit vs 32-bit SIZE field)
  - `SET_FPREG` — mov rbp, rsp+offset (использует FrameRegister/FrameOffset из UNWIND_INFO header)
- Прочие opcode'ы (`SAVE_NONVOL`, `SAVE_XMM128`, `PUSH_MACHFRAME`, etc.) — log + hard-fail. Не silent skip.
- `RhpEHEnumInitFromStackFrameIterator` (stock first/second pass начинают именно с frameIter, не ad-hoc lookup).

**Файлы**:
- `OS/src/Boot/EH/PalLimitedContext.cs`
- `OS/src/Boot/EH/RegDisplay.cs`
- `OS/src/Boot/EH/StackFrameIterator.cs`

**Smoke**: `EhProbe_L7_FrameWalk == 3`. Цепочка `A → B → C → Walk()` с `MethodImplOptions.NoInlining` на каждом. Walk инициализирует iterator из current context, считает frames до A. Дополнительно assert: `SP` strictly monotonic upward, каждый `ControlPC` в `.text`/`.managed`.

**Зависит от**: step 3.

### Step 5 — Throw entry + first pass + catch funclet (FIRST GREEN)

**Что**:
- Shellcode thunks: `RhpThrowEx`, `RhpCallCatchFunclet`.
- В `OS/src/Boot/ExceptionEngine.cs`:
  - `RhThrowEx`
  - `DispatchEx`
  - `FindFirstPassHandler`
  - `ShouldTypedClauseCatchThisException`
  - `ExInfo` chain minimum.
- Stock x64 contract:
  - `RhpThrowEx`: spill nonvols + XMM6–XMM15, build PAL_LIMITED_CONTEXT, build local ExInfo, `m_passNumber = 1`, link to thread ExInfo stack, call managed `RhThrowEx`.
  - `RhpCallCatchFunclet(exception, pHandlerIP, REGDISPLAY*, ExInfo*)` (RCX/RDX/R8/R9) — non-local transfer: restore nonvols/FP/SP from REGDISPLAY/ExInfo, set resume stack, transfer to handler funclet so catch body continues normal managed flow.

**Файлы**:
- `OS/src/Boot/EH/ExceptionThunks.cs` — byte-array emitters
- `OS/src/Boot/ExceptionEngine.cs` — managed dispatcher

**Smoke**: `EhProbe_L8_TypedCatch == 801`. `Thrower()` (`[NoInlining]`) бросает `InvalidOperationException("eh8")`, caller ловит, возвращает 801 если `ex.Message == "eh8"`.

**Зависит от**: step 4.

**См. detailed breakdown** ниже — step 5 самый сложный, потенциально разбивается на 6 sub-steps (запрос мудрецу 2 в работе).

### Step 6 — Rethrow baseline

**Что**:
- Shellcode thunk `RhpRethrow`.
- Managed `RhRethrow`.
- Корректное использование `activeExInfo._idxCurClause` / startIdx, чтобы DispatchEx продолжал с правильной clause position при `throw;`.
- Stock runtime для rethrow создаёт новый ExInfo, копирует rethrown exception object на стек, вызывает `DispatchEx(ref exInfo._frameIter, ref exInfo, activeExInfo._idxCurClause)`. Отдельный path, не plain RhpThrowEx.
- НЕ делать collided unwind здесь — только baseline rethrow semantics.

**Файлы**:
- `OS/src/Boot/EH/ExceptionThunks.cs` — добавить `RhpRethrow`
- `OS/src/Boot/ExceptionEngine.cs` — `RhRethrow`

**Smoke**: `EhProbe_L9_Rethrow == 901`. Inner catch'ит и `throw;`-ит, outer catch'ит и возвращает 901 если message сохранился.

**Зависит от**: step 5.

### Step 7 — Second pass + finally funclet

**Что**:
- `InvokeSecondPass(ref exInfo, idxStart)` + overload с `idxLimit`.
- Shellcode thunk `RhpCallFinallyFunclet`.
- В DispatchEx: если найден catch выше по стеку, partial second pass до catching try region, потом RhpCallCatchFunclet. Без catch: full second pass.
- InvokeSecondPass инициализирует EHEnum, вызывает `RhpCallFinallyFunclet(pFinallyHandler, exInfo._frameIter.RegisterSet)` per fault/finally clause.

**Файлы**:
- `OS/src/Boot/EH/ExceptionThunks.cs` — `RhpCallFinallyFunclet`
- `OS/src/Boot/ExceptionEngine.cs` — `InvokeSecondPass`

**Smoke**: `EhProbe_L10_FinallyThrow == 111`. inner-try бросает, outer catches; finally между ними должен выполниться **до** catch body. Counter `1 → +10 (try body) → +100 (finally) → catch reads 111`.

**Зависит от**: step 6.

### Step 8 — Filter clauses

**Production gate, не optional**: 38 implicit filter funclets уже в нашем binary.

**Что**:
- Shellcode thunk `RhpCallFilterFunclet`.
- В FindFirstPassHandler: filter branch — вызвать filter funclet, при `result != 0` выбрать handler.
- Не трогать second pass — filters first-pass decision.
- Stock: `RhpCallFilterFunclet(exception, filterIP, REGDISPLAY*)` (RCX/RDX/R8) → result в RAX.

**Файлы**:
- `OS/src/Boot/EH/ExceptionThunks.cs` — `RhpCallFilterFunclet`
- `OS/src/Boot/ExceptionEngine.cs` — filter branch

**Smoke**: `EhProbe_L11_Filter == 1101`. `catch (E e) when (ex.Message == "f11")` ловит, не-matching catch не triggert'ся.

**Зависит от**: step 7.

### Step 9 — Fault clauses

**Что**:
- В InvokeSecondPass: fault clause semantics — вызывать только при exception unwind, не на normal exit.
- Funclet-calling machinery переиспользуется from finally; clause-kind routing должен различаться.
- C# не имеет fault syntax → smoke через build-time-generated IL test assembly или эквивалент.

**Файлы**:
- `OS/src/Boot/ExceptionEngine.cs` — fault branch в second pass
- `OS/src/Kernel/Diagnostics/EhFaultProbeHost.cs` — C# wrapper around IL-only probe
- Build-time generator для `EhFaultProbe.dll` (или эквивалент)

**Smoke**: `EhProbe_L12_Fault == 101`. IL body: `try { throw } fault { counter += 100 }`. Counter starts at 1, fault выполняется во время unwind → `1 + 100 = 101`.

**Зависит от**: step 8.

### Step 10 — Hardware-fault bridge

**Что**:
- Shellcode `RhpThrowHwEx` вместо halt-stub.
- Managed `RhThrowHwEx`.
- Bridge из IDT vector 14 (#PF) / vector 0 (#DE) / vector 4 (#OF) → `RhpThrowHwEx`.
- Runtime-exception mapping для NullReferenceException / DivideByZeroException / OverflowException — уже готов из step 1.
- Stock `RhpThrowHwEx` собирает synthetic machine frame, сохраняет nonvols + XMM6–XMM15, создаёт ExInfo с `ExKind.HardwareFault`, зовёт managed RhThrowHwEx.

**Файлы**:
- `OS/src/Boot/EH/ExceptionThunks.cs` — `RhpThrowHwEx`
- `OS/src/Boot/ExceptionEngine.cs` — `RhThrowHwEx`
- `OS/src/Hal/Idt/Idt.cs` (или подобное) — fault → managed bridge glue

**Smoke**: `EhProbe_L13_HwBridge == 3`. `int z = 0; return 1/z;` → ловится `catch (DivideByZeroException)`. `null!.Field` → ловится `catch (NullReferenceException)`. Bitmask result.

**Зависит от**: step 9.

### Step 11 — Rich stack trace + first-chance/unhandled hooks + collided unwind (PHASE 1 CLOSURE)

**Что**:
- Реальный `AppendExceptionStackFrame` — пишет в `_corDbgStackTrace` / `_idxFirstFreeStackTraceEntry`.
- `OnFirstChanceException` — реальный hook.
- `OnUnhandledException` — реальный hook.
- Collided unwind: новый `throw` из finally во время unwind → корректно создаёт новый ExInfo и продолжает dispatch с правильного места.
- Stock `Exception.NativeAot.cs` хранит rich stack trace в `_corDbgStackTrace` + `_idxFirstFreeStackTraceEntry`. AppendExceptionStackFrame обновляет, обрабатывает first/rethrow frames. DispatchEx имеет collided-unwind path: «copy exception object to this stack location», затем `DispatchEx(..., activeExInfo._idxCurClause)`.

**Файлы**:
- `OS/src/Boot/ExceptionEngine.cs` — реальный AppendExceptionStackFrame, collided path, hooks
- `std/no-runtime/shared/System/Exception.cs` — properties/методы поверх stack fields
- `OS/src/Kernel/Diagnostics/EhProbe.cs` — final regression pack

**Smoke A — rich stack trace**: `EhProbe_L14_StackTrace == 1401`. `A → B → C → throw`. Catch получает ex; `ex.GetStackIPs()` возвращает array length ≥ 3, первые два IP non-zero. `firstChance` hook сработал ровно 1 раз.

**Smoke B — collided unwind**: `EhProbe_L15_Collided == 1501`. `try { ThrowA(); } finally { ThrowB(); }` обёрнуто в `catch (NotImplementedException ex)` — должна поймать B (новый throw перебивает A в unwind).

**Зависит от**: step 10. После прохода — Phase 1 закрыт.

## Hard gates между шагами

```
After step 1:  L4_ExceptionShape   == 127
After step 2:  L5_RootWalk         == 7
After step 3:  L6_EhDecode         == 111
After step 4:  L7_FrameWalk        == 3
After step 5:  L8_TypedCatch       == 801    <-- FIRST GREEN
After step 6:  L9_Rethrow          == 901
After step 7:  L10_FinallyThrow    == 111
After step 8:  L11_Filter          == 1101
After step 9:  L12_Fault           == 101
After step 10: L13_HwBridge        == 3
After step 11: L14_StackTrace      == 1401
               L15_Collided        == 1501  <-- PHASE 1 CLOSED
```

Любой шаг с red gate блокирует следующий — не двигаемся вперёд пока не зелёный.

## Step 5 detailed breakdown (от мудреца 2)

Step 5 — самый сложный единый шаг (3-4 недели по estimate). Может разбиться на 6 sub-steps; запрос на детализацию у мудреца 2 в работе.

### ExInfo struct (SharpOS ABI — внутренне self-consistent, не обязан быть byte-for-byte stock)

```
0x00  _pPrevExInfo        (8)
0x08  _pExContext         (8)         // PAL_LIMITED_CONTEXT*
0x10  _exception          (8)
0x18  _kind               (4)
0x1C  _passNumber         (1)
0x1D  pad                 (3)
0x20  _idxCurClause       (4)
0x24  pad                 (4)
0x28  _frameIter          (opaque, 16-byte aligned)
...   _notifyDebuggerSP   (8)
```

**Asm thunk пишет** на entry: `_pPrevExInfo`, `_pExContext`, `_kind`, `_passNumber`, `_idxCurClause`.
**Managed dispatcher** обновляет: `_exception`, `_kind` (rethrow/HwFault flags), `_frameIter`, `_notifyDebuggerSP`, `_idxCurClause` (как «handler active» state).

### PAL_LIMITED_CONTEXT layout

```
0x00  Rip
0x08  Rsp
0x10  Rbp
0x18  Rbx
0x20  Rsi
0x28  Rdi
0x30  R12
0x38  R13
0x40  R14
0x48  R15
0x50  Xmm6
...
0xE0  Xmm15
```

### REGDISPLAY layout (минимум для step 5)

```
0x00  SP
0x08  ControlPC
0x10  OriginalControlPC
0x18  FP
0x20  pRbx              (pointer to saved Rbx)
0x28  pRbp
0x30  pRsi
0x38  pRdi
0x40  pR12
0x48  pR13
0x50  pR14
0x58  pR15
0x60  Xmm[10]           (xmm6..xmm15 snapshots)
```

Critical: managed dispatcher и funclet thunks operate either on values or on addresses of saved nonvols; самые опасные баги тут — «одни части runtime думают что тут value, а другие — что pointer». Один `AsmOffsets.cs` файл, не дублировать литералы.

### RhpThrowEx contract

**Entry**: `RCX = exception object`, no return.

**Body**:
1. Spill callee-saved GPRs (rbx/rbp/rsi/rdi/r12-r15).
2. Spill xmm6..xmm15.
3. Build PAL_LIMITED_CONTEXT on stack.
4. Build stack-local ExInfo:
   - `_pPrevExInfo` = thread's m_pExInfoStackHead
   - `_pExContext` = &PAL_LIMITED_CONTEXT
   - `_kind` = Software
   - `_passNumber` = 1
   - `_idxCurClause` = MaxTryRegionIdx
5. Update thread's m_pExInfoStackHead = &ExInfo.
6. Call managed `RhThrowEx(exception, ref ExInfo)`.
7. `int 3` if returned (should never).

### RhpCallCatchFunclet contract

**Entry**: `RCX = exception object`, `RDX = handler funclet IP`, `R8 = REGDISPLAY*`, `R9 = ExInfo*`.

**Behaviour**: NOT a normal call/ret. Non-local transfer:
- Restore nonvols/FP/SP from REGDISPLAY/ExInfo so handler funclet sees parent's locals.
- Validate ExInfo pop, unlink ExInfo chain back from m_pExInfoStackHead to next ExInfo above new SP.
- Unhijack thread.
- `mov rsp, R8->SP` (resume SP), `jmp RDX` (handler IP).

### Smoke checkpoint chain для step 5

13-step trace от throw до зелёного catch (логи на каждом):

1. **RhpThrowEx entry** — exception object ptr, current RIP, current RSP.
2. **RhpThrowEx built ExInfo** — `_pPrevExInfo`, `_pExContext`, `_kind`, `_passNumber`, `_idxCurClause`.
3. **RhThrowEx entry** — exception type=InvalidOperationException, message=eh8.
4. **StackFrameIterator init** — ControlPC, SP, FP.
5. **DispatchEx first-pass start** — startIdx=MaxTryRegionIdx.
6. **frame[0] method found** — root BeginAddress, current runtimeFunction, isFunclet flag.
7. **RhpEHEnumInitFromStackFrameIterator = true** — methodStart, clause count.
8. **FindFirstPassHandler clause scan** — per clause: kind, try range, handler RVA, typed type / filter indicator.
9. **ShouldTypedClauseCatchThisException = true** — selected catchingTryRegionIdx, pCatchHandler.
10. **DispatchEx selected handlingFrameSP** — target SP and handler IP.
11. **RhpCallCatchFunclet entry** — RCX/RDX/R8/R9 + restored SP/FP.
12. **catch body entered** — ex.Message == "eh8".
13. **probe returns 801**.

**Локализация при failure**:
- До шага 4 — проблема в RhpThrowEx / ExInfo build.
- Шаги 6-8 — `.pdata` / root-walk / ehInfo decode.
- Шаг 9 — type matching.
- Шаги 10-11 — REGDISPLAY / catch funclet transfer ABI.

## Step 5 sub-breakdown (6 sub-steps от мудреца 2)

Self-contained build order. Каждый sub-step имеет свой gate, локализующий любой failure в маленькую область поиска.

### Layouts (non-UNIX AMD64, NativeAOT 7.0.20)

```
SIZEOF__ExInfo                  = 0x260
SIZEOF__PAL_LIMITED_CONTEXT     = 0x100
SIZEOF__REGDISPLAY              = 0x130
SIZEOF__StackFrameIterator      = 0x230
SIZEOF__EHEnum                  = 0x20
```

**ExInfo offsets** (как stock NativeAOT, на которые завязаны asm helpers):
```
m_pPrevExInfo        = 0x00
m_pExContext         = 0x08
m_exception          = 0x10
m_kind               = 0x18  (1 byte)
m_passNumber         = 0x19  (1 byte)
m_idxCurClause       = 0x1C  (4 bytes)
m_frameIter          = 0x20
m_notifyDebuggerSP   = 0x250
```

**PAL_LIMITED_CONTEXT offsets** (non-UNIX AMD64):
```
IP    = 0x00
Rsp   = 0x08
Rbp   = 0x10
Rdi   = 0x18
Rsi   = 0x20
Rax   = 0x28
Rbx   = 0x30
R12   = 0x38
R13   = 0x40
R14   = 0x48
R15   = 0x50
Xmm6  = 0x60   ... через 16 bytes
Xmm15 = 0xF0
```

**REGDISPLAY offsets** (subset для step 5):
```
SP    = 0x78
pRbx  = 0x18
pRbp  = 0x20
pRsi  = 0x28
pRdi  = 0x30
pR12  = 0x58
pR13  = 0x60
pR14  = 0x68
pR15  = 0x70
Xmm   = 0x90
```

**StackFrameIterator offsets** (managed shape):
```
m_FramePointer
m_ControlPC
m_RegDisplay
m_OriginalControlPC
```

### 5.1 — Ingress-only throw thunk

**Что добавляется**:
- `AsmOffsets.cs` с layouts выше.
- Real shellcode `RhpThrowEx`.
- Временный managed seam `RhpTest_ThrowIngress(object ex, ref ExInfo exInfo)` который логирует и halt'ит.
- Все остальные EH exports — halt-stub: `RhpSfiInit`, `RhpSfiNext`, `RhpEHEnumInitFromStackFrameIterator`, `RhpEHEnumNext`, `RhpCallCatchFunclet`, `RhpCallFinallyFunclet`, `RhpCallFilterFunclet`.
- Single-thread head: `static IntPtr s_exInfoHead` вместо `Thread.m_pExInfoStackHead`.

**Smoke**: `throw new TestEx();` доходит до `RhpTest_ThrowIngress` и логирует:
- `pass = 1`
- `kind = Throw` (= 1)
- `idxCurClause = 0xFFFFFFFF`
- `pExContext->IP == return-address throw-site`
- `s_exInfoHead == &exInfo`

После лога — `int3`.

**Зависит от**: steps 1-4 + RuntimeExport patcher.

**Failure-localization**:
- Не доходит до managed probe → bug в patcher / export body / ABI.
- Доходит, но IP/RSP мусор → bug в throw-site capture.
- kind/pass/idx мусор → bug в ExInfo offsets.
- head chain не совпадает → bug в s_exInfoHead plumbing.

### 5.2 — RhpSfiInit до одного валидного frame

**Что добавляется**:
- Real `RhpSfiInit(ref StackFrameIterator, PAL_LIMITED_CONTEXT*, bool)`. RhpSfiNext остаётся halt.
- Managed seam `RhpTest_SfiInit(ref ExInfo exInfo)` → `exInfo._frameIter.Init(exInfo._pExContext, instructionFault: false)` и логирует `ControlPC`, `OriginalControlPC`, `SP`, `FramePointer`.

**Smoke**: из `RhpTest_ThrowIngress` вместо int3 вызвать `RhpTest_SfiInit`. Ожидаем:
- `Init == true`
- `ControlPC` внутри throwing method
- `SP` правдоподобен и выше адреса локального ExInfo
- `RegisterSet != null`

**Зависит от**: 5.1 зелёный.

**Failure-localization**:
- 5.1 OK, 5.2 fail → bug в PAL_LIMITED_CONTEXT layout или в первом unwind entry. НЕ в ExInfo.
- ControlPC off-by-one → instruction-fault vs managed-throw semantics. RhpThrowEx должен брать `[rsp]` как return address и класть в `IP`.

### 5.3 — EH enumeration без dispatch

**Что добавляется**:
- Real `RhpEHEnumInitFromStackFrameIterator` и `RhpEHEnumNext`.
- Managed `EHEnum` struct (size only, 0x20 bytes).

**Smoke**: отдельный test method без throw, но с одним `catch (MyEx)`. Seam берёт инициализированный StackFrameIterator, вызывает `RhpEHEnumInit...`, гоняет `RhpEHEnumNext`, логирует clauses.
- count == 1
- clauseKind == typed
- handlerAddress != 0
- tryStart < codeOffset < tryEnd

**Зависит от**: 5.2 зелёный.

**Failure-localization**:
- SfiInit OK, enum пустой/битый → bug в EH info parser / method start address / code offset math.
- Clause есть, но codeOffset не попадает → bug в ControlPC normalization.

### 5.4 — First-pass handler decision без real catch thunk

**Что добавляется**:
- Real managed `FindFirstPassHandler` через отдельный seam (не full DispatchEx).
- `RhpCallCatchFunclet` остаётся halt.
- `RhpCallFilterFunclet` для typed-catch не нужен.

**Smoke**: на L8_TypedCatch-style методе:
- `found == true`
- `handler != 0`
- `tryRegionIdx == expected`

После лога — halt до second pass.

**Зависит от**: 5.3 зелёный.

**Failure-localization**:
- 5.3 OK, 5.4 fail → typed clause match (TypeCast.IsInstanceOfException), code-offset interval semantics, или wrong startIdx handling. НЕ RhpCallCatchFunclet (он ещё halt).

### 5.5 — Standalone RhpCallCatchFunclet

**Что добавляется**:
- Real shellcode `RhpCallCatchFunclet`. Делается в два этапа:
  - **5.5a minimal**: без ThreadStateFlags, без ThreadAbort, без INLINE_THREAD_UNHIJACK, без RhpValidateExInfoPop.
  - **5.5b stock-closer**: добавить stock хвост.
- Stock semantic core:
  - Сохранить nonvols + XMM6-XMM15.
  - Прочитать preserved regs из REGDISPLAY (pRbx/pRbp/pRsi/pRdi/pR12-15, Xmm, SP).
  - `RCX = establisher frame (REGDISPLAY.SP)`, `RDX = exception object`, call handler IP.
  - `RAX = resume IP`.
  - Update s_exInfoHead.
  - `mov rsp, resumeSP; jmp rax`.

**Smoke**: НЕ через throw. Отдельный seam:
- Заранее подготовить REGDISPLAY.
- Вызвать `RhpCallCatchFunclet(exception, handler, regdisplay, exInfo)`.
- Tiny catch-funclet пишет `s_probe = 701` и возвращает continuation label.

Gate: `s_probe == 701`, continuation reached, s_exInfoHead popped.

**Зависит от**: 5.1 зелёный (5.2/5.3/5.4 для standalone catch thunk не обязательны).

**Failure-localization**:
- Handler не вызывается → argument mapping RCX/RDX.
- Handler OK, continuation не reached → RAX resume IP / `jmp rax` contract.
- Continuation OK, registers wrecked → REGDISPLAY offsets / preserved-reg restore.
- Nested throws break later → head-pop logic.

### 5.6 — Full typed-catch path (L8_TypedCatch == 801)

**Что добавляется**:
- `RhpThrowEx` зовёт **real** managed `RhThrowEx` (не RhpTest_ThrowIngress).
- Real `RhpSfiInit`, `RhpSfiNext`, `RhpEHEnumInit...`, `RhpEHEnumNext`, `RhpCallCatchFunclet` — все включены.
- `RhpCallFilterFunclet` и `RhpCallFinallyFunclet` могут быть halt (если probe — чистый typed catch без filter/finally).

**Smoke**: `L8_TypedCatch == 801`. Milestones:
- First pass finds handler.
- Second pass reaches RhpCallCatchFunclet.
- No FallbackFailFast.
- No halt stubs triggered.

**Зависит от**: 5.1-5.5 все зелёные.

**Failure-localization**:
- 5.4 OK + 5.5 OK, 5.6 fail → glue между DispatchEx second-pass и catch thunk: `idxCurClause`, `startIdx/idxLimit`, ExInfo head chain, RhpSfiNext unwinding semantics между first и second pass.

### Минимальный AsmOffsets.cs (для step 5)

**Нужны уже в 5.1**:
- ExInfo: SIZEOF + все offsets.
- PAL_LIMITED_CONTEXT: SIZEOF + IP, Rsp, Rbp, Rdi, Rsi, Rax, Rbx, R12-R15, Xmm6-Xmm15.
- REGDISPLAY: SIZEOF + SP, pRbx, pRbp, pRsi, pRdi, pR12-pR15, Xmm. (pRax/pRcx etc. не нужны для typed catch.)
- StackFrameIterator: SIZEOF + m_FramePointer, m_ControlPC, m_RegDisplay, m_OriginalControlPC.
- EHEnum: SIZEOF only.

**Можно отложить до 5.5b / step 7+**:
- Thread.m_ThreadStateFlags
- Thread.m_threadAbortException
- TrapThreadsFlags_AbortInProgress
- Anything debugger/profiler-related

### Runtime export ordering

**Сразу real**:
- `RhpThrowEx`
- `RhpFallbackFailFast` (halt)
- Managed seams `RhpTest_ThrowIngress`, `RhpTest_SfiInit`, `RhpTest_EHEnum`, `RhpTest_CallCatchOnly`.

**Сразу halt-stub** (чтобы линковка не сломалась):
- `RhpSfiInit`, `RhpSfiNext`
- `RhpEHEnumInitFromStackFrameIterator`, `RhpEHEnumNext`
- `RhpCallCatchFunclet`, `RhpCallFilterFunclet`, `RhpCallFinallyFunclet`
- `RhpCopyContextFromExInfo`

**Заменять real по одному**:
- 5.2: RhpSfiInit
- 5.3: RhpEHEnumInit..., RhpEHEnumNext
- 5.5: RhpCallCatchFunclet
- 5.6: RhpSfiNext (если ещё не включён)

Нет circular dependency — flow линейный: `RhpThrowEx` asm → managed RhThrowEx → DispatchEx → SFI Init/Next + EH enum → RhpCallCatchFunclet.

### Shellcode skeleton — RhpThrowEx (5.1 first-cut)

```
; entry: RCX = exception object
48 8D 44 24 08             lea  rax, [rsp+8]       ; throw-site RSP
48 8B 14 24                mov  rdx, [rsp]         ; throw-site RIP

41 57 / 41 56 / 41 55 / 41 54   push r15/r14/r13/r12
53 / 56 / 57 / 55               push rbx/rsi/rdi/rbp

48 81 EC xx xx xx xx       sub  rsp, FRAME_SIZE    ; align + locals + shadow

; XMM6..XMM15 spills (optional in 5.1, mandatory in 5.5b)

48 89 84 24 ?? ?? ?? ??    mov  [rsp+CTX_RSP], rax
48 89 94 24 ?? ?? ?? ??    mov  [rsp+CTX_IP ], rdx

48 8D 94 24 ?? ?? ?? ??    lea  rdx, [rsp+EXINFO]      ; arg1 = &ExInfo
48 8D 84 24 ?? ?? ?? ??    lea  rax, [rsp+CTX]         ; rax = &PAL
48 89 82 08 00 00 00       mov  [rdx+0x08], rax        ; m_pExContext

49 B8 ?? ?? ?? ?? ?? ?? ?? ?? mov r8, &s_exInfoHead
4D 8B 08                   mov  r9, [r8]
4C 89 0A                   mov  [rdx+0x00], r9         ; m_pPrevExInfo

48 31 C0                   xor  rax, rax
48 89 42 10                mov  [rdx+0x10], rax        ; m_exception = null
C6 42 18 01                mov  byte [rdx+0x18], 1     ; kind=Throw
C6 42 19 01                mov  byte [rdx+0x19], 1     ; pass=1
C7 42 1C FF FF FF FF       mov  dword [rdx+0x1C], 0xFFFFFFFF

49 89 10                   mov  [r8], rdx              ; s_exInfoHead = &exInfo

49 BA ?? ?? ?? ?? ?? ?? ?? ?? mov r10, RhpTest_ThrowIngress
41 FF D2                   call r10
CC                         int3
```

Соответствует stock RhpThrowEx семантически: capture throw-site RSP/RIP, build ExInfo, link head-chain, write PAL*, transfer to managed RhThrowEx. Stock additionally делает INLINE_GETTHREAD, INLINE_THREAD_UNHIJACK, full XMM/nonvol save block — добавим в 5.5b/step 7.

ABI nuances:
- UEFI x64 = MS x64 ABI: caller даёт 32-byte shadow space, перед `call` rsp выровнен так что callee входит с rsp%16==8.
- Сразу после `call` стоит int3: managed RhThrowEx и ingress probe не должны возвращать.

### Shellcode skeleton — RhpCallCatchFunclet (5.5a minimal)

```
; entry:
;   RCX = exception object
;   RDX = handler IP
;   R8  = REGDISPLAY*
;   R9  = ExInfo*

41 57 / 41 56 / 41 55 / 41 54   push r15/r14/r13/r12
53 / 56 / 57 / 55               push rbx/rsi/rdi/rbp

48 81 EC xx xx xx xx       sub  rsp, FRAME_SIZE       ; shadow + locals + xmm

48 89 8C 24 ?? ?? ?? ??    mov  [rsp+ARG_EX ], rcx
48 89 94 24 ?? ?? ?? ??    mov  [rsp+ARG_IP ], rdx
4C 89 84 24 ?? ?? ?? ??    mov  [rsp+ARG_REG], r8
4C 89 8C 24 ?? ?? ?? ??    mov  [rsp+ARG_EXI], r9

; restore xmm6..xmm15 from REGDISPLAY.Xmm (5.5a optional / 5.5b mandatory)

49 8B 40 18                mov  rax, [r8+0x18]   ; pRbx
48 8B 18                   mov  rbx, [rax]
49 8B 40 20                mov  rax, [r8+0x20]   ; pRbp
48 8B 28                   mov  rbp, [rax]
49 8B 40 28                mov  rax, [r8+0x28]   ; pRsi
48 8B 30                   mov  rsi, [rax]
49 8B 40 30                mov  rax, [r8+0x30]   ; pRdi
48 8B 38                   mov  rdi, [rax]
49 8B 80 58 00 00 00       mov  rax, [r8+0x58]   ; pR12
4C 8B 20                   mov  r12, [rax]
49 8B 80 60 00 00 00       mov  rax, [r8+0x60]   ; pR13
4C 8B 28                   mov  r13, [rax]
49 8B 80 68 00 00 00       mov  rax, [r8+0x68]   ; pR14
4C 8B 30                   mov  r14, [rax]
49 8B 80 70 00 00 00       mov  rax, [r8+0x70]   ; pR15
4C 8B 38                   mov  r15, [rax]

49 8B 88 78 00 00 00       mov  rcx, [r8+0x78]   ; establisher frame = SP
48 8B 94 24 ?? ?? ?? ??    mov  rdx, [rsp+ARG_EX]
48 8B 84 24 ?? ?? ?? ??    mov  rax, [rsp+ARG_IP]
FF D0                      call rax              ; returns resume IP in RAX

; minimal single-thread ExInfo pop
48 8B 8C 24 ?? ?? ?? ??    mov  rcx, [rsp+ARG_EXI]
48 8B 09                   mov  rcx, [rcx]       ; prev = m_pPrevExInfo
49 B8 ?? ?? ?? ?? ?? ?? ?? ?? mov r8, &s_exInfoHead
49 89 08                   mov  [r8], rcx

4C 8B 84 24 ?? ?? ?? ??    mov  r8, [rsp+ARG_REG]
49 8B 90 78 00 00 00       mov  rdx, [r8+0x78]   ; resume SP
48 89 D4                   mov  rsp, rdx
FF E0                      jmp  rax
```

Если handler вернул `RAX=0`, делать `int3` вместо `jmp rax`.

Stock helper между `call handler` и `mov rsp,resumeSP` ещё имеет:
- Optional debug RhpValidateExInfoPop
- INLINE_THREAD_UNHIJACK
- ExInfo chain pop по resumeSP
- Thread-abort special-case

Это в 5.5b / step 6+, не в 5.5a.

### Что honest constraints срезают

**Можно отложить**:
- TLS Thread*; m_pExInfoStackHead → static head
- INLINE_THREAD_UNHIJACK
- m_ThreadStateFlags / DoNotTriggerGc
- m_threadAbortException
- Debugger / profiler hooks
- RhpValidateExInfoPop
- Reverse-pinvoke / native-boundary cases

**НЕ срезаются** (это core mechanics NativeAOT EH):
- ExInfo layout
- PAL_LIMITED_CONTEXT
- StackFrameIterator.Init/Next
- EH enumeration
- RhpCallCatchFunclet resume contract (RAX continuation + REGDISPLAY.SP as resume SP)

DispatchEx managed-side жёстко ожидает именно этот конвейер.

## Sage 2 reservation (продолжение)

Мудрец 2 предложил: «следующим сообщением я дам готовый `AsmOffsets.cs` для non-UNIX AMD64 в C#-константах и два C# emitter skeleton'а: один для 5.1 RhpThrowEx, второй для 5.5 RhpCallCatchFunclet».

Решение по этому offer'у — после старта step 1 implementation. Даже если не запросим — у нас уже все необходимые данные (sizes, offsets, opcode bytes выше).

## Источники

- `gc-experiment/dotnet-runtime/src/coreclr/nativeaot/Runtime/amd64/ExceptionHandling.asm` (asm thunks)
- `gc-experiment/dotnet-runtime/src/coreclr/nativeaot/Runtime.Base/src/System/Runtime/ExceptionHandling.cs` (managed dispatcher)
- `gc-experiment/dotnet-runtime/src/coreclr/nativeaot/Runtime/windows/CoffNativeCodeManager.cpp` (Coff EH decoder, root-walk)
- `gc-experiment/dotnet-runtime/src/coreclr/nativeaot/System.Private.CoreLib/src/System/Exception.NativeAot.cs` (Exception field offsets)
- `gc-experiment/dotnet-runtime/src/coreclr/nativeaot/Test.CoreLib/src/System/RuntimeExceptionHelpers.cs` (classlib contract template)
