# step023

Дата: 2026-04-02  
Статус: завершён

## Цель

Реализовать вложенный запуск `RunApp(path)` из приложения так, чтобы после завершения дочернего ELF родительское приложение продолжало выполнение с восстановленным процессным контекстом и маппингами.

## Что сделано

### 1. Введены базовые сущности процессного контекста

Добавлены:

- `OS_0.1/src/Kernel/Process/ProcessState.cs`
- `OS_0.1/src/Kernel/Process/ProcessContext.cs`
- `OS_0.1/src/Kernel/Process/MappingContext.cs`
- `OS_0.1/src/Kernel/Process/ProcessManager.cs`

Реализовано:

- хранение текущего процесса (`ProcessImage` + `ElfLoadedImage`);
- snapshot текущих image/stack mappings через `Pager.TryQuery`;
- временное снятие mappings родителя перед запуском child;
- восстановление mappings родителя после child;
- освобождение snapshot-буферов (`KernelHeap`).

Ограничение текущей реализации: вложенность `RunApp` поддержана в модели depth=1 (parent->child). Повторный nested-запуск из child возвращает `Unsupported`.

### 2. Интеграция в жизненный цикл app execution

Обновлён:

- `OS_0.1/src/Kernel/Elf/ElfValidation.cs`

Сделано:

- перед `JumpStub.Run(...)` для top-level app фиксируется `ProcessManager.SetCurrent(...)`;
- после выхода из app (в `finally`) выполняется `ProcessManager.ClearCurrent()`.

Это даёт kernel-сервисам доступ к актуальному parent-context во время `RunApp(...)`.

### 3. Реальный nested-path в AppService `RunApp`

Обновлён:

- `OS_0.1/src/Kernel/Process/AppServiceBuilder.cs`

Сделано:

- `RunExternalApp(...)` теперь работает через:
  - `TrySuspendCurrentForNested(...)`,
  - запуск child ELF,
  - cleanup child mappings,
  - `TryRestoreAfterNested(...)` в `finally`;
- добавлена диагностика nested execution:
  - `nested app start`
  - `nested app exit code = ...`
  - `parent context restored`
  - warning на restore-failure.

Гарантия шага: restore parent context выполняется в `finally`, даже если child path завершается ошибкой.

### 4. Практический smoke-test nested запуска из C# app

Обновлён:

- `apps/HelloSharpFs/Program.cs`

Сделано:

- добавлен детерминированный вызов:
  - `TryRunApp("\\EFI\\BOOT\\HELLO.ELF", AbiV1, WindowsX64, out exitCode)`
- вывод результата в app-лог:
  - `nested_status=... nested_exit=...`

Это временный проверочный path, пока интерактивный launcher-цикл в app выключен.

## Проверка

Выполнено:

1. `./build_app_freestanding_wsl.ps1`  
2. `./run_build.ps1`

Подтверждено в runtime-логах:

- внутри `HELLOCS.ELF` запущен дочерний `HELLO.ELF`;
- дочерний app выполнился и завершился с `exit code = 10`;
- kernel залогировал `parent context restored`;
- родительский app продолжил выполнение и вывел:
  - `nested_status=0 nested_exit=10`;
- общий batch-run завершился успешно: `passed: 4`, `failed: 0`.

## Итог

Step 23 закрыл execution-модель `parent app -> child app -> return to parent` с гарантированным restore mappings и контролируемым возвратом exit-кода в родительское приложение.

## Важная фиксация по ABI (добавлено)

В процессе nested-запуска из `HELLOCS` выявлен практический нюанс:

- в системе сейчас одновременно используются разные ABI app-вызова:
  - `HELLO/ABIINFO/MARKER` -> `AbiV1 + WindowsX64`
  - `HELLOCS` -> `AbiV2 + SystemV`

Из-за этого попытка запускать `HELLOCS.ELF` через дефолтный `TryRunApp(path)` (который идёт как `v1 + WindowsX64`) приводит к неверному вызову сервисов в дочернем app и мусорному выводу.

Временное решение в launcher-app:

- перед `TryRunApp` выбирается `AppAbiVersion/ServiceAbi` по имени файла;
- для `HELLOCS.ELF` принудительно используется `v2 + SystemV`;
- для legacy ELF остаётся `v1 + WindowsX64`.

Это **временный роутер ABI**, а не целевая архитектура.

Зафиксированный техдолг:

1. Либо унифицировать внешние apps на одном ABI (предпочтительно `v2 + SystemV`).
2. Либо добавить явный ABI-манифест/метку app-формата и выбирать ABI не по имени файла.
