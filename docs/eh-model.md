# Модель исключений SharpOS

Статическое описание EH-стеков проекта: что есть, как устроено, где
лежит. Для живых статусов фич / open issues — см.
[`coreclr-hosted-limits.md`](coreclr-hosted-limits.md) (§0, §11) и
[`nativeaot-nostdlib-limits.md`](nativeaot-nostdlib-limits.md).
Для порядка boot-патчей EH-стабов — [`boot-order.md`](boot-order.md).

## Тиры

В системе **два настоящих EH-стека** плюс один переходный halt-stub:

| Тир | Где | EH-машинерия | Образ | Покрытие |
|---|---|---|---|---|
| **A** AOT EH | ядро сегодня; апп после PE-миграции | свой (`OS/src/Boot/EH/`) | PE/COFF | `try/catch/finally/throw`, фильтры, rethrow, HW-fault, multi-frame, StackTrace |
| **B** ELF-апп | временно | **нет** (halt-on-throw) | ELF (linux-x64) | — |
| **C** CoreCLR-форк | гостевой стоковый .NET | свой (стоковый CLR + патчи) | в PE-образе ядра, статически | то же что A + BCL/PAL-bridges; один открытый путь (§11) |

Tier A и Tier C **не пересекаются** по конструкции: у CoreCLR свой
dispatcher, свой JIT-EH, свой MethodTable-layout, свой module-layout,
свой PAL. Tier B исчезнет когда апп переедут на PE — см. ниже.

---

## A. AOT EH-стек (PE/NativeAOT)

Единственный «свой» написанный EH в проекте. Используется ядром;
после PE-миграции апп станет shared между ядром и пользовательскими
PE-образами.

### Где лежит

[`OS/src/Boot/EH/`](../OS/src/Boot/EH/), ~20 файлов:

| Компонент | Файл | Что |
|---|---|---|
| Throw ingress | `ThrowExStub` + `ThrowExPatcher` | шеллкод-стаб для `RhpThrowEx`: строит `PAL_LIMITED_CONTEXT` + `ExInfo`, tail-call в managed |
| Context capture | `CaptureContextStub/Patcher` | `RhpCaptureContext` |
| Funclet-трамплины | `CallCatch/Finally/Filter FuncletStub+Patcher` | вызов catch / finally / filter funclet'ов |
| Rethrow | `RethrowStub/Patcher` | `throw;` |
| Диспетчер | `DispatchEx.cs` | главный raise-driver, search + unwind passes |
| Frame walker | `StackFrameIterator.cs`, `RegDisplay.cs` | порт CoreCLR'овских SFI / REGDISPLAY |
| State on stack | `ExInfo.cs`, `PalLimitedContext.cs` | stack-builds для dispatcher |
| EH-клаузы | `CoffEhDecoder.cs`, `VarInt.cs` | декодер ILC ehInfoRVA-blob |
| RIP→метод | `CoffMethodLookup.cs`, `CoffRuntimeFunctionTable.cs` | парсер PE `.pdata` |
| HW→managed | `HwFaultBridge.cs` | IDT-trap → managed `catch` |
| Classlib | `ExceptionHooks.cs` | `GetRuntimeException`/`FailFast`/etc. |

### От чего зависит

1. **PE/COFF в памяти.** [`CoffRuntimeFunctionTable.cs`](../OS/src/Boot/EH/CoffRuntimeFunctionTable.cs)
   ищет PE-сигнатуру `0x00004550`, Optional Header → DataDirectory →
   `.pdata` RVA, и работает с записями `RuntimeFunction { BeginAddress,
   EndAddress, UnwindInfoAddress }`.
2. **ILC ehInfoRVA-blob.** ILC при компиляции эмитит per-method
   varint-кодированные EH-клаузы, на которые ссылается `.xdata`/`.pdata`.
   `CoffEhDecoder` их разбирает.
3. **Boot-time patcher pipeline.** 9 шеллкод-стабов (`*Stub` пары)
   патчатся при boot ([`BootSequence.cs:347-417`](../OS/src/Boot/BootSequence.cs#L347))
   потому что у CoreCLR-style runtime helpers нестандартный calling
   convention, который нельзя выразить через `[UnmanagedCallersOnly]`.
4. **IDT.** `HwFaultBridge` опирается на kernel-mode владение IDT —
   маппит трапы (#PF и т.п.) в `RhpCallCatchFunclet`.

### Покрытие (верифицировано пробами)

Гейты в [`Probes.cs`](../OS/src/Kernel/Diagnostics/Probes.cs#L37-L56),
все зелёные:

| Гейт | L | Что доказывает |
|---|---|---|
| EhTryCatchWithThrow | 801 | `try/catch/throw` — реальный dispatch + funclet |
| EhRethrowChain | 901 | `throw;` поднимает к внешнему catch |
| EhTryCatchFinally | 111 | finally исполняется до catch |
| EhFilter | 1101 | `catch when (...)` |
| EhHwFault | 3 | null-deref → managed catch |
| EhStackTrace | 1401 | `Exception.StackTrace` заполнен |
| EhCollidedUnwind | 1501 | rethrow внутри finally (funclet-aware codeOffset) |
| EhMultiFrameFinally | 1616 | finally caller'а на throw callee |
| EhMultiFrameStackTrace | 1700+ | трейс содержит каждый кадр |

### Что не умеет

- **Cross-image walk.** `Coff*Lookup` сейчас обслуживает один image base
  (само ядро). Чтобы кадры из разных PE-образов проходились в одной
  раскрутке, нужен per-image lookup (registry of `(ImageBase, .pdata)`)
  и cross-image MethodTable identity (отдельные ILC-компиляции имеют
  свои MT-объекты для одного и того же типа). Решаемо, но до появления
  PE-апп с реальным `try/catch` не сделано.
- **HW-fault мост — kernel-only.** User-mode код IDT не владеет;
  получать `#PF → catch` в апп не сможет (это scope, не баг).
- **`IF` в catch funclet после HW-fault.** IRQ-флаг переносится в
  funclet выключенным; до следующего `IRETQ` прерывания не доставляются
  (комментарий в [`HwFaultBridge.cs:27-33`](../OS/src/Boot/EH/HwFaultBridge.cs#L27)).
- **Дисциплина ядра.** Production-пути ядра используют `Halt()` /
  `Panic.Fail()` для «не должно случиться» — реальный `throw new` живёт
  только в `ExceptionEngine.cs` (engine сам) и двух пробах (`EhProbe`,
  `ExceptionProbe`). Политика, не лимит инфраструктуры.

---

## B. ELF-апп (переходный)

**EH нет.** `apps/sdk/MinimalRuntime.cs:264-287` определяет `ThrowHelpers`
как halt-набор:

```csharp
public static void ThrowNullReferenceException() { while (true) ; }
public static void ThrowInvalidProgramException() { while (true) ; }
// ...14 штук
```

В апп-csproj ноль ссылок на `OS/src/Boot/EH/`; в `apps/**/*.cs` ноль
`catch (`. 29 `throw new` в `apps/`+`std/` живут в портированном BCL-коде
(`StringBuilder`, коллекции) — на текущих апп-кодопутях не триггерятся;
если триггернутся — halt.

ELF — тестовая площадка (ILC через WSL/Ubuntu, `linux-x64` RID),
не целевой формат для апп. См. секцию **PE-миграция** ниже.

---

## C. CoreCLR-форк

Стоковый `dotnet-runtime-sharpos`, статически слинкован в kernel image.
Свой раздельный EH-стек по построению.

### Где лежит

| Компонент | Файл | Что |
|---|---|---|
| Raise driver | [`SehDispatch.cs`](../OS/src/PAL/SharpOSHost/SehDispatch.cs) | `_CxxThrowException` → `captureContext` → frame walk с personality routines → second pass → jump в catch |
| Frame chain walker | [`SehUnwind.cs`](../OS/src/PAL/SharpOSHost/SehUnwind.cs) | C#-порт `RtlVirtualUnwind` (частичный — см. §11) |
| GC stack helpers | `CrtAndEhStubs.cs`, `SystemNativeStubs.cs` | bridges для GcInfoDecoder, `GetStackSlot` |

Также: managed try/catch внутри гостевого стока — обычный JIT-EH
стокового CoreCLR; мы предоставляем только PAL.

### Что работает (см. [`coreclr-hosted-limits.md §0`](coreclr-hosted-limits.md))

- `try/catch/finally/throw`, фильтры, rethrow, multi-frame
- native-origin (`COMPlusThrow`) → managed catch
- HW-fault → managed catch (по поддерживаемым векторам)
- BCL-исключения (`FileNotFound`, `CultureNotFound`, `ZLibException`,
  `OutOfMemoryException`) — **ловятся**; крах самого вызова — это
  PAL-stub, не EH-баг
- `SEHException` («External component has thrown an exception») —
  ловимое; PAL trap-stub поднял native SEH, доехал до managed как
  generic

### Применённые локальные фиксы

Локальные пластыри в кадрах-консьюмерах размотчика, не в самом
размотчике:

- `CallCatchFunclet`: save/restore RBP вокруг funclet-вызова —
  чинит resume для **native-origin** throws (`COMPlusThrow`-путь);
  симптом был «string-as-MethodTable».
- `GetStackSlot` (GC): RBP-фикс при чтении managed-кадра во время
  pause/scan + рост гостевого стека `128 KiB → 16 MiB` (для reflection
  / `System.Text.Json`).

### Что не работает — единственный EH-фронтир (§11)

Симптом одинаковый во всех 💥-случаях (Socket / OpenSSL / threading):
```
[seh] invalid Rip=0x<стек-адрес> — stop walk
[SehDispatch] no handler matched — HALT
[PANIC] unhandled exception
```

**Root cause** (выявлен в step-89, см. `done/step080.md`): walker
**корректно** разматывает все стандартные кадры по UNWIND_INFO — это
проверено per-opcode трейсом. Точка отказа — кадр, чьи байты
(`53 56 55 48 8B EC 48 8B D9 8B 4B 08`…) и custom personality
`CallDescrWorkerUnwindFrameChainHandler` однозначно идентифицируют
его как **`CallDescrWorkerInternal`** (handwritten asm trampoline
для managed-to-unmanaged transitions, `src/coreclr/vm/amd64/Call-
DescrWorkerAMD64.asm`).

Через **stub-mechanism** CoreCLR (P/Invoke, reflection invoke,
helper-method transitions) НЕ использует normal `call`/`ret` —
вместо retaddr на стек пушится указатель на `Frame*` структуру
(per-thread linked list, head в `Thread::m_pFrame`). Настоящий
continuation context живёт **в `Frame*`-объекте**, не в стек-слоте.

Наш walker наивно читает `*rsp` → попадает на self-referential
`Frame*` указатель (= стековый адрес) → fails IsValidIp → HALT.

**Это не «port `RtlVirtualUnwind`» и не «PE/Win64 dispatcher
protocol»** — гипотезы опровергнуты в step-89. Реальный fix —
**интеграция walker'а с CoreCLR's `Thread::m_pFrame`**:

1. Распознавать stub-frame по personality address.
2. Дёрнуть `GetThread()->m_pFrame`, вызвать его `UpdateRegDisplay()`.
3. Восстановить настоящий caller's Rip/Rsp/Rbp, поп'нуть Frame*.

Это **не threading** — нужна *структура* `Thread*`+`Frame*` (singleton),
не scheduler. `GetThread()` в форке уже работает; `m_pFrame`
CoreCLR ведёт сам. Gap — только читалка. Оценка: 1-2 недели.

Step-71/step-72 — локальные пластыри для **двух других** классов
кадров (native-origin RBP-clobber в `CallCatchFunclet` /
`GetStackSlot`); они закрывают свои сценарии и останутся.

---

## PE-миграция апп: что меняется в EH

Когда апп переедут с ELF на PE/NativeAOT (та же UEFI/PE ABI, что и у
ядра), Tier B исчезает. Шарить инфраструктуру Tier A с PE-апп станет
почти бесплатно:

| Препятствие | Сейчас (ELF) | После PE | Что нужно сделать |
|---|---|---|---|
| Формат образа | PE vs ELF | PE/PE | — |
| ILC EH-метадата | `.eh_frame`+LSDA vs `.pdata`+ehInfoRVA | одна | — |
| Patcher pipeline | апп без патчей | патчер дёргается из ELF/PE-loader'а до jump'а | install runtime-helper стабы на апп-образ |
| Cross-image lookup | один image base | per-image registry в `Coff*Lookup` | `(ImageBase, .pdata-table)` map по RIP |
| MethodTable identity | каждая ILC своя | каждая ILC своя | unify по qualified-name **или** shared type-universe |
| HW-fault мост | n/a | остаётся kernel-only | scope-приемлемо |

То есть EH-объединение Tier A+B **не оправдывает** PE-миграцию само по
себе (миграция оправдывается единообразием образа и ABI), но при ней
оно получается «в подарок» когда консьюмер появится — апп, который
реально захочет `try/catch`.

---

## Источники истины

- **Этот файл** — статическая модель EH.
- [`coreclr-hosted-limits.md`](coreclr-hosted-limits.md) — живой реестр
  что работает / не работает в Tier C, включая §11.
- [`nativeaot-nostdlib-limits.md`](nativeaot-nostdlib-limits.md) —
  лимиты NativeAOT+NoStdLib (Tier A и Tier B base).
- [`boot-order.md`](boot-order.md) — порядок установки EH-патчеров в
  Phase 0/1 ядра.
- [`Probes.cs`](../OS/src/Kernel/Diagnostics/Probes.cs) — гейты, через
  которые покрытие Tier A верифицировано.
