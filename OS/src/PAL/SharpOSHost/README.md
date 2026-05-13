# SharpOSHost — kernel-side host capability layer

This directory implements the **D11 firewall** C-ABI surface that CoreCLR
guest archive (via `pal/sharpos/`) calls into when running на bare-metal
SharpOS kernel.

## Scope

**Only platform capabilities** that the kernel must provide. Per sage 2's
5-layer reframe:
- L1 (CRT, math, minipal, operator new/delete) — lives in CoreCLR fork as C/C++, не here.
- L2 (CoreCLR internal templates) — bundled via other static libs.
- **L3 (platform capability) — this directory.**
- L4 (debugger, profiler, COM, etc.) — fatal stubs in pal/sharpos/.
- L5 (EH personality) — separate D13 extension.

## Entry points (~20 functions)

| Category | Function | Win32 equivalent |
|---|---|---|
| Memory | `SharpOSHost_AllocPages` | `VirtualAlloc` |
| | `SharpOSHost_FreePages` | `VirtualFree` |
| | `SharpOSHost_ProtectPages` | `VirtualProtect` |
| | `SharpOSHost_QueryPages` | `VirtualQuery` |
| | `SharpOSHost_MapFile` | `CreateFileMapping` + `MapViewOfFile` |
| | `SharpOSHost_UnmapFile` | `UnmapViewOfFile` |
| Time | `SharpOSHost_GetTickCount` | `QueryPerformanceCounter` |
| | `SharpOSHost_GetTickFreq` | `QueryPerformanceFrequency` |
| | `SharpOSHost_GetMillis` | `GetTickCount64` |
| | `SharpOSHost_GetSystemTime` | `GetSystemTimeAsFileTime` |
| TLS | `SharpOSHost_TlsAlloc` | `TlsAlloc` |
| | `SharpOSHost_TlsFree` | `TlsFree` |
| | `SharpOSHost_TlsGet` | `TlsGetValue` |
| | `SharpOSHost_TlsSet` | `TlsSetValue` |
| Diagnostics | `SharpOSHost_DebugPrint` | `OutputDebugString` |
| | `SharpOSHost_DebugBreak` | `DebugBreak` |
| CPU | `SharpOSHost_FlushICache` | `FlushInstructionCache` |
| | `SharpOSHost_MemoryBarrier` | `FlushProcessWriteBuffers` |
| EH | `SharpOSHost_RegisterFunctionTable` | `RtlInstallFunctionTableCallback` |
| | `SharpOSHost_UnregisterFunctionTable` | `RtlDeleteFunctionTable` |

## Status

**Phase 6.1.0 — skeleton, all Panic.Fail.** Every entry point currently calls
`Panic.Fail("SharpOSHost_X not implemented")` с named diagnostic message.
If CoreCLR triggers them — we know exactly which capability needed to bring up.

Implementation progression:
- **6.1.a (coreclr_initialize)** fills: memory (Alloc/Free/Protect), time,
  TLS, diagnostics, CPU primitives. ~14 entry points.
- **6.1.b (execute_assembly)** fills: MapFile/UnmapFile, QueryPages. ~3 more.
- **6.1.c (EH smoke)** fills: RegisterFunctionTable / Unregister. ~2 more.

Total surface: ~20 entry points. Honest D11 firewall spec.

## How CoreCLR reaches these

```
CoreCLR vm/ calls Win32-shaped name (e.g. VirtualAlloc)
   ↓
pal/sharpos/winapi_shim.cpp wrapper (extern "C" C function)
   ↓
   на Phase 6.1.0 (Windows host smoke): direct Win32 import (kernel32.dll)
   на bare metal kernel link: SharpOSHost_AllocPages (C-ABI extern, resolved at kernel image link)
```

The C-ABI line is **POD types only** — `void*`, `uint32_t`, `int`. No
exceptions, no C++ classes, no managed object references cross this
boundary.

## Cross-references

- `work/PAL/D1-D20 FINALIZED/D11___FINALIZED.md` — firewall design
- `work/PAL/phase6_1-L3-classification.md` — full 154 Win32 imports
  classified into REAL (38, here) / MINIMAL (17) / STUB_OK (30) /
  ABORT_FATAL (69)
- `work/PAL/phase6_1-3way-audit-overview.md` — empirical audit data
