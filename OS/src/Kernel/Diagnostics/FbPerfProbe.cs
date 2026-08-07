using OS.Hal;

namespace OS.Kernel.Diagnostics
{
    // How fast the framebuffer actually is, in numbers.
    //
    // Two machines showed the same code as "35 fps" and "a couple of seconds
    // per screen", and nothing in the log said why: resolution and stride were
    // known, the memory type behind them was not. Guessing between page
    // attributes, MTRRs and sheer pixel count cost several hardware rounds, so
    // it gets measured instead.
    //
    // Both directions are timed separately on purpose. Writes and reads have
    // completely different costs depending on the caching type — write-combining
    // makes writes fast and reads terrible — and the console scrolls by reading
    // the screen, so a fill number alone would hide the slow half.
    internal static unsafe class FbPerfProbe
    {
        // Kept so drawing policy can be decided by measurement instead of by
        // assumption: whether reading the screen back is affordable differs by
        // two orders of magnitude between machines.
        public static ulong FillMibPerSecond;
        public static ulong ScrollMibPerSecond;

        public static void Run()
        {
            if (!Probes.FbPerf) return;
            if (!Framebuffer.IsAvailable) return;
            if (!OS.Hal.Timer.Hpet.IsInitialized)
            {
                Console.WriteLine("[fbperf] no timer — skipped");
                return;
            }

            uint w = Framebuffer.Width;
            uint h = Framebuffer.Height;
            uint stride = Framebuffer.Stride;
            uint* fb = (uint*)Framebuffer.BaseAddress;
            ulong pixels = (ulong)w * h;

            ulong hz = OS.Hal.Timer.Hpet.FrequencyHz;

            // Fill: pure writes.
            ulong t0 = OS.Hal.Timer.Hpet.ReadCounter();
            for (uint y = 0; y < h; y++)
            {
                uint* row = fb + (ulong)y * stride;
                for (uint x = 0; x < w; x++) row[x] = 0x00101010;
            }
            ulong t1 = OS.Hal.Timer.Hpet.ReadCounter();

            // Scroll: reads and writes together, which is what the console does.
            for (uint y = 0; y + 1 < h; y++)
            {
                uint* dst = fb + (ulong)y * stride;
                uint* src = dst + stride;
                for (uint x = 0; x < w; x++) dst[x] = src[x];
            }
            ulong t2 = OS.Hal.Timer.Hpet.ReadCounter();

            FillMibPerSecond = Rate(pixels * 4, t1 - t0, hz);
            ScrollMibPerSecond = Rate(pixels * 8, t2 - t1, hz);

            Console.Write("[fbperf] ");
            Console.WriteUInt(w); Console.Write("x"); Console.WriteUInt(h);
            Console.Write(" fill="); Console.WriteULong(FillMibPerSecond); Console.Write("MiB/s");
            Console.Write(" scroll="); Console.WriteULong(ScrollMibPerSecond); Console.Write("MiB/s");

            // The page attributes actually in force: bit 3 write-through,
            // bit 4 cache-disable. This is what separates "too many pixels"
            // from "every store crawls to the card".
            Console.Write(" pte=0x");
            Console.WriteHex(
                OS.Kernel.Paging.X64PageTable.TryGetKernelLeafPte(
                    Framebuffer.BaseAddress, out ulong pte) ? pte : 0xDEADUL);
            Console.WriteLine("");

            TryImproveWrites();
        }

        // Where reads are already hopeless, trade them away for faster writes.
        //
        // Uncached video memory costs both directions; write-combining is the
        // one attribute the CPU lets us loosen towards, and it only hurts
        // reads — which at a few MiB/s are not being used for anything
        // anyway (the console stopped scrolling by reading at this speed).
        // Machines with cacheable video memory are left alone: for them the
        // trade would be a straight loss.
        private static void TryImproveWrites()
        {
            if (ScrollMibPerSecond == 0 || ScrollMibPerSecond >= 100) return;
            if (!Framebuffer.TrySwitchToWriteCombining())
            {
                Console.WriteLine("[fbperf] slow reads, but write-combining unavailable");
                return;
            }

            uint w = Framebuffer.Width;
            uint h = Framebuffer.Height;
            uint stride = Framebuffer.Stride;
            uint* fb = (uint*)Framebuffer.BaseAddress;
            ulong hz = OS.Hal.Timer.Hpet.FrequencyHz;

            ulong t0 = OS.Hal.Timer.Hpet.ReadCounter();
            for (uint y = 0; y < h; y++)
            {
                uint* row = fb + (ulong)y * stride;
                for (uint x = 0; x < w; x++) row[x] = 0x00202020;
            }
            ulong t1 = OS.Hal.Timer.Hpet.ReadCounter();

            FillMibPerSecond = Rate((ulong)w * h * 4, t1 - t0, hz);
            Console.Write("[fbperf] write-combining on: fill=");
            Console.WriteULong(FillMibPerSecond);
            Console.WriteLine("MiB/s");
        }

        // Bytes moved per elapsed tick, in MiB/s. Integers, because the
        // differences that matter here are factors of ten, not percentages.
        private static ulong Rate(ulong bytes, ulong ticks, ulong hz)
        {
            if (ticks == 0 || hz == 0) return 0;
            // Scale before dividing: a screenful is only a few MiB, and
            // rounding it down first would turn a real number into noise.
            return bytes * hz / ticks / (1024 * 1024);
        }
    }
}
