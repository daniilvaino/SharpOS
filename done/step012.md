# step012

Дата: 2026-04-01

## Цель

Сформировать первый формальный process ABI:
- `StartupBlock` как явный контракт старта;
- передача `StartupBlock*` в `RDI`;
- возврат `exit code` через `RAX`;
- проверка marker/exit по формальному пути после возврата.

## Что реализовано

### 1. Startup contract

Добавлены файлы:
- `OS_0.1/src/Kernel/Process/ProcessStartupBlock.cs`
- `OS_0.1/src/Kernel/Process/ProcessStartupBuilder.cs`

Реализовано:
- `ProcessStartupBlock` с полями:
  - `AbiVersion`, `Flags`
  - `ImageBase`, `ImageEnd`, `EntryPoint`
  - `StackBase`, `StackTop`
  - `MarkerAddress`
  - `ExitCode`, `Reserved`
- `ProcessStartupBuilder.TryBuild(...)`:
  - размещает block на вершине mapped stack;
  - вычисляет entry stack top ниже block (с выравниванием);
  - заполняет startup поля;
  - резолвит `StartupBlockPhysical`, `StackTopPhysical`, `EntryPointPhysical`.

Примечание:
- в текущем execution world marker передаётся как physical address;
- это явно помечено флагом `FlagMarkerAddressIsPhysical`.

### 2. Process image расширен до ABI-state

Обновлен файл:
- `OS_0.1/src/Kernel/Process/ProcessImage.cs`

Добавлено:
- `AbiVersion`, `AbiFlags`;
- `StackMappedTop`;
- `StartupBlockVirtual`, `StartupBlockPhysical`;
- `ExitCode`.

### 3. Process builder/validation/diagnostics обновлены

Обновлены файлы:
- `OS_0.1/src/Kernel/Process/ProcessImageBuilder.cs`
- `OS_0.1/src/Kernel/Process/ProcessValidation.cs`
- `OS_0.1/src/Kernel/Process/ProcessDiagnostics.cs`

Изменения:
- `ProcessImageBuilder.TryBuild(...)` теперь принимает marker VA и вызывает `ProcessStartupBuilder`;
- `ProcessValidation` проверяет:
  - корректность stack range и выравнивание;
  - отсутствие overlap image/stack;
  - entrypoint mapped + executable;
  - startup block внутри writable stack;
  - согласованность ключевых полей startup block;
- diagnostics выводит:
  - stack map;
  - startup block address;
  - entry + entry stack top;
  - ABI version/flags.

### 4. JumpStub переведен на entry ABI + exit ABI

Обновлен файл:
- `OS_0.1/src/Kernel/Exec/JumpStub.cs`

Изменения:
- новый вызов:
  - `Run(entryPhysical, stackTopPhysical, startupBlockPhysical, out exitCode)`
- ABI на входе:
  - `RCX = entry`
  - `RDX = stackTop`
  - `R8 = startupBlock`
- stub:
  - переносит `startupBlock` в `RDI` перед `call entry`;
  - сохраняет/восстанавливает non-volatile `RDI`;
  - возвращает `RAX` как `exitCode`.

### 5. Smoke ELF обновлен под новый ABI

Обновлен файл:
- `OS_0.1/src/Kernel/Elf/ElfSmokeImage.cs`

Entry код теперь:
- читает `MarkerAddress` из `StartupBlock` по `RDI+0x30`;
- пишет `0x12345678` по адресу из блока;
- возвращает `42` через `EAX`.

Добавлены константы:
- `MarkerExpectedValue = 0x12345678`
- `ExitCodeExpected = 42`

### 6. Pipeline интеграция

Обновлен файл:
- `OS_0.1/src/Kernel/Elf/ElfValidation.cs`

После load/validation теперь:
1. строится `ProcessImage` с `StartupBlock`;
2. выполняется `ProcessValidation`;
3. выполняется `JumpStub` с передачей `StartupBlockPhysical`;
4. логируется `process exit code`;
5. проверяется `exit code == 42`;
6. проверяется marker.

## Проверка

Проверено:
1. `.\run_build.ps1 -NoRun` — успешно.
2. `.\run_build.ps1` — успешно.

Ключевые строки COM1:
- `[info] process build start`
- `[info] startup block ready: 0x00000000007FFFC0`
- `[info] entry ready: 0x0000000000400010`
- `[info] process validation ok`
- `[info] jump start`
- `[info] process returned`
- `[info] process exit code = 42`
- `[info] process wrote marker = 0x12345678`

## Итог

Step 12 закрыт:
- переход от “просто jump в entry” к формальному process startup/exit ABI;
- приложение получает startup context через `RDI`;
- результат выполнения возвращается по формальному пути (`RAX exit code`) и валидируется ядром.
