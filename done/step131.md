# step 131 — managed delegates (vendored corlib) + two fundamental GC fixes

## Контекст

North-star — managed DOOM. Кирпич №1 — **managed-делегаты** (`System.Delegate`/
`MulticastDelegate`, `Action`/`Func`, лямбды). До сих пор любой managed-делегат
падал: ILC синтезирует ctor/Invoke/GetThunk-thunks, ссылаясь на поля/методы
`System.Delegate` по имени, а у нас была пустая заглушка. Этот шаг завендорил
delegate corlib, а по дороге вскрыл и прибил **два давних недетерминированных
GC-бага**, которые делегатные GC-пробы разбудили первыми.

## Результат

Полная батарея зелёная: **OK 62 / VALUE 3 / FAIL 1** (FAIL = EnumToString,
известный gap). Делегаты 4/4. CoreCLR-hosted exitCode=42, census OK=145,
launchers 4/4. Iced теперь гоняет **оригинальный** `Array.Sort(lambda)` в
BlockEncoder (боевой delegate-путь).

## 1. Вендоринг delegate corlib (Фаза 1)

Взято из dotnet/runtime **v8.0.27** (тот же сервисинг, что ilcompiler 8.0.27 —
локальный снапшот был release/7.0, не годился; тянули WebFetch'ем с тега).
Файлы (`std/no-runtime/shared/Runtime/`):

- **Delegate.cs** — nativeaot-часть (поля `m_firstParameter`/`m_helperObject`/
  `m_extraFunctionPointerOrData`/`m_functionPointer` в точном порядке; полный
  набор `Initialize*`; `GetThunk` константы 0-5) + libraries-часть (Combine/
  Remove/операторы + virtuals `CombineImpl`/`RemoveImpl`/`GetInvocationList`).
  Reflection/interop/serialization → throw/removed (нет `MethodInfo`,
  `DynamicInvoke`, `CreateDelegate`, `GetFunctionPointer`; GVM/ToInterface
  Initialize → NotSupported). `GetHashCode` через MethodTable-идентичность
  (нет reflection `Type`).
- **MulticastDelegate.cs** — Combine/Remove/GetInvocationList/Equals — чистый
  managed. `Unsafe.As` → каст; serialization/GetMethodImpl вырезаны.
- **FunctionPointerOps.cs** — порт read-side (`IsGenericMethodPointer`+`Compare`+
  `GenericMethodDescriptor`; fat-pointer tag = бит 0x2 на x64). Creation-side
  (LowLevelList/NativeMemory) отброшен.
- **RuntimeImports.Delegate.cs** — `RhNewObject(EETypePtr)` через GcHeap.
- **ActionFunc.cs** — `Action`/`Func` арности 0-4 + `Predicate<T>`, вариантность
  `in`/`out`.
- **MinimalRuntime.cs** — пустые стабы Delegate/MulticastDelegate убраны;
  `Object.GetEETypePtr()` + операторы `EETypePtr ==/!=/Equals/GetHashCode`.

**Fat-pointer tripwire** (безусловный throw, не Debug.Assert — тот
`[Conditional("DEBUG")]`, выпилился бы в Release): в `InitializeClosedInstance`
проверяет, что `m_functionPointer` не тегирован (fat-указатель легитимен только
в `m_extraFunctionPointerOrData` для generic-over-__Canon). Ни разу не сработал.

**Runtime-хелперы под cast/array в delegate-коде** (`GcRuntimeExports.cs`):
`RhTypeCast_IsInstanceOfAny` / `CheckCastAny` / `CheckCastClassSpecial` (общие
isinst/checkcast) + `RhpLdelemaRef` (`ref a[i]`). Минимальные trusted-версии.

**csproj**: 5 delegate-файлов в curated `<Compile Include>` (иначе
`System.MulticastDelegate` не определён — CS0518; `curated_csproj_drift`).

Смоук-матрица (Probe_Delegates): static метод-группа (17), closed-instance (14),
multicast x2 (21), GC-survival (8) — все зелёные.

## 2. GC-баг #1 — freelist dead-remainder (структурная corruption)

**Симптом:** пронизывающая, недетерминированная heap corruption. Sweep/walk
иногда упирался в объект с MT `0xFFFFFFFF00000000` → `test [mt+2]` → #PF;
иногда молча ломал CoreCLR-tier (ThreadPool/Math обрывы на разных местах).
Делегаты были невиновны — их GC.Collect-пробы просто первыми заходили в sweep
после нужного паттерна аллокаций.

**Корень** (`GcHeap.TryAllocateFromFreelist`): при аллокации из freelist-блока
больше запрошенного остаток `< MinFreeBlockSize (32)` **не превращался в
free-marker** — оставался нетрекнутым со **stale-содержимым** предыдущего
объекта. Heap-walk шагает по `ComputeSize` объекта и приземляется на этот
мёртвый 16-байтный остаток, читая stale-байты (напр. NaN-биты боксированного
double `0xFFFFFFFF00000000`) как MethodTable → #PF. Недетерминизм = зависит от
того, что за stale и остаётся ли остаток.

**Фикс:** ЛЮБОЙ ненулевой остаток (кратен 16 → ≥16) делаем **walkable**
free-marker'ом (`MT@0 + Length@8` = 12 байт, влезает в 16). В freelist для реюза
добавляем (next@12) только ≥32 (где влезает next-указатель); мелкие остаются
walkable-но-не-реюзаемыми. Мёртвых остатков больше нет.

## 3. GC-баг #2 — register-root marking (семантическая)

**Симптом:** после фикса #1 WriteBarrier-проба валила локальный `h` — `h.Field`
после `GC.Collect()` становился мусором; `wb-after: clean` (куча структурно
цела), значит живой `h` был **вымётен** (mark не пометил).

**Корень:** `System.GC.Collect()` (std) → `GcRoots.MarkAll()` напрямую — только
static-корни + консервативный скан ТЕКУЩЕГО стека, **без register-spill**. Если
JIT держит локал в callee-saved регистре на callsite'е Collect, скан его не
видит → sweep вымётает. `KernelGC.Collect` (делегаты) шёл через precise-walker,
который register-корни тоже не восстанавливает надёжно.

**Фикс:** `GC.s_collectHook` (raw `delegate*<void>`, layering-safe) — ядро при
бутe (`BootSequence`, после `GcHeap.Init`) ставит его на новый
`KernelGC.CollectConservative`, который ВСЕГДА спиллит все регистры через
`GcStackSpill` (пушит на стек → консервативный скан находит register-корни;
over-mark, но никогда не under-mark). Любой BCL-код, зовущий `System.GC.
Collect()`, теперь получает надёжный путь.

## 4. Iced lock-in

`iced/Intel/BlockEncoder.cs` был переписан на inline-insertion-sort, чтобы
обойти отсутствие делегатов. Возвращён к оригиналу:
`Array.Sort(blocks, (a, b) => a.RIP.CompareTo(b.RIP))` — non-capturing лямбда
(`<>c` синглтон) + `Array.Sort<T>(T[], Comparison<T>)`. `iced/` теперь
байт-в-байт с `iced_original/` кроме `EncoderException.cs` (там `[Serializable]`
+ `SerializationInfo`-ctor — serialization у нас вырезан, легитимный cut) и
LICENSE.txt. Боевая валидация делегатов реальным Iced-кодом.

## Инструмент отладки

`GcHeap.VerifyFindFirstBad` (детерминированный проход по всем объектам с
проверкой каноничности MT ДО деref) + probe-обвязка — вот что развязало
недетерминизм: бисект по GC-таймингам врал (мина всплывала где угодно), а
verify-после-каждой-пробы называл корраптор железно. `prev`-объект дамп +
raw-дамп 16-байтного гэпа привели к freelist-остатку. Инструмент удалён после
фикса (есть в git-истории).

## Lessons

- **Недетерминированная heap-corruption = детерминированный verify, не бисект.**
  Бисект по крашу зависит от GC-тайминга; проход-по-всем-объектам с safe MT-
  проверкой находит корраптор независимо от того, когда sweep случится.
- **Делегаты не виноваты — они разбудили давнюю мину.** Их GC.Collect-пробы
  первыми зашли в sweep после freelist-реюза с малым остатком. Легко было
  списать на новый код; verify показал, что corruption предшествует делегатам
  (BAD уже на `after-arraycopy`).
- **Два разных GC-бага под одним симптомом:** freelist dead-remainder
  (структурная, #PF на stale-MT) и register-root (семантическая, живой локал
  вымётен без структурной порчи). Verify ловит только первую; вторую видно лишь
  по значению поля.

## Отложено / известное

- `EnumToString` FAIL — порт Enum member-name резолва из std.
- Precise-walker не восстанавливает register-корни (обходим консервативным
  спиллом для System.GC.Collect). Точечный фикс precise-walker'а — backlog.
- `EncoderException.cs` расходится с оригиналом (serialization) — ожидаемо.
- Delegate cuts (reflection/DynamicInvoke/variance-cast/open-instance) —
  `NotSupportedException`, доделка по первому потребителю.

## Next

Первый потребитель делегатов по плану — мини-LINQ (Where/Select/ToArray), затем
managed-DOOM. См. donext §workstream 1/3.
