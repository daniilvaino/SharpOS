# step 112 — SehUnwind заполняет KNONVOLATILE_CONTEXT_POINTERS (real root для GcInfo phantom OBJECTREFs)

## Веха

Закрыт **самый старый незакрытый upstream-смежный bug**: GcInfoDecoder репортит фантомные OBJECTREF из неинициализированной memory во время GC stack walk через JIT-фреймы. Симптомы класса включают:

- `Object::ValidateInner` debug-assert `CHECK_AND_TEAR_DOWN(pMT && pMT->Validate())` halt
- `GCHeap::Relocate` corruption (relocate write в стек canary → `STATUS_STACK_BUFFER_OVERRUN 0xC0000409`)
- Heisenbug behavior — verbose=on прятал, verbose=off проявлял (timing-sensitive PC distribution)

После fix'а: census **OK=44**, launcher 4/4, **n=5 прогонов** подряд на QEMU + VirtualBox без HALT, **с отключенными** containment-guard'ами в `Object::Validate` и `GCHeap::Relocate` (которые временно ставили в step111 для validation).

Также закрыт **Task.Delay long-task teardown** (step111 follow-up): `GetThreadIOPendingFlag` стабнут — `PortableThreadPool.WorkerThread.IsIOPending` теперь корректно возвращает false на worker-exit path вместо `EntryPointNotFoundException` каскада.

## Корень

`RtlVirtualUnwind` (`OS/src/PAL/SharpOSHost/SehUnwind.cs`) принимал восьмой параметр `contextPointers` (Windows AMD64 ABI = `PKNONVOLATILE_CONTEXT_POINTERS`) и **выкидывал его** — не передавал дальше в worker. Этот struct должен заполняться unwind'ером **адресами** где каждый non-volatile регистр спилен в стеке вызывающего; GcInfoDecoder читает live OBJECTREF из этих адресов:

```cpp
// gcinfodecoder.cpp:1486
ULONGLONG **ppRax = &pRD->pCurrentContextPointers->Rax;
return (OBJECTREF*)*(ppRax + regNum);
```

При unfilled `contextPointers` decoder:
1. Читает указатель из uninitialized памяти
2. Decodes тот указатель как «адрес slot'а»
3. Дереферит → получает stale value, случайно похожее на heap pointer
4. GC компактор берёт это как OBJECTREF в managed heap range
5. По адресу — zeroed memory (uninitialized object slot, не публиковался allocator'ом)
6. `Object::Validate` assert / `Relocate` corruption

step72 (proven за полгода до этого, Frontier-B) уже частично документировал эту проблему — оверрайдил `pCurrentContextPointers->Rbp` use на `pCurrentContext->Rbp` direct read, потому что наш unwind фигачил RBP slot'у на 0x80 (комментарий в `gcinfodecoder.cpp:2237-2258`). Тот workaround сам же предсказывал: «the shared upstream root (SehUnwind's pCurrentContextPointers) remains the future hardening target». Step112 — закрытие этого предсказанного root'а.

## Фикс

В `SehUnwind.VirtualUnwind` / `ApplyUnwindInfo` / `ApplyCode` добавлен сквозной `void* contextPointers` параметр (по умолчанию `null` — backward-compatible для path'ов managed-EH dispatch'а где не нужен). Для каждого register-save unwind-opcode записывается **address slot'а** в `contextPointers + 0x80 + regId*8`:

- `UWOP_PUSH_NONVOL`: slot = `ctx->Rsp` ДО `Rsp += 8`
- `UWOP_SAVE_NONVOL`: slot = `ctx->Rsp + slotOffset*8`
- `UWOP_SAVE_NONVOL_FAR`: slot = `ctx->Rsp + offset` (24-bit)

`KNONVOLATILE_CONTEXT_POINTERS` layout (winnt.h AMD64): первые 0x80 байт — 16 × `M128A*` (Xmm0..Xmm15), потом GP pointers в processor-encoding order:
- +0x80=Rax, +0x88=Rcx, +0x90=Rdx, +0x98=Rbx
- +0xA0=Rsp, +0xA8=Rbp, +0xB0=Rsi, +0xB8=Rdi
- +0xC0=R8 .. +0xF8=R15

Helper `RecordSpill(ctxPtrs, regId, slotAddr)` — NULL-safe single-write.

## Что было опробовано до настоящего fix'а

1. **Cherry-pick PR #119403** (commit `b7c49510629`) — закрыл `executionAborted` подкласс GcInfo bug. Покрытие частичное; assert всё равно срабатывал в других PC окнах. Оставлен в форке — upstream-correct fix, безвреден.

2. **Cherry-pick PR #122620** — interpreter conservative-reporting tweak. **Регрессировал** (jump to `RIP=0x1` в `AllocateManagedClassObject`). Откатили.

3. **MT==0 containment guard в `Object::Validate`** — расширение step107 OOR-guard на in-range+zeroed case. Скрывал assert detector, но **не реальное повреждение** в Relocate.

4. **MT==0 containment guard в `GCHeap::Relocate`** — зеркальный bail. Закрыл `STATUS_STACK_BUFFER_OVERRUN` каскад. Containment, не root fix.

Containment'ы 3+4 работали и доказали что класс багa — phantom OBJECTREF от GcInfo (real live object не имеет MT=0). Это, в свою очередь, направило на SehUnwind как на upstream root.

## Lessons learned

1. **Heisenbug — это hint о corruption, не «random luck»**. Когда verbose=on прячет halt, а verbose=off проявляет — корень всегда в timing-sensitive race ИЛИ в unstable internal state. Здесь: timing решал в каком PC fire'нет GC, какие slot'ы decoder будет читать.

2. **Containment guard'ы валидны как investigation step**. Если расширяешь существующий guard симметрично его доказанной логике (step107 → step111 extension) — это разрешено как стрелка-указатель на root. Real fix — когда понимаешь почему guard работает. Здесь: guard работает because MT==0 = «реальный object не может иметь это» = «source валидно неправильный».

3. **`extern "C"` ВНУТРИ функции — parser error в clang-cl/MSVC**. Нарушал [[feedback_extern_c_only_file_scope]] **трижды** за сессию. Always file-scope between functions.

4. **Не делать `git reset --hard` когда есть uncommitted work в смежных файлах**. Cherry-pick #122620 трогал тот же `gcinfodecoder.cpp` где наш step107 guard живёт; `reset --hard HEAD~1` снёс мой uncommitted MT==0 guard в смежном `object.cpp` потому что я не понял scope reset'а. `git stash` перед операцией решал бы.

5. **step72 коммент-прогноз сработал**. «Future hardening target = SehUnwind's pCurrentContextPointers» был написан полгода назад, тогда же fix'нули симптом workaround'ом RBP-override. step112 — закрытие того предсказания. **Хорошие комменты про unknown unknowns окупаются.**

6. **PR research через subagent с code-access ловит то что я бы не нашёл**. Агент нашёл issue #128330 (May 2026, open), который я бы пропустил без targeted search.

## Файлы

### Kernel side (OS/)
- `OS/src/PAL/SharpOSHost/SehUnwind.cs` (+45/-12) — `VirtualUnwind` chain passes `contextPointers` through; `ApplyCode` records spill addresses for `PUSH_NONVOL` / `SAVE_NONVOL` / `SAVE_NONVOL_FAR`; helper `RecordSpill(ctxPtrs, regId, slotAddr)`.

### Fork side (dotnet-runtime-sharpos/)
- `src/coreclr/pal/sharpos/crt_imp_stubs.cpp` (+15) — `GetThreadIOPendingFlag` stub (returns success, IO=FALSE) + switch entry; closes Task.Delay long-task shutdown caught after step111 commit but not yet committed.

### Inherited from step111 (already in HEAD)
- Cherry-pick `b7c49510629` (PR #119403 "Unconditionally skip GC reporting for non-interruptible aborted methods")

## Что НЕ удаляется (defense in depth, для будущей валидации)

- **step107 OOR-guard в `Object::Validate`** — он покрывает legitimate non-heap pointer reports (например kernel ScanStaticRoots или register slots от non-CoreCLR кода). Не связан с unwind bug.
- **step72 RBP override в `gcinfodecoder.cpp:2237-2258`** — потенциально избыточен после step112 (наш `pCurrentContextPointers->Rbp` теперь корректный). Оставлен на следующий step (требует targeted test что RBP-relative slot lookup работает без него).

## Что откладывается

- Удаление step72 override — после dedicated test что fill RBP корректен.
- README comparative table / `docs/coreclr-hosted-limits.md` — Task.Delay 🟡→✅ (теперь работает full shutdown), DynamicMethod.GetILGenerator 🟡→✅. Per user сейчас не обновляем, отдельный commit.

## Следующий step

Options:
- Удалить step72 override после validation — закроет последний artifact эпохи pre-step112
- README/limits-table sync (накопилось со step111)
- Atomicity audit для других потенциальных «забытых» Windows ABI out-params в нашем PAL
- Phase G задачи (Process.Start, Timer (1ms) — открытые fronts из step111)
