# Step 40 — Phase 1b: ClassConstructorRunner port + GC statics materialization

## Контекст

Phase 1 — последний пункт. Цель: разблокировать **canonical pattern** `static readonly T x = new T();`. Это последний открытый пункт горизонта 1 в plan.md.

**Хронология шага** была долгой и нетривиальной:
1. Research'ем NativeAOT EH (полное managed exception handling требует full unwinder ~3-6 мес).
2. Решили начать с самого тяжёлого. Step 37 закрыл "throw → readable panic". Step 40 — про cctor.
3. Первая попытка: верватим port `ClassConstructorRunner` из `Test.CoreLib`. Без `--resilient` — ILC silent игнорирует наш тип.
4. Drop `--resilient` через MSBuild target — ILC начал использовать наш runner.
5. **Recursion deadlock** на single-thread (state==2 spin) — добавил early-return.
6. После recursion fix'а — explicit cctor с complex body работает, но **canonical implicit pattern** всё ещё крашит. Crash signature `RDX=0xFFFF000000000010` указывает на unmaterialized GC statics descriptor cell.
7. Sage 2 explained: ILC's `TypePreinit` interpreter эмитит descriptor cell with EEType reloc + PreInitData blob, а runtime materialization (превратить cell в реальный GC object) делает `StartupCodeHelpers.InitializeStatics` который у нас отсутствует.
8. Port `InitializeStatics` через нашу RTR section walking infrastructure (step 32) → **canonical pattern работает** напрямую.

## Решение из трёх частей

### 1. ClassConstructorRunner port

`std/no-runtime/shared/Runtime/ClassConstructorRunner.cs` — port из `Test.CoreLib` с одним критическим изменением для single-thread.

`StaticClassConstructionContext` struct (cctorMethodAddress + initialized) и три entrypoints что ILC ожидает: `CheckStaticClassConstructionReturnGCStaticBase`, `CheckStaticClassConstructionReturnNonGCStaticBase`, `CheckStaticClassConstruction`.

**Recursion fix:** Test.CoreLib's `CheckStaticClassConstruction` имеет CAS-loop который spins при state==2 ("another thread mid-cctor"). Single-thread case: state==2 means current thread mid-cctor recursing through a helper that read state without knowing we're already inside. Spin → deadlock. Fix: early-return at state==2.

```csharp
int oldState = context.initialized;
if (oldState == 1) return;          // already done
if (oldState == 2) return;          // mid-cctor — same thread, recursive call
// First-time entry: state == 0 → claim slot, run cctor, set state = 1.
```

SMP TODO: replace with per-thread cctor execution stack.

### 2. Drop `--resilient` ILC flag

Без этого ILC silent substitutes fallback stub'ы (sentinel `0xFFFFF0000000000E`) вместо нашего runner'а — даже когда тип присутствует в OS.dll по правильному signature и `--systemmodule:OS` указывает наш module.

`OS.csproj` добавляет MSBuild target `DropResilient` который run'ится между `WriteIlcRspFileForCompilation` (создаёт `OS.ilc.rsp`) и `IlcCompile` (читает его). Removes `--resilient` line из `@(IlcArg)` и пере-эмитит файл.

```xml
<Target Name="DropResilient" AfterTargets="WriteIlcRspFileForCompilation" BeforeTargets="IlcCompile">
  <ItemGroup>
    <IlcArg Remove="--resilient" />
  </ItemGroup>
  <WriteLinesToFile File="%(ManagedBinary.IlcRspFile)" Lines="@(IlcArg)" Overwrite="true" />
</Target>
```

### 3. GC statics materialization

`OS/src/Kernel/Memory/GcStaticsMaterializer.cs` — port `StartupCodeHelpers.InitializeStatics`.

ILC для каждого type с GC static fields эмитит:
- Entry в section `ReadyToRunSectionType.GCStaticRegion` (id=201) — указатель на block.
- Block начинается с tagged qword: `(EEType_ptr | flags)`. Flags: `Uninitialized=0x1`, `HasPreInitializedData=0x2`, mask `0x3`.
- Если `HasPreInitializedData`: второй qword block'а — указатель на preInit blob (initial values для всех GC static fields этого типа).

Materialize() walks section, для каждого Uninitialized entry:
1. Decode tagged ptr: `eetype = (blockAddr & ~Mask)`.
2. Allocate object через `GcHeap.AllocateRaw(eetype->BaseSize)` + write MT pointer at offset 0.
3. If preinit data: bulk-copy `preInitBlob → (obj + 8)` for `(eetype->BaseSize - 8)` bytes.
4. Replace tagged pointer in block: `*blockPtr = (nint)obj`.

После этого `__GetGCStaticBase_*` helpers возвращают real object references, и любое `static readonly T x = new T()` работает идентично stock NativeAOT.

Использует существующую RTR section walking infra из step 32 (NativeAotModuleInit). 

Pinned-object semantics — наш GC mark-sweep без compaction, все объекты effectively pinned. Не нужен специальный `PINNED_OBJECT_HEAP` flag.

## Boot wiring

`Kernel.Start`:
```
... (banner, heap init, exec stubs, GC test, probes, pager, ACPI, HPET) ...
GcStaticsMaterializer.Materialize();   ← step 41
CctorProbe.Run();
```

**Текущее ограничение порядка:** materialization runs **поздно** в boot. Code в шагах 1-19 не может использовать `static readonly T x = new T()` patterns. Workaround сохраняется: `""` literal вместо `string.Empty` field, factory property вместо lazy reference field.

Step 42 переместит materialization сразу после `GcHeap.Init()`, что позволит canonical pattern с самого начала. См. `docs/boot-order.md`.

## Результат

```
[info] gcstatics: materialized=4 already=0 failed=0
[info] cctor implicit-int-field: ok val=7
[info] cctor implicit-ref-field: ok val=42
[info] cctor second-ref-field: ok val=42
[info] cctor ref-field repeat: ok val=42
[info] elf validation start
```

Все 4 GC static blocks (CctorProbe + другие types) materialized без сбоев. Canonical implicit pattern `static readonly Box s_default = new Box();` возвращает корректное значение (Box.Value = 42). Multiple ref fields work, repeat access работает (initialized fast path).

Все existing 58 probes остаются зелёные. Launcher функционирует.

## Что было сложно — investigation log

Несколько неправильных гипотез прошёл прежде чем нашёл root cause:

1. **"ILC игнорирует нашу ClassConstructorRunner"** — нет. После drop'а `--resilient` ILC её использует. Verified через debug log в Check function.
2. **"Inline cctor check before access site"** — нет. cctor check встроен в `__GetGCStaticBase_*` helper для lazy types. Преinitialized types получают short helper (lea+mov+ret) без check'а.
3. **"`[EagerStaticClassConstruction]` отключает preinit"** — нет. Per `PreinitializationManager.HasEagerStaticConstructor`, eager attribute не disable'ит preinit; он работает только для **не-preinitialized** типов.
4. **"`CR2 = RDX + 8` это `StaticClassConstructionContext.initialized` field"** — частично. До materialization fix'а да. После — `RDX = 0xFFFF000000000010`, lower 16 bits = `0x10` = первый GC static field offset в materialized object. То есть `[bad_object_ref + 0x10]` not `[context + 8]`.

Sage 2 (independent technical sage) дал решающий insight: ILC эмитит descriptor cell как `[EEType_tagged_ptr][PreInitData_ptr]`, и stock runtime material'изит это в `StartupCodeHelpers.InitializeStatics`. Sage 2 указал точные ILC source files (`X64ReadyToRunHelperNode.cs`, `GCStaticsNode.cs`, `NonGCStaticsNode.cs`, `PreinitializationManager.cs`, `TypePreinit.cs`, `StartupCodeHelpers.cs`). Это сделало имплементацию однозначной.

Sage 1 (independent broad sage) предложил параллельные experiments (`--map`, explicit cctor с side effects), которые мы тоже использовали. Conservative honest answer о неуверенности был полезен.

**Independent agreement** обоих мудрецов на ключевых пунктах = strong signal: `StartupCodeHelpers.InitializeStatics` port — реальный путь. Diverging hypotheses (например, sage 1's `[RuntimeExport]` mangled names guess) проверялись experiments — ILC использует hardcoded `NodeFactory.GetKnownType` (sage 2's более точный диагноз).

## Файлы

### Новые

- `std/no-runtime/shared/Runtime/ClassConstructorRunner.cs` — managed cctor runner с recursion fix.
- `OS/src/Kernel/Memory/GcStaticsMaterializer.cs` — InitializeStatics port + diagnostic walker.
- `OS/src/Kernel/Diagnostics/CctorProbe.cs` — canonical pattern verification.
- `docs/boot-order.md` — boot phase dependencies + future reorder plan.
- `done/step040.md` — этот файл.

### Изменённые

- `OS/OS.csproj` — `DropResilient` MSBuild target.
- `std/no-runtime/shared/Runtime/RuntimeAttributes.cs` — `IsVolatile` marker type для `volatile int` fields.
- `std/no-runtime/shared/Threading.cs` — `Interlocked.MemoryBarrier()` stub.
- `OS/src/Kernel/Kernel.cs` — `GcStaticsMaterializer.Materialize()` call + `CctorProbe.Run()`.
- `docs/nativeaot-nostdlib-limits.md` — section §1 переписан с "❌ Lazy static reference field" на "✅ РАБОТАЕТ".

## Что дальше

**Step 42** — boot reorder + canonical migration:

1. Переместить `GcStaticsMaterializer.Materialize()` сразу после `GcHeap.Init()`, force'ить `NativeAotModuleInit` ранний init (без lazy).
2. Глобально заменить `""` literal на `string.Empty` field accesses в std/.
3. Mass-migrate factory properties (`public static T Default => new T()`) на canonical `static readonly T Default = new T()`.
4. Update `nativeaot-nostdlib-limits.md` — снять текущее "ограничение порядка" upgrade-к-fully-supported.

Это закроет Phase 1 целиком и приведёт codebase к BCL-canonical state. После Phase 1 → Phase 2 (PAL design + de-risk spike).

Phase 1 уже работает на 95% — последний refactor finalize'ит canonical adoption.
