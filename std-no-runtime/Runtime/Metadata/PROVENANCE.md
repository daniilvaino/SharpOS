# Metadata reader — ported

**Upstream:** dotnet/runtime release/8.0, v8.0.27 (local snapshot
`gc-experiment/dotnet-runtime-8.0`), MIT.

## Taken verbatim

- `Metadata/` — `src/coreclr/tools/Common/Internal/Metadata/NativeFormat/`:
  `MdBinaryReader.cs`, `MdBinaryReaderGen.cs`, `NativeFormatReaderCommonGen.cs`,
  `NativeFormatReaderGen.cs`, `NativeMetadataReader.cs` — the reader of ILC's
  embedded metadata (ReadyToRun blob 313, `EmbeddedMetadata`).
- `../Reflection/` — `src/libraries/System.Private.CoreLib/src/System/Reflection/`:
  `TypeAttributes`, `FieldAttributes`, `MethodAttributes`, `MethodImplAttributes`,
  `ParameterAttributes`, `PropertyAttributes`, `EventAttributes`,
  `GenericParameterAttributes`, `CallingConventions` — the enums the reader
  speaks in.

The NativeFormat primitives it stands on (`Internal.NativeFormat`) were in std
already (`Runtime/NativeFormat`, the same upstream files).

`SYSTEM_PRIVATE_CORELIB` is not defined: the only thing it adds in these files
is `[CLSCompliant(false)]`, and CoreLib here is not CLS-compliant.

## Ours

`ImageMetadata.cs` — the lookup stock does in
`TypeLoaderEnvironment.TryGetMetadataForNamedType`: the MethodTable's hash code
into the `TypeMap` blob (301), the candidate checked through the
`CommonFixupsTable` (308), the type definition read from blob 313.

Why the image has this metadata at all: CoreLib is its own assembly named
System.Private.CoreLib, and ILC builds reflection metadata only then
(`ILCompiler/Program.cs`, `supportsReflection`; done/step251.md).
