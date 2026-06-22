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

        // BCL parity: `GC.Collect()` triggers a full mark-sweep cycle.
        // Forwards to our kernel-side GC implementation (mark + sweep).
        // Overloads (generation arg, etc.) ignored — we have no
        // generations. Used by NativeAotProbe.Probe_WriteBarrier and any
        // BCL-ported code that explicitly forces a collection.
        public static void Collect() => SharpOS.Std.NoRuntime.GC.Collect();
        public static void Collect(int generation) => SharpOS.Std.NoRuntime.GC.Collect();
        public static void Collect(int generation, GCCollectionMode mode) => SharpOS.Std.NoRuntime.GC.Collect();
    }

    // Minimal enum to satisfy `GC.Collect(int, GCCollectionMode)` shape —
    // values mirror BCL; we don't honour any of them (we just collect).
    public enum GCCollectionMode { Default = 0, Forced = 1, Optimized = 2, Aggressive = 3 }
}
