# step 213 — находки тестеров: журнал загрузки через KQL, READ --lines, исключение из catch, коды выхода тестов; сборка без предупреждений, AppHost.Write* устарели

## Контекст

Шаг — разбор находок трёх тестеров (`donext.md`, «Находки тестеров»), без
CoreCLR. Главная — журнал загрузки нельзя было прочитать (`READ … | KQL`
падал по памяти). Попутно `PROCTEST | KQL "take 2"` уронил ядро (`#DF`), и
это оказалось ошибкой EH ядра. В конце — вопрос «как BENCHAOT пишет текст»
вырос в то, что `AppHost.WriteString` пишет мимо stdout, и в просьбу собрать
всё без предупреждений.

## 1. Исключение из catch-фанклета (ядро, `OS/src/Boot/EH/DispatchEx.cs`)

**Симптом.** `PROCTEST | KQL "take 2"`: PROCTEST переполнял стек, на втором
круге — `#DF` в EH ядра и `*** halting ***`.

**Корень.** Исключение, брошенное из catch-фанклета (прямо или из вызова в
нём), сопоставлялось по синтетическому смещению фанклета — `TryStartOffset`
его же предложения. Свой `catch` ловил исключение снова: бесконечная
рекурсия.

**Исправление** — три итерации:
1. Пропустить предложение самого фанклета → после фанклета обход терял
   кадр: второе исключение уходило необработанным.
2. Взять кадр родителя из цепочки ExInfo в начале кадра → `try` внутри
   `catch` прыгал в NX.
3. Двухчастное сопоставление, оставлено:
   - часть 0 — предложения, кроме своего, по литеральному смещению на
     регистрах фанклета (`try` внутри `catch` ловит своё);
   - часть 1 — `AdoptRunningFunclet`: из цепочки `PrevExInfo` берётся
     `FrameIter` с тем же корнем метода и большим SP, PC остаётся
     фанклета; `EhEnumInit` заново, только предложения с индексом больше
     своего, по синтетическому смещению.
   Второй проход (`InvokeSecondPass`) так же: finally внутри фанклета
   (`FramePart.InsideFunclet`), потом вокруг него (`AroundFunclet`).

Проверки в AOTTESTS: `CheckThrowFromCatch` — бросок из вызова внутри `catch`
уходит наружу, `catch` отработал один раз; `try` внутри `catch` ловит своё.

## 2. Журнал загрузки через KQL и большие входы

- **KQL, OOM на журнале загрузки.** `byte[]` корня становился dynamic-
  столбцом: JSON-массив по узлу на байт, 65 536 узлов на кусок, куча
  фрагментировалась. Теперь `byte[]` — текстовый столбец (`TryGetBytes` +
  UTF-8). Перечисление — текст с именем члена. Пакет ограничен 1 МиБ.
- **LESS** — строка экрана не длиннее 4096 знаков, серии NUL от 16 — `^@ ×N`,
  UTF-8 декодируется по кускам с переносом. **PipeTextWriter** режет строки
  длиннее 64 Ки знаков на несколько сообщений.
- **READ `--lines`** — строки текстом через `Pipe.WriteText()`, UTF-8 с
  переносом между кусками, CRLF; `--help`, неизвестный ключ — usage и 2,
  режимы взаимоисключающие.

## 3. Мелкие находки

- **Коды выхода тестов — число провалов** (было — пройденных): AOTTESTS,
  PROCTEST, PIPEPERF, JSONTEST, DATATEST, GVMTEST, KQLTEST, KQLBABY;
  `autorun.sh` — `expect 0`.
- **AOTTESTS на образе без тестов** — проверки ядра `[skip]`, если служба
  `PipeProbeAddress` не опубликована (было 134).
- **Тип в трубе не совпал** → код 1 и одна красная строка
  (`AppRuntime.ExitCodeForException`); ядро не печатает отчёт, когда
  программа сама назначила код (`UnhandledExceptionReport.IsAppsOwnEnd`).
- **json2dir:** отказ на `\ : * ? " < > |`, управляющих знаках, точке или
  пробеле в конце, дубликатах без учёта регистра (`SharpOS cut:`).
- **Оболочка:** `exit` без аргумента — последний код, нечисловой — 2;
  `ls a b`; `cp -r`/`mv` в самого себя отказывают; `export`/`unset`/`read`
  не путаются с программами; встроенные без учёта регистра; вывод
  встроенных в трубе идёт потоком.
- **`--help`** у PIPEGEN, PIPEFILT, VIEWFILT, SYNTH (`NumberArg`), WRITE.
- **Экран:** Esc в лаунчере спрашивает «Stay / Power off»; Terminal.Gui
  выходит из альтернативного экрана при `Suspend`; ядро перерисовывает
  терминал на смене альтернативного экрана; Alt+буква.
- **Данные:** `View` печатает DateTime/DateTimeOffset/TimeSpan/Guid/Decimal
  значением; CONVERT из JSON — `Int64`, потом `UInt64`, потом `double`.
- **Шаг 213 до этого** (из начала шага): `MethodTable.ValueTypeSize` —
  размер значения для `RhUnboxNullable`, `RhBox`, `StoreStruct`;
  `InterfaceDispatchResolver` — null-цель бросает `NullReferenceException`
  в приложении и в ядре.

## 4. AppHost.Write* устарели — вывод мимо stdout

`BENCHAOT | KQL` получал пустой вход: BENCHAOT писал через
`AppHost.WriteString`, а это служба ядра прямо на терминал.

- `AppHost.Terminal.Write/WriteChar/WriteUInt/WriteHex` — явный терминал:
  строка ввода, приглашение, управляющие последовательности, отчёт без
  аллокатора.
- Старые `AppHost.WriteString/WriteChar/WriteUInt/WriteHex` —
  `[Obsolete(DiagnosticId = "SHARPOS0001")]`, обёртки над `Terminal`.
- Переведены: BENCHAOT, эмуляторы, DOOM, «exited» лаунчера и USBTEST — на
  `Console`; оболочка, REPL LUA/JS, FETCH, внутренности SDK — на
  `Terminal`. AOTTESTS — на `Terminal`: печатает числа между замерами
  аллокатора, а `Console.Write(uint)` выделяет строку. Замер скорости экрана
  в BENCHAOT — на `Terminal`, иначе мерил бы трубу.
- `Console.Write/WriteLine` получили перегрузки `char`, `bool`, `int`,
  `uint`, `long`, `ulong` (как BCL).

## 5. Сборка без предупреждений

Было около 2550 уникальных предупреждений в приложениях и 208 в ядре. Стало
ноль (C# у ядра с `-SkipCoreClr` и у всех 31 приложения). Реестр
подавленного — `docs/build-warnings.md`.

Исправлено:
- **CS8632 (~2000)** — у приложений не было nullable-контекста, у ядра был:
  `<Nullable>annotations</Nullable>` в `FreestandingPe.props`.
- **CS3021 (~350)** — `[assembly: CLSCompliant(false)]` в std.
- **CS0162 от `BitConverter.IsLittleEndian`** — был `const`, стал свойством.
- **CS1718** — `x != x` → `float/double.IsNaN`; CS0675 в `Fat32`; лишний
  `new` в `IEqualityComparer`; CA2014 в AOTTESTS; nullable в
  `Pipes.Generator`; CS0672 — `[Obsolete]` на перекрытиях в драйвере
  Terminal.Gui; мёртвые `ProcessImage.ExitCode`, `AppProcess.TWoken`.
- **CS0114 в LuaCSharp — настоящая ошибка нашего выреза.**
  `LuaStreamDefaults` держал тела по умолчанию виртуальными членами;
  наследники их скрывали, и тело по умолчанию звало `Mode`/`Dispose()`
  базового класса, а тот бросал `NotImplementedException`. Теперь члены
  `LuaStreamDefaults` — явные реализации интерфейса и обращаются к себе
  через `ILuaStream`. Запись в файл, открытый на чтение, — нормальная
  ошибка Lua.

Подавлено точечно (прагма в файле или вокруг поля; `.editorconfig` у чужого
кода): переключатели `Probes`/`Trace` (`const` намеренно), поля ABI и
поля std, которые задаёт один ярус, `fixed`-буферы, указатели на
управляемые типы, `Lock` на `Monitor`. Чужой код не правился: Fami и
TriCNES (подмодули — `.editorconfig` уровнем выше), XtermSharp,
Terminal.Gui (секция в его `.editorconfig`), SystemTextJson, BabyKusto.

## Проверка

Рабочая копия, WHPX, `-SkipCoreClr`, батарея и добавки:
`[sh] done ran=32 failed=0`; AOTTESTS 196/196 в трёх режимах, PROCTEST
85/0, DATATEST 216 и 213, PIPEPERF 20, JSONTEST 64, KQLTEST 7, KQLBABY 9,
GVMTEST 10. `BENCHAOT | KQL "take 3"` — три строки по трубе. LUA:
`io.write`, запись в файл на чтение — ошибка, а не исключение.
`PROCTEST --only 1|2 | KQL "take 2"` — 141, без остановки. Сборка с
временным вызовом `AppHost.WriteString` — предупреждение SHARPOS0001.

## Уроки

- Обход стека из фанклета: синтетическое смещение — для кадра родителя,
  литеральное — для самого фанклета; одно смещение на оба — то ловит своё,
  то теряет родителя.
- Свой прогон QEMU — со сторожем на `halting` (COM1) и `[sh] done` (COM3):
  остановленная машина висит молча.
- Предупреждение в чужом коде может быть нашей ошибкой: CS0114 в LuaCSharp
  — след нашего выреза.

## Отложено

- Первая строка вывода стадии попадает в журнал ядра (`BENCHAOT | KQL`).
- Залипающий Ctrl, PROCTEST считает ресурсы всей машины, терминал теряет
  вывод после длинного конвейера, остальное — `donext.md`.
- `NoWarn` проекта у JsApp и LuaApp — в `.editorconfig`.
- Полная сборка с CoreCLR на предупреждения не проверялась.

## Next step

step214 — «Терминал теряет вывод программ» (находка тестеров).
