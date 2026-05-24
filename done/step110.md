# Step 110 — Precise GcInfo (WORK IN PROGRESS)

Цель: заменить conservative stack scan на precise GC walk, читая
per-method GcInfo blob который NativeAOT уже эмиттит в `.xdata`. После
этого `KernelGC.Collect()` безопасно зовётся из любого места (закрывается
§10 kernel-limits doc, M5 в memory-ownership), разблокируется Phase F
(финализаторы, hosted-CoreCLR GC suspend, RetainVM/decommit).

Это **открытый step** — пишется по мере прогресса. Коммит — после полного
закрытия (frame walker + замена conservative scan + повторная санитизация
PhaseReport).

## Прогресс

### ✅ Part 1 — Adapter (PC → gcInfo blob)

`OS/src/Boot/EH/CoffMethodGcInfo.cs` — 95 LOC.

Опирается на существующий `CoffMethodLookup` (binary-search в `.pdata` →
RUNTIME_FUNCTION) и продолжает walk на 1 байт `unwindBlockFlags` + 4 байта
если `UBF_FUNC_HAS_ASSOCIATED_DATA` + 4 байта если `UBF_FUNC_HAS_EHINFO`,
после чего получает указатель на gcInfo blob.

Возвращает `(MethodStart, MethodEnd, GcInfo*, CodeOffset)`. Проверено
sanity probe'ой `CoffGcInfoDumpProbe.Run()` — корректно резолвит
адреса трёх простых методов (`Marker1/2/3`), выдаёт gcInfo указатели
рядом с `.xdata` секцией.

### ✅ Part 2 — BCL infra (BinaryPrimitives + MemoryMarshal)

Перед декодером собрали BCL-compat infra (юзер: "хотелось бы целиком"):

- `std/no-runtime/shared/Bcl/BitConverter.cs` — `IsLittleEndian = true`
- `std/no-runtime/shared/Bcl/BinaryPrimitives.ReverseEndianness.cs` —
  scalar overloads (sbyte/byte/short/ushort/char/int/uint/long/ulong/
  nint/nuint). Без Vector/Int128/bulk-span — кernel-tier без SIMD; добавим
  когда первый consumer попросит.
- `BinaryPrimitives.ReadLittleEndian.cs` / `ReadBigEndian.cs` — Int16/
  Int32/Int64/IntPtr/UInt16/UInt32/UInt64/UIntPtr + `TryRead*`. Без
  Half/Single/Double (нужен Half-тип + `Int*BitsToFloat*` cast helpers).
- `BinaryPrimitives.WriteLittleEndian.cs` / `WriteBigEndian.cs` — Write +
  `TryWrite*` для тех же типов.
- `std/no-runtime/shared/Runtime/MemoryMarshal.cs` — добавлен `Read<T>`,
  `TryRead<T>`, `Write<T>`, `TryWrite<T>`, `Cast<TFrom,TTo>` (Span и
  ReadOnlySpan). Match BCL surface.

Total ~700 LOC; cuts документированы в headers. BCL-portable код
использующий BinaryPrimitives для integers собирается у нас без правок.

### ✅ Part 3 — BitReader + GcInfo header decoder

- `OS/src/Boot/EH/BitReader.cs` — bit-level cursor над `byte*`. Методы:
  `ReadBits(n)`, `ReadBit()`, `DecodeVarLengthUnsigned(base)`,
  `DecodeVarLengthSigned(base)`, `Align(bits)`, `BitOffset`. Алгоритм
  cross-checked против `ILCompiler.Reflection.ReadyToRun NativeReader.cs`
  (no code copied).
- `OS/src/Boot/EH/CoffGcInfoTypes.cs` — AMD64 ENCBASE constants
  (CodeLength=8, NumSafePoints=2, и т.д.) + GcInfoHeaderFlags enum + 
  GcInfoFlagsBitSize константы. Cross-checked против `GCInfoTypes.cs` в форке.
- `OS/src/Boot/EH/CoffGcInfoDecoder.cs` —
  - `DecodeHeader(gcInfo, version, out CoffGcInfoHeader)`: читает 
    slim/fat-флаг, header flags (10 бит для fat v2+), ReturnKind 
    (2/4 бита), CodeLength (varint), conditional fields (GS cookie /
    security obj / generics ctx / SBR / EnC / RPI), SizeOfStackOutgoing,
    NumSafePoints, NumInterruptibleRanges.
  - `ReadyToRunVersionToGcInfoVersion(major, minor)` — маппинг R2R-header
    Major/Minor → GcInfo version (1/2/3/4). Наш kernel = .NET 7 ILC =
    R2R 8.0 = GcInfo v2.
- `OS/src/Kernel/Memory/NativeAotModuleInit.cs` — extended: expose
  `ReadyToRunMajor / ReadyToRunMinor / ReadyToRunHeader` (нужно decoder'у
  для version-aware paths).

**Верификация**: dump-probe декодит header трёх Markers:
```
[gcinfo] rtr=8.0 gcInfoVersion=2
[gcinfo] Marker1 codeLen=34 range=34 OK safePts=0 ranges=1
[gcinfo] Marker2 codeLen=44 range=44 OK safePts=0 ranges=1
[gcinfo] Marker3 codeLen=94 range=94 OK safePts=0 ranges=1
```
`CodeLength == methodEnd - methodStart` для всех трёх — bit reader,
ENCBASE constants, и header layout работают byte-precise.

### ✅ Part 4 — Slot table decoder (counts + skip per-slot)

- `CoffGcSlotTable` struct: `NumRegisters / NumStackSlots / NumUntracked
  / NumSlots / NumTracked / BitOffsetAfterTable`.
- `CoffGcInfoDecoder.DecodeSlotTable(gcInfo, bitOffset, out slots)`:
  читает 1 бит "has registers" → conditional NumRegisters varint, 1 бит
  "has stack slots" → conditional NumStackSlots + NumUntracked varints.
  Дальше skip per-slot detail для корректного advance bit cursor'а
  через register-list (с delta-chain) и stack-slot-list (с spBase + flags).
- `SkipSafePointOffsets` / `SkipInterruptibleRanges` — служебные helpers
  для skip раздела между header и slot table.
- Per-slot detail НЕ stored — только counts. Полный decode (адреса
  слотов в REGDISPLAY-relative форме) — Part 6.

**Верификация**: dump-probe декодит slot counts на трёх Markers:
```
Marker1: numSlots=1 (reg=1 stack=0 untracked=0) tracked=1
Marker2: numSlots=1 (reg=1 stack=0 untracked=0) tracked=1
Marker3: numSlots=6 (reg=5 stack=0 untracked=1) tracked=5
```
- Marker1/2: 1 register-tracked slot — JIT держит GcStaticBlock pointer
  для `s_sinkA/B` коротко в регистре во время записи int'а. Norма.
- Marker3: 5 registers через delta-chain + 1 untracked. Register
  delta-encoding exercised корректно.

### ✅ Part 5 — Transitions decoder

`EnumerateLiveSlotsAtPc(byte* gcInfo, int gcInfoVersion, uint pcCodeOffset,
Span<bool> liveOut)` в `CoffGcInfoDecoder`.

Алгоритм:
1. Decode header + interruptible ranges + slot table inline
2. Normalize PC в interruptible-coordinate (sum preceding ranges + offset
   within current range)
3. targetChunk = normalizedPc / 64, pcInChunk = normalizedPc % 64
4. POINTER_SIZE varint → numBitsPerPointer
5. chunkPointers[numChunks] raw bits, byte-align → info2Offset
6. Walk chunks 0..targetChunk, для каждого non-empty:
   - Read couldBeLive bitmap (simple) или RLE encoding
   - Read finalState bitmap
   - Per couldBeLive slot: walk transition list, count flips
   - Apply: prev chunks → liveOut[slot] = finalState; target chunk → 
     toggle если flipsBeforePc parity=odd

Поддержаны оба encoding'а — simple-bitmap и RLE (skip/run runs).
Stackalloc'нутые буферы: 16 ranges + 128 chunk pointers (cap для
realistic kernel methods).

**Верификация** на трёх Markers:
```
Marker1/2:  live@start  not-in-interruptible-range  (prologue)
            live@mid    count=0                      (int loop body)
            live@end-2  not-in-interruptible-range  (epilogue)
Marker3:    live@start  not-in-interruptible-range
            live@mid    count=3 slots=[0 1 2]       ← string s + 
                                                      static block + 
                                                      intermediate
            live@end-2  not-in-interruptible-range
```
Поведение качественно правильное:
- Prologue/epilogue non-interruptible (JIT не emit'ит GcInfo для них) ✓
- Mid-method разные методы дают разный count (Markers 1/2 без managed
  refs в данный момент, Marker3 с одновременно 3 ref'ами) ✓
- Decoder не crash'ит, structure walks correctly через все chunks ✓

### ✅ Part 6 — Slot detail decoder

- `CoffGcSlot` struct: `Kind` (0=register, 1=stack), `SpBase` (для stack:
  0=callerSP, 1=currentSP, 2=FP), `Flags` (UNTRACKED|BASE|INTERIOR bits),
  `RegOrOffset` (reg index 0..15 или signed byte offset).
- `CoffGcInfoTypes.DenormalizeStackSlot(x) = x << 3` (AMD64 slot size 8).
- `CoffGcInfoTypes.DenormalizeStackBaseRegister(x) = x ^ 5` (encoded 0→
  RBP, encoded 1→RSP).
- `DecodeFullSlotTable(byte*, bitOffset, Span<CoffGcSlot>, out counts)` —
  заполняет caller-buffer (вместо просто счётчиков). Re-implements
  Register/StackSlot decode with full detail преservation.

Address resolver — `OS/src/Boot/EH/CoffGcInfoResolver.cs`:
- `ReadGpReg(ctx, idx)` — 16-way switch для AMD64 regs (0..15 → rax..r15)
- `ResolveSlotAddress(in slot, ctx, in hdr)` — для stack slots, returns
  byte address based on SpBase (CallerSp = `ctx->Rsp + SizeOfStackOutgoingAndScratchArea`,
  CurrentSp = `ctx->Rsp`, FpBase = `ctx->Reg(hdr.StackBaseRegister)`)
+ signed offset
- `ResolveSlotValue(in slot, ctx, in hdr)` — register → reg value;
  stack → deref via address

**Verified** synthetic-CONTEXT smoke test (Probes.GcInfoResolverSmoke):
заполняем Context каждый GP reg сентинелом `0xCC0000_0000_0000 | regIdx`,
Rsp → stackalloc'нутый 256-байт scratch с per-slot seed
`0xBB0000_0000_0000 | (i*8)`. Прогон через Marker3 mid-PC:
```
slot[0] reg=0 (rax)  LIVE@mid value=0xCC00000000000000  ✓ sentinel rax
slot[1] reg=1 (rcx)  LIVE@mid value=0xCC00000000000001  ✓ sentinel rcx
slot[2] reg=6 (rsi)  LIVE@mid value=0xCC00000000000006  ✓ sentinel rsi
slot[5] stack CurrentSp+40 UNTRACKED value=0xBB00000000000028  ✓ scratch[5]
```
Slot[3] r8 и slot[4] rsi (другой lifetime того же RSI) — НЕ в live@mid,
resolver правильно их пропустил. Tracked-live=3 + untracked=1.

Stack-slot arithmetic + deref работают корректно (адрес = `ctx->Rsp + 40`,
указывает на `scratch[5]`, dereferенс возвращает seed`0xBB...0x28`).

CallerSp и FpBase ветки в resolver'е есть, но Marker3 их не exercise'ит
(там только CurrentSp). Реальные методы будут тестировать в Part 7.

**Верификация** на трёх Markers:
```
Marker1/2: slot[0] reg=rax(0) flags=0x1                ← GcStaticBlock ptr
Marker3:   slot[0]=rax, [1]=rcx, [2]=rsi, [3]=r8, [4]=rsi (overlapping lifetimes)
           slot[5] stack currentSp+40 UNTRACKED flags=0x7  ← string s local
```
Все decoded correctly: register numbers совпадают с AMD64 ABI (rax=0,
rcx=1, rsi=6, r8=8), stack slot offset плотный (40 = 5×8 — five
pointer-slots in от current SP).

### ✅ Part 7 — Frame walker (capture + unwind + per-frame enumeration)

**Capture stub**: `OS/src/Kernel/Memory/GcContextSpill.cs` — shellcode at
ExecStubBuffer offset 512 (ExecStubBuffer bumped 512→1024). 16 byte-emit
helpers produce instructions writing each GP register into the Context
struct by `[FieldOffset]`. Caller-saved (rax/rcx/rdx/r8-r11) zeroed
because GcInfo at call-site PC never references them. Callee-saved
(rbx/rbp/rdi/rsi/r12-r15) carry caller's values directly. Plus computes
ctx.Rip = [rsp], ctx.Rsp = rsp+8 (caller's RSP at call site). Calls back
to managed delegate* with Context* arg. ~180 LOC including ModR/M encoder.

**Frame walker**: `OS/src/Kernel/Diagnostics/GcContextSpillSmokeProbe.cs`
loop using `SehUnwind.VirtualUnwind` (the existing AMD64 RtlVirtualUnwind
equivalent in SehUnwind.cs). For each frame:
1. `CoffMethodGcInfo.TryResolve(rip)` — get methodStart, gcInfo, codeOff
2. `CoffGcInfoDecoder.DecodeHeader` → slot table → live state at codeOff
3. For each live tracked + each untracked slot: `CoffGcInfoResolver.ResolveSlotValue`
4. `SehUnwind.VirtualUnwind(ctx, fn, ...)` — applies UNWIND_CODEs,
   restoring callee-saved from stack and stepping Rip/Rsp to caller's
   frame
5. Repeat until Rip doesn't resolve (out of managed code range)

`CoffMethodGcInfo.Result` extended with `RuntimeFunction*` для feeding в
VirtualUnwind. Cast `OS.Boot.EH.RuntimeFunction*` →
`OS.PAL.SharpOSHost.RuntimeFunction*` (identical layout, two namespaces).

**Верификация** end-to-end на боевом call-chain:
```
sentinel addr = 0x147320 (new string('Z', 7) в Run)

frame[0] Invoke wrapper  → tracked=0 untracked=0
frame[1] Run()           → tracked=6 untracked=2
  root[0] slot=5 reg=rsi LIVE value=0x147320 ← СОВПАДАЕТ С sentinel!
  root[1] stack base=CurrentSp+40 UNTRACKED value=0x1CCB5FA0
  root[2] stack base=CurrentSp+48 UNTRACKED value=0x0  (null slot)
frame[2] Phase4 caller   → tracked=0
frame[3] outer frame     → PC outside interruptible range (epilogue?)
frame[4] outermost       → tracked=0
frame[5] Rip=0 (unresolved, stop)
```

**Это первая успешная end-to-end precise root enumeration на live
stack'е**. JIT поместил sentinel в `rsi`, shellcode корректно его
захватил, UNWIND_CODE walker правильно provagated регистры через два
frame'а, decoder enumerated `slot=5` как live, resolver вернул значение
RSI = `0x147320` совпадающее с известным sentinel address.

Frame walker полностью функционален. Остаётся integration с
GcRoots.MarkAll (заменить conservative scan на этот pipeline) — Part 8.

Replace `GcRoots.ScanStack` conservative scan на precise iteration:
1. Start frame = current RSP/RIP
2. Loop:
   - PC → CoffMethodGcInfo → CoffGcInfoDecoder.Decode...
   - EnumerateLiveSlotsAtPc → callback(slotAddr) for each live ref
   - Apply PE unwind codes (UNWIND_CODE_OP_*) → rewind REGDISPLAY к caller'у
   - Stop когда выходим за пределы managed code range

UNWIND_CODE walker уже частично есть в SehDispatch (CallFrameInfo
unwind для exception). Возможно нужен refactor общего unwinder'а с
двумя client'ами (EH + GC).

### ✅ Part 8 — Integration: KernelGC.CollectPrecise

`OS/src/Kernel/Memory/KernelGcPreciseWalk.cs` — walker который через
`GcContextSpill.Invoke` захватывает Context и в callback итерирует
frames вызывая `SehUnwind.VirtualUnwind` после каждого, decode'ит
GcInfo, resolve'ит slot values, и зовёт `GcMark.MarkFromRoot(value)`
для каждого. Bounded MaxFrames=64. Telemetry `LastFramesWalked /
LastRootsMarked / LastFramesUnresolved`.

`OS/src/Kernel/Memory/KernelGC.cs` — добавлен `CollectPrecise()`:
1. `GcMark.Begin()`
2. `GcRoots.MarkStaticRootsOnly()` — static registered slots (no
   conservative scan)
3. `KernelGcPreciseWalk.RunFromCurrentFrame()` — precise frame walk
4. `GcSweep.Run()` — обычный sweep

`GcRoots.cs` — extracted `MarkStaticRootsOnly()` из `MarkAll()`
(было: static + conservative scan; теперь: одно делегирует другому).

**Fixed during integration**:
- `StackBaseRegister` теперь хранится **denormalized** в
  `CoffGcInfoHeader` (`CoffGcInfoTypes.DenormalizeStackBaseRegister`
  = `x ^ 5`: encoded 0 → reg index 5 (RBP), encoded 1 → 4 (RSP)).
  Без этого FpBase slots resolve'ились через `ctx.Reg(0)` = `ctx.Rax`
  = (caller-saved → zeroed by shellcode) → 0 + signed_offset = wild PF.
- `MarkOneFrame` skip'ит whole frame если PC outside interruptible
  range (prologue/epilogue). Untracked slots в этой зоне могут не быть
  в canonical home'е (FP не установлен / callee-saved не сохранён).

**Верификация** `KernelGcPreciseSmokeProbe`: создаёт `new string('Z',7)`,
читает MT pointer объекта перед Collect, flip'ает `ReclamationDisabled
= false` локально для теста, зовёт `CollectPrecise()`, проверяет что
MT после Collect не изменился. Результат:
```
[gcprec] sentinel obj=0x147D38 MT=0x1CA32370
[gcprec] post-Collect MT=0x1CA32370 framesWalked=7 rootsMarked=5 unresolved=1
[gcprec] verdict: sentinel SURVIVED, walker ran, roots found — PASS
```

7 фреймов прошли, 5 roots найдено, 1 frame unresolved (выход за
managed range). Sentinel пережил sweep потому что precise walker
правильно нашёл его в `Run()`'s rsi-tracked slot и marked.

Stress test (parallel — 62 + 50 + 20 объектов через старый
conservative path) тоже зелёный. Никакой регрессии.

**Status**: precise GC walker полностью функционален. `CollectPrecise`
безопасно зовётся из любого места — не зависит от `CaptureStackTop`
дисциплины, не имеет wild-walker bug.

### ✅ Part 9 — Production roll-out

- `KernelGC.Collect()` теперь выбирает `CollectPrecise()` когда
  `KernelGcPreciseWalk.IsAvailable` (true после ExecStubBuffer + .pdata
  готовы — то есть с Phase2 и далее). Старый conservative path
  (GcStackSpill + ScanStack) остался как fallback для very early boot
  до того как infrastructure поднята.
- `CoreClrProbe.cs:370` — `GC.ReclamationDisabled = true` → `false`.
  Sweep реально освобождает kernel managed objects. Концерн, который
  мотивировал freeze (CoreCLR-allocated refs in kernel GcHeap) снят
  step'ом 109 (NativeArena移reло все non-managed blobs).
- `docs/nativeaot-nostd-kernel-limits.md §10` переписан: было "не звать
  из произвольного места" → теперь "safe via CollectPrecise (default
  path), single-thread walker enough для current production state,
  multi-thread walker нужен будет когда появится preemptive scheduling
  или Collect во время hosted-CoreCLR session".

**Регрессия**: QEMU clean — census OK=42 DEG=2 FAIL=7 (та же поверхность
что и до step 110), launcher 4/4, все smoke probes (включая
KernelGcPreciseSmoke с реальным sweep) PASS.

## Effect

Полный precise GC mark+sweep pipeline функционален на boot thread.
Kernel GcHeap больше не растёт монотонно — sweep освобождает
unreachable managed objects. M1-M5 из memory-ownership.md §9 закрыты.

Реально работающий kernel GC + cleanly разделённый NativeArena для
native blobs = чистая база для Phase F (финализаторы, hosted-CoreCLR
GC cooperation, RetainVM политика).

## Открытые шаги (future)

- **Multi-thread root enumeration** — enumerate Scheduler.Threads + 
  hosted-CoreCLR worker threads when они существуют concurrent с
  Collect. Sейчас не нужно (Collect зовётся когда single-thread),
  понадобится при preemptive scheduling.
- **Memory-pressure trigger для Collect** — сейчас Collect только
  явный (smoke probes); ничего автоматически не запускает sweep
  в production. Когда добавим — нужно interval / heap-growth threshold.
- **Финализаторы** — теперь когда sweep работает, можно реализовать
  finalization queue (objects with destructor → специальный mark pass
  → запуск финализатора → final sweep). Phase F task.

## Files (на текущий момент)

**Новые:**
- `OS/src/Boot/EH/CoffMethodGcInfo.cs` (Part 1)
- `OS/src/Boot/EH/BitReader.cs` (Part 3)
- `OS/src/Boot/EH/CoffGcInfoTypes.cs` (Part 3)
- `OS/src/Boot/EH/CoffGcInfoDecoder.cs` (Part 3 + 4)
- `OS/src/Kernel/Diagnostics/CoffGcInfoDumpProbe.cs` (sanity probe)
- `std/no-runtime/shared/Bcl/BitConverter.cs` (Part 2)
- `std/no-runtime/shared/Bcl/BinaryPrimitives.ReverseEndianness.cs` (Part 2)
- `std/no-runtime/shared/Bcl/BinaryPrimitives.ReadLittleEndian.cs` (Part 2)
- `std/no-runtime/shared/Bcl/BinaryPrimitives.ReadBigEndian.cs` (Part 2)
- `std/no-runtime/shared/Bcl/BinaryPrimitives.WriteLittleEndian.cs` (Part 2)
- `std/no-runtime/shared/Bcl/BinaryPrimitives.WriteBigEndian.cs` (Part 2)

**Изменены:**
- `std/no-runtime/shared/Runtime/MemoryMarshal.cs` (Part 2 — добавлены
  Read/TryRead/Write/TryWrite/Cast)
- `OS/src/Kernel/Memory/NativeAotModuleInit.cs` (Part 3 — expose RTR
  version)
- `OS/src/Kernel/Diagnostics/Probes.cs` (CoffGcInfoDump = true)
- `OS/src/Boot/BootSequence.cs` (вызов probe в Phase4)

## Lessons learned (накопительно)

1. **Сначала foundation, потом decoder.** Перед тем как писать decoder
   с varint+bit packing — собрали базовые BCL piece'и (BitReader,
   BinaryPrimitives, MemoryMarshal). Decoder получился прозрачный, без
   ad-hoc bit math всюду.

2. **Dump-probe — золотой санити-check.** На каждом этапе печатаем то
   что только что декодили + cross-reference с ground truth (CodeLength
   vs methodEnd-methodStart, slot counts vs ожидаемая код-структура).
   Когда `codeLen=34=range=34` на трёх методах — уверенность что весь
   стек до этого момента byte-precise.

3. **`byte[2] == codeLength/2` — не магия, а bit alignment.** В первом
   dump'е увидели pattern `byte[2]/2 == range` и предположили mod-2
   encoding. Оказалось это сдвиг на 1 бит от slim-flag bit + 10 header
   flags + 4 returnKind = 15 бит до codeLength, что эквивалентно
   `byte[2] >> 1`. Понимание этой структуры подтвердило правильность
   decode path.

4. **RTR version 8 — это .NET 7 SDK, не магия.** Сначала удивились
   почему `MajorVersion == 8`, и тыкнули в `OS.csproj:<TargetFramework>
   net7.0`. ILC из .NET 7 эмиттит RTR 8.0 → GcInfo v2 → encoding
   соответствует нашему мапперу. Если когда-нибудь обновим SDK — version
   awareness в decoder'е уже есть.

## Открытые шаги

Parts 5/6/7/8 (см. выше). Каждая обозримая часть decoder'а отдельно,
с dump-probe верификацией перед переходом к следующей.
