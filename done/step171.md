# step 171 — ELF вырезан; отклик PowerShell против Debian, сверено с видео

Две части. Чистка: ELF-наследие, на котором после step137 держался весь запуск
PE-приложений. И вопрос «PowerShell на SharpOS тормозит?», поставленный как
сравнение с тем же PowerShell на Debian в том же QEMU.

## ELF

ELF-приложений нет с step137, но под их именами жил путь запуска PE:

- `ElfValidation` — фаза 5 загрузки: монтирует ФС и запускает лаунчер.
  Теперь `OS.Kernel.Process.LauncherBoot`; из неё ушли проверка «маркера»,
  который писали ELF-тесты, и валидатор ELF-сегментов (не вызывался). Лог:
  `[info] pe launcher start` / `pe launcher done`; строки `app run start/ok/
  failed`, которые разбирает `probe_report`, не тронуты.
- `ElfLoadedImage` — описание загруженного образа, его выдаёт `PeLoader`,
  потребляют `ProcessImageBuilder`/`ProcessManager`. Теперь `LoadedImage`
  (`LoadedSegmentCount` → `SectionCount`).
- `AppRunResult` без ELF-значений; `ElfAppContract` — три константы лаунчера
  переехали в `LauncherBoot`, остальное (пути `*.ELF`, коды выхода тестов,
  адрес маркера) удалено.
- Удалены целиком: `ElfParser`, `ElfLoader`, `ElfLoadValidation`,
  `ElfDiagnostics`, `ElfTypes`, `ProcessValidation` (его `Run` проверял
  только ELF-заголовок и никем не вызывался) — ~2100 строк.
- `AppServiceBuilder.RunExternalApp`: ветка «не MZ → ELF-парсер» заменена
  отказом `Unsupported`; удалён его `TryValidateSegments`.
- Комментарии, описывавшие сегодняшний день неверно: `\EFI\BOOT\*.ELF`,
  «ELF-загрузчик», и в `PeLoader` — «EH is Tier-B (halt-on-throw)», хотя
  `.pdata` образа регистрируется для исключений с step140.

## Замер отклика

**Ядро** (`PerfCounters`): `run.<app>.first_input_ms` — от старта интервала до
первого ожидания клавиши при пустом вводе (`ReadConsoleInput`, строчный
`ReadConsole`); для шелла это приглашение. `key.<nn>.<enter|tab|key>_ms` —
сколько программа занята клавишей: от её выдачи до следующего ожидания ввода;
печатаются ≥ 20 мс. Годится, потому что PSReadLine просит следующую клавишу
только закончив с предыдущей; набранное впрок сливается в один эпизод.

Прежнее «старт PowerShell ~15 с» (`run.PowerShellBootstrap.wall_ms`) — ошибка
чтения: это весь сеанс с паузами человека.

**Debian** (`run_linux_ref.ps1 -PowerShell`): PowerShell 7.6.5 linux-x64 на
псевдотерминале (`script`, ввод через fifo), тот же сценарий — приглашение,
`ls`, `echo $PSV` + Tab, Enter; каждый шаг до результата на терминале. Плюс
`-Command "Import-Module PSReadLine; exit"` и `-Command exit`.

Первый замер на Debian дал 16 с до приглашения: PSReadLine и .NET шлют запрос
позиции курсора (`ESC[6n`), на псевдотерминале отвечать некому, и каждый ждёт
таймаута. Стенд теперь отвечает `ESC[1;1R`, как терминал: 5.8 с. Счёт `echo`
по приглашениям дал 72 мс — автодополнение уже перерисовало одно; теперь
ждётся `PSEdition` из вывода (не перемерено).

## Итог

| | SharpOS | Debian в QEMU, тёплый / первый |
|---|---:|---:|
| до приглашения | 6.1–6.5 с | 5.8 / 8.1 с |
| `ls` + Enter → приглашение | 0.93–0.96 с | 0.76 / 0.93 с |
| Tab | 0.57–0.62 с | 0.91 / 1.04 с |
| Enter на `echo` → таблица | 0.40 с | — |

Разрыва в разы нет; «PowerShell медленный» — это TCG, Debian в нём такой же.
У SharpOS рантайм к старту pwsh уже прогрет переписью, у Debian каждый запуск
новый; зато SharpOS грузит профиль (0.8 с).

**Сверка с видео** (29.4 кадра/с, ffmpeg из `imageio-ffmpeg`): начало
интервала — появление `managed child start` на 4.33 с, конец (`wall_ms`
14.78 с) — возврат в лаунчер на 19.13 с. Приглашение по ядру 10.79 с, на
экране к 10.85; Enter после `ls` отрисован 11.63, новое приглашение 12.53
(ядро 0.96 с от выдачи клавиши); Enter на `echo`: 17.17 → 17.57, ядро 0.40 с.
Скрытой задержки отрисовки нет: экран не отстаёт от pwsh. «Набираю, а он
показывает, когда проснётся» — просто набор до конца 6-секундного старта.

## Хвосты курсора

Белые блоки оставались в конце (иногда в начале) строк и уезжали вверх с
текстом. Курсор — инвертированная клетка прямо в пикселях. `Paint` сначала
прокручивал экран сдвигом пикселей — вместе с нарисованным курсором — и
забывал его позицию, а стирал уже после; теневая копия считала клетку под
блоком обычным пробелом, и перерисовка строк её не трогала. Теперь курсор
стирается до прокрутки. Заодно `EraseCursor` рисует пустую клетку, если
теневая копия её не знает (нижние строки после прокрутки), вместо того чтобы
ничего не делать. Ошибка старше step169; пакетная отрисовка оттуда, вероятно,
чаще застаёт курсор посреди строки — не проверялось.

## Уроки

- Эталон проверяется так же, как подопытный: без ответа на `ESC[6n` Linux
  «стартовал» 16 с, и сравнение вышло бы в пользу SharpOS втрое.
- Детектор смены кадров пропускает строку в 17 символов; события на видео
  искать по кадрам, а не по порогу.
- Python в heredoc через этот shell теряет `\\`: `\a` в `\apps` стал BEL в
  двух комментариях. Обратный слэш в правках — через `chr(92)`.

## Дальше

- Куча ядра растёт и не собирается (`kgc=0` во всех прогонах) — самое неясное
  из оставшегося.
- Эталон вывода: Debian пишет в ttyS1 без фреймбуфера, сравнение `output`
  ×1.9 неполное.

## Файлы

- новое: `OS/src/Kernel/Process/LauncherBoot.cs`, `LoadedImage.cs`,
  `AppRunResult.cs`, `tools/pwsh-qemu-linux-reference.log`
- удалено: `OS/src/Kernel/Elf/` (8 файлов),
  `OS/src/Kernel/Process/ProcessValidation.cs`
- `OS/src/Kernel/Process/AppServiceBuilder.cs`, `ProcessContext.cs`,
  `ProcessImageBuilder.cs`, `ProcessManager.cs`, `OS/src/Kernel/Pe/PeLoader.cs`,
  `OS/src/Boot/BootSequence.cs`
- комментарии: `ExitBootServicesProbe.cs`, `MinimalRuntime.cs`,
  `FatBootBridge.cs`, `Platform.cs`, `JumpStub.cs`, `X64PageTable.cs`,
  `Threading/Process.cs`, `Threading/Thread.cs`, `EhProbe.cs`
- курсор: `OS/src/Hal/TerminalConsole.cs`
- замер: `OS/src/Kernel/Diagnostics/PerfCounters.cs`,
  `OS/src/PAL/SharpOSHost/ConsoleInput.cs`, `ConsoleRead.cs`, `run_linux_ref.ps1`
- документы: `docs/perf-progress.md`, `docs/coreclr-hosted-limits.md`,
  `docs/nativeaot-nostd-kernel-limits.md`, `docs/boot-order.md`,
  `docs/threading-architecture.md`, `CLAUDE.md`
