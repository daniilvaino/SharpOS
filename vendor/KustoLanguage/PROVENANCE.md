# Kusto.Language — vendored

**Upstream:** https://github.com/microsoft/Kusto-Query-Language (`src/Kusto.Language/`)
**Snapshot:** commit 6b5c2f7, 2026-10-07 (package version 12.4.1)
**License:** Apache-2.0 (see [LICENSE](LICENSE)) — © Microsoft Corporation
**Purpose:** the KQL parser, binder and editor services (diagnostics,
completion, classification) for queries over pipeline data.

## Included

- `src/` — `src/Kusto.Language/` as published, compiled into the app through
  `KustoLanguage.props` (sources, not the NuGet package: an app is its own
  system module, a prebuilt assembly has no CoreLib to bind against).
- `generators/` — upstream's generator sources (`src/Kusto.Language.Generators/`)
  that `tools/KustoGen` runs. Upstream keeps the generated files out of git;
  here they are generated once and kept in `src/`:
  - `src/Syntax/CodeGen/GeneratedSyntaxNodes.cs`
  - `src/Parser/CodeGen/{Engine,ClusterManager,DataManager,AriaBridge}Command{s,Grammar}.cs`

  Regenerate after a snapshot update: `dotnet run --project tools/KustoGen`.

## Excluded

- `src/Properties/AssemblyInfo.cs` — `InternalsVisibleTo` for upstream tests.
- Upstream tests and the other projects of the repository.

## Changes (each marked `SharpOS cut:` in the file)

- `Syntax/SyntaxFacts.cs` — `Enum.GetValues` replaced by the largest kind
  in the table + 1: enum metadata is not in an AOT image of ours.
- `Parser/Combinators/Parsers/ForwardParser.cs` — `[ThreadStatic]` removed
  from the recursion depth counter: the app tier has no thread statics yet.
  One counter for all threads is exact while one thread parses.

## What it needs from the runtime

Generic virtual methods (the parser is combinators with `Accept<TResult>`
visitors) — `std-no-runtime/Runtime/GenericVirtualMethods.cs` (step198);
`decimal`, `ConcurrentDictionary`, `Convert.ChangeType`, `Activator.CreateInstance<T>`
were ported into std for it.
