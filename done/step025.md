# step025

Дата: 2026-04-04  
Статус: завершён

## Цель

Разделить UI-вывод и debug-логи на уровне API/дисциплины использования, оставив физический backend прежним (`UEFI ConOut`).

## Что сделано

### 1) Добавлены два канала вывода

Добавлены:

- `OS_0.1/src/Hal/DebugLog.cs`
- `OS_0.1/src/Hal/UiText.cs`

Смысл:

- `DebugLog` -> диагностический поток (`[info]/[warn]/[trace]`),
- `UiText` -> пользовательский текст без лог-префиксов.

Физически оба пока пишут в тот же backend (`Platform/ConOut`), но семантика каналов разделена.

### 2) Старый `Log` оставлен как совместимый wrapper

Обновлён:

- `OS_0.1/src/Hal/Log.cs`

Сделано:

- `Log` теперь thin-wrapper поверх `DebugLog`, чтобы не ломать существующий код.

### 3) App service `WriteString` переведён на `UiText`

Обновлён:

- `OS_0.1/src/Kernel/Process/AppServiceBuilder.cs`

Сделано:

- сервисный вывод app (`WriteString/WriteUInt/WriteHex`) теперь идёт через `UiText`, а не через общий `Console`.
- nested-run diagnostics оформлены как framed debug-блоки:
  - `---- child start ----`
  - `---- child end: exit=... ----`

### 4) Kernel diagnostics переведены на `DebugLog` в ключевых местах

Обновлены:

- `OS_0.1/src/Kernel/Elf/ElfValidation.cs`
- `OS_0.1/src/Kernel/File/FileDiagnostics.cs`

Сделано:

- диагностические сообщения и summary-печать идут через `DebugLog` + `UiText` (как transport для текста внутри debug-line).

### 5) Launcher/UI path переработан в block rendering

Обновлён:

- `apps/HelloSharpFs/Program.cs`

Сделано:

- launcher печатает целыми UI-блоками (frame), а не рваными строками;
- добавлен redraw-подход (`needsRedraw`);
- результат запуска app печатается отдельным result-блоком:
  - `name/status/exit`;
- UI по умолчанию показывает только `.ELF` файлы;
  - `.abi` и `BOOTX64.EFI` скрыты на уровне представления;
- запуск из launcher идёт через `TryRunApp(path)` (auto manifest/fallback из step024), без ручного роутинга по имени.

## Проверка

Выполнено:

1. `./build_app_freestanding_wsl.ps1`  
   - `HELLOCS.ELF` собран;
   - `HELLOCS.ELF.abi` собран и скопирован в ESP.

2. `./run_build.ps1 -NoRun`  
   - kernel успешно собирается;
   - образы и `.abi` подготавливаются без ошибок.

Примечание:

- полный runtime-прогон launcher в этом шаге интерактивный (ждёт клавиатуру), поэтому подтверждение UX-блоков и поведения после nested-run проверяется руками в QEMU.

## Итог

Step 25 разделил output surface на `DebugLog` и `UiText`, убрал смешение launcher/app UI с системными логами на уровне API-дисциплины и сделал launcher-поток заметно читаемее без смены физического backend вывода.
