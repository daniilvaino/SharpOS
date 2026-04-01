# step014

Дата: 2026-04-01

## Цель

Добавить минимальный host ABI для внешнего ELF-приложения:
- startup context + pointer на таблицу сервисов;
- первый сервисный вызов из app в kernel (`WriteString`);
- сохранить существующий `exit code` и `marker` validation path.

## Что реализовано

### 1. Введена `AppServiceTable`

Добавлены файлы:
- `OS_0.1/src/Kernel/Process/AppServiceTable.cs`
- `OS_0.1/src/Kernel/Process/AppServiceBuilder.cs`

Реализовано:
- `AppServiceTable`:
  - `AbiVersion`
  - `WriteStringAddress`
  - `ExitAddress`
- `AppServiceBuilder.TryBuild(...)`:
  - заполняет таблицу сервисов;
  - размещает её в process stack area;
  - экспортирует адреса сервисов как function pointers.

Сервисы:
- `WriteString(ulong ptr)` — печатает null-terminated ASCII строку в текущую консоль;
- `Exit(int code)` — резерв под явный exit path (уже заведён и читается через `TryConsumeExit`).

### 2. Startup ABI расширен service pointer

Обновлен файл:
- `OS_0.1/src/Kernel/Process/ProcessStartupBlock.cs`

Добавлено:
- поле `ServiceTableAddress`;
- флаг `FlagServiceTableAddressIsPhysical`.

### 3. Process startup builder обновлён

Обновлены файлы:
- `OS_0.1/src/Kernel/Process/ProcessImage.cs`
- `OS_0.1/src/Kernel/Process/ProcessStartupBuilder.cs`

Изменения:
- в `ProcessImage` добавлены:
  - `ServiceTableVirtual`
  - `ServiceTablePhysical`
- `ProcessStartupBuilder` теперь:
  - выделяет layout в stack area для `AppServiceTable` + `ProcessStartupBlock`;
  - записывает `ServiceTableAddress` в startup block;
  - выставляет ABI flags `MarkerAddressIsPhysical | ServiceTableAddressIsPhysical`.

### 4. Process validation/diagnostics расширены

Обновлены файлы:
- `OS_0.1/src/Kernel/Process/ProcessValidation.cs`
- `OS_0.1/src/Kernel/Process/ProcessDiagnostics.cs`

Проверяется:
- service table адреса и границы;
- `AbiVersion` service table;
- `WriteStringAddress` и `ExitAddress` не null;
- согласованность `StartupBlock.ServiceTableAddress`.

Логи включают:
- `service table ready: 0x...`

### 5. ELF pipeline интеграция

Обновлен файл:
- `OS_0.1/src/Kernel/Elf/ElfValidation.cs`

После возврата из app:
- `exit code` берётся из return path;
- при наличии service-`Exit` запроса код может быть переопределён через `AppServiceBuilder.TryConsumeExit(...)`.

### 6. APP.ELF генератор обновлён под новый ABI

Обновлен файл:
- `run_build.ps1`

Внешний `APP.ELF` теперь:
- читает `ServiceTableAddress` из `StartupBlock` (`RDI`);
- вызывает `WriteString("hello from app\n")`;
- пишет marker `0x12345678`;
- возвращает `42`.

## Техническое исправление по ходу

Обновлен файл:
- `OS_0.1/src/Boot/MinimalRuntime.cs`

Добавлен `RuntimeExport("memset")` для устранения link error `LNK2001 memset`.

## Проверка

Проверено:
1. `.\run_build.ps1 -NoRun` — успешно.
2. `.\run_build.ps1` — успешно.

Ключевые строки COM1:
- `[info] open elf file ok`
- `[info] read elf bytes = 4096`
- `[info] process build start`
- `[info] service table ready: 0x...`
- `[info] jump start`
- `hello from app`
- `[info] process returned`
- `[info] process exit code = 42`
- `[info] process wrote marker = 0x12345678`

## Итог

Step 14 закрыт:
- внешний ELF стал клиентом минимального kernel-host ABI через таблицу сервисов;
- подтвержден первый реальный сервисный вызов из приложения в ядро (`WriteString`).
