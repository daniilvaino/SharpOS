// App-local stand-in — see Compat\Drawing.cs for why these live here.
//
// The upstream trace logger prints PPU shift registers in binary
// (Convert.ToString(value, 2)), and that is the only thing it asks Convert
// for. A full Convert is a large BCL type; this is the two overloads actually
// reachable, kept app-local rather than dropped into std under a canonical
// name it does not earn.

namespace System
{
    public static class Convert
    {
        /// <summary>
        /// Value in the given base. Only 2, 8, 10 and 16 are legal bases for
        /// this BCL method, and like the original this prints the raw
        /// two's-complement bits for negative values in the non-decimal bases.
        /// </summary>
        public static string ToString(int value, int toBase)
            => ToString((long)(uint)value, toBase, 32);

        public static string ToString(long value, int toBase)
            => ToString(value, toBase, 64);

        private static string ToString(long value, int toBase, int bits)
        {
            if (toBase == 10) return SharpOS.Std.NoRuntime.NumberFormatting.LongToString(value);
            if (toBase != 2 && toBase != 8 && toBase != 16)
                throw new ArgumentException("Invalid base.");

            ulong bitsValue = bits == 32 ? (uint)value : (ulong)value;
            if (bitsValue == 0) return "0";

            char[] buf = new char[64];
            int count = 0;
            while (bitsValue != 0)
            {
                uint digit = (uint)(bitsValue % (ulong)toBase);
                buf[count++] = digit < 10 ? (char)('0' + digit) : (char)('a' + (digit - 10));
                bitsValue /= (ulong)toBase;
            }

            char[] result = new char[count];
            for (int i = 0; i < count; i++) result[i] = buf[count - 1 - i];
            return new string(result);
        }
    }
}
