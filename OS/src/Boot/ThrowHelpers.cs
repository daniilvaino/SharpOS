// Kernel ThrowHelpers. Replaces std/no-runtime/shared/ThrowHelpers.cs for the
// OS project (see OS.csproj); apps keep their own copy in MinimalRuntime.cs.
//
// ILC emits calls here for implicit bounds / overflow / null / div-by-zero
// checks. These used to call Panic.Fail with a bare word — "index out of
// range" and nothing else: no type, no frames, no way to tell WHICH index in
// which method. That was written when the comment above it was true and the
// kernel had no exception engine; it has had one since Phase D, and every
// other throw-spot in the kernel has been raising real exceptions for a long
// time.
//
// Throwing is also safe before the engine is up: the unpatched RhpThrowEx stub
// ends in Panic.Fail, so an early-boot bounds check degrades to exactly the
// old behaviour instead of crashing differently.

namespace Internal.Runtime.CompilerHelpers
{
    internal static class ThrowHelpers
    {
        private static void ThrowOverflowException()
            => throw new System.OverflowException("Arithmetic operation resulted in an overflow.");

        private static void ThrowIndexOutOfRangeException()
            => throw new System.IndexOutOfRangeException("Index was outside the bounds of the array.");

        private static void ThrowNullReferenceException()
            => throw new System.NullReferenceException("Object reference not set to an instance of an object.");

        private static void ThrowDivideByZeroException()
            => throw new System.DivideByZeroException("Attempted to divide by zero.");

        private static void ThrowArrayTypeMismatchException()
            => throw new System.ArrayTypeMismatchException("Attempted to store an element of the wrong type.");

        private static void ThrowPlatformNotSupportedException()
            => throw new System.PlatformNotSupportedException("Operation is not supported on this platform.");

        private static void ThrowTypeLoadException()
            => throw new System.TypeLoadException("Failed to load type.");

        private static void ThrowArgumentException()
            => throw new System.ArgumentException("Value does not fall within the expected range.");

        private static void ThrowArgumentOutOfRangeException()
            => throw new System.ArgumentOutOfRangeException(null, "Specified argument was out of the range of valid values.");
    }
}
