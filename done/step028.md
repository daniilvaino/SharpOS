# step028

Дата: 2026-04-18  
Статус: завершён (стадия 1 SUPER-1a)

## Цель

Начать движение по `plan.md` — SUPER-1a (std BCL surface без массивов).

1. Переименовать `OS_0.1` → `OS` — избавиться от версии в имени проекта.
2. Стадия 1 SUPER-1a: вынести логику преобразования чисел в строку из inline-кода `Console.cs` в общий `std/` слой. Приложения впервые получают возможность строить строки с числами без кастомных stackalloc-буферов.

---

## Что сделано

### 1. Rename `OS_0.1` → `OS`

**Файлы/папки:**
- `OS_0.1/` → `OS/`
- `OS_0.1/OS_0.1.csproj` → `OS/OS.csproj`
- Git зафиксировал 78 файлов как rename (R), история сохранена.

**Референсы обновлены:**
- `OS.sln` — путь к проекту + имя
- `OS/OS.csproj` — `IlcSystemModule`
- `run_build.ps1` — `$efiProjectDir`, `$projectFile`, сообщение "Building OS", имя `.exe`
- `build_fetch_wsl.ps1`, `build_launcher_wsl.ps1`, `build_media_xorriso.ps1` — пути к `.qemu/esp`
- `README.md`, `std/no-runtime/README.md` — описание структуры

Исторические `done/step*.md` не трогаем — они фиксируют состояние на момент своего шага.

### 2. NumberFormatting в std/

**Создан `std/no-runtime/shared/NumberFormatting.cs`** — `public static unsafe class NumberFormatting` в namespace `SharpOS.Std.NoRuntime` с методами:

| Метод | Диапазон |
|---|---|
| `UIntToString(uint)` | 0 … 4 294 967 295 |
| `IntToString(int)` | -2 147 483 648 … 2 147 483 647 |
| `ULongToString(ulong)` | 0 … 18 446 744 073 709 551 615 |
| `LongToString(long)` | -9 223 372 036 854 775 808 … max long |
| `UIntToHex(uint, int minDigits)` | до 8 hex-цифр, padding нулями слева |
| `ULongToHex(ulong, int minDigits)` | до 16 hex-цифр, padding нулями слева |

**Edge cases:**
- `0` → литерал `"0"` (без аллокации).
- `int.MinValue` (-2147483648) → литерал — нельзя взять `(uint)(-value)` из-за overflow.
- `long.MinValue` — то же, через `unchecked((long)0x8000000000000000UL)` для сравнения.
- Защита от отказа аллокации: если `FastAllocateString(n).Length != n` → возвращаем полученную строку без заполнения.

**Реализация:**
1. Подсчитать количество цифр (decimal или hex).
2. Аллоцировать строку нужной длины через `StringRuntime.FastAllocateString(n)`.
3. Заполнить справа-налево через `fixed (char* dst = &result.GetPinnableReference())`.

### 3. Подключение в csproj

Во все три проекта добавлена строка:
```xml
<Compile Include="..\..\std\no-runtime\shared\NumberFormatting.cs" Link="..." />
```

- `OS/OS.csproj`
- `apps/HelloSharpFs/HelloSharpFs.csproj`
- `apps/FetchApp/FetchApp.csproj`

### 4. Smoke-test

В `FetchApp/Program.cs` временно добавлялся блок:
```
NumberFormatting:
  UIntToString(42)         = 42
  IntToString(-2147483648) = -2147483648
  ULongToHex(0xDEADBEEF,8) = DEADBEEF
  LongToString(-123456789) = -123456789
```

Запуск в QEMU strict-nx OVMF → все четыре значения корректные. Smoke-test убран после подтверждения.

---

## Работает / не работает

| Контекст | Статус |
|---|---|
| `NumberFormatting.*` в приложении (после `ManagedStartup` + `RhNewString`) | **работает**, подтверждено визуально |
| `NumberFormatting.*` в ядре | возвращает `string.Empty` — `StringRuntime.Fallback` в `OS.csproj` всегда возвращает Empty. Это ожидаемо до стадии 2 SUPER-1a (kernel string allocation через `KernelHeap`). |
| Console.WriteUInt/WriteULong/WriteHex в ядре | без изменений, inline stackalloc — пока не мигрируем, ждём стадию 2. |

---

## Архитектурные замечания

- **Дублирование логики:** `Console.WriteUInt` в ядре и `NumberFormatting.UIntToString` в std/ сейчас содержат одинаковый алгоритм подсчёта цифр и заполнения. Дублирование временное — снимется на стадии 2 SUPER-1a, когда ядро получит реальную аллокацию строк и сможет использовать `NumberFormatting` напрямую.
- **std/ shared-инвариант:** файл `NumberFormatting.cs` подключается во все проекты одинаково. В ядре работа через `FastAllocateString` возвращает Empty, но код совместим — компилируется и не крашит.
- **Rename:** проект теперь называется просто `OS`, без версии. Версионирование ушло в `SystemBanner.BuildId` и `step*.md`.

---

## Что дальше

Стадия 2 SUPER-1a — kernel string allocation:
1. Расширить `OS/src/Boot/MinimalRuntime.cs`: `EETypePtr`, `MethodTable`, `IntrinsicAttribute`, `MethodImpl*`.
2. Создать `std/no-runtime/shared/StringRuntime.KernelHeap.cs` — аллокация через `KernelHeap.Alloc` с layout `[MT 8B][Length 4B][chars 2B*(len+1)]`, MethodTable достаём из `*(void**)(object)string.Empty`.
3. Заменить `StringRuntime.Fallback.cs` на `StringRuntime.KernelHeap.cs` в `OS.csproj`.
4. Kernel smoke-test: `FastAllocateString(5)` → `fixed (char* p = s) { ... }` → `Console.Write(s)`.
5. Мигрировать `Console.WriteUInt/WriteULong/WriteHex` на `NumberFormatting` (с guard `IsInitialized` для ранне-boot сценария).

Полный план — в `plan.md` секция SUPER-1a.
