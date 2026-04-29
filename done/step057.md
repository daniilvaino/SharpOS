# Step 57 — Phase 1 step 8: filter clauses → L11 == 1101

## Контекст

`catch (E) when (predicate)` — C# 6+ exception filter syntax. Компилируется ILC'ом в **Filter clause** (kind=2) — отдельный funclet body, который при unwinding получает control, evaluates predicate (вкл. type check), и returns 0/1. Если 1 — DispatchEx invoke'ит associated catch handler; если 0 — продолжает search.

Для Phase 1 step 8 gate L11 == 1101: один method с `catch (IOE) when (ex.Message == "eh11")`. Filter funclet вызывается с exception object и должен return 1.

## Решение

### `RhpCallFilterFunclet` shellcode

`OS/src/Boot/EH/CallFilterFuncletStub.cs` + `CallFilterFuncletPatcher.cs` — новые файлы. ~111 байт shellcode (наименьший funclet calling helper из трёх).

Сравнение с catch и finally:

| Aspect | Catch | Finally | **Filter** |
|---|---|---|---|
| Args | RCX/RDX/R8/R9 | RCX/RDX | RCX/RDX/R8 |
| Frame size | 0x48 | 0x38 | **0x28** |
| Restore nonvols | Yes | Yes | Yes |
| Funclet ABI | RCX=SP, RDX=ex | RCX=SP | RCX=SP, RDX=ex |
| Return | mov rsp+jmp rax | normal+writeback | **normal+RAX preserved** |
| Write-back | No | Yes | **No** |
| Head pop | Yes | No | **No** |

Filter — простейший: just call predicate, preserve RAX result through epilogue (pops don't touch RAX), return.

### Filter eval в `FindFirstPassHandler`

Раньше Filter clauses skipped в FFPH. Теперь:

```csharp
if (clause.Kind == ClauseKind.Filter
    && codeOffset >= clause.TryStartOffset
    && codeOffset < clause.TryEndOffset)
{
    int filterResult = filterFn(exceptionPtr, clause.FilterAddress, (RegDisplay*)iter);
    if (filterResult != 0)
    {
        result.Found = true;
        result.HandlerAddress = clause.HandlerAddress;
        ...
        return result;
    }
}
```

`clause.FilterAddress` — это predicate body's IP (CoffEhDecoder парсит отдельный varint для filter offset для kind=Filter clauses). `clause.HandlerAddress` — ассоциированный catch body.

### Signature change в `FindFirstPassHandler`

Добавлен `byte* exceptionPtr` parameter для pass'а в filter funclet. Updated callers: `Dispatch` + `RhpTest_ThrowIngress` (legacy probe path в ExceptionEngine.cs).

### Wiring

`OS/src/Boot/BootSequence.cs` — `InstallCallFilterFuncletShellcode()` рядом с finally patcher.
`OS/src/Kernel/Diagnostics/Probes.cs` — `EhFilter=true` (L11 gate).
`OS/src/Kernel/Diagnostics/EhProbe.cs` — `FilterClause()` test method.

## Результат

```
[info] Dispatch: kind=0x01 ...
[info]   iter ready: ControlPC=0x0E0E3681 SP=0xFE973F0 startIdx=0xFFFFFFFF (init from ExContext)
[info]     fp[0]: PC=0x0E0E3681 ehInit=Y methodStart=0x0E0E3650
[info]       clause[0] kind=2 try=[0x0B..0x32) off=0x31 type=0x0
[info]       filter[0] result=1
[info]   fp.Found=Y handler=0x0E0E36D7 idxCurClause=0 framesWalked=0
[info] eh L11 catch-when filter: val=1101   ← GATE GREEN
```

End-to-end:
1. `EhProbe.FilterClause()` — `try { throw IOE("eh11") } catch (IOE ex) when (ex.Message == "eh11") { return 1101; }`
2. ILC throw → RhpThrowEx → ingress → Dispatch.
3. First pass: 1 clause (kind=Filter, try=[0x0B..0x32)). codeOffset 0x31 in range.
4. RhpCallFilterFunclet invoked: restores nonvols, calls filter (RCX=establisher SP, RDX=exception). Filter body executes type check (already known) + `ex.Message == "eh11"` → returns 1.
5. FFPH sees result != 0 → match. Returns Found с HandlerAddress = catch body.
6. RhpCallCatchFunclet invokes catch → returns 1101. ✓

ILC сompiled `catch (IOE) when (predicate)` как ОДНА filter clause (not typed + filter pair). Filter funclet body itself does type check before evaluating predicate.

**No regression**: L1-L10 + 5.3-A green.

## Phase 1 progress

```
After step  1: L4 == 127            ✅ step 44
After step  2: L5 == 7              ✅ step 45
After step  3: L6 == 111            ✅ step 46
After step  4: L7 == 3              ✅ step 47
After step  5: L8 == 801            ✅ step 54
After step  6: L9 == 901            ✅ step 55
After step  7: L10 == 111           ✅ step 56
After step  8: L11 == 1101          ✅ step 57  ← filter
After step  9: L12 == 101           ← fault (likely free — same encoding as finally)
After step 10: L13 == 3             ← HW-fault bridge
After step 11: L14 == 1401          ← rich stack trace + multi-frame finally
              L15 == 1501           ← collided unwind  ← PHASE 1 CLOSED
```

**8/11 hard gates closed.** Next: step 9 — fault clauses.

## Файлы

### Новые

- `OS/src/Boot/EH/CallFilterFuncletStub.cs` — `[RuntimeExport("RhpCallFilterFunclet")]`.
- `OS/src/Boot/EH/CallFilterFuncletPatcher.cs` — 111-байт shellcode emitter.
- `done/step057.md` — этот файл.

### Изменённые

- `OS/src/Boot/EH/DispatchEx.cs` — `FindFirstPassHandler` signature + filter clause eval branch.
- `OS/src/Boot/ExceptionEngine.cs` — updated FFPH call site (signature change).
- `OS/src/Boot/BootSequence.cs` — `InstallCallFilterFuncletShellcode()`.
- `OS/src/Kernel/Diagnostics/Probes.cs` — `EhFilter=true`.
- `OS/src/Kernel/Diagnostics/EhProbe.cs` — `FilterClause()` test + L11 dispatch.

## Что дальше

**Step 58 = step 9 — fault clauses**.

Fault clauses (`try { } fault { ... }`) семантически runs только on exception path (не на normal exit, в отличие от finally). В IL distinct opcode но в NativeAOT EH info encoded **identically to finally** (kind=Fault). Step 7 (finally) уже invoke'ит kind=Fault clauses на second pass — поэтому step 9 может быть **free** или потребует только tweaks.

Test L12 == 101: try { throw } fault { x++ } — outer catches, fault ran perfectly normally because runtime semantics одна и та же. Возможно step 9 == zero changes + just a probe.
