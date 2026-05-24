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

        public static void RunFromCurrentFrame()
        {
            LastFramesWalked = 0;
            LastRootsMarked = 0;
            LastFramesUnresolved = 0;

            if (!IsAvailable) return;

            Context ctx = default;
            GcContextSpill.Invoke(&ctx, &WalkCallback);
        }

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void WalkCallback(Context* ctx)
        {
            int rtrMajor = NativeAotModuleInit.ReadyToRunMajor;
            int rtrMinor = NativeAotModuleInit.ReadyToRunMinor;
            int gcInfoVersion = CoffGcInfoDecoder.ReadyToRunVersionToGcInfoVersion(rtrMajor, rtrMinor);

            byte* imageBase = CoffRuntimeFunctionTable.ImageBase;

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

                GcMark.MarkFromRoot((nint)value);
                LastRootsMarked++;
            }
        }
    }

}
