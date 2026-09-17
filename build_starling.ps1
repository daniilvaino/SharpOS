# build_starling.ps1 -- собрать низ движка Starling в payloads\starling\.
#
# Starling (https://github.com/starling-browser/starling, Apache-2.0) — движок
# браузера целиком на .NET. Берём только НИЖНИЕ слои: HTML -> DOM -> CSS ->
# раскладка -> display-list. Они все net10 и без нативных зависимостей.
#
# Верхние слои НЕ берём:
#   Starling.Engine, Starling.Bindings — net11 preview (C# union types), а наш
#                                        форк CoreCLR это net10;
#   Starling.Bindings                  — тянет Wasmtime (нативный WASM-рантайм);
#   Starling.Paint (целиком)           — тянет SixLabors (лицензия вне нашего
#                                        списка) и Silk.NET.WebGPU (нативный wgpu);
#   Starling.Net                       — SocketsHttpHandler, а NIC-драйвера нет.
#
# Из Starling.Paint берётся вырез на 5 файлов — apps_managed\StarlingProbe\paint\.
#
# Дерево Starling в репозиторий не тащим: клонируется рядом, путь задаётся -StarlingRoot.
# Собранные DLL ложатся в payloads\starling\ и в гит не попадают (payloads\* в .gitignore).
param(
    [string]$StarlingRoot = "",
    [string]$Configuration = "Release"
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSCommandPath

if ($StarlingRoot -eq "") { $StarlingRoot = Join-Path (Split-Path -Parent $repoRoot) "starling" }
if (-not (Test-Path -LiteralPath (Join-Path $StarlingRoot "src/Starling.Layout"))) {
    throw @"
Дерево Starling не найдено: $StarlingRoot
    git clone https://github.com/starling-browser/starling.git
Либо укажите путь: .\build_starling.ps1 -StarlingRoot <путь>
"@
}
$StarlingRoot = (Resolve-Path -LiteralPath $StarlingRoot).Path

$carve = Join-Path $repoRoot "apps_managed/Starling.Paint.Carve/Starling.Paint.Carve.csproj"
$dest  = Join-Path $repoRoot "payloads/starling"
New-Item -ItemType Directory -Force -Path $dest | Out-Null

# Вырез тянет за собой Common/Css/Dom/Layout по ProjectReference; Html добавляем
# отдельно (от выреза он не зависит, но пробе нужен парсер).
$html = Join-Path $StarlingRoot "src/Starling.Html/Starling.Html.csproj"
foreach ($proj in @($carve, $html)) {
    Write-Host "Building $(Split-Path -Leaf $proj)..."
    & dotnet build $proj -c $Configuration "-p:StarlingRoot=$StarlingRoot" /v:minimal
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed for $proj ($LASTEXITCODE)" }
}

$copied = 0
foreach ($pattern in @("Starling.*.dll", "Microsoft.Extensions.*.dll")) {
    Get-ChildItem -Path $StarlingRoot, (Split-Path -Parent $carve) -Recurse -Filter $pattern -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "bin[/\\]$Configuration[/\\]net10\.0" } |
        Group-Object Name | ForEach-Object {
            Copy-Item -LiteralPath $_.Group[0].FullName -Destination (Join-Path $dest $_.Name) -Force
            $copied++
        }
}
Write-Host "Prepared payloads\starling\ ($copied сборок)"
