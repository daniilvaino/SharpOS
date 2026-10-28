# CLAUDE.md

## Что это за проект

SharpOS — **experimental unikernel целиком на C#**, собирается как `NativeAOT + NoStdLib`, таргетит `UEFI` (`EFI_APPLICATION`). Это **не** обычное `dotnet console`. Точка входа — `EfiMain` через `[RuntimeExport]`. Кода с `EFI_SYSTEM_TABLE*`, `unsafe`, ручными указателями много, но он локализован в ABI-слоях.

## Архитектурные инварианты

Жёсткие правила. Любая задача должна укладываться в них. Если кажется что не укладывается — переформулируй.

### Инвариант 1 — C# is the only source language

**В дереве исходников нет ни одного `.c`, `.cpp`, `.h`, `.asm`, `.s` файла.** Каждая low-level задача решается одним из трёх:

1. **C# intrinsics** — `[RuntimeExport]`, `[UnmanagedCallersOnly]`, `delegate* unmanaged`, `fixed`, unsafe pointer arithmetic.
2. **Byte-array shellcode** — C# emitter пишет машинные инструкции байт-за-байтом в exec-stub buffer, аллоцированный UEFI через `AllocatePool(EfiLoaderCode)` (гарантированно исполнимый даже под W^X). Паттерн: `[RuntimeExport]` managed wrapper с Panic.Fail fallback, + patcher перезаписывает первые N байт тела шеллкодом при kernel boot. Примеры: `InterfaceDispatchBridge` (195 байт), `ByRefAssignRefPatcher` (15 байт), `Cr3Accessor`, `GcStackSpill`, `JumpStub`.
3. **Codegen во время сборки** — если линкеру нужен C-ABI символ данных (`__security_cookie` и пр.), его даёт `CoffStub.Generator` по `[CoffDataSymbol]`: COFF-объект из C#, без `.c` и без `cl.exe` (`generators/CoffStub.Generator/`).

**Границы с CoreCLR.** `SharpOSHost_*` — C ABI со стабильными кодами статуса; исключения через неё не ходят. PAL — только форма ABI, логика — в C# ядра. CoreCLR линкуется в образ ядра статически. Запасная реализация `SharpOSHost_*` в форке — только `weak` или объявление: сильная подменяется до линковки (урок step114). Модель размотки — `.pdata`/Windows x64.

### Инвариант 2 — Naming discipline

Канонические .NET namespaces (`System.*`, `System.Collections.Generic.*`, `System.Collections.ObjectModel.*`) — **только для fully BCL-compat реализаций** (modulo задокументированные в `docs/nativeaot-nostd-kernel-limits.md` ограничения). Частичные/экспериментальные/OS-specific типы — в SharpOS-namespaces (`SharpOS.Std.*`, `OS.Kernel.*`, `OS.Boot.*`, `OS.Hal.*`).

Цель: LINQ/System.Text.Json/прочий BCL-код должен собираться поверх нашего std **без source-level правок**. Перед тем как положить тип в `System.*`, ответь: "можно ли взять реальный BCL-код использующий этот тип и скомпилить у нас?".

## Где что смотреть

- **`docs/nativeaot-nostd-kernel-limits.md`** — живой реестр известных ограничений NoStdLib среды: что работает, что нет, workarounds, причины. **Читать перед любой нетривиальной задачей.**
- **`docs/coreclr-hosted-limits.md`** — то же для CoreCLR-hosted яруса. **`limits.md`** — сводка сломанного по всем ярусам; **`docs/open-symptoms.md`** — симптомы без разобранной причины.
- **`docs/build-warnings.md`** — сборка без предупреждений; что подавлено и почему. Новое предупреждение — чинить или вносить туда.
- **`plan.md`** — цели проекта и текущее направление. **`donext.md`** — что делать дальше и как: фронты с ближайшим действием и долги с поводом.
- **`done/stepNN.md`** — разбор завершённых шагов: архитектура, трассы ошибок, файлы. Последний — наибольший номер в `done/`. Базовая прошивка (EH/GC/threading/CoreCLR-host) — `done/step041..step090`, дальше — bring-up подсистем.
- **`OS/src/Boot/BootSequence.cs`** — single source of truth для kernel boot ordering. Phase0…Phase5 с явными pre/post conditions. `OS/src/Kernel/Kernel.cs` — пустой shell, делегирует сюда. См. `docs/boot-order.md`.
- **`OS/src/Kernel/Diagnostics/Probes.cs`** — central registry compile-time `const bool` toggles для всех boot-time probes/smoke-тестов. Отключённые probes elim'ятся ILC'ом.
- **`gc-experiment/dotnet-runtime/src/coreclr/nativeaot/`** — снимок NativeAOT runtime'а для reference при порте helpers.
- **`gc-experiment/dotnet-runtime/src/libraries/System.Private.CoreLib/src/`** — snapshot BCL для копирования коллекций.

## Правила при реализации фичи

### Для BCL-compat типа (коллекция, утилита, struct)

**Воровать из dotnet/runtime, не изобретать.** Копируем реализацию как можно ближе к оригиналу. Обрезаем:

- Serialization attrs / `[Serializable]` / `ISerializable` / `IDeserializationCallback`.
- `_version` bump + concurrent-mod detection — можно оставить (почти cost-free), можно выкинуть.
- `ctor(IEnumerable<T>)` — только если callsite нужен.

Non-generic `IDictionary`/`IList`/`ICollection`, `Array.Sort`/`Copy`/`BinarySearch` в std есть — оставлять.

`ArgumentException` / `InvalidOperationException` / прочие — **бросаем по-настоящему**. EH работает (Phase D landed step90, FH3+FH4 personality, HW-fault → managed exception, stack traces). Halt() оставляем только для unrecoverable kernel paths (`Panic.Fail`-class), не для BCL throw-spots.

**Сохраняем:**
- Оригинальные имена полей (`_size`, `_head`, `_array`, `keys`, `values`).
- Namespace (`System.Collections.Generic` для BCL-совместимых, `SharpOS.Std.*` для частичных).
- Структуру методов и их алгоритмы.

В header комменте файла указываем "Ported from X", список cuts.

### Для новой строковой/numeric/utility операции

1. **Сначала в `std-no-runtime/`** как managed C# с BCL-идентичным API.
2. **Потом использовать в ядре/SDK** — старый inline unsafe код мигрируется на вызов std.
3. Inline unsafe оставляем только где managed реально нельзя (ABI-граница, шеллкод, обход heap в `HeapDiagnostics` который не может аллоцировать).

### Для новой low-level задачи

1. Выбрать один из трёх механизмов инварианта 1.
2. Если нужен линкер-символ — [RuntimeExport] wrapper + patcher (см. `InterfaceDispatchStub` / `ByRefAssignRefStub`).
3. Если нужно сгенерить ASM байты:
   - **Форма известна при сборке (основной путь)** — `[CompileTimeAsm]` + тело `[CompileTimeAsmBody]` на `Iced.Intel.Assembler`. BootAsm.Generator исполняет тело при сборке и кладёт байты в образ как `ReadOnlySpan<byte>`; во время выполнения — одно копирование, без Iced и без аллокатора, годится и до кучи. Пример — `SehDispatch.BootAsm.cs`.
   - **Форма зависит от данных времени выполнения** (адреса, переходники служб процесса) — `Iced.Intel.Assembler` во время выполнения (`new Assembler(64); a.mov(rax, rcx); a.Assemble(writer, rip);`).
   - Iced — только кодировщик. **`NO_EVEX`:** AVX-512 трогать нельзя (почему — `vendor/Iced/PROVENANCE.md`).

### Для функциональности, которой нет ни у нас, ни в BCL

**Искать чужую библиотеку, а не писать свою.** Правило то же, что «воровать из
dotnet/runtime», только источник шире. Самописный парсер/кодек/протокол — это
годы чужих граничных случаев, переписанные заново и хуже, и он навсегда наш:
чинить, дополнять, объяснять.

Порядок действий:

1. **Лицензия.** Годятся MIT / BSD / Apache-2.0 / Unlicense. GPL в ядро и в std
   не тащим — только отдельным приложением (так живёт DOOM).
2. **Посмотреть, чего библиотеке не хватает в нашей среде**, и решить, что
   дешевле: добить недостающее у себя или вырезать. **По умолчанию — добить.**
   TurboXml (step165) требовала `Vector128`; порт векторов занял день, снял ноль
   вырезов SIMD и достался всем остальным. Вырезать код библиотеки — последнее
   средство, не первое.
3. **Положить в `vendor/<Имя>/`** вместе с `LICENSE` и `PROVENANCE.md`: откуда,
   какая лицензия, дата снимка, что взято, что исключено и **почему**.
4. **Пространство имён библиотеки не трогать** — это чужой код, инвариант 2 к
   нему не применяется.
5. **Каждую правку помечать** `SharpOS cut:` прямо в файле, рядом с местом.
   Диффабельность против upstream дороже аккуратности.
6. **Добавить в README** — в раздел «Чужой код в образе», а не в благодарности:
   лицензия этого проекта нас обязывает, и это другой вид долга.

Типичные несовместимости, которые всплывают первыми: `System.IO.Stream`,
`System.Text.Rune`, методы интерфейсов по умолчанию (`CS8701`), интерполяция
спанов, `static readonly` таблицы (ловушка cctor — а вот
`static ReadOnlySpan<byte> => [...]` переживает, компилятор кладёт данные
блобом в образ).

## Частые ловушки

### ClassConstructorRunner — закрыт, осталось одно ограничение

**Работает:** `static readonly T[] x = new T[N]`, массивы с инициализатором, явный
`static C() {...}`, lazy-init статиков. В ядре так сделано в полутора десятках мест
(`ScancodeSource`, `Pci`, `UsbKeyboard`, `BootMedium`, …). Закрыто тремя частями:
порт `ClassConstructorRunner`, снятый флаг ILC `--resilient`, материализация
GC-статики (`GcStaticsMaterializer`).

**Что осталось:** материализация идёт в Phase 2, сразу после `GcHeap.Init`
(`BootSequence.Phase2_Runtime`). Поэтому в коде, исполняющемся раньше (баннер,
heap init, exec-стабы), статиков
с инициализацией по-прежнему нельзя — там факт-property (`public static T Default
=> new T();`) или данные inline в методе. В приложениях то же делает
`GcStaticsInit.Materialize()` из `AppRuntime.Initialize`.

Подробности и три части решения — `docs/nativeaot-nostd-kernel-limits.md` §1.

### Roslyn iterator / state-machine rewriter нужен типы по имени

`yield return` / `async-await` требуют от Roslyn найти ctor'ы конкретных типов по сигнатуре через `.Single()`. Без них компилятор крашит с `Sequence contains no elements`. Нужны: `Interlocked.CompareExchange`, `Environment.CurrentManagedThreadId`, `InvalidOperationException(string)`. Работает и для `yield` (`std-no-runtime/Threading.cs`), и для `async/await` (`Threading.Tasks.Await.cs`, step160): построитель и машина состояний — структуры, поэтому всё общее состояние держится за одной ссылкой, иначе возобновляется копия.

### Shared-generic interface dispatch работает

Начиная со step 32 — полный резолвер (decoder + DispatchMap walker + sealed virtuals + single-slot cache + lazy TypeManager init через RTR scan). `IEqualityComparer<T>` / `IEquatable<T>` / `IComparable<T>` через interface call в shared-generic body — все работают. Примитивы (`Int32..UInt64, Byte..SByte, Int16..UInt16, Boolean, Char`) реализуют `IEquatable<T>` и `IComparable<T>`. Живёт в `OS/src/Kernel/Memory/InterfaceDispatch*.cs`.

### Managed GC (non-moving mark-sweep) работает

`new object()`, `new T[n]`, `new string(...)` идут через [RuntimeExport]-ed allocators (`RhpNewFast` / `RhpNewArray` / `RhNewString`) → `GcHeap.AllocateRaw`. Сборка — mark-and-sweep. Корни со стека находит **точный** обход по `gcInfo` от ILC: `KernelGC.Collect` берёт его, как только обходчик готов, а до того — консервативный через register-spill (`GcStackSpill`). Быстрый путь выделения — контекст со сдвигом указателя (`GcHeap.TryAllocateFast`). Живёт в `std-no-runtime/GC/`.

### EH работает — `throw`/`catch`/`finally`/filter/HW-fault полный конвейер

Phase D закрыта (step 90 done/step081 + последующие). Сегодня:

- managed `throw new X()` / `catch (Y e)` / `finally` / `when`-filter — все работают на kernel-AOT, PE-app и CoreCLR-hosted ярусах. (ELF-яруса нет с step137, приложения только PE.)
- HW-fault → managed: `#PF` → `NullReferenceException`, `#DE` → `DivideByZeroException` и т.д. через `HwFaultBridge` + `__C_specific_handler` / `__CxxFrameHandler3` / `__CxxFrameHandler4` personalities.
- `Exception.StackTrace` через `.pdata`-walker + funclet-aware codeOffset.
- ILC `BoxedEnumerator` thunks работают (step 115 — `Object.m_pEEType` + `RawData` declared).

Когда пишешь port из BCL — **бросай по-настоящему**, не подменяй `Halt()`. Catch'ями тоже пользоваться можно. Limits: `GC.WaitForPendingFinalizers` зависает (`SYM-003`). SIMD: `Vector128` работает на всех трёх ярусах (step165, порт в `std-no-runtime/Runtime/Intrinsics/`), `Vector256` объявлен, но не ускоряется — AVX не в целевом наборе ILC. Регистры AVX с step199 сохраняются прерываниями (XSAVE, на процессорах с AVX); из AVX сейчас используется только FMA в libm.

## Коммит-протокол

- Каждый значимый шаг завершается коммитом с сообщением `step NN: <one-liner>` + multi-line body с разбиением по подсистемам.
- Перед коммитом — `done/stepNN.md` с полным writeup: контекст, архитектура, lessons learned, файлы, что откладываем, next step.
- Co-author trailer — строка `Co-Authored-By:` текущей модели, которую даёт среда (для коммитов, сделанных мной).
- User пушит сам — я не делаю `git push`.

### Ритуал перед коммитом — не пропускать

1. **`done/stepNN.md` создан** — даже если step маленький. Иначе done-папка отстаёт от commits (как это случилось со step100/101/102 — пришлось retroactively заполнять). Если step нетривиальный — writeup полный; если cleanup — три абзаца тоже OK.
2. **Не коммитить мелочи отдельно** — one-line trivial cleanup идёт bundle'ом со следующим meaningful work, а не как standalone commit. История чище. См. memory `feedback_no_micro_commits`.
3. **Limits-таблицы синхронизированы** — `docs/coreclr-hosted-limits.md` (для CoreCLR-hosted яруса) и `docs/nativeaot-nostd-kernel-limits.md` (для kernel-AOT и PE-приложений). Если step открыл/закрыл/деградировал поверхность — соответствующая строка должна обновиться **в том же коммите**, не позже. Та же логика для `README.md` comparative table если изменилась user-facing capability.

Эти три проверки — не nice-to-have. Их пропуск ломает основной workflow: `done/` теряется как источник истины, `git log` зашумляется trivia, и `limits.md` становится бесполезен после нескольких пропусков (нельзя доверять "FAIL" статусу если он давно).
