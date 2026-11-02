// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// System.Text.Ascii - the IsValid family, from dotnet/runtime
// src/libraries/System.Private.CoreLib/src/System/Text/Ascii.cs (.NET 10, MIT).
// Signatures and results are upstream's. SharpOS: the bodies are the scalar loop of upstream's
// IsValidCore (its Vector128/256/512 paths are not ported); the result does not depend on them.
// Not ported: Equals/EqualsIgnoreCase (public), ToUpper/ToLower, Trim* — Ascii.Equality.cs,
// Ascii.CaseConversion.cs and Ascii.Trimming.cs need generic math (INumberBase<T>,
// IBinaryInteger<T>), which std does not have. FromUtf16/ToUtf16 and the internal
// Ascii.Utility are BCL 8's (Ascii.Transcoding.cs, Ascii.Utility.cs).

using System.Runtime.CompilerServices;

namespace System.Text
{
    public static partial class Ascii
    {
        /// <summary>
        /// Determines whether the provided value contains only ASCII bytes.
        /// </summary>
        /// <param name="value">The value to inspect.</param>
        /// <returns>True if <paramref name="value"/> contains only ASCII bytes or is
        /// empty; False otherwise.</returns>
        public static bool IsValid(ReadOnlySpan<byte> value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] > 0x7F)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Determines whether the provided value contains only ASCII chars.
        /// </summary>
        /// <param name="value">The value to inspect.</param>
        /// <returns>True if <paramref name="value"/> contains only ASCII chars or is
        /// empty; False otherwise.</returns>
        public static bool IsValid(ReadOnlySpan<char> value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] > 0x7F)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Determines whether the provided value is ASCII byte.
        /// </summary>
        /// <param name="value">The value to inspect.</param>
        /// <returns>True if <paramref name="value"/> is ASCII, False otherwise.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsValid(byte value) => value <= 127;

        /// <summary>
        /// Determines whether the provided value is ASCII char.
        /// </summary>
        /// <param name="value">The value to inspect.</param>
        /// <returns>True if <paramref name="value"/> is ASCII, False otherwise.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsValid(char value) => value <= 127;

        // Upstream's internal EqualsIgnoreCase(ref char, ref char, nuint) (Ascii.Equality.cs),
        // its scalar loop — the path taken without Vector128 acceleration. Ascii.Equality.cs
        // itself needs generic math (INumberBase<T>), which std does not have.
        internal static bool EqualsIgnoreCase(ref char left, ref char right, nuint length)
        {
            for (nuint i = 0; i < length; ++i)
            {
                uint valueA = Unsafe.Add(ref left, i);
                uint valueB = Unsafe.Add(ref right, i);

                if (!UnicodeUtility.IsAsciiCodePoint(valueA | valueB))
                {
                    return false;
                }

                if (valueA == valueB)
                {
                    continue; // exact match
                }

                valueA |= 0x20u;
                if (valueA - 'a' > 'z' - 'a')
                {
                    return false; // not exact match, and first input isn't in [A-Za-z]
                }

                if (valueA != (valueB | 0x20u))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
