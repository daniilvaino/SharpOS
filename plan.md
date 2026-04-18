# План развития SharpOS

Документ фиксирует стратегическое направление проекта — путь от текущего freestanding C#-ядра с минимальным std к полноценному managed окружению, способному хостить Roslyn и PowerShell.

Это **план супер-задач**, а не список конкретных todo. Каждая супер-задача представляет собой законченный блок работы с чётким критерием готовности. Разбиение на конкретные подзадачи делается в рамках самой супер-задачи по мере к ней подхода.

---

## Горизонт 1: Foundational managed runtime (must-have)

Без этих супер-задач managed runtime не работает как таковой. Это sequential-зависимости — нельзя перепрыгнуть.

---

### SUPER-1. std/ расширен до BCL surface нормального C# кода

**Цель:** любой "обычный" C# код со строками и числами компилируется и работает в freestanding профиле без обхода через unsafe или ручные буфера.

Задача разбита на **1a** (без массивов) и **1b** (после SUPER-3 с массивами).

---

#### SUPER-1a. Строки и числа без массивов

**Покрытие:**
- Number formatting/parsing — int/uint/long/ulong.ToString(), ToString("X"), ToString("D10"), Parse, TryParse.
- Character operations — char.IsDigit, IsLetter, IsLetterOrDigit, IsWhiteSpace, ToUpperInvariant/ToLowerInvariant для ASCII.
- String queries — IndexOf, LastIndexOf, Contains, StartsWith, EndsWith, IsNullOrEmpty, IsNullOrWhiteSpace.
- String transforms с одним выходом — Substring, ToUpperInvariant, ToLowerInvariant, Trim/TrimStart/TrimEnd, Replace(char,char), Replace(string,string).

**Внутри 1a есть стадии по мере усложнения инфраструктуры:**

1. **NumberFormatting в std/** ✅ (step 28) — `UIntToString/IntToString/ULongToString/LongToString/UIntToHex/ULongToHex` возвращают `string`. Линкуется во все проекты. Работает в приложениях. В ядре пока возвращает пустые строки из-за `StringRuntime.Fallback`.

2. **KernelHeap-бэкед StringRuntime для ядра** ✅ (step 28) — `StringRuntime.KernelHeap.cs` с реальной аллокацией через `KernelHeap.Alloc`. MethodTable берётся из `string.Empty` (надёжнее чем NativeAOT `[Intrinsic] EETypePtrOf<T>`). Ядро теперь полноценный потребитель общего std: `NumberFormatting` работает одинаково в приложениях и ядре. `Console.*` мигрирован на managed-путь с `*Raw` fallback для раннего boot и для кода, итерирующего heap (`HeapDiagnostics`).

3. **String queries + char helpers** ✅ (step 28) — `CharHelpers` (IsDigit/IsLetter/IsLetterOrDigit/IsWhiteSpace/ToUpper/ToLowerInvariant для ASCII), `StringQueries` (IndexOf/LastIndexOf/Contains/StartsWith/EndsWith/IsNullOrEmpty/IsNullOrWhiteSpace). Пробросы в `SystemString` как instance-методы. Без аллокаций.

4. **String transforms с одним выходом** ✅ (step 28) — `StringTransforms` (Substring/Trim/TrimStart/TrimEnd/Replace(char,char)/Replace(string,string)/ToUpperInvariant/ToLowerInvariant). Пробросы в `SystemString`. Используют `FastAllocateString`, работают в ядре после стадии 2 и в приложениях.

**Критерий готовности 1a:** в приложении и в ядре пишешь `var s = 42.ToString() + " items"; if (s.Contains("item")) { ... }` — компилируется и работает. `Console.WriteUInt` и прочие inline-реализации в ядре мигрируют на `NumberFormatting`.

---

#### SUPER-1b. Со-зависимо от SUPER-3 (массивы)

**Покрытие:**
- StringBuilder — Append, AppendLine, ToString, Clear, Length, индексатор (нужен резизируемый `char[]`).
- string.Split(char/string) → string[] (нужны массивы).
- string.Join(sep, string[]) (нужны массивы или params).
- string.Concat(params string[]) (нужны массивы).

**Критерий готовности 1b:** `string.Join(", ", new[] {"a", "b", "c"})` компилируется и выполняется корректно.

---

**Вне scope всей SUPER-1:** culture-aware operations, Regex, полноформатный string.Format со сложными format strings, Unicode normalization.

---

### SUPER-2. Managed heap с реальным allocation

**Цель:** `new` в managed коде работает без обхода через C shim. Аллокации идут через код, написанный на C#.

**Покрытие:**
- Managed allocator с API `alloc(size, eeTypePtr) → object reference`.
- Интеграция через RhNewObject / RhNewArray / RhNewString runtime hooks.
- Поддержка разных видов объектов — reference types, boxed value types, strings, arrays.
- Fallback на существующий C-аллокатор для boot phase.

**Открытый вопрос на входе:** нужен ли минимальный mark-sweep GC или достаточно "arena allocator с reset между operations"? Решается по характеру первого managed workload (SUPER-6). Bump allocator без free выглядит безопасно на бумаге, но interpreter из SUPER-6 будет аллоцировать много коротких строк — память кончится.

**Критерий готовности:** `var list = new List<int>(); list.Add(1); list.Add(2);` работает и не падает.

**Вне scope:** полный GC, write barriers для generational GC, concurrent allocation, pinning, finalizers.

---

### SUPER-3. Базовые managed collections

**Цель:** `List<T>`, `Dictionary<TKey, TValue>`, `Stack<T>`, `Queue<T>`, `HashSet<T>` работают в freestanding.

**Покрытие:**
- Managed array allocation (runtime support для `new T[size]`).
- List<T> — Add, Remove, indexer, Count, Clear, foreach, ToArray.
- Dictionary<TKey, TValue> — Add, TryGetValue, ContainsKey, indexer, Count, Keys, Values, foreach.
- Stack<T>, Queue<T>, HashSet<T> с базовым API.
- IEnumerable<T> / IEnumerator<T> infrastructure для foreach.

**Критерий готовности:** нормальный C# код с коллекциями и foreach работает.

**Вне scope:** LINQ целиком (это отдельный большой subset). Thread-safe collections.

---

### SUPER-4. IDT и обработчики CPU exceptions

**Цель:** любая CPU-level ошибка (page fault, invalid opcode, divide by zero, general protection) даёт controlled panic с диагностикой вместо triple fault / reboot.

**Покрытие:**
- IDT setup — 256-entry interrupt descriptor table в Hal.
- Handlers для критичных vectors — #DE, #UD, #NM, #DF, #TS, #NP, #SS, #GP, #PF.
- GDT/TSS setup для IST (double fault на отдельном стеке).
- Diagnostic dump — CR2, RIP, error code, register state, возможно стек-трейс.

**Принципиально:** это **не** managed try/catch. Это фундамент, без которого managed exceptions корректно работать не могут. CPU exception должен сначала стать controlled panic, и только потом превращаться в managed throw (SUPER-5).

**Критерий готовности:** `int* p = null; *p = 42;` в managed коде не перезагружает машину, а даёт читаемый panic с информацией о месте падения.

**Вне scope:** hardware interrupts от устройств (timer, keyboard) — это отдельная супер-задача позже.

---

### SUPER-5. Managed exception handling

**Цель:** `throw new Exception("msg"); try { ... } catch (Exception e) { ... }` работает в managed коде.

**Покрытие:**
- Personality function для Itanium C++ ABI / эквивалента NativeAOT.
- Stack unwinding через unwind tables из .eh_frame.
- System.Exception базовый класс с Message, StackTrace, InnerException.
- Производные типы — ArgumentException, NullReferenceException, IndexOutOfRangeException, DivideByZeroException, FormatException и т.д.
- Автоматическое поднятие exceptions при null dereference, index out of range, divide by zero — через managed throw, а не через CPU exception → triple fault.
- Finally блоки.

**Стратегия снижения риска:** супер-задача разбивается надвое по ходу работы.
- **Промежуточный milestone:** throw/catch через long jump в пределах одного метода, без раскрутки стека, но уже с System.Exception типами. Этого достаточно чтобы SUPER-6 (integration test) начал работать.
- **Финал:** полноценное unwinding через личностную функцию и unwind tables.

Personality function + unwinding — большой отдельный инженерный блок, не про C#, а про ABI и метаданные компилятора. Может занять недели.

**Критерий готовности:** `try { int[] a = new int[3]; var x = a[10]; } catch (IndexOutOfRangeException e) { Log(e.Message); }` ловит exception и корректно логирует.

**Вне scope:** exception filters (catch when), AsyncLocal через async, custom marshalling exceptions через ABI границы.

---

### SUPER-6. Первый Level 1 workload — StackInterpreter

**Цель:** полноценное managed приложение работает поверх инфраструктуры SUPER-1…5.

**Покрытие:**
- Stack-based interpreter (RPN calculator или mini-Forth).
- REPL loop — input через TryReadKey, вывод через WriteString.
- Использует всю BCL primitives из SUPER-1…3, exception handling из SUPER-5.
- Корректно обрабатывает parse errors и runtime errors через managed exceptions.

**Функция:** integration test — доказывает, что runtime достаточно зрелый для типичных managed workload'ов. Interpreter простой намеренно — он тест инфраструктуры, а не цель сам по себе.

**Критерий готовности:** StackInterpreter.elf запускается из launcher, принимает input, выводит результаты, exceptions корректно раскручиваются.

---

## Горизонт 2: Threading и async infrastructure

Prerequisite для Roslyn и PowerShell, которые внутри thread-aware.

---

### SUPER-7. Threading primitives

**Цель:** managed threads с базовой синхронизацией.

**Покрытие:**
- Timer interrupt через Local APIC для preemptive scheduling.
- Thread abstraction — Thread class, создание, завершение, Join.
- Scheduler — cooperative или preemptive, single-core.
- Synchronization — Monitor (lock), ManualResetEvent, Semaphore.
- Interlocked operations через CPU atomic instructions.
- Thread-local storage.

**Критерий готовности:** создать поток, запустить код, дождаться Join, синхронизироваться через lock.

**Вне scope:** Task/async/await (отдельный слой в SUPER-8), ThreadPool с work stealing, SMP.

---

### SUPER-8. Async/await infrastructure

**Цель:** async методы работают, `await Task.Run(...)` работает.

**Покрытие:**
- Task, ValueTask с полным life-cycle.
- TaskScheduler по умолчанию — простой ThreadPool.
- SynchronizationContext.
- IAsyncEnumerable support для `await foreach`.

**Критерий готовности:** `await SomeAsyncMethod()` работает, continuations корректно выполняются.

**Вне scope:** async I/O completion ports (пока нет async I/O), ConfigureAwait nuances.

---

## Горизонт 3: Roslyn и PowerShell

Переход от "managed OS" к "OS, которая хостит .NET workload'ы уровня Roslyn".

---

### SUPER-9. Reflection и dynamic code loading

**Цель:** Roslyn может скомпилировать C# в памяти, SharpOS может загрузить и выполнить результат.

**Покрытие:**
- Полная Type API — GetType, metadata, GetMethod, GetProperty, MethodInfo.Invoke.
- Assembly.Load из byte array.
- IL execution — либо через NativeAOT (compile+link), либо mini-JIT / interpreter для emitted IL.

**Принципиально:** это другой уровень сложности относительно всего предыдущего. Может потребовать rethinking архитектуры.

**Критерий готовности:** `CSharpScript.EvaluateAsync("1 + 1")` возвращает 2.

---

### SUPER-10. Roslyn REPL

**Цель:** SharpOS хостит C# REPL.

**Покрытие:**
- Roslyn compiler as-is из NuGet со всеми dependencies.
- REPL loop — input → Roslyn → output.

**Критерий готовности:** `2+2` даёт 4, `var x = "hello"; x.ToUpper()` даёт "HELLO".

---

### SUPER-11. PowerShell embedded

**Цель:** SharpOS хостит PowerShell через System.Management.Automation.

**Покрытие:**
- PowerShell.Create API как библиотека (embedded host).
- Базовые cmdlets — Write-Host, Write-Output, Get/Set-Variable.
- Адаптация platform-specific частей.

**Критерий готовности:** `PowerShell.Create().AddScript("1..10 | ForEach-Object { $_ * 2 }").Invoke()` работает.

---

### SUPER-12. Интерактивный PowerShell shell

**Цель:** полноценный shell — prompt, readline, history, tab completion.

**Покрытие:**
- Console host — readline-like input, history, completion.
- Filesystem cmdlets — Get-ChildItem, Get-Content, Set-Content.
- Process cmdlets (если появится concept of processes).

**Критерий готовности:** садишься за SharpOS, перед тобой PowerShell prompt, можно интерактивно работать.

---

## Зависимости и параллельность

**Sequential** (нельзя перепрыгнуть):
- SUPER-1 → SUPER-2 → SUPER-3 → SUPER-6
- SUPER-4 → SUPER-5 → SUPER-6
- SUPER-5 → SUPER-9 → SUPER-10 → SUPER-11 → SUPER-12

**Параллельные возможности:**
- SUPER-1/2/3 (std + heap + collections) параллельны SUPER-4/5 (IDT + exceptions) по большей части — пересекаются только на финале у SUPER-6.
- SUPER-7/8 (threading + async) можно делать с меньшим BCL если не собираешься сразу Roslyn. Или можно сделать minimal Roslyn (single-threaded compilation) без threading и добавить threading позже для PowerShell.

---

## Ориентиры по времени

Оценки для одного разработчика без FTE, с буфером на "неожиданности". Managed heap и exceptions — классические места долгого застревания.

| Горизонт | Диапазон |
|---|---|
| Горизонт 1 (SUPER-1…6) | 9–15 месяцев |
| Горизонт 2 (SUPER-7…8) | 4–6 месяцев |
| Горизонт 3 (SUPER-9…12) | 12–24 месяца |
| **До интерактивного PowerShell** | **2–4 года** |

После SUPER-6 в любой момент можно остановиться и получить работающую minimal managed OS, способную запускать небольшие C# приложения. Всё дальше — расширение до production use case.

---

## Правила корректировки плана

- Супер-задачу можно разбить на подзадачи только внутри неё самой, не в этом документе.
- Если критерий готовности оказывается недостижим в заявленном scope — сначала сужается scope, потом пересматривается критерий, потом двигается граница супер-задачи.
- Переход на следующую супер-задачу — только после того как критерий готовности предыдущей выполнен на реальном железе или QEMU strict-nx.
- Документ `done/stepNN.md` фиксирует результаты по завершении каждой супер-задачи или её значимой части.
