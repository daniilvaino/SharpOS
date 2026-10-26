// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from dotnet/runtime main (.NET 10, the fork dotnet-runtime-sharpos):
//   src/libraries/System.Private.CoreLib/src/System/Globalization/StringInfo.cs
//   src/libraries/System.Private.CoreLib/src/System/Globalization/TextElementEnumerator.cs
// Text elements are extended grapheme clusters (UAX#29), segmented by the
// ported TextSegmentationUtility over the ported Unicode tables — no ICU, as
// in the BCL.
//
// SharpOS changes (marked inline): ThrowHelper calls written out as throws;
// ParseCombiningCharacters collects into a List<int> (ValueListBuilder is
// CoreLib-internal and not ported).

using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Unicode;

namespace System.Globalization
{
    /// <summary>
    /// This class defines behaviors specific to a writing system.
    /// A writing system is the collection of scripts and orthographic rules
    /// required to represent a language as text.
    /// </summary>
    public class StringInfo
    {
        private string _str;

        private int[]? _indexes;

        public StringInfo() : this(string.Empty)
        {
        }

        public StringInfo(string value)
        {
            this.String = value;
        }

        public override bool Equals([NotNullWhen(true)] object? value)
        {
            return value is StringInfo otherStringInfo
                && _str.Equals(otherStringInfo._str);
        }

        public override int GetHashCode() => _str.GetHashCode();

        /// <summary>
        /// Our zero-based array of index values into the string. Initialize if
        /// our private array is not yet, in fact, initialized.
        /// </summary>
        private int[]? Indexes
        {
            get
            {
                if (_indexes == null && String.Length > 0)
                {
                    _indexes = ParseCombiningCharacters(String);
                }

                return _indexes;
            }
        }

        public string String
        {
            get => _str;
            [MemberNotNull(nameof(_str))]
            set
            {
                ArgumentNullException.ThrowIfNull(value, nameof(value));
                _str = value;
                _indexes = null;
            }
        }

        public int LengthInTextElements => Indexes?.Length ?? 0;

        public string SubstringByTextElements(int startingTextElement)
        {
            return SubstringByTextElements(startingTextElement, (Indexes?.Length ?? 0) - startingTextElement);
        }

        public string SubstringByTextElements(int startingTextElement, int lengthInTextElements)
        {
            int[] indexes = Indexes ?? Array.Empty<int>();

            if ((uint)startingTextElement >= (uint)indexes.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(startingTextElement), startingTextElement, "Specified argument was out of the range of valid values.");
            }
            if ((uint)lengthInTextElements > (uint)(indexes.Length - startingTextElement))
            {
                throw new ArgumentOutOfRangeException(nameof(lengthInTextElements), lengthInTextElements, "Specified argument was out of the range of valid values.");
            }

            int start = indexes[startingTextElement];
            int end = String.Length; // assume reading to end of the string unless the caller told us to stop early

            if ((uint)(startingTextElement + lengthInTextElements) < (uint)indexes.Length)
            {
                end = indexes[startingTextElement + lengthInTextElements];
            }

            return String.Substring(start, end - start); // SharpOS: String[start..end]
        }

        /// <summary>Returns the first text element (extended grapheme cluster) that occurs in the input string.</summary>
        public static string GetNextTextElement(string str) => GetNextTextElement(str, 0);

        /// <summary>Returns the first text element (extended grapheme cluster) at <paramref name="index"/>.</summary>
        public static string GetNextTextElement(string str, int index)
        {
            int nextTextElementLength = GetNextTextElementLength(str, index);
            return str.Substring(index, nextTextElementLength);
        }

        /// <summary>The length (in chars) of the first text element in the input string.</summary>
        public static int GetNextTextElementLength(string str) => GetNextTextElementLength(str, 0);

        /// <summary>The length (in chars) of the first text element at <paramref name="index"/>.</summary>
        public static int GetNextTextElementLength(string str, int index)
        {
            if (str is null)
            {
                throw new ArgumentNullException(nameof(str)); // SharpOS: ThrowHelper
            }
            if ((uint)index > (uint)str.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index), "Index was out of range. Must be non-negative and less than or equal to the size of the collection."); // SharpOS: ThrowHelper
            }

            return GetNextTextElementLength(str.AsSpan(index));
        }

        /// <summary>The length (in chars) of the first text element in the input span.</summary>
        public static int GetNextTextElementLength(ReadOnlySpan<char> str) => TextSegmentationUtility.GetLengthOfFirstUtf16ExtendedGraphemeCluster(str);

        public static TextElementEnumerator GetTextElementEnumerator(string str) => GetTextElementEnumerator(str, 0);

        public static TextElementEnumerator GetTextElementEnumerator(string str, int index)
        {
            if (str is null)
            {
                throw new ArgumentNullException(nameof(str)); // SharpOS: ThrowHelper
            }
            if ((uint)index > (uint)str.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index), "Index was out of range. Must be non-negative and less than or equal to the size of the collection."); // SharpOS: ThrowHelper
            }

            return new TextElementEnumerator(str, index);
        }

        /// <summary>
        /// Returns the indices of each base character or properly formed surrogate
        /// pair within the str: each index is the beginning of a text element.
        /// </summary>
        public static int[] ParseCombiningCharacters(string str)
        {
            if (str is null)
            {
                throw new ArgumentNullException(nameof(str)); // SharpOS: ThrowHelper
            }

            var builder = new List<int>(); // SharpOS: ValueListBuilder<int> over stackalloc int[64]
            ReadOnlySpan<char> remaining = str;

            while (!remaining.IsEmpty)
            {
                builder.Add(str.Length - remaining.Length); // a new extended grapheme cluster begins at this offset
                remaining = remaining.Slice(GetNextTextElementLength(remaining)); // consume this cluster
            }

            return builder.ToArray();
        }
    }

    public class TextElementEnumerator : IEnumerator
    {
        private readonly string _str;
        private readonly int _strStartIndex; // where in _str the enumeration should begin

        private int _currentTextElementOffset;
        private int _currentTextElementLength;
        private string? _currentTextElementSubstr;

        internal TextElementEnumerator(string str, int startIndex)
        {
            Debug.Assert(str != null, "TextElementEnumerator(): str != null");
            Debug.Assert(startIndex >= 0 && startIndex <= str.Length, "TextElementEnumerator(): startIndex >= 0 && startIndex <= str.Length");

            _str = str;
            _strStartIndex = startIndex;

            Reset();
        }

        public bool MoveNext()
        {
            _currentTextElementSubstr = null; // clear any cached substr

            int newOffset = _currentTextElementOffset + _currentTextElementLength;
            _currentTextElementOffset = newOffset; // advance
            _currentTextElementLength = 0; // prevent future calls to MoveNext() or get_Current from succeeding if we've hit end of data

            if (newOffset >= _str.Length)
            {
                return false; // reached the end of the data
            }

            _currentTextElementLength = TextSegmentationUtility.GetLengthOfFirstUtf16ExtendedGraphemeCluster(_str.AsSpan(newOffset));
            return true;
        }

        public object Current => GetTextElement();

        public string GetTextElement()
        {
            // Returned the cached substr if we've already generated it.
            // Otherwise perform the substr operation now.

            string? currentSubstr = _currentTextElementSubstr;
            if (currentSubstr is null)
            {
                if (_currentTextElementOffset >= _str.Length)
                {
                    throw new InvalidOperationException("Enumeration has either not started or has already finished.");
                }

                currentSubstr = _str.Substring(_currentTextElementOffset, _currentTextElementLength);
                _currentTextElementSubstr = currentSubstr;
            }

            return currentSubstr;
        }

        public int ElementIndex
        {
            get
            {
                if (_currentTextElementOffset >= _str.Length)
                {
                    throw new InvalidOperationException("Enumeration has either not started or has already finished.");
                }

                return _currentTextElementOffset - _strStartIndex;
            }
        }

        public void Reset()
        {
            // These first two fields are set to intentionally out-of-range values.
            // They'll be fixed up once the enumerator starts.

            _currentTextElementOffset = _str.Length;
            _currentTextElementLength = _strStartIndex - _str.Length;
            _currentTextElementSubstr = null;
        }
    }
}
