// System.Math - the decimal overloads. Ported from dotnet/runtime release/8.0
// (v8.0.27, MIT): src/libraries/System.Private.CoreLib/src/System/Math.cs, the
// decimal members and ThrowMinMaxException, bodies verbatim. Each forwards to
// System.Decimal (Number/Decimal.cs). Partial half of Math.cs, like Math.Double.cs.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace System
{
    public static partial class Math
    {
        public static decimal Abs(decimal value)
        {
            return decimal.Abs(value);
        }

        public static decimal Ceiling(decimal d)
        {
            return decimal.Ceiling(d);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static decimal Clamp(decimal value, decimal min, decimal max)
        {
            if (min > max)
            {
                ThrowMinMaxException(min, max);
            }

            if (value < min)
            {
                return min;
            }
            else if (value > max)
            {
                return max;
            }

            return value;
        }

        public static decimal Floor(decimal d)
        {
            return decimal.Floor(d);
        }

        public static decimal Max(decimal val1, decimal val2)
        {
            return decimal.Max(val1, val2);
        }

        public static decimal Min(decimal val1, decimal val2)
        {
            return decimal.Min(val1, val2);
        }

        public static decimal Round(decimal d)
        {
            return decimal.Round(d, 0);
        }

        public static decimal Round(decimal d, int decimals)
        {
            return decimal.Round(d, decimals);
        }

        public static decimal Round(decimal d, MidpointRounding mode)
        {
            return decimal.Round(d, 0, mode);
        }

        public static decimal Round(decimal d, int decimals, MidpointRounding mode)
        {
            return decimal.Round(d, decimals, mode);
        }

        public static int Sign(decimal value)
        {
            return decimal.Sign(value);
        }

        public static decimal Truncate(decimal d)
        {
            return decimal.Truncate(d);
        }

        [DoesNotReturn]
        internal static void ThrowMinMaxException<T>(T min, T max)
        {
            throw new ArgumentException(SR.Format(SR.Argument_MinMaxValue, min, max));
        }
    }
}
