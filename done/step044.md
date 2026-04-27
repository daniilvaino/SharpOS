# Step 44 — Phase 1 step 1: full Exception shape + 6 derived types + ExceptionIDs

## Контекст

Первый step из 11-step Phase 1 try/catch roadmap. Цель — закрыть Exception contract: полный 88-byte BCL-compatible layout + все exception types которые runtime EH dispatch и Roslyn-emitted code ожидают существующими. Smoke gate L4 = 127.

Три consecutive раунда research'а с двумя мудрецами + два empirical probe'а на текущем kernel binary привели к refined plan мудреца 2 (записан в `done/phase1-trycatch-roadmap.md`):

- Option A (full unwinder, не longjmp).
- Sequencing managed-dispatcher-first, RhpCallCatchFunclet рано.
- Phase 1 closure только после step 11 (включая filter/fault/collided/HW-bridge/rich stack trace) — НЕ останавливаемся на 1.5b.
- Полный 88-byte Exception layout сразу, не minimal-MVP.
- Все 6 missing derived types в первой итерации.

## Решение

### Полный Exception shape

`std/no-runtime/shared/Exception.cs` — 88 bytes, 10 fields в declaration order matching `gc-experiment/dotnet-runtime/src/coreclr/nativeaot/System.Private.CoreLib/src/System/Exception.NativeAot.cs:33-55`:

```
internal string  _message;
private  object  _data;             // placeholder for IDictionary
private  Exception _innerException;
private  string  _helpURL;
private  string  _source;
private  int     _HResult;
private  string  _stackTraceString;
private  string  _remoteStackTraceString;
private  IntPtr[] _corDbgStackTrace;
private  int     _idxFirstFreeStackTraceEntry;
```

Конструкторы `()` / `(string)` / `(string, Exception)`. `HResult` инициализируется в `COR_E_EXCEPTION` (0x80131500). Properties: `Message`, `InnerException`, `HResult`, `StackTrace`, `Source`, `HelpLink`, `Data`. Internal `GetStackIPs()` + `HasBeenThrown` готовы для wiring в step 11 (rich stack trace).

ILC layout pack reference fields auto, value fields последними — declaration order не bit-exact, но при необходимости один-line добавления `[StructLayout(Sequential)]` это починит. Для step 1 bit-exactness не нужен.

### 6 missing derived types + relocation existing

`std/no-runtime/shared/Exceptions.Derived.cs` — все производные types в одном файле:

**Existing** (перенесены из `Threading.cs` без изменений):
- `InvalidOperationException`, `NotSupportedException`
- `ArgumentException`, `ArgumentNullException` (+ `ThrowIfNull`)
- `ArgumentOutOfRangeException`
- `OutOfMemoryException`, `IndexOutOfRangeException`, `FormatException`

**New** (sage 2 plan):
- `ArithmeticException` — base for `DivideByZeroException` + `OverflowException`
- `DivideByZeroException`, `OverflowException`
- `InvalidCastException`, `ArrayTypeMismatchException`
- `NullReferenceException`, `NotImplementedException`

Все три конструктора `()` / `(string)` / `(string, Exception)` для каждого.

### ExceptionIDs enum

`std/no-runtime/shared/Runtime/ExceptionIDs.cs` — verbatim port из `gc-experiment/dotnet-runtime/src/coreclr/nativeaot/Runtime.Base/src/System/Runtime/ExceptionIDs.cs`. 12 values (`OutOfMemory=1` ... `AmbiguousImplementation=12`). Values must match exactly — runtime передаёт raw enum value и ожидает classlib's `GetRuntimeException` вернуть concrete экземпляр matching type.

### GetRuntimeException rewrite

`OS/src/Boot/ExceptionEngine.cs:GetRuntimeException(int id)` → `GetRuntimeException(ExceptionIDs id)`. Switch по enum, returns concrete derived types — mirror `Test.CoreLib's RuntimeExceptionHelpers.cs`. Раньше возвращал generic `Exception("runtime exception (id=" + id + ")")`, теперь:

```csharp
case ExceptionIDs.NullReference:    return new NullReferenceException();
case ExceptionIDs.DivideByZero:     return new DivideByZeroException();
case ExceptionIDs.Overflow:         return new OverflowException();
// ... etc
```

## L4 smoke probe

`EhProbe.ExceptionShape()` — bitmask 7 bits:
1. `new InvalidOperationException("m").Message == "m"` (Message round-trip через virtual property).
2-7. `new TypeX() is TypeX` для каждого из 6 missing types (NullRef, Overflow, DivByZero, InvalidCast, ArrayTypeMismatch, NotImplemented).

Все 7 set → `val=127`. Никаких throws — это step 1 gate, не зависит от unwinder.

`Probes.EhExceptionShape = true` toggle.

## Roadmap fixed

`done/phase1-trycatch-roadmap.md` (480+ строк) — single source of truth:

- Зафиксированные решения (Option A, sequencing, closure после step 11, XMM spill оставлен, 4-opcode decoder, stock trailer formula, kind=3 = parser bug).
- Текущее состояние binary (численные данные из probe_unwind_codes.ps1 + probe_eh_trailer.ps1).
- 11 steps с smokes / файлами / зависимостями + hard gates между шагами.
- **Step 5 detailed breakdown** в 6 sub-steps (5.1..5.6) от мудреца 2: layouts (ExInfo=0x260, PAL=0x100, REGDISPLAY=0x130), shellcode listings для RhpThrowEx + RhpCallCatchFunclet с opcode bytes, smoke checkpoint chains, failure-localization hints.

Sage Q&A архивы:
- `done/sage-question-phase1-trycatch.md` — round 1 (общий strategy + technical depth).
- `done/sage2-question-phase1-refine.md` — round 2 (refine с empirical data от probes).
- `done/sage2-question-step5-subbreakdown.md` — round 3 (step 5 sub-breakdown request).

Каждый файл сохранён для retroactive auditing какие решения и почему были приняты.

## Файлы

### Новые

- `std/no-runtime/shared/Exception.cs` — full BCL Exception layout.
- `std/no-runtime/shared/Exceptions.Derived.cs` — 14 derived types.
- `std/no-runtime/shared/Runtime/ExceptionIDs.cs` — enum.
- `done/phase1-trycatch-roadmap.md` — Phase 1 roadmap.
- `done/sage-question-phase1-trycatch.md` — sage round 1.
- `done/sage2-question-phase1-refine.md` — sage 2 round 2.
- `done/sage2-question-step5-subbreakdown.md` — sage 2 round 3.
- `done/step044.md` — этот файл.

### Изменённые

- `OS/src/Boot/ExceptionEngine.cs` — `GetRuntimeException(ExceptionIDs)` switch.
- `std/no-runtime/shared/Threading.cs` — exception types удалены, остались `Interlocked` + `Environment`.
- `OS/src/Kernel/Diagnostics/EhProbe.cs` — `ExceptionShape()` (L4 probe).
- `OS/src/Kernel/Diagnostics/Probes.cs` — `EhExceptionShape` toggle.
- `OS/OS.csproj` — 3 новых `Compile Include` для новых std файлов (csproj использует explicit includes, не wildcard).
- `.gitignore` — добавлены `unwind_scan.txt`, `eh_trailer_scan.txt` (regenerable probe outputs).

## Результат

```
[info] eh L1 try/finally no-throw: val=211        (existing, no regression)
[info] eh L2 try/catch no-throw: val=4            (existing, no regression)
[info] eh L4 exception shape: val=127             <-- step 1 GATE GREEN
... NativeAotProbe + CctorProbe + ELF apps + launcher all green.
```

## Что дальше

Phase 1 progress: 1/11. Step 45 = step 2 — Coff method lookup + funclet→ROOT backward walk (gate L5=7).
