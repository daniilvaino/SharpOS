# Step 47 — Phase 1 step 4: StackFrameIterator + 4-opcode unwind decoder

## Контекст

Четвёртый step из 11-step Phase 1 try/catch roadmap. Foundation для step 5 (first green typed catch) — тот же StackFrameIterator + RegDisplay будут использованы в `RhThrowEx → DispatchEx → FindFirstPassHandler → RhpCallCatchFunclet` цепочке. Smoke gate L7 = 3.

Шаг самый объёмный из infrastructure-only steps (steps 1-4): три explicit-layout структуры + capture context shellcode + 4-opcode unwind decoder + cross-function probe.

## Решение

### Layouts (sage 2 step 5 sub-breakdown, non-UNIX AMD64)

**`PalLimitedContext`** (`OS/src/Boot/EH/PalLimitedContext.cs`) — 0x100 bytes. Snapshot of CPU state. Step 4 only writes/reads GPR section (0x00..0x57). XMM6-XMM15 (0x60..0xFF) zero'd by capture stub, populated by step 5+ RhpThrowEx shellcode.

```
0x00 IP, 0x08 Rsp, 0x10 Rbp, 0x18 Rdi, 0x20 Rsi
0x28 Rax, 0x30 Rbx, 0x38 R12, 0x40 R13, 0x48 R14, 0x50 R15
0x60..0xF0 Xmm6..Xmm15 (10 × 16 bytes, unused в step 4)
```

**`RegDisplay`** (`OS/src/Boot/EH/RegDisplay.cs`) — 0x130 bytes. Pointer-to-saved-location table.

```
0x18 pRbx, 0x20 pRbp, 0x28 pRsi, 0x30 pRdi
0x58 pR12, 0x60 pR13, 0x68 pR14, 0x70 pR15
0x78 SP, 0x80 ControlPC
0x90..0x12F Xmm[10] (unused в step 4)
```

Pointer-to-value model: каждый pNonvol указывает либо в PAL_LIMITED_CONTEXT (initial — current register values), либо в stack slot (после unwind через PUSH_NONVOL).

**`StackFrameIterator`** (`OS/src/Boot/EH/StackFrameIterator.cs`) — 0x230 bytes.

```
0x000 RegDisplay (embedded, 0x130)
0x130 ControlPC
0x138 FramePointer
0x140 OriginalControlPC
0x148 Flags (bit 0 = exhausted)
```

### CaptureContext shellcode

`OS/src/Boot/EH/CaptureContextStub.cs` + `CaptureContextPatcher.cs` — 53-byte shellcode patch'ящий `[RuntimeExport] RhpCaptureContext` body (та же pattern что `ByRefAssignRefStub` / `PortIoStub`).

Entry: `RCX = PAL_LIMITED_CONTEXT*` (Microsoft x64 ABI). Snapshots:

```
mov rax, [rsp]              ; return address from CALL
mov [rcx + IP], rax
lea rax, [rsp + 8]          ; caller's RSP (after CALL push)
mov [rcx + Rsp], rax
mov [rcx + Rbp], rbp
mov [rcx + Rdi], rdi
mov [rcx + Rsi], rsi
mov [rcx + Rax], rax        ; placeholder
mov [rcx + Rbx], rbx
mov [rcx + R12..R15], r12..r15
ret
```

Managed body имеет 11 explicit zero writes + 2 `Panic.Fail` calls для гарантии что body bigger than 53 bytes (patcher fits comfortably).

Phase 2 install после PortIo:
```
[info] capture-context shellcode installed
```

### 4-opcode unwind decoder

`StackFrameIteratorOps.Next(StackFrameIterator*)`:

1. Resolve `iter.ControlPC` через `CoffMethodLookup.TryFindMethod` (включая funclet→ROOT walk).
2. Read UNWIND_INFO header bytes 0..3, codes array starting at byte 4.
3. Iterate forward through unwind codes (codes are stored "epilog-first" = reverse of prolog). Each code reverses one prolog operation:

| Opcode | Effect |
|---|---|
| `UWOP_PUSH_NONVOL` (0) | `pReg = (ulong*)SP; SP += 8` |
| `UWOP_ALLOC_LARGE` (1) | `SP += imm16*8` (opInfo=0) или `SP += imm32` (opInfo=1) |
| `UWOP_ALLOC_SMALL` (2) | `SP += (opInfo+1)*8` |
| `UWOP_SET_FPREG` (3) | `SP = *pRbp - frameOffset*16` (frameOffset из header byte 3) |

Все остальные opcodes (`SAVE_NONVOL`, `SAVE_XMM128`, `PUSH_MACHFRAME` + their FAR variants) — log + mark exhausted. Empirically отсутствуют в нашем binary.

4. После всех codes: `SP` указывает на saved return address. Read `nextIP = *(ulong*)SP`, `SP += 8`.
5. Update `iter.ControlPC = nextIP`, `iter.FramePointer = *iter.RegDisplay.pRbp`.

Volatile registers (rax/rcx/rdx/r8-r11) могут появиться в PUSH_NONVOL — мы advance SP но не отслеживаем их pointer (не нужны для unwind, вернутся естественно при возврате).

### L7 probe — cross-function chain

`EhProbe.FrameWalk` запускает `FrameWalk_A → FrameWalk_B → FrameWalk_C → FrameWalk_Walk` (все `[NoInlining]`). Внутри Walk:

1. Capture live context: `delegate* unmanaged<byte*, void> capture = (delegate* unmanaged<byte*, void>)CaptureContextStub.GetMethodAddress(); capture((byte*)&ctx);`
2. `StackFrameIteratorOps.Init(&iter, &ctx)` — pointers в PAL.
3. Resolve target `BeginAddress` of method A via `CoffMethodLookup.TryFindMethod(&FrameWalk_A)`.
4. Walk loop: at each frame check `currentRoot.BeginAddress == targetBeginAddress`. If yes — return count. Else `Next(iter)`, count++.

Expected count = 3 (Walk → C → B → A).

Negative returns:
- `-1` — capture failed / module not initialised
- `-2` — `SfiNext` returned false (unsupported opcode or TryFindMethod failed)
- `-3` — walked > 100 frames без reaching A (loop guard)

`Probes.EhFrameWalk = true` toggle.

## Результат

```
[info] capture-context shellcode installed
... (other init)
[info] eh L6 ehInfo varint decode: val=111
[info] eh L7 frame walk: val=3                 <-- step 4 GATE GREEN
[info] elf validation start
```

L1, L2, L4, L5, L6 + GC stress + NativeAotProbe + CctorProbe + ELF apps все зелёные, no regression.

## Файлы

### Новые

- `OS/src/Boot/EH/PalLimitedContext.cs` — 0x100-byte struct.
- `OS/src/Boot/EH/RegDisplay.cs` — 0x130-byte struct.
- `OS/src/Boot/EH/StackFrameIterator.cs` — 0x230-byte struct + `StackFrameIteratorOps` (Init + Next + 4-opcode decoder).
- `OS/src/Boot/EH/CaptureContextStub.cs` — `[RuntimeExport] RhpCaptureContext` host method.
- `OS/src/Boot/EH/CaptureContextPatcher.cs` — 53-byte shellcode emitter.
- `done/step047.md` — этот файл.

### Изменённые

- `OS/src/Boot/BootSequence.cs` — `InstallCaptureContextShellcode()` after PortIo.
- `OS/src/Kernel/Diagnostics/EhProbe.cs` — `FrameWalk` + `FrameWalk_A/B/C/Walk` chain.
- `OS/src/Kernel/Diagnostics/Probes.cs` — `EhFrameWalk` toggle.

## Что дальше

Phase 1 progress: **4/11**. Все infrastructure steps closed:

```
After step  1: L4_ExceptionShape  == 127     ✅ step 44
After step  2: L5_RootWalk        == 7       ✅ step 45
After step  3: L6_EhDecode        == 111     ✅ step 46
After step  4: L7_FrameWalk       == 3       ✅ step 47
After step  5: L8_TypedCatch      == 801     <-- FIRST GREEN
```

Step 48 = step 5 — самый сложный единый шаг (3-4 недели по estimate). Разбит мудрецом 2 на 6 sub-steps (5.1-5.6) с детальным breakdown в `done/phase1-trycatch-roadmap.md`:

- 5.1 Ingress-only throw thunk (`RhpThrowEx` shellcode + `RhpTest_ThrowIngress` halt-stub).
- 5.2 `RhpSfiInit` real (от halt-stub до first valid frame).
- 5.3 EH enumeration без dispatch (`RhpEHEnumInitFromStackFrameIterator` + `RhpEHEnumNext` real).
- 5.4 First-pass handler decision без real catch thunk.
- 5.5 Standalone `RhpCallCatchFunclet` (5.5a minimal + 5.5b stock-closer).
- 5.6 Full typed-catch path (`L8_TypedCatch == 801`).

Каждый sub-step имеет independent gate.
