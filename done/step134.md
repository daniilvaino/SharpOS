# step 134 — mini-LINQ (System.Linq.Enumerable) на bare metal

## Контекст

North-star кирпич перед managed-DOOM и разблокировщик фазы-2 PeNet
(imports/exports/metadata парсеры LINQ-heavy). donext workstream: «мини-LINQ
(Where/Select/ToArray)». Делаем LINQ-to-objects поверх нашей std.

## Результат

**14/14 `[Linq]` проб зелёные**, ноль регрессий: батарея OK 92 / VALUE 3 /
FAIL 1 (единственный FAIL — давно известный EnumToString), лаунчеры 4/4,
CoreCLR exitCode=42. **Generic yield-итераторы работают** (`Where<T>`,
`Select<T,R>` и пр.) — это был главный риск.

## Что где

`std/no-runtime/shared/Linq/Enumerable.cs` — `System.Linq.Enumerable` с
BCL-точными сигнатурами (реальный LINQ-код компилится без правок).

- **Lazy (yield)**: Where(×2), Select(×2), SelectMany, Skip/Take/SkipWhile/
  TakeWhile, Concat, Distinct(×2), Reverse, Cast, OfType, DefaultIfEmpty,
  Empty/Range/Repeat, OrderBy/OrderByDescending(×2).
- **Материализующие**: ToArray, ToList, ToDictionary(×3), Count(×2)/LongCount,
  Any(×2)/All, Contains(×2), First/Last/Single/ElementAt (+OrDefault),
  Aggregate(×3), Sum(int/long/double + selector), Min/Max, Average.

Подход: BCL-модерновая LINQ (Iterator<T>/partitioning fast-paths) не
вендорилась (снапшот System.Linq пуст, инфра сложна) — написали чистую
lazy-реализацию с yield (семантика deferred-execution идентична BCL, без
array/list-спецкейсов). Probe — `NativeAotProbe.Probe_Linq` (source `List<int>`).

## Две ловушки (обе — про array-как-IEnumerable)

### Массивы НЕ реализуют `IEnumerable<T>`
У нас нет SZArrayHelper runtime-машинерии → array-MT `NumInterfaces=0`.
`int[] a; IEnumerable<int> e = a; e.GetEnumerator()` → **panic
`iface-resolve: no impl slot`** (компилится, но падает в рантайме). Прямой
`foreach` по массиву работает (ldelem, без GetEnumerator). Записано в
limits.md §4. **Source для LINQ = `List<T>`/итератор, не голый массив.**

### OrderBy отдавал голый массив как IEnumerable
`OrderBy` возвращал `TSource[]` из `StableSort` напрямую как
`IEnumerable<TSource>`. `.First()` → `GetEnumerator()` на массиве → та же
panic. **Фикс:** `OrderByCore` — yield-обёртка над отсортированным массивом
(`foreach` внутри = прямой ldelem). **Урок: любой LINQ-оператор с возвратом
`IEnumerable<T>` обязан отдавать настоящий итератор, не голый массив.**

Диагностика: сперва ошибочно списал первый паник на `Array.Sort<T>(T[],
Comparison<T>)` для value-type T (ComparisonAdapter<int>). Но паник был на
`.First()` ПОСЛЕ возврата StableSort — значит Array.Sort отработал, корень
всегда был array-return. Комментарий поправлен, ложное limits-утверждение
не вносилось. (OrderBy оставлен на inline merge-sort — стабилен, а
Array.Sort introsort нет; для OrderBy стабильность обязательна.)

## Ограничения (в limits.md)

- Source — только `List<T>`/`IEnumerable<T>`/итератор, не массив.
- Deferred: ThenBy/`IOrderedEnumerable`, GroupBy, Join, Zip, Union/Intersect/
  Except, nullable-numeric aggregates, decimal. Добавляем по первому
  потребителю.

## Проверка

`run_build.ps1` (после 2 итераций: `using System.Linq` CS1061 → добавлен;
OrderBy array-return panic → yield-обёртка). QEMU: 14/14 `[Linq]` OK,
регрессий 0. probe_report.ps1 — категория `[Linq]`. limits.md §4+§8
синхронизированы.

## Next

- Фаза-2 PeNet: imports/exports/reloc парсеры (теперь LINQ есть) + .NET-
  метаданные.
- Либо **#2 PE-loader машинария** (section mapping, relocations, imports).
- Array-as-IEnumerable (SZArrayHelper) — крупный отдельный кирпич, если
  array-LINQ понадобится.
