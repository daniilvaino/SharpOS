// Minimal stubs for threading / environment primitives that Roslyn's
// iterator-state-machine rewriter emits against. We don't support real
// multithreading in the kernel — all boot code is single-threaded — so
// Interlocked.CompareExchange degenerates into a plain read-compare-
// write, and CurrentManagedThreadId is a constant.
//
// Added in step 33 to unblock `yield return`: Roslyn injects:
//   if (Interlocked.CompareExchange(ref _state, ..., ...) == initial
//       && _initialThreadId == Environment.CurrentManagedThreadId)
//       return this;                     // reuse
//   else return new StateMachine(...);   // clone
//
// plus `throw new InvalidOperationException(...)` on bad-state paths.
// The throw compiles but can't actually run in our env (no EH) —
// it'd halt via ThrowHelpers. Iterator's good paths don't hit it.
//
// All of this is explicitly labelled "added to unblock yield" — if we
// ever get real threading, these stubs must be replaced with proper
// atomics (our GC's mark phase already needs this when we multithread).

namespace System.Threading
{
    public static unsafe class Interlocked
    {
        // Every operation goes through AtomicBackend, which is a real
        // instruction once a tier installs one and a plain read-modify-write
        // until then. IsAtomic says which, so code that genuinely depends on
        // atomicity can check instead of assume.
        public static bool IsAtomic => AtomicBackend.IsAtomic;

        public static int CompareExchange(ref int location1, int value, int comparand)
        {
            fixed (int* p = &location1)
                return (int)AtomicBackend.CompareExchange32((uint*)p, (uint)value, (uint)comparand);
        }

        public static uint CompareExchange(ref uint location1, uint value, uint comparand)
        {
            fixed (uint* p = &location1)
                return AtomicBackend.CompareExchange32(p, value, comparand);
        }

        public static long CompareExchange(ref long location1, long value, long comparand)
        {
            fixed (long* p = &location1)
                return (long)AtomicBackend.CompareExchange64((ulong*)p, (ulong)value, (ulong)comparand);
        }

        public static ulong CompareExchange(ref ulong location1, ulong value, ulong comparand)
        {
            fixed (ulong* p = &location1)
                return AtomicBackend.CompareExchange64(p, value, comparand);
        }

        // Reference forms stay managed. Swapping a reference atomically means
        // writing a pointer through the GC's write barrier, and taking the
        // address of a managed reference to hand to a raw stub is exactly what
        // a non-moving collector with a conservative scan must not be asked to
        // survive. No caller needs it yet; when one does, this needs the write
        // barrier, not a cast.
        public static T CompareExchange<T>(ref T location1, T value, T comparand) where T : class
        {
            T original = location1;
            if (ReferenceEquals(original, comparand)) location1 = value;
            return original;
        }

        public static object CompareExchange(ref object location1, object value, object comparand)
        {
            object original = location1;
            if (ReferenceEquals(original, comparand)) location1 = value;
            return original;
        }

        public static int Exchange(ref int location1, int value)
        {
            // Built from compare-and-swap rather than an exchange instruction:
            // one primitive to install per tier instead of two, and the retry
            // only spins while another thread is writing the same word.
            while (true)
            {
                int original = location1;
                if (CompareExchange(ref location1, value, original) == original)
                    return original;
            }
        }

        public static long Exchange(ref long location1, long value)
        {
            while (true)
            {
                long original = location1;
                if (CompareExchange(ref location1, value, original) == original)
                    return original;
            }
        }

        public static int Increment(ref int location) => Add(ref location, 1);

        public static int Decrement(ref int location) => Add(ref location, -1);

        public static int Add(ref int location, int value)
        {
            while (true)
            {
                int original = location;
                int updated = original + value;
                if (CompareExchange(ref location, updated, original) == original)
                    return updated;
            }
        }

        public static long Increment(ref long location) => Add(ref location, 1);

        public static long Decrement(ref long location) => Add(ref location, -1);

        public static long Add(ref long location, long value)
        {
            while (true)
            {
                long original = location;
                long updated = original + value;
                if (CompareExchange(ref location, updated, original) == original)
                    return updated;
            }
        }

        public static int Read(ref int location) => CompareExchange(ref location, 0, 0);

        public static long Read(ref long location) => CompareExchange(ref location, 0, 0);

        // A real mfence once a tier installs one. On one core this matters
        // less than it will under SMP, but it is no longer nothing: the
        // compiler reorders too, and a barrier that expands to nothing gives
        // permission for exactly the reordering it was written to forbid.
        public static void MemoryBarrier() => AtomicBackend.Barrier();
    }
}

namespace System
{
    public static class Environment
    {
        // Single-threaded kernel — everyone is thread 1. Iterator
        // state-machine uses this to decide whether to reuse `this` in
        // GetEnumerator when it returns to the same thread.
        // Was the constant 1. See Threading.ManagedThreadId.cs for why that
        // had to change: anything asking "is this mine?" got yes from every
        // thread.
        public static int CurrentManagedThreadId => System.Threading.ManagedThreadIds.Current;

        // BCL reports "\r\n" on Windows, "\n" on Unix. We target UEFI
        // which generally prefers CRLF (same as Windows). Stringbuilder's
        // AppendLine() and similar paths read this.
        public static string NewLine => "\r\n";
    }

    // Exception types live in Exception.cs / Exceptions.Derived.cs as of
    // step 44 (Phase 1 try/catch roadmap). This file only hosts threading
    // and environment stubs.
}

namespace System
{
    /// <summary>
    /// One copy of the static field per thread. The attribute is honoured by
    /// the compiler and the runtime's field layout, so declaring it is all that
    /// is needed here.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, Inherited = false)]
    public sealed class ThreadStaticAttribute : Attribute
    {
        public ThreadStaticAttribute() { }
    }
}
