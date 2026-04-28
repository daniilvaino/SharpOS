# Sage 2 — sub-step 5.5 (RhpCallCatchFunclet shellcode) detailed questions

## Контекст

Phase 1 step 5 sub-steps 5.1-5.4 закрыты, infrastructure verified end-to-end:
- 5.1 RhpThrowEx shellcode builds PAL + ExInfo + head chain → ingress seam.
- 5.2 StackFrameIterator.Init reads correctly from PAL.
- 5.3 EH enumeration on live frame works (probes A direct + B throw chain).
- 5.4 FindFirstPassHandler walks frames, matches typed clauses via `IsAssignableFromClass`. Live test returns `framesWalked=1, handler=0x0E0ED166, idxCurClause=0` for `IngressThrow` (outer try/catch catching `InvalidOperationException`).

Все остальные components ready: ExInfo struct, PAL_LIMITED_CONTEXT, RegDisplay (with pNonvol + SP + ControlPC + Xmm[10] at offset 0x90), CoffMethodLookup, CoffEhDecoder, varint reader, single-thread `s_head` static.

Sub-step 5.5 — `RhpCallCatchFunclet` shellcode. Это самый рискованный единичный shellcode шага 5 (non-local transfer + register restore + ExInfo head pop). Хочется максимально согласовать contract до начала coding.

## Cross-reference observation

MOOS (https://github.com/nifanfa/MOOS) и Cosmos cloned/checked: **никто из managed-OS проектов не имеет proper RhpCallCatchFunclet/funclet-dispatch implementation**. Все они redirect'ят throw'ы в `Error(string)`/`Halt()` style helpers — panic-on-throw, нет unwinder'а. Sage 2 ранее упоминал что у Cosmos partial EH с открытыми багами, у MOSA `fault` clauses не supported — MOOS ещё проще: `ThrowHelpers.cs` literally вызывает C `Error(string)` для всех built-in exception helpers.

Это подтверждает что наш подход (port stock NativeAOT EH с funclet-dispatching + REGDISPLAY + ExInfo chain) — uncharted territory среди managed OS projects. Stock NativeAOT snapshot remains единственным reference.

## Что я узнал из research'а

**Stock asm walkthrough** (`Runtime/amd64/ExceptionHandling.asm:343-518`):

Entry: `RCX=exception`, `RDX=handlerIP`, `R8=REGDISPLAY*`, `R9=ExInfo*`.

Body:
1. **Prolog**: 8 nonvol pushes + sub rsp, 0xE8 (shadow + 3 locals + 10 XMM saves).
2. **Save args** at `[rsp+0x128 + 0x00..0x18]`.
3. **Get Thread, save** at `[rsp+0xA0]`. Compare exception to `m_threadAbortException`, set abort flag.
4. **Lock and** `m_ThreadStateFlags`, clear `TSF_DoNotTriggerGc`.
5. **Restore nonvols** indirectly: `mov rax, [r8 + offset_pNonvol]; mov reg, [rax]` — for rbx/rbp/rsi/rdi/r12-r15.
6. **Restore XMM6..XMM15** directly: `movdqa xmmN, [r8 + 0x90 + N*0x10]`.
7. **Setup handler call**: `mov rcx, [r8+0x78]` (SP = establisher), `mov rdx, [args+0x00]` (exception), `call qword ptr [args+0x08]` (handler).
8. **Handler returns RAX=resume IP**.
9. (Debug) RhpValidateExInfoPop.
10. **INLINE_THREAD_UNHIJACK** — clears m_pvHijackedReturnAddress if set.
11. **ExInfo chain pop loop**: walk `m_pPrevExInfo` until first entry that's `>=` resumeSP, set `m_pExInfoStackHead` to it.
12. **Optional ThreadAbort rethrow**: if `TrapThreadsFlags_AbortInProgress` set + this was abort exception → `mov rsp, r8; jmp RhpThrowHwEx`.
13. **Normal continuation**: `mov rsp, r8; jmp rax`.

**Resume IP origin** (per agent research):
- Catch funclet — separate compiled function (own .pdata HANDLER entry).
- ILC emits funclet body that runs catch code, then computes "continuation IP" (label after entire try/catch in parent method) и returns it в RAX (`mov rax, label_after_try_catch; ret`).
- Funclet uses regular call/ret + standard prologue (pushes nonvols).
- After RhpCallCatchFunclet's `mov rsp, REGDISPLAY->SP; jmp rax` — control resumes inside parent method body at the post-try-catch point, with nonvols/RBP/RSP restored to parent's state at throw time.

**Single-thread SharpOS constraints**:
- No TLS; no per-thread Thread* struct. `s_head` is static IntPtr.
- No hijacking, no thread suspension. INLINE_THREAD_UNHIJACK is no-op.
- No m_threadAbortException; abort path не triggers.
- No m_ThreadStateFlags / DoNotTriggerGc concept (managed code не runs concurrent с GC anyway).
- Kernel-tier code не использует SSE — managed body has no XMM register live state.

## Вопросы

### 1. Какой минимум для 5.5a contract

Подтверди что для **single-thread freestanding kernel** в 5.5a минимально нужно:

- **Restore nonvols** (rbx/rbp/rsi/rdi/r12-r15) from REGDISPLAY pNonvol slots — REQUIRED (handler funclet expects these).
- **Restore XMM6-XMM15** from REGDISPLAY.Xmm — **CAN WE SKIP?** Наш kernel managed code не использует SSE, нет SAVE_XMM128 codes в `.pdata` для всего binary (verified empirically). Если ILC не emit'ит XMM saves — handler funclet тоже не reads them. Skip XMM restore exists (saves ~50 bytes shellcode + ~80 bytes XMM save area in our prolog)?
- **Setup handler call** (RCX=SP, RDX=exception) — REQUIRED.
- **Call handler** — REQUIRED.
- **ExInfo head pop** — single-step `s_head = exInfo->PrevExInfo` enough для 5.5a (типичный single-throw scenario), или нужен multi-entry walk-up loop сразу? Sage 2's earlier note: "Не делать collided unwind в 5.5a — только baseline". Но head pop semantically всегда нужен.
- **Final transfer**: `mov rsp, REGDISPLAY->SP; jmp rax` — REQUIRED.

Skip в 5.5a (per твой ранее sub-breakdown):
- Thread* fetch.
- m_threadAbortException setup.
- DoNotTriggerGc lock-and.
- INLINE_THREAD_UNHIJACK (no hijacking).
- RhpValidateExInfoPop (debug-only).
- ThreadAbort rethrow path.

Какие из этих SKIP'ов реально безопасно для single-thread? Все? Или какие-то имеют subtle dependencies?

### 2. Smoke harness design для 5.5 standalone

Sage 2's earlier breakdown described smoke 5.5:
> Не через throw. Отдельный seam:
> - заранее готовите REGDISPLAY;
> - вызываете RhpCallCatchFunclet(exception, handler, regdisplay, exInfo);
> - tiny catch-funclet пишет s_probe = 701 и возвращает continuation label.

**Concrete design question**: как в managed C# реализовать "tiny catch-funclet что возвращает continuation label"?

Возможный подход:

```csharp
[NoInlining] [UnmanagedCallersOnly]
private static byte* TestCatchFunclet(ulong establisherSP, byte* exception) {
    s_probe5_5 = 701;
    // Need to return IP for jmp rax to land on.
    delegate* unmanaged<void> contFn = &TestContinuation;
    return (byte*)contFn;
}

[UnmanagedCallersOnly]
private static void TestContinuation() {
    s_continuationReached = 1234;
    while (true) ;
}
```

Concerns:
- Нужно ли заполнять REGDISPLAY pNonvol slots реальными pointer'ами? (Перед call'ом RhpCallCatchFunclet наш test seam не имеет throw site и saved nonvols. Можно ли направить pRbx/pRbp/etc на random local vars в test seam — handler restore'ит junk, но если managed continuation method has its own prolog который spills/loads nonvols, она просто replaces junk reasonable values).
- REGDISPLAY.SP — какое значение? Если test seam set'ит = current rsp - 0x200 (some space ниже), continuation method has space для своих pushes. Or set'ит = current rsp directly?
- ExInfo для test seam — minimal ExInfo struct on stack, m_pPrevExInfo = current `s_head`, prevExInfo = old s_head; chain pop восстановит s_head.
- Test seam гарантированно не возвращает (continuation halts), так что нам не нужно cleanup'ать seam's frame.

Worth подтвердить: ОК ли этот approach или есть subtle ABI requirement что мешает?

**Альтернатива**: skip standalone smoke, merge 5.5 + 5.6 (real throw → FindFirstPassHandler → real RhpCallCatchFunclet → real ILC-compiled catch funclet body in IngressThrow → resume в IngressThrow's epilogue → boot continues). Это тестирует ВСЁ end-to-end сразу, но если что-то не работает — большая failure-search area.

Какой approach рекомендуешь — standalone smoke (mock) или merged 5.5+5.6 (real throw)?

### 3. Resume IP — что ILC реально emit'ит

Per agent research: ILC emits catch funclet что returns "continuation IP" (label after try/catch in parent method). Funclet body: `<run catch code>; mov rax, continuation_label; ret`.

Подтверди:
- Точно ли continuation IP это **address inside parent method's body**, не calls to anything else?
- ILC обеспечивает что в этом continuation IP parent method's frame state совпадает с post-try-catch (т.е. nonvols restored to parent's expectation, RBP set correctly)?
- Catch funclet's prologue spills its own nonvols в funclet's stack, но on epilogue, funclet's nonvols restored from its own stack. Поскольку funclet was called via regular `call`, RhpCallCatchFunclet's nonvols (which were restored from REGDISPLAY before call) are saved by funclet's prologue and restored by epilogue — preserves RhpCallCatchFunclet's expected nonvol state. After funclet ret, RhpCallCatchFunclet does `mov rsp, REGDISPLAY->SP; jmp rax`. **Question**: after this jmp, parent's nonvols matches what was restored from REGDISPLAY before catch funclet call (i.e., parent's pre-throw state). ILC's continuation IP code знает это и works correctly. Так?

### 4. ExInfo head pop в 5.5a

Stock multi-entry loop, sage 2's note "Не делать collided unwind в 5.5a — только baseline rethrow semantics". Question: what's the minimum для baseline?

Option A — single step:
```
s_head = exInfo->PrevExInfo;
```

Option B — sage 2's "minimum но safe":
```
s_head = exInfo->PrevExInfo;   // just pop OUR frame's ExInfo
```

Option C — full stock loop:
```
ExInfo* p = exInfo->PrevExInfo;
while (p != null && (uintptr_t)p < resumeSP)
    p = p->PrevExInfo;
s_head = p;
```

Для single-thread, single-throw scenario наш `IngressThrow → IngressThrow_Inner → throw`, нет nested exceptions, head chain имеет ровно 1 entry на момент catch'а. Option A enough?

### 5. Stack alignment + frame size для 5.5a

Stock использует FRAME_SIZE=0xE8 (shadow + 3 locals + XMM saves). Если skip'аем XMM saves + locals (Thread/ResumeIP/IsAbort), сколько надо?

- Shadow для callee: 0x20.
- Save args (4 qwords): 0x20 (или можем хранить в registers/stack без shadow if no nested calls).
- Plus alignment: rsp must be 0 mod 16 before CALL.

После 8 push'ей (0x40) + sub rsp, X — нужно X % 16 == 8 (т.к. 0x40 % 16 == 0, +0x8 alignment).

Минимальная frame: 0x28? Or 0x38? — depends on внутренних callsites. Если RhpCallCatchFunclet просто: restore nonvols → call handler → pop head → jmp rax — нужен только shadow+args для handler call.

```
Layout:
  [rsp+0x00..0x1F]  shadow (0x20)
  [rsp+0x20..0x37]  saved RCX/RDX/R8/R9 (4 qwords = 0x20)
  alignment pad if needed
```

Total stack alloc: 0x40 минимум? Or 0x48 для alignment?

### 6. Что precisely делать с FRAME_SIZE для пропущенных features

Stock writes `pCaller` callsite save area (0x20), 3 locals (Thread/ResumeIP/IsAbort = 0x18), XMM saves (0xa0). Total 0xE8.

Если мы skip Thread+IsAbort локалы (не нужны) и XMM (sage 2 confirmed?), нужны только shadow (0x20) + saved args (0x20). Total 0x40.

Это даёт shellcode size ~80-100 bytes (vs stock ~200). Worth confirming — minimal size этого helper'а.

### 7. Ошибки которые легко допустить

Что чаще всего ломается у людей которые порт'ят this helper? Поделись 2-3 specific bug patterns на которые внимание (e.g., rsp alignment off-by-8, забытый cleanup ExInfo pointer, wrong order of register restore вs head pop, etc.).
