param(
    # WSL distro name; empty = default distro
    [string]$WslDistro = "",
    # Where to clone/keep EDK2 inside WSL (WSL path, ~ is ok)
    [string]$Edk2Dir = "~/edk2-ovmf-strict",
    # Where to write the resulting .fd files on the Windows side.
    # Default: this script's own directory (ovmf/).
    [string]$OutputDir = "",
    # Skip apt-get install (if deps already installed)
    [switch]$SkipDeps,
    # Skip git clone/pull (if already cloned)
    [switch]$SkipClone,
    # DEBUG build instead of RELEASE (bigger binary, slower)
    [switch]$DebugBuild
)

$ErrorActionPreference = "Stop"

# ── defaults ────────────────────────────────────────────────────────────────
if (-not $OutputDir) {
    $OutputDir = Split-Path -Parent $PSCommandPath   # ovmf/ folder itself
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$OutputDir = (Resolve-Path -LiteralPath $OutputDir).Path

# ── helpers ──────────────────────────────────────────────────────────────────
function ConvertTo-WslPath([string]$winPath) {
    $drive = $winPath.Substring(0, 1).ToLower()
    $rest  = $winPath.Substring(2).Replace('\', '/')
    return "/mnt/$drive$rest"
}

$wslOutputDir = ConvertTo-WslPath $OutputDir
$wslArgs      = if ($WslDistro) { @("-d", $WslDistro) } else { @() }
$buildType    = if ($DebugBuild) { "DEBUG" } else { "RELEASE" }
$skipDepsVal  = if ($SkipDeps)  { "1" } else { "0" }
$skipCloneVal = if ($SkipClone) { "1" } else { "0" }

# ── bash script (single-quoted PS heredoc → no PS interpolation) ─────────────
# Placeholders are replaced below via .Replace()
$bash = @'
#!/usr/bin/env bash
set -euo pipefail

EDK2_DIR="@@EDK2_DIR@@"
OUTPUT_DIR="@@OUTPUT_DIR@@"
BUILD_TYPE="@@BUILD_TYPE@@"
SKIP_DEPS=@@SKIP_DEPS@@
SKIP_CLONE=@@SKIP_CLONE@@
TOOLCHAIN="GCC5"
JOBS=$(nproc)

# Expand ~ in EDK2_DIR
EDK2_DIR="${EDK2_DIR/#\~/$HOME}"

log() { echo "[ovmf-build] $*"; }

# ── 1. Dependencies ───────────────────────────────────────────────────────────
if [ "$SKIP_DEPS" -eq 0 ]; then
    log "Installing build dependencies..."
    sudo apt-get update
    sudo apt-get install -y \
        build-essential uuid-dev nasm acpica-tools git \
        python3 python3-setuptools gcc g++ m4
    # python3-distutils removed in Ubuntu 23.04+; ignore failure
    sudo apt-get install -y python3-distutils 2>/dev/null || true
    log "Dependencies installed."
fi

# ── 2. Clone / update EDK2 ────────────────────────────────────────────────────
if [ "$SKIP_CLONE" -eq 0 ]; then
    if [ -d "$EDK2_DIR/.git" ]; then
        log "EDK2 dir exists — pulling latest..."
        cd "$EDK2_DIR"
        git fetch --depth=1 --progress
        git reset --hard origin/master
        git submodule update --init --depth=1 --recursive --progress
    else
        log "Cloning EDK2 (first run: 10-15 min, ~2 GB)..."
        git clone \
            --depth=1 \
            --shallow-submodules \
            --recurse-submodules \
            --progress \
            https://github.com/tianocore/edk2.git \
            "$EDK2_DIR"
    fi
else
    log "Skipping clone (-SkipClone)."
fi

cd "$EDK2_DIR"
log "EDK2 dir: $(pwd)"

# ── 3. Patch OvmfPkgX64.dsc ──────────────────────────────────────────────────
# PcdDxeNxMemoryProtectionPolicy = 0x7FD5
#   Bit mask of EFI_MEMORY_TYPE values that get NX applied by the DXE core.
#   0x7FD5 covers all non-code types including EfiConventionalMemory (bit 7).
#   This matches INSYDE / real-hardware UEFI defaults and makes QEMU behave
#   identically to real hardware regarding executable memory enforcement.
DSC="OvmfPkg/OvmfPkgX64.dsc"
log "Patching NX policy in $DSC ..."

python3 - "$DSC" << 'PYEOF'
import sys, re, glob, os

dsc_path = sys.argv[1]
pcd      = 'gEfiMdeModulePkgTokenSpaceGuid.PcdDxeNxMemoryProtectionPolicy'
new_val  = '0x7FD5'

with open(dsc_path, 'r') as f:
    content = f.read()

# 1. Replace any existing declaration (any value, any type section)
patched, n = re.subn(
    r'(' + re.escape(pcd) + r'\s*\|)\s*\S+',
    lambda m: m.group(1) + new_val,
    content
)
if n:
    print(f'Replaced {n} existing occurrence(s) of {pcd} -> {new_val}')

if n == 0:
    # 2. Find what section type the DEC file declares this PCD under
    #    so we insert into the matching [Pcds*] section in the DSC.
    dec_section = None
    dec_pattern = re.compile(r'\[Pcds(\w+)[^\]]*\](.*?)(?=\[|\Z)', re.DOTALL | re.IGNORECASE)
    workspace = os.path.dirname(os.path.abspath(dsc_path))
    for dec_file in glob.glob(os.path.join(workspace, '**', '*.dec'), recursive=True):
        try:
            dec_text = open(dec_file).read()
        except Exception:
            continue
        for m in dec_pattern.finditer(dec_text):
            if pcd in m.group(2):
                dec_section = m.group(1)
                print(f'DEC declares as Pcds{dec_section} (from {os.path.basename(dec_file)})')
                break
        if dec_section:
            break

    # Try sections in priority order: DEC type first, then common fallbacks
    candidates = []
    if dec_section:
        candidates.append(f'Pcds{dec_section}')
    candidates += ['PcdsPatchableInModule', 'PcdsFixedAtBuild', 'PcdsDynamicDefault']

    for section in candidates:
        pat = r'(\[' + re.escape(section) + r'[^\]]*\][ \t]*\n)'
        patched, n = re.subn(pat, r'\1  ' + pcd + '|' + new_val + '\n', content, count=1, flags=re.IGNORECASE)
        if n:
            print(f'Inserted into [{section}] section')
            break

if n == 0:
    # 3. Fallback: append new section
    patched = content + f'\n[PcdsFixedAtBuild]\n  {pcd}|{new_val}\n'
    print('WARNING: no suitable section found; appended [PcdsFixedAtBuild] at end of DSC.')

with open(dsc_path, 'w') as f:
    f.write(patched)

# Verify
line = next((l.strip() for l in patched.splitlines() if pcd in l), None)
if not line or new_val not in line:
    print(f'ERROR: patch verification failed. Found: {line!r}', file=sys.stderr)
    sys.exit(1)
print(f'OK: {line}')
PYEOF
PATCH_STATUS=$?
if [ $PATCH_STATUS -ne 0 ]; then
    echo "ERROR: DSC patch script failed." >&2
    exit 1
fi

# ── 4. BaseTools ─────────────────────────────────────────────────────────────
log "Building BaseTools (j=$JOBS)..."
make -C BaseTools -j"$JOBS"
log "BaseTools built."

# ── 5. EDK2 environment ───────────────────────────────────────────────────────
log "Setting up EDK2 build environment..."
set +eu
# shellcheck source=/dev/null
. edksetup.sh --reconfig
EDKSETUP_STATUS=$?
set -eu
if [ $EDKSETUP_STATUS -ne 0 ]; then
    log "WARNING: edksetup.sh exited $EDKSETUP_STATUS (usually harmless on first run)"
fi

# Auto-detect available GCC toolchain name from tools_def.txt.
# Format in tools_def.txt: *_GCC5_X64_CC_PATH = ...
TOOLCHAIN=""
for TC in GCC5 GCC; do
    if grep -q "_${TC}_" Conf/tools_def.txt 2>/dev/null; then
        TOOLCHAIN="$TC"
        break
    fi
done
if [ -z "$TOOLCHAIN" ]; then
    log "Distinct toolchain tags in tools_def.txt:"
    grep -oE '_[A-Z][A-Z0-9]+_X64_CC_PATH' Conf/tools_def.txt \
        | sed 's/^_//;s/_X64_CC_PATH$//' | sort -u || true
    echo "ERROR: no GCC5/GCC toolchain found in Conf/tools_def.txt" >&2
    exit 1
fi
log "Using toolchain: $TOOLCHAIN"

# ── 6. Build OVMF ─────────────────────────────────────────────────────────────
log "Building OVMF ($BUILD_TYPE, X64, $TOOLCHAIN, j=$JOBS)..."
build \
    -a X64 \
    -t "$TOOLCHAIN" \
    -p OvmfPkg/OvmfPkgX64.dsc \
    -b "$BUILD_TYPE" \
    -n "$JOBS"
log "Build complete."

# ── 7. Copy output ────────────────────────────────────────────────────────────
BUILD_FV="Build/OvmfX64/${BUILD_TYPE}_${TOOLCHAIN}/FV"
CODE_FD="$BUILD_FV/OVMF_CODE.fd"
VARS_FD="$BUILD_FV/OVMF_VARS.fd"

if [ ! -f "$CODE_FD" ]; then
    echo "ERROR: expected $CODE_FD — build output missing." >&2
    exit 1
fi

mkdir -p "$OUTPUT_DIR"
cp "$CODE_FD" "$OUTPUT_DIR/OVMF_CODE.strict-nx.fd"
if [ -f "$VARS_FD" ]; then
    cp "$VARS_FD" "$OUTPUT_DIR/OVMF_VARS.strict-nx.fd"
fi

log "─────────────────────────────────────────────"
log "Output: $OUTPUT_DIR"
ls -lh "$OUTPUT_DIR"/*.fd 2>/dev/null || true
log "─────────────────────────────────────────────"
'@

$bash = $bash.Replace("@@EDK2_DIR@@",   $Edk2Dir)
$bash = $bash.Replace("@@OUTPUT_DIR@@", $wslOutputDir)
$bash = $bash.Replace("@@BUILD_TYPE@@", $buildType)
$bash = $bash.Replace("@@SKIP_DEPS@@",  $skipDepsVal)
$bash = $bash.Replace("@@SKIP_CLONE@@", $skipCloneVal)

# ── write script to Windows temp, run via WSL ────────────────────────────────
$tmpWin = Join-Path $env:TEMP "build_ovmf_strict_$([System.IO.Path]::GetRandomFileName()).sh"
[System.IO.File]::WriteAllText($tmpWin, $bash, [System.Text.UTF8Encoding]::new($false))
$tmpWsl = ConvertTo-WslPath $tmpWin

try {
    Write-Host ""
    Write-Host "OVMF strict-NX build"
    Write-Host "  EDK2 dir (WSL) : $Edk2Dir"
    Write-Host "  Output         : $OutputDir"
    Write-Host "  Build type     : $buildType"
    Write-Host "  Skip deps      : $SkipDeps"
    Write-Host "  Skip clone     : $SkipClone"
    Write-Host ""
    Write-Host "Subsequent runs: .\ovmf\build.ps1 -SkipClone -SkipDeps"
    Write-Host ""

    wsl @wslArgs -- bash "$tmpWsl"
    if ($LASTEXITCODE -ne 0) {
        throw "WSL build script exited with code $LASTEXITCODE"
    }
}
finally {
    Remove-Item -LiteralPath $tmpWin -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "Done. Firmware written to $OutputDir"
Write-Host "run_build.ps1 uses it automatically."
