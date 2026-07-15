# NativeAOT + NoStdLib (kernel tier): карта ограничений

Живой документ. Перечень managed-паттернов C#, которые **не работают** или работают с оговорками в **самом ядре SharpOS** (NativeAOT **8.0.27 / RTR major 9** + `NoStdLib=true` + наш `MinimalRuntime` без полной BCL; бампнут с 7.0.20 в step130 — вся батарея проб зелёная под ILC 8, кроме `EnumToString`).

**Область применения:** только kernel-side код (`OS/` дерево). У ELF-приложений своя поверхность ограничений (`apps/` через `apps/sdk/AppHost.cs` сервис-таблицу) — см. [`nativeaot-nostd-elf-limits.md`](nativeaot-nostd-elf-limits.md). У stock CoreCLR-hosted кода ещё другая поверхность — см. [`coreclr-hosted-limits.md`](coreclr-hosted-limits.md). Общий обзор всех трёх tier'ов с компаративной таблицей — в [`README.md`](../README.md).

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

### ❌ Multi-dimensional arrays

```csharp
int[,] m = new int[3, 4];   // ILC: Code generation failed
```

ILC требует `RhpNewMultiDimArray` helper которого у нас нет.

**⚠️ Workaround:** jagged arrays (`int[][]`) работают — это массив массивов одномерных.

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

### 🔧 `yield return` — Roslyn краш на iterator rewrite

```csharp
IEnumerable<int> Foo() { yield return 1; yield return 2; }
```

**Ошибка компиляции** (!): Roslyn внутри `IteratorRewriter.GenerateEnumeratorImplementation` зовёт `SyntheticBoundNodeFactory.New(...)` который через `Single()` ищет ctor по сигнатуре и получает `Sequence contains no elements` → `FailFast`. То есть **компилятор крашится** до ILC, полная трассировка видна в `Roslyn/Microsoft.CSharp.Core.targets(89,5)`.

Причина: Iterator-state-machine rewriter Roslyn ищет в окружении какой-то тип/ctor по канонической сигнатуре. Наши минимальные `IEnumerator<T>` / `IEnumerable<T>` интерфейсы технически есть, но Roslyn хочет ещё что-то (скорее всего `System.Threading.Interlocked.CompareExchange` для thread-ID serialization в state machine, или `System.Environment.CurrentManagedThreadId`, или generated closure base class).

**⚠️ Workaround (уже используется):** писать `Enumerator` классы явно, как в нашем `Dictionary<TKey, TValue>.Enumerator`, `HashSet<T>.Enumerator`, `SortedList<K,V>.Enumerator`. Это sealed class с полями для состояния + MoveNext/Current/Reset/Dispose + IEnumerator<T>/IEnumerator implementation. Boxing-lessness теряется (когда struct), но работает.

**Фикс-позже:** добавить недостающие stub-ы (`Interlocked.CompareExchange<T>`, `Environment.CurrentManagedThreadId`) и попробовать снова. Если Roslyn захочет ещё что-то — trace покажет какой именно тип/ctor ищется.

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

---

## 6. Exceptions

### ❌ `try { ... } catch (Exception e) { ... }` — не работает

Требует полноценный exception engine: personality function, unwind tables из `.eh_frame`, класс `Exception` с `Message/StackTrace/InnerException`, и пр.

### ⚠️ `throw new SomeException()`

ILC генерит bounds-check throws, overflow throws, null-ref throws через класс `Internal.Runtime.CompilerHelpers.ThrowHelpers` — у нас есть stub где каждый Throw\* делает `while(true)`. То есть **реальный** bounds check на выходе за пределы массива приведёт к halt-у, не к exception. Это не крэш — просто program hangs.

Для явного `throw new X()` в user-коде — compiler expects runtime helpers (`RhThrow`, unwind machinery), которых нет. Практически это значит: **нельзя писать `throw` в нашем managed коде**, надо делать error-codes или direct halt через Panic.

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

---

### ✅ PeNet native-PE parsing работает (step 133) — реальный сторонний парсер на нашей std

Milestone-1 срез [PeNet](https://github.com/secana/PeNet) (Apache-2.0, `PeNet/` в корне, glob в OS.csproj как `iced/`) парсит PE32+ на bare metal: DOS → NT → optional header → 16× data directory → section table. 16/16 probe-проверок зелёные (`Probe_PeNet`, категория `[PeNet]` в `probe_report.ps1`).

Обкатывает разом: GC-alloc (`byte[]`/`char[]`/`string`/`List<T>`), `Span`/`ReadOnlySpan`+ctors, `MemoryMarshal.Read<T>`, virtual/interface dispatch (`SafeParser<T>`, `IRawFile`), делегаты (`Array.Sort(sh, Comparison)` — method-group→`Comparison<T>`, step 131), try/catch (`SafeParser`), новый `Encoding`, `new string(char[])`.

**Форк/cut** (см. `PeNet/PROVENANCE.md`): `BufferFile` перенесён с `Memory<byte>` на `byte[]` (нет `Memory<T>`/`System.Range` в std); косметические `*Resolved` хелперы обрезаны (`Enum.GetValues<T>()` требует reflection-метаданных — их нет; `static readonly Dictionary = new(){...}` = cctor-trap → factory property). Вырезаны .NET-метаданные, ресурсы, authenticode/crypto, imports/exports (LINQ + `Marshal.PtrToStructure`) — фаза 2 после mini-LINQ.

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

### Известный остаток: multi-thread enumeration

Precise walker сейчас обходит **только текущий поток**. Если есть
другие threads (scheduler workers, hosted CoreCLR threads) с managed
refs на их стеках — те refs **не enumerate'ятся**. Sweep с включённым
`ReclamationDisabled = false` мог бы освободить такие thread-local
managed objects.

Поэтому `GC.ReclamationDisabled = true` остаётся включённым в
`CoreClrProbe.cs:370` — sweep no-op. Mark phase теперь precise и
безопасный, но **реальная reclamation отложена** до multi-thread
walker'а.

**Долгосрочный фикс:** enumerate каждый Thread в `Scheduler.Threads` +
hosted-CoreCLR thread list, для каждого: capture его CONTEXT (или
прочитать сохранённый CONTEXT из его `ThreadContext` блока) → walk
frame chain → mark roots. Тогда snять `ReclamationDisabled` будет
безопасно и kernel GC начнёт реально освобождать память.

---

## Быстрый протокол при встрече новой проблемы

1. Добавить минимальный repro в `NativeAotProbe.cs`.
2. Запустить. Если `#GP` → смотреть RAX/RCX на crash site. Non-canonical high bits (`0xF...`, `0x08_F...`) — обычно ILC-emitted helper не найден.
3. Для помощника — искать в `dotnet-runtime/src/coreclr/nativeaot/` по имени класса из error message или по эвристикам (`Rhp*`, `Rh*`).
4. Если helper нельзя добавить (как ClassConstructorRunner) — переписать код под workaround, добавить пункт сюда.

---

*Этот документ обновляется при каждой новой найденной проблеме. Источник правды по части "что нельзя" в нашей среде.*
