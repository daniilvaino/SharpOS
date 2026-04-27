using OS.Hal;
using OS.Boot.EH;

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
    internal static unsafe class EhProbe
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

            if (Probes.EhExceptionShape)
                ReportLevel("eh L4 exception shape", ExceptionShape());

            if (Probes.EhRootWalk)
                ReportLevel("eh L5 .pdata + root walk", RootWalk());

            if (Probes.EhDecode)
                ReportLevel("eh L6 ehInfo varint decode", EhDecode());
        }

        // L4 — Phase 1 step 1 gate. Verifies that the full Exception
        // contract (message round-trip via virtual property + 6 missing
        // derived types via runtime type identity) is in place. No throw
        // happens, so this never depends on the unwinder. A bitmask is
        // returned so a partial pass (e.g. some derived types missing)
        // shows exactly which.
        //
        // Expected: 127 (all 7 bits set).
        private static int ExceptionShape()
        {
            int mask = 0;

            System.Exception e1 = new System.InvalidOperationException("m");
            if (e1.Message == "m") mask |= 1;

            if (new System.NullReferenceException() is System.NullReferenceException) mask |= 2;
            if (new System.OverflowException() is System.OverflowException) mask |= 4;
            if (new System.DivideByZeroException() is System.DivideByZeroException) mask |= 8;
            if (new System.InvalidCastException() is System.InvalidCastException) mask |= 16;
            if (new System.ArrayTypeMismatchException() is System.ArrayTypeMismatchException) mask |= 32;
            if (new System.NotImplementedException() is System.NotImplementedException) mask |= 64;

            return mask;
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

        // L5 — Phase 1 step 2 gate. Verifies that .pdata lookup +
        // funclet -> ROOT backward walk works on our actual binary.
        // Doesn't require the EH info decoder (step 3) — finds funclet
        // records by linear scan of unwindBlockFlags trailer bytes.
        //
        // Score bits:
        //   1 = binary search finds a record for the probe's own IP.
        //   2 = at least one funclet record (kind != ROOT) was found
        //       within the first MaxScan records.
        //   4 = WalkToRoot from that funclet returns a valid index
        //       BELOW the funclet (root must precede its funclets).
        //
        // Expected: 7. -1..-3 indicate which bit failed first so the
        // failure is localized.
        private static int RootWalk()
        {
            if (!CoffRuntimeFunctionTable.IsInitialized) return -1;
            int count = CoffRuntimeFunctionTable.Count;
            if (count <= 0) return -2;

            int score = 0;

            // Bit 1: binary search for an IP inside this very method.
            // Take the address of RootWalk itself via a function pointer.
            delegate*<int> selfPtr = &RootWalk;
            byte* selfIp = (byte*)selfPtr;
            if (CoffMethodLookup.TryFindMethod(selfIp, out CoffMethodLookup.MethodInfo selfInfo))
            {
                score |= 1;
            }

            // Bit 2: locate the first funclet record by linear scan of
            // unwindBlockFlags. Bound the scan so we don't read every
            // record on every probe boot.
            const int MaxScan = 200;
            int limit = count < MaxScan ? count : MaxScan;
            int firstFuncletIdx = -1;
            for (int i = 0; i < limit; i++)
            {
                RuntimeFunction* rf = CoffRuntimeFunctionTable.GetRecord(i);
                if (rf == null) continue;
                byte flags = CoffMethodLookup.ReadUnwindBlockFlags(rf);
                int kind = flags & CoffMethodLookup.UBF_FUNC_KIND_MASK;
                if (kind != CoffMethodLookup.UBF_FUNC_KIND_ROOT)
                {
                    firstFuncletIdx = i;
                    break;
                }
            }
            if (firstFuncletIdx >= 0) score |= 2;

            // Bit 4: walk to root from that funclet. Root must come
            // before the funclet in .pdata (funclets are emitted right
            // after their parent body).
            if (firstFuncletIdx > 0)
            {
                int rootIdx = CoffMethodLookup.WalkToRoot(firstFuncletIdx);
                if (rootIdx >= 0 && rootIdx < firstFuncletIdx)
                    score |= 4;
            }

            // Diagnostic line — always emit, useful for sanity reading
            // even when score is full.
            Log.Begin(LogLevel.Info);
            Console.Write("  l5-diag: count=");
            Console.WriteUIntRaw((uint)count);
            Console.Write(" selfIp=0x");
            Console.WriteHexRaw((ulong)(nuint)selfIp, 16);
            Console.Write(" selfRecord=");
            if ((score & 1) != 0)
                Console.WriteUIntRaw((uint)selfInfo.RootIndex);
            else
                Console.Write("none");
            Console.Write(" firstFunclet=");
            if (firstFuncletIdx >= 0)
                Console.WriteUIntRaw((uint)firstFuncletIdx);
            else
                Console.Write("none");
            Log.EndLine();

            return score;
        }

        // L6 — Phase 1 step 3 gate. Three test methods, each carrying
        // exactly one EH clause of a different kind. Sum across all
        // three: 100*filterCount + 10*finallyCount + typedCount = 111.
        //
        // Note: ILC encodes IL `finally` as kind=Fault in the EH info
        // table (no separate finally kind). Step 5+ dispatcher invokes
        // them on every unwind through their try region; normal exit
        // paths inline the body. So `finallyCount` here counts Fault.
        //
        // Methods are NoInlining + call into opaque helpers so ILC
        // can't prove the try body is throw-free and elide the EH info.
        private static int EhDecode()
        {
            if (!CoffRuntimeFunctionTable.IsInitialized) return -1;

            int typedCount = 0;
            int finallyCount = 0;
            int filterCount = 0;

            delegate*<int> ptrA = &MethodA_TryFinally;
            delegate*<int> ptrB = &MethodB_TryCatch;
            delegate*<int> ptrC = &MethodC_TryCatchWhen;

            CountClauses((byte*)ptrA, ref typedCount, ref finallyCount, ref filterCount);
            CountClauses((byte*)ptrB, ref typedCount, ref finallyCount, ref filterCount);
            CountClauses((byte*)ptrC, ref typedCount, ref finallyCount, ref filterCount);

            Log.Begin(LogLevel.Info);
            Console.Write("  l6-diag: typed=");
            Console.WriteUIntRaw((uint)typedCount);
            Console.Write(" finally=");
            Console.WriteUIntRaw((uint)finallyCount);
            Console.Write(" filter=");
            Console.WriteUIntRaw((uint)filterCount);
            Log.EndLine();

            return 100 * filterCount + 10 * finallyCount + typedCount;
        }

        private static void CountClauses(byte* methodIp,
            ref int typed, ref int finally_, ref int filter)
        {
            if (!CoffEhDecoder.EhEnumInit(methodIp, out CoffEhDecoder.EHEnum state, out byte* _))
                return;

            while (CoffEhDecoder.EhEnumNext(ref state, out CoffEhDecoder.RhEHClause clause))
            {
                switch (clause.Kind)
                {
                    case CoffEhDecoder.ClauseKind.Typed:  typed++; break;
                    case CoffEhDecoder.ClauseKind.Fault:  finally_++; break;
                    case CoffEhDecoder.ClauseKind.Filter: filter++; break;
                }
            }
        }

        // Opaque helper — ILC can't prove this can't throw, so EH info
        // for callers' try blocks is preserved.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int Opaque(int v) => v + 1;

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int MethodA_TryFinally()
        {
            int x = 0;
            int y = 0;
            try { x = Opaque(5); }
            finally { y = Opaque(10); }
            return x + y;
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int MethodB_TryCatch()
        {
            int x = 0;
            try { x = Opaque(7); }
            catch (System.InvalidOperationException) { x = -1; }
            return x;
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int MethodC_TryCatchWhen()
        {
            int x = 0;
            try { x = Opaque(11); }
            catch (System.Exception ex) when (ex.Message != null) { x = -1; }
            return x;
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
