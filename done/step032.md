# step032

Дата: 2026-04-24
Статус: закрыт

## Контекст

В step 031 закрыли SUPER-3 фундамент (System.Object virtual-методы, BCL-интерфейсный слой, `EqualityComparer<T>`, `List<T>`, `Dictionary<TKey, TValue>`). Обнаружили что любой shared-generic interface dispatch (reference-typed generic parameter через interface call) halt-ит в `RhpInitialDynamicInterfaceDispatch` — это блокировало:

- `IEqualityComparer<TKey>` как поле в Dictionary.
- `IEquatable<T>` dispatch для value-equality без reference-equality на боксах.
- Любой iface call внутри generic class body когда `T` — reference тип (шаред-дженерик через `__Canon`).

Последствия для BCL-коллекций: `EqualityComparer<int>.Default.Equals(5, 5)` возвращало `false` (два отдельных бокса, reference-equality), `List<int>.Remove(x)` не находил элементы, Dictionary не мог принять custom `IEqualityComparer<K>`. Workaround в step 031 — использовать `object.Equals` напрямую, но это закрывало только часть паттернов.

Step 32 цель: **полный порт NativeAOT cached-interface-dispatch инфраструктуры** чтобы shared-generic iface dispatch работал end-to-end. Unblock-ает всё перечисленное выше одним махом.

## Стратегия

NativeAOT's runtime dispatch — это цепочка:
```
ILC call site
  → RhpInitialDynamicInterfaceDispatch (stub)
  → RhpInterfaceDispatchSlow (asm)
  → RhpUniversalTransition_DebugStepTailCall (asm)
  → RhpCidResolve (C# managed)
  → DispatchResolve.FindInterfaceMethodImplementationTarget (C# managed)
  → tail-jump to resolved target
```

Плюс cache-update path (`RhpUpdateDispatchCellCache`) для fast-path на повторных вызовах.

Портировать всё 1:1 — сотни строк asm + runtime кода. Вместо этого — **единый x64 шеллкод в exec-stub buffer** + managed resolver на C#.

Ключевое решение: не писать `.asm`/`.obj` файл для линковки отдельно (слишком много возни с MSVC toolchain), а эмитить шеллкод в runtime byte-за-байтом в executable буфер от UEFI (EfiLoaderCode allocation — гарантированно RWX). Pattern уже обкатан в `GcStackSpill` и `Cr3Accessor`.

## Архитектура

### Layout exec-stub buffer

Расширен с 128 до 512 байт в `UefiBootInfoBuilder.cs`:

```
0..63     Cr3Accessor (read stub @0, write stub @32)
64..127   GcStackSpill (callee-saved register push/pop trampoline)
128..511  InterfaceDispatch bridge shellcode
```

384 байта хватает с запасом — фактический шеллкод ~195 байт + 16 байт data slots (resolver ptr, fail ptr).

### x64 шеллкод (InterfaceDispatchBridge.cs)

Emit-ится в C# byte-буфер. Контракт с ILC call-site:
- `rcx` = `this`
- `r10` = `InterfaceDispatchCell*`
- `rdx/r8/r9/xmm0..3` = arguments (нужно сохранить на slow path)
- `[rsp]` = return address

Fast path (single-slot cache check):
```
test rcx, rcx             ; null check this
jz   nullfail
mov  rax, [rcx]           ; MT of instance
mov  r11, [r10 + 8]       ; cell.m_pCache
test r11, 3               ; tag bits — cached or still-tagged?
jnz  slow                 ; tagged → managed resolver
cmp  rax, [r11 + 32]      ; entries[0].InstanceType == thisMT?
jne  slow                 ; miss → resolver
jmp  qword ptr [r11 + 40] ; hit: tail-jmp entries[0].TargetCode
```

Slow path (spill + managed resolver + restore + tail-jmp):
```
slow:
    sub   rsp, 0xA8
    mov   [rsp+0x20], rcx    ; spill argument regs
    mov   [rsp+0x28], rdx
    mov   [rsp+0x30], r8
    mov   [rsp+0x38], r9
    mov   [rsp+0x40], r10
    movdqu [rsp+0x50], xmm0  ; spill XMM (FP/SIMD args)
    movdqu [rsp+0x60], xmm1
    movdqu [rsp+0x70], xmm2
    movdqu [rsp+0x80], xmm3
    mov   rdx, r10            ; arg2 = cell (arg1 rcx = this)
    mov   rax, [rip + resolverPtr]
    call  rax                 ; managed Resolve returns target in rax
    test  rax, rax
    jz    fail_after_spill
    ; ... restore regs + add rsp, 0xA8
    jmp   rax                 ; tail-jump; frame unchanged
```

Трик: `sub rsp, 0xA8` выравнивает SP на 16-byte для Win64 `call`. `rsp` на входе был `8 mod 16` (после `call` от ILC), `-0xA8` делает `0 mod 16`. Проверено против Intel SDM + тестовый прогон.

### Managed wrapper + patcher (InterfaceDispatchStub.cs + InterfaceDispatchPatcher.cs)

ILC пишет адрес `RhpInitialDynamicInterfaceDispatch` в каждую dispatch-cell при эмиссии образа. Нам нужен символ с этим именем. Заводим managed метод:

```csharp
[RuntimeExport("RhpInitialDynamicInterfaceDispatch")]
[UnmanagedCallersOnly(EntryPoint = "RhpInitialDynamicInterfaceDispatch")]
private static void RhpInitialDynamicInterfaceDispatch()
{
    OS.Kernel.Panic.Fail("RhpInitialDynamicInterfaceDispatch (stub not patched / patch failed)");
}
```

**Но** managed wrapper при вызове запускает прологом `sub rsp, …` → затирает r10 до того как мы успеем его прочитать. Поэтому первые 5 байт тела wrapper-а **перезаписываются** на `E9 rel32 <shellcode>` в kernel boot. Patcher:

```csharp
byte* shellcode = (byte*)InterfaceDispatchBridge.ShellcodeStart;
byte* target = (byte*)InterfaceDispatchStub.GetMethodAddress();
long displacement = (long)shellcode - ((long)target + 5);  // JMP rel32
target[0] = 0xE9;
target[1..5] = (int)displacement little-endian;
```

Под firmware CR3 `.text` на OVMF мапнут RWX — прямая запись проходит. Для real HW с W^X потребуется alias-mapping через pager root + CR3 switch (TODO, задокументировано в docs).

### Cell encoding decoder (InterfaceDispatch.cs)

Полный порт `rhbinder.h:140-208 InterfaceDispatchCell::GetDispatchCellInfo`. Четыре варианта `m_pCache` tag-битов:

```
0x0 (cache ptr) + value < 0x1000  → VTableOffset inline (value=offset)
0x0 (cache ptr) + value >= 0x1000 → real cache pointer (cache header + entries)
0x1                                 → direct interface MT pointer (high bits)
0x2                                 → indirected rel32 (via IAT)
0x3                                 → direct rel32 (&m_pCache + (int32)value)
```

Плюс walk forward по cells до terminator (`m_pStub == 0`). Terminator encodes slot + cellType:
- Bits 0-15: `InterfaceSlot`
- Bits 16-31: `CellType` (0=InterfaceAndSlot, 1=MetadataToken, 2=VTableOffset)

В нашем тесте `IGenericPickerRef<T>`.Pick: `cache=0xFFFF5583` (tag=3, rel32 = -0xAA7D от `&m_pCache` → MT), terminator через 2 cells с `termCache=0x01` (slot=1, cellType=InterfaceAndSlot).

### Managed resolver (InterfaceDispatchResolver.cs)

```csharp
[UnmanagedCallersOnly]
public static nint Resolve(nint thisPtr, nint cellPtr)
{
    GcMethodTable* thisMT = *(GcMethodTable**)thisPtr;
    InterfaceDispatchCell* cell = (InterfaceDispatchCell*)cellPtr;
    cell->GetDispatchCellInfo(out DispatchCellInfo info);

    // Lazy: first call bootstraps TypeManager slots (see below).
    if (!NativeAotModuleInit.IsInitialized)
        NativeAotModuleInit.TryInitialize(thisMT);

    // Walk inheritance chain; look for (info.InterfaceType, info.InterfaceSlot).
    if (!FindImplSlot(thisMT, info.InterfaceType, info.InterfaceSlot, out ushort implSlot))
        Panic.Fail("iface-resolve: no impl slot");

    void* target = implSlot < thisMT->NumVtableSlots
        ? thisMT->GetSlot(implSlot)
        : thisMT->GetSealedVirtualSlot(implSlot - thisMT->NumVtableSlots);

    PublishCache(cell, thisMT, target, in info);  // single-slot inline cache
    return (nint)target;
}
```

`FindImplSlot` итерирует `thisMT.DispatchMap.Entries`, ищет запись `(InterfaceIndex, InterfaceMethodSlot) == (matching-iface-idx, info.InterfaceSlot)`, возвращает `ImplMethodSlot`. Ходит по parent chain (`GetBaseType`) до тех пор пока не найдёт или не достигнет `null`.

**Sealed virtuals**: если `ImplSlot >= NumVtableSlots`, метод живёт не в основной vtable, а в side-таблице после OptionalFields. Формат: rel32 → массив rel32 → каждый указывает на код. Пример probe: `IGenericPickerRef<T>.Pick` даёт `implSlot=3` при `NumVtableSlots=3` → sealed idx 0 → `GetSealedVirtualSlot(0)` возвращает указатель на Pick.

### Module initialization — TypeManager slot bootstrapping (NativeAotModuleInit.cs)

Тупик, который нашли в ходе работы: `MT.GetDispatchMap()` упирается в `TypeManager.m_pDispatchMapTable`. Но `TypeManager` нужно получить через `MT[TypeManagerIndirection_offset]` → rel32 → slot → `[slot] = TypeManager*`. На нашем runtime-е slot **заполнен нулями** — потому что мы не вызываем NativeAOT's `InitializeModules` из kernel boot.

В нормальном NativeAOT `main.cpp` (Bootstrap) определяет линкерные сентинелы `.modules$A..Z`, `InitializeRuntime` зовёт `InitializeModules(osModule, __modules_a, count, ...)`, которая внутри для каждого модуля:
1. Выделяет TypeManager.
2. Находит секцию `TypeManagerIndirection` (id=204) через ReadyToRunHeader.
3. Пишет указатель TypeManager в каждый слот секции.

Нам нужен минимальный эквивалент. Подход:
1. Найти `ReadyToRunHeader` сканом `.rdata` на signature `0x00525452` ('RTR'). Якорь — `thisMT` пойманный в Resolve (в `.rdata`), сканируем ±32 MB с шагом 4. Валидация: `MajorVersion==8`, `NumberOfSections ∈ (0, 100]`, `EntrySize==24`.
2. Парсим section table (массив `ModuleInfoRow` из `TypeManager.h`). Находим:
   - Секцию `InterfaceDispatchTable` (id=203) — массив `DispatchMap*`.
   - Секцию `TypeManagerIndirection` (id=204) — массив `TypeManagerSlot { TypeManager* TypeManager; int ModuleIndex; }`.
3. Выделяем 56-байтный `TypeManager` в KernelHeap. Заполняем `m_pHeader` + `m_pDispatchMapTable` (остальные поля null).
4. В каждый слот пишем указатель.

Полная цепочка `MT → TypeManager → DispatchMap table → DispatchMap*` теперь работает.

Lazy init: срабатывает на первом вызове Resolve. Якорь MT гарантирует что мы уже в `.rdata` хоть какого-то типа.

### Single-slot cache (PublishCache в Resolve)

После успешного resolve выделяем 48-байтный `InterfaceDispatchCache`:
```
+0  CacheHeader.InterfaceType
+8  CacheHeader.SlotIndexOrMetadataTokenEncoded (slot << 2)
+16 NextFree (unused)
+24 Entries = 1
+32 entries[0].InstanceType = thisMT
+40 entries[0].TargetCode = target
```

Пишем pointer в `cell->Cache`. Tag-биты 0 (KernelHeap даёт 8-byte-aligned, но для надёжности защитный `if ((ptr & 3) != 0) return` без публикации).

Следующий вызов с тем же MT: шеллкод fast path `test r11, 3` → 0 → `cmp rax, [r11+32]` → match → `jmp [r11+40]` мимо managed Resolve.

Polymorphic call site (разные MT) — пока через Resolve каждый раз (single-slot only). Multi-slot cache (как в BCL с `RhpInterfaceDispatch1/2/4/.../64` stubs и eviction + cmpxchg16b) оставлен на "если упрёмся в перформанс".

## Поддержка в GcMethodTable

Расширен `std/no-runtime/shared/GC/MethodTable.cs`:
- `HasOptionalFields` — флаг из Flags.
- `GetBaseType()` — handles `RelatedType` + `IsRelatedTypeViaIAT` indirection. ClonedEEType ещё не handle-им (редко).
- `GetOptionalFieldsPtr()` — проходит через extras region (TypeManagerIndirection + WritableData + optional Finalizer).
- `GetTypeManager()`, `GetTypeManagerDispatchMapTable()` — те же extras + dereference.
- `HasDispatchMap`, `GetDispatchMap()` — читают OptionalFields (NativeFormatDecoder) для индекса, затем индексируют TypeManager's DispatchMap table.
- `GetSealedVirtualSlot(int)` — читает side-table после OptionalFieldsPtr, возвращает target code через двойной rel32 chase.

Layout считается для AMD64 Release: `SupportsRelativePointers=true` (все pointer-ы = rel32, 4 байта), `SupportsWritableData=true` (всегда присутствует между TypeManagerIndirection и опциональными полями).

### NativeFormatDecoder

Новый `std/no-runtime/shared/GC/NativeFormatDecoder.cs`. 7/14/21/28/32-bit VLQ unsigned int decoder совместимый с `Internal.NativeFormat.NativePrimitiveDecoder` (источник внешний к coreclr snapshot, писали по known spec):

```
0xxxxxxx            → 7-bit
10xxxxxx + 1 byte   → 14-bit
110xxxxx + 2 bytes  → 21-bit
1110xxxx + 3 bytes  → 28-bit
1111xxxx + 4 bytes  → 32-bit (low 4 bits of first byte ignored)
```

### OptionalFieldsReader

Новый `std/no-runtime/shared/GC/OptionalFieldsReader.cs`. Walker для `<header, value>` stream в MT.OptionalFields. Header: bit 7 = last-entry flag, bits 0-6 = `EETypeOptionalFieldTag`:
```
0 = RareFlags
1 = DispatchMap
2 = ValueTypeFieldPadding
3 = NullableValueOffset
```

Значения stable по истории NativeAOT.

## BCL-сторона (D + E)

### Primitives с IEquatable<T>

В `OS/src/Boot/MinimalRuntime.cs` примитивы Int32/UInt32/Int64/UInt64/Byte/SByte/Int16/UInt16/Boolean/Char получают recursive-backing-field pattern из NativeAOT Runtime.Base's Primitives.cs:

```csharp
public struct Int32 : IEquatable<int>
{
    private int _value;
    public bool Equals(int other) => _value == other;
    public override bool Equals(object obj) => obj is int v && _value == v;
    public override int GetHashCode() => _value;
}
```

`_value` типа `int` внутри `struct Int32` — рекурсивное объявление, легально потому что ILC special-case-ит примитивы (namespace+name триггерит intrinsic element-type флаги в MT). Компилится, работает — 4 байта, stack-layout не меняется.

### DefaultComparer<T> через IEquatable<T>

В `std/no-runtime/shared/Bcl/EqualityComparer.cs`:

```csharp
public override bool Equals(T x, T y)
{
    if (x is IEquatable<T> eq) return eq.Equals(y);  // shared-gen iface dispatch
    if (x == null) return y == null;
    if (y == null) return false;
    return x.Equals(y);
}
```

`x is IEquatable<T>` для value-type boxes x один раз (для interface-cast), потом `eq.Equals(y)` идёт типизированным interface call — без boxing y. Для reference-типов, не реализующих `IEquatable<T>`, fallback на Object.Equals.

Для этого работал `RhTypeCast_IsInstanceOfInterface` — линкер требовал символ. Добавили в `std/no-runtime/shared/GC/GcRuntimeExports.cs`: простой walk по InterfaceMap (без variance / IDynamicInterfaceCastable). MVP достаточен.

### Dictionary с _comparer

`std/no-runtime/shared/Bcl/Dictionary.cs` возвращает BCL-compat ctor overloads:
- `Dictionary()`
- `Dictionary(int capacity)`
- `Dictionary(IEqualityComparer<TKey> comparer)` (БЫЛО absent)
- `Dictionary(int capacity, IEqualityComparer<TKey> comparer)` (БЫЛО absent)

Поле `IEqualityComparer<TKey> _comparer` (fallback `EqualityComparer<TKey>.Default`). `Find/Remove/GetBucket` идут через `_comparer.Equals/GetHashCode` вместо `object.Equals/key.GetHashCode` напрямую. Каждое обращение — interface call через shared-gen iface dispatch.

## Probe-тесты

В `NativeAotProbe.cs` добавлены:

- `dict<int,int>` — Dictionary с int ключом. Exercise EqualityComparer<int>.Default end-to-end. Зависит от Int32 реализующего IEquatable<int>.
- `dict custom comparer` — Dictionary с user `IEqualityComparer<int>` (ModNComparer с mod-10 equality). Exercise custom iface dispatch через shared-gen resolve.

Перестановка: `Probe_InterfaceCallFromSharedGeneric` ушёл в конец списка проб (был halt-блокер), теперь последний в running order.

## Финальный probe-прогон (QEMU)

```
[info] virtual dispatch: ok val=124
[info] interface: ok val=30
[info] generic method: ok val=20
[info] generic class: ok val=83
[info] static ctor: ok val=99
[info] box/unbox: ok val=77
[info] is/as: ok val=25
[info] array.length: ok val=30
[info] enum: ok val=2
[info] boxed equals (same ref): ok val=1
[info] static assign+read+call: ok val=101
[info] explicit cctor (int): ok val=77
[info] bounds-checked loop: ok val=0
[info] checked add (no overflow): ok val=150
[info] abs-gen<RefT> virtual: ok val=404
[info] iface<RefT> dispatch: ok val=808
[info] eq.Default: ok val=1
[info] eq.Equals(5,5): ok val=1      ← было val=0 (fail)
[info] bcl list<T>: ok val=549        ← было FAIL val=570
[info] bcl list foreach: ok val=60
[info] bcl list as IEnumerable: ok val=600
[info] dict ctor: ok val=0
[info] naot-init: rtr=0x000000000E1254A8 tm=0x0000000000143350 dmTable=... indir=... slots=1
[info] dict add: ok val=1
[info] dict contains: ok val=1
[info] dict tryget: ok val=100
[info] dict foreach: ok val=600
[info] dict<int,int>: ok val=300       ← новая проба
[info] dict custom comparer: ok val=2  ← новая проба
[info] shared-gen iface call: ok val=808  ← раньше halt-ил
[warn] lambda: SKIP (needs Delegate infrastructure)
[info] ---- nativeaot probe end ----
```

Все probes зелёные кроме намеренно отключённых (lambda требует Delegate infra).

`dict custom comparer: val=2` — dict.Count. Добавили 5 и 12; попытка добавить 15 не делалась, но `ContainsKey(15)` → true (mod-10 matches 5). `TryGetValue(25, out v25)` → v25="five" (25 mod 10 == 5).

## Файлы

### Новые

- `OS/src/Kernel/Memory/InterfaceDispatch.cs` — InterfaceDispatchCell + DispatchCellInfo + DispatchMap layout + decoder.
- `OS/src/Kernel/Memory/InterfaceDispatchBridge.cs` — x64 shellcode emitter (byte-literal).
- `OS/src/Kernel/Memory/InterfaceDispatchPatcher.cs` — 5-байт JMP rel32 patch в managed wrapper.
- `OS/src/Kernel/Memory/InterfaceDispatchResolver.cs` — managed resolver + single-slot cache publisher.
- `OS/src/Kernel/Memory/NativeAotModuleInit.cs` — RTR scan + TypeManager alloc + slot fill.
- `OS/src/Boot/InterfaceDispatchStub.cs` — managed wrapper `[RuntimeExport("RhpInitialDynamicInterfaceDispatch")]`.
- `std/no-runtime/shared/GC/NativeFormatDecoder.cs` — 7/14/21/28/32-bit VLQ decoder.
- `std/no-runtime/shared/GC/OptionalFieldsReader.cs` — MT.OptionalFields stream walker + EETypeOptionalFieldTag enum.

### Изменённые

- `OS/OS.csproj` — подключает новые std-файлы.
- `OS/src/Boot/MinimalRuntime.cs` — primitives с `IEquatable<T>` + backing field + `Equals/GetHashCode` overrides.
- `OS/src/Boot/UefiBootInfoBuilder.cs` — ExecStubBuffer расширен с 128 до 512 байт.
- `OS/src/Kernel/Kernel.cs` — `InstallInterfaceDispatchBridge` в boot-последовательности.
- `OS/src/Kernel/Diagnostics/NativeAotProbe.cs` — Probe_InterfaceCallFromSharedGeneric перенесён в конец, добавлены Probe_DictionaryIntKey и Probe_DictionaryCustomComparer.
- `std/no-runtime/shared/Bcl/Dictionary.cs` — _comparer поле + IEqualityComparer<TKey> ctor overloads + use в Find/Remove/GetBucket.
- `std/no-runtime/shared/Bcl/EqualityComparer.cs` — DefaultComparer.Equals через IEquatable<T>.
- `std/no-runtime/shared/GC/GcRuntimeExports.cs` — RhTypeCast_IsInstanceOfInterface RuntimeExport.
- `std/no-runtime/shared/GC/MethodTable.cs` — HasOptionalFields/GetBaseType/GetTypeManager/GetOptionalFieldsPtr/HasDispatchMap/GetDispatchMap/GetSealedVirtualSlot.
- `docs/nativeaot-nostdlib-limits.md` — §2 shared-gen iface dispatch → ✅ с описанием реализации; §7 EqualityComparer → ✅ через IEquatable; §2 generic IEquatable<T> constraint → ✅.

## Замеченные паттерны и ошибки в процессе

**Первый неуспех**: Resolve.Resolve изначально был диагностическим stub-ом — увидели первый panic с `cache=0xFFFF5583`, поняли что декодер нужен с walk-forward-to-terminator, а не просто `cache & ~3`.

**Второй**: DispatchMap вернулся null хотя `HasDispatchMap=1`. Диагностировали: `GetTypeManagerDispatchMapTable` → TypeManager slot → slot[0..8]=0. Корневая причина — нет `InitializeModules`. Пришлось написать RTR-сканер.

**Третий**: `implSlot=3 >= NumVtableSlots=3` → sealed virtual. Мы не handle-или sealed. Добавили side-table lookup.

**Четвёртый (link-time)**: `x is IEquatable<T>` в DefaultComparer эмитит `RhTypeCast_IsInstanceOfInterface` — ILC ждёт этот символ. Линкер failed `LNK2001`. Добавили runtime export.

Каждый из этих этапов требовал diagnostic dump-а в Resolve, смотрения значений в QEMU логе, понимания "что именно ILC ожидает". Diagnostic dumps оставлены на failure-пути для будущих similar случаев.

## Что откладываем

- **Multi-slot cache** (polymorphic call sites — разные MT через один cell). Сейчас single-slot; polymorphic идёт через Resolve каждый раз.
- **`cmpxchg16b` atomic cell update** — понадобится при многопоточности, сейчас single-threaded boot OK.
- **Variance** в iface dispatch (ко-/контравариантность).
- **Default interface methods**.
- **IDynamicInterfaceCastable**.
- **Alias-mapping patcher** для real HW с W^X (сейчас OVMF позволяет прямую запись в `.text` под firmware CR3).
- **`Single`, `Double`, `IntPtr`, `UIntPtr`** без IEquatable — добавим если нужно как Dictionary ключ.

## Следующий шаг

Step 33 — коллекции второго ряда (HashSet, Stack, Queue) плюс сопутствующая инфра (IComparable<T>, IComparer<T>, Comparer<T>.Default). Шаред-дженерик iface dispatch теперь работает, так что всё идёт в штатном BCL-compat API без обходов.
