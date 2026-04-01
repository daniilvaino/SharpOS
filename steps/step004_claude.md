# step004.md — Археологический анализ SharpOS

**Дата:** 2026-04-01

## Цель шага

Провести целевой анализ старого проекта SharpOS как "археологического карьера" для идей и локальных реализаций, которые можно адаптировать в текущую систему без импорта чужой архитектуры целиком.

**Главный принцип: Borrow algorithms, not architecture.**

---

## A. Краткая карта репозитория

| Каталог | Назначение | Ключевые файлы |
|---------|-----------|----------------|
| `ADC/` | Architecture-Dependent Code (абстракции над железом) | Pager.cs, MemoryManager.cs, TextMode.cs, Barrier.cs, SpinLock.cs |
| `ADC/X86/` | X86-специфичные реализации | Pager.cs (392 строк), MemoryUtil.cs, IO.cs, IDT.cs, Processor.cs |
| `Foundation/` | Низкоуровневые утилиты | StringBuilder.cs, ByteString.cs, BinaryTool.cs, Convert.cs |
| `Korlib/` | Mini-runtime library | String.cs, Collections (ArrayList, List), System exceptions |
| `Korlib/Runtime/` | Runtime metadata | Runtime.cs (1268), TypeMetadata.cs, VTable.cs |
| `Memory/` | Физическая память | PageAllocator.cs (475), MemoryBlock.cs (175) |
| `DeviceSystem/` | Hardware abstractions | Drivers (Floppy, IDE, PCI, Serial) |
| `FileSystem/` | FAT implementation | FAT.cs, MasterBootBlock.cs, PartitionDevice.cs |
| `Shell/` | Command interpreter | Prompter.cs, built-in commands |
| `HAL/` | Hardware Abstraction Layer | IOPort.cs, IRQHandler.cs, DMAChannel.cs |

**Общий объём:** ~33,600 строк C# кода в Kernel/Core.

---

## B. Таблица находок

### Memory Management

| Путь | Что это | Зачем пригодится | Риск |
|------|---------|-----------------|------|
| `PageAllocator.cs` | Free page stack + reserved pages tracking. Инициализация из multiboot, выделение страниц, резервирование блоков | Готовая идея свободных физических страниц; адаптируется под UEFI MemoryMap | low |
| `ADC/MemoryManager.cs` | Heap allocator на Red-Black tree + linked-list; alloc/free/realloc с консолидацией соседних блоков (~1067 строк) | Идея RB-tree хорошая; реализация требует переделки под современный стиль | medium |
| `ADC/X86/Pager.cs` | 2-level page directory + page tables (4K/4M). MapPage, SetPageAttributes, GetPageAttributes. | Шаблон для page tables; нужна адаптация под 4-level для x86-64/UEFI | low |
| `PageAttributes.cs` | Enum флагов страниц: ReadWrite, User, Present | Расширить для UEFI (Present/Writable/Executable/PAT/NX) | low |
| `MemoryBlock.cs` | Unsafe struct для работы с блоком памяти (Fill, Copy, Move, Offset) | Полезна для data bashing | low |

### Low-level Utilities

| Путь | Что это | Зачем пригодится | Риск |
|------|---------|-----------------|------|
| `Foundation/ByteString.cs` | C-string helpers (Length, Compare, Concat, Copy для `byte*`) | Нужны везде в ранних стадиях | low |
| `Foundation/StringBuilder.cs` | Unsafe struct-based буфер, AppendChar/Append/AppendNumber с hex | Шаблон для диагностического вывода | medium |
| `ADC/MemoryUtil.cs` | Interface: BitCount, NextPowerOf2, MemSet, MemCopy | Хорош как ADC-интерфейс | low |
| `ADC/X86/MemoryUtil.cs` | Реализации MemSet, MemCopy, BitCount | Шаблон реализации | low |
| `ADC/Barrier.cs` | Simple critical region lock (ADC stub) | Архитектура правильная | low |
| `ADC/SpinLock.cs` | Spin lock через Interlocked.Exchange | Полезна для concurrency | low |

### Diagnostics / Panic / Console

| Путь | Что это | Зачем пригодится | Риск |
|------|---------|-----------------|------|
| `Diagnostics.cs` | Panic(), Assert(), AssertZero(), Error(), Warning(), FormatDump() (hex dump с ASCII) | Отличный шаблон для kernel debug | low |
| `ADC/Debug.cs` | Debug output через serial COM1 (ADC stub) | Базовая идея для early serial logging | low |
| `ADC/TextMode.cs` | Консольная абстракция (Write, WriteLine, SetAttributes, cursor) | Основа для UEFI-консольного вывода | low |

### Kernel Boot Sequence

| Путь | Что это | Зачем пригодится | Риск |
|------|---------|-----------------|------|
| `EntryModule.cs` | Boot sequence: Architecture → TextMode → Multiboot → PageAllocator → MemoryManager → Runtime | Хороший пример bootflow | low |
| `Multiboot.cs` | Структуры Multiboot spec (Header, MemoryMap, Module, InfoTable) | Структуры, если нужен Multiboot; для UEFI — только как идея разметки | low |

### Paging / VM

| Путь | Что это | Зачем пригодится | Риск |
|------|---------|-----------------|------|
| `ADC/X86/Pager.cs` | PageDirectory[] + PageTables[], MapPage(), SetPageAttributes(). 2-level (PDE=4M, PTE=4K) | **Лучшая находка**: готовый шаблон page tables. Надо расширить до 4-level для x86-64 | low |
| `PagingMemoryRequirements.cs` | Конфиг: AtomicPages, Start pointer | Минималистичен, но идея правильная | low |

---

## C. Рекомендации по заимствованию

### 1. Можно брать почти сразу

- `PageAllocator.cs` — архитектура free page stack + reserved blocks; адаптировать под UEFI MemoryMap
- `ADC/X86/Pager.cs` — шаблон 2-level page tables; расширить до 4-level для x86-64
- `PageAttributes.cs` — enum флагов страниц
- `Foundation/ByteString.cs` — C-string utilities
- `Diagnostics.cs` — panic/assert/error/hex dump framework
- `ADC/Barrier.cs` + `SpinLock.cs` — sync primitives
- `MemoryBlock.cs` — memory range operations

### 2. Можно брать только как идею

- `ADC/MemoryManager.cs` — идея RB-tree heap хороша; реализация нуждается в переработке (или переписать buddy allocator)
- `Foundation/StringBuilder.cs` — unsafe struct-буфер для диагностики; синтаксис устарел
- `ADC/ExceptionHandling.cs` — архитектура правильная, но реализация — стабы
- `ADC/TextMode.cs` — абстракция хороша; VGA-реализация выбросить

### 3. Не брать

- `Korlib/Runtime/Runtime.cs` — специфично для старого SharpOS AOT; твой NativeAOT совсем другой
- `FileSystem/` — полностью устарело для UEFI-окружения
- `DeviceSystem/` — старомодная архитектура (Floppy, legacy IDE)
- `ADC/X86/IDT.cs` (2275 строк) — x86 32-bit, нужна переделка для UEFI x86-64
- `Shell/` — слишком high-level для kernel foundation

---

## D. Рекомендация по следующему шагу

### Приоритеты работ

**Шаг 5 — Kernel Heap (первоочередно)**

Без heap нельзя выделять динамическую память даже для kernel structures. Адаптировать PageAllocator под BootInfo/MemoryMapInfo, реализовать простой heap allocator (buddy или slab). Это блокирует всё остальное.

**Шаг 6 — Paging & VM (после heap)**

Адаптировать Pager.cs под UEFI/x86-64 (4-level page tables вместо 2-level). Нужно для isolation и безопасного memory mapping.

**Параллельно с heap:**

- `Diagnostics.cs` — panic/assert/hex dump (нужно сразу чтобы видеть баги)
- Utilities layer: ByteString, StringBuilder, bit ops, sync primitives

**Позже:**

- Kernel console/output (TextMode abstraction над UEFI ConOut protocol)
- FileSystem, DeviceSystem, Shell — не трогать, это пользовательский space

---

## Итог

SharpOS — ценный артефакт, демонстрирующий:
- правильную архитектуру physical paging (PageAllocator + Pager)
- хороший ADC pattern (interface над железом + конкретные реализации)
- практичные диагностические инструменты (Diagnostics.cs)
- clean boundary между kernel subsystems

Ограничения: 32-bit x86, 2009 год, старый C# syntax, Multiboot вместо UEFI, без ACPI.

**Рекомендованный подход:** взять базовые идеи (PageAllocator, Pager, Diagnostics), переработать под UEFI/NativeAOT. Не копировать код 1-в-1 — использовать как reference.

---

**Следующий шаг:** step005 — Kernel Heap (физический аллокатор страниц + базовый heap, основанные на идеях из SharpOS PageAllocator/MemoryManager).
