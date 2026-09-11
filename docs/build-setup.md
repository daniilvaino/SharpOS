# Building SharpOS on a fresh machine

Windows-only. The repo is already cloned; everything below is host setup and
build order. Times are for a warm cache on a mid-range desktop.

## What has to be installed

| Component | Why | Notes |
|---|---|---|
| **Visual Studio 2022** with "Desktop development with C++" | `link.exe` links the kernel image; the CoreCLR fork builds with `clang-cl` | Include the **Clang/LLVM (clang-cl)** and **Windows 11 SDK** components. Plain MSVC is not enough for the fork: `pal/inc` assumes gcc-style `__attribute__` predefines. |
| **.NET SDK 10.x preview** | The fork (`dotnet/runtime` tree) pins it | `dotnet-runtime-sharpos/global.json` requires `10.0.105`, `allowPrerelease: true`. The SDK bootstraps its own toolset on first fork build. |
| **.NET SDK 8.x** | The kernel and apps target `net8.0` with `PublishAot` | ILC 8 is what the image is built and tested against. Installing 8 and 10 side by side is fine and expected. |
| **CMake** and **Python 3** | `dotnet/runtime`'s native build invokes them | Both must be on `PATH`. Ninja comes from the VS toolchain. |
| **PowerShell 7** | Build scripts, plus its `Modules` directory is staged into the image | `run_build.ps1` copies `C:\Program Files\PowerShell\7\Modules` into the ESP. Without it PowerShell boots with no cmdlets registered. |
| **QEMU** (`qemu-system-x86_64.exe`) | Runs the image | Looked up on `PATH`, then `C:\msys64\mingw64\bin`, `C:\Program Files\qemu`, `C:\Program Files\QEMU`. Override with `-QemuExe`. |
| **Windows Debugging Tools** (optional) | `tools/symbolize.ps1` | Needs `dbghelp.dll` from the Windows Kits Debuggers directory. Only for diagnosing crashes. |

Localized Windows is supported: the build scripts force UTF-8 and
`VSLANG=1033` so MSVC diagnostics stay readable in `last_build.log`.

## QEMU and its firmware

Nothing exotic is required of QEMU: `q35` with TCG (pure software emulation —
no WHPX or Hyper-V needed), `-vga std` for the GOP framebuffer, a serial line,
a QMP socket, and `-drive format=raw,file=fat:rw:esp`, which needs the **VVFAT**
backend that every stock Windows build ships. Any reasonably recent
`qemu-system-x86_64` works.

### Option A — official Windows installer (simplest)

Grab the 64-bit installer from <https://qemu.weilnetz.de/w64/> (these are the
builds linked from qemu.org's download page) and install to the default
location. It is self-contained: every dependent DLL sits next to the binary, and
`share\edk2-x86_64-code.fd` / `edk2-x86_64-vars.fd` come with it, which is
exactly what the firmware lookup below expects.

`run_build.ps1` finds it automatically at `C:\Program Files\qemu` or
`C:\Program Files\QEMU`.

### Option B — MSYS2

```
pacman -S mingw-w64-x86_64-qemu
```

This is the layout the repo's first lookup path assumes
(`C:\msys64\mingw64\bin\qemu-system-x86_64.exe`).

One caveat that costs people an hour: the MSYS2 build is **not** self-contained.
It links against the mingw64 runtime DLLs (glib, pixman, zlib, SDL2/GTK and
friends), so launching the executable by full path from PowerShell fails with
missing-DLL errors unless `C:\msys64\mingw64\bin` is on `PATH`. Either add it
permanently, or run the build from a MINGW64 shell. The installer in option A
has no such requirement — prefer it unless MSYS2 is already in use for other
reasons.

### Firmware

A UEFI firmware image is mandatory; SharpOS boots as an `EFI_APPLICATION`.
`run_build.ps1` searches, in order:

1. `ovmf\OVMF_CODE.strict-nx.fd` — the strict-NX build produced by `.\ovmf\build.ps1`;
2. `C:\msys64\mingw64\share\qemu\edk2-x86_64-code.fd`;
3. `C:\Program Files\qemu\share\edk2-x86_64-code.fd` (and the `QEMU` / `ovmf\OVMF_CODE.fd` spellings).

The matching `*-vars.fd` is picked up the same way and is optional. Whatever is
found gets copied into `OS/.qemu/firmware/` before launch, so QEMU never reads
from the install directory directly. Override either with `-OvmfCode` /
`-OvmfVars` if the firmware lives somewhere else.

Building the strict-NX OVMF is worth doing eventually — it enforces the NX
policy the kernel relies on — but the stock firmware is fine to start with.

### Running with a window (QEMU)

Headless is the default: the framebuffer exists but is not displayed, and serial
goes to the terminal. For an actual window (which is what you want for the
graphical output and for DOOM):

```powershell
$env:SHARPOS_GUI = 1
.\run_build.ps1 -Configuration Release -ForkConfig Release
```

Serial still arrives in the same terminal in that mode.

## Optional: bootable media and other hypervisors

None of this is needed to build or to run under QEMU — skip it on a first
setup. It matters when you want an image to boot on real hardware, in
VirtualBox, or to hand to someone else.

### `build_media_xorriso.ps1` — VHD and ISO

Builds a partitioned VHD and a bootable ISO out of the staged ESP. It shells
out to five external tools, each resolved from `PATH` first and then from a
fixed fallback path:

| Tool | Fallback the script tries | Role |
|---|---|---|
| `mformat.exe`, `mcopy.exe` | `C:\msys64\mingw64\bin` | format the FAT ESP image and copy files into it without mounting anything |
| `xorriso.exe` | `C:\msys64\usr\bin` | author the El Torito / UEFI bootable ISO |
| `qemu-img.exe` | `C:\msys64\mingw64\bin` | convert the raw image to VHD |
| `sfdisk.exe` | `C:\msys64\usr\bin` | write the GPT partition table |

Note the two different MSYS2 roots: `mingw64\bin` for the native builds
(mtools, qemu-img) and `usr\bin` for the MSYS-side ports (xorriso, sfdisk).
Under MSYS2 they come from `mingw-w64-x86_64-mtools`, `mingw-w64-x86_64-qemu`,
`xorriso` and `util-linux`. Each path can also be pointed at explicitly:
`-MtoolsBinDir`, `-XorrisoPath`, `-SfdiskPath`.

`-NoIso` builds only the VHD, which drops the `xorriso` requirement.

This is the one place where MSYS2 is genuinely the path of least resistance:
QEMU alone has a self-contained installer, but mtools/xorriso/sfdisk do not.

### `run_vbox.ps1` — VirtualBox

Runs the produced media under VirtualBox instead of QEMU. Needs VirtualBox
installed for `VBoxManage`; the path can be overridden with `-VBoxManagePath`.
Useful as a second opinion when a failure smells like a QEMU quirk rather than
a real bug.

### Application build scripts

`build_doom.ps1`, `build_fetch.ps1`, `build_aottests.ps1`, `build_launcher_gui.ps1`,
`build_launcher.ps1`
build the freestanding PE apps under `apps_native/`. They need only the .NET
SDK — no WSL, no separate cross-toolchain — and `run_build.ps1` stages their
output into the image if it is present.

The `probe_*.ps1` scripts are one-off analysis helpers for EH and unwind data,
not part of any build.

## Build order

Order matters — the kernel statically links the fork.

**1. CoreCLR fork** (~20-40 min first time, then incremental)

```powershell
cd dotnet-runtime-sharpos
.\build_clr_sharpos.ps1 -Configuration Release
```

Produces two artifacts the kernel needs:

- `artifacts/bin/coreclr/windows.x64.Release/coreclr_static.lib` — linked into `BOOTX64.EFI`;
- `artifacts/bin/coreclr/linux.x64.Release/IL/System.Private.CoreLib.dll` — the IL CoreLib
  dropped into the image and loaded at runtime.

Both must come from the **same** configuration: `MethodTable` layout differs
between Debug and Release, and a mismatch corrupts field offsets at runtime.

Useful switches: `-SkipLinuxIL` when only native fork code changed;
`-NinjaClean` when ninja aborts with `FindFirstFileExA(Note: including file: ...)`
(clang-cl's `/showIncludes` output corrupts ninja's depfile database — this
deletes `.ninja_deps` and costs ~30 s instead of a full `-Clean`).

**2. OVMF firmware** (optional, once)

```powershell
.\ovmf\build.ps1
```

Builds a strict-NX OVMF. `run_build.ps1` prefers it and falls back to QEMU's
bundled firmware, so this can be skipped on a first run.

**3. Kernel + image + run**

```powershell
.\run_build.ps1 -Configuration Release -ForkConfig Release
```

This publishes the kernel with NativeAOT, stages the ESP under `OS/.qemu/esp/`,
and launches QEMU. Output is teed to `last_build.log` (UTF-8) in the repo root.
That is COM1, the kernel log. What programs print (the launcher, its children,
PowerShell, the PAL/OS census) goes to COM3, which QEMU writes to `last_app.log`
next to it, and their error streams to COM4, `last_err.log`;
`tools/probe_report.ps1` reads all three.

Flags worth knowing: `-NoRun` builds without launching, `-SkipCoreClr` builds a
bare kernel with no hosted runtime, `-Stop` kills a running instance through QMP
on port 4444, `-ForkConfig Debug|Release` selects which fork build to link.

## MSB4062: `BootAsm.EmitCoffStubsTask` could not be loaded

Seen on a fresh clone. `OS.csproj` pulls in `BootAsm.Generator` as a
`ProjectReference`, so MSBuild builds it automatically, but `CoffStub.Generator`
is wired in through an `<Import>` of its `.targets` file — nothing builds that
one implicitly, and the import then fails to load the task assembly.

`run_build.ps1` now builds it before publishing, so this should not recur. If a
build is driven by hand, do it once:

```powershell
dotnet build .\bootasm\CoffStub.Generator\CoffStub.Generator.csproj -c Release
```

## If the kernel build fails inside ILC

ILC resolves its native tools through `vswhere`, which fails outside a
developer environment. Either run the build from a **"x64 Native Tools Command
Prompt for VS 2022"**, or pass `-p:IlcUseEnvironmentalTools=true` after running
`vcvars64.bat`. The symptom is a link step exiting with code 123.

`invalid symbol redefinition` warnings from ILC are benign noise, not failures.

## Machine-independence

The produced image is self-contained: the OS version it reports (`10.0.26100`)
is a constant in `OS/src/PAL/SharpOSHost/SystemIdentity.cs`, not read from the
build host. What is host-dependent is the *build*: the PowerShell 7 modules
staged into the ESP come from the machine's own installation.

## First-run sanity check

A good run prints the boot probes, then a PowerShell prompt:

```
PS C:\sharpos>
```

Typing works with echo, colours, history, Tab completion, and the navigation
keys. If the prompt appears but input does nothing, or a feature silently does
nothing, the first thing to do is turn the diagnostics back on:
`Probes.HostedAppQuietConsole = false` in `OS/src/Kernel/Diagnostics/Probes.cs`
and rebuild the kernel only — the `[GetProcAddress ...] unknown name=` lines
name the missing API directly.
