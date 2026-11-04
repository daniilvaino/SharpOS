# step242: текст терминала не на экране очищает получатель

## Контекст

release.md §8, пункт «FETCH: голый текст, когда stdtext не экран», переосмыслен в
step240: спросить, куда идёт поток, — обязанность системы
(`Console.IsOutputRedirected`), подстраиваться — выбор программы, она не обязана.
Цвета и движения курсора, ушедшие в файл, должен убирать тот, кто делает из них
простой текст. `FETCH 3> f` клал в файл `ESC[36m…ESC[0m`.

## Архитектура

- **Метка у трубы.** `PipeAttributes.ConsoleText = 8`: строки — текст, который
  программа писала терминалу (System.Console, stdtext), а труба — не экран. Ставит
  писатель при открытии, до схемы: `ConsoleText()` и `HandedText()` (stdout,
  stdtext, `2>`) через `ConsoleTextWriter`. Ядро биты атрибутов не читает — правок
  в ядре нет. Экранная труба по-прежнему `TerminalText` (куски строк как есть).
- **Получатель.** `Pipe.InputIsConsoleText` — метка на конце 0;
  `Pipe.PlainText(span)` — ECMA-48: CSI (`ESC [`, C1 `0x9B`) с параметрами и
  промежуточными, строки OSC/DCS/SOS/PM/APC до BEL или ST, двухсимвольные `ESC x`;
  прочие управляющие (CR, BS, …) — по одному, табуляция остаётся. Обрезанная в
  конце строки последовательность съедает остаток.
- **WRITE** (и через него `>`, `3>`, `2>`) и **`CONVERT --to lines`** очищают строки
  помеченной трубы. Строки данных (`Pipe.Write<string>`) не трогаются: строка в
  трубе — данные, если писатель не сказал иного.
- Не передаётся через промежуточную стадию: `PROG | SORT > f` — SORT пишет данные.
  Это намеренно: очищает ближайший получатель.

## Проверка

Рабочая копия, WHPX.

- Прогон 300, DATATEST тест 16 — 27/27, новые четыре: `Console > f` — `red plain`
  (цвет, заголовок OSC, CR убраны); `stdtext 3> f` — `bold`;
  `| CONVERT --to lines` — `red plain`; строка данных со своими ESC — как есть.
- Прогон 301 — перепроверка step241 со свежими приложениями (см. поправку там):
  22 пары `2026h/l`, `held=70 expired=1 drops=0`.
- Батарея 302 (`s296.sh`, все приложения свежие — step241 и step242): 66/66.

## Урок

`build.ps1 apps_native` — не цель; сборка за фильтром вывода молчала, и прогоны
298–299 шли со старыми приложениями. Проверка автообёртки step241 была ложной —
совпадение числа маркеров. Все приложения — `./build.ps1` без аргумента.

## Файлы

- `std-no-runtime/Pipes/PipeScreen.cs` — `ConsoleText`, `InputIsConsoleText`,
  `PlainText`.
- `std-no-runtime/Pipes/ScreenText.cs` — `Plain`, `SequenceEnd`.
- `std-no-runtime/Pipes/PipeEnds.cs` — `ConsoleTextWriter`.
- `apps_native/WriteApp/Program.cs`, `apps_native/ConvertApp/Converters/ToLines.cs`.
- `apps_native/DataTest/Children.cs`, `apps_native/DataTest/ExternalFiles.cs`.
- `done/step241.md` (поправка), `release.md`, `docs/nativeaot-nostd-kernel-limits.md`.

## Дальше

§8: лаунчер на байтах терминала — последний пункт раздела.
