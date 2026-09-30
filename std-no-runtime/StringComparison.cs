// System.String — Compare / CompareOrdinal / CompareTo / Equals overloads.
// Ported from dotnet/runtime src/System/String.Comparison.cs, trimmed to
// Ordinal / OrdinalIgnoreCase comparison only (no Globalization, no
// CultureInfo). Matches the documented invariant that we ship Ordinal-only
// string surface (CoreCLR brings full culture-aware variants in hosted-tier).
//
// All methods avoid allocation; the comparison work is char-by-char.

namespace System
{
    public enum StringComparison
    {
        CurrentCulture = 0,
        CurrentCultureIgnoreCase = 1,
        InvariantCulture = 2,
        InvariantCultureIgnoreCase = 3,
        Ordinal = 4,
        OrdinalIgnoreCase = 5,
    }

    public sealed unsafe partial class String
    {
        // ---- Compare (static) ----

        public static int Compare(string strA, string strB)
            => CompareOrdinal(strA, strB);

        public static int Compare(string strA, string strB, bool ignoreCase)
            => ignoreCase
                ? CompareOrdinalIgnoreCaseInternal(strA, strB)
                : CompareOrdinal(strA, strB);

        public static int Compare(string strA, string strB, StringComparison comparisonType)
        {
            switch (comparisonType)
            {
                case StringComparison.Ordinal:
                case StringComparison.CurrentCulture:
                case StringComparison.InvariantCulture:
                    return CompareOrdinal(strA, strB);
                case StringComparison.OrdinalIgnoreCase:
                case StringComparison.CurrentCultureIgnoreCase:
                case StringComparison.InvariantCultureIgnoreCase:
                    return CompareOrdinalIgnoreCaseInternal(strA, strB);
                default:
                    return CompareOrdinal(strA, strB);
            }
        }

        public static int Compare(
            string strA, int indexA,
            string strB, int indexB,
            int length)
        {
            return CompareOrdinalRange(strA, indexA, strB, indexB, length, ignoreCase: false);
        }

        public static int Compare(
            string strA, int indexA,
            string strB, int indexB,
            int length, bool ignoreCase)
        {
            return CompareOrdinalRange(strA, indexA, strB, indexB, length, ignoreCase);
        }

        public static int Compare(
            string strA, int indexA,
            string strB, int indexB,
            int length, StringComparison comparisonType)
        {
            bool ignoreCase = comparisonType == StringComparison.OrdinalIgnoreCase
                || comparisonType == StringComparison.CurrentCultureIgnoreCase
                || comparisonType == StringComparison.InvariantCultureIgnoreCase;
            return CompareOrdinalRange(strA, indexA, strB, indexB, length, ignoreCase);
        }

        // ---- CompareOrdinal (static) ----

        public static int CompareOrdinal(string strA, string strB)
        {
            if ((object)strA == (object)strB) return 0;
            if (strA == null) return -1;
            if (strB == null) return 1;

            int minLen = strA.Length < strB.Length ? strA.Length : strB.Length;
            for (int i = 0; i < minLen; i++)
            {
                int diff = strA[i] - strB[i];
                if (diff != 0) return diff;
            }
            return strA.Length - strB.Length;
        }

        public static int CompareOrdinal(
            string strA, int indexA,
            string strB, int indexB,
            int length)
        {
            return CompareOrdinalRange(strA, indexA, strB, indexB, length, ignoreCase: false);
        }

        // ---- CompareTo (instance) ----

        public int CompareTo(string strB) => CompareOrdinal(this, strB);

        public int CompareTo(object value)
        {
            if (value == null) return 1;
            if (value is string s) return CompareOrdinal(this, s);
            Halt();
            return 0;
        }

        // ---- Equals overloads ----

        public bool Equals(string value)
        {
            if ((object)value == (object)this) return true;
            if (value == null) return false;
            if (value.Length != this.Length) return false;
            for (int i = 0; i < this.Length; i++)
                if (this[i] != value[i]) return false;
            return true;
        }

        public bool Equals(string value, StringComparison comparisonType)
        {
            bool ignoreCase = comparisonType == StringComparison.OrdinalIgnoreCase
                || comparisonType == StringComparison.CurrentCultureIgnoreCase
                || comparisonType == StringComparison.InvariantCultureIgnoreCase;

            if ((object)value == (object)this) return true;
            if (value == null) return false;
            if (value.Length != this.Length) return false;

            for (int i = 0; i < this.Length; i++)
            {
                char a = this[i];
                char b = value[i];
                if (ignoreCase)
                {
                    if (AsciiToLower(a) != AsciiToLower(b)) return false;
                }
                else
                {
                    if (a != b) return false;
                }
            }
            return true;
        }

        public static bool Equals(string a, string b)
        {
            if ((object)a == (object)b) return true;
            if (a == null || b == null) return false;
            return a.Equals(b);
        }

        public static bool Equals(string a, string b, StringComparison comparisonType)
        {
            if ((object)a == (object)b) return true;
            if (a == null || b == null) return false;
            return a.Equals(b, comparisonType);
        }

        public override bool Equals(object obj)
        {
            if (!(obj is string s)) return false;
            return this.Equals(s);
        }

        public override int GetHashCode()
        {
            // FNV-1a-style hash, sufficient for hashing — no need for the
            // full Marvin32 collision-resistant variant from BCL.
            uint hash = 2166136261u;
            for (int i = 0; i < Length; i++)
                hash = (hash ^ this[i]) * 16777619u;
            return (int)hash;
        }

        // ---- Internals ----

        private static int CompareOrdinalIgnoreCaseInternal(string strA, string strB)
        {
            if ((object)strA == (object)strB) return 0;
            if (strA == null) return -1;
            if (strB == null) return 1;

            int minLen = strA.Length < strB.Length ? strA.Length : strB.Length;
            for (int i = 0; i < minLen; i++)
            {
                char a = AsciiToLower(strA[i]);
                char b = AsciiToLower(strB[i]);
                int diff = a - b;
                if (diff != 0) return diff;
            }
            return strA.Length - strB.Length;
        }

        private static int CompareOrdinalRange(
            string strA, int indexA,
            string strB, int indexB,
            int length, bool ignoreCase)
        {
            if (length <= 0) return 0;
            if (strA == null && strB == null) return 0;
            if (strA == null) return -1;
            if (strB == null) return 1;

            int lenA = strA.Length - indexA;
            int lenB = strB.Length - indexB;
            if (lenA < 0 || lenB < 0) { Halt(); return 0; }

            int compareLenA = lenA < length ? lenA : length;
            int compareLenB = lenB < length ? lenB : length;
            int compareLen = compareLenA < compareLenB ? compareLenA : compareLenB;

            for (int i = 0; i < compareLen; i++)
            {
                char a = strA[indexA + i];
                char b = strB[indexB + i];
                if (ignoreCase) { a = AsciiToLower(a); b = AsciiToLower(b); }
                int diff = a - b;
                if (diff != 0) return diff;
            }

            // If we exhausted the comparison length without difference,
            // strings are equal up to that length.
            if (compareLenA == compareLenB) return 0;
            return compareLenA - compareLenB;
        }

        private static char AsciiToLower(char c)
        {
            if (c >= 'A' && c <= 'Z') return (char)(c + ('a' - 'A'));
            return c;
        }
    }
}
