# Step 38 — Phase 1c: ACPI parsing (RSDP / XSDT / MADT / HPET / MCFG)

## Контекст

Phase 1 продолжается. После Phase 1a-min (step 37: throw → readable panic) приступил к ACPI parsing — фундамент для всего hardware-aware кода:

- **MADT** (APIC topology) — нужен для timer interrupts, SMP, IRQ routing.
- **HPET** (timer base) — нужен для Phase 1d (Stopwatch / clock source).
- **MCFG** (PCIe ECAM) — нужен для Phase 5 (PCI enumeration → drivers).
- **FADT** (system info) — пригодится позже.

Без ACPI — кernel "слепой" к hardware topology. С ACPI — точно знает где APIC, где HPET, какие сегменты PCIe.

## Что сделано

### EFI расширения (`OS/src/Boot/UefiTypes.cs`)

Добавил недостающие поля в `EFI_SYSTEM_TABLE`:
- `NumberOfTableEntries` (ulong)
- `ConfigurationTable*` (EFI_CONFIGURATION_TABLE*)

И сам `EFI_CONFIGURATION_TABLE` struct (16-byte VendorGuid + 8-byte VendorTable*).

### `BootInfo.SystemTable`

Добавил поле — нужно Acpi.Init для walking config table. UefiBootInfoBuilder заполняет.

### Парсеры в `OS/src/Hal/Acpi/`

5 файлов:

- **AcpiHeader.cs** — общий 36-byte SDT header + checksum validation + signature constants (RSDT/XSDT/MADT/HPET/MCFG/FADT) как `const uint`. Не `static readonly` — избегаем ClassConstructorRunner trap.

- **Rsdp.cs** — Root System Description Pointer layout + signature/checksum validation (отдельно для ACPI 1.0 первых 20 bytes и для full 2.0 длины).

- **Acpi.cs** — orchestrator:
  1. Walk EFI_CONFIGURATION_TABLE для GUID `8868E871-E4F1-11D3-BC22-0080C73C8881` (ACPI 2.0).
  2. Validate RSDP (signature + V1 + V2 checksums).
  3. Reject ACPI 1.0 (revision < 2) — XSDT is the only sane path on x64.
  4. Walk XSDT entries (64-bit physical pointers, after 36-byte header).
  5. Validate каждую таблицу's checksum, classify по signature.
  6. Cache pointers `MadtPtr`, `HpetPtr`, `McfgPtr`, `FadtPtr`.

  GUID compared via field-by-field constants (`IsAcpi20Guid` function) вместо `static readonly EFI_GUID` поля — избегаем cctor trap для struct initializer.

- **Madt.cs** — Local APIC address (с поддержкой Type 5 64-bit override entry), counts of Local APICs (Type 0) и IO APICs (Type 1). Walks variable-length entry list после 8-byte fixed header (LocalApicAddress + Flags).

- **Hpet.cs** — Base address из ACPI Generic Address Structure at offset 44 of HPET table.

- **Mcfg.cs** — Configuration Space Allocation entries (16 bytes каждая): BaseAddress + SegmentGroup + StartBus + EndBus. `TryGetEntry(index, ...)` API.

### Boot integration (`OS/src/Kernel/Kernel.cs`)

`InitializeAcpi(bootInfo)` после `RunPagerValidation`. Печатает summary: XSDT count + LAPIC base + CPU/IO-APIC counts + HPET base + MCFG segment 0 ECAM.

## Верификация в QEMU/OVMF

```
[info] acpi xsdt entries: 6
[info] acpi madt: lapic=0xFEE00000 cpus=1 ioapics=1
[info] acpi hpet: base=0xFED00000
[info] acpi mcfg: entries=1 seg0: base=0xE0000000 bus=0..255
```

Все значения каноничные:
- 6 XSDT entries (типичный QEMU minimum: FACP/APIC/HPET/MCFG/FACS/DSDT).
- LAPIC base `0xFEE00000` — стандартный x86 Local APIC physical address.
- 1 CPU (QEMU `-smp 1` default), 1 IO-APIC.
- HPET base `0xFED00000` — стандартный.
- MCFG ECAM at `0xE0000000`, bus range 0..255 (full PCIe segment).

Все 58 probes остаются зелёными. Launcher работает.

## Ловушки которые обошёл

### `static readonly EFI_GUID` field

Первая попытка декларировала `private static readonly EFI_GUID Acpi20Guid = new EFI_GUID { ... };`. Struct initializer заворачивается в cctor → ClassConstructorRunner trap (даже несмотря на то что docs обещают что value-typed cctor работает — на практике с struct'ами с initializer'ами поведение не protested).

Решение: function `IsAcpi20Guid(EFI_GUID*)` которая сравнивает поля с hardcoded constants. Никакого cctor.

### MakeSignature как `static readonly` поле

То же самое — initializer call → cctor. Заменил на `const uint` с прекомпьютеренными значениями (little-endian uint encoding 4-char ASCII signature).

### Unaligned 64-bit reads в XSDT

XSDT pointer entries следуют за 36-byte header — не гарантировано 8-byte aligned. На x64 это не критично (CPU обрабатывает unaligned reads), но для портабельности ARM64 в будущем сделал явный `ReadUnalignedU64(byte*)` который читает byte-by-byte.

## Архитектурные заметки

### Почему MADT signature это "APIC", не "MADT"

Исторический quirk ACPI spec — таблица называется MADT (Multiple APIC Description Table), но её 4-char signature это `APIC`. Аналогично FADT signature = `FACP` (Fixed ACPI Description Table). Документировал в const'ах с комментариями.

### Почему MCFG present но мы его пока не используем

Phase 1c только parsing/discovery. Acting на эти данные = Phase 5 (PCI enumeration drivers). Pre-storing pointers сейчас — экономия лишнего walk'а позже.

## Файлы

### Новые

- `OS/src/Hal/Acpi/AcpiHeader.cs`
- `OS/src/Hal/Acpi/Rsdp.cs`
- `OS/src/Hal/Acpi/Acpi.cs`
- `OS/src/Hal/Acpi/Madt.cs`
- `OS/src/Hal/Acpi/Hpet.cs`
- `OS/src/Hal/Acpi/Mcfg.cs`
- `done/step038.md`

### Изменённые

- `OS/src/Boot/UefiTypes.cs` — `EFI_SYSTEM_TABLE` extended + `EFI_CONFIGURATION_TABLE` added.
- `OS/src/Boot/BootInfo.cs` — `SystemTable` field.
- `OS/src/Boot/UefiBootInfoBuilder.cs` — populates SystemTable.
- `OS/src/Kernel/Kernel.cs` — `InitializeAcpi` call + summary log.

## Что дальше — оставшиеся пункты Phase 1

1. **Phase 1d — RTC + HPET timekeeping.** Wall-clock через CMOS ports 0x70/0x71. High-resolution через HPET memory-mapped (Hpet.Base уже у нас есть от step 38) или RDTSC. `Stopwatch` API.

2. **Phase 1b — ClassConstructorRunner портирование.** Самый рисковый из оставшихся — может потребовать дроп `--resilient` режима ILC что раскроет остальные missing helpers. Оставляем последним в Phase 1.
