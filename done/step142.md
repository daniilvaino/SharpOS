# Step 142 — ВЕХА: ManagedDoom sim alive — полная игровая симуляция DOOM на app-std

## Результат

```
doom: start → config ok → Open WAD files: OK (DOOM1.WAD) → палитра/текстуры/
флэты/спрайты OK → core up, ticking → tic 0..140 → Load map 'E1M5': OK →
doom: ticked 175 — sim alive → exit=42 → возврат в лаунчер
```

DOOM.EXE запускается из лаунчера, парсит shareware WAD (4 MB с ESP через
AppHost read-сервис), поднимает игровое ядро и отыгрывает 175 тиков
attract-демо — это полная world-симуляция (E1M2-демка целиком + загрузка и
проигрывание E1M5), headless, на нашей std, на bare metal. Регрессий нет:
батарея OK=110 (1 known FAIL EnumToString), census OK=145 DEG=2 FAIL=20,
EBS live.

Путь к этому прошёл через шесть корневых находок — каждая закрывает
системный пласт, а не DOOM-специфику.

## 1. cctor-тракт для апп (DropResilient + GcStaticsInit)

Аппы собирались с `--resilient` — ILC молча ставил fallback-стабы вместо
`ClassConstructorRunner`, а GCStaticRegion аппы никто не материализовал
(kernel-моторика NativeAotModuleInit/GcStaticsMaterializer — только для
своего образа). DOOM-у, живущему на `static readonly`-мегатаблицах
(DoomInfo.*), это фатально.

- `FreestandingPe.props`: DropResilient target (зеркало OS.csproj).
- `std/GC/GcStaticsInit.cs`: app-side порт хода материализатора — RTR-хедер
  ищется В СВОЁМ образе (скан ограничен SizeOfImage из PE-хедеров на
  фиксированной базе), walk секции 201 (major-9 relptr), аллокация из
  своего GcHeap, `GcRoots.RegisterRawSlot`. Вызов — AppRuntime.Initialize
  сразу после GcHeap.Init.

## 2. RhpStackProbe + касты (то, что resilient маскировал)

DropResilient вскрыл три неразрешённых символа:

- `RhpStackProbe` — ILC зовёт его в прологах методов с фреймом >4KB;
  реализация в исключённом Runtime.WorkstationGC. Контракт —
  register-preserving, managed-телу доверять нельзя → `StackProbeStub`
  (sdk) + self-patch первого байта на `0xC3` первой строкой Initialize
  (паттерн `__chkstk`: на премапленных стеках probe семантически пуст).
- `RhTypeCast_CheckCastAny`/`CheckCastClassSpecial`/`RhpLdelemaRef` —
  переехали из kernel-only partial в shared std GcRuntimeExports: причина
  раскола («у апп нет Exception/Unsafe») умерла на step140/141.

## 3. Коллизия окна образа с kernel-RAM → перебазирование на 4 GiB

64MB GcAppPool в .bss раздул SizeOfImage до 65MB → окно PeLoader'а
[0x400000, +65MB) накрыло phys 4–72MB. Kernel и аппы делят одно адресное
пространство: «drop stale mappings» снёс identity-мапы живой kernel-RAM
(kernel-heap, page-table страницы) → рекурсивный #PF-шторм в
`X64PageTable.TryResolveMappedEntryForRoot` (символизация RIP по
dumpbin /PDATA + OS.pdb). Nested-механика подтвердила структурно:
TrySuspendCurrentForNested снапшотит только РОДИТЕЛЬСКИЙ extent, окно
ребёнка сносится целиком — маленькие аппы просто случайно не задевали
ничего живого.

Фикс: `/BASE:0x100000000` (4 GiB — выше RAM, ниже app-стека 256 GiB) в
props + `GcStaticsInit.ImageBase`. В ядре база нигде не зашита (PeLoader
honors ImageBase, трамплины absolute).

## 4. Array<T> — закрытие «массивы не IEnumerable<T>» навсегда

Краш `Config()` → `new KeyBinding(new DoomKey[]{...})` →
`IReadOnlyList<DoomKey>`-параметр → `keys.ToArray()` → интерфейсный
GetEnumerator на массиве → диспатч по мусору (скошенный this в
List<DoomKey>.Enumerator.MoveNext, вычислено дизассмом fault-сайта +
pdata-символизацией).

Раскопка upstream дала главный приз step'а: в NativeAOT НЕТ отдельного
SZArrayHelper — **интерфейсы SZ-массивов ILC берёт из `System.Array<T>`**
(interface list + dispatch map; `this` внутри методов — сам `T[]`). Наш
`class Array<T> : Array { }` был пустышкой → NumInterfaces=0 → вся
многолетняя готча §4. «Крупная отложенная runtime-задача» оказалась портом
одного класса: `std/Runtime/ArrayT.cs` (Array<T> + ArrayEnumerator из
v8.0.27; cuts: Empty-синглтон, ThrowHelper). Пустышки удалены из обоих
MinimalRuntime; массивы — честные
IEnumerable<T>/ICollection<T>/IList<T>/IReadOnlyList<T> на обоих тирах.
Батарея LINQ (List-based) и все прежние пробы зелёные.

Array-перегрузки Enumerable (шаг 141/142: +SkipWhile/TakeWhile/ToArray по
цензу CommandLineArgs.GetValues) остаются как fast-path без
диспатча/боксинга.

## 5. TypeManager для app-образа

Следующий слой: `iface-resolve fail` с `tm=0x0` — резолвер на
shared-generic пути ходит MT → TypeManagerIndirection → TypeManager →
DispatchMapTable, а слоты app-образа никто не заполнял (kernel-init —
только для kernel-образа; «pure»-резолвер step139 жил на прямых
совпадениях). `AppTypeManagerInit` (std, рядом с GcStaticsInit — общий
RTR-сканер): 56-байтный TypeManager в fixed .bss (НЕ GcHeap — блок без
MT-хедера свалил бы heap-walker), `m_pHeader`/+8 + `m_pDispatchMapTable`/+16,
публикация во все TypeManagerSlot'ы. Вызов — до первого диспатча.

## 6. Вариантный interface dispatch — задокументированное ❌ + vendor-патч

Финальный слой (`Load map 'E1M5'`): дизассм вокруг единственной ссылки на
fail-ячейку → `DoomInfo.MapTitles.Doom[episode-1][map-1]`. Таблица
объявлена `IReadOnlyList<IReadOnlyList<DoomString>>`, лежит `DoomString[][]`
— присваивание легально только через ковариантность `out T`, а в
interface-мапе массива `IReadOnlyList<DoomString[]>` ≠ запрошенному MT.
Резолвер матчит строгим равенством указателей; вариантного пути
(`AreTypesAssignable` по generic-аргументам) нет.

Вложенно-вариантных деклараций во всём DOOM три (MapTitles.Doom,
AnimationInfo.Episodes, WorldMap.Locations) — vendor-патч на jagged-типы
(`X[][]`, + один `.Count`→`.Length`), потребители переходят на прямое
индексирование. Ограничение записано в limits §2 c триггером «первый
невендорируемый потребитель».

## Сопутствующее

- WAD-конвейер: `wads/*.wad` (gitignored) → run_build.ps1 стейджит на ESP;
  DOOM1.WAD взят из MOOS/Ramdisk (валидный shareware IWAD v1.9, 1264 лампа).
- `ProcessStub.FileName` → `\EFI\BOOT\APP.EXE` (exe-dir идиома ManagedDoom
  резолвится туда, где лежат WAD'ы).
- HW-fault исключения не матчатся app-catch'ами (kernel-MT vs app-MT клауз,
  cross-image identity) — наблюдено вживую, записано в donext.md с
  целевой формой (RhpThrowHwEx-паттерн: кидать в образе фолта).
- Дубликаты MinimalRuntime — backlog слияния записан в donext.md.
- Инструментарий, сложившийся в step: символизация RIP/MT через
  `dumpbin /PDATA` + pdb (kernel и app), поиск callsite по ссылке на
  dispatch-cell сканом .managed, ручной дизассм fault-функций.

## Уроки

1. **Читать, что механизм уже есть у ILC** (родня step110): SZArrayHelper
   «нет» — потому что он называется Array<T> и ждёт наполнения. Час
   раскопки upstream сэкономил недельный runtime-порт.
2. **Один адресный простор kernel+apps — инвариант расположения окон**:
   раздувая образ, проверяй, что map-over не пересекает чужую RAM. База
   4 GiB делает класс ошибок невозможным.
3. resilient-режим ILC = слепота: маскирует недостающие символы до самого
   неудобного момента. Для новых тиров выключать сразу.
4. Fault-цепочки с «kernel RIP на app-стеке» — это чаще всего сервис/
   обработчик, читающий app-данные, а не kernel-баг.
5. Диагностическая связка «cell-ref скан + pdata-имена + ручной дизассм»
   закрывает вопросы «кто и что диспатчит» без пересборок с картами.

## Отложено / следующее

- P2b: видео — GOP-framebuffer сервис в AppServiceTable + blit из
  ManagedDoom Renderer (NullVideo → реальный). P3: PS/2 ввод + тайминг
  (Stopwatch/DateTime сейчас нули — демо тикает по вызову, играбельности
  нужен реальный tick-pacing).
- Вариантный dispatch — по триггеру.
- HW-fault→app-exception мост — по триггеру (donext).
- Config/savegame записи — discard до write-сервиса.
