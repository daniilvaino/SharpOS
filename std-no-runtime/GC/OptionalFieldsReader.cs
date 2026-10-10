// OptionalFields stream reader for MethodTable.
//
// Stream format: sequence of <header, value> pairs.
//   - header: 1 byte — bits 0-6 = tag (EETypeOptionalFieldTag), bit 7 = last-entry flag.
//   - value: variable-length unsigned int via NativeFormatDecoder.
//
// Tag values as in release/8.0 (MethodTable.Constants.cs,
// gc-experiment/dotnet-runtime-8.0): the dispatch map left the optional
// fields for a field of its own (HasDispatchMap), and the tags after it moved
// down. The older numbering read Nullable<T>'s value offset as absent — every
// boxed T? came out shifted (step200).
//   0 = RareFlags
//   1 = ValueTypeFieldPadding
//   2 = NullableValueOffset
//
// Source: OptionalFieldsReader in dotnet-runtime Common/src/Internal/Runtime/MethodTable.cs

namespace SharpOS.Std.NoRuntime
{
    public enum EETypeOptionalFieldTag : byte
    {
        RareFlags = 0,
        ValueTypeFieldPadding = 1,
        NullableValueOffset = 2,
    }

    public static unsafe class OptionalFieldsReader
    {
        public static uint GetInlineField(byte* pFields, EETypeOptionalFieldTag tag, uint defaultValue)
        {
            if (pFields == null) return defaultValue;

            bool isLastField = false;
            while (!isLastField)
            {
                byte header = NativeFormatDecoder.ReadUInt8(ref pFields);
                isLastField = (header & 0x80) != 0;
                EETypeOptionalFieldTag currentTag = (EETypeOptionalFieldTag)(header & 0x7f);
                uint value = NativeFormatDecoder.DecodeUnsigned(ref pFields);

                if (currentTag == tag)
                    return value;
            }

            return defaultValue;
        }
    }
}
