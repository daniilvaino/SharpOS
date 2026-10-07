namespace OS.Hal.Idt
{
    // A task-state segment of our own, for one thing: a stack for the double
    // fault (step196). Before it the kernel ran on the firmware's GDT, which
    // has no TSS, so every handler ran on the faulting stack — and a stack
    // that had run out could not take the #PF frame, nor the #DF frame after
    // it: the machine reset without a word (limits: "IST / emergency fault
    // stacks").
    //
    // The firmware's descriptors are copied as they are, at the same offsets —
    // CS 0x38 and the data selectors stay valid — and the TSS descriptor goes
    // after them. Only IST1 is filled; the double-fault gate points at it. A
    // #PF does not get an IST of its own: a nested #PF inside its handler would
    // reuse the same stack top. Running out of stack turns into #DF by itself.
    internal static unsafe class Tss
    {
        public const byte DoubleFaultIst = 1;
        private const uint IstStackBytes = 16 * 1024;

        public static bool Installed { get; private set; }

        public static bool TryInstall()
        {
            if (Installed) return true;

            byte* gdtr = stackalloc byte[16];
            if (!X64Asm.StoreGdt(gdtr)) return false;
            ushort limit = *(ushort*)gdtr;
            ulong oldBase = *(ulong*)(gdtr + 2);
            uint used = (uint)limit + 1;
            uint tssDescriptor = (used + 15) & ~15u;
            if (oldBase == 0 || tssDescriptor + 16 > 2048) return false;

            // One page: the GDT copy, then the TSS at 2 KiB.
            byte* page = (byte*)OS.Kernel.Memory.NativeArena.Allocate(4096);
            byte* ist = (byte*)OS.Kernel.Memory.NativeArena.Allocate(IstStackBytes);
            if (page == null || ist == null) return false;

            for (uint i = 0; i < used; i++) page[i] = ((byte*)oldBase)[i];
            byte* tss = page + 2048;
            for (int i = 0; i < 104; i++) tss[i] = 0;
            *(ulong*)(tss + 0x24) = ((ulong)ist + IstStackBytes) & ~0xFUL;   // IST1
            *(ushort*)(tss + 0x66) = 104;                                     // no I/O bitmap

            // A 64-bit available TSS: limit 103, type 9, present.
            ulong tssBase = (ulong)tss;
            byte* d = page + tssDescriptor;
            *(ushort*)(d + 0) = 103;
            *(ushort*)(d + 2) = (ushort)tssBase;
            d[4] = (byte)(tssBase >> 16);
            d[5] = 0x89;
            d[6] = 0;
            d[7] = (byte)(tssBase >> 24);
            *(uint*)(d + 8) = (uint)(tssBase >> 32);
            *(uint*)(d + 12) = 0;

            *(ushort*)gdtr = (ushort)(tssDescriptor + 16 - 1);
            *(ulong*)(gdtr + 2) = (ulong)page;
            if (!X64Asm.LoadGdt(gdtr)) return false;
            if (!X64Asm.LoadTaskRegister((ushort)tssDescriptor)) return false;

            Idt.SetInterruptStack(8, DoubleFaultIst);
            Installed = true;
            return true;
        }
    }
}
