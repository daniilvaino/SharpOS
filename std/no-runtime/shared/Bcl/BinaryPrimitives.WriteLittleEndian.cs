// Ported from dotnet/runtime. Companion to ReadLittleEndian.cs;
// same cuts (Half/Single/Double/Int128 dropped — integer types only).

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System.Buffers.Binary
{
    public static partial class BinaryPrimitives
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteInt16LittleEndian(Span<byte> destination, short value)
        {
            if (!BitConverter.IsLittleEndian) value = ReverseEndianness(value);
            MemoryMarshal.Write(destination, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteInt32LittleEndian(Span<byte> destination, int value)
        {
            if (!BitConverter.IsLittleEndian) value = ReverseEndianness(value);
            MemoryMarshal.Write(destination, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteInt64LittleEndian(Span<byte> destination, long value)
        {
            if (!BitConverter.IsLittleEndian) value = ReverseEndianness(value);
            MemoryMarshal.Write(destination, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteIntPtrLittleEndian(Span<byte> destination, nint value)
        {
            if (!BitConverter.IsLittleEndian) value = ReverseEndianness(value);
            MemoryMarshal.Write(destination, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteUInt16LittleEndian(Span<byte> destination, ushort value)
        {
            if (!BitConverter.IsLittleEndian) value = ReverseEndianness(value);
            MemoryMarshal.Write(destination, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteUInt32LittleEndian(Span<byte> destination, uint value)
        {
            if (!BitConverter.IsLittleEndian) value = ReverseEndianness(value);
            MemoryMarshal.Write(destination, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteUInt64LittleEndian(Span<byte> destination, ulong value)
        {
            if (!BitConverter.IsLittleEndian) value = ReverseEndianness(value);
            MemoryMarshal.Write(destination, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteUIntPtrLittleEndian(Span<byte> destination, nuint value)
        {
            if (!BitConverter.IsLittleEndian) value = ReverseEndianness(value);
            MemoryMarshal.Write(destination, value);
        }

        // ---- TryWrite* ----

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryWriteInt16LittleEndian(Span<byte> destination, short value)
        {
            if (!BitConverter.IsLittleEndian) value = ReverseEndianness(value);
            return MemoryMarshal.TryWrite(destination, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryWriteInt32LittleEndian(Span<byte> destination, int value)
        {
            if (!BitConverter.IsLittleEndian) value = ReverseEndianness(value);
            return MemoryMarshal.TryWrite(destination, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryWriteInt64LittleEndian(Span<byte> destination, long value)
        {
            if (!BitConverter.IsLittleEndian) value = ReverseEndianness(value);
            return MemoryMarshal.TryWrite(destination, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryWriteIntPtrLittleEndian(Span<byte> destination, nint value)
        {
            if (!BitConverter.IsLittleEndian) value = ReverseEndianness(value);
            return MemoryMarshal.TryWrite(destination, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryWriteUInt16LittleEndian(Span<byte> destination, ushort value)
        {
            if (!BitConverter.IsLittleEndian) value = ReverseEndianness(value);
            return MemoryMarshal.TryWrite(destination, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryWriteUInt32LittleEndian(Span<byte> destination, uint value)
        {
            if (!BitConverter.IsLittleEndian) value = ReverseEndianness(value);
            return MemoryMarshal.TryWrite(destination, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryWriteUInt64LittleEndian(Span<byte> destination, ulong value)
        {
            if (!BitConverter.IsLittleEndian) value = ReverseEndianness(value);
            return MemoryMarshal.TryWrite(destination, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryWriteUIntPtrLittleEndian(Span<byte> destination, nuint value)
        {
            if (!BitConverter.IsLittleEndian) value = ReverseEndianness(value);
            return MemoryMarshal.TryWrite(destination, value);
        }
    }
}
