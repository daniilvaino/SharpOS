// System.Runtime.InteropServices.Marshal — the HGlobal and CoTaskMem members,
// upstream's Unix bodies (dotnet/runtime v8.0.27 Marshal.Unix.cs, MIT): over
// NativeMemory, which std keeps on the managed heap (NativeMemory.cs).
// The rest of Marshal is not ported; add it from the same files as callers need.

namespace System.Runtime.InteropServices
{
    public static partial class Marshal
    {
        public static IntPtr AllocHGlobal(int cb) => AllocHGlobal((nint)cb);

        public static unsafe IntPtr AllocHGlobal(IntPtr cb)
        {
            return (nint)NativeMemory.Alloc((nuint)(nint)cb);
        }

        public static unsafe void FreeHGlobal(IntPtr hglobal)
        {
            NativeMemory.Free((void*)(nint)hglobal);
        }

        public static unsafe IntPtr ReAllocHGlobal(IntPtr pv, IntPtr cb)
        {
            return (nint)NativeMemory.Realloc((void*)(nint)pv, (nuint)(nint)cb);
        }

        public static IntPtr AllocCoTaskMem(int cb) => AllocHGlobal((nint)(uint)cb);

        public static void FreeCoTaskMem(IntPtr ptr) => FreeHGlobal(ptr);
    }
}
