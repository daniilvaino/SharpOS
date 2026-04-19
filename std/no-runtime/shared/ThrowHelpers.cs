// Stubs for runtime-throw helpers that ILC emits calls to when compiling
// bounds checks, overflow checks, null-ref checks, etc. In a real runtime
// these throw managed exceptions; we don't support exceptions in kernel
// or apps, so they just halt (infinite loop = fatal fail in practice).
//
// The type MUST be named exactly `Internal.Runtime.CompilerHelpers.ThrowHelpers`
// with these method names — ILC looks them up by fixed name during codegen
// (see dotnet/runtime src/coreclr/tools/aot/.../JitHelper.cs).

namespace Internal.Runtime.CompilerHelpers
{
    internal static class ThrowHelpers
    {
        private static void ThrowOverflowException() { while (true) ; }

        private static void ThrowIndexOutOfRangeException() { while (true) ; }

        private static void ThrowNullReferenceException() { while (true) ; }

        private static void ThrowDivideByZeroException() { while (true) ; }

        private static void ThrowArrayTypeMismatchException() { while (true) ; }

        private static void ThrowPlatformNotSupportedException() { while (true) ; }

        private static void ThrowTypeLoadException() { while (true) ; }

        private static void ThrowArgumentException() { while (true) ; }

        private static void ThrowArgumentOutOfRangeException() { while (true) ; }
    }
}
