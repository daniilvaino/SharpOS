# step 118 — migrate all shellcode emitters to BootAsm.Generator

## Веха

Перевод **всех** byte-by-byte byte-emitter'ов kernel'а на compile-time
codegen через `BootAsm.Generator` (step 117) + Iced. Покрыты 16 стабов в
6 волнах, walker расширен под labels / ref-args / data slots /
push-imm32-holes. В live прод-пути **не осталось ни одного раскатанного
по байтам шеллкода** — всё что генератор может, генератор делает.

Остаются только намеренные:
- `EmitLegacy` oracle pairs + `CompareOrPanic` parallel-emit для
  высокоставочных стабов (compile-time output verified byte-for-byte
  против rolled-by-hand reference каждый boot) — снимаются отдельным
  cleanup пассом после N зелёных прогонов.
- Per-vector `WriteVectorStub` тоже мигрирован (см. Wave 6).

## Wave 1 — patcher'ы с holes (steps 116→117 land)

| Patcher                       | Bytes | Holes              |
|-------------------------------|-------|--------------------|
| ChkstkPatcher                 |   1   | —                  |
| PortIoPatcher (Inb/Outb)      | 8/7   | —                  |
| CaptureContextPatcher         |  53   | —                  |
| BootStackSwitchPatcher        |  17   | —                  |
| ByRefAssignRefPatcher         |  15   | —                  |
| InterfaceDispatchPatcher      |   5   | 1 × `JmpRelHole`   |

## Wave 2 — EH funclet patchers

| Patcher                       | Bytes | Holes                              |
|-------------------------------|-------|------------------------------------|
| CallCatchFuncletPatcher       | 137   | 1 × `MovHole` (head)               |
| CallFinallyFuncletPatcher     | 168   | 0                                  |
| CallFilterFuncletPatcher      | 105   | 0                                  |
| RethrowPatcher                | 150   | 2 × `MovHole` (head, ingress)      |
| ThrowExPatcher                | 150   | 2 × `MovHole` (head, ingress)      |

## Wave 4 — GcContextSpill + X64Asm (10 stubs)

`GcContextSpill.Emit` — 16-mov spill caller's регистров в `Context*` +
capture Rip/Rsp + call rdx, 0 holes, args via Win64 ABI.

`X64Asm`: `EmitXsetbv`, `EmitReadCr4`, `EmitReadGsQword`,
`EmitWriteGsBaseMsr`, `EmitReadGsBaseMsr`, `EmitCmpXchg64`, `EmitXchg64`,
`EmitMemoryBarrier`, `EmitFxsave`, `EmitResume` — все 0 holes,
args через Win64 ABI.

Iced disp8 form для small offsets сэкономил 3-6 байт на стаб против
force-disp32 формы legacy эмиттеров (`EmitResume` 114→66 байт).

## Wave 5 — walker extensions + 3 deferred стаба

Walker до Wave 5 не умел:
1. **Local var declarations** для Iced Labels.
2. **`ref` arguments** (Iced `void Label(ref Label l)`).
3. **Locals lookup** в `EvaluateAtom` (для `__qword_ptr[label]` и conditional jumps к лейблу).
4. **Data slots** — runtime-patched qword-указатели в конце стаба.

Расширения в `BootAsm.Generator/BootAsmGenerator.cs`:

```
var slow = a.CreateLabel();        // var-decl → InvokeCallExpr → locals dict
a.jz(slow);                        // identifier lookup в locals (EvaluateAtom fallback)
a.Label(ref slow);                 // ref-arg unwrap byref ParameterType + write-back
__qword_ptr[resolverData]          // existing indexer + locals lookup
h.DataSlotHole(a, ref label, ...)  // emit 8-byte sentinel 0xD1F0_DA7A | ord
```

Sentinel scan расширен на standalone 8-byte qword (distinct high mark
от MovImm64's `0xD1F0_DEAD`). Patch line emission для `DataSlot8` =
тот же шаблон что `MovImm64` (`*(ulong*)(dst+N) = (ulong)(nuint)<name>`).

`InvokeCallExpr` (для `var X = a.CreateLabel()` RHS) допускает trailing
optional params — Iced `CreateLabel(string? name = null)` не требует
exact arg-count match.

Overload resolution в main dispatch теперь unwrap'ит byref
`ParameterType` (для `ref Label` param: `pt.IsByRef ? pt.GetElementType() : pt`)
+ delegate-через-reflection invoke с object[] preserve'ит ref-back
семантику.

| Patcher                       | Bytes | Holes                                  |
|-------------------------------|-------|----------------------------------------|
| CoopSwitch (X64Asm)           |  ~60  | 0 + 1 Iced label (skipGs)              |
| InterfaceDispatchBridge       | ~190  | 2 × `DataSlotHole`, 5 Iced labels      |
| IdtTrampolines.CommonStub     |  ~70  | 1 × `DataSlotHole`, 1 Iced label       |

`InterfaceDispatchBridge` старый `WriteShellcode` ушёл под `#if false`
как reference.

## Wave 6 — WriteVectorStub (per-vector IDT trampolines)

Добавлен `HoleKind.PushImm32` + `GenHoleCollector.PushImm32Hole` —
sentinel `68 ord_lo ord_hi AD DE` (push imm32 placeholder), scan
расширен на `0xE9`/`0x68` opcode dispatch, patch line
`*(uint*)(dst+N) = (uint)<name>;`.

Vector # encoded как `push imm32` (5B) вместо `push imm8` (2B) — CPU
читает qword со стека, старшие 3 байта нулевые, семантически
идентично. 16-byte slot не превышается.

Два body-варианта по error-code-mask:

```
EmitVectorStubNoErr (non-error vectors, 12B + 4× NOP):
  6A 00              push 0          ; dummy err
  68 <vector-hole>   push <vec>      ; PushImm32Hole
  E9 <jmp-hole>      jmp commonStub  ; JmpRelHole
  90 90 90 90

EmitVectorStubWithErr (error vectors, 10B + 6× NOP):
  68 <vector-hole>
  E9 <jmp-hole>
  90 90 90 90 90 90
```

`WriteVectorStub` теперь thin dispatch на одну из двух Emit-партиалов
по `VectorHasErrorCode(vector)`.

## Walker — финальное покрытие

```
// Lexical body grammar:
EXPR     ::= ATOM | INDEXER | EXPR ('+'|'-') EXPR
ATOM     ::= NUMBER | IDENT
INDEXER  ::= IDENT '[' EXPR ']'

// Statement forms:
'a.' METHOD '(' ARGS ')'
'h.' METHOD '(' ARGS ')'
'var' IDENT '=' CALL_EXPR              ; CALL_EXPR ::= 'a.' METHOD '(' ARGS ')'

// Arg forms:
ARG      ::= EXPR | 'ref' IDENT | STRING_LITERAL | 'a' | 'h'

// Identifiers resolve in order:
//   1. AssemblerRegisters static field/property
//   2. Local-var scope (declared via `var X = a.CreateLabel()`)
```

Holes API:
```
h.MovHole(a, reg, "name")              ; mov reg, imm64 — patch (ulong)(nuint)<name>
h.JmpHole(a, scratchReg, "name")       ; mov scratch, imm64; jmp scratch
h.JmpRelHole(a, "name")                ; jmp rel32 — patch rel32 displacement
h.DataSlotHole(a, ref label, "name")   ; place label + dq imm64 — patch as MovHole
h.PushImm32Hole(a, "name")             ; push imm32 — patch (uint)<name>
```

## Cleanup (тот же commit)

`EmitLegacy` + `CompareOrPanic` parallel-emit oracle удалены из
5 patcher'ов после Wave 6 зелёного прогона:
- `ChkstkPatcher`, `BootStackSwitchPatcher`, `CaptureContextPatcher`,
  `PortIoPatcher`, `GcStackSpill`.
- `SehDispatch` — вызовы EmitCapture/EmitRestore + CompareOrPanic
  убраны из EnsureShellcodeReady; сами методы (140B капчер + 137B
  restore + helpers) обёрнуты в `#if false` как REFERENCE.

**Оставлен compare-gate** (explicit KEPT marker, silent GC-heap
corruption risk):
- `ByRefAssignRefPatcher` — fires на каждой managed byref assignment,
  defensive canary надолго.

Dead-reference `WriteShellcode` обёрнут в `#if false`:
- `GcContextSpill.cs` — старый WriteShellcode + 18 mov-encoding
  helpers (не вызывались с Wave 4).
- `InterfaceDispatchBridge.cs` — `WriteShellcode` + helpers
  (обёрнуто ещё в Wave 5).

Финальный регрессионный прогон **после cleanup**: те же зелёные
результаты — census 56/2/7, EBS, 4/4 launcher, threading, drivers,
launcher 4/4.

## Файлы

```
bootasm/BootAsm.Generator/BootAsmGenerator.cs
  + HoleKind.DataSlot8, HoleKind.PushImm32
  + DataSlotSentinelBase 0xD1F0_DA7A_xxxx
  + var-decl parser + InvokeCallExpr
  + ref-arg parser + write-back loop
  + locals-lookup fallback в EvaluateAtom/EvaluateExpr (IReadOnlyDictionary param)
  + byref ParameterType unwrap в overload resolution
  + optional-param support (для CreateLabel)
  + DataSlotHole + PushImm32Hole в GenHoleCollector
  + standalone qword sentinel scan
  + 0x68 opcode dispatch в jmp/push scan
  + patch line emission для DataSlot8 / PushImm32
  + AttributeSource HoleCollector stubs

OS/src/Hal/X64Asm.cs                            — 11× call в EmitXxxBootAsm
OS/src/Hal/X64Asm.BootAsm.cs                    — 13 [CompileTimeAsm] partial
OS/src/Kernel/Memory/GcContextSpill.cs          — TryInitialize → Emit
OS/src/Kernel/Memory/GcContextSpill.BootAsm.cs  — [CompileTimeAsm] Emit
OS/src/Kernel/Memory/InterfaceDispatchBridge.cs                — WriteShellcode → #if false
OS/src/Kernel/Memory/InterfaceDispatchBridge.BootAsm.cs        — labels + DataSlotHole
OS/src/Hal/Idt/IdtTrampolines.cs                — WriteCommonStub / WriteVectorStub → Emit
OS/src/Hal/Idt/IdtTrampolines.BootAsm.cs        — EmitCommonStub + 2 vector variants
```

## Тесты — финальная батарея зелёная

```
[PhaseE]
  TebFacadeSwap..................... OK
  Atomics........................... OK  (CmpXchg/Xchg/MFence)
  ThreadPingPong.................... OK  (5/5)
  ThreadSleep/Event/Semaphore....... OK
  AllocStress / ProcessSpawn........ OK

[Drivers]
  Serial / FbRender / Ps2 / LineEdit / Shell / PciScan — all OK

[CoreCLR]
  coreclr_initialize ............... OK
  execute_assembly.exitCode ........ 42
  PAL/OS census .................... OK=56 DEG=2 FAIL=7

[EBS] OK   [Launcher] 4/4 ELF apps OK
totals: OK=54 VALUE=3
```

Threading + EH + drivers + CoreCLR + launcher + IDT trampolines (32
vector stubs через PushImm32Hole + JmpRelHole) — нулевая регрессия
против step 117 base.

## Lessons learned

1. **Generator crash каскадирует на всю compilation** — одна
   `InvalidOperationException` из walker'а в одном body methed убивает
   эмиссию всех 24 stubs (`CS8785` warning + `CS8795` errors на каждом
   partial method). Сначала видишь last-в-проекте файл как
   "сломанный", но корень в случайном предыдущем. Грепать
   `last_build.log` на `CS8785` — там message с прямым указанием
   проблемного fragment.

2. **Iced overload resolution через reflection требует exact arg
   count, ИЛИ trailing optional params** — `Assembler.CreateLabel`
   имеет `string? name = null`, без поддержки optionals walker не
   находит overload для `a.CreateLabel()`.

3. **`ref Label` параметр имеет тип `Label&`** — `IsInstanceOfType(Label)`
   возвращает false. Нужно unwrap'ить через `pt.GetElementType()` при
   проверке совместимости, плюс reflection invoke с object[] правильно
   маршалит ref-back (struct мутируется в боксе).

4. **Walker не парсит uint-литерал суффикс `u`/`U`/`L`** — `0xC0000101u`
   падал с "no matching overload"; workaround = `a.db(0xB9, 0x01,
   0x01, 0x00, 0xC0)` (raw 5-byte mov ecx, imm32). Альтернатива —
   добавить парсинг суффиксов в `LooksLikeNumber`/`TryParseLiteralAs`
   (не сделано, не блокирует).

5. **Sentinel collisions избегаются distinct high marks**: MovImm64
   `0xD1F0_DEAD_xxxx_xxxx` (внутри `mov reg, imm64`), DataSlot8
   `0xD1F0_DA7A_xxxx_xxxx` (standalone qword), JmpRel32 / PushImm32
   pattern-based `E9|68 .. .. AD DE` (внутри instruction + suffix
   marker). Скан по distinct prefix/marker — нет коллизий между
   видами hole в одном стабе.

6. **Push imm32 вместо imm8 для runtime-patched значений** —
   если значение помещается в qword (vector # 0..31), push imm32 (5B)
   удобнее чем городить byte-hole механизм. CPU читает qword со
   стека, старшие 3 байта нулевые — семантически идентично.

## Дальше

- **step 119** — cleanup `EmitLegacy` + `CompareOrPanic`
  parallel-emit oracle pairs (после N зелёных прогонов уверенности —
  достаточно). Обернуть dead-ref `WriteShellcode` в GcContextSpill.

- **`docs/nativeaot-nostd-kernel-limits.md`** — без изменений, это
  codegen refactor без сдвига tier surface.

- **README таблица** — без изменений, user-facing capability та же.
