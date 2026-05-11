## D1 — FINALIZED

### Решение

**Использовать Microsoft's `Interop.Error` enum** как lingua franca для error codes на C-ABI boundary между pal/sharpos/ (C++) и SharpOSHost (C#).

### Источники для воровства

**Source 1**: `dotnet/runtime/src/libraries/Common/src/Interop/Unix/Interop.Errors.cs`

- Microsoft's platform-agnostic errno abstraction
- Stable values (0x10001..0x10071) — designed специально для PAL boundary stability
- Production code (используется .NET file I/O, sockets, process management)
- ~75 codes покрывающих все типичные scenarios
- License: MIT (Microsoft .NET Foundation)

**Source 2**: `dotnet/runtime/src/libraries/Common/src/Interop/Unix/Interop.IOErrors.cs`

- Готовая translation `Interop.Error → System.Exception` (через HRESULT machinery)
- `GetExceptionForIoErrno()` — целевая функция для воровства
- License: MIT

**Source 3** (reference): `dotnet/runtime/src/libraries/Common/src/System/HResults.cs`

- ~115 .NET HRESULT constants
- Native error type для CoreCLR exception machinery
- License: MIT

### Архитектура

`SharpOS_SystemError` — стабильный C ABI namespace status codes. Provider environment-specific (per revised D10/D11).

#### Phase 2 — Windows-hosted spike

```
┌─────────────────────────────────────────────────────────────┐
│ SharpOSHost provider — Windows shim (C++)                  │
│ - sharpos_host_windows_shim.lib                             │
│ - Использует Win32 APIs для bodies                          │
│ - Возвращает SharpOS_SystemError values через extern "C"    │
│ - НЕТ managed code, НЕТ NativeAOT static library            │
└──────────────────────────┬──────────────────────────────────┘
                           │ C ABI boundary
                           │ (SHARPOS_* int32 status codes)
                           ▼
┌─────────────────────────────────────────────────────────────┐
│ pal/sharpos/ C++                                            │
│ - Получает SharpOS_SystemError values                       │
│ - Translation: SharpOS_SystemError → HRESULT/Win32 ERROR_*  │
│ - SetLastError() / возврат FALSE/NULL                       │
└──────────────────────────┬──────────────────────────────────┘
                           │ pal.h surface (Win32-shape)
                           ▼
┌─────────────────────────────────────────────────────────────┐
│ CoreCLR vm/                                                 │
│ - GetLastError() возвращает Win32 code                      │
│ - COMPlusThrowHR() throws System.Exception                  │
└─────────────────────────────────────────────────────────────┘
```

#### Phase 6 — Bare metal (provider environment-specific)

Provider может быть kernel-tier C# exports, C/C++ glue, или kernel-provided symbols (per revised D10/D11). Если provider это kernel-tier C# exports — тогда applies "managed provider" вариант:

```
┌─────────────────────────────────────────────────────────────┐
│ SharpOSHost provider — kernel-tier C# (optional Phase 6)   │
│ - Internal реалии (managed exceptions, kernel errors)       │
│ - Translation: internal → SystemError                       │
│ - Exposes [UnmanagedCallersOnly] symbols в kernel image     │
│ - НЕ отдельная NativeAOT static library                     │
└──────────────────────────┬──────────────────────────────────┘
                           │ Тот же C ABI boundary
                           │ (SHARPOS_* int32 status codes)
                           ▼
                    pal/sharpos/ C++ (без изменений)
                           ▼
                    CoreCLR vm/ (без изменений)
```

Provider может также быть pure C/C++ glue к kernel internal ABI — тогда managed code не задействован, как в Phase 2 spike. Решается в момент Phase 6.2 design (per D5).

### Конкретная implementation

**C ABI primary** (mandatory): `sharposhost_status.h` C++ header is the source of truth. Used by pal/sharpos/ и any provider в любом environment (Phase 2 Windows shim, Phase 6 kernel glue, Phase 6 kernel-tier C# exports).

**C# enum optional** (Phase 6 reference): mirror C# enum применим только если Phase 6 provider это kernel-tier C# exports. На Phase 2 Windows spike provider это C++ shim — C# mirror НЕ задействован.

#### Optional C# mirror — `SystemError` enum (Phase 6 reference)

Применимо только если Phase 6 provider реализован как kernel-tier C# code с `[UnmanagedCallersOnly]` exports. На Phase 2 spike — игнорировать.

csharp

```csharp
namespace SharpOS.Host;

// Stolen from dotnet/runtime: src/libraries/Common/src/Interop/Unix/Interop.Errors.cs
// Values match Microsoft's Interop.Error for binary compatibility.
//
// Why: Microsoft already designed this as a platform-agnostic errno abstraction
// for cross-platform PAL boundaries. Production-tested in .NET file I/O,
// sockets, process management. Documentation maintained by Microsoft.
//
// License: MIT (.NET Foundation)
public enum SystemError : int
{
    Success          = 0,

    E2Big            = 0x10001,    // Argument list too long
    EAcces           = 0x10002,    // Permission denied
    EAddrInUse       = 0x10003,    // Address in use
    EAddrNotAvail    = 0x10004,    // Address not available
    EAfNoSupport     = 0x10005,    // Address family not supported
    EAgain           = 0x10006,    // Resource unavailable, try again
    EAlready         = 0x10007,    // Connection already in progress
    EBadf            = 0x10008,    // Bad file descriptor
    EBadMsg          = 0x10009,    // Bad message
    EBusy            = 0x1000A,    // Device or resource busy
    ECanceled        = 0x1000B,    // Operation canceled
    EChild           = 0x1000C,    // No child processes
    EConnAborted     = 0x1000D,    // Connection aborted
    EConnRefused     = 0x1000E,    // Connection refused
    EConnReset       = 0x1000F,    // Connection reset
    EDeadlk          = 0x10010,    // Resource deadlock would occur
    EDestAddrReq     = 0x10011,    // Destination address required
    EDom             = 0x10012,    // Mathematics argument out of domain
    EDQuot           = 0x10013,    // Reserved
    EExist           = 0x10014,    // File exists
    EFault           = 0x10015,    // Bad address
    EFBig            = 0x10016,    // File too large
    EHostUnreach     = 0x10017,    // Host is unreachable
    EIDrm            = 0x10018,    // Identifier removed
    EILSeq           = 0x10019,    // Illegal byte sequence
    EInProgress      = 0x1001A,    // Operation in progress
    EIntr            = 0x1001B,    // Interrupted function
    EInval           = 0x1001C,    // Invalid argument
    EIO              = 0x1001D,    // I/O error
    EIsConn          = 0x1001E,    // Socket is connected
    EIsDir           = 0x1001F,    // Is a directory
    ELoop            = 0x10020,    // Too many levels of symbolic links
    EMFile           = 0x10021,    // File descriptor value too large
    EMLink           = 0x10022,    // Too many links
    EMsgSize         = 0x10023,    // Message too large
    EMultiHop        = 0x10024,    // Reserved
    ENameTooLong     = 0x10025,    // Filename too long
    ENetDown         = 0x10026,    // Network is down
    ENetReset        = 0x10027,    // Connection aborted by network
    ENetUnreach      = 0x10028,    // Network unreachable
    ENFile           = 0x10029,    // Too many files open in system
    ENoBufs          = 0x1002A,    // No buffer space available
    ENoDev           = 0x1002C,    // No such device
    ENoEnt           = 0x1002D,    // No such file or directory
    ENoExec          = 0x1002E,    // Executable file format error
    ENoLck           = 0x1002F,    // No locks available
    ENoLink          = 0x10030,    // Reserved
    ENoMem           = 0x10031,    // Not enough space
    ENoMsg           = 0x10032,    // No message of the desired type
    ENoProtoOpt      = 0x10033,    // Protocol not available
    ENoSpc           = 0x10034,    // No space left on device
    ENoSys           = 0x10037,    // Function not supported
    ENotConn         = 0x10038,    // Socket is not connected
    ENotDir          = 0x10039,    // Not a directory
    ENotEmpty        = 0x1003A,    // Directory not empty
    ENotRecoverable  = 0x1003B,    // State not recoverable
    ENotSock         = 0x1003C,    // Not a socket
    ENotSup          = 0x1003D,    // Not supported
    ENotTty          = 0x1003E,    // Inappropriate I/O control operation
    ENxIO            = 0x1003F,    // No such device or address
    EOverflow        = 0x10040,    // Value too large to be stored
    EOwnerDead       = 0x10041,    // Previous owner died
    EPerm            = 0x10042,    // Operation not permitted
    EPipe            = 0x10043,    // Broken pipe
    EProto           = 0x10044,    // Protocol error
    EProtoNoSupport  = 0x10045,    // Protocol not supported
    EPrototype       = 0x10046,    // Protocol wrong type for socket
    ERange           = 0x10047,    // Result too large
    EROfs            = 0x10048,    // Read-only file system
    ESpipe           = 0x10049,    // Invalid seek
    ESrch            = 0x1004A,    // No such process
    EStale           = 0x1004B,    // Reserved
    ETimedOut        = 0x1004D,    // Connection timed out
    ETxtBsy          = 0x1004E,    // Text file busy
    EXDev            = 0x1004F,    // Cross-device link

    // Aliases (POSIX permits same value)
    EOpNotSupp       = ENotSup,
    EWouldBlock      = EAgain,
}
```

#### C ABI primary — `sharposhost_status.h` (mandatory, used by all providers)

cpp

```cpp
// Mirror of SharpOS.Host.SystemError for C-ABI boundary
// Values MUST match C# enum exactly.
//
// See: SharpOS.Host/SystemError.cs

#ifndef _SHARPOS_HOST_STATUS_H
#define _SHARPOS_HOST_STATUS_H

#include <stdint.h>

typedef int32_t SharpOS_SystemError;

#define SHARPOS_SUCCESS              0
#define SHARPOS_E2BIG                0x10001
#define SHARPOS_EACCES               0x10002
// ... (all values mirroring C# enum) ...
#define SHARPOS_EXDEV                0x1004F

#endif // _SHARPOS_HOST_STATUS_H
```

#### Translation в pal/sharpos/

Pattern взят из `Interop.IOErrors.cs::GetExceptionForIoErrno`. Concrete C++ port:

cpp

```cpp
// pal/sharpos/error_translation.cpp
// Translates SharpOS_SystemError to Win32 ERROR_* / HRESULT
// Logic ported from dotnet/runtime: Interop.IOErrors.cs::GetExceptionForIoErrno

DWORD SystemErrorToWin32(SharpOS_SystemError err) {
    switch (err) {
        case SHARPOS_SUCCESS:        return ERROR_SUCCESS;
        case SHARPOS_ENOENT:         return ERROR_FILE_NOT_FOUND;
        case SHARPOS_EACCES:
        case SHARPOS_EBADF:
        case SHARPOS_EPERM:          return ERROR_ACCESS_DENIED;
        case SHARPOS_ENOMEM:         return ERROR_NOT_ENOUGH_MEMORY;
        case SHARPOS_EINVAL:         return ERROR_INVALID_PARAMETER;
        case SHARPOS_EEXIST:         return ERROR_ALREADY_EXISTS;
        case SHARPOS_ENAMETOOLONG:   return ERROR_FILENAME_EXCED_RANGE;
        case SHARPOS_ETIMEDOUT:      return ERROR_TIMEOUT;
        case SHARPOS_ECANCELED:      return ERROR_CANCELLED;
        case SHARPOS_ENOSYS:         return ERROR_NOT_SUPPORTED;
        // ... (full mapping based on Interop.IOErrors.cs) ...
        default:                     return ERROR_GEN_FAILURE;
    }
}
```

### Что украли

| Артефакт                                      | Источник                                                  | License |
| --------------------------------------------- | --------------------------------------------------------- | ------- |
| Enum values & semantics                       | `Interop.Error` (Microsoft)                               | MIT     |
| Stable platform-agnostic values               | `Interop.Error` (Microsoft)                               | MIT     |
| Translation logic                             | `Interop.IOErrors.cs::GetExceptionForIoErrno` (Microsoft) | MIT     |
| Comments & documentation                      | `Interop.Errors.cs` (Microsoft)                           | MIT     |
| HRESULT constants (для translation reference) | `HResults.cs` (Microsoft)                                 | MIT     |

### Что свое

- Naming convention C ABI (`SHARPOS_*` defines + `SharpOS_SystemError` typedef вместо `Interop.Error`)
- C++ header `sharposhost_status.h` (primary source of truth)
- C-ABI boundary integration (специфика нашего host/guest split per revised D10/D11)
- Optional C# mirror enum (Phase 6 reference, синхронизируется при необходимости)

### Maintenance plan

- При upstream merge from dotnet/runtime: проверить `Interop.Error` на изменения, обновить C++ header `sharposhost_status.h` (primary)
- Optional C# mirror (Phase 6 kernel-tier reference): синхронизируется при необходимости когда Phase 6 provider design финализирован
- Upstream редко меняет (production stable code, bus factor низкий)
- Shared spec файл + генератор для C++ (и optional C#) — premature engineering пока ручная синхронизация работает

### Преимущества решения

1. **Maximum theft**: interface, values, translation logic — всё от Microsoft, ничего не выдумываем
2. **Production-tested**: используется в .NET file I/O, sockets, process management — десятилетие в production
3. **Platform-agnostic by design**: Microsoft специально создал stable values чтобы не зависеть от raw libc errno
4. **Future WASI compat**: numerical values отличаются от WASI errno, но **semantic 1:1** (oба основаны на POSIX). Translation table при необходимости тривиальная
5. **Documentation inherited**: Microsoft maintains comments
6. **Translation готова**: `GetExceptionForIoErrno` portable как-is
7. **Соответствует invariant "developer не думает"**: dev знакомый с .NET I/O видит `EAccess`, `ENoEnt` — ровно те имена что в Microsoft codebase

### Принципы установленные D1 (применять к D2-D20)

1. **Steal interfaces, implement bodies**: на каждом decision сначала ищем production-stable existing interface
2. **Steal from production sources only**: experimental forks (runtimelab, prototype branches) — useful для inspiration, не для direct theft
3. **Maximum theft over design purism**: если Microsoft уже решил problem — берём как есть, не редизайним для "cleaner abstraction"
4. **Document sources**: каждый stolen artifact имеет comment ссылающийся на source file и license