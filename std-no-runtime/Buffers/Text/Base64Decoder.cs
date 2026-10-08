// Ported from dotnet/runtime release/8.0 (MIT), the CoreLib snapshot in gc-experiment/dotnet-runtime-8.0:
//   src/libraries/System.Private.CoreLib/src/System/Buffers/Text/Base64Decoder.cs
//
// SharpOS cuts (marked inline): the Avx2Decode / Vector128Decode / SimdShuffle fast paths and their
// System.Runtime.Intrinsics.X86 / .Arm dispatch. std has Vector128 but no Avx2 / Ssse3 /
// AdvSimd hardware-intrinsic classes; the scalar loop is the BCL's own tail path and gives
// identical output. Base64.cs (only the DEBUG read/write asserts of those vector paths) and
// Base64Validator.cs (Base64.IsValid, needs SearchValues) are not ported.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
// SharpOS cut: using System.Runtime.Intrinsics(.Arm/.X86) - vector paths not ported.

namespace System.Buffers.Text
{
    // AVX2 version based on https://github.com/aklomp/base64/tree/e516d769a2a432c08404f1981e73b431566057be/lib/arch/avx2
    // Vector128 version based on https://github.com/aklomp/base64/tree/e516d769a2a432c08404f1981e73b431566057be/lib/arch/ssse3

    public static partial class Base64
    {
        /// <summary>
        /// Decode the span of UTF-8 encoded text represented as base64 into binary data.
        /// If the input is not a multiple of 4, it will decode as much as it can, to the closest multiple of 4.
        /// </summary>
        /// <param name="utf8">The input span which contains UTF-8 encoded text in base64 that needs to be decoded.</param>
        /// <param name="bytes">The output span which contains the result of the operation, i.e. the decoded binary data.</param>
        /// <param name="bytesConsumed">The number of input bytes consumed during the operation. This can be used to slice the input for subsequent calls, if necessary.</param>
        /// <param name="bytesWritten">The number of bytes written into the output span. This can be used to slice the output for subsequent calls, if necessary.</param>
        /// <param name="isFinalBlock"><see langword="true"/> (default) when the input span contains the entire data to encode.
        /// Set to <see langword="true"/> when the source buffer contains the entirety of the data to encode.
        /// Set to <see langword="false"/> if this method is being called in a loop and if more input data may follow.
        /// At the end of the loop, call this (potentially with an empty source buffer) passing <see langword="true"/>.</param>
        /// <returns>It returns the OperationStatus enum values:
        /// - Done - on successful processing of the entire input span
        /// - DestinationTooSmall - if there is not enough space in the output span to fit the decoded input
        /// - NeedMoreData - only if <paramref name="isFinalBlock"/> is false and the input is not a multiple of 4, otherwise the partial input would be considered as InvalidData
        /// - InvalidData - if the input contains bytes outside of the expected base64 range, or if it contains invalid/more than two padding characters,
        ///   or if the input is incomplete (i.e. not a multiple of 4) and <paramref name="isFinalBlock"/> is <see langword="true"/>.
        /// </returns>
        public static OperationStatus DecodeFromUtf8(ReadOnlySpan<byte> utf8, Span<byte> bytes, out int bytesConsumed, out int bytesWritten, bool isFinalBlock = true) =>
            DecodeFromUtf8(utf8, bytes, out bytesConsumed, out bytesWritten, isFinalBlock, ignoreWhiteSpace: true);

        private static unsafe OperationStatus DecodeFromUtf8(ReadOnlySpan<byte> utf8, Span<byte> bytes, out int bytesConsumed, out int bytesWritten, bool isFinalBlock, bool ignoreWhiteSpace)
        {
            if (utf8.IsEmpty)
            {
                bytesConsumed = 0;
                bytesWritten = 0;
                return OperationStatus.Done;
            }

            fixed (byte* srcBytes = &MemoryMarshal.GetReference(utf8))
            fixed (byte* destBytes = &MemoryMarshal.GetReference(bytes))
            {
                int srcLength = utf8.Length & ~0x3;  // only decode input up to the closest multiple of 4.
                int destLength = bytes.Length;
                int maxSrcLength = srcLength;
                int decodedLength = GetMaxDecodedFromUtf8Length(srcLength);

                // max. 2 padding chars
                if (destLength < decodedLength - 2)
                {
                    // For overflow see comment below
                    maxSrcLength = destLength / 3 * 4;
                }

                byte* src = srcBytes;
                byte* dest = destBytes;
                byte* srcEnd = srcBytes + (uint)srcLength;
                byte* srcMax = srcBytes + (uint)maxSrcLength;

                // SharpOS cut: Avx2 / Vector128 fast paths (see header); straight to the scalar loop.

                // Last bytes could have padding characters, so process them separately and treat them as valid only if isFinalBlock is true
                // if isFinalBlock is false, padding characters are considered invalid
                int skipLastChunk = isFinalBlock ? 4 : 0;

                if (destLength >= decodedLength)
                {
                    maxSrcLength = srcLength - skipLastChunk;
                }
                else
                {
                    // This should never overflow since destLength here is less than int.MaxValue / 4 * 3 (i.e. 1610612733)
                    // Therefore, (destLength / 3) * 4 will always be less than 2147483641
                    Debug.Assert(destLength < (int.MaxValue / 4 * 3));
                    maxSrcLength = (destLength / 3) * 4;
                }

                ref sbyte decodingMap = ref MemoryMarshal.GetReference(DecodingMap);
                srcMax = srcBytes + maxSrcLength;

                while (src < srcMax)
                {
                    int result = Decode(src, ref decodingMap);

                    if (result < 0)
                    {
                        goto InvalidDataExit;
                    }

                    WriteThreeLowOrderBytes(dest, result);
                    src += 4;
                    dest += 3;
                }

                if (maxSrcLength != srcLength - skipLastChunk)
                {
                    goto DestinationTooSmallExit;
                }

                // If input is less than 4 bytes, srcLength == sourceIndex == 0
                // If input is not a multiple of 4, sourceIndex == srcLength != 0
                if (src == srcEnd)
                {
                    if (isFinalBlock)
                    {
                        goto InvalidDataExit;
                    }

                    if (src == srcBytes + utf8.Length)
                    {
                        goto DoneExit;
                    }

                    goto NeedMoreDataExit;
                }

                // if isFinalBlock is false, we will never reach this point

                // Handle last four bytes. There are 0, 1, 2 padding chars.
                uint t0 = srcEnd[-4];
                uint t1 = srcEnd[-3];
                uint t2 = srcEnd[-2];
                uint t3 = srcEnd[-1];

                int i0 = Unsafe.Add(ref decodingMap, (IntPtr)t0);
                int i1 = Unsafe.Add(ref decodingMap, (IntPtr)t1);

                i0 <<= 18;
                i1 <<= 12;

                i0 |= i1;

                byte* destMax = destBytes + (uint)destLength;

                if (t3 != EncodingPad)
                {
                    int i2 = Unsafe.Add(ref decodingMap, (IntPtr)t2);
                    int i3 = Unsafe.Add(ref decodingMap, (IntPtr)t3);

                    i2 <<= 6;

                    i0 |= i3;
                    i0 |= i2;

                    if (i0 < 0)
                    {
                        goto InvalidDataExit;
                    }
                    if (dest + 3 > destMax)
                    {
                        goto DestinationTooSmallExit;
                    }

                    WriteThreeLowOrderBytes(dest, i0);
                    dest += 3;
                }
                else if (t2 != EncodingPad)
                {
                    int i2 = Unsafe.Add(ref decodingMap, (IntPtr)t2);

                    i2 <<= 6;

                    i0 |= i2;

                    if (i0 < 0)
                    {
                        goto InvalidDataExit;
                    }
                    if (dest + 2 > destMax)
                    {
                        goto DestinationTooSmallExit;
                    }

                    dest[0] = (byte)(i0 >> 16);
                    dest[1] = (byte)(i0 >> 8);
                    dest += 2;
                }
                else
                {
                    if (i0 < 0)
                    {
                        goto InvalidDataExit;
                    }
                    if (dest + 1 > destMax)
                    {
                        goto DestinationTooSmallExit;
                    }

                    dest[0] = (byte)(i0 >> 16);
                    dest += 1;
                }

                src += 4;

                if (srcLength != utf8.Length)
                {
                    goto InvalidDataExit;
                }

            DoneExit:
                bytesConsumed = (int)(src - srcBytes);
                bytesWritten = (int)(dest - destBytes);
                return OperationStatus.Done;

            DestinationTooSmallExit:
                if (srcLength != utf8.Length && isFinalBlock)
                {
                    goto InvalidDataExit; // if input is not a multiple of 4, and there is no more data, return invalid data instead
                }

                bytesConsumed = (int)(src - srcBytes);
                bytesWritten = (int)(dest - destBytes);
                return OperationStatus.DestinationTooSmall;

            NeedMoreDataExit:
                bytesConsumed = (int)(src - srcBytes);
                bytesWritten = (int)(dest - destBytes);
                return OperationStatus.NeedMoreData;

            InvalidDataExit:
                bytesConsumed = (int)(src - srcBytes);
                bytesWritten = (int)(dest - destBytes);
                return ignoreWhiteSpace ?
                    InvalidDataFallback(utf8, bytes, ref bytesConsumed, ref bytesWritten, isFinalBlock) :
                    OperationStatus.InvalidData;
            }

            static OperationStatus InvalidDataFallback(ReadOnlySpan<byte> utf8, Span<byte> bytes, ref int bytesConsumed, ref int bytesWritten, bool isFinalBlock)
            {
                utf8 = utf8.Slice(bytesConsumed);
                bytes = bytes.Slice(bytesWritten);

                OperationStatus status;
                do
                {
                    int localConsumed = IndexOfAnyExceptWhiteSpace(utf8);
                    if (localConsumed < 0)
                    {
                        // The remainder of the input is all whitespace. Mark it all as having been consumed,
                        // and mark the operation as being done.
                        bytesConsumed += utf8.Length;
                        status = OperationStatus.Done;
                        break;
                    }

                    if (localConsumed == 0)
                    {
                        // Non-whitespace was found at the beginning of the input. Since it wasn't consumed
                        // by the previous call to DecodeFromUtf8, it must be part of a Base64 sequence
                        // that was interrupted by whitespace or something else considered invalid.
                        // Fall back to block-wise decoding. This is very slow, but it's also very non-standard
                        // formatting of the input; whitespace is typically only found between blocks, such as
                        // when Convert.ToBase64String inserts a line break every 76 output characters.
                        return DecodeWithWhiteSpaceBlockwise(utf8, bytes, ref bytesConsumed, ref bytesWritten, isFinalBlock);
                    }

                    // Skip over the starting whitespace and continue.
                    bytesConsumed += localConsumed;
                    utf8 = utf8.Slice(localConsumed);

                    // Try again after consumed whitespace
                    status = DecodeFromUtf8(utf8, bytes, out localConsumed, out int localWritten, isFinalBlock, ignoreWhiteSpace: false);
                    bytesConsumed += localConsumed;
                    bytesWritten += localWritten;
                    if (status is not OperationStatus.InvalidData)
                    {
                        break;
                    }

                    utf8 = utf8.Slice(localConsumed);
                    bytes = bytes.Slice(localWritten);
                }
                while (!utf8.IsEmpty);

                return status;
            }
        }

        /// <summary>
        /// Returns the maximum length (in bytes) of the result if you were to decode base 64 encoded text within a byte span of size "length".
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when the specified <paramref name="length"/> is less than 0.
        /// </exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetMaxDecodedFromUtf8Length(int length)
        {
            if (length < 0)
            {
                throw new ArgumentOutOfRangeException("length"); // SharpOS: was ThrowHelper.ThrowArgumentOutOfRangeException(ExceptionArgument.length)
            }

            return (length >> 2) * 3;
        }

        /// <summary>
        /// Decode the span of UTF-8 encoded text in base 64 (in-place) into binary data.
        /// The decoded binary output is smaller than the text data contained in the input (the operation deflates the data).
        /// If the input is not a multiple of 4, it will not decode any.
        /// </summary>
        /// <param name="buffer">The input span which contains the base 64 text data that needs to be decoded.</param>
        /// <param name="bytesWritten">The number of bytes written into the buffer.</param>
        /// <returns>It returns the OperationStatus enum values:
        /// - Done - on successful processing of the entire input span
        /// - InvalidData - if the input contains bytes outside of the expected base 64 range, or if it contains invalid/more than two padding characters,
        ///   or if the input is incomplete (i.e. not a multiple of 4).
        /// It does not return DestinationTooSmall since that is not possible for base 64 decoding.
        /// It does not return NeedMoreData since this method tramples the data in the buffer and
        /// hence can only be called once with all the data in the buffer.
        /// </returns>
        public static OperationStatus DecodeFromUtf8InPlace(Span<byte> buffer, out int bytesWritten) =>
            DecodeFromUtf8InPlace(buffer, out bytesWritten, ignoreWhiteSpace: true);

        private static unsafe OperationStatus DecodeFromUtf8InPlace(Span<byte> buffer, out int bytesWritten, bool ignoreWhiteSpace)
        {
            if (buffer.IsEmpty)
            {
                bytesWritten = 0;
                return OperationStatus.Done;
            }

            fixed (byte* bufferBytes = &MemoryMarshal.GetReference(buffer))
            {
                uint bufferLength = (uint)buffer.Length;
                uint sourceIndex = 0;
                uint destIndex = 0;

                // only decode input if it is a multiple of 4
                if (bufferLength % 4 != 0)
                {
                    goto InvalidExit;
                }

                ref sbyte decodingMap = ref MemoryMarshal.GetReference(DecodingMap);

                while (sourceIndex < bufferLength - 4)
                {
                    int result = Decode(bufferBytes + sourceIndex, ref decodingMap);
                    if (result < 0)
                    {
                        goto InvalidExit;
                    }

                    WriteThreeLowOrderBytes(bufferBytes + destIndex, result);
                    destIndex += 3;
                    sourceIndex += 4;
                }

                uint t0 = bufferBytes[bufferLength - 4];
                uint t1 = bufferBytes[bufferLength - 3];
                uint t2 = bufferBytes[bufferLength - 2];
                uint t3 = bufferBytes[bufferLength - 1];

                int i0 = Unsafe.Add(ref decodingMap, t0);
                int i1 = Unsafe.Add(ref decodingMap, t1);

                i0 <<= 18;
                i1 <<= 12;

                i0 |= i1;

                if (t3 != EncodingPad)
                {
                    int i2 = Unsafe.Add(ref decodingMap, t2);
                    int i3 = Unsafe.Add(ref decodingMap, t3);

                    i2 <<= 6;

                    i0 |= i3;
                    i0 |= i2;

                    if (i0 < 0)
                    {
                        goto InvalidExit;
                    }

                    WriteThreeLowOrderBytes(bufferBytes + destIndex, i0);
                    destIndex += 3;
                }
                else if (t2 != EncodingPad)
                {
                    int i2 = Unsafe.Add(ref decodingMap, t2);

                    i2 <<= 6;

                    i0 |= i2;

                    if (i0 < 0)
                    {
                        goto InvalidExit;
                    }

                    bufferBytes[destIndex] = (byte)(i0 >> 16);
                    bufferBytes[destIndex + 1] = (byte)(i0 >> 8);
                    destIndex += 2;
                }
                else
                {
                    if (i0 < 0)
                    {
                        goto InvalidExit;
                    }

                    bufferBytes[destIndex] = (byte)(i0 >> 16);
                    destIndex += 1;
                }

                bytesWritten = (int)destIndex;
                return OperationStatus.Done;

            InvalidExit:
                bytesWritten = (int)destIndex;
                return ignoreWhiteSpace ?
                    DecodeWithWhiteSpaceFromUtf8InPlace(buffer, ref bytesWritten, sourceIndex) : // The input may have whitespace, attempt to decode while ignoring whitespace.
                    OperationStatus.InvalidData;
            }
        }

        private static OperationStatus DecodeWithWhiteSpaceBlockwise(ReadOnlySpan<byte> utf8, Span<byte> bytes, ref int bytesConsumed, ref int bytesWritten, bool isFinalBlock = true)
        {
            const int BlockSize = 4;
            Span<byte> buffer = stackalloc byte[BlockSize];
            OperationStatus status = OperationStatus.Done;

            while (!utf8.IsEmpty)
            {
                int encodedIdx = 0;
                int bufferIdx = 0;
                int skipped = 0;

                for (; encodedIdx < utf8.Length && (uint)bufferIdx < (uint)buffer.Length; ++encodedIdx)
                {
                    if (IsWhiteSpace(utf8[encodedIdx]))
                    {
                        skipped++;
                    }
                    else
                    {
                        buffer[bufferIdx] = utf8[encodedIdx];
                        bufferIdx++;
                    }
                }

                utf8 = utf8.Slice(encodedIdx);
                bytesConsumed += skipped;

                if (bufferIdx == 0)
                {
                    continue;
                }

                bool hasAnotherBlock = utf8.Length >= BlockSize && bufferIdx == BlockSize;
                bool localIsFinalBlock = !hasAnotherBlock;

                // If this block contains padding and there's another block, then only whitespace may follow for being valid.
                if (hasAnotherBlock)
                {
                    int paddingCount = GetPaddingCount(ref buffer[^1]);
                    if (paddingCount > 0)
                    {
                        hasAnotherBlock = false;
                        localIsFinalBlock = true;
                    }
                }

                if (localIsFinalBlock && !isFinalBlock)
                {
                    localIsFinalBlock = false;
                }

                status = DecodeFromUtf8(buffer.Slice(0, bufferIdx), bytes, out int localConsumed, out int localWritten, localIsFinalBlock, ignoreWhiteSpace: false);
                bytesConsumed += localConsumed;
                bytesWritten += localWritten;

                if (status != OperationStatus.Done)
                {
                    return status;
                }

                // The remaining data must all be whitespace in order to be valid.
                if (!hasAnotherBlock)
                {
                    for (int i = 0; i < utf8.Length; ++i)
                    {
                        if (!IsWhiteSpace(utf8[i]))
                        {
                            // Revert previous dest increment, since an invalid state followed.
                            bytesConsumed -= localConsumed;
                            bytesWritten -= localWritten;

                            return OperationStatus.InvalidData;
                        }

                        bytesConsumed++;
                    }

                    break;
                }

                bytes = bytes.Slice(localWritten);
            }

            return status;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetPaddingCount(ref byte ptrToLastElement)
        {
            int padding = 0;

            if (ptrToLastElement == EncodingPad) padding++;
            if (Unsafe.Subtract(ref ptrToLastElement, 1) == EncodingPad) padding++;

            return padding;
        }

        private static OperationStatus DecodeWithWhiteSpaceFromUtf8InPlace(Span<byte> utf8, ref int destIndex, uint sourceIndex)
        {
            const int BlockSize = 4;
            Span<byte> buffer = stackalloc byte[BlockSize];

            OperationStatus status = OperationStatus.Done;
            int localDestIndex = destIndex;
            bool hasPaddingBeenProcessed = false;
            int localBytesWritten = 0;

            while (sourceIndex < (uint)utf8.Length)
            {
                int bufferIdx = 0;

                while (bufferIdx < BlockSize)
                {
                    if (sourceIndex >= (uint)utf8.Length) // TODO https://github.com/dotnet/runtime/issues/83349: move into the while condition once fixed
                    {
                        break;
                    }

                    if (!IsWhiteSpace(utf8[(int)sourceIndex]))
                    {
                        buffer[bufferIdx] = utf8[(int)sourceIndex];
                        bufferIdx++;
                    }

                    sourceIndex++;
                }

                if (bufferIdx == 0)
                {
                    continue;
                }

                if (bufferIdx != 4)
                {
                    status = OperationStatus.InvalidData;
                    break;
                }

                if (hasPaddingBeenProcessed)
                {
                    // Padding has already been processed, a new valid block cannot be processed.
                    // Revert previous dest increment, since an invalid state followed.
                    localDestIndex -= localBytesWritten;
                    status = OperationStatus.InvalidData;
                    break;
                }

                status = DecodeFromUtf8InPlace(buffer, out localBytesWritten, ignoreWhiteSpace: false);
                localDestIndex += localBytesWritten;
                hasPaddingBeenProcessed = localBytesWritten < 3;

                if (status != OperationStatus.Done)
                {
                    break;
                }

                // Write result to source span in place.
                for (int i = 0; i < localBytesWritten; i++)
                {
                    utf8[localDestIndex - localBytesWritten + i] = buffer[i];
                }
            }

            destIndex = localDestIndex;
            return status;
        }

        // SharpOS cut: Avx2Decode (see header).

        // SharpOS cut: SimdShuffle (see header).

        // SharpOS cut: Vector128Decode (see header).

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe int Decode(byte* encodedBytes, ref sbyte decodingMap)
        {
            uint t0 = encodedBytes[0];
            uint t1 = encodedBytes[1];
            uint t2 = encodedBytes[2];
            uint t3 = encodedBytes[3];

            int i0 = Unsafe.Add(ref decodingMap, t0);
            int i1 = Unsafe.Add(ref decodingMap, t1);
            int i2 = Unsafe.Add(ref decodingMap, t2);
            int i3 = Unsafe.Add(ref decodingMap, t3);

            i0 <<= 18;
            i1 <<= 12;
            i2 <<= 6;

            i0 |= i3;
            i1 |= i2;

            i0 |= i1;
            return i0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void WriteThreeLowOrderBytes(byte* destination, int value)
        {
            destination[0] = (byte)(value >> 16);
            destination[1] = (byte)(value >> 8);
            destination[2] = (byte)value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int IndexOfAnyExceptWhiteSpace(ReadOnlySpan<byte> span)
        {
            for (int i = 0; i < span.Length; i++)
            {
                if (!IsWhiteSpace(span[i]))
                {
                    return i;
                }
            }

            return -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool IsWhiteSpace(int value)
        {
            if (Environment.Is64BitProcess)
            {
                // For description see https://github.com/dotnet/runtime/blob/48e74187cb15386c29eedaa046a5ee2c7ddef161/src/libraries/Common/src/System/HexConverter.cs#L314-L330
                // Lookup bit mask for "\t\n\r ".
                const ulong MagicConstant = 0xC800010000000000UL;
                ulong i = (uint)value - '\t';
                ulong shift = MagicConstant << (int)i;
                ulong mask = i - 64;
                return (long)(shift & mask) < 0;
            }

            if (value < 32)
            {
                const int BitMask = (1 << (int)'\t') | (1 << (int)'\n') | (1 << (int)'\r');
                return ((1 << value) & BitMask) != 0;
            }

            return value == 32;
        }

        // Pre-computing this table using a custom string(s_characters) and GenerateDecodingMapAndVerify (found in tests)
        private static ReadOnlySpan<sbyte> DecodingMap => new sbyte[]
        {
            -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 62, -1, -1, -1, 63,         //62 is placed at index 43 (for +), 63 at index 47 (for /)
            52, 53, 54, 55, 56, 57, 58, 59, 60, 61, -1, -1, -1, -1, -1, -1,         //52-61 are placed at index 48-57 (for 0-9), 64 at index 61 (for =)
            -1,  0,  1,  2,  3,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 14,
            15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, -1, -1, -1, -1, -1,         //0-25 are placed at index 65-90 (for A-Z)
            -1, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40,
            41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, -1, -1, -1, -1, -1,         //26-51 are placed at index 97-122 (for a-z)
            -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,         // Bytes over 122 ('z') are invalid and cannot be decoded
            -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,         // Hence, padding the map with 255, which indicates invalid input
            -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        };
    }
}
