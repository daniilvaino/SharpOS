# Step 82 — Phase E0: pre-flight verifications PV1/PV2/PV3

**Status:** CLOSED (read-only investigation). Findings recorded in
`docs/threading-architecture.md` §17 — gating inputs for E1
(early page-table activation + XCR0 lock).

## Что это и зачем

`docs/threading-architecture.md` §15 первой строкой просит E0:
прежде чем ехать в E1 (page-table activation) и E2 (TEB facade /
gs base swap), снять три факта с живого ядра, чтобы не делать
архитектурные допущения вслепую:

- **PV1** — как сегодня выставлен `gs base`? Есть ли уже механика
  swap'а, которую можно переиспользовать на каждом context switch,
  или E2 требует написать новый wrmsr-shellcode?
- **PV2** — в каком состоянии XCR0 на момент входа CoreCLR? Если
  AVX bits включены firmware'ом, JIT увидит OSXSAVE=1 и эмиттит
  VEX → YMM регистры → наш FXSAVE (512 B, только xmm0-15) тихо
  обрежет верхние половины ymm на каждом switch.
- **PV3** — реально ли страничный clone (`s_rootTable`) сегодня
  ничего не делает (CPU CR3 == `s_kernelRootTable`), и что
  лежит в каждом из двух PML4? Если ELF-приложения уже бегут
  через `Pager.Map` (запись в clone), но clone не активен — как
  они работают?

E0 read-only: ни одной правки в исполняемом коде. Только grep,
read, и запись findings в §17 канонического документа.

## PV1 — gs base

`OS/src/Kernel/Diagnostics/CoreClrProbe.cs:600` эмитит wrmsr
shellcode в `AsmExecBuffer` offset 64: `B9 01 01 00 C0 0F 30`
(mov ecx, 0xC0000101; wrmsr) — пишет `IA32_GS_BASE = &teb`
один раз на boot. TEB layout совпадает с зафиксированным §6:
`gs:[0x10]` StackLimit, `gs:[0x30]` Self, `gs:[0x58]` TLS,
`gs:[0x60]` PEB, `gs:[0x68]` LastError. MSR ID **правильный**
(`0xC0000101` — IA32_GS_BASE), не `0xC0000102` (тот SWAPGS-
specific, в ring-0 unikernel'е irrelevant).

**Вывод.** Shellcode reusable. E2 — один indirect call с
адресом следующего TEB. Никакого нового asm не пишем.

## PV2 — XCR0

Grep по kernel + fork PAL: НОЛЬ упоминаний `xsetbv` / `XCR0` /
`AVX`. Мы XCR0 не трогаем вообще. Firmware-dependent state.

**Риск.** На современных QEMU/UEFI XCR0 обычно с AVX (bit 2),
иногда AVX-512. JIT CoreCLR проверит OSXSAVE через cpuid → AVX
доступен → эмиттит VEX → использует YMM → FXSAVE (512 B без
YMM) на context switch урежет верхние 128 бит каждого ymm
**молча**. Это classic «работает один thread, ломается на
двух» баг.

**Lock (входит в E1 preamble).** Перед первым JIT-callable
маппингом — explicit `xsetbv` byte-shellcode, выставляющий
XCR0 = x87|SSE (0x3). С AVX off JIT эмиттит SSE-only код,
FXSAVE достаточен (§5 уже фиксировал FXSAVE-only policy; PV2
дал недостающую enforcement-точку).

## PV3 — page-table active vs inactive

`OS/src/Kernel/Paging/X64PageTable.cs:58-94` (`Init`):
читает live CR3 → `s_kernelRootTable` (firmware PML4),
recursive deep-clone → `s_rootTable`, `s_pagerCr3` set,
`TryActivatePagerCr3()` объявлен но **никогда не вызывается**
(см. комментарий line 250: «we never call TryActivatePagerRoot,
so kernel CR3 == firmware CR3»).

| API | пишет в | CPU видит сегодня |
|---|---|---|
| `Pager.Map` → `X64PageTable.Map` | `s_rootTable` (clone) | НЕТ |
| `VirtualMemory.MapFixed/Map` → `X64PageTable.MapKernel` | `s_kernelRootTable` (firmware) | ДА |
| `TrySetKernelFlags*` | `s_kernelRootTable` | ДА |

Caller audit:
- **invisible writes** (Pager.Map): JumpStub, ElfLoader,
  ElfValidation, ProcessImageBuilder, ProcessManager,
  AppServiceBuilder, PagingValidation
- **visible writes** (MapKernel via VirtualMemory.MapFixed):
  Framebuffer, Pci, Ahci, SharpOSHost.Memory (JIT exec)

**Почему ELF apps бегут.** UEFI identity-maps весь
адрес-спейс в своей firmware PML4. ELF code/data живут на
PA == VA в уже-замаппленных firmware-range'ах. Loads/stores
работают через активную `s_kernelRootTable`. `Pager.Map`
кладёт записи в clone «для нашей бухгалтерии», но CPU clone
не видит. Clone сегодня **write-only ledger**.

**E1 hazard.** Если наивно вызвать `TryActivatePagerCr3()`
сейчас (после того как MapKernel уже населил
`s_kernelRootTable` для FB/AHCI/PCI/JIT), clone для этих
ranges пустой → boot валится на первом access к FB MMIO
/ AHCI MMIO / PCI ECAM / JIT-emitted code page.

**E1 fix (Sage 2 H3 в §2 локирует именно это).** Активировать
clone **рано**, сразу после `Init()`, ДО любого
`VirtualMemory.MapFixed`. После активации MapKernel
редиректится в `s_rootTable` (now active). Cleanest model:
**одна таблица, точка**. PV3 подтвердил выполнимость:
`Map` и `MapKernel` отличаются только полем root-table'а,
рефактор literal.

**Boot-order constraint.** Активация — между возвратом
`X64PageTable.Init()` и первым `VirtualMemory.MapFixed`
(сегодня это `Framebuffer.Init` в `OS/src/Hal/Framebuffer.cs:51`).
Один `TryActivatePagerCr3()` (уже реализован) + неявный
TLB invalidate через `mov cr3, …`.

## Артефакты

- `docs/threading-architecture.md` §17 — полный текст findings
  с таблицами, ссылками на код, и формулировкой E1 hazard.
- `docs/threading-architecture.md` §15 строка E0 — статус
  обновлён с «Documented findings; PV1 either confirmed or
  planned» на «DONE — findings in §17».

## Что дальше

E1 — page-table early activation + XCR0 lock. Два независимых
изменения, оба готовы к реализации:

1. В boot sequence: сразу после `X64PageTable.Init()` —
   `TryActivatePagerCr3()` + `xsetbv` shellcode (XCR0 = 0x3).
2. В X64PageTable: `MapKernel` редиректить в `s_rootTable`
   (после активации) или альтернативно дропнуть
   `s_kernelRootTable` целиком и держать один root.

E1 — первое реальное code-change за Phase E. После него можно
смотреть E2 (TEB facade + gs base swap).

## Файлы

- M `docs/threading-architecture.md` (+108 lines)
  - §15 — E0 status updated
  - §17 (new) — PV1/PV2/PV3 findings + summary table
- A `done/step082.md` (этот файл)
