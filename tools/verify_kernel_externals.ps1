# step 120 — verify_kernel_externals.ps1
#
# Архитектурный gate: гарантирует что OS.obj (managed C# → ILC → native)
# не "просасывает" символы из форка / libcmt / каких-то ещё libs кроме
# явно разрешённого whitelist'а.
#
# Принцип: dumpbin /symbols OS.obj выдаёт все UNDEF (undefined external)
# references. Каждая должна попадать в одну из whitelist-категорий:
#
#   1. Rh* / Rhp* — runtime helpers (наши [RuntimeExport] или
#      shellcode-patched). Зеркальные libcmt copies глушатся
#      /FORCE:MULTIPLE.
#   2. coreclr_* — host API форка (kernel→fork legitimate calls):
#      coreclr_initialize, coreclr_execute_assembly, coreclr_shutdown_2.
#   3. CRT primitives (memset, memcpy, memmove, memcmp, __chkstk,
#      __security_cookie, __security_check_cookie, _fltused, _tls_index)
#      — все наши через [RuntimeExport] или kernel_crt_stubs.obj.
#   4. EH personalities — __CxxFrameHandler3/4, __C_specific_handler —
#      наши через [RuntimeExport].
#   5. Phase-scrt scaffolding (atexit, __std_*, __current_exception*,
#      vcrt/acrt init helpers) — stubbed в CrtAndEhStubs.cs.
#
# Любой не-whitelisted UNDEF = failure. Запуск из VS Developer Command
# Prompt (dumpbin.exe нужно в PATH).
#
# Usage:
#   pwsh -File tools\verify_kernel_externals.ps1
#   pwsh -File tools\verify_kernel_externals.ps1 -Verbose

param(
    [string]$Configuration = "Release",
    [switch]$VerboseDump
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$osObj = Join-Path $repoRoot "OS\obj\$Configuration\net7.0\win-x64\native\OS.obj"

if (-not (Test-Path -LiteralPath $osObj)) {
    throw "OS.obj not found at $osObj — rebuild first via run_build.ps1"
}

$dumpbin = "dumpbin.exe"
try { & $dumpbin 2>&1 | Out-Null } catch {
    throw "dumpbin.exe not in PATH — run from VS Developer Command Prompt"
}

# Whitelist patterns. Matched как regex против symbol name.
# Каждая запись — категория с массивом patterns; для отчёта приятнее.
$whitelist = [ordered]@{
    "Rh runtime helpers" = @(
        '^Rhp[A-Z][a-zA-Z0-9_]+$',
        '^Rh[A-Z][a-zA-Z0-9_]+$',
        '^RhBox$'
    )
    "Kernel -> fork host API" = @(
        '^coreclr_initialize$',
        '^coreclr_execute_assembly$',
        '^coreclr_shutdown(_2)?$',
        '^coreclr_create_delegate$',
        '^coreclr_set_error_writer$',
        # SharpOSHost_* — naming convention shared between two directions:
        # most отра`ажают "kernel-provided API for fork to call" (наш
        # [RuntimeExport]), но эти — fork-exported diagnostic / walker
        # backdoors для kernel→fork debug-calls. Не leak'и.
        '^SharpOSHost_GetCurrentFrame$',           # FrameChain walker, SehDispatch
        '^SharpOSHost_RunCxxCtors$',               # CoreClrProbe ctor harness
        '^SharpOSHost_GetCtorDiag$',
        '^SharpOSHost_GetCtorTable$',
        '^SharpOSHost_SetCtorLimit$',
        '^SharpOSHost_SetCtorSkipMask$'
    )
    "CRT primitives (our stubs / shellcode-patched)" = @(
        '^memset$', '^memcpy$', '^memmove$', '^memcmp$', '^memchr$',
        '^strchr$', '^strrchr$', '^strstr$',
        '^wcschr$', '^wcsrchr$', '^wcsstr$',
        '^__chkstk$',
        '^__security_cookie$', '^__security_check_cookie$',
        '^_fltused$', '^_tls_index$',
        '^_purecall$',
        '^longjmp$'
    )
    "EH personalities (our)" = @(
        '^__CxxFrameHandler[34]$',
        '^__C_specific_handler$',
        '^__uncaught_exception$',
        '^__std_exception_(copy|destroy)$',
        '^__current_exception(_context)?$'
    )
    "vcrt/acrt scrt scaffolding (stubs)" = @(
        '^__vcrt_(initialize|uninitialize|uninitialize_critical|thread_(attach|detach))$',
        '^__acrt_(initialize|uninitialize|uninitialize_critical|thread_(attach|detach))$',
        '^_is_c_termination_complete$',
        '^atexit$',
        '^__tlregdtor$',
        '^_malloc_dbg$', '^_free_dbg$', '^_calloc_dbg$', '^_realloc_dbg$',
        '^_CrtDbgReportW$'
    )
    "GS canary support (libcmt fallback)" = @(
        '^__GSHandlerCheck$',
        '^__report_gsfailure$',
        '^__security_init_cookie$',
        '^__guard_dispatch_icall_fptr$'
    )
    "TLS support (libcmt fallback)" = @(
        '^__dyn_tls_init$', '^__dyn_tls_on_demand_init$', '^__tls_guard$'
    )
}

Write-Host "Dumping symbols from OS.obj..."
$rawSymbols = & $dumpbin /symbols $osObj

# UNDEF lines look like: 020 00000000 UNDEF  notype () External | symbol_name
$undefSymbols = $rawSymbols |
    Select-String -Pattern 'UNDEF.*External\s+\|\s+(\S+)' |
    ForEach-Object { $_.Matches[0].Groups[1].Value } |
    Sort-Object -Unique

Write-Host "OS.obj has $($undefSymbols.Count) unique UNDEF externals."

if ($VerboseDump) {
    Write-Host "`n--- All UNDEF symbols ---"
    $undefSymbols | ForEach-Object { Write-Host "  $_" }
}

$leaks = [System.Collections.Generic.List[string]]::new()
$matched = @{}
foreach ($cat in $whitelist.Keys) { $matched[$cat] = [System.Collections.Generic.List[string]]::new() }

foreach ($sym in $undefSymbols) {
    $found = $false
    foreach ($cat in $whitelist.Keys) {
        foreach ($pat in $whitelist[$cat]) {
            if ($sym -match $pat) {
                $matched[$cat].Add($sym)
                $found = $true
                break
            }
        }
        if ($found) { break }
    }
    if (-not $found) { $leaks.Add($sym) }
}

Write-Host "`n--- Whitelist coverage ---"
foreach ($cat in $whitelist.Keys) {
    $count = $matched[$cat].Count
    if ($count -gt 0) {
        Write-Host "  [$count] $cat"
        foreach ($s in $matched[$cat]) { Write-Host "    $s" }
    }
}

if ($leaks.Count -gt 0) {
    Write-Host "`n*** LEAKAGE: $($leaks.Count) non-whitelisted UNDEF symbols in OS.obj ***" -ForegroundColor Red
    foreach ($s in $leaks) { Write-Host "    $s" -ForegroundColor Red }
    Write-Host ""
    Write-Host "Эти символы тянутся из libcmt / форка / какой-то другой lib." -ForegroundColor Red
    Write-Host "Архитектурный invariant нарушен. Варианты:" -ForegroundColor Red
    Write-Host "  - Добавить в whitelist (если символ легитимный)" -ForegroundColor Red
    Write-Host "  - Реализовать [RuntimeExport] / stub чтобы closure стала self-contained" -ForegroundColor Red
    Write-Host "  - Изменить C# code чтобы не дёргать символ" -ForegroundColor Red
    exit 1
}

Write-Host "`n✓ Clean: все $($undefSymbols.Count) UNDEF symbols в whitelist." -ForegroundColor Green
exit 0
