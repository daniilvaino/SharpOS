param(
    [string]$Distro = "Ubuntu",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [int]$QemuTimeoutSec = 180
)

$ErrorActionPreference = "Stop"

function Remove-AnsiEscape {
    param([string]$Text)

    if ([string]::IsNullOrEmpty($Text)) {
        return ""
    }

    return [regex]::Replace($Text, "\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])", "")
}

function Get-HelloCsSegment {
    param([string]$Text)

    $marker = "[info] app run start: \EFI\BOOT\HELLOCS.ELF"
    $start = $Text.IndexOf($marker, [System.StringComparison]::Ordinal)
    if ($start -lt 0) {
        return $null
    }

    $next = $Text.IndexOf("[info] app run start:", $start + 1, [System.StringComparison]::Ordinal)
    if ($next -lt 0) {
        return $Text.Substring($start)
    }

    return $Text.Substring($start, $next - $start)
}

function Get-FirstReasonLine {
    param([string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return "see log"
    }

    $lines = $Text -split "`r?`n"
    $errorLine = $lines | Where-Object { $_ -match "error [A-Z]{2}\d+:" } | Select-Object -First 1
    if (-not [string]::IsNullOrWhiteSpace($errorLine)) {
        return $errorLine.Trim()
    }

    $warnLine = $lines | Where-Object { $_ -match "__ERR_" } | Select-Object -First 1
    if (-not [string]::IsNullOrWhiteSpace($warnLine)) {
        return $warnLine.Trim()
    }

    $firstNonEmpty = $lines | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Last 1
    if (-not [string]::IsNullOrWhiteSpace($firstNonEmpty)) {
        return $firstNonEmpty.Trim()
    }

    return "see log"
}

function Invoke-RunBuild {
    param(
        [string]$PwshPath,
        [string]$RepoRoot,
        [string]$Configuration,
        [int]$TimeoutSec,
        [string]$StdoutPath,
        [string]$StderrPath
    )

    if (Test-Path -LiteralPath $StdoutPath) {
        Remove-Item -LiteralPath $StdoutPath -Force
    }
    if (Test-Path -LiteralPath $StderrPath) {
        Remove-Item -LiteralPath $StderrPath -Force
    }

    $proc = Start-Process -FilePath $PwshPath -ArgumentList @(
            "-NoLogo",
            "-NoProfile",
            "-Command",
            "./run_build.ps1 -Configuration $Configuration"
        ) -WorkingDirectory $RepoRoot -RedirectStandardOutput $StdoutPath -RedirectStandardError $StderrPath -PassThru

    $timedOut = -not $proc.WaitForExit($TimeoutSec * 1000)
    if ($timedOut) {
        try {
            & $PwshPath -NoLogo -NoProfile -Command "./run_build.ps1 -Stop" | Out-Null
        }
        catch {
        }

        try {
            $proc.Kill()
        }
        catch {
        }

        try {
            $proc.WaitForExit()
        }
        catch {
        }
    }

    $stdout = if (Test-Path -LiteralPath $StdoutPath) { Get-Content -LiteralPath $StdoutPath -Raw } else { "" }
    $stderr = if (Test-Path -LiteralPath $StderrPath) { Get-Content -LiteralPath $StderrPath -Raw } else { "" }

    $combined = @($stdout, $stderr) -join [Environment]::NewLine

    return [pscustomobject]@{
        TimedOut = $timedOut
        ExitCode = if ($timedOut) { 124 } else { $proc.ExitCode }
        Output = $combined
    }
}

$scriptDir = Split-Path -Parent $PSCommandPath
$repoRoot = Split-Path -Parent (Split-Path -Parent $scriptDir)
$buildScript = Join-Path $repoRoot "build_app_freestanding_wsl.ps1"

if (-not (Test-Path -LiteralPath $buildScript)) {
    throw "Build script not found: $buildScript"
}

$pwshCommand = Get-Command pwsh -ErrorAction Stop
$pwshPath = $pwshCommand.Source

$resultsDir = Join-Path $scriptDir "results"
$logsDir = Join-Path $resultsDir "runtime-logs"
New-Item -ItemType Directory -Force -Path $logsDir | Out-Null

$tests = @(
    @{ Id = "01"; Symbol = "EXP_TEST_01"; Name = "Length"; Expected = 3 },
    @{ Id = "02"; Symbol = "EXP_TEST_02"; Name = "Indexer_FirstChar"; Expected = 97 },
    @{ Id = "03"; Symbol = "EXP_TEST_03"; Name = "Indexer_LoopSum"; Expected = 294 },
    @{ Id = "07"; Symbol = "EXP_TEST_07"; Name = "StringEqualsLiteral"; Expected = 1 },
    @{ Id = "08"; Symbol = "EXP_TEST_08"; Name = "StringNotEquals"; Expected = 1 },
    @{ Id = "09"; Symbol = "EXP_TEST_09"; Name = "AsciiEncode_Indexer"; Expected = 198 },
    @{ Id = "10"; Symbol = "EXP_TEST_10"; Name = "Utf16LeEncode_Indexer"; Expected = 131 },
    @{ Id = "11"; Symbol = "EXP_TEST_11"; Name = "Utf8Encode_Bmp_NoPin"; Expected = 423 },
    @{ Id = "12"; Symbol = "EXP_TEST_12"; Name = "FixedString"; Expected = 294 },
    @{ Id = "13"; Symbol = "EXP_TEST_13"; Name = "GetPinnableReference"; Expected = 97 },
    @{ Id = "16"; Symbol = "EXP_TEST_16"; Name = "NewStringRepeatChar"; Expected = 4 },
    @{ Id = "18"; Symbol = "EXP_TEST_18"; Name = "ConcatVariableLiteral"; Expected = 2 }
)

$rows = New-Object System.Collections.Generic.List[object]

foreach ($test in $tests) {
    $id = $test.Id
    $symbol = $test.Symbol
    $name = $test.Name
    $expected = [uint32]$test.Expected

    $buildLogPath = Join-Path $logsDir ("test{0}.build.log" -f $id)
    $runLogPath = Join-Path $logsDir ("test{0}.run.log" -f $id)
    $runStdoutPath = Join-Path $logsDir ("test{0}.run.stdout.log" -f $id)
    $runStderrPath = Join-Path $logsDir ("test{0}.run.stderr.log" -f $id)

    Write-Host "=== Runtime test $id ($name) / $symbol ==="

    $buildExitCode = 0
    $buildOutput = @()
    try {
        $buildOutput = & "$buildScript" -Distro $Distro -DefineConstants $symbol *>&1
        $buildExitCode = $LASTEXITCODE
    }
    catch {
        $buildExitCode = 1
        if ($null -ne $_ -and $null -ne $_.Exception) {
            $buildOutput += $_.Exception.Message
        }
        elseif ($null -ne $_) {
            $buildOutput += "$_"
        }
    }

    $buildText = ($buildOutput | ForEach-Object { "$_" }) -join [Environment]::NewLine
    Set-Content -LiteralPath $buildLogPath -Encoding UTF8 -Value $buildText

    $status = "pass"
    $reason = ""
    $actualId = $null
    $actualResult = $null
    $appExit = $null

    if ($buildExitCode -ne 0) {
        $status = "fail"
        $reason = "build failed: $(Get-FirstReasonLine -Text $buildText)"
    }
    else {
        $run = Invoke-RunBuild -PwshPath $pwshPath -RepoRoot $repoRoot -Configuration $Configuration -TimeoutSec $QemuTimeoutSec -StdoutPath $runStdoutPath -StderrPath $runStderrPath
        Set-Content -LiteralPath $runLogPath -Encoding UTF8 -Value $run.Output

        if ($run.TimedOut) {
            $status = "fail"
            $reason = "run timeout (${QemuTimeoutSec}s)"
        }
        elseif ($run.ExitCode -ne 0) {
            $status = "fail"
            $reason = "run_build exit code $($run.ExitCode)"
        }
        else {
            $cleanText = Remove-AnsiEscape -Text $run.Output
            $segment = Get-HelloCsSegment -Text $cleanText
            if ($null -eq $segment) {
                $status = "fail"
                $reason = "HELLOCS segment not found"
            }
            else {
                $idMatch = [regex]::Match($segment, "test_id=(\d+)")
                $resultMatch = [regex]::Match($segment, "test_result=(\d+)")
                $exitMatch = [regex]::Match($segment, "process exit code = (-?\d+)")

                if ($idMatch.Success) {
                    $actualId = [uint32]$idMatch.Groups[1].Value
                }
                if ($resultMatch.Success) {
                    $actualResult = [uint32]$resultMatch.Groups[1].Value
                }
                if ($exitMatch.Success) {
                    $appExit = [int]$exitMatch.Groups[1].Value
                }

                if (-not $idMatch.Success) {
                    $status = "fail"
                    $reason = "test_id not found"
                }
                elseif (-not $resultMatch.Success) {
                    $status = "fail"
                    $reason = "test_result not found"
                }
                elseif (-not $exitMatch.Success) {
                    $status = "fail"
                    $reason = "HELLOCS exit code not found"
                }
                elseif ($actualId -ne [uint32]$id) {
                    $status = "fail"
                    $reason = "test_id mismatch expected=$id actual=$actualId"
                }
                elseif ($appExit -ne 21) {
                    $status = "fail"
                    $reason = "HELLOCS exit mismatch expected=21 actual=$appExit"
                }
                elseif ($actualResult -ne $expected) {
                    $status = "fail"
                    $reason = "result mismatch expected=$expected actual=$actualResult"
                }
            }
        }
    }

    $rows.Add([pscustomobject]@{
            Id = $id
            Name = $name
            Symbol = $symbol
            Expected = $expected
            ActualId = $actualId
            ActualResult = $actualResult
            AppExit = $appExit
            Status = $status
            Reason = $reason
            BuildLog = "results/runtime-logs/test$id.build.log"
            RunLog = "results/runtime-logs/test$id.run.log"
        }) | Out-Null
}

$timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
$passCount = ($rows | Where-Object { $_.Status -eq "pass" }).Count
$failCount = ($rows | Where-Object { $_.Status -eq "fail" }).Count

$md = New-Object System.Collections.Generic.List[string]
$md.Add("# String Runtime Matrix Results")
$md.Add("")
$md.Add("Дата: $timestamp")
$md.Add("")
$md.Add("| test | name | symbol | expected | actual id | actual result | app exit | status | reason | build log | run log |")
$md.Add("|---|---|---|---:|---:|---:|---:|---|---|---|---|")

foreach ($row in $rows) {
    $reason = if ([string]::IsNullOrWhiteSpace($row.Reason)) { "-" } else { $row.Reason }
    $actualId = if ($null -eq $row.ActualId) { "-" } else { $row.ActualId }
    $actualResult = if ($null -eq $row.ActualResult) { "-" } else { $row.ActualResult }
    $appExit = if ($null -eq $row.AppExit) { "-" } else { $row.AppExit }
    $md.Add("| $($row.Id) | $($row.Name) | $($row.Symbol) | $($row.Expected) | $actualId | $actualResult | $appExit | $($row.Status) | $reason | $($row.BuildLog) | $($row.RunLog) |")
}

$md.Add("")
$md.Add("Summary: pass=$passCount, fail=$failCount")
$md.Add("")

$latestPath = Join-Path $resultsDir "runtime-latest.md"
Set-Content -LiteralPath $latestPath -Encoding UTF8 -Value ($md -join [Environment]::NewLine)

Write-Host ""
Write-Host "Runtime matrix results saved: $latestPath"
Write-Host "Summary: pass=$passCount fail=$failCount"
