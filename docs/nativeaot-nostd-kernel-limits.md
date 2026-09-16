# NativeAOT + NoStdLib (kernel tier): карта ограничений

Живой документ. Перечень managed-паттернов C#, которые **не работают** или работают с оговорками в **самом ядре SharpOS** (NativeAOT **8.0.27 / RTR major 9** + `NoStdLib=true` + наш `MinimalRuntime` без полной BCL; бампнут с 7.0.20 в step130 — вся батарея проб зелёная под ILC 8, кроме `EnumToString`).

**Область применения:** код ядра (`OS/`) **и** свободностоящих PE-приложений (`apps_native/`) — с step141 они компилят тот же std, поэтому карта у них общая, а немногочисленная app-специфика помечена по месту. ELF-ярус удалён; [`nativeaot-nostd-elf-limits.md`](nativeaot-nostd-elf-limits.md) оставлен как исторический снимок. У stock CoreCLR-hosted кода ещё другая поверхность — см. [`coreclr-hosted-limits.md`](coreclr-hosted-limits.md). Общий обзор всех трёх tier'ов с компаративной таблицей — в [`README.md`](../README.md).

Все пункты проверены на практике через `OS/src/Kernel/Diagnostics/NativeAotProbe.cs` — там живут минимальные repro-ы. Если что-то из этого списка понадобится для конкретной задачи — сначала убеждаемся что работаем через workaround, потом принимаем решение: либо оставить ограничение, либо дописать недостающий helper.

**Конвенция:**
- ❌ **Не работает** — компилируется или нет, но даёт `#GP`, halt-через-stub, либо silent corruption.
- ⚠️ **Работает через обход** — есть идиома которая даёт эквивалентное поведение.
- 🔧 **Не компилируется** — ILC/компилятор C# отвергает код без доп. заглушек.

**Важная терминология.** Когда пишем «halt», «уходит в стаб» — имеем в виду что наш `RhpInitialDynamicInterfaceDispatch` / `RhpFallbackFailFast` / `ThrowHelpers.Throw*` — это `while(true);`. В настоящем runtime там полноценная логика (asm-trampoline для dispatch, managed-exception throw для throws). У нас пока заглушка → код, которому не хватает логики, «зацикливается» ровно в этом `while`. Это **не баг нашей логики**, это **ненаполненный контракт с ILC**. Полный fix для каждого такого случая — портировать соответствующий asm/runtime helper (пример: `GcStackSpill` shellcode в kernel/Memory/, успешный пример реализации).

---

## 1. Static-field инициализация

### ✅ Lazy static reference field — РАБОТАЕТ (step 40-41)

Каноничный паттерн:
```csharp
private static readonly T s_default = new T();
public static T Default => s_default;
```

Работает напрямую на нашей сборке. Полное решение собрано из трёх частей:

1. **`System.Runtime.CompilerServices.ClassConstructorRunner` port** — `std/no-runtime/shared/Runtime/ClassConstructorRunner.cs`. Methods: `CheckStaticClassConstructionReturnGCStaticBase`, `CheckStaticClassConstructionReturnNonGCStaticBase`, `CheckStaticClassConstruction`. Без recursion fix (state==2 → return immediately) на single-thread депозит deadlock'ит — мы добавили early-return.
2. **Drop `--resilient` ILC flag** — без этого ILC молча подставляет fallback stub'ы (sentinel `0xFFFFF0000000000E`) вместо нашего runner'а. CSproj `DropResilient` MSBuild target пере-эмитит `OS.ilc.rsp` без флага между `WriteIlcRspFileForCompilation` и `IlcCompile`.
3. **GC statics materialization** — `OS/src/Kernel/Memory/GcStaticsMaterializer.cs`. Port `StartupCodeHelpers.InitializeStatics`: walks `ReadyToRunSectionType.GCStaticRegion` (id=201), для каждого Uninitialized entry аллоцирует объект через `RhpNewFast`-equivalent, копирует preInit blob в raw data, заменяет tagged pointer на object reference. Без неё canonical pattern с implicit cctor крашится с `#GP` (sentinel `0xFFFF000000000010`) — ILC's TypePreinit interpreter эмитит descriptor cell, но без runtime materialization она остаётся unresolved.

Ограничение текущего setup: materialization runs **поздно** в boot (после ACPI/HPET). Для использования `static readonly T x = new T()` в коде, который **выполняется на самом раннем boot'е** (banner, heap init, exec stubs), нужен step 42 — переместить materialization сразу после `GcHeap.Init()`. До этого момента continued использовать `""` literal или factory property.

### ✅ Explicit static cctor работает

```csharp
class C { public static T X; static C() { X = new T(); } }   // ok
```

Через тот же ClassConstructorRunner pathway. Reference и value поля — оба ОК.

### ✅ Прямое `s_field = new X(); ...; s_field.M()` работает

В пределах одного метода ILC видит что инициализация уже произошла, не вставляет cctor-check. Полезная лазейка для случаев когда хочется избежать static field вообще.

---

## 2. Generics

### ✅ Вариантный interface dispatch (co/contravariance) — step148

`InterfaceDispatchResolver.FindImplSlot` при несовпадении MT-указателей
сравнивает `GenericDefinition` и, если совпало, проходит по аргументам с
вектором вариантности определения (порт `TypeParametersAreCompatible` из
`nativeaot/Runtime.Base/src/System/Runtime/TypeCast.cs`): ковариантный —
присваиваемость `src→dst`, контравариантный — наоборот, инвариантный —
тождество. Проверка присваиваемости — цепочка базовых типов плюс карта
интерфейсов, прямым сравнением.

Живой случай, на котором закрыто: ManagedDoom на переходе между уровнями
запрашивает `IReadOnlyList<Y>.get_Item` у `X[]`, где карта массива несёт
`IReadOnlyList<X>`.

**Границы.** Ветка намеренно консервативна и при любой неопределённости
отказывает (то есть остаётся прежняя паника, а не догадка): нет вектора
вариантности, арность вне `1..8`, значимые типы в аргументах, вложенная
вариантность внутри проверки присваиваемости. Последнее потребовало бы
защиты от циклов и пока не нужно.

Раскладка generic-полей MethodTable разобрана в `GcMethodTable`
(`GetGenericDefinition` / `GetGenericArgument` / `GetGenericVariance`) по
канону из `gc-experiment/dotnet-runtime-8.0`: хвостовые поля идут в порядке
`TypeManagerIndirection → WritableData → [DispatchMap] → [Finalizer] →
[OptionalFields] → [SealedVirtualSlots] → [GenericDefinition] →
[GenericComposition]`, каждое — 4-байтный относительный указатель. Арность
берётся из `ComponentSize` **определения**; при арности 1 аргумент лежит в
самом поле композиции, при большей — там относительный указатель на список.

**Историческое:** до step148 обходились типизацией хранилища конкретно
(jagged `T[][]`); так пропатчены 3 таблицы ManagedDoom, они оставлены как
есть.

### 🔧 `new T()` с `where T : new()`

```csharp
static T MakeNew<T>() where T : new() => new T();
```

**Ошибка компиляции:** `Missing compiler required member 'System.Activator.CreateInstance'`.

**⚠️ Workaround:** либо добавить заглушку `System.Activator.CreateInstance<T>()`, либо передавать фабрику через `delegate*<T>` параметр.

### ❌ Generic virtual method (GVM) — не проверено

Предполагаем что тоже требует specific helpers (`RhpGenericVirtualCall` и родня). Если понадобится — добавить probe отдельно.

### ✅ Generic method + generic class + virtual override в generic abstract — работают (с оговорками)

Подтверждено probe для value-type параметра (`GenAbsIface<int>`, `EqualityComparer<int>`). Но:

### ✅ Virtual call на abstract generic base class — работает

Подтверждено probe `abs-gen<RefT> virtual` для T=class. Это была первоначальная гипотеза о причине halt в Dictionary, но она неверна — virtual через abstract class работает нормально и для value, и для reference T.

### ✅ Номер слота из карты диспетчеризации — относительно ОБЪЯВИВШЕГО типа

Запись `DispatchMapEntry.ImplMethodSlot` индексируется по таблице методов того типа,
**в чьей карте она найдена**, а не по типу объекта. Если номер выходит за размер той
таблицы — это индекс в её запечатанной (sealed) таблице, тоже принадлежащей объявившему
типу.

Разница видна только при наследовании, зато молча: у наследника слотов может быть
БОЛЬШЕ, и тогда запечатанный индех базового класса выглядит как законный слот наследника.
Найдено на `DelegateTreeBuilder<T>` (7 слотов) поверх `TreeBuilder<T>` (6 слотов + sealed):
карта говорила «слот 6» = sealed[0] базового, а читался пустой слот 6 наследника →
резолвер возвращал ноль → общая паника про отказ диспетчеризации.

В запечатанной таблице ILC держит **невиртуальные** методы, реализующие член интерфейса —
например обычное авто-свойство. Путь рядовой, а не экзотический.

Виртуальный слот при этом читается из таблицы **объекта** (иначе переопределение в
наследнике не сработает); запечатанный — из таблицы объявившего типа.

### ✅ Interface dispatch через generic interface со specialized T — работает

Probe `iface<RefT> dispatch`: `IGenericPickerRef<RefMarker> iface = new ...; iface.Pick(obj)` — работает. ILC specializes dispatch cell для конкретного `RefMarker`.

### ✅ Interface dispatch ВНУТРИ generic class (shared-generic, __Canon) — работает end-to-end

Пример:
```csharp
class GenericContainer<T> {
    IGenericPickerRef<T> _thing;
    int Call(T x) => _thing.Pick(x);   // halt тут при T=reference
}
```

Probe `shared-gen iface call: ok val=808` подтверждает end-to-end работоспособность. Dictionary<K,V> теперь держит поле типа `IEqualityComparer<TKey>` и использует его в Find/Remove/GetBucket — все обращения идут через shared-generic iface dispatch и разрешаются корректно.

**Причина (исторически):** ILC для `<T>` где T — reference type делит один canonical код между всеми T через `System.__Canon`. Interface dispatch cell при таком canonical коде не имеет pre-resolved target.

**Как устроено (step 31-32):**

**Инфраструктура готова и работает:**
- ✅ **Структуры** (`OS/src/Kernel/Memory/InterfaceDispatch.cs`): `InterfaceDispatchCell`, `InterfaceDispatchCache`, `InterfaceDispatchCacheEntry`, `DispatchCellInfo`, `InterfaceDispatchCacheHeader` — layout copy из `rhbinder.h` + `CachedInterfaceDispatch.h`.
- ✅ **`GcMethodTable` расширен** (`std/no-runtime/shared/GC/MethodTable.cs`): `NumVtableSlots`, `NumInterfaces`, `HashCode`, `GetSlot(int)`, `GetInterfaceMap()`, `EEInterfaceInfo` с IAT-aware `GetInterfaceEEType()`.
- ✅ **Shellcode** (`OS/src/Kernel/Memory/InterfaceDispatchBridge.cs`): 195-байтный byte-emitter в C#, живёт в exec-stub buffer по offset 128 (буфер расширен с 128 до 512 байт в `UefiBootInfoBuilder.cs`). Fast path: null-check + single-slot cache check + tail-jmp. Slow path: spill (0xA8 stack) + call resolver + restore + jmp rax.
- ✅ **Managed wrapper** (`OS/src/Boot/InterfaceDispatchStub.cs`): `[RuntimeExport("RhpInitialDynamicInterfaceDispatch")]` + `[UnmanagedCallersOnly]`. Body: `Panic.Fail(...)` — выполняется только если patcher не сработал (noisy fallback вместо silent spin).
- ✅ **Patcher** (`OS/src/Kernel/Memory/InterfaceDispatchPatcher.cs`): в kernel boot (до `NativeAotProbe`, под firmware CR3) пишет `E9 rel32` в первые 5 байт managed wrapper, JMP на шеллкод. OVMF по умолчанию держит kernel image RWX, прямая запись проходит. Readback check. Для real HW с W^X — alias-mapping через pager root + CR3 switch (TODO).
- ✅ **Integration** (`OS/src/Kernel/Kernel.cs`): `InstallInterfaceDispatchBridge(bootInfo)` вызывается сразу после `GcStackSpill.TryInitialize`, логирует `iface dispatch bridge installed`.

**Верификация (probe выдал):**
- `iface<RefT> dispatch: ok val=808` — мономорфный iface-call идёт через fast path (ILC пре-выпек `m_pCache` — single-entry cache с tag=0, наш `cmp rax, [r11+16]` + `jmp [r11+24]` срабатывает). Resolver не зовётся.
- `shared-gen iface call` — slow path активируется, аргументы спилят, resolver зовётся, получает корректные `this/mt/cell/stub/cache`. ILC для shared-generic cell пишет:
  - `cache = 0xFFFF5583`, tag bits = 3 → `IDC_CachePointerIsInterfaceRelativePointer`. Реальный InterfaceType-указатель = `(&cell.m_pCache + (int32)cache) & ~0x3`.
  - Terminator cell в 2 cell-ах вперёд (`m_pStub == 0`), его `m_pCache = 0x01`: низкие 16 бит = slot=1 (не 0 — это shared-generic, slot 0 занят generic-context dictionary), биты 16-31 = cellType=0 (`InterfaceAndSlot`).

**Дополнительно реализовано (step 32):**
- `DispatchCellInfo` декодер (`InterfaceDispatchCell.GetDispatchCellInfo`) — walk forward до terminator, handle tag 0x1/0x2/0x3 (direct ptr, rel32, indirected rel32).
- `NativeFormatDecoder` + `OptionalFieldsReader` в `std/no-runtime/shared/GC/` — 7/14/21/28/32-bit VLQ decoder + tag-value stream walker.
- `GcMethodTable` расширение: `HasOptionalFields`, `GetOptionalFieldsPtr`, `GetTypeManagerDispatchMapTable`, `HasDispatchMap`, `GetDispatchMap`, `GetBaseType`, `GetSealedVirtualSlot`.
- `NativeAotModuleInit` — одноразовая инициализация модуля: сканирует `.rdata` на signature `0x00525452` ('RTR'), находит ReadyToRunHeader, выделяет 56-байтный TypeManager в KernelHeap, заполняет `m_pDispatchMapTable` из секции `InterfaceDispatchTable` (id=203), записывает указатель на TypeManager в каждый слот секции `TypeManagerIndirection` (id=204). Lazy-init на первом вызове Resolve (anchor — `thisMT`).
- `DispatchMap` struct + walker по `(InterfaceIndex, InterfaceMethodSlot)` → `ImplMethodSlot`. Walk inheritance chain через `GetBaseType`.
- Если `ImplSlot >= NumVtableSlots` → sealed virtual side-table (rel32 → таблица → rel32 на target).
- Single-slot inline cache: при успешном resolve Resolve выделяет 48-байтный `InterfaceDispatchCache`, заполняет entry `{ thisMT, target }` и пишет нетегированный указатель в `cell.m_pCache`. Следующие вызовы с тем же MT идут по fast path шеллкода, минуя managed Resolve.

**Источники для порта:** живут в `gc-experiment/dotnet-runtime/src/coreclr/nativeaot/`:
- `Runtime.Base/src/System/Runtime/CachedInterfaceDispatch.cs` — `RhpCidResolve_Worker`.
- `Runtime.Base/src/System/Runtime/DispatchResolve.cs` — `FindInterfaceMethodImplementationTarget`.
- `Runtime/inc/rhbinder.h` — cell encoding.
- `Common/src/Internal/Runtime/MethodTable.cs` — `DispatchMap`, `OptionalFieldsReader`.
- `Common/src/Internal/Runtime/CompilerHelpers/StartupCodeHelpers.cs` — `InitializeGlobalTablesForModule` (мы воспроизвели минимальный путь через RTR scan).
- `Runtime/TypeManager.h`, `Runtime/TypeManager.cpp` — TypeManager layout, `m_pDispatchMapTable` = секция `InterfaceDispatchTable`.
- `Runtime/inc/ModuleHeaders.h` — ReadyToRunSectionType enum values (203, 204, ...).

**Риски:**
- Unwind info managed wrapper не синхронна с overwrite-нутым телом. Нерелевантно пока нет exception engine.
- Cache leak при polymorphism (MT меняется) — старые single-slot caches в `KernelHeap`. Допустимо для short workloads.
- Real HW с W^X: прямая запись в .text не пройдёт, понадобится alias-map через pager root + CR3 switch. Not blocking для QEMU/OVMF.

**Что откладываем:**
- Multi-slot cache (`RhpInterfaceDispatch2/4/…/64`) — +200 строк, только если упрёмся в перформанс polymorphic call-sites.
- Cache eviction / free-list.
- `cmpxchg16b` atomic cell update — понадобится при многопоточности.
- Variant interfaces (контра-/ковариантность generic параметров).
- Default interface methods.
- IDynamicInterfaceCastable.

**Perf budget:** hit path ~5 инструкций (~5ns), miss path ~150-400ns (resolver). BCL native: ~3-5ns hit, ~500ns miss. Сопоставимо.

### ✅ Generic constraint `where T : IEquatable<T>`

Раньше: `key.Equals(other)` под этим constraint → `constrained.callvirt IEquatable<T>::Equals` → shared-generic iface dispatch → halt. Теперь: тот же путь, но резолвер работает — пробы `dict<int,int>` и `dict custom comparer` подтверждают. Если T реализует `IEquatable<T>` (наши примитивы — Int32/UInt32/Int64/UInt64/Byte/SByte/Int16/UInt16/Boolean/Char — теперь делают), вызов разрешается в их `Equals(T)` напрямую без boxing обоих операндов (boxing x для iface-проверки остаётся — 1 штука).

### ❌ Generic `as T` / `(T)` cast — `RhTypeCast_IsInstanceOf` / `RhTypeCast_CheckCast` отсутствуют

```csharp
T? CastAs<T>(object o) where T : class => o as T;          // LNK2001
T  CastChecked<T>(object o) where T : class => (T)o;        // LNK2001
```

```
OS.obj : error LNK2001: unresolved external symbol RhTypeCast_IsInstanceOf
OS.obj : error LNK2001: unresolved external symbol RhTypeCast_CheckCast
```

**Корень:** в `std/no-runtime/shared/GC/GcRuntimeExports.cs` реализованы только конкретные варианты — `RhTypeCast_IsInstanceOfClass` / `RhTypeCast_IsInstanceOfException` / `RhTypeCast_IsInstanceOfInterface`. Они вызываются когда тип на месте каста известен ILC статически (`obj is Foo`, `(Foo)obj` где Foo — конкретный type-ref). При **generic** параметре (`as T` / `(T)x` в `Method<T>`) ILC эмитит лоупер на generic-helper'ы — а их у нас нет.

**Workaround:** если в API можно подставить конкретный тип, используем pattern matching:
```csharp
object? target = HandleTable.Lookup(handle);
if (target is not Iocp ev) return 0;   // ↔ RhTypeCast_IsInstanceOfClass, линкуется
```

Точно так же делают все остальные kernel-side bridges — `EventBridge` / `MutexBridge` / `SemaphoreBridge` / `ThreadStubs`. Никаких `HandleTable.LookupAs<T>(handle)`.

**Если generic-helper действительно нужен** (например, портируем generic-heavy BCL collection) — портировать `RhTypeCast_IsInstanceOf` / `RhTypeCast_CheckCast` из `gc-experiment/dotnet-runtime/src/coreclr/nativeaot/` по принципу [«воровать из BCL, не изобретать»](https://github.com/dotnet/runtime). Surfaced step103 → IocpBridge.

**Hosted tier** — изначально (step 103) **тоже падал**, но не из-за
RhTypeCast (JIT cast helper отлично делает `COMPlusThrow(kInvalidCastException)`),
а из-за трансляции C++ msc throws (code 0xE06D7363) в managed: на
TARGET_UNIX/SHARPOS `PAL_TRY/PAL_CATCH` оборачивал C++ throw как
`SEHException` без сохранения `EEException::m_kind`. **Фикс — там же,
step 103** (`reference_msc_throw_becomes_sehexception` РЕШЕНО):
двойной deref `args[1]` в `clrex.cpp:619` + `excep.cpp:5585` извлекает
оригинальный `EEException*`. Типизированные catches на конкретный
тип (`InvalidCastException`, и т.п.) теперь работают как ожидается.
В hosted tier'е generic `as T` ✅ — verified `constrained where T : class`
в `normal-hello` Sec 8 (step 119).

То есть **AOT-tier'е (kernel/ELF) и hosted tier'е были разные корни**:
- AOT: LNK2001 на `RhTypeCast_IsInstanceOf` / `RhTypeCast_CheckCast` —
  generic helper не реализован → нелинкуется. Сейчас.
- Hosted: msc-throw трансляция теряла EE-тип. Закрыто step 103.

---

## 3. Nullable\<T\>

### 🔧 `Nullable<T>.HasValue` / `.Value` / `.GetValueOrDefault()`

**Ошибка компиляции:** наш `public struct Nullable<T> where T : struct { }` — пустой. Свойства не определены.

**⚠️ Workaround:** либо дописать минимальную реализацию Nullable с `_hasValue + _value` полями и свойствами, либо не использовать nullable value types — передавать `(bool has, T value)` tuple или sentinel-значение.

---

## 4. Массивы

### ✅ Multi-dimensional arrays (step153)

```csharp
int[,] m = new int[3, 4];   // работает, включая m.Rank / m.GetLength(d)
```

ILC переводит `newobj` на массиве в `Internal.Runtime.CompilerHelpers.ArrayHelpers`
и ищет этот тип **по имени** в системном модуле; без него падает кодогенерация
всего метода, а в сообщении назван метод-владелец, а не отсутствующий помощник
(инициализатор поля `byte[2,1024]` читался как «Code generation failed for
`Ppu..ctor`»). Порт upstream-помощника —
`std/no-runtime/shared/Runtime/ArrayHelpers.cs`, плюс `Rank`/`GetLength`/
`GetLowerBound` в `Runtime/Array.cs`.

Раскладка: `[MethodTable*][длина][границы 2×rank][элементы]`. Блок границ лежит
там, где у одномерного массива первый элемент, и `BaseSize` его уже учитывает —
поэтому обычный путь размещения считает размер верно без спецслучаев.

**❌ Не поддержано:** ненулевые нижние границы и MdArray ранга 1 (`int[*]`,
выразим только в IL) — бросают. Настоящий рантайм разруливает их через
рефлексию, которой здесь нет; выделять объект не той формы молча — хуже отказа.

**⚠️ Не проверено:** многомерные массивы **ссылок** (`object[,]`) — вопрос к
обходу кучи сборщиком, не к размещению. Живые потребители (Fami) обходятся
байтовыми.

### ⚠️ Reference-array assignment (stelem.ref) — требует helpers

`object[] arr; arr[i] = obj` требует `RhpStelemRef` — у нас реализован. Работает.

`arr.Contains(item)` на reference-array с value-типом: due to boxing-based equality, reference-сравнение двух отдельных боксов всегда false → `Contains` возвращает false даже если значения равны. Не крашит, но результат "не тот".

### ⚠️ Array covariance — silent UB на wrong-type store (нет ArrayTypeMismatchException)

`RhpStelemRef` в нашем std (`std/no-runtime/shared/GC/GcRuntimeExports.cs`)
**skipped все checks**: null/bounds/**covariance**/write barrier. Кернел-
код trusted, а GC non-generational. Эффект:

```csharp
object[] o = new string[3];   // covariant alias
o[0] = 42;                     // boxed int into string[]
                               // → AOT: silent UB (heap corruption)
                               // → CoreCLR: ArrayTypeMismatchException
```

Монотипичный stelem (Base[] aliased over Derived[], писать Derived)
работает корректно — это паттерн который обычно встречается в реальном
коде. Wrong-type store — edge case, обычно симптом баги выше по стеку;
silent UB вместо early-throw усложняет диагностику.

**Когда чинить:** добавить ComponentType check в `RhpStelemRef` (или
полный port из dotnet/runtime) если столкнёмся с явным симптомом
heap corruption после array assignment. Pretty cheap fix (compare MT
pointers), но usability win небольшой пока user-code не пишет
generic-data-container'ы с covariant assignment.

### ✅ Массивы реализуют `IEnumerable<T>` / `IList<T>` / `ICollection<T>` (step 142)

В NativeAOT интерфейсы SZ-массивов приходят НЕ из CoreCLR-шного
SZArrayHelper, а из **`System.Array<T>`**: ILC берёт interface list +
dispatch map array-MT из этого класса, а `this` внутри его методов — сам
`T[]` (реинтерпретация `Unsafe.As<T[]>`). До step142 наш `Array<T>` был
пустой заглушкой в MinimalRuntime — array-MT нёс `NumInterfaces=0`, и любой
интерфейсный вызов на массиве умирал в диспатче (в т.ч. с corrupted-`this`
джампами через мусорную мапу — ManagedDoom KeyBinding, done/step142).

Порт upstream `Array<T>` (`std/no-runtime/shared/Runtime/ArrayT.cs`:
IEnumerable<T>/ICollection<T>/IList<T>/IReadOnlyList<T> + ArrayEnumerator)
делает массивы честными интерфейсными источниками на обоих тирах: LINQ по
массиву, `T[]` в `IEnumerable<T>`/`IReadOnlyList<T>`-параметр — работают.
Mutating-поверхность (`Add/Remove/Insert/RemoveAt/Clear`) бросает
NotSupportedException — BCL-контракт fixed-size коллекции.

**Perf-примечание:** наш `Enumerable` дополнительно несёт array-специфичные
перегрузки (`Select/Where/Contains/.../Min/Max(this T[] ...)`, step142) —
они биндятся раньше интерфейсной формы и обходят диспатч+боксинг совсем.
Семантика идентична; это ускорение, больше не workaround.

**✅ String — НЕ массив: `IEnumerable<char>` работает (step 141).** В отличие
от массивов, `string` — обычный класс: интерфейс объявлен на типе
(`SystemString.Enumerator.cs`), ILC кладёт его в DispatchMap, рантайм-диспатч
резолвится. `s.Last()` / `s.FirstOrDefault()` / `s.All(c => ...)` компилятся
и работают. `foreach (char c in str)` идёт мимо enumerator'а (Roslyn лоурит в
`Length` + `get_Chars` — индексатору обязателен `[IndexerName("Chars")]`,
иначе CS0656).

Repro: `OS/src/Kernel/Diagnostics/NativeAotProbe.cs` —
`Probe_ArrayCovariance` тестирует только positive monomorphic case;
negative throwing-test намеренно пропущен (silent corruption убил
бы probe runner). CoreCLR-side `work/normal-hello` Sec 8 тестирует
оба варианта (positive + ATME-throwing).

### ✅ `[ModuleInitializer]` работает

```csharp
public static class MyInit
{
    public static bool Ran;
    [System.Runtime.CompilerServices.ModuleInitializer]
    public static void Init() => Ran = true;
}
```

C# 9 `[ModuleInitializer]` — атрибут добавлен в std step 119
(`std/no-runtime/shared/Runtime/RuntimeAttributes.cs`). Roslyn нашёл
type по name, ILC dispatch'ит до user code. Verify через
`NativeAotProbe.Probe_ModuleInit` (флаг должен быть true к моменту
probe-run'а).

---

## 5. Delegates / Lambdas

### ✅ `yield return` — работает

```csharp
IEnumerable<int> Foo() { yield return 1; yield return 2; }
```

Долго значилось как «Roslyn крашится на iterator rewrite»: переписыватель
итераторов ищет опорные типы **по имени и сигнатуре** через `.Single()`, и на
отсутствующем получал `Sequence contains no elements` — то есть падал сам
компилятор, до ILC. Не хватало трёх: `Interlocked.CompareExchange`,
`Environment.CurrentManagedThreadId`, `InvalidOperationException(string)`.
Все три лежат в `std/no-runtime/shared/Threading.cs`, и с ними `yield`
компилируется и исполняется обычным порядком (generic-итераторы тоже —
на них стоит mini-LINQ, step134).

Урок общего свойства: отказ переписывателя означает «не нашёл тип», а не
«среда не тянет». Список недостающего компилятор называет сам, по одному за
попытку.

### ✅ Managed delegate + lambda — РАБОТАЕТ (step 131)

```csharp
Func<int,int,int> f = (a,b) => a+b;   // static/open-static thunk
Func<int,int> g = adder.Add;          // closed-instance thunk
Action both = a1 + a2;                 // multicast (Delegate.Combine)
Array.Sort(arr, (a,b) => a.CompareTo(b));  // non-capturing lambda + Comparison<T>
```

`System.Delegate`/`MulticastDelegate` завендорены из dotnet/runtime v8.0.27
(точный layout `m_firstParameter`/`m_helperObject`/`m_extraFunctionPointerOrData`/
`m_functionPointer`, полный `Initialize*` набор, `GetThunk` 0-5). Работают: static
метод-группа, closed-instance, multicast (Combine/Remove/GetInvocationList),
GC-survival (GCDesc делегатного типа трассирует `m_firstParameter`/`m_helperObject`),
non-capturing лямбды (`<>c` синглтон), `Array.Sort` с `Comparison<T>`. Боевая
валидация — оригинальный Iced `BlockEncoder` (`Array.Sort(blocks, (a,b)=>...)`).

**❌ Вырезано → `NotSupportedException`:** reflection-поверхность
(`Delegate.Method`/`DynamicInvoke`/`CreateDelegate`, всё через `MethodInfo`),
`GetFunctionPointer`, GVM / delegate-to-interface Initialize, open-instance
invoke, variance-cast (`Func<string,bool>`→`Func<object,bool>`). Serialization
(`[Serializable]`/`GetObjectData`) — тоже нет. Доделка по первому потребителю.

**`delegate* unmanaged<T>` / `delegate*<T>`** (IL function pointers) — работают
как и раньше, отдельный механизм (не требует Delegate-инфраструктуры).

### ❌ Делегат на **интерфейсный** метод

```csharp
IAsyncStateMachine box = ...;
Action a = box.MoveNext;        // рантайм: delegate to interface method not supported
Action b = () => box.MoveNext();  // обход: обычный интерфейсный вызов внутри замыкания
```

Связывание делегата с интерфейсным слотом не поддержано, хотя сама
интерфейсная диспетчеризация работает полностью. Собирается молча, падает при
исполнении — то есть ошибка рантаймовая, а не компиляторная, и в отладке видна
только по сообщению. Правило: если приёмник делегата объявлен интерфейсом,
пишем лямбду. Всплыло в построителе асинхронных методов (§5.1).

**App-tier (freestanding PE): те же файлы с step 141.** FreestandingPe.props
компилит тот же `Delegate.cs`/`MulticastDelegate.cs`/`ActionFunc.cs` (плюс
LINQ/StringBuilder/полный Bcl-набор) — app-std теперь зеркалит kernel-список
OS.csproj, tier-specific остались только backend'ы (GcMemorySource.AppStatic,
StringRuntime.RhNewString, DebugOutput.AppHost). Матрица делегатов и все
вырезки выше применимы к аппам as-is (боевой потребитель — ManagedDoom,
done/step141.md).

### ✅ `Task` / `async` / `await` — работают

`Task.Run`, `Task.Delay`, `Wait(token)` и полноценный `async`/`await` собираются
и исполняются на обоих AOT-ярусах (проба ядра `TaskProbe` → `async=3/3`,
батарея приложения `AotTests` 42/42). Поддержка — `std/no-runtime/shared/`:
`Threading.Tasks.cs` (задачи), `Threading.Tasks.Await.cs` (`TaskAwaiter`,
`AsyncTaskMethodBuilder`, `IAsyncStateMachine`), плюс подложка потоков на ярус
(`KernelScheduler` / `AppServices`).

Это **не планировщик**, но с пулом (step174, `TaskPool`): потоки ждут работу,
новый стартует, только когда есть очередь, нет свободных и никто не стартует —
бесконечный цикл Terminal.Gui держит свой поток, не задерживая очередь.
`Task.Wait`, `ManualResetEventSlim`, `Monitor` блокируются в ядре
(`WaitOnAddress`/`WakeByAddressAll`; приложениям — службы в хвосте таблицы),
а не опрашивают флаг через `Sleep(1)`. Ноутбук: 0.3 мкс на задачу на тёплом
пуле (было 307 мкс при 100 Гц); первые задачи приложения — ~1.5–2 мс на старт
пула, причина не найдена. Очереди продолжений нет: `await` незавершённой задачи
занимает поток пула.

**Ограничения по замыслу:**

- продолжение исполняется на потоке, **завершившем ожидание**; контекст
  синхронизации не захватывается. Коду, рассчитывающему вернуться в свой
  поток, этого недостаточно;
- `ThreadPool.QueueUserWorkItem` не реализован.

**Две ловушки, стоившие отладки.** Построитель и машина состояний — **структуры**,
и компилятор копирует их свободно: возобновление копии теряет всё после первого
ожидания, а ленивое создание задачи даёт **две** задачи (ждём одну, завершается
другая). Всё, что обязано пережить копирование, держим за одной ссылкой, а
машину упаковываем один раз при первой приостановке. Вторая — делегат на
интерфейсный метод, см. выше.

Отдельно стоит записать, почему фронт так долго считался закрытым: он был не
сломан, а **заслонён**. Без `Task` асинхронный метод падал на типе возврата
раньше, чем включался переписыватель, поэтому список недостающего не был виден
никогда. Появился `Task` — и компилятор назвал требования сам, по одному.

---

## 6. Exceptions

### ✅ `try` / `catch` / `finally` / `when`-фильтр / `throw` — работают

Полный конвейер закрыт в step90 (Phase D; C++ catch по FH3 — только с step172, §15): personality-функции FH3/FH4,
раскрутка по `.pdata`/`.xdata`, funclet-и, фильтры, аппаратный сбой →
управляемое исключение (`#PF` → `NullReferenceException`, `#DE` →
`DivideByZeroException`), `Exception.StackTrace` через обход `.pdata`.

Практическое следствие для портов из BCL: **бросать по-настоящему**.
`ArgumentException`, `InvalidOperationException` и прочие пишутся как в
оригинале, а не подменяются на `Panic.Fail`. Halt остаётся только для
невосстановимых путей самого ядра.

### ⚠️ `ThrowHelpers` в приложениях — всё ещё заглушка

В `apps_native/sdk/MinimalRuntime.cs` `Internal.Runtime.CompilerHelpers.ThrowHelpers`
остаётся halt-only копией: сгенерированный ILC-ом бросок при выходе за границы
массива или переполнении **в приложении** даёт остановку, а не исключение.
Явный `throw` в коде приложения при этом работает — конвейер общий с ядром
(step139/140: приложения одалживают рантайм ядра через таблицу служб).

---

## 7. Equality / Hashing

### ✅ `EqualityComparer<T>.Default` работает для value-типов через IEquatable<T>

`DefaultComparer<T>.Equals(x, y)` сначала пробует `x is IEquatable<T> eq ? eq.Equals(y)`. Для value-типов boxing x происходит один раз (для iface-cast), но сам compare идёт через типизированный `Equals(T)` без boxing обоих операндов. Для reference-типов, не реализующих `IEquatable<T>`, fallback на `Object.Equals` (reference-equality по умолчанию; override на пользовательском классе для value-equality).

Наши примитивы (Int32/UInt32/Int64/UInt64/Byte/SByte/Int16/UInt16/Boolean/Char) реализуют `IEquatable<T>` + перегружают `Equals(object)`/`GetHashCode` — паттерн из NativeAOT Runtime.Base's Primitives.cs с recursive `private T _value;` field.

Что **не** покрыто:
- `Single`, `Double` — нет IEquatable. Floating-point equality редко нужен как ключ Dictionary.
- `IntPtr`, `UIntPtr` — нет IEquatable.

Добавить по потребности.

---

## 8. Прочие известные рабочие паттерны

Подтверждено через probe, работает "как в обычном C#":

| Паттерн | Комментарий |
|---|---|
| `virtual`/`abstract` dispatch | Без ограничений |
| Interfaces | Дженерик интерфейсы тоже работают |
| Generic methods и classes | `new T[n]`, `Box<T>.Value` и т.д. |
| `is`/`as` | Для reference-типов |
| Boxing/unboxing | `object o = 5; int x = (int)o` |
| `Array.Length` | После того как добавили `public readonly int Length` в `System.Array` |
| `enum` + bit flags | Включая `[Flags]` |
| `checked { ... }` arithmetic | Работает (overflow throw идёт через ThrowHelpers, т.е. halt) |
| `foreach` через struct Enumerator | Duck-typing, без IEnumerator boxing |
| `foreach` через `IEnumerable<T>` | Interface dispatch работает — ключевой тест для LINQ-compat |
| `fixed (T* p = &x)` на примитивах/strings | Работает в unsafe |
| `stackalloc T[n]` для unmanaged T | Работает |
| Zero-init on allocation | **Починили** в `GcHeap.AllocateRaw` (step 31). До этого был silent bug |
| `new string(char[])` / `(char[], int, int)` | Работает (step 133). Были placeholder-ctor'ы без парного `Ctor`-метода → ILC codegen падал `Expected method 'Ctor' not found on type 'string'`. Добавлены `String.Ctor(char[])` / `Ctor(char[],int,int)` (FastAllocateString + fixed-fill), куда ILC редиректит `newobj`. Первый потребитель — `System.Text.Encoding` |
| `String.Trim/TrimStart/TrimEnd(char)` + `params char[]` | Работает (step 133). Раньше был только parameterless overload |
| `System.Text.Encoding` (`ASCII`/`Unicode`/`BigEndianUnicode`/`UTF8`/`Latin1`) | **Partial** (step 133, `std/.../Text/Encoding.cs`). Есть `GetString(ReadOnlySpan<byte>/byte[])` + `GetBytes(string)` + `GetByteCount`. Вырезано: Encoder/Decoder-fallback (invalid → `?`/U+FFFD, не throw), `EncodingProvider`/`GetEncoding(name)`/code-page registry, preamble/BOM, streaming `GetEncoder`/`GetDecoder`. Static-факторки (`Encoding.ASCII` и пр.) — factory property (свежий инстанс на вызов, stateless), НЕ кешированное static-поле (cctor-trap §1) |
| `Dictionary<K,V>` порядок перечисления | Порядок **вставки**, как в BCL (step 164). До этого хранилищем был `LowLevelDictionary` — цепочки в корзинах, перечисление в порядке хеша; `TreeView` из Terminal.Gui рисует корневые узлы в порядке перечислителя, и отсортированный список выходил перемешанным. Порт хранилища из dotnet/runtime v8.0: корзины индексов над плотным массивом записей, знаковое кодирование free-list, пересборка цепочек при росте (`Bcl/Dictionary.Storage.cs`) |
| `System.Runtime.Intrinsics.Vector128/256` | **Работает** (step 165). Порт из dotnet/runtime v8.0 в наш std; ILC узнаёт интринсики **по пространству имён и имени типа**, поэтому это единственное место, где правило именования уступает — имя и есть механизм. Три условия, каждое стоило прогона: `[Intrinsic]` обязан висеть и на ТИПЕ (иначе структура раскладывается как два `ulong`), сигнатуры обязаны совпадать с оригиналом дословно (перегрузка под `ushort` там, где в CoreLib `Equals<T>`, никогда не станет инструкцией), и ILC подменяет каждый член отдельно (операторы `-`/`~` без атрибута остались managed). Запасные пути сравнений/вычитания ОТКАЗЫВАЮТ, а не считают: не зная знаковости элемента, они бы гадали. `Vector256.IsHardwareAccelerated` = false — AVX не в целевом наборе, состояние регистров через переключение контекста не сохраняется |
| `ArrayPool<T>` | **Partial по скорости, полный по контракту** (step 165). Не хранит ничего: `Rent` выделяет новый массив, `Return` отпускает. Контракт это позволяет (`Rent` обязан вернуть массив НЕ МЕНЬШЕ запрошенного, тождество не обещано), поэтому имя каноническое. Потеряна ровно та скорость, ради которой тип существует |
| `typeof(T)` / `System.Type` | **Работает в приложениях с step175, в ядре нет.** Тип — это указатель на MethodTable, `==` сравнивает указатели; два объекта, описывающих один тип, равны, не будучи одним объектом — на этом держится равенство `record`. Нужны три вещи разом: `Type.GetTypeFromHandle` (`apps_native/sdk/MinimalRuntime.cs`), `Internal.Runtime.CompilerHelpers.LdTokenHelpers` (ILC ищет по имени в системном модуле) и `IsExternalInit` (без него не компилируется ни один `init`-аксессор, а значит и ни одна запись). Отражения нет и не будет без метаданных: ни `Name`, ни членов, ни `Type.GetType(string)`. В ядре `Type` остался пустой заглушкой — `typeof` там не используется |
| `record` / `init`-аксессоры | **Работают в приложениях с step175.** Следствие строки выше: компилятор генерирует `EqualityContract => typeof(X)` и сравнение типов. До step175 попытка собрать любую запись давала `CS0656 GetTypeFromHandle`/`op_Equality`, а `init` — `CS0518 IsExternalInit`. Первый потребитель — `vendor/ShellSyntaxTree`, построенный на записях целиком |
| `Enum.IsDefined(Type, object)` | **Заглушка: всегда `true`** (`apps_native/sdk/MinimalRuntime.cs`, step175). Правдивый ответ требует списка объявленных значений, то есть метаданных перечисления, которых ILC нам не эмиттит. Из трёх способов быть неправым выбран наименее плохой: бросок уронил бы каждого вызывающего, `false` отверг бы корректные данные, `true` верен для любого значения, которое программа сама же и построила. Неверен для значения, подделанного приведением или прочитанного извне — и этого не заметит |
| `ConditionalWeakTable<K,V>` | **Partial: ссылки сильные, не слабые** (`std/.../Runtime/ConditionalWeakTable.cs`). Слабых дескрипторов нет ни у одного из сборщиков, поэтому запись живёт, пока жива таблица, — ключ не освобождается. Поиск по тождеству ссылки, как и положено: хеш — адрес объекта (законно, никто не двигает объекты), потому что `Dictionary` по умолчанию звал бы `Equals` записи, и два разных, но равных узла слились бы в один. Годится для побочных данных на короткоживущих объектах (разбор одной команды); для долгоживущих или многочисленных — течь. Настоящее лечение — слабые дескрипторы в сборщике. Первый потребитель — `vendor/ShellSyntaxTree` |
| `Span<T>.ToString()` / `ReadOnlySpan<T>.ToString()` | **Починили** (step 165). Возвращал `null`, что ломает контракт `object` и читается потребителем как «значения не было» — так терялось имя приложения из разобранного манифеста. Для символов возвращает содержимое; тип элемента различается по РАЗМЕРУ, потому что `typeof` в std не используется нигде (спан `ushort` поэтому отрисуется текстом) |
| `char.IsBetween` / `IsAscii*` (.NET 7 API) | Работает (step 165): `IsBetween`, `IsAsciiDigit`, `IsAsciiLetter`, `IsAsciiLetterOrDigit`, `IsAsciiHexDigit`, `IsAscii` |
| `IsExternalInit`, `SkipLocalsInitAttribute` | Работают (step 165). Типы-маркеры, реализовывать нечего — под каноническими именами без оговорок |
| Разбор XML | **Работает** (step 165, `vendor/TurboXml`, BSD-2-Clause). SAX: обработчик — структура, значения приходят спанами, аллокаций нет. Правок в библиотеку 176 строк, из них 145 — изъятия (потоковые перегрузки, тела методов по умолчанию); вырезов SIMD ноль. Потребитель — чтение манифеста приложения из `RT_MANIFEST` |
| `List<T>.ToArray()` | Работает (step 135, instance-метод как в BCL). Раньше не было → `list.ToArray()` в vendored-коде без `using System.Linq` не резолвился. Instance-метод приоритетнее LINQ-extension |
| `System.Linq` LINQ-to-objects | **Partial** (step 134, `std/.../Linq/Enumerable.cs`). Lazy yield-операторы (Where/Select/SelectMany/Skip/Take/Concat/Distinct/Reverse/Cast/OfType/OrderBy) + материализующие (ToArray/ToList/ToDictionary/Count/Any/All/First/Last/Single/ElementAt/Contains/Aggregate/Sum/Min/Max/Average). OrderBy — стабильный merge-sort. **Source ОБЯЗАН быть `List<T>`/`IEnumerable<T>`, НЕ голый массив** (§4 array-IEnumerable). Deferred: ThenBy/`IOrderedEnumerable`, GroupBy, Join, Zip, Union/Intersect/Except, nullable-numeric aggregates. Generic yield-итераторы (`Where<T>`/`Select<T,R>`) РАБОТАЮТ |

---

### ✅ PeNet native-PE parsing работает (step 133) — реальный сторонний парсер на нашей std

Milestone-1 срез [PeNet](https://github.com/secana/PeNet) (Apache-2.0, `vendor/PeNet/`, glob в OS.csproj как `vendor/Iced/`) парсит PE32+ на bare metal: DOS → NT → optional header → 16× data directory → section table. 16/16 probe-проверок зелёные (`Probe_PeNet`, категория `[PeNet]` в `probe_report.ps1`).

Обкатывает разом: GC-alloc (`byte[]`/`char[]`/`string`/`List<T>`), `Span`/`ReadOnlySpan`+ctors, `MemoryMarshal.Read<T>`, virtual/interface dispatch (`SafeParser<T>`, `IRawFile`), делегаты (`Array.Sort(sh, Comparison)` — method-group→`Comparison<T>`, step 131), try/catch (`SafeParser`), новый `Encoding`, `new string(char[])`.

**Форк/cut** (см. `PeNet/PROVENANCE.md`): `BufferFile` перенесён с `Memory<byte>` на `byte[]` (нет `Memory<T>`/`System.Range` в std); косметические `*Resolved` хелперы обрезаны (`Enum.GetValues<T>()` требует reflection-метаданных — их нет; `static readonly Dictionary = new(){...}` = cctor-trap → factory property).

**Phase 2 (step135):** imports / exports / base relocations завендорены (разблокировано mini-LINQ — парсеры юзают `List<T>.ToArray`/`.Last`). Парсеры инстанцируются напрямую (минуя `DataDirectoryParsers`). `RvaToOffset` перетипизирован на `ImageSectionHeader[]` (upstream `ICollection<T>` + `.ElementAt` упал бы на массиве — см. §4). Синтетический PE с import/export/reloc таблицами парсится 6/6. Осталось cut: TLS/Debug/LoadConfig (`Marshal`), .NET-метаданные, ресурсы, authenticode.

### ✅ PE-loader stage 1-3 (step136) — flatten / relocate / bind IAT

`OS/src/Kernel/Pe/` — kernel-side PE-loader как **чистые трансформы над byte[]** (страницы/исполнение НЕ трогаются — самый безопасный инкремент; финальный map-into-pages + jump будет с реальным PE-аппом):

- **`PeImageLayout.TryFlatten`** — raw PE → in-memory image layout (SizeOfImage-буфер: headers@0 + секции по RVA, BSS/паддинг = 0).
- **`PeRelocations.TryApply`** — base relocations по `.reloc` (DIR64/HIGHLOW, `delta = actualBase - preferredBase`); no-move → no-op, moved-без-reloc → fail.
- **`PeImports.TryResolve`** — парсит импорты по **файлу** (RvaToOffset→file-offset), биндит IAT-слоты **flattened-образа** через `resolver(ImportFunction)→addr`. PE32 (32-битные слоты) пока не поддержан.

16/16 probe (`Probe_PeLoad`/`PeReloc`/`PeImports`, категория `[PeLoad]`).

### ✅ PE-loader execute + freestanding PE build (step137) — лаунчер РАБОТАЕТ

**Kernel execute-side:** `PeLoader.TryLoad(MemoryBlock)` — flatten → map contiguous phys→VA@ImageBase (RWX) → blit → `LoadedImage` → тот же `ProcessImageBuilder`+`JumpStub`. Проверка `MZ` в `AppServiceBuilder` (не PE — `Unsupported`); лаунчер при загрузке грузит `LauncherBoot` (до step171 — `ElfValidation`). **JumpStub entry-ABI**: startup block кладётся в **RCX** (Win64 arg0), не RDI (SysV-legacy от ELF) — иначе win64 PE-апп читает адрес entry как startup → #GP.

**Build-side (без WSL):** freestanding win-x64 PE через `dotnet publish -r win-x64` (рецепт в app-csproj, gated win-x64): `/ENTRY:SharpAppBootstrap /SUBSYSTEM:NATIVE /BASE:0x400000 /FIXED /NODEFAULTLIB` + снятие SDK-рантайма (`ExcludeNativeAotRuntime`: `Runtime.WorkstationGC`/`VxsortEnabled`/`bootstrapper.obj`) + `DebuggerSupport=false` + `IlcDehydrate=false` + `__security_cookie` через CoffStub.Generator + `__managed__Startup` no-op стаб. Апп на net8.0. HelloSharpFs исполнен на bare metal, TUI-файлпикер видит `.EXE`, self-launch с nested-лимитом. ELF выпилен из app-batch (kernel ELF-файлы — Stage B, пока живут unused).

### ✅ Вложенные запуски приложений — до 4 уровней (step175)

Было 1: приложение, запущенное ядром, могло стартовать ребёнка, а тот — уже
никого. Корень был не в стеке и не в счётчике: `ProcessManager.SetCurrent`
звался **только** из `LauncherBoot`, поэтому «текущий процесс» всегда описывал
самое внешнее приложение. На втором уровне ядро сняло бы отображение опять у
внешнего, а образ настоящего родителя остался бы лежать там, куда грузится
внук — все приложения собраны с `/FIXED` на одном `ImageBase 0x400000`.

Теперь `RunExternalApp` объявляет ребёнка текущим на время его работы и
забирает вытесненный контекст к себе на кадр: вложенный прогон живёт внутри
кадра родительского `RunExternalApp`, так что стеком контекстов служит стек
ядра, отдельной структуры не нужно. Стек приложения — по области на уровень
(`ProcessImageBuilder.StackMappedTopForDepth`, шаг `0x40_0000_0000`), причём
глубины 0 и 1 сохранили прежние адреса. Предел 4
(`AppServiceBuilder.MaxNestedLaunchDepth`): каждый уровень стоит области
адресов и живого кадра на стеке ядра.

Проверено: лаунчер → оболочка → лаунчер → оболочка, выход через `exit` и Esc,
12 запусков и 12 завершений парные.

### ⚙️ PE-app tier — общий SDK + тест-батарея (step138), и его REDUCED runtime

Общий рецепт вынесен в `apps_native/sdk/FreestandingPe.props` (импортится каждым app-csproj — тот несёт только `IlcSystemModule` + свои исходники). `FetchApp` мигрирован на PE (первый non-launcher PE-апп). Новый `apps_native/AotTests/` — батарея из 14 проверок app-рантайма (`new object()`/`new int[]`/`new string(char[])`/string-ops/`List<T>`/`Dictionary<K,V>`/`EqualityComparer<T>`), печатает `ok`/`FAIL` + exit=pass-count. Build-скрипты PE-only: `build_launcher.ps1` (был `_win`), `build_fetch.ps1`, `build_aottests.ps1`; WSL/ELF-скрипты удалены.

**Батарея прошла границу app-tier рантайма: 20/20 (step139 — interface dispatch, step140 — исключения).** Freestanding PE-апп несёт **минимальный** рантайм (GC + прямые вызовы); interface dispatch **и** managed EH разблокированы через **handoff + shared kernel-движок** (см. ниже). Tier-B halt-on-throw снят для PE-аппов.

| App-tier операция | Статус | Механизм |
|---|---|---|
| `new object/int[]/string(char[])`, string concat/eq/PadRight, `List<T>` add/index/count/ToArray, `Dictionary` add/count/missing-key | ✅ | Прямые вызовы + GC |
| `List<T>.Contains`, `Dictionary.TryGetValue`, `EqualityComparer<int>.Default.Equals` | ✅ (step139) | `DefaultComparer.Equals` → `x is IEquatable<T>` (isinst) + `eq.Equals(y)` (dispatch) через shared kernel-мост |
| `throw`/`catch`, `try/finally`, finally-on-unwind, catch-by-base, `e.Message`, multi-catch | ✅ (step140) | app `throw` → kernel `RhpThrowEx` (handoff) → `DispatchEx` идёт по кадрам аппы через её `.pdata` (multi-image function-table) → апп catch/finally funclet |
| `GC.Collect` при нескольких потоках | ✅ (step169) | `AppGC` берёт у ядра обход корней: стек собирающего потока, стеки остальных (`KernelGC.MarkOtherThreadStacks`) и сквозь переходники служб (`TryUnwindServiceThunk`) — спящий поток стоит внутри `Sleep`. До step169 объект, живой только у другого потока, освобождался. AotTests «other thread's stack roots survive collect». **step175:** обход читал только прерываемые диапазоны `GcInfo`, а таблицу точек безопасности пропускал не читая — кадр на адресе возврата (то есть почти любой кадр обхода) объявлялся неразметимым, и его локальные ссылки подметались живыми. Добавлены `FindSafePoint` + разбор набора живых слотов (косвенная таблица с RLE и простая битовая карта) и сдвиг смещения на −1 внутрь инструкции `call`. Касается обоих сборщиков — обходчик общий |
| Вывод строками | ✅ (step169) | запись, кончающаяся `\n`, не рисует экран сама (перевод строки рисует не чаще 60/с, остальное — помпа или `TryReadKey`); прочие записи (кадры интерфейса) рисуют сразу. 0.43 мс на строку под QEMU, как у hosted |
| Поток ошибок (stderr) | ✅ (step167) | `AppHost.WriteError(string)` → служба `WriteErrorAddress` → канал `AppErr` (COM4 / `last_err.log`); без службы — обычный вывод. `System.Console.Error` нет: в std нет `TextWriter` |

**Interface dispatch в аппах — как закрыто (step139, handoff, НЕ export-table):** разведка показала, что kernel-resolver (`InterfaceDispatchResolver`) **пурный на major-9** — резолвит целиком из MT+cell, работает на foreign app-MT (тот же ILC8/major-9 layout в shared-адресном). Плюс апп исполняется **внутри адресного пространства ядра**. Поэтому НЕ портируем машинерию и НЕ строим export table: ядро **отдаёт адрес** уже построенного bridge-шеллкода (`InterfaceDispatchBridge.ShellcodeStart`) через `AppServiceTable.InterfaceDispatchBridgeAddress`, а апп патчит свой `[RuntimeExport("RhpInitialDynamicInterfaceDispatch")]` стаб абсолютным `mov rax,<addr>; jmp rax` (`InterfaceDispatchTrampoline`, вызывается в `AppRuntime.Initialize`). Kernel-шеллкод читает cell аппы → зовёт kernel-resolver → ходит по MT аппы → tail-jmp в метод аппы. **Второй корень:** примитивы app-std были голые (`struct Int32 { }`) → int не IEquatable<int> → `x is IEquatable<T>` = false → dispatch не звался; исправлено — примитивы аппы реализуют `IEquatable<T>`/`IComparable<T>` (зеркало `OS/src/Boot/MinimalRuntime.cs`).

**EH в аппах — как закрыто (step140, multi-image function-table + throw handoff):** managed-EH-путь (`RhpThrowEx`→`DispatchEx`→`StackFrameIterator`/`CoffEhDecoder`) был **single-image** — `CoffRuntimeFunctionTable` зашит на kernel-образ, кадры аппы (RVA от 0x400000) не находились (`FindRecordIndex`→-1). Сделано:
1. **Multi-image реестр** в `CoffRuntimeFunctionTable` (`RegisterImage`/`UnregisterImage`/`TryResolvePc`/`ImageBaseForRecord`) — kernel остаётся image 0 (все kernel-only и SEH-потребители не тронуты), аппы — доп. образа. `MethodInfo`/`EHEnum` несут `ImageBase`; managed-путь (`CoffMethodLookup`/`CoffEhDecoder`/`StackFrameIterator`/`CoffMethodGcInfo`) резолвит per-PC образ. Инвариант регрессии: extras=0 → идентично старому пути (ядро 16/16 EH-проб зелёные весь рефактор).
2. **PeLoader** парсит data-dir #3 и `RegisterImage(imageBase, records, count)`; `UnregisterImage` на teardown (`UnmapMappedRange`).
3. **`RhpThrowEx` handoff** — адрес kernel-`RhpThrowEx` (`ThrowExStub.GetMethodAddress`) в `AppServiceTable.RhpThrowExAddress`; апп tail-jmp'ит свой стаб (`ThrowExTrampoline`). `ExInfoHead.s_head` — kernel-глобал (ок при single-thread ExInfo). Матч catch-типа (`IsAssignableFromClass`) — pure MT-identity, app-safe.
4. **`System.Exception` + `Exceptions.Derived.cs`** в app Compile list.

Батарея AotTests 20/20 (6 EH-кейсов). **Отложено:** `RhpRethrow` handoff (`throw;`), `RhpThrowHwEx` (HW-fault→managed в аппе), rich stack-trace (`AppendStackFrame` аллоцирует в kernel-heap → cross-heap ref, латентно), конкурентный throw kernel↔app (single `s_head`).

---

## 9. Self-modifying code: serializing instruction missing (QEMU-only correct)

### ⚠️ Patchers не делают pipeline flush после записи shellcode

**Симптом:** на QEMU TCG работает; на реальном x86 железе CPU может выполнить **stale prefetched bytes** функции, в которую только что записали shellcode. QEMU TCG re-reads инструкции каждый раз (interpreter), реальный CPU prefetches.

**Затронуто:** все patcher'ы в проекте, которые пишут shellcode в `.text` и **скоро** вызывают результат:

- `OS/src/Kernel/Memory/ByRefAssignRefPatcher.cs`
- `OS/src/Kernel/Memory/InterfaceDispatchPatcher.cs`
- `OS/src/PAL/SharpOSHost/ChkstkPatcher.cs`
- `OS/src/Hal/PortIoPatcher.cs`
- `OS/src/Boot/EH/CaptureContextPatcher.cs`
- `OS/src/Boot/EH/ThrowExPatcher.cs`
- `OS/src/Boot/EH/CallCatchFuncletPatcher.cs`
- `OS/src/Boot/EH/RethrowPatcher.cs`
- `OS/src/Boot/EH/CallFinallyFuncletPatcher.cs`
- `OS/src/Boot/EH/CallFilterFuncletPatcher.cs`
- `OS/src/Boot/BootStackSwitchPatcher.cs` (step104) — особо опасен, патчит и **сразу** вызывает.

**Корень:** Intel SDM Vol 3 §8.1.3 «Self-Modifying Code» требует serializing instruction (`cpuid` / `wbinvd` / `mfence`+`lfence` / cross-modify protocol) между записью байт и их execution. Без этого CPU может выполнить cached/prefetched stale bytes.

**Fix (отложено):** добавить `X64Asm.SerializeCpu()` (выполняет `cpuid` с eax=0) и вызывать после каждого patcher'а перед первым use. Альтернатива — `wbinvd` (тяжелее, но гарантия). Сейчас QEMU forgiving поведение скрывает баг.

**Поверхность нарушения:**
- Boot-time patchers — все вызываются один раз очень рано, типичный сценарий race'а минимальный (между write и call идёт много инструкций, prefetch успевает обновиться). Но **гарантии нет**.
- `BootStackSwitchPatcher` особенно — patch + immediate `fn(...)` через function pointer без задержки.

См. также аналогичную QEMU-vs-hardware проблему: `reference_aot_mmio_poll_hoist` (LICM с MMIO).

---

## 10. Kernel GC sweep — precise via NativeAOT GcInfo (step 110)

### ✅ `KernelGC.Collect()` теперь безопасно зовётся из любого места

Step 110 заменил conservative ScanStack на precise per-frame walker
который читает GcInfo blob который NativeAOT эмиттит на каждый
скомпилированный метод (в .xdata после UNWIND_INFO + EHINFO).

Pipeline:
1. **`GcContextSpill`** shellcode захватывает все 16 GP regs + Rsp + Rip
   в `Context*`, вызывает managed callback.
2. **`KernelGcPreciseWalk.RunFromCurrentFrame`** обходит frame'ы через
   `SehUnwind.VirtualUnwind` (применяет UNWIND_CODE'ы для размотки
   callee-saved + Rsp).
3. **`CoffMethodGcInfo`** для каждого PC находит RUNTIME_FUNCTION → 
   gcInfo blob.
4. **`CoffGcInfoDecoder`** декодит header + slot table + transitions,
   возвращает live slots at PC.
5. **`CoffGcInfoResolver`** для каждого live slot'а вычисляет pointer
   value (регистр через Context, stack — через Rsp+offset или
   FpBase+offset).
6. **`GcMark.MarkFromRoot`** обрабатывает каждый value с его
   существующими фильтрами (canonical, в heap, MT-not-in-heap).

`KernelGC.Collect()` по дефолту выбирает precise path когда
`KernelGcPreciseWalk.IsAvailable` (=`GcContextSpill` инициализирован
и `.pdata` смонтирована — то есть после Phase2). Раньше Phase2 (когда
ExecStubBuffer ещё не сконфигурирован) — fallback на conservative
ScanStack (только smoke-test-callers через `CaptureStackTop` discipline).

### ✅ Остальные потоки (закрыто; приложения — step169)

`KernelGC.MarkOtherThreadStacks` обходит каждый припаркованный поток от
контекста, оставленного переключением (`RunFromParkedThread`), а
вытесненный — дальше, через кадр прерывания (`RunFromInterruptFrame`).
С step169 тот же обход получает колбэк и отдаётся сборщику приложений
(`AppGcService.WalkRoots`).

Граница «приложение → ядро»: службы вызываются через переходники без
`.pdata` и GcInfo, и обход останавливался на первом из них — кадры
приложения у потока, спящего в `Sleep`, не обходились.
`AppServiceBuilder.TryUnwindServiceThunk` переступает переходник по его
форме. Любой новый обход, пересекающий эту границу (EH сквозь службу,
профайлер), должен делать то же.

Остаток: куча ядра на практике не собирается — растёт (`kgc.calls=0` во всех
прогонах step169).

---

## 11. Железо vs эмулятор: MMIO и HPET (step 149)

### ⚠️ Прошивка отдаёт HPET остановленным, но с выставленным ENABLE_CNF

**Симптом:** на железе `hpet=STUCK` — счётчик не двигается за 5M чтений. В QEMU/VBox не воспроизводится.

**Корень:** UEFI при teardown останавливает счётчик, **не** сбрасывая бит разрешения. `config |= 1` — no-op, запустить не может. Оживляет только цикл halt → counter=0 → enable (счётчик по спеке пишется только остановленным).

**Fix:** `Hpet.EnsureRunning()` — проверяет движение, один раз перезапускает. Зовётся из `Init` и **сразу после `ExitBootServices`** (до потребителей: AHCI-таймауты, `Stopwatch`, `TimerQueue`).

**Диагностика:** `[ebsx] hpet cfg0/cfg1/ctr/pte/caps` — `pte` бит 4 (PCD) отделяет stale-чтения от реально стоящего счётчика.

### ⚠️ MapFixed молча пропускал уже отображённые страницы

Firmware-унаследованные mapping'и (HPET, ECAM, framebuffer, ABAR) обходили `MapFixed` целиком (`if (TryQueryKernel(p)) continue;`) — атрибуты не применялись, включая бит записи. На 2 MiB-странице `TrySetKernelFlagsEx` кеш-биты не трогает (сиблинги могут держать код ядра) → нужен split.

**Fix:** `MapFixed(..., MemoryKind)` — `Normal` / `Device` (PCD|PWT) / `Framebuffer` (PWT). Для не-`Normal` флаги переустанавливаются принудительно; large page расщепляется через `Unmap` + `MapKernel`; после — `FlushTlbAll` + `wbinvd`.

**`wbinvd` обязателен:** пометить страницу uncached ≠ выбросить уже закешированное. Без него CPU продолжает отвечать из stale-строки. Стаб — `Cr3Accessor.TryInvalidateCaches()` (третий рядом с CR3 read/write, Iced+legacy compare). Это же даёт готовый serializing-инструмент для §9.

---

## 12. USB / xHCI (step 150)

### ✅ Свой xHCI-стек: клавиатура как системный ввод, флешка как Disk

Красть неоткуда: в донорах из `gc-experiment/usb/` **ноль** файлов с xHCI (все
микроконтроллерные). Транспорт написан по спеке; для сверки годится Haiku (MIT),
Linux `xhci*.c` — табу (GPL).

**Работает:** передача владения от прошивки (до неё порты обслуживает SMM и
перетирает наши записи), сброс, кольца команд/событий, scratchpad, сброс порта,
`Address Device`, управляющие передачи, HID boot-протокол → set-1 scancodes
(`ScancodeSource` — все 5 потребителей ввода не тронуты), BOT+SCSI → `UsbDisk`.

**Гочи:**
- MMIO-регистры читаются только на естественной ширине: `HCIVERSION` как ushort
  по +2 даёт 0 (QEMU не декодирует sub-dword) — читать dword целиком.
- Биты изменений `PORTSC` — write-1-to-clear: read-modify-write разрушителен.
- Порт USB 2.0 приходит connected, но disabled — обмена нет до сброса порта.
- Input context очищать перед `Configure Endpoint` (остатки от `Address Device`
  применятся повторно).
- HID шлёт **состояние**, не события: make/break восстанавливать диффом отчётов.
- Один запрос чтения в полёте на устройство, иначе выстрел пачкой.
- `-Usb` без `-NoPs2` уже ломал ввод: QEMU при `usb-kbd` шлёт клавиши туда, а
  шелл читал только i8042.

**Не поддержано:** прерывания (только опрос), хабы, мышь (кольцо событий одно на
все устройства — нужны очереди по слотам), передача > 64 KiB. На железе — §14, §15:
флешка за хабом не видна, система останавливается с сообщением о диске.

### 🟡 FAT32: создание файлов (только 8.3)

`Fat32.TryCreateFile` — цепочка кластеров + запись каталога. Только 8.3: reader
принципиально не фабрикует короткое имя из длинного (§ `feedback`/FAT-инвариант),
значит и writer не вправе. Запись зеркалится во **все** копии FAT; верхние 4 бита
записи FAT сохраняются; дата из CMOS (нулевые поля = месяц 0 → невалидно), эпоха
FAT = 1980, не `FILETIME`/1601.

**Нет:** удаления, роста файла и каталога, LFN, FSInfo.

---

## 13. Видеопамять — только для записи (step 151)

### ⚠️ Чтение из framebuffer'а может быть в 50 раз медленнее записи

Замер `FbPerfProbe` на трёх машинах:

| | fill | scroll (read+write) |
|---|---|---|
| QEMU | 2071 MiB/s | 3622 MiB/s |
| ноут | 264 | 622 |
| десктоп | 199 | **4** |

Прошивка размечает область как uncacheable через MTRR — это **сильнее** наших PTE,
`MemoryKind.Normal` там ничего не даёт. Симптомы: секундная развёртка терминала,
неиграбельный DOOM. В эмуляторе не воспроизводится (обычная RAM).

**Правило: любое «прочитать и переложить» внутри экрана — баг**, даже когда выглядит
оптимизацией. Было три места: прокрутка терминала (перенос пикселей), DOOM
(`rep[x] = dstRow[x]` при размножении строки — ~5 MiB чтений на кадр), `FbConsole.Checksum`
(оракул, помечен как последний читатель).

**Fix:** рисовать из своего состояния. Терминал перерисовывает знаки, DOOM собирает строку
в RAM. Выбор пути — **по измерению** (`FbPerfProbe.ScrollMibPerSecond < 100`), не по догадке.

**PAT/WC:** единственное направление, в котором MTRR-разметку можно ослабить — в сторону
write-combining (`MemoryTypes`, PAT-запись 1 = WC). Включать **только** там, где чтения
измеренно мертвы: WC ускоряет запись и добивает чтение. Сквозная запись (step 149) и WC
«всем подряд» — обе делали хуже, пока экран читался.

### ⚠️ GOP SetMode убивает раннюю консоль

Консоль прошивки привязана к её режиму; после `SetMode` она рисует в никуда, а нашей ещё
нет → немая загрузка без единой строки. Переключать **только** если текущий режим больше
предела (`Probes.DisplayMax*`).

`virtio-vga` в QEMU не годится: нет линейного буфера, кадр обновляется командой передачи —
post-EBS это развёртка. `bochs-display` + EDID.

---

## 14. PCI/USB на реальном железе (step 151)

- **Шины обходить через мосты**, не диапазоном: AMD-контроллер (`dev 0 func 3`) сидел за
  мостом вне 0..7 — устройства для нас не существовало. Плюс: все сегменты MCFG, границы
  шин сегмента, переполнение таблицы не обрывает скан.
- **Загрузочный носитель спрашивать у прошивки** (`BootMedium`, device path до EBS) —
  авторитетно, дешевле перебора.
- **Несколько xHCI одновременно**: на десктопе флешка и клавиатуры на разных контроллерах.
- **События xHCI фильтровать по слоту**: transfer event у клавиатуры и у диска одного типа,
  без фильтра нажатие завершает чтение с диска.
- **Дескриптор устройства — в две фазы** (8 байт → `Evaluate Context` → 18): иначе
  transaction error на железе.
- **`REQUEST SENSE` обязателен**: свежее USB-устройство не отвечает, пока не спросят причину.
- **Наличие контроллера ≠ наличие носителя**: в q35 AHCI есть всегда; критерий — читается
  ли LBA 0.

---

## 15. Железо: ноутбук и ПК (step 172)

- **Аллокатор при неудаче не отдаёт общий объект.** `FastAllocateString` до кучи возвращал
  замороженный `String.Empty` из .data; вызывающий писал в него на месте, вендор прошивки
  "American Megatrends" затёр литерал `"\n"`. Теперь: до кучи — `Panic`, неудача —
  `OutOfMemoryException`, отрицательная длина — `OverflowException`.
- **OOM как в NativeAOT**: исключение создаётся сразу после `GcHeap.Init`
  (`GcHeap.PrepareOutOfMemory`, ядро и PE-приложения); бросается свежее, при неудаче —
  заготовка. Потолок одного выделения — 0x7FFF0000.
- **Прошивка защищает образ**: новые AMI/EDK2 отображают код RO, данные NX — первая запись
  патчера фолтит в обработчик прошивки, экран чёрный. `UefiImageProtection` снимает защиту
  через `EFI_MEMORY_ATTRIBUTE_PROTOCOL`, без него — правкой таблиц страниц (CR0.WP снят).
- **Загрузочный диск — только названный прошивкой** (`BootMedium`: USB 3/5, SATA 3/0x12 с
  портом, NVMe 3/0x17). SATA — ровно тот контроллер и порт; NVMe и неизвестное — нет диска.
  Угаданный диск был разделом прошивки диска Windows. Без диска лаунчер и CoreCLR не
  стартуют, причина — одной строкой (`BootDisk.MissingReason`).
- **COM1 без чипа не трогать**: запись в отсутствующий порт на AM5 — ~99 мкс на знак.
- **FH3 C++ catch** (`__CxxFrameHandler3`) до step172 не ловил ничего: IP-to-state map —
  RVA образа, а не смещение в функции. Починен вместе с catch-фанклетами, `throw;`,
  исключениями из catch и деструкторами при размотке (`done/step172.md` §6).

---

## 16. Тик и ожидания (step 173)

- **Любое ожидание квантовано тиком** (`Probes.TimerHz`, 1000 Гц; было 100): разбуженный
  поток только встаёт в очередь и бежит, когда будивший заблокируется или придёт тик;
  простой — HLT до прерывания, а прерывание одно — тик; `Sleep(1)` = до следующего тика;
  активация для остановки потока под сборку доставляется на тике. При 100 Гц это давало
  фиксированные 10 мс там, где ждут: задачи, эхо клавиш.
- **Вытеснение включено только на время сессии CoreCLR при загрузке**
  (`ExitBootServicesProbe`: `Preemption.Enable()` … `Disable()` после
  `RunCoreClrSession`). Лаунчер и запущенные из него программы — кооперативно.
- **Профайлер сэмплирует на тике**: событие короче 1/TimerHz он не видит.
- **Потоки приложения умирают с ним** (step174): поток получает «поколение» запуска
  приложения (`Scheduler.EnterApp`), при возврате приложения оставшиеся снимаются с очереди
  готовых, таймеров и адресов (`LeaveApp`, в лог — `app threads ended with the app: N`).
  Раньше продолжали жить — в выгруженном коде или просыпались в следующем приложении по
  тому же адресу. Стеки не освобождаются (как у любого завершённого потока).
- **Засыпание атомарно с проверкой**: `AddressWait.WaitOnAddress` и `Scheduler.Sleep`
  ставят поток в очередь под `Suppress`; иначе тик между проверкой и постановкой теряет
  поток или пробуждение. Ожидание с таймаутом — сон на таймере, не цикл с `Yield`.
- **Подстройка частоты тика** правит только устойчивую ошибку (окна согласны) и проверяет
  правку: разброс окон — потерянные тики QEMU, их «исправление» раскручивало машину до
  47 тыс. прерываний в секунду.
- **Счётчики планировщика по прогону**: `sched.switches`, `sched.halt.*` (простой до
  прерывания), `sched.sleeps`, `sched.spawn.*`, `wait.address.*`, `wait.wakes`.

---

## Быстрый протокол при встрече новой проблемы

1. Добавить минимальный repro в `NativeAotProbe.cs`.
2. Запустить. Если `#GP` → смотреть RAX/RCX на crash site. Non-canonical high bits (`0xF...`, `0x08_F...`) — обычно ILC-emitted helper не найден.
3. Для помощника — искать в `dotnet-runtime/src/coreclr/nativeaot/` по имени класса из error message или по эвристикам (`Rhp*`, `Rh*`).
4. Если helper нельзя добавить (как ClassConstructorRunner) — переписать код под workaround, добавить пункт сюда.

---

*Этот документ обновляется при каждой новой найденной проблеме. Источник правды по части "что нельзя" в нашей среде.*
