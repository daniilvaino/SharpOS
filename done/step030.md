# step030

Дата: 2026-04-19
Статус: завершён (SUPER-2 закрыт по критерию plan.md)

## Контекст

Продолжение SUPER-2 после закрытия step 29. На момент старта шага есть:
- Managed GC в kernel и apps (mark + sweep + roots registry + stack-scan + register-spill trampoline)
- Stress-test на 184 объектах отработал без крашей
- Apps полностью на managed аллокаторе, C-стаб удалён
- Build-id автоматика из git sha + `build-tag.txt`

Цель step 30 — закрыть остаток фазы 3.5 SUPER-2:
- Freelist reuse в `GcHeap` (сейчас bump-only, куча только растёт — циклические аллокации типа Test 3 ротации жрут память).
- `GC.Collect()` public entry point (сейчас надо вручную звать `Begin` + `GcStackSpill.Invoke` + `Sweep.Run`).
- Threshold-триггер автоматического запуска GC при заполнении сегмента.

---

## Фаза 3.5 остаток: freelist reuse + `GC.Collect()`

### Freelist в `GcHeap`

- `std/no-runtime/shared/GC/GcHeap.cs`:
  - Статики `s_freelistHead, s_freelistNodes, s_freelistReuseCount, s_freelistSplitCount`.
  - Freelist — односвязный список через next-pointer в первых 8 байтах payload свободного объекта (offset 12 после `MT* + Length`).
  - Константа `MinFreeBlockSize = 32` — блоки меньше остаются walkable free-маркерами но не регистрируются в freelist (в них не влезает next-pointer + выравнивание).
  - `AllocateRaw` теперь идёт по порядку: **freelist first-fit → bump → grow**.
  - `TryAllocateFromFreelist(aligned)` — линейный walk, на find убираем из списка, при остатке ≥ 32 делаем split (новый free node в head).
  - `RebuildFreelist()` — walker по всем сегментам, линкует все свободные блоки ≥ 32 байт в фрешевый freelist. Вызывается из `GcSweep.Run()` в конце.

### `GC.Collect()` public entry

- `std/no-runtime/shared/GC/GC.cs` — новый файл:
  ```csharp
  public static class GC {
      public static void Collect() {
          GcMark.Begin();
          GcRoots.MarkAll();
          GcSweep.Run();   // сам зовёт GcHeap.RebuildFreelist
      }
  }
  ```
  Доступен и в kernel и в apps. Apps пользуются напрямую — без trampoline, conservative scan "best-effort".

- `OS/src/Kernel/Memory/KernelGC.cs` — kernel-wrapper:
  ```csharp
  internal static class KernelGC {
      public static void Collect() {
          GcMark.Begin();
          if (GcStackSpill.IsInitialized)
              GcStackSpill.Invoke(&GcRoots.MarkAllUnmanaged);
          else
              GcRoots.MarkAll();
          GcSweep.Run();
      }
  }
  ```
  Kernel всегда зовёт `KernelGC.Collect()` — spill trampoline если доступен, обычный `MarkAll` при раннем boot до инициализации exec-стабов.

- `Kernel.cs` smoke-тест и `GcStressTest.Run()` переведены на `KernelGC.Collect()` — убрали ручные `GcMark.Begin + GcStackSpill.Invoke + GcSweep.Run`.

### Результат

Stress-test после фикса:

```
-- test 1: binary tree depth 5 --
tree allocs=62  marked=62  kept=62  swept=1  freelist=1  reuse=1
-- test 2: 50 live + 50 dead --
half allocs=103 marked=114 kept=114 swept=51 freelist=51 reuse=2
-- test 3: rotate 10 --
rot  allocs=20  marked=116 kept=116 swept=18 freelist=49 reuse=22
```

Главное — в Test 3 (ротация `s_rot = new Payload()` 10 раз = 20 allocs) `reuse` вырос с 2 до 22, то есть **все 20 allocs попали в освобождённые блоки**. Куча больше не растёт от циклических аллокаций — это основной практический выигрыш фазы 3.5.

Test 1 тоже корректно показал 1 reuse — это тот самый `int[5]` из smoke-теста, который был заметён до стресса и теперь переиспользуется.

Файлы:
- `std/no-runtime/shared/GC/GcHeap.cs` — freelist + `RebuildFreelist` + обновлённый `AllocateRaw`
- `std/no-runtime/shared/GC/GcSweep.cs` — `GcHeap.RebuildFreelist()` в конце `Run`
- `std/no-runtime/shared/GC/GC.cs` — новый, public `GC.Collect()`
- `OS/src/Kernel/Memory/KernelGC.cs` — новый, wrapper через trampoline
- `OS/src/Kernel/Kernel.cs`, `OS/src/Kernel/Diagnostics/GcStressTest.cs` — переведены на `KernelGC.Collect`
- `OS/OS.csproj`, `apps/FetchApp/FetchApp.csproj`, `apps/HelloSharpFs/HelloSharpFs.csproj` — подключение `GC.cs`

---

## Инвентаризация managed-фич NativeAOT + NoStdLib

После того как GC поднялся, непонятно было что ещё из managed C# работает "из коробки" без нашего допила. Написали `OS/src/Kernel/Diagnostics/NativeAotProbe.cs` — 10 маленьких тестов, каждый проверяет одну фичу: компилируется, отрабатывает на kernel-старте, результат сравнивается с ожидаемым.

Первый прогон дал два data point-а:
1. `int[].Length` не компилировался — наш базовый `public abstract class Array { }` был пустой. Добавили `public readonly int Length` по образцу `String.Length` (RhpNewArray уже пишет длину в offset 8 после MT*) — заработало.
2. Любой managed `delegate T F(...)` (даже плоский `IntFn f = x => x * 3`) роняет ILC: *"Expected method 'InitializeClosedInstance' not found on type 'System.Delegate'"*. Требует stub-а с полями `_target` + `_functionPointer` + методом `InitializeClosedInstance(object, IntPtr)` + Invoke-машинерии. Отложено — у нас везде `delegate* unmanaged<T>` (IL function pointers), им Delegate не нужен.

Итог пробы:

| фича | статус |
|---|---|
| Virtual dispatch (abstract + override) | ✅ |
| Interfaces | ✅ |
| Generic methods | ✅ |
| Generic classes (с type parameter в поле) | ✅ |
| Static constructors | ✅ |
| Boxing / unboxing | ✅ |
| `is` / `as` | ✅ |
| `Array.Length` + индекс-walk | ✅ (после фикса `Array`) |
| Enum + bit-flags | ✅ |
| Managed delegates / lambdas | ❌ (нужна Delegate infra) |

**Практический вывод:** для следующих задач (коллекции `List<T>`, `Dictionary<K,V>`, `IEnumerable<T>` через ручной state machine) у нас есть весь нужный C#-инструментарий. Делегаты понадобятся когда дойдём до events / LINQ — тогда допишем Delegate.

Файлы:
- `OS/src/Kernel/Diagnostics/NativeAotProbe.cs` (новый)
- `OS/src/Kernel/Kernel.cs` — вызов `NativeAotProbe.Run()` после stress-теста
- `OS/src/Boot/MinimalRuntime.cs`, `apps/sdk/MinimalRuntime.cs` — `Array.Length` (readonly int field)

---

## Первая managed-коллекция: `List<T>`

После того как probe показал что generics/virtual/static-ctor/array/boxing работают — проверили эту гипотезу на практике: написали `std/no-runtime/shared/Collections/List.cs` (65 строк), минимальный аналог `System.Collections.Generic.List<T>`.

Стрип с `LowLevelList.cs` из dotnet/runtime: убраны `IEnumerable<T>`/`ICollection<T>` (у нас нет этих интерфейсов в BCL-стабе), убраны `throw new Argument*Exception` (нет exception-инфраструктуры — bounds-check уходит в halt через `ThrowHelpers`).

Оставлено:
- `Add(T)`, `Clear()`, `RemoveAt(int)`
- `this[int]`, `Count`, `Capacity`
- `Grow()` — x2 с дефолтной capacity 4
- struct `Enumerator` с `MoveNext`/`Current` — **duck-typed** (C# распознаёт по сигнатуре, IEnumerator-интерфейс не нужен), так что `foreach (var x in list)` работает без BCL

Probe показал:
```
list<int>:    ok val=570   (Add(0..57 шагом 3), 5 ресайзов 0→4→8→16→32)
list foreach: ok val=60    (struct Enumerator отрабатывает)
```

**Это означает что:**
1. `RhpNewArray` корректно аллоцирует generic-instantiated `int[]` и `T[]` через наш GC heap.
2. `RhpStelemRef` работает для reference-типов через `T[]` — covariance-check пропущен.
3. Написан-умри полный цикл managed C# кода (generic class + struct enumerator + foreach duck-typing) без единого стаба со стороны BCL.

Файлы:
- `std/no-runtime/shared/Collections/List.cs` (новый)
- Подключение в `OS/OS.csproj`, `apps/FetchApp/FetchApp.csproj`, `apps/HelloSharpFs/HelloSharpFs.csproj`
- `OS/src/Kernel/Diagnostics/NativeAotProbe.cs` — добавлены `Probe_ListInt` + `Probe_ListForeach`

---

## Итог шага 30: SUPER-2 закрыт

Критерий SUPER-2 из `plan.md`:
> `var list = new List<int>(); list.Add(1); list.Add(2);` работает и не падает.

Выполнен. В `NativeAotProbe` на kernel-старте:
```
list<int>:    ok val=570
list foreach: ok val=60
```

То есть `new` идёт через managed GC, `RhpNewArray` корректно аллоцирует generic `T[]`, `List<T>` растёт через несколько ресайзов без крашей.

**Scope SUPER-2 из plan.md покрыт целиком:**
- Managed allocator с API `alloc(size, eeTypePtr) → object` — `GcHeap.AllocateRaw`.
- `RhNewObject`/`RhNewArray`/`RhNewString` runtime hooks — `GcRuntimeExports.cs`.
- Поддержка reference types, boxed value types, strings, arrays — подтверждено probe (box/unbox, array.length, virtual dispatch).
- Fallback на существующий C-аллокатор для boot — фактически **убран полностью** (phase 4), apps используют тот же managed pipeline.

**Что осталось вне scope SUPER-2 (plan.md отдельно говорит "вне scope"):** полный GC (у нас минимальный mark+sweep+freelist, этого пока хватает), write barriers для generational, concurrent allocation, pinning, finalizers.

### Полировка отложена

Не блокер для критерия, не в scope plan.md, но полезно дальше:
- **Threshold auto-trigger** для `GC.Collect()` — сейчас только ручной вызов. Когда будет heavy workload — добавим вызов при достижении порога заполнения.
- **Длинные stress-тесты** — сотни тысяч аллокаций в цикле. Сейчас stress 184 объекта, достаточно для sanity но не для production-confidence.

### Параллельные работы в step 30

- **Инвентарь managed-фич NativeAOT+NoStdLib** — `NativeAotProbe` показал что 9 из 10 паттернов работают "из коробки" (виртуальная диспатч, интерфейсы, generics, static ctor, box/unbox, is/as, Array.Length, enum, array iteration). Единственный missing — managed `delegate T F(...)` (требует `System.Delegate.InitializeClosedInstance` + `_target`/`_functionPointer`/Invoke).
- **Первая managed-коллекция** `SharpOS.Std.Collections.List<T>` — 65 строк, compile-foreach через duck-typed struct enumerator. Это был **эксперимент-доказательство** что коллекции пишутся на текущей базе. Его нужно **переписать под BCL API** в рамках SUPER-3 (с `IEnumerable<T>`, в `System.Collections.Generic`).

### Открытие SUPER-3

Путь дальше — SUPER-3 (базовые managed коллекции по plan.md). Это самостоятельная супер-задача со своим критерием:
> Нормальный C# код с коллекциями и foreach работает.

Идти будем по BCL-compat дороге: namespaces и имена совпадают с реальным BCL, чтобы позже можно было затащить LINQ как есть (он extension methods на `System.Collections.Generic.IEnumerable<T>`).

Порядок планируется такой:
1. Фундамент: `Object.Equals`/`GetHashCode` virtual, `IEquatable<T>`, `IDisposable`.
2. Enumerable infrastructure: `IEnumerable`/`IEnumerable<T>`/`IEnumerator`/`IEnumerator<T>`.
3. `EqualityComparer<T>` abstract + `Default`.
4. `List<T>` переписать под BCL (убрать текущий эксперимент).
5. `Dictionary<K,V>` BCL-compat с `IEqualityComparer<K>`.
6. `Stack<T>`, `Queue<T>`, `HashSet<T>`.

Это будет step 31+.
