# step016

Дата: 2026-04-01

## Цель

Сделать read-only file layer и перевести запуск внешних приложений на `RunApp(path)` с batch-результатами без panic по app-level ошибкам.

## Важная проверка перед реализацией

Перед финальной реализацией проверен путь "частично расширить std":
- `string.IsNullOrEmpty` в этом рантайме недоступен;
- `string.Empty` недоступен;
- `new string(char[])` и `new string(char*, ...)` недоступны.

Вывод: для file-listing пришлось идти через pointer-based путь (`char*` буферы), а не через построение `string` из UEFI entry.

## Что реализовано

### 1. Boot-side file callbacks вместо предзагруженных ELF-полей

Обновлены:
- `OS_0.1/src/Boot/BootInfo.cs`
- `OS_0.1/src/Boot/UefiBootInfoBuilder.cs`
- `OS_0.1/src/Boot/UefiFileLoader.cs`
- `OS_0.1/src/Boot/UefiFile.cs`
- `OS_0.1/src/Boot/UefiTypes.cs`

Сделано:
- удалены поля `HelloElfImage / AbiInfoElfImage / MarkerElfImage` из `BootInfo`;
- добавлены file callbacks в `BootInfo`:
  - `FileExists`
  - `FileReadAll`
  - `DirectoryReadEntry`
- добавлен `BootFileStatus`;
- UEFI file-слой обобщён до операций:
  - exists by path
  - read all by path
  - read directory entry by index;
- добавлен `SetPosition` в `EFI_FILE_PROTOCOL`;
- закрыт утечечный путь в `TryReadAll` (освобождение info buffer).

### 2. HAL bridge для kernel file access

Обновлён:
- `OS_0.1/src/Hal/Platform.cs`

Добавлены platform-методы:
- `FileExists(string path)`
- `TryReadFile(string path, out void* buffer, out uint size)`
- `TryReadDirectoryEntry(...)`

### 3. Kernel file layer

Добавлены:
- `OS_0.1/src/Kernel/File/FileBuffer.cs`
- `OS_0.1/src/Kernel/File/FileInfoLite.cs`
- `OS_0.1/src/Kernel/File/FileSystem.cs`
- `OS_0.1/src/Kernel/File/FileDiagnostics.cs`

Слой даёт:
- `Exists(path)`
- `ReadAll(path, out FileBuffer)`
- `List(path)` (через index-based directory read)
- `DumpDirectory(path)` для диагностики.

### 4. Run external app by path

Обновлены:
- `OS_0.1/src/Kernel/Elf/ElfAppContract.cs`
- `OS_0.1/src/Kernel/Elf/ElfValidation.cs`

Сделано:
- добавлены path-константы:
  - `\EFI\BOOT\HELLO.ELF`
  - `\EFI\BOOT\ABIINFO.ELF`
  - `\EFI\BOOT\MARKER.ELF`
- unified pipeline теперь запускает приложения по path:
  - `Exists -> ReadAll -> Parse -> Load -> Build -> Jump -> Exit resolve -> Cleanup`
- добавлен directory dump перед app batch.

### 5. App-level crash policy: no panic, return result

Добавлен:
- `OS_0.1/src/Kernel/Elf/AppRunResult.cs`

Сделано:
- app-run ошибки переведены в `AppRunResult`, без `Panic.Fail`:
  - `FileNotFound`
  - `ReadFailed`
  - `ElfParseFailed`
  - `ElfLoadFailed`
  - `ProcessBuildFailed`
  - `ProcessValidationFailed`
  - `JumpFailed`
  - `ExitCodeMismatch`
  - `MarkerMismatch`
  - `MappingCleanupFailed`
- batch-run продолжает выполнение после ошибки отдельного app;
- добавлен batch summary:
  - `passed`
  - `failed`.

### 6. Panic policy groundwork

Добавлен:
- `OS_0.1/src/Kernel/Diagnostics/PanicMode.cs`

Обновлены:
- `OS_0.1/src/Kernel/Panic.cs`
- `OS_0.1/src/Kernel/Kernel.cs`

Сделано:
- `Panic` поддерживает `Halt / Shutdown / ReturnToKernel`;
- по умолчанию в `KernelMain.Start` установлен `PanicMode.Shutdown`;
- app-failures в batch path больше не идут через panic.

### 7. Build script cleanup

Обновлён:
- `run_build.ps1`

Сделано:
- удаляется legacy `APP.ELF`, чтобы в `\EFI\BOOT` оставались только актуальные `HELLO/ABIINFO/MARKER`.

## Проверка

Проверено:
1. `./run_build.ps1 -NoRun` — успешно.
2. `./run_build.ps1` — успешно.

Ключевые строки COM1:
- `[info] fs init ok`
- `[info] dir \EFI\BOOT`
- `[info] file: BOOTX64.EFI`
- `[info] file: HELLO.ELF`
- `[info] file: ABIINFO.ELF`
- `[info] file: MARKER.ELF`
- `[info] app run start: \EFI\BOOT\HELLO.ELF`
- `[info] app run start: \EFI\BOOT\ABIINFO.ELF`
- `[info] app run start: \EFI\BOOT\MARKER.ELF`
- `[info] exit source = service`
- `[info] app batch summary`
- `[info] passed: 3`
- `[info] failed: 0`

## Итог

Step 16 закрыт: внешний ELF pipeline переведён на file-layer + запуск по пути, directory listing работает, app-level ошибки не валят весь batch и завершаются контролируемым итоговым summary.
