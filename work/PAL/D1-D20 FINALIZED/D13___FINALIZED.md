### Решение

**Reuse and extend Phase 1 .pdata unwinder для CoreCLR coverage.** Не строить новую unwind инфраструктуру — у SharpOS уже есть рабочий `.pdata`-based unwinder в Phase 1 (`OS/src/Boot/EH/`). D13 это **extension** существующей инфраструктуры под shape that CoreCLR JIT эмитит.

`RtlVirtualUnwind` ≈ `StackFrameIterator.UnwindOneFrame` — мудрец 2 подтвердил аналогию через прямое чтение кода SharpOS.

### Архитектура

```
managed throw в guest CoreCLR
  ↓
CoreCLR vm/ ExceptionTracker
  ↓
CoreCLR PAL (наш форк)
  ↓
TARGET_SHARPOS branch на virtual unwind boundary
  ↓
SharpOS .pdata unwinder (extension Phase 1 StackFrameIterator)
  ↓
RUNTIME_FUNCTION lookup → UNWIND_INFO → unwind codes
  ↓
restored SP / nonvol register locations / caller IP
```

Та же conceptual machinery что Phase 1 — расширенная под new frame shapes.

### Что есть в Phase 1 уже (verified)

**Реализовано (4 UWOP кода)**:

- `UWOP_PUSH_NONVOL` (0)
- `UWOP_ALLOC_LARGE` (1)
- `UWOP_ALLOC_SMALL` (2)
- `UWOP_SET_FPREG` (3)

**Supporting infrastructure**:

- `StackFrameIterator.cs` — RtlVirtualUnwind equivalent
- `CoffRuntimeFunctionTable.cs` — RUNTIME_FUNCTION lookup
- `CoffMethodLookup.cs` — IP → RUNTIME_FUNCTION, funclet → root method walk
- `CoffEhDecoder.cs` — NativeAOT EH clause enum (typed/fault-finally/filter)
- `PalLimitedContext.cs` — CONTEXT subset
- `RegDisplay.cs` — KNONVOLATILE_CONTEXT_POINTERS analog
- `DispatchEx.cs` — full managed EH flow (first/second pass, typed catch matching, filter invocation, finally/fault, catch funclet transfer)
- `HwFaultBridge.cs` — CPU fault → managed exception (#PF → NRE, #GP → AVE, #DE → DivByZero)

То есть **полноценная managed EH machine уже работает в Phase 1**.

**Не реализовано**:

- `UWOP_SAVE_NONVOL` (4)
- `UWOP_SAVE_XMM128` (8)
- `UWOP_PUSH_MACHFRAME` (10)
- FAR variants
- Chained unwind info
- Возможно epilog handling

### Coverage scope

**TBD** после Windows spike measurements. Конкретный gap unknown пока не запустим dumper и не соберём реальные UNWIND_INFO образцы из CoreCLR JIT и native frames.

**Decision tree после measurements**:

- Только supported UWOP встречается → Phase 1 unwinder готов почти полностью
- - SAVE_NONVOL → разумное расширение (одно добавление в switch)
- - SAVE_XMM128 / chained / machframe → классифицировать (где встречается, можно ли избежать в early phase)
- Много новых типов → fallback на Microsoft portable amd64 unwinder

### Fallback

**Microsoft portable amd64 unwinder** (`src/coreclr/unwinder/amd64/unwinder.cpp`, 1847 LOC) — копия Windows kernel `exdsptch.c`. Self-contained C++ что читает .pdata без libunwind. Уже линкуется в libcoreclr на Unix через `unwinder_wks` library.

Если расширение Phase 1 unwinder'а оказывается слишком большим — переключаемся на portable Microsoft unwinder. Он handles полный set UWOP кодов из коробки.

### Strict rules

**`RtlVirtualUnwind` Windows API** — допустим **только** как diagnostic oracle:

cpp

```cpp
#ifdef SHARPOS_COMPARE_WITH_WINDOWS_UNWINDER
    // Compare our unwinder result with Windows native
    // For validation/debugging only
#endif
```

**Запрещено** в production path. Иначе spike замаскирует D13 риск (mixed stack unwind через собственный mechanism), и на bare metal обнаружим что зависели от Windows API которого там нет.

### Что украдено

|Артефакт|Источник|Использование|
|---|---|---|
|Phase 1 .pdata unwinder|`OS/src/Boot/EH/` (наша existing infrastructure)|Conceptual core, расширяется|
|Microsoft portable amd64 unwinder|`dotnet/runtime src/coreclr/unwinder/amd64/unwinder.cpp` (1847 LOC, MIT)|Fallback если Phase 1 extension недостаточно|
|UWOP code definitions|Microsoft documentation + CoreCLR source|Reference для new UWOP реализаций|

### Что наше

- Расширения `StackFrameIterator.cs` под new UWOP кодов
- TARGET_SHARPOS branch в `pal/src/exception/seh-unwind.cpp` (узкое изменение, не rewrite)
- Bridge между CoreCLR PAL и Phase 1 unwinder через TARGET_SHARPOS conditional

### Mixed stack risk

**Идентифицирован**: managed JIT frames + native CoreCLR C++ frames + PAL frames должны unwind через тот же mechanism. Не verified что portable Windows unwinder обработает все frame types.

**Mitigation**: Windows spike включает dumper который захватывает реальные frames через mixed managed/native traces. Анализ покажет:

- Какие UWOP реально встречаются
- Есть ли chained unwind info
- Как выглядят funclet frames
- Как выглядят native CoreCLR C++ frames
- Достаточно ли PalLimitedContext или нужно XSTATE/AVX/XMM сохранение

После measurements — finalize coverage scope.

### Связь с другими decisions

- **D2** (TLS): unwinder использует thread state для CONTEXT, инфраструктура из Phase 5.5
- **D4** (catch-all): managed exception flow defined в DispatchEx.cs (Phase 1)
- **D9** (структура pal/sharpos/): unwind code в `pal/sharpos/exception/` domain file
- **D10** (статическая линковка): unwinder code линкуется статически с CoreCLR в один artifact
- **D11** (extern "C"): pal/sharpos/ ↔ Phase 1 unwinder integration через прямые вызовы

### Принципы установленные D13

(в дополнение к D1-D11)

29. **Reuse существующую инфраструктуру где возможно.** Phase 1 уже имеет working .pdata unwinder. D13 — extension не reinvention.
30. **Hidden masking dangerous.** Hosted spike (любая host platform) может незаметно подтянуть платформенные сервисы (libunwind, libstdc++, RtlVirtualUnwind). Audit links/imports обязателен.
31. **Oracle vs implementation distinction.** Платформенные APIs (RtlVirtualUnwind на Windows) допустимы как diagnostic oracles для validation, **не** как production implementation. Иначе spike маскирует production риски.
32. **Coverage gap measurement before scope decision.** Не финализируем точный scope расширения до того как increased реальные данные о frame shapes которые встречаются. Avoid premature decisions based on theoretical concerns.