namespace SharpOS.Std.NoRuntime
{
    // Строковые преобразования, возвращающие новый string.
    // Все используют StringRuntime.FastAllocateString → работают в приложениях
    // всегда, в ядре — после KernelHeap.Init.

    internal static unsafe class StringTransforms
    {
        public static string Substring(string str, int startIndex)
        {
            if (str == null || startIndex < 0 || startIndex > str.Length)
                return string.Empty;

            return Substring(str, startIndex, str.Length - startIndex);
        }

        public static string Substring(string str, int startIndex, int length)
        {
            if (str == null || startIndex < 0 || length < 0 || startIndex + length > str.Length)
                return string.Empty;

            if (length == 0)
                return string.Empty;

            if (startIndex == 0 && length == str.Length)
                return str;

            string result = StringRuntime.FastAllocateString(length);
            if (result.Length != length)
                return result;

            fixed (char* dst = &result.GetPinnableReference())
            {
                for (int i = 0; i < length; i++)
                    dst[i] = str[startIndex + i];
            }

            return result;
        }

        public static string Trim(string str)
        {
            if (str == null)
                return string.Empty;

            int len = str.Length;
            int start = 0;
            while (start < len && CharHelpers.IsWhiteSpace(str[start]))
                start++;

            int end = len - 1;
            while (end >= start && CharHelpers.IsWhiteSpace(str[end]))
                end--;

            int newLen = end - start + 1;
            if (newLen == len)
                return str;

            return Substring(str, start, newLen);
        }

        public static string TrimStart(string str)
        {
            if (str == null)
                return string.Empty;

            int len = str.Length;
            int start = 0;
            while (start < len && CharHelpers.IsWhiteSpace(str[start]))
                start++;

            if (start == 0)
                return str;

            return Substring(str, start, len - start);
        }

        public static string TrimEnd(string str)
        {
            if (str == null)
                return string.Empty;

            int len = str.Length;
            int end = len - 1;
            while (end >= 0 && CharHelpers.IsWhiteSpace(str[end]))
                end--;

            int newLen = end + 1;
            if (newLen == len)
                return str;

            return Substring(str, 0, newLen);
        }

        public static string Replace(string str, char oldChar, char newChar)
        {
            if (str == null)
                return string.Empty;

            int len = str.Length;
            if (len == 0 || oldChar == newChar)
                return str;

            // Fast path: проверяем что хоть один символ есть.
            int firstHit = -1;
            for (int i = 0; i < len; i++)
            {
                if (str[i] == oldChar)
                {
                    firstHit = i;
                    break;
                }
            }
            if (firstHit < 0)
                return str;

            string result = StringRuntime.FastAllocateString(len);
            if (result.Length != len)
                return result;

            fixed (char* dst = &result.GetPinnableReference())
            {
                for (int i = 0; i < len; i++)
                {
                    char c = str[i];
                    dst[i] = c == oldChar ? newChar : c;
                }
            }

            return result;
        }

        public static string Replace(string str, string oldValue, string newValue)
        {
            if (str == null || oldValue == null || oldValue.Length == 0)
                return str == null ? string.Empty : str;

            if (newValue == null)
                newValue = string.Empty;

            int strLen = str.Length;
            int oldLen = oldValue.Length;
            int newLen = newValue.Length;

            // Первый проход: считаем количество вхождений и суммарную длину результата.
            int occurrences = 0;
            int scan = 0;
            while (scan <= strLen - oldLen)
            {
                bool match = true;
                for (int j = 0; j < oldLen; j++)
                {
                    if (str[scan + j] != oldValue[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                {
                    occurrences++;
                    scan += oldLen;
                }
                else
                {
                    scan++;
                }
            }

            if (occurrences == 0)
                return str;

            int resultLen = strLen + occurrences * (newLen - oldLen);
            if (resultLen < 0)
                return string.Empty;

            if (resultLen == 0)
                return string.Empty;

            string result = StringRuntime.FastAllocateString(resultLen);
            if (result.Length != resultLen)
                return result;

            // Второй проход: заполняем результат.
            fixed (char* dst = &result.GetPinnableReference())
            {
                int src = 0;
                int dstIdx = 0;
                while (src < strLen)
                {
                    bool match = false;
                    if (src <= strLen - oldLen)
                    {
                        match = true;
                        for (int j = 0; j < oldLen; j++)
                        {
                            if (str[src + j] != oldValue[j])
                            {
                                match = false;
                                break;
                            }
                        }
                    }
                    if (match)
                    {
                        for (int j = 0; j < newLen; j++)
                            dst[dstIdx++] = newValue[j];
                        src += oldLen;
                    }
                    else
                    {
                        dst[dstIdx++] = str[src++];
                    }
                }
            }

            return result;
        }

        public static string ToUpperInvariant(string str)
        {
            if (str == null)
                return string.Empty;

            int len = str.Length;
            if (len == 0)
                return str;

            string result = StringRuntime.FastAllocateString(len);
            if (result.Length != len)
                return result;

            fixed (char* dst = &result.GetPinnableReference())
            {
                for (int i = 0; i < len; i++)
                    dst[i] = CharHelpers.ToUpperInvariant(str[i]);
            }

            return result;
        }

        public static string ToLowerInvariant(string str)
        {
            if (str == null)
                return string.Empty;

            int len = str.Length;
            if (len == 0)
                return str;

            string result = StringRuntime.FastAllocateString(len);
            if (result.Length != len)
                return result;

            fixed (char* dst = &result.GetPinnableReference())
            {
                for (int i = 0; i < len; i++)
                    dst[i] = CharHelpers.ToLowerInvariant(str[i]);
            }

            return result;
        }
    }
}
