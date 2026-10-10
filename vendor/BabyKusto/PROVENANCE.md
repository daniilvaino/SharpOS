# BabyKusto — vendored

**Upstream:** https://github.com/davidnx/baby-kusto-csharp (`src/BabyKusto.Core/`)
**Snapshot:** commit 2b9cc3b, 2023-09-28
**License:** MIT (see [LICENSE](LICENSE)) — © Microsoft Corporation (author David Nissimoff)
**`tdigest/`:** t-digest-csharp (https://github.com/Cyral/t-digest-csharp,
commit 5442c5b, Heath Milligan, MIT, see `tdigest/LICENSE` and
`tdigest/OPENSOURCE.TXT`), as modified in BabyKusto's repository.
**Purpose:** executing KQL over in-memory tables, on top of Kusto.Language
(`../KustoLanguage`).

## Included

`src/BabyKusto.Core/` as `src/`, and the t-digest sources its percentile
aggregates use. `BabyKusto.props` compiles both and imports
`KustoLanguage.props`.

## Excluded

- `src/Util/IsExternalInit.cs` — std declares the type.
- Upstream tests, CLI and benchmarks.

## SharpOS cuts (marked `SharpOS cut:` in the files)

- `matches regex` and `extract()` — std has no `System.Text.RegularExpressions`
  (their files are excluded in `BabyKusto.props`, the registrations cut).
- `url_decode` and `url_encode_component` throw `NotSupportedException` — std
  has no `System.Uri`.
- `join` and `summarize` build their composite keys without
  `Uri.EscapeDataString`: `%` and the `|` separator are escaped by hand, which
  keeps the key unambiguous.
- Table dumps write dynamic values with default JSON options — no
  `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`.
- `take` stops when its count is reached exactly (step201); upstream asked the
  source for one more chunk first, which on a pipe is a wait for one more
  message.
- `summarize` without `by` returns one row over an empty input (step201) —
  `count()` is 0, as KQL has it; upstream returned no row.
- Arithmetic with a `dynamic` operand (step201): `Operators/DynamicArithmetic.cs`
  (SharpOS's), typed as Kusto.Language types it — by the other operand, two
  dynamics as dynamic; `Operators/DynamicValues.cs` takes the value out of a
  node for both.
- Comparisons with a `dynamic` operand (step201): Kusto.Language binds them,
  upstream had no implementation. `Operators/DynamicComparison.cs` (SharpOS's
  file) compares the node's value by kind; its overloads come first in
  `BuiltInOperators`, before the typed ones — a typed overload's `string`
  parameter takes any argument and would take a `dynamic` as its text.

## Status

Compiles and runs (step200): `apps_native/KqlBaby` (KQLBABY.EXE, the engine on
a table in memory, 6/6) and `apps_native/KqlApp` (KQL.EXE, a pipeline stage,
streaming since step201).
