# Релиз 0.1 — список работ

Матрица сборок 2 × 2: с CoreCLR и без, с тестами и без. Порядок — сверху
вниз; закрытое удалять. Отчёты аудита: `work/release/cleanup_kernel.md`,
`work/release/binaries.md`.

## 1. Чистка и раскладка образа

Сделано — `done/step220.md`. Осталось:

- ROM: свободный homebrew вместо `GAME.NES` (Super Mario Bros.) — ищет
  пользователь. WAD — из репозитория MOOS.
- После релиза: драйверная часть `UsbProbe` (`RunXhci`/`Enumerate`) — в
  `Hal/Usb`; поведение не меняется, а проверить на железе здесь нечем.

## 2. Форматы в std

Сделано: `System.IO.Hashing`, интринсики x86, векторы BCL целиком —
`done/step221.md`.

Подключение — как BCL, в своих namespace; что тяжёлое — отдельной строкой в
csproj, не в `Std.props`.

- JSON: `vendor/SystemTextJson` (reader/writer, `JsonDocument`, `JsonNode`) — в
  std; `PROVENANCE.md` обновить. `JsonSerializer` — генератором источников.
- XML: `XmlReader`/`XmlWriter` (и `XDocument`) в std; сейчас TurboXml только в
  ядре.
- Regex — интерпретатор (`RegexInterpreter`), без компилятора и генератора;
  вернуть `matches regex` и `extract()` в BabyKusto.
- Сжатие: SharpCompress (MIT) в `vendor/` целиком; `System.IO.Compression`
  (`DeflateStream`, `GZipStream`, `ZLibStream`, `ZipArchive`) поверх него.
  Сначала — чего ему не хватает (ZstdSharp, async `Stream`, `ArrayPool`).
- `System.Formats.Tar` — из BCL.
- Кодовые страницы: `System.Text.Encoding.CodePages` из BCL (`codepages.nlp`
  612 КБ ресурсом — ресурсы сборки работают); нужны `EncodingProvider`,
  `Encoding.RegisterProvider`, `Encoder`/`Decoder` с состоянием.

## 3. Текстовый редактор

Кандидат — `TextView` Terminal.Gui (собирается), основа — пример Editor из
UICatalog (MIT). Подсветка — после Regex (AvaloniaEdit `.xshd`, MIT).

## 4. Владение терминалом

- Оболочка передаёт терминал команде и забирает после (`tcsetpgrp`).
- Байты клавиш — только владельцу.
- Клавиши экрана (прокрутка, Ctrl+Tab) ядро берёт до раздачи — и для
  программ на сырых событиях.

## 5. Команды

- Над объектами: `head`, `tail`, `grep`, `wc`, `sort`, `uniq`, `tee`, `find`.
- `ps`, `kill`, `sleep`, `date`.
- `xxd`.

## 6. Язык оболочки

- `if` / `for` / `while`, `$(...)`, `$?`, `test` / `[`.
- Переменные оболочки (сейчас только окружение: `export`, `$ИМЯ`; `A=1` и
  `$?` отклоняются).
- `<`.

## 7. Инспектор

- Строка-сводка в ленте: `▸ 1000 × Ghost.Rusupv`; объект — одной строкой.
- Навигация: выбор, раскрытие и сворачивание узлов.
- Таблица по схеме для однотипных.
- Сводка слева выбирает, что показать справа.

## 8. Трубы и панели — всё

- Подсказки в externalpipe: `FETCH > f; READ f` показывает объект.
- Программе — размер её панели; смена размера — событие (SIGWINCH).
- Последовательный терминал стенда — плоский вид.
- stderr мимо оболочки обгоняет вывод через неё.
- Прямые записи в обход `Console` (баннер `[app]`) не упорядочены.
- Лаунчер на байтах терминала.
- Кадр из нескольких записей по 4 КиБ — наполовину (`CSI ? 2026`).
- Противодавление на полной очереди экрана.
- Подписчики трубы вместо `OutputRouting`.
- Строки ядра в журнале на диске — перемешиваются посреди строки.
- Тесты — отчёт в stdtext.
- FETCH: голый текст, когда stdtext не экран.

## 9. Ошибки (не CoreCLR)

- `enum.toString` — генератор таблицы имён перечислений.
- `[term] FAIL wide` — XtermSharp, заглушка ширины 0 (`SharpOS cut:`).
- `[ModuleInitializer]` — вызывать из секции после материализации статиков.
- `summarize` BabyKusto копирует все столбцы входа.
- Запись на FAT16 — «отказ диска».
- Вывод PROCTEST оборвался в COM3 (один раз, step207).
- Кириллица: шрифт CP437 (UNSCII), раскладка только US, COM-лог стенда —
  младший байт UTF-16.
- xHCI ноутбука 4:0.0 не адресует устройство (железо).

## 10. Ctrl+C для всех

- Ctrl+C — сигнал владельцу терминала, программа останавливается.
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
- Сборка с чистого клона по README.
- README — первая страница: что это, снимок, скачать → запустить,
  возможности, ограничения.
- Лицензии: файлы лицензий в образе; DOOM — текст GPL-2 и ссылка на исходник;
  PowerShell и рантайм .NET (MIT) в «Чужой код в образе».
- Тег `v0.1` и образы в Releases (пушит пользователь).
