# step 117 — compile-time shellcode codegen (BootAsm.Generator)

## Веха

Roslyn incremental source generator материализует kernel-shellcode стабы
**на этапе сборки OS** из fluent-Iced bodies. На runtime — только
`Span.CopyTo` из RVA blob в `.rdata`. Никакого runtime Iced, аллокатора,
cctor'а в этих стабах. Управляемые callback'и патчатся через явно
параметризованные дырки.

Покрывает поверхность достаточную для миграции всех early-tier
patcher'ов с managed callback'ами (CallCatch/Finally/FilterFunclet,
ThrowEx, CaptureContext, Rethrow, etc) — конкретные миграции — в
последующие шаги.

## Архитектура

```
bootasm/
  BootAsm.Generator/
    BootAsm.Generator.csproj           netstandard2.0 Roslyn analyzer
    BootAsmGenerator.cs                IIncrementalGenerator
```

OS.csproj ссылается на Generator как `<Analyzer>` через
`OutputItemType="Analyzer" ReferenceOutputAssembly="false"` — генератор
не попадает в kernel binary.

Iced fork **переехал** из `OS/src/Iced/` в `/iced/` в корне репо (sibling
к `OS/`, `std/`, `apps/`). Подключается **одинаковым** `<Compile Include>`
в обе сборки (OS + Generator) — литерально 4 строки xml, без
Directory.Build.props индирекций. Те же `DefineConstants` (`ENCODER;
BLOCK_ENCODER; CODE_ASSEMBLER; HAS_SPAN; IcedNoIVT; NO_EVEX`) — encoder
гарантированно одинаков compile-time и runtime.

## Слои генератора (M1-M6, поэтапно)

### M1 — pipeline skeleton

`IIncrementalGenerator` находит `[CompileTimeAsm]` partial-методы и
эмитит trivial impl `return 0;`. Атрибуты (`CompileTimeAsmAttribute`,
`CompileTimeAsmBodyAttribute`, `HoleCollector`) эмитятся в consumer
compilation через `RegisterPostInitializationOutput` — не отдельным
проектом.

### M2 — walker + zero-arg Iced calls

Body вида `Ret_Body(Assembler a) => a.ret();`. Walker распознаёт
`a.NAME()`, через reflection находит метод на `Assembler`, invoke'ит.
После — `Assembler.Assemble(codeWriter, 0)` собирает байты. Generator
эмитит template + CopyTo. Test: `Ret` → 1 байт `0xC3`.

### M3 — register identifiers + integer literals

Body вида `a.push(rbp); a.sub(rsp, 0x28); a.call(rcx); …`. Walker
парсит args, классифицирует:
- Bare number → late-bound literal (try `int`, then `long`, `ulong` per
  overload).
- Identifier (`rax`/`r12`/`cr3`/…) → reflection lookup на
  `AssemblerRegisters` static fields/properties.

Overload resolution: для каждого кандидата `Assembler.NAME(...)` с
matching param count — проверяем (a) register: param type assignable
from concrete reg type; (b) literal: `Convert.ChangeType` succeeds.
Первый match → invoke. Test: `GcStackSpill` (35 байт) byte-identical с
hand-rolled legacy.

### M4 — first real migration

`GcStackSpill.cs` сделан `partial`, hand-rolled `WriteShellcode`
переименован в `EmitLegacy` (возвращает length). Новый partial-файл
`GcStackSpill.Iced.cs` с `[CompileTimeAsm] Emit` declaration +
`[CompileTimeAsmBody] Emit_Body`. `TryInitialize` делает parallel-emit
(compile-time `Emit` пишет в live buffer, legacy в `stackalloc`
scratch) + `CompareOrPanic` + single success-print. Acceptance gate:
`[bootasm] gcstackspill compile=legacy OK len=0x23`.

### M5 — memory operands

Body вида `a.mov(__qword_ptr[rcx + 0x88], rdx);`. Walker'у добавлен
`EvaluateExpr` — рекурсивный evaluator с поддержкой:
- Indexer `IDENT[INNER]` → reflection `get_Item` на factory типе
  (`__qword_ptr`/`__dword_ptr`).
- Top-level бинарные `+`/`-` (вне скобок) → reflection `op_Addition`/
  `op_Subtraction` на LHS типе.
- Atom числа → default int, fallback long/ulong.
- Atom identifier → `AssemblerRegisters` field/property.

Подавляет `+`/`-` сразу после знака чтобы не путать с числовыми
литералами `-8`. Test canary `MemOps_Body` мирорит SehDispatch.EmitCapture
паттерны: `[rcx+0xN]`, `[rsp]`, `[rsp+8]`, `[rdx-8]`, dword variant.

### M5.1 — SehDispatch.EmitCapture/EmitRestore migration

Самые длинные стабы (138 + 137 байт). `SehDispatch.BootAsm.cs` новый
partial-файл с двумя `[CompileTimeAsm]` declarations + bodies (зеркала
runtime Iced версий из step 116). `EnsureShellcode` теперь зовёт
`EmitCaptureCompileTime(capCode)` и `EmitRestoreCompileTime(resCode)` —
runtime Iced (`EmitCaptureIced`/`EmitRestoreIced` в `SehDispatch.Iced.cs`)
**dead code**, будет удалён в step 118 cleanup. Compare-gate vs legacy
сохранён. Acceptance: `[shellcode] compile=legacy OK cap=0x8A res=0x89`.

### M6 — HoleCollector / sentinels / anchors

Body вида:
```csharp
private static void WriteHandler_Body(Assembler a, BootAsm.HoleCollector h)
{
    h.MovHole(a, r10, "handlerCalled"); a.mov(__qword_ptr[r10], rcx);
    h.JmpHole(a, r11, "continuation");
}
```

**SentinelBase** `0xD1F0_DEAD_0000_0000`. `MovHole`/`JmpHole`
записывают (ordinal, name) и эмитят `mov reg, sentinel | ordinal` через
fluent Iced (value > UInt32.Max → forced imm64). JmpHole дополнительно
эмитит терминальный `jmp scratchReg`.

После Assemble — linear scan по байтам ищет `REX.W + B8+rd + …AD DE F0
D1` паттерны, читает ordinal из low 4 байт imm64, маппит ordinal →
byte offset. Anchors v1 (минимальные две):
1. Каждый detected sentinel начинается с `(rex & 0xF8) == 0x48` + `(opc
   & 0xF8) == 0xB8` (REX.W + Mov_r64_imm64 opcode).
2. Sentinel count == hole count.

(Spec'овые anchors 0/2/3 через BlockEncoder ConstantOffsets/
NewInstructionOffsets — TODO v2.)

После offset detection — sentinels занулены, generator эмитит patch
строки `*(ulong*)(dst + 0xN) = (ulong)(nuint)<paramName>;` для каждой
дырки. Параметр-list partial-декларации забирается verbatim из syntax
tree, имена параметров матчатся с string-literals в `MovHole`/`JmpHole`
по convention (пользователь синкает руками; mismatch = compile error
от partial signature).

Эмитированный `BootAsm.HoleCollector` (в consumer compilation) — пустые
`MovHole`/`JmpHole` методы. Никакого state, никаких tuples (наш std
без `ValueTuple`). Walker внутри генератора использует свой private
`GenHoleCollector` — тот действительно записывает holes. Body methods
на runtime НИКОГДА не зовутся (они dead code с точки зрения kernel),
runtime HoleCollector — просто чтобы body methods компилились.

Test canary `WriteHandler`: 52 байта, 4 дырки (3 MovHole + 1 JmpHole)
на offsets `[0x2, 0xF, 0x1C, 0x29]`. Byte-identical со спекой §11.

## Что мигрировано на compile-time

| Стаб | Файл | Длина | Status |
|---|---|---|---|
| GcStackSpill | `OS/src/Kernel/Memory/GcStackSpill.Iced.cs` | 35 | ✅ M4 acceptance |
| SehDispatch.EmitCapture | `OS/src/PAL/SharpOSHost/SehDispatch.BootAsm.cs` | 138 | ✅ M5.1 acceptance |
| SehDispatch.EmitRestore | same | 137 | ✅ M5.1 acceptance |

**Runtime Iced миграции step 116** (`JumpStub`, `BigStack`,
`AppServiceBuilder` thunks, `wrmsr GS_BASE`, `Cr3Accessor`) остаются
runtime Iced пока — миграция на compile-time всех остальных — отдельные
шаги поэтапно (после M6 unblock'а нет принципиальных препятствий).

## Файлы

### Новые
- `bootasm/BootAsm.Generator/BootAsm.Generator.csproj` — netstandard2.0
  + Microsoft.CodeAnalysis.CSharp 4.8 + System.Memory backport
- `bootasm/BootAsm.Generator/BootAsmGenerator.cs` — IIncrementalGenerator
  с walker'ом, ExecuteBody, sentinel detection, anchors, emission
- `OS/src/Kernel/Diagnostics/BootAsmProbe.cs` — canary stubs (Ret,
  GcStackSpill дубликат, MemOps, WriteHandler) — build-time-only
  тесты walker'а
- `OS/src/Kernel/Memory/GcStackSpill.Iced.cs` — M4 migration
- `OS/src/PAL/SharpOSHost/SehDispatch.BootAsm.cs` — M5.1 migration

### Изменённые
- `OS/OS.csproj`:
  - `<ProjectReference>` на Generator как Analyzer
  - `<EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>` для
    видимости .g.cs на диске
  - `<Compile Include="..\iced\**\*.cs">` (Iced переехал из OS/src/Iced)
- `OS/src/Kernel/Memory/GcStackSpill.cs`:
  - `partial`; `WriteShellcode` → `EmitLegacy`; parallel-emit +
    compare-gate в `TryInitialize`
- `OS/src/PAL/SharpOSHost/SehDispatch.cs`:
  - `EnsureShellcode` зовёт `EmitCaptureCompileTime`/
    `EmitRestoreCompileTime` вместо runtime Iced
- `README.md`:
  - "Отдельное спасибо" — 10 проектов на плечах которых стоим
- 213 файлов Iced — `git mv` из `OS/src/Iced/` в `/iced/`

## Lessons learned

1. **Walker > ALC** для restricted bodies. Спека §4 предлагает temp
   Roslyn compilation + AssemblyLoadContext для исполнения body methods.
   Но bodies ограничены flat sequences of `a.method(args)` calls (спека
   §3) — простой syntax walker + reflection на Iced (который compiled
   в сам Generator) **строго проще** и закрывает все наши case'ы. ALC
   приберечь на случай сложных bodies в будущем.

2. **netstandard2.0 backports**. Roslyn analyzer host грузит
   netstandard2.0. Iced требует `Span<T>` (ns2.1+) → `System.Memory`
   NuGet решает. `record struct` → `IsExternalInit` polyfill нужен →
   используем plain struct. `ValueTuple` в consumer (kernel) → не имеем
   → emitted `HoleCollector` без tuples, walker внутри генератора
   tuples использует свободно.

3. **Sentinel detection через linear scan** проще чем через BlockEncoder
   ConstantOffsets/NewInstructionOffsets. Pattern `REX.W + B8+rd + …AD
   DE F0 D1` точно идентифицирует наши hole'ы — не зависит от Iced API
   изменений. Anchors v2 (через `ConstantOffsets`) пригодятся если
   когда-нибудь будут случаи когда sentinel-pattern может встретиться
   как валидный imm в реальном коде (маловероятно с 32-bit высокой
   маркой).

4. **Generated HoleCollector — пустой**. Сначала эмитил с List + tuples
   — упёрся в `ValueTuple` not defined в std. Перерисовал — runtime
   semantics не важны, walker всё равно использует свой
   `GenHoleCollector` внутри. Empty body — самое простое.

5. **EmitCompilerGeneratedFiles=true** обязателен для отладки. Roslyn
   по умолчанию держит generated source только in-memory — без этого
   флага не видно что Generator реально эмитит, диагностика через
   compile errors отнимает гораздо больше времени.

6. **`partial` accessibility должен матчиться**. Generator сначала
   хардкодил `public` — partial-декларация `internal` в OS падала с
   CS0262. Fix: читать `DeclaredAccessibility` через symbol API.

## Что откладывается / следующее

- **step 118 cleanup**: убрать runtime Iced emitters
  `EmitCaptureIced`/`EmitRestoreIced` из `SehDispatch.Iced.cs` (dead
  code после M5.1), legacy `EmitCapture`/`EmitRestore` + compare-gates
  по всем 6 step-116 миграциям (compile-time + runtime подтверждены).
- **M7 — массовая миграция patcher'ов с holes**:
  `CallCatchFuncletPatcher`, `CallFinallyFuncletPatcher`,
  `CallFilterFuncletPatcher`, `ThrowExPatcher`, `RethrowPatcher`,
  `CaptureContextPatcher`, `ByRefAssignRefPatcher`,
  `InterfaceDispatchPatcher`, `ChkstkPatcher`, `BootStackSwitchPatcher`,
  `PortIoPatcher` — все managed-callback-target stubs.
- **Anchors v2**: использовать `BlockEncoder` `ConstantOffsets` +
  `NewInstructionOffsets` для строгой verification (anchors 0, 2, 3 из
  спека §6).
- **Iced source code дедупликация**: build-time мог бы вынести в shared
  csproj если число consumer'ов вырастет (currently 2 — OS + Generator).
  Sufficient for now.
- **ILC dead-code elimination для body methods**. Body methods (`*_Body`)
  компилятся в kernel image как ordinary static methods даже хотя
  никогда не зовутся на runtime. Решение позже:
  `[Conditional("NEVER")]` на body или явный `[BootAsmBuildOnly]`
  atrribut + ILC scrub-rule.

## Acceptance

Runtime лог после step 117:
```
[bootasm] gcstackspill compile=legacy OK len=0x23
[shellcode] compile=legacy OK cap=0x8A res=0x89
```

`BootAsmProbe.WriteHandler.g.cs` — 52 байта template + 4 patch lines,
byte-identical со спека §11.
