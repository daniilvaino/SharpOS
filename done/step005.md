# step005

Дата: 2026-04-01

## Цель шага

Построить первый внутренний слой утилит и динамической памяти ядра поверх уже реализованных:
- `MemoryMapInfo`;
- `PhysicalMemory`;
- раннего page allocator.

Принцип шага:

**Borrow source ideas, re-implement under current contracts.**

То есть взяты идеи из старого SharpOS (bit ops, binary helpers, free-list heap, diagnostics direction), но реализация написана заново под текущую архитектуру `Boot -> Hal -> Kernel -> TestApp`.

## Что и как сделано

### 1. Добавлен `Kernel.Util`

Созданы файлы:
- `src/Kernel/Util/BitOps.cs`
- `src/Kernel/Util/Memory.cs`
- `src/Kernel/Util/Binary.cs`

Реализовано:

`BitOps`:
- `BitCount(uint)`
- `BitCount(ulong)`
- `NextPowerOf2(uint)`
- `AlignUp(uint, uint)`
- `AlignUp(ulong, ulong)`
- `IsPowerOf2(uint)`

`Memory`:
- `MemSet`
- `MemCopy`
- `MemMove`
- `Zero`

`Binary`:
- `Read7BitInt(byte*, out int, out uint)`
- `ReadPrefixedBytes(...)`

Смысл:
- unsafe-операции и битовые примитивы локализованы в одном месте;
- будущие heap/paging/парсинг подсистемы получают общий util-layer;
- код не зависит от внешних CRT memory helpers.

### 2. Добавлен `KernelHeap` с free-list + split/coalesce

Созданы файлы:
- `src/Kernel/Memory/HeapBlock.cs`
- `src/Kernel/Memory/KernelHeap.cs`
- `src/Kernel/Memory/HeapDiagnostics.cs`

Реализована модель блока:
- `HeapBlock { Size, IsFree, Next, Prev }`
- двусвязный список блоков.

Реализованы операции:
- `KernelHeap.Init()` — инициализация heap с начальным region (4 страницы);
- `KernelHeap.Alloc(uint)` — first-fit поиск, выравнивание на 16 байт, split блока;
- `KernelHeap.Free(void*)` — mark free + coalesce с соседями;
- автоматический grow при нехватке места через `PhysicalMemory.AllocPages(...)`.

Параметры текущей реализации:
- alignment: 16 байт;
- старт: 4 страницы (`16 KiB`);
- grow по умолчанию: 4 страницы;
- policy поиска: `first-fit`.

Важная граница:
- heap ничего не знает про UEFI;
- heap знает только про `PhysicalMemory`.

### 3. Добавлена диагностика heap

В `HeapDiagnostics` реализовано:
- `DumpSummary()`
- `DumpBlocks()`

В summary выводятся:
- `heap pages`
- `heap blocks`
- `heap used bytes`
- `heap free bytes`
- операции `alloc/free`
- счётчики `grow/coalesce/fail`

Дополнительно:
- в `KernelHeap` добавлены trace-сообщения на coalesce события;
- добавлены предупреждения на некорректные `Free` (unknown pointer / double free).

### 4. Интеграция в `Kernel.Start`

Изменен:
- `src/Kernel/Kernel.cs`

После инициализации `PhysicalMemory` теперь выполняется:
1. `KernelHeap.Init()`;
2. лог `heap init ok`;
3. `HeapDiagnostics.DumpSummary()`;
4. smoke-test аллокаций:
   - `Alloc(16)`
   - `Alloc(64)`
   - `Alloc(256)`
   - `Free(16)`
   - `Alloc(8)`
5. повторный summary и dump блоков.

Если heap не инициализируется:
- вызывается `Panic.Fail("kernel heap init failed")`.

### 5. Интеграция в `DemoApp`

Изменен:
- `src/TestApp/DemoApp.cs`

Добавлен `demo heap test`:
- выделение блоков (`16`, `64`, затем `8`);
- запись/чтение данных в выделенный буфер (`64` байта);
- вычисление checksum;
- освобождение памяти;
- лог `demo heap test ok`.

Таким образом `DemoApp` теперь реально использует heap, а не только вычисляет `fib`.

## Проверка результата

Проверено:
1. `.\run_build.ps1 -NoRun` — сборка успешна.
2. `.\run_build.ps1` — загрузка в QEMU успешна, COM1 выводит:

- memory-step:
  - `[info] memory regions: 101`
  - `[info] usable pages: 52842`
  - `[info] early allocator ready`
- heap init/summary:
  - `[info] heap grow pages: 4`
  - `[info] heap init ok`
  - `[info] heap pages: 4`
- kernel smoke alloc:
  - `[info] heap alloc 16 -> 0x...`
  - `[info] heap alloc 64 -> 0x...`
  - `[info] heap alloc 256 -> 0x...`
  - `[info] heap free -> 0x...`
  - `[info] heap alloc 8 -> 0x...`
- block dump:
  - `[info] heap block ... state=used/free`
- demo heap test:
  - `[info] demo heap test start`
  - `[info] alloc 16 -> 0x...`
  - `[info] alloc 64 -> 0x...`
  - `[info] heap checksum: 2080`
  - `[info] alloc 8 -> 0x...`
  - `[trace] heap coalesce`
  - `[info] demo heap test ok`

Система завершает работу штатно.

## Что НЕ делалось намеренно

На шаге 5 специально не добавлялись:
- paging/VM;
- userspace allocator;
- managed allocator/GC;
- VFS/graphics;
- serial subsystem expansion как отдельный канал HAL.

Причина:
- фокус шага — дать ядру первый рабочий динамический allocator + util слой.

## Итог шага

Система перешла из состояния:

**«умеем выдавать физические страницы»**

в состояние:

**«ядро умеет выделять и освобождать динамическую память для своих структур»**.

Это формирует рабочий фундамент для следующего шага:
- `paging/VM`;
- или `kernel heap` расширение (policy/fragmentation improvements);
- или util-driven подсистемы (VFS/metadata parsing).
