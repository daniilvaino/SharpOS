# CLAUDE.md

## Что это за проект

SharpOS — **experimental unikernel целиком на C#**, собирается как `NativeAOT + NoStdLib`, таргетит `UEFI` (`EFI_APPLICATION`). Это **не** обычное `dotnet console`. Точка входа — `EfiMain` через `[RuntimeExport]`. Кода с `EFI_SYSTEM_TABLE*`, `unsafe`, ручными указателями много, но он локализован в ABI-слоях.

## Архитектурные инварианты

Жёсткие правила. Любая задача должна укладываться в них. Если кажется что не укладывается — переформулируй.

### Инвариант 1 — C# is the only source language

**В дереве исходников нет ни одного `.c`, `.cpp`, `.h`, `.asm`, `.s` файла.** Каждая low-level задача решается одним из трёх:

1. **C# intrinsics** — `[RuntimeExport]`, `[UnmanagedCallersOnly]`, `delegate* unmanaged`, `fixed`, unsafe pointer arithmetic.
2. **Byte-array shellcode** — C# emitter пишет машинные инструкции байт-за-байтом в exec-stub buffer, аллоцированный UEFI через `AllocatePool(EfiLoaderCode)` (гарантированно исполнимый даже под W^X). Паттерн: `[RuntimeExport]` managed wrapper с Panic.Fail fallback, + patcher перезаписывает первые N байт тела шеллкодом при kernel boot. Примеры: `InterfaceDispatchBridge` (195 байт), `ByRefAssignRefPatcher` (15 байт), `Cr3Accessor`, `GcStackSpill`, `JumpStub`.
3. **Build-time codegen в PowerShell** — если MSVC-линкер требует C-ABI символ (`__security_cookie` и пр.), генерим `.c` ephemerally внутри `build_*.ps1`, компилим, линкуем. **Не коммитим в репо.**

### Инвариант 2 — Naming discipline

Канонические .NET namespaces (`System.*`, `System.Collections.Generic.*`, `System.Collections.ObjectModel.*`) — **только для fully BCL-compat реализаций** (modulo задокументированные в `docs/nativeaot-nostdlib-limits.md` ограничения). Частичные/экспериментальные/OS-specific типы — в SharpOS-namespaces (`SharpOS.Std.*`, `OS.Kernel.*`, `OS.Boot.*`, `OS.Hal.*`).

Цель: LINQ/System.Text.Json/прочий BCL-код должен собираться поверх нашего std **без source-level правок**. Перед тем как положить тип в `System.*`, ответь: "можно ли взять реальный BCL-код использующий этот тип и скомпилить у нас?".

## Где что смотреть

- **`docs/nativeaot-nostdlib-limits.md`** — живой реестр известных ограничений NoStdLib среды: что работает, что нет, workarounds, причины. **Читать перед любой нетривиальной задачей.**
- **`plan.md`** — супер-задачи проекта (SUPER-1…SUPER-12) с критериями готовности.
- **`done/stepNN.md`** — разбор завершённых шагов с архитектурой, трассами ошибок, файлами. Текущий актуальный: `done/step033.md`.
- **`gc-experiment/dotnet-runtime/src/coreclr/nativeaot/`** — снимок NativeAOT runtime'а для reference при порте helpers.
- **`gc-experiment/dotnet-runtime/src/libraries/System.Private.CoreLib/src/`** — snapshot BCL для копирования коллекций.

## Правила при реализации фичи

### Для BCL-compat типа (коллекция, утилита, struct)

**Воровать из dotnet/runtime, не изобретать.** Копируем реализацию как можно ближе к оригиналу. Обрезаем:

- Serialization attrs / `[Serializable]` / `ISerializable` / `IDeserializationCallback`.
- Non-generic `ICollection` / `IList` / `IDictionary` surface (нет у нас в стабах).
- `ArgumentException` / `InvalidOperationException` throws → `Halt()` (нет exception engine).
- `_version` bump + concurrent-mod detection — можно оставить (почти cost-free), можно выкинуть.
- `ctor(IEnumerable<T>)` — только если callsite нужен.
- `Array.Copy/Sort/BinarySearch` — инлайним manual loops (у нас нет этих static helpers).

**Сохраняем:**
- Оригинальные имена полей (`_size`, `_head`, `_array`, `keys`, `values`).
- Namespace (`System.Collections.Generic` для BCL-совместимых, `SharpOS.Std.*` для частичных).
- Структуру методов и их алгоритмы.

В header комменте файла указываем "Ported from X", список cuts.

### Для новой строковой/numeric/utility операции

1. **Сначала в `std/no-runtime/shared/`** как managed C# с BCL-идентичным API.
2. **Потом использовать в ядре/SDK** — старый inline unsafe код мигрируется на вызов std.
3. Inline unsafe оставляем только где managed реально нельзя (ABI-граница, шеллкод, обход heap в `HeapDiagnostics` который не может аллоцировать).

### Для новой low-level задачи

1. Выбрать один из трёх механизмов инварианта 1.
2. Если нужен линкер-символ — [RuntimeExport] wrapper + patcher (см. `InterfaceDispatchStub` / `ByRefAssignRefStub`).
3. Если нужно сгенерить ASM байты — byte emitter в C#, данные inlined в метод (НЕ `static readonly byte[]` — см. ниже ловушку).

## Частые ловушки

### ClassConstructorRunner trap

**Запрещено:** lazy-init static reference fields. Любая форма — `static T s_x = new T();`, `if (s == null) s = new T();`, `static readonly T[] Shellcode = new T[] {...};`. ILC вставляет cctor-check через `ClassConstructorRunner.CheckStaticClassConstruction*` helpers которые у нас не работают → `#GP` с `RAX = 0xF000000...`.

**Workaround:** factory property без кеширования (`public static T Default => new T();`), или inline данные прямо в методе (`target[0] = 0x48; target[1] = ...;`).

Задокументировано в `docs/nativeaot-nostdlib-limits.md` §1.

### Roslyn iterator / state-machine rewriter нужен типы по имени

`yield return` / `async-await` требуют от Roslyn найти ctor'ы конкретных типов по сигнатуре через `.Single()`. Без них компилятор крашит с `Sequence contains no elements`. Нужны: `Interlocked.CompareExchange`, `Environment.CurrentManagedThreadId`, `InvalidOperationException(string)`. Для yield это сейчас работает (`std/no-runtime/shared/Threading.cs`), для async пока нет.

### Shared-generic interface dispatch работает

Начиная со step 32 — полный резолвер (decoder + DispatchMap walker + sealed virtuals + single-slot cache + lazy TypeManager init через RTR scan). `IEqualityComparer<T>` / `IEquatable<T>` / `IComparable<T>` через interface call в shared-generic body — все работают. Примитивы (`Int32..UInt64, Byte..SByte, Int16..UInt16, Boolean, Char`) реализуют `IEquatable<T>` и `IComparable<T>`. Живёт в `OS/src/Kernel/Memory/InterfaceDispatch*.cs`.

### Managed GC (non-moving mark-sweep) работает

`new object()`, `new T[n]`, `new string(...)` идут через [RuntimeExport]-ed allocators (`RhpNewFast` / `RhpNewArray` / `RhNewString`) → `GcHeap.AllocateRaw`. Сборка — mark-and-sweep с conservative stack scan через register-spill trampoline (`GcStackSpill`). Живёт в `std/no-runtime/shared/GC/`.

### `throw` компилируется, но халтит

Exception типы (`Exception`, `InvalidOperationException` и пр.) есть для Roslyn codegen, но `throw new X()` в runtime упирается в `ThrowHelpers` которые спинят-halt-ят. Нет unwinder'а, нет catch. Это SUPER-5 задача из плана. Пока используем `Halt()` везде где BCL throw-бы.

## Коммит-протокол

- Каждый значимый шаг завершается коммитом с сообщением `step NN: <one-liner>` + multi-line body с разбиением по подсистемам.
- Перед коммитом — `done/stepNN.md` с полным writeup: контекст, архитектура, lessons learned, файлы, что откладываем, next step.
- Формат co-author trailer — `Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>` (для коммитов сделанных мной).
- User пушит сам — я не делаю `git push`.
