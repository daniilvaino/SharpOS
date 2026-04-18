namespace OS.Boot
{
    internal static unsafe class UefiBootInfoBuilder
    {
        public static BootInfo Build(BootContext context)
        {
            EFI_SYSTEM_TABLE* systemTable = context.SystemTable;
            UefiPlatformBridge.Initialize(context);
            UefiConsole.TryMaximizeTextMode(systemTable);

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
            info.KeyboardTryReadKey = &UefiPlatformBridge.KeyboardTryReadKey;
            info.FileExists = &UefiPlatformBridge.FileExists;
            info.FileReadAll = &UefiPlatformBridge.FileReadAll;
            info.FileReadIntoBuffer = &UefiPlatformBridge.FileReadIntoBuffer;
            info.DirectoryReadEntry = &UefiPlatformBridge.DirectoryReadEntry;

            if (systemTable->ConOut != null)
                info.Capabilities |= PlatformCapabilities.TextOutput;

            if (systemTable->RuntimeServices != null)
                info.Capabilities |= PlatformCapabilities.Shutdown;

            if (UefiPlatformBridge.HasKeyboardInput())
                info.Capabilities |= PlatformCapabilities.KeyboardInput;

            // Allocate EfiLoaderCode buffers before the memory map is captured.
            // EfiLoaderCode is mapped executable by firmware even with W^X/NX policies.
            if (systemTable->BootServices != null)
            {
                // Shared exec-stub buffer, layout:
                //   0..63   Cr3Accessor (read stub @0, write stub @32)
                //   64..127 GcStackSpill (callee-saved register push/pop trampoline)
                const uint ExecStubSize = 128;
                void* cr3StubAlloc = null;
                ulong cr3Status = systemTable->BootServices->AllocatePool(
                    EFI_MEMORY_TYPE.EfiLoaderCode, ExecStubSize, &cr3StubAlloc);
                if (cr3Status == 0 && cr3StubAlloc != null)
                {
                    info.ExecStubBuffer = cr3StubAlloc;
                    info.ExecStubBufferSize = ExecStubSize;
                }

                // JumpStub: shellcode called under firmware CR3 (before CR3 switch).
                // Needs page alignment for Pager.Map → allocate 4096+4095 and align up.
                const uint JumpStubRawSize = 4096 + 4095;
                void* jumpStubRaw = null;
                ulong jumpStatus = systemTable->BootServices->AllocatePool(
                    EFI_MEMORY_TYPE.EfiLoaderCode, JumpStubRawSize, &jumpStubRaw);
                if (jumpStatus == 0 && jumpStubRaw != null)
                {
                    ulong raw = (ulong)jumpStubRaw;
                    ulong aligned = (raw + 4095UL) & ~4095UL;
                    info.JumpStubExecBuffer = (void*)aligned;
                    info.JumpStubExecBufferSize = 4096;
                }
            }

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

        public static bool HasKeyboardInput()
        {
            if (!s_initialized)
                return false;

            return UefiKeyboard.IsAvailable(s_systemTable);
        }

        public static uint KeyboardTryReadKey(ushort* unicodeChar, ushort* scanCode)
        {
            if (unicodeChar == null || scanCode == null)
                return (uint)BootKeyReadStatus.InvalidParameter;

            if (!s_initialized)
                return (uint)BootKeyReadStatus.Unsupported;

            BootKeyReadStatus status;
            if (!UefiKeyboard.TryReadKey(s_context, out ushort readUnicodeChar, out ushort readScanCode, out status))
                return (uint)status;

            *unicodeChar = readUnicodeChar;
            *scanCode = readScanCode;
            return (uint)BootKeyReadStatus.Ok;
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

        public static uint FileReadIntoBuffer(char* path, void* buffer, uint capacity, uint* bytesRead)
        {
            if (bytesRead == null)
                return (uint)BootFileStatus.InvalidParameter;

            BootFileStatus status = UefiFileLoader.ReadIntoBuffer(s_context, path, buffer, capacity, out uint readSize);
            *bytesRead = readSize;
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
