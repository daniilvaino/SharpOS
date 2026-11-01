# System.Text.Json, System.Text.Encodings.Web, System.IO.Pipelines

**Source:** the fork in this repository (`dotnet-runtime-sharpos/`, .NET 10),
`src/libraries/System.Text.Json/src`, `System.Text.Encodings.Web/src`,
`System.IO.Pipelines/src` — every `.NETCoreApp` compile item of their csproj.
**License:** MIT, © .NET Foundation and Contributors.
**Taken by:** `Json.props` beside this file (one line in an app's csproj, app
tier only). The source generator is `generators/Json.Generator`.

Until step224 a cut of the reader and writer lived in `vendor/SystemTextJson`;
it is replaced by the libraries whole.

Every edit is marked in place with `// SharpOS cut:` (removed) or `// SharpOS:`
(changed).

## JsonSerializer without reflection

The image has no reflection metadata. `JsonSerializer` gets its contracts from a
`JsonSerializerContext` the source generator writes (`[JsonSerializable]`) — the
way upstream runs with `System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault`
off. `DefaultJsonTypeInfoResolver.SharpOS.cs` stands in for upstream's
reflection half (`DefaultJsonTypeInfoResolver.Helpers.cs`, the
`Reflection*MemberAccessor`s): it still answers for types whose converter is a
built-in value converter — numbers, strings, dates, `Guid`, `JsonElement`,
`JsonNode` — which have no members to discover, so `JsonSerializer.Serialize(42)`
and `JsonValue.Create<long>` work without a context. Any other type without a
context is a `NotSupportedException` naming the way out.

## Cut, and why

| Where | What | Why |
|---|---|---|
| `Serialization/Metadata/DefaultJsonTypeInfoResolver.Helpers.cs`, `ReflectionMemberAccessor.cs`, `ReflectionEmit*MemberAccessor*.cs` | removed; `DefaultJsonTypeInfoResolver.SharpOS.cs` instead | no reflection metadata, no Reflection.Emit |
| `Serialization/Metadata/FSharpCoreReflectionProxy.cs`, `Converters/FSharp/` | removed; the factory left out of the default converter list | F# types are recognised by reflection over FSharp.Core |
| `Converters/Value/Int128Converter.cs`, `UInt128Converter.cs`, the `Int128`/`UInt128` lines in `JsonMetadataServices.Converters.cs`, `JsonPropertyInfo.cs`, `JsonValueOfT.cs` | removed / `SHARPOS_INT128` | std has no `Int128` |
| `JsonSerializerOptionsUpdateHandler.cs` | removed | hot reload (`MetadataUpdateHandler`) |
| `System.Text.Json.Typeforwards.netcoreapp.cs` | removed | type forwards to other assemblies — there is one |
| `JsonHelpers.IntegerRegex` | the same pattern matched by hand, `SHARPOS_REGEX` | std has no Regex yet (release.md, section 2) |
| `JsonException`, `JsonReaderException` | serialization constructor, `[Serializable]` | no legacy formatter serialization |
| `UnsupportedTypeConverterFactory` | `typeof(SerializationInfo)` | no `SerializationInfo` in std |
| `System/ThrowHelper.cs` (of Json and of Encodings.Web) | removed | std's `System.ThrowHelper` has the same members |
| Encodings.Web `AllowedBmpCodePointsBitmap` | the `Vector<T>` loop, `SHARPOS_NUMERICS_VECTORS` | no `System.Numerics.Vector<T>`; the scalar loop |
| Pipelines `StreamPipeReader`, `StreamPipeWriter` | `[AsyncMethodBuilder(PoolingAsyncValueTaskMethodBuilder<>)]` | the pooling builder is not ported |
| Pipelines `Properties/InternalsVisibleTo.cs` | removed | one assembly |

## Changed

- `SR.Json.cs` — every key of the library's `Strings.resx`, generated, as a
  constants class in `System.Text.Json` (nearer than std's `System.SR`); its
  `Format` names `JsonTokenType`, `JsonValueKind` and `DataType` itself, because
  `Enum.ToString` has no member names here.
- `JsonException._message` is `new`: std's `Exception` has an internal field of
  that name, which upstream's (in another assembly) never sees.
- `JsonSerializer.IsReflectionEnabledByDefault` keeps upstream's default (true):
  the resolver it enables is the cut one above.

## Limits

- Enums serialize as numbers only: `Enum.GetNames` is empty (no names in the
  image), so `JsonStringEnumConverter` has nothing to write.
- No polymorphism by reflection, no `[JsonConverter]` on a type outside a
  context's knowledge — everything a context does not know.
