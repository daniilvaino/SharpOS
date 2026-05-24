# Step 109 — NativeArena: native blobs out of kernel GcHeap (M1-M4)

## Резюме

Все non-managed native blob'ы, которые жили в kernel `GcHeap.AllocateRaw`,
переехали в новую `NativeArena`. Это плановые M1/M2/M3 из
`docs/memory-ownership.md §9` плюс M4 (SEH/SHA256 структуры, TPA buffer).

Параллельно: PhaseReport — per-phase capacity baseline в логе (snapshot,
без Collect — почему именно snapshot, см. §10 в kernel-limits doc).

## Что переехало

14 call-sites `GcHeap.AllocateRaw` в 8 файлах теперь идут через
`NativeArena.Allocate(size)`. Что осталось в GcHeap — **только настоящие
managed kernel objects** (`new T()`, GcStatics через
`GcStaticsMaterializer`).

| Файл | Что | Размер |
|---|---|---|
| `CrtHeapStubs.cs` | `SharpOSHost_HeapAlloc` / `HeapRealloc` (CoreCLR CRT malloc/realloc) | variable |
| `CrtHeapStubs.cs` | `FileState` struct | 24 B |
| `CrtAndEhStubs.cs` | `_malloc_dbg` / `_calloc_dbg` / `_realloc_dbg` | variable |
| `CoreClrTeb.cs` | TEB / TLS slots / TLS block | per-thread |
| `TebFacade.cs` | TEB | per-thread |
| `SehDispatch.cs` | `ExceptionRecord` (×3) / `Context` (×5) / `DispatcherContext` (×2) | per-throw |
| `Sha256Bridge.cs` | `Sha256State` | per-handle |
| `CoreClrProbe.cs` | TPA NUL-terminated string buffer | KB single-shot |
| `Platform.cs` | File buffer (PE blobs) | KB — 22.5 MiB |
| `FatBootBridge.cs` | File buffer (PE blobs) | KB — 22.5 MiB |

## NativeArena дизайн

`OS/src/Kernel/Memory/NativeArena.cs` — bump allocator поверх
`PhysicalMemory.AllocPages` + `VirtualMemory.MapFixed`. Без free, без
freelist (та же семантика "forever" как было через `GcHeap` + no-op
`HeapFree`). Зато `GcHeap` теперь чистый.

Маршрутизация:
- `size < ChunkBytes` (64 KiB) — bump в текущем 64 KiB chunk'е; chunk
  пополняется из `PhysicalMemory.AllocPages(16)` по требованию.
- `size >= ChunkBytes` — dedicated page run через
  `PhysicalMemory.AllocPages(N)` (22.5 MiB CoreLib попадает сюда).

**Alignment policy** (важно — изначальные 8 байт ломали TEB и
Context при попадании на смежные малые allocations):
```csharp
private static ulong NaturalAlignment(ulong size)
{
    if (size >= PageSize) return PageSize;   // TEB, PE buffers
    return 16UL;                              // SSE/MOVAPS minimum
}
```

16-байтное минимум — SSE/MOVAPS требование (Context содержит
XMM_SAVE_AREA32). Page-aligned для >= 4 KiB — TEB layout и FileOpen
RWX page-walk оба полагаются на page boundaries.

## PhaseReport — capacity baseline

`BootSequence.PhaseReport(name)` запускается после каждого Phase[1-5]:

```
[phase1 done] kheap used=AKiB (+ΔKiB) free=BKiB blocks=N | arena total=CKiB (+ΔKiB) chunks=N direct=N allocs=N
[phase2 done] ...
```

Делает только snapshot — **не зовёт `KernelGC.Collect`**. См. §10 в
`docs/nativeaot-nostd-kernel-limits.md` про дисциплину `CaptureStackTop`
и почему out-of-discipline Collect = wild bit-flip corruption.

## NativeArena diagnostics

`NativeArena.TotalBytes / ChunkCount / DirectCount / AllocCount` —
public counters. PhaseReport их использует. Видно сразу:
- сколько физической памяти арена забрала
- сколько было refill-chunks (small/mid) vs dedicated runs (large)
- сколько раз дёрнули allocate

## Документация (§10 в limits doc)

Добавлен раздел "Kernel GC sweep" — почему conservative scan + wild
walker = bit-flip corruption на random памяти, какая дисциплина
работает (`CaptureStackTop` прямо перед `Collect`), и три варианта
долгосрочного фикса:

1. Precise GcInfo per stack frame (декодить то что AOT-компилятор
   уже эмиттит в `.pdata`/`.xdata`) — **target step 110**
2. Strict MT validation в EnumerateObjectReferences
3. Explicit-roots-only (без conservative scan)

## Прецедент

NativeArena — это **обобщение того что BigStack сделал в step 108**
(`AllocateBigStack` через `PhysicalMemory.AllocPages` + `MapFixed`).
Тот же паттерн, теперь стандартизирован для всех native blob
потребителей. Никаких новых абстракций — наоборот, **убираем GcHeap
из роли general-purpose allocator** там где он не нужен.

## Files

- `OS/src/Kernel/Memory/NativeArena.cs` — новый: bump arena, alignment,
  dedicated path, telemetry.
- `OS/src/PAL/SharpOSHost/CrtHeapStubs.cs` — HeapAlloc/HeapRealloc +
  FileState → NativeArena. Обновлена шапка-документация про новый roуt.
- `OS/src/PAL/SharpOSHost/CrtAndEhStubs.cs` — debug-CRT варианты.
- `OS/src/PAL/SharpOSHost/SehDispatch.cs` — все 10 GcHeap.AllocateRaw
  для ExceptionRecord/Context/DispatcherContext.
- `OS/src/PAL/SharpOSHost/Sha256Bridge.cs` — `Sha256State`.
- `OS/src/Kernel/Threading/CoreClrTeb.cs` — TEB/TLS slots/TLS block.
- `OS/src/Kernel/Threading/TebFacade.cs` — TEB.
- `OS/src/Kernel/Diagnostics/CoreClrProbe.cs` — TPA buffer.
- `OS/src/Hal/Platform.cs` — Platform.TryReadFile file buffer.
- `OS/src/Hal/FatBootBridge.cs` — FAT boot bridge file buffer.
- `OS/src/Boot/BootSequence.cs` — PhaseReport + `WriteSignedKiB` helper.
- `docs/nativeaot-nostd-kernel-limits.md` — §10 "Kernel GC sweep".
- `tools/probe_report.ps1` — ProcessSpawn / AllocStress regex
  tolerant к log-line splice (`[\s\S]{0,N}?` вместо `.*?`).
- `.gitignore` — добавлен `.vbox/` (VBox VM data dir).

## Effect

- **`GcHeap` чистый** — содержит только real managed kernel objects.
  Mark phase, теоретически, теперь работает только над known-managed
  graph'ом — НО на практике conservative scan всё ещё может ошибиться
  на stack'е (см. §10).
- `ReclamationDisabled = true` остаётся в `CoreClrProbe.cs:370` — снимать
  имеет смысл только после step 110 (precise GcInfo).
- Census регрессии **нет** — OK=42 DEG=2 FAIL=7 (как step 108).
- VBox parity сохранена.

## Lessons learned

1. **Прецедент в миниатюре сначала, потом обобщение.** BigStack-out-of-
   GcHeap (step 108) был доказательством концепции — простой
   `PhysicalMemory.AllocPages + MapFixed` решил Heisenbug. NativeArena —
   тот же паттерн, обобщённый для 14 call-sites. Не «новая абстракция»,
   а **стандартизированная форма уже доказанного приёма**.

2. **GcHeap.AllocateRaw — не general-purpose malloc.** Использование
   его как «есть свободная память, дайте» приводит к тому что GC
   sweep видит non-managed blobs и не понимает их. Лучше иметь
   несколько небольших арен с понятной семантикой (GcHeap для managed,
   NativeArena для native), чем один общий heap.

3. **Alignment matters early.** Первая попытка NativeArena с 8-байт
   alignment сразу же дала #GP в CoreCLR init (TEB/Context требуют
   16-байт или page). Натуральный alignment по размеру (16 минимум,
   PageSize для >= 4 KiB) — простая политика которая покрывает
   реальные потребности.

4. **`KernelGC.Collect` — операция-перед-операцией, не «init однажды».**
   Дисциплина `CaptureStackTop()` обязательна, ограничивает scan
   window. PhaseReport без неё попал в wild-walker bug — отличный
   маркер что текущий conservative GC опасен для casual использования.
   Без precise GcInfo не починить по-настоящему (target step 110).

5. **Conservative scan validator (Option B) был бы паллиативом.** Можно
   было бы добавить known-MT validation как "защиту от дурака", но
   правильный фикс — **читать precise GcInfo который AOT уже эмиттит**.
   Step 110 пойдёт по правильному пути.

## Открытые шаги (target step 110)

**Precise GcInfo from `.pdata`/`.xdata`:**
- Adapter: PC → RUNTIME_FUNCTION (есть в SehDispatch) → UnwindInfo blob
  skip → unwindBlockFlags + optional 4-byte fields → gcInfo pointer.
  ~50 строк.
- `GcInfoDecoder` C# port — AMD64-only, `DECODE_GC_LIFETIMES`-режим.
  Cross-checked против `dotnet-runtime-sharpos/src/coreclr/vm/
  gcinfodecoder.cpp` (no code copied). 300-500 строк.
- Replace conservative scan в `GcRoots.ScanStack` precise frame-by-
  frame walk через PE unwind tables.
- Flip `ReclamationDisabled = false` — M5 закрыт.

После 110 разблокируется Phase F: финализаторы, cooperative safepoints
для hosted-CoreCLR GC suspend, real RetainVM/decommit policy.
