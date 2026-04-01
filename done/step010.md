# step010

Дата: 2026-04-01

## Цель

Сделать `ELF loader scaffold` без `jump`:
- пройти `PT_LOAD`;
- выделить страницы;
- замапить их через `Pager`;
- скопировать `filesz`;
- занулить хвост `memsz - filesz`;
- вывести итог по entrypoint и загруженным диапазонам.

## Что реализовано

### 1. Добавлен загрузчик ELF-образа

Добавлен файл:
- `OS_0.1/src/Kernel/Elf/ElfLoader.cs`

Реализовано:
- `ElfLoader.TryLoad(...)`;
- проход по `PT_LOAD` segment headers;
- проверка границ сегмента и диапазона данных в ELF image;
- page-align helper для диапазонов сегмента;
- конвертация `ELF p_flags -> PageFlags`;
- `Pager.Map` для каждой страницы сегмента;
- copy `filesz` в замапленную память;
- zero-fill хвоста `memsz - filesz`;
- rollback unmap для уже замапленных страниц сегмента при ошибке.

### 2. Добавлены типы результата загрузки

Обновлен файл:
- `OS_0.1/src/Kernel/Elf/ElfTypes.cs`

Добавлено:
- `ElfLoadError`;
- `ElfLoadedImage` (entrypoint, число сегментов, число страниц, диапазон VA).

### 3. Расширена диагностика ELF

Обновлен файл:
- `OS_0.1/src/Kernel/Elf/ElfDiagnostics.cs`

Добавлено:
- `WriteLoadError(...)`;
- `DumpLoadedImage(...)`;
- `WriteProgramFlags(...)` открыт для повторного использования в loader-логах.

### 4. Интеграция в текущий ELF pipeline

Обновлен файл:
- `OS_0.1/src/Kernel/Elf/ElfValidation.cs`

Теперь после `parse + validate + summary` выполняется:
- `ElfLoader.TryLoad(...)`;
- вывод ошибок loader через `ElfDiagnostics.WriteLoadError(...)`;
- вывод итога загрузки (`segments/pages/range/entry`).

## Проверка

Проверено:
1. `.\run_build.ps1 -NoRun` — успешно.
2. `.\run_build.ps1` — успешно.

Ключевые строки COM1:
- `[info] elf load start`
- `[info] map seg 0: vaddr=0x0000000000400000..0x0000000000400080 pages=1 flags=R|X`
- `[info] copy seg 0: filesz=128`
- `[info] map seg 1: vaddr=0x0000000000401000..0x0000000000401100 pages=1 flags=R|W`
- `[info] copy seg 1: filesz=64`
- `[info] zero seg 1 tail: 192`
- `[info] elf image loaded`
- `[info] elf entry ready = 0x0000000000400010`

## Итог

Шаг 10 закрыт:
- ELF теперь не только парсится, но и реально загружается в память через `Pager`;
- получен рабочий scaffold для следующего рубежа: process image + user stack + jump в entrypoint.
