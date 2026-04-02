# step020

Дата: 2026-04-02

## Цель

Расширить app ABI и SDK до версии 2: добавить файловые, клавиатурные и `RunApp(path)` сервисы для внешних приложений, сохранив совместимость с ABI v1.

## Что реализовано

### 1. App ABI v2 в ядре (совместимо с v1)

Обновлены:
- `OS_0.1/src/Kernel/Process/AppServiceTable.cs`
- `OS_0.1/src/Kernel/Process/ProcessStartupBlock.cs`
- `OS_0.1/src/Kernel/Process/ProcessImage.cs`
- `OS_0.1/src/Kernel/Process/ProcessImageBuilder.cs`
- `OS_0.1/src/Kernel/Process/ProcessStartupBuilder.cs`
- `OS_0.1/src/Kernel/Process/ProcessValidation.cs`
- `OS_0.1/src/Kernel/Process/ProcessDiagnostics.cs`
- `OS_0.1/src/Kernel/Elf/ElfValidation.cs`
- `OS_0.1/src/Kernel/Elf/ElfAppContract.cs`

Сделано:
- введены версии ABI:
  - `v1` (legacy apps),
  - `v2` (новые SDK apps);
- `AppServiceTable` расширена (новые поля добавлены в конец, префикс v1 сохранён):
  - `FileExistsAddress`
  - `ReadFileAddress`
  - `ReadDirEntryAddress`
  - `TryReadKeyAddress`
  - `RunAppAddress`;
- `ProcessImageBuilder` принимает `requestedAbiVersion`;
- для старых ELF (`HELLO/ABIINFO/MARKER`) оставлен `v1`, для `HELLOCS` выставлен `v2`;
- валидация процесса теперь проверяет таблицу сервисов с учётом версии ABI.

### 2. Kernel-side реализация новых сервисов

Полностью переработан:
- `OS_0.1/src/Kernel/Process/AppServiceBuilder.cs`

Добавлено:
- новые request-структуры и `AppServiceStatus`;
- сервисы:
  - `FileExists(request*)`
  - `ReadFile(request*)`
  - `ReadDirEntry(request*)`
  - `TryReadKey(request*)`
  - `RunApp(request*)`;
- `GetAbiVersion()` теперь возвращает именно опубликованную версию таблицы для текущего app-run;
- SysV thunk-bridge расширен под новые one-arg сервисы (для freestanding C# app path);
- `RunApp` принимает:
  - путь (`ASCII C-string`),
  - `AppAbiVersion`,
  - `ServiceAbi` (`WindowsX64`/`SystemV`),
  и возвращает `ExitCode` дочернего app;
- вложенный запуск app в `RunApp` изолирует/восстанавливает состояние `Exit` родительского app;
- сохранён старый v1 сервисный префикс и старый exit-path.

### 3. Boot-side чтение файла в caller-provided buffer

Обновлены:
- `OS_0.1/src/Boot/BootInfo.cs`
- `OS_0.1/src/Boot/UefiBootInfoBuilder.cs`
- `OS_0.1/src/Boot/UefiFileLoader.cs`
- `OS_0.1/src/Boot/UefiFile.cs`

Сделано:
- добавлен callback `FileReadIntoBuffer`;
- реализован путь `ReadIntoBuffer` без возврата выделенного буфера наружу:
  - app передаёт буфер и capacity;
  - возвращается `bytesRead` + статус (`Ok` / `BufferTooSmall` / и т.д.).

Это нужно для SDK file API без неограниченного накопления временных буферов.

### 4. SDK v2 (fs + keyboard)

Обновлены:
- `apps/sdk/AppStartupBlock.cs`
- `apps/sdk/AppServiceTable.cs`
- `apps/sdk/AppHost.cs`

Добавлены:
- `apps/sdk/KeyInfo.cs`
- `apps/sdk/FileEntry.cs`

Сделано:
- SDK отражает ABI v2 layout;
- в `AppHost` добавлены:
  - `FileExistsEx(...)` / `FileExists(...)`
  - `TryReadFile(...)`
  - `TryReadDirEntry(...)`
  - `TryReadKey(...)`
  - `TryRunApp(...)`;
- для ABI v1 сервисы v2 корректно возвращают `Unsupported`.

### 5. Обновление app проектов

Обновлены:
- `apps/HelloSharpFs/HelloSharpFs.csproj`
- `apps/HelloSharp/HelloSharp.csproj`
- `apps/HelloSharpFs/Program.cs`

Сделано:
- в проекты добавлены новые SDK файлы (`KeyInfo`, `FileEntry`);
- `HelloSharpFs` дополнительно делает smoke-вызовы новых SDK API:
  - `FileExists`
  - `TryReadDirEntry`
  - `TryReadKey` (polling, non-blocking)
  - `TryRunApp("\\EFI\\BOOT\\HELLO.ELF", v1, WindowsX64)`.

## Проверка

Выполнено:
- `./run_build.ps1 -NoRun` — ядро собирается успешно.
- `dotnet build apps/HelloSharpFs/HelloSharpFs.csproj -c Release` — успешно.
- `dotnet build apps/HelloSharp/HelloSharp.csproj -c Release` — успешно.
- `./build_app_freestanding_wsl.ps1 -NoCopy` — freestanding `HELLOCS.ELF` собирается успешно.

## Итог

Step 20 закрыт: SDK получил file+keyboard+`RunApp(path)` API через App ABI v2, при этом ABI v1 приложения продолжают работать на старом префиксе сервисов без изменений layout.

Дополнительно (cleanup):
- проект `apps/HelloSharp` удалён из репозитория и из `OS.sln` как неиспользуемый/неактуальный путь;
- актуальным app-путём оставлен `apps/HelloSharpFs`.
