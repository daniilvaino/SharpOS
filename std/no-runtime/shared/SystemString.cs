using System.Runtime.InteropServices;

namespace System
{
    [StructLayout(LayoutKind.Sequential)]
    public sealed unsafe class String
    {
        public static readonly string Empty = "";
        public readonly int Length;
        private char _firstChar;

        public String(char c, int count)
        {
            Length = count;
            _firstChar = c;
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

        public static string Concat(string str0, string str1)
        {
            return SharpOS.Std.NoRuntime.StringAlgorithms.Concat(str0, str1);
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
                return Empty;

            string result = SharpOS.Std.NoRuntime.StringRuntime.FastAllocateString(count);
            if (result.Length != count)
                return Empty;

            fixed (char* dst = &result.GetPinnableReference())
            {
                for (int i = 0; i < count; i++)
                    dst[i] = c;
            }

            return result;
        }
    }
}
