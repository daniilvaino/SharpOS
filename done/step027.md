# step027

Дата: 2026-04-06  
Статус: завершён

## Цель

Запустить вложенные приложения (launcher → child app) и добиться стабильной работы на реальном железе (INSYDE firmware). Для воспроизведения железных крашей на QEMU — построить кастомный OVMF с той же политикой NX, что применяет INSYDE.

---

## Проблемы и решения

### 1. Вложенный запуск: JumpStub блокировался при вызове из сервисного потока

**Файл:** `OS_0.1/src/Kernel/Exec/JumpStub.cs`

**Проблема:** `EnsureInitialized()` содержал:
```csharp
if (Pager.IsPagerRootActive()) return false;
```
При вызове из сервисного thunk-а (контекст вложенного приложения) pager CR3 уже активен → возврат `false` → вложенный app не запускается.

**Решение:** Проверка не нужна — стаб корректно сохраняет и восстанавливает CR3. Убрана.

---

### 2. Дочернее приложение: стек по тому же VA что и у родителя

**Файл:** `OS_0.1/src/Kernel/Process/ProcessImageBuilder.cs`

**Проблема:** `StackBase`/`StackTop` вычислялись от константы `DefaultStackMappedTop` вместо переданного параметра `stackMappedTop`:
```csharp
// было:
processImage.StackBase = DefaultStackMappedTop - stackSize;
```
Дочерний стек оказывался по тому же VA что и родительский → `TryPrepareStackVirtualSpan` размапила активный стек родителя → triple fault.

**Решение:**
```csharp
processImage.StackBase = stackMappedTop - stackSize;
processImage.StackTop  = stackMappedTop;
```

---

### 3. Восстановление контекста родителя падало после выхода дочернего приложения

**Файл:** `OS_0.1/src/Kernel/Process/ProcessManager.cs` → `TryRestoreSnapshot`

**Проблема:** `TrySyncKernelLowMappings` (вызывается при запуске child) добавляла identity-маппинг страницы 0x402000 в pager. Снапшот родителя хотел замапить туда свою страницу (другой физический адрес). `Pager.Map` отказывал на уже замапленный адрес → восстановление падало с ошибкой.

**Решение:** Перед `Map` проверять: если в pager уже есть маппинг на другой физический адрес — сначала `Unmap`, затем `Map`:
```csharp
if (Pager.TryQuery(entry.VirtualAddress, out ulong existingPhysical, out PageFlags existingFlags))
{
    if (existingPhysical == entry.PhysicalAddress && normalizedExisting == expectedFlags)
        continue;   // уже правильный маппинг
    if (!Pager.Unmap(entry.VirtualAddress))
        return false;
}
if (!Pager.Map(entry.VirtualAddress, entry.PhysicalAddress, entry.Flags))
    return false;
```

---

### 4. Реальное железо (INSYDE): Cr3Accessor падал с NX fault на heap

**Файлы:** `BootInfo.cs`, `UefiBootInfoBuilder.cs`, `Cr3Accessor.cs`, `X64PageTable.cs`, `Kernel.cs`

**Проблема:** `Cr3Accessor` записывал шеллкод (`mov rax,cr3` / `mov cr3,rax`) в heap — страницу `EfiConventionalMemory`. INSYDE firmware маппит `EfiConventionalMemory` с NX-битом в своих page tables. Попытка исполнить шеллкод → `#PF`.  
На QEMU/OVMF NX на ConventionalMemory не выставляется → работало без проблем.

**Решение:** В бутлоадере до захвата memory map аллоцировать 64 байта `EfiLoaderCode`:
```csharp
systemTable->BootServices->AllocatePool(EFI_MEMORY_TYPE.EfiLoaderCode, 64, &allocated)
```
Передать в ядро через `BootInfo.ExecStubBuffer`. `Cr3Accessor` использует этот буфер (EfiLoaderCode firmware маппит без NX) вместо heap.

---

### 5. Реальное железо: pager validation падал — hardcoded VA попадал в 1GB large-page MMIO

**Файл:** `OS_0.1/src/Kernel/Paging/PagingValidation.cs`

**Проблема:** `ValidationBaseVirtual = 0xFFFF800000100000` был захардкожен. На INSYDE firmware этот VA покрыт 1GB large-page маппингом Runtime Services — `GetOrCreateNextTable` не может разбить large page → `map failed`.

**Решение:** Динамический поиск свободного PML4-слота (индекс 64–255), у которого PML4-запись отсутствует:
```csharp
private static ulong FindFreeValidationBase()
{
    for (uint pml4Index = 64; pml4Index < 256; pml4Index++)
    {
        ulong va = (ulong)pml4Index << 39;
        if (Pager.TryGetWalkInfo(va, out PageWalkInfo walk) && !walk.Pml4Present)
            return va;
    }
    return 0;
}
```

---

### 6. Реальное железо: JumpStub получал NX через TrySyncKernelLowMappings

**Файлы:** `OS_0.1/src/Kernel/Elf/ElfValidation.cs`, `OS_0.1/src/Kernel/Process/AppServiceBuilder.cs`

**Проблема:** `TrySyncKernelLowMappings` синхронизировала kernel-страницы из firmware CR3 в pager. Страница JumpStub (EfiConventionalMemory) была в pager без NX. Но в firmware CR3 та же страница — с NX. Код **перезаписывал** существующий маппинг в pager → NX появлялся → `#PF` при исполнении стаба.

**Решение:** Пропускать страницы, которые уже есть в pager (импортировать только отсутствующие):
```csharp
if (Pager.TryQuery(current, out _, out _))
    continue;   // не перезаписывать существующий маппинг
```

---

### 7. Реальное железо (INSYDE): JumpStub #PF при вызове s_jump() под firmware CR3

**Файлы:** `BootInfo.cs`, `UefiBootInfoBuilder.cs`, `JumpStub.cs`, `X64PageTable.cs`, `Kernel.cs`

**Проблема:** JumpStub аллоцировал страницу шеллкода через `PhysicalMemory.AllocPage()` (EfiConventionalMemory). Шеллкод вызывается (`s_jump(...)`) до переключения CR3 — то есть под firmware CR3, где EfiConventionalMemory имеет NX. Результат: `#PF (I:1, P:1)` на адресе JumpStub.

Это подтверждено на кастомном OVMF (шаг 8 ниже):
```
ExceptionData - 0000000000000011  I:1 R:0 U:0 W:0 P:1
RIP  - 000000000052D000   ← адрес JumpStub
CR2  - 000000000052D000
CR3  - 000000000F801000   ← firmware CR3 (не pager)
```

**Решение:** В бутлоадере аллоцировать page-aligned EfiLoaderCode буфер для JumpStub:
```csharp
// 4096+4095 → после выравнивания гарантирован page-aligned адрес внутри EfiLoaderCode
systemTable->BootServices->AllocatePool(EFI_MEMORY_TYPE.EfiLoaderCode, 4096 + 4095, &raw)
ulong aligned = ((ulong)raw + 4095UL) & ~4095UL;
info.JumpStubExecBuffer = (void*)aligned;
```
`JumpStub.TryInitialize()` использует этот буфер (`TryAllocFromExecBuffer`) как приоритетный вариант; `TryAllocFromPhysicalMemory` — fallback для QEMU без NX-enforcement.

---

### 8. QEMU не воспроизводил NX краши: кастомный OVMF с PcdDxeNxMemoryProtectionPolicy=0x7FD5

**Файл:** `ovmf/build.ps1`

**Проблема:** Стандартный OVMF из пакета QEMU не выставляет NX на EfiConventionalMemory. INSYDE firmware использует `PcdDxeNxMemoryProtectionPolicy = 0x7FD5` (бит 7 = EfiConventionalMemory). Без воспроизведения на QEMU невозможно итерировать без физического железа.

**Решение:** Скрипт `ovmf/build.ps1` (WSL2 + EDK2):
- Клонирует EDK2 в WSL2
- Патчит `OvmfPkg/OvmfPkgX64.dsc` через Python-скрипт (надёжнее sed с `|` в тексте):
  ```python
  pcd = 'gEfiMdeModulePkgTokenSpaceGuid.PcdDxeNxMemoryProtectionPolicy'
  # replace existing value or insert into correct section (по DEC-типу)
  ```
- Собирает OVMF: `build -a X64 -t GCC -p OvmfPkg/OvmfPkgX64.dsc`
- Результат: `ovmf/OVMF_CODE.strict-nx.fd` + `ovmf/OVMF_VARS.strict-nx.fd`

---

### 9. Реорганизация прошивок

**Файлы:** `run_build.ps1`, `.gitignore`

- Убран флаг `-SecureBoot` (не давал NX-enforcement, заменён кастомным OVMF)
- Создана папка `ovmf/` в корне репы: билд-скрипт + выход
- `run_build.ps1` ищет `ovmf/OVMF_CODE.strict-nx.fd` первым, фоллбэк на системный QEMU OVMF
- `+nx` на CPU-флагах оставлен (обязателен для корректной работы NX-политики)
- `ovmf/*.fd` добавлены в `.gitignore` (бинари, генерируются локально)

---

## Ключевые паттерны, выявленные в процессе

| Паттерн | Суть |
|---|---|
| EfiConventionalMemory + NX | Реальный firmware выставляет NX на все не-code страницы. Исполняемый шеллкод **обязан** быть в `EfiLoaderCode`. |
| TrySyncKernelLowMappings | Должна только **добавлять** отсутствующие маппинги, никогда не перезаписывать существующие. |
| Large pages в firmware CR3 | Нельзя выбирать VA для тестовых маппингов без проверки PML4-слота на отсутствие. |
| EfiLoaderCode буфер | Аллоцировать до захвата memory map, page-aligned (AllocatePool + ручное выравнивание). |

---

### 10. FetchApp — neofetch-стиль на SDK

**Файлы:** `apps/FetchApp/`, `apps/sdk/AppHost.cs`, `apps/sdk/AppServiceTable.cs`, `OS_0.1/src/Kernel/Process/AppServiceBuilder.cs`, `OS_0.1/src/Kernel/Process/AppServiceTable.cs`

**Проблема:** `WriteString(string)` передавала строку через `TryEncodeAscii`, отрезая всё выше 0x7F — box-drawing символы (░▒█▄▀) превращались в `?`.

**Решение:**
- Добавлен сервис `WriteChar(uint codePoint)` в таблицу сервисов ядра (thunks для Win64 и SystemV).
- `AppHost.WriteString(string)` теперь использует `WriteChar` поцепочно если адрес доступен; ASCII-путь — fallback.
- Добавлен сервис `WriteBuildId()` — ядро пишет `SystemBanner.BuildId` напрямую в UI, без маршалинга строки через границу.
- `FetchApp` (`FETCH.ELF`) — neofetch-приложение: логотип SharpOS из box-drawing символов + ABI, build, image/stack адреса.
- Скрипты сборки: `build_launcher_wsl.ps1` (переименован из `build_app_freestanding_wsl.ps1`), новый `build_fetch_wsl.ps1`.

---

### 11. Расширение ConOut: автовыбор максимального текстового режима

**Файлы:** `OS_0.1/src/Boot/UefiTypes.cs`, `OS_0.1/src/Boot/UefiConsole.cs`, `OS_0.1/src/Boot/UefiBootInfoBuilder.cs`

**Проблема:** На десктопах с дискретной GPU UEFI ConOut по умолчанию остаётся в режиме 800×600; текст рисуется в маленьком квадрате в углу, не растянутом на экран. На лаптопах прошивка сама выбирает нативное разрешение.

**Решение:** `EFI_SIMPLE_TEXT_OUTPUT_PROTOCOL` расширен (добавлены `QueryMode`, `SetMode`, `ClearScreen`, `Mode*`, `EFI_SIMPLE_TEXT_OUTPUT_MODE`). `UefiConsole.TryMaximizeTextMode()` перебирает все доступные режимы, выбирает максимальный по `columns × rows` и вызывает `SetMode`. Вызывается первым делом в `UefiBootInfoBuilder.Build`.

---

### 12. std/: PadLeft / PadRight

**Файлы:** `std/no-runtime/shared/StringAlgorithms.cs`, `std/no-runtime/shared/SystemString.cs`

**Добавлено:** `String.PadLeft(int)`, `String.PadLeft(int, char)`, `String.PadRight(int)`, `String.PadRight(int, char)` — реализованы через `StringAlgorithms`, доступны в `FetchApp` и любом другом no-runtime проекте.

---

## Проверка

1. QEMU (strict-nx OVMF): `.\run_build.ps1` — ОС загружается, вложенные приложения работают
2. Реальное железо (INSYDE): аналогичная картина — `#PF I:1` на JumpStub подтверждён и устранён
3. Кастомный OVMF собирается: `.\ovmf\build.ps1 -SkipClone -SkipDeps`
4. `.\build_fetch_wsl.ps1` → `FETCH.ELF` запускается из лаунчера, выводит логотип с box-drawing символами и системной информацией

## Итог

Step 27 устранил серию аппаратно-зависимых ошибок, связанных с NX-enforcement реального firmware. Главный вывод: **весь исполняемый шеллкод должен жить в `EfiLoaderCode`**, аллоцированном до `ExitBootServices`. Это правило теперь соблюдается для Cr3Accessor и JumpStub. Кастомный OVMF с `PcdDxeNxMemoryProtectionPolicy=0x7FD5` делает QEMU идентичным реальному железу по поведению NX.

Дополнительно: добавлен `FetchApp` с Unicode-выводом, расширен `std/` (PadLeft/PadRight), исправлен выбор видеорежима при старте.
