// Ported from dotnet/runtime release/8.0 (MIT), the CoreLib snapshot in gc-experiment/dotnet-runtime-8.0:
//   src/libraries/System.Private.CoreLib/src/System/Buffers/Text/Base64Encoder.cs
//
// SharpOS cuts (marked inline): the Avx2Encode / Vector128Encode fast paths and their
// System.Runtime.Intrinsics.X86 / .Arm dispatch. std has Vector128 but no Avx2 / Ssse3 /
// AdvSimd hardware-intrinsic classes; the scalar loop is the BCL's own tail path and gives
// identical output. Base64.cs (only the DEBUG read/write asserts of those vector paths) and
// Base64Validator.cs (Base64.IsValid, needs SearchValues) are not ported.

using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
// SharpOS cut: using System.Runtime.Intrinsics(.Arm/.X86) - vector paths not ported.

namespace System.Buffers.Text
{
    // AVX2 version based on https://github.com/aklomp/base64/tree/e516d769a2a432c08404f1981e73b431566057be/lib/arch/avx2
    // Vector128 version based on https://github.com/aklomp/base64/tree/e516d769a2a432c08404f1981e73b431566057be/lib/arch/ssse3

    /// <summary>
    /// Convert between binary data and UTF-8 encoded text that is represented in base 64.
    /// </summary>
    public static partial class Base64
    {
        /// <summary>
        /// Encode the span of binary data into UTF-8 encoded text represented as base64.
        /// </summary>
        /// <param name="bytes">The input span which contains binary data that needs to be encoded.</param>
        /// <param name="utf8">The output span which contains the result of the operation, i.e. the UTF-8 encoded text in base64.</param>
        /// <param name="bytesConsumed">The number of input bytes consumed during the operation. This can be used to slice the input for subsequent calls, if necessary.</param>
        /// <param name="bytesWritten">The number of bytes written into the output span. This can be used to slice the output for subsequent calls, if necessary.</param>
        /// <param name="isFinalBlock"><see langword="true"/> (default) when the input span contains the entire data to encode.
        /// Set to <see langword="true"/> when the source buffer contains the entirety of the data to encode.
        /// Set to <see langword="false"/> if this method is being called in a loop and if more input data may follow.
        /// At the end of the loop, call this (potentially with an empty source buffer) passing <see langword="true"/>.</param>
        /// <returns>It returns the OperationStatus enum values:
        /// - Done - on successful processing of the entire input span
        /// - DestinationTooSmall - if there is not enough space in the output span to fit the encoded input
        /// - NeedMoreData - only if <paramref name="isFinalBlock"/> is <see langword="false"/>, otherwise the output is padded if the input is not a multiple of 3
        /// It does not return InvalidData since that is not possible for base64 encoding.
        /// </returns>
        public static unsafe OperationStatus EncodeToUtf8(ReadOnlySpan<byte> bytes, Span<byte> utf8, out int bytesConsumed, out int bytesWritten, bool isFinalBlock = true)
        {
            if (bytes.IsEmpty)
            {
                bytesConsumed = 0;
                bytesWritten = 0;
                return OperationStatus.Done;
            }

            fixed (byte* srcBytes = &MemoryMarshal.GetReference(bytes))
            fixed (byte* destBytes = &MemoryMarshal.GetReference(utf8))
            {
                int srcLength = bytes.Length;
                int destLength = utf8.Length;
                int maxSrcLength;

                if (srcLength <= MaximumEncodeLength && destLength >= GetMaxEncodedToUtf8Length(srcLength))
                {
                    maxSrcLength = srcLength;
                }
                else
                {
                    maxSrcLength = (destLength >> 2) * 3;
                }

                byte* src = srcBytes;
                byte* dest = destBytes;
                byte* srcEnd = srcBytes + (uint)srcLength;
                byte* srcMax = srcBytes + (uint)maxSrcLength;

                // SharpOS cut: Avx2 / Vector128 fast paths (see header); straight to the scalar loop.

                ref byte encodingMap = ref MemoryMarshal.GetReference(EncodingMap);
                uint result = 0;

                srcMax -= 2;
                while (src < srcMax)
                {
                    result = Encode(src, ref encodingMap);
                    Unsafe.WriteUnaligned(dest, result);
                    src += 3;
                    dest += 4;
                }

                if (srcMax + 2 != srcEnd)
                    goto DestinationTooSmallExit;

                if (!isFinalBlock)
                {
                    if (src == srcEnd)
                        goto DoneExit;

                    goto NeedMoreData;
                }

                if (src + 1 == srcEnd)
                {
                    result = EncodeAndPadTwo(src, ref encodingMap);
                    Unsafe.WriteUnaligned(dest, result);
                    src += 1;
                    dest += 4;
                }
                else if (src + 2 == srcEnd)
                {
                    result = EncodeAndPadOne(src, ref encodingMap);
                    Unsafe.WriteUnaligned(dest, result);
                    src += 2;
                    dest += 4;
                }

            DoneExit:
                bytesConsumed = (int)(src - srcBytes);
                bytesWritten = (int)(dest - destBytes);
                return OperationStatus.Done;

            DestinationTooSmallExit:
                bytesConsumed = (int)(src - srcBytes);
                bytesWritten = (int)(dest - destBytes);
                return OperationStatus.DestinationTooSmall;

            NeedMoreData:
                bytesConsumed = (int)(src - srcBytes);
                bytesWritten = (int)(dest - destBytes);
                return OperationStatus.NeedMoreData;
            }
        }

        /// <summary>
        /// Returns the maximum length (in bytes) of the result if you were to encode binary data within a byte span of size "length".
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when the specified <paramref name="length"/> is less than 0 or larger than 1610612733 (since encode inflates the data by 4/3).
        /// </exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetMaxEncodedToUtf8Length(int length)
        {
            if ((uint)length > MaximumEncodeLength)
                throw new ArgumentOutOfRangeException("length"); // SharpOS: was ThrowHelper.ThrowArgumentOutOfRangeException(ExceptionArgument.length)

            return ((length + 2) / 3) * 4;
        }

        /// <summary>
        /// Encode the span of binary data (in-place) into UTF-8 encoded text represented as base 64.
        /// The encoded text output is larger than the binary data contained in the input (the operation inflates the data).
        /// </summary>
        /// <param name="buffer">The input span which contains binary data that needs to be encoded.
        /// It needs to be large enough to fit the result of the operation.</param>
        /// <param name="dataLength">The amount of binary data contained within the buffer that needs to be encoded
        /// (and needs to be smaller than the buffer length).</param>
        /// <param name="bytesWritten">The number of bytes written into the buffer.</param>
        /// <returns>It returns the OperationStatus enum values:
        /// - Done - on successful processing of the entire buffer
        /// - DestinationTooSmall - if there is not enough space in the buffer beyond dataLength to fit the result of encoding the input
        /// It does not return NeedMoreData since this method tramples the data in the buffer and hence can only be called once with all the data in the buffer.
        /// It does not return InvalidData since that is not possible for base 64 encoding.
        /// </returns>
        public static unsafe OperationStatus EncodeToUtf8InPlace(Span<byte> buffer, int dataLength, out int bytesWritten)
        {
            if (buffer.IsEmpty)
            {
                bytesWritten = 0;
                return OperationStatus.Done;
            }

            fixed (byte* bufferBytes = &MemoryMarshal.GetReference(buffer))
            {
                int encodedLength = GetMaxEncodedToUtf8Length(dataLength);
                if (buffer.Length < encodedLength)
                    goto FalseExit;

                int leftover = dataLength - (dataLength / 3) * 3; // how many bytes after packs of 3

                uint destinationIndex = (uint)(encodedLength - 4);
                uint sourceIndex = (uint)(dataLength - leftover);
                uint result = 0;
                ref byte encodingMap = ref MemoryMarshal.GetReference(EncodingMap);

                // encode last pack to avoid conditional in the main loop
                if (leftover != 0)
                {
                    if (leftover == 1)
                    {
                        result = EncodeAndPadTwo(bufferBytes + sourceIndex, ref encodingMap);
                    }
                    else
                    {
                        result = EncodeAndPadOne(bufferBytes + sourceIndex, ref encodingMap);
                    }

                    Unsafe.WriteUnaligned(bufferBytes + destinationIndex, result);
                    destinationIndex -= 4;
                }

                sourceIndex -= 3;
                while ((int)sourceIndex >= 0)
                {
                    result = Encode(bufferBytes + sourceIndex, ref encodingMap);
                    Unsafe.WriteUnaligned(bufferBytes + destinationIndex, result);
                    destinationIndex -= 4;
                    sourceIndex -= 3;
                }

                bytesWritten = encodedLength;
                return OperationStatus.Done;

            FalseExit:
                bytesWritten = 0;
                return OperationStatus.DestinationTooSmall;
            }
        }

        // SharpOS cut: Avx2Encode (see header).

        // SharpOS cut: Vector128Encode (see header).

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe uint Encode(byte* threeBytes, ref byte encodingMap)
        {
            uint t0 = threeBytes[0];
            uint t1 = threeBytes[1];
            uint t2 = threeBytes[2];

            uint i = (t0 << 16) | (t1 << 8) | t2;

            uint i0 = Unsafe.Add(ref encodingMap, (IntPtr)(i >> 18));
            uint i1 = Unsafe.Add(ref encodingMap, (IntPtr)((i >> 12) & 0x3F));
            uint i2 = Unsafe.Add(ref encodingMap, (IntPtr)((i >> 6) & 0x3F));
            uint i3 = Unsafe.Add(ref encodingMap, (IntPtr)(i & 0x3F));

            if (BitConverter.IsLittleEndian)
            {
                return i0 | (i1 << 8) | (i2 << 16) | (i3 << 24);
            }
            else
            {
                return (i0 << 24) | (i1 << 16) | (i2 << 8) | i3;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe uint EncodeAndPadOne(byte* twoBytes, ref byte encodingMap)
        {
            uint t0 = twoBytes[0];
            uint t1 = twoBytes[1];

            uint i = (t0 << 16) | (t1 << 8);

            uint i0 = Unsafe.Add(ref encodingMap, (IntPtr)(i >> 18));
            uint i1 = Unsafe.Add(ref encodingMap, (IntPtr)((i >> 12) & 0x3F));
            uint i2 = Unsafe.Add(ref encodingMap, (IntPtr)((i >> 6) & 0x3F));

            if (BitConverter.IsLittleEndian)
            {
                return i0 | (i1 << 8) | (i2 << 16) | (EncodingPad << 24);
            }
            else
            {
                return (i0 << 24) | (i1 << 16) | (i2 << 8) | EncodingPad;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe uint EncodeAndPadTwo(byte* oneByte, ref byte encodingMap)
        {
            uint t0 = oneByte[0];

            uint i = t0 << 8;

            uint i0 = Unsafe.Add(ref encodingMap, (IntPtr)(i >> 10));
            uint i1 = Unsafe.Add(ref encodingMap, (IntPtr)((i >> 4) & 0x3F));

            if (BitConverter.IsLittleEndian)
            {
                return i0 | (i1 << 8) | (EncodingPad << 16) | (EncodingPad << 24);
            }
            else
            {
                return (i0 << 24) | (i1 << 16) | (EncodingPad << 8) | EncodingPad;
            }
        }

        internal const uint EncodingPad = '='; // '=', for padding

        private const int MaximumEncodeLength = (int.MaxValue / 4) * 3; // 1610612733

        internal static ReadOnlySpan<byte> EncodingMap => "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/"u8;
    }
}
