// RhpDbl2ULng, RhpDblRem, RhpFltRem — what ILC calls for `(ulong)aDouble`
// and for `%` on float and double, which x64 has no instruction for.
//
// Ported from dotnet/runtime release/8.0
// src/coreclr/nativeaot/Runtime/MathHelpers.cpp (MIT): the ECMA cases —
// divisor zero or dividend infinite gives NaN, divisor infinite gives the
// dividend. fmod itself is musl's src/math/fmod.c (MIT): exact, on the bits —
// there is no libm here, and x87 fprem would need a stub.
//
// Missing on both tiers until `dynamic` (step 193) did arithmetic on boxed
// numbers of any type and linked them all.

using System.Runtime;

namespace System.Runtime
{
    internal static class MathHelpers
    {
        [RuntimeExport("RhpDbl2ULng")]
        public static ulong RhpDbl2ULng(double val)
        {
            const double two63 = 2147483648.0 * 4294967296.0;
            if (val < two63) return (ulong)(long)val;
            // subtract 0x8000000000000000, do the convert then add it back again
            return (ulong)(long)(val - two63) + 0x8000000000000000UL;
        }

        [RuntimeExport("RhpFltRem")]
        public static float RhpFltRem(float dividend, float divisor)
        {
            if (divisor == 0 || !IsFinite(dividend)) return float.NaN;
            if (!IsFinite(divisor) && divisor == divisor) return dividend;
            return (float)Fmod(dividend, divisor);   // exact: the remainder of two floats is a float
        }

        [RuntimeExport("RhpDblRem")]
        public static double RhpDblRem(double dividend, double divisor)
        {
            if (divisor == 0 || !IsFinite(dividend)) return double.NaN;
            if (!IsFinite(divisor) && divisor == divisor) return dividend;
            return Fmod(dividend, divisor);
        }

        private static bool IsFinite(double d) => (BitConverter.DoubleToInt64Bits(d) & 0x7FF0000000000000L) != 0x7FF0000000000000L;

        // musl fmod.c
        private static double Fmod(double x, double y)
        {
            ulong uxi = (ulong)BitConverter.DoubleToInt64Bits(x);
            ulong uyi = (ulong)BitConverter.DoubleToInt64Bits(y);
            int ex = (int)(uxi >> 52 & 0x7ff);
            int ey = (int)(uyi >> 52 & 0x7ff);
            ulong sx = uxi >> 63;
            ulong i;

            if (uyi << 1 == 0 || y != y || ex == 0x7ff)
                return (x * y) / (x * y);
            if (uxi << 1 <= uyi << 1)
            {
                if (uxi << 1 == uyi << 1)
                    return 0 * x;
                return x;
            }

            // normalize x and y
            if (ex == 0)
            {
                for (i = uxi << 12; i >> 63 == 0; ex--, i <<= 1) { }
                uxi <<= -ex + 1;
            }
            else
            {
                uxi &= ulong.MaxValue >> 12;
                uxi |= 1UL << 52;
            }
            if (ey == 0)
            {
                for (i = uyi << 12; i >> 63 == 0; ey--, i <<= 1) { }
                uyi <<= -ey + 1;
            }
            else
            {
                uyi &= ulong.MaxValue >> 12;
                uyi |= 1UL << 52;
            }

            // x mod y
            for (; ex > ey; ex--)
            {
                i = uxi - uyi;
                if (i >> 63 == 0)
                {
                    if (i == 0)
                        return 0 * x;
                    uxi = i;
                }
                uxi <<= 1;
            }
            i = uxi - uyi;
            if (i >> 63 == 0)
            {
                if (i == 0)
                    return 0 * x;
                uxi = i;
            }
            for (; uxi >> 52 == 0; uxi <<= 1, ex--) { }

            // scale result
            if (ex > 0)
            {
                uxi -= 1UL << 52;
                uxi |= (ulong)ex << 52;
            }
            else
            {
                uxi >>= -ex + 1;
            }
            uxi |= sx << 63;
            return BitConverter.Int64BitsToDouble((long)uxi);
        }
    }
}
