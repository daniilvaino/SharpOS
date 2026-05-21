# Step 99 — PAL census cleanup: +15 OK probes, all algorithm/policy moved to kernel C#

**Date:** 2026-05-21
**Status:** ✅ Census `OK=37 DEG=2 FAIL=7` vs. prior `OK=22 DEG=2 FAIL=22`. PAL has no algorithmic code or hardcoded policy.

## Result

```
PAL/OS census..................... OK  (OK=37  DEG=2  FAIL=7)
```

**+15 probes** moved to OK. The newly-passing probes:

- `Directory.GetCurrentDirectory` (×2 sections)
- `Path.GetTempPath` / `Path.GetFullPath`
- `Directory.Exists(.)`
- `Environment.{SystemDirectory, MachineName, OSVersion, UserName}`
- `RuntimeInformation.OSDescription`
- `Env.GetEnvironmentVariables`
- `Directory.EnumerateFiles(.)`
- `DateTime.Now`
- `RandomNumberGenerator.Fill`
- `SHA256.HashData`
- `Dns.GetHostName`

Still red (deferred): `Socket ctor (TCP)` [winsock], `File.{WriteAllText,ReadAllText,Delete}` [writable FS], `CultureInfo.GetCultureInfo(ru-RU)` [invariant-only by design], `GZipStream` [zlib], `Process.Start(dummy)` [process model].

## Architectural invariant respected

> CLAUDE.md "C# is the only source language": **all algorithms and policy
> live kernel-side; PAL is a thin ABI bridge with no logic.**

Pass 7 of this step refactored everything added in passes 1-6: SHA-256
algorithm, identity strings, OS version, timezone bias, and "which paths
exist" policy were all hoisted out of `crt_imp_stubs.cpp` into managed C#
under `OS/src/Kernel/` and `OS/src/PAL/SharpOSHost/`. The PAL now contains
only ABI shape transformations (wchar ↔ ASCII zero-extension, Win32
length-protocol marshaling, struct field offsets, sentinel-handle
dispatch).

## What moved kernel-side

### `OS/src/Kernel/Crypto/Sha256.cs` (new)

Full SHA-256 (FIPS 180-4) in managed C#: 8 H-words, 64-entry K table,
compress/update/final/snapshot/one-shot. K table inlined into Compress
via `stackalloc uint[64] { ... }` to avoid the `ClassConstructorRunner`
trap that a `static readonly uint[]` would trigger in NoStdLib.

State (`Sha256State`) is a fixed-layout struct (8×4 H + 64 buffer + 4
bufLen + 8 totalBytes = 108 bytes) suitable for heap or stack allocation;
the bridge layer hands out heap-allocated boxes via `GcHeap.AllocateRaw`.

### `OS/src/PAL/SharpOSHost/Sha256Bridge.cs` (new)

`[RuntimeExport]` surface:

| Export | Purpose |
|---|---|
| `SharpOSHost_Sha256_Create()` | Allocate state, init, return opaque handle |
| `SharpOSHost_Sha256_Update(h, data, len)` | Incremental hash |
| `SharpOSHost_Sha256_Final(h, out32)` | Finalize, write 32 bytes |
| `SharpOSHost_Sha256_Snapshot(h, out32)` | Non-destructive finalize-of-copy |
| `SharpOSHost_Sha256_Reset(h)` | Re-init same allocation |
| `SharpOSHost_Sha256_Destroy(h)` | No-op (mark-sweep GC frees on drop) |
| `SharpOSHost_Sha256_OneShot(data, len, out32)` | Stateless one-shot path |

### `OS/src/PAL/SharpOSHost/SystemIdentity.cs` (new)

Single export `SharpOSHost_GetSystemString(int kind, byte* outBuf, int outBufSize)`
serves all Win32 identity strings via a kind tag:

| Kind | Returns |
|---|---|
| `KindCurrentDir`   (0) | `\sharpos` |
| `KindTempPath`     (1) | `\sharpos\tmp\` |
| `KindSystemDir`    (2) | `\sharpos\system32` |
| `KindWindowsDir`   (3) | `\sharpos` |
| `KindMachineName`  (4) | `SHARPOS` |
| `KindUserName`     (5) | `local` |
| `KindHostName`     (6) | `sharpos` |
| `KindOsName`       (7) | `SharpOS` |
| `KindTimeZoneName` (8) | `UTC` |

Win32-style length protocol: `outBuf=null` returns required size incl
NUL; `outBufSize > len` copies bytes + NUL and returns chars excl NUL.

Bytes written inline in each `case` (no `static byte[]` to avoid
`ClassConstructorRunner`).

Also: `SharpOSHost_GetOSVersion(major*, minor*, build*)` reporting 10.0.26100
(mirrors the host build target until SharpOS diverges enough to claim
its own version) and `SharpOSHost_GetTimeZoneBiasMinutes()` returning 0
(UTC).

### `OS/src/PAL/SharpOSHost/FileSystemQuery.cs` (new)

`SharpOSHost_GetFileAttributes(utf8Path) -> uint`. Single policy point
for "which paths exist and what are their Win32 attributes". Currently
recognises `\sharpos` / `.` as directory, everything else as
`INVALID_FILE_ATTRIBUTES`. Future writable-FS lands here.

## What the PAL still does (legitimately)

Only ABI shape transformations:

- **wchar_t ↔ UTF-8/ASCII narrowing**: `sharpos_wpath_to_ascii`,
  `sharpos_wfetch` zero-extend each byte to `wchar_t` for Win32 wide
  buffers. Safe because every kernel-side string is 7-bit ASCII.
- **Win32 length protocol marshaling**: `sharpos_winsize_fetch` handles
  the in/out `nSize*` quirks (`ERROR_MORE_DATA` on too-small with
  required size, length-excl-NUL on success).
- **Win32 struct field offsets**: `RtlGetVersion` writes
  `dwMajor/dwMinor/dwBuild` at the right offsets in
  `RTL_OSVERSIONINFOEXW`; `sharpos_fill_tz` lays out
  `TIME_ZONE_INFORMATION` (172 B) and
  `DYNAMIC_TIME_ZONE_INFORMATION` (432 B). Policy values come from
  `SharpOSHost_GetOSVersion` / `SharpOSHost_GetTimeZoneBiasMinutes` /
  `KindTimeZoneName`.
- **Sentinel-handle dispatch**: LoadLibrary returns
  `SHARPOS_{ADVAPI32,KERNEL32,OLE32,BCRYPT,SECUR32,SYSCRYPTO,SYSNATIVE}_HMODULE`
  for the corresponding DLL names; GetProcAddress branches by sentinel to
  the in-image stubs. New for this step: `SHARPOS_SECUR32_HMODULE` (for
  `secur32.dll` → `GetUserNameExW`), `SHARPOS_SYSCRYPTO_HMODULE` (for
  `libSystem.Security.Cryptography.Native.OpenSsl` → `CryptoNative_*`).
- **Constant-return stubs**: `DeleteFileW` → `ERROR_FILE_NOT_FOUND`,
  `FindFirstFileW/Ex` → `INVALID_HANDLE_VALUE` + `ERROR_FILE_NOT_FOUND`
  (empty enumeration), `FindNextFileW` → `ERROR_NO_MORE_FILES`,
  `SetThreadDescription` → `S_OK`. These return Win32 codes that
  represent "feature not available" -- no policy, no logic.

## Module sentinels added

- `SHARPOS_SECUR32_HMODULE` → `secur32.dll` for `GetUserNameExW`.
- `SHARPOS_SYSCRYPTO_HMODULE` → `libSystem.Security.Cryptography.Native.OpenSsl`
  for the RNG / EVP hash surface used by BCL crypto.

## kernel32 resolver additions

`GetCurrentDirectoryW`, `GetTempPathW`, `GetTempPath2W`,
`GetSystemDirectoryW`, `GetWindowsDirectoryW`, `GetComputerNameW`,
`GetComputerNameExW`, `RtlGetVersion`, `GetVersionExW`,
`GetFileAttributesW`, `GetFileAttributesExW`, `FindFirstFileW`,
`FindFirstFileExW`, `FindNextFileW`, `FindClose`, `DeleteFileW`,
`GetTimeZoneInformation`, `GetDynamicTimeZoneInformation`,
`GetSystemTimePreciseAsFileTime`, `SetThreadDescription`.

## bcrypt resolver additions

`BCryptOpenAlgorithmProvider`, `BCryptCloseAlgorithmProvider`,
`BCryptCreateHash`, `BCryptHashData`, `BCryptFinishHash`,
`BCryptDestroyHash`, `BCryptGetProperty`.

## syscrypto resolver (full set, sentinel new this step)

`CryptoNative_GetRandomBytes`, `CryptoNative_EnsureOpenSslInitialized`,
`CryptoNative_EvpSha256`, `CryptoNative_EvpMdCtxCreate`,
`CryptoNative_EvpDigestUpdate`, `CryptoNative_EvpDigestReset`,
`CryptoNative_EvpDigestFinalEx`, `CryptoNative_EvpDigestCurrent`,
`CryptoNative_EvpDigestOneShot`, `CryptoNative_EvpMdCtxDestroy`,
`CryptoNative_EvpMdSize`, `CryptoNative_GetMaxMdSize`.

## sysnative resolver additions

`SystemNative_GetCryptographicallySecureRandomBytes`,
`SystemNative_GetHostName`.

## Diagnostic methodology

The cheap-win iteration ran `Verbose=true` once to capture the
`[GetProcAddress kernel32/secur32/syscrypto/...] unknown name=...` lines
that pinpoint which P/Invoke names the BCL was calling but our PAL
didn't resolve. From that list each batch of stubs was added in a
focused pass. Once census stops moving, flip Verbose back to false.

The Verbose-gated trace markers from step 98 (`[KOT]`, `[HS]`,
`[JCCLEW]`, etc.) remain in place and inert; future thread/JIT halts can
flip Verbose with a kernel-only rebuild.

## Files changed

### Kernel (`OS/`)

- `OS/src/Kernel/Crypto/Sha256.cs` *(new)* — SHA-256 algorithm.
- `OS/src/PAL/SharpOSHost/Sha256Bridge.cs` *(new)* — hash exports.
- `OS/src/PAL/SharpOSHost/SystemIdentity.cs` *(new)* — string/version/TZ exports.
- `OS/src/PAL/SharpOSHost/FileSystemQuery.cs` *(new)* — file-attr export.
- `OS/src/PAL/SharpOSHost/Diagnostics.cs` — Verbose flipped to true during
  diagnosis then back to false at step close.

### Fork (`dotnet-runtime-sharpos/`)

- `src/coreclr/pal/sharpos/crt_imp_stubs.cpp` — +730 lines:
  - SECUR32 / SYSCRYPTO sentinels + LoadLibrary handlers + GetProcAddress
    dispatch.
  - BCrypt + EVP hash surface (forwarders to `SharpOSHost_Sha256_*`).
  - kernel32 string getters / FS attrs / find / timezone / version
    (forwarders to `SharpOSHost_GetSystemString` /
    `SharpOSHost_GetOSVersion` / `SharpOSHost_GetTimeZoneBiasMinutes` /
    `SharpOSHost_GetFileAttributes`).
  - sysnative additions (`SystemNative_GetCryptographicallySecureRandomBytes`,
    `SystemNative_GetHostName`) -- both forwarders.

## What's deferred (still FAIL)

- **Socket ctor (TCP)** -- needs winsock surface (`WSAStartup`, `socket`,
  `bind`, `recv`, `send`, ...). Significant work; defer to dedicated
  net-stack phase.
- **File.WriteAllText / File.Delete / File.ReadAllText** -- needs
  writable FAT. Reader-only FAT today (step 88).
- **CultureInfo.GetCultureInfo(ru-RU)** -- intentional FAIL: kernel runs
  with globalization-invariant mode (BCL constant). Won't change.
- **GZipStream** -- needs zlib P/Invoke surface (deflate/inflate). Real
  algorithm; sizable port to managed C#. Defer.
- **Process.Start(dummy)** -- needs a process model that can actually
  spawn an executable. Defer to Phase E10+ alongside ThreadPool /
  process work.

## Next step

Phase E10 (ThreadPool implementation) or Phase E9.b/c (synchronization
PAL surface routing real Event/Semaphore via HandleTable -- currently
fakes "signaled" lie). E9.b unblocks Monitor.Wait/Pulse on arbitrary
objects (a foundation for Tasks). Choose based on what census probe
target we want next.
