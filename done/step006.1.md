# step006.1

Дата: 2026-04-01

## Цель

Не ставить новые задачи, а проверить качество реализации `Pager` из `step006` по 6 edge-case пунктам и зафиксировать фактический результат.

## Что было изменено перед проверкой

Чтобы проверки были валидны, внесены точечные правки:

1. `TryQuery` теперь поддерживает VA с offset:
- файл: `OS_0.1/src/Kernel/Paging/Pager.cs`
- изменение: убрана жесткая проверка выравнивания для query, в ответ добавляется `pageOffset`.

2. `DumpMapping` теперь показывает page-walk индексы:
- файл: `OS_0.1/src/Kernel/Paging/PagingDiagnostics.cs`
- добавлен вывод:
  - `pml4`
  - `pdpt`
  - `pd`
  - `pt`

3. Расширен `RunPagerSmokeTest` под все edge-case проверки:
- файл: `OS_0.1/src/Kernel/Kernel.cs`
- добавлены проверки с `Panic` при нарушении инвариантов и явные `pager check: ...` логи.

## Результаты 6 проверок

### 1. Query с offset

Проверка:
- `map 0xFFFF800000001000 -> 0x0010B000`
- `query 0xFFFF800000001234`

Факт:
- лог: `query 0xFFFF800000001234 -> 0x0010B234`

Статус:
- `PASS`

### 2. Повторный Unmap

Проверка:
- первый `Unmap` успешен;
- второй `Unmap` того же VA должен вернуть fail.

Факт:
- лог: `pager check: second unmap clean fail`
- summary: `pager failures map/unmap: 1/1` (ожидаемо есть один fail unmap).

Статус:
- `PASS`

### 3. Повторный Map того же VA

Проверка:
- второй `Map` на уже занятый VA должен отказать.

Факт:
- лог: `pager check: duplicate map clean fail`
- summary: `pager failures map/unmap: 1/1` (ожидаемо есть один fail map).

Статус:
- `PASS`

### 4. Round-trip флагов

Проверка:
- `Map(..., flagsA)`
- `TryQuery(...)` должен вернуть те же флаги (с учетом `Present`).

Факт:
- лог: `pager check: flags round-trip ok`
- пример: `flags=Present|Writable|Global|NoExecute`.

Статус:
- `PASS`

### 5. Отсутствие лишнего создания таблиц на соседних VA

Проверка:
- map двух соседних VA в одном PT;
- второй map не должен расширять page-table структуру.

Факт:
- лог: `pager check: adjacent map reused tables`
- проверка выполняется в коде через сравнение `TablePages` и `SpareTablePages` между первым и вторым map.

Статус:
- `PASS`

### 6. Dump с walk

Проверка:
- `DumpMapping(virt)` должен печатать индексы walk + leaf query.

Факт:
- лог:
  - `paging walk 0xFFFF800000001234: pml4=256 pdpt=0 pd=0 pt=1`
  - `paging walk 0xFFFF800000002000: pml4=256 pdpt=0 pd=0 pt=2`

Статус:
- `PASS`

## Проверка запуска

Команды:
1. `.\run_build.ps1 -NoRun` — успешно.
2. `.\run_build.ps1` — успешно, QEMU COM1 содержит все ожидаемые `pager check:` строки.

## Слабые места (честная фиксация)

1. `DirectMapBase` пока задел:
- хранится и проходит через `PagingRequirements`,
- но реальный direct-map region еще не строится.

2. Reclaim пустых page-table страниц после `Unmap` пока не реализован:
- leaf снимается корректно,
- промежуточные пустые таблицы не освобождаются.

3. `PagingRequirements` частично рабочий:
- `PageSize` и `InitialPageTablePages` реально применяются и проверяются;
- `DirectMapBase` пока инфраструктурный параметр на будущее.

## Итог

`step006` доведен до проверяемого состояния по ключевым edge-cases API.
Новый `step006.1` зафиксирован как отчет о выполненных проверках и фактическом поведении системы.
