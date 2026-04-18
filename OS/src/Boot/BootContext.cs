using System;

namespace OS.Boot
{
    internal unsafe struct BootContext
    {
        public IntPtr ImageHandle;
        public EFI_SYSTEM_TABLE* SystemTable;
    }
}
