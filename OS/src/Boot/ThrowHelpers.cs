// Kernel-specific ThrowHelpers. Replaces std/no-runtime/shared/ThrowHelpers.cs
// for the OS project — apps keep their own copy in MinimalRuntime.cs.
//
// ILC emits calls into Internal.Runtime.CompilerHelpers.ThrowHelpers for
// implicit bounds/overflow/null/div-by-zero throws. In a real runtime these
// raise managed exceptions. We don't have an exception engine, so each
// helper calls Panic.Fail with the name of the situation — that way a
// runtime-triggered check surfaces as a readable log entry + shutdown,
// instead of spinning in a silent `while (true)` loop.

namespace Internal.Runtime.CompilerHelpers
{
    internal static class ThrowHelpers
    {
        private static void ThrowOverflowException() => OS.Kernel.Panic.Fail("overflow");
        private static void ThrowIndexOutOfRangeException() => OS.Kernel.Panic.Fail("index out of range");
        private static void ThrowNullReferenceException() => OS.Kernel.Panic.Fail("null reference");
        private static void ThrowDivideByZeroException() => OS.Kernel.Panic.Fail("divide by zero");
        private static void ThrowArrayTypeMismatchException() => OS.Kernel.Panic.Fail("array type mismatch");
        private static void ThrowPlatformNotSupportedException() => OS.Kernel.Panic.Fail("platform not supported");
        private static void ThrowTypeLoadException() => OS.Kernel.Panic.Fail("type load");
        private static void ThrowArgumentException() => OS.Kernel.Panic.Fail("argument");
        private static void ThrowArgumentOutOfRangeException() => OS.Kernel.Panic.Fail("argument out of range");
    }
}
