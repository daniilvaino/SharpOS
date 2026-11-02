// Array.Empty<T>() in the kernel: a new empty array per call. BCL keeps one per
// element type in a GC static (EmptyArray<T>.Value, Array.Empty.App.cs), but
// the kernel's GC statics exist only from Phase 2 (BootSequence) and std code
// runs before that. Code that compares Array.Empty<T>() by reference
// (SearchValues<string>'s Aho-Corasick) is app-tier only. Pair: Array.Empty.App.cs.

namespace System
{
    public abstract partial class Array
    {
        public static T[] Empty<T>() => new T[0];
    }
}
