# System.Text.Encoding.CodePages

**Source:** dotnet/runtime **v8.0.27** (`gc-experiment/dotnet-runtime-8.0`),
`src/libraries/System.Text.Encoding.CodePages/src` — the non-Windows `.NETCoreApp`
compile items, `Data/codepages.nlp` embedded under its upstream name.
**License:** MIT, © .NET Foundation and Contributors.
**Taken by:** `CodePages.props` beside this file, app tier; `SharpCompress.props`
imports it. Use as upstream: `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)`,
then `Encoding.GetEncoding(1251)`, `GetEncoding("shift_jis")` and so on.

It sits on std's BCL 8 encoding hierarchy (`Text/Encoding.cs` and the rest,
release 0.1), the base classes it was written against.

Every edit is marked in place with `// SharpOS cut:` or `// SharpOS:`.

## Changed, and why

| Where | What | Why |
|---|---|---|
| `DecoderNLS`, `EncoderNLS`, `EncodingTable`, `EncodingCharBuffer`, `EncodingByteBuffer` | renamed `CodePages…` within this folder | upstream the library is its own assembly and sees only CoreLib's public API; compiled into one with std's CoreLib encodings, its private copies would collide with CoreLib's internal types of the same names |
| `CodePagesDecoderNLS/EncoderNLS.InternalHasFallbackBuffer`, `EncodingNLS.ThrowBytesOverflow/ThrowCharsOverflow` | `new` | the same: CoreLib's internal members of those names are visible here |
| `CodePagesEncodingProvider`, `EncodingTable` caches | `CodePagesCacheLock` (one `Monitor`) for `ReaderWriterLockSlim` | std has no `ReaderWriterLockSlim`: its per-thread state is `[ThreadStatic]`, which this runtime lacks; lookups serialize instead of running side by side |
| `BaseCodePageEncoding`, `DecoderNLS`, `EncoderNLS`, `ISCIIEncoding` | `ISerializable`, `GetObjectData` | no legacy formatter serialization |
| `EncodingNLS` | `using System.Resources` | unused, and std has no `System.Resources` |
| `SafeAllocHHandle.cs` | lives in this folder, not `Microsoft/Win32/SafeHandles/` | `.gitignore` drops every `Win32\` directory |

`SR.CodePages.cs` holds the library's resource strings that std did not have;
three keys exist in CoreLib under the same name with other wording
(`NotSupported_NoCodepageData`, `Argument_EncodingConversionOverflowBytes/Chars`) —
CoreLib's text shows.

## What std gained for it

The BCL 8 encoding hierarchy (`Encoding`, `Encoder`/`Decoder` with state,
fallbacks, `EncodingProvider`, `UTF7`, `TranscodingStream`),
`System.Numerics.Vector<T>`, `Marshal.AllocHGlobal`/`FreeHGlobal`, `SafeBuffer`,
`SafeHandleZeroOrMinusOneIsInvalid`, `UnmanagedMemoryStream`,
`Object.MemberwiseClone`, `string(char*)`.
