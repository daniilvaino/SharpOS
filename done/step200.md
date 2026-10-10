# step 200 — KQL в конвейере: BabyKusto собран и исполняется; упаковка `T?`; регрессия step199 под `--gc-stress`

## Контекст

Следующий фронт после step199 — фильтр конвейера на KQL: `PIPEGEN 100 |
KQL 'where Level >= 3 | …'`. Разбор и связывание (Kusto.Language) работали с
step198; исполнения не было — BabyKusto не собирался. Перед этим — два
провала батареи под `--gc-stress`, оставшиеся от step199.

Итог (QEMU, qemu64): батарея **27/27** с KQLBABY 6/6 и двумя строками KQL.

## 1. Регрессия step199 под `--gc-stress`

`AOTTESTS --gc-stress 16 4` и `--gc-stress 1 1 --pipe-stress 1` кончались
кодом 134: `Exception.AppendStackFrame` читал объект исключения аппаратного
отказа уже залитым `0xCC`. Корень — правка step199 в `HwFaultBridge`: запись
`ExInfo` снималась из цепочки на всё время передачи CoreCLR и нативному SEH, а
обход SEH печатает (`[seh-step]`) и выделяет — сборка под стрессом шла, не
видя исключения, и освобождала его.

Исправление: запись снимается только на вызов CoreCLR (он не выделяет в нашей
куче и возобновляет своим кодом); на время обхода SEH она в цепочке, а
`SehDispatch` перед возобновлением на найденном обработчике снимает записи
ниже SP возобновления — `ExInfoHead.PopBelow`, как `ExInfo::PopExInfos` в
NativeAOT. Под стрессом 298/298; строки `[bigstack] … entry left` нет —
утечка step199 закрыта по-прежнему.

Попутно: ожидание `--gc-stress 16 4` в `autorun.sh` было 297 — проверку FMA
(step199) я добавил, а эту строку не поднял.

## 2. Упаковка и распаковка `T?` — смещение значения

KQLBABY первым прогоном выдал правильные схемы и строки, а числа — `4` как
`288230376151711744` (= `4 << 56`): значение `Nullable<long>` читалось со
смещения 1, а лежит по 8.

- `RhBox` не разворачивал `Nullable<T>`: `object o = (long?)5` давал
  упакованный `Nullable`, а не `long`. Теперь как `RhBox` NativeAOT: `null`
  без значения, упакованный `T` — со значением.
- Смещение значения — `GcMethodTable.NullableValueOffset`, из optional
  fields (release/8.0). Указатель на поля находился верно, но таблица тегов
  была старше .NET 8: `DispatchMap=1, ValueTypeFieldPadding=2,
  NullableValueOffset=3` (с комментарием «stable across NativeAOT history»).
  В .NET 8 `DispatchMap` ушёл в отдельное поле, и теги сдвинулись: `RareFlags=0,
  ValueTypeFieldPadding=1, NullableValueOffset=2` (`gc-experiment/
  dotnet-runtime-8.0`). Найдено по сырым байтам `MethodTable` `long?`: блоб
  `82 0E` — «тег 2, последний, значение 7».
- Затронуты были все упаковки/распаковки `T?` в ядре и приложениях со
  step198 (`RhUnboxNullable` писал по смещению 1).

Урок: «формат major 9» — это и есть .NET 8 (ILC 8); сверять с
`gc-experiment/dotnet-runtime-8.0`, а не с поздними снимками и не с памятью.

## 3. BabyKusto собирается и исполняет KQL

72 ошибки компиляции (в step198 — ≈280) закрыты портами std:

- `Convert.To{Boolean,Int32,Int64,Double,String}(object)` поверх `ChangeType`,
  `decimal` как источник.
- `Math.Sign(double/float/nint)` (NaN → `ArithmeticException`, как BCL).
- `DateTime`: `Kind` в двух старших битах `_dateData` (имя поля как в BCL —
  каталог труб ключует поля по имени), конструкторы с `DateTimeKind`,
  `SpecifyKind`, `AddMonths`/`AddYears`/`AddMilliseconds`, `DayOfYear`;
  `Now` — Local, `UtcNow` — Utc, ISO с `Z` — Utc. Сравнение по тикам.
- `Array.GetValue(int)` — элемент упакован, как BCL.
- Консоль приложений: `ForegroundColor`/`BackgroundColor`/`ResetColor` (ANSI).
- `Type.Namespace`/`DeclaringType`/`IsNested`/`IsGenericParameter`/
  `GetArrayRank` — как NativeAOT без метаданных.
- `System.Linq.AsyncEnumerable.ToAsyncEnumerable` и `Empty` — порт из .NET 10.

Вырезы в BabyKusto (`SharpOS cut:`): `url_decode`/`url_encode_component`
(нет `System.Uri`), ключи `join`/`summarize` без `Uri.EscapeDataString`;
`matches regex`/`extract()` — со step198.

**KQLBABY.EXE** (`apps_native/KqlBaby`): таблица в памяти, 6/6 —
where/project/take, summarize count() by, sort, extend, contains, print.

## 4. `KQL 'запрос'` — стадия конвейера

`apps_native/KqlApp` → KQL.EXE. Сообщения входа — таблица `Input`; запрос,
начинающийся с оператора, применяется к ней. Схема из полей сообщений: имя —
столбец, тип — по всем значениям (целые long, с дробью real, текст string,
bool), отсутствующее поле — null, вложенные объекты — текстом. Ошибки запроса
— из Kusto.Language до исполнения, с позицией в тексте пользователя. Строки
результата уходят `Expando`.

```
PIPEGEN 100 | KQL 'where Level >= 3 | summarize n = count() by Level | sort by Level asc'
Expando { Level = 3, n = 20 }
Expando { Level = 4, n = 20 }
KQL: at 7: The name 'Levl' does not refer to any known column, table, variable or function.
```

Весь вход читается до запуска: `sort`/`summarize` его требуют, у BabyKusto
нет потокового источника.

## 5. Инструмент: серии загрузок без пересборки

`par_worker.sh` (scratchpad): эталонный `esp.pristine.img` после
`run_build.ps1 -NoRun`, QEMU напрямую с `-chardev file`, свой каталог и порт
QMP на поток — 4 параллельно. `run_build.ps1`, запущенный из bash, ломает
путь к mise с кириллицей (проверка инструментов: «нет lld-link»); сборка —
из PowerShell.

## 6. Файлы

- `OS/src/Boot/EH/HwFaultBridge.cs`, `OS/src/Boot/EH/ExInfo.cs`
  (`PopBelow`), `OS/src/PAL/SharpOSHost/SehDispatch.cs` — регрессия step199.
- `std-no-runtime/GC/OptionalFieldsReader.cs` (теги), `GC/MethodTable.cs`
  (`NullableValueOffset`), `GC/GcRuntimeExports.cs` (`RhBox`),
  `Runtime/UnboxHelpers.cs`, `OS/src/Kernel/Memory/InterfaceDispatchResolver.cs`.
- `std-no-runtime/`: `Runtime/Convert.cs`, `Runtime/Math.cs`, `DateTime.cs`,
  `Runtime/Array.cs`, `Runtime/Type.Statics.cs`, `Linq/AsyncEnumerable.cs`,
  `Std.props`, `Pipes/PipeProbeMessages.cs`; `apps_native/sdk/SystemConsole.cs`.
- `vendor/BabyKusto/` — вырезы, `PROVENANCE.md`.
- `apps_native/KqlBaby/` (из `.wip`), `apps_native/KqlApp/` (новое).
- `apps_native/Shell/autorun.sh` — 298 под стрессом, две строки KQL, KQLBABY.
- Документы: limits (KQL, `Nullable<T>`), README, donext.

## 7. Дальше

1. KQL: дополнение в оболочке (Tab), потоковый источник, столбцы `dynamic`.
2. Математика по процессору.
