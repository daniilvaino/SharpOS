using OS.Hal;
using OS.Boot.EH;

#pragma warning disable CS0162 // Probes/Trace switches are compile-time constants: the branch a switch turns off is meant to be dead (docs/build-warnings.md)

namespace OS.Kernel.Diagnostics
{
    // Exception-handling gates in the kernel, L8 and up: a real throw
    // caught each way the runtime must handle — typed catch, rethrow,
    // finally, filters, a hardware fault, stack traces. Each toggles
    // independently in Probes.cs. (L1–L7, the steps before a throw worked,
    // are gone: everything here goes through them.)
    //
    // Runtime-dependent inputs prevent ILC from constant-folding the
    // bodies away.
    internal static unsafe class EhProbe
    {
        public static void Run()
        {
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

            if (Probes.EhTraceText)
            {
                int v = TraceText();
                ReportLevel("eh L18 stack trace text", v);
            }

            if (Probes.EhTraceTruncation)
            {
                int v = TraceTruncation();
                ReportLevel("eh L19 trace truncation marker", v);
            }

            if (Probes.EhRethrowAppends)
            {
                int v = RethrowAppends();
                ReportLevel("eh L20 rethrow appends frames", v);
            }
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

        // L18 gate — the trace as TEXT, which L14 and L17 never checked.
        //
        // L14 asks whether StackTrace is non-null and L17 counts the recorded
        // addresses; between them they passed for years while the getter
        // returned the literal "[trace]". Both were true statements about
        // something nobody could read.
        //
        // The negative returns are distinct because they fail for different
        // reasons and want different fixes: no trace at all, the old marker,
        // text too short to be frames, no frame lines, or lines that do not
        // match the addresses recorded.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int TraceText()
        {
            try
            {
                HelperLevel1_17();
            }
            catch (System.Exception ex)
            {
                string trace = ex.StackTrace;
                if (trace == null) return -1;
                if (trace == "[trace]") return -2;
                if (trace.Length < 32) return -3;
                if (trace.IndexOf("   at 0x") < 0) return -4;

                int lines = 0;
                for (int i = 0; i < trace.Length; i++)
                    if (trace[i] == '\n') lines++;

                System.IntPtr[] ips = ex.GetStackIPs();
                if (ips == null || ips.Length != lines) return -5;

                return 1800 + lines;
            }
            return -6;
        }

        // L19 gate — a cut-off trace says it was cut off.
        //
        // The cap was sixteen, and nothing said when it was reached: sixteen
        // frames of a forty-frame stack read exactly like a forty-frame stack
        // that happened to be sixteen deep. Now the cap is sixty-four and the
        // text ends with "... N more frames" when there were more.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int TraceTruncation()
        {
            try
            {
                DeepThrow(80);
            }
            catch (System.Exception ex)
            {
                if (ex.DroppedStackFrames <= 0) return -1;

                string trace = ex.StackTrace;
                if (trace == null) return -2;
                if (trace.IndexOf("more frames") < 0) return -3;

                System.IntPtr[] ips = ex.GetStackIPs();
                if (ips == null || ips.Length != 64) return -4;

                return 1900 + ex.DroppedStackFrames;
            }
            return -5;
        }

        // Deliberately not a tail call: the addition after the recursive call
        // keeps each level as a real frame, which is the whole point.
        private static int s_deepSink;

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void DeepThrow(int depth)
        {
            if (depth == 0)
                throw new System.InvalidOperationException("deep-trace");
            DeepThrow(depth - 1);
            s_deepSink += depth;
        }

        // L20 gate — `throw;` keeps walking, and the frames it walks are
        // added to the ones already there.
        //
        // Until step177 the dispatcher passed no trace target on a rethrow,
        // so the trace stopped at the rethrow site: the one frame the reader
        // is already standing in. Counting, not naming - names are a separate
        // piece of work and `rethrow preserves stack trace` still guards them.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int RethrowAppends()
        {
            int inner = 0;
            try
            {
                try
                {
                    HelperLevel1_17();
                }
                catch (System.Exception e)
                {
                    inner = e.GetStackIPs().Length;
                    throw;
                }
            }
            catch (System.Exception e)
            {
                int outer = e.GetStackIPs().Length;
                if (inner <= 0) return -1;
                if (outer <= inner) return -2;
                return 2000 + (outer - inner);
            }
            return -3;
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
