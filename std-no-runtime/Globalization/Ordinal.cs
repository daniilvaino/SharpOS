// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from dotnet/runtime src/libraries/System.Private.CoreLib/src/System/Globalization/Ordinal.cs
// (.NET 10, MIT) — the members SearchValues<string> uses: CompareStringIgnoreCase,
// EqualsIgnoreCase, IndexOfOrdinalIgnoreCase, ToUpperOrdinal.
//   - SharpOS cut: the other members (IndexOf, LastIndexOf, ...) — std's string search
//     has its own implementation; take them when a caller needs them.
//   - SharpOS cut: the NLS and ICU branches — GlobalizationMode.Invariant is always true
//     in std (Globalization/GlobalizationMode.cs), so upstream's invariant branch is the
//     one that runs; and the ISimdVector path of EqualsIgnoreCase.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Unicode;

namespace System.Globalization
{
    internal static partial class Ordinal
    {
        internal static int CompareStringIgnoreCase(ref char strA, int lengthA, ref char strB, int lengthB)
        {
            int length = Math.Min(lengthA, lengthB);
            int range = length;

            ref char charA = ref strA;
            ref char charB = ref strB;

            const char maxChar = (char)0x7F;

            while (length != 0 && charA <= maxChar && charB <= maxChar)
            {
                // Ordinal equals or lowercase equals if the result ends up in the a-z range
                if (charA == charB ||
                    ((charA | 0x20) == (charB | 0x20) && char.IsAsciiLetter(charA)))
                {
                    length--;
                    charA = ref Unsafe.Add(ref charA, 1);
                    charB = ref Unsafe.Add(ref charB, 1);
                }
                else
                {
                    int currentA = charA;
                    int currentB = charB;

                    // Uppercase both chars if needed
                    if (char.IsAsciiLetterLower(charA))
                    {
                        currentA -= 0x20;
                    }
                    if (char.IsAsciiLetterLower(charB))
                    {
                        currentB -= 0x20;
                    }

                    // Return the (case-insensitive) difference between them.
                    return currentA - currentB;
                }
            }

            if (length == 0)
            {
                return lengthA - lengthB;
            }

            range -= length;

            return CompareStringIgnoreCaseNonAscii(ref charA, lengthA - range, ref charB, lengthB - range);
        }

        internal static int CompareStringIgnoreCaseNonAscii(ref char strA, int lengthA, ref char strB, int lengthB)
        {
            if (GlobalizationMode.Invariant)
            {
                return InvariantModeCasing.CompareStringIgnoreCase(ref strA, lengthA, ref strB, lengthB);
            }

            // SharpOS cut: the NLS branch (Windows globalization).

            // SharpOS cut: OrdinalCasing (ICU) — std is always in invariant mode.
            return InvariantModeCasing.CompareStringIgnoreCase(ref strA, lengthA, ref strB, lengthB);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool EqualsIgnoreCase(ref char charA, ref char charB, int length)
        {
            // SharpOS cut: EqualsIgnoreCase_Vector (generic over ISimdVector, not in std) — the
            // scalar path upstream takes without Vector128 acceleration.
            return EqualsIgnoreCase_Scalar(ref charA, ref charB, length);
        }

        internal static bool EqualsIgnoreCase_Scalar(ref char charA, ref char charB, int length)
        {
            IntPtr byteOffset = IntPtr.Zero;

#if TARGET_64BIT
            ulong valueAu64 = 0;
            ulong valueBu64 = 0;
            // Read 4 chars (64 bits) at a time from each string
            while ((uint)length >= 4)
            {
                valueAu64 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<char, byte>(ref Unsafe.AddByteOffset(ref charA, byteOffset)));
                valueBu64 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<char, byte>(ref Unsafe.AddByteOffset(ref charB, byteOffset)));

                // A 32-bit test - even with the bit-twiddling here - is more efficient than a 64-bit test.
                ulong temp = valueAu64 | valueBu64;
                if (!Utf16Utility.AllCharsInUInt32AreAscii((uint)temp | (uint)(temp >> 32)))
                {
                    goto NonAscii64; // one of the inputs contains non-ASCII data
                }

                // Generally, the caller has likely performed a first-pass check that the input strings
                // are likely equal. Consider a dictionary which computes the hash code of its key before
                // performing a proper deep equality check of the string contents. We want to optimize for
                // the case where the equality check is likely to succeed, which means that we want to avoid
                // branching within this loop unless we're about to exit the loop, either due to failure or
                // due to us running out of input data.

                if (!Utf16Utility.UInt64OrdinalIgnoreCaseAscii(valueAu64, valueBu64))
                {
                    return false;
                }

                byteOffset += 8;
                length -= 4;
            }
#endif
            uint valueAu32 = 0;
            uint valueBu32 = 0;
            // Read 2 chars (32 bits) at a time from each string
#if TARGET_64BIT
            if ((uint)length >= 2)
#else
            while ((uint)length >= 2)
#endif
            {
                valueAu32 = Unsafe.ReadUnaligned<uint>(ref Unsafe.As<char, byte>(ref Unsafe.AddByteOffset(ref charA, byteOffset)));
                valueBu32 = Unsafe.ReadUnaligned<uint>(ref Unsafe.As<char, byte>(ref Unsafe.AddByteOffset(ref charB, byteOffset)));

                if (!Utf16Utility.AllCharsInUInt32AreAscii(valueAu32 | valueBu32))
                {
                    goto NonAscii32; // one of the inputs contains non-ASCII data
                }

                // Generally, the caller has likely performed a first-pass check that the input strings
                // are likely equal. Consider a dictionary which computes the hash code of its key before
                // performing a proper deep equality check of the string contents. We want to optimize for
                // the case where the equality check is likely to succeed, which means that we want to avoid
                // branching within this loop unless we're about to exit the loop, either due to failure or
                // due to us running out of input data.

                if (!Utf16Utility.UInt32OrdinalIgnoreCaseAscii(valueAu32, valueBu32))
                {
                    return false;
                }

                byteOffset += 4;
                length -= 2;
            }

            if (length != 0)
            {
                Debug.Assert(length == 1);

                valueAu32 = Unsafe.AddByteOffset(ref charA, byteOffset);
                valueBu32 = Unsafe.AddByteOffset(ref charB, byteOffset);

                if ((valueAu32 | valueBu32) > 0x7Fu)
                {
                    goto NonAscii32; // one of the inputs contains non-ASCII data
                }

                if (valueAu32 == valueBu32)
                {
                    return true; // exact match
                }

                valueAu32 |= 0x20u;
                if ((uint)(valueAu32 - 'a') > (uint)('z' - 'a'))
                {
                    return false; // not exact match, and first input isn't in [A-Za-z]
                }

                return valueAu32 == (valueBu32 | 0x20u);
            }

            Debug.Assert(length == 0);
            return true;

        NonAscii32:
            // Both values have to be non-ASCII to use the slow fallback, in case if one of them is not we return false
            if (Utf16Utility.AllCharsInUInt32AreAscii(valueAu32) || Utf16Utility.AllCharsInUInt32AreAscii(valueBu32))
            {
                return false;
            }
            goto NonAscii;

#if TARGET_64BIT
        NonAscii64:
            // Both values have to be non-ASCII to use the slow fallback, in case if one of them is not we return false
            if (Utf16Utility.AllCharsInUInt64AreAscii(valueAu64) || Utf16Utility.AllCharsInUInt64AreAscii(valueBu64))
            {
                return false;
            }
#endif
        NonAscii:
            // The non-ASCII case is factored out into its own helper method so that the JIT
            // doesn't need to emit a complex prolog for its caller (this method).
            return CompareStringIgnoreCase(ref Unsafe.AddByteOffset(ref charA, byteOffset), length, ref Unsafe.AddByteOffset(ref charB, byteOffset), length) == 0;
        }

        internal static int IndexOfOrdinalIgnoreCase(ReadOnlySpan<char> source, ReadOnlySpan<char> value)
        {
            if (value.Length == 0)
            {
                return 0;
            }

            if (value.Length > source.Length)
            {
                // A non-linguistic search compares chars directly against one another, so large
                // target strings can never be found inside small search spaces. This check also
                // handles empty 'source' spans.
                return -1;
            }

            if (GlobalizationMode.Invariant)
            {
                return InvariantModeCasing.IndexOfIgnoreCase(source, value);
            }

            // SharpOS cut: the NLS branch and the ICU/vectorized search after it
            // (OrdinalCasing, PackedSpanHelpers, Vector128) — std is always in invariant mode.
            return -1;
        }

        internal static int ToUpperOrdinal(ReadOnlySpan<char> source, Span<char> destination)
        {
            if (source.Overlaps(destination))
                ThrowHelper.ThrowInvalidOperationException(ExceptionResource.InvalidOperation_SpanOverlappedOperation);

            // Assuming that changing case does not affect length
            if (destination.Length < source.Length)
                return -1;

            if (GlobalizationMode.Invariant)
            {
                InvariantModeCasing.ToUpper(source, destination);
                return source.Length;
            }

            // SharpOS cut: the NLS branch (Windows globalization).

            // SharpOS cut: OrdinalCasing (ICU) — std is always in invariant mode.
            InvariantModeCasing.ToUpper(source, destination);
            return source.Length;
        }
    }
}
