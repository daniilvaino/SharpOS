# step006

Дата: 2026-04-01

## Цель шага

Добавить фундамент виртуальной памяти без включения CR3/аппаратного переключения paging:
- ввести собственный pager-контракт;
- ввести page flags;
- реализовать x64 4-level page table backend;
- добавить `map / query / unmap`;
- добавить paging diagnostics.

Принцип шага:

**Borrow API ideas, keep current architecture.**

Слой `Boot/Hal/Kernel` не ломается, UEFI не просачивается в paging-код.

## Что сделано

### 1. Новый слой `Kernel/Paging`

Добавлены файлы:
- `src/Kernel/Paging/PageFlags.cs`
- `src/Kernel/Paging/PagingRequirements.cs`
- `src/Kernel/Paging/Pager.cs`
- `src/Kernel/Paging/X64PageTable.cs`
- `src/Kernel/Paging/PagingDiagnostics.cs`

### 2. Введен paging contract

Добавлено:
- `PageFlags` (`Present`, `Writable`, `User`, `WriteThrough`, `CacheDisable`, `Global`, `NoExecute`);
- `PagingRequirements` (`PageSize`, `DirectMapBase`, `InitialPageTablePages`);
- фасад `Pager`:
  - `Init(...)`
  - `Map(...)`
  - `Unmap(...)`
  - `TryQuery(...)`
  - `GetSummary(...)`

`Pager` валидирует выравнивание адресов и проксирует вызовы в backend.

### 3. Реализован x86-64 4-level backend

В `X64PageTable` реализовано:
- PML4 / PDPT / PD / PT walk;
- создание промежуточных таблиц по требованию;
- хранение root table;
- software-managed mapping logic;
- счётчики вызовов/ошибок.

Источник памяти для page tables:
- `PhysicalMemory.AllocPage()`.

Поддержаны операции:
- `Map(virt, phys, flags)` — только 4 KiB страницы, без remap уже занятого leaf;
- `TryQuery(virt)` — возвращает физический адрес + флаги;
- `Unmap(virt)` — снимает leaf mapping.

### 4. Добавлена paging диагностика

В `PagingDiagnostics` добавлено:
- `DumpSummary()`;
- `DumpMapping(virt)`.

В summary выводятся:
- размер страницы;
- root table address;
- число table pages и spare pages;
- число mapped pages;
- счётчики `map/query/unmap`;
- счётчики ошибок `map/unmap`.

### 5. Интеграция в `Kernel.Start`

Изменен:
- `src/Kernel/Kernel.cs`

После memory+heap шага теперь выполняется:
1. `Pager.Init(...)`
2. лог `pager init ok`
3. pager summary dump
4. выделение 2 физических страниц
5. test mappings:
   - `0xFFFF800000001000 -> physA`
   - `0xFFFF800000002000 -> physB`
6. query обеих страниц
7. `Unmap` первой
8. проверка `not mapped`
9. `PagingDiagnostics.DumpMapping(...)` + итоговый summary.

## Проверка

Проверено:
1. `.\run_build.ps1 -NoRun` — сборка успешна.
2. `.\run_build.ps1` — QEMU COM1 выводит ожидаемые строки:
- `pager init ok`
- `map ... flags=Present|Writable`
- `query ...`
- `unmap ... ok`
- `query ... -> not mapped`
- `paging query ...`

## Что не делалось намеренно

На шаге 6 специально не добавлялись:
- включение paging на CPU/CR3 switch;
- userspace VM;
- large pages;
- TLB invalidation path;
- копирование старой SharpOS архитектуры.

## Итог шага

Система перешла от:

**«есть physical pages и kernel heap»**

к:

**«есть собственный paging contract и рабочая модель map/query/unmap для x64 page tables»**.

Это готовый фундамент для следующих шагов:
- реальное включение VM (при необходимости отдельным шагом);
- page attributes/policy расширение;
- memory safety/diagnostics усиление.
