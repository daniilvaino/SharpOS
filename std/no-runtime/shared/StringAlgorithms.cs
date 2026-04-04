namespace SharpOS.Std.NoRuntime
{
    internal static unsafe class StringAlgorithms
    {
        internal static string Concat(string str0, string str1)
        {
            if (str0 == null)
                str0 = string.Empty;

            if (str1 == null)
                str1 = string.Empty;

            int len0 = str0.Length;
            int len1 = str1.Length;
            int total = len0 + len1;
            if (total <= 0)
                return string.Empty;

            string result = StringRuntime.FastAllocateString(total);
            if (result.Length != total)
                return string.Empty;

            fixed (char* dst = result)
            {
                for (int i = 0; i < len0; i++)
                    dst[i] = str0[i];

                for (int i = 0; i < len1; i++)
                    dst[len0 + i] = str1[i];
            }

            return result;
        }
    }
}
