// System.String — Concat / Split / Join overloads. BCL groups these in
// String.Manipulation.cs + String.Split.cs; we keep them inline here as
// partial-class additions. All BCL-compat signatures + observable
// behaviour; internal implementation uses managed loops rather than the
// Span-optimised BCL fast paths. Acceptable for our workloads.
//
// Cuts vs BCL:
//  - StringSplitOptions enum + overloads that take it — partial support
//    (None only). Add TrimEntries / RemoveEmptyEntries when a caller
//    needs them.
//  - IFormatProvider / ICustomFormatter variants — no i18n in kernel.

using System.Collections.Generic;
using System.Text;

namespace System
{
    public sealed unsafe partial class String
    {
        // ---- Concat ----

        public static string Concat(string str0, string str1, string str2)
        {
            if (str0 == null) str0 = "";
            if (str1 == null) str1 = "";
            if (str2 == null) str2 = "";
            int totalLength = str0.Length + str1.Length + str2.Length;
            if (totalLength == 0) return "";

            string result = FastAllocateString(totalLength);
            fixed (char* dest = result)
            {
                int pos = 0;
                for (int i = 0; i < str0.Length; i++) dest[pos++] = str0[i];
                for (int i = 0; i < str1.Length; i++) dest[pos++] = str1[i];
                for (int i = 0; i < str2.Length; i++) dest[pos++] = str2[i];
            }
            return result;
        }

        public static string Concat(string str0, string str1, string str2, string str3)
        {
            if (str0 == null) str0 = "";
            if (str1 == null) str1 = "";
            if (str2 == null) str2 = "";
            if (str3 == null) str3 = "";
            int totalLength = str0.Length + str1.Length + str2.Length + str3.Length;
            if (totalLength == 0) return "";

            string result = FastAllocateString(totalLength);
            fixed (char* dest = result)
            {
                int pos = 0;
                for (int i = 0; i < str0.Length; i++) dest[pos++] = str0[i];
                for (int i = 0; i < str1.Length; i++) dest[pos++] = str1[i];
                for (int i = 0; i < str2.Length; i++) dest[pos++] = str2[i];
                for (int i = 0; i < str3.Length; i++) dest[pos++] = str3[i];
            }
            return result;
        }

        public static string Concat(params string[] values)
        {
            if (values == null || values.Length == 0) return "";

            int totalLength = 0;
            for (int i = 0; i < values.Length; i++)
                if (values[i] != null) totalLength += values[i].Length;
            if (totalLength == 0) return "";

            string result = FastAllocateString(totalLength);
            fixed (char* dest = result)
            {
                int pos = 0;
                for (int i = 0; i < values.Length; i++)
                {
                    string s = values[i];
                    if (s == null) continue;
                    for (int j = 0; j < s.Length; j++) dest[pos++] = s[j];
                }
            }
            return result;
        }

        public static string Concat(object arg0) => arg0 == null ? "" : arg0.ToString();

        public static string Concat(object arg0, object arg1)
            => Concat(arg0 == null ? "" : arg0.ToString(), arg1 == null ? "" : arg1.ToString());

        public static string Concat(object arg0, object arg1, object arg2)
            => Concat(
                arg0 == null ? "" : arg0.ToString(),
                arg1 == null ? "" : arg1.ToString(),
                arg2 == null ? "" : arg2.ToString());

        // (string)Empty literal inlined above — same object identity as
        // string.Empty once the cctor path gets fixed; see
        // nativeaot-nostdlib-limits.md §1 (ClassConstructorRunner).

        public static string Concat(params object[] args)
        {
            if (args == null || args.Length == 0) return "";
            string[] converted = new string[args.Length];
            for (int i = 0; i < args.Length; i++)
                converted[i] = args[i] == null ? "" : args[i].ToString();
            return Concat(converted);
        }

        // ---- Join ----

        public static string Join(string separator, string[] value)
        {
            if (value == null) return "";
            return Join(separator, value, 0, value.Length);
        }

        public static string Join(string separator, string[] value, int startIndex, int count)
        {
            if (value == null) { Halt(); return null; }
            if (startIndex < 0 || count < 0 || startIndex > value.Length - count) { Halt(); return null; }
            if (count == 0) return "";
            if (separator == null) separator = "";

            int sepLen = separator.Length;
            int totalLength = 0;
            for (int i = startIndex; i < startIndex + count; i++)
                if (value[i] != null) totalLength += value[i].Length;
            totalLength += sepLen * (count - 1);
            if (totalLength <= 0) return "";

            string result = FastAllocateString(totalLength);
            fixed (char* dest = result)
            {
                int pos = 0;
                for (int i = 0; i < count; i++)
                {
                    if (i > 0)
                        for (int k = 0; k < sepLen; k++) dest[pos++] = separator[k];
                    string s = value[startIndex + i];
                    if (s != null)
                        for (int k = 0; k < s.Length; k++) dest[pos++] = s[k];
                }
            }
            return result;
        }

        public static string Join(char separator, string[] value)
        {
            if (value == null) return "";
            return Join(separator, value, 0, value.Length);
        }

        public static string Join(char separator, string[] value, int startIndex, int count)
        {
            if (value == null) { Halt(); return null; }
            if (startIndex < 0 || count < 0 || startIndex > value.Length - count) { Halt(); return null; }
            if (count == 0) return "";

            int totalLength = 0;
            for (int i = startIndex; i < startIndex + count; i++)
                if (value[i] != null) totalLength += value[i].Length;
            totalLength += (count - 1);
            if (totalLength <= 0) return "";

            string result = FastAllocateString(totalLength);
            fixed (char* dest = result)
            {
                int pos = 0;
                for (int i = 0; i < count; i++)
                {
                    if (i > 0) dest[pos++] = separator;
                    string s = value[startIndex + i];
                    if (s != null)
                        for (int k = 0; k < s.Length; k++) dest[pos++] = s[k];
                }
            }
            return result;
        }

        public static string Join(string separator, params object[] values)
        {
            if (values == null || values.Length == 0) return "";
            string[] converted = new string[values.Length];
            for (int i = 0; i < values.Length; i++)
                converted[i] = values[i] == null ? "" : values[i].ToString();
            return Join(separator, converted, 0, converted.Length);
        }

        // ---- Split ----

        public string[] Split(char separator)
            => SplitInternal(separator, int.MaxValue);

        public string[] Split(char separator, int count)
        {
            if (count < 0) { Halt(); return null; }
            if (count <= 1 || Length == 0) return new string[] { this };
            return SplitInternal(separator, count);
        }

        public string[] Split(char separator, StringSplitOptions options)
            => ApplySplitOptions(SplitInternal(separator, int.MaxValue), options);

        public string[] Split(char separator, int count, StringSplitOptions options)
        {
            if (count < 0) { Halt(); return null; }
            if (count == 0 || Length == 0) return new string[0];
            return ApplySplitOptions(SplitInternal(separator, count), options);
        }

        public string[] Split(params char[] separator)
        {
            if (separator == null || separator.Length == 0)
                return new string[] { this };
            return SplitInternalMulti(separator, int.MaxValue);
        }

        public string[] Split(char[] separator, int count)
        {
            if (count < 0) { Halt(); return null; }
            if (count <= 1 || Length == 0) return new string[] { this };
            if (separator == null || separator.Length == 0)
                return new string[] { this };
            return SplitInternalMulti(separator, count);
        }

        public string[] Split(char[] separator, StringSplitOptions options)
        {
            if (separator == null || separator.Length == 0)
                return ApplySplitOptions(new string[] { this }, options);
            return ApplySplitOptions(SplitInternalMulti(separator, int.MaxValue), options);
        }

        public string[] Split(char[] separator, int count, StringSplitOptions options)
        {
            if (count < 0) { Halt(); return null; }
            if (count == 0 || Length == 0) return new string[0];
            if (separator == null || separator.Length == 0)
                return ApplySplitOptions(new string[] { this }, options);
            return ApplySplitOptions(SplitInternalMulti(separator, count), options);
        }

        public string[] Split(string separator)
        {
            if (separator == null || separator.Length == 0)
                return new string[] { this };
            return SplitInternalString(separator, int.MaxValue);
        }

        public string[] Split(string separator, StringSplitOptions options)
        {
            if (separator == null || separator.Length == 0)
                return ApplySplitOptions(new string[] { this }, options);
            return ApplySplitOptions(SplitInternalString(separator, int.MaxValue), options);
        }

        public string[] Split(string separator, int count, StringSplitOptions options)
        {
            if (count < 0) { Halt(); return null; }
            if (count == 0 || Length == 0) return new string[0];
            if (separator == null || separator.Length == 0)
                return ApplySplitOptions(new string[] { this }, options);
            return ApplySplitOptions(SplitInternalString(separator, count), options);
        }

        // Apply TrimEntries / RemoveEmptyEntries post-process. Single pass:
        // walk parts, optionally Trim, optionally skip empty, write into a
        // fresh array sized exactly to the output count.
        private static string[] ApplySplitOptions(string[] parts, StringSplitOptions options)
        {
            if (options == StringSplitOptions.None) return parts;

            bool trim = (options & StringSplitOptions.TrimEntries) != 0;
            bool removeEmpty = (options & StringSplitOptions.RemoveEmptyEntries) != 0;

            int outCount = 0;
            // First pass: count survivors.
            for (int i = 0; i < parts.Length; i++)
            {
                string s = parts[i];
                if (trim && s != null) s = s.Trim();
                if (removeEmpty && (s == null || s.Length == 0)) continue;
                outCount++;
            }

            string[] result = new string[outCount];
            int outIdx = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                string s = parts[i];
                if (trim && s != null) s = s.Trim();
                if (removeEmpty && (s == null || s.Length == 0)) continue;
                result[outIdx++] = s;
            }
            return result;
        }

        // ---- Split internals ----

        private string[] SplitInternal(char separator, int maxCount)
        {
            // Count matches up to maxCount-1.
            int segments = 1;
            for (int i = 0; i < Length && segments < maxCount; i++)
                if (this[i] == separator) segments++;

            string[] result = new string[segments];
            int startIdx = 0;
            int outIdx = 0;
            for (int i = 0; i < Length && outIdx < segments - 1; i++)
            {
                if (this[i] == separator)
                {
                    result[outIdx++] = Substring(startIdx, i - startIdx);
                    startIdx = i + 1;
                }
            }
            result[outIdx] = startIdx >= Length ? "" : Substring(startIdx, Length - startIdx);
            return result;
        }

        private string[] SplitInternalMulti(char[] separators, int maxCount)
        {
            int segments = 1;
            for (int i = 0; i < Length && segments < maxCount; i++)
            {
                char c = this[i];
                for (int s = 0; s < separators.Length; s++)
                {
                    if (c == separators[s]) { segments++; break; }
                }
            }

            string[] result = new string[segments];
            int startIdx = 0;
            int outIdx = 0;
            for (int i = 0; i < Length && outIdx < segments - 1; i++)
            {
                char c = this[i];
                bool match = false;
                for (int s = 0; s < separators.Length; s++)
                {
                    if (c == separators[s]) { match = true; break; }
                }
                if (match)
                {
                    result[outIdx++] = Substring(startIdx, i - startIdx);
                    startIdx = i + 1;
                }
            }
            result[outIdx] = startIdx >= Length ? "" : Substring(startIdx, Length - startIdx);
            return result;
        }

        private string[] SplitInternalString(string separator, int maxCount)
        {
            int sepLen = separator.Length;
            int segments = 1;
            int i = 0;
            while (i <= Length - sepLen && segments < maxCount)
            {
                if (MatchesAt(i, separator))
                {
                    segments++;
                    i += sepLen;
                }
                else i++;
            }

            string[] result = new string[segments];
            int startIdx = 0;
            int outIdx = 0;
            i = 0;
            while (i <= Length - sepLen && outIdx < segments - 1)
            {
                if (MatchesAt(i, separator))
                {
                    result[outIdx++] = Substring(startIdx, i - startIdx);
                    i += sepLen;
                    startIdx = i;
                }
                else i++;
            }
            result[outIdx] = startIdx >= Length ? "" : Substring(startIdx, Length - startIdx);
            return result;
        }

        private bool MatchesAt(int pos, string needle)
        {
            for (int i = 0; i < needle.Length; i++)
            {
                if (this[pos + i] != needle[i]) return false;
            }
            return true;
        }

        private static void Halt() { while (true) ; }
    }
}
