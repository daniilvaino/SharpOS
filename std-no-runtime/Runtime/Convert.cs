// System.Convert.ChangeType (step198): the BCL behaviour for the types
// IConvertible covers in .NET — bool, char, the integers, float, double,
// string — without IConvertible, which std's primitives do not implement:
// the source is read by its exact type, the target built with the same
// checked conversions Convert.ToXxx applies (OverflowException past the
// range, InvalidCastException where .NET has no conversion — char to and
// from bool, float or double), strings parsed invariantly, anything to string
// by ToString. A value already of the target type comes back as it is.
//
// Cuts: decimal and DateTime as targets (decimal until System.Decimal is in
// std), IFormatProvider is accepted and ignored (std has the invariant
// culture only), Nullable<T> targets, the rest of System.Convert (ToXxx,
// base64 and friends) — this class holds only what callers have needed.

namespace System
{
    public static partial class Convert
    {
        public static object ChangeType(object value, Type conversionType) => ChangeType(value, conversionType, null);

        public static object ChangeType(object value, Type conversionType, IFormatProvider provider)
        {
            if (conversionType == null) throw new ArgumentNullException(nameof(conversionType));
            if (value == null)
            {
                TypeCode nullTarget = Type.GetTypeCode(conversionType);
                if (nullTarget != TypeCode.Object && nullTarget != TypeCode.String)
                    throw new InvalidCastException("Null object cannot be converted to a value type.");
                return null;
            }
            if (value.GetType() == conversionType) return value;

            TypeCode target = Type.GetTypeCode(conversionType);
            if (target == TypeCode.String) return value.ToString();

            if (value is string text) return Parse(text, target);

            switch (target)
            {
                case TypeCode.Boolean:
                    if (value is char || value is float || value is double) break;
                    return Integral(value, target) != 0;
                case TypeCode.Char:
                    if (value is bool || value is float || value is double) break;
                    return checked((char)Integral(value, target));
                case TypeCode.SByte: return checked((sbyte)Integral(value, target));
                case TypeCode.Byte: return checked((byte)Integral(value, target));
                case TypeCode.Int16: return checked((short)Integral(value, target));
                case TypeCode.UInt16: return checked((ushort)Integral(value, target));
                case TypeCode.Int32: return checked((int)Integral(value, target));
                case TypeCode.UInt32: return checked((uint)Integral(value, target));
                case TypeCode.Int64: return Integral(value, target);
                case TypeCode.UInt64:
                    if (value is ulong u) return u;
                    return checked((ulong)Integral(value, target));
                case TypeCode.Single:
                    if (value is char) break;
                    return (float)Floating(value);
                case TypeCode.Double:
                    if (value is char) break;
                    return Floating(value);
            }
            throw new InvalidCastException("Invalid cast from '" + value.GetType().ToString() + "' to '" + conversionType.ToString() + "'.");
        }

        // The value as a long, rounded as Convert does from floating point
        // (to even), with the same overflow checks.
        private static long Integral(object value, TypeCode target)
        {
            switch (value)
            {
                case bool b: return b ? 1 : 0;
                case char c: return c;
                case sbyte v: return v;
                case byte v: return v;
                case short v: return v;
                case ushort v: return v;
                case int v: return v;
                case uint v: return v;
                case long v: return v;
                case ulong v: return checked((long)v);
                case float f: return checked((long)Math.Round((double)f));
                case double d: return checked((long)Math.Round(d));
            }
            throw new InvalidCastException("Object must implement IConvertible.");
        }

        private static double Floating(object value)
        {
            switch (value)
            {
                case float f: return f;
                case double d: return d;
                case ulong u: return u;
                case bool b: return b ? 1 : 0;
            }
            return Integral(value, TypeCode.Double);
        }

        private static object Parse(string text, TypeCode target)
        {
            switch (target)
            {
                case TypeCode.Boolean: return bool.Parse(text);
                case TypeCode.Char:
                    if (text.Length != 1) throw new FormatException("String must be exactly one character long.");
                    return text[0];
                case TypeCode.SByte: return checked((sbyte)long.Parse(text));
                case TypeCode.Byte: return checked((byte)long.Parse(text));
                case TypeCode.Int16: return checked((short)long.Parse(text));
                case TypeCode.UInt16: return checked((ushort)long.Parse(text));
                case TypeCode.Int32: return int.Parse(text);
                case TypeCode.UInt32: return checked((uint)long.Parse(text));
                case TypeCode.Int64: return long.Parse(text);
                case TypeCode.UInt64: return ParseUInt64(text);
                case TypeCode.Single: return float.Parse(text);
                case TypeCode.Double: return double.Parse(text);
            }
            throw new InvalidCastException("Invalid cast from 'System.String'.");
        }

        // std has no ulong.Parse yet: digits with an optional '+', white space
        // around, checked — FormatException and OverflowException as Parse.
        private static ulong ParseUInt64(string text)
        {
            string s = text.Trim();
            int i = s.Length > 0 && s[0] == '+' ? 1 : 0;
            if (i == s.Length) throw new FormatException("The input string '" + text + "' was not in a correct format.");
            ulong value = 0;
            for (; i < s.Length; i++)
            {
                char c = s[i];
                if (c < '0' || c > '9') throw new FormatException("The input string '" + text + "' was not in a correct format.");
                value = checked(value * 10 + (ulong)(c - '0'));
            }
            return value;
        }
    }
}
