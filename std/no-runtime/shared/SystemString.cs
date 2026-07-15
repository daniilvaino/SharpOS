using System.Runtime.InteropServices;

namespace System
{
    [StructLayout(LayoutKind.Sequential)]
    public sealed unsafe partial class String
    {
        public static readonly string Empty = "";
        // BCL-canonical field name: ILC's TypePreinit.ConstructStringInstance
        // (frozen-string codegen) looks the length field up by the name
        // `_stringLength`. ILC 7 tolerated our old `Length` field; ILC 8+
        // NREs in ConstructStringInstance when the lookup misses. Keep the
        // field named exactly as BCL and expose `Length` as a property.
        private readonly int _stringLength;
        public int Length => _stringLength;
        // Accessible to other types in the same assembly (StringBuilder,
        // MemoryExtensions.AsSpan via StringHelpers.GetFirstCharRef).
        // `ref _firstChar` + pointer arithmetic is the canonical way BCL
        // code walks a string's character storage.
        internal char _firstChar;

        public String(char c, int count)
        {
            _stringLength = count;
            _firstChar = c;
        }

        public String(char[] value)
        {
            if (value == null || value.Length == 0)
            {
                _stringLength = 0;
                return;
            }
            _stringLength = value.Length;
            fixed (char* dest = &_firstChar)
                for (int i = 0; i < value.Length; i++) dest[i] = value[i];
        }

        public String(char[] value, int startIndex, int length)
        {
            if (value == null || length <= 0)
            {
                _stringLength = 0;
                return;
            }
            _stringLength = length;
            fixed (char* dest = &_firstChar)
                for (int i = 0; i < length; i++) dest[i] = value[startIndex + i];
        }

        // String(ReadOnlySpan<char>) lives in SystemString.Span.cs — it
        // is the only Span-typed member, split out so projects without
        // the Span types (the minimal apps) can compile a coherent
        // Span-free String. Kernel/OS.csproj carries Span and includes
        // that partial; apps don't include it and never call the ctor.

        // Internal allocation helper. Mirrors BCL's internal
        // `string.FastAllocateString(int)` — returns a fresh string with
        // the requested length; contents zero. Callers use
        // `fixed (char* p = result)` to write the character data.
        internal static string FastAllocateString(int length)
        {
            return SharpOS.Std.NoRuntime.StringRuntime.FastAllocateString(length);
        }

        public char this[int index]
        {
            get
            {
                fixed (char* p = &_firstChar)
                {
                    return p[index];
                }
            }
        }

        public ref char GetPinnableReference()
        {
            return ref _firstChar;
        }

        // BCL-internal helper used by StringBuilder / String operations —
        // direct `ref` to the first character of the string's backing
        // store. Equivalent to GetPinnableReference but the name matches
        // the BCL callsite (`string.GetRawStringData()`).
        internal ref char GetRawStringData() => ref _firstChar;

        public static string Concat(string str0, string str1)
        {
            return SharpOS.Std.NoRuntime.StringAlgorithms.Concat(str0, str1);
        }

        // Decode a raw ASCII buffer into a managed string. Non-ASCII bytes
        // (> 0x7F) are mapped to '?'. Useful at ABI boundaries that hand us
        // C-style byte buffers (filenames from the app-service table, etc).
        public static string FromAscii(byte* source, int length)
        {
            if (source == null || length <= 0)
                return "";

            string result = new string('\0', length);
            fixed (char* destination = result)
            {
                for (int i = 0; i < length; i++)
                {
                    byte b = source[i];
                    destination[i] = b <= 0x7F ? (char)b : '?';
                }
            }
            return result;
        }

        // uint overload — ABI buffers usually carry unsigned lengths.
        public static string FromAscii(byte* source, uint length)
        {
            return FromAscii(source, (int)length);
        }

        // NUL-terminated variant — scans until 0x00, then delegates.
        public static string FromAsciiZ(byte* source)
        {
            if (source == null)
                return "";

            int length = 0;
            while (source[length] != 0)
                length++;

            return FromAscii(source, length);
        }

        // Decode a UTF-16 buffer of known length into a managed string.
        // Used at ABI boundaries that hand us raw `char*` (UEFI firmware
        // vendor strings, directory entry names, etc).
        public static string FromUtf16(char* source, int length)
        {
            if (source == null || length <= 0)
                return "";

            string result = FastAllocateString(length);
            fixed (char* destination = result)
            {
                for (int i = 0; i < length; i++)
                    destination[i] = source[i];
            }
            return result;
        }

        // NUL-terminated UTF-16 variant. maxLen bounds the scan to survive
        // unterminated buffers; callers pass the allocated capacity.
        public static string FromUtf16Z(char* source, int maxLen)
        {
            if (source == null || maxLen <= 0)
                return "";

            int length = 0;
            while (length < maxLen && source[length] != '\0')
                length++;

            return FromUtf16(source, length);
        }

        public string PadLeft(int totalWidth)
        {
            return SharpOS.Std.NoRuntime.StringAlgorithms.PadLeft(this, totalWidth, ' ');
        }

        public string PadLeft(int totalWidth, char paddingChar)
        {
            return SharpOS.Std.NoRuntime.StringAlgorithms.PadLeft(this, totalWidth, paddingChar);
        }

        public string PadRight(int totalWidth)
        {
            return SharpOS.Std.NoRuntime.StringAlgorithms.PadRight(this, totalWidth, ' ');
        }

        public string PadRight(int totalWidth, char paddingChar)
        {
            return SharpOS.Std.NoRuntime.StringAlgorithms.PadRight(this, totalWidth, paddingChar);
        }

        // ---- Queries (stage 3) ----

        public static bool IsNullOrEmpty(string str)
        {
            return SharpOS.Std.NoRuntime.StringQueries.IsNullOrEmpty(str);
        }

        public static bool IsNullOrWhiteSpace(string str)
        {
            return SharpOS.Std.NoRuntime.StringQueries.IsNullOrWhiteSpace(str);
        }

        public int IndexOf(char value)
        {
            return SharpOS.Std.NoRuntime.StringQueries.IndexOf(this, value, 0);
        }

        public int IndexOf(char value, int startIndex)
        {
            return SharpOS.Std.NoRuntime.StringQueries.IndexOf(this, value, startIndex);
        }

        public int IndexOf(string value)
        {
            return SharpOS.Std.NoRuntime.StringQueries.IndexOf(this, value, 0);
        }

        public int IndexOf(string value, int startIndex)
        {
            return SharpOS.Std.NoRuntime.StringQueries.IndexOf(this, value, startIndex);
        }

        public int LastIndexOf(char value)
        {
            return SharpOS.Std.NoRuntime.StringQueries.LastIndexOf(this, value);
        }

        public int LastIndexOf(string value)
        {
            return SharpOS.Std.NoRuntime.StringQueries.LastIndexOf(this, value);
        }

        public bool Contains(char value)
        {
            return SharpOS.Std.NoRuntime.StringQueries.Contains(this, value);
        }

        public bool Contains(string value)
        {
            return SharpOS.Std.NoRuntime.StringQueries.Contains(this, value);
        }

        public bool StartsWith(string value)
        {
            return SharpOS.Std.NoRuntime.StringQueries.StartsWith(this, value);
        }

        public bool EndsWith(string value)
        {
            return SharpOS.Std.NoRuntime.StringQueries.EndsWith(this, value);
        }

        // ---- Transforms (stage 4) ----

        public string Substring(int startIndex)
        {
            return SharpOS.Std.NoRuntime.StringTransforms.Substring(this, startIndex);
        }

        public string Substring(int startIndex, int length)
        {
            return SharpOS.Std.NoRuntime.StringTransforms.Substring(this, startIndex, length);
        }

        public string Trim()
        {
            return SharpOS.Std.NoRuntime.StringTransforms.Trim(this);
        }

        public string TrimStart()
        {
            return SharpOS.Std.NoRuntime.StringTransforms.TrimStart(this);
        }

        public string TrimEnd()
        {
            return SharpOS.Std.NoRuntime.StringTransforms.TrimEnd(this);
        }

        public string TrimStart(char trimChar)
        {
            int start = 0;
            while (start < Length && this[start] == trimChar) start++;
            return Substring(start, Length - start);
        }

        public string TrimEnd(char trimChar)
        {
            int end = Length - 1;
            while (end >= 0 && this[end] == trimChar) end--;
            return Substring(0, end + 1);
        }

        public string Trim(char trimChar)
        {
            int start = 0, end = Length - 1;
            while (start <= end && this[start] == trimChar) start++;
            while (end >= start && this[end] == trimChar) end--;
            return start > end ? "" : Substring(start, end - start + 1);
        }

        public string TrimStart(params char[] trimChars)
        {
            if (trimChars == null || trimChars.Length == 0) return TrimStart();
            int start = 0;
            while (start < Length && ContainsChar(trimChars, this[start])) start++;
            return Substring(start, Length - start);
        }

        public string TrimEnd(params char[] trimChars)
        {
            if (trimChars == null || trimChars.Length == 0) return TrimEnd();
            int end = Length - 1;
            while (end >= 0 && ContainsChar(trimChars, this[end])) end--;
            return Substring(0, end + 1);
        }

        public string Trim(params char[] trimChars)
        {
            if (trimChars == null || trimChars.Length == 0) return Trim();
            int start = 0, end = Length - 1;
            while (start <= end && ContainsChar(trimChars, this[start])) start++;
            while (end >= start && ContainsChar(trimChars, this[end])) end--;
            return start > end ? "" : Substring(start, end - start + 1);
        }

        private static bool ContainsChar(char[] chars, char c)
        {
            for (int i = 0; i < chars.Length; i++)
                if (chars[i] == c) return true;
            return false;
        }

        public string Replace(char oldChar, char newChar)
        {
            return SharpOS.Std.NoRuntime.StringTransforms.Replace(this, oldChar, newChar);
        }

        public string Replace(string oldValue, string newValue)
        {
            return SharpOS.Std.NoRuntime.StringTransforms.Replace(this, oldValue, newValue);
        }

        public string ToUpperInvariant()
        {
            return SharpOS.Std.NoRuntime.StringTransforms.ToUpperInvariant(this);
        }

        public string ToLowerInvariant()
        {
            return SharpOS.Std.NoRuntime.StringTransforms.ToLowerInvariant(this);
        }

        public static bool operator ==(string str0, string str1)
        {
            if ((object)str0 == (object)str1)
                return true;

            if ((object)str0 == null || (object)str1 == null)
                return false;

            int length = str0.Length;
            if (length != str1.Length)
                return false;

            for (int i = 0; i < length; i++)
            {
                if (str0[i] != str1[i])
                    return false;
            }

            return true;
        }

        public static bool operator !=(string str0, string str1)
        {
            return !(str0 == str1);
        }

        private static string Ctor(char c, int count)
        {
            if (count <= 0)
                return "";

            string result = SharpOS.Std.NoRuntime.StringRuntime.FastAllocateString(count);
            if (result.Length != count)
                return "";

            fixed (char* dst = &result.GetPinnableReference())
            {
                for (int i = 0; i < count; i++)
                    dst[i] = c;
            }

            return result;
        }

        // ILC redirects `newobj String::.ctor(char[])` / `(char[],int,int)` to
        // these Ctor methods (the public constructors above are placeholders,
        // like Ctor(char,int)). Missing before: `new string(char[]...)` failed
        // ILC codegen with "Expected method 'Ctor' not found on type 'string'".
        private static string Ctor(char[] value)
        {
            if (value == null || value.Length == 0)
                return "";
            return Ctor(value, 0, value.Length);
        }

        private static string Ctor(char[] value, int startIndex, int length)
        {
            if (value == null || length <= 0)
                return "";

            string result = SharpOS.Std.NoRuntime.StringRuntime.FastAllocateString(length);
            if (result.Length != length)
                return "";

            fixed (char* dst = &result.GetPinnableReference())
            {
                for (int i = 0; i < length; i++)
                    dst[i] = value[startIndex + i];
            }

            return result;
        }
    }
}
