# step015

Дата: 2026-04-01

## Цель

Расширить App ABI и перейти от одного smoke-app к повторяемой модели запуска нескольких внешних ELF-приложений в одном boot-сеансе.

## Что реализовано

### 1. Boot-side: загрузка нескольких внешних ELF

Обновлены:
- `OS_0.1/src/Boot/BootInfo.cs`
- `OS_0.1/src/Boot/UefiFileLoader.cs`
- `OS_0.1/src/Boot/UefiBootInfoBuilder.cs`

Сделано:
- вместо одного `APP.ELF` добавлены три источника:
  - `HELLO.ELF`
  - `ABIINFO.ELF`
  - `MARKER.ELF`
- в `BootInfo` добавлены отдельные поля `Image`/`Size` для каждого приложения;
- `UefiBootInfoBuilder` выставляет `PlatformCapabilities.ExternalElf`, если найден хотя бы один внешний ELF.

### 2. Rich App ABI (service table)

Обновлены:
- `OS_0.1/src/Kernel/Process/AppServiceTable.cs`
- `OS_0.1/src/Kernel/Process/AppServiceBuilder.cs`
- `OS_0.1/src/Kernel/Process/ProcessValidation.cs`

Добавлены/активированы сервисы:
- `WriteString`
- `WriteUInt`
- `WriteHex`
- `GetAbiVersion`
- `Exit`

Сделано:
- `Exit(code)` стал рабочим формальным путём завершения;
- `TryConsumeExit(...)` используется после возврата из jump-stub;
- валидация process path теперь проверяет все service pointers.

### 3. Sequential app runner и источник exit-кода

Обновлены:
- `OS_0.1/src/Kernel/Elf/ElfAppContract.cs`
- `OS_0.1/src/Kernel/Elf/ElfValidation.cs`

Сделано:
- добавлен последовательный запуск трёх приложений:
  - `HELLO.ELF` (ожидаемый exit: `10`)
  - `ABIINFO.ELF` (ожидаемый exit: `11`)
  - `MARKER.ELF` (ожидаемый exit: `12`)
- в лог добавлен `exit source = service|return`;
- добавлена проверка ABI версии через `processImage.AbiVersion`;
- для `MARKER.ELF` сохранена проверка marker (`0x12345678`).

### 4. Освобождение маппингов между app-runs

Обновлён:
- `OS_0.1/src/Kernel/Elf/ElfValidation.cs`

Сделано:
- после каждого запуска выполняется cleanup image/stack mappings;
- это устранило падение второго приложения на `segment_page_map_failed` при повторном использовании того же VA-диапазона.

### 5. Генерация трёх внешних ELF в build pipeline

Обновлён:
- `run_build.ps1`

Сделано:
- добавлены генераторы:
  - `New-HelloElfImage`
  - `New-AbiInfoElfImage`
  - `New-MarkerElfImage`
- в ESP теперь записываются:
  - `EFI/BOOT/HELLO.ELF`
  - `EFI/BOOT/ABIINFO.ELF`
  - `EFI/BOOT/MARKER.ELF`
- оставлено восстановление заголовка окна после завершения QEMU.

## Проверка

Проверено:
1. `./run_build.ps1 -NoRun` — успешно.
2. `./run_build.ps1` — успешно.

Ключевые строки COM1:
- `[info] app run start: HELLO.ELF`
- `hello from hello app`
- `[info] process exit code = 10`
- `[info] exit source = service`
- `[info] app run start: ABIINFO.ELF`
- `abi info app`
- `abi=1`
- `[info] process exit code = 11`
- `[info] exit source = service`
- `[info] app run start: MARKER.ELF`
- `marker app`
- `marker=0x...`
- `[info] process exit code = 12`
- `[info] process wrote marker = 0x12345678`
- `[info] exit source = service`

## Итог

Step 15 закрыт: внешний ELF pipeline теперь поддерживает несколько приложений, расширенный host ABI и формальный service-based exit path в одном запуске системы.
