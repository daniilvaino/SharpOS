// Math.Round to a number of fractional digits. Ported from dotnet/runtime
// main (.NET 10, the fork dotnet-runtime-sharpos), src/libraries/
// System.Private.CoreLib/src/System/Math.cs (MIT), verbatim; the
// out-of-range digits throw is written out (ThrowHelper's has no twin here).

namespace System
{
    public static partial class Math
    {
        private const int maxRoundingDigits = 15;

        private const double doubleRoundLimit = 1e16d;

        // This table is required for the Round function which can specify the number of digits to round to
        private static ReadOnlySpan<double> RoundPower10Double =>
        [
            1E0, 1E1, 1E2, 1E3, 1E4, 1E5, 1E6, 1E7, 1E8,
            1E9, 1E10, 1E11, 1E12, 1E13, 1E14, 1E15
        ];

        public static double Round(double value, int digits)
        {
            return Round(value, digits, MidpointRounding.ToEven);
        }

        public static double Round(double value, int digits, MidpointRounding mode)
        {
            if ((uint)digits > maxRoundingDigits)
            {
                throw new ArgumentOutOfRangeException(nameof(digits), "Rounding digits must be between 0 and 15, inclusive.");
            }

            if (Abs(value) < doubleRoundLimit)
            {
                double power10 = RoundPower10Double[digits];
                value = Round(value * power10, mode) / power10;
            }

            return value;
        }
    }
}
