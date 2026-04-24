// System.Math — subset: Min/Max/Abs/Clamp for int/long/uint/ulong. Used
// by BCL StringBuilder, collections, path helpers and similar. Full
// BCL Math has 150+ methods for double/decimal/transcendental; we add
// them lazily as callers appear.

namespace System
{
    public static class Math
    {
        public static int Min(int a, int b) => a < b ? a : b;
        public static int Max(int a, int b) => a > b ? a : b;
        public static long Min(long a, long b) => a < b ? a : b;
        public static long Max(long a, long b) => a > b ? a : b;
        public static uint Min(uint a, uint b) => a < b ? a : b;
        public static uint Max(uint a, uint b) => a > b ? a : b;
        public static ulong Min(ulong a, ulong b) => a < b ? a : b;
        public static ulong Max(ulong a, ulong b) => a > b ? a : b;

        public static int Abs(int value) => value < 0 ? -value : value;
        public static long Abs(long value) => value < 0 ? -value : value;

        public static int Clamp(int value, int min, int max)
        {
            if (min > max) return value;
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
