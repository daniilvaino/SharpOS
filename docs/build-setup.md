# Building SharpOS on a fresh machine

The build is the same on Windows, macOS and Linux: the same scripts, the same
tools at the same versions. The repo is already cloned; this page covers
QEMU, firmware, media and build order. Times are for a warm cache on a
mid-range desktop.

## What has to be installed

The tools are installed by `mise bootstrap` from [`mise.toml`](../mise.toml) (README, «Как запустить»); the exact versions are in [`toolchain.json`](../toolchain.json), which is
the single place the versions are written down. The build scripts install
nothing: before building they locate the tools and check their versions
against `toolchain.json` (`tools/Toolchain.ps1`), failing early with a message
that names what is missing or mismatched.

In short: .NET SDK 10.0.x (the fork pins the exact one it needs); LLVM 22.1.8 (`clang-cl`, `lld-link`, `llvm-lib`,
`llvm-rc`); for the CoreCLR fork also JWasm 2.21, the MSVC CRT + Windows SDK
splat made by xwin, cmake, ninja and python3. Visual Studio is not used on any
host: the kernel and apps are linked by `lld-link`, the fork is compiled by
`clang-cl` against the xwin splat.

On NixOS (and any Linux with nix), [`flake.nix`](../flake.nix) replaces `mise
bootstrap`: `nix develop` is the shell for the kernel, the apps and running the
image, `nix develop .#fork` the one for the CoreCLR fork. The fork needs an FHS
environment because its Arcade downloads its own .NET SDK and builds crossgen2
and friends as ordinary binaries that expect `/lib64/ld-linux-x86-64.so.2`.
`nix run .#fork -- -c "…"` runs one command in that environment, for scripts.

`ilc` arrives from NuGet as a plain ELF binary and does not start on nix until
patchelf has been over it. Nothing to do by hand: the `SharpOsPatchNupkgs`
target in [`SharpOsNativeLink.props`](../SharpOsNativeLink.props) runs
`patch-nupkgs` between the restore and `IlcCompile`, the way nixpkgs does it for
its own imperative .NET build (`patch-restored-packages.proj`). The target is
driven by `SHARPOS_PATCH_NUPKGS`, which only the nix shell sets.

One thing does need care: make the xwin splat on the machine that will use it.
The one xwin writes on Linux carries the lowercase symlinks (`kernel32.lib` →
`kernel32.Lib`) that a case-sensitive filesystem needs; a splat copied from
Windows does not.

Optional: **Windows Debugging Tools** for `tools/symbolize.ps1` (needs
`dbghelp.dll` from the Windows Kits Debuggers directory; only for diagnosing
crashes).

Localized Windows is supported: the build scripts force UTF-8 so tool
diagnostics stay readable in `last_build.log`.

## QEMU and its firmware

`mise bootstrap` installs QEMU (winget on Windows, brew on macOS, apt on
Linux), so the two options below matter only when installing it by hand.

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
2. `share/qemu/edk2-x86_64-code.fd` next to the `qemu-system-x86_64` binary itself
   (Homebrew, NixOS, any prefix install);
3. the fixed layouts: `C:\msys64\mingw64\share\qemu`, `C:\Program Files\qemu\share`,
   `/usr/share/qemu`, `/usr/share/OVMF`. On Debian and Ubuntu the firmware is not in
   the qemu packages — it comes from the `ovmf` package, which `mise bootstrap` installs.

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
QEMU alone has a self-contained installer, but mtools/xorriso/sfdisk do not. `mise bootstrap` installs MSYS2 and mtools on
Windows for exactly this reason — the ESP image is built with `mformat`/`mcopy`.

### `run_vbox.ps1` — VirtualBox

Runs the produced media under VirtualBox instead of QEMU. Needs VirtualBox
installed for `VBoxManage`; the path can be overridden with `-VBoxManagePath`.
Useful as a second opinion when a failure smells like a QEMU quirk rather than
a real bug.

### Application build scripts

`build_launcher.ps1`, `build_fetch.ps1`, `build_aottests.ps1`, `build_benchaot.ps1`,
`build_doom.ps1`, `build_shell.ps1`, `build_tricnes.ps1`, `build_fami.ps1` build the
freestanding PE apps under `apps_native/`. They need only the .NET SDK and `lld-link`
— no MSVC, no Windows SDK libraries, no WSL — and `run_build.ps1` stages their output
into the image if it is present.

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

- `artifacts/obj/coreclr/windows.x64.Release/dlls/mscoree/coreclr/coreclr_static.lib` — linked into `BOOTX64.EFI`;
- `artifacts/bin/coreclr/windows.x64.Release/System.Private.CoreLib.dll` — the CoreLib
  dropped into the image and loaded at runtime.

Both come from the same build, so their configuration always matches: `MethodTable`
layout differs between Debug and Release, and a mismatch corrupts field offsets at runtime.

Useful switches: `-NinjaClean` when ninja aborts with `FindFirstFileExA(Note: including file: ...)`
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
`tools/probe_report.ps1` reads all three. `tools/perf_report.ps1` collects the
`[perf]` lines of a run (the kernel's counters plus `\SHARPOS\Bench.dll` started
from the launcher) and compares them with the previous run.
`tools/prof_report.ps1` sums the sampling profiler's `[prof]` lines of an
interval (`run.Bench` by default) per function, named from the kernel's PDB.

Flags worth knowing: `-NoRun` builds without launching, `-SkipCoreClr` builds a
bare kernel with no hosted runtime, `-Stop` kills a running instance through QMP
on port 4444, `-ForkConfig Debug|Release` selects which fork build to link
(Release by default; Debug needs the Debug fork and its CoreLib built first).

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

`invalid symbol redefinition` warnings from ILC are benign noise, not failures.

## Machine-independence

The produced image is self-contained: the OS version it reports (`10.0.26100`)
is a constant in `OS/src/PAL/SharpOSHost/SystemIdentity.cs`, not read from the
build host. The PowerShell modules staged into the ESP come from
`payloads/pwsh/PowerShell-<version>-win-x64`, the version pinned in
`toolchain.json`, never from the build machine.

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
