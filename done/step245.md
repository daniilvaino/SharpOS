# step245: свободные картриджи — AccuracyCoin и Famidash; путь к ROM аргументом

## Контекст

release.md §1: в образе был один слот `\games\GAME.NES` (Super Mario Bros. из
`payloads\`, не наш для распространения), первый `.nes` по имени; остальные
картриджи сборка пропускала — сказать эмулятору, что открыть, было нечем.
Пользователь выбрал два свободных: AccuracyCoin (MIT, тесты точности, автор
TriCNES) и Famidash (MIT, игра, демейк Geometry Dash).

## Архитектура

- **`vendor/AccuracyCoin/`, `vendor/FamiDash/`** — ROM, `LICENSE`, `PROVENANCE.md`
  (коммит/релиз, размер, sha256, маппер). AccuracyCoin — `main` `b037b41`, NROM;
  Famidash — релиз Demo5, последний не pre-release, MMC3, NES 2.0.
- **`run_build.ps1`** — картриджи из `vendor` и все `payloads\*.nes`, каждый в
  `\games\<ИМЯ>.NES`; одноимённый payload заменяет вендоренный. Старый
  `GAME.NES` с ESP убирается.
- **TriCNES, Fami** — `FindRom`: путь из первого аргумента, иначе первый `.NES` в
  `\games` по имени; строка `cartridge <путь>` в выводе. Код 2 — нет картриджа.
- **Лаунчер** — плитка на каждый `.NES` в `\games`: Fami с путём аргументом
  (`AppHost.TryRunApp(path, args)`). Плитка TriCNES без аргумента открывает
  первый по имени — AccuracyCoin: точный эмулятор и тест точности.
- `sbom.cdx.json` — два компонента `data` с sha256; README — «Сторонние
  приложения» и Payloads; `payloads/README.md`.

## Проверка

Рабочая копия, WHPX, прогон 309 (`s309.sh` + клавиши по QMP):

- `ls /games`: `ACCURACYCOIN.NES`, `FAMIDASH.NES` рядом с `DOOM.EXE`, `FAMI.EXE`,
  `TRICNES.EXE`; `GAME.NES` нет.
- `FAMI.EXE /games/FAMIDASH.NES` — маппер 4, экран выбора уровня; Esc — код 42.
- `TRICNES.EXE` без аргумента — `cartridge /games/ACCURACYCOIN.NES`, меню тестов;
  Start — тесты идут.
- `FAMI.EXE /games/ACCURACYCOIN.NES` — меню тестов; после Start экран пустой:
  неточность Fami (README: «играбелен, не идеален»), не правка.
- `TRICNES.EXE /games/FAMIDASH.NES` — маппер 4, главное меню игры.
- `FAMI.EXE /games/NONE.NES` — `no cartridge`, код 2.
- Лаунчер: плитки `ACCURACYCOIN` и `FAMIDASH` («NES cartridge, Fami») после
  Shell, DOOM, Fami, TriCNES.

## Файлы

- `vendor/AccuracyCoin/` (`AccuracyCoin.nes`, `LICENSE`, `PROVENANCE.md`),
  `vendor/FamiDash/` (`famidash.nes`, `LICENSE`, `PROVENANCE.md`).
- `run_build.ps1`.
- `apps_native/TriCNES/Program.cs`, `apps_native/Fami/Program.cs`.
- `apps_native/Launcher/Tiles.cs`, `apps_native/Launcher/Program.cs`.
- `sbom.cdx.json`, `README.md`, `payloads/README.md`, `release.md`.

## Дальше

Релизная сборка не должна брать `payloads\*.nes` — §13.
