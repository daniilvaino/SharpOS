using System;
using OS.Boot.EH;
using OS.Hal;
using OS.Kernel.Memory;

namespace OS.Kernel.Diagnostics
{
    // Step 110 part 1 — sanity check the gcInfo locator on the kernel
    // image itself. Calls TryResolve on the IPs of a handful of known
    // kernel methods, prints what it finds: method-start RVA, code
    // offset, first 16 bytes of the gcInfo blob.
    //
    // We don't decode anything yet — this is the "did we land on
    // plausible varint-encoded data?" check before the decoder lands.
    // Expected pattern for a small method's gcInfo blob: small varint-
    // shaped bytes (0x00-0x7F or first byte with continuation flag).
    // Random / all-zero / all-0xFF would mean we misaligned the walker.
    internal static unsafe class CoffGcInfoDumpProbe
    {
        public static void Run()
        {
            Log.Write(LogLevel.Info, "---- coff gcinfo dump begin ----");

            int rtrMajor = NativeAotModuleInit.ReadyToRunMajor;
            int rtrMinor = NativeAotModuleInit.ReadyToRunMinor;
            int gcInfoVersion = CoffGcInfoDecoder.ReadyToRunVersionToGcInfoVersion(rtrMajor, rtrMinor);

            Log.Begin(LogLevel.Info);
            Console.Write("[gcinfo] rtr=");
            Console.WriteUInt((uint)rtrMajor);
            Console.Write(".");
            Console.WriteUInt((uint)rtrMinor);
            Console.Write(" gcInfoVersion=");
            Console.WriteUInt((uint)gcInfoVersion);
            Log.EndLine();

            // Use addresses of a few static methods. Cast through delegate*
            // unmanaged to grab an IP we know lies inside a real compiled
            // method body (not a thunk).
            delegate*<void> a = &Probe.Marker1;
            delegate*<void> b = &Probe.Marker2;
            delegate*<void> c = &Probe.Marker3;

            DumpFor("Marker1", (byte*)a, gcInfoVersion);
            DumpFor("Marker2", (byte*)b, gcInfoVersion);
            DumpFor("Marker3", (byte*)c, gcInfoVersion);

            Log.Write(LogLevel.Info, "---- coff gcinfo dump end ----");
        }

        private static void DumpFor(string tag, byte* ip, int gcInfoVersion)
        {
            Log.Begin(LogLevel.Info);
            Console.Write("[gcinfo] ");
            Console.Write(tag);
            Console.Write(" ip=0x");
            Console.WriteHex((ulong)ip);

            if (!CoffMethodGcInfo.TryResolve(ip, out CoffMethodGcInfo.Result r))
            {
                Console.WriteLine(" → resolve FAILED");
                return;
            }

            Console.Write(" methodStart=0x");
            Console.WriteHex((ulong)r.MethodStart);
            Console.Write(" methodEnd=0x");
            Console.WriteHex((ulong)r.MethodEnd);
            Console.Write(" codeOff=0x");
            Console.WriteHex(r.CodeOffset);
            Console.Write(" gcInfo=0x");
            Console.WriteHex((ulong)r.GcInfo);
            Console.Write(" bytes=");
            for (int i = 0; i < 16; i++)
            {
                if (i > 0) Console.Write(" ");
                Console.WriteHex(r.GcInfo[i]);
            }
            Log.EndLine();

            // Decode the header and verify CodeLength matches method
            // range. If it doesn't, the locator / ENCBASE constants /
            // bit-reader algorithm is wrong somewhere.
            CoffGcInfoDecoder.DecodeHeader(r.GcInfo, gcInfoVersion, out CoffGcInfoHeader hdr);
            uint methodRange = (uint)(r.MethodEnd - r.MethodStart);
            bool match = (uint)hdr.CodeLength == methodRange;

            Log.Begin(LogLevel.Info);
            Console.Write("[gcinfo]   header: ");
            Console.Write(hdr.SlimHeader ? "slim" : "fat");
            Console.Write(" codeLen=");
            Console.WriteUInt((uint)hdr.CodeLength);
            Console.Write(" range=");
            Console.WriteUInt(methodRange);
            Console.Write(" ");
            Console.Write(match ? "OK" : "MISMATCH");
            Console.Write(" returnKind=");
            Console.WriteUInt(hdr.ReturnKind);
            Console.Write(" safePts=");
            Console.WriteUInt(hdr.NumSafePoints);
            Console.Write(" ranges=");
            Console.WriteUInt(hdr.NumInterruptibleRanges);
            if (hdr.HasStackBaseRegister)
            {
                Console.Write(" SBR=");
                Console.WriteUInt(hdr.StackBaseRegister);
            }
            if (hdr.HasGsCookie || hdr.HasSecurityObject || hdr.HasGenericsInstContext
                || hdr.HasPspSym || hdr.HasReversePInvokeFrame
                || hdr.HasSizeOfEditAndContinuePreservedArea
                || hdr.WantsReportOnlyLeaf)
            {
                Console.Write(" flags=[");
                if (hdr.HasGsCookie) Console.Write("GS ");
                if (hdr.HasSecurityObject) Console.Write("SEC ");
                if (hdr.HasGenericsInstContext) Console.Write("GEN ");
                if (hdr.HasPspSym) Console.Write("PSP ");
                if (hdr.HasReversePInvokeFrame) Console.Write("RPI ");
                if (hdr.HasSizeOfEditAndContinuePreservedArea) Console.Write("EnC ");
                if (hdr.WantsReportOnlyLeaf) Console.Write("Leaf ");
                Console.Write("]");
            }
            Log.EndLine();

            // Continue past safepoint offsets + interruptible ranges and
            // decode the slot table. We don't keep per-slot detail yet —
            // just the counts, which is enough to tell whether the method
            // has any GC-tracked storage at all (i.e. whether Mark walker
            // would need to do anything for this frame).
            int afterSp = CoffGcInfoDecoder.SkipSafePointOffsets(r.GcInfo, in hdr, hdr.BitOffsetAfterHeader);
            int afterIr = CoffGcInfoDecoder.SkipInterruptibleRanges(r.GcInfo, in hdr, afterSp);
            CoffGcInfoDecoder.DecodeSlotTable(r.GcInfo, afterIr, out CoffGcSlotTable slots);

            Log.Begin(LogLevel.Info);
            Console.Write("[gcinfo]   slots: numSlots=");
            Console.WriteUInt(slots.NumSlots);
            Console.Write(" (reg=");
            Console.WriteUInt(slots.NumRegisters);
            Console.Write(" stack=");
            Console.WriteUInt(slots.NumStackSlots);
            Console.Write(" untracked=");
            Console.WriteUInt(slots.NumUntracked);
            Console.Write(") tracked=");
            Console.WriteUInt(slots.NumTracked);
            Log.EndLine();

            // Part 6 sanity: decode per-slot details (register number /
            // stack offset / flags). For Markers we expect:
            //   - Marker1/2: one register slot. Probably rcx/rdx/etc.
            //   - Marker3: 5 register slots + 1 untracked stack slot
            //     (the `string s` local pinned to a frame slot).
            // Skip safepoint+ranges to find the slot table again — same
            // path as DecodeSlotTable above, but now with details.
            int afterSp2 = CoffGcInfoDecoder.SkipSafePointOffsets(r.GcInfo, in hdr, hdr.BitOffsetAfterHeader);
            int afterIr2 = CoffGcInfoDecoder.SkipInterruptibleRanges(r.GcInfo, in hdr, afterSp2);
            Span<CoffGcSlot> slotDetails = stackalloc CoffGcSlot[32];
            CoffGcInfoDecoder.DecodeFullSlotTable(r.GcInfo, afterIr2, slotDetails, out CoffGcSlotTable countsAgain);

            for (int i = 0; i < (int)countsAgain.NumSlots && i < 32; i++)
            {
                Log.Begin(LogLevel.Info);
                Console.Write("[gcinfo]   slot[");
                Console.WriteInt(i);
                Console.Write("] ");
                ref CoffGcSlot s = ref slotDetails[i];
                if (s.Kind == 0)
                {
                    Console.Write("reg=");
                    Console.Write(RegName(s.RegOrOffset));
                    Console.Write("(");
                    Console.WriteInt(s.RegOrOffset);
                    Console.Write(")");
                }
                else
                {
                    Console.Write("stack ");
                    Console.Write(SpBaseName(s.SpBase));
                    Console.Write(s.RegOrOffset >= 0 ? "+" : "");
                    Console.WriteInt(s.RegOrOffset);
                }
                if ((s.Flags & CoffGcSlotFlags.Untracked) != 0) Console.Write(" UNTRACKED");
                Console.Write(" flags=0x");
                Console.WriteHex(s.Flags);
                Log.EndLine();
            }

            // Part 5 sanity: probe live state at a few PCs inside the method.
            // Expectation pattern for our markers:
            //   - PC at method start (offset 0): typically nothing live (no
            //     incoming managed args, no setup yet).
            //   - PC mid-method: managed refs alive while being used.
            //   - PC near end: typically nothing (epilogue cleared regs).
            if (slots.NumTracked > 0)
            {
                Span<bool> live = stackalloc bool[(int)slots.NumTracked];
                uint range = (uint)(r.MethodEnd - r.MethodStart);
                ProbeLiveAt(r.GcInfo, gcInfoVersion, 0,         live, "start");
                ProbeLiveAt(r.GcInfo, gcInfoVersion, range / 2, live, "mid");
                if (range >= 2)
                    ProbeLiveAt(r.GcInfo, gcInfoVersion, range - 2, live, "end-2");
            }
        }

        private static string RegName(int regNum) => regNum switch
        {
            0 => "rax", 1 => "rcx", 2 => "rdx", 3 => "rbx",
            4 => "rsp", 5 => "rbp", 6 => "rsi", 7 => "rdi",
            8 => "r8",  9 => "r9", 10 => "r10", 11 => "r11",
            12 => "r12", 13 => "r13", 14 => "r14", 15 => "r15",
            _ => "?",
        };

        private static string SpBaseName(byte spBase) => spBase switch
        {
            0 => "callerSp", 1 => "currentSp", 2 => "fpBase",
            _ => "?",
        };

        private static void ProbeLiveAt(byte* gcInfo, int gcInfoVersion, uint pcOff, Span<bool> live, string tag)
        {
            for (int i = 0; i < live.Length; i++) live[i] = false;
            bool ok = CoffGcInfoDecoder.EnumerateLiveSlotsAtPc(gcInfo, gcInfoVersion, pcOff, live);

            int liveCount = 0;
            for (int i = 0; i < live.Length; i++) if (live[i]) liveCount++;

            Log.Begin(LogLevel.Info);
            Console.Write("[gcinfo]   live@");
            Console.Write(tag);
            Console.Write("(pc=0x");
            Console.WriteHex(pcOff);
            Console.Write(") ");
            if (!ok)
            {
                Console.Write("not-in-interruptible-range");
            }
            else
            {
                Console.Write("count=");
                Console.WriteInt(liveCount);
                if (liveCount > 0)
                {
                    Console.Write(" slots=[");
                    for (int i = 0; i < live.Length; i++)
                    {
                        if (live[i])
                        {
                            Console.WriteInt(i);
                            Console.Write(" ");
                        }
                    }
                    Console.Write("]");
                }
            }
            Log.EndLine();
        }

        // Three marker methods we can take addresses of. Bodies kept
        // non-trivial so ILC doesn't fold them away — each calls a noinline
        // sink the others don't use, giving distinct gcInfo blobs.
        private static class Probe
        {
            private static int s_sinkA;
            private static int s_sinkB;
            private static int s_sinkC;

            public static void Marker1()
            {
                int x = 0;
                for (int i = 0; i < 4; i++) x = x * 31 + i;
                s_sinkA = x;
            }

            public static void Marker2()
            {
                int x = 7;
                for (int i = 0; i < 6; i++) x = x ^ (x << 3) ^ i;
                s_sinkB = x;
            }

            public static void Marker3()
            {
                string s = "abc";
                int n = s.Length;
                for (int i = 0; i < n; i++) s_sinkC += s[i];
            }
        }
    }
}
