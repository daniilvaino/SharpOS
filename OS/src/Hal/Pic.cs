namespace OS.Hal
{
    // Legacy 8259 pair — masked, never used.
    //
    // Before ExitBootServices the firmware owns these: its handlers service
    // the PIT, the PS/2 controller and the RTC, and our IDT deliberately keeps
    // the firmware's entries for vectors 32..255 so that keeps working. After
    // ExitBootServices those handlers are code without an owner — the firmware
    // that installed them is gone.
    //
    // Until now that was survivable only because interrupts stayed disabled
    // post-EBS (CLI, and every driver polls). But the hardware-fault path
    // re-enables them: HwFaultBridge issues STI so a managed catch block runs
    // with interrupts on. One keypress after any post-EBS fault would have
    // delivered IRQ1 straight into dead firmware code.
    //
    // Masking every line closes that: the chips stay wired but silent, and the
    // only interrupts that reach the CPU are the ones we raise ourselves
    // through the local APIC. We mask rather than remap because nothing here
    // is wanted — device interrupts, when they come, will come from the
    // IO-APIC, which is a separate piece of work.
    internal static class Pic
    {
        private const ushort MasterData = 0x21;
        private const ushort SlaveData  = 0xA1;

        private static bool s_masked;

        public static bool IsMasked => s_masked;

        /// <summary>
        /// Mask all 16 legacy IRQ lines. Safe to call once UEFI is gone; NOT
        /// safe before that — the firmware needs its own interrupts to keep
        /// its console and timer alive.
        /// </summary>
        public static void MaskAll()
        {
            // Data port with ICW1 not in progress = the interrupt mask
            // register. 0xFF masks all eight lines on each chip. The slave
            // goes first: it is cascaded through the master's line 2, so
            // masking the master first could leave a slave line asserted with
            // no path to acknowledge it.
            PortIo.Out8(SlaveData, 0xFF);
            PortIo.Out8(MasterData, 0xFF);
            s_masked = true;
        }
    }
}
