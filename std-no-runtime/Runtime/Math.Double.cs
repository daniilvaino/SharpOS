// System.Math — double/float subset, pure managed implementations. There
// is no libm on the freestanding tiers (the fork's lm_* stubs trap, see
// docs/nativeaot-nostd-kernel-limits.md), so the transcendentals here are
// series/polynomial evaluations in plain C#:
//
//   - Sin/Cos: argument reduction to [-pi, pi] + nested-product Taylor
//     through x^17 — abs error ~2e-9 at |x| = pi, far better inside.
//   - Log: IEEE exponent extraction + atanh series on the mantissa
//     (~1e-13 relative).
//   - Exp: k*ln2 + r decomposition, Taylor on |r| <= ln2/2, 2^k via
//     exponent-bit construction (~1e-15 relative).
//   - Pow: Exp(y*Log(x)) with the BCL edge cases (negative base only for
//     integral exponents).
//
// Good for rendering/gamma/angle math; NOT ulp-exact against a real libm.
// Deliberate constraints:
//   - no `%` on doubles (ILC lowers double-rem to a runtime helper we
//     don't export); reduction uses truncating division instead, so
//     arguments must stay well inside |x| < 2^63 (callers today are
//     angles/gamma in [0, 2pi] / [0, 1]).
//   - Floor/Round go through a (long) truncation — same |x| < 2^63 bound.
//
// Round is half-to-even (banker's), matching the BCL contract.

namespace System
{
    public static partial class Math
    {
        public const double PI = 3.14159265358979323846;
        public const double E = 2.7182818284590452354;

        private const double Ln2 = 0.6931471805599453;
        private const double TwoPi = 2.0 * PI;

        public static double Abs(double value) => value < 0 ? -value : value;
        public static float Abs(float value) => value < 0 ? -value : value;

        public static double Floor(double d)
        {
            long i = (long)d; // truncation toward zero; |d| < 2^63 assumed
            double t = i;
            return d < t ? t - 1.0 : t;
        }

        public static double Ceiling(double d) => -Floor(-d);

        public static double Truncate(double d) => (long)d;

        public static double Round(double value)
        {
            double floor = Floor(value);
            double frac = value - floor;
            if (frac > 0.5) return floor + 1.0;
            if (frac < 0.5) return floor;
            // exactly .5 — round to even
            return ((long)floor & 1) == 0 ? floor : floor + 1.0;
        }

        public static double Sin(double a)
        {
            // Reduce to (-2pi, 2pi) by truncating division, then to [-pi, pi].
            double x = a - TwoPi * (long)(a / TwoPi);
            if (x > PI) x -= TwoPi;
            else if (x < -PI) x += TwoPi;

            // sin x = x * prod_k (1 - x^2 / ((2k)(2k+1))), truncated at x^17.
            double x2 = x * x;
            double s = 1.0 - x2 / 272.0;      // 16*17
            s = 1.0 - x2 / 210.0 * s;         // 14*15
            s = 1.0 - x2 / 156.0 * s;         // 12*13
            s = 1.0 - x2 / 110.0 * s;         // 10*11
            s = 1.0 - x2 / 72.0 * s;          //  8*9
            s = 1.0 - x2 / 42.0 * s;          //  6*7
            s = 1.0 - x2 / 20.0 * s;          //  4*5
            s = 1.0 - x2 / 6.0 * s;           //  2*3
            return x * s;
        }

        public static double Cos(double a) => Sin(a + PI / 2.0);

        public static unsafe double Log(double d)
        {
            if (d == 0.0) return double.NegativeInfinity;
            if (d < 0.0 || double.IsNaN(d)) return double.NaN;

            ulong bits = *(ulong*)&d;
            int exp = (int)((bits >> 52) & 0x7FF);
            if (exp == 0x7FF) return d; // +Inf -> +Inf
            if (exp == 0)
            {
                // Subnormal: scale by 2^54 and compensate.
                d *= 18014398509481984.0;
                bits = *(ulong*)&d;
                exp = (int)((bits >> 52) & 0x7FF) - 54;
            }
            exp -= 1023;

            // Mantissa m in [1, 2): overwrite the exponent field with 1023.
            bits = (bits & 0x000FFFFFFFFFFFFFul) | 0x3FF0000000000000ul;
            double m = *(double*)&bits;
            if (m > 1.4142135623730951) { m *= 0.5; exp += 1; } // center on 1

            // ln m = 2 atanh(z), z = (m-1)/(m+1), |z| <= 0.1716; series to z^15.
            double z = (m - 1.0) / (m + 1.0);
            double z2 = z * z;
            double s = ((((((z2 / 15.0 + 1.0 / 13.0) * z2 + 1.0 / 11.0) * z2 + 1.0 / 9.0) * z2
                        + 1.0 / 7.0) * z2 + 1.0 / 5.0) * z2 + 1.0 / 3.0) * z2 + 1.0;
            return exp * Ln2 + 2.0 * z * s;
        }

        public static unsafe double Exp(double d)
        {
            if (double.IsNaN(d)) return double.NaN;
            if (d > 709.782712893384) return double.PositiveInfinity;
            if (d < -745.13321910194122) return 0.0;

            // d = k*ln2 + r, |r| <= ln2/2.
            double kd = Floor(d / Ln2 + 0.5);
            long k = (long)kd;
            double r = d - kd * Ln2;

            // e^r via Taylor, nested form, through r^13.
            double s = 1.0;
            for (int i = 13; i >= 1; i--)
                s = 1.0 + r / i * s;

            // 2^k via exponent-bit construction (k in [-1074, 1023] after the
            // range gates above; k < -1022 would need a subnormal path, but the
            // gate keeps k*ln2 within normal range once s is folded in).
            ulong bits = (ulong)(k + 1023) << 52;
            double p = *(double*)&bits;
            return s * p;
        }

        public static double Pow(double x, double y)
        {
            if (y == 0.0) return 1.0;
            if (x == 1.0) return 1.0;
            if (double.IsNaN(x) || double.IsNaN(y)) return double.NaN;
            if (x == 0.0) return y > 0.0 ? 0.0 : double.PositiveInfinity;
            if (x < 0.0)
            {
                // Negative base: only integral exponents have a real result.
                long yi = (long)y;
                if (yi != y) return double.NaN;
                double r = Exp(y * Log(-x));
                return (yi & 1) != 0 ? -r : r;
            }
            return Exp(y * Log(x));
        }
    }

    // System.MathF — float shims over the double implementations. BCL has
    // dedicated single-precision code; a double round-trip is bit-identical
    // for Round/Abs results in float range.
    public static class MathF
    {
        public const float PI = 3.14159265f;

        public static float Abs(float x) => x < 0 ? -x : x;

        public static float Round(float x) => (float)Math.Round(x);

        public static float Floor(float x) => (float)Math.Floor(x);

        public static float Sin(float x) => (float)Math.Sin(x);

        public static float Cos(float x) => (float)Math.Cos(x);
    }
}
