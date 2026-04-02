# step019

Дата: 2026-04-01

## Цель

Прокинуть UEFI keyboard input в SharpOS через существующую границу слоёв (`Boot -> Hal -> Kernel`) без протаскивания UEFI keyboard-протоколов в ядро.

## Что реализовано

### 1. Boot-side keyboard bridge

Добавлено:
- `OS_0.1/src/Boot/UefiKeyboard.cs`

Сделано:
- добавлены UEFI-типы клавиатуры:
  - `EFI_INPUT_KEY`
  - `EFI_SIMPLE_TEXT_INPUT_PROTOCOL`
- в `EFI_SYSTEM_TABLE` поле `ConIn` теперь типизировано как `EFI_SIMPLE_TEXT_INPUT_PROTOCOL*`;
- реализован polling bridge `TryReadKey(...)` с разделением статусов:
  - `Ok`
  - `NotReady`
  - `Unsupported`
  - `InvalidParameter`
  - `DeviceError`

### 2. Capability + callback в BootInfo

Обновлено:
- `OS_0.1/src/Boot/BootCapabilities.cs`
- `OS_0.1/src/Boot/BootInfo.cs`
- `OS_0.1/src/Boot/UefiBootInfoBuilder.cs`

Сделано:
- добавлена capability `KeyboardInput`;
- в `BootInfo` добавлен callback:
  - `KeyboardTryReadKey(ushort* unicodeChar, ushort* scanCode)`;
- `UefiPlatformBridge` теперь:
  - проверяет наличие keyboard input;
  - пробрасывает `KeyboardTryReadKey` в `BootInfo`.

### 3. HAL abstraction (без UEFI-типов в ядре)

Добавлено:
- `OS_0.1/src/Hal/KeyboardReadStatus.cs`

Обновлено:
- `OS_0.1/src/Hal/Platform.cs`

Сделано:
- добавлен `Platform.TryReadKey(out ushort unicodeChar, out ushort scanCode)`;
- UEFI/boot-статусы на входе маппятся в HAL-статусы:
  - `KeyAvailable`
  - `NoKey`
  - `Unsupported`
  - `DeviceError`

### 4. Kernel input API + diagnostics demo

Добавлено:
- `OS_0.1/src/Kernel/Input/KeyInfo.cs`
- `OS_0.1/src/Kernel/Input/KeyReadStatus.cs`
- `OS_0.1/src/Kernel/Input/Keyboard.cs`
- `OS_0.1/src/Kernel/Input/InputDiagnostics.cs`

Обновлено:
- `OS_0.1/src/Kernel/Kernel.cs`
- `OS_0.1/src/Kernel/SystemBanner.cs`

Сделано:
- введён kernel-side input facade:
  - `Keyboard.IsAvailable`
  - `Keyboard.TryReadKey(out KeyInfo key)`
  - `Keyboard.TryReadChar(out char value)`
- добавлен demo loop:
  - лог `keyboard init ok`
  - лог `press keys, ESC to continue`
  - вывод каждой клавиши в формате `char/scan`
  - выход по `ESC` (Unicode `0x001B` или scan code `0x0017`)
- capability `KeyboardInput` добавлена в banner.

## Проверка

Проверено:
- `./run_build.ps1 -NoRun` — успешно, проект собирается после всех изменений.

Замечание:
- полный интерактивный runtime-прогон клавиатурного цикла требует ручного ввода в QEMU-сессии (ESC для выхода из demo).

## Итог

Step 19 закрывает базовый UEFI keyboard bridge: ядро получило собственный минимальный input API и polling-модель ввода без прямой зависимости от UEFI keyboard protocol.
