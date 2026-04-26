# Step 36 — Phase 0b: BCL base extension

## Контекст

Закрывает Phase 0 ("IDT + BCL base"). Step 35 (Phase 0a) поставил IDT + диагностический panic dump. Этот шаг расширяет `std/no-runtime/shared/` до **стабильного 1:1 BCL surface'а** для типов и методов которые портируемый BCL-код реально дёргает в kernel/native-tier.

Цель Phase 0 в целом — чтобы Phase 1 (managed exception handling) и Phase 3 (scheduler/threading с BCL portированием) работали на готовом фундаменте, без того чтобы каждый под-шаг тратил время на "ой, нет ещё IndexOf".

Подход — invariant 2: **"воровать BCL, не изобретать"**. Файлы вида `gc-experiment/dotnet-runtime/src/libraries/System.Private.CoreLib/src/System/*.cs` копируются с трим'ом непортируемого (Globalization, Throw paths → Halt, ISerializable, async, SIMD).

## Что добавлено

### IntPtr / UIntPtr (`OS/src/Boot/MinimalRuntime.cs`)

Расширены до полного BCL surface:
- Arithmetic operators `+`, `-` для (IntPtr, int) и (UIntPtr, int).
- `Add(ptr, offset)`, `Subtract(ptr, offset)` static methods.
- `ToInt32()`, `ToInt64()`, `ToPointer()` instance methods.
- `MaxValue`, `MinValue` static properties.
- `ToString()` через `NumberFormatting.LongToString` / `ULongToString`.

### Math (`std/no-runtime/shared/Runtime/Math.cs`)

Добавлены:
- `Min/Max` для **byte, sbyte, short, ushort** (раньше только int/uint/long/ulong).
- `Abs` для **sbyte, short** (раньше только int/long).
- `Sign(sbyte/short/int/long)` — полный набор для целочисленных типов.
- `Clamp` для всех целочисленных шириной.
- `DivRem(int, int, out int)` + `DivRem(long, long, out long)`.

**Намеренно не добавлено:** float/double Math (Floor/Ceiling/Round/Sqrt/Abs/Sign + transcendentals). Наши `Single`/`Double` в MinimalRuntime — пустые placeholder structs без recursive `_value` поля и без BitConverter инфраструктуры. Kernel/native-tier нигде FP math не используют. CoreCLR в hosted-tier (Phase 6) принесёт полноценный double Math со своими Single/Double.

### Debug (`std/no-runtime/shared/Runtime/Diagnostics.cs`)

Заменено на реальный impl поверх `OS.Hal.Console` (раньше all no-op stubs):
- `Assert(condition)`, `Assert(condition, message)`, `Assert(condition, message, detail)`.
- `Fail(message)`, `Fail(message, detail)` — печатают и halt'ят.
- `Write/WriteLine/Print` с indentation support.
- `Indent/Unindent/IndentLevel` управление отступом.

Все методы помечены `[Conditional("DEBUG")]` — в Release builds компилятор стрипает call sites, asserts free.

**Limitation:** файл сейчас зависит от `OS.Hal.Console` (kernel-side namespace). Apps не включают этот файл в свой csproj, так что portability нарушения нет. Когда apps понадобится Debug — добавим env-specific hook или per-app stub.

### StringSplitOptions (`std/no-runtime/shared/StringSplitOptions.cs`)

Новый enum: `None`, `RemoveEmptyEntries`, `TrimEntries` (Flags). Pure verbatim из BCL.

### String split overloads (`StringManipulation.cs`)

Добавлены 6 новых overloads для всех combinations of separator (char/char[]/string) × (с count) × StringSplitOptions:
- `Split(char, StringSplitOptions)`
- `Split(char, int, StringSplitOptions)`
- `Split(char[], StringSplitOptions)`
- `Split(char[], int, StringSplitOptions)`
- `Split(string, StringSplitOptions)`
- `Split(string, int, StringSplitOptions)`

Internal helper `ApplySplitOptions(parts, options)` — single-pass post-process: walks, optionally trim'ит, optionally скипает empty entries, аллоцирует output array строго размером result count.

### String comparison (`std/no-runtime/shared/StringComparison.cs` — новый)

Партиал расширение `System.String` с Compare/CompareOrdinal/CompareTo/Equals overloads. Ordinal-only — без CultureInfo / Globalization.

- `static Compare(strA, strB)` + `(strA, strB, ignoreCase)` + `(strA, strB, StringComparison)`.
- `static Compare(strA, indexA, strB, indexB, length, ...)` — range comparison.
- `static CompareOrdinal(strA, strB)` + range overload.
- Instance `CompareTo(string)` + `CompareTo(object)`.
- Instance `Equals(string)` + `Equals(string, StringComparison)`.
- Static `Equals(a, b)` + `Equals(a, b, StringComparison)`.
- Override `Equals(object)` и `GetHashCode()` — закрывает CS0660/CS0661 warnings которые висели после введения operator==.

`StringComparison` enum также определён в этом же файле. CurrentCulture/InvariantCulture в нашей среде ведут себя identical to Ordinal (нет i18n). OrdinalIgnoreCase — ASCII-only `AsciiToLower` (A..Z → a..z).

GetHashCode — FNV-1a, не Marvin32 collision-resistant вариант BCL. Для Dictionary бакетов хватит.

### MemoryExtensions (`std/no-runtime/shared/Runtime/MemoryExtensions.cs`)

Расширено с AsSpan-only до full search/transform surface:
- `IndexOf<T>(this Span<T>, T)` + ROSpan вариант.
- `LastIndexOf<T>(this Span<T>, T)` + ROSpan.
- `Contains<T>(this Span<T>, T)` + ROSpan.
- `SequenceEqual<T>(this Span<T>, ROSpan<T>)` + ROSpan-ROSpan.
- `StartsWith<T>(this Span<T>, ROSpan<T>)` + ROSpan.
- `EndsWith<T>(this Span<T>, ROSpan<T>)` + ROSpan.
- `Reverse<T>(this Span<T>)`.
- `Fill<T>(this Span<T>, T)`.
- `Clear<T>(this Span<T>)`.

Все методы где требуется equality — `where T : IEquatable<T>` constraint. Equality резолвится через **shared-generic interface dispatch** (step 32). Для T-примитивов (int/long/byte/...) которые `IEquatable<T>` имплементят, dispatch попадает в их типизированный `Equals(T)` без boxing.

BCL версия — 3200+ строк с SIMD/Vector/SearchValues optimization. Наша — scalar loops. Performance hit, корректность идентична. Когда Phase 6 принесёт CoreCLR, hosted-tier code пойдёт через настоящий vectorized SpanHelpers.

### Array (`std/no-runtime/shared/Runtime/Array.cs`)

Добавлены search + transform методы:
- `IndexOf<T>(T[], T)` + 2 overload'а с startIndex / count.
- `LastIndexOf<T>(T[], T)` + 2 overload'а.
- `Reverse<T>(T[])` + range overload `(T[], int, int)`.
- `Resize<T>(ref T[], int)` — allocates fresh array, copies min(old, new) элементов, swap'ит ref.
- `Clear<T>(T[], int, int)` — устанавливает default(T) на range.

Все search методы под `where T : IEquatable<T>` через тот же shared-generic iface dispatch.

**Намеренно не добавлено:**
- `Find<T>`, `FindAll<T>`, `FindIndex<T>`, `Exists<T>`, `TrueForAll<T>`, `ForEach<T>` — все требуют `Predicate<T>` / `Action<T>` (managed delegates), которых у нас нет ([nativeaot-nostdlib-limits.md §5](../docs/nativeaot-nostdlib-limits.md)). Появятся когда CoreCLR придёт в Phase 6.
- `Sort<T>`, `BinarySearch<T>` — могут работать через `IComparer<T>` shared-generic dispatch, но добавляют сложность. Defer до первого caller'а.

## Изменённый surface — кратко

| Тип | Раньше | Теперь |
|---|---|---|
| `IntPtr/UIntPtr` | ctor + cast operators + `==`/`!=` | + arithmetic (`+`,`-`), `Add`, `Subtract`, `ToInt32/64`, `ToString`, `MinValue/MaxValue` |
| `Math` | int/uint/long/ulong Min/Max/Abs/Clamp | + byte/sbyte/short/ushort Min/Max/Clamp, +Abs(sbyte/short), +Sign(*), +DivRem |
| `Debug` | no-op stubs | реальный Assert/Fail/Write/WriteLine + indent |
| `StringSplitOptions` | — | enum + 6 Split overloads |
| `String.Compare*` | — | CompareOrdinal + Compare + CompareTo + Equals + GetHashCode |
| `MemoryExtensions` | AsSpan only | + IndexOf/Contains/SequenceEqual/StartsWith/EndsWith/Reverse/Fill/Clear |
| `Array` | Empty + Copy | + IndexOf/LastIndexOf/Reverse/Resize/Clear |

## Файлы

### Новые

- `std/no-runtime/shared/StringSplitOptions.cs`
- `std/no-runtime/shared/StringComparison.cs`
- `done/step036.md` (этот файл)

### Изменённые

- `OS/src/Boot/MinimalRuntime.cs` — IntPtr/UIntPtr расширены.
- `std/no-runtime/shared/Runtime/Math.cs` — extended.
- `std/no-runtime/shared/Runtime/Diagnostics.cs` — Debug real impl.
- `std/no-runtime/shared/Runtime/MemoryExtensions.cs` — search/transform extensions.
- `std/no-runtime/shared/Runtime/Array.cs` — IndexOf/LastIndexOf/Reverse/Resize/Clear.
- `std/no-runtime/shared/StringManipulation.cs` — Split overloads с StringSplitOptions.
- `OS/OS.csproj` — добавлены StringSplitOptions.cs, StringComparison.cs.

## Верификация

Build pass: должен скомпилироваться без новых ошибок. Warnings CS0660/CS0661 на operator== должны исчезнуть (override Equals/GetHashCode добавлены).

Runtime: все 52 probes должны остаться зелёными — мы не меняли existing path'ов, только расширяли surface. Никакие probe не дёргают новые методы напрямую (можно добавить в следующих шагах если понадобится).

Launcher (HelloSharpFs) — unaffected. Apps не включают новые файлы (все additive за пределами того что они уже используют).

## Что откладываем (Phase 0 → Phase 1+ или дальше)

- **Math float/double** — нет caller'ов. Появится когда первый native-tier app запросит. Если CoreCLR (Phase 6) придёт раньше — отдаст оттуда.
- **Array.Sort, Array.BinarySearch** — отдельная задача. Сложность IComparer<T> dispatch + sort algorithm port.
- **Array.Find/FindAll/Exists/TrueForAll/ForEach/FindIndex** — требуют managed delegates (Predicate/Action). Заблокировано до Phase 6.
- **String.Format, AppendFormat, InterpolatedStringHandler** — большая отдельная задача (SUPER-1b advanced). Для kernel и native-tier пока не нужно.
- **ReadOnlyMemory<T>, Memory<T>** — для hosted-tier нужны. Не добавляем сейчас.
- **MemoryExtensions Globalization variants** (Trim with culture, IndexOf with comparison) — за-Ordinal, не нужны.

## Phase 0 закрыт

После step 36 фаза 0 целиком завершена:
- Phase 0a (step 35): IDT + signal-dispatch + PanicDump.
- Phase 0b (step 36): BCL base.

Готовы к Phase 1: **kernel exception handling + platform infrastructure**:
- Полноценное managed exception handling (через personality + .eh_frame, **не урезано** до longjmp).
- ClassConstructorRunner портирование (разблокирует все static reference fields).
- ACPI parsing (RSDP → RSDT/XSDT → MADT/HPET/MCFG).
- RTC + HPET/TSC для timekeeping.

Самый рисковый пункт всего плана — full unwinding. Может занять 2-6 месяцев focused работы.
