# step 139 — interface dispatch в аппах через handoff (батарея 14/14)

## Контекст

step138 добавил тест-батарею app-рантайма, которая вскрыла границу: freestanding
PE-апп нёс минимальный рантайм (GC + прямые вызовы), но `List.Contains`/
`Dictionary.TryGetValue`/`EqualityComparer<int>` падали (11/14) — всё через
`IEquatable<T>`-dispatch, которого у аппа не было. Цель step139: закрыть interface
dispatch в аппах, как первый шаг «apps share the kernel runtime» (к DOOM).

## Результат

**Батарея 14/14.** Три dispatch-FAIL'а → ok. Аппа теперь делает value-type
interface dispatch, шаря kernel-мост через адрес в service table. Handoff-паттерн
доказан — он же переиспользуется для EH.

## Разворот: handoff, а не port/export-table

Разведка по kernel-коду дала ключевой факт: `InterfaceDispatchResolver` **пурный на
major-9** — резолвит целиком из instance-MT + cell (rel32, всё внутри образа),
никакого kernel-глобального type-состояния (кэш per-cell, `NativeAotModuleInit`-guard
вестигиален на major-9). Значит тот же resolver корректно ходит по **foreign
app-MT** (тот же ILC8/major-9 layout). Плюс апп исполняется **внутри адресного
пространства ядра** (PeLoader мапит на 0x400000 в kernel CR3).

Отсюда — НЕ портируем машинерию в аппу (тянет exec-буфер, BootAsm.Generator,
диагностику) и НЕ строим export table (ядро экспортит 0 символов под EFI). Вместо
этого **ядро отдаёт адрес уже построенного bridge-шеллкода**, а апп в него
трамплинит. Ничего не легло в `std/shared`.

## Два корня (оба чинились)

**Корень 1 — dispatch-мост:** аппа не имела рабочего `RhpInitialDynamicInterfaceDispatch`
(ILC-дефолт инертный). Фикс — handoff:
- Ядро: `AppServiceTable.InterfaceDispatchBridgeAddress` заполняется
  `InterfaceDispatchBridge.ShellcodeStart` в `AppServiceBuilder` (безусловно,
  ABI-agnostic raw-адрес).
- Аппа: `InterfaceDispatchTrampoline` несёт свой `[RuntimeExport("RhpInitialDynamicInterfaceDispatch")]`
  стаб + 12-байтный **абсолютный** patcher `mov rax,<bridge>; jmp rax` (rel32 не
  дотянет: 0x400000 → высокий kernel-адрес > int32). Вызывается в
  `AppRuntime.Initialize` **до** `GcHeap.Init` (окно до патча = fallback-halt).
- Поток: ILC-cell → app-стаб (пропатчен → jmp kernel-шеллкод) → шеллкод читает
  cell аппы (r10) → kernel-resolver ходит по MT аппы → tail-jmp в метод аппы.

**Корень 2 — примитивы голые:** после фикса №1 батарея НЕ изменилась (тот же
11/14). Улика: батарея допечатала до конца (не зависла) → dispatch не доходил до
стаба. Причина: `apps_native/sdk/MinimalRuntime.cs` объявлял примитивы как
`struct Int32 { }` — без интерфейсов. Значит `int is IEquatable<int>` = **false**
→ `DefaultComparer.Equals` уходил в fallthrough `x.Equals(y)` (object.Equals) →
неверный ответ, dispatch не звался. Фикс — примитивы аппы (Boolean..UInt64)
реализуют `IEquatable<T>`/`IComparable<T>` с `_value` backing field, зеркало
`OS/src/Boot/MinimalRuntime.cs`. После этого `x is IEquatable<int>` = true →
dispatch реально задействуется → мост работает → 14/14.

Урок: два независимых корня под одним симптомом; «результат не изменился после
фикса» = фикс не на критическом пути, копать что раньше по потоку.

## Файлы

- `OS/src/Kernel/Process/AppServiceTable.cs` — поле `InterfaceDispatchBridgeAddress`.
- `OS/src/Kernel/Process/AppServiceBuilder.cs` — заполнение.
- `apps_native/sdk/InterfaceDispatchTrampoline.cs` (new) — стаб + абсолютный patcher.
- `apps_native/sdk/AppServiceTable.cs` — зеркальное поле.
- `apps_native/sdk/AppRuntime.cs` — вызов patcher'а до GcHeap.Init.
- `apps_native/sdk/MinimalRuntime.cs` — примитивы → IEquatable/IComparable.
- `apps_native/sdk/FreestandingPe.props` — trampoline в Compile list.
- `build_launcher.ps1` — cosmetics («Built:» вместо «Built freestanding PE launcher:»).
- `docs/nativeaot-nostd-kernel-limits.md` — §8 PE-app tier обновлён (dispatch ✅).

## Следующий фронт — EH в аппах (разведка сделана)

Тот же handoff + одна kernel-работа. managed-EH (`RhpThrowEx`→`RhpTest_ThrowIngress`→
`DispatchEx`) шарится handoff'ом (адреса `RhpThrowEx`/funclet-энтри в service table;
`ExInfoHead.s_head` — kernel-глобал, ок при single-thread ExInfo). Матч catch-типа
(`IsAssignableFromClass`) уже pure/app-safe. **Блокер** — managed function-table
**single-image**: `CoffRuntimeFunctionTable` зашит на kernel-образ, весь путь
(`CoffMethodLookup`/`CoffEhDecoder`/`StackFrameIterator`, ~8 мест читают `ImageBase`)
знает один образ; кадры аппы (RVA от 0x400000) не находятся (`FindRecordIndex`→-1).

Работа: multi-image реестр `(imageBase, records, count)` (шаблон — уже существующий
`SehUnwind.s_stat`/`RegisterStaticFunctionTable` из SEH-движка), протянуть resolved
per-PC base через те ~8 мест; `PeLoader` читает app data-dir #3 и регистрирует
`(0x400000, +pdataRva, count)`; `System.Exception` (+ derived) в app Compile list.
Детектор пройден: AotTests.exe несёт `.pdata` (61 RUNTIME_FUNCTION запись).

## Откладываем

- EH (следующий step) — большой, за пределы этой сессии.
- Stage B: удаление kernel ELF-кода.
- Апгрейд лаунчера на новый std.
