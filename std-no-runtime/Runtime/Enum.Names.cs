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
    }
}
