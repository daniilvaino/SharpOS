// Internal.Runtime.CompilerHelpers.MathHelpers — the checked floating point
// to integer conversions ILC calls for `checked((int)aDouble)` and kin
// (step198: Convert.ChangeType needed the first). Ported from dotnet/runtime
// release/8.0, src/coreclr/nativeaot/System.Private.CoreLib/src/Internal/
// Runtime/CompilerHelpers/MathHelpers.cs (MIT). Cuts: the 32-bit and ARM
// helpers (LMulOvf, divisions) — x64 has instructions for those; the
// unchecked RhpDbl2ULng and the remainders live in Runtime/MathHelpers.cs.

using System;
using System.Runtime;
using System.Runtime.CompilerServices;

namespace Internal.Runtime.CompilerHelpers
{
    public static class MathHelpers
    {
        [RuntimeExport("Dbl2IntOvf")]
        public static int Dbl2IntOvf(double val)
        {
            const double two31 = 2147483648.0;

            // Note that this expression also works properly for val = NaN case
            if (val > -two31 - 1 && val < two31)
                return unchecked((int)val);

            return ThrowIntOvf();
        }

        [RuntimeExport("Dbl2UIntOvf")]
        public static uint Dbl2UIntOvf(double val)
        {
            // Note that this expression also works properly for val = NaN case
            if (val > -1.0 && val < 4294967296.0)
                return unchecked((uint)val);

            return ThrowUIntOvf();
        }

        [RuntimeExport("Dbl2LngOvf")]
        public static long Dbl2LngOvf(double val)
        {
            const double two63 = 2147483648.0 * 4294967296.0;

            // Note that this expression also works properly for val = NaN case
            // We need to compare with the very next double to two63. 0x402 is epsilon to get us there.
            if (val > -two63 - 0x402 && val < two63)
                return unchecked((long)val);

            return ThrowLngOvf();
        }

        [RuntimeExport("Dbl2ULngOvf")]
        public static ulong Dbl2ULngOvf(double val)
        {
            const double two64 = 2.0 * 2147483648.0 * 4294967296.0;

            // Note that this expression also works properly for val = NaN case
            if (val > -1.0 && val < two64)
                return unchecked((ulong)val);

            return ThrowULngOvf();
        }

        [RuntimeExport("Flt2IntOvf")]
        public static int Flt2IntOvf(float val)
        {
            const double two31 = 2147483648.0;

            // Note that this expression also works properly for val = NaN case
            if (val > -two31 - 1 && val < two31)
                return ((int)val);

            return ThrowIntOvf();
        }

        [RuntimeExport("Flt2LngOvf")]
        public static long Flt2LngOvf(float val)
        {
            const double two63 = 2147483648.0 * 4294967296.0;

            // Note that this expression also works properly for val = NaN case
            // We need to compare with the very next double to two63. 0x402 is epsilon to get us there.
            if (val > -two63 - 0x402 && val < two63)
                return ((long)val);

            return ThrowIntOvf();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int ThrowIntOvf() => throw new OverflowException();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static uint ThrowUIntOvf() => throw new OverflowException();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static long ThrowLngOvf() => throw new OverflowException();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ulong ThrowULngOvf() => throw new OverflowException();
    }
}
