// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from dotnet/runtime main (.NET 10, the fork dotnet-runtime-sharpos):
//   src/libraries/System.Private.CoreLib/src/System/Globalization/CompareInfo.cs
// What the BCL's CompareInfo does in globalization-invariant mode, which is
// the only mode std has (Globalization.cs: one culture, the invariant one):
// linguistic comparison becomes ordinal, IgnoreCase ordinal-ignore-case by
// simple case mapping (InvariantModeCasing over CharUnicodeInfo); the other
// Ignore* flags are accepted and have no effect, as there. Bodies of the
// Compare overloads and the option checks are verbatim.
//
// Cuts: the members nothing here calls yet — IndexOf/LastIndexOf, IsPrefix/
// IsSuffix, sort keys, GetHashCode(string, options), Version, and the NLS/ICU
// back ends. Recorded in docs/nativeaot-nostd-kernel-limits.md.

using System.Diagnostics.CodeAnalysis;

namespace System.Globalization
{
    public sealed class CompareInfo
    {
        // Mask used to check if Compare() / GetHashCode(string) / GetSortKey has the right flags.
        private const CompareOptions ValidCompareMaskOffFlags =
            ~(CompareOptions.IgnoreCase | CompareOptions.IgnoreSymbols | CompareOptions.IgnoreNonSpace |
              CompareOptions.IgnoreWidth | CompareOptions.IgnoreKanaType | CompareOptions.StringSort |
              CompareOptions.NumericOrdering);

        private readonly string m_name;

        internal CompareInfo(string name) => m_name = name ?? "";

        public string Name => m_name;

        public int LCID => 0x007F; // LOCALE_INVARIANT

        public static CompareInfo GetCompareInfo(string name) => CultureInfo.GetCultureInfo(name).CompareInfo;

        // BCL (internal): the invariant culture's CompareInfo.
        internal static CompareInfo Invariant => CultureInfo.InvariantCulture.CompareInfo;

        // BCL (internal), used by DateTimeParse: Compare with IgnoreCase.
        internal int CompareOptionIgnoreCase(ReadOnlySpan<char> string1, ReadOnlySpan<char> string2) =>
            Compare(string1, string2, CompareOptions.IgnoreCase);

        public int Compare(string? string1, string? string2)
        {
            return Compare(string1, string2, CompareOptions.None);
        }

        public int Compare(string? string1, string? string2, CompareOptions options)
        {
            int retVal;

            // Our paradigm is that null sorts less than any other string and
            // that two nulls sort as equal.

            if (string1 == null)
            {
                retVal = (string2 == null) ? 0 : -1;
                goto CheckOptionsAndReturn;
            }
            if (string2 == null)
            {
                retVal = 1;
                goto CheckOptionsAndReturn;
            }

            return Compare(string1.AsSpan(), string2.AsSpan(), options);

        CheckOptionsAndReturn:

            // If we're short-circuiting the globalization logic, we still need to check that
            // the provided options were valid.

            CheckCompareOptionsForCompare(options);
            return retVal;
        }

        public int Compare(string? string1, int offset1, int length1, string? string2, int offset2, int length2)
        {
            return Compare(string1, offset1, length1, string2, offset2, length2, CompareOptions.None);
        }

        public int Compare(string? string1, int offset1, string? string2, int offset2, CompareOptions options)
        {
            return Compare(string1, offset1, string1 == null ? 0 : string1.Length - offset1,
                           string2, offset2, string2 == null ? 0 : string2.Length - offset2, options);
        }

        public int Compare(string? string1, int offset1, string? string2, int offset2)
        {
            return Compare(string1, offset1, string2, offset2, CompareOptions.None);
        }

        public int Compare(string? string1, int offset1, int length1, string? string2, int offset2, int length2, CompareOptions options)
        {
            // SharpOS: string.TryGetSpan is CoreLib-internal; the same bounds test.
            if (length1 < 0 || length2 < 0 || offset1 < 0 || offset2 < 0
                || offset1 > (string1 == null ? 0 : string1.Length) - length1
                || offset2 > (string2 == null ? 0 : string2.Length) - length2)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(length1);
                ArgumentOutOfRangeException.ThrowIfNegative(length2);
                ArgumentOutOfRangeException.ThrowIfNegative(offset1);
                ArgumentOutOfRangeException.ThrowIfNegative(offset2);

                if (offset1 > (string1 == null ? 0 : string1.Length) - length1)
                {
                    throw new ArgumentOutOfRangeException(nameof(string1), SR.ArgumentOutOfRange_OffsetLength);
                }

                throw new ArgumentOutOfRangeException(nameof(string2), SR.ArgumentOutOfRange_OffsetLength);
            }

            int retVal;

            // Our paradigm is that null sorts less than any other string and
            // that two nulls sort as equal.

            if (string1 == null)
            {
                retVal = (string2 == null) ? 0 : -1;
                goto CheckOptionsAndReturn;
            }
            if (string2 == null)
            {
                retVal = 1;
                goto CheckOptionsAndReturn;
            }

            return Compare(string1.AsSpan(offset1, length1), string2.AsSpan(offset2, length2), options);

        CheckOptionsAndReturn:

            CheckCompareOptionsForCompare(options);
            return retVal;
        }

        public int Compare(ReadOnlySpan<char> string1, ReadOnlySpan<char> string2, CompareOptions options = CompareOptions.None)
        {
            if ((options & ValidCompareMaskOffFlags) == 0)
            {
                // Globalization-invariant mode: the flags normalize to Ordinal[IgnoreCase].

                if ((options & CompareOptions.IgnoreCase) == 0)
                {
                    return string1.SequenceCompareTo(string2);
                }

                return CompareStringIgnoreCase(string1, string2);
            }
            else
            {
                // Less common case: caller is attempting to perform non-linguistic comparison,
                // or an invalid combination of flags was supplied.

                if (options == CompareOptions.Ordinal)
                {
                    return string1.SequenceCompareTo(string2);
                }

                if (options == CompareOptions.OrdinalIgnoreCase)
                {
                    return CompareStringIgnoreCase(string1, string2);
                }

                ThrowCompareOptionsCheckFailed(options);

                return -1; // make the compiler happy;
            }
        }

        // Checks that 'CompareOptions' is valid for a call to Compare, throwing the appropriate
        // exception if the check fails.
        private static void CheckCompareOptionsForCompare(CompareOptions options)
        {
            // Any combination of defined CompareOptions flags is valid, except for
            // Ordinal and OrdinalIgnoreCase, which may only be used in isolation.

            if ((options & ValidCompareMaskOffFlags) != 0)
            {
                if (options != CompareOptions.Ordinal && options != CompareOptions.OrdinalIgnoreCase)
                {
                    ThrowCompareOptionsCheckFailed(options);
                }
            }
        }

        [DoesNotReturn]
        private static void ThrowCompareOptionsCheckFailed(CompareOptions options)
        {
            throw new ArgumentException(
                paramName: nameof(options),
                message: ((options & CompareOptions.Ordinal) != 0) ? SR.Argument_CompareOptionOrdinal : SR.Argument_InvalidFlag);
        }

        // InvariantModeCasing.CompareStringIgnoreCase: code point by code
        // point, each through its simple uppercase mapping.
        private static int CompareStringIgnoreCase(ReadOnlySpan<char> string1, ReadOnlySpan<char> string2)
        {
            int i = 0, j = 0;
            while (i < string1.Length && j < string2.Length)
            {
                uint a = Next(string1, ref i);
                uint b = Next(string2, ref j);
                if (a == b) continue;
                a = CharUnicodeInfo.ToUpper(a);
                b = CharUnicodeInfo.ToUpper(b);
                if (a != b) return (int)a - (int)b;
            }
            return (string1.Length - i) - (string2.Length - j);
        }

        private static uint Next(ReadOnlySpan<char> s, ref int i)
        {
            char c = s[i++];
            if (char.IsHighSurrogate(c) && i < s.Length && char.IsLowSurrogate(s[i]))
                return (uint)char.ConvertToUtf32(c, s[i++]);
            return c;
        }

        public override bool Equals([NotNullWhen(true)] object? value) => value is CompareInfo that && Name == that.Name;

        public override int GetHashCode() => Name.GetHashCode();

        public override string ToString() => "CompareInfo - " + Name;
    }
}

namespace System
{
    // In System, not System.Globalization: a nearer SR would hide the shared
    // one from every type in this namespace (NumberFormatInfo among them).
    internal static partial class SR
    {
        internal const string ArgumentOutOfRange_OffsetLength = "Offset and length must refer to a position in the string.";
        internal const string Argument_CompareOptionOrdinal = "CompareOption.Ordinal cannot be used with other options.";
        internal const string Argument_InvalidFlag = "Value of flags is invalid.";
    }
}
