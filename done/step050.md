# Step 50 — Phase 1 step 5.3: EH enumeration on live frame (probes A + B) + DebugLog reentrancy fix

## Контекст

Sub-step 5.3 of step 5 (sage 2 breakdown). Combines Phase 1 step 3's CoffEhDecoder (step 46) with step 4's StackFrameIterator (step 47) — managed pieces of `RhpEHEnumInitFromStackFrameIterator` / `RhpEHEnumNext` at runtime: clause table look-up driven by current IP from a live SFI.

Two probes (per user request "обе пробы"):

- **Probe A — direct, non-halting**: `EnumLive_TryHost` captures CPU context FROM INSIDE its own try region; main probe inits SFI from that PAL, calls `EhEnumInit`, enumerates clauses. Boot continues normally.
- **Probe B — throw chain, halting**: extends `RhpTest_ThrowIngress` with walk-up — after 5.1 + 5.2 logging, calls `StackFrameIteratorOps.Next` until finds frame with `UBF_FUNC_HAS_EHINFO`, enumerates that frame's clauses.

Bonus: DebugLog reentrancy guard (cosmetic).

## Решение

### Probe A — direct EH enumeration без throw

`EhProbe.EnumLiveDirect()` (gated `Probes.EhEnumLive=true`, default on):

```csharp
PalLimitedContext ctx = default;
EnumLive_TryHost(&ctx);   // captures from inside try region

StackFrameIterator iter = default;
StackFrameIteratorOps.Init(&iter, &ctx);

if (CoffEhDecoder.EhEnumInit(iter.ControlPC, out enumState, out methodStart)) {
    while (CoffEhDecoder.EhEnumNext(ref enumState, out clause)) {
        // log per clause
        if (codeOffset in [clause.TryStart, clause.TryEnd))
            score |= 8;
    }
}
```

`EnumLive_TryHost` captures live context inside its own try region:

```csharp
[NoInlining]
private static int EnumLive_TryHost(PalLimitedContext* ctx) {
    int x = 0;
    try {
        delegate* unmanaged<byte*, void> capture = ...;
        capture((byte*)ctx);   // ctx.IP = next instruction = inside try
        x = Opaque(7);
    }
    catch (System.InvalidOperationException) { x = -1; }
    return x;
}
```

Score bits: 1 = SFI init OK; 2 = EhEnumInit OK; 4 = ≥1 clause; 8 = codeOffset within try range. Expected `15`.

Probe A не halts — после logging boot continues to Phase 5.

### Probe B — throw chain walk-up

Extends `RhpTest_ThrowIngress` (after 5.1+5.2 blocks). Walks up via `StackFrameIteratorOps.Next` looking for frame с EH info:

```csharp
int walked = 0;
while (walked < 16) {
    if (CoffEhDecoder.EhEnumInit((byte*)exInfo->FrameIter.ControlPC, ...)) {
        // enumerate + log clauses
        break;
    }
    if (!StackFrameIteratorOps.Next(&exInfo->FrameIter)) break;
    walked++;
}
```

`EhProbe.IngressThrow` теперь wrapped в outer try/catch:

```csharp
[NoInlining]
private static void IngressThrow() {
    try {
        IngressThrow_Inner();  // does the actual throw
    } catch (System.InvalidOperationException) {
        // unreachable until step 5.6
    }
}

[NoInlining]
private static void IngressThrow_Inner() {
    throw new System.InvalidOperationException("ingress-5.1");
}
```

Так в .pdata появляется EH info на `IngressThrow` (1 typed clause). При throw из `IngressThrow_Inner`, walk-up step 1 = `IngressThrow` frame which has clause.

### DebugLog reentrancy guard

Bonus fix: replaced naive `DebugLog.Begin/EndLine` с reentrancy guard.

**Cause** (user observed): Phase 5 log lines like `paddr=0x...` torn by вклеенным `[info] heap grow pages: 4`. Mechanism — `Console.WriteHex(..., 16)` allocates string через `FastAllocateString` → `KernelHeap.Alloc` → если pool full — `KernelHeap.GrowHeap()` → who has its own `Log.Begin → Console.Write → Log.EndLine`. Без guard'а вложенный line tears outer.

**Fix**: `static bool s_inLine`. Begin/Write/EndLine — no-op while set. Lose nested message, outer line stays clean. Single-thread, no atomicity needed.

## Результат

### Probe A (default boot, не halt'ит)

```
[info]   5.3-A diag: methodStart=0x000000000E0ED0C0 controlPC=0x000000000E0ED0F2 codeOffset=0x00000032 nClauses=1
[info]     clause[0] kind=0 try=[0x00000018..0x00000048) handler=0x000000000E0ED11C
[info] eh 5.3 enum-live: val=15
```

`val=15` (all 4 bits) — SFI init OK, EhEnumInit OK, 1 clause enumerated, codeOffset=0x32 falls в try=[0x18..0x48) ✓. Boot continues to ELF apps + launcher.

### Probe B (with `EhIngressThrow=true`)

```
*** RhpTest_ThrowIngress (5.1) ***
  ... (5.1 ingress block) ...

*** RhpTest_SfiInit (5.2) ***
  ... (5.2 SFI init block) ...

*** RhpTest_EhEnumChain (5.3 probe B) ***
  found EH at frame walked=1 methodStart=0x000000000E0ED150 codeOffset=0x0000000F nClauses=1
    clause[0] kind=0 try=[0x0000000A..0x00000010) handler=0x000000000E0ED166
*** halting (5.3 probe B) ***
```

Все invariants точно:
- `walked=1` — SfiNext один раз перенёс из `IngressThrow_Inner` (no EH) в `IngressThrow` (1 typed clause). Frame transition correct.
- `methodStart=0x0E0ED150` — start of `IngressThrow` method body.
- `codeOffset=0x0F` falls в `try=[0x0A..0x10)` — IP действительно после CALL к inner, внутри try region.
- `kind=0` (Typed) — соответствует `catch (InvalidOperationException)`.
- `handler=0x0E0ED166` — catch funclet entry IP.

### DebugLog fix verified

Log lines в Phase 5 чистые — никаких вклеек `[info] heap grow pages: ...` в середине other log lines. Diagnostic для heap grow lost (acceptable trade), outer log stays atomic.

## Файлы

### Изменённые

- `OS/src/Boot/ExceptionEngine.cs` — extended `RhpTest_ThrowIngress` с 5.3 probe B walk-up + enum block.
- `OS/src/Kernel/Diagnostics/EhProbe.cs` — `EnumLiveDirect` + `EnumLive_TryHost` (probe A); split `IngressThrow` into outer wrapper + `IngressThrow_Inner`.
- `OS/src/Kernel/Diagnostics/Probes.cs` — added `EhEnumLive` toggle (default true).
- `OS/src/Hal/DebugLog.cs` — reentrancy guard via `s_inLine` static.
- `done/step050.md` — этот файл.

## Что дальше

Phase 1 progress: 4 + 3/6 of step 5.

**Sub-step 5.4** — first-pass handler decision без real catch thunk. Вместо ad-hoc enumeration loop в test seam — реальный `FindFirstPassHandler` который:
1. Walks frames upward.
2. Per frame enumerates clauses.
3. Per clause: если Typed and `IsInstanceOfClass(clause.TargetTypeRaw, exception)` — match found, return `(handlerIP, idxCurClause)`.
4. Per Filter clause: skip in 5.4 (filter funclet thunk не готов до 5.5).

`RhpCallCatchFunclet` остаётся halt — мы лишь проверяем что first-pass decision корректна. Smoke: для `IngressThrow → IngressThrow_Inner → throw InvalidOperationException`, FindFirstPassHandler returns `IngressThrow`'s frame + clause[0] handler IP.

После 5.4 — 5.5 (RhpCallCatchFunclet shellcode), 5.6 (full L8=801).
