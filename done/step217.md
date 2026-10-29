# step 217 — ввод программы байтами терминала; CursorLeft/CursorTop

## Почему

Ввод программы был службой клавиш (`TryReadKey`: код UEFI и символ), а не
потоком байт терминала. Ответам терминала некуда было идти: XtermSharp умеет
отвечать на `CSI 6n`, `CSI c`, `CSI 18t`, но ответы выбрасывались, и
`Console.CursorLeft`/`CursorTop` не было. Модификаторов у стрелок и F-клавиш
программы не видели вовсе. Решение из donext — как на Unix: ввод терминала —
байты, разбор в SDK.

## Что сделано

- **Ядро, `Kernel/Input/TerminalInput.cs`.** Читает те же события, что сырая
  служба (`Keyboard.TryReadKeyRaw`): один источник, одна защёлка
  модификаторов — игра на сырых событиях и оболочка на байтах не забирают
  друг у друга половину клавиши. Нажатие → байты xterm: печатные — UTF-8,
  Enter — CR, Backspace — DEL, Ctrl+буква — управляющий байт (так уже давал
  декодер), Alt — ESC перед клавишей, стрелки/Home/End — `CSI A..D H F`,
  Insert/Delete/PgUp/PgDn — `CSI n ~`, F1–F4 — `SS3 P..S`, F5–F12 —
  `CSI 15..24 ~`, модификаторы — `1;m`, Shift+Tab — `CSI Z`. Отпускания — не
  байты. Фиксированные кольца, без выделений.
- **Ответы терминала.** Делегат XtermSharp (`AnswersToInput.Send`) кладёт их в
  свою очередь; она отдаётся раньше клавиш — программа, спросившая, ждёт
  именно ответ.
- **F-клавиши** в декодере (`Platform.FunctionScanCode`): set-1 0x3B–0x44,
  0x57, 0x58 → коды UEFI 0x0B–0x16. Раньше их не было ни у кого.
- **Служба `TerminalRead`** (хвост таблицы, адрес 0 — старое ядро, SDK тогда
  читает `TryReadKey`): байты без ожидания.
- **SDK, `TerminalKeys.cs`.** `TerminalKeyParser` — разбор по последовательностям
  xterm: CSI с параметрами, SS3, Alt, UTF-8, одинокий Esc по паузе 50 мс
  (`ESCDELAY`). Ответы глотаются: отчёт позиции курсора (сохраняется),
  device attributes (`CSI ? … c`), отчёты `CSI … t`, OSC. `CSI r;c R` — ответ,
  пока запрос ждёт, иначе F3 с модификаторами. Источник байт — делегат: разбор
  проверяется без клавиатуры.
- **`System.Console`:** `ReadKey` — через разбор; `KeyAvailable`;
  `GetCursorPosition`, `CursorLeft`, `CursorTop` — `ESC [6n` и ответ из ввода
  (клавиши, пришедшие в ожидании, не теряются); (0, 0), если ответа нет за
  0,5 с, как BCL на Unix.

Оболочка, LESS, Lua, JS, ReadLine уже читали через `Console.ReadKey` — правок
не понадобилось.

## Не сделано (donext)

- Владелец терминала: байты берёт тот, кто читает.
- Ctrl+C не останавливает программу — 0x03 только байт.
- Лаунчер (Terminal.Gui) — на сырых событиях: ему нужны отпускания.
- Раскладки — только US.

## Проверка

Рабочая копия, WHPX, `-SkipCoreClr`. Батарея autorun + строки step216:
ran=37 failed=0, ни одного FAIL. Новый тест DATATEST 29 (12 проверок, оба
прогона DATATEST): буквы, Enter, DEL, Tab; Ctrl+C = 0x03; стрелки и Home/End;
модификаторы `1;5`, `1;2`, `1;3`; Insert/Delete/PgUp/PgDn и Ctrl+Delete;
F1–F4, F5, F12, Shift+F1; back-tab, Alt+x; одинокий Esc и клавиша после паузы;
UTF-8; ответы проглочены; отчёт позиции во время запроса; настоящий
терминал — `GetCursorPosition` после `SetCursorPosition(7, H−1)` дал (7, 48).
Глазами (QMP): оболочка — вставка в середину стрелками (`xQyz`), Up/Home/
вправо, Ctrl+C (`^C`), Ctrl+A; LESS — PgDn, Down, F1, End без мусора на экране;
лаунчер — плитка оболочки, ввод в ней, выход, лаунчер отвечает.

## Уроки

- `enum.ToString()` в приложении — `(object)`: метаданных имён нет. Тест,
  описывавший клавиши через `ToString`, упал целиком при верном разборе.

## Файлы

- Ядро: `Kernel/Input/TerminalInput.cs` (новый), `Hal/TerminalConsole.cs`
  (делегат), `Hal/Platform.cs` (F-клавиши), `Kernel/Process/AppServiceBuilder.cs`
  (служба), `AppServiceTable.cs`.
- SDK: `TerminalKeys.cs` (новый), `SystemConsole.cs`, `AppServiceTable.cs`,
  `FreestandingPe.props`.
- Тесты: `DataTest/TerminalInputTest.cs` (новый), `Program.cs`, `DataTest.csproj`.
- limits, donext.
