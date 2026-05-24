using System.Runtime.InteropServices;
using OS.Boot.EH;
using OS.Hal;
using OS.Kernel.Memory;
using OS.PAL.SharpOSHost;

namespace OS.Kernel.Diagnostics
{
    // Step 110 Part 7 sanity smoke — exercise the GcContextSpill shellcode.
    //
    // Allocates a Context on the local stack, invokes the spill stub
    // passing &ctx + a managed callback. Shellcode writes the live GP
    // register set into ctx and calls back. The callback prints:
    //   - ctx.Rip — should be the IP just after `call rdx` in shellcode's
    //     CALLER (i.e. the address right after our `Invoke` in the
    //     compiled C# helper).
    //   - ctx.Rsp — should point inside our local stack region.
    //   - ctx.Rbp — should be a stack-shape value (typically Rsp + small).
    //   - callee-saved regs should be sensible non-zero values.
    //   - caller-saved regs should be 0 (our spill zeros them).
    //
    // We also verify ctx.Rip resolves via CoffMethodGcInfo to a method
    // inside this image — confirms shellcode RIP capture works end-to-end
    // with the .pdata lookup.
    internal static unsafe class GcContextSpillSmokeProbe
    {
        private static Context s_capturedCtx;
        private static string s_sentinelEcho;

        public static void Run()
        {
            Log.Write(LogLevel.Info, "---- gc context spill smoke begin ----");

            if (!GcContextSpill.IsInitialized)
            {
                Log.Write(LogLevel.Warn, "GcContextSpill not initialized");
                return;
            }

            // Hold a sentinel managed ref live across the Invoke call so
            // GcInfo for Run() at the return-from-Invoke PC includes a
            // tracked slot. JIT will pin `sentinel` to a callee-saved reg
            // (so it survives the call) or to a stack slot.
            string sentinel = new string('Z', 7);

            Log.Begin(LogLevel.Info);
            Console.Write("[ctxspill] sentinel addr=0x");
            byte* anchor;
            fixed (char* pin = sentinel) anchor = (byte*)pin - 12;  // start of string object
            Console.WriteHex((ulong)anchor);
            Log.EndLine();

            fixed (Context* ctxPtr = &s_capturedCtx)
            {
                byte* p = (byte*)ctxPtr;
                for (int i = 0; i < sizeof(Context); i++) p[i] = 0;

                GcContextSpill.Invoke(ctxPtr, &Callback);
            }

            // Post-call use of sentinel — keeps the ref live in JIT's eyes
            // through the Invoke call, so GcInfo lists a tracked slot at
            // the call-site PC pointing at it.
            s_sentinelEcho = sentinel;

            Log.Write(LogLevel.Info, "---- gc context spill smoke end ----");
        }

        // Walk up to `maxFrames` frames from the captured ctx via PE
        // UNWIND_INFO, printing each frame's PC + tracked-slot count + live
        // root values. Stops on the first frame whose Rip doesn't resolve
        // (out of managed code) or after maxFrames.
        private static void WalkAndEnumerate(Context* ctx, int gcInfoVersion, int maxFrames)
        {
            byte* imageBase = OS.Boot.EH.CoffRuntimeFunctionTable.ImageBase;
            for (int frameIdx = 0; frameIdx < maxFrames; frameIdx++)
            {
                byte* rip = (byte*)ctx->Rip;
                if (!CoffMethodGcInfo.TryResolve(rip, out CoffMethodGcInfo.Result r))
                {
                    Log.Begin(LogLevel.Info);
                    Console.Write("[ctxspill]   frame[");
                    Console.WriteInt(frameIdx);
                    Console.Write("] Rip=0x");
                    Console.WriteHex(ctx->Rip);
                    Console.Write(" (unresolved, stop)");
                    Log.EndLine();
                    return;
                }

                Log.Begin(LogLevel.Info);
                Console.Write("[ctxspill]   frame[");
                Console.WriteInt(frameIdx);
                Console.Write("] Rip=0x"); Console.WriteHex(ctx->Rip);
                Console.Write(" methodStart=0x"); Console.WriteHex((ulong)r.MethodStart);
                Console.Write(" codeOff=0x"); Console.WriteHex(r.CodeOffset);
                Log.EndLine();

                EnumerateOneFrame(ctx, in r, gcInfoVersion);

                // Unwind to caller's frame using SehUnwind.VirtualUnwind.
                // It applies the function's UNWIND_CODE'ы to ctx, popping
                // callee-saved regs back from the stack and adjusting Rsp,
                // then sets Rip = popped return address. There are two
                // RuntimeFunction structs in the tree (OS.Boot.EH vs
                // OS.PAL.SharpOSHost) with identical {BeginAddress,
                // EndAddress, UnwindInfoAddress} layout — cast pointer
                // through to satisfy SehUnwind's signature.
                ulong establisher = 0;
                void* handlerData = null;
                OS.PAL.SharpOSHost.SehUnwind.VirtualUnwind(
                    /*handlerType*/ 0,
                    (ulong)imageBase,
                    ctx->Rip,
                    (OS.PAL.SharpOSHost.RuntimeFunction*)r.RuntimeFunction,
                    ctx,
                    &handlerData,
                    &establisher);
            }
        }

        private static void EnumerateOneFrame(Context* ctx, in CoffMethodGcInfo.Result r, int gcInfoVersion)
        {
            CoffGcInfoDecoder.DecodeHeader(r.GcInfo, gcInfoVersion, out CoffGcInfoHeader hdr);
            int afterSp = CoffGcInfoDecoder.SkipSafePointOffsets(r.GcInfo, in hdr, hdr.BitOffsetAfterHeader);
            int afterIr = CoffGcInfoDecoder.SkipInterruptibleRanges(r.GcInfo, in hdr, afterSp);
            System.Span<CoffGcSlot> slots = stackalloc CoffGcSlot[32];
            CoffGcInfoDecoder.DecodeFullSlotTable(r.GcInfo, afterIr, slots, out CoffGcSlotTable counts);

            System.Span<bool> live = stackalloc bool[(int)counts.NumTracked > 0 ? (int)counts.NumTracked : 1];
            bool inRange = CoffGcInfoDecoder.EnumerateLiveSlotsAtPc(
                r.GcInfo, gcInfoVersion, r.CodeOffset, live);

            Log.Begin(LogLevel.Info);
            Console.Write("[ctxspill]     tracked=");
            Console.WriteUInt(counts.NumTracked);
            Console.Write(" untracked=");
            Console.WriteUInt(counts.NumUntracked);
            if (!inRange) Console.Write(" (PC outside interruptible range)");
            Log.EndLine();

            int rootIdx = 0;
            for (int i = 0; i < (int)counts.NumSlots && i < slots.Length; i++)
            {
                bool isUntracked = (slots[i].Flags & CoffGcSlotFlags.Untracked) != 0;
                bool isLive = isUntracked || (inRange && i < live.Length && live[i]);
                if (!isLive) continue;

                ulong value = CoffGcInfoResolver.ResolveSlotValue(in slots[i], ctx, in hdr);

                Log.Begin(LogLevel.Info);
                Console.Write("[ctxspill]     root[");
                Console.WriteInt(rootIdx++);
                Console.Write("] slot=");
                Console.WriteInt(i);
                Console.Write(" ");
                if (slots[i].Kind == 0)
                {
                    Console.Write("reg=");
                    Console.WriteInt(slots[i].RegOrOffset);
                }
                else
                {
                    Console.Write("stack base=");
                    Console.WriteInt(slots[i].SpBase);
                    Console.Write(slots[i].RegOrOffset >= 0 ? "+" : "");
                    Console.WriteInt(slots[i].RegOrOffset);
                }
                Console.Write(isUntracked ? " UNTRACKED " : " LIVE ");
                Console.Write("value=0x");
                Console.WriteHex(value);
                Log.EndLine();
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        [UnmanagedCallersOnly]
        private static void Callback(Context* ctx)
        {
            Log.Begin(LogLevel.Info);
            Console.Write("[ctxspill] Rip=0x"); Console.WriteHex(ctx->Rip);
            Console.Write(" Rsp=0x"); Console.WriteHex(ctx->Rsp);
            Console.Write(" Rbp=0x"); Console.WriteHex(ctx->Rbp);
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("[ctxspill] callee-saved Rbx=0x"); Console.WriteHex(ctx->Rbx);
            Console.Write(" Rdi=0x"); Console.WriteHex(ctx->Rdi);
            Console.Write(" Rsi=0x"); Console.WriteHex(ctx->Rsi);
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("[ctxspill] R12=0x"); Console.WriteHex(ctx->R12);
            Console.Write(" R13=0x"); Console.WriteHex(ctx->R13);
            Console.Write(" R14=0x"); Console.WriteHex(ctx->R14);
            Console.Write(" R15=0x"); Console.WriteHex(ctx->R15);
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("[ctxspill] caller-saved zeroed: Rax=0x"); Console.WriteHex(ctx->Rax);
            Console.Write(" Rcx=0x"); Console.WriteHex(ctx->Rcx);
            Console.Write(" Rdx=0x"); Console.WriteHex(ctx->Rdx);
            Console.Write(" R8=0x"); Console.WriteHex(ctx->R8);
            Log.EndLine();

            // Walk up to 6 frames from the captured ctx via PE
            // UNWIND_INFO, enumerating live roots at each. The first frame
            // is the `Invoke` wrapper (usually 0 roots), then `Run` (where
            // the sentinel should appear), then earlier callers in Phase4.
            int rtrMajor = NativeAotModuleInit.ReadyToRunMajor;
            int rtrMinor = NativeAotModuleInit.ReadyToRunMinor;
            int gcInfoVersion = CoffGcInfoDecoder.ReadyToRunVersionToGcInfoVersion(rtrMajor, rtrMinor);

            WalkAndEnumerate(ctx, gcInfoVersion, maxFrames: 6);
        }
    }
}
