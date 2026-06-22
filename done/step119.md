# step 119 — comprehensive runtime mechanics probe battery

Расширение тестовой батареи в обе стороны (AOT kernel + CoreCLR
hosted) для документального покрытия base runtime mechanics.

## AOT (`NativeAotProbe`)

Early-boot, 11 новых проб:
- `Probe_ArrayCovariance` — monomorphic stelem.ref
- `Probe_ModuleInit` — `[ModuleInitializer]` через bool flag
- `Probe_WriteBarrier` — ref field + GC.Collect
- `Probe_GcRootsThroughEhUnwind` — string выживает throw/catch + GC
- `Probe_FinallyOrderingNested` — порядок f1→c
- `Probe_ExceptionFilter` — `when`-filter с false/true веткой
- `Probe_RethrowStackTrace` — bare `throw;` preserves frame
- `Probe_GcSpanInterior` — Span slice через GC.Collect
- `Probe_ArrayCopyOverlap` — Array.Copy memmove семантика
- `Probe_XmmAcrossThrow` — **P0-1 canary (red до фикса XMM-lost)**
- `Probe_StringFormat` — `{0}`/`{0:D5}`/`{0:X}`/`{0:F2}`/alignment

Late-boot (`NativeAotProbe.RunLate()`), 2 пробы — после Phase E
threading + scheduler online:
- `Probe_ThreadHandoffWithGc` — producer thread + Event + GC mid-transfer
- `Probe_OomDeterministic` — `new int[int.MaxValue]` deterministic exit

Std додобавлен `[ModuleInitializerAttribute]` shim
(`std/no-runtime/shared/Runtime/RuntimeAttributes.cs`) и
`System.GC.Collect()` forward в `SharpOS.Std.NoRuntime.GC.Collect`.

## CoreCLR-hosted (`work/normal-hello`)

Три новые секции, **+59 проб**:
- **Sec 8 RUNTIME MECHANICS** (27): boxing × 6, array covariance × 4,
  interface dispatch × 4, virtual × 3, generic sharing × 4, cctor × 4,
  module init × 1, write barrier × 3
- **Sec 9 RUNTIME ADVANCED** (11): EH/GC/concurrency/SIMD/OOM
- **Sec 10 STRING.FORMAT** (8)
- **Sec 11 REGEX LADDER** (9): L0-L8 + Compiled

Census: было 56/2/7, стало **113/2/8** (+57 OK, +1 FAIL = L6
source-generated regex by design — needs partial class).

## Открытые limits (зафиксированы в docs)

В `docs/coreclr-hosted-limits.md` §12 (новая секция):
- **LIMIT-12.1** — `Exception.StackTrace` пустой для exception'ов
  брошенных из CLR-internal C++ EH path (`0xE06D7363
  PEAVEEMessageException`). SehUnwind не заполняет trace для не-
  `RhpThrowEx` путей.
- **LIMIT-12.2** — EE-internal exceptions пробивают managed catch на
  inner frames; ловятся только top-level Probe handler'ом. Тот же
  корень что 12.1, общий с FrameChain walker.
- **LIMIT-12.3** — cctor exception → raw type, **не** оборачивается в
  `TypeInitializationException`. Catch по конкретному типу работает,
  catch (TIE) — нет.

Все три скорее всего закроются попутно с P0-1 (XMM lost) и P0-2
(CollidedUnwind) фиксами — общий корень в SehUnwind/FrameChain.

В `docs/nativeaot-nostd-kernel-limits.md` §4:
- Array covariance silent UB на wrong-type store (RhpStelemRef skipped
  checks) — задокументировано когда чинить и зачем
- ✅ `[ModuleInitializer]` работает (positive confirmation)

## README

Добавлено 10 новых рядов в compare-table, включая:
- `string.Format`, Regex (новые)
- Boxing/unboxing, ModuleInitializer, Generic sharing, Virtual+interface
  dispatch, Write barrier (no-op), GC.Collect, Array.Copy overlap
- Refinement: StackTrace → 🟡 в hosted, cctor wrapping → 🟡 в hosted,
  Array covariance → 🟡 в AOT (silent UB note)

## Файлы

```
README.md                                                 + ~12 rows
docs/coreclr-hosted-limits.md                             + §12 + census row 119
docs/nativeaot-nostd-kernel-limits.md                     + array covariance UB + ModuleInit positive
std/no-runtime/shared/Runtime/RuntimeAttributes.cs        + ModuleInitializerAttribute
std/no-runtime/shared/Runtime/GC.cs                       + System.GC.Collect/GCCollectionMode
OS/src/Kernel/Diagnostics/NativeAotProbe.cs               + 11 early + 2 late probes
OS/src/Kernel/Diagnostics/Probes.cs                       + NativeAotFeaturesLate toggle
OS/src/Boot/BootSequence.cs                               + RunLate() call после Phase E
work/normal-hello/Program.cs                              + Sec 8-11 (~55 sub-probes)
```

## Прогон

QEMU green. Census 113/2/8, EBS OK, launcher 4/4, threading +
drivers + CoreCLR + IDT trampolines (32 vector stubs).
