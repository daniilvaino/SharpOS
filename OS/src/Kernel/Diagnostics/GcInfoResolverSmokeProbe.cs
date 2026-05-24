using System;
using System.Runtime.InteropServices;
using OS.Boot.EH;
using OS.Hal;
using OS.Kernel.Memory;
using OS.PAL.SharpOSHost;

namespace OS.Kernel.Diagnostics
{
    // Step 110 Part 6 sanity smoke — synthetic CONTEXT fed into
    // CoffGcInfoResolver. Goal: prove the resolver produces correct
    // pointer values for each live slot at a known PC, without yet
    // having a real frame walker (Part 7).
    //
    // Picks Marker3 (5 register + 1 untracked stack slot) because its
    // slot table exercises both kinds. Builds a Context* with known
    // sentinel values in each GP register, fakes RSP to a stackalloc'd
    // scratch buffer (so SpBase==CurrentSp resolutions land inside our
    // own memory and the dereference is safe), then for each live slot
    // at mid-PC prints the resolved pointer value.
    internal static unsafe class GcInfoResolverSmokeProbe
    {
        public static void Run()
        {
            Log.Write(LogLevel.Info, "---- gcinfo resolver smoke begin ----");

            // 1. Find Marker3's gcInfo.
            delegate*<void> mark3 = &Probe.Marker3;
            byte* ip = (byte*)mark3;
            if (!CoffMethodGcInfo.TryResolve(ip, out CoffMethodGcInfo.Result r))
            {
                Log.Write(LogLevel.Warn, "resolver smoke: TryResolve failed");
                return;
            }

            int rtrMajor = NativeAotModuleInit.ReadyToRunMajor;
            int rtrMinor = NativeAotModuleInit.ReadyToRunMinor;
            int gcInfoVersion = CoffGcInfoDecoder.ReadyToRunVersionToGcInfoVersion(rtrMajor, rtrMinor);

            CoffGcInfoDecoder.DecodeHeader(r.GcInfo, gcInfoVersion, out CoffGcInfoHeader hdr);

            // 2. Decode slot details (same path as dump probe).
            int afterSp = CoffGcInfoDecoder.SkipSafePointOffsets(r.GcInfo, in hdr, hdr.BitOffsetAfterHeader);
            int afterIr = CoffGcInfoDecoder.SkipInterruptibleRanges(r.GcInfo, in hdr, afterSp);
            Span<CoffGcSlot> slots = stackalloc CoffGcSlot[32];
            CoffGcInfoDecoder.DecodeFullSlotTable(r.GcInfo, afterIr, slots, out CoffGcSlotTable counts);

            // 3. Live state at mid-PC (where Marker3 had 3 live slots).
            uint range = (uint)(r.MethodEnd - r.MethodStart);
            uint midPc = range / 2;
            Span<bool> live = stackalloc bool[(int)counts.NumTracked];
            CoffGcInfoDecoder.EnumerateLiveSlotsAtPc(r.GcInfo, gcInfoVersion, midPc, live);

            // 4. Build a synthetic CONTEXT. Each register carries a
            //    sentinel = 0xCC000000 | regIndex so resolved values are
            //    visually identifiable.
            Context ctx = default;
            ctx.Rax = 0xCC00000000000000UL | 0;
            ctx.Rcx = 0xCC00000000000000UL | 1;
            ctx.Rdx = 0xCC00000000000000UL | 2;
            ctx.Rbx = 0xCC00000000000000UL | 3;
            ctx.Rbp = 0xCC00000000000000UL | 5;
            ctx.Rsi = 0xCC00000000000000UL | 6;
            ctx.Rdi = 0xCC00000000000000UL | 7;
            ctx.R8  = 0xCC00000000000000UL | 8;
            ctx.R9  = 0xCC00000000000000UL | 9;
            ctx.R10 = 0xCC00000000000000UL | 10;
            ctx.R11 = 0xCC00000000000000UL | 11;
            ctx.R12 = 0xCC00000000000000UL | 12;
            ctx.R13 = 0xCC00000000000000UL | 13;
            ctx.R14 = 0xCC00000000000000UL | 14;
            ctx.R15 = 0xCC00000000000000UL | 15;

            //    For stack-resident slots: point Rsp at a 256-byte scratch
            //    block on our own stack and seed every 8-byte slot with a
            //    distinct sentinel so we can verify the offset math.
            ulong* scratch = stackalloc ulong[32];
            for (int i = 0; i < 32; i++)
                scratch[i] = 0xBB00000000000000UL | (uint)(i * 8);
            ctx.Rsp = (ulong)scratch;

            // 5. Walk every slot, resolve, print.
            int liveTracked = 0;
            int untrackedAlways = 0;
            for (int i = 0; i < (int)counts.NumSlots && i < slots.Length; i++)
            {
                bool isUntracked = (slots[i].Flags & CoffGcSlotFlags.Untracked) != 0;
                bool isLive;
                if (isUntracked)
                {
                    isLive = true;     // untracked = always live for whole method
                    untrackedAlways++;
                }
                else
                {
                    isLive = i < live.Length && live[i];
                    if (isLive) liveTracked++;
                }
                if (!isLive) continue;

                ulong value = CoffGcInfoResolver.ResolveSlotValue(in slots[i], &ctx, in hdr);

                Log.Begin(LogLevel.Info);
                Console.Write("[gcresolve] slot[");
                Console.WriteInt(i);
                Console.Write("] ");
                if (slots[i].Kind == 0)
                {
                    Console.Write("reg=");
                    Console.WriteInt(slots[i].RegOrOffset);
                }
                else
                {
                    Console.Write("stack ");
                    Console.WriteInt(slots[i].SpBase);
                    Console.Write(slots[i].RegOrOffset >= 0 ? "+" : "");
                    Console.WriteInt(slots[i].RegOrOffset);
                }
                Console.Write(isUntracked ? " UNTRACKED " : " LIVE@mid ");
                Console.Write("value=0x");
                Console.WriteHex(value);
                Log.EndLine();
            }

            Log.Begin(LogLevel.Info);
            Console.Write("[gcresolve] tracked-live=");
            Console.WriteInt(liveTracked);
            Console.Write(" untracked=");
            Console.WriteInt(untrackedAlways);
            Console.Write(" (expected 3 + 1 for Marker3 at mid-pc)");
            Log.EndLine();

            Log.Write(LogLevel.Info, "---- gcinfo resolver smoke end ----");
        }

        // Re-uses the same Marker3 method shape as CoffGcInfoDumpProbe to
        // keep slot table layout deterministic between probes.
        private static class Probe
        {
            private static int s_sinkC;

            public static void Marker3()
            {
                string s = "abc";
                int n = s.Length;
                for (int i = 0; i < n; i++) s_sinkC += s[i];
            }
        }
    }
}
