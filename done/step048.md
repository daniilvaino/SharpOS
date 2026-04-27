# Step 48 — Phase 1 step 5.1: RhpThrowEx ingress shellcode + ExInfo + head chain

## Контекст

Первый из 6 sub-steps шага 5 (per sage 2's breakdown в `done/phase1-trycatch-roadmap.md`). Step 5 — самый сложный единый шаг roadmap'а (3-4 нед по estimate); decomposition позволяет каждому sub-step иметь independent gate с маленькой failure-search area.

5.1 цель: shellcode RhpThrowEx правильно строит PAL_LIMITED_CONTEXT + ExInfo на stack + связывает head chain + передаёт control в managed seam с правильными arguments. Полный DispatchEx + StackFrameIterator + RhpCallCatchFunclet — последующие sub-steps 5.2-5.6.

Smoke gate (intermediate, не L-numbered — финальный gate шага 5 это L8=801): visible log message от RhpTest_ThrowIngress показывает correct ExInfo invariants.

## Решение

### ExInfo struct (0x260 bytes)

`OS/src/Boot/EH/ExInfo.cs`. Layout matches sage 2's step 5 sub-breakdown / stock NativeAOT:

```
0x000  PrevExInfo        (8)
0x008  ExContext         (8)   PAL_LIMITED_CONTEXT*
0x010  Exception         (8)   GcObject*
0x018  Kind              (1)   ExKind enum (Throw=1, HardwareFault=2, Rethrow flag bit)
0x019  PassNumber        (1)   1 = first pass, 2 = second pass
0x01C  IdxCurClause      (4)   handler-active state during walk
0x020  FrameIter         (...) embedded StackFrameIterator (size 0x230)
0x250  NotifyDebuggerSP  (8)   debugger sync point (unused в SharpOS)
```

`MaxTryRegionIdx = 0xFFFFFFFFu` — sentinel for "no handler yet".

### ExInfoHead — single-thread head chain

```csharp
internal static unsafe class ExInfoHead {
    public static IntPtr s_head;           // ExInfo* via cast
    public static ExInfo* Current => (ExInfo*)s_head;
    public static byte** GetHeadAddress() =>
        (byte**)Unsafe.AsPointer(ref s_head);
}
```

Stock NativeAOT puts head в Thread.m_pExInfoStackHead (per-thread). Single-thread kernel → static IntPtr. `Unsafe.AsPointer<T>` запрещает `T = pointer`, отсюда тип `IntPtr`. Migration to per-thread storage — Phase 3 (scheduler).

### ThrowExStub host class

`OS/src/Boot/EH/ThrowExStub.cs`. Same patcher pattern as ByRefAssignRefStub / PortIoStub / CaptureContextStub:

- `[RuntimeExport("RhpThrowEx")]` + `[UnmanagedCallersOnly]`. Single definition (removed from `ExceptionEngine.cs`).
- Body inflated с 32 zero-writes + 2 `Panic.Fail` calls — гарантирует ILC компилит ≥ 250 bytes для 186-byte shellcode + safety margin.
- Signature `(byte*)` instead of `(object)` потому что `[UnmanagedCallersOnly]` запрещает managed types.

### ThrowExPatcher — 186-byte shellcode

`OS/src/Boot/EH/ThrowExPatcher.cs`. Frame layout после `sub rsp, 0x388`:

```
[rsp + 0x000 .. 0x01F]   shadow space (0x20)
[rsp + 0x020 .. 0x11F]   PAL_LIMITED_CONTEXT (0x100)
[rsp + 0x120 .. 0x37F]   ExInfo (0x260)
[rsp + 0x380 .. 0x387]   alignment pad (8 bytes)
```

Stack alignment: at entry RSP=8 mod 16 (Win64 ABI). 8 nonvol pushes (0x40) + 0x388 sub = 0x3C8 displacement. (8 - 0x3C8) mod 16 = 0. ✓ before CALL.

Shellcode sequence:
1. **Capture throw-site context BEFORE pushes**: `lea rax, [rsp+8]` → throw-site RSP, `mov rdx, [rsp]` → return address (throw-site IP).
2. **Spill nonvolatile GPRs**: 8 pushes (r15/r14/r13/r12/rbx/rsi/rdi/rbp).
3. **Allocate frame**: `sub rsp, 0x388`.
4. **Snapshot GPRs into PAL**: 10 `mov [rsp+disp32], reg` (8 bytes each — REX + 89 + ModR/M + SIB + disp32).
5. **Build ExInfo args**: `lea rdx, [rsp+0x120]` (arg2 = &ExInfo), `lea rax, [rsp+0x020]` (= &PAL), write `m_pExContext`.
6. **Link head chain**: `mov r8, &s_head` (placeholder patched at install), `mov r9, [r8]` (current head), write `m_pPrevExInfo = r9`.
7. **Init ExInfo fields**: `m_exception = null`, `m_kind = 1` (Throw), `m_passNumber = 1`, `m_idxCurClause = 0xFFFFFFFF`.
8. **Update head**: `mov [r8], rdx` (s_head = &exInfo).
9. **Tail-call managed seam**: RCX still holds exception object, RDX = &exInfo, `call r10` (placeholder = address of `RhpTest_ThrowIngress`).
10. **`int3`** if returned (should not — managed seam halts).

Two placeholder addresses patched at install:
- `&ExInfoHead.s_head` (resolved через `Unsafe.AsPointer(ref s_head)`).
- `&RhpTest_ThrowIngress` (resolved через `delegate* unmanaged<byte*, ExInfo*, void> &RhpTest_ThrowIngress`).

XMM6-XMM15 NOT spilled в 5.1 (sage 2: optional in 5.1, mandatory in 5.5b/step 7).

### RhpTest_ThrowIngress — managed seam

`OS/src/Boot/ExceptionEngine.cs`. `[UnmanagedCallersOnly]` export; signature `(byte* exceptionPtr, ExInfo* exInfo)`. Reinterprets `byte*` → managed `object` через unsafe slot store:

```csharp
object exception = null;
*(byte**)&exception = exceptionPtr;
```

Logs ExInfo invariants + halts. Не возвращает.

### Wiring

**Phase 2** (`BootSequence.Phase2_Runtime`) — после CaptureContext:
```
[info] capture-context shellcode installed
[info] throw-ex shellcode installed
```

`Probes.EhIngressThrow` — gated probe в Phase 4 после L7. Default `false` (предотвращает halt в normal boot). Set `true` для одноразовой verification что 5.1 работает.

`EhProbe.IngressThrow()`: `[NoInlining]` метод что бросает `new InvalidOperationException("ingress-5.1")`. Через ILC `throw` → `call RhpThrowEx` → shellcode → `RhpTest_ThrowIngress` halt.

## Результат

С `EhIngressThrow = true`:

```
[info] eh L8.ingress: triggering throw -> RhpThrowEx shellcode -> RhpTest_ThrowIngress (will halt)

*** RhpTest_ThrowIngress (5.1) ***
  exception type: message: ingress-5.1
  exInfo=0x000000000FE97180 head=0x000000000FE97180
  pass=1 kind=1 idxCurClause=0xFFFFFFFF
  prevExInfo=0x0000000000000000 exContext=0x000000000FE97080
  ctx.IP=0x000000000E0EC9DB ctx.Rsp=0x000000000FE97430
*** halting (5.1 ingress probe) ***
```

Подтверждено:
- **Pointer reinterpret** RCX → managed `object` работает; virtual `Message` getter through MT успешно reads "ingress-5.1".
- **Head chain link**: `exInfo == head == 0x0FE97180` — shellcode правильно записывает `s_head = &exInfo`.
- **All ExInfo invariants ровно sage 2's expected**: `pass=1, kind=1, idxCurClause=0xFFFFFFFF`.
- **`prevExInfo=0`** — head был пустым перед throw'ом.
- **`exContext` adjacent to exInfo**: delta = 0x100 = `sizeof(PAL_LIMITED_CONTEXT)`. Frame layout правильный.
- **`ctx.IP=0x0E0EC9DB`** в kernel image range (kernel image base 0x0E0E0000, thus IP внутри method byte ~0xC9DB).
- **`ctx.Rsp=0x0FE97430`** above current sp 0x0FE97180 (delta 0x2B0 ≈ frame stash + alignment, plausible).

С `EhIngressThrow = false` (default after verification): no regression. All probes L1-L7 green, `throw-ex shellcode installed` в Phase 2, ELF apps + launcher работают.

## Файлы

### Новые

- `OS/src/Boot/EH/ExInfo.cs` — ExInfo struct (0x260) + ExInfoHead (single-thread).
- `OS/src/Boot/EH/ThrowExStub.cs` — `[RuntimeExport]` host method для RhpThrowEx, body inflated.
- `OS/src/Boot/EH/ThrowExPatcher.cs` — 186-byte shellcode emitter с placeholder patching.
- `done/step048.md` — этот файл.

### Изменённые

- `OS/src/Boot/ExceptionEngine.cs` — removed RhpThrowEx (теперь в ThrowExStub). Added `RhpTest_ThrowIngress` `[UnmanagedCallersOnly]` seam.
- `OS/src/Boot/BootSequence.cs` — `InstallThrowExShellcode()` после CaptureContext.
- `OS/src/Kernel/Diagnostics/EhProbe.cs` — `IngressThrow()` test method + gated probe.
- `OS/src/Kernel/Diagnostics/Probes.cs` — `EhIngressThrow` toggle (default false).

## Что дальше

Phase 1 progress: 4 + 1/6 of step 5 = 5/11 in roadmap-step terms.

**Step 49 = sub-step 5.2** — `RhpSfiInit` real (от halt-stub до first valid frame). Плюс `RhpTest_SfiInit` seam который вызывается из ingress (вместо int3) и логирует ControlPC/SP/FP/RegisterSet. Smoke: разумные значения после `frameIter.Init(exInfo._pExContext, false)`.

После 5.2 — 5.3 (EH enumeration), 5.4 (FindFirstPassHandler), 5.5 (RhpCallCatchFunclet), 5.6 (full L8=801).
