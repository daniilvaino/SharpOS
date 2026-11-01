// integer types, as BCL's IBinaryInteger implementations expose them
// (ulong.PopCount(x)); over System.Numerics.BitOperations (step224).

using System.Numerics;

namespace System
{
    public partial struct UInt64
    {
        public static ulong PopCount(ulong value) => (ulong)BitOperations.PopCount(value);
        public static ulong TrailingZeroCount(ulong value) => (ulong)BitOperations.TrailingZeroCount(value);
    }

    public partial struct UInt32
    {
        public static uint PopCount(uint value) => (uint)BitOperations.PopCount(value);
        public static uint TrailingZeroCount(uint value) => (uint)BitOperations.TrailingZeroCount(value);
    }
}
