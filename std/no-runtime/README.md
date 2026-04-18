# no-runtime

Общий freestanding слой для профиля `NoStdLib`.

Цель: держать здесь замену базовых std/runtime-примитивов, которая должна быть общей для:

- ядра/загрузочного образа (`OS`);
- внешних C# приложений (через `apps/sdk` + app projects).

## Текущее содержимое

- `shared/MemoryPrimitives.cs`
  - общая реализация `memset/memcpy/memmove`, используемая обоими мирами (OS и app).
- `shared/SystemString.cs`
  - общая no-runtime реализация `System.String` (`Length`, индексатор, `GetPinnableReference`, `Ctor`, `Concat`).
- `shared/StringAlgorithms.cs`
  - общие строковые алгоритмы (`Concat`).
- `shared/StringRuntime.*.cs`
  - платформенные стратегии аллокации строки:
  - `StringRuntime.RhNewString.cs` для app pipeline;
  - `StringRuntime.Fallback.cs` для OS pipeline.

## Дальше

Новые общие no-runtime компоненты (строки, corelib-поверхность, runtime-контракты) добавлять сюда, а не размазывать по `OS/src/*` и `apps/sdk/*`.
