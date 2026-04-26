using OS.Hal;
using OS.Hal.Idt;

namespace OS.Kernel.Diagnostics
{
    // Phase 0 IDT validation. Triggers a deliberate #PF (null dereference)
    // and verifies the system reaches PanicDump instead of triple-faulting.
    //
    // Run order matters: this probe must come AFTER Idt.Install. Without
    // an IDT the test would reboot the machine, with one we expect a
    // formatted panic and an infinite halt loop — observable in the QEMU
    // log as "*** EXCEPTION ***".
    //
    // The probe is gated behind Run() rather than auto-firing because we
    // do NOT return from a CPU exception. Once Run() executes, the kernel
    // is permanently in panic state. Tests that need to follow must come
    // before this probe.
    internal static unsafe class IdtProbe
    {
        public static void TriggerNullDeref()
        {
            // NULL (address 0x0) turned out to be mapped writable in the
            // active CR3 — OVMF / UEFI maps low memory for legacy IVT
            // compatibility. Writing to it succeeded silently.
            //
            // Use a high canonical address that is definitely unmapped
            // (well beyond physical RAM): 64 TiB = 0x0000_4000_0000_0000.
            // bit 47 = 0, bits 48..63 = 0 → canonical low-half.
            Log.Write(LogLevel.Info, "idt probe: triggering #PF at 0x4000_0000_0000");
            int* p = (int*)0x400000000000UL;
            *p = 42;
            // Unreachable — CPU traps to vector 14 (#PF) before this line.
            Log.Write(LogLevel.Error, "idt probe: returned from #PF (BUG)");
        }
    }
}
