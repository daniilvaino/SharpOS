using OS.Hal;

namespace OS.Kernel.Diagnostics
{
    // Exception-handling probe — gradient from "no-throw EH frame" up to
    // "real throw → catch". Each level toggles independently in Probes.cs
    // so we can pinpoint which stage of the pipeline breaks.
    //
    // Level 1 (Probes.EhTryFinallyNoThrow):
    //   try { x++; } finally { y++; }
    //   No throw, no catch. ILC emits EH info, runtime never invokes
    //   RhpCallFinallyFunclet. Same shape as ElfValidation's existing
    //   try/finally — known good.
    //
    // Level 2 (Probes.EhTryCatchNoThrow):
    //   try { x++; } catch (Exception) { x = -1; }
    //   No throw. ILC emits EH info with a TYPED clause; runtime never
    //   invokes RhpCallCatchFunclet. Probes whether ILC's catch-frame
    //   layout differs from finally-frame layout.
    //
    // Level 3 (Probes.EhTryCatchWithThrow):
    //   try { throw new InvalidOperationException("..."); }
    //   catch (Exception e) { return e.Message.Length; }
    //   REAL test. Currently expected to halt — RhpThrowEx in
    //   ExceptionEngine.cs prints message and spins. This probe is the
    //   target of the upcoming unwinder work; flipping it green is the
    //   Phase 1 closure criterion.
    //
    // Runtime-dependent inputs prevent ILC from constant-folding the
    // bodies away.
    internal static class EhProbe
    {
        public static void Run()
        {
            if (Probes.EhTryFinallyNoThrow)
                ReportLevel("eh L1 try/finally no-throw", TryFinallyNoThrow(1));

            if (Probes.EhTryCatchNoThrow)
                ReportLevel("eh L2 try/catch no-throw", TryCatchNoThrow(2));

            if (Probes.EhTryCatchWithThrow)
            {
                Log.Write(LogLevel.Info, "eh L3 try/catch with throw — expected to halt unless unwinder is in place");
                int v = TryCatchWithThrow(3);
                ReportLevel("eh L3 try/catch with throw", v);
            }
        }

        // x is non-const at compile time (depends on caller arg) so ILC
        // can't reduce the try body away.
        private static int TryFinallyNoThrow(int seed)
        {
            int x = seed;
            int y = 10;
            try
            {
                x = x + seed;       // 1+1=2 for seed=1
            }
            finally
            {
                y = y + seed;       // 10+1=11
            }
            return x * 100 + y;     // 2*100 + 11 = 211 for seed=1
        }

        private static int TryCatchNoThrow(int seed)
        {
            int x = seed;
            try
            {
                x = x + seed;       // 2+2=4 for seed=2
            }
            catch (System.Exception)
            {
                x = -1;
            }
            return x;               // 4 for seed=2
        }

        private static int TryCatchWithThrow(int seed)
        {
            try
            {
                System.Exception e = new System.InvalidOperationException("ehprobe");
                throw e;
            }
            catch (System.Exception ex)
            {
                // If we ever land here, the unwinder works. Return the
                // length of the message so we can verify Exception.Message
                // round-trips through the dispatch path.
                string m = ex.Message;
                return m == null ? -1 : m.Length;   // expected: 7 ("ehprobe")
            }
        }

        private static void ReportLevel(string label, int value)
        {
            Log.Begin(LogLevel.Info);
            Console.Write(label);
            Console.Write(": val=");
            Console.WriteUIntRaw((uint)value);
            Log.EndLine();
        }
    }
}
