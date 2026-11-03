# step 236 — язык оболочки

## Почему

Релиз 0.1, §6. Оболочка исполняла только `a && b`, `a | b` и `$ИМЯ`; `x=1`, `if`,
циклы и `$(...)` отклонялись.

## Разборщик заменён

ShellSyntaxTree — анализатор безопасности: на `if`, `while`, `x=1`, `[`, `{ }` он
отвечает «не разбирается», и это его назначение, а не недоработка. Поиск дал
`Meziantou.Framework.Language.Shell` (MIT): дерево всего языка, диагностика с местом,
никогда не бросает. Подключён в `vendor/Meziantou.Shell`, подмодуль ShellSyntaxTree
удалён. Вырезов два (`FrozenDictionary`, `Enum.GetValues`), std дополнена по BCL:
`string.IndexOf(char, int, StringComparison)`, `MemoryExtensions.IsWhiteSpace`,
`Array.Exists/Find/FindIndex`, `Enumerable.Order`. `SHELL.EXE` остался 1,37 МБ.

## Что сделано

`apps_native/Shell/Language.cs` — обход дерева. `Executor` потерял разбор строк и
клаузы, `RunPipeline` принимает готовые стадии.

- **Управление:** `if`/`elif`/`else`, `while`, `until`, `for`, `case`, `break N`,
  `continue N`, `{ }`, `( )`, `!`.
- **Переменные:** `x=1`, `X=1 программа`, `export ИМЯ` переносит в окружение; `$?`, `$#`,
  `$1`, `"$@"`, `${x:-слово}`, `:=`, `:+`, `:?`, `${#x}`.
- **Раскрытие:** `$(...)`, `$((...))`, деление на поля, шаблоны путей.
- **`test`/`[`**, **`< файл`** (`READ` рядом со стадией, объекты остаются объектами).
- **Составная команда стадией** (`for … done | sort`, `while … done < f`) — `SHELL -c` с
  переменными в окружении.
- **Сценарии и приглашение** копят строки, пока конструкция не закрыта.

Заметное решение: `$(...)` исполняется в этой же оболочке, а не дочерней: дешевле на
порядок, встроенные работают. Переменные, окружение и каталог сохраняются и
восстанавливаются. Жалобы оболочки внутри `$(...)` идут на терминал, а не в значение.

## Ловушки

- `[$x]` разборщик держит одним токеном-шаблоном: без отдельной обработки `$x` внутри
  не раскрывался.
- Запуск скачанного кода на хосте запрещён средой: форма дерева определена чтением
  исходников, разборщик исполняется только в госте.

## Проверка

Рабочая копия, WHPX, `-SkipCoreClr`. Батарея 269: с тестами 66/66, без тестов 9/9.
DATATEST 351/351 и 348/348, включая новый тест 32 и переписанный список отказов в
тесте 19. PROCTEST 85/85 и 80/80, regexcheck 93/93, XMLTEST 20/20. Вся батарея идёт
через новый разборщик, в том числе `n>&m`, `sh -c` и строки с `KQL '…'`.

Не проверено: hosted-ярус, железо; продолжение незакрытой конструкции на приглашении
(только клавиатурой, в автопрогоне нет).

## Не сделано

Функции, here-документы, `[[ ]]`, `(( ))`, массивы, `&`, `IFS`, `set -e` — отклоняются
или отсутствуют. `while read x` не нужен: строки — объекты, их режет `lines`, а `read`
остаётся `READ.EXE`. Вечный цикл Ctrl+C не прервать — §10.

## Файлы

`vendor/Meziantou.Shell/` (новый), `vendor/ShellSyntaxTree/` (удалён), `.gitmodules`,
`apps_native/Shell/{Language.cs (новый),Executor.cs,Program.cs,Shell.csproj}`,
`std-no-runtime/{SystemString.Search.cs,Runtime/Array.cs,Runtime/MemoryExtensions.Search.cs,Linq/Enumerable.Ordering.cs}`,
`apps_native/DataTest/{ShellLanguageTest.cs (новый),ShellCommands.cs,Program.cs,DataTest.csproj}`,
`run_build.ps1`, `sbom.cdx.json`, `README.md`, `release.md`, `donext.md`,
`docs/{nativeaot-nostd-kernel-limits.md,build-warnings.md}`.
