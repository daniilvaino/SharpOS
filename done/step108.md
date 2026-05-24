# Step 108 — VirtualBox-first-class boot + FAT read perf + AHCI corruption fix + BigStack out of GcHeap

## Резюме

Один длинный сэшн с пятью переплетающимися fronts. Все слепились в один
step потому что вытащить отдельно невозможно — каждый чинит регрессию,
которую открыл предыдущий:

1. **GPT в FAT-mount** — VirtualBox грузил BOOTX64.EFI через firmware,
   но post-EBS наш FAT-driver не находил ESP на GPT-диске.
2. **AHCI multi-PRDT corruption** — обнаружился когда стали читать
   больше 16 секторов за команду. Два разных бага в одном пути.
3. **FAT read perf** — на VBox AHCI на порядок медленнее QEMU по
   per-command latency, нужно сократить количество команд.
4. **BigStack out of GcHeap** — корневая причина почему пропажа
   «лишнего» CoreLib-read раньше ломала boot: BigStack был внутри
   первого GC-сегмента, layout-сдвиг ломал fault classifier.
5. **memory-ownership.md обновлён** — отражает все эти решения как
   архитектурный контракт, плюс новый Cumulative Budget раздел.

Плюс инфраструктура: VirtualBox runner (`run_vbox.ps1`), bump размеров
FAT-образов, NoIso skip, hashtable-splat фикс build-media,
`build_media_xorriso.ps1` теперь проверяет ESP-source ПОСЛЕ билда не до.

## 1. GPT FAT-mount

### Симптом

VirtualBox EFI грузит `BOOTX64.EFI` (firmware читает GPT само).
Post-EBS наш `Fat32.Mount` падает:
```
[fat] mount=N jmp=0x0,0x0 bps@11=0 p0type=0xEE p0lba=1 FAIL
```

Тип 0xEE в partition 0 = **GPT protective MBR**. Старый `Mount()`
понимал только superfloppy + legacy MBR; при сканировании 4 entries
видел entry с start LBA=1 (где лежит GPT header, не BPB) → fail.

### Решение

Третья ветка в `Mount()` после MBR-scan:

```csharp
if (!ReadAbs(1)) return false;
// "EFI PART" magic = 45 46 49 20 50 41 52 54
if (s_sec[0] != 0x45 || ... ) return false;
ulong entriesLba = RdU64(s_sec, 72);
uint entryCount = RdU32(s_sec, 80);
uint entrySize  = RdU32(s_sec, 84);
if (entrySize < 128 || entrySize > 512 || entryCount > 256) return false;
uint perSec = 512u / entrySize;
for (uint i = 0; i < entryCount; i++) {
    ulong lba = entriesLba + (i / perSec);
    ReadAbs(lba);
    byte* e = s_sec + (int)((i % perSec) * entrySize);
    // skip empty (all-zero PartitionType GUID)
    ulong firstLba = RdU64(e, 32);
    if (ParseBpbAt(firstLba)) { DiagPath = 3; s_mounted = true; return true; }
}
```

Field offsets cross-checked против UEFI 2.10 §5.3 и
`DiscUtils.Core/Partitions/GptHeader.cs` (MIT) — no code copied,
clean-room minimum. Не фильтруем по PartitionType GUID, доверяемся
`ParseBpbAt` как detector'у (то же что делает MBR-ветка).

Sanity caps: `entrySize ∈ [128..512]`, `entryCount ≤ 256` — защита от
corrupted header'а с billion-LBA scan.

### Диагностика

`DiagPath=3` для GPT, `DiagGptEntries`, `DiagGptFirstLba`. `FatProbe`
печатает `gptEnt=N gptLba=N` в FAIL-сообщении.

## 2. AHCI multi-PRDT corruption — два бага

Когда `Fat32.ReadFile` стал issue'ить команды > 16 секторов,
заработал давно-dead multi-PRDT путь и оказался сломан.

### Баг #1 — `Buffer +=` typo

```csharp
(&table->PRDTEntry)[i].ByteCount = 8 * 1024 - 1;   // PRDT покрывает 8 KiB
Buffer += 4 * 1024;                                 // advance на 4 KiB ❌
```

Соседние PRDT'ы overlap'ились на 4 KiB. Видно никогда не выполнялось
потому что все callers использовали Count ≤ 16 (single PRDT, цикл
не итерировался).

Fix: `Buffer += 8 * 1024`.

### Баг #2 — `Count` reused for two unrelated purposes (это **the** corruption)

Оригинальный код:

```csharp
hdr->PRDTLength = (ushort)(((Count - 1) >> 4) + 1);
for (i = 0; i < hdr->PRDTLength - 1; i++) {
    ...
    Count -= 16;                              // decrement for next PRDT
}
(&table->PRDTEntry)[i].ByteCount = (uint)((Count << 9) - 1);   // last PRDT
...
FIS->Count = Count;   // ❌ Count уже = leftover (16), не totalCount
```

`FIS->Count` это **sector-count register ATA-команды**. ATA думала
«передай 16 секторов», но PRDT'ы суммарно требовали 128. Device
рапортовал completion после первой PRDT, остальные 7 PRDT'ов
оставались с мусорными байтами. Тихая corruption внутри файла.

**Это был настоящий root** corruption-bug, не #1. Симптом:
```
[fat] CoreLib.dll sz=23576576 mz=Y PASS         ← заголовок OK
coreclr_initialize hr=0x80131018                ← CLDB_E_FILE_CORRUPT внутри
```

Fix: развязка значения по семантике.

```csharp
ushort totalCount = Count;     // для FIS sector-count и PRDTLength formula
ushort remaining = Count;      // для последнего PRDT ByteCount
hdr->PRDTLength = (ushort)(((totalCount - 1) >> 4) + 1);
for (i = 0; i < hdr->PRDTLength - 1; i++) {
    ...
    remaining = (ushort)(remaining - 16);
}
(&table->PRDTEntry)[i].ByteCount = (uint)((remaining << 9) - 1);
...
FIS->Count = totalCount;
```

### Дополнительно по AHCI

- **Прерывания глушатся систематически**: новый `DisableInterrupts(Controller, Port)`
  вызывается на controller init, port init, до/после `Configure`,
  до/после каждого `ReadOrWrite`. VBox AHCI явно более чувствителен
  к необработанным IRQ.
- `InterruptOnCompletion = false` на всех PRDT (было `true`).
- `Count == 0` guard в начале `ReadOrWrite`.
- Телеметрия: `s_readCommands / s_readSectors / s_readTicks / s_readFailures`
  плюс HPET-обёрнутый `Read()` для timing.

## 3. FAT read perf

### Проблема

VBox AHCI: per-command latency ~1 ms. CoreLib 23 MB через 512 B/команда
= 47000 команд = много секунд застоя в boot.

### Изменения в `Fat32.cs`

1. **`BulkBytes` 8 KiB → 64 KiB** (8 PRDT × 8 KiB). После фикса
   AHCI multi-PRDT bug это безопасно.

2. **FAT-sector cache** — отдельный DMA-буфер `s_fatCache` +
   `s_fatCachedLba`. `FatNext` теперь идёт через
   `ReadFatSector(lba)` — попадание в кэш = мгновенно. На FAT32 в
   одном секторе 128 cluster entries, последовательный walk файла
   из 5750 кластеров делает теперь ~45 уникальных FAT-чтений
   вместо 5750.

3. **Contiguous-cluster coalescing** — перед issue'ить read,
   walk вперёд по FAT пока `FatNext(last) == last+1`, накопить
   `runClusters` (cap'нутый `maxChunk / s_spc` и оставшимся file
   size'ом). Одна AHCI-команда читает весь run.

4. **`Unsafe.CopyBlock`** вместо byte-by-byte loop в data-copy.

5. **Capture `nextCluster` внутри run-detect**: реюзится для
   следующей итерации outer-loop, не делаем лишнего FatNext'а.

Лимиты: `runSectors` cap'ается ещё и через `sectorsNeeded` от
remaining size — не читаем больше чем нужно.

### Результат

CoreLib 23 MB: ~360 AHCI-команд вместо 47000. На VBox boot
ускорился в десятки раз; на QEMU — в разы (там per-command стоит
дёшево, но всё равно меньше overhead).

## 4. BigStack out of GcHeap — наконец

### Корневая причина

Раньше `BootSequence.RunCoreClrSession` делал `GcHeap.AllocateRaw(16 MB)`
для BigStack-буфера. Сегмент GcHeap раздувался до 16+ MB чтобы вместить
аллокацию, и **BigStack оказывался внутри первого kernel GC-сегмента**.

Следствия:
- Fault classifier (`HwFaultBridge`) видел RSP внутри GcHeap → false
  positive `[SO-SUSPECT]` каждый раз когда CoreCLR ловил исключение.
- `SharpOSHost_GetStackBounds` отдавал bounds огромного UEFI usable
  region'а (~419 MB) вместо реального 16 MB буфера. CoreCLR думал
  что у него 419 MB стека → SO guard не срабатывал → recursion
  убегал в чужую память → triple fault.
- Layout-зависимость: убери лишний CoreLib-read из boot — сегмент
  GcHeap чуть подвинется — BigStack оказывается на новом offset —
  что-то ломается. Класическая Heisenbug-симптоматика.

### Решение

`BootSequence.RunCoreClrSession` теперь:

```csharp
const uint BigStackSize = 16u * 1024u * 1024u;
void* bigBuf = AllocateBigStack(BigStackSize);
...

private static void* AllocateBigStack(uint size) {
    ulong bytes = ((ulong)size + 4095) & ~4095UL;
    ulong phys = PhysicalMemory.AllocPages((uint)(bytes / 4096));
    if (phys == 0) return null;
    if (!VirtualMemory.MapFixed((void*)phys, phys, bytes, exec: false)) return null;
    byte* p = (byte*)phys;
    for (ulong i = 0; i < bytes; i++) p[i] = 0;
    return p;
}
```

Лог `[bigstack] buf=0x... size=0x1000000` для верификации.

### Удалённые workaround'ы

- `BigStack.cs` header-комментарий который оправдывал region heuristic
  через «BigStack живёт в GcHeap region» — больше не актуален.
- `HwFaultBridge` classification: убран комментарий про false-positive
  `[SO-SUSPECT]` (теперь BigStack bounds авторитетны через
  `TryGetActiveBounds`, и если RSP вне них + внутри GcHeap — это
  настоящий overflow/overlap, не маскировка).
- `StackBounds.cs` (`SharpOSHost_GetStackBounds`) — обновлён
  комментарий: hosted threads и BigStack-active path возвращают
  точные bounds первыми, region-fallback только если ничего из этого.

## 5. memory-ownership.md обновлён

Зафиксировал текущую архитектуру памяти как контракт:

- §1 диаграмма: добавлены ESTUB (EfiLoaderCode pools), THRSTATE
  (HandleTable/Binding/WaitBlock inline-in-Thread), FAT с тремя tier'ами.
- §2 строка EfiLoaderCode boot pools — отдельно от BootStackPool.
- §3 список kernel GcHeap-обитателей синхронизирован.
- §6 явно перечислены три FAT mount tier'а.
- §7.2 с конкретикой: `0x167C000` = 22.5 MiB CoreLib → ~32 MiB
  GcHeap-сегмент. Превращает абстрактный тех-долг в число.
- §9 нумерация M1-M5 + новый M5 (re-enable kernel GC sweep после M1/M2).
- **§10 Cumulative Budget** — новый раздел: BootStackPool 4 MiB,
  EfiLoaderCode stubs ~14 KiB, KernelHeap base 16 KiB, kernel GcHeap
  base 256 KiB, CoreLib buffer 22.5 MiB → 32 MiB segment, BigStack
  16 MiB, kernel thread 64 KiB + guard + context, CoreCLR hosted
  thread 1 MiB + guard + context, VM window 4 GiB lazy, AHCI 34
  pages = 136 KiB, FAT scratch 18 pages = 72 KiB, ELF app stack 32
  KiB, app static GC 1 MiB, HandleTable ~2 KiB.
- §11 code map расширен (Fat32.cs, UefiBootInfoBuilder.cs,
  Thread.cs, ThreadStubs.cs).

## 6. Build pipeline

- `run_vbox.ps1`: array-splat (`@(...)`) → hashtable-splat (`@{...}`)
  чтобы `-Configuration` биндился по имени, а не позиционно.
- `build_media_xorriso.ps1`:
  - ESP image size 64 → 128 MiB, VHD disk 128 → 256 MiB, ISO boot
    16 → 96 MiB (ESP source распух с CoreCLR runtime).
  - `-NoIso` switch для skip'а ISO build.
  - `run_vbox.ps1` пробрасывает `-NoIso = $true` если ни `-AttachIso`
    ни `-BuildIso` не задано (дефолт = только VHD).
  - Проверка `EspSource` теперь стоит **после** `run_build.ps1`, а
    не до — иначе на чистом дереве падало раньше чем сборка успевала
    наполнить директорию.

## Effect

VirtualBox теперь полноправно бутится:
- GPT mount работает (`DiagPath=3`).
- CoreLib читается ~360 AHCI-командами вместо 47000.
- Никакого corruption — multi-PRDT путь правильный.
- BigStack изолирован от GcHeap, fault classifier даёт точные
  результаты, layout-зависимости устранены.

Census под VBox: тот же что под QEMU (`OK=42 DEG=2 FAIL=7`).

## Files

- `OS/src/Hal/Fat32.cs` — GPT branch, FAT cache, contig coalesce,
  CopyBlock, public BytesPerSector/SectorsPerCluster/BulkReadBytes,
  DiagGptEntries/DiagGptFirstLba.
- `OS/src/Hal/Ahci.cs` — totalCount/remaining split, Buffer += 8K,
  DisableInterrupts wrapper, InterruptOnCompletion=false, Count=0
  guard, telemetry counters, HPET-wrapped Read().
- `OS/src/Kernel/Diagnostics/FatProbe.cs` — bps/spc/bulk reporting,
  gptEnt/gptLba in FAIL line.
- `OS/src/Boot/BootSequence.cs` — `AllocateBigStack` via
  `PhysicalMemory + VirtualMemory.MapFixed`, `[bigstack]` diag.
- `OS/src/Kernel/Memory/BigStack.cs` — обновлены комментарии,
  убран workaround-narrative.
- `OS/src/Boot/EH/HwFaultBridge.cs` — обновлён classification
  комментарий.
- `OS/src/PAL/SharpOSHost/StackBounds.cs` — обновлён комментарий
  про bigstack/hosted thread path.
- `OS/src/Hal/Platform.cs` — `[fsread]` timing trace для файлов
  >= 1 MiB (probe_ms/alloc_ms/read_ms/total_ms/ahci_cmds/sectors/ahci_ms).
- `OS/src/Boot/ExitBootServicesProbe.cs`, `OS/src/PAL/SharpOSHost/CrtHeapStubs.cs`,
  `OS/src/PAL/SharpOSHost/Diagnostics.cs` — мелкие сопутствующие правки.
- `docs/memory-ownership.md` — новый документ + §10 Cumulative Budget.
- `run_vbox.ps1` — новый VBox runner.
- `build_media_xorriso.ps1` — bumps, NoIso, check ordering fix.

## Lessons learned

1. **Развязка переменных по семантике** — если одна `ushort Count`
   используется как (а) loop-decrement, (б) PRDTLength formula
   input, (в) ATA FIS sector count — это три разных значения, не
   одно. Реюз → corruption через тонкий путь, диагностируется
   почти невозможно. Запись в memory: новый feedback.

2. **Heisenbug = invariant violation, не «лишнее»** — когда
   удаление кажущегося-избыточного действия (второго CoreLib read)
   ломает boot, это **не значит что действие было нужно**. Это
   значит что под скрытой инвариантой что-то держится через case
   layout. Здесь — BigStack inside GcHeap. Чинить корень, не
   возвращать «магический» extra read.

3. **multi-PRDT path был dead code до first contact** — в
   AHCI-драйвере 2+ года жил overlap-bug в loop, который никогда
   не выполнялся (все callers Count=16, single PRDT). Bump
   BulkBytes до 64K мгновенно его триггернул. Dead code не
   protect'нул — он просто откладывал момент когда баг проснётся.

4. **VBox AHCI emulation чувствительнее к interrupts** — нужно
   систематически глушить (controller + per-port + per-IO) и
   ставить `InterruptOnCompletion=false`. QEMU был forgiving к
   этому, VBox — нет.

5. **Cumulative Budget таблица в memory-ownership.md** —
   превращает doc из текстового описания в инструмент capacity
   planning. Видно сразу куда уходят 100+ MiB на boot и где
   именно тех-долг будет больно (CoreLib 22.5 → 32 MiB).

## Открытые шаги (future)

- M1/M2 из memory-ownership.md §9: CoreCLR native CRT arena
  + PE/file image arena. После них можно снять `GC.ReclamationDisabled`
  freeze и вернуть kernel GC sweep во время hosted session (M5).
- Полноценный RW FAT32 (mkdir/create/write/delete + FAT mirror
  + FSInfo + cluster allocator + LFN write). Сейчас RO — closes
  census FAIL #2-4 («\sharpos\probe.txt»). Серьёзная задача,
  откладываем.
- `[ebsx] fb=FAIL` на VBox — golden CRC привязан к 1280x800
  (QEMU OVMF default), VBox EFI выдаёт 1024x768 → CRC другой,
  рендер при этом успешен. Либо записать второй golden, либо
  переписать oracle нормированно (CRC по logical glyph-bitmap'у).
