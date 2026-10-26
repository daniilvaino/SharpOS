// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from dotnet/runtime src/libraries/System.Private.CoreLib/src/System/SearchValues/SearchValues.cs
// (.NET 10, MIT). Public surface for byte and char is upstream's; the choice of implementation is cut
// down to the ones that are ported:
//   - SharpOS cut: the specialized implementations upstream picks first (Any1..Any5, the Packed
//     variants, RangeByte/RangeChar, AsciiByte/AsciiChar, Probabilistic*). They are all thin shells
//     over SpanHelpers/PackedSpanHelpers/IndexOfAnyAsciiSearcher vector kernels that std does not
//     have. What remains is upstream's own final fallback for each type - AnyByteSearchValues
//     (BitVector256 lookup) and BitmapCharSearchValues ("This will also match ASCII values when
//     IndexOfAnyAsciiSearcher is not supported") - which is what the BCL itself selects on hardware
//     without vector support. Results are identical; only speed differs.
//   - SharpOS cut: Create(ReadOnlySpan<string>, StringComparison) - SearchValues<string> needs the
//     Teddy/Aho-Corasick/Rabin-Karp machinery (StringSearchValues), not ported.
//   - SharpOS cut: TryGetSingleRange (generic math: INumber<T>, IMinMaxValue<T> - not in std) and
//     ShuffleNativeModified (Ssse3) - used only by the cut implementations.

namespace System.Buffers
{
    /// <summary>
    /// Provides a set of initialization methods for instances of the <see cref="SearchValues{T}"/> class.
    /// </summary>
    /// <remarks>
    /// SearchValues are optimized for situations where the same set of values is frequently used for searching at runtime.
    /// </remarks>
    public static class SearchValues
    {
        /// <summary>
        /// Creates an optimized representation of <paramref name="values"/> used for efficient searching.
        /// </summary>
        /// <param name="values">The set of values.</param>
        /// <returns>The optimized representation of <paramref name="values"/> used for efficient searching.</returns>
        public static SearchValues<byte> Create(params ReadOnlySpan<byte> values)
        {
            if (values.IsEmpty)
            {
                return new EmptySearchValues<byte>();
            }

            // SharpOS cut: Any1/Range/AsciiByte/Any2..Any5 selection - see the file header.
            return new AnyByteSearchValues(values);
        }

        /// <summary>
        /// Creates an optimized representation of <paramref name="values"/> used for efficient searching.
        /// </summary>
        /// <param name="values">The set of values.</param>
        /// <returns>The optimized representation of <paramref name="values"/> used for efficient searching.</returns>
        public static SearchValues<char> Create(params ReadOnlySpan<char> values)
        {
            if (values.IsEmpty)
            {
                return new EmptySearchValues<char>();
            }

            // SharpOS: upstream gets maxInclusive from TryGetSingleRange (cut, generic math).
            char maxInclusive = char.MinValue;
            foreach (char value in values)
            {
                if (value > maxInclusive)
                {
                    maxInclusive = value;
                }
            }

            // SharpOS cut: Any1/Range/Any2/Any3/Ascii/Any4/Any5/Probabilistic selection - see the file header.
            // This will also match ASCII values when IndexOfAnyAsciiSearcher is not supported.
            return new BitmapCharSearchValues(values, maxInclusive);
        }

        internal interface IRuntimeConst
        {
            static abstract bool Value { get; }
        }

        internal readonly struct TrueConst : IRuntimeConst
        {
            public static bool Value => true;
        }

        internal readonly struct FalseConst : IRuntimeConst
        {
            public static bool Value => false;
        }
    }
}
