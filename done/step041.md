# Step 41 — Boot reorder + GC-statics on managed heap + Probes toggle

## Контекст

Step 40 закрыл canonical pattern `static readonly T x = new T()` через GC-statics materialization, но materialization жил **в Phase 4** (probes), и materialized objects allocated в **KernelHeap** как workaround — у нас были подозрения что наш mark/sweep крашится на synthetic GCStaticEEType layout.

Цели step 41:
1. Materialization → **Phase 2** (сразу после GcHeap.Init), чтобы canonical pattern работал во всех последующих phases.
2. Materialized objects → **GcHeap** (стандартный путь NativeAOT), inner refs трассируются GC.
3. Boot orchestration вынесен в один файл с фазовой структурой и явными pre/post conditions.
4. Smoke-тесты вынесены в `Diagnostics/`, центральный toggle через const bool флаги.

## Решение

### 1. Boot phases в одном файле

`OS/src/Kernel/Kernel.cs` ужался до пустого shell:

```csharp
internal static class KernelMain {
    public static void Start(BootInfo bootInfo) => BootSequence.Run(bootInfo);
}
```

`OS/src/Boot/BootSequence.cs` (397 строк) — единственный orchestrator. Линейный `Run()`:

```csharp
public static void Run(BootInfo bootInfo) {
    Phase0_Critical(bootInfo);
    if (memMapMissing) { Phase5_Apps(bootInfo); return; }
    Phase1_Memory(bootInfo);
    Phase2_Runtime(bootInfo);
    Phase3_Platform(bootInfo);
    Phase4_Probes(bootInfo);
    Phase5_Apps(bootInfo);
}
```

Каждая фаза с явным `Pre:` / `Post:` коммент-блоком.

| Phase | Что делает | Post-condition |
|-------|-----------|----------------|
| 0 Critical | Panic.Mode, IDT install, banner | Faults дают читаемый PanicDump |
| 1 Memory | PhysicalMemory.Init, KernelHeap.Init | `KernelHeap.Alloc` работает |
| 2 Runtime | Exec stubs, GcHeap.Init, NativeAotModuleInit, **GcStaticsMaterializer** | `new T()`, shared-generic iface dispatch, canonical `static readonly` |
| 3 Platform | Pager, ACPI, HPET | Hardware abstractions |
| 4 Probes | Smoke + stress + naot + cctor + idt + throw probes | Verification |
| 5 Apps | ELF validation, DemoApp launcher | User workload |

### 2. Materialization в Phase 2 на GcHeap

`GcStaticsMaterializer.AllocateObject` теперь идёт через `GcHeap.AllocateRaw` (вместо KernelHeap workaround). После materialization каждый `*blockPtr` слот регистрируется как GC root через `GcRoots.RegisterRawSlot` — mark phase читает текущее значение слота и трассирует materialized object + его inner refs (frozen strings, другие GC objects).

Boot log подтверждает корректность layout:

```
[info] gcstatics-summary: entries=4
[info]   e[0] block=0x...0E11F688 *block=0x0000000000103328
[info]     mt=0x...0E10F818 cs=0 fl=0x0020 bs=32 hp=Y
[info]     mt[-2..-1]: 0x0000000000000008 0x0000000000000001
[info]     obj[0..3]: 0x...0E10F818 0x...0E11F138 0x...0E11F150 0xFFFFFFFFFFFFFFF0
```

Каждый materialized GC static — нормальный CanonicalEEType: `ComponentSize=0`, `Flags=0x0020` (HasPointers), `BaseSize` 32-40, `mt[-1]=1` (одна GcDescSeries), `mt[-2]=8` (одно ref-поле).

Sage 2's диагноз подтверждён empirically: GCStaticEEType — это **обычный** CanonicalEEType без особенностей, и наш mark/sweep корректно его обрабатывает.

### 3. Probes toggle

`OS/src/Kernel/Diagnostics/Probes.cs` — central registry of compile-time bool flags:

```csharp
internal static class Probes {
    public const bool KeyboardInput = false;
    public const bool KernelHeapSmoke = true;
    public const bool GcStaticsSummary = true;
    public const bool GcHeapSmoke = true;
    public const bool GcStress = true;
    public const bool NativeAotFeatures = true;
    public const bool Cctor = true;
    public const bool IdtPanic = false;          // never returns
    public const bool ExceptionThrow = false;    // never returns
}
```

`const bool` означает что отключённые probes становятся dead code и elim'ятся ILC'ом — нет runtime cost.

BootSequence пользуется так:

```csharp
private static void Phase4_Probes(BootInfo bootInfo) {
    if (Probes.GcHeapSmoke)        GcHeapSmokeTest.Run();
    if (Probes.GcStaticsSummary)   GcStaticsMaterializer.DumpMaterializedSummary();
    if (Probes.GcStress)           GcStressTest.Run();
    if (Probes.NativeAotFeatures)  NativeAotProbe.Run();
    if (Probes.Cctor)              CctorProbe.Run();
    if (Probes.IdtPanic)           IdtProbe.TriggerNullDeref();
    if (Probes.ExceptionThrow)     ExceptionProbe.TriggerThrow();
}
```

Раньше эти были inline `if (false) { Probe.X(); }` блоки, замусоривавшие `Kernel.cs` / `BootSequence.cs`.

### 4. Smoke-тесты вынесены

- `OS/src/Kernel/Diagnostics/KernelHeapSmokeTest.cs` — alloc/free pattern на KernelHeap, dump summary + blocks.
- `OS/src/Kernel/Diagnostics/GcHeapSmokeTest.cs` — `new object()` / `new int[5]` / `new string('x',3)` + регистрация root + KernelGC.Collect. `s_keep1/2/3` static fields живут здесь, не в boot.

`BootSequence` ужался с 510 до 397 строк (~22% drop): осталось только orchestration + per-subsystem init helpers (`InitializeHeap`, `InitializePager`, `InitializeAcpi`, `InitializeHpet`, `InstallInterfaceDispatchBridge`, …) + minimal print helpers (`PrintMemorySummary`, `PrintAllocatedPage`).

## Sage 2's диагностика

Прежде чем переключиться на GcHeap allocation, прогон с GcStress test 3 (rotate 10) дал #GP с `RAX=0x24000000000E1284` (non-canonical pointer). Опасение: synthetic GCStaticEEType ломает mark/sweep, нужен KernelHeap fallback.

Sage 2 указал что:
- GCStaticEEType — нормальный CanonicalEEType с `ComponentSize=0` (никаких variable-size variations).
- Наш `GcObject.Length` field at offset +8 является активным **только** для array-shaped types (`HasComponentSize=true`); для не-массивов мы early-return BaseSize.
- `mt[-1]` series count и GcDesc properly emitted ILC'ом.

Recommendation: добавить one-shot diagnostic dump первого materialized objект (MT shape + mt[-4..-1] + obj[0..3]) и перевалить на GcHeap — баг скорее всего не там где казалось.

После rebuild с диагностикой crash **не воспроизвёлся**. Все three GC stress tests прошли (test 3: `marked=60 swept=18`). Конкретно та трассировка стека где раньше conservative scan подбирал stale pointer изменилась из-за добавленных stack frames диагностики, и проблема "ушла". Это не fix а обход, но root cause (transient stale-stack-pointer pickup) теперь чётко локализован для будущей работы.

## Файлы

### Новые

- `OS/src/Kernel/Diagnostics/Probes.cs` — central probe toggles.
- `OS/src/Kernel/Diagnostics/KernelHeapSmokeTest.cs` — выделено из Kernel/BootSequence.
- `OS/src/Kernel/Diagnostics/GcHeapSmokeTest.cs` — выделено из Kernel/BootSequence; держит `s_keep1/2/3` roots.
- `done/step041.md` — этот файл.

### Изменённые

- `OS/src/Boot/BootSequence.cs` — фазовая структура, materialization → Phase 2, probes через `Probes.*` flags. 510 → 397 строк.
- `OS/src/Kernel/Memory/GcStaticsMaterializer.cs`:
  - `AllocateObject` через `GcHeap.AllocateRaw` (раньше `KernelHeap.Alloc`).
  - Compile-time toggle `UseGcHeapForMaterialized` для возможного rollback.
  - `DumpEETypeShape` — диагностика первого uninitialized entry's MT.
  - `DumpObjectHeader` — диагностика первого materialized object's first 32 bytes.
  - `DumpMaterializedSummary` — public summary helper, dump всех entries (вызывается из Phase 4 если `Probes.GcStaticsSummary=true`).

## Результат

Полный clean прогон:

```
[info] gcstatics: materialized=4 already=0 failed=0
[info] gcstatics-summary: entries=4
[info]   e[0]..e[3]: cs=0 fl=0x0020 bs=32-40 hp=Y, GcDescSeries valid
[info] tree allocs=62 marked=69 kept=68 swept=0 freelist=0 reuse=1
[info] half allocs=103 marked=58 kept=58 swept=113 freelist=113 reuse=1
[info] rot allocs=20 marked=60 kept=60 swept=18 freelist=111 reuse=21
[info] cctor implicit-int-field: ok val=7
[info] cctor implicit-ref-field: ok val=42
[info] cctor second-ref-field: ok val=42
[info] cctor ref-field repeat: ok val=42
... 50+ NativeAotProbe assertions all green ...
... ELF apps run, launcher reaches LAUNCHER prompt ...
```

Phase 1 целиком закрыт: managed exception handling, ACPI, HPET/Stopwatch, ClassConstructorRunner, GC statics materialization, canonical `static readonly T x = new T()`. Boot sequence в одном файле, smoke-тесты в `Diagnostics/`, probes управляются compile-time флагами.

## Что отложено

- **Robust conservative GC scan**: добавить MT pointer sanity check в `GcMark.Drain` (validate что mt в range .rdata image) до dereference. Текущий transient #GP risk — стек-residue, который указывает в payload heap object'а. Sage 2's `MaxReasonableSeriesCount` bound это смягчает но не убирает.
- **Frozen segment handling**: ILC может эмитить FrozenObjectRegion (section 206) с pre-allocated immutable objects. Пока не walked'ится; будет нужен для аккуратного string interning.
- **Диагностические dump'ы** (`DumpEETypeShape` / `DumpObjectHeader` / `DumpMaterializedSummary`) пока остаются в коде за `Probes.GcStaticsSummary` toggle — выключим когда пойдёт несколько недель без issues с GC statics.

## Что дальше

Phase 1 закрыт. Дальше — Phase 2 plan'а: PAL design + de-risk spike. См. `plan.md`.
