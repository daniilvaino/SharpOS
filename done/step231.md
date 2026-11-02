# step 231 — `System.Xml`: читатель из BCL

## Почему

Релиз 0.1, §3: подсветка синтаксиса для `EDIT`. Кандидат — подсветка
AvaloniaEdit, вынесенная под терминал в `tui-cs/Editor` (MIT); её загрузчик
определений `.xshd` (`V2Loader`) — потоковый читатель на
`System.Xml.XmlReader`, в std был только TurboXml (SAX). Решение пользователя —
порт `XmlReader` из BCL, а не переписывать загрузчик (donext ждал «потребителя,
которому TurboXml не годится»).

## Что сделано

- **`std-no-runtime/Xml`** (`Xml.props`, приложения): читатель форка .NET 10 —
  56 файлов, ~29,7 тыс. строк, пути `System/Xml/...` как в upstream.
  `XmlReader`, `XmlTextReader`/`XmlTextReaderImpl`, `XmlReaderSettings`,
  `NameTable`, `XmlNamespaceManager`, `XmlConvert`, `XmlException`,
  `XmlSubtreeReader`, `XmlCharCheckingReader`, файловый резолвер; из
  `Schema`/`XPath` — только типы, которые читатель называет (`XsdDateTime`,
  `XsdDuration`, `XmlSeverityType`, `XmlTypeCode`, `XPathNodeType`). `SR.Xml.cs`
  — все 1105 ключей `Strings.resx`, константами в `System.Xml`.
- **Вырезано** (каждое место — `// SharpOS cut:`, сводка — README рядом): DTD
  (`DtdParser`; `DtdProcessing.Parse` → `NotSupportedException`, `Prohibit` и
  `Ignore` — как upstream; `XmlTextReaderImpl.SharpOS.cs`), валидация DTD/XSD и
  `SchemaInfo`, `ReadInnerXml`/`ReadOuterXml` (нужен `XmlWriter`), асинхронный
  API, бинарный XML, `XmlUrlResolver` и `Credentials`, сериализация исключения;
  `XmlUntypedConverter` → преобразование через `XmlConvert` (примитивы, даты,
  `Guid`, base64); `XmlSchemaException` → `XmlException` с тем же текстом.
- **std под это** (оба яруса, BCL-форма): `System.SystemException`;
  `string.GetHashCode(ReadOnlySpan<char>)` (тот же хеш, что у строки);
  `MemoryExtensions.Trim/TrimStart/TrimEnd(ReadOnlySpan<char>, ReadOnlySpan<char>)`;
  `Parse`/`TryParse` у `sbyte`, `short`, `byte`, `ushort`, `char` (порт CoreLib
  7.0, `Number/Primitives.Parsing.SmallInteger.cs`); `Guid.TryFormat`;
  `Convert.ToDouble/ToString(object, IFormatProvider)`; `Array.Sort<T>(T[])` и
  `(T[], int, int)` без ограничения `IComparable<T>` — через
  `Comparer<T>.Default`, как BCL (атрибуты `XmlTextReaderImpl` — только
  необобщённый `IComparable`); `null` вместо компаратора — «по умолчанию», а
  не «ничего не делать». Цена — у значимых типов сравнение через интерфейс.
- **`apps_native/XmlTest`** (`\tests\XMLTEST.EXE`): узлы, атрибуты,
  пространства имён, CDATA и сущности, пробелы, типизированное содержимое,
  `XmlConvert`, ошибка с позицией, запрет DTD.

## Проверка

Рабочая копия, WHPX: XmlTest и ядро собираются без ошибок и предупреждений.
Прогон 258: XMLTEST 19/20 — 15 проверок разбора сразу, четыре трассы узлов
падали на `XmlNodeType.ToString()` (`(object)`: имён перечислений в образе
нет — тест берёт имя своим `switch`), одна — опечатка в ожидании (пробел
разделителя). Тест исправлен; прогон 259 (step232): XMLTEST 20/20.

## Не сделано

`XmlWriter`, `XmlDocument`/`XDocument`, DTD, асинхронный API — donext
(«Разборщики BCL», п. 6). Схемы, XSLT, XPath — не планируются.

## Файлы

`std-no-runtime/Xml/**` (новые: копии форка, `Xml.props`, `README.md`,
`SR.Xml.cs`, `HResults.Xml.cs`, `XmlTextReaderImpl.SharpOS.cs`);
`std-no-runtime/{SystemException.cs,Number/Primitives.Parsing.SmallInteger.cs}`
(новые), `Std.props`, `StringComparison.cs`, `Runtime/{Array,Convert,MemoryExtensions.Search}.cs`,
`Bcl/Guid.cs`; `apps_native/XmlTest/**` (новые); `README.md`, `donext.md`,
`docs/nativeaot-nostd-kernel-limits.md`.
