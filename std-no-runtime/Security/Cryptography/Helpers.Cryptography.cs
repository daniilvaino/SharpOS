// From dotnet/runtime v8.0.27 System.Security.Cryptography/src/System/Security/Cryptography/Helpers.cs.
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
// SharpOS cut: using System.Formats.Asn1; — ASN.1 is not ported
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Internal.Cryptography
{
    internal static partial class Helpers
    {
        internal static void AddRange<T>(this ICollection<T> coll, IEnumerable<T> newData)
        {
            foreach (T datum in newData)
            {
                coll.Add(datum);
            }
        }

        public static bool UsesIv(this CipherMode cipherMode)
        {
            return cipherMode != CipherMode.ECB;
        }

        public static byte[]? GetCipherIv(this CipherMode cipherMode, byte[]? iv)
        {
            if (cipherMode.UsesIv())
            {
                if (iv == null)
                {
                    throw new CryptographicException(SR.Cryptography_MissingIV);
                }

                return iv;
            }

            return null;
        }

        public static byte[] FixupKeyParity(this byte[] key)
        {
            byte[] oddParityKey = new byte[key.Length];
            for (int index = 0; index < key.Length; index++)
            {
                // Get the bits we are interested in
                oddParityKey[index] = (byte)(key[index] & 0xfe);

                // Get the parity of the sum of the previous bits
                byte tmp1 = (byte)((oddParityKey[index] & 0xF) ^ (oddParityKey[index] >> 4));
                byte tmp2 = (byte)((tmp1 & 0x3) ^ (tmp1 >> 2));
                byte sumBitsMod2 = (byte)((tmp2 & 0x1) ^ (tmp2 >> 1));

                // We need to set the last bit in oddParityKey[index] to the negation
                // of the last bit in sumBitsMod2
                if (sumBitsMod2 == 0)
                    oddParityKey[index] |= 1;
            }
            return oddParityKey;
        }

        // Encode a byte array as an array of upper-case hex characters.
        internal static char[] ToHexArrayUpper(this byte[] bytes)
        {
            char[] chars = new char[bytes.Length * 2];
            global::System.HexConverter.EncodeToUtf16(bytes, chars); // SharpOS: qualified — LuaCSharp declares its own public HexConverter
            return chars;
        }

        // Encode a byte array as an upper case hex string.
        internal static string ToHexStringUpper(this byte[] bytes) =>
            Convert.ToHexString(bytes);

        // Decode a hex string-encoded byte array passed to various X509 crypto api.
        // The parsing rules are overly forgiving but for compat reasons, they cannot be tightened.
        internal static byte[] LaxDecodeHexString(this string hexString)
        {
            int whitespaceCount = 0;

            ReadOnlySpan<char> s = hexString;

            if (s.Length != 0 && s[0] == '\u200E')
            {
                s = s.Slice(1);
            }

            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsWhiteSpace(s[i]))
                    whitespaceCount++;
            }

            uint cbHex = (uint)(s.Length - whitespaceCount) / 2;
            byte[] hex = new byte[cbHex];
            byte accum = 0;
            bool byteInProgress = false;
            int index = 0;

            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];

                if (char.IsWhiteSpace(c))
                {
                    continue;
                }

                accum <<= 4;
                accum |= (byte)global::System.HexConverter.FromChar(c);

                byteInProgress = !byteInProgress;

                // If we've flipped from 0 to 1, back to 0, we have a whole byte
                // so add it to the buffer.
                if (!byteInProgress)
                {
                    Debug.Assert(index < cbHex, "index < cbHex");

                    hex[index] = accum;
                    index++;
                }
            }

            // .NET Framework compat:
            // The .NET Framework algorithm removed all whitespace before the loop, then went up to length/2
            // of what was left.  This means that in the event of odd-length input the last char is
            // ignored, no exception should be raised.
            Debug.Assert(index == cbHex, "index == cbHex");

            return hex;
        }

        internal static bool ContentsEqual(this byte[]? a1, byte[]? a2)
        {
            if (a1 == null)
            {
                return a2 == null;
            }

            if (a2 == null || a1.Length != a2.Length)
            {
                return false;
            }

            return a1.AsSpan().SequenceEqual(a2);
        }

        // SharpOS cut: DecodeOctetStringAsMemory, AreSamePublicECParameters — ASN.1 and EC keys are not ported

        internal static bool IsValidDay(this Calendar calendar, int year, int month, int day, int era)
        {
            return (calendar.IsValidMonth(year, month, era) && day >= 1 && day <= calendar.GetDaysInMonth(year, month, era));
        }

        private static bool IsValidMonth(this Calendar calendar, int year, int month, int era)
        {
            return (calendar.IsValidYear(year) && month >= 1 && month <= calendar.GetMonthsInYear(year, era));
        }

        private static bool IsValidYear(this Calendar calendar, int year)
        {
            return (year >= calendar.GetYear(calendar.MinSupportedDateTime) && year <= calendar.GetYear(calendar.MaxSupportedDateTime));
        }

        internal static void DisposeAll(this IEnumerable<IDisposable> disposables)
        {
            foreach (IDisposable disposable in disposables)
            {
                disposable.Dispose();
            }
        }

        // SharpOS cut: ValidateDer — ASN.1 is not ported

        public static int GetPaddingSize(this SymmetricAlgorithm algorithm, CipherMode mode, int feedbackSizeInBits)
        {
            return (mode == CipherMode.CFB ? feedbackSizeInBits : algorithm.BlockSize) / 8;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static unsafe ref readonly byte GetNonNullPinnableReference(ReadOnlySpan<byte> buffer)
        {
            // Based on the internal implementation from MemoryMarshal.
            return ref buffer.Length != 0 ? ref MemoryMarshal.GetReference(buffer) : ref Unsafe.AsRef<byte>((void*)1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static unsafe ref byte GetNonNullPinnableReference(Span<byte> buffer)
        {
            // Based on the internal implementation from MemoryMarshal.
            return ref buffer.Length != 0 ? ref MemoryMarshal.GetReference(buffer) : ref Unsafe.AsRef<byte>((void*)1);
        }
    }
}
