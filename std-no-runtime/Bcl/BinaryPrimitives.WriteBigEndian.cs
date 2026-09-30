// Ported from dotnet/runtime. Companion to WriteLittleEndian.cs.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System.Buffers.Binary
{
    public static partial class BinaryPrimitives
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteInt16BigEndian(Span<byte> destination, short value)
        {
            if (BitConverter.IsLittleEndian) value = ReverseEndianness(value);
            MemoryMarshal.Write(destination, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteInt32BigEndian(Span<byte> destination, int value)
        {
            if (BitConverter.IsLittleEndian) value = ReverseEndianness(value);
            MemoryMarshal.Write(destination, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteInt64BigEndian(Span<byte> destination, long value)
        {
            if (BitConverter.IsLittleEndian) value = ReverseEndianness(value);
            MemoryMarshal.Write(destination, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteIntPtrBigEndian(Span<byte> destination, nint value)
        {
            if (BitConverter.IsLittleEndian) value = ReverseEndianness(value);
            MemoryMarshal.Write(destination, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteUInt16BigEndian(Span<byte> destination, ushort value)
        {
            if (BitConverter.IsLittleEndian) value = ReverseEndianness(value);
            MemoryMarshal.Write(destination, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteUInt32BigEndian(Span<byte> destination, uint value)
        {
            if (BitConverter.IsLittleEndian) value = ReverseEndianness(value);
            MemoryMarshal.Write(destination, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteUInt64BigEndian(Span<byte> destination, ulong value)
        {
            if (BitConverter.IsLittleEndian) value = ReverseEndianness(value);
            MemoryMarshal.Write(destination, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteUIntPtrBigEndian(Span<byte> destination, nuint value)
        {
            if (BitConverter.IsLittleEndian) value = ReverseEndianness(value);
            MemoryMarshal.Write(destination, value);
        }

        // ---- TryWrite* ----

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryWriteInt16BigEndian(Span<byte> destination, short value)
        {
            if (BitConverter.IsLittleEndian) value = ReverseEndianness(value);
            return MemoryMarshal.TryWrite(destination, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryWriteInt32BigEndian(Span<byte> destination, int value)
        {
            if (BitConverter.IsLittleEndian) value = ReverseEndianness(value);
            return MemoryMarshal.TryWrite(destination, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryWriteInt64BigEndian(Span<byte> destination, long value)
        {
            if (BitConverter.IsLittleEndian) value = ReverseEndianness(value);
            return MemoryMarshal.TryWrite(destination, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryWriteIntPtrBigEndian(Span<byte> destination, nint value)
        {
            if (BitConverter.IsLittleEndian) value = ReverseEndianness(value);
            return MemoryMarshal.TryWrite(destination, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryWriteUInt16BigEndian(Span<byte> destination, ushort value)
        {
            if (BitConverter.IsLittleEndian) value = ReverseEndianness(value);
            return MemoryMarshal.TryWrite(destination, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryWriteUInt32BigEndian(Span<byte> destination, uint value)
        {
            if (BitConverter.IsLittleEndian) value = ReverseEndianness(value);
            return MemoryMarshal.TryWrite(destination, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryWriteUInt64BigEndian(Span<byte> destination, ulong value)
        {
            if (BitConverter.IsLittleEndian) value = ReverseEndianness(value);
            return MemoryMarshal.TryWrite(destination, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryWriteUIntPtrBigEndian(Span<byte> destination, nuint value)
        {
            if (BitConverter.IsLittleEndian) value = ReverseEndianness(value);
            return MemoryMarshal.TryWrite(destination, value);
        }
    }
}
