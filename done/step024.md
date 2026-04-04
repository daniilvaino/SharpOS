# step024

Дата: 2026-04-02  
Статус: завершён

## Цель

Убрать зависимость `RunApp(path)` от имени файла и ввести явное чтение ABI sidecar-манифеста с fallback, без автодетекта ELF.

Цепочка выбора ABI:

1. явные параметры из запроса `RunApp` (manual override),
2. чтение sidecar `path + ".abi"`,
3. fallback на `AbiV1 + WindowsX64`.

## Что реализовано

### 1) ABI auto-маркер в API

Обновлены:

- `OS_0.1/src/Kernel/Process/AppServiceAbi.cs`
- `OS_0.1/src/Kernel/Process/AppServiceTable.cs`
- `apps/sdk/AppServiceTable.cs`

Добавлено:

- `AppServiceAbi.Auto = 0xFFFFFFFF`;
- `AppServiceTable.AutoSelectAbiVersion = 0xFFFFFFFF`.

Это позволяет запросить у kernel режим "выбери ABI сам через manifest/fallback".

### 2) SDK default RunApp теперь авто-режим

Обновлён:

- `apps/sdk/AppHost.cs`

Сделано:

- `TryRunApp(path, out exitCode)` теперь отправляет:
  - `AppAbiVersion = AutoSelectAbiVersion`,
  - `ServiceAbi = Auto`.

То есть обычный вызов `TryRunApp(path)` больше не привязан к `v1 + WindowsX64`.

### 3) Kernel-side resolver: request -> manifest -> fallback

Обновлён:

- `OS_0.1/src/Kernel/Process/AppServiceBuilder.cs`

Добавлено:

- `TryResolveRunAppAbi(...)`:
  - если ABI задан явно, используется request;
  - если запрошен auto, пробуется sidecar manifest;
  - при отсутствии/ошибке manifest используется fallback (`v1 + WindowsX64`);
- чтение manifest через `BootInfo.FileReadIntoBuffer`;
- парсер бинарного manifest-формата;
- лог источника ABI выбора:
  - `runapp abi source=request|manifest|fallback`.

### 4) Формат sidecar manifest

Файл: `APP.ELF.abi` (бинарный, 16 байт)

- bytes `[0..3]`: magic `SABI`
- u16 `[4..5]`: format version (`1`)
- u16 `[6..7]`: app ABI version (`1`/`2`)
- u16 `[8..9]`: service ABI (`0=WindowsX64`, `1=SystemV`)
- u16 `[10..11]`: reserved (`0`)
- u32 `[12..15]`: flags (`0`)

### 5) Генерация manifest в build-пайплайнах

Обновлены:

- `run_build.ps1`
- `build_app_freestanding_wsl.ps1`

Сделано:

- `run_build.ps1` теперь создаёт `.abi` рядом с:
  - `HELLO.ELF` -> `v1/windows`
  - `ABIINFO.ELF` -> `v1/windows`
  - `MARKER.ELF` -> `v1/windows`
  - `HELLOCS.ELF` (если присутствует) -> `v2/systemv`
- `build_app_freestanding_wsl.ps1` генерирует `.abi` для собранного артефакта и копирует его в ESP.

### 6) Launcher-app больше не роутит ABI по имени файла

Обновлён:

- `apps/HelloSharpFs/Program.cs`

Сделано:

- удалён ручной роутинг `ResolveLaunchAbi(...)` по имени;
- запуск из проводника снова идёт через простой вызов `AppHost.TryRunApp(path, out exitCode)`.

## Проверка

Выполнено:

1. `./build_app_freestanding_wsl.ps1`  
   - собран `HELLOCS.ELF`
   - создан `HELLOCS.ELF.abi`
   - оба файла скопированы в ESP

2. `./run_build.ps1 -NoRun`  
   - ядро успешно собрано
   - для handcrafted apps созданы `.abi`
   - `HELLOCS.ELF` обнаружен и подготовлен в ESP

## Что проверить руками в QEMU

В `HELLOCS` проводнике:

1. Выбрать `HELLOCS.ELF` и нажать Enter.
2. Проверить в логах kernel строку:
   - `runapp abi source=manifest app=2 service=sysv`
3. Убедиться, что больше нет мусора вида `0 / 000` после self-run.

## Итог

Step 24 закрыл ABI-routing через sidecar manifest с надёжным fallback, и `RunApp(path)` снова может быть простым API без ручной маршрутизации по имени файла.
