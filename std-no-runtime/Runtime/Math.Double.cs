// System.Math / System.MathF — the double and float members, over SharpLibm
// (the C math library in C#; ../SharpLibm, step198). Before that these were
// hand-written series (Taylor sin/cos, atanh-series log) good to ~1e-9 and
// valid only for |x| < 2^63; SharpLibm's transcendentals are CORE-MATH's,
// correctly rounded.
//
// Where the BCL and C differ, the BCL contract is kept: Math.Round rounds
// halfway cases to even (C's roundeven, not C's round); Max/Min propagate NaN
// (Math.cs). Sqrt is an intrinsic: ILC emits sqrtsd.

using SharpLibm;

namespace System
{
    public static partial class Math
    {
        public const double PI = 3.14159265358979323846;
        public const double E = 2.7182818284590452354;
        public const double Tau = 6.283185307179586476925;

        public static double Abs(double value) => Libm.fabs(value);
        public static float Abs(float value) => Libm.fabsf(value);

        // Square root: an intrinsic — the JIT/ILC emits sqrtsd (SSE2), as in
        // NativeAOT's CoreLib, which is also what SharpLibm's sqrt relies on.
        // The body is only reached if the call is not expanded, and then it
        // recurses: a missing expansion shows at once instead of computing
        // something approximate.
        [System.Runtime.CompilerServices.Intrinsic]
        public static double Sqrt(double d) => Sqrt(d);

        public static double Floor(double d) => Libm.floor(d);
        public static double Ceiling(double d) => Libm.ceil(d);
        public static double Truncate(double d) => Libm.trunc(d);
        public static double Round(double a) => Libm.roundeven(a);

        public static double Round(double value, MidpointRounding mode) => mode switch
        {
            MidpointRounding.ToEven => Libm.roundeven(value),
            MidpointRounding.AwayFromZero => Libm.round(value),
            MidpointRounding.ToZero => Libm.trunc(value),
            MidpointRounding.ToNegativeInfinity => Libm.floor(value),
            MidpointRounding.ToPositiveInfinity => Libm.ceil(value),
            _ => throw new ArgumentException("The value is not a valid MidpointRounding value.", nameof(mode)),
        };

        public static double Sin(double a) => Libm.sin(a);
        public static double Cos(double d) => Libm.cos(d);
        public static double Tan(double a) => Libm.tan(a);
        public static (double Sin, double Cos) SinCos(double x) => (Libm.sin(x), Libm.cos(x));
        public static double Asin(double d) => Libm.asin(d);
        public static double Acos(double d) => Libm.acos(d);
        public static double Atan(double d) => Libm.atan(d);
        public static double Atan2(double y, double x) => Libm.atan2(y, x);
        public static double Sinh(double value) => Libm.sinh(value);
        public static double Cosh(double value) => Libm.cosh(value);
        public static double Tanh(double value) => Libm.tanh(value);
        public static double Asinh(double d) => Libm.asinh(d);
        public static double Acosh(double d) => Libm.acosh(d);
        public static double Atanh(double d) => Libm.atanh(d);

        public static double Exp(double d) => Libm.exp(d);
        public static double Log(double d) => Libm.log(d);
        public static double Log10(double d) => Libm.log10(d);
        public static double Log2(double x) => Libm.log2(x);

        public static double Log(double a, double newBase)
        {
            if (double.IsNaN(a)) return a;
            if (double.IsNaN(newBase)) return newBase;
            if (newBase == 1 || (a != 1 && (newBase == 0 || double.IsPositiveInfinity(newBase)))) return double.NaN;
            return Libm.log(a) / Libm.log(newBase);
        }

        public static double Pow(double x, double y) => Libm.pow(x, y);
        public static double Cbrt(double d) => Libm.cbrt(d);
        public static double IEEERemainder(double x, double y) => Libm.remainder(x, y);
        public static double FusedMultiplyAdd(double x, double y, double z) => Libm.fma(x, y, z);
        public static double ScaleB(double x, int n) => Libm.scalbn(x, n);
        public static int ILogB(double x) => Libm.ilogb(x);
        public static double CopySign(double x, double y) => Libm.copysign(x, y);
        public static double BitIncrement(double x) => Libm.nextafter(x, double.PositiveInfinity);
        public static double BitDecrement(double x) => Libm.nextafter(x, double.NegativeInfinity);
        public static double ReciprocalEstimate(double d) => 1.0 / d;
        public static double ReciprocalSqrtEstimate(double d) => Libm.rsqrt(d);
    }

    public static class MathF
    {
        public const float PI = 3.14159265f;
        public const float E = 2.71828183f;
        public const float Tau = 6.283185307f;

        public static float Abs(float x) => Libm.fabsf(x);
        public static float Sqrt(float x) => Libm.sqrtf(x);
        public static float Floor(float x) => Libm.floorf(x);
        public static float Ceiling(float x) => Libm.ceilf(x);
        public static float Truncate(float x) => Libm.truncf(x);
        public static float Round(float x) => Libm.roundevenf(x);

        public static float Sin(float x) => Libm.sinf(x);
        public static float Cos(float x) => Libm.cosf(x);
        public static float Tan(float x) => Libm.tanf(x);
        public static float Asin(float x) => Libm.asinf(x);
        public static float Acos(float x) => Libm.acosf(x);
        public static float Atan(float x) => Libm.atanf(x);
        public static float Atan2(float y, float x) => Libm.atan2f(y, x);
        public static float Sinh(float x) => Libm.sinhf(x);
        public static float Cosh(float x) => Libm.coshf(x);
        public static float Tanh(float x) => Libm.tanhf(x);
        public static float Exp(float x) => Libm.expf(x);
        public static float Log(float x) => Libm.logf(x);
        public static float Log10(float x) => Libm.log10f(x);
        public static float Log2(float x) => Libm.log2f(x);
        public static float Pow(float x, float y) => Libm.powf(x, y);
        public static float Cbrt(float x) => Libm.cbrtf(x);
        public static float IEEERemainder(float x, float y) => Libm.remainderf(x, y);
        public static float FusedMultiplyAdd(float x, float y, float z) => Libm.fmaf(x, y, z);
        public static float CopySign(float x, float y) => Libm.copysignf(x, y);
    }
}
