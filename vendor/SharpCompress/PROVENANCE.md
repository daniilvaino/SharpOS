# SharpCompress — whole library, vendored

**Upstream:** https://github.com/adamhathcock/sharpcompress, tag `0.50.7`
(commit `b4af8afe6683d019d504954a3b4e4f18b06e2cd6`, 2026-10-20), `src/SharpCompress/`.
**License:** MIT — © Adam Hathcock; `LICENSE.txt` as upstream ships it, which also
carries the Bouncy Castle (MIT) notice. Parts the upstream README credits:
Zstandard from ZstdSharp (oleg-st, MIT), XZ from XZ.NET (sambott), 7-Zip from
managed-lzma.
**Purpose:** every archive and compression format for SharpOS apps, all managed:
zip, tar, gzip, bzip2, 7z, xz, lzma, lz, zstd, rar (read), ace, arj, arc and the
rest. Its zlib is also the zlib under BCL's `System.IO.Compression` here
(`std-no-runtime/IO/Compression`, `Interop.ZLib.SharpCompress.cs`).

Namespace untouched. Every edit is marked in place with `// SharpOS cut:`
(something removed) or `// SharpOS fix:` (an upstream bug, fixed). Apps take it
with one line, `<Import Project="..\..\vendor\SharpCompress\SharpCompress.props" />`,
which also brings `System.IO.Compression` (see `apps_native/CompressTest`).
`.editorconfig` silences upstream's CS0162 (`Path.DirectorySeparatorChar` is a
constant here, so its Windows branches are unreachable).

## Included

All of `src/SharpCompress/**/*.cs` (874 files). Not taken: `SharpCompress.csproj`,
`packages.lock.json`, the signing key `SharpCompress.pfx`.

## Cut, and why

| Where | What | Why |
|---|---|---|
| `AssemblyInfo.cs` | `[assembly: CLSCompliant(false)]` | compiled into the app, whose std already declares it — a duplicate is an error |
| `Common/Ace/Headers/AceFileHeader.cs`, `Compressors/Arj/LhaStream.cs`, `Factories/ArcFactory.cs` | `using System.Xml.Linq;`, `using System.Data;`, `using static System.Net.Mime.MediaTypeNames;` | unused |
| `Archives/ArchiveVolumeFactory.cs`, `Archives/Rar/RarArchiveVolumeFactory.cs`, `Archives/Zip/ZipArchiveVolumeFactory.cs` | volume names by `Regex`, under `#if SHARPOS_REGEX` | std has no Regex yet (release.md, section 2). Without it the next volume of a multi-volume archive is not found; single-volume archives are unaffected |
| `Common/ArchiveEncodingExtensions.cs` | `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)`, under `#if SHARPOS_CODEPAGES` | std has no code pages yet (release.md, section 2): names in legacy code pages (CP437 and the like) are read as UTF-8 |
| `Utility.Async.cs` | `File.OpenHandle` for async file creation, under `#if SHARPOS_FILE_HANDLES` | no `SafeFileHandle` here; the `FileStream` branch upstream keeps for older targets is taken |
| `Compressors/ZStandard/Unsafe/ZSTD_getAllMatchesFn.cs`, `ZSTD_BlockCompressor_f.cs` | `[UnmanagedFunctionPointer(CallingConvention.Cdecl)]` | the delegates are only called from managed code; the attribute makes ILC emit marshalling stubs, which need NativeAOT's `InteropHelpers` (not ported). std does not declare the attribute, so code that needs marshalling fails to compile instead of crashing ILC |

## Fixed upstream bug

`Compressors/Deflate/DeflateStream.cs` and `DeflateStream.Async.cs`:
`DeflateStream` built with `leaveOpen: true` in `Compress` mode wrote **no
output**. Its `Dispose`/`DisposeAsync` skipped disposing the inner
`ZlibBaseStream` when `leaveOpen` was set — but that `Dispose` is what writes the
final deflate block (`finish()`), and `ZlibBaseStream` already honours
`leaveOpen` for the stream under it. Regressed in 0.45.0 (commit `8df92321`,
PR #1132, async rework); present through 0.50.7, 0.51.0-beta2 and master of
2026-10-31; reproduced on stock .NET 10 with the NuGet package (0 bytes with
`leaveOpen: true`, 413–423 with `false`). No upstream issue exists. The fix is
the one upstream already made for `GZipStream` (PR #1346): always dispose the
`ZlibBaseStream`; the now-unread `_leaveOpen` field is commented out.

## Limits that are upstream's, not ours

- Encrypted zip entries written in streaming mode (data descriptor after the
  data, as bsdtar writes them) are refused: "SharpCompress cannot currently read
  non-seekable Zip Streams with encrypted data…".
- Compressed tars (`.tar.gz`, `.tar.bz2`, `.tar.xz`, `.tar.zst`) open through the
  reader API (`ReaderFactory`); the archive API (`ArchiveFactory`) knows GZip,
  Tar, Rar, Zip and 7Zip.

## Test archives

`apps_native/CompressTest/esp/compress/` has, besides archives made on the build
machine, five of upstream's own test archives (same MIT licence,
`tests/TestArchives/Archives/` at the tag above): `Zip.deflate.WinzipAES.zip`,
`7Zip.LZMA2.Aes.7z`, `7Zip.LZMA.Aes.7z`, `7Zip.LZMA2.7z`,
`Rar5.encrypted_filesOnly.rar`.

## What std gained for it (step223)

Added rather than cut, so other code gets them too: `IProgress<T>`,
`MemoryPool<T>`/`IMemoryOwner<T>`/`MemoryHandle`, `Memory<T>.Pin`,
`InlineArrayAttribute`, `GCHandle`, `SafeHandle`, `NativeMemory`, `MemoryStream`
whole, `Tuple<T1,T2,T3>`, `Monitor.Wait`/`Pulse`, `SemaphoreSlim`,
`ConcurrentQueue<T>`, `BlockingCollection<T>`, `WaitHandle` and its events,
`Thread.Start`/`Join`, `Environment.TickCount`, `Task.ConfigureAwait(ConfigureAwaitOptions)`,
LINQ `GroupBy`/`ToLookup`, `Stream.BeginRead`/`EndRead`, `RuntimeInformation`,
`Buffer.BlockCopy` over any primitive array, the state-machine attributes ILC's
dataflow needs, and `System.Security.Cryptography` (SHA-1/256/384/512, HMAC,
PBKDF2, AES, `CryptoStream`) for encrypted zip, rar and 7z.
