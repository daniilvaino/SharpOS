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

    // Compile-time-only stubs. Throwing in our env halts via
    // ThrowHelpers.* (no unwinder, no catch), but Roslyn needs these
    // types to exist so it can emit `new InvalidOperationException(...)`
    // in generated iterator state machines and similar synthesised code.
    public class Exception
    {
        private readonly string _message;

        public Exception() { _message = null; }
        public Exception(string message) { _message = message; }
        public Exception(string message, Exception innerException) { _message = message; }

        public virtual string Message => _message;
        public Exception InnerException => null;
        public virtual string StackTrace => null;
    }

    public class InvalidOperationException : Exception
    {
        public InvalidOperationException() { }
        public InvalidOperationException(string message) : base(message) { }
        public InvalidOperationException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class NotSupportedException : Exception
    {
        public NotSupportedException() { }
        public NotSupportedException(string message) : base(message) { }
        public NotSupportedException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class ArgumentException : Exception
    {
        public ArgumentException() { }
        public ArgumentException(string message) : base(message) { }
        public ArgumentException(string message, string paramName) : base(message) { }
    }

    public class ArgumentNullException : ArgumentException
    {
        public ArgumentNullException() { }
        public ArgumentNullException(string paramName) : base(null, paramName) { }
        public ArgumentNullException(string paramName, string message) : base(message, paramName) { }

        // BCL helper: `ArgumentNullException.ThrowIfNull(arg)` is a common
        // pattern in verbatim-ported BCL code. Halts via the exception
        // throw — which itself halts since we have no unwinder.
        public static void ThrowIfNull(object argument)
        {
            if (argument == null) Throw();
        }

        public static void ThrowIfNull(object argument, string paramName)
        {
            if (argument == null) Throw(paramName);
        }

        public static void Throw() { while (true) ; }
        public static void Throw(string paramName) { while (true) ; }
    }

    public class ArgumentOutOfRangeException : ArgumentException
    {
        public ArgumentOutOfRangeException() { }
        public ArgumentOutOfRangeException(string paramName) : base(null, paramName) { }
        public ArgumentOutOfRangeException(string paramName, string message) : base(message, paramName) { }
        public ArgumentOutOfRangeException(string paramName, object actualValue, string message) : base(message, paramName) { }
    }

    public class OutOfMemoryException : Exception
    {
        public OutOfMemoryException() { }
        public OutOfMemoryException(string message) : base(message) { }
        public OutOfMemoryException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class IndexOutOfRangeException : Exception
    {
        public IndexOutOfRangeException() { }
        public IndexOutOfRangeException(string message) : base(message) { }
        public IndexOutOfRangeException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class FormatException : Exception
    {
        public FormatException() { }
        public FormatException(string message) : base(message) { }
        public FormatException(string message, Exception innerException) : base(message, innerException) { }
    }
}
