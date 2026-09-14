# step 172 — железо: ноутбук и ПК; порча литерала, OOM, защита образа, выбор диска, C++-исключения

Первый полный прогон батареи (перепись, AOTTESTS, BenchAot, Bench ×3,
PowerShell) на двух настоящих машинах — ноутбуке и настольном ПК — плюс
VirtualBox, с эталонами: Windows на тех же машинах, Debian в том же QEMU.
По дороге всплыло пять дефектов, которые эмуляторы не показывали; один
(C++ catch) был мёртв с самого начала.

Машины: ноутбук — Ryzen 3 7320U, 7.3 ГиБ, AMI (`rev 327707`), флешка в
корневом порту; ПК — Ryzen 9 7900X3D, 64 ГиБ, MSI MAG B650 TOMAHAWK WIFI,
AMI 1.P7 (`rev 327721`), 4 xHCI, SATA-диски в AHCI, BitLocker на всех.
Флешка одна — SanDisk Cruzer Blade, USB 2.0.

## 1. VirtualBox: OutOfMemory в JIT-стрессе → лимит кучи hosted GC от памяти

JIT-проба «4 threads, retained» на VirtualBox держала 56 тыс. живых
`DynamicMethod` (под QEMU — 5 тыс.), куча дошла до 63 МиБ из фиксированных
64 (step068, подобран под QEMU с 256–512 МиБ), следующее выделение — OOM.
Стоковый .NET вне контейнера жёсткого лимита не имеет.

Лимит теперь — половина usable-памяти, от 64 МиБ до 1 ГиБ, диапазон регионов
вдвое больше (`CoreClrProbe.HostedHeapLimit`; потолок держит резервы в общем
окне 4 ГиБ). В лог — `[host] GC heap limit 994 MiB of 1988 MiB usable`.
В перепись добавлены: проба финализаторов (1000/1000 отработали — поток
финализатора жив; `WaitForPendingFinalizers` отдельно), состояние GC после
JIT-проб, обработчик `UnhandledException` с потоком и HResult. Перепись
156 → 157 OK за счёт новой пробы.

## 2. Ноутбук: порча литерала `"\n"` — аллокатор с тихим sentinel

Симптом: на ноутбуке падения в разных местах, не каждый раз, после
`coreclr_initialize`; QEMU и VirtualBox чистые. Похоже на вытеснение или DMA.

Корень: `StringRuntime.KernelHeap.FastAllocateString` без кучи (фаза 0) или
при неудаче возвращал `""` — единственный замороженный `String.Empty` в .data
образа. Вызывающие (`FromUtf16Z`, форматирование чисел, Concat) заполняют
результат на месте. Баннер `fw:` копировал вендора прошивки до кучи:
`"American Megatrends"` (19 знаков) лёг поверх заголовка и длины соседнего
литерала `"\n"`, и первый `Write("\n")` после инициализации CoreCLR выводил
мегабайты образа. На OVMF `"EDK II"` (6 знаков) запись падала в выравнивание —
невидимо. Улика стояла в логе годами: `fw: unknown` на всех машинах.

Сошлось вместе: литералы в записываемой .data (запись не фолтит), аллокатор,
отдающий правдоподобный общий объект вместо отказа, вызывающий, пишущий в
результат на месте, и строка из внешнего мира, длина которой зависит от
машины. Опасность знали — `Console` проверял готовность кучи сам, — но
чинили у одного вызывающего, а не в источнике.

Починено: баннер печатает вендора по знаку из буфера прошивки. Аллокатор
больше не отдаёт общий объект (§3).

**Ловушка по дороге.** Детектор записи на отладочных регистрах (WriteWatch)
выдал «не запись процессора»: его ставили уже после порчи, и целостность
литерала в момент постановки не проверялась. Удалён. Сначала сверять
содержимое порчи с внешними строками (вендор, SMBIOS, дескрипторы), потом
ставить ловушки.

## 3. OOM как в NativeAOT; аудит тихих заглушек аллокаторов

В NativeAOT исключение OOM создаётся заранее
(`PreallocatedOutOfMemoryException.Initialize` в инициализации CoreLib);
`GetRuntimeException` сначала пробует свежее, при неудаче отдаёт заготовку.
Сделано так же, для ядра и для PE-приложений:

- `GcHeap.PrepareOutOfMemory()` — сразу после `GcHeap.Init` (ядро —
  `BootSequence`, приложения — `AppRuntime`): объект, зарезервированная
  трасса стека, корень в сыром слоте.
- `GcHeap.OutOfMemory()` — свежий OOM, иначе заготовка со сброшенной трассой;
  до кучи или до заготовки — `Fatal` (ядро — `Panic.Fail`, приложение —
  `[fatal] …` в поток ошибок и код 134).
- `FastAllocateString`: длина 0 → `""`, отрицательная → `OverflowException`,
  до кучи → `Panic`, размер в ulong с потолком `MaxAllocationSize`
  (0x7FFF0000), неудача → OOM. `StringRuntime.Fallback.cs` (заглушка,
  отдававшая `""`) удалён.
- `RhpNewFast`/`RhpNewArray`/`RhBox`/`RhNewObject` бросают OOM;
  отрицательная длина массива — `OverflowException`; `Concat`/`Join`
  проверяют длину в long; `KernelHeap` отказывает запросам больше
  0x7FFF0000; `_calloc_dbg` — проверка переполнения; переполнение таблицы
  корней GC — `Fatal`.
- `ExceptionEngine.GetRuntimeException(OutOfMemory)` → `GcHeap.OutOfMemory()`.

Проверка: `[info] OOM/huge-alloc -> deterministic exception: ok val=1` —
QEMU, ноутбук, ПК.

## 4. ПК: чёрный экран — прошивка защищает образ

Симптом: экран очищен, курсор сверху, дальше ничего; лога нет; ноутбук с той
же флешкой грузится.

Корень: патчеры пишут машинный код поверх тел методов в образе
(`BootStackSwitch` — первым, до IDT и баннера), часть шеллкода живёт в .data.
Новые прошивки отображают загруженный образ по секциям: код только для
чтения, данные без исполнения (EDK2 image protection). Первая запись фолтит в
обработчик прошивки, который печатает в невидимый отладочный порт и стоит.

Починено (`UefiImageProtection.MakeImageWritable`, из
`UefiBootInfoBuilder.Build` сразу после `TryMaximizeTextMode`):

- есть `EFI_MEMORY_ATTRIBUTE_PROTOCOL` (UEFI 2.10, OVMF) — снять RO/XP/RP со
  всего образа и напечатать итог одной строкой;
- нет (AMI 1.P7: `no memory attribute protocol, base=0x180000000`) — открыть
  записи таблиц страниц: CR0.WP снят, RW поставлен, NX снят на всех уровнях
  для страниц образа, CR3 перезагружен. Стабы CR0/CR3/CR4 — в пуле
  EfiLoaderCode.

Попутно откатил ложную гипотезу про 4K: `DisplayMaxWidth` снова 1920×1080 —
прошивка ПК отдаёт 1024×768, режим не переключался ни разу.

## 5. ПК: не тот диск — загрузочный диск только по ответу прошивки

Симптом после §4: исключение вместо загрузки, лога на флешке нет.

Корень: флешка стояла за USB-хабом, хабы наш стек не перечисляет; `BootDisk`
при неудаче USB падал на AHCI и монтировал первый читаемый SATA-диск —
раздел прошивки диска Windows. `\sharpos` там нет, CoreCLR не нашёл CoreLib
и упал (§6). Писать в выбранный диск могли лог и пробы записи — на диск с
BitLocker. Второй путь к той же ловушке — `Disk.Instance`: «диск» — тот, что
создан последним (пробой, драйвером), а не выбранный.

Починено:

- `BootMedium` различает USB (3/5), SATA (3/0x12, порт HBA по +4) и NVMe
  (3/0x17) из device path до EBS.
- `BootDisk`: USB → только USB; SATA → ровно тот контроллер и порт
  (`Ahci.Initialize(slot, function, port)`); NVMe и неизвестное → нет диска,
  без угадывания. `MissingReason` — почему (в т. ч. «за хабом не видно»,
  «нет FAT»).
- `Disk.Instance` вырезан.
- Диск — обязательное условие: `FileSystem.Init` после EBS = смонтирован ли
  диск; `LauncherBoot` — паника `no boot disk: <причина> — the launcher and
  every app load from it; nothing to run`; `ExitBootServicesProbe` не
  запускает сессию CoreCLR без диска и печатает причину.

Проверено на ПК по всем портам: за хабом — сообщение о диске, в двух
корневых портах — полный прогон, лог на флешке.

## 6. C++-исключения в CoreCLR не ловились никогда

Нашлось на ПК: нет CoreLib → `ThrowHR` в `PEAssembly::DoOpenSystem` →
`Rip=0` вместо кода ошибки из `coreclr_initialize`. Стоковый CoreCLR ловит
это своим `EX_CATCH` в `EEStartupHelper` и возвращает hr.

Корень — `CxxFrameHandler.FindCurrentState`: на x64 (FuncInfo magic
`0x19930522`) IP-to-state map, `dispOfHandler` и unwind actions — RVA образа,
а сравнивались со смещением от начала функции. Состояние всегда -1: ни один
C++ catch в образе не срабатывал и деструкторы при размотке не звались —
все `EX_TRY/EX_CATCH` CoreCLR были мертвы.

Починено (`CxxFrameHandler`, `SehDispatch`, `CxxActiveCatch`, `X64Asm`):

- состояние по RVA; catch — фанклет по RVA `dispOfHandler`;
- вход в фанклет: объект в переменную catch (`BuildCatchObject`: ссылка,
  указатель с поправкой на базу, copy ctor), кадр родителя в RDX, возврат
  через stub (`X64Asm` 0x4C0) — FH3-фанклет отдаёт продолжение в RAX;
- `throw;` — объект и тип из записи активного catch потока
  (`Thread.ActiveCatches`), объект скопирован с мёртвого стека броска;
- исключение из фанклета: его хендлер спрашивается с кадром родителя
  (состояние «внутри catch»), родитель — из сохранённого контекста без
  повторного вызова хендлера; то же в `RtlUnwind`;
- деструкторы при размотке для любых исключений (раньше проверка «не C++»
  стояла до ветки размотки); в ловящем кадре — до `tryLow`;
- вход в хендлер из контекста самого ловящего кадра (FH3/FH4/`__except`),
  а не вызывающего; у `__except` RAX = код исключения;
- непойманное C++-исключение — всегда отчёт: `[seh] unhandled C++ exception,
  raised from rip=… (krnl+0x…) hr=0x…` и паника (была заплатка «продолжить»
  по зашитому диапазону RVA); не-C++ без нативного хендлера — строка `[seh] no
  native handler, handing to managed dispatch: code=…`.

Проверка: `Probes.HostedHideCoreLib = true` → `coreclr_initialize
hr=0x80070002`, по логу — вложенные catch, `throw;` из фанклета,
`EEFileLoadException` из catch. Батарея QEMU после: census 157/2/18,
AOTTESTS 57/57, бенчи, PowerShell — без изменений. ПК на этой сборке — так же.

`collections` в Bench после фикса под QEMU делает полную сборку в каждом
прогоне (gc0=gc1=gc2=1, как Linux и Windows), на ПК на той же сборке — нет.
Это порог, а не EH: Dictionary/List дают ~3.4 МБ массивов в LOH при бюджете
LOH ~3 МБ. Ранняя гипотеза «сборка ломалась на C++-исключении» снята.

`docs/coreclr-hosted-limits.md` §12 (LIMIT-12.1–12.3) винит тот же путь;
не перепроверено — Sec 9 `absurd-size alloc` всё ещё `Skip()`.

## 7. COM1 без чипа

На ПК (AM5) COM1 нет, но `WriteChar` писал в порт — статус и запись на пустой
шине, ~99 мкс на знак: 1.6 с из 2.4 с записи за прогон Bench, отсюда 10 мс на
строку вывода против 2.8 мс на ноутбуке (там 16 мкс на знак). Теперь в COM1
без ответа на петлевой тест не пишется ничего, как в COM3/COM4.
**Правка сделана после прогона на ПК — ещё не запускалась.**

## 8. Замеры: три профиля

Bench.dll — один и тот же файл. Три прогона в одной сессии рантайма: на
SharpOS — три запуска из лаунчера в одной сессии CoreCLR, на Windows и Linux —
`BenchHost` (три `ExecuteAssembly` в одном процессе, настройки GC как у
SharpOS: не concurrent, лимит 1 ГиБ, регион 1 МиБ, RetainVM). Раньше эталоны
снимались отдельными процессами — холодный JIT против нашего тёплого;
второй-третий проход в одном процессе быстрее первого в 3–20 раз.

Третий прогон, нс/оп; в скобках первый; Windows — диапазон двух запусков.
SharpOS: QEMU и ноутбук — сборка до §5–§7, ПК — после. Сырьё —
`work/bench-2026-09-14/`.

| | QEMU SharpOS | QEMU Linux | ноут SharpOS | ноут Windows | ПК SharpOS | ПК Windows |
|---|---|---|---|---|---|---|
| alloc.short | 427 (924) | 80 | 50 (181) | 12–13 | 3 (173) | 2 |
| alloc.retained | 136 (279) | 251 | 20 (83) | 28 | 9 (22) | 6–8 |
| strings | 737 (1869) | 869 | 73 (149) | 113–118 | 42 (88) | 26 |
| collections | 77 (2294) | 172 | 24 (49) | 77–85 | 16 (31) | 29–31 |
| exceptions | 59944 | 31482 | 4147 (5213) | 3114–3563 | 2113 (2644) | 1089–1152 |
| jit | 511278 | 456776 | 36534 | 48089–54399 | 19543 (26845) | 24803–28009 |
| tasks | 19417 (146791) | 8568 | 15307 (74780) | 475–1272 | 14589 (59175) | 164–542 |
| output | 426487 | 234242 | 2840967 | 2638–4188 в пайп | 10427026 | 1273–1578 в пайп |

Сборки gen0 в `alloc.short` по прогонам: QEMU 1/1/1 (Linux 0), ноутбук 5/1/1
(Windows 8/0/0), ПК 1/1/0 (Windows 0/0/0).

BenchAot (NativeAOT-ярус), нс/оп — alloc.short / retained / strings /
collections / exceptions / tasks: ПК (второй прогон) 8/11/69/21/257/492433;
ноутбук 14/21/133/34/463/306622; QEMU 128/197/910/195/5724/351006.

Что видно (разбор — следующим шагом):

- Без сборки gen0 выделение вровень: ПК 3 нс против 2. Со сборкой — 92 нс,
  то есть одна сборка gen0 стоит ~27 мс на ПК и ~11 мс на ноутбуке: на
  быстрой машине дольше. Не понято.
- `tasks` ~15 мкс на задачу на ноутбуке и на ПК одинаково, Windows —
  0.2–1.3 мкс: не счёт, а задержка пробуждения. Не понято.
- `exceptions`: ноутбук ×1.2–1.3 к Windows, ПК ×1.9, QEMU ×1.9 к Linux.
- `jit` на железе быстрее Windows (×0.7–0.8), в QEMU ×1.1 к Linux.
- retained/strings/collections — у нас не медленнее; в collections у Windows
  и Linux полная сборка (§6), у нас не всегда.
- `output` на железе — синхронная запись каждой строки лога на флешку
  (`usb.wait ≈ disklog`, 1.5–3.5 мс на сектор; та же флешка под Windows со
  сквозной записью — медиана 1.7–2.1 мс) плюс на ПК пустой COM1 (§7).
- AOT `tasks` 0.3–0.5 мс на задачу везде — известное: AOT-задачи без пула.

## 9. PowerShell 7.6.5

| | до приглашения | `ls` | Tab | `echo` |
|---|---|---|---|---|
| SharpOS QEMU | 6441 мс | 1092 | 561 | 350 |
| Linux QEMU | 5879 (8369 холодный) | 756 | 863 | 678 |
| SharpOS ноутбук | 5323 | 1720 | 96 | 1260 |
| SharpOS ПК | 4371 | — | 80 | — |

Windows, старт→выход с `-Command exit` (без интерактивной строки — меньше
работы): ноутбук 403–529 мс с флешки, 294–300 с NVMe; ПК 198–223.

На ПК сценарий набирался руками: Enter на ошибочной команде — 7.1 с, второй
Tab — 2.3 с; `usb.wait` 25.1 с из 58.5 с прогона. На ноутбуке `usb.wait`
7.5 с, 89 % непростойного времени профиля — ожидание xHCI; `ls` и `echo` на
железе медленнее, чем в эмуляторе. PowerShell упирается в чтение с флешки
USB 2.0: сборки читаются целиком (плоская раскладка PE), кеша страниц нет.
Объём чтения ещё не посчитан.

## Уроки

- Аллокатор при неудаче — паника, исключение или null, никогда не общий
  объект: замороженные литералы записываемы, порча всплывает через секунды в
  другой подсистеме.
- Загрузочный диск — только тот, что назвала прошивка. Угаданный диск хуже
  отсутствующего: падает позже и в другом месте, и в него пишут.
- Прошивки защищают образ; всё, что правит собственный код, должно сначала
  снять защиту — или сказать одной строкой, почему не может.
- Сравнивать тёплое с тёплым: эталон в отдельных процессах занижал нас в
  3–20 раз.
- Ловушку проверять на целостность в момент постановки.

## Файлы

- Аллокаторы и OOM: `std/no-runtime/shared/GC/GcHeap.cs`, `GcRoots.cs`,
  `GcRuntimeExports.cs`, `Exception.cs`, `StringRuntime.KernelHeap.cs`,
  `StringRuntime.RhNewString.cs`, `StringRuntime.Fallback.cs` (удалён),
  `StringManipulation.cs`, `Runtime/RuntimeImports.Delegate.cs`;
  `OS/src/Kernel/Memory/KernelHeap.cs`, `OS/src/Boot/ExceptionEngine.cs`,
  `OS/src/Boot/BootSequence.cs`, `OS/src/PAL/SharpOSHost/CrtAndEhStubs.cs`,
  `apps_native/sdk/AppRuntime.cs`, `OS/src/Kernel/Diagnostics/NativeAotProbe.cs`,
  `OS/src/Hal/Console.cs` (комментарий), `docs/memory-ownership.md`.
- Баннер: `OS/src/Kernel/SystemBanner.cs`.
- Прошивка: `OS/src/Boot/UefiImageProtection.cs` (новый),
  `UefiBootInfoBuilder.cs`, `EfiEntry.cs`.
- Диск: `OS/src/Boot/BootMedium.cs`, `OS/src/Hal/Ahci.cs`, `BootDisk.cs`,
  `Disk.cs`, `OS/src/Kernel/File/FileSystem.cs`,
  `OS/src/Kernel/Process/LauncherBoot.cs`, `OS/src/Boot/ExitBootServicesProbe.cs`.
- C++ EH: `OS/src/PAL/SharpOSHost/CxxFrameHandler.cs`, `CxxFrameHandler4.cs`,
  `CxxActiveCatch.cs` (новый), `SehDispatch.cs`, `CrtHeapStubs.cs`
  (`HostedHideCoreLib`), `OS/src/Hal/X64Asm.cs`,
  `OS/src/Kernel/Threading/Thread.cs`, `OS/src/Kernel/Diagnostics/Probes.cs`.
- COM1: `OS/src/Hal/Serial.cs`.
- Hosted GC и перепись: `OS/src/Kernel/Diagnostics/CoreClrProbe.cs`,
  `apps_managed/normal-hello/Program.cs`.
- Замеры: `apps_managed/BenchHost/` (новый), `run_linux_ref.ps1`,
  `tools/bench-qemu-linux-reference.log`, `tools/pwsh-qemu-linux-reference.log`.
- Документы: `docs/coreclr-hosted-limits.md`,
  `docs/nativeaot-nostd-kernel-limits.md`, `docs/perf-progress.md`,
  `README.md`, `CLAUDE.md`, `donext.md`.

## Откладываем

- Хабы USB (флешка за хабом на ПК не видна).
- PowerShell на железе: счётчики чтения файлов, частичное чтение / кеш.
- Лог на диск пачками, а не посекторно синхронно.
- Цена сборки gen0 на железе; задержка `tasks` — счётчики SuspendEE и
  пробуждения.
- Режим GOP при захвате консоли (ПК остаётся в 1024×768).
- NVMe-драйвер; неоднозначность шины PCI в `BootMedium`.
- Перепроверить LIMIT-12.1–12.3 после фикса C++ EH (снять `Skip()`).
- Прогнать правку COM1.

## Следующий шаг

Разбор чисел по трём профилям (QEMU: SharpOS/Linux, ноутбук и ПК:
SharpOS/Windows): что масштабируется с процессором, что нет, и почему.
