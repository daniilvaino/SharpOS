# step 191 — типы std в каталоге труб, `Expando`

Основа для чтения без класса: готовые типы, которые есть в каждом образе с
одним ключом, поэтому ни одна сторона трубы их не объявляет.

## Что сделано

- **Корень сообщения — любой тип каталога** (было: только `[Message]`).
  SOSR001 теперь спрашивает `MessageGenerator.InCatalog`: `[Message]`-класс,
  `string`, одномерный массив примитивов, `string`, `object` или
  `[Message]`-типа. `PipeWriter<string>`, `PipeReader<byte[]>`,
  `Pipe.Create<Node[]>` — без обёрток.
- **Массив каждого `[Message]`-типа** генератор регистрирует всегда, а не только
  когда его достигает поле: иначе `T[]` корнем отказывал бы при отправке.
- **Структуры std в каталоге** — `DateTime`, `TimeSpan`, `Guid`,
  `ConsoleKeyInfo` (`[Message]`, `partial`), `System.Numerics.Vector3`
  (`[Message]`). Регистрирует генератор, как любые `[Message]`-типы, со
  смещениями ILC. Генератор теперь пишет `readonly partial` для `readonly`-структур.
- **`SharpOS.Std.Pipes.Expando`** — `[Message]`, `IDictionary<string, object>`:
  индексатор, `TryGetValue`, `ContainsKey`, `Add`, `Remove`, `Clear`, `Keys`,
  `Values`, перечисление в порядке добавления, `ToString`. Значение — null или
  тип каталога; чужой отвергается при присваивании (`ArgumentException`). В
  принятом регионе читается на месте, запись нового значения — барьер
  (`RegionReferenceException`).
- **`KeyNotFoundException`** в std (`System.Collections.Generic`, форма BCL).
- **Дыра каталога:** генератор считал `ushort[]` и `sbyte[]` массивами std и не
  регистрировал, std их тоже не регистрировал — `[Message]` с таким полем
  отказал бы при отправке. Зарегистрированы.

## Тесты

Общий образец `StdProbe` (std, одна копия для ядра и приложения): три строки
(ASCII, кириллица с CJK, пустая), три массива байт (1, 4096, 100 000) и
`Expando` из 16 полей — строка, `int`, `long`, `double`, `bool`, `char`,
`DateTime`, `TimeSpan`, `Guid`, `ConsoleKeyInfo`, `Vector3`, `int[]`, `byte[]`,
`string[]`, вложенный `Expando`, null.

`AotTests` тест 12 (+10, всего 199):

| что | итог |
|---|---|
| строка, `byte[]` в приложении | ok |
| `Expando` целиком, на месте и через `ToHeap` | ok |
| новое значение в принятый `Expando` — отказ, поле прежнее | ok |
| изменённая копия уходит снова | ok |
| чужой тип при присваивании, нет имени | `ArgumentException`, `KeyNotFoundException` |
| приложение → ядро: типизированные читатели ядра (тесты 15–17) | 3 строки, 3 массива, `Expando` — как отправлены |
| ядро → приложение: `Expando` и строка (op 15) | ok |

Хостовые тесты анализатора: 47/47 (+ корень `string`/`byte[]`/`Node[]` без
срабатываний, массив класса без пометки — SOSR001).

Прогон: `[sh] done ran=13 failed=0`, 199/199 в каждой батарее, включая
`--gc-stress 16 4` и `--pipe-stress`.

## Что осталось

- Вид по имени для чужих объектов, запись через вид, пересылка непереведённого
  блока (Р33–Р35) — следующий шаг.
- `Expando.ToString` печатает массивы и структуры без своего `ToString` как
  `(object)` (в ядре `object.ToString` не знает имени типа).
- Массив перечисления полем `[Message]` регистрируется под именем базового
  типа (`System.Int32[]`) с другой таблицей — конфликт ключа; не проверялось.

## Файлы

- std: `Pipes/Expando.cs` (новый), `Pipes/{MessageCatalog,PipeProbeMessages}.cs`,
  `DateTime.cs`, `Bcl/{TimeSpan,Guid}.cs`, `ConsoleTypes.cs`,
  `Numerics/Vector3.cs`, `Exceptions.Derived.cs`, `Std.props`.
- Генераторы: `Pipes.Generator/{MessageGenerator,RegionAnalyzer}.cs`,
  `Pipes.Generator.Tests/Program.cs`.
- Ядро: `Diagnostics/PipeProbe.cs`.
- Приложения: `AotTests/PipeTests.cs`, `Shell/autorun.sh`.
- Документы: `docs/nativeaot-nostd-kernel-limits.md`, `work/pipes-api.md`.
