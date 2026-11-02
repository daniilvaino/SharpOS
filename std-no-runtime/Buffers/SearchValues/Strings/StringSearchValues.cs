// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from dotnet/runtime src/libraries/System.Private.CoreLib/src/System/SearchValues/Strings/StringSearchValues.cs
// (.NET 10, MIT), with Strings/ beside it: Aho-Corasick, Rabin-Karp and the fallbacks.
//   - SharpOS cut: the vector-only implementations upstream picks first — Teddy
//     (AsciiStringSearchValuesTeddy*, TeddyHelper, TeddyBucketizer) and
//     SingleStringSearchValues(Packed)ThreeChars. They need Vector512/Avx512* and
//     PackedSpanHelpers, which std does not have, and the full IndexOfAnyAsciiSearcher
//     (std's is the scalar one, IsVectorizationSupported = false). What remains is what
//     upstream selects on hardware without Ssse3/AdvSimd and Vector128 acceleration:
//     Aho-Corasick for several values, a single-value fallback for one. Results are
//     identical; only speed differs.

using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using System.Text;
using static System.Buffers.StringSearchValuesHelper;

namespace System.Buffers
{
    internal static class StringSearchValues
    {
        private const int TeddyBucketCount = 8;

        private static readonly SearchValues<char> s_asciiLetters =
            SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

        private static readonly SearchValues<char> s_allAsciiExceptLowercase =
            SearchValues.Create("\0\u0001\u0002\u0003\u0004\u0005\u0006\a\b\t\n\v\f\r\u000E\u000F\u0010\u0011\u0012\u0013\u0014\u0015\u0016\u0017\u0018\u0019\u001A\e\u001C\u001D\u001E\u001F !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`{|}~\u007F");

        public static SearchValues<string> Create(ReadOnlySpan<string> values, bool ignoreCase)
        {
            if (values.Length == 0)
            {
                return new EmptySearchValues<string>();
            }

            if (values.Length == 1)
            {
                // Avoid additional overheads for single-value inputs.
                string value = values[0];
                ArgumentNullException.ThrowIfNull(value, nameof(values));
                string normalizedValue = NormalizeIfNeeded(value, ignoreCase);

                AnalyzeValues(new ReadOnlySpan<string>(ref normalizedValue), ref ignoreCase, out bool ascii, out bool asciiLettersOnly, out _, out _);
                return CreateForSingleValue(normalizedValue, uniqueValues: null, ignoreCase, ascii, asciiLettersOnly);
            }

            var uniqueValues = new HashSet<string>(values.Length, ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

            foreach (string value in values)
            {
                ArgumentNullException.ThrowIfNull(value, nameof(values));

                uniqueValues.Add(value);
            }

            if (uniqueValues.Contains(string.Empty))
            {
                return new SingleStringSearchValuesFallback<SearchValues.FalseConst>(string.Empty, uniqueValues);
            }

            Span<string> normalizedValues = new string[uniqueValues.Count];
            int i = 0;
            foreach (string value in uniqueValues)
            {
                normalizedValues[i++] = NormalizeIfNeeded(value, ignoreCase);
            }
            Debug.Assert(i == normalizedValues.Length);

            // Aho-Corasick's ctor expects values to be sorted by length.
            normalizedValues.Sort(static (a, b) => a.Length.CompareTo(b.Length));

            // We may not end up choosing Aho-Corasick as the implementation, but it has a nice property of
            // finding all the unreachable values during the construction stage, so we build the trie early.
            HashSet<string>? unreachableValues = null;
            var ahoCorasickBuilder = new AhoCorasickBuilder(normalizedValues, ignoreCase, ref unreachableValues);

            if (unreachableValues is not null)
            {
                // Some values are exact prefixes of other values.
                // Exclude those values now to reduce the number of buckets and make verification steps cheaper during searching.
                normalizedValues = RemoveUnreachableValues(normalizedValues, unreachableValues);
            }

            SearchValues<string> searchValues = CreateFromNormalizedValues(normalizedValues, uniqueValues, ignoreCase, ref ahoCorasickBuilder);
            ahoCorasickBuilder.Dispose();
            return searchValues;

            static string NormalizeIfNeeded(string value, bool ignoreCase)
            {
                if (ignoreCase && value.AsSpan().ContainsAnyExcept(s_allAsciiExceptLowercase))
                {
                    string upperCase = string.FastAllocateString(value.Length);
                    int charsWritten = Ordinal.ToUpperOrdinal(value, new Span<char>(ref upperCase.GetRawStringData(), upperCase.Length));
                    Debug.Assert(charsWritten == upperCase.Length);
                    value = upperCase;
                }

                return value;
            }

            static Span<string> RemoveUnreachableValues(Span<string> values, HashSet<string> unreachableValues)
            {
                int newCount = 0;
                foreach (string value in values)
                {
                    if (!unreachableValues.Contains(value))
                    {
                        values[newCount++] = value;
                    }
                }

                Debug.Assert(newCount <= values.Length - unreachableValues.Count);
                Debug.Assert(newCount > 0);

                return values.Slice(0, newCount);
            }
        }

        private static SearchValues<string> CreateFromNormalizedValues(
            ReadOnlySpan<string> values,
            HashSet<string> uniqueValues,
            bool ignoreCase,
            ref AhoCorasickBuilder ahoCorasickBuilder)
        {
            AnalyzeValues(values, ref ignoreCase, out bool allAscii, out bool asciiLettersOnly, out bool nonAsciiAffectedByCaseConversion, out int minLength);

            if (values.Length == 1)
            {
                // We may reach this if we've removed unreachable values and ended up with only 1 remaining.
                return CreateForSingleValue(values[0], uniqueValues, ignoreCase, allAscii, asciiLettersOnly);
            }

            // SharpOS cut: the Teddy branch (Ssse3/AdvSimd) — see the header.
            _ = minLength;

            // Fall back to Aho-Corasick for all other multi-value sets.
            AhoCorasick ahoCorasick = ahoCorasickBuilder.Build();

            if (!ignoreCase)
            {
                return PickAhoCorasickImplementation<CaseSensitive>(ahoCorasick, uniqueValues);
            }

            if (nonAsciiAffectedByCaseConversion)
            {
                if (ContainsIncompleteSurrogatePairs(values))
                {
                    // Aho-Corasick can't deal with the matching semantics of standalone surrogate code units.
                    // We will use a slow but correct O(n * m) fallback implementation.
                    return new MultiStringIgnoreCaseSearchValuesFallback(uniqueValues);
                }

                return PickAhoCorasickImplementation<CaseInsensitiveUnicode>(ahoCorasick, uniqueValues);
            }

            if (asciiLettersOnly)
            {
                return PickAhoCorasickImplementation<CaseInsensitiveAsciiLetters>(ahoCorasick, uniqueValues);
            }

            return PickAhoCorasickImplementation<CaseInsensitiveAscii>(ahoCorasick, uniqueValues);

            static SearchValues<string> PickAhoCorasickImplementation<TCaseSensitivity>(AhoCorasick ahoCorasick, HashSet<string> uniqueValues)
                where TCaseSensitivity : struct, ICaseSensitivity
            {
                return ahoCorasick.ShouldUseAsciiFastScan
                    ? new StringSearchValuesAhoCorasick<TCaseSensitivity, AhoCorasick.IndexOfAnyAsciiFastScan>(ahoCorasick, uniqueValues)
                    : new StringSearchValuesAhoCorasick<TCaseSensitivity, AhoCorasick.NoFastScan>(ahoCorasick, uniqueValues);
            }
        }

        // SharpOS cut: TryGetTeddyAcceleratedValues, PickTeddyImplementation,
        // TryGenerateAllCasePermutationsForPrefixes — Teddy only.

        private static SearchValues<string> CreateForSingleValue(
            string value,
            HashSet<string>? uniqueValues,
            bool ignoreCase,
            bool allAscii,
            bool asciiLettersOnly)
        {
            // We make use of optimizations that may overflow on 32bit systems for long values.
            int maxLength = IntPtr.Size == 4 ? 1_000_000_000 : int.MaxValue;

            // SharpOS cut: the ThreeChars branch (Vector128) — see the header.
            _ = maxLength;
            _ = allAscii;
            _ = asciiLettersOnly;

            uniqueValues ??= new HashSet<string>(1, ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal) { value };

            return ignoreCase
                ? new SingleStringSearchValuesFallback<SearchValues.TrueConst>(value, uniqueValues)
                : new SingleStringSearchValuesFallback<SearchValues.FalseConst>(value, uniqueValues);
        }

        // SharpOS cut: TryCreateSingleValuesThreeChars, CreateSingleValuesThreeChars — ThreeChars only.

        private static void AnalyzeValues(
            ReadOnlySpan<string> values,
            ref bool ignoreCase,
            out bool allAscii,
            out bool asciiLettersOnly,
            out bool nonAsciiAffectedByCaseConversion,
            out int minLength)
        {
            allAscii = true;
            asciiLettersOnly = true;
            minLength = int.MaxValue;

            foreach (string value in values)
            {
                allAscii = allAscii && Ascii.IsValid(value);
                asciiLettersOnly = asciiLettersOnly && !value.AsSpan().ContainsAnyExcept(s_asciiLetters);
                minLength = Math.Min(minLength, value.Length);
            }

            // Potential optimization: Not all characters participate in Unicode case conversion.
            // If we can determine that none of the non-ASCII characters do, we can make searching faster
            // by using the same paths as we do for ASCII-only values.
            nonAsciiAffectedByCaseConversion = ignoreCase && !allAscii;

            // If all the characters in values are unaffected by casing, we can avoid the ignoreCase overhead.
            if (ignoreCase && !nonAsciiAffectedByCaseConversion && !asciiLettersOnly)
            {
                ignoreCase = false;

                foreach (string value in values)
                {
                    if (value.AsSpan().ContainsAny(s_asciiLetters))
                    {
                        ignoreCase = true;
                        break;
                    }
                }
            }
        }

        private static bool ContainsIncompleteSurrogatePairs(ReadOnlySpan<string> values)
        {
            foreach (string value in values)
            {
                int i = value.AsSpan().IndexOfAnyInRange(CharUnicodeInfo.HIGH_SURROGATE_START, CharUnicodeInfo.LOW_SURROGATE_END);
                if (i < 0)
                {
                    continue;
                }

                for (; (uint)i < (uint)value.Length; i++)
                {
                    if (char.IsHighSurrogate(value[i]))
                    {
                        if ((uint)(i + 1) >= (uint)value.Length || !char.IsLowSurrogate(value[i + 1]))
                        {
                            // High surrogate not followed by a low surrogate.
                            return true;
                        }

                        i++;
                    }
                    else if (char.IsLowSurrogate(value[i]))
                    {
                        // Low surrogate not preceded by a high surrogate.
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
