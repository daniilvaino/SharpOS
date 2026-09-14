namespace SharpOS.Std.NoRuntime
{
    internal static class StringRuntime
    {
        internal static string FastAllocateString(int length)
        {
            if (length == 0)
                return "";

            // Not "": callers fill the result in place, and "" is the one
            // frozen string.Empty in the image (see StringRuntime.KernelHeap).
            if (length < 0)
                throw new System.OverflowException();

            return System.Runtime.RuntimeImports.RhNewString(System.EETypePtr.EETypePtrOf<string>(), length);
        }
    }
}
