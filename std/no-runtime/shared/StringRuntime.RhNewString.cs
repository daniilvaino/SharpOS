namespace SharpOS.Std.NoRuntime
{
    internal static class StringRuntime
    {
        internal static string FastAllocateString(int length)
        {
            if (length <= 0)
                return string.Empty;

            return System.Runtime.RuntimeImports.RhNewString(System.EETypePtr.EETypePtrOf<string>(), length);
        }
    }
}
