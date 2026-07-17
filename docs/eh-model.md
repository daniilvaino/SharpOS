# Модель исключений SharpOS

> **Обновление:** PE-миграция апп состоялась — Tier B (ELF, halt-on-throw)
> исчез; freestanding PE-приложения шарят Tier A EH-движок ядра через
> AppServiceTable-handoff (RhpThrowEx + per-image .pdata registry,
> multi-image function table). Секции ниже, описывающие Tier B и
> «PE-миграцию» в будущем времени, — исторический контекст.


Статическое описание EH-стеков проекта: что есть, как устроено, где
лежит. Для живых статусов фич / open issues — см.
[`coreclr-hosted-limits.md`](coreclr-hosted-limits.md) (§0, §11) и
[`nativeaot-nostd-kernel-limits.md`](nativeaot-nostd-kernel-limits.md).
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
  generic. **После step103c** msc-throws (`0xE06D7363` от
  `COMPlusThrow*`) больше НЕ оборачиваются в SEHException — раскрываются
  в исходный specific тип (`InvalidCastException`,
  `EntryPointNotFoundException`, ...). SEHException остаётся только для
  редких raw SEH без managed-throwable.

### Применённые локальные фиксы

Локальные пластыри в кадрах-консьюмерах размотчика, не в самом
размотчике:

- `CallCatchFunclet`: save/restore RBP вокруг funclet-вызова —
  чинит resume для **native-origin** throws (`COMPlusThrow`-путь);
  симптом был «string-as-MethodTable».
- `GetStackSlot` (GC): RBP-фикс при чтении managed-кадра во время
  pause/scan + рост гостевого стека `128 KiB → 16 MiB` (для reflection
  / `System.Text.Json`).

### Жизненный путь исключения в Tier C

Tier C сложнее A потому что у CoreCLR **два уровня типов** (native C++ Exception
hierarchy + managed BCL `System.Exception` hierarchy), **четыре кода SEH**
которыми могут прийти, и **два пути конвертации** между ними. Без этой
карты мы постоянно ошибаемся какой указатель что значит и где
ищется message.

#### Native (C++) иерархия в форке (`vm/clrex.h`, `inc/ex.h`)

```
Exception                         базовый PAL-класс
└── CLRException                  c_type='CLR ' (0x434C5220) — несёт m_throwableHandle
    ├── EEException               c_type='EE  ' (0x45452020) — несёт m_kind: RuntimeExceptionKind
    │   ├── EEMessageException    + m_hr, m_resID, m_arg1..6 (InlineSString/SString)
    │   ├── EEResourceException   + m_resourceName
    │   ├── EECOMException        + m_ED (ExceptionData)
    │   ├── EEFieldException      + pFD/szDemangledMessage
    │   ├── EEMethodException     + pMD/szDemangledMessage
    │   ├── EEArgumentException   + m_argumentName/m_resourceName
    │   ├── EETypeLoadException   + класс/namespace/assembly
    │   └── EEFileLoadException   + path/m_hr
    ├── CLRLastThrownObjectException  bridge для случая "throwable уже в Thread::LastThrownObject"
    ├── ObjrefException           обёртка вокруг OBJECTREF
    └── EHRangeTreeNode...
SEHException                      несёт m_exception: EXCEPTION_RECORD (raw SEH запись)
HRException                       несёт m_hr; родитель COMException и т.д.
OutOfMemoryException              отдельный prealloc'нутый singleton
```

`new EEMessageException(kInvalidCastException, IDS_EE_INVALIDCAST_FROM_TO,
W("Foo"), W("Bar"))` — это **C++ объект**, который кидается через
`PAL_CPP_THROW` (= MSVC `throw <ptr>;`). В EXCEPTION_RECORD попадает
указатель на него (см. §«msc throw layout» ниже).

#### Managed (BCL) иерархия

`System.Exception` → `SystemException` → `InvalidCastException` /
`CultureNotFoundException` / `EntryPointNotFoundException` / etc. Это
то, что **видит managed user code** в `catch (T)`. **Не путать** с C++
типом: один `EEMessageException(m_kind = kInvalidCastException)`
**конструирует** managed `InvalidCastException` через
`CoreLibBinder::GetException(m_kind)` → `AllocateObject(pMT)` →
`CallDefaultConstructor` → `SetMessage(...)`. См. блок «CreateThrowable».

#### Коды SEH с которыми приходит exception

| Код | Источник | Где throwable | Кто конвертирует в managed |
|---|---|---|---|
| `EXCEPTION_COMPLUS` (`0xE0434352`) | managed-side `throw` из JIT-кода (`RaiseTheExceptionInternalOnly`) | заранее в `Thread::LastThrownObject` | `case EXCEPTION_COMPLUS` в обоих местах ниже — просто `return pThread->LastThrownObject()` |
| `MSC_EXCEPTION` (`0xE06D7363`) | native-side `throw new EEMessageException(...)` через `PAL_CPP_THROW` (т.е. **любой `COMPlusThrow*` из C++**) | сам бросаемый C++ объект, доступный через `ExceptionInformation[1]` (см. layout) | **step103-fix**: distinct deref + cast в `CLRException`/`EEException` → `GetThrowable()`. Без фикса попадал в default `MapWin32FaultToCOMPlusException` → kSEHException ⇒ потеря типа. |
| `STATUS_ACCESS_VIOLATION` etc. | HW-fault, dereference NULL | нет; синтезируется | `MapWin32FaultToCOMPlusException` → kNullReferenceException/kAccessViolationException + `EEException::CreateThrowable` |
| `STATUS_STACK_OVERFLOW`, `STATUS_NO_MEMORY` | OOM/SO | preallocated singletons (`GetBestOutOfMemoryException`/`GetPreallocatedStackOverflowException`) | special-cases в `CreateCOMPlusExceptionObject` |

#### msc throw layout (для `0xE06D7363`)

```
ExceptionInformation[0] = magic       (0x19930520 / 0x19930521 / 0x19930522)
ExceptionInformation[1] = void**      ── адрес слота хранящего бросаемый указатель
                                         для `throw new T(...)` (тип throw-выражения — pointer)
                                         **Нужен один extra deref** чтобы получить сам объект:
                                            Exception* pE = *(Exception**)ExceptionInformation[1];
                                         (для `throw T(...)` без new — слот хранит сам value, один deref
                                          и так не нужен; в CoreCLR практически всегда `throw new`)
ExceptionInformation[2] = ThrowInfo*  ── RTTI: CatchableTypeArray с mangled именами иерархии
ExceptionInformation[3] = imageBase   ── RVA-база для RTTI
```

**Ловушка**: ранее `step103` пытался читать `(Exception*)einfo[1]` напрямую
— получал «vtable» которая на самом деле адрес объекта, виртуальный
вызов халтил. Двойной deref решает (step103c, 2026-05-22).

#### Точки конвертации native → managed

Два места, оба надо держать в синхроне:

1. **`CLRException::GetThrowableFromException(Exception* pE)`**
   ([`clrex.cpp:586`](../dotnet-runtime-sharpos/src/coreclr/vm/clrex.cpp)).
   Принимает C++ Exception*. Используется когда вокруг — `EX_CATCH`
   и есть инстанс. Идёт по веткам `IsType(CLRException)`/
   `IsType(EEException)`/`IsType(SEHException)`. Для SEHException
   ветка `case EXCEPTION_COMPLUS` возвращает LastThrownObject;
   step103-fix добавляет **msc-throw recovery** перед switch'ем.

2. **`CreateCOMPlusExceptionObject(Thread*, EXCEPTION_RECORD*, BOOL)`**
   ([`excep.cpp:5567`](../dotnet-runtime-sharpos/src/coreclr/vm/excep.cpp)).
   Принимает raw EXCEPTION_RECORD. Используется когда мы внутри
   personality routine и есть только запись (`ExInfo::CreateThrowable`,
   `exceptionhandling.cpp:809`). По умолчанию идёт
   `MapWin32FaultToCOMPlusException` → строит `EEException(kind)`.
   step103-fix добавляет ту же msc-throw recovery в начале для кода
   `0xE06D7363`.

Не дублировать оригинальный C++ объект; **только взять managed throwable** —
он уже корректно построен (`m_kind`, `m_hr`, args) и его `GetThrowable()`
вернёт managed object с правильным MethodTable.

#### CreateThrowable (где формируется managed exception)

`EEException::CreateThrowable()` ([`clrex.cpp:998`](../dotnet-runtime-sharpos/src/coreclr/vm/clrex.cpp)):

```
pMT = CoreLibBinder::GetException(m_kind)        // RuntimeExceptionKind → MethodTable*
throwable = AllocateObject(pMT)                  // managed alloc
CallDefaultConstructor(throwable)
throwable.SetHResult(GetHR())                    // virtual override
SString msg;
if (GetThrowableMessage(msg))                    // ← КЛЮЧЕВАЯ точка
    throwable.SetMessage(NewString(msg))
```

`GetThrowableMessage` — **virtual**, перегружен в каждом
EEException-производном:

| Класс | Что делает |
|---|---|
| `EEMessageException` | Если `m_resID != 0` → `GetResourceMessage` → `StackSString::LoadResource(CCompRC::Error, m_resID)` → **mscorrc.dll** lookup (PE-resource). Иначе fallback на `EEException::GetThrowableMessage`. |
| `EEResourceException` | `LoadString(m_resourceName)` из сборки указанной в m_resourceAssembly. |
| `EEException` (base, line 945) | fallback: пустой message, BCL-конструктор положит default ("Specified cast is not valid", etc. — берётся из managed-side в `Exception` constructor). |
| `EEArgumentException`, `EEMethodException`, ... | свои источники (m_argumentName + resource lookup). |

**Где живут messages:**
- **mscorrc.dll** (CoreCLR shared resource DLL) — большинство templates,
  ключатся по UINT `m_resID` (см. `corerror.h`/`mscorrc.rc`). У нас
  **НЕ сбандлена** → `LoadResource` → throw → recursion → fatal
  `TerminateProcess(COR_E_EXECUTIONENGINE)`. Это поверхность Regex/Path/
  Reflection probes, см. `coreclr-hosted-limits.md`.
- **mscorlib (System.Private.CoreLib)** — defaults из `Exception(string?)`
  конструктора по умолчанию (когда `SetMessage(NULL)`). Текст вшит в
  `SR.resx` → managed RVA → ловится pinned strings. Этот путь не
  требует mscorrc.
- **System.Globalization.dll** (в invariant mode) — `CultureNotFoundException`
  собирает свой message сам ("Only the invariant culture is supported in
  globalization-invariant mode... ru-ru is an invalid culture identifier")
  не дёргая `EEException::CreateThrowable`. Поэтому она у нас работает
  без mscorrc.

**Следствие step103c**: до фикса все msc-throws оборачивались как
SEHException → `EEException::GetThrowableMessage` для kSEHException не
шёл в mscorrc → безопасно. После фикса корректный m_kind открывает
правильный resource path → mscorrc cascade на отсутствие. **Не
регрессия EH-машины, а смежная PAL-проблема (mscorrc bundle).**
Закрывается либо bundling mscorrc, либо guard'ом в
`GetThrowableMessage` (`EX_TRY` + пустой message на failure).

#### Диаграмма (минимальный путь для `throw new EEMessageException(kInvalidCast, ...)`)

```
managed code: (Bar)foo
   │
   ▼ JIT CORINFO_HELP_CHKCASTANY → CastHelpers.cpp
ThrowInvalidCastException()
   │
   ▼ COMPlusThrow(kInvalidCastException, IDS_EE_CANNOTCASTSOURCE_TO_DEST, srcN, dstN)
EX_THROW(EEMessageException, (kInvalidCastException, IDS_..., srcN, dstN))
   │
   ▼ PAL_CPP_THROW = MSVC `throw <ptr>;`
RaiseException(code=0xE06D7363, args=[magic, &pObj, pThrowInfo, imgBase])
   │  args[1] = void** (адрес локального слота с указателем)
   │
   ▼ наш SehDispatch.cs:RaiseExceptionImpl
   ├── search pass — walk frames через personalities
   │       ProcessCLRException вызывается на JIT-кадрах
   │           └── ExInfo::CreateThrowable
   │                   └── CreateCOMPlusExceptionObject ← step103c фикс ЗДЕСЬ
   │                          │ exceptionCode == 0xE06D7363, double-deref einfo[1]:
   │                          │   pE = *(Exception**)einfo[1];
   │                          │   if (pE->IsType(EEException)) return ((EEException*)pE)->GetThrowable();
   │                          │   → CreateThrowable()
   │                          │       → AllocateObject(InvalidCastException MT)
   │                          │       → SetHResult(COR_E_INVALIDCAST)
   │                          │       → GetThrowableMessage(): m_resID != 0 →
   │                          │           LoadResource(CCompRC::Error, m_resID)
   │                          │           ↘ mscorrc.dll: not bundled → fatal cascade
   │                          │             (для CultureNotFound managed-side даёт message
   │                          │              сам, mscorrc lookup не нужен — работает)
   │                          └── на success: managed throwable пинится в Thread::ThrowableObject
   ├── unwind pass — выполняет destructors + jump в catch funclet
   ▼
managed user code: catch (InvalidCastException ex) { ... }   ← теперь СРАБАТЫВАЕТ
```

#### Сводка: что почему ломалось/чинилось в Tier C на пути msc-throw

| Симптом | Когда | Корень | Где фикс |
|---|---|---|---|
| `string-as-MethodTable` halt | step 71 | `ExInfo::UpdateNonvolatileRegisters` затирал RBP resume-контекста | `SehUnwind.CallCatchFunclet` save/restore RBP |
| `catch (T)` мимо, ловится только `(SEHException)` | до step103c | `CreateCOMPlusExceptionObject` для 0xE06D7363 шёл в default `MapWin32FaultToCOMPlusException` → kSEHException | excep.cpp + clrex.cpp double-deref msc recovery |
| Сначала «vtable» с мусором, halt на `IsType` | step103b | пытались `(Exception*)einfo[1]` без extra deref — `einfo[1]` это `void**`, не сам объект | step103c: один лишний deref |
| `TerminateProcess(COR_E_EXECUTIONENGINE)` на Regex/Path probes | после step103c | `EEMessageException::GetThrowableMessage` дёргает mscorrc.dll → not bundled → throw в `CreateThrowable` → cascade | TBD: либо bundle mscorrc, либо `EX_TRY` в `GetThrowableMessage` с пустым результатом |

#### Открытые вопросы (после step103c)

- **mscorrc bundle vs guard.** Решить какой подход. Guard минимально-инвазивный (~5 строк EX_TRY), bundle честный.
- **Frame-types ещё не покрытые в TryActivateFrameChain.** `HelperMethodFrame`, `TransitionFrame`, `UMThunk*` — пока не триггерятся, но если потребуется — добавление ветки по `frameId`.
- **threading cohort hard-panic.** `new Thread().Start()`, `ThreadPool`, `Task.Run`, `Timer` — phase E (threading-PAL), не EH-вопрос.

---

### EH-фронтир §11 — закрыт (step 90) ✅

Был **единственный** non-EH-баг класс — managed-to-unmanaged
transition stubs (P/Invoke / reflection invoke / helper-method
transitions). CoreCLR'овский `CallDescrWorkerInternal` (handwritten
asm в `vm/amd64/CallDescrWorkerAMD64.asm`) и аналоги пушат на стек
`Frame*` указатель в место retaddr'а — настоящий continuation
context живёт **в `Frame*`-объекте** (per-thread linked list, head
в `Thread::m_pFrame`), не в стеке. Наш walker наивно читал `*rsp`
→ попадал на этот `Frame*` адрес → fail IsValidIp → HALT,
никакого user-catch'а не достигалось.

**Fix (step 90)** — `SehDispatch.TryActivateFrameChain(Context*)`:
читает `Thread::m_pFrame` через два EXTERN_C helper'а в форке
(`SharpOSHost_GetCurrentFrame` / `SharpOSHost_SetCurrentFrame`); для
активного `InlinedCallFrame` overrid'ит ctx из его полей
(`m_pCallerReturnAddress`/`m_pCallSiteSP`/`m_pCalleeSavedFP`).
Hook'и в обоих walker'ах — search-pass `DispatchException` и
unwind-pass `RtlUnwind` — на месте bail'а по `IsValidIp` пробуют
FrameChain skip до halt'а. Анти-реактивация — `CallSiteSP > Rsp`
guard, pop делает personality routine во время unwind pass
(`CleanUpForSecondPass`). Frame layout в форке — без vtable,
ID-based dispatch.

**Не threading** — singleton `Thread*` + `Frame*`-семейство
struct'ов, без scheduler'а / TLS / SwitchToThread (это всё
Phase E, остаётся отложенным).

**Покрыто**: `InlinedCallFrame` (P/Invoke / reflection invoke /
helper-method transitions). Census-cohort Socket / OpenSSL /
P/Invoke trap'ы — catchable. **После step103c** ловятся как
specific тип (`EntryPointNotFoundException`, `InvalidCastException`
и т.д.), а не общий `SEHException` — см. секцию «Жизненный путь
исключения в Tier C» ниже.
**Не покрыто** (если потребуется в будущем): `HelperMethodFrame`,
`TransitionFrame`, `UMThunk*` и т.д. — добавление ветки в
TryActivateFrameChain по `frameId`. В текущем census не триггерятся.

**Остаётся как настоящий HARD-PANIC** (другой класс, **не** §11):
threading cohort — `new Thread().Start()`, `ThreadPool`, `Task.Run`,
`Timer`, `Thread.Sleep`. Эти идут через direct `SharpOSHost_Panic`
в `SleepEx`/`SwitchToThread`-стабах **без** поднятия C-SEH —
walker даже не запускается. Это Phase E (threading-PAL), не EH.

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
- [`nativeaot-nostd-kernel-limits.md`](nativeaot-nostd-kernel-limits.md) —
  лимиты NativeAOT+NoStdLib (Tier A и Tier B base).
- [`boot-order.md`](boot-order.md) — порядок установки EH-патчеров в
  Phase 0/1 ядра.
- [`Probes.cs`](../OS/src/Kernel/Diagnostics/Probes.cs) — гейты, через
  которые покрытие Tier A верифицировано.
