using OS.Hal;

namespace OS.Kernel.Diagnostics
{
    // The counter must not run backwards when the hardware's does.
    //
    // This laptop reports `hpet: freq=14318180 Hz ... 64bit=no`: a 32-bit main
    // counter that returns to zero every 300 seconds. Until step177 the driver
    // read it as a 64-bit register and said in a comment that the upper half
    // was zero so the value was correct anyway — true exactly once, for the
    // first five minutes of uptime.
    //
    // After that every "deadline = now + delta" in the system named a value
    // the counter could no longer reach: Sleep, Monitor.Wait with a timeout,
    // WaitOnAddress, WaitForExit, every bounded wait CoreCLR computes, and the
    // spin loops in the AHCI and xHCI drivers. The rig stopped after ninety
    // seconds of the launcher and stayed stopped for fourteen hours with every
    // thread Waiting, ticks still arriving and nothing able to end a wait.
    //
    // Waiting five minutes to test that is not a test. Both checks here drive
    // the counter by hand: the first over the arithmetic alone, the second
    // over the real driver pointed at a buffer instead of the chip, which
    // crosses two wraps in microseconds and does it identically on QEMU, whose
    // HPET is 64-bit and cannot show the bug at all.
    //
    // Precondition: an empty timer queue and no I/O in flight. While the
    // synthetic base is installed the whole kernel's clock is fiction, and a
    // sleeper due during it wakes early.
    internal static unsafe class HpetWrapProbe
    {
        // Register window as a plain static, not a heap block: this runs
        // wherever it is placed in the boot order, and a probe that needs an
        // allocator to test the clock is a probe that cannot run early.
        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Sequential, Size = 4096)]
        private struct RegisterBlock { }
        private static RegisterBlock s_registers;

        private const uint OFFSET_COUNTER = 0x0F0;

        public static void Run()
        {
            Console.WriteLine("hpet wrap probe begin");
            CheckExtend();
            CheckDriverAcrossWraps();
            Console.WriteLine("hpet wrap probe end");
        }

        // The arithmetic on its own. Not a reproduction of the failure — a
        // fence around the four cases that matter, so a later rewrite of the
        // driver cannot quietly lose one.
        private static void CheckExtend()
        {
            uint bad = 0;

            // Ordinary step forward inside one epoch.
            if (OS.Hal.Timer.Hpet.Extend(0x1000, 0x2000) != 0x2000) bad |= 1;

            // Across the boundary: 256 short of it, 256 past it.
            if (OS.Hal.Timer.Hpet.Extend(0xFFFFFF00, 0x00000100) != 0x1_00000100UL) bad |= 2;

            // A second wrap must add a second epoch, not repeat the first.
            if (OS.Hal.Timer.Hpet.Extend(0x1_FFFFFF00, 0x00000100) != 0x2_00000100UL) bad |= 4;

            // Slightly behind the epoch is a step back, not a wrap. This is
            // the case a naive "smaller means wrapped" test gets wrong, and it
            // gets it wrong in the worst direction: a timestamp 300 s in the
            // future, which is a deadline nobody reaches.
            if (OS.Hal.Timer.Hpet.Extend(0x1_00000100, 0x000000FF) != 0x1_000000FFUL) bad |= 8;

            ReportProbe("hpet.extend", bad == 0, bad);
        }

        // The driver itself, over a counter we step ourselves.
        private static void CheckDriverAcrossWraps()
        {
            // A static field is a moveable variable to the compiler, so
            // address-of needs Unsafe.AsPointer rather than `&`. Nothing
            // moves it in fact — the collector does not compact, and this
            // block holds no references for it to care about.
            byte* window = (byte*)System.Runtime.CompilerServices.Unsafe.AsPointer(
                ref s_registers);

            uint* counter = (uint*)(window + OFFSET_COUNTER);
            *counter = 0x10000000;

            byte* real = OS.Hal.Timer.Hpet.UseSyntheticBase(window);
            ulong first = 0, last = 0, expected = 0;
            uint backwards = 0;
            try
            {
                first = OS.Hal.Timer.Hpet.ReadCounter();
                last = first;

                // 600 steps of 2^24 ticks: 2.3 times round the 32-bit range.
                // The step is well under half the range, which is the same
                // condition the timer tick keeps for the real counter by
                // refreshing at 15 Hz.
                for (int i = 0; i < 600; i++)
                {
                    *counter = (uint)(*counter + 0x01000000u);
                    ulong now = OS.Hal.Timer.Hpet.ReadCounter();
                    if (now <= last) backwards++;
                    last = now;
                }

                expected = first + 600UL * 0x01000000UL;
            }
            finally
            {
                OS.Hal.Timer.Hpet.RestoreRealBase(real);
            }

            bool ok = backwards == 0 && last == expected;
            ReportProbe("hpet.wrap", ok, backwards);

            if (!ok)
            {
                Log.Begin(LogLevel.Info);
                Console.Write("hpet.wrap detail: first=");
                Console.WriteHex(first);
                Console.Write(" last=");
                Console.WriteHex(last);
                Console.Write(" expected=");
                Console.WriteHex(expected);
                Log.EndLine();
            }
        }

        private static void ReportProbe(string name, bool ok, uint value)
        {
            Log.Begin(LogLevel.Info);
            Console.Write(name);
            Console.Write(": ");
            Console.Write(ok ? "ok" : "FAIL");
            Console.Write(" val=");
            Console.WriteUInt(value);
            Log.EndLine();
        }
    }
}
