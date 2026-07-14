# step 130 — net7 → net8 bring-up (ILC 8, RTR major 9)

## Контекст

North-star — managed DOOM против собственной std. Для него нужны делегаты +
PE-машинария. Автор решил: **сначала бампнуть .NET**, потом делегаты, иначе
делегаты придётся портить дважды (под net7 и снова под свежий ILC).

Ранее (2026-07-13, `done`-заметка в donext §«Эксперимент net8») флип net7→net8
упёрся в scanner-фазу ILC 8.0.27 (`UnmanagedEntryPointsRootProvider` duplicate
key `ScannedMethodNode` — методы с ОБОИМИ атрибутами `[RuntimeExport]` +
`[UnmanagedCallersOnly]` под одним именем). Эта сессия прошла ту стену и все
последующие — до полностью зелёной батареи.

Таргет: kernel-AOT `net8.0` / ILC `8.0.27` / **RTR major 9** (было major 8 на
ILC 7). `project.assets.json` → `ilcompiler/8.0.27`. `LangVersion` запинен
`14.0`. Форк-CoreCLR (hosted-tier) — отдельно, net10.0 BCL.

## Результат (полный прогон, gate=on)

```
Phase4: NativeAotFeatures OK, IcedEncode OK, GenericDictionary OK, ExplicitCctor OK,
        ComplexCctor OK, EnumCast/Bitwise OK, EnumToString FAIL (known gap)
EH:     L1..L17 все OK
PhaseE: ping-pong 5/5, atomics, sleep/event/semaphore/allocstress/process — OK
CoreCLR: coreclr_initialize hr=0x0, execute_assembly exitCode=42,
         PAL/OS census OK=145 DEG=2 FAIL=20
Launcher: HELLO/ABIINFO/HELLOCS/MARKER.ELF — 4/4 OK
Totals: OK 58 / VALUE 3 / FAIL 1 (EnumToString)
```

Единственный FAIL — `EnumToString`: Enum-стаб в `MinimalRuntime.cs` без ToString,
известный давний gap (не регрессия), флипнется зелёным при порте Enum из std.

## Корни, закрытые по дороге (каждый — дизасм фолта из BOOTX64.EFI)

Формат major-9 (ILC 8) перекроил метаданные NativeAOT. Адаптировано:

1. **RTR major 8→9 accept** (`NativeAotModuleInit`): диапазон Min=8..Max=9.
2. **EEType/MethodTable major-9 layout** (`MethodTable.cs`): `_uFlags` 32-бит;
   high-16 = флаги. `HasComponentSize = (Flags & 0x8000) != 0` — НЕ старое
   `ComponentSize != 0` (major-8). Это ловило plain-объекты как массивы →
   heap corruption. `HasDispatchMap 0x0004` (заменил RelatedTypeViaIAT, убран),
   `IsGeneric 0x0200` (был 0x0400), `ElementType 0x7C00>>10`. DispatchMap теперь
   в хвосте EEType (RELPTR), а не в секции 203. Sealed-vtable / optional-fields
   tail-skip учитывают DispatchMap.
3. **GCStaticRegion RELPTR32** (`GcStaticsMaterializer`): major-9 entries —
   self-relative 4-байтные указатели, stride 4; block decode через
   `blockPtr + (rel & ~mask)`.
4. **Ленивый cctor** (`ClassConstructorRunner`): major-9 БЕЗ отдельного
   `initialized` int (тот слот — GC static base). Состояние = `cctorMethodAddress
   != 0`; runner нулит его ПОСЛЕ запуска. Старое чтение `initialized` видело
   ненулевой указатель и скипало каждый ленивый cctor → Iced/сложные cctor'ы
   не инициализировались. Фикс оживил runtime-Iced (см. ниже) и ComplexCctor.
5. **GC free-object marker HasComponentSize** (`GcSweep`) — ГЛАВНЫЙ корень всей
   net8 GC-нестабильности. Синтетический free-MT ставил `Flags = 0` → на major-9
   `HasComponentSize=false` → `ComputeSize()` free-блока = `BaseSize` (12),
   игнорит `Length`. Sweeper шагал на 16 байт в 64-байтный free-блок → в
   середину → мусорный MT → тихий хэнг в mark/sweep ЛИБО недетерминированная
   heap corruption (симптом плавал: то хэнг, то перетёртое ref-поле `h.Field=20`
   после `GC.Collect` в WriteBarrier-пробе). Фикс: `Flags = 0x8000`. Один фикс
   в источнике чинит всех потребителей `ComputeSize` (sweep, RebuildFreelist,
   TryAllocateFromFreelist split-tail). Он же разблокировал CoreCLR-tier —
   форк аллоцирует в ТУ ЖЕ kernel GcHeap.
6. **ILC 8 линкер-добавки** (`OS.csproj`): net8 по дефолту докидывает то, чего
   net7 не было — `DebuggerSupport=false` (иначе export `DotNetRuntimeDebugHeader`
   → LNK1294 под EFI_APPLICATION), `IlcDehydrate=false` (иначе frozen strings
   сжаты и рехайдрятся стоковым bootstrapper'ом, которого у нас нет → строки
   пустые), `bootstrapper.obj` exclusion, `DropExportsDef` (пустой /DEF).
7. **SystemString** field `Length` → `_stringLength` (BCL-совместимость с major-9
   string layout). `RuntimeTypeHandle/MethodHandle/FieldHandle` получили
   `_value`-поле; `LdTokenHelpers` (новый) — pointer-store хелперы.

## Прочее в этом коммите

- **probe_report.ps1** — crash-aware: детектит `HW fault`/`unhandled exception`,
  показывает `RUN CRASHED -- last output` + последнюю строку до фолта, а
  batch-пробы (шарят `nativeaot probe begin`) при краше метит `NOTRUN` вместо
  ложного `HALT`. Протухший `Cctor`-entry (detect `cctor implicit-int-field`
  ничего не печатал → вечный UNKNOWN) заменён на реальный `ExplicitCctor`.
  Новые строки: GenericDictionary, ComplexCctor, ExplicitCctor.
- **NativeAotProbe** — новые пробы: GenericDictionary (+ inst stubs, кирпич под
  делегаты), ComplexCctor (cctor через method+vcall, зеркалит Iced OpCodeHandlers).
- **Cr3Accessor** — runtime-Iced восстановлен (был legacy-байты + skip); cctor-фикс
  оживил encoder-таблицы Iced. Самый ранний runtime-Iced в boot order (Phase3),
  parity-gate против legacy — молча зелёный либо громкий `[cr3] byte mismatch`.
- **run_build.ps1** — UTF-8 блок (chcp 65001, OutputEncoding, DOTNET_CLI_UI_LANGUAGE=en)
  чтобы last_build.log читался.
- Массовая правка PAL/EH-стабов — снятие мёртвого кода / выравнивание под net8
  (в основном удаления, см. --stat: PAL-файлы «−N»).

## Проба net10 (не landed, откачено)

Флип TFM net8→net10 (ILC 10.0.x): падает на build-фазе —
`error: Expected method 'BulkMoveWithWriteBarrier' not found on type
'[OS]System.Buffer'`. ILC 10 требует новый обязательный runtime-helper
`System.Buffer.BulkMoveWithWriteBarrier` в system-module. Откачено на net8.
net9 ILC не пробован. Вывод: бамп выше net8 = порт новых обязательных хелперов
(начиная с BulkMoveWithWriteBarrier) + вероятная переверстка layout major-10/11.
Пока не нужно — net8 достаточно для делегатов/DOOM.

## Lessons

- **Корень GC-нестабильности сидел в СИНТЕТИЧЕСКОМ MT**, а не в реальных типах —
  HasComponentSize-фикс для обычных объектов его не покрыл, потому что free-marker
  строится руками в `GcSweep.EnsureInit`. Один невыставленный флаг = недетерминированный
  хэнг/corruption через ВСЕХ потребителей ComputeSize.
- **Недетерминизм GC-бага = разный layout кучи между прогонами.** Один прогон
  падал в WriteBarrier (перетёртое поле), другой висел в mark/sweep — один корень.
- **Скрипт-анализатор врал** классификацией per-probe для крашнутого run (OK у
  ранних, HALT у поздних, UNKNOWN у протухших). HALT должен быть только у пробы
  с СОБСТВЕННЫМ begin-маркером; batch-пробы при краше = NOTRUN.

## Отложено

- Temp-счётчики в `ClassConstructorRunner` (CheckCalls/CctorRuns/FirstCtx*) —
  load-bearing для ComplexCctor-пробы, оставлены; FirstCtx-capture можно снять
  следующим cleanup'ом.
- `EnumToString` FAIL — порт Enum member-name резолва из std.
- Бамп ≥ net9/net10 — по мере надобности (BulkMoveWithWriteBarrier + layout).

## Next

Делегаты: порт `System.Delegate`/`MulticastDelegate` из ILC-снапшота (кирпичи
зелёные — step129 + GenericDictionary этой сессии). Fat-pointer детектор первым.
См. donext §P0 workstream 1.
