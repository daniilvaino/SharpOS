# payloads

Данные для приложений: то, что программы читают, но что не является исходным
кодом. Кладите файлы прямо сюда — `run_build.ps1` разложит их по ESP сам,
разбирая **по расширению**.

| расширение | куда попадёт | кто читает |
|---|---|---|
| `*.wad` | `\apps\<ИМЯ>.WAD` | `DOOM.EXE` |
| `*.nes` | `\apps\GAME.NES` | `TRICNES.EXE`, `FAMI.EXE` |
| `pwsh\<дистрибутив>\` | `\sharpos\pwsh\` | `pwsh.dll` |
| `starling\*.dll` | `\sharpos\starling\` | `StarlingProbe.dll` |

Starling — низ движка браузера (HTML, DOM, CSS, раскладка, display-list).
Собирается скриптом `build_starling.ps1` из дерева Starling, клонированного
рядом с репозиторием; сам движок в гит не попадает. Верхние слои не берём:
`Engine` и `Bindings` требуют net11, `Bindings` тянет нативный Wasmtime,
`Paint` целиком — SixLabors и wgpu, `Net` — сокеты, которых у нас нет. Из
`Paint` берётся вырез на 5 файлов, см. `apps_managed/Starling.Paint.Carve/`.

PowerShell — распакованный каталог целиком, из релизов проекта; берётся тот, чьё
имя больше по алфавиту. Версия должна быть под ту же .NET, что и наш рантайм
(сейчас 10 — то есть PowerShell 7.6.x): сборки несут предкомпилированный код с
номером формата, и рантайм грузит только свой. С 7.5 отвергается всё, включая
`System.Management.Automation` (19 МБ), и движок компилируется заново при каждом
запуске. `System.Private.CoreLib` из дистрибутива не берётся — он и рантайм
собираются вместе.

На macOS и Linux дистрибутив кладётся так же, только без `curl.exe` и с
прямыми чертами (в bash обратная черта — экранирование, и файл ляжет в корень
репозитория):

```bash
mkdir -p payloads/pwsh && cd payloads/pwsh
curl -L -o pwsh.zip https://github.com/PowerShell/PowerShell/releases/download/v7.6.5/PowerShell-7.6.5-win-x64.zip
unzip -q pwsh.zip -d PowerShell-7.6.5-win-x64 && rm pwsh.zip
```

Содержимое каталога не попадает в репозиторий: игровые данные — не исходники,
а часть из них нам и не принадлежит. В гите живёт только этот файл.

Картридж выкладывается **один** — первый по имени: сказать приложению, какую
игру открыть, пока нечем, имя файла на ESP фиксированное. Остальные найденные
`.nes` перечисляются в выводе сборки, чтобы «положил ROM, а запустился другой»
не выглядело загадкой. Когда появится передача аргументов, ограничение уйдёт.

Откуда взять DOOM (свободно распространяемая версия):

```
curl.exe -L -o payloads\DOOM1.WAD https://raw.githubusercontent.com/nifanfa/MOOS/refs/heads/master/Ramdisk/DOOM1.WAD
# macOS/Linux (в bash обратная черта в пути — экранирование, файл уехал бы в корень):
curl -L -o payloads/DOOM1.WAD https://raw.githubusercontent.com/nifanfa/MOOS/refs/heads/master/Ramdisk/DOOM1.WAD
```
