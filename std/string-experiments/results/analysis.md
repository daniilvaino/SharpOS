# String Experiments Analysis

Дата: 2026-04-02

Источник матрицы: `latest.md`

## Ключевые наблюдения

После расширения `System.String` в `MinimalRuntime`:
- матрица: `pass=10`, `fail=0`;
- проходят:
  - `01 Length`
  - `02 Indexer_FirstChar`
  - `03 Indexer_LoopSum`
  - `09 AsciiEncode_Indexer`
  - `10 Utf16LeEncode_Indexer`
  - `11 Utf8Encode_Bmp_NoPin`
  - `12 FixedString`
  - `13 GetPinnableReference`
  - `16 NewStringRepeatChar`
  - `18 ConcatVariableLiteral`

## Вывод

- Базовый string-surface в freestanding профиле уже поднят:
  - индексатор,
  - pinning reference,
  - concat-path.
- Для закрытия `new string(char,int)` добавлен статический `string.Ctor(char,int)`, который удовлетворяет expected-контракту ILC.

## Ограничение текущего эксперимента

- `Concat` и `Ctor` сейчас реализованы как экспериментальные заглушки под compile/matrix-проверку.
- Это доказывает корректность surface-контракта с ILC, но не является полноценной runtime-реализацией строковой аллокации.
