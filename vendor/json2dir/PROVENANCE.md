# json2dir (C#, NativeAOT)

Дерево файлов и каталогов по JSON-документу со стандартного входа
(спецификация RFC J2D-1, https://github.com/kitsunoff/awesome-json2dir).

- Откуда: https://github.com/json2dir-guru/json2dir-aot
- Лицензия: MIT (`LICENSE`)
- Снимок: коммит `d1515c6fd1754e058c0f33219346a8afa2ddc487` (2026-10-06), взят 2026-10-14
- Взято: `Program.cs`, `Json.cs` → `src/`. `json2dir.csproj` — нет: приложение
  собирает `apps_native/Json2Dir/Json2Dir.csproj` (вход `Entry.cs`).

Программа: `JSON2DIR.EXE` в `\bin`; `READ tree.json | JSON2DIR`.

## Правки (`SharpOS cut:`) — FAT-версия

- **Символические ссылки.** У FAT их нет. `["link", target]` отказывает при
  проверке всего документа (`Validate`), до любой записи на диск — код 1; ветка
  `File.CreateSymbolicLink` в `Apply` убрана.
- **Биты режима.** У FAT их нет. `["script", content]` пишется обычным файлом,
  `File.SetUnixFileMode` убран.
- **Поток со стеком 256 МиБ.** `Main` запускал `Run` в потоке с большим стеком
  ради глубоко вложенных документов. Потоки здесь стартуют через `Task.Run`, их
  стек — размер яруса (`Thread.Start` в std сознательно нет). `Run` выполняется
  в потоке программы; документ глубже, чем позволяет его стек, не пройдёт.
- **`Main` — `internal`.** Его зовёт вход приложения
  (`apps_native/Json2Dir/Entry.cs`): C#-точка входа там — `Main()` без
  аргументов, ILC не умеет стартовать свободный образ с `Main(string[])`.

## Что добавлено в std/SDK ради него (step208)

`Console.OpenStandardInput` (стандартный вход трубы байтами), `Console.Error`
(поток ошибок: экран и COM4), `Stream.CopyTo`, `File.GetAttributes` и
`FileAttributes`, `SupportedOSPlatformAttribute`/`UnsupportedOSPlatformAttribute`.
Остальное, что он трогает (`FileStream` с `CreateNew`, `File.Delete`,
`Directory.CreateDirectory`, строгий `UTF8Encoding`), было.

## Ограничения FAT, которых спецификация не знает

Спецификация запрещает в именах только `/` и NUL; FAT не принимает ещё
`: * ? " < > | \` — такое имя кончается ошибкой ввода-вывода (код 1). Имена
сравниваются без учёта регистра: `A.txt` и `a.txt` — один файл.

Проверка — DATATEST, тест 27.
