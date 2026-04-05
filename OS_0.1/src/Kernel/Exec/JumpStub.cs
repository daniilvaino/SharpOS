using OS.Kernel.Paging;
using OS.Kernel.Util;

namespace OS.Kernel.Exec
{
    internal static unsafe class JumpStub
    {
        private const ulong PageSize = X64PageTable.PageSize;
        private const uint StubPageSize = 4096;

        private static bool s_initialized;
        private static delegate* unmanaged<ulong, ulong, ulong, ulong, int> s_jump;
        private static ulong s_stubVirtualAddress;
        private static ulong s_stubPhysicalAddress;

        public static bool EnsureInitialized()
        {
            if (s_initialized)
                return true;

            if (Pager.IsPagerRootActive())
                return false;

            return TryInitialize();
        }

        public static bool TryGetAddress(out ulong virtualAddress)
        {
            virtualAddress = s_stubVirtualAddress;
            return s_initialized && virtualAddress != 0;
        }

        public static bool Run(
            ulong entryVirtualAddress,
            ulong stackTopVirtualAddress,
            ulong startupBlockVirtualAddress,
            ulong pagerCr3,
            out int exitCode)
        {
            exitCode = 0;

            if (Pager.IsPagerRootActive())
                return false;

            if (!s_initialized)
                return false;

            if ((pagerCr3 & 0x000FFFFFFFFFF000UL) == 0)
                return false;

            if (!Pager.TryQuery(s_stubVirtualAddress, out _, out PageFlags stubFlags))
                return false;

            if ((stubFlags & PageFlags.NoExecute) == PageFlags.NoExecute)
                return false;

            exitCode = s_jump(entryVirtualAddress, stackTopVirtualAddress, startupBlockVirtualAddress, pagerCr3);
            return true;
        }

        private static bool TryInitialize()
        {
            ulong stubPhysical = global::OS.Kernel.PhysicalMemory.AllocPage();
            if (stubPhysical == 0)
                return false;

            if ((stubPhysical & (PageSize - 1)) != 0)
                return false;

            ulong stubVirtual = stubPhysical;
            if (Pager.TryQuery(stubVirtual, out ulong mappedPhysical, out _))
            {
                if ((mappedPhysical & ~(PageSize - 1)) != stubPhysical)
                    return false;
            }
            else if (!Pager.Map(stubVirtual, stubPhysical, PageFlags.Writable))
            {
                return false;
            }

            OS.Kernel.Util.Memory.Zero((void*)stubVirtual, StubPageSize);
            if (!TryWriteStub((byte*)stubVirtual))
                return false;

            s_stubPhysicalAddress = stubPhysical;
            s_stubVirtualAddress = stubVirtual;
            s_jump = (delegate* unmanaged<ulong, ulong, ulong, ulong, int>)stubVirtual;
            s_initialized = true;
            return true;
        }

        private static bool TryWriteStub(byte* destination)
        {
            if (destination == null)
                return false;

            // Windows x64 ABI:
            // rcx = entry address, rdx = stack top, r8 = startup block pointer,
            // r9 = pager CR3.
            // Save kernel context on the kernel stack, disable interrupts,
            // switch to pager CR3, run entry on app stack, then restore CR3/state.
            // Keep original CR3 on the kernel stack (not in a callee-preserved register),
            // because app code is untrusted and may clobber non-volatile regs.
            //
            // mov r11, rsp
            // push r12
            // push r13
            // mov r12, r11
            // mov r13, rdi
            // pushfq
            // cli
            // mov rax, cr3
            // push rax
            // mov cr3, r9
            // mov rsp, rdx
            // sub rsp, 0x20
            // mov rdi, r8
            // call rcx
            // mov r10, rax
            // mov rax, [r12-0x20]
            // mov cr3, rax
            // lea rsp, [r12-0x18]
            // popfq
            // mov rdi, r13
            // pop r13
            // pop r12
            // mov rax, r10
            // ret
            destination[0] = 0x49; destination[1] = 0x89; destination[2] = 0xE3;
            destination[3] = 0x41; destination[4] = 0x54;
            destination[5] = 0x41; destination[6] = 0x55;
            destination[7] = 0x4D; destination[8] = 0x89; destination[9] = 0xDC;
            destination[10] = 0x49; destination[11] = 0x89; destination[12] = 0xFD;
            destination[13] = 0x9C;
            destination[14] = 0xFA;
            destination[15] = 0x0F; destination[16] = 0x20; destination[17] = 0xD8;
            destination[18] = 0x50;
            destination[19] = 0x41; destination[20] = 0x0F; destination[21] = 0x22; destination[22] = 0xD9;
            destination[23] = 0x48; destination[24] = 0x89; destination[25] = 0xD4;
            destination[26] = 0x48; destination[27] = 0x83; destination[28] = 0xEC; destination[29] = 0x20;
            destination[30] = 0x4C; destination[31] = 0x89; destination[32] = 0xC7;
            destination[33] = 0xFF; destination[34] = 0xD1;
            destination[35] = 0x49; destination[36] = 0x89; destination[37] = 0xC2;
            destination[38] = 0x49; destination[39] = 0x8B; destination[40] = 0x44; destination[41] = 0x24; destination[42] = 0xE0;
            destination[43] = 0x0F; destination[44] = 0x22; destination[45] = 0xD8;
            destination[46] = 0x49; destination[47] = 0x8D; destination[48] = 0x64; destination[49] = 0x24; destination[50] = 0xE8;
            destination[51] = 0x9D;
            destination[52] = 0x4C; destination[53] = 0x89; destination[54] = 0xEF;
            destination[55] = 0x41; destination[56] = 0x5D;
            destination[57] = 0x41; destination[58] = 0x5C;
            destination[59] = 0x4C; destination[60] = 0x89; destination[61] = 0xD0;
            destination[62] = 0xC3;
            return true;
        }
    }
}
