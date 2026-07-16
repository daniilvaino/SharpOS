// System.BitConverter — minimal subset ported from dotnet/runtime.
// BCL version is large (float/double bit-casts, ToString hex-pretty,
// GetBytes overloads). Kernel-tier currently needs only IsLittleEndian
// (BinaryPrimitives uses it as a compile-time const branch).
//
// Cuts:
//   - Int{16,32,64}BitsToHalf/Single/Double — need Half/Single/Double
//     bit-cast intrinsics we don't have; revisit when float runtime lands.
//   - GetBytes / ToBoolean / ToDouble — no consumer in our std yet.
//     ToInt16/32/64 + unsigned variants landed step141 (ManagedDoom WAD
//     parsing); manual little-endian composition, no Unsafe.ReadUnaligned.
//   - ToString(...) — Halt's text formatting path, no consumer yet.
//
// Endianness is hard-coded little-endian: kernel target is x86_64.
// Adding ARM/big-endian targets later flips this to a runtime check.

namespace System
{
    public static class BitConverter
    {
        public const bool IsLittleEndian = true;

        // byte[]-reader subset (step141: ManagedDoom WAD/lump parsing).
        // Little-endian composition, bounds via the array indexer.
        public static short ToInt16(byte[] value, int startIndex)
        {
            return (short)(value[startIndex] | (value[startIndex + 1] << 8));
        }

        public static ushort ToUInt16(byte[] value, int startIndex)
        {
            return (ushort)(value[startIndex] | (value[startIndex + 1] << 8));
        }

        public static int ToInt32(byte[] value, int startIndex)
        {
            return value[startIndex]
                | (value[startIndex + 1] << 8)
                | (value[startIndex + 2] << 16)
                | (value[startIndex + 3] << 24);
        }

        public static uint ToUInt32(byte[] value, int startIndex)
        {
            return (uint)ToInt32(value, startIndex);
        }

        public static long ToInt64(byte[] value, int startIndex)
        {
            uint lo = ToUInt32(value, startIndex);
            uint hi = ToUInt32(value, startIndex + 4);
            return (long)(((ulong)hi << 32) | lo);
        }

        public static ulong ToUInt64(byte[] value, int startIndex)
        {
            return (ulong)ToInt64(value, startIndex);
        }
    }
}
