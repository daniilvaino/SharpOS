// System.Math — integer/short subset. Min/Max/Abs/Clamp/Sign for the
// canonical integer widths. Used by BCL StringBuilder, collections, path
// helpers and similar.
//
// The double/float subset (Floor/Round/Sin/Cos/Pow/...) lives in
// Math.Double.cs (partial half, step141) — pure managed series, no libm.

namespace System
{
    public static partial class Math
    {
        // ---- Min ----
        public static byte   Min(byte a, byte b)     => a < b ? a : b;
        public static sbyte  Min(sbyte a, sbyte b)   => a < b ? a : b;
        public static short  Min(short a, short b)   => a < b ? a : b;
        public static ushort Min(ushort a, ushort b) => a < b ? a : b;
        public static int    Min(int a, int b)       => a < b ? a : b;
        public static uint   Min(uint a, uint b)     => a < b ? a : b;
        public static long   Min(long a, long b)     => a < b ? a : b;
        public static ulong  Min(ulong a, ulong b)   => a < b ? a : b;

        // Floating point, added for Terminal.Gui's RectangleF: with only the
        // integer overloads present, Math.Max(float, float) bound to the byte
        // one and failed as "cannot convert float to byte", which reads like a
        // type error in the CALLER rather than a missing overload here.
        //
        // NaN follows the BCL: it wins, because a comparison against NaN is
        // false either way and picking the other operand would quietly turn a
        // bad number into a plausible one. Spotted by self-comparison — the one
        // value that is not equal to itself — since float.IsNaN does not exist
        // in this environment.
        public static float Min(float a, float b)
            => a != a ? a : b != b ? b : (a < b ? a : b);

        public static double Min(double a, double b)
            => a != a ? a : b != b ? b : (a < b ? a : b);

        // ---- Max ----
        public static float  Max(float a, float b)
            => a != a ? a : b != b ? b : (a > b ? a : b);

        public static double Max(double a, double b)
            => a != a ? a : b != b ? b : (a > b ? a : b);

        public static byte   Max(byte a, byte b)     => a > b ? a : b;
        public static sbyte  Max(sbyte a, sbyte b)   => a > b ? a : b;
        public static short  Max(short a, short b)   => a > b ? a : b;
        public static ushort Max(ushort a, ushort b) => a > b ? a : b;
        public static int    Max(int a, int b)       => a > b ? a : b;
        public static uint   Max(uint a, uint b)     => a > b ? a : b;
        public static long   Max(long a, long b)     => a > b ? a : b;
        public static ulong  Max(ulong a, ulong b)   => a > b ? a : b;

        // ---- Abs ----
        // For signed types, Abs(MinValue) is undefined (overflow). BCL throws
        // OverflowException; we return MinValue silently — same effect as
        // unchecked { -value } for two's-complement. Callers shouldn't pass
        // MinValue.
        public static sbyte Abs(sbyte value) => value < 0 ? (sbyte)-value : value;
        public static short Abs(short value) => value < 0 ? (short)-value : value;
        public static int   Abs(int value)   => value < 0 ? -value : value;
        public static long  Abs(long value)  => value < 0 ? -value : value;

        // ---- Sign ----
        public static int Sign(sbyte value) => value < 0 ? -1 : (value > 0 ? 1 : 0);
        public static int Sign(short value) => value < 0 ? -1 : (value > 0 ? 1 : 0);
        public static int Sign(int value)   => value < 0 ? -1 : (value > 0 ? 1 : 0);
        public static int Sign(long value)  => value < 0 ? -1 : (value > 0 ? 1 : 0);

        // ---- Clamp ----
        public static byte   Clamp(byte value, byte min, byte max)     => value < min ? min : (value > max ? max : value);
        public static sbyte  Clamp(sbyte value, sbyte min, sbyte max)  => value < min ? min : (value > max ? max : value);
        public static short  Clamp(short value, short min, short max)  => value < min ? min : (value > max ? max : value);
        public static ushort Clamp(ushort value, ushort min, ushort max) => value < min ? min : (value > max ? max : value);
        public static int    Clamp(int value, int min, int max)        => value < min ? min : (value > max ? max : value);
        public static uint   Clamp(uint value, uint min, uint max)     => value < min ? min : (value > max ? max : value);
        public static long   Clamp(long value, long min, long max)     => value < min ? min : (value > max ? max : value);
        public static ulong  Clamp(ulong value, ulong min, ulong max)  => value < min ? min : (value > max ? max : value);

        // ---- DivRem ----
        public static int DivRem(int a, int b, out int result)
        {
            int div = a / b;
            result = a - div * b;
            return div;
        }

        public static long DivRem(long a, long b, out long result)
        {
            long div = a / b;
            result = a - div * b;
            return div;
        }
    }
}
