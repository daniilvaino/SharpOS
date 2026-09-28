using System.Runtime.CompilerServices;
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

            ReportMemoryType();
            MeasureStoreWidth(fb, w, h, stride, hz);

            TryImproveWrites();
        }

        /// <summary>
        /// What the firmware says the framebuffer range is, and what the PAT
        /// makes of it.
        /// </summary>
        /// <remarks>
        /// The page tables are not the answer on their own. A page's memory
        /// type is the stronger of what the PAT entry says and what the range
        /// registers say, so a leaf that reads write-back can still be running
        /// uncached or write-through because the firmware marked the aperture
        /// that way — and then the throughput numbers below look like a slow
        /// card rather than a mapping nobody chose.
        ///
        /// Printed rather than reasoned about because the reasoning went wrong
        /// twice on the same log: writes that scale exactly with the width of a
        /// store mean each store is its own bus transaction, which is
        /// uncached — but reads on the same range were cached and fast, which
        /// uncached is not. Both cannot be inferred from throughput. This is
        /// two MSR reads and it just says which.
        /// </remarks>
        private static void ReportMemoryType()
        {
            const uint IA32_MTRRCAP = 0xFE;
            const uint IA32_MTRR_DEF_TYPE = 0x2FF;
            const uint IA32_MTRR_PHYSBASE0 = 0x200;
            const uint IA32_PAT = 0x277;

            if (!X64Asm.ReadMsr(IA32_MTRRCAP, out ulong cap) ||
                !X64Asm.ReadMsr(IA32_MTRR_DEF_TYPE, out ulong def))
            {
                Console.WriteLine("[fbperf] range: MSRs unreadable");
                return;
            }

            ulong addr = Framebuffer.BaseAddress;

            Console.Write("[fbperf] range: mtrr=");
            if ((def & (1UL << 11)) == 0)
            {
                // Disabled range registers leave every page to its PAT entry.
                Console.Write("off");
            }
            else
            {
                // The last matching range wins in the sense that overlaps are
                // firmware's problem; printing each hit is more use than
                // resolving them, since an aperture normally matches one.
                uint count = (uint)(cap & 0xFF);
                bool matched = false;
                for (uint i = 0; i < count; i++)
                {
                    if (!X64Asm.ReadMsr(IA32_MTRR_PHYSBASE0 + i * 2, out ulong basePhys)) break;
                    if (!X64Asm.ReadMsr(IA32_MTRR_PHYSBASE0 + i * 2 + 1, out ulong mask)) break;
                    if ((mask & (1UL << 11)) == 0) continue;          // range not valid

                    ulong bits = mask & 0x000FFFFFFFFFF000UL;
                    if ((addr & bits) != (basePhys & bits)) continue;

                    if (matched) Console.Write("+");
                    WriteMemoryType((uint)(basePhys & 0xFF));
                    matched = true;
                }
                if (!matched)
                {
                    Console.Write("none(def=");
                    WriteMemoryType((uint)(def & 0x07));
                    Console.Write(")");
                }
            }

            // Entry 1 is the one MemoryKind.Framebuffer selects, and the one
            // TryEnableWriteCombining rewrites; entry 0 is everything else.
            if (X64Asm.ReadMsr(IA32_PAT, out ulong pat))
            {
                Console.Write(" pat0=");
                WriteMemoryType((uint)(pat & 0xFF));
                Console.Write(" pat1=");
                WriteMemoryType((uint)((pat >> 8) & 0xFF));
            }

            Console.WriteLine("");
        }

        private static void WriteMemoryType(uint type)
        {
            switch (type)
            {
                case 0: Console.Write("UC"); break;
                case 1: Console.Write("WC"); break;
                case 4: Console.Write("WT"); break;
                case 5: Console.Write("WP"); break;
                case 6: Console.Write("WB"); break;
                case 7: Console.Write("UC-"); break;
                default: Console.Write("?"); Console.WriteUInt(type); break;
            }
        }

        /// <summary>
        /// The same fill, written four, eight and sixteen bytes at a time, and
        /// then once more in the shape a glyph writes it.
        /// </summary>
        /// <remarks>
        /// One number cannot tell "this memory is slow" from "our stores are
        /// small", and those have completely different fixes. If throughput
        /// climbs with the width of a store, the cost is per access and the
        /// answer is to write wider — glyphs a scanline at a time instead of a
        /// pixel at a time. If it stays flat, the bus is the ceiling and no
        /// amount of rewriting the renderer will move it.
        ///
        /// Worth measuring rather than reasoning about, because the obvious
        /// explanation — read-for-ownership on a write-back mapping, where a
        /// four-byte store first reads the whole 64-byte line back from video
        /// memory — predicts a large jump, and the equally obvious one, an
        /// uncached range the firmware set through the range registers,
        /// predicts none.
        /// </remarks>
        private static void MeasureStoreWidth(uint* fb, uint w, uint h, uint stride, ulong hz)
        {
            ulong bytes = (ulong)w * h * 4;

            ulong t0 = OS.Hal.Timer.Hpet.ReadCounter();
            for (uint y = 0; y < h; y++)
            {
                uint* row = fb + (ulong)y * stride;
                for (uint x = 0; x < w; x++) row[x] = 0x00181818;
            }
            ulong t1 = OS.Hal.Timer.Hpet.ReadCounter();

            for (uint y = 0; y < h; y++)
            {
                ulong* row = (ulong*)(fb + (ulong)y * stride);
                uint pairs = w / 2;
                for (uint x = 0; x < pairs; x++) row[x] = 0x0018181800181818UL;
            }
            ulong t2 = OS.Hal.Timer.Hpet.ReadCounter();

            // Sixteen bytes, built by reading a vector back rather than with
            // Create: this is the kernel, and the fewer intrinsics on the path
            // the fewer ways the measurement itself can be the thing that is
            // slow.
            uint* pattern = stackalloc uint[4];
            pattern[0] = 0x00181818; pattern[1] = 0x00181818;
            pattern[2] = 0x00181818; pattern[3] = 0x00181818;
            System.Runtime.Intrinsics.Vector128<uint> wide =
                Unsafe.ReadUnaligned<System.Runtime.Intrinsics.Vector128<uint>>(pattern);

            for (uint y = 0; y < h; y++)
            {
                uint* row = fb + (ulong)y * stride;
                uint quads = w / 4;
                for (uint x = 0; x < quads; x++)
                    Unsafe.WriteUnaligned(row + x * 4, wide);
            }
            ulong t3 = OS.Hal.Timer.Hpet.ReadCounter();

            // The same bytes again, in the order a glyph writes them: eight
            // pixels on one scanline, then the next scanline, seven thousand
            // bytes further on. Thirty-two bytes is half a cache line, and on a
            // write-back mapping half a line has to be read back before it can
            // be written — from memory whose reads were measured at a fiftieth
            // of its writes.
            //
            // This is the measurement that separates the two readings of a slow
            // paint. If this rate matches the contiguous one, the row loop's
            // fourteen milliseconds are being spent deciding which cells to
            // draw, and the palette lookups and the shadow compare are where to
            // look. If it collapses, the cost is the shape of the writes, no
            // amount of deciding faster will help, and the fix is to gather a
            // run of cells and put it out in whole lines.
            uint cellRows = h / 16;
            uint cellCols = w / 8;
            for (uint cy = 0; cy < cellRows; cy++)
            {
                for (uint cx = 0; cx < cellCols; cx++)
                {
                    uint* cell = fb + (ulong)(cy * 16) * stride + cx * 8;
                    for (uint line = 0; line < 16; line++)
                    {
                        ulong* p = (ulong*)(cell + (ulong)line * stride);
                        p[0] = 0x0018181800181818UL;
                        p[1] = 0x0018181800181818UL;
                        p[2] = 0x0018181800181818UL;
                        p[3] = 0x0018181800181818UL;
                    }
                }
            }
            ulong t4 = OS.Hal.Timer.Hpet.ReadCounter();

            // Bytes actually covered by the cell walk: the last partial row and
            // column of the screen are not touched, and charging them to the
            // rate would report the glyph shape as slower than it is.
            ulong cellBytes = (ulong)cellRows * 16 * cellCols * 8 * 4;

            Console.Write("[fbperf] store width: 4B=");
            Console.WriteULong(Rate(bytes, t1 - t0, hz));
            Console.Write(" 8B=");
            Console.WriteULong(Rate(bytes, t2 - t1, hz));
            Console.Write(" 16B=");
            Console.WriteULong(Rate(bytes, t3 - t2, hz));
            Console.Write(" cell8x16=");
            Console.WriteULong(Rate(cellBytes, t4 - t3, hz));
            Console.WriteLine(" MiB/s");
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
