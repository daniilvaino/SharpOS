# Релиз 0.1 — список работ

Матрица сборок 2 × 2: с CoreCLR и без, с тестами и без. Порядок — сверху
вниз; закрытое удалять. Отчёты аудита: `work/release/cleanup_kernel.md`,
`work/release/binaries.md`.

## 1. Чистка и раскладка образа

Сделано — `done/step220.md`; SBOM чужого кода (`sbom.cdx.json`) и тексты
лицензий в образе (`\sharpos\licence\`) — `done/step229.md`; шина USB в
`Hal/Usb` — `done/step244.md`; ROM из `vendor/` — `done/step245.md`. Раздел
закрыт.

## 2. Форматы в std

Сделано: `System.IO.Hashing`, интринсики x86, векторы BCL целиком —
`done/step221.md`; `System.Formats.Tar` и файловый API — `done/step222.md`;
сжатие (SharpCompress, `System.IO.Compression`) и криптография —
`done/step223.md`; System.Text.Json целиком с сериализатором через генератор
источников — `done/step224.md`; Regex целиком (интерпретатор, NonBacktracking,
`[GeneratedRegex]`), TurboXml в std обоих ярусов (порт `System.Xml` — не в
0.1, по первому потребителю: donext) — `done/step225.md`; BCL `Encoding` целиком
и кодовые страницы — `done/step226.md`. Раздел закрыт.

Подключение — как BCL, в своих namespace; что тяжёлое — отдельной строкой в
csproj, не в `Std.props`.

## 3. Текстовый редактор

`EDIT [путь]` — `done/step230.md` (TextView, правка, поиск и замена, диалоги
открытия и сохранения), подсветка синтаксиса — `done/step232.md` (движок
AvaloniaEdit из tui-cs/Editor, `.xshd` читает `XmlReader` из BCL, step231).
Раздел закрыт.

## 4. Владение терминалом

`done/step234.md`: группы процессов и владелец (`TerminalOwner`), задание
оболочки на конвейер, клавиши экрана — в `InputPump`, до всех читателей. Раздел
закрыт.

## 5. Команды

`done/step235.md`: `head`, `tail`, `grep`, `wc`, `sort`, `uniq`, `tee`, `find`,
`xxd`, `ps`, `kill`, `sleep`, `date` — одна программа `UTILS.EXE`. Раздел закрыт.

## 6. Язык оболочки

`done/step236.md`: `if`, циклы, `case`, переменные, `$(...)`, `$((...))`, `test`, шаблоны
путей, `<`, сценарии через несколько строк; разбор — `vendor/Meziantou.Shell`. Раздел
закрыт.

## 7. Инспектор

`done/step237.md`: F12 и `inspect`, свой ввод панели (Ctrl+Tab), строка-сводка с
номером команды, таблица однотипных, дерево с раскрытием, строки и байты без
автооткрытия, 16 команд с переходом `p`/`n` и `inspect N`. Раздел закрыт.

## 8. Трубы и панели — всё

Сделано: подсказки в externalpipe, порядок вывода (stderr, баннер `[app]`, строки
ядра в журнале — одним читателем экранных труб по сквозному номеру сообщений) —
`done/step239.md`; синхронный вывод (`CSI ? 2026`), противодавление сверено —
`done/step241.md`; текст терминала не на экране очищает получатель —
`done/step242.md`; одна клавиша на оба пути ввода (байты и сырые события), Terminal.Gui
на карте стокового `WindowsDriver` — `done/step243.md`. Раздел закрыт.

## Звук

Сделано: AC'97 (QEMU), служба `AudioWrite`, `SharpOS.AppSdk.Audio`, `tone` —
`done/step246.md`; Intel HD Audio (порт SerenityOS) — `done/step247.md`; PC speaker,
`Console.Beep`, `beep` — `done/step248.md`; звук DOOM (эффекты) и Fami —
`done/step249.md`; микшер в ядре и музыка DOOM (MeltySynth, TimGM6mb) —
`done/step250.md` (там же — HD Audio на стенде: Realtek ALC236, динамик). Раздел
закрыт; переключение на наушники по гнезду — в donext.

## 9. Ошибки (не CoreCLR)

Сделано — `done/step251.md`: `[ModuleInitializer]`, ширина второй ячейки
широкого символа, `summarize` без копии лишних столбцов; xHCI ноутбука — пауза
после сброса и повтор, ждёт прогона на стенде. Имена перечислений — разделом
«CoreLib как в стоке». FAT16, обрыв COM3 и кириллица — не в 0.1 (donext). Раздел закрыт.

## CoreLib как в стоке

Сделано — `done/step252.md`: std — отдельная сборка `System.Private.CoreLib`, по
одной на ярус из одного дерева (как сток собирает CoreLib под платформы); ядро и
SDK приложений — сборки над ней, порты BCL (Json, Regex, Xml, CodePages,
SharpCompress) — своими сборками; ILC стоковый и пишет метаданные — `Enum`
по именам, как в стоке. Раздел закрыт; что осталось (низкоуровневый API труб
вместо `InternalsVisibleTo`, имена кадров в трассах) — в donext.

## 10. Ctrl+C для всех

Ctrl+C — сигнал владельцу терминала, `CancelKeyPress`, `PosixSignalRegistration`
(`done/step253.md`). Осталось:

- Смена размера панели — сигнал (SIGWINCH, `PosixSignalRegistration`); сам размер
  программа уже получает свой.
- Потерянное отпускание Ctrl: `c` → Ctrl+C (`InputPump.DetectBreak`); прогон с
  журналом скан-кодов.

## 11. CoreCLR

- Раскладка full: `-NoTests` — без NormalHello и Bench; `tpa.txt` —
  StarlingProbe только когда он есть; `CoreClrProbe.RunAssembliesInSequence`
  (мёртвый) — удалить; `CoreClrProbe` — переименовать (`CoreClrSession`).
- Ctrl+C в pwsh → FailFast (режим консоли, `MapVirtualKey`).
- `BreakHandler` pwsh после выхода → `*** halting ***`.
- FailFast hosted-процесса вешает ОС.
- `.DLL` с `>` или `|` не стартует.
- PowerShell: `mkdir`/`New-Item`/удаление молча «успешны» (PAL).
- Нет `CreateProcessW`, `BCryptHash`, `K32GetProcessMemoryInfo`, `msvcrt`;
  даты файлов 1601.
- Пути: Unix по умолчанию.

## 12. Батарея и стенд

- Батарея на всех четырёх сборках.
- Прогоны на стенде (железо).

## 13. Выпуск

- Сценарий сборки четырёх образов, строка запуска QEMU, запись на флешку.
  Релизная сборка не берёт `payloads\*.nes`; WAD — из репозитория MOOS.
- Сборка с чистого клона по README.
- README — первая страница: что это, снимок, скачать → запустить,
  возможности, ограничения.
- Лицензии: файлы лицензий в образе; DOOM — текст GPL-2 и ссылка на исходник;
  PowerShell и рантайм .NET (MIT) в «Чужой код в образе».
- Тег `v0.1` и образы в Releases (пушит пользователь).
