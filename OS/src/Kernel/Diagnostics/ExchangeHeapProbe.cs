using OS.Hal;
using OS.Kernel.Memory;

namespace OS.Kernel.Diagnostics
{
    // The exchange heap's own properties (pipe_plan.md "Подготовить под
    // трубы", item 1): outside every collector's segments, below 4 GiB, zeroed,
    // owned, reused, refusing what is not a live block of it, and handing back
    // everything an owner held.
    internal static unsafe class ExchangeHeapProbe
    {
        private const uint FakeOwner = 0x7E570001;

        public static void Run()
        {
            byte* a = (byte*)ExchangeHeap.Allocate(100, ExchangeHeap.OwnerKernel);
            bool allocated = a != null;
            Report("exchange: allocate", allocated, 0);
            if (!allocated) return;

            bool outside = SharpOS.Std.NoRuntime.GcHeap.FindSegmentContaining((nint)a) == null;
            bool low = (ulong)a < 0x1_0000_0000UL;
            bool zeroed = true;
            for (int i = 0; i < 100; i++) if (a[i] != 0) { zeroed = false; break; }
            Report("exchange: outside the collector, below 4 GiB, zeroed", outside && low && zeroed, 0);
            Report("exchange: owner and size", ExchangeHeap.OwnerOf(a) == ExchangeHeap.OwnerKernel
                                               && ExchangeHeap.SizeOf(a) >= 100, (uint)ExchangeHeap.SizeOf(a));

            for (int i = 0; i < 100; i++) a[i] = 0x5A;
            bool freed = ExchangeHeap.Free(a);
            bool doubleRefused = !ExchangeHeap.Free(a);
            bool strangerRefused = !ExchangeHeap.Free((void*)0x12345678) && !ExchangeHeap.Free(a + 8);
            Report("exchange: free, double free and stray pointers refused", freed && doubleRefused && strangerRefused, 0);

            byte* again = (byte*)ExchangeHeap.Allocate(100, ExchangeHeap.OwnerKernel);
            Report("exchange: freed block reused, zeroed again", again == a && again[0] == 0, 0);
            ExchangeHeap.Free(again);

            byte* large = (byte*)ExchangeHeap.Allocate(100_000, ExchangeHeap.OwnerKernel);
            bool largeOk = large != null && ExchangeHeap.SizeOf(large) >= 100_000 && large[99_999] == 0;
            if (large != null) large[99_999] = 1;
            Report("exchange: large block", largeOk, (uint)(large == null ? 0 : ExchangeHeap.SizeOf(large)));
            ExchangeHeap.Free(large);

            void* x = ExchangeHeap.Allocate(64, FakeOwner);
            void* y = ExchangeHeap.Allocate(5000, FakeOwner);
            void* z = ExchangeHeap.Allocate(200_000, FakeOwner);
            ulong liveBefore = ExchangeHeap.LiveBlocks;
            int released = ExchangeHeap.ReleaseOwner(FakeOwner);
            bool gone = ExchangeHeap.OwnerOf(x) == 0 && ExchangeHeap.OwnerOf(y) == 0 && ExchangeHeap.OwnerOf(z) == 0;
            Report("exchange: everything an owner held goes back",
                   released == 3 && gone && ExchangeHeap.LiveBlocks == liveBefore - 3, (uint)released);
        }

        private static void Report(string name, bool ok, uint value)
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
