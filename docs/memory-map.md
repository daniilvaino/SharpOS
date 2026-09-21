# Картография памяти

Откуда берутся страницы, кто их держит и кто возвращает. Составлено чтением
кода, не комментариев (2026-09-17). Комментарии, которые врут, перечислены в
конце — им не верить.

Рядом: `memory-ownership.md` — кто чем владеет и какие сборщики что видят;
этот файл про механику — кто у кого берёт и возвращает ли.

Назначение — основание для переделки стека выделения (`donext.md`, фронт
«стек выделения памяти»). Поэтому главная колонка везде одна: **возвращается
ли память**.

## Слои

```
UEFI карта памяти
   └─ PhysicalMemory      страницы 4 KiB, bump по регионам + LIFO-freelist
        ├─ KernelHeap     first-fit, 24 B заголовок          → страниц не возвращает
        │    └─ GcHeap(ядро)  сегменты 256 KiB               → сегментов не возвращает
        ├─ NativeArena    чанк 64 KiB, bump                  → free-пути нет вовсе
        ├─ VirtualMemory  окно VA 4 GiB, reserve≠commit      → физпамять возвращает, VA нет
        ├─ стеки потоков, ContextBlock                       → не возвращаются
        ├─ образы приложений + стеки                         → возврат только вложенным путём
        ├─ DMA-буферы                                        → free-пути нет
        └─ таблицы страниц                                   → не возвращаются, large page не сворачиваются

GcHeap(приложение) → GcAppPool, 64 MiB в .bss образа, bump → не возвращается
```

### UEFI handoff

Пять `AllocatePool(EfiLoaderCode)` под исполняемые буферы до снятия карты
(`OS/src/Boot/UefiBootInfoBuilder.cs:53,70,82,97,114`), плюс 64 B в
`UefiImageProtection.cs:121` без сохранения указателя. `FreePool` нет ни для
одного. Карта памяти — ещё два `AllocatePool`, тоже навсегда
(`UefiMemoryMapBuilder.cs:33,43`); массив `MemoryRegion*` — единственный
источник истины для `PhysicalMemory`.

`Usable` = только `ConventionalMemory|PersistentMemory`
(`UefiMemoryMapBuilder.cs:103-138`). `BootServicesCode/Data` в оборот не
попадают **никогда**: `PhysicalMemory.Init` зовётся один раз до
ExitBootServices (`BootSequence.cs:156`), пост-EBS карта считывается и
выбрасывается (`ExitBootServicesProbe.cs:81`).

`UefiImageProtection` делает образ ядра RW+X на всех уровнях при выключенном
CR0.WP (`UefiImageProtection.cs:159-163`).

### PhysicalMemory (`OS/src/Kernel/PhysicalMemory.cs`)

Bump-курсор по регионам плюс LIFO-freelist. Гранулярность 4096, нижняя граница
`0x00100000`, **верхней нет** (`:5-6`).

- Регионы не перебираются повторно; остаток текущего при переходе теряется
  (`:131-132`).
- Freelist — `ulong[262144]`, создаётся лениво **внутри пути освобождения**
  (`:73`). Переполнение — молчаливая потеря страницы (`:74`).
- Многостраничный запрос смотрит во freelist только после исчерпания всех
  регионов: `Array.Sort` по 256K элементов плюс линейный поиск непрерывного
  участка — ровно в момент, когда память уже кончилась (`:152-187`).

### Трансляция (`OS/src/Kernel/Paging/X64PageTable.cs`)

База — глубокий клон PML4 прошивки (`:832-882`), то есть identity-маппинг с
её large pages. При клонировании и расщеплении все directory-записи
принудительно `P|W|U, NX=0` (`:776,877`) — ограничения решают только листья, а
бит `User` роздан всему дереву.

Новые маппинги всегда 4 KiB (`:801-805`). Попадание в унаследованную large
page расщепляет её навсегда: обратной операции нет (`:718-784`). `Unmap`
чистит лист, табличные страницы не освобождает (`:260-328`).

**EFER.NXE нигде в `OS/src` не программируется** (проверено grep'ом). Биты NX
в записях ставятся, но включена ли защита — зависит от прошивки. Не
установлено.

`DirectMapBase = 0xFFFF800000000000` — мёртвое поле, по нему ничего не
маппится (`BootSequence.cs:743`, использования только в валидации и печати).

### KernelHeap (`OS/src/Kernel/Memory/KernelHeap.cs`)

Страницы у `PhysicalMemory` (`:236`), **без** `MapFixed` — расчёт на
унаследованный identity-маппинг. Старт 16 KiB, шаг 16 KiB.

Один глобальный двусвязный список всех блоков всех регионов, заголовок 24 B
(`HeapBlock.cs:3-15`).

- Alloc — **first-fit линейным обходом всего списка** (`:203-215`).
- Free — **ещё один полный обход** ради валидации указателя (`:352-364`),
  затем слияние с соседями только внутри одного региона (`:57-63`).
- Страниц в `PhysicalMemory` не возвращает ни разу. Куча только растёт.
- Payload по смещению 24 ⇒ выравнивание **8, а не 16** (`HeapBlockOps.cs:15-20`).
- `Alloc` без `try/finally` вокруг `Suppress/Allow` (`:62-65`); `Free` —
  правильно (`:106-108`).

### NativeArena (`OS/src/Kernel/Memory/NativeArena.cs`)

Нативные блобы без MethodTable. Чанк 64 KiB, крупное — отдельным участком.

- **Free-пути нет вообще.** Остаток чанка при пополнении теряется (`:136-150`).
- **Без сериализации** — единственный крупный аллокатор без `Preemption`.
- Потребители: каждый CRT `malloc` CoreCLR (`CrtHeapStubs.cs:45`), **каждый
  PE-файл целиком** (`FatBootBridge.cs:38`), TEB/TLS, и **четыре структуры на
  каждое диспетчеризованное SEH-исключение** (`SehDispatch.cs:467-470`).

### VirtualMemory (`OS/src/Kernel/Memory/VirtualMemory.cs`)

Единственное место с раздельными reserve и commit. Окно VA
`0x0000_5000_0000_0000`, 4 GiB, PML4[160] — нижняя каноническая половина
нужна write-barrier'у CoreCLR (`:16-20,41-42`).

`Reserve` — чистый bump, **курсор не откатывается**: VA не освобождается
никогда (`:132-165`). `Decommit`/`Release` возвращают физпамять — это одно из
двух мест во всей системе, где она вообще возвращается (`:260-275`); второе —
`AppServiceBuilder.cs:1886`.

### Управляемые кучи

Ядро — поверх `KernelHeap`. Приложение — `GcAppPool`, `StructLayout(Size =
64 MiB)` в `.bss` образа, bump-курсор, без освобождения
(`GcMemorySource.AppStatic.cs:14-46`).

`GcHeap` (`std/no-runtime/shared/GC/GcHeap.cs`): сегмент 256 KiB (`:21`),
порядок «freelist → bump → новый сегмент» (`:248-302`). Freelist —
односвязный список маркеров, first-fit от головы, остатки расщепления кладутся
**в голову** (`:307-376`). `RebuildFreelist` — полный обход всех сегментов
после каждой сборки (`:382-415`). `FindSegmentContaining` линеен (`:418-428`).
Сегменты не возвращаются никогда.

### Стеки

| Стек | Размер | Guard | Возврат |
|---|---|---|---|
| Boot | 4 MiB `.bss` (`BootStackPool.cs:29`) | нет | н/д |
| BigStack (CoreCLR) | 16 MiB (`BootSequence.cs:575`) | нет | нет |
| Поток ядра | 64 KiB (`Scheduler.cs:22,748-772`) | **есть**, нижняя страница снята | **нет** |
| ContextBlock | целая страница под 528 B (`Scheduler.cs:730-746`) | — | нет |
| Поток hosted | 1 MiB (`ThreadStubs.cs:65`) | есть | нет |
| Поток приложения | 64 KiB (`AppServiceBuilder.cs:1149`) | есть | нет |
| Главный стек приложения | 32 KiB, доступно 0x7E90 (`ProcessImageBuilder.cs:10,100-110`) | **нет** | зависит от пути |

Регионы стеков по глубине вложенности: страйд 256 GiB,
`top(depth) = (depth+1)*stride` (`ProcessImageBuilder.cs:21-30`). Защита от
переполнения — только величина страйда.

### Образы приложений

Файл целиком в `NativeArena` (`FatBootBridge.cs:38`) — навсегда, плюс копия в
managed `byte[]` (`PeLoader.cs:37`).

`PeLoader.TryLoad`: одна непрерывная `AllocPages` на весь `SizeOfImage`
(`:64`), маппинг `Present|Writable|User` без NX ⇒ **весь образ RWX**, per-section
защит нет (`:70`).

Все приложения линкуются `/BASE:0x100000000 /FIXED` — все образы претендуют на
один VA. И каждый несёт 64 MiB `GcAppPool` в `.bss`, то есть **каждый запуск —
непрерывный физический участок ≥64 MiB плюс memset по нему**.

Уборка асимметрична: вложенный путь страницы возвращает
(`AppServiceBuilder.cs:1864-1889`), верхнеуровневый — **нет ни одной**
(`LauncherBoot.cs:434-456`). Одна операция реализована дважды и по-разному.

### Специальные регионы

Исполняемые стабы — пять UEFI-пулов с плотной картой смещений
(`Cr3Accessor`, `GcStackSpill`, `InterfaceDispatchBridge`, `GcContextSpill` в
одном буфере на 1024 B; `X64Asm` и `SehDispatch` в буфере на 2048 B; IDT и
трамплины в буфере на 8192 B).

Патчеры пишут байты **прямо в тело managed-метода** в `.text`, отдельного
буфера не берут (`ByRefAssignRefPatcher`, `PortIoPatcher`, `ChkstkPatcher`,
`BootStackSwitchPatcher`, `InterfaceDispatchPatcher`, `ThrowExPatcher`,
`CallCatchFuncletPatcher`).

DMA — `AllocPages` + `MapFixed` + зануление, virt==phys
(`DmaMemory.cs:17-29`), дубль в `Ahci.AllocDma`. Free-пути нет.

Фреймбуфер — адрес из GOP до EBS, identity-маппинг; **back-буфера нет**,
скролл читает видеопамять и копирует в себя (`FbConsole.cs:119`).

Страница переходников служб приложений — одна на весь boot, маппится
`Writable` без NX ⇒ RWX (`AppServiceBuilder.cs:592`), занято 2432 из 4096.

### Hosted-тир

Своей памяти не имеет. Win32-именованный слой `Memory.cs` — `AllocPages`,
`FreePages`, `ProtectPages`, `QueryPages`, `MapFile`, `UnmapFile` — **все
`Panic.Fail`** (`:42-82`). Живут только `AllocExecutable` и
`ProtectExecutable`. Настоящий VirtualAlloc идёт в `VirtualMemory`, malloc — в
`NativeArena`, TLS-экспорты — тоже `Panic.Fail` (`Tls.cs:26,33,40,47`),
реальный TLS через gs-base и арену.

## Что структурно сломано

Список отсортирован по тому, насколько мешает переделке аллокаторов.

**Память не возвращается почти нигде.**
1. `KernelHeap` не отдаёт страницы `PhysicalMemory` ни разу; `GcHeap` не
   отдаёт сегменты `KernelHeap`. Куча ядра монотонна на всю жизнь машины.
2. `NativeArena` без free-пути, а через неё идут каждый `malloc` CoreCLR,
   каждый PE-файл целиком и ~3.7 KiB на каждое SEH-исключение. Последнее —
   течь, линейная по числу исключений.
3. Табличные страницы не освобождаются, расщеплённые large page не
   сворачиваются обратно.
4. `VirtualMemory.Reserve` двигает курсор только вперёд: 4 GiB окна
   исчерпываются резервациями JIT, после чего `Reserve` возвращает `null`
   навсегда — при полностью свободной физпамяти.
5. Стеки и `ContextBlock` завершённых потоков не освобождаются: пул потоков
   CoreCLR течёт 68 KiB на поток.
6. `LauncherBoot` не возвращает ни образ (≥64 MiB), ни стек, тогда как
   параллельный путь это делает.
7. Провальные пути `PeLoader.TryLoad` теряют весь физический участок
   (`:75-78,92-93`); вызывающие уборку не запускают.
8. Ранний `return` при отказе `MapFixed` теряет уже выделенные страницы
   (`DmaMemory.cs:24`, `NativeArena.cs:129,143`).
9. Освободившаяся после EBS память `EfiBootServices*` в оборот не попадает.

**Молчаливая потеря корректности.**
10. `GcMark.Push` при переполнении стека метки (4096 записей) теряет объект —
    живой объект останется непомеченным и будет подметён. Счётчик
    `LastDroppedCount` есть и читается приложением (`AppGC.cs:119`), ядром —
    нет.
11. `PhysicalMemory.FreePageCore` при полном freelist молча теряет страницу
    (`:74`).
12. `SehUnwind.RegisterStubRange` при заполнении 1024 записей молча игнорирует
    регистрацию (`:362`) — размотка через такой stub перестаёт работать без
    единого сообщения.

**Реентрантность и двойное владение.**
13. `PhysicalMemory.FreePage` → `new ulong[262144]` (`:73`) → `GcHeap` →
    `KernelHeap` → `PhysicalMemory.AllocPages`. Освобождение физпамяти
    реентрантно вызывает её же выделение, и всё это под `Preemption.Suppress`.
14. `KernelHeap.AddRegion` → `GcHeapCensus.Dump` (`:292`) → форматирование
    числа → `FastAllocateString` → `KernelHeap.Alloc`: рекурсия в аллокатор,
    чей новый регион уже подшит в список, но ещё не слит. Держится на
    проверке `IsFree` в `MergeWithPrevious`.
15. `NativeArena` без сериализации при вызовах из преемптируемых путей PAL.
16. `Preemption.Suppress` — общесистемный счётчик, а не по потокам
    (`Preemption.cs:29-38`); нарушения считаются и никем не читаются.

**Линейные проходы на горячих путях.**
17. `KernelHeap.FindFirstFit` — обход всех блоков на каждое выделение.
18. `KernelHeap.ContainsBlock` — второй полный обход на каждое освобождение.
19. `GcHeap.TryAllocateFromFreelist` — first-fit от головы; `RebuildFreelist` —
    полный обход после каждой сборки; `FindSegmentContaining` — линейный.
20. `PhysicalMemory.TakeContiguousFromFreeList` — `Array.Sort` по 256K
    элементов в момент исчерпания памяти.
21. `SehUnwind.InStubRange` — линейный поиск по 1024 диапазонам на каждом
    кадре размотки.

**Права доступа.**
22. Образы приложений маппятся RWX целиком, без per-section защиты. Страница
    переходников — RWX. JIT-память — RWX по построению. Demand-commit в
    VM-окне даёт исполняемые страницы.
23. Directory-записи принудительно `P|W|U, NX=0`; `User` роздан всему дереву.
24. Главный стек приложения без guard-страницы при 32 KiB объёма.

**Непроверяемые предположения.**
25. `KernelHeap.AddRegion`, `Scheduler.AllocateStack`,
    `X64PageTable.AllocateTablePage`, `ProcessImageBuilder` разыменовывают
    физический адрес напрямую, без `MapFixed`, полагаясь на identity-маппинг
    прошивки. `PhysicalMemory` сверху не ограничен. На машине с большим
    объёмом RAM это `#PF` внутри аллокатора.
26. Выравнивание `KernelHeap` — 8 байт. `FXSAVE`/`MOVAPS` требуют 16;
    `Scheduler` обходит это целой страницей на 528-байтовую структуру, любой
    следующий потребитель наступит на ту же мину.
27. `Platform.BootServicesGone` выведен из переключения консоли, которое
    происходит **до** фактического `ExitBootServices`.

## Комментарии, которые врут

Проверено построчно; исправлять по мере касания файлов.

| Место | Комментарий говорит | Код делает |
|---|---|---|
| `VirtualMemory.cs:24-26` | «Decommit/Release — no-op, PhysicalMemory bump-only, no locking» | реально освобождает страницы, у `PhysicalMemory` есть freelist, все операции под `Preemption` |
| `GcHeapCensus.cs:20-21` | «Allocates nothing» | печатает через `FastAllocateString` → `KernelHeap.Alloc`, и вызывается изнутри `KernelHeap.AddRegion` |
| `CrtHeapStubs.cs:64-65,154,316` | «managed GC handles reclaim» | память из `NativeArena`, которую GC не видит и которая не освобождает; шапка того же файла говорит правду |
| `SehDispatch.cs:461-464,473` | «All large state lives in GcHeap» | четыре структуры из `NativeArena`, ~3.7 KiB безвозвратно на исключение |
| `NativeArena.cs:33-35` | «No lock — same discipline as KernelHeap» | `KernelHeap` под `Preemption`, арена — нет |
| `KernelHeap.cs:42-45` | «NOT lock-wrapped, no concurrent access window» | обёрнуто, преемпция существует, а `Yield` внутри критической секции есть |
| `NativeArena.cs:17-21` | «GC sweep frozen via ReclamationDisabled=true» | снято, дефолт `false` |
| `UefiGop.cs:4` | «SharpOS never ExitBootServices» | вызывается (`ExitBootServicesProbe.cs:90`) |
| `Memory.cs:91-92` | «kernel never calls ExitBootServices» | в том же файле `:102` ветка под пост-EBS |
| `BootInfo.cs:51-52` | буфер 256 B | 2048, раскладка до `0x4E0` |
| `X64Asm.cs:30,84,99` | «Buffer 1024» | 2048; тот же файл на `:49` говорит 2 KiB |
| `SehDispatch.cs:1118` | «restore at offset 0x100» | пишет в `+0x200` |
| `BigStack.cs:8-10` | «runs on the 128 KiB UEFI boot stack» | 4 MiB `.bss` с `EfiEntry.cs:46` |
| `BigStack.cs:108,113` | «14 bytes» | эмитируется 18 |
| `AppServiceBuilder.cs:48-51` | «Max 1 nested launch» | `MaxNestedLaunchDepth = 4` |
| `GcMemorySource.AppStatic.cs:21` | «PeLoader flattens a SizeOfImage buffer» | маппит страницы и пишет секции на месте |
| `FileSystem.AppHost.cs:8` | пул 1 MB | 64 MiB |
| `Tls.cs:15-17` | «static slot array ~64 slots» | все четыре экспорта `Panic.Fail` |
| `Ahci.cs:137` | «Low RAM ⇒ 32-bit-safe» | `PhysicalMemory` сверху не ограничен |
| `ChkstkStub.cs:19` | «no guard pages» | у потоков Scheduler guard есть |
| `VirtualMemory.cs:66-70` | «NX is globally off» | EFER.NXE нигде не трогается — не установлено |
