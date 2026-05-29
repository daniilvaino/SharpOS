# SharpOS D-decisions — archived Round 7 record

**Archive status (2026-05-29):** this directory is no longer an active
roadmap. The surviving D1-D20 outcomes have been collapsed into
`plan.md` §3. Keep these files as historical reasoning/evidence only; do
not add new active work here.

Дата сборки: 2026-05-09

## История версий

**Round 6** (initial finalize): D1-D20 финализированы, D2/D3/D5/D17/D20/Deferred исправлены, revised D10/D11 internalized, D1/D4/D20/TARGET_SHARPOS Build Config refined.

**Round 7** (current): sage 2 audit нашёл residual inconsistencies:
- TARGET_SHARPOS Build Config build steps возвращали старый SharpOSHost.lib pipeline → fixed
- HOST_WIN32 + TARGET_SHARPOS + !TARGET_WIN32 configure axis split → primary first gate
- C++ static initialization audit gate → added в D10
- .pdata/.xdata retention audit gate → added в D10
- System lib allowlist слишком щедрый → tightened, evidence-based expansion
- D20 формулировка про C# UCO → softened (одна из форм, не default)
- Phase 2A direct Win32 shortcut vs D11 firewall → resolved (strict A: no shortcut)
- Compile-time firewall infrastructure → added в D11 (forced include + #pragma GCC poison + fake windows.h + audit)

## Состав

### Финализированные decisions (D1-D20)

| Файл | Статус |
|---|---|
| D1___FINALIZED.md | Refined (revised D10/D11): C ABI primary, C# enum optional Phase 6 |
| D2___FINALIZED.md | Path fixes (preinit/ removed per D9) |
| D3___FINALIZED.md | Принцип 8 = REMOVED (superseded by D9 flat structure) |
| D4___FINALIZED.md | Refined (revised D10/D11): managed + native provider contexts |
| D5___FINALIZED.md | Path fixes (forward/ removed per D9), libunwind reference removed (per D13) |
| D9___FINALIZED.md | Без изменений (flat structure был правильным) |
| D10___FINALIZED.md | **REVISED + Round 7 audits** — CoreCLR guest archive, tightened allowlist, C++ static init audit, .pdata retention audit |
| D10___FINALIZED_old.md | Backup старой версии |
| D11___FINALIZED.md | **REVISED + Round 7 firewall** — ABI namespace, compile-time firewall infrastructure |
| D11___FINALIZED_old.md | Backup старой версии |
| D12_closure.md | Not applicable (Redhawk PAL не нужен для CoreCLR гостя) |
| D13___FINALIZED.md | Без изменений (mixed stack risk + Windows spike measurement TBD) |
| D14_closure.md | Closed via D4 |
| D15_closure.md | Closed via D3+D9 |
| D16_closure.md | Closed via D3 (add when needed) |
| D17___FINALIZED.md | Reference fix (added Phase 2 Redesign + TARGET_SHARPOS) |
| D18_closure.md | Closed via D3 (trace-driven progressive classification) |
| D19_closure.md | Closed (per-function detailed format) |
| D20___FINALIZED.md | Refined Phase 2 vs Phase 6 + Round 7 UCO clarification |

### Дополнительные decisions

| Файл | Содержание |
|---|---|
| Phase_2_Redesign___FINALIZED.md | **Round 7**: Production-shaped path с первого дня, no Phase 2A Win32 shortcut |
| TARGET_SHARPOS_Build_Configuration___FINALIZED.md | **Round 7**: HOST_WIN32 configure gate, C++ shim build (no SharpOSHost.lib), patch 4 removed |
| Deferred_Decisions.md | D6, D7, D8 (deferred to Phase 6.2) |

## Ключевые архитектурные принципы (45 принципов)

1-4: D1 (steal interfaces, steal from production, max theft, document sources)
5-7: D2 (build infrastructure before, stable contract, narrow X.5 subtasks)
8: REMOVED (superseded by D9)
9-11: D3 (add when needed, trace-driven, plain text)
12-14: D4 (catch known only, runtime guarantees, verify before assume)
15-18: D5 (custom narrow conditionals, narrative, defer design, X.1/X.2 split)
19-22: D9 (thin PAL, steal structure, add defenses when needed, PAL size as metric)
23-25: D10 (spike validates production, production embedding patterns, mental model preserved)
26-28: D11 (decisions interconnected, simplest working, linker does work)
29-32: D13 (reuse existing infrastructure, hidden masking, oracle vs implementation, coverage gap measurement)
33-36: Phase 2 Redesign (hosted spike validates production pathway, soul disambiguation, link audit, X.A/X.B split)
37: TARGET_SHARPOS Build Config (custom target = not Unix not Windows)
38-41: D17 (diagnostic both paths, crash-safe discipline, ABORT_FATAL dumps trace, hardcoded defaults)
42-45: D20 (static linking transparent, maximize C# для Invariant 1, reuse Phase 1, trivial constants OK)

## Provider model (revised D10/D11 + Round 7 firewall)

```
Phase 2A:  CoreCLR → pal/sharpos/ → SharpOSHost_* → Windows shim (stub) → Win32
Phase 2B:  CoreCLR → pal/sharpos/ → SharpOSHost_* → Windows shim (full) → Win32
Phase 6:   CoreCLR → pal/sharpos/ → SharpOSHost_* → bare-metal provider → kernel
```

`pal/sharpos/` код **identical** во всех трёх. Меняется только provider behind SharpOSHost_*.

**Boundary firewall enforced physically** (Round 7):
- Forced include `sharpos_no_winapi.h` с `#pragma GCC poison` для WinAPI identifiers
- Fake `windows.h` в forbidden_headers/ (include падает immediately)
- Object-level audit через `nm -u` / `dumpbin /SYMBOLS`
- Link trace через `--trace-symbol` / `link /VERBOSE:LIB`
- Acceptance criterion в CI

## Готовность к implementation

Все decisions финализированы и consistent. Готов к Phase 2A Windows spike implementation:

1. Setup Visual Studio Build Tools на Windows machine
2. Vanilla CoreCLR Windows build (verify работает)
3. Apply TARGET_SHARPOS patches (4 patches, additive)
4. **HOST_WIN32 + TARGET_SHARPOS + !TARGET_WIN32 configure proof gate** (FIRST GATE)
5. Build coreclr_sharpos_static.lib с D11 firewall enforced
6. Implement sharpos_host_windows_shim.lib (C++ Win32-backed, minimal для Hello World)
7. First spike-host.exe link с link audit (Tier A initial only)
8. **C++ static initialization audit + .pdata retention audit** (acceptance gates)
9. Hello World scenario
10. UNWIND_INFO dumper для D13 coverage measurement

Timebox Phase 2A: 3-5 дней до первого pal/sharpos trace на Windows.

## Открытые риски (зафиксированы, не блокирующие)

1. **HOST_WIN32 + TARGET_SHARPOS axis split** — может потребовать дополнительные CMake patches beyond текущих 4
2. **C++ static initializers** — на bare metal (Phase 6) NativeAOT entrypoint может не запускать `.CRT$XCU` секции CoreCLR archive. Phase 6 link dry-run gate concern.
3. **CRT linkage conflict** (libcmt vs ucrt) — Microsoft форсирует ucrt для NativeAOT, CoreCLR static использует libcmt. Resolve via `/NODEFAULTLIB` based on first link errors.
4. **`coreclr_static` на Windows host** — target существует но не testing'уется Microsoft'ом для Windows native. Может требовать debug.
