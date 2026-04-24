# NativeAOT + NoStdLib: карта ограничений

Живой документ. Перечень managed-паттернов C#, которые **не работают** или работают с оговорками в текущем SharpOS-сетапе (NativeAOT 7.0.20 + `NoStdLib=true` + наш `MinimalRuntime` без полной BCL).

Все пункты проверены на практике через `OS/src/Kernel/Diagnostics/NativeAotProbe.cs` — там живут минимальные repro-ы. Если что-то из этого списка понадобится для конкретной задачи — сначала убеждаемся что работаем через workaround, потом принимаем решение: либо оставить ограничение, либо дописать недостающий helper.

**Конвенция:**
- ❌ **Не работает** — компилируется или нет, но даёт `#GP`, halt-через-stub, либо silent corruption.
- ⚠️ **Работает через обход** — есть идиома которая даёт эквивалентное поведение.
- 🔧 **Не компилируется** — ILC/компилятор C# отвергает код без доп. заглушек.

**Важная терминология.** Когда пишем «halt», «уходит в стаб» — имеем в виду что наш `RhpInitialDynamicInterfaceDispatch` / `RhpFallbackFailFast` / `ThrowHelpers.Throw*` — это `while(true);`. В настоящем runtime там полноценная логика (asm-trampoline для dispatch, managed-exception throw для throws). У нас пока заглушка → код, которому не хватает логики, «зацикливается» ровно в этом `while`. Это **не баг нашей логики**, это **ненаполненный контракт с ILC**. Полный fix для каждого такого случая — портировать соответствующий asm/runtime helper (пример: `GcStackSpill` shellcode в kernel/Memory/, успешный пример реализации).

---

## 1. Static-field инициализация

### ❌ Lazy static reference field

```csharp
private static T s_default;
public static T Default
{
    get
    {
        if (s_default == null) s_default = new T();
        return s_default;
    }
}
```

**Симптом:** первый доступ к `Default` даёт `#GP`; RAX на crash site — либо `0xF0000000_xxxx_xxxx` (non-canonical mangled ptr), либо `0x08_FFFFFFFFFFFFFF` (sentinel "unresolved helper").

**Причина:** ILC перед первым чтением static-field вставляет вызов в `System.Runtime.CompilerServices.ClassConstructorRunner.CheckStaticClassConstructionReturn{GC,NonGC}StaticBase`. В стоковой BCL эти методы запускают cctor и выставляют `initialized=1`. В NoStdLib их нет. Порт из Test.CoreLib не помог — ILC в `--resilient` режиме его молча игнорирует (проверено через `ilc --map`: наш тип отсутствует в бинаре).

**⚠️ Workaround:** factory-property, без кеширования:
```csharp
public static T Default => new T();
```
Одна аллокация за чтение. `EqualityComparer<T>.Default` у нас реализован так.

**Фикс-позже:** клонировать `ILCompiler.Compiler` sources, разобраться в resolution path. Или обновиться до SDK 10+ и проверить поведение.

### ❌ Reference-typed field initializer

```csharp
private static L1 s_l1 = new L1Child();   // крашит при first-access class-а
```

Та же причина: компилятор заворачивает это в cctor, cctor зовётся через ClassConstructorRunner.

**⚠️ Workaround:** объявить без инициализации, присвоить в первом методе, который трогает класс:
```csharp
private static L1 s_l1;

public static void Init() { s_l1 = new L1Child(); }   // вызываем явно
```

### ❌ Explicit static cctor на classe с reference-полем

```csharp
class C { public static T X; static C() { X = new T(); } }
```

Тот же механизм — `static C()` делает класс не-beforefieldinit → ClassConstructorRunner нужен.

### ✅ Explicit static cctor с value-typed полем работает

```csharp
class C { public static int X; static C() { X = 99; } }   // ok
```

Подтверждено probe. Вероятно ILC для value-type static использует другой helper (NonGC-вариант), который нам удаётся не задевать. Но reference-cctor всё равно не работает — избегаем.

### ✅ Прямое `s_field = new X(); ...; s_field.M()` работает

В пределах одного метода ILC видит что инициализация уже произошла, не вставляет cctor-check. Это главная лазейка.

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

### ❌ Любой managed delegate (`delegate T F(...)`) + lambda

```csharp
delegate int IntFn(int x);
IntFn f = x => x * 3;   // ILC: InitializeClosedInstance not found on System.Delegate
```

Требует инфраструктуру `System.Delegate` с полями `_target`, `_functionPointer`, методом `InitializeClosedInstance` и Invoke-машинерией.

**⚠️ Workaround:** `delegate* unmanaged<T>` / `delegate*<T>` (IL function pointers) — **работают** и используются в нашем kernel/GC везде (`GcStackSpill.Invoke(&GcRoots.MarkAllUnmanaged)`, service table in apps и т.д.). Они не требуют Delegate-инфраструктуры.

Managed delegate допишем когда понадобится events/LINQ (там просто `Func<T>` и `Action<T>`).

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

---

## Быстрый протокол при встрече новой проблемы

1. Добавить минимальный repro в `NativeAotProbe.cs`.
2. Запустить. Если `#GP` → смотреть RAX/RCX на crash site. Non-canonical high bits (`0xF...`, `0x08_F...`) — обычно ILC-emitted helper не найден.
3. Для помощника — искать в `dotnet-runtime/src/coreclr/nativeaot/` по имени класса из error message или по эвристикам (`Rhp*`, `Rh*`).
4. Если helper нельзя добавить (как ClassConstructorRunner) — переписать код под workaround, добавить пункт сюда.

---

*Этот документ обновляется при каждой новой найденной проблеме. Источник правды по части "что нельзя" в нашей среде.*
