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

        // Переливание битов между целым и плавающим, без арифметики.
        //
        // Нужно всему, что читает числа из потока: сначала приходит целое в
        // нужном порядке байтов, и только потом оно объявляется float. Через
        // указатель, а не через Unsafe: так короче и не зависит от того, какая
        // часть System.Runtime.CompilerServices у нас есть.
        public static unsafe float Int32BitsToSingle(int value) => *(float*)&value;
        public static unsafe int SingleToInt32Bits(float value) => *(int*)&value;
        public static unsafe double Int64BitsToDouble(long value) => *(double*)&value;
        public static unsafe long DoubleToInt64Bits(double value) => *(long*)&value;

        // Unsigned twins (BCL .NET 6+); the number formatter reads the IEEE
        // fields through them.
        [CLSCompliant(false)]
        public static unsafe float UInt32BitsToSingle(uint value) => *(float*)&value;
        [CLSCompliant(false)]
        public static unsafe uint SingleToUInt32Bits(float value) => *(uint*)&value;
        [CLSCompliant(false)]
        public static unsafe double UInt64BitsToDouble(ulong value) => *(double*)&value;
        [CLSCompliant(false)]
        public static unsafe ulong DoubleToUInt64Bits(double value) => *(ulong*)&value;

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
