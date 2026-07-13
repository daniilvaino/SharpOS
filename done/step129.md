# Step 129 — delegate-prerequisite probe + EH as-built map + LangVersion pin

Cleanup / prep-шаг перед работой над managed-делегатами. Три несвязанных,
но мелких дельты собраны в один коммит (no-micro-commits): проба-кирпич,
документная карта EH и фиксация версии языка.

## 1. GenericDictionary probe (кирпич под делегаты)

`NativeAotProbe.Probe_GenericDictionary` — проверяет generic dictionaries
+ instantiating stubs, последний вопросительный кирпич из delegate-
чеклиста. Две reference-инстанциации (`string`/`object`) делят один
`__Canon`-body, `int` компилит exact-body; вложенный `DictNewArray<T>` —
method-dictionary hand-off. Ключевой assert — MethodTable-identity:
`DictNewArray<string>(1)` должен дать тот же MT, что `new string[1]`
(доказывает, что словарь резолвит настоящий `string[]`, не `__Canon`/
`object[]` — иначе делегат на generic-метод создался бы с неверным
типом).

Прогон: `GenericDictionary ... OK (ok)`, val=1072. Зелёный.

Зарегистрирован отдельной строкой в `tools/probe_report.ps1` (маска
`generic dictionary \+ inst stubs: (ok|FAIL)`), т.к. это gating-кирпич.

**Итог delegate-readiness:** все кирпичи проверены фактически (код +
прогон). Блокеров нет. `Delegate.Method`/`DynamicInvoke`/boxing-для-
invoke режем через `throw new NotSupportedException` (в AOT работают, но
тянут reflection-core, которого в kernel-tier нет; потребителей нет).
Fat-function-pointer детектор пишется первым днём внутри
`Delegate.Initialize*` — отдельной пробой не тестируется (C# не даёт
материализовать указатель на generic-метод иначе как через делегат).

## 2. docs/eh-actual-map.md — as-built карта EH

Mermaid-диаграмма всего EH-конвейера (ядро + форк, от HW до типов
Frame), снятая ПО КОДУ на состояние step128. Включает поведение
«как есть» (отброшенные disposition-коды, лимиты реестров, hard-coded
константы, патчи-пластыри) + таблицу из 18 фактов с `file:line`.
Отдельный жанр от `eh-model.md` (модель) и `eh-audit-2026-06.md`
(аудит дыр). Правило синка — как для limits-таблиц.

## 3. donext.md — backlog: единый UNWIND_INFO-декодер

Зафиксирован долг: в дереве ДВЕ независимые реализации декодера
winnt.h UNWIND_INFO — `SehUnwind.ApplyUnwindInfo` (опкоды 0-5 +
consume-only) и собственный applier Tier A `StackFrameIterator`
(только 0-3). Одна спека — два места правок. Решение: сводить Tier A
SFI на `SehUnwind.VirtualUnwind`, но НЕ сейчас (Tier A зелёный, риск
регрессии до P0-1/P0-2). Триггер — первый декодер-баг в Tier A либо
момент дублирования XMM-фикса P0-1. Интеримное правило: правка
опкодов в одном декодере обязана в step-writeup ответить «нужно ли
во втором». Факт №18 в eh-actual-map.md.

## 4. LangVersion pin (OS.csproj)

`<LangVersion>latest</LangVersion>` → `<LangVersion>14.0</LangVersion>`.
`latest` плавал с установленным SDK (под SDK 10.0.204 = C# 14); нет
`global.json`, версия недетерминирована. Пин = воспроизводимая сборка.
Эквивалентно текущему поведению (net7.0 TFM + ILC 7.0.x остаются).
На машине только с SDK 9 C# 14 не примется — теперь это видимая
ошибка вместо молчаливого даунгрейда.

## Limits-таблицы

Не трогаются: GenericDictionary подтверждает уже существующую
capability, tier-поверхность не менялась. Delegate limits §5 остаётся
❌ до реального порта `System.Delegate`.

## Изменённые файлы

- `OS/OS.csproj` — LangVersion 14.0
- `OS/src/Kernel/Diagnostics/NativeAotProbe.cs` — Probe_GenericDictionary
- `tools/probe_report.ps1` — GenericDictionary отчётная строка
- `docs/eh-actual-map.md` — новый (as-built EH карта)
- `donext.md` — backlog единого декодера

## Не в коммите

Дебаг-тумблеры (`Console.Quiet`, `TraceGate.*`, `Verbose`) оставлены
незакоммиченными в working tree — временное состояние для отладки,
коммит их вернул бы шумный boot поверх «тихого» PS-baseline (step126/127).

## Next

Порт `System.Delegate`/`MulticastDelegate` из снапшота ILC 7.0.20:
fat-pointer детектор в `Initialize*` → probe-градиент (closed instance
→ static lambda → closure → `Func<int,int>` → struct-метод/unboxing
thunk → generic-метод → multicast/event → делегат через GC.Collect) →
limits §5 + ELF-таблица.
