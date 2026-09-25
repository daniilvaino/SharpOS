# tools/Apps.ps1 -- что считается приложением SharpOS. Одно определение.
#
# Отдельный файл, а не функция внутри build.ps1, по единственной причине:
# подсказки по Tab считает ArgumentCompleter, а он исполняется, когда тело
# скрипта ещё не запускалось, и ни одной его функции не видит. Будь определение
# в двух местах — список целей и список подсказок разошлись бы молча, и первым
# это заметил бы тот, кто собрал не то, что думал.
#
# Приложение — это проект под apps_native/, импортирующий freestanding-рецепт
# (apps_native/sdk/FreestandingPe.props). Признак выбран так, чтобы новое
# приложение попадало в сборку тем, что оно есть, а не тем, что его вписали в
# таблицу; ядра эмуляторов рядом (FamiAot, TriCNESAot, ManagedDoom) рецепт не
# импортируют, и это ровно нужное различие.

function Get-SharpOsApps {
    param([Parameter(Mandatory)][string]$RepoRoot)

    $appsDir = Join-Path $RepoRoot "apps_native"
    if (-not (Test-Path -LiteralPath $appsDir)) { return @() }

    Get-ChildItem -LiteralPath $appsDir -Filter "*.csproj" -Recurse -File |
        Where-Object { (Get-Content -LiteralPath $_.FullName -Raw) -match 'FreestandingPe\.props' } |
        Sort-Object Name |
        ForEach-Object {
            $name = [System.IO.Path]::GetFileNameWithoutExtension($_.Name)
            [PSCustomObject]@{
                Name    = $name
                Folder  = $_.Directory.Name
                # Имя, которым приложение зовут вслух: "doom", а не "DoomApp"
                # и не "GPL_AHEAD_WARNING_DOOM_managed".
                Short   = ($name -replace 'App$', '')
                Project = $_.FullName
            }
        }
}
