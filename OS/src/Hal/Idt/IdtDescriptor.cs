using System.Runtime.InteropServices;

namespace OS.Hal.Idt
{
    // x64 IDT gate descriptor — 16 bytes. Layout per Intel SDM Vol. 3A § 6.14.1:
    //
    //   bytes 0..1   offset bits  0..15
    //   bytes 2..3   segment selector (kernel CS)
    //   byte  4      IST bits 0..2 in low 3 bits, rest reserved (zero)
    //   byte  5      type/attributes:
    //                  bit 7    Present (P)
    //                  bits 5..6 DPL (descriptor privilege level)
    //                  bit 4    must be 0 (system)
    //                  bits 0..3 type — 0xE = interrupt gate, 0xF = trap gate
    //   bytes 6..7   offset bits 16..31
    //   bytes 8..11  offset bits 32..63
    //   bytes 12..15 reserved (zero)
    //
    // Interrupt gate (0xE) clears IF on entry — we get reentrancy-safe
    // exception handling without needing CLI in the trampoline.
    [StructLayout(LayoutKind.Sequential, Size = 16)]
    internal unsafe struct IdtDescriptor
    {
        public ushort OffsetLow;
        public ushort Selector;
        public byte IstAndReserved;
        public byte TypeAttr;
        public ushort OffsetMid;
        public uint OffsetHigh;
        public uint Reserved;

        public const byte TypeInterruptGate = 0x8E; // present, DPL=0, interrupt gate
        public const byte TypeTrapGate = 0x8F;      // present, DPL=0, trap gate

        public static void Build(IdtDescriptor* entry, void* handler, ushort selector, byte ist, byte typeAttr)
        {
            ulong addr = (ulong)handler;
            entry->OffsetLow = (ushort)(addr & 0xFFFFu);
            entry->Selector = selector;
            entry->IstAndReserved = (byte)(ist & 0x07);
            entry->TypeAttr = typeAttr;
            entry->OffsetMid = (ushort)((addr >> 16) & 0xFFFFu);
            entry->OffsetHigh = (uint)((addr >> 32) & 0xFFFFFFFFu);
            entry->Reserved = 0;
        }
    }

    // 10-byte IDTR layout for LIDT instruction (m16:m64).
    [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 10)]
    internal struct IdtRegister
    {
        public ushort Limit;   // size of IDT - 1
        public ulong Base;     // pointer to IDT
    }
}
