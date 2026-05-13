# Phase 6.1 L3 surface classification — 154 Win32 imports

**Source:** `work/PAL/symbol-audit/phase6_1_min/full_bundle/L3_win32_imports.txt`
**Bundle:** 10 static libs (coreclr_static + gc_pal + gcinfo_win_x64 + utilcodestaticnohost + minipal + coreclrminipal + coreclrpal + clrjit + eventprovider + traceptprovider).

These 154 symbols are `__imp_*` Win32 thunks in the bundled static archive — currently resolved via Win32 import libs (kernel32.lib, advapi32.lib, ole32.lib, user32.lib) на HOST_WINDOWS phase. On bare-metal kernel target, each needs **one of**:

- **REAL** — real `SharpOSHost_*` implementation (kernel does it for actual)
- **MINIMAL** — trivial impl что just enough for single-thread Phase 6.1 (e.g. SetEvent no-op, GetCurrentThreadId returns 1)
- **ABORT_FATAL** — printf'нет имя + halt. Phase 6.1 спецификация (D5/D6/etc.) guarantees unreachable. Triggers — bug or wrong scenario.
- **STUB_OK** — trivial value (e.g. GetLastError returns 0, IsDebuggerPresent returns FALSE)

Per **D5** (no threading), **D6** (GC state ownership), **D9** (memory forward), **D2** (TLS), **D13** (EH via Phase 1).

---

## Category A — REAL (must implement for Phase 6.1)

### Memory (per D9 — forward to SharpOSHost_*)

| Symbol | SharpOSHost mapping | Note |
|---|---|---|
| `VirtualAlloc` | `SharpOSHost_AllocPages` | GC heap, code heap, stub heap |
| `VirtualFree` | `SharpOSHost_FreePages` | |
| `VirtualProtect` | `SharpOSHost_ProtectPages` | RX/RW transitions для code |
| `VirtualQuery` | `SharpOSHost_QueryPages` | Heap walking |
| `VirtualUnlock` | minimal no-op | Not critical |
| `VirtualAllocExNuma` | route в VirtualAlloc, ignore NUMA arg | Single-NUMA для kernel |
| `MapViewOfFile` / `MapViewOfFileEx` | `SharpOSHost_MapFile` (or fatal) | Used для assembly loading — TBD |
| `UnmapViewOfFile` | `SharpOSHost_UnmapFile` (or fatal) | Pair с above |
| `CreateFileMappingA` / `CreateFileMappingW` | `SharpOSHost_CreateMapping` (or fatal) | Pair |

### Time

| Symbol | SharpOSHost mapping | Note |
|---|---|---|
| `QueryPerformanceCounter` | `SharpOSHost_GetTickCount` | HPET / TSC |
| `QueryPerformanceFrequency` | `SharpOSHost_GetTickFreq` | Returns constant |
| `GetTickCount64` | `SharpOSHost_GetMillis` | |
| `GetSystemTime` / `GetSystemTimeAsFileTime` | `SharpOSHost_GetSystemTime` | |
| `GetSystemTimes` | minimal — returns dummy idle/kernel/user | |

### EH primitives (per D13 — Phase 1 .pdata unwinder)

| Symbol | Strategy | Note |
|---|---|---|
| `RtlCaptureContext` | REAL — Phase 1 primitive | Capture current CONTEXT |
| `RtlLookupFunctionEntry` | REAL — Phase 1 .pdata lookup | |
| `RtlRestoreContext` | REAL — Phase 1 context restore | |
| `RtlUnwind` / `RtlUnwindEx` | REAL — Phase 1 unwinder | Funclet-based |
| `RtlInstallFunctionTableCallback` | REAL — dynamic code .pdata registration | JIT'ed code |
| `RtlDeleteFunctionTable` | REAL — pair с install | |
| `CopyContext` | REAL — memcpy CONTEXT structure | |

### TLS (per D2 — Phase 5.5 prereq)

| Symbol | SharpOSHost mapping | Note |
|---|---|---|
| `TlsAlloc` | `SharpOSHost_TlsAlloc` | Single-thread, small static array |
| `TlsFree` | `SharpOSHost_TlsFree` | |
| `TlsGetValue` | `SharpOSHost_TlsGet` | |
| `TlsSetValue` | `SharpOSHost_TlsSet` | |

### Identity / state

| Symbol | Strategy | Note |
|---|---|---|
| `GetLastError` / `SetLastError` | REAL — thread-local (single thread, just a global) | Used everywhere |
| `GetCurrentThread` | STUB — returns const handle 1 | Single thread |
| `GetCurrentThreadId` | STUB — returns const 1 | |
| `GetCurrentProcess` | STUB — returns const handle | |
| `GetCurrentProcessId` | STUB — returns const 1 | |

### Atomics / cache

| Symbol | Strategy | Note |
|---|---|---|
| `FlushInstructionCache` | REAL — `wbinvd` or `clflush` loop | JIT writes code |
| `FlushProcessWriteBuffers` | REAL — `mfence` | GC barriers |

### Module / globalization minimal

| Symbol | Strategy | Note |
|---|---|---|
| `MultiByteToWideChar` | REAL — UTF-8 → UTF-16 (managed already в SharpOS std) | Used в bootstrap |
| `WideCharToMultiByte` | REAL — UTF-16 → UTF-8 | |
| `FormatMessageW` | minimal (just `%s/%d` subset) или ABORT_FATAL | Used в diagnostics |

### Debug output

| Symbol | Strategy | Note |
|---|---|---|
| `OutputDebugStringA` / `OutputDebugStringW` | REAL — kernel debug port (serial/com1) | Diagnostic |
| `DebugBreak` | REAL — `int 3` | Triggered в asserts |
| `IsDebuggerPresent` | STUB — returns FALSE | |

---

## Category B — MINIMAL (single-thread Phase 6.1, no real sync needed)

Sync primitives — single-thread CoreCLR никогда не reach actual wait state. Implement as **no-op or trivial**.

| Symbol | Minimal impl | Note |
|---|---|---|
| `InitializeCriticalSection` | no-op | Single thread, никто никогда не блокируется |
| `DeleteCriticalSection` | no-op | |
| `EnterCriticalSection` / `LeaveCriticalSection` | no-op | |
| `CreateEventW` / `OpenEventW` | return handle с in-memory flag | |
| `SetEvent` / `ResetEvent` | flip in-memory flag | |
| `CreateSemaphoreExW` / `ReleaseSemaphore` | trivial counter | |
| `WaitForSingleObject` / `WaitForSingleObjectEx` | check flag, return immediately | Single thread — никто не сигналит → either flag set (return WAIT_OBJECT_0) или dead-lock (assert) |
| `WaitForMultipleObjects` / `WaitForMultipleObjectsEx` | same logic | |
| `SignalObjectAndWait` | combine SetEvent + WaitForSingleObject | |
| `DuplicateHandle` | return same handle | |
| `CloseHandle` | no-op (in-memory handles) | |

---

## Category C — STUB_OK (constant return, never blocks)

Trivial values что не вызовут поведенческих проблем.

| Symbol | Stub returns | Note |
|---|---|---|
| `GetSystemInfo` | dummy SYSTEM_INFO (1 processor) | |
| `GetLogicalProcessorInformation` | empty | Single-CPU |
| `GetLogicalProcessorInformationEx` | empty | |
| `GetProcessAffinityMask` | 1 (only CPU 0) | |
| `GetProcessGroupAffinity` | 1 group | |
| `GetCurrentProcessorNumberEx` | 0 | |
| `GetNumaHighestNodeNumber` | 0 | No NUMA |
| `GetNumaNodeProcessorMaskEx` | mask 1 | |
| `GetNumaProcessorNodeEx` | 0 | |
| `GetLargePageMinimum` | 0 (no large pages в Phase 6.1) | |
| `GlobalMemoryStatusEx` | dummy с reasonable defaults | |
| `IsProcessInJob` | FALSE | |
| `GetEnabledXStateFeatures` / `GetXStateFeaturesMask` / `SetXStateFeaturesMask` | 0 / no-op | No CET, no AVX context save |
| `VerSetConditionMask` / `VerifyVersionInfoW` | TRUE | Version check passes |

### Environment (Phase 6.1 — пустое окружение)

| Symbol | Stub returns |
|---|---|
| `GetCommandLineW` | empty L"" |
| `GetEnvironmentStringsW` / `FreeEnvironmentStringsW` | empty |
| `GetEnvironmentVariableW` / `SetEnvironmentVariableW` | not found / no-op |
| `GetCPInfo` / `GetConsoleOutputCP` | UTF-8 defaults |

### Module/PE introspection

| Symbol | Stub returns |
|---|---|
| `GetModuleFileNameW` | const path (kernel image) |
| `GetModuleHandleA` / `GetModuleHandleW` | self handle |
| `GetProcAddress` | NULL (no dynamic loading в kernel) |
| `GetStdHandle` | dummy handle |
| `LocalFree` | free via SharpOS allocator |
| `GetProcessHeap` | dummy handle |
| `HeapAlloc` / `HeapFree` / `HeapCreate` / `HeapDestroy` | route via VirtualAlloc/Free (or SharpOSHost direct) |

---

## Category D — ABORT_FATAL (per D5/D6 — unreachable в Phase 6.1)

### Threading (per D5)

`CreateThread`, `ExitThread`, `ResumeThread`, `SwitchToThread`, `Sleep`, `SleepEx`, `TerminateProcess`, `QueueUserAPC`, `GetThreadContext`, `SetThreadContext`, `SetThreadAffinityMask`, `SetThreadGroupAffinity`, `GetThreadGroupAffinity`, `SetThreadIdealProcessorEx`, `GetThreadIdealProcessorEx`, `SetThreadPriority`, `GetThreadPriority`, `SetThreadErrorMode`, `SetThreadToken`, `QueryThreadCycleTime`, `OpenThreadToken`

### Process control

`CreateProcessW`, `ExitProcess`, `GetExitCodeProcess`, `QueryInformationJobObject`

### File I/O (Phase 6.1 — assembly из memory blob)

`CreateFileA` / `CreateFileW`, `ReadFile`, `WriteFile`, `FlushFileBuffers`, `SetFilePointer`, `GetFileSize`, `GetFullPathNameW`, `SearchPathW`, `CopyFileExW`, `CancelIoEx`, `GetOverlappedResult`

### Named pipes (debugger IPC — off в Phase 6.1)

`CreateNamedPipeA` / `CreateNamedPipeW`, `ConnectNamedPipe`, `DisconnectNamedPipe`

### Registry (config из env vars only)

`RegCloseKey`, `RegOpenKeyExW`, `RegQueryValueExW`

### Security / privileges (large pages off → не reach)

`AdjustTokenPrivileges`, `LookupPrivilegeValueW`, `OpenProcessToken`, `GetTokenInformation`, `GetSidSubAuthority`, `GetSidSubAuthorityCount`, `RevertToSelf`

### Module loading (no dynamic loading в kernel)

`LoadLibraryExA` / `LoadLibraryExW`, `FreeLibrary`

### Watson / Fail fast (custom kernel panic path вместо)

`RaiseException`, `RaiseFailFastException`

### Write watch (SOFTWARE_WRITE_WATCH off, but `__imp_*` остаётся в bundle)

`GetWriteWatch`, `ResetWriteWatch` — these resolve at link через kernel32.lib import, но Phase 6.1 ZeroGC + SOFTWARE_WRITE_WATCH off → call site nicht reached. ABORT_FATAL OK.

### Resources (mscorrc.dll — kernel doesn't ship это)

`LoadStringW`

### COM (off на TARGET_UNIX path — но IIDs still referenced)

`CoCreateGuid`, `CoTaskMemAlloc`, `CoTaskMemFree` — minimal stubs (CoCreateGuid → kernel RNG + format; CoTaskMemAlloc → SharpOSHost_AllocPages)

### Diagnostics

`ReadProcessMemory` — DAC only, не reached in Phase 6.1

---

## Summary breakdown

| Category | Count | Effort |
|---|---|---|
| A — REAL | **38** | Real impl через SharpOSHost_* для memory/time/EH/TLS, ~5-10 weeks |
| B — MINIMAL | **17** | Trivial single-thread no-ops, ~1 week |
| C — STUB_OK | **30** | Const returns, ~1 week |
| D — ABORT_FATAL | **69** | printf+halt batch, ~1 day |
| **Total** | **154** | |

Реальная работа = **Category A + B** = 55 functions. Остальные 99 = либо stubs (~30), либо abort (~69).

## Что должно быть в `SharpOSHost_*` API spec

Category A subset где Win32 name maps на kernel capability:

```
SharpOSHost_AllocPages(size, flags) → void*           // VirtualAlloc
SharpOSHost_FreePages(addr, size)                     // VirtualFree
SharpOSHost_ProtectPages(addr, size, prot)            // VirtualProtect
SharpOSHost_QueryPages(addr) → MEMORY_BASIC_INFO      // VirtualQuery
SharpOSHost_MapFile(...)                              // CreateFileMapping + MapViewOfFile
SharpOSHost_UnmapFile(view)                           // UnmapViewOfFile

SharpOSHost_GetTickCount() → uint64                   // QueryPerformanceCounter
SharpOSHost_GetTickFreq() → uint64                    // QueryPerformanceFrequency
SharpOSHost_GetMillis() → uint64                      // GetTickCount64
SharpOSHost_GetSystemTime() → FILETIME                // GetSystemTime[AsFileTime]

SharpOSHost_TlsAlloc() → DWORD                        // TlsAlloc
SharpOSHost_TlsFree(idx)                              // TlsFree
SharpOSHost_TlsGet(idx) → void*                       // TlsGetValue
SharpOSHost_TlsSet(idx, val)                          // TlsSetValue

SharpOSHost_DebugPrint(str)                           // OutputDebugString
SharpOSHost_DebugBreak()                              // DebugBreak (int 3)

SharpOSHost_FlushICache(addr, size)                   // FlushInstructionCache
SharpOSHost_MemoryBarrier()                           // FlushProcessWriteBuffers (mfence)

// EH primitives (Phase 1 unwinder extensions)
SharpOSHost_RegisterFunctionTable(base, callback)     // RtlInstallFunctionTableCallback
SharpOSHost_UnregisterFunctionTable(handle)           // RtlDeleteFunctionTable
```

**~20 SharpOSHost_* functions** = full true kernel surface (rest of L3 stays in shim layer with stubs/aborts/minimal impls). This is the **honest D11 firewall** spec.

## Next implementation

1. **Phase 6.1 stub layer** (`pal/sharpos/winapi_shim.cpp` extended): 69 ABORT_FATAL + 30 STUB_OK + 17 MINIMAL = 116 trivial impls. ~1-2 weeks coding.
2. **SharpOSHost_* skeleton** (C# `OS/src/PAL/SharpOSHost/`): ~20 entry points. Initially fatal-only — fill в Phase 6.1.a → 6.1.b → 6.1.c.
3. **EH integration** (L5 separate): `__C_specific_handler` + `__CxxFrameHandler3` personality routines on top of Phase 1 unwinder primitives.

After this — `coreclr_initialize()` smoke-test possible.
