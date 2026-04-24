// System.GC — shim layer. BCL exposes GC controls (Collect, allocator
// hints, etc.); we only need the Array-uninitialized allocation helper
// that several BCL types call. Zero-init (normal `new T[n]`) is
// functionally equivalent — you observe the same content before first
// write. BCL uses uninitialized for perf; we forgo that here.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace System
{
    public static class GC
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T[] AllocateUninitializedArray<T>(int length, bool pinned = false)
        {
            if (length < 0) SpanHelpers.Halt();
            return new T[length];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T[] AllocateArray<T>(int length, bool pinned = false)
        {
            if (length < 0) SpanHelpers.Halt();
            return new T[length];
        }
    }
}
