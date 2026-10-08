using OS.Boot.EH;
using OS.Hal;
using OS.PAL.SharpOSHost;
using SharpOS.Std.NoRuntime;

namespace OS.Kernel.Memory
{
    // Step 110 Part 8 — precise stack-root walker for kernel GC mark phase.
    //
    // Replaces the conservative ScanStack on a per-frame basis: instead of
    // treating every qword in the stack region as a maybe-pointer, we
    // walk the actual call chain via PE UNWIND_CODE'ы, ask CoffGcInfo for
    // each frame's live tracked + untracked GC slots, resolve their
    // addresses with CoffGcInfoResolver, and pass each resulting pointer
    // to GcMark.MarkFromRoot.
    //
    // GcMark.MarkFromRoot already enforces safety: skips out-of-heap
    // pointers, non-canonical addresses, MTs that live inside the heap.
    // With precise discovery the false-positive rate drops to ~zero,
    // because we only ever pass pointers that GcInfo claims are managed.
    //
    // Invariant: caller must already have run GcMark.Begin and registered
    // static roots (via GcRoots.MarkStaticRootsOnly or equivalent) before
    // invoking RunFromCurrentFrame.
    internal static unsafe class KernelGcPreciseWalk
    {
        // Telemetry — tells the caller how the walk went without us
        // needing to thread through return values from the unmanaged
        // callback signature.
        public static int LastFramesWalked;
        public static int LastRootsMarked;
        public static int LastFramesUnresolved;

        /// <summary>Walks that ended where a stack legitimately ends.</summary>
        /// <remarks>
        /// Counted apart from LastFramesUnresolved because they mean the
        /// opposite thing. A walk that stops at the bottom of a thread saw
        /// every frame there was; a walk that stops because a frame would not
        /// resolve left roots behind it unmarked. Both used to be "unresolved",
        /// so the number could never answer whether anything was actually lost.
        /// </remarks>
        public static int LastBottomsReached;

        /// <summary>Frames stepped through without marking anything.</summary>
        /// <remarks>
        /// Native frames — CoreCLR and the CRT are linked into this image —
        /// have real unwind codes and no slot table. Walking through them is
        /// correct and necessary: the managed frames that hold roots are on
        /// the other side. Counted separately because "nothing to mark here"
        /// and "could not read this frame" are opposite statements, and until
        /// step177 the second was what happened: the trailer read for a native
        /// record returns the next record's bytes, and the walker decoded
        /// those as a live-slot table.
        /// </remarks>
        public static int LastFramesWithoutGcInfo;

        // Where the walk gives up quietly. Each of these is a frame whose
        // roots nobody reports and nobody misses until the sweep frees them,
        // so they are counted rather than left to inference: a stack that
        // ends at the frame cap and a stack that ends at its bottom look
        // identical from the outside.
        public static int LastFramesSkippedOutOfRange;
        public static int LastFramesSlotOverflow;
        public static int LastFrameCapHits;

        /// <summary>Kernel walks that stopped at an app's frames and resumed below the app.</summary>
        public static int LastAppBoundaries;
        public static int LastAppRunsCrossed;

        /// <summary>Interrupted threads scanned word by word instead of walked.</summary>
        public static int LastConservativeScans;

        // The addresses behind the counters. A number says three frames were
        // dropped; it cannot say WHICH, and a dropped frame is a root nobody
        // reports and the sweep then frees. Eight is enough: if there are more
        // than eight, the first eight already name the district.
        private const int SkipCapacity = 8;

        private unsafe struct SkipTable
        {
            public fixed ulong Rip[SkipCapacity];
            public fixed ulong From[SkipCapacity];
        }

        private static SkipTable s_skipped;
        private static int s_skippedCount;

        public static int LastSkippedCount => s_skippedCount;

        public static ulong SkippedRip(int index)
        {
            if ((uint)index >= (uint)s_skippedCount) return 0;
            fixed (SkipTable* t = &s_skipped) return t->Rip[index];
        }

        /// <summary>
        /// The frame the walk was standing on when the next one came out
        /// unusable. The skipped address alone cannot say whose unwind
        /// produced it, and when that address is not code at all — a value
        /// read out of data because the stack pointer was wrong — the only
        /// thing worth knowing is which function it was read from.
        /// </summary>
        public static ulong SkippedFrom(int index)
        {
            if ((uint)index >= (uint)s_skippedCount) return 0;
            fixed (SkipTable* t = &s_skipped) return t->From[index];
        }

        // Room for the slot table and live set of a frame too big for the
        // walk's stack: grown from the kernel's native heap, never shrunk.
        // The live set takes the buffer's last bytes, the slots its start.
        private static byte* s_scratch;
        private static int s_scratchBytes;

        private static byte* Scratch(int bytes)
        {
            if (bytes > s_scratchBytes)
            {
                int size = 64 * 1024;
                while (size < bytes) size *= 2;
                byte* grown = (byte*)KernelHeap.Alloc((uint)size);
                if (grown == null)
                    OS.Kernel.Panic.Fail("GC walk: no memory for a frame's slot table");
                if (s_scratch != null) KernelHeap.Free(s_scratch);
                s_scratch = grown;
                s_scratchBytes = size;
            }
            return s_scratch;
        }

        // The frame being marked while the walk's context moves on to its caller.
        private static Context s_frameSnapshot;

        // Every place an app's walk loses roots, said (the first 40 of a
        // boot). The counters alone are printed only outside GC stress, and a
        // root lost in silence surfaces later as a stranger's object.
        private static int s_lostSaid;

        private static void SayLost(string what, ulong rip, ulong from = 0)
        {
            if (s_markRoot == null || s_lostSaid >= 40) return;   // the app's walks only
            s_lostSaid++;
            OS.Hal.Console.Write("[gc] ");
            OS.Hal.Console.Write(what);
            OS.Hal.Console.Write(" rip=0x");
            OS.Hal.Console.WriteHex(rip);
            if (from != 0)
            {
                OS.Hal.Console.Write(" after a frame at 0x");
                OS.Hal.Console.WriteHex(from);
            }
            OS.Hal.Console.WriteLine("");
        }

        private static void NoteSkipped(ulong rip, ulong from)
        {
            if (s_skippedCount >= SkipCapacity) return;
            fixed (SkipTable* t = &s_skipped)
            {
                t->Rip[s_skippedCount] = rip;
                t->From[s_skippedCount] = from;
            }
            s_skippedCount++;
        }

        public static void ResetTelemetry()
        {
            LastFaultFramesScanned = 0;
            s_skippedCount = 0;
            LastFramesWalked = 0;
            LastRootsMarked = 0;
            LastFramesUnresolved = 0;
            LastBottomsReached = 0;
            LastFramesWithoutGcInfo = 0;
            LastFramesSkippedOutOfRange = 0;
            LastFramesSlotOverflow = 0;
            LastFrameCapHits = 0;
            LastAppBoundaries = 0;
            LastAppRunsCrossed = 0;
            LastConservativeScans = 0;
        }

        public static bool IsAvailable =>
            GcContextSpill.IsInitialized
            && CoffRuntimeFunctionTable.ImageBase != null;

        // Where a discovered root goes. Null means "our own heap".
        //
        // The walk itself — register spill, unwinding, GcInfo decoding — is
        // expensive machinery that only makes sense in one copy, and it is
        // already image-aware. What differs between the kernel and a loaded
        // app is only WHICH heap a root should be marked in, so that is the
        // one thing made pluggable. Apps keep their own heap and their own
        // sweep; they borrow the walker, not the memory.
        private static delegate* unmanaged<nuint, void> s_markRoot;

        // Whether the topmost frame's Rip is an instruction about to execute
        // or an address to return to. Only an interrupt captures the former;
        // a spilled context and a parked thread both hand us a return
        // address, and a return address needs the same one-byte step back
        // into the call that every frame below it gets.
        private static bool s_topFrameIsActive;

        public static void RunFromCurrentFrame() => RunFromCurrentFrame(null);

        // Method starts of the throw and rethrow stubs (see WalkFrames).
        private static byte* s_throwStub, s_rethrowStub;

        private static void FindThrowStubs()
        {
            if (s_throwStub != null) return;
            s_throwStub = (byte*)OS.Boot.EH.ThrowExStub.GetMethodAddress();
            s_rethrowStub = (byte*)OS.Boot.EH.RethrowStub.GetMethodAddress();
        }

        public static void RunFromCurrentFrame(delegate* unmanaged<nuint, void> markRoot)
        {
            ResetTelemetry();

            if (!IsAvailable) return;
            FindThrowStubs();

            s_markRoot = markRoot;
            s_topFrameIsActive = false;
            Context ctx = default;
            GcContextSpill.Invoke(&ctx, &WalkCallback);
            s_markRoot = null;

            if (markRoot == null)
                ContinueBelowApps(OS.Kernel.Threading.Scheduler.Current);

            MarkExceptionChain((OS.Boot.EH.ExInfo*)OS.Boot.EH.ExInfoHead.s_head,
                               OS.Kernel.Threading.Scheduler.Current, markRoot);
        }

        /// <summary>
        /// Roots held by the exceptions a thread is dispatching: each
        /// exception object, and for a hardware fault the code it stopped.
        /// </summary>
        /// <remarks>
        /// The dispatcher keeps the exception as a raw address (ExInfo.Exception)
        /// between the throw and the catch, and runs finally and filter funclets
        /// in between — any of which may allocate. Stock NativeAOT reports the
        /// same field for the same reason.
        ///
        /// A hardware fault leaves an interrupt frame under the handler, and the
        /// walk from the handler stops at the entry stub: the faulting code and
        /// everything beneath it — including, during a catch funclet, the
        /// method that catches — are on the far side. Until this, a collection
        /// in the fault path (the exception's own allocation, a finally, a
        /// catch body) did not see them, and an app's factory held its
        /// collector off to stay safe. Scanned as a preempted thread is:
        /// conservatively, since the faulting instruction is not a safe point.
        /// </remarks>
        internal static void MarkExceptionChain(OS.Boot.EH.ExInfo* head, OS.Kernel.Threading.Thread? thread,
                                                delegate* unmanaged<nuint, void> markRoot)
        {
            int guard = 0;
            for (OS.Boot.EH.ExInfo* e = head; e != null && guard < 64; e = e->PrevExInfo, guard++)
            {
                // An entry is a stack address of the thread. Anything else
                // means a dispatch left the chain pointing into dead memory:
                // said, not followed.
                if (((ulong)e & 7) != 0 || ((ulong)e >> 47) != 0)
                {
                    BadChainEntries++;
                    OS.Hal.Console.Write("[gc] exception chain entry implausible: 0x");
                    OS.Hal.Console.WriteHex((ulong)e);
                    OS.Hal.Console.WriteLine(", chain not followed further");
                    break;
                }
                if (e->Exception != 0)
                    MarkCandidate(e->Exception, markRoot);

                // A software throw: the frames from the throw site down. The
                // throw stub's own frame is shellcode in a C# method's body,
                // and unwinding it by that method's codes lands on garbage —
                // so a walk from a catch funclet (which allocates) ended
                // there, and the method that catches, with every caller below
                // it, was never marked (step193: `dynamic`'s cases lost their
                // closures under GC stress). The stub kept the thrower's
                // context; the walk resumes from it.
                if (e->Kind != OS.Boot.EH.ExInfo.KindHardwareFault && e->ExContext != null)
                    RunFromThrowSite(e->ExContext, markRoot);

                if (e->Kind == OS.Boot.EH.ExInfo.KindHardwareFault && e->FaultFrame != null)
                {
                    LastFaultFramesScanned++;
                    if (thread == null || !ScanInterruptedConservatively(thread, e->FaultFrame, markRoot))
                        RunFromInterruptFrame(e->FaultFrame, markRoot);
                }
            }
        }

        /// <summary>Hardware-fault frames the last walk scanned through MarkExceptionChain.</summary>
        public static int LastFaultFramesScanned;

        /// <summary>Exception-chain entries found implausible, ever.</summary>
        public static int BadChainEntries;

        /// <summary>
        /// The kernel frames under every app this thread is running, for the
        /// kernel's own heap.
        /// </summary>
        /// <remarks>
        /// A walk that meets an app stops there: the next frames are the app's
        /// (its objects, its collector), and below them is the jump stub, which
        /// has no unwind data. But under the stub is the kernel code that
        /// launched the app, with live locals of its own — and before
        /// pipe_plan.md item 2 a collection during an app's service call saw
        /// none of them. JumpStub keeps every register the kernel handed over
        /// and the stack pointer it returns to (JumpContext), which is exactly
        /// a context to resume the walk from. Runs nest; each one crossed in
        /// turn, innermost first, as the stack has them.
        /// </remarks>
        internal static void ContinueBelowApps(OS.Kernel.Threading.Thread? thread)
        {
            if (thread == null) return;
            for (OS.Kernel.Exec.JumpContext* run = thread.Jump; run != null; run = run->Previous)
            {
                if (run->KernelRsp == 0)
                    continue;

                Context ctx = default;
                ctx.Rip = *(ulong*)run->KernelRsp;      // return address into JumpStub.Run
                ctx.Rsp = run->KernelRsp + 8;
                ctx.Rbx = run->Rbx;
                ctx.Rbp = run->Rbp;
                ctx.Rsi = run->Rsi;
                ctx.Rdi = run->Rdi;
                ctx.R12 = run->R12;
                ctx.R13 = run->R13;
                ctx.R14 = run->R14;
                ctx.R15 = run->R15;

                LastAppRunsCrossed++;
                s_markRoot = null;
                s_topFrameIsActive = false;
                WalkFrames(&ctx);
            }
        }

        /// <summary>
        /// A thread stopped by an interrupt, scanned word by word from where it
        /// was stopped to the top of its stack, registers included.
        /// </summary>
        /// <remarks>
        /// The interrupted code is at an arbitrary instruction, not at a call:
        /// unless its method is fully interruptible, GcInfo has nothing to say
        /// about it, and the precise walk dropped the frame and every root in
        /// it. Conservative over-marks, never under-marks. False when the
        /// thread was stopped off its own stack (an app's main thread runs on
        /// the app's stack): the caller then walks it precisely, as before.
        /// </remarks>
        public static bool ScanInterruptedConservatively(OS.Kernel.Threading.Thread thread, void* frame,
                                                         delegate* unmanaged<nuint, void> markRoot)
        {
            ulong* f = (ulong*)frame;
            return ScanStackConservatively(thread, f[21], f, markRoot);
        }

        /// <summary>
        /// A thread that gave up the CPU to a deferred tick (Preemption
        /// .TakeDeferred, step196), scanned word by word from its saved stack
        /// pointer: the callee-saved registers its switch pushed are there.
        /// </summary>
        /// <remarks>
        /// It may have yielded at the end of an allocator's critical section,
        /// with the object just allocated held only as a raw address — which
        /// a precise walk does not see. A timer tick at the same instruction
        /// gets the conservative scan; so does this.
        /// </remarks>
        public static bool ScanParkedConservatively(OS.Kernel.Threading.Thread thread, delegate* unmanaged<nuint, void> markRoot)
        {
            if (thread.ContextBlock == null) return false;
            return ScanStackConservatively(thread, *(ulong*)thread.ContextBlock, null, markRoot);
        }

        // From rsp to the top of whichever stack holds it — the thread's own,
        // or an app stack it runs app code on — and the registers of an
        // interrupt frame when there is one.
        private static bool ScanStackConservatively(OS.Kernel.Threading.Thread thread, ulong rsp, ulong* f,
                                                    delegate* unmanaged<nuint, void> markRoot)
        {
            ulong low = (ulong)thread.StackBase;
            ulong high = (ulong)thread.StackTop;

            // The thread that runs an app runs it on the app's stack, not on
            // its own: find the run whose stack it was stopped on.
            if (low == 0 || rsp < low || rsp >= high)
            {
                low = high = 0;
                for (OS.Kernel.Exec.JumpContext* run = thread.Jump; run != null; run = run->Previous)
                {
                    if (run->AppStackBase != 0 &&
                        rsp >= run->AppStackBase && rsp < run->StackTop)
                    {
                        low = run->AppStackBase;
                        high = run->StackTop;
                        break;
                    }
                }
                if (high == 0)
                {
                    if (Tracing)
                    {
                        OS.Hal.Console.Write("[walk-trace] conservative REFUSED rsp=0x"); OS.Hal.Console.WriteHex(rsp);
                        OS.Hal.Console.WriteLine("");
                    }
                    return false;
                }
            }

            LastConservativeScans++;
            if (Tracing)
            {
                OS.Hal.Console.Write("[walk-trace] conservative rsp=0x"); OS.Hal.Console.WriteHex(rsp);
                OS.Hal.Console.Write(" low=0x"); OS.Hal.Console.WriteHex(low);
                OS.Hal.Console.Write(" high=0x"); OS.Hal.Console.WriteHex(high);
                if (f != null) { OS.Hal.Console.Write(" rip=0x"); OS.Hal.Console.WriteHex(f[18]); }
                OS.Hal.Console.WriteLine("");
            }
            if (f != null)
                for (int i = 1; i <= 15; i++)
                    MarkCandidate(f[i], markRoot);
            for (ulong p = rsp & ~7UL; p < high; p += 8)
                MarkCandidate(*(ulong*)p, markRoot);
            return true;
        }

        private static void MarkCandidate(ulong value, delegate* unmanaged<nuint, void> markRoot)
        {
            if (value == 0) return;
            if (markRoot != null) markRoot((nuint)value);
            else GcMark.MarkFromRoot((nint)value);
        }

        /// <summary>
        /// Walk the stack of a thread that is NOT running, from the context
        /// its last switch-out left behind.
        /// </summary>
        /// <remarks>
        /// Roots live on every thread's stack, not just the running one, and
        /// until this existed the collector saw only the stack it was called
        /// on. Objects held solely by a sleeping thread were unreachable to
        /// the marker and freed underneath it — a corruption that surfaces
        /// whenever that thread wakes, arbitrarily far from the collection
        /// that caused it.
        ///
        /// The layout is the one CoopSwitch produces and Scheduler fabricates
        /// for a thread that has never run (see both, they must agree):
        ///   SavedRsp + 0..56  eight callee-saved GPRs, r15 first
        ///   SavedRsp + 64     return address — where the thread resumes
        ///   SavedRsp + 72     the stack pointer it resumes with
        ///
        /// A thread that has never been dispatched resolves to its entry thunk
        /// and unwinds no further, which is correct: it holds nothing yet.
        /// </remarks>
        public static void RunFromParkedThread(byte* contextBlock,
                                               delegate* unmanaged<nuint, void> markRoot)
        {
            if (!IsAvailable || contextBlock == null) return;

            ulong savedRsp = *(ulong*)contextBlock;
            if (savedRsp == 0) return;

            ulong* slot = (ulong*)savedRsp;

            Context ctx = default;
            ctx.R15 = slot[0];
            ctx.R14 = slot[1];
            ctx.R13 = slot[2];
            ctx.R12 = slot[3];
            ctx.Rdi = slot[4];
            ctx.Rsi = slot[5];
            ctx.Rbp = slot[6];
            ctx.Rbx = slot[7];
            ctx.Rip = slot[8];
            ctx.Rsp = savedRsp + 72;

            if (ctx.Rip == 0) return;

            s_markRoot = markRoot;
            s_topFrameIsActive = false;
            WalkFrames(&ctx);
            s_markRoot = null;
        }

        /// <summary>
        /// Continue a walk across an interrupt boundary, from the register
        /// snapshot the entry stub saved.
        /// </summary>
        /// <remarks>
        /// A preempted thread is parked inside the interrupt handler. Walking
        /// its context covers the handler's own managed frames and then stops:
        /// the next thing down is the entry shellcode, which has no unwind
        /// data, and the unwinder cannot step over what it cannot describe.
        /// Everything below that — the code actually interrupted, holding the
        /// roots that matter — would be lost.
        ///
        /// The frame has exactly what is needed to resume the walk on the far
        /// side. Field offsets match InterruptFrame and the common stub's push
        /// order; see X64Asm.TryResumeFrame, which reads the same layout.
        /// </remarks>
        public static void RunFromInterruptFrame(void* frame,
                                                 delegate* unmanaged<nuint, void> markRoot)
        {
            if (!IsAvailable || frame == null) return;
            FindThrowStubs();

            ulong* f = (ulong*)frame;

            Context ctx = default;
            ctx.Rax = f[1];
            ctx.Rcx = f[2];
            ctx.Rdx = f[3];
            ctx.Rbx = f[4];
            ctx.Rsi = f[5];
            ctx.Rdi = f[6];
            ctx.Rbp = f[7];
            ctx.R8  = f[8];
            ctx.R9  = f[9];
            ctx.R10 = f[10];
            ctx.R11 = f[11];
            ctx.R12 = f[12];
            ctx.R13 = f[13];
            ctx.R14 = f[14];
            ctx.R15 = f[15];
            ctx.Rip = f[18];
            ctx.Rsp = f[21];

            if (ctx.Rip == 0 || ctx.Rsp == 0) return;

            s_markRoot = markRoot;
            s_topFrameIsActive = true;
            WalkFrames(&ctx);
            s_markRoot = null;
        }

        /// <summary>A walk from where a managed throw entered the runtime: the thrower's return address and registers.</summary>
        private static void RunFromThrowSite(OS.Boot.EH.PalLimitedContext* site, delegate* unmanaged<nuint, void> markRoot)
        {
            if (!IsAvailable || site == null || site->IP == 0 || site->Rsp == 0) return;
            FindThrowStubs();
            if (((ulong)site & 7) != 0 || ((ulong)site >> 47) != 0) return;

            Context ctx = default;
            ctx.Rip = site->IP;
            ctx.Rsp = site->Rsp;
            ctx.Rbp = site->Rbp;
            ctx.Rdi = site->Rdi;
            ctx.Rsi = site->Rsi;
            ctx.Rbx = site->Rbx;
            ctx.R12 = site->R12;
            ctx.R13 = site->R13;
            ctx.R14 = site->R14;
            ctx.R15 = site->R15;

            s_markRoot = markRoot;
            s_topFrameIsActive = false;   // a return address: the call into the throw stub
            WalkFrames(&ctx);
            s_markRoot = null;
        }

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void WalkCallback(Context* ctx) => WalkFrames(ctx);

        /// <summary>Walks still to trace frame by frame ("[walk-trace]"); a debugging switch.</summary>
        public static int TraceWalks;

        /// <summary>Set only around an app's walk: kernel collections are never traced.</summary>
        public static bool Tracing;

        private static void Trace(string what, Context* ctx, int roots)
        {
            OS.Hal.Console.Write("[walk-trace] ");
            OS.Hal.Console.Write(what);
            OS.Hal.Console.Write(" rip=0x"); OS.Hal.Console.WriteHex(ctx->Rip);
            OS.Hal.Console.Write(" rsp=0x"); OS.Hal.Console.WriteHex(ctx->Rsp);
            OS.Hal.Console.Write(" roots="); OS.Hal.Console.WriteUInt((uint)roots);
            OS.Hal.Console.WriteLine("");
        }

        private static void WalkFrames(Context* ctx)
        {
            bool trace = Tracing;
            int rtrMajor = NativeAotModuleInit.ReadyToRunMajor;
            int rtrMinor = NativeAotModuleInit.ReadyToRunMinor;
            int gcInfoVersion = CoffGcInfoDecoder.ReadyToRunVersionToGcInfoVersion(rtrMajor, rtrMinor);

            // Bounded walk — typical kernel boot stack is < 30 frames.
            // Cap protects against runaway loops if unwind glitches.
            const int MaxFrames = 64;

            ulong prevRip = 0;

            for (int frameIdx = 0; ; frameIdx++)
            {
                if (frameIdx >= MaxFrames)
                {
                    LastFrameCapHits++;
                    OS.Hal.Console.Write("[gc] stack walk stopped at the frame cap (");
                    OS.Hal.Console.WriteUInt((uint)MaxFrames);
                    OS.Hal.Console.Write(") at rip=0x");
                    OS.Hal.Console.WriteHex(ctx->Rip);
                    OS.Hal.Console.WriteLine(": the roots below are lost");
                    return;
                }

                // The bottom of a thread's stack: the slot ThreadStartThunk
                // would return through, zeroed at spawn. Before step177 it held
                // whatever the page carried from its last owner, and the walk
                // read that as a return address — four identical non-canonical
                // values in every collection, on both machines.
                if (ctx->Rip == 0)
                {
                    LastBottomsReached++;
                    if (trace) Trace("end: bottom", ctx, 0);
                    return;
                }

                // The kernel-to-application trampoline. It switches CR3 and
                // RSP and carries no unwind data, so the walk stops here
                // whatever we do; what changes is that it is now counted as an
                // end rather than as a frame that failed to resolve.
                if (OS.Kernel.Exec.JumpStub.ContainsAddress(ctx->Rip))
                {
                    LastBottomsReached++;
                    if (trace) Trace("end: jump stub", ctx, 0);
                    return;
                }

                byte* rip = (byte*)ctx->Rip;
                if (!CoffMethodGcInfo.TryResolve(rip, out CoffMethodGcInfo.Result r))
                {
                    // An app's call into a kernel service: no GcInfo, no
                    // unwind data, no roots — but the app's frames, which do
                    // hold roots, are on the other side of it. For the
                    // kernel's own heap they do not: the walk ends here and
                    // goes on below the app (ContinueBelowApps).
                    if (OS.Kernel.Process.AppServiceBuilder.TryUnwindServiceThunk(ref ctx->Rip, ref ctx->Rsp))
                    {
                        if (s_markRoot == null)
                        {
                            LastAppBoundaries++;
                            return;
                        }
                        continue;
                    }

                    LastFramesUnresolved++;
                    NoteSkipped(ctx->Rip, prevRip);
                    SayLost("walk ended at an unresolved frame", ctx->Rip, prevRip);
                    if (trace) Trace("end: unresolved", ctx, 0);
                    return;
                }

                // The throw and rethrow stubs: shellcode in a C# method's body,
                // which that method's unwind codes do not describe. The walk
                // ends here; the frames from the throw site down are walked
                // from the context the stub saved (MarkExceptionChain).
                if (r.MethodStart == s_throwStub || r.MethodStart == s_rethrowStub)
                {
                    LastBottomsReached++;
                    if (trace) Trace("end: throw stub", ctx, 0);
                    return;
                }

                // A frame of another image, reached without a thunk (a direct
                // [UnmanagedCallersOnly] service). Not walked for the kernel's
                // heap: app frames hold the app's objects, and a nested parent's
                // image is not even mapped while its child runs.
                if (s_markRoot == null &&
                    CoffRuntimeFunctionTable.ImageBaseForRecord(r.RuntimeFunction) != CoffRuntimeFunctionTable.ImageBase)
                {
                    LastAppBoundaries++;
                    return;
                }

                LastFramesWalked++;
                prevRip = ctx->Rip;

                // The frame as it stands is kept; the context is unwound to the
                // caller first, because the caller's SP is the base of the
                // slots the frame describes relative to its caller (its
                // stack-passed arguments). The walk is single-threaded under a
                // stopped world, so one static snapshot serves every frame.
                fixed (Context* frame = &s_frameSnapshot)
                {
                    *frame = *ctx;

                    // Image base PER FRAME, not one fixed base for the whole walk.
                    // A stack that crosses from an app into the kernel (or back)
                    // has frames from different PE images, and unwinding one with
                    // another's base decodes garbage. The lookup table already
                    // knows which image a record came from — it just was not being
                    // asked.
                    byte* imageBase = CoffRuntimeFunctionTable.ImageBaseForRecord(r.RuntimeFunction);
                    bool calledFunclet = false;
                    if (imageBase != null)
                    {
                        // A funclet its own method called on the normal path
                        // (a finally on leaving a try): the walk goes on from
                        // its own unwind, into the parent's body, and the
                        // parent is walked at its call. The root's codes,
                        // applied to a funclet, step through the frame pointer
                        // to the parent's caller — the parent frame and the
                        // registers it held were never walked (step197: an
                        // array two frames below a pipeline waited for in a
                        // foreach's finally was swept under --gc-stress).
                        fixed (Context* own = &s_funcletUnwound)
                            calledFunclet = TryUnwindCalledFunclet(rip, imageBase, ctx, own);
                        Unwind(imageBase, r.RuntimeFunction, ctx);
                    }

                    // A frame with no slot table is stepped, not marked. Its
                    // unwind codes are genuine and move to the caller correctly;
                    // what it does not have is anything to tell us which of its
                    // slots hold references.
                    int rootsBefore = LastRootsMarked;
                    int skippedBefore = LastFramesSkippedOutOfRange;
                    if (r.HasGcInfo)
                    {
                        MarkOneFrame(frame, in r, gcInfoVersion,
                                     isActiveFrame: frameIdx == 0 && s_topFrameIsActive,
                                     callerSp: imageBase != null ? ctx->Rsp : 0);
                    }
                    else
                    {
                        LastFramesWithoutGcInfo++;
                    }
                    if (trace)
                        Trace(!r.HasGcInfo ? "frame (no gcinfo)"
                              : LastFramesSkippedOutOfRange != skippedBefore ? "frame OUT OF RANGE" : "frame",
                              frame, LastRootsMarked - rootsBefore);

                    if (imageBase == null) return;

                    // The frame is reported as before (its slots against the
                    // parent's caller SP, which the root's unwind gives); the
                    // walk itself continues below the funclet, not below its parent.
                    if (calledFunclet)
                        fixed (Context* own = &s_funcletUnwound)
                            *ctx = *own;
                }
            }
        }

        private static Context s_funcletUnwound;

        private static void Unwind(byte* imageBase, void* runtimeFunction, Context* ctx)
        {
            ulong establisher = 0;
            void* handlerData = null;
            SehUnwind.VirtualUnwind(
                0,
                (ulong)imageBase,
                ctx->Rip,
                (OS.PAL.SharpOSHost.RuntimeFunction*)runtimeFunction,
                ctx,
                &handlerData,
                &establisher);
        }

        // Whether the frame at ctx is a funclet whose own unwind returns into
        // the body of the same method; if so, that unwound context is in `own`.
        // Mirrors StackFrameIteratorOps.TryUnwindCalledFunclet (EH).
        private static bool TryUnwindCalledFunclet(byte* rip, byte* imageBase, Context* ctx, Context* own)
        {
            if (!CoffMethodLookup.TryFindMethod(rip, out CoffMethodLookup.MethodInfo info))
                return false;
            if ((info.CurrentBlockFlags & CoffMethodLookup.UBF_FUNC_KIND_MASK) == CoffMethodLookup.UBF_FUNC_KIND_ROOT)
                return false;
            byte* unwindInfo = info.ImageBase + info.CurrentRuntimeFunction->UnwindInfoAddress;
            if ((unwindInfo[0] >> 3) != 0) return false;   // chained or handler flags: not a plain funclet prolog
            *own = *ctx;
            Unwind(imageBase, info.CurrentRuntimeFunction, own);
            if (!CoffMethodLookup.TryFindMethod((byte*)own->Rip, out CoffMethodLookup.MethodInfo caller))
                return false;
            if ((caller.CurrentBlockFlags & CoffMethodLookup.UBF_FUNC_KIND_MASK) != CoffMethodLookup.UBF_FUNC_KIND_ROOT)
                return false;
            return caller.ImageBase + caller.RootRuntimeFunction->BeginAddress
                   == info.ImageBase + info.RootRuntimeFunction->BeginAddress;
        }

        // AMD64 volatile registers in GcInfo numbering: rax, rcx, rdx, r8-r11.
        private static bool IsScratchRegister(int reg)
            => reg == 0 || reg == 1 || reg == 2 || (reg >= 8 && reg <= 11);

        private static void MarkOneFrame(Context* ctx, in CoffMethodGcInfo.Result r, int gcInfoVersion,
                                         bool isActiveFrame, ulong callerSp)
        {
            CoffGcInfoDecoder.DecodeHeader(r.GcInfo, gcInfoVersion, out CoffGcInfoHeader hdr);

            int afterSp = CoffGcInfoDecoder.SkipSafePointOffsets(r.GcInfo, in hdr, hdr.BitOffsetAfterHeader);
            int afterIr = CoffGcInfoDecoder.SkipInterruptibleRanges(r.GcInfo, in hdr, afterSp);

            System.Span<CoffGcSlot> slots = stackalloc CoffGcSlot[32];
            CoffGcInfoDecoder.DecodeFullSlotTable(r.GcInfo, afterIr, slots, out CoffGcSlotTable counts);

            if (counts.NumSlots == 0) return;
            if ((int)counts.NumSlots > slots.Length)
            {
                // Bigger frames are common enough (a test method with dozens
                // of locals): decode again with room for all of them — on the
                // stack while that is modest, past it in a scratch buffer the
                // walk keeps (one walk at a time, the world stopped). Until
                // step194 a frame past 4096 slots was skipped and its roots
                // were lost.
                int n = (int)counts.NumSlots;
                slots = n <= CoffGcInfoDecoder.MaxStackBuffered
                    ? stackalloc CoffGcSlot[n]
                    : new System.Span<CoffGcSlot>(Scratch(n * sizeof(CoffGcSlot) + n), n);
                CoffGcInfoDecoder.DecodeFullSlotTable(r.GcInfo, afterIr, slots, out counts);
            }

            int trackedCount = (int)counts.NumTracked;
            // stackalloc cannot be 0-sized — use 1 as floor; we just won't read it.
            System.Span<bool> live = trackedCount <= CoffGcInfoDecoder.MaxStackBuffered
                ? stackalloc bool[trackedCount > 0 ? trackedCount : 1]
                : new System.Span<bool>(s_scratch + (s_scratchBytes - trackedCount), trackedCount);
            // Every frame but the innermost is stopped at a return address,
            // and a return address is not where the call site was recorded:
            // the encoder indexes call sites by an offset INSIDE the call
            // instruction. One byte back lands there, and the next call's
            // live set — a different one — is what the unadjusted offset
            // would have found. NativeAOT's own code manager does exactly
            // this and says the encoder depends on it.
            uint codeOffset = isActiveFrame ? r.CodeOffset : r.CodeOffset - 1;

            bool inRange = CoffGcInfoDecoder.EnumerateLiveSlotsAtPc(
                r.GcInfo, gcInfoVersion, codeOffset, live);

            // When PC is outside any interruptible range we're in a
            // prologue/epilogue transition window. Slot table may name
            // refs that aren't yet/anymore at their canonical home (e.g.
            // FP not set up, callee-saved not yet stored). Skip the whole
            // frame in that case — JIT placed the call site such that
            // GC shouldn't fire there anyway; we just got there as a
            // return PC because the previous frame was inside body.
            if (!inRange)
            {
                LastFramesSkippedOutOfRange++;
                NoteSkipped(ctx->Rip, 0);
                SayLost("frame skipped (not at a safe point)", ctx->Rip);
                return;
            }

            if (Tracing)
            {
                OS.Hal.Console.Write("[walk-trace]   slots="); OS.Hal.Console.WriteUInt(counts.NumSlots);
                OS.Hal.Console.Write(" tracked="); OS.Hal.Console.WriteUInt((uint)trackedCount);
                OS.Hal.Console.Write(" codeOffset=0x"); OS.Hal.Console.WriteHex(codeOffset);
                OS.Hal.Console.Write(" rbp=0x"); OS.Hal.Console.WriteHex(ctx->Rbp);
                OS.Hal.Console.Write(" safepoints="); OS.Hal.Console.WriteUInt(hdr.NumSafePoints);
                OS.Hal.Console.Write(" ranges="); OS.Hal.Console.WriteUInt(hdr.NumInterruptibleRanges);
                OS.Hal.Console.Write(" spIndex="); OS.Hal.Console.WriteUInt(CoffGcInfoDecoder.FindSafePoint(r.GcInfo, in hdr, hdr.BitOffsetAfterHeader, codeOffset));
                OS.Hal.Console.WriteLine("");
            }

            for (int i = 0; i < (int)counts.NumSlots && i < slots.Length; i++)
            {
                bool isUntracked = (slots[i].Flags & CoffGcSlotFlags.Untracked) != 0;
                bool isLive = isUntracked || (i < trackedCount && live[i]);
                if (Tracing)
                {
                    ulong v = CoffGcInfoResolver.ResolveSlotValue(in slots[i], ctx, in hdr, callerSp);
                    OS.Hal.Console.Write("[walk-trace]   slot "); OS.Hal.Console.WriteUInt((uint)i);
                    OS.Hal.Console.Write(" kind="); OS.Hal.Console.WriteUInt(slots[i].Kind);
                    OS.Hal.Console.Write(" base="); OS.Hal.Console.WriteUInt(slots[i].SpBase);
                    OS.Hal.Console.Write(" flags=0x"); OS.Hal.Console.WriteHex(slots[i].Flags);
                    OS.Hal.Console.Write(" off="); OS.Hal.Console.WriteInt(slots[i].RegOrOffset);
                    OS.Hal.Console.Write(isLive ? " LIVE" : " dead");
                    OS.Hal.Console.Write(" value=0x"); OS.Hal.Console.WriteHex(v);
                    OS.Hal.Console.WriteLine("");
                }
                if (!isLive) continue;

                // A frame below the top is stopped at a call, and the call
                // clobbers the scratch registers: what the context holds for
                // them is someone else's value (GcInfoDecoder::ReportSlotToGC
                // skips them the same way unless the frame is the active one).
                if (!isActiveFrame && slots[i].Kind == 0 && IsScratchRegister(slots[i].RegOrOffset))
                    continue;

                ulong value = CoffGcInfoResolver.ResolveSlotValue(in slots[i], ctx, in hdr, callerSp);
                if (value == 0) continue;

                if (s_markRoot != null) s_markRoot((nuint)value);
                else GcMark.MarkFromRoot((nint)value);
                LastRootsMarked++;
            }
        }
    }

}
