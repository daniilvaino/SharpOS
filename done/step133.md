# step 133 — PeNet native-PE parser vendored on bare metal (PE-migration milestone 1)

## Контекст

Первый шаг ELF→PE миграции апп (donext.md workstream 2). Прежде чем строить
PE-loader, надо уметь **читать** PE. Вместо clean-room парсера — вендорим
[PeNet](https://github.com/secana/PeNet) (Apache-2.0) и гоняем его на нашей
std на bare metal. Задача-проверка: «взлетит ли реальный сторонний
PE-парсер поверх нашего std без правок логики».

North-star — managed DOOM; PE-машинария для managed-апп на пути к нему.

## Результат

**16/16 penet-проб зелёные** на kernel-AOT. Синтетический PE32+ (2 секции,
реальные optional-header поля, 16× data directory) проходит
DOS → NT → optional header → data directories → section table. Ноль правок
логики парсинга — только форк одного файла + нейтрализация двух косметических
хелперов. Логи: категория `[PeNet]` в `probe_report.ps1`.

## Что где

**Vendored cut** `PeNet/` (15 .cs, sibling к `iced/`, glob в OS.csproj):
FileParser (`IRawFile`, `BufferFile`), Header (`AbstractStructure`, Pe/6 структур),
HeaderParser (`SafeParser`, Pe/4 парсера), `ExtensionMethods` (только Is64Bit/Is32Bit).
Entry — `NativeStructureParsers(IRawFile)` (internal — доступен, т.к. PeNet
компилится в ту же OS.dll). `PeFile` god-object НЕ вендорен (тянет
authenticode/net/imports в ctor'е). Полная карта — `PeNet/PROVENANCE.md`.

**Probe** — `NativeAotProbe.Probe_PeNet` + `BuildSyntheticPe64` (byte-precise
PE32+ layout, opt-base 0x98, секции с 0x188).

## Новые std-кирпичи (по дороге)

### 1. `System.Text.Encoding` (partial) — `std/.../Text/Encoding.cs`
ASCII / Unicode(UTF-16LE) / BigEndianUnicode / UTF8 / Latin1 с
`GetString`/`GetBytes`/`GetByteCount`. Вырезано: fallback-стейт-машины
(invalid → `?`/U+FFFD), `EncodingProvider`/code-page registry, preamble/BOM,
streaming encoder/decoder. Static-факторки — factory property (не кешированное
static-поле, cctor-trap §1). Добавлен в curated compile list OS.csproj.

### 2. `new string(char[])` / `(char[], int, int)` — РАНЬШЕ НЕ РАБОТАЛ
Корень: публичные char[]-ctor'ы в `SystemString.cs` были placeholder'ами
(ILC игнорит тело `newobj`-ctor'а и редиректит на метод `String::Ctor(...)`),
а парного `Ctor(char[])`/`Ctor(char[],int,int)` не было → `ILCompiler`
падал `Code generation failed ... Expected method 'Ctor' not found on type
'string'`. Никто до Encoding не строил строку из char-массива, потому и
всплыло только сейчас. Фикс — добавлены оба `Ctor` (FastAllocateString +
`fixed`-fill, как эталонный `Ctor(char c, int count)`).

### 3. `String.Trim/TrimStart/TrimEnd(char)` + `params char[]`
Был только parameterless. `ImageSectionHeader.Name` делает
`.TrimEnd((char)0)` → CS1501. Добавлены char + `params char[]` overload'ы
инлайн (Length/indexer/Substring).

## Форк / cut (локальные правки vs upstream)

- **`BufferFile.cs`** — backing `Memory<byte>` → `byte[]` (нет `Memory<T>`);
  range-слайсы `[a..]` → `Span.Slice` (нет `System.Range`);
  `MemoryMarshal.Write(span, in v)` → `(span, v)` (наш `Write<T>` берёт `T`
  by-value); null-скан вручную. Семантика 1:1.
- **`ImageFileHeader.cs` / `ImageSectionHeader.cs`** — косметические
  `Resolve*Characteristics` использовали `Enum.GetValues<T>()` (нужны
  reflection-метаданные, которых нет) → тело обрезано (сырое значение
  парсится). `MachineNames` был `static readonly Dictionary = new(){...}`
  = cctor-trap → factory property.

## Ловушки (для следующего раза)

- **ILC string-ctor redirect**: любой `new string(<новая сигнатура>)` требует
  парного `private static string Ctor(<та же сигнатура>)`, иначе codegen-фейл.
  Паттерн — placeholder-ctor + Ctor-метод.
- **`Enum.GetValues<T>()`** — reflection, у нас нет. Флаг→строка резолверы в
  портируемом коде надо резать или переписывать на switch.
- **`static readonly RefType = new()`** в чужом коде — cctor-trap, ловить при
  вендоринге (grep `static +readonly +[A-Z].*= *new`).
- Missing-BCL-overload'ы всплывают по одному на сборку — чиним точечным
  BCL-дополнением в std, НЕ форком vendored-кода.

## Проверка

`run_build.ps1` собрал (после трёх итераций: `TrimEnd(char)` CS1501 →
overload; `new string(char[])` ILC-фейл → Ctor-методы). QEMU-ран: 16/16
`[PeNet]` OK, регрессий в остальной батарее нет. limits.md §8 синхронизирован
(Encoding partial + string-ctor + Trim + PeNet capability).

## Откладываем (фаза 2)

- **mini-LINQ** (Where/Select/ToArray/…) — разблокирует imports/exports/reloc
  парсеры PeNet (`DataDirectoryParsers`) и .NET-метаданные. Это же — следующий
  north-star кирпич перед DOOM.
- `Marshal.PtrToStructure`/`AllocHGlobal` — для `ImageLoadConfigDirectory`.
- `Header/Net` (managed-метаданные) — для парсинга managed PE-апп.
- `PeNet-main/` (upstream, 141 файлов) — reference, вынести в gc-experiment
  как `iced_original` (не в дереве репо).

## Next

Либо mini-LINQ (разблокирует фазу-2 PeNet + north-star), либо начать саму
PE-loader машинарию (маппинг секций по SectionAlignment, relocations,
imports resolution) — теперь, когда заголовки читаются.
