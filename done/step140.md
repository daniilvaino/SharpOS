# step 140 — исключения в freestanding PE-аппах через multi-image function-table (батарея 20/20)

## Контекст

step139 закрыл interface dispatch в аппах через handoff (шаринг kernel-моста).
Следующий фронт к DOOM — исключения. Аппы были Tier-B (halt-on-throw): нет типа
`System.Exception`, ThrowHelpers = `while(true)`, EH-машинерия kernel-only. Цель —
дать аппам `throw`/`catch`, шаря kernel EH-движок, а не переписывая его.

## Результат

**Батарея AotTests 20/20**, из них 6 EH-кейсов зелёные на аппе: `throw`/`catch`,
`try/finally`, finally-on-unwind (finally при размотке → внешний catch), catch-by-base
(throw derived / catch `Exception`), `e.Message` round-trip, multi-catch select.
Трасса подтверждает весь путь: app `throw` → kernel `RhpThrowEx` → `DispatchEx`
идёт по кадрам аппы (ControlPC=0x40xxxx), резолвит апп-clause'ы (type=0x405xxx в
образе аппы), матчит по app-MT, гоняет апп catch/finally funclet'ы. **Ноль регрессий
ядра** (census OK=145, EH 16/16, launcher exitCode=21) весь рефактор.

## Корень: managed EH-путь был single-image

Разведка вскрыла: релевантный движок — `OS.Boot.EH.*` (managed NativeAOT:
`RhpThrowEx`→`RhpTest_ThrowIngress`→`DispatchEx`), и весь его lookup зашит на **один
образ** через синглтон `CoffRuntimeFunctionTable` (`s_imageBase`/`s_records` = kernel).
`FindRecordIndex(appIp)` → -1 (RVA аппы от 0x400000 вне kernel-диапазона) → walk
останавливался. `~25` мест в 12 файлах читают `CoffRuntimeFunctionTable.ImageBase`,
но три разных потребителя: managed-EH (нужен image-aware), SEH-движок (свой multi-
image реестр, только kernel/JIT/R2R), kernel-only (GC precise walk, диагностика —
всегда kernel-образ). Ключ — **хирургия, не «всё multi-image»**.

## Реализация (4 инкремента, сборка между каждым)

**Инкремент 1 — аддитивный реестр** (`CoffRuntimeFunctionTable.cs`): fixed-capacity
value-struct extra-образов (no cctor trap) + `RegisterImage`/`UnregisterImage`/
`TryResolvePc`/`ImageBaseForRecord`. Kernel = image 0, старая API (`ImageBase`/
`Records`/`Count`/`GetRecord`) **не тронута** — все kernel-only и SEH-потребители
работают как раньше. Регрессия невозможна by construction.

**Инкремент 2 — `CoffMethodLookup` image-aware**: `TryFindMethod`→`TryResolvePc`,
`MethodInfo` несёт `ImageBase`/`ImageRecords`/`ImageCount` (индексы image-local),
`ReadUnwindBlockFlags`→`ImageBaseForRecord(rf)`, `WalkToRootInImage` (funclet→ROOT в
пределах образа). Старые `FindRecordIndex`/`WalkToRoot` (kernel-only) оставлены для
внешних вызывающих (EhProbe/SehUnwind).

**Инкремент 3 — потребители на per-image base**: `CoffEhDecoder` (`EhEnumInit`/
`TryFindFuncletProtectedOffset`→`info.ImageBase`; `EhEnumNext` type-RVA→`state.ImageBase`
через новое поле `EHEnum.ImageBase`), `StackFrameIterator`, `CoffMethodGcInfo` — все
на `info.ImageBase`.

**Инвариант регрессии (инкременты 1-3):** extras=0 → `TryResolvePc`/`ImageBaseForRecord`
резолвят kernel image 0 идентично старому пути. Ядро EH 16/16 держалось всю дорогу.

**Инкремент 4 — apps throw/catch**:
- `PeLoader.TryLoad` парсит data-dir #3 (exception directory) из mapped-заголовка →
  `RegisterImage(imageBase, imageBase+pdataRva, pdataSize/12)`. `UnregisterImage` в
  `UnmapMappedRange` (ElfValidation + AppServiceBuilder), keyed на base (no-op для
  стека/ELF).
- **`RhpThrowEx` handoff**: `AppServiceTable.RhpThrowExAddress` = kernel
  `ThrowExStub.GetMethodAddress()`; апп `ThrowExTrampoline` патчит свой
  `[RuntimeExport("RhpThrowEx")]` стаб абсолютным `mov rax,<addr>; jmp rax` (tail-jmp
  сохраняет throw-site return address, который kernel-shellcode читает из `[rsp]`).
  Патчится в `AppRuntime.Initialize`.
- `System.Exception` + `Exceptions.Derived.cs` в app Compile list (самодостаточны;
  никаких новых deps — как проверка перед сборкой).
- 6 EH-тестов в AotTests.

## Почему handoff, а не порт (как и диспач)

Матч catch-типа (`IsAssignableFromClass`) — pure MT-identity + `GetBaseType`, работает
на app-MT (тот же ILC8/major-9 layout в shared-адресном). `RhpThrowEx`-shellcode
читает `ExInfoHead.s_head` (kernel-глобал, не per-thread) — ок в shared-адресном при
single-thread ExInfo. Апп исполняется **внутри адресного пространства ядра**, так что
шарит движок целиком; своя работа только — сделать function-table multi-image.

## Файлы

Kernel: `OS/src/Boot/EH/{CoffRuntimeFunctionTable,CoffMethodLookup,CoffEhDecoder,
StackFrameIterator,CoffMethodGcInfo}.cs`, `OS/src/Kernel/Pe/PeLoader.cs`,
`OS/src/Kernel/Elf/ElfValidation.cs`, `OS/src/Kernel/Process/{AppServiceBuilder,
AppServiceTable}.cs`.
App: `apps_native/sdk/{ThrowExTrampoline.cs(new),AppServiceTable.cs,AppRuntime.cs,
FreestandingPe.props}`, `apps_native/AotTests/Program.cs`.
Docs: `docs/nativeaot-nostd-kernel-limits.md` (§8 PE-app tier — throw/catch ✅).

## Откладываем

- `RhpRethrow` handoff (`throw;` в catch) — тот же паттерн, +1 стаб/поле.
- `RhpThrowHwEx` — HW-fault→managed в аппе (в kernel-AOT tier это halt-стаб).
- Rich stack-trace: `AppendStackFrame` аллоцирует `IntPtr[]` в kernel-heap во время
  app-throw → cross-heap ref в app-объекте (латентно; caught+discard не триггерит).
- Конкурентный throw kernel↔app (единый `ExInfoHead.s_head`) — single-thread ExInfo.
- Stage B (удаление kernel ELF), апгрейд лаунчера на новый std.
