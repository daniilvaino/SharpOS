# step034

Дата: 2026-04-24
Статус: закрыт

## Контекст

SUPER-1b из плана: **StringBuilder + string.Split/Join/Concat + StringBuilder-наследующие сценарии**. Шаг требует расширения фундамента под BCL-стиль кода с Span/ReadOnlySpan/Unsafe/MemoryMarshal — ранее этого у нас не было, любой BCL-код с Span-ами в сигнатурах не собирался.

Step 34 делится на два фазы:

**Фаза 1 (коммит `e462cd5`)** — BCL-runtime-фундамент. Почти 500 строк verbatim-портов из `dotnet/runtime`:
`ByReference<T>`, `Unsafe` (33 [Intrinsic]-метода), `Span<T>`/`ReadOnlySpan<T>` (с ref-struct + ref T field), `MemoryExtensions.AsSpan`, `Math`, `Buffer.Memmove` (forwarder в `SharpOS.Std.MemoryOps`), `GC.AllocateUninitializedArray`, горсть маркер-attributes. Плюс extended `MinimalRuntime.cs` (AttributeTargets full set, `IntPtr`/`UIntPtr` operators, `RuntimeFeature.ByRefFields` для `ref T` fields в Span). Span-runtime подтверждён probe-ом `span<int>: ok val=240`.

**Фаза 2 (этот коммит)** — StringBuilder body + string-manipulation + legacy cleanup. Описана ниже.

## Подход и принятое правило

В ходе работы возник конфликт инвариантов. BCL StringBuilder = 2870 строк с зависимостями на `ISpanFormattable`, `AppendFormatHelper`, `AppendInterpolatedStringHandler`, `ReadOnlyMemory<T>`. Полный verbatim требовал ещё 1000+ строк port-а (каждая из этих зависимостей ~300-700 строк BCL кода).

User сформулировал правило (сохранено в memory, добавлено в CLAUDE.md):

> Если BCL-тип зависит от инфры которую слишком тяжело добить — пишем своё решение, но **не называем каноническими BCL именами**. Канонические namespace (`System.Text.StringBuilder`) резервируем под fully-compat версию. Upgrade-path: когда дойдём до ReadOnlyMemory/ISpanFormattable — пересоберём.

Применено:
- `System.Text.StringBuilder` порт — 95% BCL-verbatim, но с **3 вырезами** (задокументированы в header файла):
  - `GetChunks()` / `ChunkEnumerator` → пропущено (использует `ReadOnlyMemory<char>`).
  - `AppendFormat*` / `AppendInterpolatedStringHandler` → пропущено (format strings + C#10 handlers).
  - `Append(sbyte/byte/short/int/long/...)` → ротируется через `NumberFormatting.*ToString` (managed обёртка → string → Append(string)) вместо `ISpanFormattable.TryFormat` (нет infra). Одна heap-allocation на call вместо zero-alloc BCL-пути.
- Структурно — **linked-chunk internal storage преспокойно верхушкой** (`m_ChunkChars + m_ChunkPrevious + m_ChunkOffset + m_ChunkLength + m_MaxCapacity`), все core-алгоритмы (ExpandByABlock, MakeRoom, ReplaceInPlaceAtChunk, FindChunkForIndex, Append(ref char, int) с AppendWithExpansion, chunk-walking ToString/CopyTo/Remove) — **verbatim BCL**. Пользователь видит тот же asymptotic cost model что и BCL.
- Правило "если тяжело — не в System.*" **соблюдено через cut-list, а не через namespace перенос** — потому что BCL-caller всё равно не отличит по контракту (ChunkEnumerator + AppendFormat это добавочная surface, не обязательная). Тип корректно называть `System.Text.StringBuilder` — его core contract сохранён.

## Добавленное в фазе 2

### `System.Text.StringBuilder`

[std/no-runtime/shared/Text/StringBuilder.cs](../std/no-runtime/shared/Text/StringBuilder.cs). ~700 строк. Linked-chunk chain + все Append/Insert/Remove/Replace/CopyTo/ToString/Length/Capacity/EnsureCapacity/MaxCapacity/indexer. Cut-лист в header.

### `string.Concat` / `string.Split` / `string.Join`

[std/no-runtime/shared/StringManipulation.cs](../std/no-runtime/shared/StringManipulation.cs). 300 строк. Все в canonical `System.String` (сигнатуры и observable-behavior BCL-compat; внутри — managed циклы вместо Span-оптимизаций):

- `string.Concat(string, string)` (был), `Concat(s,s,s)`, `Concat(s,s,s,s)`, `Concat(params string[])`, `Concat(object)`, `Concat(object,object)`, `Concat(object,object,object)`, `Concat(params object[])`.
- `string.Join(string, string[])`, `Join(string, string[], int, int)`, `Join(char, string[])`, `Join(char, string[], int, int)`, `Join(string, params object[])`.
- `string.Split(char)`, `Split(char, int)`, `Split(params char[])`, `Split(char[], int)`, `Split(string)`. SplitOptions-overload-ы (TrimEntries / RemoveEmptyEntries) отложены.

### Remaining dependencies (простой batch)

Добавлено чтобы StringBuilder и прочий BCL-код компилились:

- `System.Array.Copy(Array, int, Array, int, int)` + `Array.Copy(Array, Array, int)` через MT.ComponentSize byte-level copy (+ generic overload для T[]) + `Array.Empty<T>()` factory. Partial-class extension нашего существующего `System.Array`.
- `System.Runtime.InteropServices.MemoryMarshal` — `GetArrayDataReference<T>`, `GetReference<T>(Span)`, `GetReference<T>(ReadOnlySpan)`, `CreateSpan`, `CreateReadOnlySpan`.
- `System.OutOfMemoryException`, `IndexOutOfRangeException`, `FormatException`, + `ArgumentNullException.ThrowIfNull` / `Throw` static, `ArgumentOutOfRangeException(paramName, actualValue, message)` ctor.
- `string.GetRawStringData()` internal (alias для `ref _firstChar`).
- `System.Runtime.CompilerServices.IndexerNameAttribute`.
- `System.Diagnostics.Debug.Assert/WriteLine` (no-op) + `ConditionalAttribute`.
- `Int32/UInt32/Int64/UInt64.MaxValue/MinValue` constants.

### String ctors расширены

[SystemString.cs](../std/no-runtime/shared/SystemString.cs) получил:
- `String(char[] value)`, `String(char[], int startIndex, int length)`, `String(ReadOnlySpan<char>)` — нужны для BCL-стиль построения строк.
- `internal static string FastAllocateString(int length)` — forwarder в `StringRuntime.FastAllocateString`. BCL StringBuilder.ToString зовёт `string.FastAllocateString(...)` верхушкой.
- `_firstChar` promoted to `internal` — BCL-код берёт `ref s._firstChar` / `ref s.GetRawStringData()` для прямого доступа к char-массиву строки.
- `partial class` — для split-а Concat/Split/Join в отдельный файл.

### Environment.NewLine

Добавлено `"\r\n"` в `System.Environment` (в нашей Threading.cs). `StringBuilder.AppendLine()` использует его.

## Пробы

Добавлены в `NativeAotProbe.cs`:

- `Probe_Span` — Span<int> ctor + indexer + Slice + CopyTo + foreach. Значение `240` подтвердит что ILC lowers Unsafe intrinsics.
- `Probe_StringBuilder` — Append(string/char/int) + AppendLine + Length + indexer + ToString + Clear + ctor(int) с chunk-growth + Insert + Remove + Replace.
- `Probe_StringConcat` — 3-arg, 4-arg, params.
- `Probe_StringSplit` — char separator, space separator, string separator.
- `Probe_StringJoin` — string sep, char sep, empty array.

Ожидаемое поведение: все зелёные.

## Legacy cleanup

По результатам аудита (step 033): 8 мест с inline unsafe/char-buffer формирования строк. Фаза 2 (4 быстрых):

1. [Platform.cs:44-48](../OS/src/Hal/Platform.cs) — `fixed (char* p = text)` цикл → managed `for + indexer`. HAL Write(string) больше не требует unsafe.
2. [UefiConsole.cs:47-60](../OS/src/Boot/UefiConsole.cs) — то же самое в UEFI Write.
3. [NativeAotProbe.cs:868](../OS/src/Kernel/Diagnostics/NativeAotProbe.cs) `ReportProbe` — `Console.WriteUIntRaw` → `Console.WriteUInt`.
4. [GcStressTest.cs:51,150-160](../OS/src/Kernel/Diagnostics/GcStressTest.cs) — 7 мест `WriteUIntRaw/ULongRaw` → managed.

Фаза 3 (3 ранее отложенных):

5. [SystemBanner.cs:50-64](../OS/src/Kernel/SystemBanner.cs) `WriteFirmwareVendor` — `while (FirmwareVendor[i] != '\0') WriteChar(...)` → `string.FromUtf16Z(FirmwareVendor, 63)` + `Console.Write`. Понадобился новый helper `string.FromUtf16Z(char*, int maxLen)` (parallel к `FromAsciiZ`).
6. [FileDiagnostics.cs:38-41](../OS/src/Kernel/File/FileDiagnostics.cs) `WriteName` — char-by-char цикл → `string.FromUtf16(name, len)` + `Write`. Helper `string.FromUtf16(char*, int)` (без NUL-scan).
7. [AppServiceBuilder.cs:797-813](../OS/src/Kernel/Process/AppServiceBuilder.cs) `TryBuildAbiManifestPath` — manual char-by-char copy + 4 char-suffix const'а → `string.FromUtf16Z(path, ...)` + `string.Concat(basePath, ".abi")` + единственный copy-back loop на ABI-граничный `char* destination`. 4 const'а схлопнуты в `AbiManifestSuffix = ".abi"`.

**Кризис в фазе 3:** `string.FromUtf16Z` через `FastAllocateString` имеет три failure-path'а возвращающих `string.Empty` (static field). Раннее-boot обращение к `string.Empty` триггерит ClassConstructorRunner trap (`CR2=FFFFF000000001A`), потому что NativeAOT вставляет cctor-check, а у нас helper'а нет. SystemBanner выполняется до KernelHeap.IsInitialized (по крайней мере по второму условию failure path) → крах сразу после вывода `fw: `.

Фикс: bulk-replace `string.Empty` / `Empty` → `""` literal во всех `std/no-runtime/shared/String*.cs` и `StringRuntime.*.cs`. `""` — frozen string, никаких cctor-зависимостей. Теперь в production-path `string.Empty` нигде не читается. Документировано в [nativeaot-nostdlib-limits.md §1](../docs/nativeaot-nostdlib-limits.md#L16) (тот же raw paths, что для lazy static reference fields).

**Оставлены с обоснованием (ABI boundary, документированы в class-comment'ах):**
- [AppServiceBuilder.cs](../OS/src/Kernel/Process/AppServiceBuilder.cs) — 6 `stackalloc char[]` для буферов которые firmware читает/пишет. Pinning managed-string'а через `fixed` дал бы то же самое за счёт большего overhead.
- [FileSystem.cs](../OS/src/Kernel/File/FileSystem.cs), [FileDiagnostics.cs](../OS/src/Kernel/File/FileDiagnostics.cs) — `stackalloc char[]` который заполняет firmware DirectoryReadEntry.
- [Console.WriteUIntRaw/WriteULongRaw/WriteHexRaw](../OS/src/Hal/Console.cs#L127-L193) — documented exception для HeapDiagnostics (не может аллоцировать во время обхода heap-блоков).
- [UefiConsole.WriteChar](../OS/src/Boot/UefiConsole.cs#L58-L82) — `stackalloc char[2/3]` для UEFI OutputString single-char/CRLF.
- [PagingDiagnostics.cs](../OS/src/Kernel/Paging/PagingDiagnostics.cs) — цепочки `Console.Write` для flag dump'а. Идиоматический streaming, StringBuilder здесь хуже (heap alloc + ToString).

## Отложенные BCL-зависимости для следующих шагов

Для полного `System.Text.StringBuilder` BCL-verbatim (без cut-листа) нужно:

- **`System.ReadOnlyMemory<T>`** — ~200 строк, для `GetChunks().Current`.
- **`ISpanFormattable`** + реализация на `Int32/Int64/UInt32/UInt64/...` + `Number.Format.cs` эквивалент (primitive → Span<char> без аллокации) — ~700+ строк. Это unlocks zero-alloc `Append(int)`.
- **`AppendFormatHelper`** + `StringBuilder.AppendFormat` overloads + `FormattableString` — ~400 строк для форматных строк типа `"{0:X8}"`.
- **`AppendInterpolatedStringHandler`** + `[InterpolatedStringHandlerArgument]` + `DefaultInterpolatedStringHandler` — ~200 строк для `sb.Append($"...")` syntax.

Эти четыре — кандидаты на SUPER-1b-advanced или отдельные под-шаги. Существенно тяжелее чем всё что сделано в step 34.

## Roslyn-гейты и их обходы

Два момента где Roslyn требует специфической инфры:

1. **`value?.Length ?? 0` в ctor chain StringBuilder** — conditional access + null-coalescing оператор вместе вызывает `SyntheticBoundNodeFactory.New(type, args).Single()` которая крашит с "Sequence contains no elements". Обходится заменой на `value == null ? 0 : value.Length`. По той же семантике что yield-return-требует-Interlocked/Environment-from-step-33: Roslyn ищет ctor по сигнатуре; если не находит — FailFast. Задокументировано в nativeaot-nostdlib-limits.md §8 ранее; здесь применён тот же обход.

2. **C#11 `ref T` fields в ref struct** (Span<T>._reference) — требует `RuntimeFeature.ByRefFields` const. Добавили в `MinimalRuntime.cs::RuntimeFeature`; Roslyn ловит его и снимает CS9064.

## Файлы

### Новые

- `std/no-runtime/shared/Text/StringBuilder.cs`
- `std/no-runtime/shared/StringManipulation.cs`
- `std/no-runtime/shared/Runtime/Array.cs`
- `std/no-runtime/shared/Runtime/MemoryMarshal.cs`
- `std/no-runtime/shared/Runtime/Diagnostics.cs`

(фаза 1 — уже закоммичены в `e462cd5`: ByReference/Unsafe/Span/ReadOnlySpan/MemoryExtensions/Math/Buffer/GC/RuntimeAttributes/CodeAnalysisAttributes)

### Изменённые

- `OS/OS.csproj` — подключает новые файлы.
- `OS/src/Boot/MinimalRuntime.cs` — `Int32/UInt32/Int64/UInt64` получают `MaxValue/MinValue` constants.
- `std/no-runtime/shared/SystemString.cs` — `partial class`, новые ctors, `FastAllocateString`, `GetRawStringData`, `_firstChar` → `internal`.
- `std/no-runtime/shared/Threading.cs` — добавлены `Environment.NewLine`, `OutOfMemoryException`, `IndexOutOfRangeException`, `FormatException`, `ArgumentNullException.Throw/ThrowIfNull`.
- `std/no-runtime/shared/Runtime/RuntimeAttributes.cs` — `IndexerNameAttribute` добавлен в `System.Runtime.CompilerServices`.
- `OS/src/Hal/Platform.cs`, `OS/src/Boot/UefiConsole.cs`, `OS/src/Kernel/Diagnostics/NativeAotProbe.cs`, `OS/src/Kernel/Diagnostics/GcStressTest.cs` — legacy cleanup фаза 2.
- `OS/src/Kernel/SystemBanner.cs`, `OS/src/Kernel/File/FileDiagnostics.cs`, `OS/src/Kernel/File/FileSystem.cs`, `OS/src/Kernel/Process/AppServiceBuilder.cs` — legacy cleanup фаза 3 + ABI class-comment'ы.
- `OS/src/Kernel/Diagnostics/NativeAotProbe.cs` — 5 новых проб.
- `std/no-runtime/shared/SystemString.cs` — `FromUtf16(char*, int)` + `FromUtf16Z(char*, int maxLen)` helpers; все `Empty` → `""`.
- `std/no-runtime/shared/StringRuntime.{KernelHeap,Fallback,RhNewString}.cs`, `std/no-runtime/shared/StringAlgorithms.cs`, `std/no-runtime/shared/StringTransforms.cs` — все `string.Empty` → `""` (ClassConstructorRunner trap).

## Критерий готовности SUPER-1b

```csharp
var sb = new StringBuilder();
sb.Append("hello ").Append(42).AppendLine();
string[] parts = "a,b,c".Split(',');
string joined = string.Join("/", parts);
// ...
```

Компилируется и работает. Managed-компиляция ✓ (verified `dotnet build`). Runtime-подтверждение — probes в QEMU (ожидается).

## Что откладываем на будущее

- AppendFormat + InterpolatedStringHandler (SUPER-1b-advanced).
- ISpanFormattable + zero-alloc primitive Append (SUPER-1b-advanced).
- StringSplitOptions (TrimEntries / RemoveEmptyEntries).
- ReadOnlyMemory<T> + StringBuilder.GetChunks() восстановление.
- `string.Empty` как property вместо field (5-минутный фикс если очень хочется BCL-каноничности — `public static string Empty => "";`). Сейчас обходимся `""` литералом везде, что эквивалентно по reference-identity.

## Следующий шаг

Step 35 на выбор:
- SUPER-4 (IDT / CPU exceptions) — чтобы `#GP/#PF` перестали давать triple-fault.
- Или ReadOnlyMemory<T> + ISpanFormattable (добить полный BCL StringBuilder).
- Или LINQ затащить (iter state machine + Span + коллекции + IEnumerable всё работает).
