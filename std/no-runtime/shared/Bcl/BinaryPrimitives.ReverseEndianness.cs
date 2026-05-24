// Ported from dotnet/runtime:
//   src/libraries/System.Private.CoreLib/src/System/Buffers/Binary/
//     BinaryPrimitives.ReverseEndianness.cs
//
// Cuts (all local, public contract preserved for ported overloads):
//  - Int128 / UInt128 overloads — Int128 type not in our std yet.
//  - Vector128 / Vector256 bulk span variants
//    (ReverseEndianness(ReadOnlySpan<T>, Span<T>)) — SIMD intrinsics
//    not available in kernel; bulk callers can fall back to per-element
//    loop if needed. Add when first consumer lands.
//  - [Intrinsic] attribute — JIT doesn't recognize it here; we lose the
//    `bswap`/`movbe` fast-path and fall back to shift+or, which is fine
//    (ILC inlines AggressiveInlining; bswap pattern is also recognized
//    by code generators).
//  - BitOperations.RotateLeft / RotateRight in ReverseEndianness(uint) —
//    replaced with the equivalent shift+or sequence (kernel doesn't
//    have BitOperations yet; revisit when ported).

using System.Runtime.CompilerServices;

namespace System.Buffers.Binary
{
    public static partial class BinaryPrimitives
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static sbyte ReverseEndianness(sbyte value) => value;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte ReverseEndianness(byte value) => value;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static short ReverseEndianness(short value) => (short)ReverseEndianness((ushort)value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort ReverseEndianness(ushort value)
        {
            return (ushort)((value >> 8) + (value << 8));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static char ReverseEndianness(char value) => (char)ReverseEndianness((ushort)value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReverseEndianness(int value) => (int)ReverseEndianness((uint)value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint ReverseEndianness(uint value)
        {
            // Stock BCL uses ROR/ROL via BitOperations to give JIT the
            // bswap pattern. We use the equivalent shift+or sequence —
            // produces identical net result; modern code generators
            // still recognize this as bswap.
            return ((value & 0xFF000000u) >> 24)
                 | ((value & 0x00FF0000u) >> 8)
                 | ((value & 0x0000FF00u) << 8)
                 | ((value & 0x000000FFu) << 24);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long ReverseEndianness(long value) => (long)ReverseEndianness((ulong)value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong ReverseEndianness(ulong value)
        {
            // 32-bit ops have higher throughput than 64-bit on most x86
            // implementations; decompose, reverse each half, swap them.
            return ((ulong)ReverseEndianness((uint)value) << 32)
                 + ReverseEndianness((uint)(value >> 32));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nint ReverseEndianness(nint value) => (nint)ReverseEndianness((long)value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nuint ReverseEndianness(nuint value) => (nuint)ReverseEndianness((ulong)value);
    }
}
