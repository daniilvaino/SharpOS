using System.Runtime;
using System.Runtime.InteropServices;

namespace OS.Hal
{
    // Host class for x86 port I/O instructions (`in` / `out`). Managed C#
    // can't emit these directly — we need raw machine code. Same pattern
    // as ByRefAssignRefStub: reserve a [RuntimeExport] symbol whose body
    // is a Panic.Fail fallback, then have a patcher overwrite the first
    // few bytes with hand-crafted shellcode at boot time.
    //
    // Calling convention (Win64 ABI, what managed code & C# `delegate*`
    // expect):
    //   Inb(ushort port)              → byte
    //     port in CX (lower 16 of RCX), result in AL (lower 8 of RAX)
    //   Outb(ushort port, byte value) → void
    //     port in CX, value in DL (lower 8 of RDX)
    //
    // The Panic.Fail body is reserved space — only fires if patching
    // failed. Padding inside Panic.Fail's call sequence guarantees
    // the body is comfortably larger than 7 bytes (max shellcode size).
    internal static class PortIoStub
    {
        [RuntimeExport("PortIo_Inb")]
        [UnmanagedCallersOnly(EntryPoint = "PortIo_Inb")]
        private static byte Inb(ushort port)
        {
            OS.Kernel.Panic.Fail("PortIo.Inb (stub not patched)");
            return 0;
        }

        [RuntimeExport("PortIo_Outb")]
        [UnmanagedCallersOnly(EntryPoint = "PortIo_Outb")]
        private static void Outb(ushort port, byte value)
        {
            OS.Kernel.Panic.Fail("PortIo.Outb (stub not patched)");
        }

        public static unsafe void* GetInbAddress()
        {
            delegate* unmanaged<ushort, byte> fn = &Inb;
            return (void*)fn;
        }

        public static unsafe void* GetOutbAddress()
        {
            delegate* unmanaged<ushort, byte, void> fn = &Outb;
            return (void*)fn;
        }
    }
}
