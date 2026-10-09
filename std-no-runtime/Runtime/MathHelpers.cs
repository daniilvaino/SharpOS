// RhpDbl2ULng, RhpDblRem, RhpFltRem — what ILC calls for `(ulong)aDouble`
// and for `%` on float and double, which x64 has no instruction for.
//
// Ported from dotnet/runtime release/8.0
// src/coreclr/nativeaot/Runtime/MathHelpers.cpp (MIT): the ECMA cases —
// divisor zero or dividend infinite gives NaN, divisor infinite gives the
// dividend. fmod itself is SharpLibm's (exact, on the bits; step198 — it was
// a private copy of musl's fmod here before).
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
            return SharpLibm.Libm.fmodf(dividend, divisor);
        }

        [RuntimeExport("RhpDblRem")]
        public static double RhpDblRem(double dividend, double divisor)
        {
            if (divisor == 0 || !IsFinite(dividend)) return double.NaN;
            if (!IsFinite(divisor) && divisor == divisor) return dividend;
            return SharpLibm.Libm.fmod(dividend, divisor);
        }

        private static bool IsFinite(double d) => (BitConverter.DoubleToInt64Bits(d) & 0x7FF0000000000000L) != 0x7FF0000000000000L;
    }
}
