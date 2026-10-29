# step 216 — подсказки у писателя, FETCH вместо SYSINFO, ядро вне экрана сессии, вход оболочки каждой команде, часовой пояс

## Почему

Пачка из donext: «Каналы вывода», пункты 3–5 и хвосты; находки тестеров
(время файлов, JSON `when`); мелочи прошлой сессии (лишняя `t` в LESS,
перемешанные баннеры).

## Что сделано

- **Подсказки** (`PipeHints`, std `Pipes/PipeEnds.cs`). `NotForTerminal` у
  stdout: `Pipe.Write<T>(PipeHints)` без переданного конца пишет в никуда, а не
  на экран. `DescribesOutput` у stdtext (`Pipe.TextHints`): без конца 3 текст
  выпадает, если stdout передан (труба, файл, `>&-`). `PIPEHINTS=off` выводит
  всё. Соблюдает писатель — экран рисует сама программа; в транспорт подсказки
  переедут с оболочкой-читателем (панели).
- **FETCH = FETCH + SYSINFO.** stdout — `SystemInfo` с «не для терминала»,
  stdtext — логотип и факты из `SystemInfo` («describes output»). Голый `fetch`
  — панель; `fetch | kql …` — только запись; `fetch 3>&1 >&- | less` — панель
  по трубе. SysInfoApp удалён; `run_build.ps1` стирает `SYSINFO.EXE` с ESP.
- **Строки ядра — вне экрана сессии.** `OutputRouting.UserSession`: пока
  работает INIT (`LauncherBoot`), канал ядра идёт в COM1 и журнал на диске, не
  на экран. Загрузка до INIT и итог после — на экране, паника — всегда. Отчёт
  о необработанном исключении приложения — в журнал целиком, пользователю одна
  строка в поток ошибок программы (`NAME: unhandled TYPE: message`).
- **Вход оболочки — каждой команде.** Копии читающего конца в ядре
  (`KernelPipes.Duplicate` для обеих ролей, `Pipe.Readers`): копии читают одну
  очередь по очереди, писатель видит читателя ушедшим с последней. Оболочка
  раздаёт копию своего входа каждой команде (`-c` и скрипт), как один stdin в
  sh. `PIPEGEN 5 | SHELL -c 'take 2; PIPECNT'` → 3.
- **Часовой пояс.** `TZ` в `system.env` — сдвиг ISO 8601 (`+03:00`, `UTC+3`;
  не POSIX). std `TimeZoneInfo.Local` — фиксированный пояс с этим сдвигом
  (`DateTime.Now`, `ToLocalTime`); ядро пишет отметки FAT в местном времени
  (`Fat32.LocalOffsetMinutes`), RTC — UTC. `run_build.ps1` пишет сдвиг хоста.
  Без правил перехода на летнее время.
- **Запись программы — целиком.** `WriteUtf8` держит подавленное вытеснение на
  всю запись, а не на символ: баннеры двух стартующих программ резали друг
  друга (`9c96ade-pi[app] PipeCnt …pe-dev` — воспроизведено в прогоне). Сам
  баннер (`AppRuntime`) — одна запись, а не пять.

## Что оказалось не багом

- **JSON `when` с 8 знаками** — тестер сам отозвал: `Z` в шрифте похожа на `2`.
- **Лишняя `t` в LESS** — не воспроизводится (QMP: `cat … | less`, поиск
  `/TZ`, выход): артефакт.

## Отложено

- `1>&2` — нужен конец «экран ошибок»; даёт труба к экрану (donext).
- Подсказки в описании трубы и в заголовке externalpipe — с панелями.
- Отчёт тестов в stdtext — ломает проверки DATATEST, читающие stdout детей;
  пользы до оболочки-читателя нет.
- Дополнение KQL внутри записи — столбцов в строке оболочки нет вообще
  (известный пробел), внутри `> f` то же.

## Проверка

Рабочая копия, WHPX, `-SkipCoreClr`, батарея autorun + строки шага.
Три прогона, последний ran=37 failed=0, ни одного FAIL: AOTTESTS 319/319 ×3 и
196/196, PROCTEST, PIPEPERF, JSONTEST, DATATEST (обычный и `--gc-stress 16`),
GVMTEST, KQLTEST, KQLBABY. Новые проверки DATATEST: вход оболочки по очереди
(3), FETCH в трубу — один `SystemInfo`, `FETCH 3>&1 >&-` — панель 36 строк,
`TZ=+02:00` — местный пояс и `Now − UtcNow`. Строки шага: голый `fetch` —
панель без строки объекта, `fetch | pipecnt` → 1, `env PIPEHINTS=off fetch` —
и объект, и панель, `pipegen 7 | pipecnt > c216.txt` → 7, `ls -l` — 05:06 по
часам хоста (EET), `PIPEGEN 5 | SHELL -c 'PIPECNT; PIPECNT'` → 5 и 0. Баннеры:
в первом прогоне склейка посреди слова, после записи целиком — между кусками
баннера (он писался пятью записями), после баннера одной записью — 0 из 1347.
Глазами (QMP): после старта INIT строк ядра на экране нет, LESS по трубе и
поиск `/TZ` — без лишних знаков.

## Уроки

- QMP `send-key` по одной клавише отпускает Shift до буквы: сочетания —
  одним вызовом с несколькими клавишами.
- Флаг, объявленный и не выставленный, прошёл первую батарею зелёным: строки
  ядра на экране проверяет только снимок экрана.

## Файлы

- std: `Pipes/PipeEnds.cs` (`PipeHints`, `Write<T>(hints)`, `TextHints`,
  `PipeReadEnd.Duplicate`), `TimeZoneInfo.SharpOS.cs` (TZ),
  `EnvironmentStore.Kernel.cs`.
- Ядро: `Hal/Output/OutputChannel.cs` (`UserSession`),
  `Kernel/Process/LauncherBoot.cs`, `Boot/EH/UnhandledExceptionReport.cs`,
  `Kernel/Pipes/KernelPipes.cs`, `PipeServices.cs`, `AppServiceTable.cs`,
  `Kernel/Process/AppServiceBuilder.cs` (`WriteUtf8`), `BootConfig.cs` (TZ),
  `Hal/Fat32Create.cs` (местное время).
- Приложения: `FetchApp/Program.cs`, SysInfoApp удалён; `Shell/Executor.cs`,
  `Shell/Program.cs`; `LuaApp` (комментарий); SDK `AppRuntime.cs` (баннер).
- Тесты: DATATEST 19 (вход по очереди, FETCH в трубу и панель по 3),
  24 (TZ).
- `run_build.ps1` (TZ, стирание `SYSINFO.EXE`), README, limits, donext.
