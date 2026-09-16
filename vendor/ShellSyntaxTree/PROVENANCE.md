# ShellSyntaxTree

Разбор командной строки bash в дерево: кавычки, склейка слов, `&&` `||` `;` `|`,
перенаправления, подстановка переменных и путей.

- Откуда: https://github.com/Aaronontheweb/ShellSyntaxTree
- Лицензия: Apache-2.0 (`upstream/LICENSE`)
- Снимок: тег `0.3.5`, подмодуль `upstream/`, 2026-09-15
- Зависимостей NuGet нет; цели `netstandard2.0` и `net8.0`, в проекте
  `IsAotCompatible`

## Подмодуль, а не копия

Правок нет, поэтому копировать незачем: `upstream/` — подмодуль, как у
`apps_native/TriCNES` и `apps_native/Fami`. Собираем исходники сами через
`ShellSyntaxTree.props` — пакетом подключить нельзя, приложения строятся с
`IlcSystemModule` на себя, общей `System.Private.CoreLib` нет, и ссылки
пакетной сборки на `[netstandard]System.String` разрешать нечем.

## Что взято

Половина bash целиком: публичное дерево (`Clause`, `Arg`, `VerbChain`,
`Redirect`, `CompoundOperator`), лексер и разбор bash, общий разбор, слой
разрешения путей.

`Clause` отдаёт глагол, список `Arg` с полем `Resolved` — значением уже без
кавычек, — перенаправления и оператор связи. Этого хватает, чтобы по дереву
выполнять, хотя писалось оно для анализа.

## Что исключено и почему

- **`Internal/Pwsh/**` и `PwshParser.cs`** — разбор PowerShell, 17.8 тыс. строк
  из 29. Нам не нужен, и только он тянет `System.Text.RegularExpressions`,
  которых на ярусе приложений нет.
- **`Properties/IsExternalInit.cs`** — этот тип объявляет наша std
  (`std/no-runtime/shared/Runtime/CompilerFeatures.cs`), два объявления в одной
  сборке не собираются. Сам файл std в рецепт приложений до этого не входил —
  без него `init` и `record` не компилируются вовсе, и первая же сборка дала
  больше сотни `CS0518`; добавлен в `FreestandingPe.props`.
- **`tests/`, `samples/`, `tools/`** — в подмодуле лежат, не компилируются.

`PwshParserOptions.cs` и `Internal/Resolving/PwshResolver.cs` **оставлены**:
`BashStructuralCoordinator` → `ShellSyntaxProjection` →
`AuthoredOperandSemanticsProjection` расходится на оба резолвера, а
`PwshResolver` самодостаточен (709 строк, только `System`, `System.IO`,
`System.Text`). Вырезать эту нитку — больше правок, чем пользы.

## Чего не хватало нашей среде

Ничего. Набор пространств имён оставленного кода — `System`,
`System.Collections.Generic`, `System.Collections.ObjectModel`, `System.Text`,
`System.Globalization`, `System.Runtime.CompilerServices`, `System.IO` — есть
целиком. `System.IO` в `BashResolver.cs` вообще только в комментариях.

## Обновление

```
git -C vendor/ShellSyntaxTree/upstream fetch --tags
git -C vendor/ShellSyntaxTree/upstream checkout <тег>
```

Дальше сверить список файлов в `ShellSyntaxTree.props`: исключения привязаны к
путям, и переехавший файл молча выпадет из сборки или притащит PowerShell
обратно.
