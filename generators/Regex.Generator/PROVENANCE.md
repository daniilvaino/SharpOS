# Regex.Generator — System.Text.RegularExpressions source generator

**Source:** the fork in this repository (`dotnet-runtime-sharpos/`, .NET 10,
commit `47abec00`), `src/libraries/System.Text.RegularExpressions/gen` — its
`*.cs`, and the library and `Common`/`CoreLib` files its csproj links, under
`src/` (`Production/`, `Common/`). `Resources/Strings.resx` as is.
**License:** MIT, © .NET Foundation and Contributors.

## Why our own

The SDK passes every app the targeting pack's generator
(`System.Text.RegularExpressions.Generator.dll`). Its runner writes `protected
override` for `RegexRunnerFactory.CreateInstance` and `RegexRunner.Scan`, which
are `protected internal`: right for code in another assembly than the library,
a compile error (CS0507) here, where the library is compiled into the app. The
SDK deduplicates analyzers by file name, so the assembly is
`SharpOS.Regex.Generator`, and `std-no-runtime/Text/RegularExpressions/Regex.props`
removes the SDK's.

## Changed

- `src/RegexGenerator.Emitter.cs` — `// SharpOS:` `protected internal override`
  for `CreateInstance` and `Scan`.

## Added

- `SR.cs` — what the dotnet/runtime build's resource generator writes, generated
  from `Resources/Strings.resx` (embedded under the same name).
- `src/CoreLib/System/Diagnostics/CodeAnalysis/NullableAttributes.cs` — the
  netstandard2.0 polyfill the runtime build gets from its shared sources.
