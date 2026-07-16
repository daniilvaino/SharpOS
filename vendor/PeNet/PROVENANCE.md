# PeNet — vendored cut

**Upstream:** https://github.com/secana/PeNet (`PeNet-main/`, snapshot 2026-07-15)
**License:** Apache-2.0 (see [LICENSE](LICENSE)) — © 2017-2026 Stefan Hausotte
**Purpose:** native PE (Portable Executable) parsing on bare-metal SharpOS,
first step of the ELF→PE app-migration (donext.md workstream 2).

This is **not** the full PeNet. It is a milestone-1 *native-PE* subset compiled
directly into the kernel image (`OS.csproj` glob-include, like `vendor/Iced/`). It runs
against our own std (`std/no-runtime/shared/`), not the BCL.

## Included

### Milestone 1 (step133) — DOS/NT/section headers

```
FileParser/          IRawFile, BufferFile (forked)
Header/              AbstractStructure
Header/Pe/           ImageDosHeader, ImageNtHeaders, ImageFileHeader,
                     ImageOptionalHeader, ImageSectionHeader, ImageDataDirectory
HeaderParser/        SafeParser
HeaderParser/Pe/     NativeStructureParsers, ImageDosHeaderParser,
                     ImageNtHeadersParser, ImageSectionHeadersParser
```

Entry: `NativeStructureParsers(IRawFile)` → `ImageDosHeader` / `ImageNtHeaders`
/ `ImageSectionHeader[]`.

### Phase 2 (step135) — imports / exports / base relocations

```
Header/Pe/           ImageImportDescriptor, ImageThunkData, ImageImportByName,
                     ImportFunction, ImageBaseRelocation (+TypeOffset),
                     ImageExportDirectory, ExportFunction
HeaderParser/Pe/     ImageImportDescriptorsParser, ImportedFunctionsParser,
                     ImageBaseRelocationsParser, ImageExportDirectoriesParser,
                     ExportedFunctionsParser
ExtensionMethods     RvaToOffset / TryRvaToOffset (array-typed, see below)
```

Instantiated directly (like NativeStructureParsers) — the `DataDirectoryParsers`
god-aggregator is NOT vendored (it eagerly pulls resources / .NET metadata /
authenticode). Enabled by the mini-LINQ std brick (parsers use `List<T>.ToArray`
/ `.Last`). The `PeFile` god-object is likewise NOT vendored.

## Cut (deferred / not applicable)

- **.NET metadata** — `Header/Net/**` (+ MetaDataTables, 49 files). Phase 2:
  needed to parse managed PE apps' metadata; requires mini-LINQ.
- **Resources** — `Header/Resource/**`.
- **Authenticode + Crypto + ImpHash** — needs `System.Security.Cryptography.*`,
  `System.Formats.Asn1`, X509 — out of scope for a unikernel PE loader.
- **TLS / Debug / LoadConfig / DelayImport / BoundImport** — remaining
  `DataDirectoryParsers` members. `ImageLoadConfigDirectory` uses
  `Marshal.AllocHGlobal/PtrToStructure` (no Marshal in std). Add per consumer.
- **`DataDirectoryParsers` aggregator** — pulls resources / metadata; we
  instantiate the phase-2 parsers directly instead.
- **Editor/**, `MMFile` (MemoryMappedFiles), `StreamFile` (Stream).

## Local modifications vs upstream

- `FileParser/BufferFile.cs` — **forked**: backing store `Memory<byte>` → `byte[]`
  (no `Memory<T>` in our std); range slices `[a..]` → `Span.Slice` (no
  `System.Range`); `MemoryMarshal.Write(span, in v)` → `(span, v)`; null-scan by
  hand. Parsing semantics identical. Header comment in the file marks the cuts.
- `ExtensionMethods.RvaToOffset` — re-typed from `ICollection<ImageSectionHeader>`
  (`.Count`/`.ElementAt`) to `ImageSectionHeader[]` (`.Length`/`[i]`). Our arrays
  don't implement `ICollection<T>`/`IEnumerable<T>` (no SZArrayHelper), so the
  interface version faults at runtime; array indexing is direct. Semantics same.
- New std bricks this pulled in: `System.Text.Encoding` (partial — step133),
  `new string(char[])` + `String.Trim*(char)` (step133), `System.Linq.Enumerable`
  mini-LINQ (step134), `List<T>.ToArray()` (step135). All BCL-compat; see
  `docs/nativeaot-nostd-kernel-limits.md`.
