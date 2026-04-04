namespace SharpOS.Std.NoRuntime
{
    internal static class StringRuntime
    {
        internal static string FastAllocateString(int length)
        {
            if (length <= 0)
                return string.Empty;

            return string.Empty;
        }
    }
}
