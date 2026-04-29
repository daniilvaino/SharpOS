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
        // ── Step 5.5a observable statics ──────────────────────────────
        // Test harness shellcodes write to these via &s_X imm64 patches.
        // Probe5_5_PrintResults reads them after continuation reached.
        private static ulong s_5_5_handler_called;
        private static ulong s_5_5_observed_rcx;
        private static ulong s_5_5_observed_rdx;
        private static ulong s_5_5_continuation_called;
        private static ulong s_5_5_observed_rsp;

        // Called from BootSequence Phase 2 — patches test harness stubs
        // with shellcode что writes to our statics.
        public static void InstallStep5_5TestHarness()
        {
            byte** addrHandlerCalled = (byte**)System.Runtime.CompilerServices.Unsafe.AsPointer(ref s_5_5_handler_called);
            byte** addrObservedRcx = (byte**)System.Runtime.CompilerServices.Unsafe.AsPointer(ref s_5_5_observed_rcx);
            byte** addrObservedRdx = (byte**)System.Runtime.CompilerServices.Unsafe.AsPointer(ref s_5_5_observed_rdx);
            byte** addrContCalled = (byte**)System.Runtime.CompilerServices.Unsafe.AsPointer(ref s_5_5_continuation_called);
            byte** addrObservedRsp = (byte**)System.Runtime.CompilerServices.Unsafe.AsPointer(ref s_5_5_observed_rsp);

            delegate*<void> printFn = &Probe5_5_PrintResults;
            void* printAddr = (void*)printFn;

            bool ok = OS.Boot.EH.Stub5_5_Patcher.TryInstall(
                addrHandlerCalled, addrObservedRcx, addrObservedRdx,
                addrContCalled, addrObservedRsp, printAddr);

            Log.Write(ok ? LogLevel.Info : LogLevel.Warn,
                ok ? "5.5a test harness installed" : "5.5a test harness install failed");
        }

        public static void Run()
        {
            if (Probes.EhTryFinallyNoThrow)
                ReportLevel("eh L1 try/finally no-throw", TryFinallyNoThrow(1));

            if (Probes.EhTryCatchNoThrow)
                ReportLevel("eh L2 try/catch no-throw", TryCatchNoThrow(2));

            if (Probes.EhExceptionShape)
                ReportLevel("eh L4 exception shape", ExceptionShape());

            if (Probes.EhRootWalk)
                ReportLevel("eh L5 .pdata + root walk", RootWalk());

            if (Probes.EhDecode)
                ReportLevel("eh L6 ehInfo varint decode", EhDecode());

            if (Probes.EhFrameWalk)
                ReportLevel("eh L7 frame walk", FrameWalk());

            if (Probes.EhEnumLive)
                ReportLevel("eh 5.3 enum-live", EnumLiveDirect());

            if (Probes.EhTryCatchWithThrow)
            {
                int v = TryCatchWithThrow(3);
                ReportLevel("eh L8 typed catch (real dispatch)", v);
            }

            if (Probes.EhRethrowChain)
            {
                int v = RethrowChain();
                ReportLevel("eh L9 rethrow chain", v);
            }

            if (Probes.EhTryCatchFinally)
            {
                int v = TryCatchFinally();
                ReportLevel("eh L10 finally + catch", v);
            }

            if (Probes.EhFilter)
            {
                int v = FilterClause();
                ReportLevel("eh L11 catch-when filter", v);
            }

            if (Probes.EhHwFault)
            {
                int v = HwFault();
                ReportLevel("eh L13 hw fault (null deref)", v);
            }

            if (Probes.EhStackTrace)
            {
                int v = StackTraceCheck();
                ReportLevel("eh L14 stack trace populated", v);
            }

            if (Probes.EhCollidedUnwind)
            {
                int v = CollidedUnwind();
                ReportLevel("eh L15 collided unwind", v);
            }

            if (Probes.EhMultiFrameFinally)
            {
                int v = MultiFrameFinally();
                ReportLevel("eh L16 multi-frame finally", v);
            }

            if (Probes.EhMultiFrameStackTrace)
            {
                int v = MultiFrameStackTrace();
                ReportLevel("eh L17 multi-frame stack trace", v);
            }

            if (Probes.EhCatchFuncletProbe)
            {
                Log.Write(LogLevel.Info,
                    "eh 5.5a: probing RhpCallCatchFunclet standalone (will halt in PrintResults)");
                Probe5_5();   // never returns
            }

            if (Probes.EhIngressThrow)
            {
                Log.Write(LogLevel.Info,
                    "eh L8.ingress: triggering throw -> RhpThrowEx shellcode -> RhpTest_ThrowIngress (will halt)");
                IngressThrow();   // never returns
            }
        }

        // Step 5.5a smoke: standalone test of RhpCallCatchFunclet.
        // Builds fake REGDISPLAY + ExInfo on stack, calls patched
        // RhpCallCatchFunclet shellcode. Test handler captures incoming
        // RCX/RDX into statics, returns continuation IP. Continuation
        // shellcode captures observed RSP, jumps to Probe5_5_PrintResults
        // which reads статикs and prints + halts.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void Probe5_5()
        {
            // Saved nonvol values — handler restore loop reads pNonvol[N]
            // pointer and dereferences it. We point each pNonvol to a local
            // here. Recognizable constants help spot wrong restores.
            ulong savedRbx = 0x1111111111111111UL;
            ulong savedRbp = 0x2222222222222222UL;
            ulong savedRsi = 0x3333333333333333UL;
            ulong savedRdi = 0x4444444444444444UL;
            ulong savedR12 = 0xCCCCCCCCCCCCCCCCUL;
            ulong savedR13 = 0xDDDDDDDDDDDDDDDDUL;
            ulong savedR14 = 0xEEEEEEEEEEEEEEEEUL;
            ulong savedR15 = 0xFFFFFFFFFFFFFFFFUL;

            OS.Boot.EH.RegDisplay rd = default;
            rd.pRbx = &savedRbx;
            rd.pRbp = &savedRbp;
            rd.pRsi = &savedRsi;
            rd.pRdi = &savedRdi;
            rd.pR12 = &savedR12;
            rd.pR13 = &savedR13;
            rd.pR14 = &savedR14;
            rd.pR15 = &savedR15;

            // Fake establisher SP — pick a stack region 4KB below current
            // rsp. This region is callable stack memory (UEFI-allocated),
            // writable, аligned. Continuation lands here with rsp = this
            // value, has space for its own pushes/locals.
            byte localMarker;
            ulong fakeSP = (ulong)(nint)(&localMarker) - 0x1000UL;
            fakeSP &= ~0xFUL;   // 16-byte align
            rd.SP = fakeSP;

            // Fake ExInfo — link to current head, install ourselves.
            OS.Boot.EH.ExInfo fakeEx = default;
            fakeEx.PrevExInfo = (OS.Boot.EH.ExInfo*)OS.Boot.EH.ExInfoHead.s_head;
            OS.Boot.EH.ExInfoHead.s_head = (System.IntPtr)(&fakeEx);

            // Fake exception object — just a recognizable byte pattern.
            ulong fakeException = 0xEEEEEEEEEEEEEEEEUL;
            byte* fakeExceptionPtr = (byte*)&fakeException;

            // Resolve handler IP and main shellcode.
            byte* handlerIp = (byte*)OS.Boot.EH.TestCatchHandlerStub.GetMethodAddress();
            delegate* unmanaged<byte*, byte*, OS.Boot.EH.RegDisplay*, OS.Boot.EH.ExInfo*, void> catchFn =
                (delegate* unmanaged<byte*, byte*, OS.Boot.EH.RegDisplay*, OS.Boot.EH.ExInfo*, void>)
                OS.Boot.EH.CallCatchFuncletStub.GetMethodAddress();

            Log.Begin(LogLevel.Info);
            Console.Write("5.5a probe entering: handler=0x");
            Console.WriteHexRaw((ulong)(nuint)handlerIp, 16);
            Console.Write(" rd=0x");
            Console.WriteHexRaw((ulong)(nuint)(&rd), 16);
            Console.Write(" exInfo=0x");
            Console.WriteHexRaw((ulong)(nuint)(&fakeEx), 16);
            Console.Write(" fakeSP=0x");
            Console.WriteHexRaw(fakeSP, 16);
            Log.EndLine();

            // Tail call — never returns.
            catchFn(fakeExceptionPtr, handlerIp, &rd, &fakeEx);

            // If we reach here, shellcode returned normally — bug.
            Console.Write("5.5a: catchFn returned (BUG — should jmp to continuation)\r\n");
            while (true) { }
        }

        // Continuation shellcode tail-jumps here via `mov rax, &this; jmp rax`.
        // RSP at entry = REGDISPLAY.SP (our fakeSP). Method's prolog pushes
        // its own nonvols at this fake stack region. Body reads observable
        // statics + prints results. Halts at end (jmp without return addr —
        // never reach our epilog/ret).
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static void Probe5_5_PrintResults()
        {
            Log.Begin(LogLevel.Info);
            Console.Write("\r\n*** 5.5a results ***\r\n");
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("  handler_called  = 0x");
            Console.WriteHexRaw(s_5_5_handler_called, 16);
            Console.Write("  (expected 0xAAAA)");
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("  observed_rcx    = 0x");
            Console.WriteHexRaw(s_5_5_observed_rcx, 16);
            Console.Write("  (expected = REGDISPLAY.SP from probe seam)");
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("  observed_rdx    = 0x");
            Console.WriteHexRaw(s_5_5_observed_rdx, 16);
            Console.Write("  (expected = pointer to fakeException 0xEE...EE local)");
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("  cont_called     = 0x");
            Console.WriteHexRaw(s_5_5_continuation_called, 16);
            Console.Write("  (expected 0xBBBB)");
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("  observed_rsp    = 0x");
            Console.WriteHexRaw(s_5_5_observed_rsp, 16);
            Console.Write("  (expected = REGDISPLAY.SP = observed_rcx)");
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("  s_head_now      = 0x");
            Console.WriteHexRaw((ulong)(long)OS.Boot.EH.ExInfoHead.s_head, 16);
            Console.Write("  (expected = original head before probe linked fakeEx)");
            Log.EndLine();

            Console.Write("*** halting (5.5a probe complete) ***\r\n");
            while (true) { }
        }

        // Step 5.3 probe A — Direct enumeration without throw.
        // Captures live context from INSIDE a try region of a known
        // method, inits SFI, walks EH enumerator, logs each clause.
        // Sanity: codeOffset (= ControlPC - methodStart) must fall within
        // the typed clause's [tryStart, tryEnd) range.
        //
        // Score bits:
        //   1 = SFI init succeeded
        //   2 = EhEnumInit succeeded (method has HAS_EHINFO trailer)
        //   4 = at least 1 clause enumerated
        //   8 = ControlPC's codeOffset falls within a clause's try range
        // Expected: 15 (all 4 bits).
        private static int EnumLiveDirect()
        {
            if (!CoffRuntimeFunctionTable.IsInitialized) return -1;

            PalLimitedContext ctx = default;
            // Run through method that captures context FROM INSIDE try block.
            EnumLive_TryHost(&ctx);

            int score = 0;

            // Init SFI from captured context.
            StackFrameIterator iter = default;
            StackFrameIteratorOps.Init(&iter, &ctx);
            score |= 1;

            // Enumerate clauses for the frame containing ControlPC.
            byte* methodStart;
            CoffEhDecoder.EHEnum enumState;
            if (!CoffEhDecoder.EhEnumInit((byte*)iter.ControlPC, out enumState, out methodStart))
            {
                Log.Write(LogLevel.Warn, "  5.3-A: EhEnumInit returned false (no EH info on this frame)");
                return score;
            }
            score |= 2;

            uint codeOffset = (uint)((nint)iter.ControlPC - (nint)methodStart);

            Log.Begin(LogLevel.Info);
            Console.Write("  5.3-A diag: methodStart=0x");
            Console.WriteHexRaw((ulong)(nuint)methodStart, 16);
            Console.Write(" controlPC=0x");
            Console.WriteHexRaw(iter.ControlPC, 16);
            Console.Write(" codeOffset=0x");
            Console.WriteHexRaw(codeOffset, 8);
            Console.Write(" nClauses=");
            Console.WriteUIntRaw(enumState.TotalClauses);
            Log.EndLine();

            int clauseIdx = 0;
            while (CoffEhDecoder.EhEnumNext(ref enumState, out CoffEhDecoder.RhEHClause clause))
            {
                score |= 4;

                Log.Begin(LogLevel.Info);
                Console.Write("    clause[");
                Console.WriteUIntRaw((uint)clauseIdx);
                Console.Write("] kind=");
                Console.WriteUIntRaw((uint)clause.Kind);
                Console.Write(" try=[0x");
                Console.WriteHexRaw(clause.TryStartOffset, 8);
                Console.Write("..0x");
                Console.WriteHexRaw(clause.TryEndOffset, 8);
                Console.Write(") handler=0x");
                Console.WriteHexRaw((ulong)(nuint)clause.HandlerAddress, 16);
                Log.EndLine();

                if (codeOffset >= clause.TryStartOffset && codeOffset < clause.TryEndOffset)
                    score |= 8;

                clauseIdx++;
            }

            return score;
        }

        // Test method that captures context FROM INSIDE its try region.
        // ControlPC after capture returns will be the instruction after
        // the CALL — which lies inside the try { ... } block.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int EnumLive_TryHost(PalLimitedContext* ctx)
        {
            int x = 0;
            try
            {
                delegate* unmanaged<byte*, void> capture =
                    (delegate* unmanaged<byte*, void>)CaptureContextStub.GetMethodAddress();
                capture((byte*)ctx);
                x = Opaque(7);
            }
            catch (System.InvalidOperationException)
            {
                x = -1;
            }
            return x;
        }

        // Step 5.1+ ingress test: triggers `throw` so RhpThrowEx shellcode
        // builds ExInfo and tail-calls RhpTest_ThrowIngress, which logs
        // ExInfo invariants and halts. Verifies the asm thunk works
        // before any managed dispatcher is in place.
        //
        // Wrapped in outer try/catch so the caller frame carries EH info
        // (1 typed clause). Step 5.3 probe B walks up from throw site and
        // finds these clauses on this frame.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void IngressThrow()
        {
            try
            {
                IngressThrow_Inner();
            }
            catch (System.InvalidOperationException)
            {
                // Unreachable until step 5.6 wires the dispatcher. Until
                // then RhpTest_ThrowIngress halts before reaching here.
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void IngressThrow_Inner()
        {
            throw new System.InvalidOperationException("ingress-5.1");
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
                throw new System.InvalidOperationException("eh8");
            }
            catch (System.InvalidOperationException ex)
            {
                // L8 gate (per phase1-trycatch-roadmap.md). Real dispatch
                // path: throw → RhpThrowEx shellcode → RhpTest_ThrowIngress
                // → DispatchEx.Dispatch → FindFirstPassHandler →
                // RhpCallCatchFunclet → ILC catch funclet body (this code)
                // → mov rsp + jmp rax → continuation IP in TryCatchWithThrow's
                // body past the catch → returns 801.
                return ex.Message == "eh8" ? 801 : -1;
            }
        }

        // L9 gate — rethrow baseline (Phase 1 step 6).
        // Path:
        //   throw → first catch caught it → throw; (rethrow) →
        //   RhpRethrow shellcode builds new ExInfo with Kind=Throw|Rethrow,
        //   PrevExInfo = active (= first catch's ExInfo) →
        //   DispatchEx detects Rethrow flag, uses prev->ExContext (=
        //   original throw site PAL) + startIdx = prev->IdxCurClause
        //   (skip clauses up to и including the inner catch's clause) →
        //   second catch matches → returns 901.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int RethrowChain()
        {
            try
            {
                try
                {
                    throw new System.InvalidOperationException("eh9");
                }
                catch (System.InvalidOperationException)
                {
                    throw;   // RhpRethrow path
                }
            }
            catch (System.InvalidOperationException ex)
            {
                return ex.Message == "eh9" ? 901 : -1;
            }
        }

        // L10 gate — finally + second pass (Phase 1 step 7).
        // Path:
        //   throw → DispatchEx first pass finds outer catch (idxCurClause=K) →
        //   InvokeSecondPass walks frames от throw site до handlingFrameSP,
        //   on catch frame partial-pass с idxLimit=K so that finally clause
        //   (curIdx<K, kind=Fault, codeOffset in TRY range) is invoked →
        //   RhpCallFinallyFunclet runs `x = 11` body, writes back nonvols
        //   к REGDISPLAY → returns → catch funclet runs `return 100 + x` → 111.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int TryCatchFinally()
        {
            int x = 0;
            try
            {
                try
                {
                    throw new System.InvalidOperationException("eh10");
                }
                finally
                {
                    x = 11;
                }
            }
            catch (System.InvalidOperationException)
            {
                return 100 + x;
            }
        }

        // L11 gate — filter clause (`catch (E) when (predicate)`)
        // (Phase 1 step 8). Path:
        //   throw → DispatchEx FFPH iterates clauses → filter clause
        //   covers codeOffset → invoke RhpCallFilterFunclet с user
        //   predicate body (returns 1 = match) → catch handler runs →
        //   return 1101.
        //
        // First non-matching filter test: throw IOE("eh11"), filter
        // checks ex.Message=="bogus" → returns 0 → no match → second
        // filter (or default catch) matches.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int FilterClause()
        {
            try
            {
                throw new System.InvalidOperationException("eh11");
            }
            catch (System.InvalidOperationException ex) when (ex.Message == "eh11")
            {
                return 1101;
            }
        }

        // L16/L17 multi-frame test state — populated by helper methods,
        // verified by outer probe.
        private static int s_multiFrameFinallyCount;

        // L16 helper: throws but has no catch. Caller catches.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void HelperThrow16()
        {
            throw new System.InvalidOperationException("eh16");
        }

        // L16 helper: protects HelperThrow16 with finally. The finally must
        // fire when callee throws — that's the multi-frame scenario.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void HelperWithFinally16()
        {
            try
            {
                HelperThrow16();
            }
            finally
            {
                s_multiFrameFinallyCount = 16;
            }
        }

        // L16 gate — multi-frame finally walk (Phase 1 polish).
        // Path: caller catches; intermediate frame has try/finally that
        // protected the throwing callee. Stock NativeAOT walks frames
        // during second pass и invokes finally on each frame между
        // throw site и handlingFrameSP. We do the equivalent here.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int MultiFrameFinally()
        {
            s_multiFrameFinallyCount = 0;
            try
            {
                HelperWithFinally16();
            }
            catch (System.InvalidOperationException)
            {
                return 1600 + s_multiFrameFinallyCount;
            }
            return -1;
        }

        // L17 helper chain: each level adds one frame to stack trace.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void HelperLevel3_17()
        {
            throw new System.InvalidOperationException("eh17");
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void HelperLevel2_17() => HelperLevel3_17();

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void HelperLevel1_17() => HelperLevel2_17();

        // L17 gate — multi-frame stack trace.
        // First-pass должен append PC for every frame walked. Test verifies
        // non-trivial trace (>= 4 frames: HelperLevel3 → 2 → 1 → MultiFrameStackTrace).
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int MultiFrameStackTrace()
        {
            try
            {
                HelperLevel1_17();
            }
            catch (System.Exception ex)
            {
                System.IntPtr[] trace = ex.GetStackIPs();
                if (trace == null || trace.Length < 4)
                    return -1;
                return 1700 + trace.Length;
            }
            return -1;
        }

        // L13 gate — hardware fault bridge (Phase 1 step 10). Path:
        //   write to non-canonical address → CPU #GP → IDT trampoline →
        //   Idt.Dispatch → HwFaultBridge.DispatchTrap → builds
        //   NullReferenceException + PAL + ExInfo (kind=HardwareFault) →
        //   DispatchEx.Dispatch → FFPH finds catch в этом frame →
        //   RhpCallCatchFunclet → return 3.
        //
        // Note: actual `*((int*)null) = 0` doesn't fault в UEFI environment
        // потому что low memory identity-mapped writable. Use non-canonical
        // address (0x8000_0000_0000_0000) which guarantees CPU #GP regardless
        // of paging.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static unsafe int HwFault()
        {
            try
            {
                int* p = (int*)0x8000_0000_0000_0000UL;   // non-canonical
                *p = 0;        // → #GP (non-canonical write)
                return -1;     // unreachable
            }
            catch (System.AccessViolationException)
            {
                return 3;
            }
        }

        // L14 gate — rich stack trace (Phase 1 step 11). DispatchEx
        // appends throw-site IP к exception's _corDbgStackTrace во время
        // first-pass; StackTrace getter returns non-null marker. Test
        // verifies trace populated после catch.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int StackTraceCheck()
        {
            try
            {
                throw new System.InvalidOperationException("eh14");
            }
            catch (System.Exception ex)
            {
                return ex.StackTrace != null ? 1401 : -1;
            }
        }

        // L15 gate — collided unwind (Phase 1 step 11). Inner finally
        // throws while outer dispatch is in second pass. New throw should
        // "steal" the catch и deliver the LATER exception. Stack-jump
        // semantics naturally handles this — recursive Dispatch на throw "b"
        // finds outer catch и jumps к continuation, abandoning original "a"
        // dispatch state. Outer catch sees "b".
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int CollidedUnwind()
        {
            try
            {
                try
                {
                    throw new System.InvalidOperationException("a");
                }
                finally
                {
                    throw new System.InvalidOperationException("b");
                }
            }
            catch (System.InvalidOperationException ex)
            {
                return ex.Message == "b" ? 1501 : -1;
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

        // L7 — Phase 1 step 4 gate. Builds the chain A → B → C → Walk
        // via NoInlining; from inside Walk, captures the live CPU
        // context, initialises a StackFrameIterator over it, and walks
        // upward counting frames until method A's body is reached.
        //
        // Expected: 3 (Walk → C is step 1, C → B is step 2, B → A is step 3).
        //
        // Returns negative on failure:
        //   -1 = capture failed / no module
        //   -2 = SfiNext returned false before A
        //   -3 = walked > 100 frames without reaching A (loop guard)
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int FrameWalk_A() => FrameWalk_B();

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int FrameWalk_B() => FrameWalk_C();

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int FrameWalk_C() => FrameWalk_Walk();

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int FrameWalk_Walk()
        {
            if (!CoffRuntimeFunctionTable.IsInitialized) return -1;

            // Capture live context via the patched shellcode.
            PalLimitedContext ctx = default;
            delegate* unmanaged<byte*, void> capture =
                (delegate* unmanaged<byte*, void>)CaptureContextStub.GetMethodAddress();
            capture((byte*)&ctx);

            // Initialise iterator from PAL.
            StackFrameIterator iter = default;
            StackFrameIteratorOps.Init(&iter, &ctx);

            // Resolve method A's start RVA. We compare ROOT runtime
            // function BeginAddress because addresses of funclets and
            // mid-method IPs all map back to ROOT via WalkToRoot.
            delegate*<int> ptrA = &FrameWalk_A;
            byte* ipA = (byte*)ptrA;
            if (!CoffMethodLookup.TryFindMethod(ipA, out CoffMethodLookup.MethodInfo infoA))
                return -1;
            uint targetBeginRva = infoA.RootRuntimeFunction->BeginAddress;

            int count = 0;
            for (int safety = 0; safety < 100; safety++)
            {
                // Resolve current frame.
                if (!CoffMethodLookup.TryFindMethod((byte*)iter.ControlPC, out CoffMethodLookup.MethodInfo cur))
                    return -2;
                if (cur.RootRuntimeFunction->BeginAddress == targetBeginRva)
                    return count;

                if (!StackFrameIteratorOps.Next(&iter))
                    return -2;
                count++;
            }
            return -3;
        }

        private static int FrameWalk()
        {
            // Entry point — runs the chain. We need a layer that's NOT
            // FrameWalk_A so its caller (FrameWalk → FrameWalk_A) doesn't
            // affect the count. The probe expects exactly Walk→C→B→A = 3.
            return FrameWalk_A();
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
