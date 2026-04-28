# Step 52 — Phase 1 step 5.5a: RhpCallCatchFunclet shellcode (standalone smoke)

## Контекст

Sub-step 5.5a of step 5 (sage 2 breakdown). Самый рискованный shellcode шага 5 — non-local transfer + register restore + ExInfo head pop. ~140 bytes asm. Перед coding'ом проведён детальный sage round-trip с двумя мудрецами (см. `done/sage-question-step5_5.md`); MOOS clone подтвердил, что наш approach (port stock NativeAOT funclet-dispatching) — uncharted territory среди managed-OS проектов.

5.5a verified standalone: fake REGDISPLAY + fake (shellcode) handler + fake (shellcode) continuation. 5.5b следующий wire'ит real REGDISPLAY от throw site, 5.6 — full ILC catch funclet end-to-end.

## Решение

### Decisions из sage round (sage 1 + sage 2 consensus)

| Что | Решение |
|---|---|
| **XMM6-XMM15 restore** | Skip в 5.5a (no SAVE_XMM128 codes empirically). Restore вернётся в 5.5b/step 7 для stock-compat. |
| **Thread/Abort/Hijack/DoNotTriggerGc/Validate** | Skip — все safe для single-thread freestanding. |
| **ExInfo head pop** | Single-step `s_head = exInfo->PrevExInfo`. Multi-entry walk — для collided unwind в 5.7+. |
| **Order** | restore nonvols → call handler → pop head → mov rsp + jmp rax. Don't touch nonvols между restore и call. |
| **Funclet ABI args** | RCX = REGDISPLAY.SP (establisher), RDX = exception. Reverse от C convention. |
| **Frame alignment** | После 8 pushes (0x40 mod 16 == 0), `sub rsp, 0x48` для alignment. |
| **Smoke approach** | Standalone shellcode harness (sage 2 explicit). Не merge с 5.6. |
| **Continuation invocation** | `call rax`, не `jmp rax` — managed PrintResults requires Win64 ABI rsp%16==8 entry. |

### `RhpCallCatchFunclet` shellcode (140 bytes)

```
Prologue (19 bytes):
  41 57 / 41 56 / 41 55 / 41 54   push r15/r14/r13/r12  (each 2 bytes)
  53 / 56 / 57 / 55                push rbx/rsi/rdi/rbp  (each 1 byte)
  48 81 EC 48 00 00 00             sub rsp, 0x48

Save 4 args (20 bytes):
  48 89 4C 24 20    mov [rsp+0x20], rcx    ; exception
  48 89 54 24 28    mov [rsp+0x28], rdx    ; handler IP
  4C 89 44 24 30    mov [rsp+0x30], r8     ; REGDISPLAY*
  4C 89 4C 24 38    mov [rsp+0x38], r9     ; ExInfo*

Restore 8 nonvols from REGDISPLAY (56 bytes):
  Pattern: mov rax, [r8+pX]; mov reg, [rax]   (each 7 bytes × 8)
  pRbx=0x18, pRbp=0x20, pRsi=0x28, pRdi=0x30
  pR12=0x58, pR13=0x60, pR14=0x68, pR15=0x70

Handler call (13 bytes):
  49 8B 48 78         mov rcx, [r8+0x78]    ; arg1 = REGDISPLAY.SP
  48 8B 54 24 20      mov rdx, [rsp+0x20]   ; arg2 = exception
  FF 54 24 28         call qword ptr [rsp+0x28]

ExInfo head pop (21 bytes):
  4C 8B 44 24 38      mov r8, [rsp+0x38]    ; reload ExInfo*
  4D 8B 08            mov r9, [r8]          ; r9 = exInfo->PrevExInfo
  49 BA <imm64>       mov r10, &s_head      ; placeholder patched
  4D 89 0A            mov [r10], r9         ; *s_head = prev

Non-local transfer (11 bytes):
  4C 8B 44 24 30      mov r8, [rsp+0x30]    ; reload REGDISPLAY*
  49 8B 60 78         mov rsp, [r8+0x78]    ; rsp = REGDISPLAY.SP (DESTROYS frame)
  FF E0               jmp rax               ; resume in parent
```

Stack layout после prolog:
```
[rsp+0x00..0x1F]   shadow space (0x20)
[rsp+0x20..0x27]   saved exception (RCX entry)
[rsp+0x28..0x2F]   saved handler IP (RDX entry)
[rsp+0x30..0x37]   saved REGDISPLAY* (R8 entry)
[rsp+0x38..0x3F]   saved ExInfo* (R9 entry)
[rsp+0x40..0x47]   alignment pad
```

Entry rsp%16==8 → +0x40 (8 pushes) → still 8 → +0x48 (sub rsp) → 0. ✓ aligned для CALL.

### Test harness shellcodes

**TestCatchHandler** (54 bytes) — fake catch funclet:
```
mov r10, &s_handler_called; mov qword ptr [r10], 0xAAAA
mov r10, &s_observed_rcx;   mov [r10], rcx
mov r10, &s_observed_rdx;   mov [r10], rdx
mov rax, &TestContinuation_entry
ret
```

**TestContinuation** (42 bytes) — fake resume IP target:
```
mov r10, &s_continuation_called; mov qword ptr [r10], 0xBBBB
mov r10, &s_observed_rsp;        mov [r10], rsp
mov rax, &Probe5_5_PrintResults
call rax    ; <-- NOT jmp! (alignment fix)
```

**Critical fix**: первая итерация использовала `jmp rax` для continuation tail-jump в managed PrintResults. Crashed в #GP внутри `Console.WriteHexRaw` через misaligned stack. Win64 ABI: managed function entry ожидает rsp%16==8 (CALL push'ит 8-byte ret addr). Через `jmp rax` continuation entered с rsp%16==0 (REGDISPLAY.SP 16-byte aligned). Method's `sub rsp, X` rolled forward misalignment → first nested CALL hits `movaps` на mis-aligned stack. **Fix**: `call rax` instead of `jmp rax` (single-byte difference: `FF E0` → `FF D0`). PrintResults halts in body, never reaches ret, faked return addr never dereferenced.

### Probe seam

`EhProbe.Probe5_5()`:
1. Allocates 8 saved nonvol values на stack с recognizable constants (RBX=0x111..., RBP=0x222..., R12=0xCCC..., etc).
2. Sets `RegDisplay rd = default; rd.pRbx = &savedRbx; ...` для 8 pNonvol slots.
3. Computes `fakeSP = (currentRsp - 0x1000) & ~0xF` — 4KB below current, 16-byte aligned.
4. Allocates fake ExInfo, links `PrevExInfo = current s_head`, sets `s_head = &fakeEx`.
5. Calls patched `RhpCallCatchFunclet(fakeException, handlerIp, &rd, &fakeEx)`.
6. Never returns — RhpCallCatchFunclet does non-local transfer, continuation reaches PrintResults which halts.

`EhProbe.Probe5_5_PrintResults()` — managed function, prints all observable statics + halts.

`Probes.EhCatchFuncletProbe` toggle (default false — halts when on).

## Результат

С `EhCatchFuncletProbe=true`:

```
[info] call-catch-funclet shellcode: bytes=140 head=0x...
[info] 5.5a test harness: handler=0x...E0F4E70 continuation=0x...
... (other infrastructure init) ...
[info] eh 5.5a: probing RhpCallCatchFunclet standalone (will halt in PrintResults)
[info] 5.5a probe entering: handler=0x...E0F4E70 rd=0x000000000FE972A8 exInfo=0x000000000FE97040 fakeSP=0x000000000FE962A0

*** 5.5a results ***
  handler_called  = 0x000000000000AAAA  (expected 0xAAAA)
  observed_rcx    = 0x000000000FE962A0  (expected = REGDISPLAY.SP from probe seam)
  observed_rdx    = 0x000000000FE97038  (expected = pointer to fakeException 0xEE...EE local)
  cont_called     = 0x000000000000BBBB  (expected 0xBBBB)
  observed_rsp    = 0x000000000FE962A0  (expected = REGDISPLAY.SP = observed_rcx)
  s_head_now      = 0x0000000000000000  (expected = original head before probe linked fakeEx)
*** halting (5.5a probe complete) ***
```

Все 6 invariants точно matched:
- **handler_called = 0xAAAA** — shellcode handler executed (через restored nonvols + Funclet ABI args).
- **observed_rcx = fakeSP** — funclet ABI: arg1 = REGDISPLAY.SP (NOT exception, как в C convention).
- **observed_rdx = stack ptr to fakeException** — arg2 = exception correctly.
- **cont_called = 0xBBBB** — continuation shellcode entered (RhpCallCatchFunclet's `mov rsp; jmp rax` worked).
- **observed_rsp = fakeSP = observed_rcx** — RSP correctly set to REGDISPLAY.SP at jmp time.
- **s_head_now = 0x0** — head pop correctly restored к original value.

End-to-end shellcode chain verified standalone:
1. RhpCallCatchFunclet shellcode prolog (8 pushes + sub rsp, 0x48).
2. Save 4 args.
3. Restore 8 nonvols indirectly through pNonvol pointers in REGDISPLAY.
4. Funclet ABI args set up correctly.
5. CALL handler.
6. Handler executes, captures observable values, returns continuation IP.
7. Reload R8 from saved slot (after volatile clobber by call).
8. Pop ExInfo head.
9. Reload R8 again (redundant — could optimize).
10. `mov rsp, REGDISPLAY.SP` — non-local stack transfer.
11. `jmp rax` — non-local control transfer.
12. Continuation entered with correct RSP.
13. Continuation captures observed_rsp, jumps к managed PrintResults.
14. PrintResults runs on fake stack (4KB region), prints results, halts.

С `EhCatchFuncletProbe=false` (default after verification): no regression — все probes L1-L7, 5.3-A green, ELF apps + launcher работают.

## Файлы

### Новые

- `OS/src/Boot/EH/CallCatchFuncletStub.cs` — `[RuntimeExport] RhpCallCatchFunclet`, body padded для 140-byte shellcode.
- `OS/src/Boot/EH/CallCatchFuncletPatcher.cs` — 140-byte shellcode emitter с byte-by-byte annotation.
- `OS/src/Boot/EH/Stub5_5_TestHarness.cs` — TestCatchHandlerStub + TestContinuationStub host classes + Stub5_5_Patcher (handler 54 bytes + continuation 42 bytes).
- `done/sage-question-step5_5.md` — sage round-trip transcript (preserved для архива).
- `done/step052.md` — этот файл.

### Изменённые

- `OS/src/Boot/BootSequence.cs` — `InstallCallCatchFuncletShellcode()` + `EhProbe.InstallStep5_5TestHarness()` в Phase 2.
- `OS/src/Kernel/Diagnostics/EhProbe.cs` — добавлены статики `s_5_5_*`, `InstallStep5_5TestHarness()`, `Probe5_5()`, `Probe5_5_PrintResults()`.
- `OS/src/Kernel/Diagnostics/Probes.cs` — `EhCatchFuncletProbe` toggle.

## Что дальше

Phase 1 progress: 4 + 5/6 of step 5 (sage 2's three-tier 5.5 split: 5.5a done, 5.5b и 5.6 ahead).

**Sub-step 5.5b** — bridge real REGDISPLAY (от live throw ingress) с still-fake handler. Цель: verify что наш handler restore логика работает на real frame (не только на synthetic seam'е). Ingress'ит throw из IngressThrow → SfiInit → FindFirstPassHandler returns real handler IP — но **подменим** handler IP на fake `TestCatchHandlerStub` чтобы сначала verify register restore + transfer корректны. Затем 5.6 unleashes real ILC catch funclet.

**Sub-step 5.6** — final L8 = 801. Real throw → real DispatchEx → real ILC catch funclet → resume в real parent's continuation.

После 5.6 — step 6 (rethrow), step 7 (finally + second pass), step 8 (filter), step 9 (fault), step 10 (HW-bridge), step 11 (rich stack trace + collided unwind) → Phase 1 closure.
