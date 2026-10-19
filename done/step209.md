# step 209 — `[ThreadStatic]` в ядре и приложениях; TEB у каждого потока; чистка plan, donext, CLAUDE.md

## Контекст

`[ThreadStatic]` не собирался ни на одном ярусе: ILC требует тип
`Internal.Runtime.ThreadStatics`. Обходили вырезами в чужом коде (Kusto — один
счётчик глубины на все потоки, `Random.Shared` под замком, кэш писателя в
System.Text.Json, `ArrayPool` без кэша на поток). Ограничение было только в
donext и в комментариях, отдельной строки в limits и README не было.

## 1. Что требует ILC 8

Опыт сборкой AOTTESTS с одним `[ThreadStatic]`:

1. Без типа — `Expected type 'Internal.Runtime.ThreadStatics' not found`.
2. С заглушкой типа — `lld-link: undefined symbol: _tls_index`. ILC 8 на win-x64
   по умолчанию встраивает потоковые статики в родной TLS образа: `gs:[0x58]` →
   массив блоков потока → `[_tls_index]` → смещение в `.tls`. Символ и каталог
   TLS обычно приносит CRT (`tlssup.obj`), у приложений `/NODEFAULTLIB`.
3. В `ilc.exe` есть ключ `--noinlinetls`: каждое обращение — вызов
   `ThreadStatics.GetThreadStaticBaseForType(TypeManagerSlot*, int)`, хранилище
   на поток — управляемое. Выбран он: не нужен родной TLS приложения, каждый
   ярус держит хранилище в своей куче.

## 2. Хранилище: порт BCL, слова потока

`std-no-runtime/Runtime/ThreadStatics.cs` — порт пути без встраивания из
dotnet/runtime (`object[][]` по модулю и индексу типа, блок типа по секции
`ThreadStaticRegion`, 202). Вырезы — встроенный путь и динамические типы.

Записи `ThreadStaticRegion` — абсолютные 8-байтные указатели и при RTR major 9,
в отличие от самоотносительных записей `GCStaticRegion`. Первая версия читала
их как относительные: мусорный `BaseSize`, `OutOfMemoryException` в пробе ядра.
Проверено разбором секции прямо в BOOTX64.EFI (8 байт, `0x1405ff450`).

Где висит `object[][]` (в стоке — `RhGetThreadStaticStorage`, поле потока
рантайма): **слова потока** в `ThreadArea`, найденные через `gs`. Заглушка
`mov rax, gs:[0x58]; mov rax, [rax+62*8]; ret` в буфере X64Asm (+0x540,
`[CompileTimeAsm]`). Ядро зовёт её указателем на функцию, приложение получает
адрес хвостом таблицы служб (`ThreadWordsAddress`, ABI не меняется). Слово [0]
— ядра, [1] — приложения: хранилища в разных кучах. Приложение при старте
обнуляет своё слово — поток мог прийти от прежней программы.

Корни: ядро размечает слова ядра всех потоков через новую зацепку
`GcRoots.ExtraRoots` (её зовёт `MarkStaticRootsOnly`, то есть и точный, и
консервативный путь), приложение — свои слова потоков своего процесса в
`AppGcService.WalkRoots`.

## 3. TEB у каждого потока (`ThreadArea`)

Страница контекста потока (4 КиБ, занято до 0x210 — SavedRsp, TEB, FXSAVE):

- +0x600 — копия `.tls` образа ядра (0x189 байт: `thread_local` CoreCLR), слот 0;
- +0x800 — TEB: границы стека, Self, `ThreadLocalStoragePointer`;
- +0xC00 — массив из 64 слотов; слот 62 — адрес слов;
- +0xE00 — слова.

`ContextBlock+0x08` теперь всегда ненулевой, `CoopSwitch` грузит `GS_BASE` на
каждом переключении. Поток CoreCLR получает полный TEB (`CoreClrTeb`) с тем же
адресом слов в слоте 62 — хранилище не переезжает. Загрузочный поток ставит свой
TEB в `GS` в `Scheduler.Init` (`ThreadArea.Activate`); `[ThreadStatic]` в ядре —
с этого момента.

**Вскрытая ошибка.** Первый прогон: AOTTESTS кончился переполнением стека —
рекурсия #PF в `GetThread()` CoreCLR (`[slots[_tls_index] + 0xE1]`, слот 0
пуст). До step209 TEB был только у потоков CoreCLR, остальные жили со старым
`GS`, и обход SEH (`TryActivateFrameChain` → `SharpOSHost_GetCurrentFrame`)
на потоке приложения читал `thread_local` **чужого** потока CoreCLR. Исправлено
как в Windows: у каждого потока своя копия `.tls`, для CoreCLR он — незнакомый
родной поток (`GetThread()` = null). Плюс проверка `ThreadArea.HasClrTls` в
`SehDispatch` — для потоков, рождённых до того, как шаблон TLS стал читаем.

## 4. Вырезы, которые снялись

- Kusto `ForwardParser.s_callDepth` — снова `[ThreadStatic]` (как upstream).
- `Random.Shared` — генератор на поток, как upstream, без замка. Сборка с ним
  потребовала второй вход: потоковый статик у типа с конструктором класса ILC
  зовёт через `ClassConstructorRunner.CheckStaticClassConstructionReturnThreadStaticBase`
  и ищет `TypeManagerSlot` по имени BCL — `Internal.Runtime.CompilerHelpers`.
  Отдельная проверка в AOTTESTS: `type with a static constructor`.
- Комментарии «потоковые статики не работают» сверены: `GcHeap`,
  `SynchronizationContext`, `ManagedThreadId`, `ArrayPool`, `JsonNode.To`,
  `AppServiceBuilder`, `LockProbe`, `KernelScheduler`.

Не сделано (donext): обращение из разделяемого обобщённого кода (ячейка
словаря), порты `TlsOverPerCoreLockedStacksArrayPool` и `Utf8JsonWriterCache`.

## 5. plan.md, donext.md, CLAUDE.md

- **plan.md** (513 → 58 строк): только цели — текущее направление (асинхронный
  ввод-вывод, затем сеть: семь этапов с критериями) и дальние цели. История
  step127 (baseline, D1-D20, фазы, риски R1-R5, Tier A-E, CSharpRepl в
  подробностях) убрана: она в done/ и git.
- **donext.md:** подробности §11 переехали в раздел «Асинхронный ввод-вывод и
  сеть» вместе с бывшим «Сеть: Windows IL целиком». Удалены закрытые: точная
  сборка ядра (уже `KernelGC.Collect`), дубли BinaryReader/Writer (порт в std),
  номер версии ABI (решение записано в `AppServiceTable.cs`), переполнение стека
  пометки и `alloc_context` (step208). Regex — в одном месте, математика — один
  раздел, пауза сборки — в «Поколениях».
- **CLAUDE.md:** механизм 3 инварианта 1 — `CoffStub.Generator`, а не `.c` в
  PowerShell; `[CompileTimeAsm]` как основной путь шеллкода; границы с CoreCLR
  (из plan §3); `IDictionary`/`BinarySearch` есть; ссылки на limits.md,
  open-symptoms, donext; строка co-author без версии модели.
- limits.md — без ссылок на R1-R5.

## Проверка (WHPX)

- Полная батарея: `[sh] done ran=28 failed=0`, fsck чистый,
  `thread-static probe: own=ok held=ok other=ok — ok`.
- Урезанный прогон после восстановления Kusto и `Random.Shared`: AOTTESTS 313
  (+6: пять `thread static:` и `Random.Shared on two threads`), KQL, KQLTEST,
  KQLBABY, GVMTEST — `ran=9 failed=0`.

## Файлы

- std: `Runtime/ThreadStatics.cs` (новый), `Std.props`, `GC/GcRoots.cs`
  (`ExtraRoots`), `Runtime/Random/Random.cs`, комментарии в `GC/GcHeap.cs`,
  `SynchronizationContext.cs`, `Threading.ManagedThreadId.cs`,
  `Threading.Tasks.KernelScheduler.cs`, `Buffers/ArrayPool.cs`.
- ядро: `Kernel/Threading/ThreadArea.cs`, `ThreadStaticProbe.cs` (новые),
  `Scheduler.cs`, `CoreClrTeb.cs`, `Hal/X64Asm.cs`, `X64Asm.BootAsm.cs`,
  `Exec/CoreClrHost.cs`, `Diagnostics/CoreClrProbe.cs`, `Diagnostics/Probes.cs`,
  `Boot/BootSequence.cs`, `Memory/AppGcService.cs`, `PAL/SharpOSHost/SehDispatch.cs`,
  `Process/AppServiceTable.cs`, `Process/AppServiceBuilder.cs`,
  `Threading/LockProbe.cs`; `OS.csproj` (`--noinlinetls`).
- SDK: `AppServiceTable.cs`, `AppRuntime.cs`, `FreestandingPe.props`.
- вендор: Kusto `ForwardParser.cs`, System.Text.Json `JsonNode.To.cs` (комментарий).
- тесты: `AotTests/ThreadStaticTests.cs` (новый), `AotTests.csproj`,
  `Program.cs`; `Shell/autorun.sh` (AOTTESTS 313).
- std: также `Runtime/ClassConstructorRunner.cs` (вход для потоковых статиков).
- документы: `plan.md`, `donext.md`, `CLAUDE.md`, `limits.md`, `README.md`,
  `docs/nativeaot-nostd-kernel-limits.md`, `docs/threading-architecture.md`.

## Дальше

plan.md, текущее направление: этап 1 — прерывания устройств (MSI).
