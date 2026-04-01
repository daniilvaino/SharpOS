# step018

Дата: 2026-04-01

## Цель

Собрать отдельное C# приложение в freestanding ELF-формате, совместимом с текущим SharpOS loader (`ET_EXEC`, без `PT_INTERP/PT_DYNAMIC`), и реально запустить его через `RunApp("\EFI\BOOT\HELLOCS.ELF")`.

## Что реализовано

### 1. Новый freestanding C# app project

Добавлены:
- `apps/HelloSharpFs/HelloSharpFs.csproj`
- `apps/HelloSharpFs/Program.cs`

Сделано:
- отдельный app-проект в no-runtime стиле (`NoStdLib`, `NoConfig`, `PublishAot`, `IlcSystemModule`, `EntryPointSymbol=SharpAppEntry`);
- используется текущий app ABI (`StartupBlock`, `AppServiceTable`, `Exit(21)`);
- вывод:
  - `hello from csharp fs app`
  - `abi=<version>`

### 2. Отдельный WSL-скрипт freestanding сборки

Добавлен:
- `build_app_freestanding_wsl.ps1`

Скрипт делает:
1. `dotnet publish` в режиме:
   - `/p:OutputType=Library`
   - `/p:NativeLib=Static`
2. ручную линковку в freestanding ELF:
   - `ld -e SharpAppEntry <static-lib> + security_cookie stub`
3. валидацию формата:
   - `ET_EXEC` обязателен
   - `PT_INTERP/PT_DYNAMIC` запрещены
4. копирование в ESP:
   - `OS_0.1/.qemu/esp/EFI/BOOT/HELLOCS.ELF`

### 3. ABI bridge для service-calls из SysV app в win-x64 kernel

Добавлен:
- `OS_0.1/src/Kernel/Process/AppServiceAbi.cs`

Обновлены:
- `OS_0.1/src/Kernel/Process/AppServiceBuilder.cs`
- `OS_0.1/src/Kernel/Process/ProcessImage.cs`
- `OS_0.1/src/Kernel/Process/ProcessImageBuilder.cs`
- `OS_0.1/src/Kernel/Process/ProcessStartupBuilder.cs`
- `OS_0.1/src/Kernel/Elf/ElfValidation.cs`

Сделано:
- введён выбор ABI сервисов:
  - `WindowsX64` (legacy hand-crafted apps)
  - `SystemV` (freestanding Linux-built C# app)
- для `SystemV` добавлены машинные thunk-обёртки (SysV -> Win64) в `AppServiceBuilder`;
- `HELLO/ABIINFO/MARKER` оставлены на `WindowsX64`;
- `HELLOCS` переведён на `SystemV`.

## Проверка

Проверено:
1. `./build_app_freestanding_wsl.ps1 -NoCopy` — успешно.
2. `./build_app_freestanding_wsl.ps1` — успешно, `HELLOCS.ELF` скопирован в ESP.
3. `./run_build.ps1` — успешно, полный batch-run проходит.

Ключевые строки COM1:
- `[info] app run start: \EFI\BOOT\HELLOCS.ELF`
- `hello from csharp fs app`
- `abi=1`
- `[info] process exit code = 21`
- `[info] exit source = service`
- `[info] app batch summary`
- `[info] passed: 4`
- `[info] failed: 0`

## Итог

Step 18 закрыт: получен отдельный freestanding C# app pipeline под текущий SharpOS ABI, `HELLOCS.ELF` совместим с loader и реально исполняется в системе.
