using OS.Hal;
using OS.Boot.EH;

namespace OS.Hal.Idt
{
    // Diagnostic register dump for CPU exceptions. Called from the managed
    // dispatcher. Intentionally avoids heap allocation — at exception time
    // the heap may be mid-write, the GC may be mid-mark. We use Console.*Raw
    // (stackalloc-only number formatting) and string literals exclusively.
    //
    // Output goes through UEFI Console (we live inside firmware boot
    // services in Phase 0). Once Phase 4 introduces UEFI encapsulation +
    // Phase 5 brings up our framebuffer driver, PanicDump should switch to
    // direct framebuffer writing (the firmware Console is unsafe inside an
    // exception handler — its internal state may be torn).
    internal static unsafe class PanicDump
    {
        public static void Print(InterruptFrame* frame)
        {
            Console.Write("\r\n*** EXCEPTION ***\r\n");

            Console.Write("vector=0x");
            Console.WriteHexRaw(frame->Vector, 2);
            Console.Write(" (");
            Console.Write(VectorName((int)frame->Vector));
            Console.Write(")\r\n");

            Console.Write("errcode=0x");
            Console.WriteHexRaw(frame->ErrorCode, 16);
            if (frame->Vector == 14) // #PF
            {
                Console.Write(" [");
                WritePageFaultBits(frame->ErrorCode);
                Console.Write("]");
            }
            Console.Write("\r\n");

            Console.Write("RIP=0x"); Console.WriteHexRaw(frame->Rip, 16);
            Console.Write(" CS=0x"); Console.WriteHexRaw(frame->Cs, 4);
            Console.Write(" RFLAGS=0x"); Console.WriteHexRaw(frame->Rflags, 8);
            Console.Write("\r\n");

            Console.Write("CR2=0x"); Console.WriteHexRaw(frame->Cr2, 16);
            Console.Write(" SS=0x"); Console.WriteHexRaw(frame->Ss, 4);
            Console.Write(" RSP=0x"); Console.WriteHexRaw(frame->Rsp, 16);
            Console.Write("\r\n");

            Console.Write("RAX=0x"); Console.WriteHexRaw(frame->Rax, 16);
            Console.Write(" RCX=0x"); Console.WriteHexRaw(frame->Rcx, 16);
            Console.Write("\r\n");
            Console.Write("RDX=0x"); Console.WriteHexRaw(frame->Rdx, 16);
            Console.Write(" RBX=0x"); Console.WriteHexRaw(frame->Rbx, 16);
            Console.Write("\r\n");
            Console.Write("RSI=0x"); Console.WriteHexRaw(frame->Rsi, 16);
            Console.Write(" RDI=0x"); Console.WriteHexRaw(frame->Rdi, 16);
            Console.Write("\r\n");
            Console.Write("RBP=0x"); Console.WriteHexRaw(frame->Rbp, 16);
            Console.Write(" R8 =0x"); Console.WriteHexRaw(frame->R8, 16);
            Console.Write("\r\n");
            Console.Write("R9 =0x"); Console.WriteHexRaw(frame->R9, 16);
            Console.Write(" R10=0x"); Console.WriteHexRaw(frame->R10, 16);
            Console.Write("\r\n");
            Console.Write("R11=0x"); Console.WriteHexRaw(frame->R11, 16);
            Console.Write(" R12=0x"); Console.WriteHexRaw(frame->R12, 16);
            Console.Write("\r\n");
            Console.Write("R13=0x"); Console.WriteHexRaw(frame->R13, 16);
            Console.Write(" R14=0x"); Console.WriteHexRaw(frame->R14, 16);
            Console.Write("\r\n");
            Console.Write("R15=0x"); Console.WriteHexRaw(frame->R15, 16);
            Console.Write("\r\n");

            // Image base — needed to convert RIP → RVA for PDB lookups.
            ulong imageBase = (ulong)CoffRuntimeFunctionTable.ImageBase;
            Console.Write("imageBase=0x"); Console.WriteHexRaw(imageBase, 16);
            if (imageBase != 0 && frame->Rip >= imageBase)
            {
                Console.Write(" rva=0x"); Console.WriteHexRaw(frame->Rip - imageBase, 8);
            }
            Console.Write("\r\n");

            // Bytes at [RIP-16, RIP+24] — lets us see the offending opcode
            // и couple of instructions on each side without re-running. Wrapped
            // in a sanity check on RIP pointer to avoid recursive fault.
            byte* rip = (byte*)frame->Rip;
            if (rip != null)
            {
                Console.Write("bytes@RIP-16..+23: ");
                for (int i = -16; i <= 23; i++)
                {
                    if (i == 0) Console.Write("[");
                    Console.WriteHexRaw((ulong)rip[i], 2);
                    if (i == 0) Console.Write("]");
                    else Console.Write(" ");
                }
                Console.Write("\r\n");
            }

            // Stack top — first 16 qwords from RSP. Return addresses to
            // .text (relative to imageBase) get tagged so we can identify
            // the call chain via PDB without manually filtering.
            ulong* sp = (ulong*)frame->Rsp;
            if (sp != null)
            {
                for (int i = 0; i < 16; i++)
                {
                    ulong v = sp[i];
                    Console.Write("  [RSP+0x"); Console.WriteHexRaw((ulong)(i * 8), 3);
                    Console.Write("] = 0x"); Console.WriteHexRaw(v, 16);
                    // Tag .text-range hits: image_base..image_base+0x10000000 is generous.
                    if (imageBase != 0 && v >= imageBase && v < imageBase + 0x10000000UL)
                    {
                        Console.Write("  rva=0x"); Console.WriteHexRaw(v - imageBase, 8);
                    }
                    Console.Write("\r\n");
                }
            }

            Console.Write("*** halting ***\r\n");
        }

        // Intel-defined names for vectors 0..31. Vectors 32+ are platform-
        // specific (IRQ/MSI), shouldn't reach this dispatcher in Phase 0.
        private static string VectorName(int vector)
        {
            switch (vector)
            {
                case 0: return "#DE divide-by-zero";
                case 1: return "#DB debug";
                case 2: return "NMI";
                case 3: return "#BP breakpoint";
                case 4: return "#OF overflow";
                case 5: return "#BR bound-range";
                case 6: return "#UD invalid-opcode";
                case 7: return "#NM device-not-available";
                case 8: return "#DF double-fault";
                case 9: return "coprocessor-segment-overrun";
                case 10: return "#TS invalid-tss";
                case 11: return "#NP segment-not-present";
                case 12: return "#SS stack-segment";
                case 13: return "#GP general-protection";
                case 14: return "#PF page-fault";
                case 15: return "reserved-15";
                case 16: return "#MF x87-fp";
                case 17: return "#AC alignment-check";
                case 18: return "#MC machine-check";
                case 19: return "#XM simd-fp";
                case 20: return "#VE virtualization";
                case 21: return "#CP control-protection";
                default: return "unknown";
            }
        }

        // Decode #PF error code into the canonical Intel mnemonic flags.
        // Bit 0 P  : 0=non-present-page, 1=protection-violation
        // Bit 1 W  : 0=read, 1=write
        // Bit 2 U  : 0=supervisor, 1=user
        // Bit 3 RSV: reserved-bit-set in PTE
        // Bit 4 I  : instruction-fetch
        // Bit 5 PK : protection-key violation
        // Bit 6 SS : shadow-stack
        // Bit 15 SGX
        private static void WritePageFaultBits(ulong code)
        {
            bool first = true;
            WriteBit(code, 0, "P", "NP", ref first);
            WriteBit(code, 1, "W", "R", ref first);
            WriteBit(code, 2, "U", "S", ref first);
            if ((code & (1ul << 3)) != 0) WriteSep(ref first, "RSV");
            if ((code & (1ul << 4)) != 0) WriteSep(ref first, "I");
            if ((code & (1ul << 5)) != 0) WriteSep(ref first, "PK");
            if ((code & (1ul << 6)) != 0) WriteSep(ref first, "SS");
            if ((code & (1ul << 15)) != 0) WriteSep(ref first, "SGX");
        }

        private static void WriteBit(ulong code, int bit, string set, string clear, ref bool first)
        {
            WriteSep(ref first, ((code & (1ul << bit)) != 0) ? set : clear);
        }

        private static void WriteSep(ref bool first, string text)
        {
            if (!first) Console.Write("|");
            Console.Write(text);
            first = false;
        }
    }
}
