# step 132 — app folder refactor: apps_native/ + apps_managed/ (PE-миграция prep)

## Контекст

Перед началом ELF→PE миграции апп (workstream 2 в donext.md) дерево
приложений было «кашей»: `apps/*` (native ELF + managed вперемешку) плюс
одинокий `work/normal-hello`. Разложили по тиру исполнения, чтобы дальнейшая
PE-работа шла по чистой границе.

Мотивация — north-star managed DOOM: managed-порт ляжет в `apps_managed/`
рядом с normal-hello и PowerShellBootstrap.

## Результат

Чисто механический рефактор (git mv, история сохранена), ноль изменений
логики:

```
apps_native/     (ELF / NativeAOT, собираются через WSL)
  FetchApp/
  HelloSharpFs/   ← лаунчер (переименуем при самой PE-миграции)
  sdk/
apps_managed/    (CoreCLR-hosted, net10.0 DLL; сюда пойдёт managed-DOOM)
  normal-hello/
  PowerShellBootstrap/
```

17 tracked-файлов переехали как `R` (rename) — git видит переименование,
история каждого файла цела. `apps/` и `work/normal-hello/` больше нет.

## Что двигали и почему так

**Классификация по тиру** (подтверждена автором — «лаунчер это hellofs»):

| Было | Стало | Тир |
|---|---|---|
| `apps/FetchApp` | `apps_native/FetchApp` | ELF/AOT |
| `apps/HelloSharpFs` (лаунчер) | `apps_native/HelloSharpFs` | ELF/AOT |
| `apps/sdk` | `apps_native/sdk` | shared-source для native-апп |
| `apps/PowerShellBootstrap` | `apps_managed/PowerShellBootstrap` | CoreCLR-hosted |
| `work/normal-hello` | `apps_managed/normal-hello` | CoreCLR-hosted |

**Инвариант глубины.** `apps/X` → `apps_native/X` сохраняет глубину (оба
2 уровня под корнем), поэтому относительные Include в native-csproj
остались валидны без правок:
- `..\sdk\*.cs` (FetchApp/HelloSharpFs → sdk) — sdk переехал в тот же
  `apps_native/`, путь резолвится.
- `..\..\std\no-runtime\...` — `apps_native/FetchApp/../../std` = `root/std`, ок.

Managed-проекты (normal-hello, PowerShellBootstrap) — standalone, `..\sdk`
ссылок нет, переезд в другой родитель их не задел.

## Ловушка: git mv залоченного дерева

`git mv apps/FetchApp …` и `…/HelloSharpFs` упали с `Permission denied` —
git пытается атомарно перенести **всю** папку, включая `bin/obj` с залоченным
ELF-выходом. Обход: для этих двух перенесли только tracked-исходники
(`git mv <csproj>` + `git mv <Program.cs>`), а orphaned `bin/obj/*.lscache`
(ignored-артефакты) снесли `rm -rf apps/`. sdk (нет bin/obj) и managed-апп
переехали целой папкой без проблем.

## Обновлённые ссылки (иначе сборка бы сломалась)

- `build_fetch_wsl.ps1` — `apps/FetchApp` → `apps_native/FetchApp`
- `build_launcher_wsl.ps1` — `apps/HelloSharpFs` → `apps_native/HelloSharpFs`
- `run_build.ps1` — `$normalProj` → `apps_managed\normal-hello`;
  `$psBootstrapProj` → `apps_managed\PowerShellBootstrap` (+ комментарий)
- `.gitignore` — убран мёртвый `!work/normal-hello/` exception (normal-hello
  ушёл из `work/`); `work/*` оставлен (скрывает CSharpRepl/PAL/spc-test);
  новые app-деревья tracked, их bin/obj под глобальным build-output ignore
- `OS.sln` — пути проектов HelloSharpFs/FetchApp → `apps_native\…`;
  solution-folder «apps» → «apps_native» (VS при открытии успела выкинуть
  проекты со сломанными путями — откатили и поправили пути, сохранив
  GUID/config-блоки). Сборка `.sln` не использует (скрипты бьют по csproj),
  правка — для консистентности IDE.
- комментарии-ссылки: `GcRuntimeExports.cs`, `CoreClrProbe.cs`

Финальный скан (Grep по `*.{ps1,cs,csproj,json}`) — остаточных ссылок на
старые пути нет.

## Проверка

Тир-поверхность не менялась → limits-таблицы не трогаем. Deliverable шага —
сборка резолвит новые пути; проверяется зелёным прогоном `run_build.ps1`
(запускает автор). Логики не менял, риск — только пропущенная ссылка на путь.

## Отложено

- Переименование `HelloSharpFs` (лаунчер) — при самой PE-миграции.
- Переименование проекта `OS` → `Kernel`/`SharpOS.Kernel` — большой blast
  radius (`IlcSystemModule=OS`, namespace `OS.*`, пути), отдельным степом.
  Записано в donext.md backlog.

## Next

ELF→PE миграция апп (workstream 2): PE-машинария, уход WSL, Tier B EH
(halt-on-throw) исчезает, апп шарят Tier A EH-стек с ядром.
