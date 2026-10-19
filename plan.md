# SharpOS roadmap — единый актуальный план

Дата актуализации: 2026-06-30 (после step127).

Этот файл — единственный рабочий roadmap проекта. Старые PAL D1-D20
decision-файлы считаются архивом: они остаются как история reasoning'а,
но не являются отдельным планом и не должны читаться как current status.
Все живые направления сведены ниже.

Пошаговые writeup'ы остаются в `done/stepNN.md`. Текущие user-facing
ограничения hosted .NET — в `docs/coreclr-hosted-limits.md`. Открытые
симптомы без полноценного step'а — в `docs/open-symptoms.md`.

---

## 1. Текущий baseline (2026-06-30)

SharpOS прошёл крупный milestone — **стоковый PowerShell 7.5.5 запускается
на bare metal до интерактивного prompt'а и выполняет реальные cmdlet'ы**
(step 124-127):

- `pwsh -nologo` загружается с FAT32, инициализирует runspace, печатает
  prompt.
- `ls`, `cd`, `cat`, `Get-ChildItem`, `Get-Content`, `Get-Process` (stub),
  `[DateTime]::Now`, pipelines, переменные — работают.
- PSReadLine / ConHost совместимый ANSI: SGR colors, cursor moves,
  `Clear-Host` — рендерятся на framebuffer консоли.
- Backspace, стрелочки, escape-последовательности — обрабатываются.
- FAT32 чтение со statful cursor'ом (O(N) enum), Fs.Stat без слурпа
  файлов целиком, NativeArena с freelist'ом, TLB-flush elision.

Текущие user-facing ограничения PS:

- **ConstrainedLanguage Mode** активен — `[Math]::Sqrt(16)` блокирован.
  Все попытки flip'нуть в FullLanguage через консенсус WLDP/Safer/
  AppLocker стабов в PS 7.5 fail-secure'ят. См. `done/step126.md` для
  деталей; отдельный шаг отложен.
- **Залип ввода во время фоновой работы** — кооперативный single-CPU
  scheduler не вытесняет ThreadPool worker'а пока он JIT'ит/работает
  без `Yield`. Visible симптом post-step127 после того как probe-шум
  ушёл. Чинится preemption'ом (Phase F-prempt).
- **`Set-Content` / write paths** — FAT32 RO. Любая модификация —
  read-only error.

Текущие три execution tier'а — kernel-AOT, ELF-app AOT, CoreCLR-hosted —
все green в боевом режиме. Census FAIL'ы (~7-20 в зависимости от probe
set) — отдельные API не-implemented, без архитектурного запрета.

> **NB о прошлом плане:** до 2026-05-29 plan.md говорил "PowerShell
> deferred indefinitely, after Roslyn". Реальный путь оказался
> противоположным — PowerShell мы подняли **до** Roslyn'а, использовав
> его как самый требовательный stress test всего стека (TPL, EH,
> reflection, GC, FAT32, ANSI console). Roslyn REPL как отдельная веха
> снят с roadmap'а (см. §7).

---

## 2. Архитектура исполнения

SharpOS has three execution tiers:

| Tier | Meaning | Status |
|---|---|---|
| Kernel-AOT | SharpOS kernel, drivers, scheduler, PAL provider, NativeAOT/no-std | primary system tier, green |
| ELF-app AOT | external SharpOS apps (`HELLO.ELF`, `ABIINFO.ELF`, `HELLOCS.ELF`, etc.) | launcher green |
| CoreCLR-hosted | stock .NET DLLs loaded through forked CoreCLR | Release-hosted green, OS surface incomplete |

Hard boundaries:

- Kernel and hosted CoreCLR heaps are separate ownership domains.
- `SharpOSHost_*` is the C ABI boundary.
- No managed object references cross that ABI as raw stored pointers.
  Hosted references must be CoreCLR handles; kernel references stay kernel.
- PAL is a translation layer, not the place for kernel policy.

---

## 3. Consolidated D1-D20 outcome

D1-D20 are no longer a separate active checklist. Their useful outcomes
are collapsed into these current rules:

1. Use stable C ABI status codes at the `SharpOSHost_*` boundary.
2. Do not let exceptions cross the C ABI boundary. Convert expected
   errors to status codes; fail loud on unexpected violations.
3. PAL is thin and domain-split by function area (`memory`, `thread`,
   `file`, `sync`, `time`, `exception`, etc.).
4. CoreCLR is statically linked into the final SharpOS image. No `dlopen`,
   no separate `SharpOSHost.lib` provider model.
5. `SharpOSHost_*` provider binding is done by the linker. No runtime API
   table.
6. Any fallback provider inside the fork must be `weak` or declaration-only.
   Strong fallbacks are forbidden because Release codegen can fold calls
   before the final kernel export wins.
7. C++/Win32 helpers in the fork are acceptable only inside the
   fork/CoreCLR boundary. Kernel implementation remains C#.
8. Windows APIs in `pal/sharpos` are not architectural dependencies.
   The intended end state is a D11-style firewall/audit.
9. `.pdata`/Windows x64 unwind metadata is the canonical unwind model.
10. D5/D6/D8 have been reopened by reality:
    - D5 "threads abort" is obsolete for current Phase E; real hosted
      threads exist.
    - D6 ownership is split: kernel thread carrier + opaque CoreCLR
      binding.
    - D8 GC suspension is not solved; it moved to production CoreCLR work.

Historical D-files stay in `work/PAL/D1-D20 FINALIZED/` only as archive.

---

## 4. Current Status By Phase

| Area | Status | Notes |
|---|---|---|
| A — milestone freeze | green | step125 PS-on-bare-metal milestone закреплён |
| B — native console/drivers | green core | serial, framebuffer + full ANSI, PS/2, line editor, PCI scan green |
| C — post-EBS survival | green core | EBS + reroute green; IST/emergency stacks остаются (R2) |
| D — SehUnwind | closed | step112-114 |
| E — cooperative threading | green | kernel threads, waits, sleeps, process spawn, CoreCLR ThreadPool/Task/Timer; залип ввода — отдельный F-prempt фронт |
| F — hosted CoreCLR production | green core | Release host + FH4 + full PS bootstrap; GC suspend/finalizers/RetainVM policy не production-complete |
| F-prempt — preemptive scheduler | not started | HPET/APIC timer IRQ + ISR-Yield; разблокирует UX (PS залип) |
| E′ — storage/filesystem | partial | AHCI + FAT32 read green; FAT32 write 🔴 — следующий capability фронт |
| ~~G — Roslyn REPL~~ | snipped | заменён на CSharpRepl bring-up (см. §7) |
| H — PowerShell | **green core** | stock 7.5.5 boots; CLM-mode и write-FS остаются |
| I — hardware expansion | deferred | network, SMP, USB |
| J — EH P0 hardening | open | donext.md L18 (XMM-across-catch) + L19 (CollidedUnwind) latent corruption |

---

## 5. Active Risks

### R1 — Release static binding regressions

The step114 `malloc -> xor eax,eax; ret` bug proved that strong fallback
definitions inside fork translation units can break Release before the
kernel export participates in final linking.

Required hardening:

- Audit every `SharpOSHost_*` fallback in the fork.
- Fallback implementations must be `__attribute__((weak))` or removed in
  favor of declarations.
- Concrete grep gate for the known-bad pattern:
  `grep -nE 'extern "C" .*SharpOSHost_.*\{' dotnet-runtime-sharpos/src/coreclr/pal/sharpos/*.cpp | grep -v __attribute__`
  must return only intentional strong provider symbols, never fallback
  stubs.
- Add a binary/symbol smoke for `malloc`, `calloc`, `realloc`,
  `__imp_malloc`, `_callnewh`.
- Treat this as D10/D11 production hardening.

### R2 — Silent triple fault on stack overflow

Step113 fixed the known infinite `sqrt <-> lm_sqrt` recursion, but the
system can still triple fault silently if #PF/#DF handlers fault on an
overflowed stack.

Required hardening:

- Add IST/TSS stacks for #PF/#DF/NMI or equivalent emergency fault stacks.
- Panic path must avoid managed allocation.
- Keep QEMU `-d int,cpu_reset,guest_errors -no-shutdown` as the first
  diagnostic tool for any silent exit.

### R3 — Hosted finalizers

`GC.WaitForPendingFinalizers()` hangs (`SYM-003`). Current theory:
finalizer thread/event completion path is incomplete.

Required investigation:

- Confirm whether finalizer thread starts.
- Confirm finalizer queue drain.
- Confirm finalizer-done event signaling.
- Decide whether finalizers are required before Roslyn or can remain
  parked until later F work.

### R4 — Hosted GC production mode

CoreCLR-hosted execution is green, but production GC cooperation is not
closed.

Open items:

- GC suspend/resume cooperation with scheduler.
- RetainVM/decommit policy.
- Hosted heap cleanup.
- Cross-GC reference discipline audit.

### R5 — Storage gap for Roslyn

Current read path is enough for boot/launcher/probes. Roslyn needs more
predictable `System.IO` probing and package resolution.

Open items:

- Hosted assembly resolver.
- Deterministic file probing diagnostics.
- FAT32 read completeness under real Roslyn closure.
- FAT32 write is deferred unless persistence becomes required.

---

## 6. Immediate Next Steps

Текущее направление (с 2026-10-18) — §11: асинхронный ввод-вывод, затем сеть.
Ниже — приоритеты на момент step127, часть закрыта (вытеснение, запись FAT32).

### Tier A — снижение риска (до новых фич)

1. **IST / emergency fault stacks** (R2) — stack overflow → silent
   triple fault убивает диагностику. Самый болезненный класс багов в
   кооперативе. Не feature work — это инфраструктура.
2. **L18 XMM-across-catch fix** (donext.md P0-1) — RyuJIT эмитит
   `UWOP_SAVE_XMM128`, наш decoder опкоды 8/9 глотает. Тихая corruption
   `double`/`float` через try/catch. Probe-then-fix задокументирован.
3. **L19 CollidedUnwind fix** (donext.md P0-2) — SehDispatch и
   RtlUnwind игнорируют `ExceptionCollidedUnwind` return. Latent HALT
   или потеря replacement-exception.
4. **R1 fork-fallback grep gate** — `extern "C" SharpOSHost_* { ... }`
   strong fallback'и → `weak`. Превентивно после урока step114.

### Tier B — UX разблок

5. **Preemptive scheduling** (F-prempt) — HPET/APIC timer IRQ → ISR-
   Yield. Фиксит залип ввода при фоновом JIT'е PS ThreadPool worker'а.
   Самый видимый UX win.
6. **CLM → Math.Sqrt / FullLanguage** — managed-инжект
   `SystemPolicy.s_systemLockdownPolicy = SystemEnforcementMode.None`
   через reflection до первого `InitialSessionState.CreateDefault2`.
   Маленький, но очевидный visible win.

### Tier C — capability

7. **CSharpRepl bring-up** (см. §7) — следующий "showcase" workload:
   stock CSharpRepl от waf, использует Roslyn interactive + Spectre.
   Console + ANSI rendering. Меньше surface чем PS, но раскрывает
   terminal rendering pipeline и Roslyn scripting на bare metal.
8. **FAT32 RW** — `Set-Content`, `New-Item`, `Out-File`. Без записи
   "настоящий unikernel" history не пишется. Алгоритм документирован
   (DiscUtils.Fat MIT референс), нужны AllocCluster + chain extend +
   dir entry write + LFN write + FSINFO update.
9. **PS regression battery** — после стабилизации 5-8, чтобы регрессии
   не съели прогресс. Subset PS-скриптов гоняется на каждом commit'е.

### Tier D — дальний

10. **R2R BCL** — cold start tax. Сейчас Linux-IL BCL без R2R,
    JIT'имся холодно.
11. **SYM-003 hosted finalizers** — `using`/Dispose-heavy код.
12. **R4 GC suspend/resume + RetainVM** — длинные сессии.
13. **libm port: fork crt_imp_stubs.cpp → kernel C# через `[RuntimeExport]`** —
    архитектурная чистка. Сейчас `lm_sin`/`lm_cos`/`lm_log`/`lm_exp`/
    `lm_pow`/`lm_tan`/`lm_atan`/`lm_sqrt`/... (~25 реальных kernels +
    ~47 thin double/float wrappers, всего ~72 символа) живут в форке
    `dotnet-runtime-sharpos/src/coreclr/pal/sharpos/crt_imp_stubs.cpp`.

    **Проблема:** инвариант 1 (C# only) нарушается для НОВОГО кода
    написанного нами в форке (не vendored upstream). Когда kernel
    самому понадобится sin/cos/log (графика, ML, аудио, физика —
    что угодно с FP) — придётся либо как-то цеплять fork-функцию
    из kernel'я (грязно, kernel может работать без fork'а), либо
    писать **вторую** реализацию. Две имплементации = расхождение,
    два места править, два места регрессить.

    **Цель:** один lm_* набор в `OS/src/Std/Math/Libm.cs`, помеченный
    `[RuntimeExport("sin")]` / `[RuntimeExport("cos")]` / etc. CoreCLR
    fork `extern "C" sin` резолвится туда же через `/FORCE:MULTIPLE`
    (уже включён). Kernel Math.Sin использует напрямую без P/Invoke.

    **Объём:** ~150-200 строк C# (тривиальная арифметика без аллокаций,
    `union DBits` → `BitConverter.DoubleToInt64Bits`) + ~50 строк
    boilerplate wrappers. День-полтора работы.

    **Why это backlog а не Tier A:** не блокирует ничего. Sin/cos
    работают (см. step128). Это чисто **архитектурная чистка** перед
    тем как kernel начнёт использовать FP. Триггер — первый kernel
    workload с FP (графический rendering, audio mixing, ML inference)
    или просто желание выровнять под инвариант.

### Tier E — вне scope этого этапа

- Network — теперь направление: §11 «Асинхронный ввод-вывод, затем сеть»
  (порт завершения ядра — общий слой для файлов и сети).
- SMP / multicore.
- USB.
- Firecracker virtio-mmio.

---

## 7. CSharpRepl path (заменяет старый Roslyn REPL)

**Старая трактовка "Roslyn REPL" (свой CSharpScript.EvaluateAsync обвес)
снята** — слишком абстрактно, слабый user value. Вместо неё —
конкретный готовый тул:

[CSharpRepl by waf](https://github.com/waf/CSharpRepl) (MIT) — кросс-
платформенный командлайн C# REPL. Features:

- Roslyn interactive scripting.
- Syntax highlighting via ANSI escape sequences (наш FbTty ANSI parser
  это покрывает).
- Intellisense, doc tooltips, overload navigation.
- Rich Spectre.Console formatting.
- Дамп объектов с цветами.
- IL disassembly + lowered C# decompilation (ILSpy).

Дроп лежит в корне репо: `C:\work\OS\CSharpRepl-main\`.

### Почему именно CSharpRepl как следующий showcase

- **Меньше surface чем PS** — нет cmdlet engine'а, нет module loading,
  нет SnapIn'ов, нет CLM-машинерии.
- **Больше terminal rendering** — Spectre.Console кидает плотный поток
  ANSI с cursor moves, color changes, diff-based rendering. Стресс-
  тест для FbTty parser'а который мы построили в step126.
- **Roslyn interactive** — настоящий scripting REPL, не EvaluateAsync
  one-shot. Требует assembly resolver работающий для Roslyn closure,
  но без NuGet-package install machinery (можно отрезать).
- **Маленький managed surface** — несколько проектов: CSharpRepl,
  CSharpRepl.Services, InjectedHook. Все .NET 10.

### Minimum prerequisites для bring-up

- ✅ Stock CoreCLR-hosted (есть со step110+).
- ✅ FAT32 read (есть).
- ✅ ANSI escape parsing на framebuffer (step126).
- ✅ Console keyboard input + line editing (есть).
- ⏳ Roslyn closure assemblies в `\sharpos\` (нужно положить DLL).
- ⏳ Stubbed-out network paths (NuGet install, source-link отключить).
- ⏳ Stubbed-out FS write paths где можно (cache, themes — read-only
  default theme inline).

### First acceptance

```
> 1 + 1
2

> Console.WriteLine("Hello")
Hello
```

Без intellisense / без NuGet / без AI completion — bare metal C# REPL
с цветами и редактированием строки.

### Объём

Оптимистично 1 неделя если Roslyn loader заведётся сходу. Реалистично
2-3 недели — будут вылезать missing API в `System.Reflection.Metadata`,
`Microsoft.CodeAnalysis.*`, какие-нибудь nethost lookup paths.

---

## 8. PowerShell Path — уже сделано

PowerShell 7.5.5 работает на bare metal со step124-127. Полная история
в `done/step124..127.md`. Текущие open items для PS:

- **CLM → FullLanguage** ([Math]::Sqrt блок) — см. step126.md
- **FAT32 RW** для `Set-Content` / `Out-File`
- **Preemption** для UX (залип ввода)
- **`GZipStream`, `Process.Start`, `DateTime.Now` (local TZ)** — известные
  лимиты, см. README таблицу.

Никакой "minimal PowerShell" не отдельный проект — мы запустили stock.
Расширение поверхности — incremental, по мере натыкания workload'ов
на missing API.

---

## 9. Deferred Hardware/OS Expansion

Deferred до конкретного триггера:

- preemptive scheduling — в Tier B выше, не deferred (запланирован).
- SMP,
- virtio-net/TCP/IP/DNS/TLS,
- USB,
- crash dumps to disk,
- full GUI/windowing/audio,
- **Firecracker microVM drivers** — virtio-mmio (не PCI) transport + virtio-blk / virtio-net / virtio-vsock + i8042-less serial console. Цель: запуск SharpOS как guest под Firecracker (AWS microVM hypervisor) для serverless/функциональных сценариев — быстрый cold-start, минимальная attack surface. Virtio-mmio лежит ниже virtio-{blk,net} в стеке (transport-слой, не device-слой); общая часть с virtio-PCI — устройства, разное — discovery (DT/ACPI MMIO vs PCI config space) и notify path (MMIO write vs PCI BAR). Триггер: реальный use-case или законченный virtio-net по PCI.

---

## 10. Documentation Rules

- `plan.md` is current roadmap.
- `done/stepNN.md` is historical evidence for each implemented step.
- `docs/coreclr-hosted-limits.md` is the live feature oracle for hosted
  .NET.
- `docs/open-symptoms.md` is for unresolved symptoms only.
- `work/PAL/D1-D20 FINALIZED/` is archive. Do not add new active work
  there; copy the surviving rule into this file instead.
- If a new direction becomes active, update this file in the same step
  that changes the implementation.

---

## 11. Асинхронный ввод-вывод, затем сеть (с 2026-10-18)

Текущее направление. Порядок этапов — ниже; номера шагов не фиксируются.

### Где мы сейчас (step208)

- Весь ввод-вывод синхронный и останавливает машину. Служба `FileRead` →
  FAT32 под одной блокировкой на всю систему (`Fat32.Lock.cs`) → `CachedDisk` →
  AHCI: прерывания порта выключены, одна команда в одном слоте, опрос
  `CommandIssue` с подавленным вытеснением (`Ahci.cs`). USB-диск — так же, опрос
  кольца событий xHCI. На одном процессоре всё это время не работает никто.
- Прерываний от устройств нет: только таймер локального APIC (вектор 0x20);
  IOAPIC лишь считается в MADT, MSI нет.
- `Iocp` / `IocpBridge` — LIFO-семафор для пула потоков CoreCLR, не порт
  завершения: без привязки к файлам и без overlapped.
- PAL `ReadFile`/`WriteFile` — синхронные, `OVERLAPPED` не используется.
- Сети нет: ни драйвера сетевой карты, ни стека TCP/IP.

Цель: ожидание устройства не держит процессор; один порт завершения ядра на
файлы, трубы и сеть; CoreCLR видит форму Windows (overlapped + порт), на которую
встаёт Windows-IL `System.Net.Sockets` (donext, «Сеть: Windows IL целиком»).

### Этап 1. Прерывания устройств (MSI)

- Обход списка возможностей PCI; MSI и MSI-X; векторы с 0x30; таблица
  обработчиков в IDT; EOI. IOAPIC не нужен: AHCI, xHCI и сетевые карты QEMU и
  стенда умеют MSI. Нет MSI — опрос, как сейчас, и строка об этом в логе.
- AHCI: прерывания порта (`PxIE`: D2H, SDB, ошибки) — обработчик отмечает
  завершённые слоты и будит ждущих.
- xHCI: interrupter 0 (`IMAN`, `IMOD`) — обработчик будит ждущих кольцо событий.

Готово: `[irq] ahci msi vector=… count=N`, `[irq] xhci …` в логе; батарея
28/28 и под AHCI, и под `-UsbOnly`; прогон на стенде.

### Этап 2. Драйверы ждут, а не крутятся

- AHCI: подать команду → `Event.Wait(timeout)` до прерывания, без
  `Preemption.Suppress` на время команды; очередь к порту — мьютекс ядра, ждущий
  спит.
- USB BOT: то же на событиях xHCI.
- Блокировка FAT32: ждущий спит (`SpinYieldLock` → мьютекс ядра).

Готово: во время чтения большого файла другой поток получает процессор
(`PIPEPERF --ipc-threads` рядом с чтением: его время растёт не больше чем в
2 раза); `sched.halt` > 0 во время одного только чтения; чтение не медленнее
прежнего (замер `[start]` для `KQL.EXE` с диска, `ProgramCache` выключен).

### Этап 3. Очередь команд AHCI

- До 32 слотов, разбор завершений по `PxCI`/`PxSACT`; NCQ (`READ/WRITE FPDMA
  QUEUED`), где поддерживается.
- Запрос драйвера: сектор, число, буфер, событие завершения.

Готово: два потока читают разные файлы — суммарная скорость выше одного потока
на стенде, под QEMU не ниже.

### Этап 4. Порт завершения ядра

- `CompletionPort`: очередь пакетов (ключ, байты, статус, адрес overlapped),
  привязка дескриптора к порту, `Post`, `Get(timeout)`, `GetMany(n)`; ожидание —
  `Event`/ожидание по адресу с таймаутом (step208), пробуждение LIFO.
- Порт — ресурс процесса: уходит с процессом вместе с невзятыми пакетами.
- Нынешний `Iocp` становится частным случаем (порт без привязок).

Готово: тесты в AOTTESTS — post/get, таймаут, LIFO, много ждущих, конец
процесса с ждущими и с полной очередью; `IocpBridge` поверх нового порта —
PowerShell и `Bench.dll` не медленнее.

### Этап 5. Асинхронные операции ФС

- Рабочие потоки ввода-вывода в ядре (2–4): `ReadAsync(handle, offset,
  buffer, key)` → синхронная операция FAT в работнике (её ожидания спят после
  этапа 2) → пакет в порт. Настоящая асинхронность внутри FAT — позже, если
  понадобится.
- Службы в хвосте ABI (версия не меняется): чтение и запись по смещению,
  асинхронные чтение и запись, порт (`Create`, `Bind`, `Get`, `Post`).
- SDK/std: `FileStream.ReadAsync`/`WriteAsync`, `RandomAccess` — поверх порта и
  нашего `async`/`await`; поток завершений приложения.

Готово: DATATEST — параллельные `ReadAsync` разных файлов, закрытие посреди,
ошибки диска, конец процесса с запросами в работе; копирование большого файла
асинхронно не медленнее синхронного.

### Этап 6. CoreCLR: overlapped и порт

- PAL: `CreateFileW` с `FILE_FLAG_OVERLAPPED`; `ReadFile`/`WriteFile` с
  `OVERLAPPED` → этап 5; `CreateIoCompletionPort` с привязкой файла,
  `GetQueuedCompletionStatus(Ex)`, `PostQueuedCompletionStatus`, `CancelIoEx`,
  `GetOverlappedResult`.
- Выяснить, какие пути нынешней Unix-IL CoreLib дойдут до overlapped; Windows-IL
  CoreLib — этап 0 сети, решать вместе с ним.

Готово: hosted-тест — `FileStream(useAsync: true)` `ReadAsync`/`WriteAsync`
через порт; PowerShell, `Bench.dll`, перепись PAL не хуже.

### Этап 7. Сеть

- Этапы из donext («Сеть: Windows IL целиком»): Windows-IL CoreLib и Sockets,
  ws2_32 PAL, сторона IOCP у пула потоков — на порту этапов 4 и 6.
- Чего там нет:
  - драйвер сетевой карты: QEMU — virtio-net или e1000 (с MSI из этапа 1);
    у ноутбука стенда RJ45 нет — USB-сеть CDC-NCM/ECM через наш USB-стек
    (телефон стенда умеет быть такой картой);
  - стек TCP/IP, DHCP, DNS — взять готовый по правилу «искать чужую
    библиотеку» (MIT/BSD/Apache; кандидатов сравнить отдельно), не писать свой.
- Порядок: кадры туда и обратно (ARP, ICMP) → UDP/TCP стека → сокеты ядра на
  порту → ws2_32 PAL → Windows-IL Sockets → `HttpClient`.

Готово: `ping` из оболочки до хоста QEMU (user-net) и обратно; `HttpClient`
GET к хосту из приложения и из PowerShell; то же на стенде.

### Зависимости

1 → 2 — главный выигрыш: машина перестаёт стоять, ABI не меняется.
3 — после 2, независим от остального. 4 — до 5, 6, 7. 6 — общий с сетью.
