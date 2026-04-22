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

### ❌ Virtual call на generic abstract class через reference-type параметр — halt-ит (уходит в наш `while(true)`-стаб вместо real dispatch)

Пример: `EqualityComparer<MyKey>.Equals(x, y)` где `MyKey` — class. Зависает (halt loop) внутри interface/shared-generic dispatch. Для value-типа `EqualityComparer<int>` работает.

**Причина:** ILC для generic с reference-типом параметром использует **shared generic code через `System.__Canon`**. Virtual dispatch через __Canon vtable нуждается в runtime dictionary lookup helper (что-то типа `RhpGenericLookupFromType`/`RhpUniversalTransition`), которого у нас нет. Та же категория ошибок что `RhpInitialDynamicInterfaceDispatch` (interface-first-call).

**⚠️ Workaround для Dictionary-like коллекций:** не пользоваться `IEqualityComparer<TKey>` параметром внутри, вместо этого — `object.Equals(x, y)` + `key.GetHashCode()` (virtual на Object-vtable, non-generic, работает). Цена: consumer должен override `Object.Equals(object)` + `GetHashCode()` на своём ключе вместо передачи comparer-а. Это всё равно BCL-идиома для reference-ключей.

Поле `_comparer` в коллекциях храним для будущего (API-compat с BCL), но не используем.

### ❌ Generic constraint `where T : IEquatable<T>`

`key.Equals(other)` под этим constraint → `constrained.callvirt IEquatable<T>::Equals` → interface-first-call dispatch → halt (наш `RhpInitialDynamicInterfaceDispatch` stub).

**⚠️ Workaround:** убрать constraint, использовать `object.Equals(a, b)`.

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

### ⚠️ `EqualityComparer<T>.Default` для value-типа даёт reference-equality, не value-equality

Наш `DefaultComparer<T>` делает `x.Equals(y)` — для int это boxing + inherited `Object.Equals` → reference-equality на двух отдельных боксах → всегда false.

**⚠️ Workarounds:**
1. Передавать custom `IEqualityComparer<T>` в конструктор `Dictionary`/`HashSet` — там где нужна value-equality.
2. Когда-то позже: в `DefaultComparer<T>` добавить рантайм-проверку `x is IEquatable<T>` и вызов `IEquatable<T>.Equals` без boxing. Потребует чтобы primitive types (int, long) реализовали `IEquatable<T>` — сейчас они пустые stubs.

Для reference-типов (class-ов) reference-equality обычно и нужна — там не проблема.

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
