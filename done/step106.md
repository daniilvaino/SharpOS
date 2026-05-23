# Step 106 — HW AV через PAL SEH dispatcher

## Корень

HW fault'ы (`#GP` vec=13 / `#PF` vec=14) шли через `HwFaultBridge.OnFault` →
`DispatchEx.Dispatch`. `DispatchEx` использует **только** `CoffEhDecoder` —
NativeAOT EH format. C++ frames со standard MSVC `__C_specific_handler`
personality (стопкa CoreCLR runtime'а: `Object::ValidateInner`,
`MT::Validate`, `EX_TRY/EX_CATCH` блоки с `AVInRuntimeImplOkayHolder` и т.д.)
получали `ehInit=N` → silently пропускались. До C++ `EX_CATCH` блоков никогда
не доходили → unhandled exception → halt.

При этом в PAL (`SehDispatch.cs`) `__C_specific_handler` **уже был
реализован** для software throws (managed `RaiseException` path). Просто
не вызывался из HW-fault path.

## Фикс

Подключить существующую PAL SEH machinery к HW-fault path. Новый internal
entry `SehDispatch.DispatchFromHwFault(ExceptionRecord*, Context*)` принимает
явный fault context (не делает `CaptureCurrentContext + UnwindOneFrame` как
обычный `RaiseException`), сразу зовёт `DispatchException` — walks frames с
`__C_specific_handler` для C++, `ProcessCLRException` для JIT.

- `SehDispatch.cs` — добавлен `internal static void DispatchFromHwFault(...)`, ~5 строк
- `HwFaultBridge.cs` — для vec=13/14 строит `ExceptionRecord` (code=`STATUS_ACCESS_VIOLATION` 0xC0000005, `ExceptionAddress=Rip`, `ExceptionInformation` хранит write/exec/CR2), `Context` (17 GPRs + RIP + segments + EFlags) из `InterruptFrame`, вызывает `DispatchFromHwFault`. Если handler не нашёлся — fall through на текущий `DispatchEx.Dispatch` (старый fallback для NativeAOT null-deref'ов сохранён)

Static `s_hwRec` / `s_hwCtx` в `HwFaultBridge` — не зависят от GcHeap state
(который может быть corrupted в момент fault'а). Single-threaded fault
handling = re-entry не страшно. Доступ через `fixed (ExceptionRecord* rec =
&s_hwRec)` — C# require pin scope для address-of static field, но zero-cost
под non-moving GC.

## Эффект

Defensive AVs внутри CoreCLR runtime'а теперь правильно обрабатываются.
В логе видна полная цепочка:

```
HW fault: vec=13 RIP=0x... (наш kernel #GP handler)
   ↓
SehDispatch.DispatchFromHwFault (новый PAL entry)
   ↓
__C_specific_handler walks C++ frames через EX_TRY  ✅
   ↓
Exception propagates вверх через C++ → ProcessCLRException на JIT frame
   ↓
System.AccessViolationException propagates через user managed stack
   ↓
Доходит до user code (Program.<>c.<<Main>$>b__0_49)
   ↓
Нет catch (AccessViolationException) → unhandled
   ↓
CoreCLR's "Fatal error" reporter печатает FULL managed stack trace 🎉
```

Это **proper .NET behaviour** для unhandled AV в managed code — на native Windows
было бы то же самое.

## Pillar status

[project_jit_frame_seh_unwind_pillar](../C:/Users/админимтратор/.claude/projects/c--work-OS/memory/project_jit_frame_seh_unwind_pillar.md) закрыт на ~80%. Остаётся:

- **Tail unwind**: когда handler нигде не найден — `RtlUnwind target frame not reached → PANIC`. Должен быть clean exit. Малая правка, отдельный шаг.
- **Finally/Filter funclets** через PAL SEH — пока не проверены
- **Nested AVs** — поведение неизвестно

## Frontier-D2 переориентирован

Видна реальная цепочка для Regex.IsMatch (которой не было видно до step106
потому что corruption замаскировывала её):

```
Regex.IsMatch → RegexCache.GetOrAdd → Regex..ctor → RegexParser.Parse →
RegexNode.ReduceSet → RegexCharClass.IsEmpty(String) →
GetGCStaticBaseSlow(MethodTable*) → InitClassSlow → AV
```

`pMT=0x500009970008` указывает **внутрь JIT stub heap** (4 MiB reservation
на 0x500009970000). Первые 8 байт — это **JIT-emitted machine code bytes**,
случайно совпавшие с UTF-16 "Rtor". JIT-generated code передаёт thunk
address как MT pointer в GC static base helper. CoreCLR-side JIT/QCall bug.
Открытый investigation — step107.
