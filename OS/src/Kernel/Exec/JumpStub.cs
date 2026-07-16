using OS.Hal;
using OS.Kernel.Paging;
using OS.Kernel.Util;

namespace OS.Kernel.Exec
{
    internal static unsafe partial class JumpStub
    {
        private const ulong PageSize = X64PageTable.PageSize;
        private const uint StubPageSize = 4096;

        private static bool s_initialized;
        private static delegate* unmanaged<ulong, ulong, ulong, ulong, int> s_jump;
        private static ulong s_stubVirtualAddress;
        private static ulong s_stubPhysicalAddress;

        // EfiLoaderCode buffer provided by the bootloader.
        // Firmware CR3 maps EfiLoaderCode as executable; EfiConventionalMemory is NX on real hardware.
        private static void* s_execBuffer;
        private static uint s_execBufferSize;

        public static void SetExecBuffer(void* buffer, uint size)
        {
            s_execBuffer = buffer;
            s_execBufferSize = size;
        }

        public static bool EnsureInitialized()
        {
            if (s_initialized)
                return true;

            // Phase E1 note: pre-E1 this method ran on firmware CR3 and the
            // guard `if (IsPagerRootActive()) return false;` was a stale
            // defensive check (IsPagerRootActive was never true before E1).
            // Post-E1 kernel CR3 == pager root by design; the EfiLoaderCode
            // buffer is still mapped executable in the clone (deep-copied
            // from firmware), so TryAllocFromExecBuffer still succeeds. The
            // guard would now block every ELF launch — removed.
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
            if (!TryAllocFromExecBuffer(out ulong stubPhysical, out ulong stubVirtual) &&
                !TryAllocFromPhysicalMemory(out stubPhysical, out stubVirtual))
                return false;

            OS.Kernel.Util.Memory.Zero((void*)stubVirtual, StubPageSize);

            byte* dst = (byte*)stubVirtual;
            int icedLen = EmitStubIced(dst, (int)StubPageSize);

            Console.Write("[stub] jumpstub len=0x");
            Console.WriteHex((ulong)icedLen);
            Console.WriteLine("");

            s_stubPhysicalAddress = stubPhysical;
            s_stubVirtualAddress = stubVirtual;
            s_jump = (delegate* unmanaged<ulong, ulong, ulong, ulong, int>)stubVirtual;
            s_initialized = true;
            return true;
        }

        // Preferred: use EfiLoaderCode buffer from bootloader.
        // Firmware CR3 maps it executable — no NX fault when s_jump() is called
        // before the CR3 switch to the pager root.
        private static bool TryAllocFromExecBuffer(out ulong stubPhysical, out ulong stubVirtual)
        {
            stubPhysical = 0;
            stubVirtual = 0;

            if (s_execBuffer == null || s_execBufferSize < StubPageSize)
                return false;

            ulong addr = (ulong)s_execBuffer;
            if ((addr & (PageSize - 1)) != 0)
                return false;   // bootloader must provide page-aligned address

            stubPhysical = addr;
            stubVirtual = addr;

            // The pager clones the firmware CR3, so EfiLoaderCode pages are already mapped.
            if (Pager.TryQuery(stubVirtual, out ulong mappedPhysical, out _))
            {
                // Already in pager (expected). Verify it maps to our physical page.
                if ((mappedPhysical & ~(PageSize - 1)) != stubPhysical)
                    return false;
            }
            else
            {
                // Not cloned for some reason — map it explicitly.
                if (!Pager.Map(stubVirtual, stubPhysical, PageFlags.Writable))
                    return false;
            }

            return true;
        }

        // Fallback: allocate from EfiConventionalMemory.
        // Works on QEMU/OVMF without strict NX policy; fails on real hardware (INSYDE NX).
        private static bool TryAllocFromPhysicalMemory(out ulong stubPhysical, out ulong stubVirtual)
        {
            stubPhysical = 0;
            stubVirtual = 0;

            ulong page = global::OS.Kernel.PhysicalMemory.AllocPage();
            if (page == 0 || (page & (PageSize - 1)) != 0)
                return false;

            stubPhysical = page;
            stubVirtual = page;

            if (Pager.TryQuery(stubVirtual, out ulong mappedPhysical, out _))
            {
                if ((mappedPhysical & ~(PageSize - 1)) != stubPhysical)
                    return false;
            }
            else if (!Pager.Map(stubVirtual, stubPhysical, PageFlags.Writable))
            {
                return false;
            }

            return true;
        }

    }
}
