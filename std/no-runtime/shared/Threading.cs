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
    public static class Interlocked
    {
        public static int CompareExchange(ref int location1, int value, int comparand)
        {
            int original = location1;
            if (original == comparand) location1 = value;
            return original;
        }

        public static long CompareExchange(ref long location1, long value, long comparand)
        {
            long original = location1;
            if (original == comparand) location1 = value;
            return original;
        }

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
            int original = location1;
            location1 = value;
            return original;
        }

        // Single-threaded kernel — full barrier is a no-op semantically
        // (all loads/stores already happen in program order from our
        // perspective). Will need a real `mfence` shellcode when SMP
        // arrives in Phase 3.5.
        public static void MemoryBarrier() { }
    }
}

namespace System
{
    public static class Environment
    {
        // Single-threaded kernel — everyone is thread 1. Iterator
        // state-machine uses this to decide whether to reuse `this` in
        // GetEnumerator when it returns to the same thread.
        public static int CurrentManagedThreadId => 1;

        // BCL reports "\r\n" on Windows, "\n" on Unix. We target UEFI
        // which generally prefers CRLF (same as Windows). Stringbuilder's
        // AppendLine() and similar paths read this.
        public static string NewLine => "\r\n";
    }

    // Exception types live in Exception.cs / Exceptions.Derived.cs as of
    // step 44 (Phase 1 try/catch roadmap). This file only hosts threading
    // and environment stubs.
}
