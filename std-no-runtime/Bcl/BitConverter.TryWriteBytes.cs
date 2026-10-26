// Ported from dotnet/runtime release/7.0 (MIT):
//   src/libraries/System.Private.CoreLib/src/System/BitConverter.cs
// (the CoreLib snapshot in gc-experiment/dotnet-runtime). The TryWriteBytes family,
// verbatim. Cut: the Half overload — no System.Half in std.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System
{
    public static partial class BitConverter
    {
        /// <summary>
        /// Converts a Boolean into a span of bytes.
        /// </summary>
        /// <param name="destination">When this method returns, the bytes representing the converted Boolean.</param>
        /// <param name="value">The Boolean to convert.</param>
        /// <returns><see langword="true"/> if the conversion was successful; <see langword="false"/> otherwise.</returns>
        public static bool TryWriteBytes(Span<byte> destination, bool value)
        {
            if (destination.Length < sizeof(byte))
                return false;

            Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(destination), value ? (byte)1 : (byte)0);
            return true;
        }

        /// <summary>
        /// Converts a character into a span of bytes.
        /// </summary>
        /// <param name="destination">When this method returns, the bytes representing the converted character.</param>
        /// <param name="value">The character to convert.</param>
        /// <returns><see langword="true"/> if the conversion was successful; <see langword="false"/> otherwise.</returns>
        public static bool TryWriteBytes(Span<byte> destination, char value)
        {
            if (destination.Length < sizeof(char))
                return false;

            Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(destination), value);
            return true;
        }

        /// <summary>
        /// Converts a 16-bit signed integer into a span of bytes.
        /// </summary>
        /// <param name="destination">When this method returns, the bytes representing the converted 16-bit signed integer.</param>
        /// <param name="value">The 16-bit signed integer to convert.</param>
        /// <returns><see langword="true"/> if the conversion was successful; <see langword="false"/> otherwise.</returns>
        public static bool TryWriteBytes(Span<byte> destination, short value)
        {
            if (destination.Length < sizeof(short))
                return false;

            Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(destination), value);
            return true;
        }

        /// <summary>
        /// Converts a 32-bit signed integer into a span of bytes.
        /// </summary>
        /// <param name="destination">When this method returns, the bytes representing the converted 32-bit signed integer.</param>
        /// <param name="value">The 32-bit signed integer to convert.</param>
        /// <returns><see langword="true"/> if the conversion was successful; <see langword="false"/> otherwise.</returns>
        public static bool TryWriteBytes(Span<byte> destination, int value)
        {
            if (destination.Length < sizeof(int))
                return false;

            Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(destination), value);
            return true;
        }

        /// <summary>
        /// Converts a 64-bit signed integer into a span of bytes.
        /// </summary>
        /// <param name="destination">When this method returns, the bytes representing the converted 64-bit signed integer.</param>
        /// <param name="value">The 64-bit signed integer to convert.</param>
        /// <returns><see langword="true"/> if the conversion was successful; <see langword="false"/> otherwise.</returns>
        public static bool TryWriteBytes(Span<byte> destination, long value)
        {
            if (destination.Length < sizeof(long))
                return false;

            Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(destination), value);
            return true;
        }

        /// <summary>
        /// Converts a 16-bit unsigned integer into a span of bytes.
        /// </summary>
        /// <param name="destination">When this method returns, the bytes representing the converted 16-bit unsigned integer.</param>
        /// <param name="value">The 16-bit unsigned integer to convert.</param>
        /// <returns><see langword="true"/> if the conversion was successful; <see langword="false"/> otherwise.</returns>
        [CLSCompliant(false)]
        public static bool TryWriteBytes(Span<byte> destination, ushort value)
        {
            if (destination.Length < sizeof(ushort))
                return false;

            Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(destination), value);
            return true;
        }

        /// <summary>
        /// Converts a 32-bit unsigned integer into a span of bytes.
        /// </summary>
        /// <param name="destination">When this method returns, the bytes representing the converted 32-bit unsigned integer.</param>
        /// <param name="value">The 32-bit unsigned integer to convert.</param>
        /// <returns><see langword="true"/> if the conversion was successful; <see langword="false"/> otherwise.</returns>
        [CLSCompliant(false)]
        public static bool TryWriteBytes(Span<byte> destination, uint value)
        {
            if (destination.Length < sizeof(uint))
                return false;

            Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(destination), value);
            return true;
        }

        /// <summary>
        /// Converts a 64-bit unsigned integer into a span of bytes.
        /// </summary>
        /// <param name="destination">When this method returns, the bytes representing the converted 64-bit unsigned integer.</param>
        /// <param name="value">The 64-bit unsigned integer to convert.</param>
        /// <returns><see langword="true"/> if the conversion was successful; <see langword="false"/> otherwise.</returns>
        [CLSCompliant(false)]
        public static bool TryWriteBytes(Span<byte> destination, ulong value)
        {
            if (destination.Length < sizeof(ulong))
                return false;

            Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(destination), value);
            return true;
        }

        /// <summary>
        /// Converts a single-precision floating-point value into a span of bytes.
        /// </summary>
        /// <param name="destination">When this method returns, the bytes representing the converted single-precision floating-point value.</param>
        /// <param name="value">The single-precision floating-point value to convert.</param>
        /// <returns><see langword="true"/> if the conversion was successful; <see langword="false"/> otherwise.</returns>
        public static bool TryWriteBytes(Span<byte> destination, float value)
        {
            if (destination.Length < sizeof(float))
                return false;

            Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(destination), value);
            return true;
        }

        /// <summary>
        /// Converts a double-precision floating-point value into a span of bytes.
        /// </summary>
        /// <param name="destination">When this method returns, the bytes representing the converted double-precision floating-point value.</param>
        /// <param name="value">The double-precision floating-point value to convert.</param>
        /// <returns><see langword="true"/> if the conversion was successful; <see langword="false"/> otherwise.</returns>
        public static bool TryWriteBytes(Span<byte> destination, double value)
        {
            if (destination.Length < sizeof(double))
                return false;

            Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(destination), value);
            return true;
        }
    }
}
