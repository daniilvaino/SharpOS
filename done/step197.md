# step 197 — внешние данные: READ, WRITE, CONVERT; оболочка; две ошибки размотки фанклетов

Задание мастер-агента «внешние данные»: байты и текст по трубе, файлы на
запись, `READ`/`WRITE`/`CONVERT`, `> файл`, `Into<T>` из `Expando`,
`Pipe.From/To` из кода. По ходу — просьбы пользователя: оболочка
(редактор строки, история, цвет), доказательство «объекты, а не
сериализация» из оболочки, синтез объекта несуществующего типа.
Автономный режим: только QEMU без графики, ноутбук не трогал.

Итог: целевой конвейер из задания работает целиком —

```
PIPEGEN 5 | CONVERT --to json > in.json
READ in.json | CONVERT --from json | VIEWFILT 3 | CONVERT --to json > out.json
```

DATATEST 44/44 (100 МиБ JSON / миллион объектов через кучу в 64 МиБ, копии
0 Б / 1 Б / 64 МиБ), под `--gc-stress 16` 41/41, JSONTEST 42/42; батарея
зелёная в QEMU 2 и 8 ГиБ (`ran=22 failed=0`). По пути найдены и закрыты две
ошибки, общие для всех программ: исключение из `finally`, вызванного на
обычном пути, возобновляло `catch` с кадром фанклета (EH), и тот же кадр
сборщик разматывал мимо родителя — терял корни (GC).

## 1. Ядро: файлы на запись

- `Hal/Fat32Files.cs`: дескрипторы на чтение (курсор — позиция и кластер,
  чтение прогонами смежных кластеров до 64 КиБ за команду) и на запись
  (создание / обрезка / дописывание; данные копятся прогоном смежных
  кластеров до 64 КиБ и уходят одной командой; связи кластеров — в
  кэшированном секторе FAT, `s_fatDirty`, пишется при уходе кэша или
  закрытии; размер — в запись каталога при закрытии).
- `Hal/Fat32LongNames.cs`: создание файла с длинным именем — записи LFN и
  псевдоним `ИМЯ~N.РАС` с контрольной суммой (`> out.json` в корне и в
  папках).
- Службы `FileOpen/Read/Write/Close` (`AppServiceBuilder.Files.cs`):
  дескриптор принадлежит процессу, конец процесса закрывает его файлы с тем,
  что пришло. Писатель у файла один: второе открытие на запись — `Busy`
  (второй писатель строил бы свою цепочку, последнее закрытие теряло
  кластеры другого). Отказ записи пишет причину в журнал (`[file] write: …`).
- FSInfo: первое изменение FAT после монтирования ставит счётчик свободных
  кластеров «неизвестно» (0xFFFFFFFF). Драйвер его не вёл, и mtools
  показывали полный диск как 165 МБ свободных — это стоило вечера.
- Рабочая папка процесса в ядре (наследуется; `cd` оболочки меняет;
  относительные пути `AppFile`, `File.*` — от неё).
- SDK: `AppFile`, `FileStream` на запись, `File.WriteAllBytes/WriteAllText/
  AppendAllText`, `StreamWriter(path, append)`.

## 2. Трубы: байты, текст, обрыв

- `Pipe.ReadBytes()/WriteBytes()` (`Stream`), то же у концов `Pipe.Create()`:
  чтение берёт `byte[]` на месте в регионе и `string` как UTF-8 + LF, запись
  режет на куски по 64 КиБ (`Pipes/PipeStreams.cs`). `ReadText()` —
  `StreamReader` поверх байтов, `WriteText()` — строка на сообщение. В std —
  `TextReader/TextWriter/StringReader/StringWriter`, `StreamReader` с
  настоящим UTF-8 (порт CoreLib).
- Экран (`ScreenText`): `string` — строкой, `byte[]` — текстом, если это
  UTF-8 без управляющих (символ, разрезанный концом куска, ждёт следующего),
  иначе размер и 16 байт hex.
- Необработанный обрыв стандартного конца — тихий выход 141: приложение
  отдаёт ядру код для необработанного исключения (`SetExceptionExitCode`),
  ядро при 141 не печатает ничего.
- Очередь трубы ограничена и в байтах — 4 МиБ (`KernelPipes.MaxQueuedBytes`),
  одно сообщение проходит всегда: 4096 кусков по 64 КиБ больше всей арены
  обмена (32 МиБ).

## 3. READ, WRITE, CONVERT, оболочка

- `READ файл [--chunk N]`, `WRITE файл [--append]` (`byte[]` — байтами,
  `string` — UTF-8 + LF, прочее — строкой, как на экране; обрыв входа —
  файл с тем, что пришло, код не 0).
- `CONVERT --from json|lines`, `--to json [--lines]` (`ConvertApp`):
  указание — файл в `Converters/`, реестр генерирует MSBuild по именам
  файлов. `--from json` потоково (куски по 64 КиБ с переносом
  `JsonReaderState`, `AllowMultipleValues` для JSON Lines; в памяти — один
  элемент), ошибка — строка и столбец, код 1. `--to json`: перечисления
  именем, `DateTime`/`TimeSpan`/`Guid` строками, `byte[]` base64; цикл, NaN,
  бесконечность — ошибка с типом и полем. Неизвестное указание — список
  известных, код 2.
- JSON — `Utf8JsonReader`/`Utf8JsonWriter` из System.Text.Json (.NET 10) в
  `vendor/SystemTextJson/`; std добита: `IBufferWriter`, `ArrayBufferWriter`,
  `OperationStatus`, `StandardFormat`, `Utf8Parser/Utf8Formatter`, Base64,
  `HexConverter`, разбор `double`/`float` (`NumberToFloatingPointBits`),
  поиск в спанах, `Guid.ToString/Parse`, `TimeSpan` "c", `DateTime` "O" и
  ISO 8601.
- `Into<T>` из `Expando`: поля по именам; числа сужаются с проверкой,
  дробное с целым значением — в целое поле, строка — в перечисление по
  имени, `DateTime`/`TimeSpan`/`Guid` — разбором; не подошло —
  `InvalidCastException` с типом и полем.
- Оболочка: `> файл` / `>> файл` в конце конвейера — стадия `WRITE`; код
  конвейера — первая слева стадия с кодом не 0 и не 141, печатается ошибка
  только её. `SHELL -c LINE [--report ИМЯ]` — строка со своими концами.
- `Pipe.From(line)` / `Pipe.To(line)` (SDK, `PipeLines.cs`) — через
  `SHELL.EXE -c`: разбор и запуск — буквально тот же код. Конец чтения или
  записи ждёт всех стадий; код ≠ 0 — `PipelineException` (конвейер, стадия,
  код).

## 4. EH: исключение из finally, вызванного на обычном пути

Симптом: `foreach` по читателю `Pipe.From`, ждавшему конвейер в `Dispose`
(в finally цикла); конвейер упал → исключение из finally → `catch` того же
метода возобновлялся с RSP и регистрами фанклета (порча rsi вызывающего или
прыжок в стек).

Корень: `StackFrameIteratorOps.Next` разматывал любой кадр по unwind info
корня метода. Finally, вызванный на обычном пути, — отдельный фанклет со
своим прологом; размотка по корню давала ложного родителя.

Исправление: `StackFrameIterator.TryUnwindCalledFunclet` — IP в фанклете,
его собственная размотка возвращает в тело того же метода → следующий кадр —
настоящий кадр родителя; иначе прежняя размотка. `DispatchEx`: у такого
кадра нет синтетического смещения, у родителя пропускаются клаузы до клаузы
фанклета включительно в обоих проходах. Первая попытка (только в
`DispatchEx`) ломала AotTests. Тесты DATATEST 12–15; 15 — исходный путь
(`PipeClosing.WaitInDispose` — тестовый переключатель).

## 5. GC: тот же кадр в обходе сборщика

`DATATEST --gc-stress 16`: массив `before` (локал `Run`, живёт через все
тесты) читался как `0xCC…` — отравленная освобождённая память. Разбиение
`--only N` под стрессом: только тест 15 — сборка внутри finally `foreach`.

Корень — тот же, в `KernelGcPreciseWalk`: кадр разматывался по записи корня
(`CoffMethodGcInfo.Result.RuntimeFunction` — всегда корень). Коды корня,
применённые к фанклету, шагают через указатель кадра к вызвавшему
родителя — кадр родителя и его регистры при вызове finally не обходились.

Исправление по образцу итератора: кадр фанклета отмечается как прежде, а
если его собственная размотка возвращает в тело того же метода — обход
продолжается от неё, родитель обходится в точке вызова. Фанклеты диспетчера
— без изменений. Множество корней только растёт.

## 6. Найдено по пути

- Оболочка роняла единственный аргумент (`echo t7a1` → пусто): парсер
  помечает цепочку слов глаголом («git commit»), `Split` брал первое и молча
  выкидывал остальные. Теперь программа — первое слово, остальное — argv.
- Перечисление писалось числом: генератор каталога пропускал перечисления
  полей без своего `[Message]`. Теперь регистрирует сам (кроме private и
  protected вложенных); тест генератора.
- `DateTime.TryParseExact` не требовал дойти до конца строки: ISO-строка
  разбиралась как дата, время терялось.
- `PipeByteReader` не отпускал прочитанные сообщения (`Next(reuse)` ждёт уже
  отпущенный регион) — `READ` большого файла кончал арену обмена.
- Тихий выход 141 всё равно печатал `[unhandled]` и `*** unhandled
  exception ***`.

## 7. Оболочка

- Редактор строки — tonerdo/ReadLine (MIT, `vendor/ReadLine/`): курсор,
  Home/End, Delete, Ctrl+A/E/B/F/U/K/W/T, история на Up/Down, дополнение по
  Tab. Вырез один — имя клавиши через `Enum.ToString()`. Адаптер к курсору
  `System.Console` не собирается: свой (`Shell/ReadLineConsole.cs`) считает
  курсор сам и двигает относительными ESC — терминал позицию не сообщает;
  запись, кончившаяся в последнем столбце, добивается « \r» (xterm держит
  курсор в столбце, модель библиотеки — консоль Windows).
- Ctrl+C бросает строку, Ctrl+D на пустой — выход, Ctrl+L — очистка и
  перерисовка. История — в `\sh_history` (500 последних), `history`;
  `clear`. Tab: на месте команды — builtins и программы `\apps`, дальше —
  имена файлов и папок.
- Builtin с выводом (`echo`, `pwd`, `ls`, `history`, `help`) может начинать
  конвейер — `echo '' > f` отказывал; `cat ФАЙЛ` в конвейере — `READ ФАЙЛ`.
- `cat` — весь файл кусками, UTF-8 (было 8 КиБ побайтно как Latin-1).
- Цвет только на экран, трубы и файлы без ESC: `View.ToScreenString()` (тип,
  числа, строки, слова, пунктуация — разными цветами), приглашение (папка,
  код ошибки), красный `sh:`, `ls` красит папки и `.EXE`.
- Строка `[app] ИМЯ build ID` по умолчанию не печатается: бит
  `SettingAnnounceBuild` в новом хвостовом поле `AppServiceTable.Settings`
  (0 у старого ядра), ядро ставит его при autorun — лог батареи по-прежнему
  называет сборки; переключение — операция процесса 14,
  `Process.AnnounceBuild`, в оболочке `buildinfo on|off`.
- Необработанное исключение программы — одна красная строка «Имя: Тип:
  сообщение» в её поток ошибок (крючок `ExitCodeForException`); раньше на
  экране было только «exited with 134», отчёт ядра — в его журнале.
- Ядро: Ctrl+буква — управляющий символ (1–26), как в терминале;
  Home/End/Insert/Delete/PgUp/PgDn — сканкодами UEFI. SDK:
  `Console.ReadKey(bool)`, `Console.Clear()`; std: `string.LastIndexOfAny`.

## 8. Объекты, а не сериализация — из оболочки

`PIPESPY [метка]`: пропускает сообщения нетронутыми, первые три описывает
(адрес объекта, адрес своего объекта для сравнения, поля на месте), в конце
— сколько выделено на остальные. Прогон пользователя:

```
PIPEGEN 1000 | PIPESPY a | PIPESPY b | PIPECNT
[a] an object of my own is at 0x2000C4000C80 (my heap)
[b] an object of my own is at 0x200104000C80 (my heap)
[a] #1 PipeApps.LogEntry at 0x17A9328 ... { Level = 0, Text = "text 0" }
[b] #1 PipeApps.LogEntry at 0x17A9328 ...
[a] 1000 messages passed on; 0 objects allocated here for the 997 after them
```

Один адрес у обеих стадий, вне куч обеих (обменная область), ноль выделений.
С сериализацией посередине (`CONVERT --to json | CONVERT --from json`) —
другой адрес и `Expando`. Копия одна — `Copy` писателя раскладывает граф в
общий блок; дальше блок только передаётся.

`SYNTH [n]`: тип, которого нет ни в одной программе (случайное имя, поля
`Level`, `Weight`, `Name` и одно со случайным именем) — описание и блоки
собраны байтами, `new` нет. Для трубы тип — это описание и раскладка; VIEWFILT
его фильтрует, CONVERT превращает в JSON, стадия с классом отказывает по типу.

## Прогоны

- Батарея (шаг 196 + `JSONTEST` 42, `DATATEST` 44, `DATATEST --gc-stress 16
  --quick` 41): QEMU 2 ГиБ и 8 ГиБ (`-m 8192` по логу) — `ran=22 failed=0`.
- После этого прогона менялись оболочка (редактор, конвейер с builtin,
  цвет), клавиатура ядра, `Settings` таблицы служб, строка исключения,
  PIPESPY и SYNTH: всё собирается, оболочку, PIPESPY и SYNTH пользователь
  проверил руками в QEMU. Полная батарея по ним не гонялась — следующим
  шагом.
- В журнале ядра под стрессом ≈40 строк `[gc] walk ended at an unresolved
  frame` — заглушка входа прерывания у вытесненного потока, остаток стека
  сканируется консервативно; ожидаемо.

## Замеры (для ноутбука)

`work/tmp/autorun.perf197` — два прогона `DATATEST --perf`: копия `READ |
WRITE` 64 МиБ (МиБ/с), JSON туда и обратно на 200 тыс. объектов (МиБ/с, нс
на объект), `Into<T>` на объект. Под QEMU (TCG) только порядок: 110 МБ JSON —
запись 62 с, чтение 38 с.

## Откладываем (→ donext)

- Сбой посреди конвейера выглядит для соседа как чистый конец: `WriteTo` и
  `using (writer)` при исключении закрывают выход обычным `Close`; в ядре
  есть `KernelPipes.Break`, приложениям не открыт.
- Сравнение `View` с `UInt64` больше `long.MaxValue` неверно (приведение к
  `long`).
- `Expando`: поле ищется перебором имён в каждом объекте — кэш индекса.
- FILTER вместо VIEWFILT с языком выражений — следующий шаг (KQL:
  Kusto.Language + BabyKusto, см. donext).
- Кириллица и «…» на экране (шрифт CP437; кандидат — UNSCII, public domain);
  удаление файлов в FAT; `<`; Ctrl+C не останавливает запущенную программу.

## Файлы

Ядро: `Hal/Fat32Files.cs`, `Fat32LongNames.cs`, `Fat32.cs`, `Fat32Create.cs`,
`Platform.cs`, `Ps2Keyboard.cs`; `Kernel/Process/AppServiceBuilder*.cs`,
`AppServiceTable.cs`, `AppProcess.cs`, `LauncherBoot.cs`;
`Kernel/Pipes/KernelPipes.cs`; `Kernel/Memory/KernelGcPreciseWalk.cs`;
`Boot/EH/StackFrameIterator.cs`, `DispatchEx.cs`,
`UnhandledExceptionReport.cs`; `Kernel/Exec/JumpStub.cs`,
`Threading/Thread.cs`. std: `Pipes/*` (`PipeStreams`, `ScreenText`, `View`,
`ViewCopy`, …), `IO/*`, `Buffers/*`, `Number/*`, `Bcl/Guid`, `TimeSpan`,
`DateTime`, `StringQueries`, `SystemString`. SDK: `AppFile`, `PipeLines`,
`Process`, `SystemConsole`, `AppRuntime`, `AppServiceTable`,
`FileSystem.AppHost`. Генератор: `Pipes.Generator/MessageGenerator.cs` и
тест. Приложения: `ReadApp`, `WriteApp`, `ConvertApp`, `ViewFiltApp`,
`JsonTest`, `DataTest`, `PipeSpy`, `Synth`, `Shell` (`Executor`,
`LineReader`, `Completion`, `ReadLineConsole`), `PipeGen`, `AotTests`.
Чужой код: `vendor/SystemTextJson/`, `vendor/ReadLine/`. Документы:
`docs/nativeaot-nostd-kernel-limits.md`, `README.md`, `donext.md`.

## Дальше

KQL в конвейере (шаг 198): сначала Kusto.Language в нашей std — разбор,
диагностика, дополнение; потом дополнение в оболочке; потом исполнение на
BabyKusto поверх трубы. Попутно — встаёт ли `System.Text.RegularExpressions`
из BCL на нашу std.
