using OS.Hal;

namespace OS.Kernel.Threading
{
    // Phase E2 gate — proves the gs-base swap is reversible and the new
    // TEB is reachable via gs:[N]. Boot-time only (single thread); the
    // real per-switch swap lands in E4. Sequence:
    //
    //   1. Capture current IA32_GS_BASE (CoreCLR primary TEB, or 0 if
    //      probe runs before CoreClrProbe).
    //   2. Allocate a fresh TebFacade with synthetic-but-mapped stack
    //      range (32 KiB above/below current SP).
    //   3. CLI (block IRQs that might read gs).
    //   4. Swap gs base to teb2.
    //   5. Read gs:[Self] (expect teb2 address) and gs:[StackLimit]
    //      (expect probe-supplied limit2).
    //   6. Restore original gs base.
    //   7. STI.
    //   8. Compare reads to expectations; emit info or warn.
    //
    // The CLI/STI fence keeps the probe atomic — between SetActive(teb2)
    // and the restore, no IDT handler (which might dereference gs:[N])
    // can fire. STI is reapplied even on failure paths.
    internal static unsafe class TebFacadeProbe
    {
        public static void Run()
        {
            Log.Write(LogLevel.Info, "TEB facade probe start");

            if (!X64Asm.ReadGsBaseMsr(out ulong originalGsBase))
            {
                Log.Write(LogLevel.Warn, "TEB facade probe: ReadGsBaseMsr unavailable — skipped");
                return;
            }

            int stackMarker = 0;
            ulong sp = (ulong)&stackMarker;
            ulong base2  = (sp + 0x8000UL) & ~0xFFFUL;
            ulong limit2 = (sp - 0x8000UL) & ~0xFFFUL;

            byte* teb2 = TebFacade.Allocate(base2, limit2);
            if (teb2 == null)
            {
                Log.Write(LogLevel.Warn, "TEB facade probe: TebFacade.Allocate failed — skipped");
                return;
            }

            X64Asm.Cli();
            TebFacade.SetActive(teb2);
            ulong readSelf  = X64Asm.ReadGsQword(TebFacade.OffsetSelf);
            ulong readLimit = X64Asm.ReadGsQword(TebFacade.OffsetStackLimit);
            X64Asm.WriteGsBaseMsr(originalGsBase);
            X64Asm.Sti();

            bool selfOk  = readSelf  == (ulong)teb2;
            bool limitOk = readLimit == limit2;

            Log.Begin(selfOk && limitOk ? LogLevel.Info : LogLevel.Warn);
            Console.Write("TEB facade probe: swap ");
            Console.Write(selfOk  ? "Self=ok "  : "Self=FAIL ");
            Console.Write(limitOk ? "Limit=ok " : "Limit=FAIL ");
            Console.Write("teb2=0x"); Console.WriteHex((ulong)teb2);
            Console.Write(" gs.Self=0x"); Console.WriteHex(readSelf);
            Console.Write(" gs.Limit=0x"); Console.WriteHex(readLimit);
            Console.Write(" origGsBase=0x"); Console.WriteHex(originalGsBase);
            Log.EndLine();
        }
    }
}
