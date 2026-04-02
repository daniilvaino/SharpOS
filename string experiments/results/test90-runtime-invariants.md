# EXP_TEST_90 runtime invariants

Date: 2026-04-02
Target: `apps/HelloSharpFs` with define `EXP_TEST_90`

## Scope

Single focused runtime test for string object invariants:

- object address from pinned pointer
- raw `Length` field in memory
- first 3 chars in memory
- `RuntimeHelpers.OffsetToStringData`

## Result

Pass.

Observed in COM1 log:

- `len_prop=3`
- `len_raw=3`
- `offset=12`
- `c0=97`
- `c1=98`
- `c2=99`
- `test_result=1`

## Root cause and fix

Root cause was in ELF loading for physical execution mode:

- app code executes by physical entrypoint
- C# NativeAOT image has segmented layout with virtual gaps
- old loader packed segment physical pages without preserving virtual deltas
- cross-segment references (including `ldstr` path) pointed to wrong physical memory

Fix in `OS_0.1/src/Kernel/Elf/ElfLoader.cs`:

- compute full image page span from lowest to highest load address
- allocate one contiguous physical span for whole image range (including holes)
- map each segment page to `imagePhysicalBase + (segmentVirtualPage - imageVirtualBase)`
- keep file copy + zero-tail logic unchanged

Additional build fix in `build_app_freestanding_wsl.ps1`:

- link via bootstrap entry `SharpAppBootstrap` that calls `__managed__Startup()` then `SharpAppEntry(startupPointer)`.
