# Meziantou.Framework.Language.Shell

Разбор сценариев оболочки в дерево: POSIX sh, bash, zsh, PowerShell, cmd. На нём
стоит язык `SHELL.EXE` (`apps_native/Shell/Language.cs`), диалект — bash.

- Откуда: https://github.com/meziantou/Meziantou.Framework
- Лицензия: MIT (`LICENSE`)
- Снимок: коммит `aebd59eee3f7ca8ca956aafdcb5e82567c43b80e`, 2026-11-03
- Взято: `src/Meziantou.Framework.Language` и
  `src/Meziantou.Framework.Language.Shell` целиком
- Подключение: `Meziantou.Shell.props`, исходниками, как и всё у приложений

## Почему не ShellSyntaxTree

ShellSyntaxTree, стоявший здесь до step236, — анализатор безопасности: он
отказывается от `if`, `while`, `x=1`, `[`, `{ }`, потому что не может доказать их
свойства. Для исполнителя это тупик. Этот разборщик даёт дерево всего языка.

## Что не исключено

Половины PowerShell и cmd компилируются: ничего, чего нет у нас, они не требуют.
`SHELL.EXE` от замены разборщика почти не вырос (1,37 МБ).

## Правки (`SharpOS cut:` в файле)

`Syntax/InternalSyntax/SyntaxFactory.cs`:

- `FrozenDictionary` → `Dictionary`: `System.Collections.Frozen` в std нет; две
  таблицы на десятки записей, собираются один раз. Порт Frozen ради них не окупается.
- `Enum.GetValues<SyntaxKind>()` → цикл от `None` до последнего вида: метаданных
  перечислений у нас нет, вызов вернул бы пустоту и молча оставил таблицу пустой.
