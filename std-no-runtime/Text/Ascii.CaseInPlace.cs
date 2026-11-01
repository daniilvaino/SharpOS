// Ascii.ToLowerInPlace / ToUpperInPlace — BCL's (System.Private.CoreLib,
// Text/Ascii.CaseConversion.cs) signatures, scalar (step224: Utf8Formatter's
// dates). Bytes or chars past ASCII stop the conversion, as BCL's do:
// OperationStatus.InvalidData with the count converted before them.

using System.Buffers;

namespace System.Text
{
    public static partial class Ascii
    {
        public static OperationStatus ToLowerInPlace(Span<byte> value, out int bytesWritten) => CaseInPlace(value, lower: true, out bytesWritten);

        public static OperationStatus ToUpperInPlace(Span<byte> value, out int bytesWritten) => CaseInPlace(value, lower: false, out bytesWritten);

        public static OperationStatus ToLowerInPlace(Span<char> value, out int charsWritten) => CaseInPlace(value, lower: true, out charsWritten);

        public static OperationStatus ToUpperInPlace(Span<char> value, out int charsWritten) => CaseInPlace(value, lower: false, out charsWritten);

        private static OperationStatus CaseInPlace(Span<byte> value, bool lower, out int written)
        {
            for (int i = 0; i < value.Length; i++)
            {
                byte c = value[i];
                if (c > 0x7F) { written = i; return OperationStatus.InvalidData; }
                if (lower && c >= (byte)'A' && c <= (byte)'Z') value[i] = (byte)(c | 0x20);
                else if (!lower && c >= (byte)'a' && c <= (byte)'z') value[i] = (byte)(c & ~0x20);
            }
            written = value.Length;
            return OperationStatus.Done;
        }

        private static OperationStatus CaseInPlace(Span<char> value, bool lower, out int written)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c > (char)0x7F) { written = i; return OperationStatus.InvalidData; }
                if (lower && c >= 'A' && c <= 'Z') value[i] = (char)(c | 0x20);
                else if (!lower && c >= 'a' && c <= 'z') value[i] = (char)(c & ~0x20);
            }
            written = value.Length;
            return OperationStatus.Done;
        }
    }
}
