# Step 53 — Phase 1 step 5.5b: RhpCallCatchFunclet on REAL REGDISPLAY (fake handler)

## Контекст

Sub-step 5.5b of step 5 (sage 2 three-tier breakdown). 5.5a verified
shellcode on synthetic seam (fake REGDISPLAY с recognizable constants);
5.5b замыкает gap до real-EH-state — REGDISPLAY содержит actual pNonvol
pointers от throw site (после `RhpThrowEx → SfiInit → FindFirstPassHandler`
walk-up). Handler IP подменён на наш `TestCatchHandlerStub` чтобы
изолировать verification register restore + non-local transfer от
real ILC funclet body (которое тестируется в 5.6).

## Решение

Расширил `RhpTest_ThrowIngress` (`OS/src/Boot/ExceptionEngine.cs`) после
5.4 `FindFirstPassHandler` MATCH branch. Под gate `Probes.EhCatchFuncletReal=true`:

```csharp
byte* fakeHandler = (byte*)TestCatchHandlerStub.GetMethodAddress();

delegate* unmanaged<byte*, byte*, RegDisplay*, ExInfo*, void> catchFn =
    (delegate* unmanaged<...>)CallCatchFuncletStub.GetMethodAddress();

// RegDisplay is first field of StackFrameIterator, so &FrameIter == &FrameIter.RegDisplay.
catchFn(exceptionPtr, fakeHandler,
        (RegDisplay*)&exInfo->FrameIter, exInfo);
```

Reuses entire 5.5a harness (`TestCatchHandlerStub`, `TestContinuationStub`,
`Probe5_5_PrintResults`) — they don't care fake vs real inputs.

`Probes.EhCatchFuncletReal` toggle добавлен. Срабатывает только когда
`EhIngressThrow=true` тоже (требует throw для дойти до RhpTest_ThrowIngress).

## Результат

С обоими toggles `true`:

```
*** RhpTest_ThrowIngress (5.1) ***
*** RhpTest_SfiInit (5.2) ***
*** FindFirstPassHandler (5.4) ***
  exType=0x000000000E102A78
  MATCH: framesWalked=1 handler=0x000000000E0E9346 idxCurClause=0
  methodStart=0x000000000E0E9330 codeOffset=0x0000000F

*** RhpCallCatchFunclet 5.5b (real REGDISPLAY + fake handler) ***
  passing exception=0x00000000001066F8 fakeHandler=0x000000000E0F4DA0
  rd.SP=0x000000000FE97430 rd.ControlPC=0x000000000E0E933F

*** 5.5a results ***
  handler_called  = 0x000000000000AAAA
  observed_rcx    = 0x000000000FE97430   ← REAL establisher SP
  observed_rdx    = 0x00000000001066F8   ← REAL Exception object на KernelHeap
  cont_called     = 0x000000000000BBBB
  observed_rsp    = 0x000000000FE97430   ← = REGDISPLAY.SP
  s_head_now      = 0x0000000000000000
*** halting (5.5a probe complete) ***
```

**Differences vs 5.5a confirm real data flow**:

| Variable | 5.5a (fake) | 5.5b (real) | Verified |
|---|---|---|---|
| `observed_rcx` | `0x0FE962A0` (synthetic fakeSP) | `0x0FE97430` (real est. SP) | RCX = REGDISPLAY.SP correct on real walk |
| `observed_rdx` | `0x0FE97038` (stack ptr) | `0x001066F8` (managed Exception) | exception passed как arg2 |
| `observed_rsp` | `0x0FE962A0` | `0x0FE97430` | mov rsp ; jmp/call rax работает |

**Critical end-to-end verification**:

- REGDISPLAY's pNonvol pointers были populated by `FindFirstPassHandler`'s
  `SfiNext` walk-up: некоторые указывают в PAL_LIMITED_CONTEXT (initial
  capture from RhpThrowEx), некоторые в stack saved-from-prologue
  locations (где IngressThrow's prolog push'ил nonvols).
- 8 nonvol restore loop в `RhpCallCatchFunclet` indirect-loaded через
  these REAL pointers без crash → все 8 pNonvol pointers valid.
- Funclet ABI args setup от real REGDISPLAY data (RCX = real SP).
- ExInfo head pop работает на real chain (s_head correctly null'ed).
- Non-local transfer landed continuation на real establisher SP.

5.5a + 5.5b together = comprehensive verification of `RhpCallCatchFunclet`
без зависимости от real ILC catch funclet body. 5.6 теперь bridges это
с actual ILC-emitted funclet → resume в parent's real continuation IP.

С обоими toggles `false` (default after verification): no regression,
all probes L1-L7 + 5.3-A green, ELF apps + launcher работают.

## Файлы

### Изменённые

- `OS/src/Boot/ExceptionEngine.cs` — extended `RhpTest_ThrowIngress` с 5.5b
  block после 5.4 MATCH branch.
- `OS/src/Kernel/Diagnostics/Probes.cs` — `EhCatchFuncletReal` toggle.
- `done/step053.md` — этот файл.

## Что дальше

Phase 1 progress: 4 + 5.5/6 of step 5.

**Sub-step 5.6** — final L8 = 801. Real throw → real DispatchEx → real
ILC catch funclet body → resume в real parent's continuation IP.

Changes для 5.6:
- Replace test seam с production `DispatchEx` (managed orchestrator
  что calls `FindFirstPassHandler` + `RhpCallCatchFunclet`).
- Remove fake handler swap — pass `fp.HandlerAddress` (real ILC funclet IP).
- Catch handler в `IngressThrow` test method writes `s_l8_value = 801`
  (or similar verifiable marker via captured exception's `ex.Message.Length`).
- L8 probe verifies `IngressThrow()` returns после catch's value vs
  expected 801.

После 5.6 — full step 5 closure (L8=801 hard gate). Затем step 6
(rethrow), step 7 (finally + second pass), step 8 (filter), step 9
(fault), step 10 (HW-bridge), step 11 (rich stack trace + collided)
→ Phase 1 closure.
