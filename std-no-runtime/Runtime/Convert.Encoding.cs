// System.Convert — Base64 and hex text, and ToDecimal. Signatures and results
// are the BCL's (dotnet/runtime main, .NET 10: Convert.cs, Convert.Base64.cs,
// MIT).
//
// SharpOS: the Base64 members run over the System.Buffers.Text.Base64 port
// (UTF-8 bytes) instead of Convert's own char-level codec; FromBase64String
// drops the white space the BCL allows (' ', '\t', '\r', '\n') before
// decoding. ToBase64String's line-breaking option is not ported. Hex goes
// through HexConverter, as in the BCL.

using System.Buffers;
using System.Buffers.Text;

namespace System
{
    public static partial class Convert
    {
        // ---- Base64 ----

        public static string ToBase64String(byte[] inArray)
        {
            if (inArray == null) throw new ArgumentNullException(nameof(inArray));
            return ToBase64String(new ReadOnlySpan<byte>(inArray));
        }

        public static string ToBase64String(byte[] inArray, int offset, int length)
        {
            if (inArray == null) throw new ArgumentNullException(nameof(inArray));
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length), "Index was out of range. Must be non-negative and less than the size of the collection.");
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), "Value must be positive.");
            if (offset > inArray.Length - length) throw new ArgumentOutOfRangeException(nameof(offset), "Offset and length must refer to a position in the string.");
            return ToBase64String(new ReadOnlySpan<byte>(inArray, offset, length));
        }

        public static string ToBase64String(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length == 0) return string.Empty;

            var utf8 = new byte[Base64.GetMaxEncodedToUtf8Length(bytes.Length)];
            Base64.EncodeToUtf8(bytes, utf8, out _, out int written);

            var chars = new char[written];
            for (int i = 0; i < written; i++) chars[i] = (char)utf8[i];
            return new string(chars);
        }

        public static byte[] FromBase64String(string s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            return FromBase64CharSpan(s.AsSpan());
        }

        public static byte[] FromBase64CharArray(char[] inArray, int offset, int length)
        {
            if (inArray == null) throw new ArgumentNullException(nameof(inArray));
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length), "Index was out of range. Must be non-negative and less than the size of the collection.");
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), "Value must be positive.");
            if (offset > inArray.Length - length) throw new ArgumentOutOfRangeException(nameof(offset), "Offset and length must refer to a position in the string.");
            return FromBase64CharSpan(new ReadOnlySpan<char>(inArray, offset, length));
        }

        private static byte[] FromBase64CharSpan(ReadOnlySpan<char> chars)
        {
            var utf8 = new byte[chars.Length];
            int count = 0;
            foreach (char c in chars)
            {
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') continue;
                if (c > 0x7F) throw new FormatException("The input is not a valid Base-64 string as it contains a non-base 64 character, more than two padding characters, or an illegal character among the padding characters.");
                utf8[count++] = (byte)c;
            }

            var bytes = new byte[Base64.GetMaxDecodedFromUtf8Length(count)];
            OperationStatus status = Base64.DecodeFromUtf8(new ReadOnlySpan<byte>(utf8, 0, count), bytes, out _, out int written);
            if (status != OperationStatus.Done)
                throw new FormatException("The input is not a valid Base-64 string as it contains a non-base 64 character, more than two padding characters, or an illegal character among the padding characters.");

            if (written == bytes.Length) return bytes;
            var result = new byte[written];
            Array.Copy(bytes, result, written);
            return result;
        }

        // ---- Hex ----

        public static string ToHexString(byte[] inArray)
        {
            if (inArray == null) throw new ArgumentNullException(nameof(inArray));
            return ToHexString(new ReadOnlySpan<byte>(inArray));
        }

        public static string ToHexString(byte[] inArray, int offset, int length)
        {
            if (inArray == null) throw new ArgumentNullException(nameof(inArray));
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length), "Index was out of range. Must be non-negative and less than the size of the collection.");
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), "Value must be positive.");
            if (offset > inArray.Length - length) throw new ArgumentOutOfRangeException(nameof(offset), "Offset and length must refer to a position in the string.");
            return ToHexString(new ReadOnlySpan<byte>(inArray, offset, length));
        }

        public static string ToHexString(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length == 0) return string.Empty;
            if (bytes.Length > int.MaxValue / 2) throw new ArgumentOutOfRangeException(nameof(bytes), "The input array is too large.");
            return HexConverter.ToString(bytes, HexConverter.Casing.Upper);
        }

        public static string ToHexStringLower(byte[] inArray)
        {
            if (inArray == null) throw new ArgumentNullException(nameof(inArray));
            if (inArray.Length == 0) return string.Empty;
            return HexConverter.ToString(inArray, HexConverter.Casing.Lower);
        }

        public static byte[] FromHexString(string s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            return FromHexString(s.AsSpan());
        }

        public static byte[] FromHexString(ReadOnlySpan<char> chars)
        {
            if (chars.Length == 0) return Array.Empty<byte>();
            if ((uint)chars.Length % 2 != 0) throw new FormatException("The input is not a valid hex string as its length is not a multiple of 2.");

            byte[] result = new byte[chars.Length >> 1];
            if (!HexConverter.TryDecodeFromUtf16(chars, result, out _))
                throw new FormatException("The input is not a valid hex string as it contains a non-hex character.");
            return result;
        }

        // ---- Decimal ----

        public static decimal ToDecimal(double value) => (decimal)value;

        public static decimal ToDecimal(float value) => (decimal)value;

        public static decimal ToDecimal(int value) => value;

        public static decimal ToDecimal(long value) => value;

        public static decimal ToDecimal(decimal value) => value;

        public static decimal ToDecimal(string? value) => value == null ? 0m : decimal.Parse(value);

        public static decimal ToDecimal(string? value, IFormatProvider? provider) => value == null ? 0m : decimal.Parse(value, provider);

        public static decimal ToDecimal(object? value) => ToDecimal(value, null);

        // BCL: ((IConvertible)value).ToDecimal(provider). The primitives' IConvertible
        // is not ported; the conversions it would make are spelled out.
        public static decimal ToDecimal(object? value, IFormatProvider? provider) => value switch
        {
            null => 0m,
            decimal d => d,
            double d => (decimal)d,
            float f => (decimal)f,
            int i => i,
            long l => l,
            uint u => u,
            ulong u => u,
            short s => s,
            ushort u => u,
            byte b => b,
            sbyte b => b,
            bool b => b ? 1m : 0m,
            string s => decimal.Parse(s, provider),
            _ => throw new InvalidCastException("Unable to cast object to type 'System.Decimal'."),
        };
    }
}
