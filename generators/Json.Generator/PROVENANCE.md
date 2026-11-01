# Json.Generator — System.Text.Json source generator

**Source:** the fork in this repository (`dotnet-runtime-sharpos/`, .NET 10,
commit `47abec00`), `src/libraries/System.Text.Json/gen` — the Compile items
of `System.Text.Json.SourceGeneration.targets` and of
`System.Text.Json.SourceGeneration.Roslyn4.4.csproj`, with the `Common` and
`CoreLib` files they link, under `src/`. `Resources/Strings.resx` as is.
**License:** MIT, © .NET Foundation and Contributors.

## Why our own

The SDK passes every app the targeting pack's generator
(`microsoft.netcore.app.ref`, `System.Text.Json.SourceGeneration.dll`). It
knows member attributes (`[JsonPropertyName]`, `[JsonIgnore]`, …) only when
their assembly is named `System.Text.Json`; here the library is compiled into
the app, so every one of them was silently skipped. The SDK also deduplicates
analyzers by file name, so a generator of the same name beside it is dropped:
the assembly is `SharpOS.Json.Generator`, and `std-no-runtime/Text/Json/Json.props`
removes the SDK's.

## Changed

- `src/JsonSourceGenerator.Parser.cs` — `// SharpOS:` an attribute is
  System.Text.Json's by its namespace `System.Text.Json.Serialization` too.

## Added

- `SR.cs` — what the dotnet/runtime build's resource generator writes, generated
  from `Resources/Strings.resx` (embedded under the same name).
- `src/CoreLib/System/Diagnostics/CodeAnalysis/NullableAttributes.cs`,
  `src/CoreLib/System/Runtime/CompilerServices/CallerArgumentExpressionAttribute.cs`,
  `src/Common/System/ExceptionPolyfills.cs` — netstandard2.0 polyfills the
  runtime build gets from its own shared sources.
