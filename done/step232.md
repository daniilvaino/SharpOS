# step 232 — подсветка синтаксиса в `EDIT`

## Почему

Релиз 0.1, §3 — последний пункт редактора. Подсветка AvaloniaEdit (`.xshd`,
построчно с состоянием, MIT), вынесенная под терминал в `tui-cs/Editor`;
`XmlReader` для её загрузчика — step231.

## Что сделано

- **`vendor/TuiEditor/`** — `src/Terminal.Gui.Editor` целиком (коммит
  `b33bf48e`, 2026-09-11), собирается только подсветка (`TuiEditor.props`):
  движок, правила, загрузчик `.xshd` и 15 встроенных определений как
  встроенные ресурсы, интерфейсы документа. Сам редактор написан под
  Terminal.Gui v2 — не собирается. Вне сборки: `DocumentHighlighter` (держит
  состояние в дереве строк `TextDocument` v2), `XshdRoleMap` (роли тем v2),
  `SaveXshdVisitor` (`XmlWriter`). Наше в `SharpOS/`: `Terminal.Gui.Drawing.Color`
  (RGB из `#…` и CSS-имён) и `VisualRole` — два типа v2, которые подсветка
  называет; неявные `using` SDK. Правки в чужих файлах помечены `SharpOS:` /
  `SharpOS cut:` — псевдоним `Color` (иначе находится enum v1), вырез
  `TypeConverter` и `XshdRoleMap`, `GetTypeInfo()` → `Type.Assembly`. Сводка —
  `PROVENANCE.md`. `Nullable` — `annotations`, не `enable`: настройка общая на
  приложение, и `enable` включил бы предупреждения в коде `EDIT`.
- **`EDIT`: `HighlightedTextView`** — подкласс `TextView` v1. Определение — по
  расширению (`HighlightingManager.GetDefinitionByExtension`), имя языка — в
  строке состояния. Движку нужен от документа только текст строки и её
  смещение — документ из одной строки (`LineDocument` поверх `StringTextSource`).
  Состояние на начало каждой строки (`SpanStack`) хранится вместе с текстом,
  по которому получено: перерисовка пересчитывает с первой изменившейся строки и
  раскрашивает только видимые. Цвет — `SetNormalColor(line, idx)` (крючок v1).
  Цвета `.xshd` сделаны под белый фон: ближайший из 16 консольных, тёмные —
  в яркие, чёрный и серый — цвет текста. Сбой движка выключает подсветку и
  пишется в строку состояния, редактор живёт дальше.
- **Terminal.Gui:** `TextView.GetLine(int)` — `protected`, подклассу нужна
  строка модели по номеру (`SharpOS:`, `PROVENANCE.md`).
- **Ошибка раскладки `SharpOSKeyMap`, найдена этим шагом:** `ShiftMask`
  ставился символам (`key < CharMask`), а не служебным клавишам. `TextView`
  не печатает символ с `ShiftMask` — заглавные, `?`, `*`, кавычки, скобки
  в `EDIT` и полях лаунчера не набирались; Shift+стрелки не выделяли.
  Теперь как в штатных драйверах: маска — только при `SpecialMask`.
- **std** (оба яруса, форма BCL): `ImmutableStack<T>` (порт форка .NET 10,
  `Collections/Immutable/`, вырезан `[DebuggerTypeProxy]`);
  `StringBuilder.AppendFormat` (через `string.Format`); `char.IsLetter`/
  `IsDigit`/`IsLetterOrDigit`/`IsWhiteSpace(string, int)`; `string.IndexOf`/
  `IndexOfAny`/`LastIndexOf` с диапазоном; `Stack<T>(IEnumerable<T>)`;
  `IServiceProvider`; `CollectionBuilderAttribute`.
- **Лицензии:** `vendor/TuiEditor/LICENSE` (gui.cs) и
  `LICENSE-AvaloniaEdit.txt` (© AlphaSierraPapa, заголовок файлов движка) — в
  `\sharpos\licence\`. SBOM: `Terminal.Gui.Editor` с предком AvaloniaEdit,
  `System.Collections.Immutable` и `System.Private.Xml` — последний пропустил
  step231.

## Проверка

Рабочая копия, WHPX; EditApp и ядро собираются без ошибок и новых
предупреждений. Клавиши — через QMP (`work/tmp/keys259.py`, `keys260.py`).

- Прогон 259: `EDIT` на `.cs` — ключевые слова, строки, числа, комментарии,
  `#region`; блочный комментарий тянется на следующую строку до `*/`; в строке
  состояния «C#». `.json` — имена полей, строки, числа, `true`, `null`; «Json».
  Набор `/*` не прошёл — так нашлась ошибка раскладки. XMLTEST 20/20.
- Прогон 260 (после исправления раскладки): `/* ` в начале файла — весь файл
  до `*/` стал комментарием, ниже подсветка как была (пересчёт с первой
  изменённой строки); Shift+→ ×2 выделяет; F3 сохранил ровно набранное
  (`cat`). В `\sharpos\licence\` — `Terminal.Gui.Editor.txt` и
  `AvaloniaEdit.txt`.
- Батарея 261 (полная сборка, правка раскладки задевает все приложения на
  Terminal.Gui): с тестами 62/62 — AOTTESTS 362/362 ×3, XMLTEST 20/20,
  COMPRESSTEST 53/53, regexcheck 93/93; без тестов 6/6.
## Не сделано

- Палитра: у разных ролей совпадают цвета (16 консольных) — donext, «EDIT:
  что осталось».
- Остальной `System.Collections.Immutable` — по потребителю.

## Файлы

`vendor/TuiEditor/**` (новые: копия апстрима, `LICENSE`,
`LICENSE-AvaloniaEdit.txt`, `PROVENANCE.md`, `TuiEditor.props`, `SharpOS/*`);
`vendor/Terminal.Gui/{Terminal.Gui/Views/TextView.cs,SharpOS/SharpOSKeyMap.cs,PROVENANCE.md}`;
`apps_native/EditApp/{EditApp.csproj,Program.cs,HighlightedTextView.cs}`;
`std-no-runtime/Collections/Immutable/*` (новые),
`std-no-runtime/{IServiceProvider.cs,Runtime/CompilerServices/CollectionBuilderAttribute.cs,Text/StringBuilder.AppendFormat.cs}`
(новые), `Std.props`, `Bcl/Stack.cs`, `SystemString.Search.cs`,
`Char.Statics.cs`; `run_build.ps1` (лицензии), `sbom.cdx.json`, `README.md`,
`release.md`, `donext.md`, `docs/nativeaot-nostd-kernel-limits.md`,
`done/step231.md` (итог XMLTEST).
