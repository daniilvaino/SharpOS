using System.Runtime.InteropServices;

namespace OS.Kernel.Input
{
    // The one runtime entry the kernel calls rather than exports.
    //
    // Everywhere else the direction is kernel-provides / runtime-consumes; this
    // is the reverse, and it exists because a thread the kernel created has to
    // announce itself before it may run managed code. Declared next to its only
    // caller so the direction of the dependency stays obvious.
    //
    // Compiled out of the kernel-only build: the symbol lives in the fork, and
    // an import is resolved at link time whether or not the call is ever made,
    // so referencing it unconditionally broke `-SkipCoreClr` with an unresolved
    // external. Without CoreCLR there is no runtime to introduce a thread to,
    // and nothing registers a console handler either — the caller never gets
    // that far.
    internal static unsafe class SharpOSHostRuntime
    {
#if SKIP_CORECLR
        public static int AttachCurrentThread() => 0;
#else
        [DllImport("*", EntryPoint = "SharpOSHost_AttachCurrentThread")]
        public static extern int AttachCurrentThread();
#endif
    }
}
