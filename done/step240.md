# step 240 — Console.IsOutputRedirected и соседи; раздел 8 подчищен

## Почему

Разбирая пункт «FETCH: голый текст, когда stdtext не экран», пользователь развёл две
вещи: возможность спросить, куда идёт поток, — обязанность системы; подстраиваться ли
— выбор программы, она не обязана. Проверка: `bool color = !Console.IsOutputRedirected
&& Environment.GetEnvironmentVariable("NO_COLOR") is null;` — обычный код под Linux и
Windows (`isatty`, `GetConsoleMode` за свойством) — под нашей std не компилировался:
свойств не было, хотя ответ у std есть (`Pipe.OutputIsTerminal`). Правильно — выставить
его BCL-именами, а не заводить свои и переводить на них программы (инвариант 2).

## Что сделано

- `System.Console.IsOutputRedirected` = `!Pipe.OutputIsTerminal`: вывод не выдан или
  выдан экранной трубой оболочки — не перенаправлен; файл, следующая стадия, «никуда» —
  перенаправлен.
- `IsErrorRedirected` — то же для конца 2 (`Pipe.ErrorIsTerminal`, новое: не выдан
  или экранная труба).
- `IsInputRedirected` — выдан ли конец 0 (`Pipe.InputIsTerminal`, новое).
- `release.md` §8: убраны «плоский вид последовательного терминала» (инспектор — для
  человека, порту не нужен) и «отчёт тестов в stdtext» (не нужен); пункт про FETCH
  переосмыслен: текст терминала, ушедший не на экран, должен очищать получатель, а не
  каждая программа — позже.

## Проверка

Прогон 297: DATATEST тест 16 23/23 — `> файл`, `2> файл`, `< файл` дают каждый свой
бит (режим `DATATEST --child redirected`, ответы битами кода выхода), при `> файл`
stderr — терминал. На приглашении `DATATEST --child redirected` — 0 (все три —
терминал), `| wc -l` — 1.

## Файлы

`apps_native/sdk/SystemConsole.cs`, `std-no-runtime/Pipes/PipeScreen.cs`,
`apps_native/DataTest/{Children,ExternalFiles}.cs`, `release.md`, `donext.md`,
`docs/nativeaot-nostd-kernel-limits.md`.
