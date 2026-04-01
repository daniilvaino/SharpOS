namespace OS.Boot
{
    internal static unsafe class UefiBootInfoBuilder
    {
        public static BootInfo Build(BootContext context)
        {
            EFI_SYSTEM_TABLE* systemTable = context.SystemTable;
            UefiPlatformBridge.Initialize(context);

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
            info.FileExists = &UefiPlatformBridge.FileExists;
            info.FileReadAll = &UefiPlatformBridge.FileReadAll;
            info.DirectoryReadEntry = &UefiPlatformBridge.DirectoryReadEntry;

            if (systemTable->ConOut != null)
                info.Capabilities |= PlatformCapabilities.TextOutput;

            if (systemTable->RuntimeServices != null)
                info.Capabilities |= PlatformCapabilities.Shutdown;

            if (UefiMemoryMapBuilder.TryBuild(systemTable, out info.MemoryMap))
            {
                info.MemoryMapAvailable = 1;
                info.Capabilities |= PlatformCapabilities.MemoryMap;
            }

            if (UefiPlatformBridge.HasFileAccess())
                info.Capabilities |= PlatformCapabilities.ExternalElf;

            return info;
        }
    }

    internal static unsafe class UefiPlatformBridge
    {
        private static BootContext s_context;
        private static bool s_initialized;

        private static EFI_SYSTEM_TABLE* s_systemTable;

        public static void Initialize(BootContext context)
        {
            s_context = context;
            s_systemTable = context.SystemTable;
            s_initialized = true;
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

        public static bool HasFileAccess()
        {
            if (!s_initialized)
                return false;

            if (!UefiFile.TryOpenRoot(s_context, out EFI_FILE_PROTOCOL* root))
                return false;

            UefiFile.Close(root);
            return true;
        }

        public static uint FileExists(char* path)
        {
            return (uint)UefiFileLoader.Exists(s_context, path);
        }

        public static uint FileReadAll(char* path, void** buffer, uint* size)
        {
            if (buffer == null || size == null)
                return (uint)BootFileStatus.InvalidParameter;

            BootFileStatus status = UefiFileLoader.ReadAll(s_context, path, out void* image, out uint imageSize);
            *buffer = image;
            *size = imageSize;
            return (uint)status;
        }

        public static uint DirectoryReadEntry(
            char* directoryPath,
            uint index,
            char* nameBuffer,
            uint nameBufferChars,
            uint* nameLength,
            ulong* attributes)
        {
            if (nameLength == null || attributes == null)
                return (uint)BootFileStatus.InvalidParameter;

            BootFileStatus status = UefiFileLoader.ReadDirectoryEntry(
                s_context,
                directoryPath,
                index,
                nameBuffer,
                nameBufferChars,
                out uint readLength,
                out ulong readAttributes);

            *nameLength = readLength;
            *attributes = readAttributes;
            return (uint)status;
        }
    }
}
