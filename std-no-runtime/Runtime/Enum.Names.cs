// Enum.GetNames / GetValues / TryParse — BCL's signatures (step224, System
// .Text.Json's EnumConverter).
//
// SharpOS cut: an enum's member names and its list of members are metadata
// the image does not carry, so GetNames and GetValues answer empty arrays —
// a converter built over them knows no names and works by number. TryParse
// understands numbers only, as Parse does (Enum.Statics.cs).

namespace System
{
    public abstract partial class Enum
    {
        public static string[] GetNames<TEnum>() where TEnum : struct, Enum => Array.Empty<string>();

        public static string[] GetNames(Type enumType)
        {
            CheckEnumType(enumType);
            return Array.Empty<string>();
        }

        public static TEnum[] GetValues<TEnum>() where TEnum : struct, Enum => Array.Empty<TEnum>();

        public static Array GetValues(Type enumType)
        {
            CheckEnumType(enumType);
            return Array.Empty<object>();
        }

        public static bool TryParse<TEnum>(string? value, out TEnum result) where TEnum : struct =>
            TryParse(value, ignoreCase: false, out result);

        public static bool TryParse<TEnum>(string? value, bool ignoreCase, out TEnum result) where TEnum : struct
        {
            _ = ignoreCase;
            if (value != null && typeof(TEnum).IsEnum && TryParseNumber(typeof(TEnum), value, out object? boxed))
            {
                result = (TEnum)boxed!;
                return true;
            }
            result = default;
            return false;
        }

        public static bool TryParse(Type enumType, string? value, out object? result) =>
            TryParse(enumType, value, ignoreCase: false, out result);

        public static bool TryParse(Type enumType, string? value, bool ignoreCase, out object? result)
        {
            CheckEnumType(enumType);
            _ = ignoreCase;
            result = null;
            return value != null && TryParseNumber(enumType, value, out result);
        }

        private static bool TryParseNumber(Type enumType, string value, out object? result)
        {
            string text = value.Trim();
            if (text.Length != 0 && (char.IsAsciiDigit(text[0]) || text[0] == '-' || text[0] == '+'))
            {
                if (long.TryParse(text, out long signed)) { result = ToObjectCore(enumType, signed); return true; }
                if (ulong.TryParse(text, out ulong unsigned)) { result = ToObjectCore(enumType, unchecked((long)unsigned)); return true; }
            }
            result = null;
            return false;
        }

        // Upstream's internal TryFormatUnconstrained (Enum.cs), which the UTF-8 interpolated
        // string handler calls. SharpOS: by number — no member names here, as above.
        internal static unsafe bool TryFormatUnconstrained<TEnum>(TEnum value, Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default)
        {
            _ = format;
            TypeCode code = Type.GetTypeCode(typeof(TEnum));
            ref byte raw = ref System.Runtime.CompilerServices.Unsafe.As<TEnum, byte>(ref value);
            string text = code switch
            {
                TypeCode.SByte => System.Runtime.CompilerServices.Unsafe.As<byte, sbyte>(ref raw).ToString(),
                TypeCode.Byte => raw.ToString(),
                TypeCode.Int16 => System.Runtime.CompilerServices.Unsafe.As<byte, short>(ref raw).ToString(),
                TypeCode.UInt16 => System.Runtime.CompilerServices.Unsafe.As<byte, ushort>(ref raw).ToString(),
                TypeCode.Int32 => System.Runtime.CompilerServices.Unsafe.As<byte, int>(ref raw).ToString(),
                TypeCode.UInt32 => System.Runtime.CompilerServices.Unsafe.As<byte, uint>(ref raw).ToString(),
                TypeCode.Int64 => System.Runtime.CompilerServices.Unsafe.As<byte, long>(ref raw).ToString(),
                _ => System.Runtime.CompilerServices.Unsafe.As<byte, ulong>(ref raw).ToString(),
            };
            if (text.AsSpan().TryCopyTo(destination))
            {
                charsWritten = text.Length;
                return true;
            }
            charsWritten = 0;
            return false;
        }
    }
}
