using OS.Kernel.Paging;
using OS.Kernel.Util;

namespace OS.Kernel.Exec
{
    internal static unsafe class JumpStub
    {
        private const ulong PageSize = X64PageTable.PageSize;

        private static bool s_initialized;
        private static delegate* unmanaged<ulong, ulong, ulong, int> s_jump;

        public static bool Run(ulong entryPhysicalAddress, ulong stackTopPhysicalAddress, ulong startupBlockPhysicalAddress, out int exitCode)
        {
            exitCode = 0;

            if (!s_initialized && !TryInitialize())
                return false;

            exitCode = s_jump(entryPhysicalAddress, stackTopPhysicalAddress, startupBlockPhysicalAddress);
            return true;
        }

        private static bool TryInitialize()
        {
            ulong page = global::OS.Kernel.PhysicalMemory.AllocPage();
            if (page == 0)
                return false;

            OS.Kernel.Util.Memory.Zero((void*)page, (uint)PageSize);
            if (!TryWriteStub((byte*)page))
                return false;

            s_jump = (delegate* unmanaged<ulong, ulong, ulong, int>)page;
            s_initialized = true;
            return true;
        }

        private static bool TryWriteStub(byte* destination)
        {
            if (destination == null)
                return false;

            // Windows x64 ABI:
            // rcx = entry address, rdx = stack top, r8 = startup block pointer.
            // mov rax, rsp
            // mov r9, rdi
            // mov rsp, rdx
            // sub rsp, 0x30
            // mov [rsp+0x20], rax
            // mov [rsp+0x28], r9
            // mov rdi, r8
            // call rcx
            // mov r10, rax
            // mov r9, [rsp+0x28]
            // mov rax, [rsp+0x20]
            // mov rsp, rax
            // mov rdi, r9
            // mov rax, r10
            // ret
            destination[0] = 0x48; destination[1] = 0x89; destination[2] = 0xE0;
            destination[3] = 0x49; destination[4] = 0x89; destination[5] = 0xF9;
            destination[6] = 0x48; destination[7] = 0x89; destination[8] = 0xD4;
            destination[9] = 0x48; destination[10] = 0x83; destination[11] = 0xEC; destination[12] = 0x30;
            destination[13] = 0x48; destination[14] = 0x89; destination[15] = 0x44; destination[16] = 0x24; destination[17] = 0x20;
            destination[18] = 0x4C; destination[19] = 0x89; destination[20] = 0x4C; destination[21] = 0x24; destination[22] = 0x28;
            destination[23] = 0x4C; destination[24] = 0x89; destination[25] = 0xC7;
            destination[26] = 0xFF; destination[27] = 0xD1;
            destination[28] = 0x49; destination[29] = 0x89; destination[30] = 0xC2;
            destination[31] = 0x4C; destination[32] = 0x8B; destination[33] = 0x4C; destination[34] = 0x24; destination[35] = 0x28;
            destination[36] = 0x48; destination[37] = 0x8B; destination[38] = 0x44; destination[39] = 0x24; destination[40] = 0x20;
            destination[41] = 0x48; destination[42] = 0x89; destination[43] = 0xC4;
            destination[44] = 0x4C; destination[45] = 0x89; destination[46] = 0xCF;
            destination[47] = 0x4C; destination[48] = 0x89; destination[49] = 0xD0;
            destination[50] = 0xC3;
            return true;
        }
    }
}
