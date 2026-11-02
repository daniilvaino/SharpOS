# step 225 — Regex целиком, `[GeneratedRegex]`, TurboXml в std

## Почему

Релиз 0.1, раздел 2 — форматы в std. Regex был самым нужным пробелом: под
`SHARPOS_REGEX` или вырезанными стояли `matches regex` и `extract()` в BabyKusto,
тома многотомных архивов в SharpCompress, шаблон целых чисел в STJ и восемь
`[GeneratedRegex]` Okojo (шли через его движок ECMAScript). XML — по решению
пользователя: TurboXml хватает, выносим его из ядра в std; порт `System.Xml` —
по первому потребителю.

## Что сделано

- **System.Text.RegularExpressions форка (.NET 10) целиком** —
  `std-no-runtime/Text/RegularExpressions` (80 файлов): интерпретатор,
  NonBacktracking (`Symbolic/`, 9 тыс. строк — чисто управляемый, взят, а не
  вырезан). `RegexOptions.Compiled` идёт интерпретатором
  (`RuntimeFeature.IsDynamicCodeCompiled` = false, как у NativeAOT). Вырезы —
  `README.md` рядом: компиляторы (Reflection.Emit), `CompileToAssembly`,
  сериализация, `IEnumerator<ValueMatch>` у ref struct-перечислителя. Подключение —
  `Regex.props`; его тянут `Json.props` и `SharpCompress.props`.
- **`[GeneratedRegex]`** — свой генератор `generators/Regex.Generator`
  (исходники форка, сборка `SharpOS.Regex.Generator`). Генератор SDK пишет
  `protected override` для `CreateInstance`/`Scan`, а у нас библиотека в той же
  сборке, что и сгенерированный код: член `protected internal` своей сборки
  переопределяется только `protected internal` (CS0507). Одна правка в
  эмиттере; `Regex.props` снимает генератор SDK.
- **Потребители вернулись к апстриму:** SharpCompress (три файла томов —
  байт в байт с апстримом), BabyKusto (`matches regex`, `extract()`), STJ
  (`JsonHelpers` — копия форка), Okojo (`OkojoShims.cs` удалён, его
  `[GeneratedRegex]` генерирует наш генератор).
- **std под Regex** (BCL или форк):
  - `SearchValues<string>`: Aho-Corasick, Rabin-Karp, запасные; векторные
    Teddy/ThreeChars вырезаны (нужны `Vector512`/`Avx512*`, `PackedSpanHelpers`),
    как уже было с `IndexOfAnyAsciiSearcher`; `AsciiState`;
  - `Globalization/Ordinal` (ветки инвариантного режима), `InvariantModeCasing`,
    `SurrogateCasing`, `TextInfo.ToUpperOrdinal`/`ToUpperAsciiInvariant`,
    `GlobalizationMode.UseNls`, `Ascii.EqualsIgnoreCase`,
    `Utf16Utility.*OrdinalIgnoreCaseAscii`;
  - `string : IComparable, IComparable<string>, IEquatable<string>` (было без
    интерфейсов — `SearchValues<string>` не компилировался), `CompareTo(object)`
    с не-строкой бросает, а не `Halt()`; `GetRawStringDataAsUInt16/UInt8`;
  - `StringBuilder.GetChunks`, `Task.Run<TResult>`, `List.TrueForAll`,
    `HashSet.CopyTo(T[])`, `ValueListBuilder.Pop`, `HashCode.Combine` до восьми,
    `MemoryExtensions.IndexOfAnyExcept`/`LastIndexOfAnyExcept` по набору и по
    трём, `IndexOfAny`/`ContainsAny` по `SearchValues<string>`,
    `Span.Sort(Comparison)`, `Unsafe.IsAddress{GreaterThanOrEqualTo,LessThanOrEqualTo}`,
    `SpanHelpers.SequenceEqual(ref byte…)`, `ReadOnlySpan(ref readonly T)`
    (форма BCL 8), `ThrowHelper`-члены;
  - SDK: `AppContext.GetData`, `RuntimeFeature.IsDynamicCode{Supported,Compiled}`.
- **TurboXml в std обоих ярусов** — `vendor/TurboXml/TurboXml.props` импортирует
  `Std.props` (было: только `OS.csproj`). Проверка в AOTTESTS.
- **REGEXTEST** (`apps_native/RegexTest`): одни и те же проверки на
  интерпретаторе, `Compiled` и NonBacktracking; обратные ссылки, просмотры,
  атомарные группы, балансирующие группы, RightToLeft; ошибки разбора, таймаут,
  отказ NonBacktracking от обратной ссылки; `[GeneratedRegex]` с шаблонами Okojo.

## Находки

- **`Environment.TickCount64` в приложении был нулём**, если приложение не
  запускало пул задач: часы ставил только `TaskBackendInstaller.Install()`.
  Таймаут Regex — дедлайн в `TickCount64` — не наступал никогда (REGEXTEST:
  катастрофический шаблон не прерывался; на хосте .NET 10 — таймаут за 77 мс).
  Та же история, что с `DateTime` в step208: теперь часы ставит
  `AppRuntime.Initialize` всем.
- **`Array.Empty<T>()` выделял новый массив на каждый вызов.** В BCL это один
  экземпляр на тип, и код на это полагается: Aho-Corasick узнаёт узел без детей
  по `ReferenceEquals(_children, Array.Empty<int>())` — у нас сравнение было
  ложным, и второй ребёнок приводил `int[]` к `Dictionary` →
  `InvalidCastException` в статическом конструкторе сгенерированного кода
  (символизировано по образу: `AhoCorasickNode.AddChild` ← `StringSearchValues.Create`
  ← `Utilities..cctor`). В приложениях теперь `EmptyArray<T>.Value`, как в BCL;
  в ядре — по-прежнему новый массив (ярусная пара `Runtime/Array.Empty.*`): GC-статики
  ядра появляются с Phase 2, а std исполняется раньше.
  Первая попытка — общий код с флагом «статики готовы» — повесила Shell при
  старте (прогон 240, `GcHeap.Fatal` «object allocated before the GC heap
  exists», снято с живого QEMU): чтение изменяемой статики лишило ILC
  предвычисления всех `static readonly … = Array.Empty<…>()`, у `Type` появился
  статический конструктор, и запись `Type.ModuleBase` в `AppRuntime.Initialize`
  запустила его до кучи. BCL-форма предвычисляется целиком — конструкторы не
  нужны вовсе.
- **Генератор SDK** находит `[GeneratedRegex]` по полному имени типа — в отличие
  от генератора JSON, атрибут он видит; сломался только модификатор доступа.
- В JS ошибки уходят в stderr, а он в лог прогона не попадает — для разбора
  `2>&1`.

## Проверка

Рабочая копия, WHPX, `-SkipCoreClr`, прогон 241. Все приложения и ядро собраны,
предупреждений 0. С тестами `ran=50 failed=1`: REGEXTEST 93/0 (три движка,
таймаут, `[GeneratedRegex]`), AOTTESTS ×3 с проверкой TurboXml, JSONTEST 72/0,
DATATEST 239/0 и 236/0, KQLTEST 7/0, COMPRESSTEST 44/44, GVMTEST 10/0, PIPEPERF
20/0, PROCTEST 84/1 и 80/0 — сбой тот же, что в 230/236/237: проверка 15,
временная (доля времени счётчика). Через оболочку: KQL `matches regex` → 10 из
20, `extract()` → `"345"`, `summarize by L = tolong(Level)` над JSON Lines; JS
`Date.parse('2024-01-02T03:04:05Z')` → 1704164645000 (как .NET на хосте),
`parseFloat('  -1.5e3xyz')` → −1500 — оба через `[GeneratedRegex]` Okojo.
`-NoTests` 6/6.

## Не сделано

- Regex в ядре (`.env` ядра — ручным сканированием).
- Векторные пути `SearchValues<string>`.
- Порт `System.Xml` — по первому потребителю (donext).
- Кодовые страницы — следующий пункт раздела 2.

## Файлы

std: `Text/RegularExpressions/**` (новые: копия форка, `Regex.props`,
`README.md`, `SR.RegularExpressions.cs`), `Buffers/SearchValues/Strings/**`
(новые), `Globalization/{Ordinal,InvariantModeCasing,SurrogateCasing}.cs`,
`Text/StringBuilder.Chunks.cs`, `GC/GcStaticsState.cs` (новые);
`Buffers/SearchValues/{SearchValues,IndexOfAnyAsciiSearcher}.cs`,
`Globalization/{TextInfo,GlobalizationMode}.cs`, `StringComparison.cs`,
`Text/{Ascii,StringBuilder}.cs`, `Text/Unicode/Utf16Utility.cs`,
`Runtime/{Array,ReadOnlySpan,Span,Unsafe,MemoryExtensions.Search,MemoryExtensions.SearchValues,MemoryExtensions.Sort,ThrowHelper.Tasks}.cs`,
`Bcl/{HashSet,List,HashCode,ThrowHelper.CoreLib}.cs`, `Number/ValueListBuilder.cs`,
`Threading.Tasks.{Continuations,AppServices}.cs`, `GC/GcStaticsInit.cs`,
`SR.Collections.cs`, `Std.props`; `Text/Json/{Json.props,README.md}`,
`Text/Json/System/Text/Json/JsonHelpers.cs`. Генератор: `generators/Regex.Generator/`
(новый). vendor: `TurboXml/{TurboXml.props,PROVENANCE.md}`,
`SharpCompress/{SharpCompress.props,PROVENANCE.md}` и три `*VolumeFactory.cs`,
`BabyKusto/{BabyKusto.props,PROVENANCE.md}`, `BuiltInOperators.cs`,
`BuiltInScalarFunctions.cs`, `Okojo/PROVENANCE.md`, `Okojo/SharpOS/OkojoShims.cs`
(удалён). Ядро: `OS/OS.csproj`, `OS/src/Kernel/Memory/GcStaticsMaterializer.cs`.
SDK: `AppRuntime.cs`, `MinimalRuntime.cs`. Приложения: `RegexTest/` (новое),
`AotTests/FormatTests.cs`, `JsApp/JsApp.csproj`. Документы: `README.md`,
`release.md`, `donext.md`, `docs/nativeaot-nostd-kernel-limits.md`,
`docs/build-warnings.md`.
