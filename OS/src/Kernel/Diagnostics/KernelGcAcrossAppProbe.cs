using OS.Hal;

namespace OS.Kernel.Diagnostics
{
    // Does a kernel collection see the kernel frames UNDER a running app?
    // (pipe_plan.md, item 2, condition (c).)
    //
    // A nested launch (RunExternalApp) holds a string only in its own frame,
    // which sits below the child's jump stub while the child runs. The child's
    // first service call collects the kernel heap. If the walk stops at the
    // app — as it did before JumpContext carried the kernel's registers — the
    // string is unreachable and the sweep frees it; after the child returns
    // it is no longer a string.
    internal static class KernelGcAcrossAppProbe
    {
        private static bool s_armed;
        private static int s_crossed;
        private static int s_boundaries;
        private static int s_serial;

        /// <summary>Before the launch: the object the collection must not free.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static object Arm()
        {
            s_armed = true;
            s_crossed = -1;
            return Expected(++s_serial);
        }

        /// <summary>From the child's service call: collect once, with the child on the stack.</summary>
        public static void OnService()
        {
            if (!s_armed) return;
            s_armed = false;

            OS.Kernel.Memory.KernelGC.Collect();
            s_crossed = OS.Kernel.Memory.KernelGcPreciseWalk.LastAppRunsCrossed;
            s_boundaries = OS.Kernel.Memory.KernelGcPreciseWalk.LastAppBoundaries;
        }

        /// <summary>After the child returned: is the object still what it was?</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static void Check(object sentinel)
        {
            s_armed = false;
            if (s_crossed < 0)
            {
                Log.Write(LogLevel.Info, "[kgc-probe] child made no service call: nothing collected");
                return;
            }

            // Checked against its own shape, not against the latest serial:
            // launches nest, and an inner one arms again before the outer one
            // checks (that read as two FAILs that were the probe's own).
            bool ok = sentinel is string text && text.Length > 13 && text.StartsWith("kgc-sentinel-");
            Log.Begin(LogLevel.Info);
            Console.Write("[kgc-probe] kernel root under a running app survived a collection: ");
            Console.Write(ok ? "ok" : "FAIL");
            Console.Write(" runs crossed=");
            Console.WriteUInt((uint)s_crossed);
            Console.Write(" app boundaries=");
            Console.WriteUInt((uint)s_boundaries);
            Log.EndLine();
        }

        // Built at run time, so it lives on the heap and nowhere else.
        private static object Expected(int serial) => "kgc-sentinel-" + serial;
    }
}
