# step243: одна клавиша — два пути ввода

## Контекст

release.md §8, последний пункт: «лаунчер на байтах терминала». Разбор показал, что
переводить Terminal.Gui на байты не нужно: байты теряют отпускания и Ctrl+Shift, а
сырой путь остаётся ради игр. Беда была в другом — четыре переводчика клавиши:
кодировщик байтов в ядре (`TerminalInput.Encode`), разбор байтов в SDK
(`TerminalKeys`), запасной `SystemConsole.TryTranslate` (без модификаторов) и своя
таблица скан-кодов с защёлкой модификаторов в Terminal.Gui (`SharpOSKeyMap`; в ней
уже чинили step230 и step232). Решение: механизм один, путей два.

## Архитектура

- **Ядро определяет клавишу один раз.** `Platform.TryReadKeyRaw` кладёт в сырое
  событие Shift/Ctrl/Alt на момент события (биты 10–12, ABI не меняется). Ctrl+буква
  в сыром событии — буква: её управляющий код совпадает с Backspace/Tab/Enter
  (Ctrl+H/I/M), а сырой путь должен знать, какая это была клавиша.
- **`SharpOS.Std.ConsoleKeys` (std, общий ядру и приложениям):**
  `FromKeyboard(символ, код UEFI, модификаторы) → ConsoleKeyInfo`,
  `FromChar` — символ как байт терминала, `ToTerminal(ConsoleKeyInfo) → байты xterm`.
  `ConsoleKey` для символов — как у Windows на US-клавиатуре (`Oem*`, цифра для
  `!@#…`): стоковые драйверы Terminal.Gui по ним отличают символ от клавиши.
- **Путь байтов:** `TerminalInput` кодирует через `ToTerminal`, свой кодировщик
  удалён; `TerminalKeys.Plain` берёт символы из `FromChar`. Попутно: Ctrl+Space —
  0x00, как у xterm (было — пробел); ESC `]` без продолжения — Alt+] (пропуск OSC
  его съедал).
- **Сырой путь:** `KeyInfo.Modifiers`, `KeyInfo.ToConsoleKeyInfo()`; запасной
  `TryTranslate` удалён.
- **Terminal.Gui:** `SharpOSKeyMap` — `MapKey`/`MapKeyModifiers` стокового
  `WindowsDriver` (вырезаны случаи NumPad), события — как его `ProcessInput`:
  нажатие — down + keystroke, отпускание — up, модификатор один — `ShiftMask`/
  `CtrlMask`/`AltMask`. Защёлка модификаторов и таблица скан-кодов удалены. Alt один
  открывает меню (по отпусканию); Ctrl+Shift+S отличается от Ctrl+S.
- Игры и эмуляторы не тронуты: им нужна физическая клавиша, она в сыром событии.

Поправка к разбору перед шагом: «модификаторы читаются в момент чтения, а не
события» — не баг. Скан-коды декодируются по порядку очереди, защёлка всегда
соответствует событию, которое только что декодировано.

## Проверка

Рабочая копия, WHPX, все приложения собраны `./build.ps1` (35, 0 предупреждений).

- Прогон 303, DATATEST 29 — 17/17: круговая проверка — 431 клавиша (печатные ASCII
  без и с Alt, Ctrl+буква без и с Alt, клавиши без символа под всеми 8 наборами
  модификаторов, Esc, Enter, Backspace, Tab, Shift+Tab, Ctrl+Space, кириллица):
  `FromKeyboard` → `ToTerminal` → разбор = то же; потери терминала (Ctrl+Shift+S,
  Ctrl+Enter, Ctrl+H/I/J/M) проверены как потери. DATATEST 30 — 9/9.
- Прогон 303, EDIT по QMP: `ab/*X(` набрано как есть (step232); Shift+← выделяет;
  Alt один подсвечивает File; Alt+F открывает меню; Esc закрывает меню и окно
  поиска; Ctrl+Shift+S — Find Next, Ctrl+S — Find; Ctrl+Q — вопрос о сохранении,
  Alt+N — выход. Кнопки диалога в стоке — только по Alt+буква (`Button.ExecuteHotKey`).
- Батарея 304 (`s296.sh`, все приложения свежие): 65/66. Упал DATATEST под
  `--gc-stress` — тест 24 `DriveInfo`: после удаления МиБ места вернулось на 4096
  меньше. Причина — не клавиши: журнал загрузки с step238 растёт на том же томе
  (`BootLog`, `Fat32.Sync`), кластер, занятый им между замерами, тест считал своим;
  под стрессом окно шире. Тест: тройка замеров повторяется до пяти раз, проверка
  осталась точной. Прогон 305: тест 24 обычный и дважды под `--gc-stress 16` —
  3/3 (гонка в этих прогонах не выпала, повтор не понадобился).

## Файлы

- `std-no-runtime/ConsoleKeys.cs` (новый), `std-no-runtime/Std.props`.
- `OS/src/Hal/Platform.cs` — модификаторы в сыром событии, Ctrl+буква буквой.
- `OS/src/Kernel/Input/TerminalInput.cs` — байты через `ConsoleKeys`.
- `apps_native/sdk/KeyInfo.cs`, `apps_native/sdk/TerminalKeys.cs`,
  `apps_native/sdk/SystemConsole.cs`.
- `vendor/Terminal.Gui/SharpOS/SharpOSKeyMap.cs`, `vendor/Terminal.Gui/SharpOS/SharpOSMainLoop.cs`.
- `apps_native/DataTest/TerminalInputTest.cs` — круговая проверка.
- `apps_native/DataTest/FileApi.cs` — тест 24 `DriveInfo` с повтором при росте журнала.
- `docs/nativeaot-nostd-kernel-limits.md`, `README.md`, `release.md`, `donext.md`.

## Дальше

§8 закрыт. §9 «Ошибки (не CoreCLR)».
