# step004

Дата: 2026-04-01

## Цель шага

Провести археологический анализ `old SharpOS` и выделить, что можно безопасно адаптировать в `OS_0.1`, не перетаскивая старую архитектуру целиком.

Ключевой принцип:

**Borrow algorithms, not architecture.**

## Контекст и ограничения

- Анализ выполнен по локальному репозиторию: `C:\work\OS\old SharpOS`.
- Локальный `git log` показывает последний коммит: `2009-01-26` (`ffb7d08`).
- В корне лежат `SharpOS-VS05.sln` и `SharpOS-VS08.sln`, плюс `SharpOS.build` на `nant` — это исторический стек.
- Большая часть кода под лицензией `GNU GPL v3` (с Classpath exception), что повышает риск прямого копирования в текущий проект.

Вывод по рамке:

- использовать как источник идей/алгоритмов;
- не импортировать код 1:1;
- не подтягивать build/runtime/boot модель SharpOS.

## A. Краткая карта репозитория

| Каталог | Роль |
|---|---|
| `AOT/` | Собственный AOT-компилятор, IR, x86 backend, атрибуты (`KernelMain`, `ADCStub`). |
| `Kernel/Core/` | Основной код ядра: `EntryModule`, память, диагностика, shell, VFS, drivers. |
| `Kernel/Core/ADC/` | Абстрактные платформенные stubs (BootControl, Pager, TextMode, Interlocked и т.д.). |
| `Kernel/Core/ADC/X86/` | x86-реализации ADC stubs (asm-heavy код). |
| `Kernel/Core/Foundation/` | Низкоуровневые utility-типы и парсинг/конвертация. |
| `Kernel/Core/Korlib/` | Собственный runtime/корлиб слой старого проекта. |
| `Kernel/Tests/` | Набор kernel/AOT тестов. |
| `Tools/` | Вспомогательные тулзы (диагностика, обновление образа и пр.). |
| `build/`, `SharpOS.build` | Сборочный сценарий через `nant` и исторические дистрибутивные сценарии. |

## B. Таблица находок

| Путь | Что это | Зачем может пригодиться | Риск переноса |
|---|---|---|---|
| `Kernel/Core/PageAllocator.cs` | Ранний page allocator + reserved ranges + low memory reserve (`<1MB`). | Полезная идея двухфазной модели: ранняя выдача страниц + явное резервирование критичных диапазонов. | `medium` |
| `Kernel/Core/PagingMemoryRequirements.cs` | Контракт требований к памяти для paging control data. | Хороший шаблон контракта между allocator и paging backend. | `low` |
| `Kernel/Core/ADC/Pager.cs` | Абстракция pager API через stubs (`Setup`, `MapPage`, `SetPageAttributes`). | Подход к платформенной границе: сначала общий API, потом backend-реализация. | `low` |
| `Kernel/Core/ADC/X86/Pager.cs` | Реализация paging для x86 (PDE/PTE, flags, map/set/get attr). | Полезная модель API и mapping-операций для будущего шага paging. | `high` |
| `Kernel/Core/ADC/X86/MemoryUtil.cs` | `MemSet/MemCopy`, `BitCount`, `NextPowerOf2`. | Алгоритмы `BitCount`/`NextPowerOf2` можно быстро воспроизвести в своем util-слое. | `low` |
| `Kernel/Core/Foundation/BinaryTool.cs` | `Read7BitInt` и `ReadPrefixedString`. | Нормальная компактная реализация для бинарного парсинга метаданных/протоколов. | `low` |
| `Kernel/Core/Foundation/Convert.cs` | Числовой парсинг/форматирование в буферы. | Можно взять идею API для безаллокационного форматирования чисел. | `medium` |
| `Kernel/Core/Diagnostics.cs` | `Panic/Assert/Warning/Error`, дамп памяти, stack trace через debug-канал. | Полезный шаблон развития `Panic + Log + dump` в один аварийный pipeline. | `medium` |
| `Kernel/Core/ADC/Debug.cs` | Абстракция debug output через `COM1/COM2`. | Идея dual-channel диагностики (экран + serial) полезна для OS_0.1. | `high` |
| `Kernel/Core/MemoryBlock.cs` | Typed wrapper над raw memory (Fill/Copy/GetUInt/SetUInt). | Идея легкого `MemoryBlock`-слоя поверх физических/виртуальных буферов. | `medium` |
| `Kernel/Core/Vfs/PathResolver.cs` | Итеративный resolver с ограничением глубины симлинков. | Полезная модель на будущее для VFS (не для текущего шага памяти). | `medium` |
| `Kernel/Core/EntryModule.cs` + `Kernel/Core/Multiboot.cs` | Старый boot pipeline через Multiboot и stage-based init. | Полезно только как историческая справка по порядку инициализации подсистем. | `high` |

## Диагностика качества (почему не копировать напрямую)

В коде есть маркеры незавершенности/дефектов, что подтверждает формат "источник идей, не upstream":

- `Kernel/Core/ADC/Debug.cs:134-137` — `WriteLine(int, bool)` вызывает себя рекурсивно.
- `Kernel/Core/ADC/Debug.cs:145-154` — рекурсия в `Write(uint)` / `WriteLine(uint)`.
- `Kernel/Core/ADC/SimpleEventDispatch.cs:53-55` — в `Add` нет увеличения `dispatchCount`.
- `Kernel/Core/PageAllocator.cs:128` — `rpStackSize = 1` (жесткая заглушка).
- `Kernel/Core/ADC/Pager.cs` и др. ADC stubs — множество `not implemented` заглушек.

## C. Рекомендации по заимствованию

### 1) Можно брать почти сразу

- Идею контракта `PagingMemoryRequirements` (`Kernel/Core/PagingMemoryRequirements.cs`).
- Модель `Pager` API как платформенной границы (`Kernel/Core/ADC/Pager.cs`), но с вашей сигнатурой и типами.
- Алгоритмы `BitCount` и `NextPowerOf2` (`Kernel/Core/ADC/X86/MemoryUtil.cs`) через чистую re-implementation.
- Алгоритм `Read7BitInt`/`ReadPrefixedString` (`Kernel/Core/Foundation/BinaryTool.cs`) через re-implementation.
- Правило резервирования низкой памяти `<1MB` из `PageAllocator` как policy для early allocator.
- Идею dual diagnostics: экран + serial (`Diagnostics` + `Debug`) через ваш `Log`/`Panic`.

### 2) Можно брать только как идею

- `PageAllocator` целиком: полезны принципы резервирования, но реализация слишком завязана на старый стек.
- `MemoryManager` структура (split/coalesce/free list + дерево): брать только идеи, писать заново.
- `MemoryBlock` API как удобный фасад для raw memory, но без x86/old runtime зависимостей.
- `PathResolver` и `DirectoryEntry` для будущего VFS как reference design.

### 3) Не брать

- Полный boot pipeline (`EntryModule` + `Multiboot`) и stage init.
- `ADC/X86/*` asm-код как есть.
- Build stack `SharpOS.build`/`nant` и VS05/VS08 решения.
- `Korlib`/runtime слой старого SharpOS.
- Прямое копирование файлов из-за архитектурной несовместимости и лицензионных рисков.

## Shortlist (3–7 вещей, реально адаптировать)

1. Ввести собственный контракт `PagingRequirements` по мотивам `PagingMemoryRequirements`.
2. Спроектировать `Kernel.Paging` API в стиле `MapPage/SetPageAttributes/GetPageAttributes` (без привязки к x86 внутри API).
3. Добавить `Kernel.Util.BitOps` (`BitCount`, `NextPowerOf2`).
4. Добавить `Kernel.Util.BinaryReaderLite` (`Read7BitInt`, prefixed string).
5. Расширить `Panic` до единого аварийного отчета: message + context + (опционально) hexdump.
6. Добавить `Hal.DebugSerial` как второй канал логирования рядом с текстовой консолью.

## D. Рекомендация по шагу 5

Рекомендуемый шаг 5:

**Kernel heap + util layer (без paging как обязательного этапа).**

Почему:

- После шага 3 уже есть `MemoryMap + PhysicalMemory`.
- Из анализа SharpOS наиболее практичные и безопасные находки сейчас — это именно util-слой и простая heap-модель (а не полный VM/paging перенос).
- Heap нужен почти всем следующим подсистемам (структуры ядра, VFS, драйверы, графика).

Минимальный scope шага 5:

- `Kernel/Util/BitOps.cs` (`BitCount`, `NextPowerOf2`).
- `Kernel/Util/Binary.cs` (`Read7BitInt`, prefixed read).
- `Kernel/Memory/KernelHeap.cs`:
  - `Init(PhysicalMemory)`
  - `Alloc(uint bytes)`
  - `Free(void* ptr)` (минимальный coalesce).
- Диагностический вывод heap-статистики через `Log`.

Следующий шаг после этого:

- шаг 6: paging/virtual memory с уже готовыми util и heap-инструментами.

## Итог шага

Анализ подтвердил:

- SharpOS полезен как **археологический источник локальных идей**;
- непригоден как архитектурный upstream для `OS_0.1`;
- разумная стратегия: брать принципы и алгоритмы, переписывать под текущий контракт `Boot -> Hal -> Kernel -> TestApp`.
