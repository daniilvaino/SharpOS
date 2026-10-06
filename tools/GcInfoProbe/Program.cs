// GcInfoProbe — a method's GcInfo decoded on the build host by the kernel's
// own decoder (OS/src/Boot/EH/CoffGcInfoDecoder.cs): header, slot table, and
// the live slots at each call site asked about. Written when `dynamic`'s
// registration lost roots under GC stress (step193): it showed a slim header
// decoded with no stack base register while the slots were RBP-relative.
//
//   dotnet run -c Release -- <image.exe> <method RVA hex> <return-address RVA hex>...
//
// RVAs from the PDB (llvm-pdbutil dump -publics) and a disassembly
// (llvm-objdump -d); a return address is the instruction after the call.

using System;
using System.IO;
using OS.Boot.EH;

namespace OS.PAL.SharpOSHost { internal struct Placeholder { } }

internal static unsafe class Program
{
    private static byte* s_f, s_secs;
    private static int s_nsec;

    private static byte* Map(uint rva)
    {
        for (int i = 0; i < s_nsec; i++)
        {
            byte* s = s_secs + i * 40;
            uint va = *(uint*)(s + 12), raw = *(uint*)(s + 20), rawSize = *(uint*)(s + 16), vsize = *(uint*)(s + 8);
            if (rva >= va && rva < va + Math.Max(vsize, rawSize)) return s_f + raw + (rva - va);
        }
        throw new Exception("rva not mapped " + rva.ToString("x"));
    }

    private static int Main(string[] args)
    {
        byte[] file = File.ReadAllBytes(args[0]);
        uint methodRva = Convert.ToUInt32(args[1], 16);
        fixed (byte* f = file)
        {
            int pe = *(int*)(f + 0x3C);
            ushort nsec = *(ushort*)(f + pe + 6);
            ushort optSize = *(ushort*)(f + pe + 20);
            byte* opt = f + pe + 24;
            byte* secs = opt + optSize;
            uint pdataRva = *(uint*)(opt + 112 + 3 * 8), pdataSize = *(uint*)(opt + 112 + 3 * 8 + 4);
            s_f = f; s_secs = secs; s_nsec = nsec;
            byte* pdata = Map(pdataRva);

            uint begin = 0, unwind = 0;
            for (uint i = 0; i < pdataSize / 12; i++)
            {
                uint b = *(uint*)(pdata + i * 12), e = *(uint*)(pdata + i * 12 + 4);
                if (b == methodRva) { begin = b; unwind = *(uint*)(pdata + i * 12 + 8); break; }
            }
            if (begin == 0) { Console.WriteLine("method not found"); return 1; }
            byte* ui = Map(unwind);
            byte flags = (byte)((ui[0] >> 3) & 0x1F);
            int unwindSize = 4 + 2 * ui[2];
            if ((flags & 3) != 0) { unwindSize = (unwindSize + 3) & ~3; unwindSize += 4; }
            byte* p = ui + unwindSize;
            byte ubf = *p++;
            if ((ubf & 0x10) != 0) p += 4;   // UBF_FUNC_HAS_ASSOCIATED_DATA
            if ((ubf & 0x04) != 0) p += 4;   // UBF_FUNC_HAS_EHINFO
            byte* gc = p;
            int version = CoffGcInfoDecoder.ReadyToRunVersionToGcInfoVersion(9, 0);
            CoffGcInfoDecoder.DecodeHeader(gc, version, out CoffGcInfoHeader hdr);
            Console.WriteLine($"code length 0x{hdr.CodeLength:x} safepoints {hdr.NumSafePoints} ranges {hdr.NumInterruptibleRanges} stackBase {hdr.StackBaseRegister}");
            int afterSp = CoffGcInfoDecoder.SkipSafePointOffsets(gc, in hdr, hdr.BitOffsetAfterHeader);
            int afterIr = CoffGcInfoDecoder.SkipInterruptibleRanges(gc, in hdr, afterSp);
            var slots = new CoffGcSlot[4096];
            CoffGcInfoDecoder.DecodeFullSlotTable(gc, afterIr, slots, out CoffGcSlotTable counts);
            Console.WriteLine($"slots {counts.NumSlots} tracked {counts.NumTracked} regs {counts.NumRegisters}");
            for (int a = 2; a < args.Length; a++)
            {
                uint ret = Convert.ToUInt32(args[a], 16);
                uint off = ret - methodRva - 1;
                var live = new bool[counts.NumTracked];
                bool inRange = CoffGcInfoDecoder.EnumerateLiveSlotsAtPc(gc, version, off, live);
                uint sp = CoffGcInfoDecoder.FindSafePoint(gc, in hdr, hdr.BitOffsetAfterHeader, off);
                Console.Write($"ret 0x{ret:x} (off 0x{off:x}) safepoint {sp}{(sp == hdr.NumSafePoints ? " (none)" : "")} inRange {inRange}: ");
                for (int i = 0; i < live.Length; i++)
                    if (live[i])
                        Console.Write(slots[i].Kind == 0 ? $"reg{slots[i].RegOrOffset} " : $"[base{slots[i].SpBase}{slots[i].RegOrOffset:+#;-#;+0}] ");
                Console.WriteLine();
            }
        }
        return 0;
    }
}
