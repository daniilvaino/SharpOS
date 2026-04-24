# step033

Дата: 2026-04-24
Статус: закрыт

## Контекст

После step 32 (shared-generic interface dispatch заработал end-to-end) все фундаментальные блокеры BCL-compat коллекций сняты. Dictionary<K,V>, List<T>, EqualityComparer<T>, IEquatable<T> на примитивах — всё работает. MVP достигнут.

Step 33 — расширение surface-а до второго ряда коллекций + обёрток + фундамента сравнения. Цель: любой типичный managed C# код с коллекциями должен собраться и исполниться без обходов. По ходу работы появились два побочных результата: **архитектурные инварианты #1 и #2 зафиксированы в документации**, и **`yield return` разблокирован** (неожиданно — оказалось достаточно добавить Interlocked/Environment/Exception type symbols).

## Добавленные коллекции и инфраструктура

### Основные коллекции

- **Stack<T>** ([std/no-runtime/shared/Bcl/Stack.cs](../std/no-runtime/shared/Bcl/Stack.cs)) — `T[] _array + int _size`, LIFO. Push/Pop/Peek/TryPop/TryPeek/Contains/Clear/ToArray + struct Enumerator. Growth x2 c DefaultCapacity=4.
- **Queue<T>** ([std/no-runtime/shared/Bcl/Queue.cs](../std/no-runtime/shared/Bcl/Queue.cs)) — circular buffer, `_array + _head + _tail + _size`. Grow разворачивает wrap-around в contiguous layout. Enqueue/Dequeue/Peek/TryDequeue/TryPeek + struct Enumerator.
- **HashSet<T>** ([std/no-runtime/shared/Bcl/HashSet.cs](../std/no-runtime/shared/Bcl/HashSet.cs)) — chain-based bucket/entry pattern идентичный нашему Dictionary. `_comparer: IEqualityComparer<T>`, fallback на `EqualityComparer<T>.Default`. Add/Remove/Contains + `UnionWith`. Остальные set-ops (ExceptWith/IntersectWith/IsSubsetOf/...) — stubbed как Halt, подключим когда consumer понадобится. Enumerator — class (тот же паттерн что Dictionary.Enumerator — из-за ILC 7.0.20 boxed-struct bug).
- **LinkedList<T> + LinkedListNode<T>** ([std/no-runtime/shared/Bcl/LinkedList.cs](../std/no-runtime/shared/Bcl/LinkedList.cs)) — **verbatim port** из `src/libraries/System.Collections/src/System/Collections/Generic/LinkedList.cs`. Doubly-linked circular list, head pointer + count. Cuts: serialization, non-generic ICollection.CopyTo (требует reflection для ArrayTypeMismatchException catch), Debug.Assert'ы, throws → Halt.
- **SortedList<TKey, TValue>** ([std/no-runtime/shared/Bcl/SortedList.cs](../std/no-runtime/shared/Bcl/SortedList.cs)) — два параллельных массива `keys[] + values[]`, keys всегда отсортированы. Binary-search insert O(log n) + shift на O(n). Все ctors, Add/Remove/RemoveAt/Clear, `this[key]`, ContainsKey/ContainsValue, IndexOfKey/IndexOfValue, TryGetValue, GetKeyAtIndex/GetValueAtIndex, TrimExcess, Keys/Values (через KeyCollection/ValueCollection). Cuts: IDictionary non-generic, Array.Sort/BinarySearch/Copy (заменены на manual loops).

### Обёртки

- **ReadOnlyCollection<T>** ([std/no-runtime/shared/Bcl/ReadOnlyCollection.cs](../std/no-runtime/shared/Bcl/ReadOnlyCollection.cs)) — port из `ObjectModel/ReadOnlyCollection.cs`. Cuts: IList non-generic, IList.CopyTo(Array) с ArrayTypeMismatchException.
- **ReadOnlyDictionary<K,V>** ([std/no-runtime/shared/Bcl/ReadOnlyDictionary.cs](../std/no-runtime/shared/Bcl/ReadOnlyDictionary.cs)) — port. Keys/Values cached через `if (_keys == null)` вместо BCL's `??=` (во избежание Collections-namespace dependency). KeyCollection/ValueCollection вложенные классы.
- **ArraySegment<T>** ([std/no-runtime/shared/Bcl/ArraySegment.cs](../std/no-runtime/shared/Bcl/ArraySegment.cs)) — port из `System.Private.CoreLib/src/System/ArraySegment.cs`. Slice/ToArray/CopyTo + operator `==`/`!=` + implicit conversion from T[]. `Array.Copy`/`IndexOf` заменены на manual loops (в нашем env нет `System.Array.Copy` static).

### Фундамент сравнения

- **IComparable<T>, IComparable, IComparer<T>** — добавлены в `Interfaces.cs`. Variance `in T` / `in T` соответственно.
- **Comparer<T>** ([std/no-runtime/shared/Bcl/Comparer.cs](../std/no-runtime/shared/Bcl/Comparer.cs)) — abstract + `Default` как factory property (BCL pattern обхода ClassConstructorRunner). `DefaultComparerImpl<T>` — `x is IComparable<T> g ? g.CompareTo(y) : (x is IComparable ng ? ng.CompareTo(y) : 0)`.
- **Primitive CompareTo** в `MinimalRuntime.cs` — 10 примитивов (Int32/UInt32/Int64/UInt64/Byte/SByte/Int16/UInt16/Boolean/Char) получили `IComparable<T>` + `IComparable` + тело `CompareTo` на primitive compare. Тот же recursive-backing-field pattern что в step 32 для IEquatable.

### Threading + Exception stubs

- **Threading.cs** ([std/no-runtime/shared/Threading.cs](../std/no-runtime/shared/Threading.cs)) — добавлены:
  - `System.Threading.Interlocked.CompareExchange` (int/long/object/generic-class overloads) — single-threaded degenerates в plain read-compare-write.
  - `System.Threading.Interlocked.Exchange` (int).
  - `System.Environment.CurrentManagedThreadId` — возвращает константу 1.
  - `System.Exception` + `InvalidOperationException` + `NotSupportedException` + `ArgumentException` + `ArgumentNullException` + `ArgumentOutOfRangeException`.

Зачем: **Roslyn iterator state-machine rewriter** в `SyntheticBoundNodeFactory.New(NamedTypeSymbol, BoundExpression[])` ищет ctor по сигнатуре через `Single()`. Без `InvalidOperationException(string)` compile крашил с `Sequence contains no elements` → `FailFast`. Дополнительно state machine эмитит `Interlocked.CompareExchange` на `_state` (thread-safe CAS переход "fresh → iterating") + `Environment.CurrentManagedThreadId` сравнение в `GetEnumerator()` для reuse-this optimization.

**Результат: `yield return` работает.** Probe `yield return: ok val=6` на `IEnumerable<int> YieldOneTwoThree() { yield return 1; yield return 2; yield return 3; }`. Runtime не видит разницы между compiler-generated state machine class и нашими ручными Enumerator-классами — оба реализуют IEnumerator<T>, оба работают через shared-gen iface dispatch из step 32.

### Runtime link-blocker: RhpByRefAssignRef

Посреди работы линкер споткнулся на `RhpByRefAssignRef` — ILC эмитит call в эту функцию для ref-struct copies (когда struct содержит object refs). Проблема: функция использует **нестандартный calling convention** — rdi=destination, rsi=source, clobbers rcx, post-condition: rdi/rsi increment на 8.

Managed C# методы компилятся под Win64 ABI (rcx/rdx для args), так что просто `[RuntimeExport("RhpByRefAssignRef")]` не подойдёт. Решение по паттерну `InterfaceDispatchStub`:

- [OS/src/Boot/ByRefAssignRefStub.cs](../OS/src/Boot/ByRefAssignRefStub.cs) — managed wrapper с `[RuntimeExport]` + `[UnmanagedCallersOnly]`, тело Panic.Fail как fallback.
- [OS/src/Kernel/Memory/ByRefAssignRefPatcher.cs](../OS/src/Kernel/Memory/ByRefAssignRefPatcher.cs) — в kernel boot перезаписывает первые 15 байт тела wrapper-а на shellcode (non-moving GC = не нужны card tables / ephemeral generations):
  ```
  mov rcx, [rsi]    ; 48 8B 0E
  mov [rdi], rcx    ; 48 89 0F
  add rdi, 8        ; 48 83 C7 08
  add rsi, 8        ; 48 83 C6 08
  ret               ; C3
  ```

Интеграция: `Kernel.cs` вызывает `InstallByRefAssignRefShellcode()` сразу после `InstallInterfaceDispatchBridge()`, под firmware CR3.

### Probe-тесты

Добавлены:
- `stack<int>`, `queue<int>`, `hashset<int>` — push/pop/grow/LIFO/FIFO/dedup.
- `linkedlist<int>` — AddFirst/AddLast/AddAfter/Remove/RemoveFirst + enumerator.
- `readonly collection`, `readonly dict` — wrapper integrity.
- `arraysegment<int>` — offset+count view, Slice, индексатор, enum.
- `sortedlist<int,string>` — out-of-order inserts, остаются отсортированными, Remove/TryGetValue/enum.
- `yield return` — IEnumerable из compiler-generated state machine.

### Итоговый probe-прогон

```
virtual dispatch: ok val=124
interface: ok val=30
generic method: ok val=20
generic class: ok val=83
static ctor: ok val=99
box/unbox: ok val=77
is/as: ok val=25
array.length: ok val=30
enum: ok val=2
boxed equals (same ref): ok val=1
static assign+read+call: ok val=101
explicit cctor (int): ok val=77
bounds-checked loop: ok val=0
checked add (no overflow): ok val=150
abs-gen<RefT> virtual: ok val=404
iface<RefT> dispatch: ok val=808
eq.Default: ok val=1
eq.Equals(5,5): ok val=1
bcl list<T>: ok val=549
bcl list foreach: ok val=60
bcl list as IEnumerable: ok val=600
dict ctor: ok val=0
dict add: ok val=1
dict contains: ok val=1
dict tryget: ok val=100
dict foreach: ok val=600
dict<int,int>: ok val=300
dict custom comparer: ok val=2
stack<int>: ok val=10
queue<int>: ok val=18
hashset<int>: ok val=5
linkedlist<int>: ok val=6
readonly collection: ok val=31
readonly dict: ok val=200
arraysegment<int>: ok val=70
sortedlist<int,string>: ok val=40
yield return: ok val=6
shared-gen iface call: ok val=808
lambda: SKIP (needs Delegate infrastructure)
```

37 зелёных проб. Последний отказ — lambda/managed delegate (требует полноценную `System.Delegate` инфраструктуру, оставлено на будущие шаги).

## Архитектурные инварианты

По ходу work стало очевидно что нужно формализовать правила проекта. Записаны в [README.md](../README.md) и [plan.md](../plan.md) + в cross-session memory для применения в будущих сессиях.

### Инвариант 1 — C# is the only source language

В дереве исходников **не** появляется ни одного `.c`, `.cpp`, `.h`, `.asm`, `.s` файла. Каждая low-level задача решается одним из трёх механизмов:

1. **C# intrinsics** (`[RuntimeExport]`, `[UnmanagedCallersOnly]`, `delegate* unmanaged`, unsafe pointer math).
2. **Byte-array shellcode**, эмитится C# кодом в runtime в EfiLoaderCode buffer. Примеры: `InterfaceDispatchBridge` (195 байт для interface dispatch), `ByRefAssignRefPatcher` (15 байт), `Cr3Accessor`, `GcStackSpill`, `JumpStub`.
3. **Build-time codegen в PowerShell** scripts — когда MSVC-линкер требует C-ABI символов (security cookie и пр.), генерим `.c` ephemerally внутри `build_*.ps1`, компилим, подхватываем, **не коммитим в репо**.

Если задача кажется нерешаемой в рамках этих трёх — задача сформулирована неправильно.

Дополнительно: `static readonly T[] = new T[] {...}` **запрещён** тем же духом — триггерит ClassConstructorRunner lazy-init который не работает. Для byte-literal shellcode — inline bytes в methode (`target[0] = 0x48; target[1] = ...`).

### Инвариант 2 — Naming discipline

Канонические .NET namespaces (`System.*`, `System.Collections.Generic.*`, `System.Collections.ObjectModel.*`) — **только** для fully BCL-compat реализаций (modulo документированные ограничения в `docs/nativeaot-nostdlib-limits.md`). Partial / platform-specific / experimental — в SharpOS-specific namespaces (`SharpOS.Std.*`, `OS.Kernel.*`, `OS.Boot.*`, `OS.Hal.*`).

Цель: LINQ, System.Text.Json, прочий BCL-код когда-нибудь должен собираться поверх нашего std **без source-level правок**. Каждый раз когда ставим тип в `System.*` — должен держать BCL-контракт.

Задокументированные исключения (где API тот же, поведение отличается):
- `EqualityComparer<T>.Default` / `Comparer<T>.Default` — factory без кеширования (не lazy static field).
- Exception ctors — есть для codegen, но `throw` халтит без unwinder.
- Коллекции на error-path делают `Halt()` вместо `throw ArgumentException/KeyNotFoundException`.

## Новое правило воровства BCL

Параллельно user сформулировал правило (сохранено в memory): **для BCL-compat типов копировать код из dotnet/runtime, а не писать свой**. Обрезать только то, что реально не работает в нашей env (serialization, ICollection non-generic, throws → Halt). Оригинальные имена полей и методов сохранять — читатель BCL-кода должен узнавать структуру.

По этому правилу LinkedList, ReadOnlyCollection, ReadOnlyDictionary, ArraySegment, SortedList — реальные copy-paste с BCL с минимальными локальными правками. Stack/Queue/HashSet тоже BCL-shape-совместимые (те же поля, тот же алгоритм) хотя и писались изначально самостоятельно до формализации правила.

## Файлы

### Новые

- `std/no-runtime/shared/Bcl/Stack.cs`
- `std/no-runtime/shared/Bcl/Queue.cs`
- `std/no-runtime/shared/Bcl/HashSet.cs`
- `std/no-runtime/shared/Bcl/LinkedList.cs`
- `std/no-runtime/shared/Bcl/ReadOnlyCollection.cs`
- `std/no-runtime/shared/Bcl/ReadOnlyDictionary.cs`
- `std/no-runtime/shared/Bcl/ArraySegment.cs`
- `std/no-runtime/shared/Bcl/SortedList.cs`
- `std/no-runtime/shared/Bcl/Comparer.cs`
- `std/no-runtime/shared/Threading.cs`
- `OS/src/Boot/ByRefAssignRefStub.cs`
- `OS/src/Kernel/Memory/ByRefAssignRefPatcher.cs`

### Изменённые

- `OS/OS.csproj` — подключает все новые std-файлы.
- `OS/src/Boot/MinimalRuntime.cs` — primitives получили `IComparable<T>` + `IComparable` + `CompareTo`.
- `OS/src/Kernel/Kernel.cs` — вызов `InstallByRefAssignRefShellcode` после interface dispatch bridge.
- `OS/src/Kernel/Diagnostics/NativeAotProbe.cs` — 8 новых проб (Stack/Queue/HashSet/LinkedList/ReadOnlyCollection/ReadOnlyDictionary/ArraySegment/SortedList/Yield).
- `std/no-runtime/shared/Bcl/Interfaces.cs` — `ISet<T>`, `IReadOnlySet<T>`, `IComparable`, `IComparable<T>`, `IComparer<T>`.
- `docs/nativeaot-nostdlib-limits.md` — раздел про yield return раньше был "не проверено", теперь документирует что заработало через Threading.cs stubs + exception hierarchy.
- `README.md` — раздел "Архитектурные инварианты" с инвариантами 1+2.
- `plan.md` — раздел "Архитектурные инварианты" с детальной формулировкой обоих + примерами.

## Что откладываем

- **Multi-slot interface dispatch cache** — polymorphic call-sites сейчас каждый раз через managed Resolve.
- **Real Interlocked atomics** — наши stubs будут неверны при SMP / threading.
- **Exception unwinding** — throw компилится, runtime халтит.
- **Managed delegates / lambda** — требует `Delegate.InitializeClosedInstance` + MulticastDelegate machinery. Ждёт SUPER-5 exception infrastructure (или отдельной под-задачи).
- **`Single`, `Double`, `IntPtr`, `UIntPtr`** без IEquatable / IComparable — добавим если понадобится как ключ Dictionary / элемент SortedList.
- **Set-операции в HashSet** (ExceptWith, IntersectWith, IsSubsetOf, Overlaps, SetEquals, SymmetricExceptWith) — stubbed Halt, реализуем когда consumer появится.

## Следующий шаг

Step 34 — по ощущениям либо:
- IDT / CPU exceptions (SUPER-4 из плана) — чтобы #GP / #PF перестали давать triple-fault.
- Либо real managed exception handling (SUPER-5) — так как базовые Exception классы уже есть, можно попробовать поднять минимальный throw/catch в пределах одного метода через setjmp/longjmp-стайл механизм.
- Либо LINQ начать затаскивать — iter-state-machine работает, IEnumerable<T> работает, IEqualityComparer работает.

Выбор за user-ом.
