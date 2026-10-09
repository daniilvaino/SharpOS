# BabyKusto — vendored, not yet built

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

## Status

Does not compile yet: it needs `System.Text.Json.Nodes` (`JsonNode`),
async streams (`IAsyncEnumerable<T>`, `ValueTask`, `Task<T>`),
`System.Linq.Async` (`ToAsyncEnumerable`) and `Regex` (two functions).
`apps_native/KqlBaby` is parked until then.
