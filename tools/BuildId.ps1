# tools/BuildId.ps1 -- один способ назвать состояние дерева, общий для
# run_build.ps1 (ядро) и build.ps1 (приложения).
#
# Почему не просто `git rev-parse --short HEAD`: ровно этим ядро и
# приложения представлялись раньше, и 2026-09-24 в логе стенда стояло
# `build: 567c8df`, хотя собранный код содержал ещё не закоммиченный step178.
# Идентификатор называл коммит, а не то, что собрано, и весь вечер ушёл на
# поиск несуществующей разницы между двумя сборками. Суффикс `+dirty` ставится
# по изменениям **отслеживаемых** файлов: неотслеживаемое в корне — это логи и
# образы, они к собранному коду отношения не имеют.

function Get-SharpOsBuildId {
    param([string]$RepoRoot)

    $id = "local"
    $sha = (& git -C $RepoRoot rev-parse --short HEAD 2>$null)
    if ($LASTEXITCODE -eq 0 -and $sha) { $id = $sha.Trim() }

    $dirty = (& git -C $RepoRoot status --porcelain --untracked-files=no 2>$null)
    if ($LASTEXITCODE -eq 0 -and $dirty) { $id = "$id+dirty" }

    # Свободная метка прогона: build-tag.txt в корне. Пустой файл — только SHA.
    $tagFile = Join-Path $RepoRoot "build-tag.txt"
    if (Test-Path -LiteralPath $tagFile) {
        $tag = (Get-Content -LiteralPath $tagFile -Raw).Trim()
        if ($tag) { $id = "$id-$tag" }
    }

    return $id
}
