# Step 49 — Phase 1 step 5.2: real RhpSfiInit + SFI init from captured PAL

## Контекст

Sub-step 5.2 of step 5 (sage 2 breakdown). После 5.1 у нас shellcode RhpThrowEx правильно строит ExInfo + PAL. Теперь надо подтвердить что StackFrameIterator может быть инициализирован из этого PAL и его invariants после `Init` matchают what shellcode записал.

Stock NativeAOT сам делает init в DispatchEx после first-pass entry. Здесь — intermediate seam просто validates layout consistency между shellcode-built PAL и StackFrameIteratorOps.Init.

## Решение

`OS/src/Boot/ExceptionEngine.cs:RhpTest_ThrowIngress` расширен. После 5.1 ingress logging:

```csharp
if (exInfo->ExContext != null)
{
    Console.Write("\r\n*** RhpTest_SfiInit (5.2) ***\r\n");

    StackFrameIteratorOps.Init(&exInfo->FrameIter, exInfo->ExContext);

    // Log sfi.controlPC, sfi.originalPC, sfi.framePointer, sfi.SP
    // Log regSet pointers (pRbx, pRbp, pR12) — should point INTO PAL
    // Sanity check: pRbx in [palStart, palStart + sizeof(PAL))
}
```

`StackFrameIteratorOps.Init` уже existed from step 47 (sub-step 4):
- Copies `pal->IP` to `ControlPC` + `OriginalControlPC`.
- Copies `pal->Rsp` to `RegDisplay.SP`.
- Copies `pal->Rbp` to `FramePointer`.
- Sets `pNonvol[Rbx/Rbp/Rsi/Rdi/R12-R15]` to point INTO the PAL.

5.2 не добавляет нового кода в Init — только validates что existing path работает на shellcode-built PAL.

## Результат

С `EhIngressThrow = true`:

```
*** RhpTest_ThrowIngress (5.1) ***
  exception type: message: ingress-5.1
  exInfo=0x000000000FE97180 head=0x000000000FE97180
  pass=1 kind=1 idxCurClause=0xFFFFFFFF
  prevExInfo=0x0000000000000000 exContext=0x000000000FE97080
  ctx.IP=0x000000000E0EC9DB ctx.Rsp=0x000000000FE97430

*** RhpTest_SfiInit (5.2) ***
  sfi.controlPC=0x000000000E0EC9DB sfi.originalPC=0x000000000E0EC9DB
  sfi.framePointer=0x000000000FE97800 sfi.SP=0x000000000FE97430
  regSet: pRbx=0x000000000FE970B0 pRbp=0x000000000FE97090 pR12=0x000000000FE970B8
  pRbx in PAL? yes
*** halting (5.2 sfi-init probe) ***
```

Все invariants точно matching:
- **`sfi.controlPC == ctx.IP`** (0x0E0EC9DB) — Init правильно скопировал pal->IP.
- **`sfi.originalPC == sfi.controlPC`** — initial originalPC equals current.
- **`sfi.SP == ctx.Rsp`** (0x0FE97430) — Init правильно скопировал pal->Rsp.
- **`sfi.framePointer == 0x0FE97800`** — actual rbp value at throw site (captured by shellcode `mov [rsp+0x10], rbp`).
- **regSet pointers**:
  - `pRbx = PAL + 0x30 = 0x0FE970B0` ✓ (PAL base 0x0FE97080 + Rbx offset 0x30)
  - `pRbp = PAL + 0x10 = 0x0FE97090` ✓
  - `pR12 = PAL + 0x38 = 0x0FE970B8` ✓
- **`pRbx in PAL? yes`** — sanity check confirms register pointers fall within the PAL struct on stack.

С `EhIngressThrow = false` (default after verification): no regression, all probes L1-L7 green, ELF apps + launcher работают.

## Файлы

### Изменённые

- `OS/src/Boot/ExceptionEngine.cs` — `RhpTest_ThrowIngress` extended с SFI init + diag block.
- `OS/src/Kernel/Diagnostics/Probes.cs` — comment update on `EhIngressThrow` toggle.
- `done/step049.md` — этот файл.

## Что дальше

Phase 1 progress: 4 + 2/6 of step 5.

**Sub-step 5.3** — real `RhpEHEnumInitFromStackFrameIterator` + `RhpEHEnumNext`. Запустить EH enumeration на actual frame's clauses без dispatch logic (handler не вызывается). Smoke: для known method с `try/finally` (или `try/catch`/`when`) seam должен enumerate clauses и вывести kind/handler/typeRVA per clause.

Это sub-step где впервые managed dispatcher's pieces (CoffEhDecoder из step 46 уже был — теперь применяется к live frame). Funclet→ROOT walk via CoffMethodLookup.TryFindMethod уже работает (step 45). Step 5.3 склеивает их вместе через RhpEHEnumInitFromStackFrameIterator wrapper.
