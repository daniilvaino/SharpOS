// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// System.Text.Ascii - the IsValid family, from dotnet/runtime
// src/libraries/System.Private.CoreLib/src/System/Text/Ascii.cs (.NET 10, MIT).
// Signatures and results are upstream's. SharpOS: the bodies are the scalar loop of upstream's
// IsValidCore (its Vector128/256/512 paths are not ported); the result does not depend on them.
// Not ported yet: Equals/EqualsIgnoreCase, ToUpper/ToLower(InPlace), Trim*, FromUtf16/ToUtf16 -
// add them (from the same file) when a caller needs them.

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
    }
}
