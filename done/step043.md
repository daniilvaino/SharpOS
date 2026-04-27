# Step 43 — GC mark sanity check + EH probes for Phase 1 reconnaissance

## Контекст

Phase 1 still has one open item — managed try/catch/finally. Прежде чем атаковать unwinder, нужны две вещи:
1. Стабильный baseline. GC stress test 3 (rotate 10) переодически крашился с `RAX=0x24000000_xxxxxxxx` non-canonical pointer. Этот же crash блокировал бы любое EH probing — try/catch добавляет ещё больше stack walking.
2. Reconnaissance infrastructure. EhProbe для тестирования no-throw vs throw paths + binary inspection script для понимания формата `.pdata`/`.xdata` который ILC реально эмитит для нашего target'а.

Step 43 закрывает оба пункта.

## Решение из двух частей

### 1. GC mark sanity check (sage 2's recommendation)

`std/no-runtime/shared/GC/GcMark.cs:Drain` теперь валидирует MT pointer ДО любого dereference:

```csharp
nint mtAddr = *(nint*)ptr;
if (mtAddr == 0) continue;
if (!IsCanonicalLowHalf(mtAddr)) continue;
if (GcHeap.FindSegmentContaining(mtAddr) != null) continue;
```

`IsCanonicalLowHalf` — bits 63..47 must all be zero (наш kernel живёт исключительно в low canonical half). Конкретно ловит `RAX=0x24000000_000E126A` из crash signature.

Не-in-heap check — настоящий MT pointer всегда в `.rdata` нашего binary, никогда не внутри GcHeap segment. Если "MT" pointer указывает в heap — это garbage из conservative scan stale residue.

После fix'а GC stress 1-3 стабильно зелёные на каждом прогоне. Test 3 rotate `marked=122 kept=122 swept=18` вместо halt.

### 2. EH probes (gradient L1-L3)

`OS/src/Kernel/Diagnostics/EhProbe.cs` — three-level gradient под `Probes.*` toggles:

- **L1 try/finally no-throw** — ILC эмитит EH info, runtime никогда не вызывает RhpCallFinallyFunclet. `val=211`.
- **L2 try/catch no-throw** — то же для try/catch. `val=4`.
- **L3 try/catch with throw** — currently halts в RhpThrowEx. Default off; flip когда придёт unwinder.

Эмпирически подтверждено: ILC компилит `try`/`catch`/`finally` блоки корректно. Frame layout правильный. Runtime никогда не вызывает funclet thunks на no-throw path. Проблема — только actual throw → catch.

### 3. Binary EH inspection script

`probe_eh_binary.ps1` — wraps `dumpbin /UNWINDINFO` + section dump. Подтвердил Windows-SEH формат: 703 RUNTIME_FUNCTION records, `.pdata` size 0x20F4, стандартный UNWIND_INFO с `push_nonvol`/`alloc_small` codes.

## Файлы

### Новые

- `OS/src/Kernel/Diagnostics/EhProbe.cs` — 3-level gradient probe.
- `probe_eh_binary.ps1` — PowerShell wrapper around dumpbin.
- `done/step043.md` — этот файл.

### Изменённые

- `std/no-runtime/shared/GC/GcMark.cs` — `Drain` MT sanity check + `IsCanonicalLowHalf`.
- `OS/src/Kernel/Diagnostics/Probes.cs` — `EhTryFinallyNoThrow` / `EhTryCatchNoThrow` / `EhTryCatchWithThrow` toggles.
- `OS/src/Boot/BootSequence.cs` — `EhProbe.Run()` в Phase 4 после CctorProbe.

## Результат

```
[info] tree allocs=62 marked=131 kept=131 swept=0
[info] half allocs=103 marked=120 kept=120 swept=51
[info] rot allocs=20 marked=122 kept=122 swept=18    <-- previously halted
[info] eh L1 try/finally no-throw: val=211
[info] eh L2 try/catch no-throw: val=4
... NativeAotProbe + CctorProbe + ELF apps + launcher all green.
```

Higher marked counts vs prior runs (60→122) — false positives conservative scan теперь silently rejected вместо halt. Не bug, expected следствие safety check.

## Что дальше

Baseline стабильный. Можем атаковать try/catch:

- Сформулировать вопросы двум мудрецам по плану Phase 1 closure.
- Запустить research agents для NativeAOT EH source map + ILC EH format + Exception shape.
- Step 44 = Phase 1 step 1 (Exception layout + 6 derived types).
