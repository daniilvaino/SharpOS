using OS.Hal;
using OS.Kernel.Paging;

namespace OS.Kernel.Diagnostics
{
    // Can a page be made executable after code has been written into it?
    //
    // This is what VirtualProtect is for, and it is the sequence every JIT
    // performs: allocate writable memory, emit instructions into it, change the
    // protection to executable, jump in. Miss the third step and the jump takes
    // an instruction-fetch fault on a non-executable page — which is exactly
    // the failure that ended a preempted PowerShell session:
    //
    //     HW fault: vec=14 RIP=0x500000B392C0 CR2=0x500000B392C0 ERR=0x11
    //     PFEC: I=1 (instr-fetch protection)   PTE=0x800000000D3C7063 NX=1
    //
    // Written BEFORE the fix, on purpose. A test authored after the code it
    // checks tends to encode what the code does rather than what it should do;
    // this one is red first and states the requirement in its own terms.
    //
    // Red state today is a panic naming SharpOSHost_ProtectPages, because the
    // export is a Panic.Fail stub. That is a legitimate failure — it names the
    // missing thing — but it does halt the boot, so the probe runs late and can
    // be switched off.
    internal static unsafe class ProtectPagesProbe
    {
        // Win32 PAGE_* values, as the PAL contract uses them.
        private const uint PAGE_READWRITE = 0x04;
        private const uint PAGE_EXECUTE_READWRITE = 0x40;

        private const ulong PageSize = 0x1000;
        private const ulong NxBit = 1UL << 63;

        // mov eax, 0x5A5A ; ret
        private const int CodeLength = 6;

        public static void Run()
        {
            ulong pa = OS.Kernel.PhysicalMemory.AllocPage();
            if (pa == 0)
            {
                Console.WriteLine("[protect] SKIP no free frame");
                return;
            }

            // Writable and NOT executable — the state a JIT starts from.
            ulong va = pa;
            if (!X64PageTable.MapKernel(va, pa, PageFlags.Present | PageFlags.Writable | PageFlags.NoExecute))
            {
                Console.WriteLine("[protect] SKIP could not map");
                return;
            }

            byte* code = (byte*)va;
            code[0] = 0xB8; code[1] = 0x5A; code[2] = 0x5A; code[3] = 0x00; code[4] = 0x00;  // mov eax, 0x5A5A
            code[5] = 0xC3;                                                                   // ret

            if (!X64PageTable.TryGetKernelLeafPte(va, out ulong before) || (before & NxBit) == 0)
            {
                // The starting state has to be non-executable or the test
                // proves nothing: a page that was already executable would pass
                // without VirtualProtect doing anything at all.
                Console.WriteLine("[protect] SKIP page not NX to begin with");
                return;
            }

            uint old = 0;
            int rc = OS.PAL.SharpOSHost.SharpOSHostMemory.ProtectPages((void*)va, PageSize, PAGE_EXECUTE_READWRITE, &old);

            if (rc == 0)
            {
                Console.WriteLine("[protect] FAIL ProtectPages refused");
                return;
            }

            if (!X64PageTable.TryGetKernelLeafPte(va, out ulong after) || (after & NxBit) != 0)
            {
                Console.WriteLine("[protect] FAIL page still NX after PAGE_EXECUTE_READWRITE");
                return;
            }

            // The real check. Everything above is about flags; this is about
            // whether the CPU agrees.
            var fn = (delegate* unmanaged<int>)va;
            int value = fn();

            Console.Write("[protect] old=0x");
            Console.WriteHex(old);
            Console.Write(" returned=0x");
            Console.WriteHex((ulong)value);
            Console.WriteLine(value == 0x5A5A ? " PASS" : " FAIL");
        }
    }
}
