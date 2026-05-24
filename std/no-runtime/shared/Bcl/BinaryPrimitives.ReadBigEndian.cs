// Ported from dotnet/runtime — companion to ReadLittleEndian.cs.
// Same cuts apply (Half/Single/Double/Int128 dropped).
//
// On our LE-x86_64 target, !IsLittleEndian is false (compile-time
// const), so the ReverseEndianness branch is the live one. We keep
// the stock IsLittleEndian dispatch shape so the file is line-for-line
// portable when a BE target is added.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System.Buffers.Binary
{
    public static partial class BinaryPrimitives
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static short ReadInt16BigEndian(ReadOnlySpan<byte> source)
        {
            return BitConverter.IsLittleEndian ?
                ReverseEndianness(MemoryMarshal.Read<short>(source)) :
                MemoryMarshal.Read<short>(source);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReadInt32BigEndian(ReadOnlySpan<byte> source)
        {
            return BitConverter.IsLittleEndian ?
                ReverseEndianness(MemoryMarshal.Read<int>(source)) :
                MemoryMarshal.Read<int>(source);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long ReadInt64BigEndian(ReadOnlySpan<byte> source)
        {
            return BitConverter.IsLittleEndian ?
                ReverseEndianness(MemoryMarshal.Read<long>(source)) :
                MemoryMarshal.Read<long>(source);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nint ReadIntPtrBigEndian(ReadOnlySpan<byte> source)
        {
            return BitConverter.IsLittleEndian ?
                ReverseEndianness(MemoryMarshal.Read<nint>(source)) :
                MemoryMarshal.Read<nint>(source);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort ReadUInt16BigEndian(ReadOnlySpan<byte> source)
        {
            return BitConverter.IsLittleEndian ?
                ReverseEndianness(MemoryMarshal.Read<ushort>(source)) :
                MemoryMarshal.Read<ushort>(source);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint ReadUInt32BigEndian(ReadOnlySpan<byte> source)
        {
            return BitConverter.IsLittleEndian ?
                ReverseEndianness(MemoryMarshal.Read<uint>(source)) :
                MemoryMarshal.Read<uint>(source);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong ReadUInt64BigEndian(ReadOnlySpan<byte> source)
        {
            return BitConverter.IsLittleEndian ?
                ReverseEndianness(MemoryMarshal.Read<ulong>(source)) :
                MemoryMarshal.Read<ulong>(source);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nuint ReadUIntPtrBigEndian(ReadOnlySpan<byte> source)
        {
            return BitConverter.IsLittleEndian ?
                ReverseEndianness(MemoryMarshal.Read<nuint>(source)) :
                MemoryMarshal.Read<nuint>(source);
        }

        // ---- TryRead* ----

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryReadInt16BigEndian(ReadOnlySpan<byte> source, out short value)
        {
            if (!BitConverter.IsLittleEndian)
                return MemoryMarshal.TryRead(source, out value);

            bool ok = MemoryMarshal.TryRead(source, out short tmp);
            value = ReverseEndianness(tmp);
            return ok;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryReadInt32BigEndian(ReadOnlySpan<byte> source, out int value)
        {
            if (!BitConverter.IsLittleEndian)
                return MemoryMarshal.TryRead(source, out value);

            bool ok = MemoryMarshal.TryRead(source, out int tmp);
            value = ReverseEndianness(tmp);
            return ok;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryReadInt64BigEndian(ReadOnlySpan<byte> source, out long value)
        {
            if (!BitConverter.IsLittleEndian)
                return MemoryMarshal.TryRead(source, out value);

            bool ok = MemoryMarshal.TryRead(source, out long tmp);
            value = ReverseEndianness(tmp);
            return ok;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryReadIntPtrBigEndian(ReadOnlySpan<byte> source, out nint value)
        {
            if (!BitConverter.IsLittleEndian)
                return MemoryMarshal.TryRead(source, out value);

            bool ok = MemoryMarshal.TryRead(source, out nint tmp);
            value = ReverseEndianness(tmp);
            return ok;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryReadUInt16BigEndian(ReadOnlySpan<byte> source, out ushort value)
        {
            if (!BitConverter.IsLittleEndian)
                return MemoryMarshal.TryRead(source, out value);

            bool ok = MemoryMarshal.TryRead(source, out ushort tmp);
            value = ReverseEndianness(tmp);
            return ok;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryReadUInt32BigEndian(ReadOnlySpan<byte> source, out uint value)
        {
            if (!BitConverter.IsLittleEndian)
                return MemoryMarshal.TryRead(source, out value);

            bool ok = MemoryMarshal.TryRead(source, out uint tmp);
            value = ReverseEndianness(tmp);
            return ok;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryReadUInt64BigEndian(ReadOnlySpan<byte> source, out ulong value)
        {
            if (!BitConverter.IsLittleEndian)
                return MemoryMarshal.TryRead(source, out value);

            bool ok = MemoryMarshal.TryRead(source, out ulong tmp);
            value = ReverseEndianness(tmp);
            return ok;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryReadUIntPtrBigEndian(ReadOnlySpan<byte> source, out nuint value)
        {
            if (!BitConverter.IsLittleEndian)
                return MemoryMarshal.TryRead(source, out value);

            bool ok = MemoryMarshal.TryRead(source, out nuint tmp);
            value = ReverseEndianness(tmp);
            return ok;
        }
    }
}
