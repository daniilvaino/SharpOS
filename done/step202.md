# step 202: чистка docs, комментарии сверены с кодом, корень тормозов KQL

## Контекст

KQL после step201 заметно тормозил. Разбор лога батареи показал, что дело не
в KQL, а в `KernelHeap` (ниже). Чтобы проверить, что вообще известно о памяти,
шесть агентов прочитали код по слоям и собрали рабочую карту выделения памяти:
прошивка и страницы, KernelHeap, управляемая куча, обменная куча и трубы,
потоки и процессы, CoreCLR. Карта — рабочий материал (`work/memory-map/`),
в репозиторий не кладётся. По ней видно, что `docs/memory-map.md`
(2026-09-17) и `docs/memory-ownership.md` отстали на step189–196, а
комментарии в коде врут в тех же местах. Этот шаг — их чистка.

## Корень тормозов KQL

Лог `step201_battery_final_kernel.log`:
- **Устройство `KernelHeap`.** Один список всех блоков, `FindFirstFit` с головы, `Free` тоже проходит список (`ContainsBlock`); страниц не отдаёт.
- **Чем забит список.** Кэши диспетчеризации `InterfaceDispatchResolver.PublishCache`: 48 + 24 Б на каждую смену типа в точке вызова, ядра и приложений; старый не освобождается, связи с процессом нет. Строки ядра (`StringRuntime.KernelHeap`) тоже бессмертны.
- **Доказательство.** Выделение длиннее тика, отложенный тик берётся в `Allow` один раз, поэтому отложенных yield столько же, сколько выделений. 226 на рост в 4 страницы ⇒ блоки по 72 Б.
- **Конец батареи.** 424 МиБ роста, ≈0,8 млн блоков (оценка), ≈5 мс на выделение, 96–99 % выборок в `FindFirstFit`, ≈99 % тиков отклонено. KQLBABY: 4,9 с на свежей загрузке → 188 с в конце батареи.
- **Крупные росты** — сегменты GcHeap ядра под копию EXE в `PeLoader` (`byte[]` на каждый запуск); хвост текущего сегмента при росте теряется (≈95 МиБ).

## Что сделано

**Удалены из docs** (история — в git):
- `memory-map.md`, `memory-ownership.md` — устарели на step189–196;
- `nativeaot-nostd-elf-limits.md` — ELF-яруса нет с step137;
- `eh-audit-2026-06.md`, `eh-actual-map.md` + `index.html` (as-built на step128) — EH описывают `eh-model.md` и живой `eh-audit-2026-09.md`;
- `preemption-readiness.md` (оценка перед Phase F) и `pal-minimal-audit.md` (2026-08) — пройденные этапы.

**Документы:**
- limits:
  - материализация статиков — Phase 2, а не «поздно»;
  - кэши диспетчеризации — главный источник длины списка, а не «допустимо»;
  - `/BASE:0x200000000000`;
  - большой объект приложения (step196, предел ≈31,75 МиБ — вывод из кода);
  - скорость труб 25,5 + 17,2 мкс вместо «≈35»;
  - куча ядра собирается перед ростом;
  - вытеснение в лаунчере включено;
  - потоки приложений отдают стеки;
- `CLAUDE.md` — то же про статики, последний шаг;
- `donext.md` — раздел «Память ядра» с порядком работ вместо закрытых этапов step178/196; пункт о скорости KQL указывает на корень;
- ссылки на удалённое в `threading-architecture.md`, `eh-audit-2026-09.md`, `fronts-2026-09.md`.

**Комментарии** — только текст, поведение не менялось:
- **ядро:** `KernelHeap`, `InterfaceDispatchResolver`, `GcHeapCensus`, `Scheduler`, `Thread`, `VirtualMemory`, `VirtualMemoryHost`, `SharpOSHost/Memory`, `UefiGop`, `BootInfo`, `SehDispatch`, `BigStack`, `ProcessImageBuilder`, `Threading/Process`, `CoffRuntimeFunctionTable`, `NativeArena`, `CrtHeapStubs`, `CoreClrProbe`, `AppProcess`, `Probes`, `AppGcService`, `EfiEntry`;
- **std и SDK:** `GcSegment` (заголовок 56 Б, карта начал), `GcHeap` («удвоение», а не «на половину»), `GC`, `GcRuntimeExports`, `GcStaticsInit`, `GcMemorySource.KernelHeap`, `Chunked`, `AppRuntime`, `AppHeapCensus`, `FreestandingPe.props`;
- **форк** (отдельный коммит): `crt_imp_stubs.cpp` (шапка VirtualAlloc/VirtualFree, NativeArena вместо GcHeap для файлов и TEB), `winapi_shim.cpp`, `gc.cpp` (ветка `regions_range`).

Удалён мёртвый код: `Scheduler.ContextBlockBytes`, `ProcessImageBuilder.StackRegionStride/DefaultStackMappedTop/StackMappedTopForDepth` — grep показывает ноль использований, сборка прошла.

Оставлены как есть: исторические разделы с датой шага (`coreclr-hosted-limits.md`, «IL-only» в разделе step152) и рассказ о прошлом в `apps_native/sdk/AppGC.cs:7-10`.

## Проверка

`build.ps1` и `run_build.ps1 -Autorun`, QEMU 2 ГиБ, сборка
`809d863+dirty.1012-1906`: `[sh] done ran=28 failed=0`. Паник, вложенных отказов
и `[oom]` нет; четыре `stack overflow … exit code 134` — проверки PROCTEST.

## Что дальше

`donext.md` → «Память ядра»:
1. прибор шагов обхода;
2. корзины в `KernelHeap`;
3. пул кэшей диспетчеризации с освобождением;
4. строки ядра в управляемую кучу;
5. хвост сегмента и копия EXE;
6. мелочь, найденная при разборе.
