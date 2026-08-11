using OS.Hal;
using OS.Kernel.Memory;

namespace OS.Kernel.Diagnostics
{
    // Does a page fault preserve the interrupted code's XMM registers?
    //
    // The demand-paging path is the one interrupt handler that RETURNS: a
    // not-present #PF inside the VM window backs the page and IRETQ-resumes
    // the faulting instruction. Everything that runs in between — the managed
    // dispatcher, the page-table walk, the mapping — is C#, and C# codegen
    // uses XMM registers freely. X64PageTable.MapKernel and TryQueryForRoot
    // both emit `xorps xmm4,xmm4` in the shipped object file.
    //
    // XMM0-5 are volatile in Win64, which means they belong to whatever was
    // interrupted. Before the common stub started saving FP state, this probe
    // failed: the fault silently returned with xmm4 zeroed. It is kept as a
    // permanent check rather than a one-off, because the failure mode is a
    // wrong floating-point value appearing much later with nothing pointing
    // back here.
    //
    // Method: load a known FP image, touch an uncommitted page in the window
    // to take the fault, save FP state again, compare. Only the XMM area is
    // compared — x87 and the status word carry state the fault legitimately
    // changes.
    internal static unsafe class FpFaultProbe
    {
        // FXSAVE image layout (Intel SDM Vol. 1 §10.5.1): XMM0 starts at 160,
        // 16 bytes per register.
        private const int XmmAreaOffset = 160;
        private const int XmmRegisterBytes = 16;
        private const int XmmRegistersChecked = 6;   // XMM0..5, the volatile set
        private const int ImageBytes = 512;

        public static void Run()
        {
            // Two 16-byte aligned images. FXSAVE/FXRSTOR fault on anything
            // less aligned, and KernelHeap payloads are 8-aligned — so the
            // alignment is done by hand rather than assumed.
            byte* storage = stackalloc byte[(ImageBytes * 2) + 16];
            byte* before = Align16(storage);
            byte* after = before + ImageBytes;

            // Seed both images from a real FXSAVE. A hand-built image is a
            // trap: FXRSTOR raises #GP on reserved MXCSR bits, so the only
            // safe starting point is state the CPU itself produced.
            X64Asm.Fxsave(before);
            for (int i = 0; i < ImageBytes; i++) after[i] = before[i];

            // Stamp a recognisable pattern into the volatile XMM registers.
            for (int reg = 0; reg < XmmRegistersChecked; reg++)
            {
                byte* slot = before + XmmAreaOffset + (reg * XmmRegisterBytes);
                for (int b = 0; b < XmmRegisterBytes; b++)
                    slot[b] = (byte)(0xA0 + reg);
            }

            ulong probeVa = VirtualMemory.ProbeAddressForFaultTest;
            if (probeVa == 0)
            {
                Console.WriteLine("[fpfault] SKIP no demand window");
                return;
            }

            uint faultsBefore = VirtualMemory.FaultCommits;

            X64Asm.Fxrstor(before);
            *(ulong*)probeVa = 0x5A5A5A5A5A5A5A5AUL;   // #PF → commit → resume
            X64Asm.Fxsave(after);

            // If the page happened to be mapped already there was no fault, so
            // the handler never ran and matching registers prove nothing. Say
            // so instead of reporting a pass that tested nothing.
            if (VirtualMemory.FaultCommits == faultsBefore)
            {
                Console.WriteLine("[fpfault] SKIP page was already committed - no fault taken");
                return;
            }

            int firstBadRegister = -1;
            for (int reg = 0; reg < XmmRegistersChecked && firstBadRegister < 0; reg++)
            {
                int at = XmmAreaOffset + (reg * XmmRegisterBytes);
                for (int b = 0; b < XmmRegisterBytes; b++)
                {
                    if (after[at + b] != before[at + b]) { firstBadRegister = reg; break; }
                }
            }

            if (firstBadRegister < 0)
            {
                Console.WriteLine("[fpfault] PASS xmm0-5 survive a demand fault");
                return;
            }

            Console.Write("[fpfault] FAIL xmm");
            Console.WriteInt(firstBadRegister);
            Console.WriteLine(" clobbered by the fault handler");
        }

        private static byte* Align16(byte* p)
        {
            ulong v = (ulong)p;
            return (byte*)((v + 15UL) & ~15UL);
        }
    }
}
