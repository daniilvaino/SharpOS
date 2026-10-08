# System.Text.Json — Utf8JsonReader / Utf8JsonWriter, vendored cut

**Upstream:** dotnet/runtime, `src/libraries/System.Text.Json/` — taken from the fork
in this repository (`dotnet-runtime-sharpos/`, main / .NET 10), snapshot 2026-10-08
**License:** MIT (see [LICENSE](LICENSE)) — © .NET Foundation and Contributors
**Purpose:** the low-level JSON reader and writer for SharpOS apps: a forward-only
reader over UTF-8 spans that can be fed in chunks (`isFinalBlock: false` +
`JsonReaderState`), and a writer into any `IBufferWriter<byte>` or `Stream`.
Not the serializer, not `JsonDocument`, not `JsonNode`.

Namespace untouched (`System.Text.Json`); every edit is marked in place with
`// SharpOS cut:` (something removed) or `// SharpOS:` (something changed).
Apps take it with one line, `<Import Project="..\..\vendor\SystemTextJson\SystemTextJson.props" />`
(see `apps_native/JsonTest/JsonTest.csproj`).

## Included

```
Common/JsonCommentHandling.cs  Common/JsonConstants.cs  Common/ThrowHelper.cs
src/System/Text/Json/
  BitStack.cs  JsonConstants.cs  JsonEncodedText.cs  JsonException.cs  JsonHelpers.cs
  JsonHelpers.Escaping.cs  JsonTokenType.cs  ThrowHelper.cs  Document/JsonValueKind.cs
  SR.cs                         generated here, see below
  Reader/  ConsumeNumberResult  ConsumeTokenResult  JsonReaderException  JsonReaderHelper
           JsonReaderHelper.Unescaping  JsonReaderHelper.net8  JsonReaderOptions
           JsonReaderState  Utf8JsonReader  Utf8JsonReader.TryGet
  Writer/  JsonWriterHelper  JsonWriterHelper.Escaping  JsonWriterOptions  Utf8JsonWriter
           Utf8JsonWriter.WriteValues.{Helpers,Literal,SignedNumber,UnsignedNumber,Double,Float,String,Bytes}
           Utf8JsonWriter.WriteProperties.{Helpers,String,Literal,SignedNumber,UnsignedNumber,Double,Float,Bytes}
```

`SR.cs` is generated from the library's own `src/Resources/Strings.resx`: only the
keys the files above reference, texts verbatim, as constants in
`System.Text.Json` (std has an internal `System.SR` of its own; the nearer
namespace wins). `SR.Format` is a plain `string.Format` — and names the enums it
is handed (`JsonTokenType`, `JsonValueKind`, `DataType`) itself, because
`Enum.ToString` has no member names here (no enum metadata from ILC). Without
that, "Cannot get the value of a token type 'String' as a number" would print a
number or a type name where `String` stands.

## Excluded, and why

| What | Why |
|---|---|
| `ReadOnlySequence<byte>` input: `Utf8JsonReader.MultiSegment.cs`, `ValueSequence`, `Position`, every `HasValueSequence ?` branch | std has no `ReadOnlySequence<T>` / `SequencePosition`. `HasValueSequence` stays and is always `false`. Chunked input is the single-segment path with `isFinalBlock: false` and carried `JsonReaderState`, which is kept whole |
| `JavaScriptEncoder` (`JsonWriterOptions.Encoder`, every `encoder` parameter) | System.Text.Encodings.Web is not ported. The writer escapes exactly as with `JavaScriptEncoder.Default`, the upstream default: Basic Latin only, HTML-sensitive characters as `\uXXXX`, everything non-ASCII as `\uXXXX` (astral as a surrogate pair), ill-formed input as `�`. That part of the default encoder is re-implemented in `JsonWriterHelper.Escaping.cs` (`EscapeStringDefault`), on the file's own `AllowList` table. Lines that lost the parameter carry `// SharpOS: no JavaScriptEncoder` |
| `GetDecimal`/`TryGetDecimal`, `decimal` writers | no `System.Decimal` in std |
| `GetDateTime(Offset)`/`TryGet…`, `GetGuid`/`TryGetGuid`, their writers, `JsonHelpers.Date.cs`, `JsonWriterHelper.Date.cs` | no `DateTimeOffset`; the ISO 8601 and `Guid` UTF-8 parsers/formatters are not ported. Read as a string and parse yourself |
| `Half` constants in `JsonReaderHelper` | no `System.Half` |
| `DisposeAsync`, `FlushAsync`, `IAsyncDisposable` | no `ValueTask`, `CancellationToken`; `Flush`/`Dispose` work, `Stream` output works synchronously |
| `WriteRawValue`, `WriteStringValueSegment`/`WriteBase64StringSegment`, comments writer, `WriteNumberValue` from a formatted string (`…Raw.cs`, `…StringSegment.cs`, `…Comment.cs`, `…FormattedNumber.cs`) | out of scope for this cut; nothing missing underneath, each is a later copy-in. Reading comments (`JsonCommentHandling.Skip/Allow`) works |
| Serializer helpers: `JsonHelpers.TryLookupUtf8Key`, `CreateDictionaryFromCollection`, `IntegerRegex`, `AreEqualJsonNumbers`; `Common/JsonHelpers.cs`; `ThrowHelper` members for converters, `JsonDocument`, `JsonElement.DeepEquals` | serializer / document code, not ported; the regex needs System.Text.RegularExpressions |
| `JsonException`/`JsonReaderException` serialization constructor, `GetObjectData`, `[Serializable]` | no legacy formatter serialization |
| `JsonReaderHelper.ContainsSpecialCharacters` | JSON-path formatting for the serializer; needs `SearchValues<char>` |

## Changed in place (not cut)

- `JsonReaderHelper.net8.cs` — `IndexOfQuoteOrAnyControlOrBackSlash` is a scalar
  loop; upstream is `IndexOfAny(SearchValues<byte>)`, which std does not have.
- `JsonReaderHelper.Unescaping.cs` — `\uXXXX` → UTF-8 through a local
  `TryEncodeScalarToUtf8` instead of `System.Text.Rune`; `ValidateUtf8` takes the
  netstandard path (strict `UTF8Encoding`) instead of `Utf8.IsValid`.
- `JsonWriterHelper.cs` — `IsValidUtf8String` and `ToUtf8` use local scalar
  versions of `Utf8.IsValid` / `Utf8.FromUtf16(replaceInvalidSequences: false)`.
- `Utf8JsonWriter.cs` — the 3-byte partial-string buffer is the netstandard
  `byte[]`, not an `[InlineArray]` struct (ILC 8).

## What std gained for this

Ported into `std-no-runtime/` (BCL namespaces, BCL-compatible): `IBufferWriter<T>`,
`ArrayBufferWriter<T>`, `OperationStatus`, `StandardFormat`, `Utf8Parser`
(Boolean, all integer widths D/N/X, Single/Double), `Utf8Formatter` (Boolean,
integers, Single/Double — over std's `Number.Formatting` UTF-8 path), `Base64`
(scalar path), `HexConverter`, `Number.NumberToFloatingPointBits` + the
floating-point half of `Number.Parsing` (so `double`/`float.Parse/TryParse` now
exist), `MemoryExtensions.IndexOfAny/IndexOfAnyExcept/Count/Trim/AsMemory`,
`Math.BigMul`, `string.StartsWith/EndsWith(char)`, settable `Exception.Source`,
`StringSyntaxAttribute`, `StackTraceHiddenAttribute`, `Environment.Is64BitProcess`;
strict `UTF8Encoding` now throws `DecoderFallbackException` /
`EncoderFallbackException` as the BCL does (System.Formats.Cbor catches these too).
