# Sage 2 — step 5 в 6 подшагов

## Контекст

Зафиксировали refined план в `done/phase1-trycatch-roadmap.md`. Принимаем твой 11-step roadmap, твой step 5 detailed breakdown (ExInfo / PAL_LIMITED_CONTEXT / REGDISPLAY layouts, RhpThrowEx + RhpCallCatchFunclet contracts, 13-step smoke checkpoint chain). Берём все твои решения по XMM spill (оставить), kind=3 (parser bug), trailer formula (stock).

Двигаемся **последовательно**, не параллельно. Step 1 (Exception shape) скоро начнёт implementation. Прежде чем дойдём до step 5 (3-4 недели по estimate, самый сложный единый шаг), хотим максимально его декомпозировать.

Ты сам предложил:

> «следующим сообщением я разложу step 5 в ещё более приземлённый build order на 6 подшагов: какие runtime exports завести сначала, какой минимальный `AsmOffsets.cs` сделать, и какой именно shellcode sequence нужен для первого зелёного `L8_TypedCatch`».

Просим разложить.

## Что хотим получить

6 sub-steps **5.1 → 5.6**, каждый с:

1. **Что добавляется** — runtime exports / managed types / shellcode bytes / fields в `AsmOffsets.cs`.
2. **Smoke probe** — конкретное observable behaviour (либо log message который должен появиться, либо probe value которое должно вернуться, либо crash signature которая должна сменить вид). Цель: каждый sub-step имеет gate.
3. **Зависимости** — что должно быть готово (включая steps 1-4 и предыдущие sub-step'ы 5.x).
4. **Failure-localization hint** — если smoke не зелёный, в каком из предыдущих компонентов скорее всего bug.

## Минимальный AsmOffsets.cs

Поскольку в SharpOS C# и shellcode emitter лежат в одном binary, мы держим один `AsmOffsets.cs` файл (или эквивалент) с offset литералами для:

- ExInfo (поля + total size)
- PAL_LIMITED_CONTEXT (поля + total size)
- REGDISPLAY (поля + total size)
- Thread (если нужен; у нас single-thread, можно `m_pExInfoStackHead` сделать просто `static` field)

Какие именно offsets критичны для step 5 (т.е. читаются/пишутся либо managed dispatcher'ом либо shellcode'ом)? И какие можно отложить до step 7+?

## Shellcode sequence для RhpThrowEx + RhpCallCatchFunclet

В нашей экосистеме shellcode пишется как byte-array emitter в `OS/src/Boot/EH/ExceptionThunks.cs`. Patcher (по аналогии с `ByRefAssignRefPatcher` / `PortIoPatcher`) перезаписывает `[RuntimeExport]` host method bodies.

Для каждого из двух thunks хотим:

- **Точная sequence x64 instructions** (mnemonic + opcode bytes), step by step. Например для RhpThrowEx первые ~20 строк могут быть spill nonvols, build PAL_LIMITED_CONTEXT etc.
- **Какие нюансы calling convention** (rsp alignment, shadow space, callee-saved restore order) обязаны соблюдаться.
- **Где именно asm должен `int 3` halt'нуть** на ошибку (чтобы не silent fall-through).

Ориентир — `gc-experiment/dotnet-runtime/src/coreclr/nativeaot/Runtime/amd64/ExceptionHandling.asm:111` (RhpThrowEx) и `:354` (RhpCallCatchFunclet). Если можно — приведи конкретные диапазоны строк stock asm которые становятся каждым sub-step'ом нашего 6-part shellcode sequence.

## Runtime export ordering

В каком порядке заводить `[RuntimeExport]` symbols? Например:

- Сначала halt-stub `RhpEHEnumInitFromStackFrameIterator` / `RhpEHEnumNext` чтобы линковка не сломалась → потом fill in real bodies.
- Или сразу real implementation одного, halt'ы остальных?
- Какие есть circular dependency между `RhThrowEx` (managed) и asm thunks?

## Конкретные test cases внутри step 5

Какой минимальный set probe'ов нужен внутри step 5 (помимо финального `L8_TypedCatch == 801`)? Например:

- Probe который тестирует только `RhpThrowEx` build phase (PAL/ExInfo well-formed, halt после).
- Probe который тестирует только StackFrameIterator init from PAL_LIMITED_CONTEXT.
- Probe который тестирует `FindFirstPassHandler` поверх предзаготовленного fake exInfo.
- Probe который тестирует `RhpCallCatchFunclet` поверх предзаготовленного fake REGDISPLAY + IP.

Если можно изолировать каждый компонент через test seam, отладка step 5 становится дискретной.

## Honest constraints

- Single thread, single core. Нет TLS. `m_pExInfoStackHead` = static field.
- Нет debugger DAC. `_notifyDebuggerSP` можно держать null'ом.
- Нет profiler.
- Нет hijacking, нет thread suspension.
- Kernel binary single-PE — `pCallerControlPC` всегда в нашем `.text` или `.managed` секции; reverse-pinvoke / managed-native boundaries отсутствуют до Phase 6.

Эти упрощения позволяют срезать sub-step'ы по сравнению с stock NativeAOT? Или они не сильно меняют sequencing?
