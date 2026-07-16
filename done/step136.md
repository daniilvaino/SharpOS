# step 136 — kernel-side PE-loader stage 1-3: flatten / relocate / bind IAT

## Контекст

PE-migration (donext workstream 2), kernel-half. После PeNet phase-1/2
(step133/135, PE-чтение) — сам загрузчик. Выбран безопасный путь: PE-loader
как **чистые трансформы над byte[]-буфером** (никаких live page-tables /
исполнения), тестируемые probe'ами — как весь остальной сессионный паттерн.
Финальный map-into-pages + jump отложен до реального PE-аппа (build-side).

## Результат

**16/16 `[PeLoad]` проб зелёные**, батарея OK 114 / FAIL 1 (только известный
EnumToString), ноль регрессий. Цепочка **flatten → relocate → bind IAT** работает
на синтетических PE.

## Что где (`OS/src/Kernel/Pe/`, авто-глоб)

### M1 — `PeImageLayout.TryFlatten(file)`
Raw PE-файл → in-memory image layout: `SizeOfImage`-буфер, headers@0 +
каждая секция по её `VirtualAddress` (RVA), BSS (`VirtualSize > SizeOfRawData`)
и паддинг = 0. Заголовки — через PeNet `NativeStructureParsers`. Отдаёт
`imageBase`, `entryPoint = imageBase + AddressOfEntryPoint`, `sectionCount`.
8 проб: image size, imagebase, entrypoint, headers(MZ), section-байт на RVA,
BSS-ноль.

### M2 — `PeRelocations.TryApply(image, preferredBase, actualBase, relRva, relSize)`
Base relocations по `.reloc`. `delta = actualBase - preferredBase`; парсит
блоки через `ImageBaseRelocationsParser` (на flattened RVA==offset), патчит
**DIR64** (64-bit) / **HIGHLOW** (32-bit), ABSOLUTE пропускает. no-move → no-op
(0 applied), moved-без-reloc → fail. 4 пробы: значение 0x140001234 → +delta →
0x150001234, no-op при preferred-base.

### M3 — `PeImports.TryResolve(flatImage, file, resolver)`
Резолв импортов + бинд IAT. Парсинг импортов идёт по **файлу** (`RvaToOffset`
даёт корректный file-offset через section-заголовки); резолвленные адреса
пишутся в IAT-слоты **flattened-образа** по `slotRva = IAT.VA + IATOffset`
(в flattened == offset). `resolver(ImportFunction) → addr` (0 = unresolved).
PE32 (32-битные слоты) пока не поддержан. 4 пробы: pre-slot, resolve 1/0,
IAT bound → 0xCAFEF00D00001000, unresolved-счётчик.

## Ключевой инвариант

**Парсинг — по файлу, патч — по flattened-образу.** Vendored-парсеры юзают
`RvaToOffset(sections)` = `rva - VA + PointerToRawData` (даёт FILE-offset). На
flattened-образе (где данные по RVA) это дало бы неверный адрес. Поэтому:
импорты/релоки ПАРСЯТСЯ из исходного файла, а ПРИМЕНЯЮТСЯ к flattened-буферу по
RVA. Reloc-парсер, правда, работает прямо на flattened (там reloc-блоки по
RVA==offset, RvaToOffset не нужен — offset передаётся напрямую).

Probe'ы юзают секции с **VA ≠ PointerToRawData** (import-тест) — чтобы
flatten+patch тестировался по-настоящему, не в identity-вырожденности.

Utility: `PeRelocations.ReadU32/U64/WriteU32/U64` (LE, byte-wise) —
переиспользуются M2/M3 и probe'ами.

## Проверка

`run_build.ps1` (3 итерации по одной на M1/M2/M3: `using System.Linq` не при
чём — тут были `using PeNet` для extension-методов в PeImports). QEMU: 16/16
`[PeLoad]`, регрессий 0. probe_report.ps1 — категория `[PeLoad]`. limits.md §8 +
PROVENANCE синхронизированы. M1/M2/M3 в одном коммите (буфер-трансформы,
логически один загрузчик).

## Откладываем

- **Per-section page-протекция** (NX/RO по `Characteristics`), .pdata/EH
  регистрация per-image, **map-into-pages + execute** — нужен реальный PE-апп.
- **Build-side**: эмиссия апп как PE (ILC win-x64) вместо ELF (linux-x64),
  убирает WSL-зависимость.
- PE32 (32-битные IAT-слоты) в PeImports; forwarded-exports; TLS/Debug/
  LoadConfig директории.

## Vendor reorg (в этом же коммите)

Vendored-либы съехали в `vendor/`: `iced/` → `vendor/Iced/` (215 файлов),
`PeNet/` → `vendor/PeNet/` (29). git-renames (история цела). Обновлены
include-пути: `OS/OS.csproj` (оба glob + комменты) и
`bootasm/BootAsm.Generator/BootAsm.Generator.csproj` (iced glob — второй
потребитель Iced-сорса), плюс doc-ссылки (`probe_report.ps1`, PROVENANCE,
limits, probe-коммент). Per-file `git mv` (целиком папка падала на
залоченных IDE untracked `.lscache`), остатки снесены.

## Next

Build-side PE-апп (ILC win-x64) — тогда вся цепочка замкнётся на реальном
образе: flatten → relocate → bind (kernel-экспорты) → protect → .pdata → jump.
