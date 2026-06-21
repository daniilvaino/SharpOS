# ASM inventory — все hand-rolled byte-emitters в кодбейзе

Снимок состояния перед массовой миграцией на BootAsm.Generator (step
118+). Категоризация по: (1) где живёт байт-эмитер, (2) есть ли managed
callback hole, (3) статус миграции.

Цель — перевести **всё** на compile-time codegen. После step 118 в
дереве не остаётся ни одной строки руками-написанного `target[p++] =
0xNN`.

## Метрики

- **9 эмитеров уже мигрированы** (3 compile-time, 6 runtime-Iced)
- **13 эмитеров ещё руками** (12 patcher'ов + 2 нестандартных)
- **~145 строк** прямых byte-writes в коде (`p[i++] = 0xN`-стиль) до
  миграции; ~80 уже устранены.

---

## Категория A — runtime-Iced мигрировано в step 116 (СОХРАНЯЕМ)

**Намеренно остаются runtime emit'ом** — это отдельная демонстрация
capability: мы умеем эмитить шеллкод через Iced и на runtime когда
managed-адреса доступны только на boot. Это **фича**, не долг. SehDispatch
выделен в compile-time (M5.1) только потому что bodies были не привязаны
к runtime-known данным.

| Стаб | Файл | Bytes | Holes? | Статус |
|---|---|---|---|---|
| **SehDispatch.EmitCapture** | `PAL/SharpOSHost/SehDispatch.cs` | 138 | нет | ✅ compile-time M5.1 |
| **SehDispatch.EmitRestore** | same | 137 | нет | ✅ compile-time M5.1 |
| JumpStub | `Kernel/Exec/JumpStub.cs` | 63 | нет | runtime Iced (по дизайну) |
| BigStack | `Kernel/Memory/BigStack.cs` | 18 | нет | runtime Iced (по дизайну) |
| Cr3Accessor (read/write) | `Kernel/Paging/Cr3Accessor.cs` | 4 + 7 | нет | runtime Iced (по дизайну) |
| wrmsr GS_BASE | `Kernel/Diagnostics/CoreClrProbe.cs` | 18 | нет | runtime Iced (по дизайну) |
| AppServiceBuilder Win64 thunks | `Kernel/Process/AppServiceBuilder.cs` | 21 × 12 | **да** — managed target | runtime Iced (по дизайну) |
| AppServiceBuilder SysV thunks | same | 24 × 12 | **да** — то же | runtime Iced (по дизайну) |

→ **Не трогаем.** Демонстрирует что наш Iced fork работает в kernel
runtime end-to-end. Compare-gate vs legacy уже валидирован.

---

## Категория B — compile-time мигрировано в step 117

| Стаб | Файл | Bytes | Pattern |
|---|---|---|---|
| **GcStackSpill** | `Kernel/Memory/GcStackSpill.cs` + `.Iced.cs` | 35 | 0-hole, M4 |
| **SehDispatch.EmitCapture** | `PAL/SharpOSHost/SehDispatch.cs` + `.BootAsm.cs` | 138 | 0-hole, M5.1 |
| **SehDispatch.EmitRestore** | same | 137 | 0-hole, M5.1 |

---

## Категория C — ещё руками: patcher'ы которые перезаписывают managed method bodies

Все Phase2-3, очень ранние. Каждый имеет **managed callback hole** (или
несколько) — адрес managed static field / static method. Идеальный case
для M6 `MovHole`/`JmpHole`.

| Patcher | Файл | Bytes | Holes (managed targets) | Заметки |
|---|---|---|---|---|
| **CaptureContextPatcher** | `Boot/EH/CaptureContextPatcher.cs` | 37 | 0 — все через rcx | Capture CPU context в PAL_LIMITED_CONTEXT, ABI-driven |
| **ThrowExPatcher** | `Boot/EH/ThrowExPatcher.cs` | 27 + holes | 1+ — DispatchEx callback | Builds PAL_LIMITED_CONTEXT + ExInfo на стеке, зовёт managed DispatchEx |
| **RethrowPatcher** | `Boot/EH/RethrowPatcher.cs` | 27 + holes | 1+ — DispatchEx callback | То же что Throw но для `throw;` (rethrow) |
| **CallCatchFuncletPatcher** | `Boot/EH/CallCatchFuncletPatcher.cs` | 24 + 1 hole (`s_head`) | 1 — managed `s_head` static field | Самый сложный, 211 строк, REGDISPLAY restore + ExInfo head pop |
| **CallFinallyFuncletPatcher** | `Boot/EH/CallFinallyFuncletPatcher.cs` | 26 + holes | 1 — managed `s_head` | finally funclet вариант |
| **CallFilterFuncletPatcher** | `Boot/EH/CallFilterFuncletPatcher.cs` | 25 + holes | 0–1 | filter funclet вариант |
| **BootStackSwitchPatcher** | `Boot/BootStackSwitchPatcher.cs` | 2 + 1 hole | 1 — stack pointer constant | switch RSP на boot |
| **ByRefAssignRefPatcher** | `Kernel/Memory/ByRefAssignRefPatcher.cs` | 2 + 1 hole | 1 — managed write barrier | 15-byte ref-assign helper |
| **InterfaceDispatchPatcher** | `Kernel/Memory/InterfaceDispatchPatcher.cs` | ~5 (rel32 jmp) | 1 — `InterfaceDispatchBridge` addr | Просто 5-байтный `JMP rel32` к bridge |
| **InterfaceDispatchBridge** | `Kernel/Memory/InterfaceDispatchBridge.cs` | **195** | 1+ — managed `InterfaceDispatchResolver.Resolve` | 268 строк, длиннющий — full interface dispatch resolver |
| **PortIoPatcher** | `Hal/PortIoPatcher.cs` | 8 + 7 (Inb + Outb) | 0 — args через регистры | Splits в 2 стаба, `in al, dx` + `out dx, al` |
| **ChkstkPatcher** | `PAL/SharpOSHost/ChkstkPatcher.cs` | 1 (=`0xC3`) | 0 | No-op `ret` patch на `__chkstk` |

Все 12 — кандидаты для **compile-time migration через M6 holes**. Сразу
после migration их `*Stub.cs` файлы становятся trivial managed
declarations (которые они и так есть, с panic-fallback body), а patcher
заменяется на `BootAsm.Generator`-произведённую `Emit(byte* dst, void**
callback)`-style функцию + caller-side patch.

## Категория D — ещё руками: GcContextSpill + X64Asm + IdtTrampolines

| Стаб | Файл | Bytes | Holes? | Заметки |
|---|---|---|---|---|
| **GcContextSpill** | `Kernel/Memory/GcContextSpill.cs` | 8 byte-writes (~185 lines) | **да, 1+** — managed callback | Sister of GcStackSpill, **precise GC** version: captures full CPU register set в Context struct, потом managed callback |
| **X64Asm** | `Hal/X64Asm.cs` | **36 byte-writes**, 559 lines | вероятно нет | Контейнер для **множества** мелких стабов (STI/CLI/HLT, CoopSwitch, и т.д.). Каждый отдельная функция |
| **IdtTrampolines** | `Hal/Idt/IdtTrampolines.cs` | 25 byte-writes, 202 lines | сложно — interrupt vector | IDT entry trampolines для каждого vector'а. Нет managed callback'ов в классическом смысле — diverge к kernel HW handler |

X64Asm — **самый "размазанный"** случай: 559 строк = N мелких стабов
(CoopSwitch, разные no-op для bootstrap'а, и т.д.). Каждый — отдельный
0-hole стаб для compile-time миграции. Большой количественный winner.

IdtTrampolines — **уникальный**: они должны быть в фиксированной таблице
с известными offset'ами (IDT.set_entry читает их адреса). Compile-time
emit генерит template, runtime patcher положит template в IDT-slot
buffer. Holes только если interrupt-handler adresses нужны — обычно нет
(jmp к HW handler из fixed location).

---

## Полный план миграции — step 118+

**Не трогаем категорию A** (runtime-Iced cohort) — это сохраняем как
demonstration of runtime capability. Migration focus: только 13
hand-rolled byte emitters.

### Wave 1 — patcher'ы 0–1 hole (категория C маленькие)

- ChkstkPatcher (1 байт, 0 holes — самый дешёвый)
- PortIoPatcher (8+7 байт, 0 holes — args через рег.)
- BootStackSwitchPatcher (3 байта, 1 hole)
- ByRefAssignRefPatcher (15 байт, 1 hole)
- InterfaceDispatchPatcher (5 байт `JMP rel32`, 1 hole)
- CaptureContextPatcher (37 байт, 0 holes — pure CPU context save)

### Wave 2 — EH patcher'ы (категория C большие, multi-hole)

- ThrowExPatcher (DispatchEx callback)
- RethrowPatcher (то же)
- CallCatchFuncletPatcher (s_head + funclet entry)
- CallFinallyFuncletPatcher
- CallFilterFuncletPatcher

### Wave 3 — InterfaceDispatchBridge (195 байт)

Самый длинный стаб, нужны cross-stub references (он зовётся через
`JMP rel32` из InterfaceDispatchPatcher; внутри зовёт managed
`InterfaceDispatchResolver.Resolve`). Возможно нужны новые walker
features.

### Wave 4 — X64Asm + GcContextSpill + IdtTrampolines

X64Asm — много отдельных мелких функций. Каждая = свой `[CompileTimeAsm]`
stub. Простая массовая работа.

GcContextSpill — больше CONTEXT-capture логики чем у GcStackSpill, но та
же машинерия (push'и + callback + pops). Прямая аналогия GcStackSpill
M4 миграции.

IdtTrampolines — может потребовать compile-time arrays of stubs (одна
функция = много trampolin'ов). Walker уже умеет array indexing? Нет.
Возможно генератор должен поддержать "generate N copies of this stub
with different SentinelBase per N" — это feature request на walker.

---

## Что разблокирует BootAsm на M7

- Walker УЖЕ покрывает: 0-arg / register / int literal / memory operand
  (`__qword_ptr[base ± disp]`) / MovHole / JmpHole / string literal.
- **Не покрывает** (когда понадобится — расширим):
  - Conditional emit (e.g. only emit X if param meets condition)
  - Loops in body (for-style unrolling — например emit 16 идентичных
    pushes)
  - Cross-stub references (call/jmp к другому stub'у)
  - Multiple holes на одной инструкции (например ModR/M + imm64 patch'и
    одновременно — но это редко)

Каждое расширение — 5-20 строк в walker'е.

---

## Acceptance criteria для step 118 (полная миграция)

После step 118+:
- `grep -rE "0x[0-9A-Fa-f]{2}" OS/src/Boot OS/src/Kernel OS/src/Hal
  OS/src/PAL` показывает только const'ы (`const byte JmpOpcode = 0xE9`)
  + ENUM values, никаких прямых `p[i++] = 0xNN`-writes
- Все `*Patcher.cs` файлы удалены либо превращены в trivial
  `Emit(dst, …)` + caller-side patch
- `bootasm/BootAsm.Generator/` content + Iced source = единственное
  место где описаны х64 байты
- Runtime perf: zero allocation, zero cctor, на всех stub'ах (как
  верифицировано на трёх мигрированных)
- ILC binary size: ожидаемое **уменьшение** (Iced-runtime dead-code
  elimination после M5.1 cleanup + потенциально dead-code elimination
  body methods если решим помечать `[Conditional("NEVER")]`).
