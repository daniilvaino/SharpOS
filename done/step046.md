# Step 46 — Phase 1 step 3: EH trailer + ehInfoRVA varint decoder

## Контекст

Третий step из 11-step Phase 1 try/catch roadmap. Цель — managed reader EH clause table из NativeAOT-private trailer'а после standard UNWIND_INFO blob'а. Foundation для:
- Step 4 (StackFrameIterator) — `RhpEHEnumInitFromStackFrameIterator` использует тот же decoder.
- Step 5 (DispatchEx) — `FindFirstPassHandler` enumerate'ит clauses чтобы найти catching handler.

Smoke gate L6 = 111.

## Решение

### VarInt — NativeAOT-specific length-prefix encoding

`OS/src/Boot/EH/VarInt.cs` — port из `gc-experiment/dotnet-runtime/src/coreclr/nativeaot/Runtime/inc/varint.h`.

Это НЕ continuation-bit varint (LEB128). Encoding (для unsigned values):
- `value < 128` → 1 byte: `value*2 + 0` (low 1 bit = 0)
- `value < 128²` → 2 bytes: `value*4 + 1` (low 2 bits = 01)
- `value < 128³` → 3 bytes: `value*8 + 3` (low 3 bits = 011)
- `value < 128⁴` → 4 bytes: `value*16 + 7` (low 4 bits = 0111)
- иначе → 5 bytes: `0x0F` marker, then 4 LE bytes (low 4 bits = 1111)

Length detection on first byte's low bits. Value bits в верхних позициях, little-endian после length tag.

`ReadUnsigned(ref byte* p)` — switch по low bits, advance pointer past varint, return decoded uint.

### CoffEhDecoder — EH clause enumeration

`OS/src/Boot/EH/CoffEhDecoder.cs` — managed analog `RhpEHEnumInit` / `RhpEHEnumNext` из stock `CoffNativeCodeManager.cpp:841-926`.

**`EhEnumInit(byte* ip, out EHEnum state, out byte* methodStart)`**:
1. `CoffMethodLookup.TryFindMethod(ip)` — resolve IP → MethodInfo (включая funclet→ROOT walk если IP в funclet).
2. Compute UNWIND_INFO blob size: `4 + 2 * countOfCodes` + `ALIGN_UP(size, 4) + 4` если EHANDLER/UHANDLER (никогда не triggert у нас).
3. Read trailer byte `unwindBlockFlags` immediately after standard blob.
4. Skip optional `associatedDataRVA` (4 bytes) если `UBF_FUNC_HAS_ASSOCIATED_DATA` set.
5. Bail out если `UBF_FUNC_HAS_EHINFO` not set — у method'а нет try/catch/finally.
6. Read `ehInfoRVA` (4 bytes LE), translate to absolute pointer via image base.
7. Read `nClauses` varint at start of EH info blob.

**`EhEnumNext(ref EHEnum state, out RhEHClause clause)`**:
1. Check `currentClauseIndex < totalClauses`.
2. Read varint `tryStart`.
3. Read varint `tryEndDeltaAndKind`. Low 2 bits = clause kind, upper bits = `(tryEnd - tryStart)`.
4. Switch on kind:
   - `Typed (0)` — varint `handlerOffset` + 4-byte `typeRVA` (unaligned LE).
   - `Fault (1)` — varint `handlerOffset` only. ILC encodes IL `finally` as Fault.
   - `Filter (2)` — varint `handlerOffset` + varint `filterOffset`.
   - `Unused (3)` — never appears in real EH info.

Все handler/filter offsets — relative to method start, transformed to absolute pointers in `RhEHClause`.

### IsInstanceOfClass + IsInstanceOfException runtime helpers

ILC начал generating call к `RhTypeCast_IsInstanceOfClass` при первом появлении `catch (TypedException)` в нашем коде (probe MethodB и MethodC). Linker error `LNK2001: unresolved external symbol RhTypeCast_IsInstanceOfClass`.

Добавлены оба helper'а в `std/no-runtime/shared/GC/GcRuntimeExports.cs`:
- `RhTypeCast_IsInstanceOfClass(MT* target, object obj) -> object` — для `obj is Class` / explicit cast.
- `RhTypeCast_IsInstanceOfException(MT* target, object obj) -> bool` — boolean variant для catch-clause matching.

Algorithm: identity check (fast path) → walk class hierarchy via `GcMethodTable.GetBaseType()` (тот же helper что interface dispatch), capped at 32 levels.

Пропущены: generic variance (Phase 6+ when hosted-tier), cloned types (rare), array→object special case (no array catches in kernel). Можно extend при необходимости.

### MethodImplOptions.NoInlining

Нужен для test methods L6 probe чтобы ILC не заinline'ил их (без try blocks в final binary). В наш минимальный `MethodImplOptions` enum (`OS/src/Boot/MinimalRuntime.cs`) добавлено standard BCL value `NoInlining = 0x0008`.

### L6 probe — 3 test methods

`EhProbe.EhDecode()`:

```csharp
private static int MethodA_TryFinally() {
    int x = 0; int y = 0;
    try { x = Opaque(5); }
    finally { y = Opaque(10); }
    return x + y;
}

private static int MethodB_TryCatch() {
    int x = 0;
    try { x = Opaque(7); }
    catch (InvalidOperationException) { x = -1; }
    return x;
}

private static int MethodC_TryCatchWhen() {
    int x = 0;
    try { x = Opaque(11); }
    catch (Exception ex) when (ex.Message != null) { x = -1; }
    return x;
}

[NoInlining] static int Opaque(int v) => v + 1;
```

`Opaque` — non-inlinable helper, ILC не может prove "throw-free body" → EH info preserved.

`CountClauses(ip, ref typed, ref finally, ref filter)` — для каждого method enumerate clauses, count by kind.

Sum: `100 * filterCount + 10 * finallyCount + typedCount`. Expected `100*1 + 10*1 + 1 = 111`.

`Probes.EhDecode = true` toggle.

## Результат

```
[info]   l6-diag: typed=1 finally=1 filter=1
[info] eh L6 ehInfo varint decode: val=111
```

Подтверждено:
- VarInt decoder работает (correct nClauses + tryStart/tryEnd/handler offsets).
- EH info trailer location формула правильная (5 records were `<not found>` в раннем probe — это была off-by-2 в нашей PowerShell probe, stock formula `4 + 2*N` рабочая).
- Все три clause kinds распознаются ILC-emitted code'ом нашими test methods.
- `RhTypeCast_IsInstanceOfClass` / `IsInstanceOfException` linker symbols resolved.

L1, L2, L4, L5 + GC stress + NativeAotProbe + CctorProbe + ELF apps все зелёные, no regression.

## Файлы

### Новые

- `OS/src/Boot/EH/VarInt.cs` — NativeAOT length-prefix varint reader.
- `OS/src/Boot/EH/CoffEhDecoder.cs` — `EhEnumInit` + `EhEnumNext` + `RhEHClause` + `EHEnum` state.
- `done/step046.md` — этот файл.

### Изменённые

- `std/no-runtime/shared/GC/GcRuntimeExports.cs` — `RhTypeCast_IsInstanceOfClass` + `RhTypeCast_IsInstanceOfException`.
- `OS/src/Boot/MinimalRuntime.cs` — `MethodImplOptions.NoInlining = 0x0008`.
- `OS/src/Kernel/Diagnostics/EhProbe.cs` — `EhDecode()` probe + 3 test methods + `Opaque` helper + `CountClauses`.
- `OS/src/Kernel/Diagnostics/Probes.cs` — `EhDecode` toggle.

## Что дальше

Phase 1 progress: 3/11. Step 47 = step 4 — StackFrameIterator + 4-opcode unwind decoder. Smoke L7=3 (cross-function frame walk).
