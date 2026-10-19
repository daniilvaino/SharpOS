using OS.Hal;

namespace OS.Kernel.Threading
{
    // [ThreadStatic] in the kernel (step209): the boot thread and a spawned
    // one each see their own value, a reference held only by a thread static
    // survives a collection, and a thread that never wrote one reads zero.
    // AOTTESTS checks the same in an app.
    internal static unsafe class ThreadStaticProbe
    {
        private sealed class Box
        {
            public int Value;
        }

        private static class PerThread
        {
            [System.ThreadStatic] public static int Number;
            [System.ThreadStatic] public static Box? Held;
        }

        private static int s_otherSaw = -1;
        private static bool s_otherHeldEmpty;
        private static bool s_otherDone;

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void Other()
        {
            s_otherSaw = PerThread.Number;
            s_otherHeldEmpty = PerThread.Held == null;
            PerThread.Number = 2;
            PerThread.Held = new Box { Value = 2 };
            s_otherDone = true;
            Scheduler.Exit();
        }

        public static void Run()
        {
            if (!Internal.Runtime.ThreadStatics.IsAvailable)
            {
                Log.Write(LogLevel.Warn, "thread-static probe: FAIL not available");
                return;
            }

            PerThread.Number = 1;
            PerThread.Held = new Box { Value = 1 };

            if (Scheduler.Spawn(&Other, 0) == null)
            {
                Log.Write(LogLevel.Warn, "thread-static probe: FAIL spawn");
                return;
            }
            for (int i = 0; i < 1000 && !s_otherDone; i++)
                Scheduler.Yield();

            System.GC.Collect();
            object filler = new Box[64];    // reuse what a wrongly swept block would leave

            bool own = PerThread.Number == 1;
            bool held = PerThread.Held != null && PerThread.Held.Value == 1;
            bool other = s_otherDone && s_otherSaw == 0 && s_otherHeldEmpty;

            Log.Begin(own && held && other ? LogLevel.Info : LogLevel.Warn);
            Console.Write("thread-static probe: own=");
            Console.Write(own ? "ok" : "FAIL");
            Console.Write(" held=");
            Console.Write(held ? "ok" : "FAIL");
            Console.Write(" other=");
            Console.Write(other ? "ok" : "FAIL");
            Console.Write(filler != null && own && held && other ? " — ok" : " — FAIL");
            Log.EndLine();
        }
    }
}
