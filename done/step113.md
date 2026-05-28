# step 113 — lm_sqrt infinite recursion: real root of silent triple-fault under ThreadPool load

## Веха

Закрыт **silent qemu-exit / triple-fault** который вылезал под ThreadPool нагрузкой (SYM-002). Корень — **бесконечная взаимная рекурсия `sqrt ↔ lm_sqrt`** в Debug-сборке форка. После фикса:

- CoreCLR-hosted census: **OK=54** DEG=2 FAIL=7 (было 44 до binary-search проб)
- Hill-climbing включён, ThreadPool stress 1000×20 ✅, FP/XMM smoke ✅
- Ноль triple fault, launcher 4/4, exit 42

## Корень

[`crt_imp_stubs.cpp`](../dotnet-runtime-sharpos/src/coreclr/pal/sharpos/crt_imp_stubs.cpp) line ~414:

```cpp
static double lm_sqrt(double x){ return __builtin_sqrt(x); }   // ← ловушка
...
extern "C" double sqrt(double x){ return lm_sqrt(x); }          // line ~446
```

В **Debug (`/Od`, optimizations OFF) форк-сборке** clang-cl **не лоуэрит** `__builtin_sqrt` в инструкцию `sqrtsd` — вместо этого эмиттит **`call sqrt`**, попадающий в наш же публичный `extern "C" sqrt()` стаб, который зовёт `lm_sqrt()`, который снова `__builtin_sqrt` → `call sqrt` → … **бесконечная взаимная рекурсия**.

~48 байт на кадр × ~21756 кадров = **ровно 1 MiB** worker-стека → stack overflow → #PF (CR2=SP-8) → #PF в #PF handler (#DF) → #PF в #DF handler → **triple fault** → CPU reset. Серийный лог ничего не печатает (panic handler сам fault'ит без IST), поэтому qemu просто «закрывается».

### Почему годами не всплывало

Managed `Math.Sqrt(double)` обычно лоуэрится RyuJIT'ом в `sqrtsd` inline-интринсик — наш libc-`sqrt` стаб НЕ вызывается. Поэтому обычные пробы (включая ранние Math.Sqrt) баг не триггерили. **Первым тяжёлым потребителем sqrt по libc-пути** оказался `PortableThreadPool.HillClimbing.Complex.Abs()` (= `Math.Sqrt(r²+i²)`) в неоптимизированном tier-0 коде, который запускается только после достаточного числа ThreadPool completions. Отсюда «падает только под ThreadPool stress».

### Почему bump стека не помогал

1→4→8 MiB — каждый раз весь стек исчерпан. Рекурсия бесконечная, любой конечный стек съедается. Это и был ключевой признак что root — не legitimate depth, а recursion.

## Фикс

`lm_sqrt` эмиттит `sqrtsd` напрямую через inline asm (clang-cl GCC-style, как уже используется в файле):

```cpp
static double lm_sqrt(double x){
    double r;
    __asm__ ("sqrtsd %1, %0" : "=x"(r) : "x"(x));
    return r;
}
```

Гарантированно одна инструкция, без call назад в `sqrt()`. Работает в Debug и Release одинаково.

## Как диагностировали (методология — главная ценность шага)

1. **QEMU `-d int,cpu_reset,guest_errors -no-shutdown -D qemu-debug.log`** — отличил triple fault от clean shutdown за один прогон. Увидели цепочку `check_exception old:0xff new:0xe → old:0xe new:0xe → old:0x8 new:0xe → Triple fault` с `CR2 = SP-8` = signature stack overflow.
2. **`pmemsave` через QMP** (`tools/dump_stack.ps1`) — пока QEMU висел после triple fault (`-no-shutdown`), сдампили 1 MiB worker-стека (identity-mapped, VA==PA). Worker нашли по `[CT] OK id=20 stackBase=.. stackTop=..` матчингу к fault RSP.
3. **`tools/walk_stack.ps1`** — сканировал дамп на code-like return-адреса, частотный анализ. Идеальный 2-frame cycle: `0x..795 ×10878` + `0x..7B5 ×10878`, чередующиеся. Символизация → `sqrt` (446) + `lm_sqrt` (414). Прямой ответ за минуты вместо дней гадания (XMM? tiered? GcInfo?).

## Lessons learned

1. **`__builtin_<fn>` в libm-стабе = потенциальная рекурсия в Debug.** Если есть `extern "C" <fn>` обёртка над тем же `lm_<fn>`, а `lm_<fn>` зовёт `__builtin_<fn>`, то под `/Od` builtin может стать `call <fn>` → замкнуть. Аудит остальных `lm_*` на это (см. ниже).
2. **QEMU `-d int,cpu_reset` — ПЕРВЫЙ инструмент при silent qemu-exit/hang.** Без него мы гадали XMM-corruption / tiered-JIT / GcInfo неделю. С ним — root за один прогон. Записано в memory `reference_qemu_d_flags_for_silent_crashes`.
3. **Дамп памяти + walk return-адресов** превращает «глубокий непонятный стек» в точный список кадров. `pmemsave` работает пока QEMU висит на `-no-shutdown`. Инструменты сохранены: `tools/dump_stack.ps1`, `tools/walk_stack.ps1`.
4. **Bump ресурса (стек 1→4→8) который не помогает — сам по себе сигнал** что проблема не в размере, а в unbounded цикле.
5. **Heisenbug-гипотезы (XMM, tiered re-JIT) были правдоподобны но неверны.** SYM-001 (multiple JIT) оказался нормальным tiered Debug behavior; SYM-002 «XMM corruption» — мимо. Дамп стека опроверг обе за один шаг. Не строить инфраструктуру по недоказанной гипотезе — сначала дешёвый прямой замер.

## Отложенный аудит

Проверить остальные `lm_*` в crt_imp_stubs.cpp на `__builtin_*` использование которое может замкнуться в Debug через одноимённый `extern "C"` экспорт. Кандидаты: любые `lm_<fn>` вызывающие `__builtin_<fn>`. (sqrt был единственным замеченным; быстрый grep `__builtin_` по файлу закроет вопрос.)

## Файлы

### Fork (dotnet-runtime-sharpos/)
- `src/coreclr/pal/sharpos/crt_imp_stubs.cpp` — `lm_sqrt` `__builtin_sqrt` → inline `sqrtsd`.

### Kernel (OS/) — диагностические правки эпохи расследования, откат/уточнение
- `OS/src/PAL/SharpOSHost/ThreadStubs.cs` — `HostedDefaultStackBytes` остался 1 MiB (bump 4/8 был отладочным, откатан); комментарий уточнён что root был recursion, не depth.
- `OS/src/PAL/SharpOSHost/Diagnostics.cs` — `Verbose` обратно false.

### Probe (work/normal-hello/Program.cs, не в git)
- Binary-search матрица свёрнута в 2 regression-пробы: `ThreadPool stress (1000 x20)` + `FP/XMM smoke (100k Sqrt+Sin)`.

### Tools
- `tools/dump_stack.ps1` (new) — QMP `pmemsave` дамп guest-памяти из висящего QEMU.
- `tools/walk_stack.ps1` (new) — сканер return-адресов + частотный/cyclic анализ.

### Docs
- `docs/open-symptoms.md` — SYM-001/SYM-002 закрыты; добавлен SYM-003.

## Followup — step72 RBP override УДАЛЁН (подтверждённо избыточен)

Воспользовавшись моментом, проверили отложенный вопрос: нужен ли ещё
SharpOS-only override в `gcinfodecoder.cpp` (step72), который для
`GC_FRAMEREG_REL` слотов возвращал `pCurrentContext->Rbp + spOffset`
вместо `*pCurrentContextPointers->Rbp + spOffset`.

**Процесс (с поучительной ошибкой):**
1. Отключил override + добавил пробу `GC FRAMEREG_REL refs across Collect`
   (рекурсия со string-ref'ами + `GC.Collect()` + `WaitForPendingFinalizers`).
   Повисло. Поспешно заключил «override load-bearing».
2. **Ошибка вскрыта:** проба была confounded — `WaitForPendingFinalizers()`
   виснет НЕЗАВИСИМО (наш finalizer-поток не доводит completion — новый
   SYM-003). RIP сэмплился по `GCHolderBase::EnterInternalCoop` (coop-wait),
   не FRAMEREG-misreport.
3. Убрал `WaitForPendingFinalizers` из пробы → прошла ✅ (с override ON).
4. **Валидный single-variable тест:** отключил override + чистая проба →
   ✅. Плюс добавил **точный оригинальный step72-триггер** —
   `System.Text.Json roundtrip (reflection)` — тоже ✅ с override OFF.

**Вывод:** override **избыточен после step112**. Правильный fill
`KNONVOLATILE_CONTEXT_POINTERS->Rbp` в SehUnwind (step112) перекрыл и
FRAMEREG-случай — `GetRegisterSlot` теперь даёт корректный slot. step72
был локальным band-aid'ом того же корня что step112 закрыл системно.
Override удалён, upstream `GetRegisterSlot` path восстановлен.

**Урок:** проба должна изолировать ОДНУ переменную. Confounded проба
(FRAMEREG + finalizer wait разом) дала ложный «load-bearing» вывод;
поймал только потому что RIP-сэмплинг показал finalizer-wait, а не
FRAMEREG. Целевой триггер (Json reflection) + изоляция переменной =
честный результат. Связь с [[feedback_cheap_detector_before_acting_on_unproven_hypothesis]].

## Открытый SYM-003

`GC.WaitForPendingFinalizers()` виснет — finalizer-поток не сигналит
completion. Залогировано в docs/open-symptoms.md. Не критический путь.

## Следующий step

- **IST для #PF/#DF** — proper panic вместо silent triple fault для
  будущих stack overflow'ов (механизм всё ещё дырявый; step113 убрал
  один триггер, не сам triple-fault-on-overflow).
- SYM-003 finalizer thread — когда понадобится IDisposable/finalize-heavy
  код.
- Limits-таблицы: ThreadPool stress / hill-climbing / Json-reflection
  стабильны; обновить если меняет user-facing поверхность.
