# step 198 — Kusto.Language на голом железе; обобщённые виртуальные методы; async-потоки и JsonNode; SharpLibm

## Контекст

Нужен язык запросов для конвейера с дополнением и диагностикой — KQL.
Решено взять настоящий Kusto.Language (Microsoft), а не писать свой. Он
собирается на нашей std после серии портов BCL, но в ILC упирается в
обобщённые виртуальные методы (GVM): парсер Kusto — комбинаторы с
посетителями `Accept<TResult>`, а ILC компилирует такой вызов как
`TypeLoaderExports.GVMLookupForSlot` и оставляет ответ загрузчику типов,
которого у нас нет. Шаг — этот загрузчик в объёме, достаточном для Kusto.

Итог (QEMU, батарея 24/24): **GVMTEST 10/10, KQLTEST 7/7** — разбор и
связывание запроса по таблице, диагностика с позицией, автодополнение после
`where` и `|` (387 вариантов), раскраска.

Вторая половина шага (§6–7) — путь к исполнению KQL (BabyKusto): async-потоки,
`JsonDocument`/`JsonNode`, хвосты std; и `Math` целиком заменён своей libm —
**SharpLibm**, отдельный репозиторий-сабмодуль, правильно округлённый.

## 1. Kusto.Language в дереве

- `vendor/KustoLanguage/` — снимок microsoft/Kusto-Query-Language `6b5c2f7`
  (Apache-2.0, 12.4.1), только `src/Kusto.Language`, компилируется в
  приложение исходниками (`KustoLanguage.props`).
- Синтаксические узлы и грамматики команд upstream генерирует T4 и в git не
  держит: `tools/KustoGen` запускает те же генераторы (`generators/` из
  снимка) на хосте, результат лежит в `src/`.
- Вырезы (`SharpOS cut:`): `SyntaxFacts` — `Enum.GetValues` (метаданных нет)
  → наибольший вид из таблицы + 1; `ForwardParser` — `[ThreadStatic]` (нет
  потоковых статиков) → один счётчик глубины.
- `vendor/BabyKusto/` (MIT, исполнение KQL) лежит, но не собирается: нужны
  `JsonNode`, async streams, Regex. `apps_native/KqlBaby` припаркован.

Добито в std ради Kusto (порты BCL):
`System.Decimal` + `DecCalc` (8.0, с форматированием и разбором; 17 проверок
в AotTests), `ConcurrentDictionary` (10.0; 11 проверок), `Volatile`,
`object.GetType()`, `Type.Name/FullName` (как NativeAOT без метаданных:
`EETypeRva:0x…`), `Type.GetTypeCode`, `TypeCode`, `Convert.ChangeType`
(примитивы и строки), `Activator.CreateInstance<T>` (интринсики ILC),
`MathHelpers` (проверяемые преобразования double→int), `RhUnboxNullable`,
`bool.Parse/TryParse`, `string.Insert/Remove/LastIndexOfAny`,
`Array.BinarySearch`/`MaxLength`, `List<T>.BinarySearch`,
`HashSet(items, comparer)`, `ContainsAnyExcept`, необобщённые
`IComparer`/`IEqualityComparer`, `Encoding.GetString(byte*, int)`.

## 2. Компилятор ел память

`PublishAot=true` включает trim/AOT-анализаторы SDK; их поток данных
квадратичен по локалам: `GcBigFrame.cs` AotTests (4400 локалов) — csc
10–23 ГБ, общий сервер копил до 38 ГБ. Выключены (`EnableTrimAnalyzer`,
`EnableAotAnalyzer`, `EnableSingleFileAnalyzer` в `FreestandingPe.props` и
`OS.csproj`): AotTests — 8 с / 230 МБ. Наши анализаторы ни при чём
(`RunAnalyzers=false`, бисекция).

## 3. GVM — архитектура

`std-no-runtime/Runtime/GenericVirtualMethods.cs`; `TypeLoaderExports`
зовёт его; `Internal.NativeFormat` (читатель NativeFormat 8.0) перенесён в
std. Алгоритм — загрузчик типов NativeAOT 8.0 (`GVMLookupForSlotWorker`,
`ResolveGenericVirtualMethodTarget`, `FindMatchingInterfaceSlot`,
`TryGetGenericVirtualMethodPointer`, `GenericDictionaryCell`), но без его
системы типов: работает над MethodTable и таблицами образа.

Таблицы — секции RTR 300 + `ReflectionMapBlob`:

| id | таблица | для чего |
|---|---|---|
| 318 / 319 | (Interface)GenericVirtualMethodTable | кто переопределяет слот: типичный объявивший тип, типичный реализатор, имя+сигнатура |
| 336 | ExactMethodInstantiations | неразделяемый код — простой указатель |
| 322 | GenericMethodsTemplateMap | общий код над `__Canon` + раскладка словаря |
| 335 | GenericMethodsHashtable | словари, построенные ILC |
| 330 / 331 / 308 | NativeLayoutInfo / NativeReferences / CommonFixups | подписи и внешние ссылки (RELPTR32) |
| 332 / 302 | GenericsHashtable / ArrayMap | существующие инстанциации и массивы |
| 333 / 334 | NativeStatics / StaticsInfoHashtable | базы статиков для ячеек словаря |

Ход:
1. Слот — `RuntimeMethodHandle` → `RuntimeMethodHandleInfo` → подпись
   NativeLayout (объявивший тип, имя, сигнатура, аргументы). Свои ручки
   (для ячеек `MethodLdToken`) помечены младшим битом, как у загрузчика.
2. Обход базовых типов объекта: для класса — таблица 318, для интерфейса —
   319 (сигнатуры интерфейсов с подстановкой аргументов реализатора, второй
   проход — реализации по умолчанию); найденный класс-реализатор снова
   через 318.
3. Точный экземпляр (336) → код. Иначе шаблон (322), сопоставленный по
   канонической форме, и словарь: готовый (335) или собранный из раскладки.
   Ответ — толстый указатель `&{code, dictionary} + 2`.
4. Ячейки словаря: TypeHandle, InterfaceCall (блок из двух ячеек, 16 байт,
   как `RhNewInterfaceDispatchCell`, заглушка —
   `RhpInitialDynamicInterfaceDispatch` приложения → мост ядра),
   MethodDictionary (рекурсивно; словарь регистрируется до заполнения),
   StaticData, UnwrapNullable, MethodLdToken, AllocateObject (`RhpNewFast`).
   DefaultConstructor — заглушка, бросающая `NotSupportedException`.
5. Память под словари, дескрипторы и ячейки — управляемые `long[]` в
   корневом списке: сборщик не двигает объекты. Кэш (тип, слот) → ответ под
   `lock`.

`Rtr` задаёт `AppTypeManagerInit`, адрес заглушки — `AppRuntime`. В ядре
таблиц нет — вызов GVM там бросает исключение.

## 4. Ошибки по дороге

- Пустой словарь: `&storage[1]` у массива длины 1 → IndexOutOfRange
  (запасной элемент).
- Владелец шаблона — не сигнатура инстанциации, а готовый MT канонической
  формы (`Parser<__Canon,__Canon>`): сравнение MT по канонической форме —
  владелец по определению и аргументам, аргумент `__Canon` ≙ любой
  ссылочный тип. До фикса — «no code compiled for Accept» в автодополнении.
- Kusto глотает исключения внутри `GetCompletionItems` (0 вариантов без
  объяснений): KQLTEST при пустом ответе зовёт внутренний `KustoCompleter`
  напрямую и печатает исключение со стеком.
- `RhUnboxNullable` брал `RelatedType` — у `Nullable<T>` это базовый
  ValueType, приведение всегда падало бы; тип значения — первый
  обобщённый аргумент (`MethodTable.NullableType`).
- Таблица корней GC (256 слотов) переполнилась блоками статиков Kusto
  (fatal «GC root table full») → 4096.

## 6. К BabyKusto: async-потоки, JsonNode, хвосты std

- **Async-потоки** (работа агента из worktree `async-main`, перенесена
  патчем): `Task<T>`, `ValueTask[<T>]`, `IValueTaskSource`,
  `ManualResetValueTaskSourceCore`, построители `async ValueTask` и
  `async IAsyncEnumerable`, `IAsyncEnumerable`/`IAsyncDisposable`,
  `WithCancellation`/`ConfigureAwait`, `Task[<T>].ConfigureAwait`,
  `Stream.ReadAsync/WriteAsync/FlushAsync` (база синхронна).
  `OperationCanceledException` перенесён из `System.Threading` в `System`
  (инвариант 2). AotTests +5.
- **JsonDocument/JsonElement + System.Text.Json.Nodes** в
  `vendor/SystemTextJson`. Сериализатора нет: примитивы пишет
  `JsonValue.SharpOS.cs`, `Create<T>` — только примитивы;
  `Utf8JsonWriterCache` (`[ThreadStatic]`) → `ArrayBufferWriter` на вызов;
  ветки `ReadOnlySequence`/`DateTimeOffset`/разбор DateTime и Guid вырезаны.
  Вернулись decimal-члены читателя/писателя. JSONTEST 42 → 64.
- **std:** `OrderedDictionary<,>`, `ValueStringBuilder`, `Marvin`
  (постоянное зерно), `CollectionsMarshal`, `CollectionExtensions`,
  `EnumerableHelpers`, `Collection<T>`, `System.Random` (Xoshiro256**;
  `Shared` под замком вместо `[ThreadStatic]`), необобщённые `IDictionary`/
  `DictionaryEntry`, `ArgumentOutOfRangeException.ThrowIf*`,
  `CallerArgumentExpression`, атрибуты `Requires*`/`SuppressMessage`,
  `MemoryMarshal.Write(in T)`, `AsSpan/AsMemory(ArraySegment)`,
  `Memory.Empty`, `HashSet.EnsureCapacity`, `Utf8Parser/Formatter` для
  decimal, `Type.IsEnum/IsValueType/IsGenericType/GetGenericArguments…`,
  `Nullable.GetUnderlyingType`.
- **Баги std:** `Double`/`Single` без `IEquatable<T>` и `Equals(object)` —
  `EqualityComparer<double>.Default.Equals(2.5, 2.5) == false`,
  `Dictionary<double,…>` не находил ключ (порт из BCL, AotTests +2);
  `Dictionary` бросал одно `InvalidOperationException` на всё — теперь
  исключения BCL, `Values.Contains` был Halt.

BabyKusto всё ещё не собирается: ≈280 ошибок — пробелы std (`Convert.ToX`,
`DateTime.Kind`, `Array.GetValue`, `Uri`, `Console.ForegroundColor`,
`Type.DeclaringType`) и до §7 — `Math` (t-digest, гео-функции).

## 7. SharpLibm — libm на C#

Своего `Math` хватало на `Sqrt`/`Abs`; остальное было рядами на глаз.
Решение — отдельная библиотека, ничего не знающая про SharpOS:
github.com/daniilvaino/SharpLibm (MIT), сабмодуль `SharpLibm/`, исходниками
в std через `SharpLibm.props`. Тесты — отдельный репозиторий
github.com/daniilvaino/SharpLibm-tests (наборы под чужими лицензиями).

Состав:
- точные операции — свои по донорам: округления (Go), `fmod` (Cosmos gen3,
  e_fmod), `remainder` (Go/msun), `remquo` (musl через GeographicLib.NET),
  `frexp/ldexp/ilogb/nextafter…`; C-имена и C-семантика;
- трансцендентные — CORE-MATH (правильное округление) через C#-порт
  CoreMathSharp 1.0.0: 47 double + 48 float, программная `fma`;
- под NoStdLib: records → структуры, диапазоны → `Slice`, интринсики
  выключены, таблицы — `ReadOnlySpan<T> = [...]` (блоб в образе), без
  статических конструкторов и кучи.

**CoreMathSharp не правильно округлён.** Проверка на худших случаях
CORE-MATH (`.wc`, ≈30 млн входов против MPFR через gmpy2) и полный перебор
binary32 (2³² против C CORE-MATH, собранного в DLL) дали 28 double- и
7 float-функций с ошибками. C-оригинал проходит всё — значит, перевод. Около
80 ошибок, найдены трассировкой C ↔ C# (WSL gcc) семью агентами по группам:
- pow: 128-битное сложение клало сумму туда, где в C перенос, — весь
  третий этап Зива неверен, `pow(0.84, 0.987) = 0.62`;
- tgamma: таблицы читались наполовину, `tgamma(1.0018) = 1.93`; lgamma: шаг
  по парным таблицам, потерянный минус в таблице;
- log1p: `1 + bits(x)` вместо `1 + x`; expm1: пропущен `ldexp`; exp2:
  субнормальные из битового образа; exp: приоритет `&` и `>>`;
- atanh: сдвиг `0x3ff << 52` в int → IndexOutOfRange; asin/atan2: 128-битные
  сдвиги на 0 и ≥ 64 (C# маскирует счётчик сдвига);
- sin/cos: `Mul21` от pow вместо своего (показатель на 1 больше); tan:
  выпавший член приведения; rsqrt: знак расширен на 64 бита из 128;
- erf/erfc, cosh/sinh, acosh, asinh, log2, atanpi: `*` вместо `+`, `=`
  вместо `+=`, не та переменная, скобки Горнера;
- float: rsqrtf/exp10f/exp2m1f/log1pf/lgammaf/log2p1f/log10p1f.

Плюс исправления CORE-MATH после января 2026 (hypot `midm`, cospi, точные
случаи pow, границы погрешности). Все места — `// SharpLibm fix:`.

Итог: double — бит в бит с MPFR на всех худших случаях (40 функций); float —
бит в бит с C на всех 2³² входах (36) и 2²⁸ парах (5); libc-test: точные
операции бит в бит, трансцендентные ≤ 0.5 ulp.

В SharpOS:
- `Math`/`MathF` в std — тонкие переходы на `Libm` (`Round` = roundeven как
  в BCL, `IEEERemainder`, `FusedMultiplyAdd`, `ScaleB`, `BitIncrement`…);
  `Math.Sqrt` — `[Intrinsic]` → `sqrtsd`; `RhpDblRem/RhpFltRem` → `fmod`.
- `LibmPatcher` (std) — ускорение, не зависимость: при SSE4.1 переписывает
  входы floor/ceil/trunc/rint/nearbyint (+f) на `roundsd/roundss`, при FMA —
  fma/fmaf на `vfmadd`. `CpuFeatures` (ядро) читает CPUID; FMA не включается
  (XCR0 = x87|SSE, VEX дал бы #UD). Флаги — в хвост `AppServiceTable`,
  `AppRuntime` патчит приложение тем же патчером. modf пока не патчится.
- Образ без нативной libm; +650 КБ при всех экспортах.

## 8. Проверка

QEMU, 2 ГиБ, autorun: 24/24 (`[sh] done ran=24 failed=0`), AotTests 286
(+11 ConcurrentDictionary, +17 decimal), GVMTEST 10, KQLTEST 7, launcher
batch 1/0. Цифры под TCG: глобальное состояние Kusto 266 мс, первый разбор +
связывание 3,2 с (холодные разрешения GVM, словари, статики), дополнение
325 мс.

Итоговый прогон (после §6–7): 24/24, AotTests 297/297 (+5 async, +2 double,
+4 libm), JSONTEST 64, GVMTEST 10, KQLTEST 7, qemu64 без SSE4.1
(`libm entries replaced: 0`); с `-cpu qemu64,+sse4.1` ядро переписало 10
входов, AotTests проходит. SharpLibm на хосте — см. §7.

Прогоном раньше — однократный #DF ядра на `--pipe-stress 2`:
`KernelGcPreciseWalk.RunFromThrowSite` → #GP → порча кучи
(`HeapBlockOps.Split`) → переполнение ядерного стека. Повтор чистый; в
donext (лог `C:/work/OS-archive/run_df_pipestress.log` (вне репозитория)).

## Файлы

- `std-no-runtime/Runtime/GenericVirtualMethods.cs`, `TypeLoaderExports.cs`,
  `NativeFormat/*` (новые); `GC/GcRoots.cs`, `GC/GcStaticsInit.cs`,
  `GC/GcRuntimeExports.cs`, `Runtime/UnboxHelpers.cs`, `Text/Encoding.cs`,
  `Std.props`.
- Порты std: `Number/Decimal*.cs`, `Runtime/Math.Decimal.cs`,
  `Bcl/MidpointRounding.cs`, `Bcl/ConcurrentDictionary.cs`,
  `Threading.Volatile.cs`, `Runtime/{Activator,Convert,Type.Statics,TypeCode,
  CompilerHelpers.MathHelpers}.cs`, `Bcl/Boolean.Statics.cs`,
  `Runtime/CompilerServices/DecimalConstantAttribute.cs` и правки
  коллекций/строк/чисел.
- SDK: `AppRuntime.cs`, `InterfaceDispatchTrampoline.cs`, `MinimalRuntime.cs`
  (оба яруса: `Type`/`Boolean` partial, `GetType()`), `FreestandingPe.props`,
  `OS/OS.csproj` (анализаторы).
- `vendor/KustoLanguage/`, `vendor/BabyKusto/` (+ PROVENANCE), `tools/KustoGen`,
  `tools/GvmProbe` (печать таблиц GVM/шаблонов/словарей образа).
- `apps_native/GvmTest`, `apps_native/KqlTest`, `apps_native/KqlBaby`
  (припаркован), `apps_native/Shell/autorun.sh`, `AotTests`,
  `TriCNES/Compat/Convert.cs` (partial).
- §6: `std-no-runtime/Threading.Tasks*.cs`, `AsyncStreams.cs`,
  `ValueTaskAwaiter.cs`, `ThrowHelper.{Tasks,CoreLib,Collections}.cs`,
  `Bcl/{OrderedDictionary,Collection,CollectionExtensions,EnumerableHelpers,
  Dictionary,HashSet,List,Interfaces}.cs`, `Random/*`, `Marvin.cs`,
  `ValueStringBuilder.cs`, `CollectionsMarshal.cs`, `Exceptions.Derived.cs`,
  `IO/Stream.cs`, `Primitives.Equality.cs`; `vendor/SystemTextJson`
  (Document, Nodes); `apps_native/JsonTest`, `AotTests/AsyncShape.cs`.
- §7: сабмодуль `SharpLibm` (`.gitmodules`), `Std.props` (импорт),
  `Runtime/Math.Double.cs`, `Runtime/MathHelpers.cs`,
  `Runtime/LibmPatcher.cs`; `OS/src/Hal/CpuFeatures.cs`,
  `Boot/BootSequence.cs`, `Kernel/Process/AppServiceTable.cs`,
  `AppServiceBuilder.cs`; SDK `AppServiceTable.cs`, `AppRuntime.cs`;
  AotTests `CheckLibm`.
- Документы: limits (GVM, `new T()`, decimal, ConcurrentDictionary, Math,
  async-потоки, JsonNode), README, donext.

## Отложено

- Хвосты GVM: вариантная интерфейсная диспетчеризация, unboxing-заглушки
  для структур, конструктор по умолчанию в словаре, типы вне образа.
- Потоковые статики (`[ThreadStatic]`) — отдельный пункт donext.
- BabyKusto: Regex и пробелы std (§6).
- SharpLibm: функции Бесселя, `erfinv`; патч `modf`; наборы glibc,
  llvm-libc, TestFloat в репозитории тестов не подключены. Ошибки
  CoreMathSharp — отправить автору.

## Следующий шаг

Дополнение KQL в оболочке (`KQL '…'` + Tab) и исполнение запроса поверх
трубы; VIEWFILT → FILTER.
