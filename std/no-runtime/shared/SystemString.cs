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
