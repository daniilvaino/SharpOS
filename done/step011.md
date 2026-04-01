# step011

Дата: 2026-04-01

## Цель

Сделать первый execution milestone:
- собрать `ProcessImage` поверх загруженного ELF;
- замапить отдельный stack;
- провалидировать entry/stack/image;
- выполнить controlled jump в entrypoint;
- поймать возврат и зафиксировать наблюдаемый эффект.

## Что реализовано

### 1. Process image слой

Добавлены файлы:
- `OS_0.1/src/Kernel/Process/ProcessImage.cs`
- `OS_0.1/src/Kernel/Process/ProcessImageBuilder.cs`
- `OS_0.1/src/Kernel/Process/ProcessValidation.cs`
- `OS_0.1/src/Kernel/Process/ProcessDiagnostics.cs`

Реализовано:
- структура `ProcessImage` с полями:
  - `EntryPoint`, `ImageStart`, `ImageEnd`
  - `StackBase`, `StackTop`, `StackPages`
  - `MappedImagePages`, `MappedStackPages`
  - `EntryPointPhysical`, `StackTopPhysical` (для controlled jump в текущем execution world);
- `ProcessImageBuilder.TryBuild(...)`:
  - фиксированный stack (`8` страниц),
  - map stack pages через `Pager`,
  - zero stack pages,
  - проверка `image/stack` overlap,
  - резолв `EntryPointPhysical` и `StackTopPhysical` через `Pager.TryQuery`.

### 2. Pre-jump process validation

`ProcessValidation.Run(...)` проверяет:
- entrypoint внутри executable `PT_LOAD` сегмента;
- entrypoint mapped и не `NoExecute`;
- stack range корректен и aligned;
- stack pages mapped + writable;
- image и stack не пересекаются.

### 3. Controlled jump stub

Добавлен файл:
- `OS_0.1/src/Kernel/Exec/JumpStub.cs`

Реализовано:
- маленький машинный stub (x64):
  - сохранить старый `rsp`,
  - переключить `rsp` на `stackTop`,
  - `call entry`,
  - восстановить `rsp`,
  - `ret`.
- вызов через `delegate* unmanaged<ulong, ulong, void>`.

### 4. Smoke ELF upgraded для execution-check

Обновлен файл:
- `OS_0.1/src/Kernel/Elf/ElfSmokeImage.cs`

Изменения:
- в text-сегмент добавлен реальный код entrypoint:
  - `mov eax, 0x12345678`
  - `mov [rip+disp32], eax`
  - `ret`
- добавлен marker slot в image:
  - `MarkerVirtualAddress`
  - `MarkerExpectedValue`.

### 5. Интеграция в pipeline

Обновлен файл:
- `OS_0.1/src/Kernel/Elf/ElfValidation.cs`

После `ElfLoadValidation` теперь:
1. `ProcessImageBuilder.TryBuild(...)`
2. `ProcessValidation.Run(...)`
3. `ProcessDiagnostics.DumpSummary(...)`
4. `JumpStub.Run(entryPhysical, stackTopPhysical)`
5. лог `process returned`
6. проверка marker value через mapped memory
7. `Platform.Shutdown()` как policy после возврата.

## Проверка

Проверено:
1. `.\run_build.ps1 -NoRun` — успешно.
2. `.\run_build.ps1` — успешно.

Ключевые строки COM1:
- `[info] process build start`
- `[info] process validation ok`
- `[info] stack map: 0x00000000007F8000..0x0000000000800000`
- `[info] entry ready: 0x0000000000400010`
- `[info] jump start`
- `[info] process returned`
- `[info] process wrote marker = 0x12345678`
- `[info] elf validation done`

## Итог

Step 11 закрыт:
- ELF-образ не только загружается, но и реально исполняется через controlled jump с отдельным stack;
- возврат из entrypoint корректно обрабатывается;
- выполнение подтверждается наблюдаемым marker-write действием.
