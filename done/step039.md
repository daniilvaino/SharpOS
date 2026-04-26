# Step 39 — Phase 1d: HPET + Stopwatch

## Контекст

Phase 1 продолжается. Step 38 нашёл HPET base address через ACPI. Этот шаг — **превратить адрес в работающий timer**: enable counter, считать частоту, обернуть в `System.Diagnostics.Stopwatch` API.

RTC (wall-clock через CMOS port I/O) намеренно skip'ну — kernel не нуждается в wall-clock на этом этапе. Boot diagnostics, scheduler timing, performance measurement — всё это нужно через **monotonic high-res counter**, который HPET даёт идеально.

## Что сделано

### `OS/src/Hal/Timer/Hpet.cs` (новый)

Hardware abstraction для HPET register block:

- **Init()** — читает Capabilities register (offset 0), извлекает COUNTER_CLK_PERIOD (бит 32:63, в фемтосекундах), вычисляет frequency = 10^15 fs/s ÷ period. Sets ENABLE_CNF в Configuration register (offset 0x10) для запуска counter'а.
- **ReadCounter()** — atomic 64-bit read main counter (offset 0xF0).
- Properties: `FrequencyHz`, `PeriodFemtoseconds`, `Is64BitCounter`, `NumComparators`.

Регистры через memory-mapped pointers — **никакого shellcode'а** для HPET (всё прямой read/write через `*(ulong*)(base + offset)`). Это в отличие от RTC где нужны port I/O инструкции `inb/outb`.

### `OS/src/Hal/Timer/Stopwatch.cs` (новый)

`System.Diagnostics.Stopwatch` API поверх HPET. Минимальный port из BCL:

- `Start/Stop/Reset/Restart/StartNew`
- `IsRunning`, `Frequency`, `IsHighResolution`
- `ElapsedTicks` (raw HPET counter delta)
- `ElapsedMilliseconds`, `ElapsedMicroseconds`
- `GetTimestamp()` — raw counter snapshot

`TimeSpan`-returning properties **не порт'ил** — у нас нет TimeSpan struct'а. Если понадобится — добавим.

Threading: not safe — single-thread only. Phase 3 scheduler будет per-thread.

### Boot integration (`OS/src/Kernel/Kernel.cs`)

`InitializeHpet()` после `InitializeAcpi`. Печатает summary + 2 sanity test'а:
1. **Counter increment test:** read counter, busy-loop 100k iterations, read again — выводит delta.
2. **Stopwatch round-trip:** `StartNew` + busy-spin до `freq/1000` ticks (≈1 ms), `Stop`, выводит `ElapsedMicroseconds` + `ElapsedMilliseconds`.

## Верификация в QEMU

```
[info] hpet: freq=100000000 Hz period=10000000 fs comparators=3 64bit=yes
[info] hpet counter delta: 37430 ticks
[info] stopwatch ~1ms spin: elapsed_us=1027 elapsed_ms=1
```

Все каноничные значения для QEMU HPET:
- 100 MHz frequency (QEMU default)
- 10 ns per tick (10,000,000 femtoseconds = 10 ns)
- 3 comparators, 64-bit counter

Timing measurements:
- 100k empty loop iterations → 37,430 HPET ticks → 374 µs → ~3.7 ns/iter (плотно работающий compiler-optimized loop, плюс HPET register read latency)
- Stopwatch ~1 ms spin → 1027 µs / 1 ms — overshoot на 27 µs от busy-loop overhead, ожидаемо.

Все 58 probes остаются зелёные. Launcher работает.

## Что НЕ покрыто

- **RTC (wall-clock)** — port I/O через 0x70/0x71 + BCD decoding. Skip — нет caller'а в kernel.
- **TSC (RDTSC)** — per-CPU cycle counter. Skip — TSC ненадёжен на multi-core (drift между CPUs), HPET достаточен для duration.
- **HPET comparators** — interrupt-based timers (нужны в Phase 3 для scheduler preemption). Не активирую сейчас — нет handler infrastructure.
- **TimeSpan struct** — BCL Stopwatch returns `TimeSpan Elapsed`. Skip — нет TimeSpan в нашем std.

## Архитектурные заметки

### Period в фемтосекундах

ACPI HPET spec кодирует counter period в **femtoseconds (10^-15 s)**, не в наносекундах. Frequency = 1e15 / period. QEMU period = 10,000,000 fs → 10 ns/tick → 100 MHz.

### 64-bit counter atomic read на x64

HPET может быть 32-bit или 64-bit. Capabilities bit 13 = COUNT_SIZE_CAP. На x64 чтение 64-bit register single instruction (atomic). На 32-bit-only HPET (rare на modern HW) high half = 0 и обычный 64-bit read даёт правильное значение. Мы не делаем split-read.

### Static reference fields

HPET state хранится как `private static byte* s_base;`, `private static ulong s_frequencyHz;` — все value-typed без initializer. ClassConstructorRunner не дёргается.

## Файлы

### Новые

- `OS/src/Hal/Timer/Hpet.cs`
- `OS/src/Hal/Timer/Stopwatch.cs`
- `done/step039.md`

### Изменённые

- `OS/src/Kernel/Kernel.cs` — `InitializeHpet()` call + summary log + verification.

## Что дальше

Phase 1 — остался один пункт:

**Step 40 — Phase 1b: ClassConstructorRunner.** Самый рисковый из всех. Дроп `--resilient` режима ILC для строгой линковки → раскрывает все недостающие helpers, каждый чиним. Реализация cctor walker'а: при первом доступе к static reference field зовёт cctor, выставляет initialized flag, возвращает base pointer.

Если получится — разблокирует все `static readonly T x = new T();` паттерны. Если не получится — остаёмся с `""`-литеральным workaround.

После Phase 1b → Phase 1 закрыт целиком, переходим к Phase 2 (PAL design + de-risk spike) или сначала Phase 3.7 (StackInterpreter integration milestone).
