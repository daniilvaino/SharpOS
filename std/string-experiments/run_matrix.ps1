param(
    [string]$Distro = "Ubuntu"
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $PSCommandPath
$repoRoot = Split-Path -Parent (Split-Path -Parent $scriptDir)
$buildScript = Join-Path $repoRoot "build_app_freestanding_wsl.ps1"

if (-not (Test-Path -LiteralPath $buildScript)) {
    throw "Build script not found: $buildScript"
}

$resultsDir = Join-Path $scriptDir "results"
$logsDir = Join-Path $resultsDir "logs"
New-Item -ItemType Directory -Force -Path $logsDir | Out-Null

$tests = @(
    @{ Id = "01"; Symbol = "EXP_TEST_01"; Name = "Length" },
    @{ Id = "02"; Symbol = "EXP_TEST_02"; Name = "Indexer_FirstChar" },
    @{ Id = "03"; Symbol = "EXP_TEST_03"; Name = "Indexer_LoopSum" },
    @{ Id = "07"; Symbol = "EXP_TEST_07"; Name = "StringEqualsLiteral" },
    @{ Id = "08"; Symbol = "EXP_TEST_08"; Name = "StringNotEquals" },
    @{ Id = "09"; Symbol = "EXP_TEST_09"; Name = "AsciiEncode_Indexer" },
    @{ Id = "10"; Symbol = "EXP_TEST_10"; Name = "Utf16LeEncode_Indexer" },
    @{ Id = "11"; Symbol = "EXP_TEST_11"; Name = "Utf8Encode_Bmp_NoPin" },
    @{ Id = "12"; Symbol = "EXP_TEST_12"; Name = "FixedString" },
    @{ Id = "13"; Symbol = "EXP_TEST_13"; Name = "GetPinnableReference" },
    @{ Id = "16"; Symbol = "EXP_TEST_16"; Name = "NewStringRepeatChar" },
    @{ Id = "18"; Symbol = "EXP_TEST_18"; Name = "ConcatVariableLiteral" }
)

$rows = New-Object System.Collections.Generic.List[object]

foreach ($test in $tests) {
    $id = $test.Id
    $symbol = $test.Symbol
    $name = $test.Name
    $logPath = Join-Path $logsDir ("test{0}.log" -f $id)

    Write-Host "=== Test $id ($name) / $symbol ==="
    $exitCode = 0
    $output = @()

    try {
        $output = & "$buildScript" -NoCopy -Distro $Distro -DefineConstants $symbol *>&1
        $exitCode = $LASTEXITCODE
    }
    catch {
        $exitCode = 1
        if ($null -ne $_ -and $null -ne $_.Exception) {
            $output += $_.Exception.Message
        }
        elseif ($null -ne $_) {
            $output += "$_"
        }
    }

    $outputText = ($output | ForEach-Object { "$_" }) -join [Environment]::NewLine
    Set-Content -Path $logPath -Encoding UTF8 -Value $outputText

    $status = if ($exitCode -eq 0) { "pass" } else { "fail" }
    $reason = ""

    if ($status -eq "fail") {
        $match = [regex]::Match($outputText, "Expected type '([^']+)' not found")
        if ($match.Success) {
            $reason = "missing type: $($match.Groups[1].Value)"
        }
        else {
            $match = [regex]::Match($outputText, "Code generation failed for method '([^']+)'")
            if ($match.Success) {
                $reason = "codegen failed: $($match.Groups[1].Value)"
            }
            else {
                $errorLine = ($outputText -split "`r?`n" | Where-Object { $_ -match "error CS\d+:" } | Select-Object -First 1)
                if (-not [string]::IsNullOrWhiteSpace($errorLine)) {
                    $errorLine = [regex]::Replace($errorLine, "\s\[[^\]]+\]\s*$", "")
                    $match = [regex]::Match($errorLine, "error (CS\d+):\s*(.+)$")
                    if ($match.Success) {
                        $reason = "$($match.Groups[1].Value): $($match.Groups[2].Value)"
                    }
                    else {
                        $reason = "see log"
                    }
                }
                else {
                    $reason = "see log"
                }
            }
        }
    }

    $rows.Add([pscustomobject]@{
            Id = $id
            Name = $name
            Symbol = $symbol
            Status = $status
            Reason = $reason
            Log = "results/logs/test$id.log"
        }) | Out-Null
}

$timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
$passCount = ($rows | Where-Object { $_.Status -eq "pass" }).Count
$failCount = ($rows | Where-Object { $_.Status -eq "fail" }).Count

$md = New-Object System.Collections.Generic.List[string]
$md.Add("# String Matrix Results")
$md.Add("")
$md.Add("Дата: $timestamp")
$md.Add("")
$md.Add("| test | name | symbol | status | reason | log |")
$md.Add("|---|---|---|---|---|---|")

foreach ($row in $rows) {
    $reason = if ([string]::IsNullOrWhiteSpace($row.Reason)) { "-" } else { $row.Reason }
    $md.Add("| $($row.Id) | $($row.Name) | $($row.Symbol) | $($row.Status) | $reason | $($row.Log) |")
}

$md.Add("")
$md.Add("Summary: pass=$passCount, fail=$failCount")
$md.Add("")

$latestPath = Join-Path $resultsDir "latest.md"
Set-Content -Path $latestPath -Encoding UTF8 -Value ($md -join [Environment]::NewLine)

Write-Host ""
Write-Host "Matrix results saved: $latestPath"
Write-Host "Summary: pass=$passCount fail=$failCount"
