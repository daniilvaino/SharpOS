# step 116 — runtime shellcode emit through Iced (6 эмиттеров) + compare-gate

## Веха

Все шесть hand-rolled byte-emitter'ов в kernel-AOT мигрированы на
**runtime codegen через Iced** (запечённый в step 115). На всех — parallel-emit
с byte-compare gate против legacy, на 5 из 6 байт-в-байт совпадение, на
1 (BigStack) — semantic-only с дельтой +4 байта, требующей форензики
(см. ниже, на пост-commit пас).

Подтверждено что AssemblerRegisters cctor materialization готова уже
на Phase3 (раньше первого Probe_IcedEncode), значит Iced **годится для
runtime shellcode в любой точке kernel boot'а** post-Phase2.

## Мигрированные эмиттеры

| # | Эмиттер | Файл | Phase | Iced len | Legacy len |
|---|---|---|---|---|---|
| 1 | EmitCapture / EmitRestore | `PAL/SharpOSHost/SehDispatch.cs` | EH-dispatch (lazy) | 138 / 137 | 138 / 137 ✅ |
| 2 | JumpStub | `Kernel/Exec/JumpStub.cs` | 5 (ELF launch) | 63 | 63 ✅ |
| 3 | BigStack | `Kernel/Memory/BigStack.cs` | 3 (CoreCLR boot) | 18 | 14 (см. ниже) |
| 4 | Win64/SysV thunks | `Kernel/Process/AppServiceBuilder.cs` | 5 | 21 / 24 | 21 / 24 ✅ |
| 5 | wrmsr GS_BASE | `Kernel/Diagnostics/CoreClrProbe.cs` | 4 | 18 | 18 ✅ |
| 6 | Cr3 read/write stubs | `Kernel/Paging/Cr3Accessor.cs` | 3 (earliest!) | 4 / 7 | 4 / 7 ✅ |

Все подтверждены на QEMU + VirtualBox.

## Паттерн

Каждый эмиттер сделан как:

1. Класс → `partial`, легаси-эмиттер возвращает `int` (длина).
2. Отдельный `.Iced.cs` partial-файл с `using static Iced.Intel.AssemblerRegisters`
   и nested `BufWriter : Iced.Intel.CodeWriter`.
3. `TryWrite*` / `TryInitialize` делает parallel-emit (Iced пишет в live
   buffer, legacy в `stackalloc` scratch), потом `CompareOrPanic`.
4. Mismatch → panic с offset + iced/legacy байты (видно в serial-console).
5. Success — single-shot `[<name>] iced=legacy OK len=0xNN` print.

После 2-3 зелёных прогонов compare-gate + legacy emitter снимаются,
остаётся только Iced.

## Обнаруженные баги

- **Array.Copy ComponentSize-deref bug** (мой со step 115) — surfaced при
  первом запуске #1: я читал `ushort` из адреса object'а (low-2 байта
  MT-pointer'а), а не из самой MethodTable. Triggered только когда Iced
  `InstructionList` физически растил capacity → `Array.Copy(newArr, oldArr)`
  → out-of-bounds copy → AV. Любой `List<T>` который ранее не вырастал
  в kernel-AOT не показывал баг. Fix landed в Array.cs (см. step 115
  amendment).

## Что под вопросом — BigStack +4 байт

Iced даёт 18 байт, легаси-комментарий говорит "14 bytes". Подозрение:
комментарий протух / посчитан неверно — фактическая запись могла быть
18 байт всё это время. Форензику делаем после фиксации этого коммита
(см. следующий пас на ветке).

## Файлы

### Новые
- `OS/src/PAL/SharpOSHost/SehDispatch.Iced.cs`
- `OS/src/Kernel/Exec/JumpStub.Iced.cs`
- `OS/src/Kernel/Process/AppServiceBuilder.Iced.cs`
- `OS/src/Kernel/Paging/Cr3Accessor.Iced.cs`

### Изменённые (partial split + parallel-emit)
- `OS/src/PAL/SharpOSHost/SehDispatch.cs`
- `OS/src/Kernel/Exec/JumpStub.cs`
- `OS/src/Kernel/Memory/BigStack.cs` (inline, без partial)
- `OS/src/Kernel/Process/AppServiceBuilder.cs`
- `OS/src/Kernel/Diagnostics/CoreClrProbe.cs` (inline, без partial)
- `OS/src/Kernel/Paging/Cr3Accessor.cs`

## Lessons learned

1. **Compare-gate платит за себя дважды** — нашёл `Array.Copy` heisenbug
   на #1 (через падение Iced в EH-context), верифицировал byte-parity
   на самом раннем Phase3 эмиттере (#6). Стоимость — одни `stackalloc`
   + memcmp на каждый emit, копейки в boot-time.

2. **Iced encoder детерминирован** для каноничных x64 паттернов
   (`mov reg-reg`, `mov rax,imm64`, `call reg`, `lea` с SIB, `pushfq/popfq`,
   `cli/sti`, привилегированные `mov rax,cr3` / `mov cr3,r9`, indirect
   `call rcx`). Не угадывает варианты — берёт каноничную форму.

3. **AssemblerRegisters cctor materialization готова на Phase3.** Это
   значит весь kernel boot post-Phase2 — safe zone для Iced runtime emit.

## Что откладывается / следующее

- Форензика BigStack (анализ git diff на исходных байтах).
- После 2-3 green boots — снять legacy emitter'ы + compare-gates по всем 6,
  оставить только Iced. Это step 117 (cleanup pass).
- Обновить CLAUDE.md Invariant 1: byte-array shellcode остаётся
  легитимным механизмом, но для каноничных x64 последовательностей
  предпочитаем Iced.
