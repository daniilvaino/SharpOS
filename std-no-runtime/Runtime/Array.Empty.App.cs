// Array.Empty<T>() on the app tier: BCL's (System.Private.CoreLib, Array.cs) —
// one empty array per element type, and code relies on it being the same
// object: Aho-Corasick (SearchValues<string>) tells a node without children by
// reference equality with Array.Empty<int>(). ILC preinitializes EmptyArray<T>
// and every `static readonly T[] x = Array.Empty<T>()` through it, so no class
// constructor runs; GC statics are materialized in AppRuntime.Initialize before
// any app code. Pair: Array.Empty.Kernel.cs.

namespace System
{
    public abstract partial class Array
    {
        public static T[] Empty<T>() => EmptyArray<T>.Value;
    }

    internal static class EmptyArray<T>
    {
        internal static readonly T[] Value = new T[0];
    }
}
