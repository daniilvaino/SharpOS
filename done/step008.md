# step008

Дата: 2026-04-01

## Цель

Добавить в текущую систему следующий полезный слой для будущего loader/VM:
- `MemoryBlock`-фасад над сырой памятью;
- `BinaryReaderLite` для безопасного бинарного чтения;
- richer diagnostics (`KernelAssert`, `HexDump`);
- шлифовка `PageFlags` и `PagingRequirements`.

Шаг выполнен без переноса архитектуры старого SharpOS: взяты только идеи и shape API.

## Что реализовано

### 1. `MemoryBlock` в util-слое

Добавлен:
- `OS_0.1/src/Kernel/Util/MemoryBlock.cs`

Реализовано:
- работа с указателем + длиной;
- `TryRead/Write` для `byte`, `ushort`, `uint` (LE);
- `Clear`, `Fill`, `TryCopyTo`, `TryCopyFrom`, `TryMoveFrom`;
- `TryOffset` для суб-блоков.

Назначение:
- локализовать низкоуровневую работу с буферами в одном API;
- подготовить базу под ELF/binary parsing.

### 2. `BinaryReaderLite`

Добавлен:
- `OS_0.1/src/Kernel/Util/BinaryReaderLite.cs`

Реализовано:
- позиционное чтение из буфера (`Position`, `Remaining`);
- `TryReadByte`, `TryReadUInt16`, `TryReadUInt32`;
- `TryRead7BitInt`;
- `TryReadPrefixedBlock` (length-prefixed payload как `MemoryBlock`);
- `TrySkip`, `Reset`.

Назначение:
- безопасный бинарный parsing primitive без зависимости на runtime I/O.

### 3. Richer diagnostics

Добавлены:
- `OS_0.1/src/Kernel/Diagnostics/KernelAssert.cs`
- `OS_0.1/src/Kernel/Diagnostics/HexDump.cs`

`KernelAssert`:
- `True`, `False`, `NotNull`, `Equal(uint/ulong/int)`;
- при mismatch печатает expected/actual и завершает через `Panic.Fail`.

`HexDump`:
- dump памяти построчно (адрес, hex, ascii);
- используется в pager validation для наглядной проверки util-буфера.

### 4. Полировка paging contract

Обновлены:
- `OS_0.1/src/Kernel/Paging/PageFlags.cs`
- `OS_0.1/src/Kernel/Paging/PagingRequirements.cs`
- `OS_0.1/src/Kernel/Paging/Pager.cs`

Сделано:
- добавлен `PageFlagOps` (`SupportedMask`, `IsSupported`, `NormalizeForMap`);
- `PagingRequirements.Normalize()` и `IsValid()`:
  - page size validation;
  - non-zero initial table pages;
  - alignment check для `DirectMapBase`;
- `Pager.Init` теперь использует `Normalize/IsValid`;
- `Pager.Map` теперь валидирует и нормализует флаги централизованно.

### 5. Интеграция в реальный validation pipeline

Обновлен:
- `OS_0.1/src/Kernel/Paging/PagingValidation.cs`

Что добавлено в рантайм-проверки:
- `ValidateUtilityLayer()`:
  - запись/чтение через `MemoryBlock`;
  - чтение prefixed payload через `BinaryReaderLite`;
  - `HexDump` вывода test-буфера;
- существующие paging-check'и переведены на `KernelAssert`.

## Проверка

Проверено:
1. `.\run_build.ps1 -NoRun` — успешно.
2. `.\run_build.ps1` — успешно.

Ключевые строки в COM1:
- `pager check: util memory block/binary reader pass`
- `hexdump util sample ...`
- `pager check: offset query pass`
- `pager check: duplicate map/unmap pass`
- `pager check: neighbor pages reuse tables`
- `pager check: multi-flag round trip pass`
- `pager check: map/unmap range pass`
- `pager validation done`

## Итог

Система получила первый полноценный **loader-supporting util + diagnostics слой**:
- удобная модель работы с сырыми буферами;
- минимальный бинарный reader;
- assert/hexdump для отладки;
- более строгий paging contract (`flags + requirements`).

Это готовая база для следующего шага: ранний ELF parser / memory-range driven loader scaffolding.
