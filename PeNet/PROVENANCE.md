# PeNet — vendored cut

**Upstream:** https://github.com/secana/PeNet (`PeNet-main/`, snapshot 2026-07-15)
**License:** Apache-2.0 (see [LICENSE](LICENSE)) — © 2017-2026 Stefan Hausotte
**Purpose:** native PE (Portable Executable) parsing on bare-metal SharpOS,
first step of the ELF→PE app-migration (donext.md workstream 2).

This is **not** the full PeNet. It is a milestone-1 *native-PE* subset compiled
directly into the kernel image (`OS.csproj` glob-include, like `iced/`). It runs
against our own std (`std/no-runtime/shared/`), not the BCL.

## Included (milestone 1 — DOS/NT/section headers)

```
FileParser/          IRawFile, BufferFile (forked)
Header/              AbstractStructure
Header/Pe/           ImageDosHeader, ImageNtHeaders, ImageFileHeader,
                     ImageOptionalHeader, ImageSectionHeader, ImageDataDirectory
HeaderParser/        SafeParser
HeaderParser/Pe/     NativeStructureParsers, ImageDosHeaderParser,
                     ImageNtHeadersParser, ImageSectionHeadersParser
```

Entry point for the subset: `NativeStructureParsers(IRawFile)` →
`ImageDosHeader` / `ImageNtHeaders` / `ImageSectionHeader[]`. The `PeFile`
god-object is intentionally NOT vendored (it eagerly pulls authenticode, .NET
metadata, imports/exports and resources in its constructor).

## Cut (deferred / not applicable)

- **.NET metadata** — `Header/Net/**` (+ MetaDataTables, 49 files). Phase 2:
  needed to parse managed PE apps' metadata; requires mini-LINQ.
- **Resources** — `Header/Resource/**`.
- **Authenticode + Crypto + ImpHash** — needs `System.Security.Cryptography.*`,
  `System.Formats.Asn1`, X509 — out of scope for a unikernel PE loader.
- **Imports / Exports / Relocations / TLS / Debug / LoadConfig** — the
  `DataDirectoryParsers` family. LINQ-heavy (Where/Select/ToArray) and
  `ImageLoadConfigDirectory` uses `Marshal.AllocHGlobal/PtrToStructure`. Phase 2
  once mini-LINQ lands (donext north-star brick before DOOM).
- **Editor/**, `MMFile` (MemoryMappedFiles), `StreamFile` (Stream).

## Local modifications vs upstream

- `FileParser/BufferFile.cs` — **forked**: backing store `Memory<byte>` → `byte[]`
  (no `Memory<T>` in our std); range slices `[a..]` → `Span.Slice` (no
  `System.Range`); `MemoryMarshal.Write(span, in v)` → `(span, v)`; null-scan by
  hand. Parsing semantics identical. Header comment in the file marks the cuts.
- New std brick this pulled in: `System.Text.Encoding` (partial — ASCII / Unicode
  (UTF-16LE) / UTF8 / Latin1 `GetString`/`GetBytes`), see
  `std/no-runtime/shared/Text/Encoding.cs` and
  `docs/nativeaot-nostd-kernel-limits.md`.
