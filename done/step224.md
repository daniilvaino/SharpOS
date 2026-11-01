# step 224 — System.Text.Json целиком, `JsonSerializer` через генератор источников

## Почему

Релиз 0.1, раздел 2 — форматы в std. JSON был вырезкой в `vendor/SystemTextJson`:
ридер, писатель, `JsonDocument`, `JsonNode`; без сериализатора, без
`JavaScriptEncoder`, без `ReadOnlySequence`. План раздела — STJ в std,
`JsonSerializer` генератором источников. По правилу проекта библиотека взята
целиком, недостающее добавлено в std, а не вырезано из неё.

## Что сделано

- **STJ форка (.NET 10) целиком** — `std-no-runtime/Text/Json` (317 файлов), с
  зависимостями `Text/Encodings.Web` (21) и `IO/Pipelines` (32). Подключение —
  строкой `std-no-runtime/Text/Json/Json.props`, ярус приложений. Вырезы и
  причины — `std-no-runtime/Text/Json/README.md`: `Int128`/`UInt128`, F#,
  Reflection.Emit, горячая перезагрузка, целочисленный `Regex` (тот же шаблон
  руками, `SHARPOS_REGEX`), `Vector<T>`-цикл Encodings.Web, пулящий построитель
  async в Pipelines. `vendor/SystemTextJson` удалён; потребители (JsonTest,
  DataTest, ConvertApp, JsApp, BabyKusto) — на новом props. В BabyKusto вернулся
  `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` в выводе таблиц.
- **`JsonSerializer` без отражения.** Метаданных в образе нет. Контракт типа
  даёт `JsonSerializerContext` (`[JsonSerializable]`) — так upstream работает с
  выключенным `IsReflectionEnabledByDefault`. Отражательная половина
  `DefaultJsonTypeInfoResolver` заменена `DefaultJsonTypeInfoResolver.SharpOS.cs`:
  он отвечает за типы со встроенным конвертером значения (числа, строки, даты,
  `Guid`, `JsonElement`, `JsonNode` — членов искать не нужно), на остальное —
  `NotSupportedException` с подсказкой. Переключатель оставлен со значением
  upstream (`true`).
- **Свой генератор** — `generators/Json.Generator`, исходники генератора из
  форка, сборка `SharpOS.Json.Generator`; `PROVENANCE.md` там же.
- **std под STJ** (всё по BCL 8 или форку):
  - формы `System.Reflection` (`MemberInfo`, `PropertyInfo`, `FieldInfo`,
    `MethodInfo`, `ConstructorInfo`, `ParameterInfo`, `BindingFlags`,
    `CustomAttributeExtensions`), `Type : MemberInfo` с тем, что знает
    MethodTable (`IsClass`, `IsAbstract`, `IsSealed`, `GetInterfaces`, …);
    поиск членов — `null`/пусто. `Activator.CreateInstance(Type)` для значимых
    типов, `Assembly.GetType`;
  - `Enum.GetNames`/`GetValues` (пустые), `Type.GetTypeCode` перечисления —
    по нижележащему типу, `Convert.ChangeType` в перечисление — `InvalidCast`;
  - `MemoryManager<T>`, `ReadOnlySequence<T>`, `SequenceReader<T>`,
    `SequencePosition`, `BuffersExtensions`; `Memory`/`ReadOnlyMemory` с
    владельцем `MemoryManager`; `MemoryMarshal.TryGetMemoryManager`/`TryGetString`;
  - `Utf8Parser`/`Utf8Formatter` для дат, `Guid`, `TimeSpan`; `Utf8` (скалярно,
    через `Rune`), `Ascii` регистр на месте, `BitOperations.PopCount`/`TrailingZeroCount`;
  - `Version`, `Guid` равенство и сравнение, `ValueTuple<T1,T2> : IComparable`,
    `BitArray` (BCL 8 целиком), `DBNull`, `AggregateException`,
    `Exception.GetBaseException`, `ConcurrentStack`, `ExecutionContext`
    (ничего не течёт), `ThreadPool.QueueUserWorkItem` (поверх `Task.Run`),
    `MemoryExtensions.Sort(keys, items)`/`CommonPrefixLength`, атрибуты
    `FeatureSwitchDefinition`/`GeneratedCode`.
- **JSONTEST** расширен сериализатором (`apps_native/JsonTest/Serializer.cs`):
  объект с `[JsonPropertyName]`, `[JsonIgnore]`, списком, словарём, `int?`,
  `DateTime`, `Guid`, `TimeSpan`, массивом, перечислением; запись `record`;
  контекст с camelCase и отступами; туда и обратно; ошибка без контекста.

## Находки

- **SDK подставлял свой генератор.** Каждое приложение получает генератор STJ
  из targeting pack (`microsoft.netcore.app.ref` 8.0.25). Он узнаёт атрибуты
  членов, только если их сборка называется `System.Text.Json`; у нас STJ
  компилируется в само приложение, и `[JsonPropertyName]`/`[JsonIgnore]`
  молча пропускались — сериализация шла, имена были не те. Генератор того же
  имени из проекта SDK отбрасывает: анализаторы дедуплицируются по имени
  файла. Отсюда своя сборка `SharpOS.Json.Generator`, снятие анализатора SDK
  целью в `Json.props` и правка в генераторе: атрибут STJ узнаётся и по
  пространству имён `System.Text.Json.Serialization`. Проверено по
  командной строке csc (`/analyzer:` — только наш) и по сгенерированному коду.
- **`Type.GetTypeCode` отдавал `Object` для перечислений** → STJ считал
  перечисление неподдерживаемым типом. Теперь код берётся из `ElementType`
  MethodTable.
- **`JsonValue.Create<long>` требовал резолвер отражения** (JSONTEST упал с 134;
  символизировано по образу приложения) → ветка конвертеров значений.
- **Сообщения об ошибках печатали `'(object)'`** вместо имени токена: `SR.Format`
  форматирует перечисление через `ToString`, имён нет. Свой `SR` STJ (все 240
  ключей, ближе std-ного `System.SR`) называет `JsonTokenType`/`JsonValueKind`
  сам.

## Проверка

Рабочая копия, WHPX, `-SkipCoreClr`, прогон 236. Все приложения собраны,
предупреждений 0. С тестами `ran=46 failed=2`: JSONTEST 72/0, DATATEST 239/0 и
236/0, KQLTEST 7/0, COMPRESSTEST 44/44, GVMTEST 10/0, PIPEPERF 20/0, PROCTEST
84/1 и 80/0, AOTTESTS ×3. Два сбоя — не этого шага:
- PROCTEST 15 (доля времени счётчика рядом с собирающим процессом) — временная
  проверка, падала так же в прогоне 230, в 233 прошла;
- добавленная строка `KQL "summarize … by Level"` над Expando — код 2, ошибка
  запроса: группировать по `dynamic` KQL не даёт, нужно `by tolong(Level)`.
Потребители JSON вне тестов: `CONVERT --to jsonl`/`--from jsonl`/`--to json`
через трубу и файл, `JS.EXE -e "JSON.stringify(…)"` — вывод верный. `-NoTests`
6/6.

## Не сделано

- Перечисления строками (`JsonStringEnumConverter`) — нужны имена членов,
  метаданных нет.
- Сериализация без контекста, полиморфизм по отражению.
- Regex — следующий пункт раздела 2; вернёт целочисленный шаблон STJ.

## Файлы

std: `Text/Json/**`, `Text/Encodings.Web/**`, `IO/Pipelines/**` (новые);
`Buffers/*`, `Buffers/Text/Utf8Parser/*`, `Buffers/Text/Utf8Formatter/*`,
`Bcl/{AggregateException,DBNull,Guid.Equality,SequencePosition,ThrowHelper.Memory,Version}.cs`,
`Collections/Concurrent/{ConcurrentStack,CDSCollectionETWBCLProvider.SharpOS}.cs`,
`Runtime/{Activator.Type,Enum.Names,MemoryExtensions.Sort,Reflection.Members,Type.Reflection}.cs`,
`Runtime/CompilerServices/*`, `Number/Primitives.BitOperations.cs`,
`Text/Ascii.CaseInPlace.cs`, `Text/Unicode/Utf8.cs`,
`Threading.{ExecutionContext,ThreadPool}.cs`, `SR.Collections.cs` (новые);
`Bcl/{BitArray,Memory,ReadOnlyMemory}.cs`, `Exception.cs`,
`Runtime/{Convert,MemoryExtensions.Search,MemoryMarshal.TryGetArray,Reflection.Assembly,ThrowHelper.Tasks,Type.Statics,ValueTuple}.cs`,
`SR.IO.cs`, `Std.props`. Генератор: `generators/Json.Generator/` (новый). vendor:
`SystemTextJson/` (удалён), `BabyKusto/` (props, `TableSourceExtensions.cs`,
`PROVENANCE.md`). Ядро: `OS/src/Boot/MinimalRuntime.cs`. SDK:
`MinimalRuntime.cs`. Приложения: csproj JsonTest, DataTest, ConvertApp, JsApp;
`JsonTest/Program.cs`, `JsonTest/Serializer.cs` (новый). Документы:
`README.md`, `release.md`, `donext.md`, `docs/nativeaot-nostd-kernel-limits.md`,
`docs/build-warnings.md`.
