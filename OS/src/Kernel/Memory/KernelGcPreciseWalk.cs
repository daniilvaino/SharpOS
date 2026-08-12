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

        public static void RunFromCurrentFrame() => RunFromCurrentFrame(null);

        public static void RunFromCurrentFrame(delegate* unmanaged<nuint, void> markRoot)
        {
            LastFramesWalked = 0;
            LastRootsMarked = 0;
            LastFramesUnresolved = 0;

            if (!IsAvailable) return;

            s_markRoot = markRoot;
            Context ctx = default;
            GcContextSpill.Invoke(&ctx, &WalkCallback);
            s_markRoot = null;
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
            WalkFrames(&ctx);
            s_markRoot = null;
        }

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void WalkCallback(Context* ctx) => WalkFrames(ctx);

        private static void WalkFrames(Context* ctx)
        {
            int rtrMajor = NativeAotModuleInit.ReadyToRunMajor;
            int rtrMinor = NativeAotModuleInit.ReadyToRunMinor;
            int gcInfoVersion = CoffGcInfoDecoder.ReadyToRunVersionToGcInfoVersion(rtrMajor, rtrMinor);

            // Bounded walk — typical kernel boot stack is < 30 frames.
            // Cap protects against runaway loops if unwind glitches.
            const int MaxFrames = 64;

            for (int frameIdx = 0; frameIdx < MaxFrames; frameIdx++)
            {
                byte* rip = (byte*)ctx->Rip;
                if (!CoffMethodGcInfo.TryResolve(rip, out CoffMethodGcInfo.Result r))
                {
                    LastFramesUnresolved++;
                    return;
                }

                LastFramesWalked++;
                MarkOneFrame(ctx, in r, gcInfoVersion);

                // Image base PER FRAME, not one fixed base for the whole walk.
                // A stack that crosses from an app into the kernel (or back)
                // has frames from different PE images, and unwinding one with
                // another's base decodes garbage. The lookup table already
                // knows which image a record came from — it just was not being
                // asked.
                byte* imageBase = CoffRuntimeFunctionTable.ImageBaseForRecord(r.RuntimeFunction);
                if (imageBase == null) return;

                ulong establisher = 0;
                void* handlerData = null;
                SehUnwind.VirtualUnwind(
                    0,
                    (ulong)imageBase,
                    ctx->Rip,
                    (OS.PAL.SharpOSHost.RuntimeFunction*)r.RuntimeFunction,
                    ctx,
                    &handlerData,
                    &establisher);
            }
        }

        private static void MarkOneFrame(Context* ctx, in CoffMethodGcInfo.Result r, int gcInfoVersion)
        {
            CoffGcInfoDecoder.DecodeHeader(r.GcInfo, gcInfoVersion, out CoffGcInfoHeader hdr);

            int afterSp = CoffGcInfoDecoder.SkipSafePointOffsets(r.GcInfo, in hdr, hdr.BitOffsetAfterHeader);
            int afterIr = CoffGcInfoDecoder.SkipInterruptibleRanges(r.GcInfo, in hdr, afterSp);

            System.Span<CoffGcSlot> slots = stackalloc CoffGcSlot[32];
            CoffGcInfoDecoder.DecodeFullSlotTable(r.GcInfo, afterIr, slots, out CoffGcSlotTable counts);

            if (counts.NumSlots == 0) return;

            int trackedCount = (int)counts.NumTracked;
            // stackalloc cannot be 0-sized — use 1 as floor; we just won't read it.
            System.Span<bool> live = stackalloc bool[trackedCount > 0 ? trackedCount : 1];
            bool inRange = CoffGcInfoDecoder.EnumerateLiveSlotsAtPc(
                r.GcInfo, gcInfoVersion, r.CodeOffset, live);

            // When PC is outside any interruptible range we're in a
            // prologue/epilogue transition window. Slot table may name
            // refs that aren't yet/anymore at their canonical home (e.g.
            // FP not set up, callee-saved not yet stored). Skip the whole
            // frame in that case — JIT placed the call site such that
            // GC shouldn't fire there anyway; we just got there as a
            // return PC because the previous frame was inside body.
            if (!inRange) return;

            for (int i = 0; i < (int)counts.NumSlots && i < slots.Length; i++)
            {
                bool isUntracked = (slots[i].Flags & CoffGcSlotFlags.Untracked) != 0;
                bool isLive = isUntracked || (i < trackedCount && live[i]);
                if (!isLive) continue;

                ulong value = CoffGcInfoResolver.ResolveSlotValue(in slots[i], ctx, in hdr);
                if (value == 0) continue;

                if (s_markRoot != null) s_markRoot((nuint)value);
                else GcMark.MarkFromRoot((nint)value);
                LastRootsMarked++;
            }
        }
    }

}
