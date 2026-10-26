// System.Enum — ToObject and Parse over an enum Type, shared by both tiers'
// MinimalRuntime (where the rest of Enum lives).
//
// ToObject is the BCL's: the value boxed as the enum type. The MethodTable of
// an enum carries its underlying primitive as the element type, and RhBox
// copies that many bytes from the start of the value — little-endian, so a
// long holds every narrower underlying type in its low bytes. BCL truncates
// the same way (Enum.ToObject(typeof(ByteEnum), 300) is 44).
//
// Parse: numeric strings only. Member names live in reflection metadata,
// which images here do not carry (Type.Statics.cs); a name is refused with
// the BCL's "not found" ArgumentException, as for a name the enum lacks.

using System.Runtime.CompilerServices;

namespace System
{
    public abstract partial class Enum
    {
        public static object ToObject(Type enumType, int value) => ToObjectCore(enumType, value);

        public static object ToObject(Type enumType, long value) => ToObjectCore(enumType, value);

        public static object ToObject(Type enumType, sbyte value) => ToObjectCore(enumType, value);

        public static object ToObject(Type enumType, byte value) => ToObjectCore(enumType, value);

        public static object ToObject(Type enumType, short value) => ToObjectCore(enumType, value);

        public static object ToObject(Type enumType, ushort value) => ToObjectCore(enumType, value);

        public static object ToObject(Type enumType, uint value) => ToObjectCore(enumType, value);

        public static object ToObject(Type enumType, ulong value) => ToObjectCore(enumType, unchecked((long)value));

        public static object ToObject(Type enumType, object value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            return value switch
            {
                int v => ToObjectCore(enumType, v),
                long v => ToObjectCore(enumType, v),
                sbyte v => ToObjectCore(enumType, v),
                byte v => ToObjectCore(enumType, v),
                short v => ToObjectCore(enumType, v),
                ushort v => ToObjectCore(enumType, v),
                uint v => ToObjectCore(enumType, v),
                ulong v => ToObjectCore(enumType, unchecked((long)v)),
                char v => ToObjectCore(enumType, v),
                bool v => ToObjectCore(enumType, v ? 1 : 0),
                _ => throw new ArgumentException("The value passed in must be an enum base or an underlying type for an enum, such as an Int32.", nameof(value)),
            };
        }

        public static object Parse(Type enumType, string value) => Parse(enumType, value, ignoreCase: false);

        public static object Parse(Type enumType, string value, bool ignoreCase)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            CheckEnumType(enumType);

            string text = value.Trim();
            if (text.Length != 0 && (char.IsAsciiDigit(text[0]) || text[0] == '-' || text[0] == '+'))
            {
                if (long.TryParse(text, out long signed)) return ToObjectCore(enumType, signed);
                if (ulong.TryParse(text, out ulong unsigned)) return ToObjectCore(enumType, unchecked((long)unsigned));
            }

            throw new ArgumentException("Requested value '" + value + "' was not found.");
        }

        private static unsafe object ToObjectCore(Type enumType, long value)
        {
            CheckEnumType(enumType);
            var table = (Internal.Runtime.MethodTable*)enumType.TypeHandle._value;
            return SharpOS.Std.NoRuntime.GcRuntimeExports.RhBox(table, ref Unsafe.As<long, byte>(ref value));
        }

        private static void CheckEnumType(Type enumType)
        {
            if (enumType == null) throw new ArgumentNullException(nameof(enumType));
            if (!enumType.IsEnum) throw new ArgumentException("Type provided must be an Enum.", nameof(enumType));
        }
    }
}
