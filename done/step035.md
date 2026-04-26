# Step 35 — Phase 0a: IDT + диагностический panic dump

## Контекст

После закрытия SUPER-1b (step 34) и согласования нового плана развития (Phase 0..7 вместо SUPER-1..12), приступаем к Phase 0. Первая задача фазы — установить IDT, чтобы любой `#PF/#GP/#UD/#DE/...` давал читаемый register dump в логе вместо triple-fault'а с моментальным ребутом OVMF.

До этого шага у нас не было своей IDT — действовала UEFI'шная, которой не хватает обработчиков для большинства exception'ов. Любой kernel-side bug, попадающий в `#PF`, превращался в triple-fault → OVMF reset → 30 минут расследования через QEMU log.

После этого шага каждый CPU exception попадает в наш C# dispatcher, который печатает RIP/CR2/error code/все 16 GP regs/RFLAGS/CS/SS — достаточно чтобы локализовать баг с одного boot'а.

## Цель

- 256-entry IDT с обработчиками для CPU-зарезервированных vectors 0..31.
- Per-vector entry stubs + общий dispatcher trampoline (shellcode).
- Managed `[UnmanagedCallersOnly]` dispatcher печатает PanicDump и halt'ит.
- LIDT helper для загрузки IDTR.
- MSI vector range 0x40-0xFE остаётся зарезервированным (не используется в этом шаге, нужен будет в Phase 5 для драйверов).
- Probe (gated `if (false)`) для ручной проверки: `int* p = null; *p = 42` → дамп вместо ребута.

## Что НЕ входит в этот step

- **TSS + IST для #DF на отдельном стеке** — single-fault уже даёт читаемый panic, double-fault triple-fault'ит как и раньше. Отложил на polish step или Phase 1.
- **iretq / recovery handlers** — dispatcher всегда halt'ит. Recovery + managed exceptions = Phase 1.
- **Hardware IRQ delivery** через MSI vectors — Phase 5 (драйверы).
- **GDT rebuild** — пользуемся UEFI GDT (CS=0x38 long-mode code selector). Trap gates ссылаются на этот же CS.

## Файлы

### Новые

- `OS/src/Hal/Idt/IdtDescriptor.cs` — 16-byte IDT gate descriptor + IdtRegister (10-byte IDTR для LIDT).
- `OS/src/Hal/Idt/InterruptFrame.cs` — struct с layout сохранённого фрейма (CR2 + 15 GP regs + vector + errcode + 5 CPU-pushed values).
- `OS/src/Hal/Idt/IdtTrampolines.cs` — shellcode emitter: per-vector entry stubs (16 bytes каждый) + common dispatcher stub (~80 bytes).
- `OS/src/Hal/Idt/PanicDump.cs` — формат-дамп фрейма через Console.*Raw (без heap allocation; вектор-имена 0..31; PF error-code декодер с битами P/W/U/RSV/I/PK/SS/SGX).
- `OS/src/Hal/Idt/Idt.cs` — `Install(BootInfo)`: пишет stubs, строит IDT, кладёт LIDT helper, дёргает его. Managed Dispatch — `[UnmanagedCallersOnly]`, зовёт PanicDump + spin.
- `OS/src/Kernel/Diagnostics/IdtProbe.cs` — `TriggerNullDeref()` для ручной верификации (gated в Kernel.cs `if (false)`).

### Изменённые

- `OS/src/Boot/BootInfo.cs` — добавлены поля `IdtExecBuffer` + `IdtExecBufferSize` (8 KiB).
- `OS/src/Boot/UefiBootInfoBuilder.cs` — `AllocatePool(EfiLoaderCode, 8192)` для IDT/trampolines/LIDT helper.
- `OS/src/Kernel/Kernel.cs` — `Idt.Install(bootInfo)` первой задачей (до banner'а), `RunIdtPanicProbe()` в конце boot'а с gate `if (false)`.

## Архитектурные детали

### Layout IdtExecBuffer (8 KiB EfiLoaderCode page)

```
0..4095        IDT (256 × 16 byte gate descriptors)
4096..4191     Common stub (~96 bytes)
4192..4703     Per-vector entry stubs (32 × 16 = 512 bytes)
4704..4707     LIDT helper (4 bytes: lidt [rcx]; ret)
```

EfiLoaderCode гарантированно exec даже при W^X (real HW).

### Per-vector entry stub (16 bytes)

Для vectors БЕЗ error code (0,1,2,3,4,5,6,7,9,15..18,20..31):
```asm
push 0           ; 6A 00      — dummy error code для uniform stack
push <vec>       ; 6A NN      — vector number (0-31 fits in imm8)
jmp common_stub  ; E9 disp32  — relative jmp на common
<NOP fill>       ; 90 ...     — pad до 16 байт
```

Для vectors С error code (8 #DF, 10 #TS, 11 #NP, 12 #SS, 13 #GP, 14 #PF, 17 #AC, 21 #CP):
```asm
push <vec>       ; 6A NN      — без dummy push'а, errcode уже на стеке от CPU
jmp common_stub  ; E9 disp32
<NOP fill>
```

### Common stub

Сохраняет 15 GP regs + CR2 на стек, формирует InterruptFrame*, зовёт managed dispatcher:

```asm
push   r15..r8                  ; 41 57..50 (8 регистров)
push   rbp,rdi,rsi,rbx,rdx,rcx,rax  ; 55 57 56 53 52 51 50
mov    rax, cr2                 ; 0F 20 D8
push   rax                      ; 50 — CR2 для дампа PF
mov    rcx, rsp                 ; 48 89 E1 — Win64 arg1 = frame*
sub    rsp, 0x28                ; 48 83 EC 28 — shadow + 8-byte align
mov    rax, [rip + dispOff]     ; 48 8B 05 ?? ?? ?? ??
call   rax                      ; FF D0
1: hlt; jmp 1b                  ; F4 EB FE — never reached
.qword <Dispatch addr>          ; патчится в Install()
```

### Stack alignment

CPU pushes 5 qwords (40 байт) на entry. Per-vector stub добавляет 2 qwords (non-error) или 1 qword (error vector + CPU's errcode). После него RSP+56 от исходной точки = +8 mod 16. Common stub добавляет 16 регистров (128 байт = 0 mod 16) → итого +8 mod 16 при входе в `mov rcx, rsp`. `sub rsp, 0x28` (40 байт = +8 mod 16) → 0 mod 16 при `call rax`. ✓

### Async hardware interrupts — две итерации фикса

**Попытка 1:** просто LIDT с нашими 32 trampoline'ами. Дала #GP сразу:
```
vector=0x0D (#GP general-protection)
errcode=0x0000000000000102
```
Errcode `0x102` = bit 1 (IDT) + selector index `0x20` (= vector 32). CPU попытался доставить hardware interrupt на vector 32 → IDT[32]=zero (Present=0) → #GP.

UEFI работает с IF=1. Hardware interrupt (Local APIC timer на 32) фаерится в любой момент. Пока работала UEFI'шная IDT, у неё был обработчик. Мы заменили на свою без обработчика для 32+ → следующий tick → #GP.

**Попытка 2:** добавил CLI перед LIDT в helper'е. Прогресс — banner отпечатался полностью, но #GP всё равно. Дамп показал RFLAGS=0x10206 → IF=1.

CLI отработал корректно (после LIDT helper'а IF=0). Но **UEFI's `OutputString` в Console.Write вызвал STI обратно** — firmware часто делает это для timer-based timeout управления внутри BootServices.

То есть полагаться на CLI persistence через UEFI calls нельзя.

**Попытка 3 (финал):** установил IRETQ stub (2 байта: `48 CF`) для всех vectors 32-255. Любой hardware interrupt теперь → CPU → IDT[N] → IRETQ → no-op. EOI намеренно не шлём — Local APIC timer в periodic mode после первого tick'а замолкает до EOI, что для boot phase идеально (никаких scheduler'ов до Phase 3).

CLI оставил в LIDT helper'е — лишним не будет, защищает от race window между LIDT и установкой IRETQ stub'а.

**Урок:** при замене IDT в среде с активными hardware interrupts:
1. CLI перед LIDT.
2. Stub все vectors 32+ на IRETQ (or proper handler).
3. Не полагаться на CLI persistence через external code (UEFI, чужие thunks).

**Попытка 4 (финал-финал):** IRETQ stub без EOI ломал UEFI keyboard (UEFI's клавиатурный handler interrupt-driven, без EOI буфер scancode'ов больше не пополняется). Решение: SIDT прочитать UEFI's IDTR, скопировать UEFI IDT entries для vectors 32-255 в наш IDT verbatim. Тогда firmware handlers (PS/2 keyboard на IRQ 1, RTC, PIT) продолжают идти через IDT[32+] и обрабатываться UEFI-кодом. CLI убрал — UEFI's interrupts должны продолжать работать.

LIDT atomic, race window нет: до LIDT → UEFI IDT, после LIDT → наш IDT с firmware tail.

### Bug в shellcode: MOV RAX, CR2 vs CR3

Первый успешный panic dump показал `CR2=0x0F801000`. Это значение совпадало с **kernel CR3 physical address** (видно в pager init: `cr3 kernel/pager/active: 0x000000000F801000/...`). Не CR2!

Причина: ModRM byte для `mov rN, CRn` кодируется как `11 reg rm`, где `reg` field = номер CR. Я написал `D8` (= 11 011 000 → CR3) вместо `D0` (= 11 010 000 → CR2). Один бит ошибки в encoding'е.

После фикса CR2 в дампе показывает реальный faulting address.

### MSI vector reservation

Vectors 32..255 в IDT остаются zero. Любой interrupt туда → triple-fault. Это намеренно — в Phase 5 (drivers) каждое устройство получит свой vector в диапазоне 0x40-0xFE через MSI/MSI-X. Пока что нет hardware interrupts → нет нужды в обработчиках.

## Ловушки которые обошёл

### `[UnmanagedCallersOnly]` для dispatcher'а

Вызывается из shellcode через `delegate* unmanaged<InterruptFrame*, void>`. ILC генерирует точку входа с Win64 calling convention. Reverse PInvoke инфраструктура NativeAOT обеспечивает корректность GC state при native→managed переходе.

Тот же паттерн работает в `InterfaceDispatchResolver.Resolve` (step 32) — ссылка на полноценный пример.

### ClassConstructorRunner trap

`s_buffer` и `s_installed` — pointer/bool без initializer. Roslyn НЕ генерит cctor → ILC не вставляет `CheckStaticClassConstruction*`. Безопасно.

Принципиально избегали `static readonly T s_x = ...;` паттерна, который бы дал тот же `CR2=0xFFFFF000000001A` что в SystemBanner step 34.

### Console.WriteHexRaw в panic state

PanicDump использует только `*Raw` варианты Console (stackalloc-only number formatting). Heap может быть в неконсистентном состоянии в момент exception'а — managed `WriteHex` через `NumberFormatting.ULongToHex` аллоцирует в heap, что катастрофа. *Raw — pure stackalloc, безопасно.

### Stack alignment при call в Win64

Уже обсудил выше. Главное: после CPU pushes (5) + per-vector pushes (2 для non-error / 1 для error + CPU's errcode) + 15 GP reg pushes + 1 CR2 push = 23 qwords с момента interrupt. `sub rsp, 0x28` (5 qwords) = 28 qwords = 0 mod 16. ✓

## Верификация

### Build pass

`run_build.ps1` должен собраться без C# / ILC ошибок. Главный источник риска — типизация function pointer'а на `&Dispatch` где Dispatch это `[UnmanagedCallersOnly]`. По существующему паттерну (`&InterfaceDispatchResolver.Resolve` в Kernel.cs:222) — должно работать.

### QEMU happy path

В логе должно появиться "idt installed" сразу после "kernel start". Все остальные probes (52 штуки + GcStressTest + PagingValidation + ElfValidation) проходят как обычно. Никаких регрессий.

### QEMU panic verification

В Kernel.cs `RunIdtPanicProbe`: flip `if (false)` → `if (true)`, ребилд, run. Ожидается:
- Все predшествующие probes проходят.
- "idt probe: triggering null deref" в логе.
- Затем дамп вида:

```
*** EXCEPTION ***
vector=0x0E (#PF page-fault)
errcode=0x0000000000000002 [NP|W|S]
RIP=0x... CS=0x0038 RFLAGS=0x00010202
CR2=0x0000000000000000 SS=0x0030 RSP=0x...
RAX=0x... RCX=0x...
...
R15=0x...
*** halting ***
```

Затем kernel halt'ит (бесконечный hlt + spin), QEMU процесс не падает.

После верификации — flip обратно в `false` и закоммитить.

## Что откладываем

- **TSS + IST для #DF.** Single fault сейчас работает; double-fault triple-fault'ит. Достаточная защита для bug-hunting'а в Phase 0/1. TSS/IST добавим когда понадобится — может в составе Phase 1 polish или Phase 3 (scheduler требует TSS для interrupt-stack).
- **iretq + recovery в dispatcher'е.** Сейчас всегда halt. Recovery нужен для managed exceptions (Phase 1) — дамп оттуда будет уже не fatal panic, а часть try/catch.
- **GDT rebuild.** UEFI GDT нам подходит. Свой GDT понадобится при ExitBootServices (Phase 4) или для TSS/IST.

## Следующий шаг

Step 36 — **BCL base** (вторая половина Phase 0):
- MemoryExtensions (IndexOf, SequenceEqual, Contains, и т.д.)
- Math для float/double (Sin, Cos, Sqrt, Floor, Ceiling, Round, Pow, Log, Exp, Min, Max, Abs)
- Array методы (Sort, Find, BinarySearch, Reverse, Resize, IndexOf)
- String/StringBuilder остальные методы (Compare, CompareOrdinal, Split with options, AppendJoin)
- IntPtr arithmetic (`+`, `-`, `==`, `!=`, ToString, Parse)
- Debug реальные методы (Assert, WriteLine, IndentLevel)

Все мелкие пункты можно параллелить — они независимы. Каждый порт 1:1 из dotnet/runtime. Вместе закроют Phase 0 и разблокируют переход к Phase 1 (managed exception handling).
