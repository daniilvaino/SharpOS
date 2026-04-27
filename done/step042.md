# Step 42 — RTC через CMOS + port I/O shellcode infrastructure

## Контекст

Step 41 закрыл часть Phase 1, но критерий готовности фазы по plan.md требует двух вещей:
1. Managed try/catch/finally — самый дорогой пункт фазы (2-6 месяцев).
2. RTC через CMOS ports — несколько часов работы.

Step 42 берёт fast-forward: RTC. Заодно строится переиспользуемая port-I/O инфраструктура, которая понадобится для PIC remap, PS/2 keyboard driver (Phase 5), и любого будущего legacy hardware code'а.

Параллельно: исправил неправдивое утверждение в step041.md о "Phase 1 закрыт целиком" — оно было ошибочным.

## Решение из двух частей

### 1. Port I/O shellcode infrastructure

Managed C# не может эмитить инструкции `in` / `out`. По инварианту 1 — byte-array shellcode patcher, тот же паттерн что `ByRefAssignRefStub`/`ByRefAssignRefPatcher`.

**Три файла:**

`OS/src/Hal/PortIoStub.cs` — host class с двумя `[RuntimeExport]` + `[UnmanagedCallersOnly]` методами:

```csharp
[RuntimeExport("PortIo_Inb")]
[UnmanagedCallersOnly(EntryPoint = "PortIo_Inb")]
private static byte Inb(ushort port) {
    OS.Kernel.Panic.Fail("PortIo.Inb (stub not patched)");
    return 0;
}

[RuntimeExport("PortIo_Outb")]
[UnmanagedCallersOnly(EntryPoint = "PortIo_Outb")]
private static void Outb(ushort port, byte value) {
    OS.Kernel.Panic.Fail("PortIo.Outb (stub not patched)");
}
```

Тела — Panic.Fail fallback. Срабатывает если patcher не отработал.

`OS/src/Hal/PortIoPatcher.cs` — overwrites первые байты managed bodies хэнд-крафтед shellcode'ом.

```
byte Inb(ushort port)   — Win64 ABI: port в CX, return в AL
  66 89 ca       mov dx, cx        ; load port number into DX
  ec             in  al, dx        ; read byte from port DX into AL
  0f b6 c0       movzx eax, al     ; clear upper bits of return reg
  c3             ret
                                   ; 8 bytes total

void Outb(ushort port, byte value) — Win64 ABI: port в CX, value в DL
  88 d0          mov al, dl        ; rescue value (DL gets clobbered next)
  66 89 ca       mov dx, cx        ; load port into DX
  ee             out dx, al        ; write AL to port DX
  c3             ret
                                   ; 7 bytes total
```

**Тонкость в Outb:** `mov dx, cx` пишет в DX (= DH+DL), затирая DL который хранит value. Поэтому сначала `mov al, dl` (rescue), потом `mov dx, cx`, потом `out dx, al`. Порядок важен.

`OS/src/Hal/PortIo.cs` — managed wrapper:

```csharp
public static byte In8(ushort port) {
    delegate* unmanaged<ushort, byte> fn =
        (delegate* unmanaged<ushort, byte>)PortIoStub.GetInbAddress();
    return fn(port);
}

public static void Out8(ushort port, byte value) {
    delegate* unmanaged<ushort, byte, void> fn =
        (delegate* unmanaged<ushort, byte, void>)PortIoStub.GetOutbAddress();
    fn(port, value);
}
```

PortIoPatcher.TryInstall() runs в Phase 2 (под firmware CR3, кернел .text RWX на OVMF). После EBS / на real HW с W^X потребуется alias-map путь.

### 2. RTC через CMOS

`OS/src/Hal/Rtc.cs` — port-I/O CMOS reader.

CMOS лежит за двумя legacy I/O port'ами:
- `0x70` — index/select port (write register number)
- `0x71` — data port (read/write selected register)

Регистры:
- `0x00` — секунды
- `0x02` — минуты
- `0x04` — часы (или 1..12 + PM-bit в 12-hour mode)
- `0x06` — день недели
- `0x07` — день месяца
- `0x08` — месяц
- `0x09` — год (low 2 digits)
- `0x32` — век (не всегда, варьируется по board'ам)
- `0x0A` — Status A (bit 7 = Update In Progress)
- `0x0B` — Status B (bit 1 = 24-hour mode, bit 2 = binary mode)

**Read protocol** (canonical из osdev wiki):

1. Wait until UIP=0 (bounded ~1M iterations, защита от стуэнной CMOS).
2. Read all fields → "old" snapshot.
3. Wait until UIP=0.
4. Read all fields → "new" snapshot.
5. If old == new, accept. Else loop (до 16 attempts).
6. Apply BCD→binary if Status B bit 2 = 0.
7. Apply 12→24-hour correction if Status B bit 1 = 0:
   - Rescue PM bit (`hour & 0x80`) **до** BCD-conversion (PM bit мешает BCD decode'у).
   - 12 PM → 12, 1..11 PM → +12.
   - 12 AM → 0.
8. Combine century: `centuryValid ? (century * 100 + year) : (year < 70 ? 2000+year : 1900+year)` — fallback convention из dotnet/runtime.

`Rtc.TryRead(out Snapshot)` возвращает нормализованный `Snapshot { Year, Month, Day, Hour, Minute, Second, Weekday, CenturyValid }`.

## Boot wiring

`Probes.RtcSnapshot` — новый toggle (default true). 

**Phase 2** добавлено:

```csharp
InstallByRefAssignRefShellcode();
InstallPortIoShellcode();   // ← новое
```

**Phase 3** добавлено в конец:

```csharp
InitializeHpet();
if (Probes.RtcSnapshot) DumpRtcSnapshot();
```

`DumpRtcSnapshot` форматит `YYYY-MM-DD HH:MM:SS UTC dow=N centuryReg=yes/no`.

`docs/boot-order.md` переписан под текущую фазовую структуру + RTC.

## Результат

Boot log:

```
[info] iface dispatch bridge installed
[info] byref-assign shellcode installed
[info] port-io shellcode installed
...
[info] hpet: freq=100000000 Hz period=10000000 fs comparators=3 64bit=yes
[info] hpet counter delta: 36960 ticks
[info] stopwatch ~1ms spin: elapsed_us=1051 elapsed_ms=1
[info] rtc: 2026-04-27 08:23:41 UTC dow=2 centuryReg=yes
```

Year/month/day матчит сегодня (2026-04-27 monday). DoW=2 = понедельник по convention 1=Sunday. Century register supported в QEMU OVMF.

Все остальные probe'ы остаются зелёные: GC stress 1-3 (test 3 rotate `marked=60 swept=18`), 50+ NativeAotProbe assertions, CctorProbe, 4 ELF apps, launcher.

## Phase 1 statu

| Пункт plan.md | Статус |
|---|---|
| ClassConstructorRunner | ✅ step 40 |
| ACPI parsing | ✅ step 38 |
| HPET/Stopwatch | ✅ step 39 |
| `throw → readable panic` | ✅ step 37 |
| Canonical `static readonly T x = new T()` | ✅ steps 40-41 |
| **RTC через CMOS** | ✅ **step 42** |
| Managed try/catch/finally | ❌ остаётся (самый дорогой) |

Критерий готовности фазы (plan.md):
> `try { throw new InvalidOperationException("test"); } catch (Exception e) { Console.WriteLine(e.Message); }` ловит exception и логирует. ACPI таблицы парсятся, APIC/HPET адреса найдены, `Stopwatch` показывает корректное время.

Половина критерия (ACPI/HPET/Stopwatch) — закрыта. Половина (try/catch) — остаётся как единственный открытый пункт Phase 1.

## Файлы

### Новые

- `OS/src/Hal/PortIoStub.cs` — host class для shellcode patching.
- `OS/src/Hal/PortIoPatcher.cs` — installer of inb/outb shellcode.
- `OS/src/Hal/PortIo.cs` — managed wrapper.
- `OS/src/Hal/Rtc.cs` — CMOS reader с UIP wait + двойное чтение + BCD/binary handling.
- `done/step042.md` — этот файл.

### Изменённые

- `OS/src/Boot/BootSequence.cs` — install PortIoPatcher в Phase 2, DumpRtcSnapshot в Phase 3.
- `OS/src/Kernel/Diagnostics/Probes.cs` — добавлен `RtcSnapshot` toggle.
- `docs/boot-order.md` — переписан под фазы Phase0..Phase5 + RTC.
- `done/step041.md` — исправлено ложное утверждение о закрытии Phase 1.

## Что дальше

**Step 43+ — managed try/catch/finally.** Самый рискованный пункт Phase 1. План.md помечает 2-6 месяцев. Готовы к серьёзному заходу с двумя мудрецами:

1. Personality function для NativeAOT/Itanium ABI (или MSVC `__C_specific_handler` стиль если нужно).
2. Stack unwinding через `.eh_frame` (Linux/SysV) или `.pdata` (Windows/MSVC) — какой из них ILC эмитит для нашего target'а.
3. `System.Exception` базовый класс — `Message`, `StackTrace`, `InnerException`.
4. Производные типы: `InvalidOperationException`, `ArgumentException`, и т.д.
5. `try` / `catch` / `finally` блоки фунцкиональны.

Возможно Phase 1 fallback: longjmp-only milestone (если real unwinder упирается). Plan.md явно это допускает.

После закрытия — Phase 2 (PAL design + spike).
