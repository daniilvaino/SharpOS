// Ported from dotnet/runtime 8.0, src/libraries/System.Formats.Cbor (MIT).
//
// Rewritten rather than copied, because almost every member of the original was
// a one-line forward to something this std does not have. What is left is the
// same set of names with the same signatures, so the call sites are untouched.
//
// Cuts, all of them missing dependencies rather than choices:
//   * CreateBigIntegerFromUnsignedBigEndianBytes, CreateUnsignedBigEndianBytesFromBigInteger
//     and GetBitsFromDecimal - no BigInteger, no decimal. Their callers (the
//     bignum and decimal-fraction tags) are cut with them.
//   * ReadHalfBigEndian - no System.Half.
//   * UnixEpoch - no DateTimeOffset.
//
// See docs/nativeaot-nostd-kernel-limits.md.

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace System.Formats.Cbor
{
    internal static partial class CborHelpers
    {
        public static int GetBytes(Encoding encoding, ReadOnlySpan<char> source, Span<byte> destination)
            => encoding.GetBytes(source, destination);

        public static int GetByteCount(Encoding encoding, ReadOnlySpan<char> chars)
            => encoding.GetByteCount(chars);

        public static int GetChars(Encoding encoding, ReadOnlySpan<byte> source, Span<char> destination)
            => encoding.GetChars(source, destination);

        public static int GetCharCount(Encoding encoding, ReadOnlySpan<byte> source)
            => encoding.GetCharCount(source);

        public static string GetString(Encoding encoding, ReadOnlySpan<byte> bytes)
            => encoding.GetString(bytes);

        /// <summary>The text of an indefinite-length string, assembled chunk by chunk.</summary>
        /// <remarks>
        /// The original hands string.Create a callback that fills the string's
        /// own storage in place. This std has no string.Create, so the callback
        /// fills an array and the string is built from it - one copy more, on a
        /// path CTAP2 forbids outright and nothing hot ever takes.
        /// </remarks>
        public static string BuildStringFromIndefiniteLengthTextString<TState>(int length, TState state, SpanAction<char, TState> action)
        {
            char[] buffer = new char[length];
            action(buffer, state);
            return new string(buffer, 0, length);
        }

        /// <summary>A half-precision float, widened - without a Half type.</summary>
        /// <remarks>
        /// The original reads a System.Half and hands it to HalfHelpers to
        /// widen. There is no Half here, and cutting the path would have made
        /// ReadSingle throw on a perfectly legal encoding, so the fifteen lines
        /// of IEEE-754 binary16 are written out instead. Nothing about them is
        /// ours: sign, five exponent bits, ten of mantissa, with subnormals
        /// normalised by shifting and infinities and NaNs passed through.
        /// </remarks>
        public static float ReadHalfAsSingle(ReadOnlySpan<byte> source)
        {
            ushort h = BinaryPrimitives.ReadUInt16BigEndian(source);

            uint sign = (uint)(h >> 15) << 31;
            int exponent = (h >> 10) & 0x1F;
            uint mantissa = (uint)(h & 0x3FF);
            uint bits;

            if (exponent == 0)
            {
                if (mantissa == 0)
                {
                    bits = sign;                                   // signed zero
                }
                else
                {
                    // Subnormal: shift until the implicit bit appears, and
                    // charge every shift to the exponent.
                    int shifts = -1;
                    do { shifts++; mantissa <<= 1; } while ((mantissa & 0x400) == 0);
                    mantissa &= 0x3FF;
                    bits = sign | (uint)((127 - 15 - shifts) << 23) | (mantissa << 13);
                }
            }
            else if (exponent == 0x1F)
            {
                bits = sign | 0x7F800000u | (mantissa << 13);       // infinity or NaN
            }
            else
            {
                bits = sign | (uint)((exponent - 15 + 127) << 23) | (mantissa << 13);
            }

            return BitConverter.Int32BitsToSingle((int)bits);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float ReadSingleBigEndian(ReadOnlySpan<byte> source)
            => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(source));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double ReadDoubleBigEndian(ReadOnlySpan<byte> source)
            => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(source));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteSingleBigEndian(Span<byte> destination, float value)
            => BinaryPrimitives.WriteInt32BigEndian(destination, BitConverter.SingleToInt32Bits(value));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteDoubleBigEndian(Span<byte> destination, double value)
            => BinaryPrimitives.WriteInt64BigEndian(destination, BitConverter.DoubleToInt64Bits(value));
    }
}
