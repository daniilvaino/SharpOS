# build.ps1 -- собрать freestanding-приложения SharpOS (win-x64 PE).
#
#   ./build.ps1                 все приложения
#   ./build.ps1 launcher        одно, по имени
#   ./build.ps1 list            что вообще есть
#   ./build.ps1 apps_native/Foo/Foo.csproj     проект по пути
#
# Цели подсказываются по Tab (ArgumentCompleter ниже) и находятся обходом
# дерева — см. tools/Apps.ps1, там же почему это отдельный файл.
#
# Заменяет восемь скриптов, стоявших здесь раньше (build_launcher.ps1 и семь
# обёрток вокруг него). Отличались они одной строкой — путём к проекту, — и
# цена была не в копировании: когда у каждого приложения свой скрипт, собрать
# их все — ничья обязанность. 2026-09-24 стенд весь вечер исполнял трёхдневный
# лаунчер рядом со свежим ядром, и оба выглядели сегодняшними. Одна точка входа
# делает вопрос «а пересобрал ли я» отвечаемым.
#
# Рецепт линковки (/ENTRY:SharpAppBootstrap, /SUBSYSTEM, /BASE, /FIXED,
# /NODEFAULTLIB) и поверхность std/sdk лежат в apps_native/sdk/
# FreestandingPe.props; __security_cookie даёт CoffStub.Generator через
# @(NativeLibrary). Поэтому `dotnet publish -r win-x64` выдаёт PE сразу — без
# cl.exe и без ручной линковки. Линкер — lld-link на любом хосте
# (SharpOsNativeLink.props), MSVC и Windows SDK приложениям не нужны. PeLoader
# кладёт образ по ImageBase 0x100000000.
#
# Версии инструментов сверяются с toolchain.json (tools/Toolchain.ps1); ничего
# не устанавливается.

param(
    [Parameter(Position = 0)]
    [ArgumentCompleter({
            param($commandName, $parameterName, $wordToComplete, $commandAst, $fakeBoundParameters)

            # Корень берём от того, как скрипт назвали в строке (./build.ps1,
            # C:\work\OS\build.ps1), а не от текущего каталога: подсказки должны
            # работать и когда зовут по пути из другого места.
            $root = $null
            $typed = $commandAst.CommandElements[0].Extent.Text
            if ($typed) {
                $resolved = Resolve-Path -LiteralPath $typed -ErrorAction SilentlyContinue
                if ($resolved) { $root = Split-Path -Parent $resolved.Path }
            }
            if (-not $root) { $root = (Get-Location).ProviderPath }

            $hints = [System.Collections.Generic.List[object]]::new()
            $hints.Add([PSCustomObject]@{ Text = 'all'; Tip = 'все приложения' })
            $hints.Add([PSCustomObject]@{ Text = 'list'; Tip = 'показать цели и выйти' })

            # Имена приложений — тем же обходом, что и сама сборка. Если дерева
            # не видно, остаются all и list: они верны всегда, а зашитый про
            # запас список приложений — ровно та таблица, которая протухает.
            $helper = Join-Path $root 'tools/Apps.ps1'
            if (Test-Path -LiteralPath $helper) {
                . $helper
                foreach ($app in (Get-SharpOsApps -RepoRoot $root)) {
                    $relative = $app.Project
                    if ($relative.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
                        $relative = $relative.Substring($root.Length).TrimStart([char]92, [char]47)
                    }
                    # Прямые косые и в подсказке, и в сравнении ниже: набирают
                    # путь и так, и так, а подставлять надо одно. PowerShell на
                    # Windows принимает оба вида.
                    $relative = $relative.Replace([char]92, [char]47)
                    $hints.Add([PSCustomObject]@{
                            Text = $app.Short.ToLowerInvariant()
                            Tip  = $relative
                        })
                    # Путь к проекту — отдельной подсказкой, но только когда его
                    # и набирают. Иначе пустой Tab показывал бы каждую цель
                    # дважды.
                    if ($wordToComplete -match '[/\\]') {
                        $hints.Add([PSCustomObject]@{ Text = $relative; Tip = $app.Name })
                    }
                }
            }

            $needle = $wordToComplete.Replace([char]92, [char]47)
            $hints |
                Where-Object { $_.Text -like "$needle*" } |
                ForEach-Object {
                    [System.Management.Automation.CompletionResult]::new(
                        $_.Text, $_.Text, 'ParameterValue', $_.Tip)
                }
        })]
    [string]$Target = "all",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$RuntimeIdentifier = "win-x64"
)

$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot

function Build-SharpOsApp {
    param([string]$ProjectFile)

    $projectDir = Split-Path -Parent $ProjectFile
    $projectName = [System.IO.Path]::GetFileNameWithoutExtension($ProjectFile)
    $outDir = Join-Path $projectDir "bin/$Configuration/out-$RuntimeIdentifier"

    & dotnet publish $ProjectFile -c $Configuration -r $RuntimeIdentifier --output $outDir "/p:BuildId=$script:BuildId" /v:minimal
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

    $exe = Join-Path $outDir "$projectName.exe"
    if (-not (Test-Path -LiteralPath $exe)) { throw "PE not produced: $exe" }
    return $exe
}

. (Join-Path $PSScriptRoot "tools/Apps.ps1")
$apps = @(Get-SharpOsApps -RepoRoot $PSScriptRoot)

if ($Target -ieq "list") {
    "{0,-12} {1}" -f "ЦЕЛЬ", "ПРОЕКТ"
    foreach ($a in $apps) {
        "{0,-12} {1}" -f $a.Short.ToLowerInvariant(), (Resolve-Path -LiteralPath $a.Project -Relative)
    }
    ""
    "а также: all (по умолчанию), list, путь к .csproj. Цели подсказываются по Tab."
    Pop-Location
    return
}

if ($Target -ieq "all") {
    $selected = $apps
}
elseif ($Target -match '\.csproj$') {
    if (-not (Test-Path -LiteralPath $Target)) { Pop-Location; throw "Нет такого проекта: $Target" }
    $selected = @([PSCustomObject]@{
            Name    = [System.IO.Path]::GetFileNameWithoutExtension($Target)
            Project = (Resolve-Path -LiteralPath $Target).Path
        })
}
else {
    $selected = @($apps | Where-Object {
            $_.Name -ieq $Target -or $_.Folder -ieq $Target -or $_.Short -ieq $Target
        })
    if ($selected.Count -eq 0) {
        $known = (($apps | ForEach-Object { $_.Short.ToLowerInvariant() }) | Sort-Object) -join ", "
        Pop-Location
        throw "Неизвестная цель '$Target'. Есть: $known (а также all, list и путь к .csproj)"
    }
}

# Тот же идентификатор, что кладёт в ядро run_build.ps1: у ядра и приложений в
# одном образе должно быть одно имя состояния дерева, иначе их не сопоставить.
. (Join-Path $PSScriptRoot "tools/BuildId.ps1")
$script:BuildId = Get-SharpOsBuildId -RepoRoot $PSScriptRoot
Write-Host "BuildId=$script:BuildId"

. (Join-Path $PSScriptRoot "tools/Toolchain.ps1")
$saved = Enter-SharpOsToolchain -Components lld, dotnet
$built = @()
$failed = @()
try {
    # Один раз на весь прогон, а не перед каждым приложением: csproj импортирует
    # его .targets, а DLL, оставшаяся от старой копии дерева, была бы
    # использована молча. Десять пересборок подряд — чистая трата.
    & dotnet build "bootasm/CoffStub.Generator/CoffStub.Generator.csproj" -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "CoffStub.Generator build failed ($LASTEXITCODE)" }

    foreach ($app in $selected) {
        Write-Host ""
        Write-Host "=== $($app.Name) ===" -ForegroundColor Cyan
        try {
            $exe = Build-SharpOsApp -ProjectFile $app.Project
            $built += [PSCustomObject]@{ Name = $app.Name; Path = $exe }
        }
        catch {
            # Собирается всё, и в итоге названо всё. Остановка на первой
            # поломке означает, что вторую увидишь только следующим прогоном.
            $failed += [PSCustomObject]@{ Name = $app.Name; Reason = $_.Exception.Message }
            Write-Host "СБОЙ: $($app.Name) — $($_.Exception.Message)" -ForegroundColor Red
        }
    }
}
finally {
    Exit-SharpOsToolchain $saved
    Pop-Location
}

Write-Host ""
foreach ($b in $built) { Write-Host "собрано  $($b.Name): $($b.Path)" }
foreach ($f in $failed) { Write-Host "СБОЙ     $($f.Name): $($f.Reason)" -ForegroundColor Red }
Write-Host ""
Write-Host "собрано $($built.Count), сбоев $($failed.Count)."

if ($failed.Count -gt 0) { exit 1 }
