// OptionalFields stream reader for MethodTable.
//
// Stream format: sequence of <header, value> pairs.
//   - header: 1 byte — bits 0-6 = tag (EETypeOptionalFieldTag), bit 7 = last-entry flag.
//   - value: variable-length unsigned int via NativeFormatDecoder.
//
// Tag values (stable across NativeAOT history):
//   0 = RareFlags
//   1 = DispatchMap
//   2 = ValueTypeFieldPadding
//   3 = NullableValueOffset
//
// Source: OptionalFieldsReader in dotnet-runtime Common/src/Internal/Runtime/MethodTable.cs

namespace SharpOS.Std.NoRuntime
{
    public enum EETypeOptionalFieldTag : byte
    {
        RareFlags = 0,
        DispatchMap = 1,
        ValueTypeFieldPadding = 2,
        NullableValueOffset = 3,
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
