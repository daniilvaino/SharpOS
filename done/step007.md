# step007

Дата: 2026-04-01

## Цель

Укрепить существующий paging-слой без добавления нового большого subsystem:
- расширить валидацию API;
- усилить диагностику page-walk;
- добавить range-операции маппинга.

## Что реализовано

### 1. Paging validation как отдельный модуль

Добавлен файл:
- `OS_0.1/src/Kernel/Paging/PagingValidation.cs`

Валидация запускается из `Kernel.Start()` после `Pager.Init()` и проверяет:
- offset-query в `TryQuery`;
- duplicate map fail-path;
- duplicate unmap fail-path;
- соседние страницы без лишнего роста таблиц;
- round-trip для разных наборов flags;
- `MapRange`/`UnmapRange`.

При нарушении инварианта вызывается `Panic.Fail(...)`.

### 2. Добавлены range API

Обновлен файл:
- `OS_0.1/src/Kernel/Paging/Pager.cs`

Добавлено:
- `MapRange(ulong vaStart, ulong paStart, uint pageCount, PageFlags flags)`
- `UnmapRange(ulong vaStart, uint pageCount)`

Особенности:
- в `MapRange` есть rollback уже замапленных страниц при частичном фейле;
- в `UnmapRange` операция возвращает `false`, если встретила unmapped страницу;
- добавлена защита от overflow при инкременте page address.

### 3. Расширена диагностика page-walk

Добавлены/обновлены:
- `OS_0.1/src/Kernel/Paging/PageWalkInfo.cs`
- `OS_0.1/src/Kernel/Paging/X64PageTable.cs`
- `OS_0.1/src/Kernel/Paging/PagingDiagnostics.cs`

Теперь `DumpMapping(...)` выводит:
- индексы `pml4/pdpt/pd/pt`;
- raw entry для `pml4e/pdpte/pde/pte`;
- признак `present`;
- физический адрес entry;
- декодированные entry-flags (`P/W/U/WT/CD/A/D/PS/G/NX`);
- итоговый `paging query ... -> phys/flags` или `not mapped`.

### 4. Интеграция в kernel pipeline

Обновлен:
- `OS_0.1/src/Kernel/Kernel.cs`

Изменения:
- `RunPagerSmokeTest()` заменен на `RunPagerValidation()`;
- после валидации выводится итоговый `PagingDiagnostics.DumpSummary()`.

## Проверка

Проверено командами:
1. `.\run_build.ps1 -NoRun` — успешно.
2. `.\run_build.ps1` — успешно, в COM1 зафиксировано:
- `pager validation start`
- `pager check: offset query pass`
- `pager check: duplicate map/unmap pass`
- `pager check: neighbor pages reuse tables`
- `pager check: multi-flag round trip pass`
- `pager check: map/unmap range pass`
- расширенные строки `paging walk ...`, `paging pml4e ...`, `paging pdpte ...`, `paging pde ...`, `paging pte ...`
- `pager validation done`

## Итог

Paging-контракт из шага 6 усилен:
- API проверяется на ключевых edge-cases;
- диагностика стала пригодной для практического дебага walk/flags;
- добавлены batch-операции `MapRange/UnmapRange` для следующего слоя VM-работ.
