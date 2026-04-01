# step017

Дата: 2026-04-01

## Цель

Собрать первый отдельный C# app pipeline: `C# source -> WSL dotnet publish -> ELF artifact -> ESP -> RunApp(path)`.

## Что реализовано

### 1. Отдельный WSL build script для app

Добавлен:
- `build_app_wsl.ps1`

Сделано:
- сборка `apps/HelloSharp/HelloSharp.csproj` в WSL (`dotnet publish -r linux-x64`);
- устойчивый поиск собранного ELF-артефакта на Windows-стороне;
- копирование результата в ESP:
  - `OS_0.1/.qemu/esp/EFI/BOOT/HELLOCS.ELF`;
- поддержан режим `-NoCopy`.

### 2. App SDK и отдельный C# app проект

Добавлены:
- `apps/sdk/AppStartupBlock.cs`
- `apps/sdk/AppServiceTable.cs`
- `apps/sdk/AppRuntime.cs`
- `apps/sdk/AppHost.cs`
- `apps/sdk/MinimalRuntime.cs` (задел под freestanding path)
- `apps/HelloSharp/HelloSharp.csproj`
- `apps/HelloSharp/Program.cs`

Сделано:
- отдельный `HelloSharp` app с entry `SharpAppEntry`;
- app использует текущий ABI (`StartupBlock`, `AppServiceTable`, `Exit(21)`).

### 3. Интеграция HELLOCS в kernel app batch

Обновлены:
- `OS_0.1/src/Kernel/Elf/ElfAppContract.cs`
- `OS_0.1/src/Kernel/Elf/ElfValidation.cs`

Сделано:
- добавлен app path:
  - `\EFI\BOOT\HELLOCS.ELF`;
- добавлен expected exit code `21`;
- app запускается в общем batch рядом с `HELLO/ABIINFO/MARKER`.

### 4. Безопасность pipeline при несовместимом ELF

Обновлены:
- `OS_0.1/src/Kernel/Elf/ElfValidation.cs`
- `OS_0.1/src/Kernel/Process/ProcessImageBuilder.cs`
- `OS_0.1/src/Kernel/Process/ProcessStartupBuilder.cs`
- `OS_0.1/src/Kernel/Process/ProcessValidation.cs`

Сделано:
- marker в startup block сделан опциональным (только для app, где нужен marker-check);
- добавлены диагностические `warn` в process build path;
- добавлен pre-check формата ELF: сейчас допускается только `ET_EXEC`, отклоняются `ET_DYN`/`PT_INTERP`/`PT_DYNAMIC` с controlled fail.

## Проверка

Проверено:
1. `./build_app_wsl.ps1 -NoCopy` — успешно.
2. `./build_app_wsl.ps1` — успешно, `HELLOCS.ELF` копируется в ESP.
3. `./run_build.ps1` — успешно, kernel не падает на `HELLOCS.ELF`, batch завершается контролируемо.

Ключевые строки:
- `Built ELF artifact: ...\apps\HelloSharp\bin\Release\out-linux-x64\HelloSharp`
- `Copied ELF to ESP: ...\EFI\BOOT\HELLOCS.ELF`
- `[info] app run start: \EFI\BOOT\HELLOCS.ELF`
- `[warn] unsupported ELF type: only ET_EXEC is supported`
- `[warn] app failed: \EFI\BOOT\HELLOCS.ELF reason=ElfParseFailed`

## Текущий результат и техдолг

Шаг 17 частично закрыт:
- отдельный C# app pipeline через WSL работает до стадии "собрать и доставить ELF в ESP";
- запуск этого ELF в текущем loader не поддержан по формату.

Причина:
- `dotnet` NativeAOT на `linux-x64` сейчас даёт `ET_DYN` с `PT_INTERP/PT_DYNAMIC` и dynamic relocation/dependency моделью, а текущий loader поддерживает только `ET_EXEC` без dynamic linker path.

Следующий технический выбор:
1. сделать freestanding C# app build (без dynamic loader зависимостей), либо
2. расширять loader до dynamic/relocation path (гораздо тяжелее).
