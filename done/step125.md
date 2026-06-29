# Step 125 — PowerShell cmdlets register & execute on bare metal

После step124 (REGDISPLAY layout / Layer 3 Windows-SPC commitment) PowerShell 7.5.5 загружался, печатал баннер и принимал ввод, но **ни один cmdlet не был зарегистрирован** — `Get-ChildItem`, `Write-Output`, `cd`, `cat` → `The term 'X' is not recognized`. Step125 проводит PowerShell сквозь полный bootstrap до момента когда built-in cmdlet'ы реально работают: `ls` показывает каталог с размерами, `cat file` читает содержимое, `cls` очищает (cosmetic).

Веха: **`Get-ChildItem`, `Write-Output`, `Set-Location`, `Get-Content`, `Get-Command`, `Clear-Host` и большинство commands из `Microsoft.PowerShell.Commands.Management/.Utility` работают на bare-metal SharpOS через форк CoreCLR без modifications PowerShell sources.**

## Архитектура

Step делится на четыре крупных фронта, найденных последовательно по log-evidence:

1. **Path normalization** (std/no-runtime) — drive-letter пути не распознавались как rooted, BCL Path.GetFullPath дублировал префикс
2. **EH unwind EstablisherFrame** — MSVC SEH funclet ABI требует frame base из exception-time контекста, ApplyUnwindInfo возвращал post-unwind значение
3. **PAL surface buildout** — Safer/Wldp/ntdll directory APIs / WIN32 file info / network drive enumeration
4. **Deployment chain** — Modules manifests + TPA expansion + disk sizing

### 1. Path normalization root cause

**Симптом:** `[host] FileOpen path="\sharpos\C:\sharpos\pwsh\System.Management.Automation.dll" → not found`. Удвоенный префикс `\sharpos\` + `C:\sharpos\` → file not found → `FileLoadException` → cascade.

**Корень:** в `std/no-runtime/shared/Bcl/Path.cs::IsPathRooted` проверка только `path[0] == '\\' || path[0] == '/'`. Drive-letter паттерн `C:\X` НЕ считался rooted. `GetFullPath("C:\\sharpos\\pwsh\\X.dll")`:
1. `IsPathRooted` → false (потому что `C` не сепаратор)
2. → `Normalize(CurrentDirectory + path)` = `Normalize("C:\\sharpos\\" + "C:\\sharpos\\pwsh\\X.dll")` = `"C:\\sharpos\\C:\\sharpos\\pwsh\\X.dll"`

CoreCLR `LongFile::NormalizePath` ([longfilepathwrappers.cpp:463](../dotnet-runtime-sharpos/src/coreclr/utilcode/longfilepathwrappers.cpp#L463)) звала `GetFullPathNameW` → fork shim → `SharpOSHost_GetFullPathName` → наш `std/no-runtime/shared/Bcl/Path.cs::GetFullPath` → удваивала.

**Фикс:** `IsPathRooted` теперь распознаёт обе формы (path-rooted `\X` И drive-rooted `X:`). Добавлен `IsPathFullyQualified` — только fully qualified (drive+sep `X:\X` или UNC `\\X`). `GetFullPath` использует `IsPathFullyQualified` для skip-normalize early-return.

```csharp
public static bool IsPathRooted(string? path) {
    if (path == null || path.Length == 0) return false;
    char c = path[0];
    if (c == DirectorySeparatorChar || c == AltDirectorySeparatorChar) return true;
    if (path.Length >= 2 && path[1] == ':' &&
        ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))) return true;
    return false;
}
```

Зеркальный фикс в kernel `SharpOSHost_GetFileAttributes` ([FileSystemQuery.cs](../OS/src/PAL/SharpOSHost/FileSystemQuery.cs)) — стрипает `C:\` префикс в начале (mirror `SharpOSHost_FileOpen` normalization), плюс расширена логика распознавания путей: drive root (`\`, `/`, `""`), `\sharpos` корень, любой `\sharpos\…` без расширения → directory. Пути с расширением проверяются через `Platform.TryReadFile`.

`SystemIdentity.cs` (kernel) — все `KindCurrentDir/TempPath/SystemDir/WindowsDir` теперь возвращают `C:\sharpos[...]` (с drive letter) вместо `\sharpos[...]`. BCL `Path.IsPathFullyQualified` на возвращённые пути даёт true → нет коллизии с user-supplied path при `Path.Join`/`Combine`.

### 2. EH unwind EstablisherFrame fix

**Симптом:** managed `try/catch` на `Thread::DoSignalAndWait::fin$0` (или другой MSVC SEH funclet) валился на `Thread::EnablePreemptiveGC` с CR2 = non-canonical pointer. Cascade в FileSystem PSDrive auto-mount → SetSessionStateDrive throw → каскад через `ProcessOneModulePath`.

**Корень:** `SehUnwind.cs::ApplyUnwindInfo` устанавливал `*establisherFrame = context->Rsp` ПОСЛЕ применения unwind кодов (на post-VU `RSP` после pop'a saved return address). Это caller's RSP. Windows ABI требует EstablisherFrame = **базу фиксированной стек-аллокации** функции, что для FP-функций = `FrameRegister − FrameOffset*16`, для FPO = `Rsp_orig` ДО применения unwind codes.

MSVC funclet'ы принимают EstablisherFrame в `RDX` и используют его как базу для доступа к parent locals — `mov rcx, [rdx + this_off]`. С неверной базой `this` указатель = соседний стек-слот = мусор → следующий deref → #GP/#PF.

**Фикс** (зеркалит fork [unwinder.cpp:1129](../dotnet-runtime-sharpos/src/coreclr/unwinder/amd64/unwinder.cpp#L1129)):
1. До unwind-цикла capture `originalRsp = context->Rsp` И `originalFrameRegValue = ReadReg(context, frameReg)` если `frameReg != 0`
2. Pre-scan unwind кодов на `UWOP_SET_FPREG` (op=3) → если `progress >= setFpRegOffset`, frame-pointer уже установлен
3. `*establisherFrame = (frameReg != 0 && fpregEstablished) ? originalFrameRegValue - frameOffset*16 : originalRsp`

CHAININFO рекурсия теперь устанавливает EstablisherFrame в **child** и передаёт `null` в parent (parent не должен переустанавливать значение из уже-изменённого контекста).

Это закрыло Phase D остаточный фронт (managed-funclet RDX) и unblock'ило весь runtime threading в PS.

### 3. PAL surface buildout

Дополнения к [crt_imp_stubs.cpp](../dotnet-runtime-sharpos/src/coreclr/pal/sharpos/crt_imp_stubs.cpp):

#### Network drive enumeration (mpr/iphlpapi)

PS FileSystemProvider при `InitializeDefaultDrives` зовёт `WNetGetConnectionW` для классификации local vs network. Без stub'a → `EntryPointNotFoundException` → каскад до SetSessionStateDrive → C: drive не зарегистрирован → `Get-ChildItem` resolves to alias but cmdlet не загружен.

- **`mpr.dll` sentinel module** + `WNetGetConnectionW` → `ERROR_NOT_CONNECTED` (2250) = "C: не сетевой диск"
- **`iphlpapi.dll` sentinel module** + `GetAdaptersAddresses` → `ERROR_NO_DATA` (232) = "нет адаптеров"

#### Safer / Wldp / SystemPolicy

PS 7.5 определяет ConstrainedLanguage / FullLanguage через `SystemPolicy::GetSystemLockdownPolicy()`. Цепочка:
1. `WldpQueryWindowsLockdownPolicy(out mode)` — алиас на наш `WldpQueryWindowsLockdownMode` (PS 7.5 переименовал API)
2. `SaferIdentifyLevel` + `SaferComputeTokenFromLevel` — Software Restriction Policy classifier

**Без `SaferComputeTokenFromLevel`** PS бросает `EntryPointNotFoundException` в `Import-Module Microsoft.PowerShell.Management` → cmdlet находится в модуле, но `Get-ChildItem: module could not be loaded` (видно в логе).

Все возвращают sentinel SAFER_LEVELID_FULLYTRUSTED (0x40000). PS → FullLanguage → built-in modules auto-load → cmdlets register.

#### Directory enumeration через CreateFileW BACKUP_SEMANTICS + NtQueryDirectoryFile

Windows BCL `FileSystemEnumerator` ([FileSystemEnumerator.Windows.cs:165](../dotnet-runtime-sharpos/src/libraries/System.Private.CoreLib/src/System/IO/Enumeration/FileSystemEnumerator.Windows.cs)) использует:
1. `CreateFileW(dir, GENERIC_READ, ..., FILE_FLAG_BACKUP_SEMANTICS)` → handle на каталог
2. `NtQueryDirectoryFile(handle, FileInformationClass=FileFullDirectoryInformation, ...)` → пакетная enumeration

**`FindFirstFileW`/`FindNextFileW`** этой BCL цепочкой НЕ используется (только в legacy/Linux pal).

Реализация:
- `DirHandle` struct (magic + path + nextIndex) аллоцируется через `SharpOSHost_HeapAlloc`
- `CreateFileW` детектит `FILE_FLAG_BACKUP_SEMANTICS=0x02000000` → возвращает DirHandle (magic at offset 0)
- `sharpos_is_dir_handle` — sanity-guard: handle должен быть в диапазоне `0x100000..0x800000000000` (фильтрует малые int-sentinel handles вроде HKEY=0x4D из registry stub'ов, иначе `*(uint64_t*)0x4D` → page fault)
- `NtQueryDirectoryFile` — paks entries в `FILE_FULL_DIR_INFORMATION` формат (44 байта header + name, выровнено к 8), поддерживает 4 info-классов (1/2/3/60), `RestartScan` flag, `ReturnSingleEntry`. Iterates через `SharpOSHost_FindDirEntry` (kernel-side, делегирует в `Platform.TryReadDirectoryEntry` → FAT enum)
- `NtClose` + `CloseHandle` — диспатчат на DirHandle free через `SharpOSHost_HeapFree`, иначе — через `SharpOSHost_CloseHandle` в kernel HandleTable
- Параллельно реализован `FindFirstFileW`/`FindNextFileW`/`FindClose` со стейтовой `DirIterState` — для legacy путей и FindFirst-based кода

Kernel-side `SharpOSHost_FindDirEntry(utf8Path, index, outName, outAttrs)` ([FileSystemQuery.cs](../OS/src/PAL/SharpOSHost/FileSystemQuery.cs)):
- Стрипает `C:\` префикс
- Для drive-root (`\` или `""`) возвращает синтетическую запись `sharpos` на index 0
- Иначе делегирует в `Platform.TryReadDirectoryEntry(path, index, ...)` (наш FAT enumerator)

#### File info / seek

- **`SetFilePointerEx`** — 64-bit вариант (BCL FileStream.Seek), обёртка вокруг существующего `SharpOSHost_FileSetPosition`
- **`GetFileInformationByHandleEx`** — три info-класса: `FileBasicInfo` (times+attrs), `FileStandardInfo` (size + directory bit), `FileAttributeTagInfo`. Для DirHandle возвращает directory bit + size=0, для file handle — размер из `SharpOSHost_FileGetSize`. Это unblock'ило `cat <file>` (Get-Content) И **размеры файлов в `ls` listing**

#### Прочие kernel32 / ntdll missing

- `RtlQueryProcessPlaceholderCompatibilityMode` → PHCM_APPLICATION_DEFAULT (0). FileSystem provider init требовал.
- `GetLogicalDrives` → bitmask C: only
- `GetVolumeInformationW` → volume label "SharpOS", FS type "FAT", FILE_CASE_PRESERVED_NAMES flag
- `GetDriveTypeW` → DRIVE_FIXED (3) для C:
- `K32EnumProcesses` → 1 process (pid=1)
- `AmsiNotifyOperation`/`AmsiNotifyOperationA` → S_OK
- `FillConsoleOutputCharacterW/A` + `FillConsoleOutputAttribute` → success no-op (Clear-Host не throw'ит, screen не очищается без real buffer)

#### Console pseudo-files + std handles

PowerShell открывает `CONOUT$` / `CONIN$` / `CONERR$` через CreateFileW. Сделано:
- [ConsoleWin32.cs](../OS/src/PAL/SharpOSHost/ConsoleWin32.cs) — `WriteConsoleW` UTF-16 → UART byte stream через `OS.Hal.Platform.WriteChar` (bypass'ит `Console.Quiet` чтобы PS видел вывод даже когда kernel diag muted)
- [ConsoleRead.cs](../OS/src/PAL/SharpOSHost/ConsoleRead.cs) — `ReadConsole` через `OS.Hal.Ps2Keyboard` + `LineEditor`, polling до Enter, echo BS-space-BS на Backspace
- [FileSystemPolicy.cs](../OS/src/PAL/SharpOSHost/FileSystemPolicy.cs) `ClassifyConsoleFileName` — kernel decides CONOUT$/CONERR$ → STD_OUTPUT, CONIN$ → STD_INPUT
- Std handle sentinels: `0x0000_BEEF_C0DE_000{1,2,3}`

#### Прочая PAL policy

Все новые файлы в `OS/src/PAL/SharpOSHost/`:
- **AmsiPolicy.cs** — AmsiInitialize/Scan/NotifyOperation → CLEAN
- **ComPolicy.cs** — CoInitializeEx → S_OK, CoCreateInstance → REGDB_E_CLASSNOTREG, CoTaskMemAlloc через NativeArena
- **LockdownPolicy.cs** — WLDP API (LOCKDOWN_OFF)
- **ProcessAndCodepage.cs** — OpenProcess (pid=1), GetCPInfoEx, K32EnumProcesses, LookupAccountName
- **Registry.cs** — 8 RegXxx functions, all return FILE_NOT_FOUND для subkeys, HKLM/HKCU recognized
- **ShellFolders.cs** — SHGetKnownFolderPath → E_FAIL
- **ThreadApartment.cs** — STA/MTA tracking, ThreadNative_SetApartmentState QCALL
- **ThreadStubs.cs** — `WaitForMultipleObjects` теперь real wait-any (через poll loop по single-handles, HPET deadline)
- **TokenSecurity.cs** — 10 advapi32 token functions (controlled failure ERROR_NO_SUCH_PRIVILEGE)
- **TypeEquivalence.cs** — `RuntimeTypeHandle_IsEquivalentTo` QCALL → false
- **UserUiPolicy.cs** — SystemParametersInfo zero-out, GetSystemMetrics → 0, GetConsoleWindow → null

### 4. Deployment chain

#### Modules + TPA

- [run_build.ps1](../run_build.ps1) теперь копирует `C:\Program Files\PowerShell\7\Modules\` → `\sharpos\pwsh\Modules\` рекурсивно (без этого PS не находит manifests → не auto-import'ит cmdlets)
- TPA генерация расширена: SPC + fx/* + **pwsh/\*.dll** (минус дубликаты SPC + дубликаты fx-имён) + NormalHello. Это unblock'ило резолв `Microsoft.PowerShell.Commands.Management/Utility.dll` через TPABinder по имени.
- Все пути в `s_propValTPA` / `s_propValAppPaths` / `s_normalAppPath` + tpa.txt теперь `C:\sharpos\…` формате (не `\sharpos\…`)

#### Empty directories

- `\sharpos\tmp\` и `\sharpos\system32\` создаются при build deployment — PS valid'ит paths из `GetTempPath`/`GetSystemDirectory` при PSDrive init

#### Build infrastructure

- [build_media_xorriso.ps1](../build_media_xorriso.ps1): `EspImageSizeMb` 128→512, `VhdDiskSizeMb` 256→768 (full PS deployment занимает ~245 MB)
- [OS.csproj](../OS/OS.csproj): новый `SkipCoreClr` MSBuild property → собрать kernel-only image (без CoreCLR linkage). Полезно для замера базы или sub-host'ed варианта. Wraps CoreCLR `<LinkerArg>` блок + activates `SKIP_CORECLR` constant для conditional `#if` в `CoreClrProbe.cs` и `BootSequence.cs`.

## Уроки

### Не доверять интуиции про симптомы

User'ская гипотеза «TPA не должен содержать полные пути» оказалась inverse-incorrect (TPA full paths — это стандарт CoreCLR), но направление было верным: **path doubling приходил не из TPA**, а из managed-уровневого `Path.Join` в PS внутри `ExecutionContext.LoadAssembly` поверх **нашей kernel-side `IsPathRooted`** которая не распознавала drive-letter. Без probe в `SharpOSHost_FileOpen` найти source было невозможно — все promosed paths приходили через managed BCL слой.

### Probe-driven RCA

Без managed-side instrumentation throws через `ExceptionDispatchInfo.Throw` не видны в `[seh] throw`. Probes в native PAL layer (CreateFileW return-address chain, GetFileAttributesEx path log) — single decisive tool для трейсинга.

### Architecture-correct path resolution

Drive-rooted `C:\X` vs path-rooted `\X` vs UNC `\\X` — три разные категории. BCL обходится через unified `Path.IsPathFullyQualified` который четко классифицирует все три. Наш std/no-runtime должен зеркалить эту semantics, иначе любой managed Roslyn/Reflection/AssemblyLoadContext вход с user-friendly Windows path даст concat-driven duplicate prefix.

### Layer-1-Layer-2 sanity guard на handles

Fork-side stub'ы для registry/COM/etc. возвращают **малые int sentinel handles** (0x4D, 0x1234). Наш HandleTable dispatcher в CloseHandle не должен dereference handle без sanity-guard на pointer range. `sharpos_is_dir_handle` именно это и делает (rejects v < 0x100000).

## Файлы

### Main repo (OS/)

```
M  OS/OS.csproj                                         — SkipCoreClr toggle
M  OS/src/Boot/BootSequence.cs                          — #if SKIP_CORECLR for CoreClrProbe call
M  OS/src/Boot/EH/HwFaultBridge.cs                      — #if SKIP_CORECLR for SharpOS_CoreCLR_TryHandleHardwareException
M  OS/src/Hal/Console.cs                                — Console.Quiet flag (mute kernel diag during CLR run)
M  OS/src/Kernel/Diagnostics/CoreClrProbe.cs            — TPA/APP_PATHS/app path в C:\sharpos\…, SKIP_CORECLR gate
M  OS/src/PAL/SharpOSHost/CrtHeapStubs.cs               — FileOpen normalize C:\ stripping
M  OS/src/PAL/SharpOSHost/Diagnostics.cs                — DebugPrintForced respects Console.Quiet
M  OS/src/PAL/SharpOSHost/FileSystemQuery.cs            — GetFileAttributes drive-strip + path tree probe, FindDirEntry
M  OS/src/PAL/SharpOSHost/SehDispatch.cs                — #if SKIP_CORECLR for SharpOSHost_GetCurrentFrame
M  OS/src/PAL/SharpOSHost/SehUnwind.cs                  — EstablisherFrame fix (FrameReg pre-capture)
M  OS/src/PAL/SharpOSHost/SystemIdentity.cs             — KindCurrentDir/Temp/System/Windows → C:\sharpos[...]
M  OS/src/PAL/SharpOSHost/ThreadStubs.cs                — WaitForMultipleObjects real wait-any

A  OS/src/PAL/SharpOSHost/AmsiPolicy.cs
A  OS/src/PAL/SharpOSHost/ComPolicy.cs
A  OS/src/PAL/SharpOSHost/ConsoleRead.cs
A  OS/src/PAL/SharpOSHost/ConsoleWin32.cs
A  OS/src/PAL/SharpOSHost/FileSystemPolicy.cs
A  OS/src/PAL/SharpOSHost/LockdownPolicy.cs
A  OS/src/PAL/SharpOSHost/ProcessAndCodepage.cs
A  OS/src/PAL/SharpOSHost/Registry.cs
A  OS/src/PAL/SharpOSHost/ShellFolders.cs
A  OS/src/PAL/SharpOSHost/ThreadApartment.cs
A  OS/src/PAL/SharpOSHost/TokenSecurity.cs
A  OS/src/PAL/SharpOSHost/TypeEquivalence.cs
A  OS/src/PAL/SharpOSHost/UserUiPolicy.cs

M  std/no-runtime/shared/Bcl/Path.cs                    — IsPathRooted drive-letter + IsPathFullyQualified, CurrentDirectory→C:\sharpos\
M  bootasm/CoffStub.Generator/CoffStub.Generator.targets — re-register existing .obj when target skipped
M  build_media_xorriso.ps1                              — ESP 128→512, VHD 256→768
M  run_build.ps1                                        — pwsh/Modules deploy + TPA pwsh/* + tmp/system32 dirs + C:\ paths + SkipCoreClr switch
```

### Fork (dotnet-runtime-sharpos)

```
M  src/coreclr/pal/sharpos/crt_imp_stubs.cpp            — +2000 lines: DirHandle/NtQueryDirectoryFile/NtClose, SaferIdentify/Compute/Get/Close, GetFileInformationByHandleEx, SetFilePointerEx, FillConsoleOutput*, GetLogicalDrives, GetVolumeInformationW, GetDriveTypeW, K32EnumProcesses, WNetGetConnectionW, GetAdaptersAddresses, RtlQueryProcessPlaceholderCompatibilityMode, AmsiNotifyOperation, FindFirstFile/FindNextFile real impls, WldpQueryWindowsLockdownPolicy alias
M  src/coreclr/pal/sharpos/winapi_shim.cpp              — weak fallback for new SharpOSHost_* exports
M  src/coreclr/vm/qcallentrypoints.cpp                  — ThreadNative_SetApartmentState/GetApartmentState + RuntimeTypeHandle_IsEquivalentTo QCALL gating
```

## Состояние после step125

```
PS C:\sharpos> ls
    Directory: C:\sharpos

Mode    LastWriteTime    Length Name
----    -------------    ------ ----
d----                          fx
d----                          pwsh
d----                          tmp
-a---             16003072      System.Private.CoreLib.dll
-a---                  ...      tpa.txt

PS C:\sharpos> cat tpa.txt
C:\sharpos\System.Private.CoreLib.dll;C:\sharpos\fx\Microsoft.CSharp.dll;...
```

Работают:
- Получение списка cmdlet'ов: `Get-Command`
- Filesystem operations: `Get-ChildItem`, `Set-Location`, `Get-Content`, `Test-Path`, `Get-Item`
- Output: `Write-Output`, `Write-Host`, `Write-Error`
- Pipeline + format: built-in formatters работают
- Clear-Host (cosmetic — нет real buffer clear, но не throw'ит)
- Auto-completion базовая
- `cat <file>` читает файл (любой текстовый из FAT)
- `ls` показывает размеры файлов (через GetFileInformationByHandleEx)

Не работает (deferred):
- **PSReadLine** — `Cannot load PSReadline module. Console is running without PSReadline.` Нужны дополнительные Console manipulation APIs + async input. Big task.
- **Backspace в input** — PS2 driver + LineEditor BS-space-BS echo есть, но возможно decode KeyKind не срабатывает для scancode 0x0E. Не проверено отдельным smoke-test'ом.
- **File deletion / write** — наш FAT read-only. Нужна writable FS implementation (отдельный pillar).
- **Add-Type через Roslyn** — теоретически должен работать (форк CoreCLR + clrjit + Microsoft.CodeAnalysis.CSharp.dll в pwsh/ и TPA), но не тестировано на bare-metal.
- **Network cmdlets** — нет network stack.

## Что отложено

1. **Step125 в README + comparative table** — пользователь хотел сперва проверить на VBox (увеличили disk image), потом обновим
2. **`docs/coreclr-hosted-limits.md`** — sync пройдёт после VBox verification
3. **Cosmetic UX** — Backspace decode, PSReadLine attempt, screen clear через ANSI escape sequence

## Next step

Step126 — cosmetic improvements + writable FS investigation:
- PSReadLine analysis (что нужно добавить чтобы загрузился)
- Backspace decode probe
- Clear-Host через ANSI `\e[2J\e[H` если cleaner буфер не нужен
- Writable FAT — pillar для `New-Item`, `Remove-Item`, `Out-File`
