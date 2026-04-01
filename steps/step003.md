# step003

Дата: 2026-04-01

## Цель шага

Реализовать память как системный контракт, а не как UEFI-деталь:
- получить memory map в `Boot`;
- конвертировать UEFI descriptors в внутренние типы;
- передать карту памяти в ядро через `BootInfo`;
- добавить ранний физический page allocator;
- убрать warning `memory map not implemented` и подтвердить работу в логе.

## Что и как сделано

### 1. Введены внутренние типы памяти ядра

Добавлены:
- `src/Kernel/MemoryRegion.cs`
- `src/Kernel/MemoryMapInfo.cs`
- `src/Kernel/MemoryDiagnostics.cs`
- `src/Kernel/PhysicalMemory.cs`

Новый контракт:
- `MemoryRegionType` (`Usable`, `Reserved`, `Acpi`, `Mmio`, `BootServices`, `RuntimeServices`, `Loader`, `Unknown`);
- `MemoryRegion` (`PhysicalStart`, `PageCount`, `Type`);
- `MemoryMapInfo` (`MemoryRegion* Regions`, `uint RegionCount`).

Смысл:
- ядро и HAL теперь работают только с внутренней моделью памяти;
- `EFI_MEMORY_DESCRIPTOR` не выходит за пределы `Boot`.

### 2. Расширен boot-контракт

Изменен:
- `src/Boot/BootInfo.cs`

Добавлено поле:
- `MemoryMapInfo MemoryMap`.

Сохранены/использованы флаги:
- `MemoryMapAvailable` (0/1),
- capability `PlatformCapabilities.MemoryMap`.

Итог:
- карта памяти передается в систему как часть `BootInfo`, а не как сырой UEFI-указатель.

### 3. Типизирован `EFI_BOOT_SERVICES` и memory descriptor

Изменен:
- `src/Boot/UefiTypes.cs`

Добавлены:
- `EFI_MEMORY_TYPE`;
- `EFI_MEMORY_DESCRIPTOR`;
- `EFI_BOOT_SERVICES` с нужными полями до `GetMemoryMap` и `AllocatePool`;
- `EFI_SYSTEM_TABLE.BootServices` сменен с `void*` на `EFI_BOOT_SERVICES*`.

Это позволило вызывать `GetMemoryMap` и `AllocatePool` напрямую и безопасно по ABI.

### 4. Реализован `UefiMemoryMapBuilder`

Добавлен:
- `src/Boot/UefiMemoryMapBuilder.cs`

Логика:
1. Вызов `GetMemoryMap` для получения требуемого размера и размера дескриптора.
2. `AllocatePool(EfiLoaderData, ...)` под буфер дескрипторов.
3. Повторный `GetMemoryMap` в выделенный буфер.
4. Конвертация каждого `EFI_MEMORY_DESCRIPTOR` в `MemoryRegion`.
5. Выделение отдельного буфера под итоговый массив `MemoryRegion`.
6. Возврат `MemoryMapInfo` (`pointer + count`) в `BootInfo`.

Карта памяти остается в контролируемом внутреннем буфере (через pool), а UEFI-формат изолирован в `Boot`.

### 5. Интеграция в boot pipeline

Изменен:
- `src/Boot/UefiBootInfoBuilder.cs`

Что сделано:
- при сборке `BootInfo` вызывается `UefiMemoryMapBuilder.TryBuild(...)`;
- при успехе:
  - `MemoryMapAvailable = 1`,
  - выставляется capability `MemoryMap`,
  - в `BootInfo.MemoryMap` кладется внутренний формат карты.

Pipeline остаётся:
- `EfiMain -> Boot.Entry -> UefiBootInfoBuilder.Build -> Platform.Init -> KernelMain.Start`.

### 6. Реализован ранний физический аллокатор страниц

Добавлен:
- `src/Kernel/PhysicalMemory.cs`

Реализация:
- `Init(MemoryMapInfo)` принимает карту памяти и выбирает usable-регионы;
- `AllocPage()` и `AllocPages(uint)` выдают физические страницы по 4 KiB;
- аллокатор bump-only (без free), последовательно проходит usable-регионы;
- добавлен нижний порог `0x00100000`, чтобы не выдавать низкую область и страницу `0`.

Это intentional early allocator: простой и предсказуемый фундамент для следующих шагов (paging/heap и т.д.).

### 7. Добавлена диагностика памяти в ядре

Изменены:
- `src/Kernel/Kernel.cs`
- `src/Hal/Console.cs`

Что добавлено:
- вывод summary:
  - `memory regions: <N>`
  - `usable pages: <M>`
- инициализация `PhysicalMemory`;
- тестовые `AllocPage()` (3 раза) с выводом в hex;
- при отсутствии capability `MemoryMap` сохранился fallback warning.

В `Console` добавлены сервисные методы:
- `WriteUInt(uint)`,
- `WriteULong(ulong)`,
- `WriteHex(ulong, int minDigits)`.

### 8. Стабилизация линковки (`memcpy`)

Изменен:
- `src/Boot/MinimalRuntime.cs`

Причина:
- после расширения `BootInfo` AOT начал генерировать вызовы `memcpy` на копиях структуры.

Решение:
- добавлены runtime-экспорты:
  - `memcpy`
  - `memmove`

Это устранило `LNK2001 memcpy` и вернуло стабильную линковку.

## Проверка результата

Проверено:
1. `.\run_build.ps1 -NoRun`:
   - publish проходит;
   - `BOOTX64.EFI` формируется.

2. `.\run_build.ps1`:
   - QEMU запускается;
   - COM1 выводит:
     - `[info] caps: TextOutput Shutdown MemoryMap`
     - `[info] memory regions: 101`
     - `[info] usable pages: 52842`
     - `[info] early allocator ready`
     - `[info] alloc page: 0x00100000`
     - `[info] alloc page: 0x00101000`
     - `[info] alloc page: 0x00102000`
   - затем `demo` отрабатывает и VM завершает работу через shutdown.

## Что не делалось намеренно

На этом шаге специально не добавлялись:
- heap allocator;
- virtual memory/paging;
- scheduler;
- filesystem;
- graphics;
- runtime-level расширения.

Шаг 3 закрывает только фундамент:
- узнать RAM через boot contract;
- и уметь брать из неё физические страницы.

## Итог шага

Система перешла от предупреждения `memory map not implemented` к реальному memory pipeline:
- UEFI memory map изолирован в `Boot`;
- ядро получает внутренний `MemoryMapInfo`;
- capability `MemoryMap` стал рабочим;
- ранний физический аллокатор выдает реальные страницы;
- проект готов к следующему шагу (kernel heap/paging/graphics) без ломки архитектурных границ.
