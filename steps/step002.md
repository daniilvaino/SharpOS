# step002

Дата: 2026-03-31

## Цель шага

Сформировать жесткую границу между миром загрузки (UEFI) и миром ядра:
- передавать в систему не сырые UEFI-указатели, а собственный `BootInfo`;
- проектировать поведение через capabilities;
- ввести системную диагностику (`Log`, `SystemBanner`, `Panic`) как first-class API.

Идея шага: добавить не "новые фичи", а правильный контракт старта.

## Что и как сделано

### 1. Введен стартовый контракт `BootInfo`

Добавлены:
- `src/Boot/BootInfo.cs`
- `src/Boot/BootCapabilities.cs`

Что фиксирует `BootInfo`:
- режим загрузки (`BootMode`, сейчас `UEFI`);
- firmware revision/vendor;
- capability-флаги платформы;
- availability-флаги для будущих подсистем (`MemoryMapAvailable`, `GraphicsAvailable`);
- opaque hooks платформы:
  - `WriteChar`
  - `Shutdown`

Ключевой смысл:
- `Kernel` получает только данные и возможности своей модели;
- прямой `EFI_SYSTEM_TABLE*` больше не является системным API.

### 2. Построение `BootInfo` из UEFI

Добавлен:
- `src/Boot/UefiBootInfoBuilder.cs`

Сделано:
- из `EFI_SYSTEM_TABLE*` собирается `BootInfo`;
- вычисляются capabilities (на текущем этапе:
  - `TextOutput`, если доступен `ConOut`;
  - `Shutdown`, если доступны runtime services);
- инициализируется bridge для platform hooks (`UefiPlatformBridge`), который скрывает прямые вызовы UEFI за делегатами.

### 3. Обновлен entry pipeline

Изменен:
- `src/Boot/EfiEntry.cs`

Новый путь старта:

`EfiMain -> Boot.Entry -> UefiBootInfoBuilder.Build -> Platform.Init(BootInfo) -> KernelMain.Start(BootInfo)`

Этим:
- `EfiMain` остается тонким;
- загрузочный код конвертирует внешний мир в внутренний контракт;
- ядро не зависит от UEFI-типов напрямую.

### 4. Введена capability boundary в `Platform`

Изменен:
- `src/Hal/Platform.cs`

Что сделано:
- `Platform.Init` теперь принимает `BootInfo`, а не `BootContext`;
- добавлены:
  - `Capabilities`
  - `HasCapability(...)`
  - `GetBootInfo()`
- `WriteChar` работает только при capability `TextOutput`;
- `Shutdown` работает только при capability `Shutdown`, иначе fallback в `Halt`.

Это переводит систему с модели "UEFI умеет X" на модель "платформа декларирует возможности X".

### 5. Добавлен системный логгер

Добавлен:
- `src/Hal/Log.cs`

Реализовано:
- `LogLevel`: `Trace`, `Info`, `Warn`, `Error`, `Panic`;
- `Log.Write(level, message)` с префиксами:
  - `[info] ...`
  - `[warn] ...`
  - `[PANIC] ...`

На текущем этапе логгер пишет через `Console`, но интерфейс уже отделен от конкретного устройства вывода.

### 6. Panic с единым системным форматом

Изменен:
- `src/Kernel/Panic.cs`

Поведение:
- форматирует аварийный вывод через `Log`:
  - `[PANIC] <message>`
  - `System halted.`
- после этого управление не возвращается (`Halt` + бесконечный loop).

### 7. Добавлен `SystemBanner`

Добавлен:
- `src/Kernel/SystemBanner.cs`

Печатает на старте:
- имя и версию системы (`SharpOS 0.1`);
- boot mode;
- firmware vendor/revision;
- capabilities.

Это служит проверкой:
- корректности `BootInfo`;
- корректности логгера;
- корректности capability-контракта.

### 8. Обновлены `Kernel` и `DemoApp`

Изменены:
- `src/Kernel/Kernel.cs`
- `src/TestApp/DemoApp.cs`

Что сделано:
- `KernelMain.Start(BootInfo)`:
  - печатает banner;
  - пишет `[info] kernel start`;
  - пишет `[warn] memory map not implemented` при отсутствии capability `MemoryMap`;
  - запускает `DemoApp`.
- `DemoApp` переведен на `Log` для служебных сообщений (`demo start` / `demo done`), сохранив вычисление `fib`.

### 9. Стабилизация сборки

Контекст:
- при развитии шага всплывали линкерные проблемы (`__security_cookie`, затем `memcpy`).

Итоговое состояние:
- `BootInfo` оставлен pointer-based для vendor-строки (без копирования в fixed buffer);
- линковка стабилизирована через `libcmt.lib` в `OS_0.1.csproj` (фикс, введенный на предыдущем шаге, сохранен).

## Проверка результата

Проверено двумя сценариями:

1. `.\run_build.ps1 -NoRun`
- сборка проходит;
- `BOOTX64.EFI` формируется.

2. `.\run_build.ps1`
- QEMU стартует и грузит `BOOTX64.EFI`;
- в COM1 выводится:
  - `[info] SharpOS 0.1`
  - `[info] boot: UEFI`
  - `[info] fw: EDK II / rev 65536`
  - `[info] caps: TextOutput Shutdown`
  - `[info] kernel start`
  - `[warn] memory map not implemented`
  - `[info] demo start`
  - `fib(...)`
  - `[info] demo done`

## Что НЕ делалось намеренно

На этом шаге специально не трогались:
- allocator / memory manager;
- paging / VM;
- scheduler / async;
- filesystem;
- NuGet/runtime-level расширения.

Причина: сначала зафиксирован контракт старта и граница слоев.

## Итог шага

Система перешла от "UEFI-driven кода" к "контрактно-ориентированному старту":
- UEFI-знания локализованы в `Boot`;
- ядро потребляет `BootInfo` и capabilities;
- диагностика стала системной (`Log`, `SystemBanner`, `Panic`);
- заложена правильная база для следующих шагов (memory map, timer, graphics) без архитектурного сноса.
