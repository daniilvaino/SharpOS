# step001

Дата: 2026-03-31

## Цель шага

Перевести проект из состояния "вся логика в одном файле и в `EfiMain`" в базовый системный каркас, чтобы дальше развивать ядро без постоянного переписывания фундамента.

Целевые результаты шага:
- тонкий UEFI entrypoint;
- изоляция UEFI-деталей в отдельном слое;
- собственный platform API;
- отдельный `Kernel` и отдельный `TestApp`.

## Что и как сделано

### 1. Приведение проекта к одному рабочему модулю

- Проект очищен от лишних sample-частей и оставлен единый рабочий модуль `OS_0.1`.
- Каталог проекта перенесен в `C:\work\OS\OS_0.1`.
- Исходник переименован в `main.cs`.
- Решение приведено к новому layout, чтобы не тянуть старые пути.

### 2. Закладка архитектурных слоев

Создана физическая структура:
- `src/Boot`
- `src/Hal`
- `src/Kernel`
- `src/TestApp`

Разделение ответственности:
- `Boot`: вход из UEFI + low-level glue + UEFI-типы.
- `Hal`: минимальные платформенные примитивы (`write`, `shutdown`, `halt`).
- `Kernel`: логика ядра без прямой зависимости на UEFI.
- `TestApp`: демо-логика поверх API системы.

### 3. Детализация Boot-слоя (что вынесено по файлам)

Добавлены и наполнены:
- `src/Boot/MinimalRuntime.cs`
- `src/Boot/UefiTypes.cs`
- `src/Boot/BootContext.cs`
- `src/Boot/UefiConsole.cs`
- `src/Boot/EfiEntry.cs`

Что сделано внутри:
- `MinimalRuntime.cs`: вынесены минимальные `System`-типы и runtime-заглушки для режима `NoStdLib`.
- `UefiTypes.cs`: собраны EFI-структуры и ABI-сигнатуры (`EFI_SYSTEM_TABLE`, `EFI_RUNTIME_SERVICES`, и т.д.).
- `BootContext.cs`: введен контейнер загрузочного контекста, чтобы не растаскивать сырой `EFI_SYSTEM_TABLE*` по коду.
- `UefiConsole.cs`: сделан UEFI-адаптер вывода (`Write`, `WriteChar`), изолирующий прямой вызов `ConOut->OutputString`.
- `EfiEntry.cs`: `EfiMain` стал тонким входом (получить аргументы -> собрать context -> передать управление дальше).

### 4. Реализация стартового pipeline

Зафиксирован единый путь старта:

`EfiMain -> Boot.Entry -> Platform.Init -> KernelMain.Start -> DemoApp.Run`

Этим убрана прикладная логика из `EfiMain`.

### 5. Реализация HAL и системного Console API

Добавлены:
- `src/Hal/Platform.cs`
- `src/Hal/Console.cs`

Реализовано:
- `Platform.Init(BootContext)` хранит инициализированный платформенный контекст.
- `Platform.Write/WriteLine/WriteChar` дают единый интерфейс вывода.
- `Platform.Shutdown()` делает завершение через UEFI runtime services.
- `Platform.Halt()` делает fallback halt loop.
- `Console.WriteInt(int)` реализован вручную (без зависимостей на обычные .NET-форматтеры), включая edge-case `int.MinValue`.

### 6. Реализация Kernel и аварийного пути

Добавлены:
- `src/Kernel/Kernel.cs`
- `src/Kernel/Panic.cs`

Что сделано:
- `KernelMain.Start()` — единая точка старта ядра (`kernel start`).
- `Panic.Fail(string)` — аварийный путь (`PANIC: ...` + остановка системы).

### 7. Тестовое приложение поверх API системы

Добавлен:
- `src/TestApp/DemoApp.cs`

Что делает:
- печатает `demo start`;
- считает и печатает `fib(n)` для нескольких значений через системный `Console`;
- печатает `demo done`.

Важно: `DemoApp` не обращается к UEFI напрямую.

### 8. Обновление сборочного контура

Обновлено:
- `run_build.ps1`:
  - сборка `OS_0.1`;
  - подготовка `BOOTX64.EFI` в `.qemu/esp/EFI/BOOT`;
  - запуск QEMU с OVMF и COM1 в терминал.
- `build.cmd`:
  - ручная компиляция теперь берет файлы из `src/Boot`, `src/Hal`, `src/Kernel`, `src/TestApp`.
- `OS_0.1.csproj`:
  - добавлен `libcmt.lib` для стабильной линковки NativeAOT (`__security_cookie` fix).

## Проверка результата

Проверено запуском через `run_build.ps1`:
- сборка проходит;
- QEMU загружает `BOOTX64.EFI`;
- в консоль выводится:
  - `kernel start`
  - `demo start`
  - `fib(0)=0 ... fib(7)=13`
  - `demo done`
- после выполнения система завершается через `Shutdown`.

## Итог шага

Получен рабочий системный каркас, а не "чуть более умный hello world":
- `EfiMain` стал тонким;
- UEFI изолирован в `Boot`;
- у системы есть собственный `Platform` и `Console`;
- есть `Kernel.Start()` и `Panic.Fail()`;
- есть демонстрационная нагрузка, не завязанная на прямые UEFI-вызовы.
