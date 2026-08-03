# step 146 — терминальный движок компилируется в ядро (AOT → своя std → OS.csproj)

## Контекст

steps 144–145 довели форк XtermSharp до 74/76 на xterm.js и чистых байтовых корпусов, но
всё это на взрослом .NET с JIT. Вопрос шага: доедет ли движок до ядра вообще — три ступени
подряд, без попытки его там запустить.

## Ступень 1 — NativeAOT на полной BCL (`work/TermRace/aot`)

Собралось почти сразу: 1.5 МБ нативного образа, движок исполняется, ноль trim/AOT-варнингов
(значит ни рефлексии, ни динамического кодогена в горячем пути нет).

Две ловушки окружения:

- `AllowUnsafeBlocks` в форке стоял под `Condition="'$(Configuration)|$(Platform)' ==
  '*|AnyCPU'"`, поэтому любая сборка с явным RID падала на `CS0227`. Сделан безусловным.
- ILC не находит линкер: `findvcvarsall.bat` внутри пакета зовёт `vswhere.exe`, которого нет
  в PATH, и результат его ошибки попадает в командную строку линкера (`exited with code
  123`). Лечится штатным `-p:IlcUseEnvironmentalTools=true` под `vcvars64` — тогда берётся
  `link.exe` из окружения. Это ровно та же грабля, что в памяти про `run_build.ps1`.

## Ступень 2 — NativeAOT против нашей std

Проба собиралась по рецепту `apps_native/sdk/FreestandingPe.props`, то есть ответ переносится
на ядро один в один. Дыр оказалось восемь, и они честно разделились надвое.

### В std — портом из dotnet/runtime v8.0.27

Канонические BCL-типы, поэтому не самоделки в форке (это была первая моя попытка, и она была
неправильной):

| что | обрезано |
| --- | --- |
| `System.Collections.BitArray` | SIMD-пути `And/Or/Xor/Not` + `CopyTo` (Vector128/Avx2 у нас нет), ctor'ы из массивов, `Clone`, энумератор, `_version` |
| `System.ValueTuple` арности 1–5 + `TupleElementNamesAttribute` | арности 6–8 и `TRest`, структурные компараторы, `ToString` |
| `Array.CopyTo(Array, int)` | проверка `Rank != 1` |
| `List<T>.ForEach` | проверка `_version` |
| `string.StartsWith(string, StringComparison)` | культурные ветки (std Ordinal-only by design) |

Без `ValueTuple` не компилируется ни один метод, возвращающий кортеж — а в движке их два.
`Array.CopyTo` пришлось класть на `System.Array`: generic-версия в `ArrayT.cs` уже была, но
компилятор для `data.CopyTo(...)` смотрит на базовый `System.Array`.

Оба списка подключены и в `OS.csproj`, и в `FreestandingPe.props`.

### В форке — то, что не BCL, а его собственные решения

- **NStack убран целиком.** `CharData.Rune` стал обычным кодпойнтом `int`, `ustring` →
  `string`, декодирование UTF-8 переехало в собственный `RuneExt.DecodeRune`. Это то, что
  планировалось при выборе движка; батарея после — те же 74/76, ноль регрессий.
- `Enum.HasFlag` → битовые тесты (прецедент самого ядра: `NativeAotProbe` документирует ту
  же замену).
- Таблица `wcwidth` из `uint[,]` в плоский `uint[]` пар: многомерные массивы требуют
  рантайм-поддержки, которой в ядре нет.
- `Terminal.GetEnvironmentVariables` вынесен в `Terminal.Environment.cs` — единственное
  место с `System.Environment`, нужно только хостовой сборке.
- `Encoding.Default` → `Encoding.UTF8`.

### app-SDK — `ByRefAssignRefStub`

ILC требует `RhpByRefAssignRef` для byref-копий структур со ссылками (движения элементов
`List<T>`). В отличие от диспетчера интерфейсов, хендофф из ядра не нужен: наш GC
неперемещающий и без барьеров, поэтому приложение само вписывает те же 15 байт, что и ядро
(`mov rcx,[rsi]; mov [rdi],rcx; add rdi,8; add rsi,8; ret`). Установка — в `AppRuntime`
рядом с патчем диспетчера.

Результат ступени: freestanding win-x64 PE, 186 КБ, слинкован.

## Ступень 3 — компиляция в ядре

`OS.csproj` включает движок глобом, как `vendor/Iced` и `vendor/PeNet`. Исключены `Pty`,
`Terminal.Environment`, `SelectionService`, `SearchService`.

Не хватило одного — `Console`. И вот тут правильный ответ оказался не «дать ядру
`System.Console`»: **консолей в ядре несколько** (HAL-консоль, framebuffer, UART), и какая
активна — решается по ходу загрузки. Поэтому движок больше не выбирает: добавлен
`TerminalLog.Writer`, обычное статическое поле без инициализатора (cctor-ловушка), которое
хост при желании заполняет. По умолчанию диагностика движка отбрасывается.

`dotnet build OS/OS.csproj` — `Build succeeded, 0 errors`. Полная сборка образа (ILC +
линковка + EFI) — за пользователем.

## Что это НЕ значит

Компилируется ≠ работает. В ядре движок пока ничем не используется, и до запуска есть
жёсткая зависимость по фазам загрузки:

- `Color.DefaultAnsiColors` — это `static List<Color>` со статическим конструктором, и его
  читает `MatchColor` на каждом truecolor-SGR. До материализации GC-статики в конце
  **Phase 2** это та самая cctor-ловушка, то есть движок физически не запустится раньше.
- Рисовать некуда до **Phase 3** (identity-map GOP).

Значит точка включения — начало Phase 4, после того как GC/EH-пробы отработали.

## Следующий шаг

Фронт-энд `TerminalConsole` в ядре: `Platform.WriteChar` кормит движок вместо `FbTty`,
рендер только грязных строк через `GetUpdateRange`, `Serial` остаётся всегда как страховка
(если движок сломается, лог по UART должен выжить). Нагрузка по возрастанию —
самогенерируемый прогон, потом реплей записи реальной сессии с ESP, оракул через
`FbConsole.Checksum` как в Phase B#2.

## Файлы

- `std/no-runtime/shared/Bcl/BitArray.cs`, `std/no-runtime/shared/Runtime/ValueTuple.cs` — новые.
- `std/no-runtime/shared/{Runtime/Array,Bcl/List,StringQueries,SystemString}.cs` — дополнены.
- `apps_native/sdk/ByRefAssignRefStub.cs` + `AppRuntime.cs` + `FreestandingPe.props`.
- `vendor/XtermSharp/` — снятие NStack и остальные правки тира, таблица в `PROVENANCE.md`.
- `OS/OS.csproj` — движок и два новых std-файла.
- `work/TermRace/aot/` — AOT-проба (ступень 1). Проба ступени 2 удалена как одноразовая.
