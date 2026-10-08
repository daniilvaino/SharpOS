# ReadLine

Редактор строки в духе GNU Readline: курсор, Home/End, история, Ctrl-клавиши
Emacs, дополнение по Tab.

- Откуда: https://github.com/tonerdo/readline
- Лицензия: MIT (`LICENSE`)
- Снимок: коммит `9665e3149b8b0614c66729ca5ca6eba6055e70f9` (2018-06-12), взят 2026-10-08
- Взято: `src/ReadLine/` — `ReadLine.cs`, `KeyHandler.cs`, `IAutoCompleteHandler.cs`,
  `Abstractions/IConsole.cs`, `Abstractions/Console2.cs`. Демо, тесты, сборка — нет.

## Что не собирается

`Abstractions/Console2.cs` — переходник к курсору `System.Console`
(`CursorLeft`, `SetCursorPosition`, `BufferWidth`): позицию курсора нашему
терминалу спросить нечем. Лежит как есть; свой `Console2` даёт приложение
(оболочка — `apps_native/Shell/ReadLineConsole.cs`: считает курсор сама и
двигает его относительными ESC-последовательностями).

## Правки (`SharpOS cut:`)

- `KeyHandler.BuildKeyInput`: имя клавиши строилось `Enum.ToString()`, а у
  ядерного AOT-яруса метаданных перечислений нет. Имена, которые знает таблица
  действий, выписаны явно (`KeyName`); результат для них тот же.
