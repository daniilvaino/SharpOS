# step031

Дата: 2026-04-19
Статус: в работе

## Контекст

Продолжение после закрытия SUPER-2 в step 30. Managed GC работает, `new List<int>()` выполняется. Текущая версия `SharpOS.Std.Collections.List<T>` (65 строк) — **эксперимент**, не BCL-compat.

В рамках step 31 открываем **SUPER-3** из `plan.md`:
> Базовые managed collections. `List<T>`, `Dictionary<TKey, TValue>`, `Stack<T>`, `Queue<T>`, `HashSet<T>` работают в freestanding. IEnumerable<T> / IEnumerator<T> infrastructure для foreach.
>
> **Критерий готовности:** нормальный C# код с коллекциями и foreach работает.

## Стратегия — BCL-compat API

Коллекции размещаются в `System.Collections.Generic` namespace, имена и сигнатуры совпадают с реальным BCL. Цель — чтобы потом можно было **брать LINQ и другой BCL-код из dotnet/runtime почти as-is** (LINQ — это extension methods на `System.Collections.Generic.IEnumerable<T>`).

Срезаются только вещи которые в нашем окружении не работают:
- Exceptions остаются halt-стабами (не `throw`, просто `while(true)` через `ThrowHelpers`).
- `EqualityComparer<T>.Default` не может через reflection выбрать Generic vs Object comparer — возвращает `ObjectEqualityComparer<T>` (boxing-based). Пользователь передаёт custom comparer для hot-path.
- `yield return` пока нет (требует Delegate + generated IEnumerator класс). Enumerators пишем как явные struct/class.

## Порядок работ

1. **Фундамент**:
   - `System.Object` получает virtual `Equals(object)` + `GetHashCode()` (reference-based defaults).
   - `IEquatable<T>` в `System`.
   - `IDisposable` в `System`.
   - `IEnumerable`, `IEnumerable<T>`, `IEnumerator`, `IEnumerator<T>` в нужных namespace-ах.
2. **`EqualityComparer<T>`** abstract class в `System.Collections.Generic` + `Default` property → `ObjectEqualityComparer<T>`.
3. **`List<T>`** переписать под BCL API — убрать текущий эксперимент.
4. **`Dictionary<K,V>`** BCL-compat с `IEqualityComparer<K>`.
5. **`Stack<T>`, `Queue<T>`, `HashSet<T>`** — базовый API.

---

## Фундамент SUPER-3

### System.Object — virtual Equals/GetHashCode/ToString

В обоих `MinimalRuntime.cs` (`OS/src/Boot/` и `apps/sdk/`) `System.Object` расширен до:
- `virtual bool Equals(object obj)` — default reference equality
- `virtual int GetHashCode()` — address-based (stable в нашем non-moving GC)
- `virtual string ToString()` — null по умолчанию
- `static bool Equals(object, object)` — null-safe + дела­гирует instance Equals
- `static bool ReferenceEquals(object, object)` — просто ссылочное сравнение

### Интерфейсный слой BCL

Новый файл `std/no-runtime/shared/Bcl/Interfaces.cs` содержит все интерфейсы, нужные для коллекций — в **каноничных namespace-ах**, чтобы LINQ и прочий BCL-код потом собирался as-is:
- `System.IEquatable<T>`, `System.IDisposable`
- `System.Collections.IEnumerable`, `IEnumerator`
- `System.Collections.Generic.IEnumerable<out T>`, `IEnumerator<out T>`, `ICollection<T>`, `IReadOnlyCollection<out T>`, `IList<T>`, `IReadOnlyList<out T>`, `IEqualityComparer<T>`, `IDictionary<K,V>`, `IReadOnlyDictionary<K,V>`, `KeyValuePair<K,V>`

### EqualityComparer<T>

`std/no-runtime/shared/Bcl/EqualityComparer.cs` — абстрактный `EqualityComparer<T> : IEqualityComparer<T>` + внутренний `DefaultComparer<T>` (boxing-based через `Object.Equals`/`Object.GetHashCode`).

**Важная находка:** `EqualityComparer<T>.Default` как **static lazy field** в abstract generic class роняет virtual dispatch на inst­ance'ах: RAX на callsite — `0xF0000000_xxxx_xxxx` (non-canonical) → `#GP`. Причина пока не понята до конца (вероятно ILC devirtualization optimization для abstract generic со static-backed singleton). Workaround — **factory property без кеширования**: `Default => new DefaultComparer<T>()`. Одна аллокация за чтение, зато работает. Производительность поправим когда будет понятна root cause.

Value equality через boxing даёт reference equality (два отдельных бокса не равны). Это причина почему `Contains(21)` не находит 21. Для hot-path типов (int, long, string) либо пользователь передаст custom `IEqualityComparer<T>` в Dictionary/HashSet, либо когда-то позже добавим детекцию `IEquatable<T>` в `DefaultComparer`.

## Первая BCL-compat коллекция: List<T>

`std/no-runtime/shared/Bcl/List.cs` — API-совместимая копия `System.Collections.Generic.List<T>` в 170 строк.

- `IList<T>`, `IReadOnlyList<T>`
- Ctors: default, `int capacity`
- Methods: `Add`, `Clear`, `Contains`, `CopyTo`, `IndexOf`, `Insert`, `Remove`, `RemoveAt`
- Props: `Count`, `Capacity` (get+set), `IsReadOnly`
- Индексатор `this[int]`
- Public `struct Enumerator : IEnumerator<T>` — boxing-less foreach через duck-typing + boxed-struct через interface

Сокращено относительно BCL: `_version`-счётчик, ctor(IEnumerable<T>), AddRange/InsertRange/GetRange, Sort/BinarySearch/FindIndex. Добавим когда консументу понадобится.

## Probe-тесты

В `NativeAotProbe` добавлены пробы чтобы убедиться что BCL-compat путь работает:
- `boxed equals (same ref): ok val=1`
- `eq.Default: ok val=1`
- `eq.Equals(5,5): ok val=0` — через boxing+virtual, работает без краша (result=false ожидаемо для reference-equality)
- `bcl list<T>: FAIL val=570` — не крашит, но `Contains(21)` возвращает false (boxing equality), это известное ограничение
- `bcl list foreach: ok val=60` — duck-typed struct enumerator
- **`bcl list as IEnumerable: ok val=600`** — ключевой тест: `foreach (int v in (IEnumerable<int>)list)` работает через boxed struct enumerator и virtual dispatch. **Значит LINQ теоретически можно затащить** — он как раз extension methods на `IEnumerable<T>`.

Файлы:
- `std/no-runtime/shared/Bcl/Interfaces.cs` (новый)
- `std/no-runtime/shared/Bcl/EqualityComparer.cs` (новый)
- `std/no-runtime/shared/Bcl/List.cs` (новый)
- `OS/src/Boot/MinimalRuntime.cs`, `apps/sdk/MinimalRuntime.cs` — Object virtual methods
- Подключение BCL-файлов в `OS/OS.csproj`, `apps/FetchApp/FetchApp.csproj`, `apps/HelloSharpFs/HelloSharpFs.csproj`
- `OS/src/Kernel/Diagnostics/NativeAotProbe.cs` — BCL-probes + набор debug-проб оставлен как документация (не вызывается, но показывает что ILC делает)
