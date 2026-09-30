// Ported from dotnet/runtime:
//   src/libraries/System.Private.CoreLib/src/System/Buffers/Binary/
//     BinaryPrimitives.ReadLittleEndian.cs
//
// Subset rationale:
//   - Integer scalars (Int16/UInt16/Int32/UInt32/Int64/UInt64) + their
//     IntPtr/UIntPtr variants are fully ported, both ReadXxx and
//     TryReadXxx.
//   - Half / Single / Double overloads are dropped — kernel-tier has no
//     Half type and no Int*BitsToFloat* helpers. The stubs in stock
//     BCL go through BitConverter.Int64BitsToDouble etc., which depend
//     on JIT intrinsics we don't surface. Add when first float consumer
//     lands and the BitConverter cast helpers come with it.
//   - Int128 / UInt128 dropped — Int128 type not in our std.
//
// BitConverter.IsLittleEndian is a compile-time `true` const in our
// std (kernel target is x86_64). The non-LE branches dead-code out;
// we keep them in source verbatim so the file matches stock BCL line-
// for-line for the ported overloads.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System.Buffers.Binary
{
    public static partial class BinaryPrimitives
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static short ReadInt16LittleEndian(ReadOnlySpan<byte> source)
        {
            return !BitConverter.IsLittleEndian ?
                ReverseEndianness(MemoryMarshal.Read<short>(source)) :
                MemoryMarshal.Read<short>(source);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReadInt32LittleEndian(ReadOnlySpan<byte> source)
        {
            return !BitConverter.IsLittleEndian ?
                ReverseEndianness(MemoryMarshal.Read<int>(source)) :
                MemoryMarshal.Read<int>(source);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long ReadInt64LittleEndian(ReadOnlySpan<byte> source)
        {
            return !BitConverter.IsLittleEndian ?
                ReverseEndianness(MemoryMarshal.Read<long>(source)) :
                MemoryMarshal.Read<long>(source);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nint ReadIntPtrLittleEndian(ReadOnlySpan<byte> source)
        {
            return !BitConverter.IsLittleEndian ?
                ReverseEndianness(MemoryMarshal.Read<nint>(source)) :
                MemoryMarshal.Read<nint>(source);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort ReadUInt16LittleEndian(ReadOnlySpan<byte> source)
        {
            return !BitConverter.IsLittleEndian ?
                ReverseEndianness(MemoryMarshal.Read<ushort>(source)) :
                MemoryMarshal.Read<ushort>(source);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint ReadUInt32LittleEndian(ReadOnlySpan<byte> source)
        {
            return !BitConverter.IsLittleEndian ?
                ReverseEndianness(MemoryMarshal.Read<uint>(source)) :
                MemoryMarshal.Read<uint>(source);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong ReadUInt64LittleEndian(ReadOnlySpan<byte> source)
        {
            return !BitConverter.IsLittleEndian ?
                ReverseEndianness(MemoryMarshal.Read<ulong>(source)) :
                MemoryMarshal.Read<ulong>(source);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nuint ReadUIntPtrLittleEndian(ReadOnlySpan<byte> source)
        {
            return !BitConverter.IsLittleEndian ?
                ReverseEndianness(MemoryMarshal.Read<nuint>(source)) :
                MemoryMarshal.Read<nuint>(source);
        }

        // ---- TryRead* (return false on short source) ----

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryReadInt16LittleEndian(ReadOnlySpan<byte> source, out short value)
        {
            if (BitConverter.IsLittleEndian)
                return MemoryMarshal.TryRead(source, out value);

            bool ok = MemoryMarshal.TryRead(source, out short tmp);
            value = ReverseEndianness(tmp);
            return ok;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryReadInt32LittleEndian(ReadOnlySpan<byte> source, out int value)
        {
            if (BitConverter.IsLittleEndian)
                return MemoryMarshal.TryRead(source, out value);

            bool ok = MemoryMarshal.TryRead(source, out int tmp);
            value = ReverseEndianness(tmp);
            return ok;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryReadInt64LittleEndian(ReadOnlySpan<byte> source, out long value)
        {
            if (BitConverter.IsLittleEndian)
                return MemoryMarshal.TryRead(source, out value);

            bool ok = MemoryMarshal.TryRead(source, out long tmp);
            value = ReverseEndianness(tmp);
            return ok;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryReadIntPtrLittleEndian(ReadOnlySpan<byte> source, out nint value)
        {
            if (BitConverter.IsLittleEndian)
                return MemoryMarshal.TryRead(source, out value);

            bool ok = MemoryMarshal.TryRead(source, out nint tmp);
            value = ReverseEndianness(tmp);
            return ok;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryReadUInt16LittleEndian(ReadOnlySpan<byte> source, out ushort value)
        {
            if (BitConverter.IsLittleEndian)
                return MemoryMarshal.TryRead(source, out value);

            bool ok = MemoryMarshal.TryRead(source, out ushort tmp);
            value = ReverseEndianness(tmp);
            return ok;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryReadUInt32LittleEndian(ReadOnlySpan<byte> source, out uint value)
        {
            if (BitConverter.IsLittleEndian)
                return MemoryMarshal.TryRead(source, out value);

            bool ok = MemoryMarshal.TryRead(source, out uint tmp);
            value = ReverseEndianness(tmp);
            return ok;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryReadUInt64LittleEndian(ReadOnlySpan<byte> source, out ulong value)
        {
            if (BitConverter.IsLittleEndian)
                return MemoryMarshal.TryRead(source, out value);

            bool ok = MemoryMarshal.TryRead(source, out ulong tmp);
            value = ReverseEndianness(tmp);
            return ok;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryReadUIntPtrLittleEndian(ReadOnlySpan<byte> source, out nuint value)
        {
            if (BitConverter.IsLittleEndian)
                return MemoryMarshal.TryRead(source, out value);

            bool ok = MemoryMarshal.TryRead(source, out nuint tmp);
            value = ReverseEndianness(tmp);
            return ok;
        }
    }
}
