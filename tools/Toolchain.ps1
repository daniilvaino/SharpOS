# tools/Toolchain.ps1 — проверка сборочных инструментов SharpOS.
#
# Подключается точкой (. tools/Toolchain.ps1) из скриптов сборки ядра,
# приложений и форка CoreCLR. Ничего не ставит и не качает: находит
# инструменты и сверяет их версии с toolchain.json в корне SharpOS. Не та
# версия или нет инструмента — понятная ошибка до начала сборки.
#
# Где ищет (одинаково на Windows, macOS и Linux):
#   LLVM    SHARPOS_LLVM_BIN, иначе каталог clang-cl из PATH; lld-link,
#           llvm-lib, llvm-rc — оттуда же
#   JWasm   SHARPOS_JWASM, иначе jwasm из PATH, иначе .cache/jwasm в корне SharpOS
#   splat   SHARPOS_XWIN_SPLAT, иначе .xwin-cache/splat в корне SharpOS
#   cmake, ninja, python — из PATH
#   dotnet  global.json в корне SharpOS (он же требует версию SDK)
#
# Работает и в Windows PowerShell 5.1, и в pwsh 7+.

$script:SharpOsRoot = if ($env:SHARPOS_ROOT) { $env:SHARPOS_ROOT } else { Split-Path -Parent $PSScriptRoot }

function Get-SharpOsToolchainSpec {
    Get-Content -LiteralPath (Join-Path $script:SharpOsRoot 'toolchain.json') -Raw | ConvertFrom-Json
}

function Test-SharpOsWindowsHost { $env:OS -eq 'Windows_NT' }

# Хост в терминах RID: win-x64, linux-x64, linux-arm64, osx-arm64, osx-x64.
# $IsWindows есть только в pwsh 6+; $env:OS = Windows_NT на любой Windows.
function Get-SharpOsHostRid {
    if (Test-SharpOsWindowsHost) {
        if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { return 'win-arm64' }
        return 'win-x64'
    }
    $os = (& uname -s).Trim()
    $arch = (& uname -m).Trim()
    $osPart = switch ($os) { 'Darwin' { 'osx' } 'Linux' { 'linux' } default { throw "Хост $os не поддерживается" } }
    $archPart = switch ($arch) { { $_ -in 'x86_64', 'amd64' } { 'x64' } { $_ -in 'arm64', 'aarch64' } { 'arm64' } default { throw "Архитектура $arch не поддерживается" } }
    return "$osPart-$archPart"
}

function Get-SharpOsExeName([string]$Name) {
    if (Test-SharpOsWindowsHost) { return "$Name.exe" }
    return $Name
}

# Первая строка вывода команды (stdout+stderr), $null если не запустилась.
function Get-SharpOsToolOutput([string]$Exe, [string[]]$Arguments) {
    try { return (& $Exe @Arguments 2>&1 | Select-Object -First 1 | Out-String).Trim() }
    catch { return $null }
}

function Find-SharpOsInPath([string]$Name) {
    $c = Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($c) { return $c.Source }
    return $null
}

# Проверяет нужные компоненты и возвращает найденные пути. Все проблемы
# собираются в одну ошибку, чтобы не чинить их по одной.
#   llvm    clang-cl, lld-link, llvm-lib, llvm-rc одной точной версии, из одного каталога
#   lld     только lld-link (ядро и приложения); версия — та же, что у LLVM
#   jwasm   JWasm точной версии
#   winsdk  splat от xwin с нужными версиями SDK и CRT
#   cmake, ninja, python — не ниже минимальной
#   dotnet  SDK из global.json
function Assert-SharpOsToolchain {
    param([Parameter(Mandatory)][string[]]$Components)
    $spec = Get-SharpOsToolchainSpec
    $bad = New-Object System.Collections.Generic.List[string]
    $found = [ordered]@{}

    if ($Components -contains 'llvm' -or $Components -contains 'lld') {
        $want = $spec.llvm.version
        $bin = $env:SHARPOS_LLVM_BIN
        if (-not $bin) {
            $probe = if ($Components -contains 'llvm') { 'clang-cl' } else { 'lld-link' }
            $p = Find-SharpOsInPath $probe
            if ($p) { $bin = Split-Path -Parent $p }
        }
        if (-not $bin -or -not (Test-Path -LiteralPath $bin)) {
            $bad.Add("LLVM $want`: не найден (нет clang-cl/lld-link в PATH и не задан SHARPOS_LLVM_BIN)")
        } else {
            $tools = if ($Components -contains 'llvm') { 'clang-cl', 'lld-link', 'llvm-lib', 'llvm-rc' } else { 'lld-link' }
            foreach ($t in $tools) {
                $exe = Join-Path $bin (Get-SharpOsExeName $t)
                if (-not (Test-Path -LiteralPath $exe)) { $bad.Add("LLVM $want`: в $bin нет $t"); continue }
                # llvm-lib и llvm-rc версию не сообщают; они из того же каталога,
                # что clang-cl, — то есть из той же установки LLVM.
                if ($t -in 'llvm-lib', 'llvm-rc') { continue }
                $v = Get-SharpOsToolOutput $exe @('--version')
                if ($v -notmatch "(^|\D)$([regex]::Escape($want))(\D|$)") {
                    $bad.Add("LLVM: $t в $bin — '$v', нужна $want")
                }
            }
            $found.LlvmBin = $bin
        }
    }

    if ($Components -contains 'jwasm') {
        $want = $spec.jwasm.version
        $exe = $env:SHARPOS_JWASM
        if (-not $exe) { $exe = Find-SharpOsInPath 'jwasm' }
        # Туда его собирает mise bootstrap (задача bootstrap:jwasm).
        if (-not $exe) { $exe = Join-Path $script:SharpOsRoot ('.cache/jwasm/' + (Get-SharpOsExeName 'jwasm')) }
        if (-not $exe -or -not (Test-Path -LiteralPath $exe)) {
            $bad.Add("JWasm $want`: не найден (нет jwasm в PATH и не задан SHARPOS_JWASM)")
        } else {
            $v = Get-SharpOsToolOutput $exe @('-?')
            if ($v -notmatch "JWasm v$([regex]::Escape($want))\b") { $bad.Add("JWasm: $exe — '$v', нужна v$want") }
            $found.Jwasm = $exe
        }
    }

    if ($Components -contains 'winsdk') {
        $sdk = $spec.winsdk.sdkVersion
        $crt = $spec.winsdk.crtVersion
        $splat = $env:SHARPOS_XWIN_SPLAT
        if (-not $splat) { $splat = Join-Path $script:SharpOsRoot '.xwin-cache/splat' }
        if (-not (Test-Path -LiteralPath (Join-Path $splat 'crt/lib/x64/libcmt.lib'))) {
            $bad.Add("splat xwin (SDK $sdk, CRT $crt): не найден в $splat (задайте SHARPOS_XWIN_SPLAT)")
        } else {
            # xwin кладёт в sdk/include каталог с номером версии SDK (ссылку на '.').
            if (-not (Test-Path -LiteralPath (Join-Path $splat "sdk/include/$sdk"))) {
                $have = (Get-ChildItem -LiteralPath (Join-Path $splat 'sdk/include') -Directory -Filter '10.*' -ErrorAction SilentlyContinue | ForEach-Object Name) -join ', '
                $bad.Add("splat xwin: Windows SDK $have, нужен $sdk ($splat)")
            }
            $h = Get-Content -LiteralPath (Join-Path $splat 'crt/include/crtversion.h') -Raw -ErrorAction SilentlyContinue
            $parts = foreach ($n in 'MAJOR', 'MINOR', 'BUILD') {
                if ($h -match "#define _VC_CRT_$($n)_VERSION\s+(\d+)") { $Matches[1] } else { '?' }
            }
            $haveCrt = $parts -join '.'
            if ($haveCrt -ne $crt) { $bad.Add("splat xwin: MSVC CRT $haveCrt, нужен $crt ($splat)") }
            $found.XwinSplat = (Resolve-Path -LiteralPath $splat).Path
        }
    }

    foreach ($t in 'cmake', 'ninja', 'python') {
        if ($Components -notcontains $t) { continue }
        $min = [version]$spec.minimum.$t
        $names = if ($t -eq 'python') { 'python3', 'python' } else { @($t) }
        $exe = $null
        foreach ($n in $names) { $exe = Find-SharpOsInPath $n; if ($exe) { break } }
        if (-not $exe) { $bad.Add("$t $min+: не найден в PATH"); continue }
        $v = Get-SharpOsToolOutput $exe @('--version')
        if ($v -match '(\d+\.\d+(\.\d+)?)' -and [version]$Matches[1] -ge $min) { $found[$t] = $exe }
        else { $bad.Add("$t`: $exe — '$v', нужна не ниже $min") }
    }

    if ($Components -contains 'dotnet') {
        # Ядру и приложениям довольно любого SDK той же major.minor: они
        # NoStdLib, от SDK нужен только Roslyn нужной версии C#, а кодогенерацию
        # задаёт ILCompiler — он закреплён отдельно (SharpOsNativeLink.props).
        # Точную версию SDK требует Arcade форка, и проверяет её его global.json.
        $want = [string]::Join('.', $spec.dotnet.sdkVersion.Split('.')[0..1])
        Push-Location $script:SharpOsRoot
        try { $v = Get-SharpOsToolOutput 'dotnet' @('--version') } finally { Pop-Location }
        if ($v -notlike "$want.*") { $bad.Add(".NET SDK $want.x`: dotnet --version в корне SharpOS даёт '$v'") }
    }

    if ($bad.Count -gt 0) {
        throw ("Сборочные инструменты не совпадают с toolchain.json:`n  " + ($bad -join "`n  ") +
               "`nПоставить нужные версии: mise bootstrap (см. README «Как запустить»).")
    }
    return [pscustomobject]$found
}

# Проверяет компоненты и выставляет переменные окружения, по которым их
# находят MSBuild (SharpOsNativeLink.props) и cmake (sharpos-toolchain.cmake).
# Возвращает прежние значения для Exit-SharpOsToolchain: скрипт, запущенный в
# интерактивной сессии, не оставляет её с изменённым окружением.
function Enter-SharpOsToolchain {
    param([Parameter(Mandatory)][string[]]$Components)
    $t = Assert-SharpOsToolchain -Components $Components
    $saved = @{}
    foreach ($n in 'SHARPOS_LLVM_BIN', 'SHARPOS_LLVM_VERSION', 'SHARPOS_JWASM', 'SHARPOS_XWIN_SPLAT') {
        $saved[$n] = [Environment]::GetEnvironmentVariable($n)
    }
    if ($t.LlvmBin)   { $env:SHARPOS_LLVM_BIN = $t.LlvmBin; $env:SHARPOS_LLVM_VERSION = (Get-SharpOsToolchainSpec).llvm.version }
    if ($t.Jwasm)     { $env:SHARPOS_JWASM = $t.Jwasm }
    if ($t.XwinSplat) { $env:SHARPOS_XWIN_SPLAT = $t.XwinSplat }
    return $saved
}

function Exit-SharpOsToolchain($Saved) {
    if (-not $Saved) { return }
    foreach ($n in @($Saved.Keys)) { [Environment]::SetEnvironmentVariable($n, $Saved[$n]) }
}
