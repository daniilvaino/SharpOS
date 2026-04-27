# Phase 1 last item — managed try/catch/finally в SharpOS

## Контекст

SharpOS — экспериментальный unikernel целиком на C# (NativeAOT + NoStdLib + UEFI PE, win-x64, single-thread, single-core). 24-36 мес roadmap до Roslyn REPL внутри ОС. Phase 1 закрыт почти целиком; остался единственный пункт — managed try/catch/finally. Plan.md помечает этот пункт «2-6 месяцев, samый рискованный, fallback на longjmp-only milestone разрешён».

## Что мы знаем достоверно

### Текущий бинарь (kernel, win-x64 EFI PE)

- 703 RUNTIME_FUNCTION в `.pdata` (size 0x20F4 bytes, 12 bytes per record).
- `.xdata` упакован в `.rdata` (стандарт MSVC PE).
- Section list: `.text`, `.unbox`, unnamed#3 (0x1CA6D bytes — судя по размеру, EH/methodtable blob), `.rdata` (0xFF7D), `.data`, `.pdata`, unnamed#7 (8 bytes), `.rsrc`, `.reloc`.
- Unwind codes наблюдаемые: `PUSH_NONVOL` (rbx/rbp/rsi/rdi/r12-r15), `ALLOC_SMALL` (≤128 байт). Других codes пока не видели в наших 703 методах.
- Format подтверждён согласно `nativeaot/Runtime/windows/CoffNativeCodeManager.cpp:62-92, 98-167, 841-926`: standard UNWIND_INFO + NativeAOT trailer (`unwindBlockFlags` byte + optional `dataRVA` + optional `ehInfoRVA`), затем varint GCInfo. EH-clause blob по ehInfoRVA: varint-encoded `(nClauses; (tryStart, (tryLen<<2)|kind, handlerOff, [typeRVA|filterOff])*)`.

### ILC уже эмитит EH info

- `try { JumpStub.Run(...) } finally { ProcessManager.ClearCurrent(); }` живёт в production код [`OS/src/Kernel/Elf/ElfValidation.cs:225-237`], исполняется на каждом app launch — **работает**, no halt.
- Probe три уровня под compile-time toggles в `Probes.cs`:
  - **L1 try/finally no-throw** — вычисляемое тело + finally bump. Зелёное: `val=211`.
  - **L2 try/catch no-throw** — try/catch без throw. Зелёное: `val=4`.
  - **L3 try/catch с throw new InvalidOperationException("ehprobe")** — currently halts в `RhpThrowEx`, default off.
- Вывод: ILC уже эмитит `.pdata`/`.xdata` + EH clauses корректно для всех `try` блоков. Frame layout правильный. Runtime никогда не вызывает funclet thunks на no-throw path. Проблема — только actual throw → catch.

### Что у нас есть (managed-side, EH stubs)

`OS/src/Boot/ExceptionEngine.cs` (155 строк):

- `RhpThrowEx(object)` — печатает type+message читая `_message` field по hardcoded offset +8, halt-spin loop. Не возвращает.
- `RhpRethrow(object)` — то же самое.
- `RhpThrowHwEx(uint exceptionCode, IntPtr faultingIP)` — halt-stub. Currently не invoked (IDT goes through PanicDump напрямую).
- `GetRuntimeException(int id)` — returns generic `new Exception("runtime exception (id=" + id + ")")`.
- `FailFast(uint reason, Exception, IntPtr, IntPtr)` — print + halt.
- `AppendExceptionStackFrame(object, IntPtr, int flags)` — no-op.
- `OnFirstChanceException(object)` — no-op.
- `OnUnhandledException(object)` — no-op.

### Что отсутствует

- **6 asm-only thunks**: `RhpCallCatchFunclet` / `RhpCallFinallyFunclet` / `RhpCallFilterFunclet` (есть RhpThrowEx/Rethrow/HwEx как halt-stubs).
- **Managed dispatcher**: `DispatchEx`, `StackFrameIterator`, EH-clause varint decoder, `FindFirstPassHandler`, `InvokeSecondPass`, `ShouldTypedClauseCatchThisException`, `RhpEHEnumInit`/`RhpEHEnumNext`, `RhpSfiInit`/`RhpSfiNext`.
- **Coff `.pdata` walker**: backward-walk до `UBF_FUNC_KIND_ROOT` для funclet→parent linkage.
- **`Exception` shape**: сейчас 1 поле (`_message` only) в `std/no-runtime/shared/Threading.cs:87`. Нужно полный 10-field 88-byte BCL layout (`_message`, `_data`, `_innerException`, `_helpURL`, `_source`, `_HResult`, `_stackTraceString`, `_remoteStackTraceString`, `_corDbgStackTrace`, `_idxFirstFreeStackTraceEntry`).
- **Производные exception types**: отсутствуют `NullReferenceException`, `OverflowException`, `DivideByZeroException`, `InvalidCastException`, `ArrayTypeMismatchException`, `NotImplementedException`. Roslyn-emitted code, который ловит/throw'ит эти, currently не binds.

### Architectural инварианты которые ограничивают подход

1. **Single-source C#**: нет `.c`/`.cpp`/`.h`/`.asm`/`.s` файлов в репо. Низкоуровневая работа решается одним из трёх:
   - C# intrinsics (`[RuntimeExport]`, `[UnmanagedCallersOnly]`, `delegate* unmanaged`, `fixed`, unsafe pointer math).
   - **Byte-array shellcode** — C# emitter пишет машинные инструкции байт-за-байтом в exec-stub buffer (UEFI EfiLoaderCode). Уже работает: `InterfaceDispatchBridge` (195 байт), `ByRefAssignRefPatcher` (15 байт), `Cr3Accessor`, `GcStackSpill`, `JumpStub`, `PortIoPatcher` (step 42).
   - Build-time codegen в PowerShell для линкер-обязательных C-ABI символов.
2. **Naming discipline**: канонические `System.*` namespaces только для fully BCL-compat реализаций.
3. **Single-thread, single-core**. Нет SMP, нет TLS, нет thread-hijacking, нет debugger DAC.
4. **Runtime под firmware CR3** на OVMF (kernel .text mapped RWX → patcher паттерн работает). Real HW с W^X — TODO для post-EBS, не блокирует Phase 1.

### Reference paths в нашем snapshot'е dotnet/runtime

`gc-experiment/dotnet-runtime/src/coreclr/nativeaot/`:

- `Runtime/amd64/ExceptionHandling.asm:111` — RhpThrowEx (NESTED_ENTRY, spills все callee-saved + XMM6-15)
- `Runtime/amd64/ExceptionHandling.asm:354` — RhpCallCatchFunclet (mov rsp, r8 ; jmp rax — non-local return)
- `Runtime/amd64/ExceptionHandling.asm:530` — RhpCallFinallyFunclet (re-spill nonvols обратно после возврата)
- `Runtime/amd64/ExceptionHandling.asm:657` — RhpCallFilterFunclet (только RBP restored)
- `Runtime.Base/src/System/Runtime/ExceptionHandling.cs:554-572` — RhThrowEx
- `Runtime.Base/src/System/Runtime/ExceptionHandling.cs:591-705` — DispatchEx (two-pass)
- `Runtime.Base/src/System/Runtime/ExceptionHandling.cs:738-817` — FindFirstPassHandler
- `Runtime.Base/src/System/Runtime/ExceptionHandling.cs:853-922` — InvokeSecondPass
- `Runtime/windows/CoffNativeCodeManager.cpp:271-283` — backward-walk до ROOT
- `Runtime/windows/CoffNativeCodeManager.cpp:841-926` — EHEnumInit/EHEnumNext varint decoder
- `Runtime/EHHelpers.cpp:472` — RhpVectoredExceptionHandler (Windows hardware-fault bridge)
- `nativeaot/System.Private.CoreLib/src/System/Exception.NativeAot.cs:33-55` — authoritative field offsets
- `Test.CoreLib/src/System/RuntimeExceptionHelpers.cs` — 127 строк, classlib контракт template (минимальный пример)

## Вопросы

### Strategy / scope

1. **Какой из 4 вариантов выбрать с учётом roadmap 24-36 мес до Roslyn REPL?**
   - **A — full unwinder**: port из NativeAOT (6 asm thunks + ~935 LoC managed dispatcher + Coff `.pdata` walker + EH varint decoder + полный `Exception` shape + 6 missing derived types). Bit-exact NativeAOT semantics. Estimate 2-6 мес. Phase 1 закрывается полностью.
   - **B — longjmp-only milestone**: setjmp/longjmp pair shellcode, catch = matching jump target в той же функции. No stack walk, no funclet calling, no cross-function unwind. Finally НЕ работает на BCL-imported код который assumes proper finally semantics. Estimate 1-2 недели. Phase 1 формально не закрыт, но coding model unblock'ается.
   - **C — hybrid**: B сейчас (быстрое разблокирование), A позже когда понадобится для scheduler/threading или CoreCLR-host integration.
   - **D — defer**: skip try/catch до Phase 6 (CoreCLR fork приносит свой EH). Phase 2 (PAL design) и Phase 3 (scheduler/threading) пишутся без try/catch — manual error returns везде.

2. **Насколько Option D realistic** в практике? Конкретно: scheduler/threading code (Phase 3 plan) — насколько он страдает без try/finally cleanup paths vs работает с manual error handling? Опыт реальных kernels?

3. **MVP scope для Option A**: первый зелёный ход — это `try { throw } catch (TypedException)` ONLY, или сразу нужно finally? Filter clauses (`catch (E e) when (...)`) и fault clauses можем отложить до v2?

4. **Имеет ли смысл split на milestones**: Phase 1.5a (bare try/catch с typed clauses), Phase 1.5b (finally), Phase 1.5c (filter+fault+collided unwind+hardware-fault bridge), и считать «Phase 1 done» уже после 1.5b?

### Sequencing / implementation tactics

5. **Asm-thunks-first vs managed-dispatcher-first**: писать 6 byte-array shellcode thunks ПЕРВЫМИ (с halt'ящим dispatcher) или managed dispatcher с stub-thunks (которые halt'ят при попытке вызвать funclet)? Какой подход быстрее convergence — где раньше получаем работающий end-to-end путь для smoke testing?

6. **Существует ли минимальный path-of-least-resistance**, при котором мы пропускаем StackFrameIterator целиком? Например: ограничить try/catch одной функцией (no cross-function unwind), тогда `RhpThrowEx` мог бы просто scan'ить current function's `.pdata` запись и jump в catch funclet без stack walk. Полезно для kernel-internal try/catch (где не пересекаем function boundaries часто)?

### Technical depth — runtime layout

7. **PAL_LIMITED_CONTEXT minimal subset**: NativeAOT asm `RhpThrowEx` спилит XMM6-XMM15. Наш kernel не использует SSE/FP code (UEFI environment, нет FP в .text). Можем ли opt out из XMM spill полностью, или NativeAOT runtime ЧИТАЕТ XMM regs во время dispatch (например, через REGDISPLAY)?

8. **REGDISPLAY decoder**: managed-side StackFrameIterator должен интерпретировать unwind codes чтобы derive nonvols текущего frame'а. Минимальный subset codes который покрывает ILC-generated frames: мы видим только `PUSH_NONVOL` + `ALLOC_SMALL ≤128` в нашем 703-function binary. Достаточно ли стартовать с decoder на эти два? `SAVE_NONVOL`, `ALLOC_LARGE`, `SET_FPREG`, `SAVE_XMM128` — нужны или ILC их не эмитит?

9. **Collided unwind**: throw-from-finally case. Насколько критично для Phase 1 MVP? Есть ли в BCL-ported коде (List/Dict/Stack/Queue/HashSet/SortedList/ROCollection — наш repo) реальные сценарии где throws происходят INSIDE finally?

10. **Hardware-fault → managed exception** (#PF → NullReferenceException через RhpThrowHwEx): defer до try/catch baseline? Сейчас IDT идёт через PanicDump — нет необходимости в managed-bridging пока не появится `catch (NullReferenceException)` где-то.

11. **`ehInfoRVA` blob location**: на нашем binary `dumpbin /HEADERS` показывает unnamed section #3 (0x1CA6D bytes, между `.unbox` и `.rdata`) и unnamed #7 (8 bytes). Подходит ли #3 на роль NativeAOT EH/MT blob, или ehInfoRVA указывает в `.rdata`? Где в ILC source это решается (мы знаем что в нашем repo snapshot'е `tools/aot/` отсутствует — есть ли способ верифицировать без полного upstream pull)?

### Exception object shape

12. **Минимальный `Exception` field set** для PHASE 1 MVP — нужно ли сразу делать полный 88-byte BCL layout или хватит `_message` + `_innerException` + `_HResult` (24 bytes)? Какой из неполных layout'ов сломается на типичном Roslyn-codegen для `throw new X("msg") + catch (Exception e) { e.Message }`?

13. **`_corDbgStackTrace` + `_idxFirstFreeStackTraceEntry`**: единственные fields которые NativeAOT runtime EH dispatch ЧИТАЕТ/ПИШЕТ напрямую (через `AppendExceptionStackFrame` → `AppendStackIP`). Нужен ли их сразу в MVP или можем отложить (приняв что StackTrace всегда `null`)?

14. **Production patterns**: реалистично ли в первой итерации Exception **без** `StackTrace` (всегда `null`), без `Data`, без `HelpLink`, без `Source`, оставить только `Message` + `InnerException` + `HResult`? Что в BCL-imported коде сломается?

### Roadmap / risk

15. **Failure modes которые мы ещё не предвидим**? Что обычно ловит людей которые порт'ют NativeAOT EH — какие edge cases bit'ят на вторую неделю работы?

16. **Альтернативные impl** — есть ли open-source kernels на C# (Cosmos, FlingOS, MOSA) или на других managed languages, которые решали эту задачу и от чьего опыта можно унаследовать конкретные tricks?
