namespace OS.Boot
{
    internal static unsafe class UefiBootInfoBuilder
    {
        public static BootInfo Build(BootContext context)
        {
            EFI_SYSTEM_TABLE* systemTable = context.SystemTable;
            UefiPlatformBridge.Initialize(systemTable);

            BootInfo info = default;
            info.BootMode = BootMode.Uefi;
            info.FirmwareRevision = systemTable->FirmwareRevision;
            info.FirmwareVendor = systemTable->FirmwareVendor;
            info.Capabilities = PlatformCapabilities.None;
            info.MemoryMapAvailable = 0;
            info.GraphicsAvailable = 0;
            info.MemoryMap = default;
            info.WriteChar = &UefiPlatformBridge.WriteChar;
            info.Shutdown = &UefiPlatformBridge.Shutdown;

            if (systemTable->ConOut != null)
                info.Capabilities |= PlatformCapabilities.TextOutput;

            if (systemTable->RuntimeServices != null)
                info.Capabilities |= PlatformCapabilities.Shutdown;

            if (UefiMemoryMapBuilder.TryBuild(systemTable, out info.MemoryMap))
            {
                info.MemoryMapAvailable = 1;
                info.Capabilities |= PlatformCapabilities.MemoryMap;
            }

            return info;
        }
    }

    internal static unsafe class UefiPlatformBridge
    {
        private static EFI_SYSTEM_TABLE* s_systemTable;

        public static void Initialize(EFI_SYSTEM_TABLE* systemTable)
        {
            s_systemTable = systemTable;
        }

        public static void WriteChar(char value)
        {
            if (s_systemTable == null || s_systemTable->ConOut == null)
                return;

            UefiConsole.WriteChar(s_systemTable, value);
        }

        public static void Shutdown()
        {
            if (s_systemTable == null || s_systemTable->RuntimeServices == null)
                return;

            s_systemTable->RuntimeServices->ResetSystem(EFI_RESET_TYPE.EfiResetShutdown, 0, 0, null);
        }
    }
}
