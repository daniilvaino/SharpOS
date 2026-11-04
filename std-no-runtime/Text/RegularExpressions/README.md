# System.Text.RegularExpressions

**Source:** the fork in this repository (`dotnet-runtime-sharpos/`, .NET 10),
`src/libraries/System.Text.RegularExpressions/src` — the library's own Compile
items (the `Common`/`CoreLib` links are std's already).
**License:** MIT, © .NET Foundation and Contributors.
**Taken by:** `Regex.props` beside this file, app tier; `Json.props` and
`SharpCompress.props` import it. `[GeneratedRegex]` goes through the .NET 10
targeting pack's generator, the library's version
(`std-no-runtime/Libraries/PackGenerators.props`; until step252 a copy of the
fork's, `generators/Regex.Generator`).

Every edit is marked in place with `// SharpOS cut:` (removed) or `// SharpOS:`
(changed).

## Engines

- **Interpreter** (`RegexInterpreter`) — the default, as upstream.
- **NonBacktracking** (`Symbolic/`) — whole.
- **`RegexOptions.Compiled`** runs on the interpreter: `RuntimeFeature.IsDynamicCodeCompiled`
  is false here, which is what upstream does under NativeAOT.
- **`[GeneratedRegex]`** — source-generated runners, as upstream.

## Cut, and why

| Where | What | Why |
|---|---|---|
| `RegexCompiler.cs`, `RegexLWCGCompiler.cs`, `CompiledRegexRunner*.cs`, `RegexAssemblyCompiler.cs` | removed; `Regex.Compile` returns null (unreachable) | Reflection.Emit |
| `Regex.CompileToAssembly` (three overloads) | removed | `AssemblyName`/`CustomAttributeBuilder`; threw `PlatformNotSupportedException` upstream |
| `Regex`, `RegexMatchTimeoutException`, `RegexParseException` | `ISerializable`, `[Serializable]`, serialization constructors | no legacy formatter serialization |
| `Regex.ValueMatchEnumerator` | `: IEnumerator<ValueMatch>` and its explicit members | a ref struct as a type argument needs `allows ref struct` (ILC 8 has no byref-like generics); `foreach` binds to the public members as before |

## Changed

- `SR.RegularExpressions.cs` — every key of the library's `Strings.resx`, generated,
  a constants class in `System.Text.RegularExpressions` (nearer than std's `System.SR`).
- `RegexCharClass`, `Regex.Replace` — `string.Create` gets the span as a pointer
  (`IntPtr`), as upstream did before .NET 9: a span state needs `allows ref struct`.
- `Symbolic/MintermClassifier` — the lookup builder converts int/byte itself:
  `IBinaryInteger<T>` (generic math) is not in std.

## What std gained for it

`SearchValues<string>` (Aho-Corasick, Rabin-Karp and the fallbacks — the vector
Teddy/ThreeChars paths cut, see `Buffers/SearchValues/Strings/StringSearchValues.cs`),
`Globalization/Ordinal` (invariant mode), `InvariantModeCasing`, `SurrogateCasing`,
`string : IComparable<string>, IEquatable<string>`, `StringBuilder.GetChunks`,
`Task.Run<TResult>`, `List.TrueForAll`, `MemoryExtensions.IndexOfAnyExcept` over
values, `Span.Sort(Comparison)`, `HashCode.Combine` up to eight, `AppContext.GetData`,
`RuntimeFeature.IsDynamicCode*`.

## Limits

- `RegexOptions.Compiled` is the interpreter's speed.
- Culture-sensitive `IgnoreCase` is the invariant culture's: std has one culture.
