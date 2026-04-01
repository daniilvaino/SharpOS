# step013

Дата: 2026-04-01

## Цель

Переключить запуск с встроенного ELF smoke image на внешний ELF-файл с ESP:
- чтение `APP.ELF` через UEFI File Protocol;
- parse/load/validation/process-jump через существующий pipeline;
- проверка `exit code` и `marker` после возврата.

## Что реализовано

### 1. UEFI file read в boot-слое

Добавлены файлы:
- `OS_0.1/src/Boot/UefiFile.cs`
- `OS_0.1/src/Boot/UefiFileLoader.cs`

Реализовано:
- открытие root volume через:
  - `LoadedImageProtocol`
  - `SimpleFileSystemProtocol`
  - `OpenVolume`
- открытие файла в read-only режиме:
  - primary path: `\EFI\BOOT\APP.ELF`
  - fallback: `\APP.ELF`
- чтение размера через `GetInfo(EFI_FILE_INFO)`
- чтение всего файла в `AllocatePool` буфер.

### 2. Передача внешнего ELF в kernel

Обновлены файлы:
- `OS_0.1/src/Boot/BootInfo.cs`
- `OS_0.1/src/Boot/UefiBootInfoBuilder.cs`
- `OS_0.1/src/Boot/BootCapabilities.cs`
- `OS_0.1/src/Kernel/SystemBanner.cs`

Добавлено:
- в `BootInfo`:
  - `ExternalElfImage`
  - `ExternalElfImageSize`
- в capabilities:
  - `ExternalElf`
- `UefiBootInfoBuilder` загружает ELF в boot-фазе и выставляет capability.

### 3. Обновлен UEFI type-layer для file protocols

Обновлен файл:
- `OS_0.1/src/Boot/UefiTypes.cs`

Добавлены структуры и сигнатуры:
- `EFI_GUID`
- `EFI_LOADED_IMAGE_PROTOCOL`
- `EFI_SIMPLE_FILE_SYSTEM_PROTOCOL`
- `EFI_FILE_PROTOCOL`
- `EFI_FILE_INFO`
- расширенный `EFI_BOOT_SERVICES` с `HandleProtocol`.

### 4. Источник ELF заменен на внешний файл

Обновлены файлы:
- `OS_0.1/src/Kernel/Kernel.cs`
- `OS_0.1/src/Kernel/Elf/ElfValidation.cs`

Изменения:
- `ElfValidation.Run(...)` теперь принимает `BootInfo`;
- создает `MemoryBlock` из `bootInfo.ExternalElfImage/Size`;
- логирует:
  - `open elf file ok`
  - `read elf bytes = ...`.

### 5. Убрана зависимость от `ElfSmokeImage`

Изменения:
- удален файл:
  - `OS_0.1/src/Kernel/Elf/ElfSmokeImage.cs`
- добавлен контракт тестового app ABI:
  - `OS_0.1/src/Kernel/Elf/ElfAppContract.cs`
    - marker address
    - expected marker value
    - expected exit code.

### 6. В run_build добавлена генерация внешнего `APP.ELF`

Обновлен файл:
- `run_build.ps1`

Добавлено:
- генератор минимального ELF64 app (`New-AppElfImage`);
- укладка файла в ESP:
  - `OS_0.1/.qemu/esp/EFI/BOOT/APP.ELF`
- лог:
  - `Prepared app ELF: ...`.

`APP.ELF` реализует тот же startup ABI:
- читает `StartupBlock*` из `RDI`;
- берет `MarkerAddress`;
- пишет `0x12345678`;
- возвращает `42`.

## Проверка

Проверено:
1. `.\run_build.ps1 -NoRun` — успешно.
2. `.\run_build.ps1` — успешно.

Ключевые строки COM1:
- `[info] caps: TextOutput Shutdown MemoryMap ExternalElf`
- `[info] open elf file ok`
- `[info] read elf bytes = 4096`
- `[info] elf ok`
- `[info] process build start`
- `[info] jump start`
- `[info] process returned`
- `[info] process exit code = 42`
- `[info] process wrote marker = 0x12345678`

## Итог

Step 13 закрыт:
- pipeline работает с внешним ELF-файлом на EFI-разделе;
- встроенный `ElfSmokeImage` полностью убран;
- load/execute проходит end-to-end через file read -> parse -> load -> jump -> exit/marker checks.
