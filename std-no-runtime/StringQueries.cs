namespace SharpOS.Std.NoRuntime
{
    // Чистые query-функции над string: IndexOf, LastIndexOf, Contains,
    // StartsWith, EndsWith, IsNullOrEmpty, IsNullOrWhiteSpace.
    // Ничего не аллоцируют, работают в любом контексте.

    internal static unsafe class StringQueries
    {
        public static int IndexOf(string str, char value)
        {
            return IndexOf(str, value, 0);
        }

        public static int IndexOf(string str, char value, int startIndex)
        {
            if (str == null || startIndex < 0)
                return -1;

            int len = str.Length;
            for (int i = startIndex; i < len; i++)
            {
                if (str[i] == value)
                    return i;
            }
            return -1;
        }

        public static int IndexOf(string str, string value)
        {
            return IndexOf(str, value, 0);
        }

        public static int IndexOf(string str, string value, int startIndex)
        {
            if (str == null || value == null || startIndex < 0)
                return -1;

            int strLen = str.Length;
            int valLen = value.Length;
            if (valLen == 0)
                return startIndex <= strLen ? startIndex : -1;
            if (valLen > strLen)
                return -1;

            int last = strLen - valLen;
            for (int i = startIndex; i <= last; i++)
            {
                bool match = true;
                for (int j = 0; j < valLen; j++)
                {
                    if (str[i + j] != value[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                    return i;
            }
            return -1;
        }

        public static int LastIndexOf(string str, char value)
        {
            if (str == null)
                return -1;

            for (int i = str.Length - 1; i >= 0; i--)
            {
                if (str[i] == value)
                    return i;
            }
            return -1;
        }

        public static int LastIndexOf(string str, string value)
        {
            if (str == null || value == null)
                return -1;

            int strLen = str.Length;
            int valLen = value.Length;
            if (valLen == 0)
                return strLen;
            if (valLen > strLen)
                return -1;

            for (int i = strLen - valLen; i >= 0; i--)
            {
                bool match = true;
                for (int j = 0; j < valLen; j++)
                {
                    if (str[i + j] != value[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                    return i;
            }
            return -1;
        }

        public static bool Contains(string str, char value)
        {
            return IndexOf(str, value, 0) >= 0;
        }

        public static bool Contains(string str, string value)
        {
            return IndexOf(str, value, 0) >= 0;
        }

        // Ported from dotnet/runtime v8.0.27 String.Comparison.cs StartsWith(string,
        // StringComparison). Cuts: the culture-aware branches (CurrentCulture /
        // InvariantCulture) fall back to the ordinal path — std is Ordinal-only by
        // design, see the header of this file.
        public static bool StartsWith(string str, string value, System.StringComparison comparisonType)
        {
            if (str == null || value == null)
                return false;
            if (value.Length == 0)
                return true;
            if (value.Length > str.Length)
                return false;

            bool ignoreCase = comparisonType == System.StringComparison.OrdinalIgnoreCase
                || comparisonType == System.StringComparison.CurrentCultureIgnoreCase
                || comparisonType == System.StringComparison.InvariantCultureIgnoreCase;

            for (int i = 0; i < value.Length; i++)
            {
                char a = str[i];
                char b = value[i];
                if (a == b)
                    continue;
                if (!ignoreCase)
                    return false;
                if (CharHelpers.ToUpperInvariant(a) != CharHelpers.ToUpperInvariant(b))
                    return false;
            }
            return true;
        }

        public static bool StartsWith(string str, string value)
        {
            if (str == null || value == null)
                return false;

            int valLen = value.Length;
            if (valLen == 0)
                return true;
            if (valLen > str.Length)
                return false;

            for (int i = 0; i < valLen; i++)
            {
                if (str[i] != value[i])
                    return false;
            }
            return true;
        }

        /// <summary>First position holding any of the given characters, or -1.</summary>
        public static int IndexOfAny(string str, char[] anyOf)
        {
            if (str == null || anyOf == null)
                return -1;

            for (int i = 0; i < str.Length; i++)
            {
                char c = str[i];
                for (int j = 0; j < anyOf.Length; j++)
                {
                    if (c == anyOf[j])
                        return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// Last position of <paramref name="value"/> at or before
        /// <paramref name="startIndex"/>, searching backwards, or -1.
        /// </summary>
        public static int LastIndexOf(string str, char value, int startIndex)
        {
            if (str == null)
                return -1;
            if (startIndex >= str.Length)
                startIndex = str.Length - 1;

            for (int i = startIndex; i >= 0; i--)
            {
                if (str[i] == value)
                    return i;
            }
            return -1;
        }

        // The mirror of StartsWith(string, string, StringComparison) above, and
        // absent for as long as it was: the pair is asymmetric in BCL-facing
        // code only by accident. Same cut — culture-aware branches fall back to
        // the ordinal path.
        public static bool EndsWith(string str, string value, System.StringComparison comparisonType)
        {
            if (str == null || value == null)
                return false;
            if (value.Length == 0)
                return true;
            if (value.Length > str.Length)
                return false;

            bool ignoreCase = comparisonType == System.StringComparison.OrdinalIgnoreCase
                || comparisonType == System.StringComparison.CurrentCultureIgnoreCase
                || comparisonType == System.StringComparison.InvariantCultureIgnoreCase;

            int start = str.Length - value.Length;
            for (int i = 0; i < value.Length; i++)
            {
                char a = str[start + i];
                char b = value[i];
                if (a == b)
                    continue;
                if (!ignoreCase)
                    return false;
                if (CharHelpers.ToUpperInvariant(a) != CharHelpers.ToUpperInvariant(b))
                    return false;
            }
            return true;
        }

        public static bool EndsWith(string str, string value)
        {
            if (str == null || value == null)
                return false;

            int strLen = str.Length;
            int valLen = value.Length;
            if (valLen == 0)
                return true;
            if (valLen > strLen)
                return false;

            int offset = strLen - valLen;
            for (int i = 0; i < valLen; i++)
            {
                if (str[offset + i] != value[i])
                    return false;
            }
            return true;
        }

        public static bool IsNullOrEmpty(string str)
        {
            return str == null || str.Length == 0;
        }

        public static bool IsNullOrWhiteSpace(string str)
        {
            if (str == null)
                return true;

            int len = str.Length;
            for (int i = 0; i < len; i++)
            {
                if (!CharHelpers.IsWhiteSpace(str[i]))
                    return false;
            }
            return true;
        }
    }
}
