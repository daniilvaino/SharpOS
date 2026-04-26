# Step 37 — Phase 1a-min: managed throw → readable panic

## Контекст

Phase 1 — самый рисковый блок плана: managed exception handling, ClassConstructorRunner, ACPI parsing, RTC/HPET. Решили начать с самого тяжёлого — managed exceptions.

Research'ем NativeAOT EH (см. `gc-experiment/dotnet-runtime/src/coreclr/nativeaot/Runtime.Base/src/System/Runtime/ExceptionHandling.cs`) выявил что full unwinder = 3-6 месяцев focused work + высокий риск (зависит от ILC-emitted EH table format'а который может быть недокументирован, требует stack walker, integration с pager/W^X). До CoreCLR в Phase 6 это очень дорого.

Поэтому Phase 1 для exceptions разбивается на milestones:
- **1a-min** (this step): `throw new X("msg")` → читаемый panic с type + message + halt. Без catch.
- **1a-extended** (если понадобится): setjmp/longjmp ExceptionContext для explicit try/catch без Roslyn syntax.
- **1a-full** (если CoreCLR не справится): full unwinder. Skip — CoreCLR в Phase 6 принесёт настоящий BCL EH.

Этот шаг — minimum viable improvement. ~150 строк кода, 1 day work, разблокирует читабельность реальных багов в kernel и BCL-ported коде.

## Что сделано

### `OS/src/Boot/ExceptionEngine.cs` (новый)

Все необходимые `[RuntimeExport]` функции для exception path'а:

- **`RhpThrowEx(object)`** — entry point ILC emits для IL `throw`. Печатает type + message + halt.
- **`RhpRethrow(object)`** — для IL `throw;` (rethrow).
- **`RhpThrowHwEx(uint, IntPtr)`** — bridge для hardware faults (currently unused — IDT идёт через PanicDump в step 35).
- **`GetRuntimeException(int)`** — classlib hook для конвертации exception ID → object.
- **`FailFast(reason, exception, addr, ctx)`** — terminal failure handler.
- **`AppendExceptionStackFrame(obj, IP, flags)`** — stack trace builder hook (no-op без unwinder'а).
- **`OnFirstChanceException(e)` / `OnUnhandledException(e)`** — instrumentation hooks (no-op).

`PrintExceptionInfo` — формат-дамп. Избегает `is Exception` cast (требует `RhTypeCast_IsInstanceOfClass` helper), вместо этого direct memory access через known field offsets:

```
Exception layout: [MT*](8) [_message reference](8) ...
String layout:    [MT*](8) [Length](4) [chars...](length*2)
```

Тот же паттерн что в `MemoryExtensions.StringHelpers.GetFirstCharRef`. Безопасно потому что IL spec гарантирует — только System.Exception subtypes можно throw'ать.

### `OS/src/Kernel/Diagnostics/ExceptionProbe.cs` (новый)

Триггерит `throw new InvalidOperationException("test message from exception probe")`. Gated в Kernel.cs `if (false)` — flip для верификации.

### `OS/src/Kernel/Kernel.cs` (модифицирован)

Добавлен `RunExceptionThrowProbe()` после `RunIdtPanicProbe`, перед `RunElfValidation`.

## Что НЕ покрыто

- **try/catch syntax** — IL `try`/`catch` ILC emit'ит с `.pdata`/`.xdata` unwind tables, но без StackFrameIterator + DispatchEx эти tables не используются. Throw → halt, catch handlers недостижимы.
- **finally блоки** — same. Не run'ятся при unwinding (потому что нет unwinding).
- **Hardware fault → managed exception** — IDT step 35 идёт прямо в PanicDump. RhpThrowHwEx как stub для совместимости с ILC linkage.
- **Stack trace** — без unwinder'а нечем.

Это всё придёт либо с longjmp milestone (если будет реально нужно catch в kernel-tier), либо с CoreCLR в Phase 6.

## Верификация

```
[info] exception probe: throw new InvalidOperationException
*** UNHANDLED EXCEPTION ***
message: test message from exception probe
*** halting ***
```

Successfully boots, all 58 probes green when probe gated false. When flipped to true — exception probe fires last and produces the dump above.

## Ловушки которые обошёл

### `is Exception` cast → RhTypeCast_IsInstanceOfClass

C# `is X` для class type триггерит ILC link на runtime helper. Без него — LNK2001. Замена: direct memory access (`*(nint*)(objAddr + 8)` для `_message`). Безопасно по IL invariant.

### `Exception ex = (Exception)obj;` cast

Тоже требует typecheck helper (RhTypeCast_CheckCast). Same fix — избегаем cast'ов через memory access.

### `nint` arithmetic

Прямое чтение через `*(int*)(addr + 8)` для length и `(char*)(addr + 12)` для chars работает в unsafe context, no helpers.

### Console.Write vs Console.WriteChar в exception path

Проигнорировал риск что Console.Write может аллоцировать (через NumberFormatting). Для нашего случая — мы пишем string literals и char-by-char, без аллокаций. Если бы дёргали `Console.WriteUInt`, было бы рискованно.

## Файлы

### Новые

- `OS/src/Boot/ExceptionEngine.cs` — все [RuntimeExport] helpers.
- `OS/src/Kernel/Diagnostics/ExceptionProbe.cs` — manual verification probe.
- `done/step037.md` — этот файл.

### Изменённые

- `OS/src/Kernel/Kernel.cs` — `RunExceptionThrowProbe()` gated в `if (false)`.

## Что дальше — оставшиеся пункты Phase 1

1. **ClassConstructorRunner портирование** — разблокирует все static reference fields с initializer'ами. Включая каноничный `string.Empty` field. Дроп `--resilient` режима.

2. **ACPI parsing** — RSDP discovery → RSDT/XSDT → MADT (APIC topology) + HPET (timer base) + MCFG (PCIe ECAM).

3. **RTC + HPET/TSC timekeeping** — wall-clock через CMOS ports, high-resolution через HPET memory-mapped или RDTSC, `Stopwatch` API.

После Phase 1 → Phase 2 (PAL design + de-risk spike).
