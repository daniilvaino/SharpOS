using System.Runtime.InteropServices;

namespace OS.Kernel.Input
{
    // The one runtime entry the kernel calls rather than exports.
    //
    // Everywhere else the direction is kernel-provides / runtime-consumes; this
    // is the reverse, and it exists because a thread the kernel created has to
    // announce itself before it may run managed code. Declared next to its only
    // caller so the direction of the dependency stays obvious.
    internal static unsafe class SharpOSHostRuntime
    {
        [DllImport("*", EntryPoint = "SharpOSHost_AttachCurrentThread")]
        public static extern int AttachCurrentThread();
    }
}
