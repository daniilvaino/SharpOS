# step 135 — PeNet phase-2: imports / exports / base relocations

## Контекст

Продолжение PE-migration (donext workstream 2). После native-заголовков (step133)
и mini-LINQ (step134) вендорим phase-2 парсеры PeNet — imports/exports/reloc.
Они нужны PE-loader'у (M2 relocations, M3 import resolution) и были отложены как
LINQ-heavy. Теперь LINQ есть → разблокировано.

## Результат

**6/6 `[PeNet]` phase-2 проб зелёные** (всего PeNet 22/22), ноль регрессий.
Синтетический PE32+ с import/export/reloc таблицами парсится end-to-end.

## Что где

12 файлов в `PeNet/` (glob подхватил, csproj не трогали):

- **Imports**: `ImageImportDescriptor`, `ImageThunkData`, `ImageImportByName`,
  `ImportFunction` + `ImageImportDescriptorsParser`, `ImportedFunctionsParser`.
- **Exports**: `ImageExportDirectory`, `ExportFunction` +
  `ImageExportDirectoriesParser`, `ExportedFunctionsParser`.
- **Relocations**: `ImageBaseRelocation` (+`TypeOffset`) +
  `ImageBaseRelocationsParser`.

Парсеры инстанцируются напрямую (как `NativeStructureParsers` в phase-1),
минуя 227-строчный `DataDirectoryParsers` (тянет resources/metadata/authenticode).

**Probe** — `Probe_PeNetPhase2` + `BuildSyntheticPe64Phase2`: PE с одной
identity-mapped секцией (VirtualAddress == PointerToRawData → RVA == file offset),
несущей все три таблицы. Проверяет: 1 import descriptor, imported func
"MyFunc"/hint=7/DLL "TESTDLL", export dir nfuncs=1, exported func "ExpFunc"/
ordinal=1/addr=0x1000, reloc block VA=0x1000/size=0x0C/2 TypeOffsets.

## Std-дополнения (BCL-compat)

- **`List<T>.ToArray()`** — instance-метод (как в BCL). Раньше не было →
  `impFuncs.ToArray()` в vendored-парсерах (без `using System.Linq`) не
  резолвился (CS1061). Instance-метод приоритетнее LINQ-extension.
- **`ExtensionMethods.RvaToOffset`** — перетипизирован upstream'ский
  `ICollection<ImageSectionHeader>` (`.Count`/`.ElementAt`) → `ImageSectionHeader[]`
  (`.Length`/`[i]`). Массивы не реализуют `ICollection<T>`/`IEnumerable<T>`
  (нет SZArrayHelper) → interface-версия упала бы `iface-resolve` в рантайме;
  индексация массива прямая. Семантика та же.

## Заметки

- Парсеры юзают `List<T>.ToArray()`/`.Last()` — `List` реализует `IEnumerable<T>`,
  безопасно. Результаты — массивы (`ImportFunction[]` и пр.), в probe индексирую
  напрямую, НЕ итерирую как IEnumerable (массивы не IEnumerable<T>, step134).
- Все 12 файлов чисты от phase-1-ловушек (Enum.GetValues / cctor-trap /
  Marshal / range) — bulk-скан перед копированием.

## Ловушка репорта (не баг phase-2)

`probe_report.ps1` показал ложный HALT на ThreadPingPong — не скрипт, а
**интерливинг лога**: `heap grow pages: 4` из аллокатора врезался в строку
ping-pong-результата (`ping-pong probe: T1=5/5 T2=5/heap grow pages: 4`).
`Console`/`Log` не потокобезопасны при конкурентной записи из потоков. Ping-pong
реально прошёл (пробы после него OK). Ортогонально PE. Фикс (атомарный лог) —
отдельный backlog, если threading-пробы будут мешать.

## Проверка

`run_build.ps1` (после 1 итерации: `List.ToArray` CS1061 → добавлен instance-
метод). QEMU: PeNet 22/22, батарея без регрессий. limits.md §4/§8 + PROVENANCE
синхронизированы.

## Next

Kernel-side PE-loader: теперь читаются imports/exports/reloc — можно
`PeLoader.MapImage` (section mapping по SectionAlignment) → base relocations
(.reloc) → import resolution → .pdata/EH → execute. Плюс build-side: эмиссия
PE-апп (ILC win-x64) вместо ELF.
