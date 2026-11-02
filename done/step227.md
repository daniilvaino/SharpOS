# step 227 — байты и строки: `CONVERT --to bytes`, `LINES`/`BYTES` в оболочке

## Почему

Пользователь: «нужна программа для явного конвертирования из трубы в строки…
и наоборот, преобразователь в чанки байтов (или даже одиночные байты)». Байты
в строки уже были — `CONVERT --from lines`; обратного не было. Решение
пользователя: преобразование остаётся в `CONVERT`, у оболочки — алиасы на оба
направления.

## Что сделано

- **`CONVERT --to bytes [--chunk N]`** (`apps_native/ConvertApp/Converters/ToBytes.cs`):
  `byte[]` — как есть, строка — строкой UTF-8 с `\n`, прочее — строкой, как на
  экране (что делает из него `--to lines`). Без ключа каждое сообщение уходит
  сразу своим блоком — ничего не копится; `--chunk N` — блоки ровно по N байт,
  последний короче; `--chunk 1` — по байту (`byte[1]`: труба байтов остаётся
  трубой `byte[]`).
- **`CONVERT --from lines` через `Pipe.WriteText`**, как `READ --lines`: строка
  длиннее `PipeTextWriter.MaxLineChars` (64 К символов) идёт кусками. До этого
  `ReadLine` собирал её целиком: журнал загрузки с нулёвыми секторами (step213)
  давал одну строку больше арены обмена — `PipeException: no memory`,
  CONVERT падал с 134 (символизировано: `PipeWriter.Answer` ←
  `FromLines.Run`). Потоковость та же: строка уходит, когда пришёл её конец.
- **Оболочка:** `LINES` = `CONVERT --from lines`, `BYTES` = `CONVERT --to bytes`
  (хвост аргументов — после). Не встроенные команды: стадия — процесс CONVERT,
  работает в любом месте трубы, с `env`, перенаправлениями, `expect`.
  `which LINES` называет развёртку, оба имени — в `help` и автодополнении.

## Проверка

Рабочая копия, WHPX, прогоны 247–249 (Shell и ConvertApp пересобраны):
`which LINES BYTES`; `READ bootlog --lines | PIPECNT`,
`READ bootlog --chunk 7 | LINES | PIPECNT`, `READ bootlog --bytes | LINES | PIPECNT` —
все 1332; `READ bootlog --chunk 100 | LINES | KQL "take 2"` — две первые строки;
`SAMPLE_PIPEGEN 3 | BYTES | LINES` и `BYTES --chunk 5 | LINES` — три строки;
`BYTES --chunk 1 | PIPECNT` — 147 (три строки по 49 байт); `expect 2 BYTES --chunk 0`;
`BYTES --nope` — код 2. Полной батареей не прогонялось.

## Не сделано

- Одиночный `\r` (старый Mac) как конец строки: `PipeTextWriter`, как
  и `READ --lines`, режет только по `\n`.
- Сообщения типа `byte` (а не `byte[1]`).

## Файлы

`apps_native/ConvertApp/Converters/{ToBytes,FromLines}.cs`,
`apps_native/Shell/{Executor,Completion}.cs`, `README.md`.
