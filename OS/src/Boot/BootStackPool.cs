using System.Runtime.InteropServices;

namespace OS.Boot
{
    // Statically-allocated stack for the boot thread, sized via
    // [StructLayout(Size = ...)]. Lives in our PE binary's .bss segment
    // (or equivalent — placed by ILC), NOT in UEFI-provided memory. So
    // PhysicalMemory.AllocPages (which scans the UEFI memory map) cannot
    // accidentally hand this range to KernelHeap / GC.
    //
    // The boot thread starts on the UEFI-provided stack (somewhere
    // inside an EfiLoaderData region). EarlyTo PhysicalMemory.Init that
    // region is "Usable" — allocator gives it out as GC heap segment —
    // then push/pop on the still-active boot stack overwrites managed
    // object headers (m_pMT field). Symptom: #GP with RAX = UTF-16
    // chars treated as MethodTable* (see done/step103.md).
    //
    // Fix: very early in boot (in `Boot.Entry`, just after `Platform.Init`)
    // we switch RSP to `Top`, set RBP=0, push a fake return-address 0,
    // and jmp to a no-args continuation. The old UEFI stack is then
    // abandoned — PhysicalMemory may safely give that physical range
    // to GC, since we no longer write to it.
    //
    // 4 MiB is generous (CoreCLR-hosted Regex / Reflection.Emit / JIT
    // recursion needs ~hundreds of KiB; default kernel paths use far
    // less). Same order as the BigStack increase from step72.
    internal static unsafe class BootStackPool
    {
        public const int Size = 4 * 1024 * 1024;

        [StructLayout(LayoutKind.Sequential, Size = Size)]
        private struct Pool { }

        private static Pool s_pool;

        public static byte* Base
        {
            get
            {
                fixed (Pool* p = &s_pool)
                    return (byte*)p;
            }
        }

        public static byte* Top => Base + Size;
    }
}
