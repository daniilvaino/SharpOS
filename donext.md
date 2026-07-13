# P0 — ближайшие шаги по EH

Источник: аудит [docs/eh-audit-2026-06.md](docs/eh-audit-2026-06.md), Q1+Q4 и Q2+Q11.
Два подтверждённых latent-бага, оба чинятся в обозримом объёме. Принцип
«сначала probe, потом фикс»: красный → зелёный переход фиксируется в
`done/stepNN.md`.

---

## P0-1 — XMM lost across catch (Q1 + Q4)

**Подтверждено с двух концов:**

- RyuJIT эмитит `UWOP_SAVE_XMM128` (см. `unwindamd64.cpp:466` в форке) для
  xmm6+ в прологах.
- Наш decoder опкоды 8/9 распознаёт, но `ApplyCode` их **глотает** без
  эффекта.
- `EmitCapture` не захватывает FP/XMM/MxCsr; `EmitRestore` не
  восстанавливает.
- `Context` обрывается на `Rip @ 0xF8` — нет места хранить.

→ значение `double`/`float` в callee-saved XMM (xmm6–xmm15), удерживаемое
через `throw`, после `catch` — мусор. Тихая corruption, не падение.

### Probe сначала (L18)

`EhProbe` — новый сценарий: шесть живых `double` через вызов, throw,
catch, сверка значений. Должен быть **красным** до фикса (значения !=
ожидаемых), **зелёным** после.

Минимальная форма (managed, hosted-tier):

```csharp
static double L18_Inner(double a, double b, double c, double d, double e, double f)
{
    Throw();        // bubbles up
    return a + b + c + d + e + f;
}

static void Throw() => throw new InvalidOperationException();

static (bool ok, double[] got) L18()
{
    double a = 1.1, b = 2.2, c = 3.3, d = 4.4, e = 5.5, f = 6.6;
    try { L18_Inner(a, b, c, d, e, f); }
    catch (InvalidOperationException) { }
    return (a == 1.1 && b == 2.2 && c == 3.3 && d == 4.4 && e == 5.5 && f == 6.6,
            new[] { a, b, c, d, e, f });
}
```

Регистровое давление в `L18_Inner` должно быть достаточным, чтобы RyuJIT
честно положил часть `a..f` в xmm6+. Возможно потребуется руками
форсировать (extra arithmetic, no-inline) — проверить дизасм через
COMPlus_JitDisasm или эквивалент.

### Объём фикса

1. **Расширить `Context` до 0x4D0** ([SehStructs.cs](OS/src/PAL/SharpOSHost/SehStructs.cs)):
   - добавить `XmmRegister` (128-bit) поля `Xmm0..Xmm15` на каноничных
     offset'ах (Windows AMD64 CONTEXT layout: FltSave/Xmm area внутри
     XSAVE_FORMAT, см. winnt.h).
   - `MxCsr @ 0x34` уже объявлен — проверить что не разваливает после
     расширения.
   - Минимум для корректности EH: xmm6–xmm15 (callee-saved). Volatile
     xmm0–xmm5 — нет необходимости в восстановлении, но layout всё равно
     должен совпадать с Win64 ABI.

2. **`EmitCapture` / `EmitRestore`** (RestoreContextAsm и парная capture-
   рутина):
   - В capture: `movaps [ctx+Xmm6_off], xmm6` … `movaps [ctx+Xmm15_off], xmm15`,
     `stmxcsr [ctx+MxCsr_off]`.
   - В restore: симметрично `movaps xmmN, [ctx+XmmN_off]` + `ldmxcsr`.
   - Аккуратно с alignment: 128-bit movaps требует 16-byte aligned
     адрес. `Context` буфер должен быть выровнен (см. memory
     `kernelheap_payload_not_16_aligned` — KernelHeap даёт 8 mod 16,
     нужен page-alloc или manual align).

3. **`ApplyCode` SAVE_XMM128 (8) / SAVE_XMM128_FAR (9)** ([SehUnwind.cs](OS/src/PAL/SharpOSHost/SehUnwind.cs)):
   - Опкод 8: SAVE_XMM128 — реги xmm6..xmm15 (по `OpInfo`),
     16-bit scaled offset (× 16).
   - Опкод 9: SAVE_XMM128_FAR — 32-bit offset.
   - Чтение: `movaps xmmN, [framePtr + offset]` эквивалент в коде:
     прочитать 16 байт по адресу, положить в соответствующий слот
     `Context.XmmN`.
   - `KNONVOLATILE_CONTEXT_POINTERS` xmm-секция — пока **опционально**
     (GcInfoDecoder её не читает, см. Q5). Можно отложить.

### Acceptance

- L18 probe красный до фикса (зафиксировать в log как baseline).
- L18 probe зелёный после фикса.
- Существующая EH-батарея (16/17 + 21/21 reflection-JSON + census)
  без регрессий.
- step-writeup в `done/stepNN.md` с baseline-red → fixed-green переходом.

### Риск

- Alignment trap при первом же `movaps` если `Context` буфер не
  16-aligned — обнаружится сразу, не silent.
- Layout shift: если кто-то читает `Context` по hard-coded offset (не
  через имя поля), сломается. Проверить grep по `0xF8` / `Rip_off` /
  любым magic-offset'ам в Context.

---

## P0-2 — CollidedUnwind silently dropped (Q2 + Q11)

**Подтверждено:**

- First pass ([SehDispatch.cs](OS/src/PAL/SharpOSHost/SehDispatch.cs)):
  проверяется только `ExecuteHandlerMarker` (0x100), остальные коды
  игнорируются.
- Second pass (`RtlUnwind`): return personality routine **полностью
  отбрасывается**.
- `ExceptionCollidedUnwind` реально возвращается из форка
  (`HijackHandler @1929`, `FixRedirectContextHandler @2275`,
  `ProcessCLRException` в determined paths).
- Ожидаемый симптом сегодня: HALT в `RtlUnwind "target not found"` или
  тихая потеря replacement-исключения (зависит от прохода).

### Probe сначала (L19, Q12)

Сценарий: managed `try/finally`, в finally — новый `throw`, всё это
поверх C++-фрейма (hosted-путь, иначе collided unwind не возникает —
нужен фрейм с personality которая обрабатывает unwind).

Каркас (hosted-tier):

```csharp
static void L19_Outer()
{
    try { L19_Inner(); }
    catch (InvalidOperationException) { /* expected: original lost, replaced */ }
}

static void L19_Inner()
{
    try { throw new ArgumentException("original"); }
    finally { throw new InvalidOperationException("replacement"); }
}
```

Под C++-фреймом — например, вызов hosted через `coreclr_execute_assembly`
который уже сидит на C++ stack (`__CxxFrameHandler4` зарегистрирован).
Документировать текущее поведение: HALT? double unwind? lost exception?
Это baseline для зелёного после фикса.

### Объём фикса

1. **FixupDispatcherContext уже работает** —
   `dc->ContextRecord` приземляется в наш буфер. Половина дела сделана,
   проверить что данные корректны (новый RIP/RSP/регистры consistent
   с frame куда коллизия привела).

2. **First pass — SehDispatch:**
   - После вызова personality, **если disp == `ExceptionCollidedUnwind`
     (0x40000005)**:
     - принять `dc->ContextRecord` как новый текущий контекст,
     - **не продвигать фрейм** (не делать VirtualUnwind),
     - перезапустить диспетчер с этого контекста (loop continue, не
       advance).
   - Все остальные неизвестные disp коды → HALT с диагностикой (не
     silent).

3. **Second pass — RtlUnwind:**
   - Тот же паттерн: после `fn(...)` если return ==
     `ExceptionCollidedUnwind`, рестартовать unwind с
     `dc->ContextRecord`, не продвигая фрейм.
   - Конечный return должен быть проверен и проактивно обработан
     минимум для `ExecuteHandlerMarker` (continue) и
     `ExceptionCollidedUnwind` (restart); остальное → HALT.

4. **Защита от бесконечной коллизии** — счётчик collided-restart'ов,
   при превышении (например 8) — HALT с trace. Иначе можно зациклиться
   если personality всегда возвращает collided.

### Acceptance

- L19 probe документирует красное baseline (HALT/lost) в log.
- L19 probe зелёный после фикса: replacement exception ловится
  как `InvalidOperationException`, original теряется (это правильное
  Windows-семантическое поведение для finally-replace).
- HijackHandler / FixRedirectContextHandler пути не должны вести к
  HALT в любой существующей батарее.
- step-writeup с baseline-red → fixed-green.

### Риск

- `dc->ContextRecord` может быть в стеке функции которая уже unwound —
  если FixupDispatcherContext не позаботился о сохранении в стабильное
  место. Проверить, что буфер живёт достаточно долго (наш статический
  per-thread буфер для CONTEXT — должен).
- Семантика replacement: in-Windows finally-rethrow заменяет
  оригинал — это by design, не баг. Не путать с double-throw scenario.

---

## Порядок

1. **L18 probe** добавить, зафиксировать red baseline. Коммит.
2. **XMM fix** (Context+EmitCapture+EmitRestore+ApplyCode). L18 → green.
   Существующая батарея без регрессий. Коммит с step-writeup.
3. **L19 probe** добавить, зафиксировать red baseline (HALT/lost).
   Коммит.
4. **CollidedUnwind fix** в обоих проходах + защитный счётчик. L19 →
   green. Коммит с step-writeup.

После — следующий заход в аудит:
- `__GSHandlerCheck` ассерт (превентивно).
- Q6+Q8 `__C_specific_handler` AVInRuntimeImplOkayHolder reachability
  (уже trip'ался по step106, требует отдельной проверки EstablisherFrame
  semantics).
- Q13 P/Invoke boundary / cross-tier ловля — белые клетки матрицы.

---

## Backlog: единый UNWIND_INFO-декодер (Tier A SFI → SehUnwind)

**Зафиксировано 2026-07-06. Решение: сводить, но НЕ сейчас** — отложено
до/вместе с P0-1, приоритет ниже L18/L19.

**Факт:** в дереве живут **две независимые реализации декодера
winnt.h UNWIND_INFO**:

1. `SehUnwind.ApplyUnwindInfo` (`OS/src/PAL/SharpOSHost/SehUnwind.cs:610`) —
   опкоды 0–5 полные, 6/8/9/10 consume-only, CHAININFO, mid-prolog
   skip + SET_FPREG prescan, EstablisherFrame per Windows ABI, KNCP.
   Потребители: SehDispatch (оба прохода), RtlUnwind, форковский
   StackFrameIterator через `Thread::VirtualUnwindCallFrame`.
2. Собственный applier внутри Tier A `StackFrameIterator`
   (`OS/src/Boot/EH/StackFrameIterator.cs:125-165`) — **только опкоды
   0–3** (PUSH_NONVOL / ALLOC_LARGE / ALLOC_SMALL / SET_FPREG);
   SAVE_NONVOL / SAVE_XMM128 / PUSH_MACHFRAME объявлены unsupported
   в шапке файла. Ноль ссылок на SehUnwind. Потребитель: kernel-AOT
   DispatchEx (Tier A).

**Почему это долг:** одна спека — два места правок и два места
регрессий. Каждый декодер-фикс (ближайший — P0-1 XMM: опкоды 8/9)
надо приземлять в ОБА, иначе tier'ы разъезжаются молча. Дифферен-
циальный оракул (сравнение с ntdll `RtlVirtualUnwind` на хосте),
когда появится, тестировал бы одну общую реализацию за оба стека.

**Целевая форма:** Tier A SFI вызывает `SehUnwind.VirtualUnwind`
(или общий внутренний `ApplyUnwindInfo`) вместо своего switch'а.
SehUnwind уже kernel-side и не зависит от CoreCLR — циклической
зависимости нет. Отличия учесть: Tier A ходит по PAL_LIMITED_CONTEXT /
RegDisplay, не по CONTEXT — нужен тонкий адаптер либо расширение
сигнатуры.

**Почему не сейчас:** Tier A зелёный (L8–L17), его узкое покрытие
0–3 пока достаточно для ILC-прологов ядра; риск регрессии стабильного
tier'а ради чистоты — плохой размен до закрытия P0-1/P0-2.

**Триггер:** первый декодер-баг, воспроизводящийся в Tier A, ЛИБО
момент когда P0-1 XMM-фикс придётся дублировать в
StackFrameIterator.cs — в этот момент сведение дешевле дубля.

**Интеримное правило (пока два декодера):** любой PR, трогающий
опкоды/EstablisherFrame в одном декодере, обязан явно ответить
«нужен ли тот же фикс во втором» (строчка в step-writeup).

---

## Big bet: snocket stack → Windows IL целиком, в комплекте

**Решено НЕ ходить через fake-epoll и НЕ изобретать SocketAsyncEngine для
SharpOS.** epoll — Linux-historical wart (readiness-multiplexer вокруг
slow `select`), для новой ОС никакой пользы не несёт: stateful kernel
объект, ET/LT семантика, thundering herd, лишний syscall round-trip. На
оси, которая контролирует scheduler и net-стек end-to-end, async I/O
выражается прямо через cooperative park/unpark — без multiplexer'а.

Windows-side путь (IOCP) на проде .NET'а **дозревший и стабильный**.
Хотим взять его целиком, не выкусывая отдельные компоненты:

### Что переключаем (в одном комплекте, не по частям)

1. **ThreadPool IOCP-сторона** — Windows-side `PortableThreadPool` с
   IOCP-bound completion threads. Сейчас у нас Unix-shaped, без
   completion port'а.
2. **OverlappedData / NativeOverlapped** — Windows-only managed типы в
   CoreLib (`g_pOverlappedDataClass`), runtime path в CoreCLR для
   аллокации pinned overlapped'ов и delivery callback'ов.
3. **ThreadPool.BindHandle / UnsafeQueueNativeOverlapped** — public API
   которое Sockets BCL зовёт для регистрации FD в pool'е.
4. **CoreCLR IO thread bridge** — `g_IOCompletionPort`,
   `ThreadpoolMgr::CompletionPortThreadStart`, сейчас Unix-no-op'ы.
5. **System.Net.Sockets.dll (Windows IL)** — overlapped-based
   `SocketAsyncEventArgs`, `WSARecv`/`WSASend`, `CancelIoEx` пути.
   Заменяет epoll-based SocketAsyncEngine.
6. **PAL: ws2_32 + IOCP shims** — `WSAStartup`/`WSACleanup`/
   `WSASocketW`/`WSARecv`/`WSASend`/`closesocket`/`bind`/`listen`/
   `accept`/`getaddrinfo`/`ioctlsocket`/`WSAGetLastError`/
   `CreateIoCompletionPort`/`GetQueuedCompletionStatus(Ex)`/
   `PostQueuedCompletionStatus`/`BindIoCompletionCallback`/
   `CancelIoEx`. ~25 функций.
7. **IOCP-эмуляция в kernel** — in-process completion queue (нашими
   coop-нитями обслуживается естественно), привязка NIC ISR completion
   → packet → IOCP entry → unblock waiting completion thread.

### Почему именно "в комплекте"

Граница "только сетка" режется по ThreadPool/Overlapped — это **не
модуль, это runtime contract**. Если переключить только Sockets.dll, то
первый же `ThreadPool.BindHandle` упадёт `MissingMethodException` (в
нашей Unix-IL CoreLib его нет). Если переключить только Sockets+CoreLib
— на load не найдут `CreateIoCompletionPort` (PAL пуст). Точки разреза
нет. Делать одной волной.

### Side benefit

После этого появляется **весь Windows-side async I/O** — не только
Sockets: pipes, file overlapped, console async. Это даёт IOCP-backend
для всего `System.IO.Pipelines` / `FileStream` async path. Стоимость
делится на больший surface.

### Стоимость и порядок

- Подготовительный этап: собрать Windows-IL варианты CoreLib + Sockets
  + Primitives в нашем fork'е (build/clr_sharpos.ps1 — добавить ветку).
- step125: ws2_32 PAL stubs (~25 функций, без логики, только ABI).
- step126: IOCP emulation в нашем kernel (Windows-shaped completion
  queue, привязка к coop-scheduler).
- step127: CoreCLR side — `g_IOCompletionPort` activation,
  CompletionPortThread bring-up, OverlappedData allocation path.
- step128: ThreadPool.BindHandle / UnsafeQueueNativeOverlapped public
  API working, simple ping pong test (no network).
- step129: Sockets BCL Windows-IL поверх — `Socket ctor (TCP)` зелёная,
  sync send/recv working.
- step130: async send/recv через `SocketAsyncEventArgs` → IOCP →
  completion thread.

Реалистично: 1.5-2 недели сосредоточенной работы. Не "просто переключить
dll".

### Сейчас (step124)

Пробу `Socket ctor (TCP)` **пропускаем** (отключаем в Probes.cs / probe
list). Закрываем step124 на вехе catchable HW-exceptions + 4
автокороткозамкнутых RSP-patch resume'а. Сетевой комплект — отдельная
super-задача (SUPER-?, добавить в `plan.md`).

---

## Ссылки

- Полный аудит: [docs/eh-audit-2026-06.md](docs/eh-audit-2026-06.md)
- План проекта: [plan.md](plan.md) (Section 6 immediate next steps)
- Open symptoms: [docs/open-symptoms.md](docs/open-symptoms.md)
- CoreCLR-hosted limits: [docs/coreclr-hosted-limits.md](docs/coreclr-hosted-limits.md)
