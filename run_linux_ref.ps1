# run_linux_ref.ps1
# ----------------------------------------------------------------------------
# Run apps_managed/Bench on stock Linux inside the same QEMU as SharpOS.
#
# Why: SharpOS benchmarks run under QEMU TCG, which is itself ~20x slower
# than the host on plain code. Comparing SharpOS against the host mixes that
# emulation cost with SharpOS's own. The same Bench.dll on a normal Linux, on
# the same emulated CPU (TCG, -cpu qemu64,+nx, one CPU, 2 GiB), leaves only
# the difference that is SharpOS's.
#
# What it does, unattended:
#   1. Downloads (once, cached under OS\.qemu\linux-ref) a Debian cloud image
#      and the .NET 10 runtime for linux-x64.
#   2. Builds two small FAT disks with mtools: BENCH (runtime archive,
#      Bench.dll, run.sh) and CIDATA (cloud-init NoCloud seed).
#   3. Boots Debian with -snapshot (every run is a clean first boot; the image
#      is never modified). cloud-init mounts BENCH, unpacks the runtime, runs
#      Bench three times with its output on the serial port, powers off.
#   4. Writes the median of the warm runs to
#      tools\bench-qemu-linux-reference.log, which tools\perf_report.ps1 shows
#      as a "linux" column next to the host one.
#
# Usage:
#   .\run_linux_ref.ps1                 # ~5-15 min under TCG, mostly boot
#   .\run_linux_ref.ps1 -Cpu max        # same CPU model as a SharpOS -Cpu max run
#   .\run_linux_ref.ps1 -PowerShell     # PowerShell startup instead of Bench
#
# -PowerShell: the same PowerShell release SharpOS stages (payloads\pwsh),
# its linux-x64 build, $Runs interactive sessions on a pty doing what a
# person does by hand on SharpOS — wait for the prompt, `ls`, `echo $PSV`
# completed with Tab, Enter — each step timed to its result on the terminal
# (compare with SharpOS's run.PowerShellBootstrap.first_input_ms and key.*).
# Then `-Command "Import-Module PSReadLine; exit"` and `-Command exit` for
# the line editor's and the engine's share. Results go to
# tools\pwsh-qemu-linux-reference.log.
#
# Needs: QEMU, mtools (mformat/mcopy, as for run_build.ps1), internet for the
# first run, and apps_managed\Bench built (run_build.ps1 builds it).
# ----------------------------------------------------------------------------
# ASCII-only, like the other tools scripts.

[CmdletBinding()]
param(
    [string]$Cpu = "qemu64,+nx",
    [int]$MemoryMb = 2048,
    [int]$Runs = 3,
    [int]$TimeoutMin = 40,
    [string]$ImageUrl = "https://cloud.debian.org/images/cloud/trixie/latest/debian-13-generic-amd64.qcow2",
    [string]$DotnetUrl = "https://aka.ms/dotnet/10.0/dotnet-runtime-linux-x64.tar.gz",
    [string]$QemuExe = "",
    # Download the image and runtime again even if cached.
    [switch]$Refresh,
    # Measure PowerShell startup instead of running Bench.
    [switch]$PowerShell,
    [string]$PowerShellVersion = "7.6.5"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"   # Invoke-WebRequest is ~10x slower with the bar

$repoRoot = Split-Path -Parent $PSCommandPath
$cache = Join-Path $repoRoot "OS\.qemu\linux-ref"
$benchBin = Join-Path $repoRoot "apps_managed\Bench\bin\Release\net10.0"
$serialLog = Join-Path $repoRoot "last_linux_ref.log"      # ttyS1: the benchmark's output only
$bootLog = Join-Path $cache "boot.log"                        # ttyS0: kernel console, getty
$reference = Join-Path $repoRoot "tools\bench-qemu-linux-reference.log"

. (Join-Path $repoRoot "tools\New-EspImage.ps1")   # for Resolve-MtoolsPath

if (-not $QemuExe) {
    $cmd = Get-Command "qemu-system-x86_64.exe" -ErrorAction SilentlyContinue
    $QemuExe = if ($cmd) { $cmd.Source } else { "C:\msys64\mingw64\bin\qemu-system-x86_64.exe" }
}
if (-not (Test-Path -LiteralPath $QemuExe)) { throw "QEMU not found: $QemuExe (pass -QemuExe)" }

$mformat = Resolve-MtoolsPath "mformat.exe"
$mcopy = Resolve-MtoolsPath "mcopy.exe"
if (-not $mformat -or -not $mcopy) { throw "mtools (mformat/mcopy) not found - install mingw-w64-x86_64-mtools" }

if (-not (Test-Path -LiteralPath (Join-Path $benchBin "Bench.dll"))) {
    throw "Bench.dll not found in $benchBin - run run_build.ps1 once (it builds apps_managed\Bench)"
}

New-Item -ItemType Directory -Force -Path $cache | Out-Null

# --- downloads --------------------------------------------------------------

function Get-Cached([string]$url, [string]$name) {
    $path = Join-Path $cache $name
    if ($Refresh -or -not (Test-Path -LiteralPath $path)) {
        Write-Host "Downloading $url"
        Invoke-WebRequest -Uri $url -OutFile "$path.part"
        Move-Item -LiteralPath "$path.part" -Destination $path -Force
    }
    return $path
}

$image = Get-Cached $ImageUrl "debian.qcow2"
$dotnet = Get-Cached $DotnetUrl "dotnet-runtime-linux-x64.tar.gz"
if ($PowerShell) {
    $pwshName = "powershell-$PowerShellVersion-linux-x64.tar.gz"
    $pwshTar = Get-Cached "https://github.com/PowerShell/PowerShell/releases/download/v$PowerShellVersion/$pwshName" $pwshName
    $reference = Join-Path $repoRoot "tools\pwsh-qemu-linux-reference.log"
}

# --- disks -------------------------------------------------------------------

# A superfloppy FAT image (no partition table) with a volume label; Linux
# mounts it by label, cloud-init finds its seed by label.
function New-LabeledFat([string]$sourceDir, [string]$rawPath, [int]$sizeMb, [string]$label) {
    if (Test-Path -LiteralPath $rawPath) { Remove-Item -LiteralPath $rawPath -Force }
    $stream = [System.IO.File]::Open($rawPath, [System.IO.FileMode]::CreateNew,
                                     [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    try { $stream.SetLength([int64]$sizeMb * 1MB) } finally { $stream.Dispose() }
    & $mformat -i $rawPath -v $label "::"
    if ($LASTEXITCODE -ne 0) { throw "mformat failed ($LASTEXITCODE)" }
    foreach ($item in Get-ChildItem -LiteralPath $sourceDir -Force) {
        & $mcopy -i $rawPath -s $item.FullName "::/"
        if ($LASTEXITCODE -ne 0) { throw "mcopy failed for $($item.FullName) ($LASTEXITCODE)" }
    }
}

function Write-Lf([string]$path, [string[]]$lines) {
    [System.IO.File]::WriteAllText($path, (($lines -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))
}

$payloadDir = Join-Path $cache "payload"
if (Test-Path -LiteralPath $payloadDir) { Remove-Item -LiteralPath $payloadDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $payloadDir "bench") | Out-Null
Copy-Item -LiteralPath $dotnet -Destination (Join-Path $payloadDir "dotnet-runtime.tar.gz")
Copy-Item -Path (Join-Path $benchBin "Bench.*") -Destination (Join-Path $payloadDir "bench")

if ($PowerShell) {
    Copy-Item -LiteralPath $pwshTar -Destination (Join-Path $payloadDir "powershell.tar.gz")

    # The environment SharpOS gives PowerShell (EnvironmentPolicy.cs): the
    # invariant culture, no telemetry, no update check. -NoLogo on both.
    #
    # Time to prompt: pwsh on a pty (script) with its input on a fifo, until
    # "PS ...> " is in the recorded output. Then killed rather than sent
    # "exit": on a pty nobody answers the line editor's cursor-position
    # query, and an "exit" typed into that exchange is not a command.
    Write-Lf (Join-Path $payloadDir "run.sh") @(
        'echo "=== linux-ref begin ==="',
        'uname -r',
        'grep -m1 "model name" /proc/cpuinfo',
        'nproc',
        'mkdir -p /opt/pwsh && tar -xzf /mnt/payload/powershell.tar.gz -C /opt/pwsh && chmod +x /opt/pwsh/pwsh',
        'export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 POWERSHELL_TELEMETRY_OPTOUT=1 POWERSHELL_UPDATECHECK=Off DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 TERM=xterm',
        'sync; sleep 5',
        'ms() { echo $(( ($(date +%s%N) - $1) / 1000000 )); }',
        # A terminal answers the line editor's cursor-position queries
        # (ESC[6n); unanswered, each one waits out a timeout — 11 s of a 16 s
        # "startup" the first time this was measured. Answered from the
        # recorded output, as a real terminal would.
        'answer() { local q; q=$(grep -ao $''\e\[6n'' /tmp/pout | wc -l); while [ $answered -lt $q ]; do printf ''\033[1;1R'' >&3; answered=$((answered+1)); done; }',
        'prompts() { grep -ao "PS [^>]*> " /tmp/pout | wc -l; }',
        'wait_prompts() { local n=0; until [ $(prompts) -ge $1 ]; do answer; sleep 0.05; n=$((n+1)); [ $n -gt 12000 ] && break; done; }',
        'wait_text() { local n=0; until grep -aqF -- "$1" /tmp/pout; do answer; sleep 0.05; n=$((n+1)); [ $n -gt 12000 ] && break; done; }',
        # The session a person runs by hand on SharpOS: the prompt, `ls`,
        # `echo $PSV` completed with Tab, Enter. Each step is timed from the
        # keys being sent to what it produces on the terminal; compare with
        # the kernel's run.PowerShellBootstrap.first_input_ms and key.* lines.
        ('for i in $(seq 1 ' + $Runs + '); do'),
        '  rm -f /tmp/pin /tmp/pout; mkfifo /tmp/pin; : > /tmp/pout',
        '  answered=0',
        '  t0=$(date +%s%N)',
        '  script -qfec "/opt/pwsh/pwsh -NoLogo" /tmp/pout < /tmp/pin > /dev/null 2>&1 &',
        '  pid=$!',
        '  exec 3>/tmp/pin',
        '  wait_prompts 1',
        '  echo "[perf] pwsh.prompt_ms=$(ms $t0)"',
        '  sleep 1',
        '  t0=$(date +%s%N); printf ''ls\r'' >&3; wait_prompts 2',
        '  echo "[perf] pwsh.ls_ms=$(ms $t0)"',
        '  sleep 1',
        '  printf ''echo $PSV'' >&3; wait_text ''$PSV''; sleep 1',
        '  t0=$(date +%s%N); printf ''\t'' >&3; wait_text ''PSVersionTable''',
        '  echo "[perf] pwsh.tab_ms=$(ms $t0)"',
        '  sleep 1',
        # Not the next prompt: counting prompts here gave 72 ms, less than the
        # table could have been printed in — the completion had already
        # redrawn one. PSEdition is only in the command's output.
        '  echo "[perf] pwsh.prompts_before_echo=$(prompts)"',
        '  t0=$(date +%s%N); printf ''\r'' >&3; wait_text ''PSEdition''',
        '  echo "[perf] pwsh.echo_ms=$(ms $t0)"',
        '  echo "[perf] pwsh.cursor_queries=$answered"',
        '  pkill -KILL -f /opt/pwsh/pwsh; kill -KILL $pid 2>/dev/null; exec 3>&-; wait $pid 2>/dev/null',
        '  sleep 2',
        'done',
        ('for i in $(seq 1 ' + $Runs + '); do'),
        '  t0=$(date +%s%N)',
        '  /opt/pwsh/pwsh -NoLogo -NoProfile -NonInteractive -Command "Import-Module PSReadLine; exit"',
        '  echo "[perf] pwsh.psreadline_exit_ms=$(ms $t0)"',
        'done',
        ('for i in $(seq 1 ' + $Runs + '); do'),
        '  t0=$(date +%s%N)',
        '  /opt/pwsh/pwsh -NoLogo -NoProfile -NonInteractive -Command exit',
        '  echo "[perf] pwsh.command_exit_ms=$(ms $t0)"',
        'done',
        '/opt/pwsh/pwsh -NoLogo -NoProfile -Command ''$PSVersionTable.PSVersion.ToString(); [System.Runtime.InteropServices.RuntimeInformation]::FrameworkDescription''',
        'echo "=== linux-ref end ==="'
    )
}

# Invariant globalization: SharpOS runs the invariant culture too, and a cloud
# image may not carry libicu. The libstdc++ check only matters if the image
# ever drops it; installing it needs the network, which -nic user provides.
if (-not $PowerShell) {
Write-Lf (Join-Path $payloadDir "run.sh") @(
    'echo "=== linux-ref begin ==="',
    'uname -r',
    'grep -m1 "model name" /proc/cpuinfo',
    'nproc',
    'mkdir -p /opt/dotnet && tar -xzf /mnt/payload/dotnet-runtime.tar.gz -C /opt/dotnet',
    'export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1',
    'export DOTNET_CLI_TELEMETRY_OPTOUT=1',
    'if ! ldconfig -p | grep -q "libstdc++.so.6"; then apt-get update && apt-get install -y libstdc++6; fi',
    '/opt/dotnet/dotnet --list-runtimes',
    'sleep 5',
    ('for i in $(seq 1 ' + $Runs + '); do echo "=== linux-ref run $i ==="; /opt/dotnet/dotnet /mnt/payload/bench/Bench.dll; done'),
    'echo "=== linux-ref end ==="'
)
}

$seedDir = Join-Path $cache "seed"
if (Test-Path -LiteralPath $seedDir) { Remove-Item -LiteralPath $seedDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $seedDir | Out-Null
Write-Lf (Join-Path $seedDir "meta-data") @(
    ("instance-id: sharpos-bench-" + (Get-Date -Format 'yyyyMMddHHmmss')),
    'local-hostname: bench'
)
Write-Lf (Join-Path $seedDir "user-data") @(
    '#cloud-config',
    'runcmd:',
    '  - mkdir -p /mnt/payload',
    '  - mount -o ro LABEL=BENCH /mnt/payload',
    # ttyS1, not the console: the serial getty on ttyS0 calls vhangup() when
    # it starts, which cut the benchmark's shell off mid-run (SIGHUP) the
    # first time this was tried.
    '  - bash /mnt/payload/run.sh > /dev/ttyS1 2>&1',
    '  - poweroff'
)

$payloadImg = Join-Path $cache "payload.img"
$seedImg = Join-Path $cache "seed.img"
New-LabeledFat $payloadDir $payloadImg $(if ($PowerShell) { 256 } else { 128 }) "BENCH"
New-LabeledFat $seedDir $seedImg 4 "CIDATA"

# --- run ---------------------------------------------------------------------

$qemuArgs = @(
    "-machine", "q35,accel=tcg",
    "-cpu", $Cpu,
    "-smp", "1",
    "-m", "$MemoryMb",
    "-display", "none",
    "-serial", "file:$bootLog",
    "-serial", "file:$serialLog",
    "-nic", "user,model=virtio-net-pci",
    "-snapshot",
    "-drive", "file=$image,if=virtio,format=qcow2",
    "-drive", "file=$payloadImg,if=virtio,format=raw",
    "-drive", "file=$seedImg,if=virtio,format=raw"
)
Write-Output "QEMU: $QemuExe $($qemuArgs -join ' ')"
Write-Host "Booting Debian under TCG; benchmark output -> $serialLog, console -> $bootLog"
Write-Host "(Get-Content -Wait on either to follow.)"

$proc = Start-Process -FilePath $QemuExe -ArgumentList $qemuArgs -PassThru -NoNewWindow
if (-not $proc.WaitForExit($TimeoutMin * 60 * 1000)) {
    $proc.Kill()
    throw "Timed out after $TimeoutMin min - see $bootLog and $serialLog"
}

# --- result ------------------------------------------------------------------

$text = Get-Content -Raw -LiteralPath $serialLog
if ($text -notmatch '=== linux-ref end ===') {
    throw "The VM finished without completing the benchmark - see $serialLog and $bootLog"
}

# Same rule as perf_report: several runs -> median of the warm ones.
$groups = [ordered]@{}
foreach ($m in [regex]::Matches($text, '\[perf\] ((?:bench|pwsh)\.[A-Za-z0-9_.]+)=(-?\d+)')) {
    $name = $m.Groups[1].Value
    if (-not $groups.Contains($name)) { $groups[$name] = New-Object System.Collections.Generic.List[long] }
    $groups[$name].Add([long]$m.Groups[2].Value)
}
function Get-Warm([long[]]$list) {
    $l = @($list)
    if ($l.Count -ge 2) { $l = $l[1..($l.Count - 1)] }
    $s = @($l | Sort-Object)
    $mid = [int][math]::Floor($s.Count / 2)
    if ($s.Count % 2 -eq 1) { return $s[$mid] }
    return [long][math]::Round(($s[$mid - 1] + $s[$mid]) / 2.0)
}

$runtime = [regex]::Match($text, '(?:Microsoft\.NETCore\.App |\.NET )(\d+\.\S+)')
$kernel = [regex]::Match($text, '(?m)^(\d+\.\d+\.\S+)\s*$')
$what = if ($PowerShell) { "PowerShell $PowerShellVersion (linux-x64) startup" } else { "Bench.dll" }
$lines = @(
    ("# $what on Debian under QEMU TCG (-cpu $Cpu, 1 CPU, $MemoryMb MiB), " + (Get-Date -Format 'yyyy-MM-dd')),
    ("# runtime " + $(if ($runtime.Success) { $runtime.Groups[1].Value } else { '?' }) +
     ", kernel " + $(if ($kernel.Success) { $kernel.Groups[1].Value } else { '?' }) +
     "; median of warm runs out of $Runs. Written by run_linux_ref.ps1.")
)
foreach ($name in $groups.Keys) {
    # The first start of a program reads it from disk; SharpOS's runtime is
    # already up when PowerShell starts, so both numbers are worth having.
    if ($PowerShell) { $lines += "[perf] $name.first=$($groups[$name][0])" }
    $lines += "[perf] $name=$(Get-Warm $groups[$name].ToArray())"
}
[System.IO.File]::WriteAllText($reference, (($lines -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))

Write-Host ""
$lines | ForEach-Object { Write-Host $_ }
Write-Host ""
Write-Host "reference -- $reference" -ForegroundColor DarkGray
