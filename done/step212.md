# step 212 — LUA и JS: два языка стадиями конвейера; std дорос до Okojo (даты, Uri, Task, глобализация, Array как IList); RDRAND; нумерованные концы вывода; гонка ввода клавиатуры

## Контекст

Шаг начался с хвостов труб из step211: вывод программы — несколько каналов
(объекты, stderr, stdtext), оболочке нужны bash-перенаправления `3>&1 1>&-`.
Потом вопрос «как превратить дерево SYSINFO в каталоги» упёрся в то, что KQL
не умеет рекурсии (`work/reports/Языки обработки объектов.md`). Решили взять
готовые языки на C#: Lua-CSharp (nuskey8) и Okojo (akeit0, ECMAScript). Lua
встал на std почти сразу. Okojo не собирался: 278 ошибок, около 80 API, которых
нет в std. По правилу «добить, а не вырезать» std дорос до него, а не наоборот.
Итог — демо:

```
pipegen 20 | lua -e "return function(m) m.Square = m.Level * m.Level return m end" \
           | js -e "m => ({...m, Even: m.Level % 2 == 0})" | kql 'where tobool(Even) | project Level, Square, Text'
```

## 1. Нумерованные концы вывода и перенаправления оболочки

- **Ядро:** запуск процесса передаёт ребёнку кроме входа и выхода ещё до 8
  пишущих концов под номерами — 2 stderr, 3 stdtext
  (`AppServiceBuilder.Processes`, `[8]`/`[9]` запроса старта; все или ни
  одного). `PipeDuplicate` (служба, `AppServiceTable` хвост) — копия пишущего
  конца: поток кончается с последней копией, ломается от любой упавшей.
- **Труба в никуда:** `PipeOverflow.Discard`, `Pipe.Nowhere()` — `Send`
  освобождает блок и отвечает Ok.
- **Оболочка** (`apps_native/Shell/Executor.cs`): `n>&m`, `n>&-` по порядку,
  как в bash; конец, нужный двум номерам, уходит один раз, второй получает
  копию. `> f` — WRITE рядом со стадией, на любой стадии; код WRITE — код
  конвейера, если стадии чисты.
- **Программы:** SDK `SystemConsole` пишет `Console.Error` в конец 2, `Pipe.Text` — в 3;
  SYSINFO печатает панель всегда в `Pipe.Text`, объект — в выход; LESS
  показывает принятое до обрыва трубы с пометкой; KQL дополняет поля записи
  внутри `dynamic` (`Display.` + Tab).

## 2. LUA (`apps_native/LuaApp`, `vendor/LuaCSharp`)

- `lua -e КОД`, `lua ФАЙЛ`, приглашение (выражение печатается, иначе
  оператор); `lua` в трубе — код возвращает функцию, сообщение входит таблицей
  (`View` → `LuaTable`, массивы с 1), выходит таблица (Expando; последовательность —
  массив) или текст; первый результат задаёт тип выхода.
- Функции в файлах: каждое состояние сначала выполняет `\bin\lua\init.lua`
  (`LUA_INIT` — код или `@файл`), `require` ищет в текущем каталоге и
  `\bin\lua` (`LUA_PATH`, `;;` — путь по умолчанию). `prelude.lua`: `walk`,
  `strings`, `keyed`. Файлы лежат в `apps_native/LuaApp/esp/lua/`;
  `run_build.ps1` теперь копирует `esp/` приложения с подкаталогами.
- Стандартные потоки: Lua при `OpenStandardLibraries` сразу открывает
  `io.stdout` байтами — и печать результата получала отказ («a pipe has one
  type»), приглашение падало на первой строке. Вне трубы `print`/`io.write`
  идут текстом через `Console` (`TerminalStandardIO`), в трубе — в поток
  ошибок (`FilterStandardIO`).
- `os.clock`: ленивый статический конструктор читал старт после
  `GetTimestamp()` — первое значение отрицательное; старт читается первым.

## 3. JS (`apps_native/JsApp`, `vendor/Okojo`)

`js -e КОД` (значение печатается, кроме `undefined`), `js ФАЙЛ`, приглашение,
`console.log`/`console.error`; в трубе значение кода — функция над
сообщением (`View` → `JsPlainObject`/`JsArray`, назад — Expando по
`Object.keys` или строка), `console.log` — в поток ошибок. Имя поля
сообщения копируется перед передачей в Okojo — его держит область обмена,
анализатор `SOSR004` иначе не пропускает. Без `require`/`import`, API Node,
браузера и `WebAssembly` (у Okojo это отдельные проекты — donext).

## 4. std: что добавлено ради Okojo (и Lua)

Каждое — порт BCL (.NET 10, форк), отклонения помечены `SharpOS:`/`SharpOS cut:`.

- **Даты** (агент, порт целиком): `DateTime`, `DateTimeOffset`, `TimeSpan`,
  `DateOnly`, `TimeOnly`, `DateTimeFormat`/`DateTimeParse`/`DateTimeFormatInfo`
  (инвариантная культура, данные `CultureData.Invariant`), `GregorianCalendar`,
  `ISOWeek`, `TimeSpanFormat`/`Parse`, `TimeZoneInfo` в режиме Invariant (только
  UTC). Старые самописные `DateTime.cs`, `DateTime.Local.cs` и поддельный
  `DateTimeFormatInfo` удалены; вызовы `TryParseIso`/`TryParseConstant`
  переведены на BCL. Вид по умолчанию теперь как в .NET (`10/26/2026 14:05:09`).
- **`System.Uri`** (агент): System.Private.Uri целиком, с `SearchValues`
  (скалярные реализации BCL), `IdnMapping` (инвариантный режим), `Hashtable`,
  `Ascii`. std — Unix-платформа (`IsWindows()` = false; решение пользователя).
- **`Task`**: продолжения (`ContinueWith`, `WhenAny`/`WhenAll`, `WaitAsync`,
  опции, `TaskScheduler.Default`, `TaskCompletionSource(state, options)`) —
  список действий у задачи, запускает `Complete`.
- **`RandomNumberGenerator`** — §5.
- **Глобализация и текст:** `CharUnicodeInfo` с таблицами, `Rune`,
  `CompareInfo`, `TextInfo`, `string.Normalize` (инвариантный режим),
  `StringInfo`/`TextElementEnumerator` (UAX#29), `RegionInfo`; сравнения
  строк и спанов со `StringComparison`; `StringBuilder.AppendInterpolatedStringHandler`.
- **`System.Array` — `ICloneable`/`IList`/`ICollection`/`IEnumerable`**,
  `SetValue`, `IndexOf(Array, object)`, `Clone`, `Fill`, `Clear(Array)`.
  Причина — падение компилятора C# 14 (§6).
- **Ресурсы образа:** `Assembly.GetManifestResourceStream` из секций
  ReadyToRun 324/325 — данные Intl у Okojo.
- **Числа:** `BigInteger`, `Half`, разбор целых, `Math.Clamp(double)`,
  `Math.Round(…, digits)`, `Convert` Base64/hex/`ToDecimal`.
- **Прочее:** `Range`/`Index`, `Memory<T>`, `Lazy`, `SpinWait`, `TimeProvider`,
  регистрации `CancellationToken`, `Lock`, `StrongBox`, `WeakReference`
  (сильный), `Enum.ToObject`/`Parse` (числом), `Type.IsByRef`/`IsInstanceOfType`/
  `Assembly`, `ReferenceEqualityComparer`, `IEqualityComparer<in T>`,
  `ThenBy`/`IOrderedEnumerable`, `Dictionary`/`Stack.EnsureCapacity`,
  копирующие конструкторы `Dictionary`, `List.LastIndexOf`,
  `Unsafe.BitCast`, `RuntimeHelpers.IsReferenceOrContainsReferences`,
  `GC.CollectionCount`, `MemoryMarshal.Create[ReadOnly]Span` с `scoped`,
  `ReadOnlySpan.CastUp`, `Path.HasExtension`/`GetFullPath(path, base)`,
  исключения (`StackOverflow`, `Unreachable`, `Timeout`, `Rank`,
  `CultureNotFound`, `Cryptographic`).
- В Okojo откатаны два выреза, ставшие лишними: заглушка `SearchValues`
  (в std настоящий) и в Lua — `HexConverter` без `BigInteger`.

Std компилируется в одну сборку с приложением, поэтому его внутренние типы
видны чужому коду: `System.DateTimeFormat`, `System.Globalization.CalendarData`
и `System.HexConverter` пришлось снять псевдонимами в `vendor/*/SharpOS/GlobalUsings.cs`;
у Terminal.Gui свой `System.Rune` — в пространствах кроме `System.Text`
std пишет `System.Text.Rune` полностью (иначе Launcher и UsbTest не собирались).

## 5. RDRAND и `RandomNumberGenerator`

Генераторы в std — `Random` и `Guid.NewGuid` — засевались адресом объекта и
счётчиком: годится для хешей, не для криптографического имени. Сделано:
заготовка `rdrand` в буфере `X64Asm` (`[CompileTimeAsm]`, смещение 0x560),
`OS/src/Hal/HardwareRandom.cs` (CPUID.1:ECX[30], до 10 повторов на слово по
совету Intel), установка в `BootSequence` рядом с часами; приложениям — служба
`RandomFillAddress` в хвосте таблицы, `AppRuntime` подключает её в `SystemRandom`.
`RandomNumberGenerator` — порт BCL; без RDRAND — `CryptographicException`.
`run_build.ps1`: `-cpu qemu64,+nx,+rdrand`. AOTTESTS +2 проверки (315).

## 6. Компилятор C# падал на `[x]` к `IReadOnlyList<T>`

Okojo прошёл все семантические проверки, и `csc` упал при генерации кода:
`Unexpected value 'NoConversion'` в `CompileSynthesizedMethods`, без указания
места. Найдено утилитой на Roslyn (`scratchpad/convscan`): компиляция по
командной строке csc, тела методов и инициализаторы половин файлов
заменяются на `throw null`, поиск сужается до файла и члена. Первый проход
(«пустим половину») не сходился — источников много; сошёлся обратный («живая
только половина»): `ExecutionCheckpoint.StackFrames => stackFrames ?? [CurrentFrameInfo]`.
Для выражения коллекции, приводимого к `IEnumerable<T>`/`IReadOnlyList<T>`,
C# 14 синтезирует свой класс-список, и его `ICollection.CopyTo` отдаёт `T[]`
как `ICollection`. `System.Array` в std не реализовывал необобщённых
интерфейсов — преобразования нет, а вместо ошибки компилятор падает.
Минимальные пробы (`[x]` к `IReadOnlyList<int>`/`<string>`/`IEnumerable<int>`)
падали и над одной std; после `Array.Collections.cs` проходят.

## 7. Гонка ввода клавиатуры

Тестовая демо-сессия: строка в приглашении `lua` пришла как
`STRING>FORMAT(… _VeRSIoN< @!)`. Проверено через QMP на живой машине
пользователя: медленный набор цел, быстрые серии портятся, 120 Backspace —
мусор `oe1 '.oe1 '.`. Корень: `ScancodeSource.TryReadScancode` при пустом
кольце читал железо сам, параллельно с насосом `InputPump`. Код, вынутый
насосом, но ещё не положенный в кольцо, обгонялся следующим (отпускание
Shift раньше своей клавиши); очередь `UsbKeyboard` без защиты делили два
потока. Теперь после старта насоса читатели берут только кольцо, `Push` —
под `Preemption.Suppress`. Проверено на USB-клавиатуре через QMP втрое
быстрее прежнего (удержание 30 мс, пауза 20 мс): длинная строка с Shift
символ в символ, 60 Backspace — ровно 60 символов.

## 8. Вендоринг

`vendor/LuaCSharp` (MIT, © 2025 Yusuke Nakada, снимок a97aac8) и
`vendor/Okojo` (MIT, © 2026 Akito Inoue, снимок 478ec2d, с
`THIRD_PARTY_NOTICES.md`) — только компилируемые исходники и данные Intl,
`LICENSE`, `PROVENANCE.md` (что взято, что вырезано и почему), своё —
в `SharpOS/`. README: раздел «Вендоринг и библиотеки», строки таблиц std и
приложений, примеры в «Трубах».

## Проверка

- Ядро компилируется, все 31 приложение собираются (+ LUA, JS).
- Штатная батарея (`autorun.sh`) под WHPX: `ran=28 failed=0` до порта дат и
  после него, после `Array`-интерфейсов — тоже.
- Демо-команды и пробы LUA/JS (`js_smoke.sh`, `js_pipe.sh`, `demo.sh`) —
  все прошли.
- Финальный прогон после вендоринга (WHPX, сборка из `vendor/`): штатная
  батарея плюс демо и откатанные вырезы — `ran=32 failed=0`; AOTTESTS 315
  (и с `--gc-stress 16 4`), PROCTEST 85, DATATEST 212/209; `0x1234567890abcdef12`
  в Lua → `3.358127276274943E+20` (через `BigInteger`), `RegExp` в JS над
  `SearchValues` std → `10+20 3`.
- Не проверено прогоном: `Uri`, `Convert` Base64/hex, продолжения `Task`
  напрямую, `RandomNumberGenerator` в ядре.

## Файлы

- Ядро: `OS/src/Hal/{HardwareRandom.cs (новый), X64Asm.cs, X64Asm.BootAsm.cs, ScancodeSource.cs}`,
  `OS/src/Kernel/Input/InputPump.cs`, `OS/src/Kernel/Pipes/{KernelPipes.cs, PipeServices.cs}`,
  `OS/src/Kernel/Process/{AppServiceBuilder.cs, AppServiceBuilder.Processes.cs, AppServiceTable.cs}`,
  `OS/src/Boot/{BootSequence.cs, MinimalRuntime.cs}`.
- SDK: `apps_native/sdk/{AppGC, AppRuntime, AppServiceTable, FileSystem.AppHost, MinimalRuntime, Process, SystemConsole, Environment.AppHost (новый)}.cs`, `FreestandingPe.props`.
- Приложения: `LuaApp`, `JsApp` (новые), `Shell`, `SysInfoApp`, `KqlApp`,
  `LessApp`, `ConvertApp`, `ReadApp`, `WriteApp`, `PipeSpy`, `Synth`,
  `DataTest`, `ProcTest`, `AotTests`; `TriCNES/Compat/Convert.cs` удалён (std
  теперь даёт `Convert`).
- std: `std-no-runtime/**` (§4), `Std.props`.
- vendor: `LuaCSharp/`, `Okojo/` (новые), `Terminal.Gui/SharpOS` (псевдоним `Rune`).
- Документы: `README.md`, `limits.md`, `docs/nativeaot-nostd-kernel-limits.md`, `donext.md`.
- `run_build.ps1`: `esp/` с подкаталогами, `+rdrand`.

## Дальше

donext: «Скрипты LUA и JS — следующее» (`import`, `Okojo.Node`, вывод
объектов, wasm, `tobool` в KQL), «Разборщики BCL» (Regex), находка про FAT16.
