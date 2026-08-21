# TurboXml — vendored cut

**Upstream:** https://github.com/xoofx/TurboXml (snapshot 2026-08-21)
**License:** BSD-2-Clause (see [LICENSE](LICENSE)) — © Alexandre Mutel
**Purpose:** reading the application manifest out of a PE image's resources, so
an app's ABI record travels inside the file instead of beside it.

A pull-free SAX parser: the caller implements `IXmlReadHandler` and receives
spans, so nothing is allocated per element or attribute. That shape is why this
one was taken rather than a port of `System.Xml` — the kernel reads a manifest
before the app runs, on a path where allocating is a cost and a risk both.

## Included

```
TurboXml/ICharProvider.cs        the source-of-characters seam
TurboXml/IXmlReadHandler.cs      the callback interface
TurboXml/StringCharProvider.cs   characters from a string
TurboXml/XmlChar.cs              character classification
TurboXml/XmlChar.Generated.cs    its tables (generated upstream)
TurboXml/XmlParser.cs            entry points
TurboXml/XmlParserInternal.cs    the parser
TurboXml/XmlThrowHelper.cs       error reporting
```

## Excluded

- `StreamCharProvider.cs` — needs `Stream` and an incremental UTF-8 decoder. A
  manifest is already a resource in memory when we get to it, so the string
  provider is the whole story.
- `TurboXml.csproj` — the sources are compiled into the kernel image directly,
  as with `vendor/Iced` and `vendor/PeNet`.

## Not cut: the SIMD paths

The parser scans with `Vector128`/`Vector256`, and those blocks are **kept as
written**. Vector types now live in our std (`std/no-runtime/shared/Runtime/
Intrinsics/`, ported from dotnet/runtime v8.0), and ILC turns them into real
instructions — confirmed on all three tiers before this library was brought in.

`Vector256.IsHardwareAccelerated` folds to false, since AVX is not in the target
instruction set and its register state is not saved across our context switches.
ILC removes those branches, and the parser drops to the `Vector128` path on its
own — the code above it is unchanged.

## Tables and the cctor trap

`XmlChar.Generated.cs` holds its classification tables as
`static ReadOnlySpan<byte> => [...]`, which the C# compiler lowers to a blob in
the image with no class constructor. That is the one shape that survives here
(limits §1); a `static readonly byte[]` of the same data would fault on first
use.
