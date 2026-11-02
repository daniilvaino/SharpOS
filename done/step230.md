# step 230 — текстовый редактор `EDIT`

## Почему

Релиз 0.1, раздел 3: текстовый редактор. План — `TextView` Terminal.Gui
(собирался давно), основа — пример Editor из UICatalog (MIT).

## Что сделано

- **`apps_native/EditApp`** → `\bin\EDIT.EXE`: `EDIT [путь]`. Пример Editor
  перенесён из «сценария» UICatalog в программу (`TerminalGui.props`,
  `SharpOSDriver`, `SharpOSTheme`). Есть: правка, отмена/повтор и буфер обмена
  (клавиши самого `TextView`: Ctrl+Z/R, Ctrl+C/W/Y), поиск и замена (окно с
  вкладками, Ctrl+S/H, «следующее», «заменить всё»), перенос строк,
  автодополнение словами текста, «только чтение», строка состояния со
  строкой и столбцом, вопрос о сохранении при выходе, F2/F3/F4, Ctrl+Q, меню
  по Alt и F9. Вырезано: меню культур, демо-файл, меню-демонстрации контрола.
  Изменено: файл — из командной строки (нет файла — новый под этим именем);
  о сохранении и поиске — строкой состояния, не окном на каждое; слова для
  автодополнения — разбором, а не Regex (не тянуть `Regex.props`). Поле текста
  — своя схема цветов: `TextView` рисует текст цветом `Focus`, а у темы это
  подсветка (вся страница была чёрным по коричневому).
- **`FileDialog` Terminal.Gui** включён (`OpenDialog`/`SaveDialog`): вырез
  `FileSystemWatcher` (`SharpOS cut:` — список папки читается при входе) и
  остаток NStack — обход байтов `Utf8.DecodeRune` стал обходом рун строки.
  В SDK — `DirectoryInfo.GetFileSystemInfos`/`EnumerateFileSystemInfos`
  (папки, потом файлы).
- **Драйвер клавиш Terminal.Gui** (`SharpOSKeyMap`): Ctrl+буква отдавался
  управляющим кодом (`(Key)17 | CtrlMask` для Ctrl+Q), а библиотека везде ждёт
  букву с маской (`QuitKey = Key.Q | CtrlMask`, `TextView`: `Key.Y | CtrlMask`…)
  — ни одна Ctrl-привязка библиотеки не срабатывала, и в лаунчере тоже. Теперь
  как у стоковых драйверов, с `ShiftMask` для Ctrl+Shift.
- **Массив как необобщённая коллекция падал паникой ядра.** `OpenDialog`
  отдаёт список типов (`string[]`) в `ComboBox`/`ListView` как `IList` →
  `[PANIC] iface-resolve: no impl slot` (символизировано по PDB EDIT:
  `__Array<String>`, интерфейс `ICollection`, слот 0). У upstream необобщённые
  `ICloneable`/`ICollection`/`IList` массива — на `System.Array`, рантайм
  доходит до них от типа массива к базе; наш резолвер (`FindImplSlot`) на
  массиве останавливается (`IsArray → break`: «родственный» тип массива —
  элемент). Исправлено в std: `Array<T>` перечисляет их сам, с семантикой
  `System.Array`, записи ложатся в карту каждого типа массива. AOTTESTS: 8
  проверок (`Count`, индексатор, `IndexOf`/`Contains`, фиксированный размер,
  `Add` отказ, `CopyTo`, `int[]` упаковкой, `Clone`).

## Проверка

Рабочая копия, WHPX, `-SkipCoreClr`. Клавиши — через QMP `send-key` скриптом
(`work/tmp/keys25x.py`), экран — `screendump`:
- 252: набор двух строк, F3, `cat` — те же строки; Ctrl+Q не закрыл (драйвер);
- 253: Ctrl+Q закрыл; Alt+F сдвинул курсор (привязка `TextView` word-right);
- 254: Alt+F — меню File, Q — выход; F2 — паника ядра (массив как `IList`);
- 255: F2 — диалог, Esc, Ctrl+Q — все три сеанса до конца, паники нет;
  AOTTESTS: новые проверки 7/7 (в голом запуске без `--pipe-writer-dies`
  «pipe 2» ×2 и «collect reclaimed» не проходят — проверки этого окружения);
- батарея 256 (всё пересобрано, 0 предупреждений; к сценарию 246 добавлены
  `LINES`/`BYTES` step227, `project` KQL step228, `ls /sharpos/licence`
  step229, `EDIT --help`/код 2): с тестами **ran=61 failed=1**, AOTTESTS
  362/362 ×3, паник 0; `-NoTests` 6/6. Единственный провал — PROCTEST №15
  (доля счётчика рядом со сборщиком 20.5 % при пороге 25 %): плавал и до этих
  шагов — провалы в 223, 224, 230, 236, 237, 241, 243; прогон 257 (три PROCTEST
  без пересборки) — 23.5/24.4/22.7 %. Записан как SYM-008
  (`docs/open-symptoms.md`).

## Не сделано

- Подсветка синтаксиса (`.xshd` AvaloniaEdit через TurboXml) — следующий шаг §3.
- `FileSystemWatcher`, обход резолвером массива до `System.Array` — donext.
- Мыши нет (драйвер Terminal.Gui), выделение — Shift+стрелки.

## Файлы

`apps_native/EditApp/{EditApp.csproj,Program.cs}` (новые);
`vendor/Terminal.Gui/{TerminalGui.props,Terminal.Gui/Windows/FileDialog.cs,SharpOS/SharpOSKeyMap.cs}`;
`apps_native/sdk/FileSystemInfo.AppHost.cs`; `std-no-runtime/Runtime/ArrayT.cs`;
`apps_native/AotTests/FormatTests.cs`; `README.md`, `release.md`, `donext.md`,
`docs/nativeaot-nostd-kernel-limits.md`.
