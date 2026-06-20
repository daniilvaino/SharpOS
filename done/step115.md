# step 115 — Iced x64 encoder baked into kernel + std уровень для лифта BCL/Iced кода

## Веха

**Iced** — настоящая x86/x64 encoder-библиотека от icedland — теперь
**работает на bare metal в kernel-AOT tier'е**. Конкретно: `Probe_IcedEncode`
зелёный, `new Assembler(64); a.mov(rax, rcx); a.Assemble(writer, 0);`
end-to-end эмитит канонические `48 89 C8` через наш `BufWriter`.

Это первый раз когда **полноценная сторонняя C# библиотека** (213 .cs
файлов) запекается в NativeAOT NoStdLib SharpOS kernel и реально живёт.
Все Iced'овские dependencies — `List<T>` / `Dictionary<K,V>` / `Span<T>` /
`ReadOnlySpan<T>` / `StringBuilder` / exception family — крутятся
через наш std. До step115 у нас не было `string.Format`,
`Array.Sort`, `Comparison<T>`, `Nullable<T>`, `IFormattable`,
`MinValue/MaxValue` на маленьких primitives, `RawData`, `m_pEEType`
field name, non-generic `IList`/`ICollection`, `RuntimeHelpers.CreateSpan`
intrinsic + другие мелочи — Iced wave заставила всё это закрыть в один
заход.

## Что вошло (по подсистемам)

### A — std разрастание (BCL-compat surface)

Все ниже — каноничные namespaces `System.*` / `System.Collections.Generic.*`
по правилу инварианта 2, реализации портированы из dotnet/runtime
дословно с минимальными cut'ами:

- **`String.Format(...)` × 4 overloads** + `AppendFormatHelper` парсер
  `{N[,width][:spec]}` + `ParamsArray` inline-3 — порт из BCL
  `String.Manipulation.cs`, минус `IFormatProvider` (всегда null), минус
  `ICustomFormatter` lookup, минус `s_*ArgArray` sentinel-statics
  (триггер нашего cctor trap'а). `IFormattable` consumers получают
  spec ("X", "N2") как в upstream.
  ([std/no-runtime/shared/StringFormat.cs](../std/no-runtime/shared/StringFormat.cs))

- **`IFormatProvider` / `IFormattable`** interfaces (canonical shapes).
  ([std/no-runtime/shared/Bcl/Interfaces.cs](../std/no-runtime/shared/Bcl/Interfaces.cs))

- **`Comparison<T>` delegate** — для `Array.Sort(arr, (a,b) => ...)`
  overload. Сам делегат runtime-managed в AOT — не работает; используется
  Iced (там мы локально вырезали lambda на insertion sort) и любым
  hosted-code'ом который попадает в std.

- **`Array.Sort` honest introsort** — порт `ArraySortHelper.cs` из BCL:
  quicksort с median-of-three pivot, insertion sort для партиций ≤16,
  heapsort backstop на превышении глубины `2 × log₂(n)`. Двойной
  движок: IComparable<T>-путь (прямой `CompareTo`, без virtual indirection)
  и IComparer<T>-путь. Все overloads (`Sort<T>(T[])`,
  `Sort<T>(T[], int, int)`, `Sort<T>(T[], IComparer<T>)`,
  `Sort<T>(T[], int, int, IComparer<T>)`, `Sort<T>(T[], Comparison<T>)`).
  ([std/no-runtime/shared/Runtime/Array.cs](../std/no-runtime/shared/Runtime/Array.cs))

- **`Nullable<T>`** honest impl — ctor, `HasValue`, `Value`,
  `GetValueOrDefault()`, `GetValueOrDefault(T)`, override'ы, операторы.
  Был пустой stub (`public struct Nullable<T> where T : struct { }`) — Roslyn
  lowering на `arr?.Length ?? 0` крашил `Sequence contains no elements` в
  `SyntheticBoundNodeFactory.New(typeof(Nullable<int>), [intValue]).Single(c => c.Parameters.Length==1)`.

- **Primitive `MinValue` / `MaxValue` константы** — `SByte`, `Byte`,
  `Int16`, `UInt16`, `Char` (раньше не было); `Single` / `Double` —
  полный набор (`MinValue`, `MaxValue`, `Epsilon`, `PositiveInfinity`,
  `NegativeInfinity`, `NaN`). Значения дословно из BCL bit-patterns.

- **`RuntimeHelpers.CreateSpan<T>(RuntimeFieldHandle)`** — intrinsic
  stub. Под `[Intrinsic]` ILC сворачивает `ldtoken <rva_field>` +
  call в прямой указатель на RData blob, тело не вызывается.
  Разблокирует `ReadOnlySpan<byte> x = new byte[] { ... }` и
  collection-expr `[1,2,3]` без allocation. Проба `Probe_CreateSpan` ✅.

- **Non-generic `System.Collections.IList` / `ICollection`** —
  legacy интерфейсы для типов с двойным наследием (modern generic +
  legacy non-generic). Канонические shapes.

- **Debugger/EditorBrowsable/Obsolete атрибуты** — zero-runtime stubs.
  `DebuggerDisplay`, `DebuggerBrowsable` + `DebuggerBrowsableState`,
  `DebuggerTypeProxy`, `DebuggerStepThrough`, `DebuggerHidden`,
  `DebuggerNonUserCode`, `EditorBrowsable` + `EditorBrowsableState`,
  `ObsoleteAttribute`. Iced'у нужны, BCL-port'ам тоже пригодится.
  ([std/no-runtime/shared/Bcl/DebuggerAttributes.cs](../std/no-runtime/shared/Bcl/DebuggerAttributes.cs))

### B — runtime contract fixes (NativeAOT ILC compatibility)

- **`Object.m_pEEType` rename** — наше поле header'а называлось
  `m_pMethodTable`, но NativeAOT `ILC.GenericUnboxingThunk.EmitIL()`
  ищет по точному имени через `Context.GetWellKnownType(Object).GetKnownField("m_pEEType")`
  ([BoxedTypes.cs:447](../dotnet-runtime-sharpos/src/coreclr/tools/aot/ILCompiler.Compiler/Compiler/CompilerTypeSystemContext.BoxedTypes.cs#L447)).
  Триггер: `Boxed_Enumerator<__Canon>.Dispose_Unbox()` thunk который
  Iced'овские `IEnumerator<T>` impls притащили. Поменяли в обоих
  MinimalRuntime (`OS/` + `apps/sdk/`).

- **`System.Runtime.CompilerServices.RawData` class + `byte Data` field** —
  второй known-name lookup в том же thunk'е
  ([BoxedTypes.cs:451 + :544](../dotnet-runtime-sharpos/src/coreclr/tools/aot/ILCompiler.Compiler/Compiler/CompilerTypeSystemContext.BoxedTypes.cs#L451)).
  `ldflda RawData::Data` даёт thunk'у `ref byte` указатель на payload
  boxed value-type'а сразу после `m_pEEType` header'а. Объявлен в обоих
  MinimalRuntime как `internal class RawData { public byte Data; }` —
  byte-перфектный port из NativeAOT runtime.

### C — Iced bake-in

- **213 .cs файлов** из upstream `icedland/iced` (MIT) положены в
  `OS/src/Iced/` — auto-picked SDK glob'ом (in-tree, никакого явного
  `<Compile Include>` не нужно).

- **DefineConstants** для encoder-only surface:
  - `ENCODER` + `BLOCK_ENCODER` — emitter side (без decoder/formatter)
  - `CODE_ASSEMBLER` — fluent `asm.mov(rax, rcx)` API
  - `HAS_SPAN` — у нас Span<T>/ROS<T> работают
  - `IcedNoIVT` — strip InternalsVisibleTo к UnitTests
  - `NO_EVEX` — AVX-512 path содержит `static readonly TryConvertToDisp8N
    tryConvertToDisp8N = TryConvertToDisp8NImpl.TryConvertToDisp8N;` —
    delegate-in-static-ref-field, обе половины несовместимы с AOT-tier
    (managed delegates + cctor trap)

- **LangVersion=latest** — net7.0 default = C#11; Iced использует C#12+.

- **2 файла выключены** из компиляции через `<Compile Remove>`:
  - `Intel/StreamCodeWriter.cs` — требует `System.IO.Stream`, у нас своя
    минимальная `BufWriter` шим
  - (`InstructionListDebugView.cs` сначала тоже исключили, но после
    `DebuggerBrowsable` stub'а вернули обратно — компилится)

- **Один локальный патч в Iced**:
  - `Intel/BlockEncoder.cs:189` — оригинальный
    `Array.Sort(blocks, (a, b) => a.RIP.CompareTo(b.RIP));` заменён
    inline insertion sort'ом (blocks.Length типично единицы, insertion
    sort оптимален на таких размерах). Хедер-коммент про причину —
    managed delegates AOT-tier 🔴 из feature table README.
  - `Intel/EncoderException.cs` — `[Serializable]` + `protected ctor
    (SerializationInfo, StreamingContext)` снесены (legacy
    BinaryFormatter, у нас нет SerializationInfo и в base Exception
    тоже).

### D — Probes

- **`Probe_IcedEncode`** в `NativeAotProbe` — `new Assembler(64); a.mov(rax, rcx);
  a.Assemble(w, 0);` → ожидаем 3 байта `48 89 C8`. ✅ Подтверждает
  работу GcStaticsMaterializer на Iced'овских `static readonly
  AssemblerRegister64 rax = new ...(Register.RAX)` register tables
  (десятки полей с ctor-init).

- **`Probe_CreateSpan`** — `ReadOnlySpan<byte> ros = new byte[] {0x11,0x22,...};
  sum == 0xFF` → подтверждает что ILC свернул intrinsic в RData-blob
  pointer, тело CreateSpan не исполнялось.

- **6 delegate-probes** в `work/normal-hello/Program.cs`:
  - `Func<T,R> no-capture` — static cached delegate
  - `Action<T> side-effect` — void-return Invoke path
  - `Func<T,R> closure` — display-class аллокация + captured field
  - `Func<T,R> method group` — `ldftn + newobj Func<int,int>(null,methodPtr)`
  - `Action multicast` — `CombineImpl` + invocation list walk
  - `Comparison<T> via Array.Sort` — точно та сигнатура что в std
  
  Все 6 ✅ на CoreCLR-hosted. AOT-tier (kernel/ELF-app) их не запускает
  by design.

### E — probe_report.ps1

Отдельные строки для `CreateSpanIntrinsic` и `IcedEncode` — чтобы
тихий FAIL не утонул в агрегированном NativeAotFeatures.

### F — Roslyn-крах'и устранены попутно

`?.` + `??` + params-array в `throw new X(string.Format(...))` сценариях
крашили Roslyn `LocalRewriter` со стеком в
`SyntheticBoundNodeFactory.New ... .Single() ThrowNoElementsException` —
до тех пор пока `Nullable<T>` был пустым stub'ом без ctor. Также убрали
два собственных `??=` / `?.` в новых stub-файлах. После `Nullable<T>`
honest port'а вся семья таких lowering'ов перестала падать.

## Файлы

### std (`std/no-runtime/shared/`)
- `StringFormat.cs` **NEW** — String.Format + AppendFormatHelper + ParamsArray
- `Bcl/Interfaces.cs` — + non-generic IList/ICollection, + IFormatProvider/IFormattable, + Comparison<T>
- `Bcl/DebuggerAttributes.cs` **NEW** — Obsolete + Debugger* + EditorBrowsable stubs
- `Runtime/Array.cs` — + Sort × 5 overloads + introsort engine (IComparable + IComparer paths)

### Kernel (`OS/`)
- `OS/src/Boot/MinimalRuntime.cs`:
  - `m_pMethodTable` → `m_pEEType` (rename, comment)
  - `Nullable<T>` empty stub → honest impl
  - + `System.Runtime.CompilerServices.RawData` class
  - + `Char/SByte/Byte/Int16/UInt16` const MinValue/MaxValue
  - `Single`/`Double` empty stubs → full const set (MinValue/MaxValue/Epsilon/Inf/NaN)
  - + `RuntimeHelpers.CreateSpan<T>(RuntimeFieldHandle)` `[Intrinsic]`
- `OS/OS.csproj` — `<LangVersion>latest</LangVersion>`, `DefineConstants` +=
  ENCODER;BLOCK_ENCODER;CODE_ASSEMBLER;HAS_SPAN;IcedNoIVT;NO_EVEX,
  `<Compile Remove>` StreamCodeWriter.cs, новые Compile Include'ы для std файлов
- `OS/src/Kernel/Diagnostics/Probes.cs` — (никаких новых toggle'ов, пробы
  под существующим NativeAotFeatures зонтом)
- `OS/src/Kernel/Diagnostics/NativeAotProbe.cs` — `Probe_CreateSpan`,
  `Probe_IcedEncode` (+ inner `BufWriter : Iced.Intel.CodeWriter`)
- `OS/src/Iced/` **NEW** — 213 .cs файлов upstream Iced (MIT)
- `OS/src/Iced/Intel/BlockEncoder.cs` — local patch: lambda → inline insertion sort
- `OS/src/Iced/Intel/EncoderException.cs` — local: `[Serializable]` + SerializationInfo ctor removed

### Apps (`apps/sdk/`)
- `apps/sdk/MinimalRuntime.cs` — то же что OS: `m_pEEType` rename + RawData

### Probe wiring
- `tools/probe_report.ps1` — + `CreateSpanIntrinsic` + `IcedEncode` строки
- `work/normal-hello/Program.cs` — 7. секция DELEGATES / LAMBDAS, 6 проб

### Docs
- `README.md` — feature table: + managed delegates row, + runtime x64 assembler row
- `CLAUDE.md` — раздел про `throw` halt'аем устарел; переписан под текущее
  состояние (EH работает). + Iced упоминание в "Для новой low-level задачи".
  + Array.Sort появился в стандартном порт-trim списке.

## Lessons learned

1. **NativeAOT runtime contract = name-match.** ILC ищет `m_pEEType` /
   `RawData.Data` через `GetKnownField`/`GetKnownType` по строковым
   именам. Минимальный layout (`IntPtr` vs canonical `MethodTable*`) — не
   важен, имя — критично. Сэйв'ит дни диагностики если знать что
   `Boxed_Enumerator<__Canon>.Dispose_Unbox()` thunk эмиттится для
   ЛЮБОГО generic `IEnumerator<T>` с struct-backed value, и **обе**
   зависимости (Object field + RawData type) надо иметь в SystemModule.

2. **Roslyn-крах = signature mismatch на BCL types.** `Sequence contains
   no elements` в `SyntheticBoundNodeFactory.New(...).Single(...)` —
   фактически "ожидаемый ctor с N параметрами не найден". `Nullable<T>`
   без `(T value)` ctor крашит лавину lowering'ов (`a?.b ?? 0`,
   `??=`, тернарки на nullable). Honest port BCL-типов сразу — а не
   лениво по мере "пока всё работает" — экономит итерации.

3. **Iced на AOT — реальный use case.** Изначально было опасение что
   `static readonly AssemblerRegister64 rax = new ...` — это
   `ClassConstructorRunner trap`. Оказалось GcStaticsMaterializer
   переваривает их (десятки полей одного типа, всех в один cctor). Это
   расширяет наше представление о пригодных для AOT BCL-port'ов: не
   "никаких static reference fields", а "никаких lazy-init и no-op
   cctor'ы" — обычные ctor-init статикс в cctor'е работают.

4. **`?.` + `??` lowering — наиболее частая поверхность Roslyn-fragility.**
   Скан новых файлов на эти комбинации перед сборкой сэкономил бы один
   build-cycle. Сейчас grep — пара секунд, value пропорционально размеру
   нового PR'а.

5. **Замена `Array.Sort(arr, lambda)` на inline insertion sort** —
   универсальный workaround для AOT-tier'а пока managed delegates не
   приземлятся. Стоимость низкая (`n²` на маленьких n меньше overhead'а
   делегата), читается явно.

## Что откладывается / следующие

- **Managed delegates AOT** — реально нужны для произвольных third-party
  BCL-port'ов. Сейчас обходим (inline patches, `delegate* unmanaged<T>`).
  Закрытие — отдельная фронт.
- **Iced EVEX/AVX-512** — пока `NO_EVEX`. Когда понадобится AVX-512
  emission, или закрываем delegate-в-статике инфраструктурой, или
  патчим тот specific helper.
- **`Array.BinarySearch`** — не было раньше, не было нужно сейчас. Когда
  кому-то понадобится — взять из BCL.
- **`apps/sdk/MinimalRuntime.cs`** — те же удобства (RawData, Nullable
  honest, MinValue/MaxValue на маленьких primitives, Sort) — пока не
  затащены, ELF-apps пока проще. Затащим когда первый ELF-app коснётся
  тех же паттернов.

## Acceptance

```
[Phase4]
  GcHeapSmoke....................... OK  (val=end)
  GcStress.......................... OK  (val=end)
  NativeAotFeatures................. OK  (val=end)
  CreateSpanIntrinsic............... OK  (ok)
  IcedEncode........................ OK  (ok)
  Cctor............................. OK  (ok)
```

CoreCLR-hosted census: `OK=56 DEG=2 FAIL=7`. Все 7 FAIL и 2 DEG —
known limits из README "Известные проблемы". Все 6 новых delegate-проб
✅ — confirmed CoreCLR-hosted delegate machinery alive.

Кодовый размер: +213 файлов Iced, ~+1100 строк std/runtime, ~+150 строк
проб. Final kernel size: ожидаемый прирост ~150-200 KB BOOTX64.EFI от
Iced encoder tables.
