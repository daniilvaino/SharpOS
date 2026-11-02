# System.Xml — the reader

**Source:** the fork in this repository (`dotnet-runtime-sharpos/`, .NET 10),
`src/libraries/System.Private.Xml/src` — the reader's files under `System/Xml`
and `System/Xml/Core`, paths kept, plus the few `Schema`/`XPath` types they name
(`XmlSeverityType`, `XmlTypeCode`, `XPathNodeType`, `XsdDateTime`, `XsdDuration`).
**License:** MIT, © .NET Foundation and Contributors.
**Taken by:** `Xml.props` beside this file, app tier; it brings `Regex.props`
(`XmlConvert.EncodeName`/`DecodeName` are `[GeneratedRegex]`, as upstream).

Every edit is marked in place with `// SharpOS cut:` (removed) or `// SharpOS:`
(changed).

## What there is

`XmlReader.Create` over a `Stream`, `TextReader` or path; `XmlTextReader`;
`XmlReaderSettings` (whitespace, comments, PIs, conformance, character
checking, `DtdProcessing.Prohibit`/`Ignore`); `NameTable`,
`XmlNamespaceManager`, `XmlConvert`, `XmlException` with line and position;
`ReadContentAs*`/`ReadElementContentAs*`, `ReadSubtree`, `ReadToFollowing`
and the rest of the pull API; the file resolver (`XmlResolver.FileSystemResolver`).

## Cut, and why

| Where | What | Why |
|---|---|---|
| DTD | `DtdParser` and the DTD model (`SchemaEntity`, default attributes); `DtdProcessing.Parse` throws `NotSupportedException` at `<!DOCTYPE` (`XmlTextReaderImpl.SharpOS.cs`) | a parser and schema model of its own; Prohibit — upstream's default — and Ignore work as upstream |
| Validation | `XmlReaderSettings.ValidationFlags`/`Schemas`/`ValidationEventHandler`; `ValidationType` other than `None` throws; `XmlValidatingReader*`, `XsdValidatingReader`; `SchemaInfo` | the XML Schema object model |
| `XmlReader.ReadInnerXml`/`ReadOuterXml` | throw `NotSupportedException` | serialize through `XmlTextWriter`: the writer side (`XmlWriter` and its raw writers) is not ported |
| `XmlReader.ReadContentAs(Type)` | `XmlUntypedConverter` → a converter over `XmlConvert` for the CLR primitives, `DateTime(Offset)`, `TimeSpan`, `Guid`, `byte[]` (base64) | `XmlValueConverter` is bound to the XML Schema type system |
| Async | `ReadAsync` and the rest (`XmlReaderAsync.cs`, `XmlTextReaderImplAsync.cs`), `XmlAsyncCheckReader` | not ported yet |
| Binary XML | `XmlSqlBinaryReader` (0xDF 0xFF streams read as text and fail) | SQL Server's format |
| Resolving | `XmlUrlResolver`, `XmlResolver.Credentials`; the default resolver is the file one | no networking; `LocalAppContextSwitches` cut (their defaults kept) |
| `XmlException`, `XmlConvert` | serialization; `XmlSchemaException` → `XmlException` with the same text | no legacy formatter; no schema types |

## Changed

- `SR.Xml.cs` — every key of the library's `Strings.resx`, generated, a constants
  class in `System.Xml`.
- `HResults.Xml.cs` — the one constant of the library's `Misc/HResults.cs`, on
  std's `HResults`.
- `XmlWrappingReader` — `SchemaInfo` cut.

## What std gained for it (step231)

`System.SystemException`; `string.GetHashCode(ReadOnlySpan<char>)`;
`MemoryExtensions.Trim/TrimStart/TrimEnd(ReadOnlySpan<char>, ReadOnlySpan<char>)`;
`Parse`/`TryParse` of `sbyte`, `short`, `byte`, `ushort`, `char`;
`Guid.TryFormat`; `Convert.ToDouble/ToString(object, IFormatProvider)`;
`Array.Sort<T>` without the `IComparable<T>` constraint (as the BCL).
