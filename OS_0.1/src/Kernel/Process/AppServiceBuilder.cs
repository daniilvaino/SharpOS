using OS.Boot;
using OS.Hal;
using OS.Kernel.Elf;
using OS.Kernel.Exec;
using OS.Kernel.Input;
using OS.Kernel.Paging;
using OS.Kernel.Util;

namespace OS.Kernel.Process
{
    internal static unsafe class AppServiceBuilder
    {
        private const int MaxWriteStringBytes = 512;
        private const uint MaxPathChars = 260;
        private const uint MaxNameChars = 260;
        private const ulong EfiFileAttributeDirectory = 0x0000000000000010UL;
        private const ulong PageSize = X64PageTable.PageSize;

        private const uint SystemVThunkPageSize = 4096;
        private const uint SystemVOneArgThunkSize = 24;
        private const uint SystemVNoArgThunkSize = 21;

        private static int s_exitRequested;
        private static int s_exitCode;
        private static uint s_publishedAbiVersion = AppServiceTable.AbiVersionV1;

        private static bool s_systemVThunksInitialized;
        private static ulong s_systemVWriteStringThunk;
        private static ulong s_systemVWriteUIntThunk;
        private static ulong s_systemVWriteHexThunk;
        private static ulong s_systemVGetAbiVersionThunk;
        private static ulong s_systemVExitThunk;
        private static ulong s_systemVFileExistsThunk;
        private static ulong s_systemVReadFileThunk;
        private static ulong s_systemVReadDirEntryThunk;
        private static ulong s_systemVTryReadKeyThunk;
        private static ulong s_systemVRunAppThunk;

        public static bool TryBuild(
            ulong serviceVirtual,
            AppServiceAbi serviceAbi,
            uint requestedAbiVersion,
            out ulong servicePhysical)
        {
            servicePhysical = 0;
            if (!Pager.TryQuery(serviceVirtual, out servicePhysical, out _))
                return false;

            uint publishedAbiVersion = NormalizeAbiVersion(requestedAbiVersion);

            delegate* managed<ulong, void> writeStringAddress = &WriteString;
            delegate* managed<uint, void> writeUIntAddress = &WriteUInt;
            delegate* managed<ulong, void> writeHexAddress = &WriteHex;
            delegate* managed<uint> getAbiVersionAddress = &GetAbiVersion;
            delegate* managed<int, void> exitAddress = &Exit;
            delegate* managed<ulong, uint> fileExistsAddress = &FileExists;
            delegate* managed<ulong, uint> readFileAddress = &ReadFile;
            delegate* managed<ulong, uint> readDirEntryAddress = &ReadDirEntry;
            delegate* managed<ulong, uint> tryReadKeyAddress = &TryReadKey;
            delegate* managed<ulong, uint> runAppAddress = &RunApp;

            ulong tableWriteStringAddress = (ulong)writeStringAddress;
            ulong tableWriteUIntAddress = (ulong)writeUIntAddress;
            ulong tableWriteHexAddress = (ulong)writeHexAddress;
            ulong tableGetAbiVersionAddress = (ulong)getAbiVersionAddress;
            ulong tableExitAddress = (ulong)exitAddress;
            ulong tableFileExistsAddress = 0;
            ulong tableReadFileAddress = 0;
            ulong tableReadDirEntryAddress = 0;
            ulong tableTryReadKeyAddress = 0;
            ulong tableRunAppAddress = 0;

            if (serviceAbi == AppServiceAbi.SystemV)
            {
                if (!EnsureSystemVThunks(
                    (ulong)writeStringAddress,
                    (ulong)writeUIntAddress,
                    (ulong)writeHexAddress,
                    (ulong)getAbiVersionAddress,
                    (ulong)exitAddress,
                    (ulong)fileExistsAddress,
                    (ulong)readFileAddress,
                    (ulong)readDirEntryAddress,
                    (ulong)tryReadKeyAddress,
                    (ulong)runAppAddress))
                {
                    return false;
                }

                tableWriteStringAddress = s_systemVWriteStringThunk;
                tableWriteUIntAddress = s_systemVWriteUIntThunk;
                tableWriteHexAddress = s_systemVWriteHexThunk;
                tableGetAbiVersionAddress = s_systemVGetAbiVersionThunk;
                tableExitAddress = s_systemVExitThunk;
                if (publishedAbiVersion >= AppServiceTable.AbiVersionV2)
                {
                    tableFileExistsAddress = s_systemVFileExistsThunk;
                    tableReadFileAddress = s_systemVReadFileThunk;
                    tableReadDirEntryAddress = s_systemVReadDirEntryThunk;
                    tableTryReadKeyAddress = s_systemVTryReadKeyThunk;
                    tableRunAppAddress = s_systemVRunAppThunk;
                }
            }
            else if (publishedAbiVersion >= AppServiceTable.AbiVersionV2)
            {
                tableFileExistsAddress = (ulong)fileExistsAddress;
                tableReadFileAddress = (ulong)readFileAddress;
                tableReadDirEntryAddress = (ulong)readDirEntryAddress;
                tableTryReadKeyAddress = (ulong)tryReadKeyAddress;
                tableRunAppAddress = (ulong)runAppAddress;
            }

            AppServiceTable table = default;
            table.AbiVersion = publishedAbiVersion;
            table.Reserved = 0;
            table.WriteStringAddress = tableWriteStringAddress;
            table.WriteUIntAddress = tableWriteUIntAddress;
            table.WriteHexAddress = tableWriteHexAddress;
            table.GetAbiVersionAddress = tableGetAbiVersionAddress;
            table.ExitAddress = tableExitAddress;
            table.FileExistsAddress = tableFileExistsAddress;
            table.ReadFileAddress = tableReadFileAddress;
            table.ReadDirEntryAddress = tableReadDirEntryAddress;
            table.TryReadKeyAddress = tableTryReadKeyAddress;
            table.RunAppAddress = tableRunAppAddress;

            *((AppServiceTable*)servicePhysical) = table;
            s_exitRequested = 0;
            s_exitCode = 0;
            s_publishedAbiVersion = publishedAbiVersion;
            return true;
        }

        private static uint NormalizeAbiVersion(uint requestedAbiVersion)
        {
            if (requestedAbiVersion <= AppServiceTable.AbiVersionV1)
                return AppServiceTable.AbiVersionV1;

            return AppServiceTable.AbiVersionV2;
        }

        private static bool EnsureSystemVThunks(
            ulong writeStringTarget,
            ulong writeUIntTarget,
            ulong writeHexTarget,
            ulong getAbiVersionTarget,
            ulong exitTarget,
            ulong fileExistsTarget,
            ulong readFileTarget,
            ulong readDirEntryTarget,
            ulong tryReadKeyTarget,
            ulong runAppTarget)
        {
            if (s_systemVThunksInitialized)
                return true;

            ulong thunkPage = global::OS.Kernel.PhysicalMemory.AllocPage();
            if (thunkPage == 0)
                return false;

            global::OS.Kernel.Util.Memory.Zero((void*)thunkPage, SystemVThunkPageSize);

            byte* page = (byte*)thunkPage;
            uint cursor = 0;

            s_systemVWriteStringThunk = thunkPage + cursor;
            if (!TryWriteSystemVOneArgThunk(page + cursor, writeStringTarget))
                return false;
            cursor += SystemVOneArgThunkSize;

            s_systemVWriteUIntThunk = thunkPage + cursor;
            if (!TryWriteSystemVOneArgThunk(page + cursor, writeUIntTarget))
                return false;
            cursor += SystemVOneArgThunkSize;

            s_systemVWriteHexThunk = thunkPage + cursor;
            if (!TryWriteSystemVOneArgThunk(page + cursor, writeHexTarget))
                return false;
            cursor += SystemVOneArgThunkSize;

            s_systemVGetAbiVersionThunk = thunkPage + cursor;
            if (!TryWriteSystemVNoArgThunk(page + cursor, getAbiVersionTarget))
                return false;
            cursor += SystemVNoArgThunkSize;

            s_systemVExitThunk = thunkPage + cursor;
            if (!TryWriteSystemVOneArgThunk(page + cursor, exitTarget))
                return false;
            cursor += SystemVOneArgThunkSize;

            s_systemVFileExistsThunk = thunkPage + cursor;
            if (!TryWriteSystemVOneArgThunk(page + cursor, fileExistsTarget))
                return false;
            cursor += SystemVOneArgThunkSize;

            s_systemVReadFileThunk = thunkPage + cursor;
            if (!TryWriteSystemVOneArgThunk(page + cursor, readFileTarget))
                return false;
            cursor += SystemVOneArgThunkSize;

            s_systemVReadDirEntryThunk = thunkPage + cursor;
            if (!TryWriteSystemVOneArgThunk(page + cursor, readDirEntryTarget))
                return false;
            cursor += SystemVOneArgThunkSize;

            s_systemVTryReadKeyThunk = thunkPage + cursor;
            if (!TryWriteSystemVOneArgThunk(page + cursor, tryReadKeyTarget))
                return false;
            cursor += SystemVOneArgThunkSize;

            s_systemVRunAppThunk = thunkPage + cursor;
            if (!TryWriteSystemVOneArgThunk(page + cursor, runAppTarget))
                return false;
            cursor += SystemVOneArgThunkSize;

            s_systemVThunksInitialized = true;
            return true;
        }

        private static bool TryWriteSystemVOneArgThunk(byte* destination, ulong target)
        {
            if (destination == null || target == 0)
                return false;

            // mov rax, imm64
            destination[0] = 0x48;
            destination[1] = 0xB8;
            WriteU64(destination + 2, target);

            // mov rcx, rdi
            destination[10] = 0x48;
            destination[11] = 0x89;
            destination[12] = 0xF9;

            // sub rsp, 0x28
            destination[13] = 0x48;
            destination[14] = 0x83;
            destination[15] = 0xEC;
            destination[16] = 0x28;

            // call rax
            destination[17] = 0xFF;
            destination[18] = 0xD0;

            // add rsp, 0x28
            destination[19] = 0x48;
            destination[20] = 0x83;
            destination[21] = 0xC4;
            destination[22] = 0x28;

            // ret
            destination[23] = 0xC3;
            return true;
        }

        private static bool TryWriteSystemVNoArgThunk(byte* destination, ulong target)
        {
            if (destination == null || target == 0)
                return false;

            // mov rax, imm64
            destination[0] = 0x48;
            destination[1] = 0xB8;
            WriteU64(destination + 2, target);

            // sub rsp, 0x28
            destination[10] = 0x48;
            destination[11] = 0x83;
            destination[12] = 0xEC;
            destination[13] = 0x28;

            // call rax
            destination[14] = 0xFF;
            destination[15] = 0xD0;

            // add rsp, 0x28
            destination[16] = 0x48;
            destination[17] = 0x83;
            destination[18] = 0xC4;
            destination[19] = 0x28;

            // ret
            destination[20] = 0xC3;
            return true;
        }

        private static void WriteU64(byte* destination, ulong value)
        {
            destination[0] = (byte)(value & 0xFF);
            destination[1] = (byte)((value >> 8) & 0xFF);
            destination[2] = (byte)((value >> 16) & 0xFF);
            destination[3] = (byte)((value >> 24) & 0xFF);
            destination[4] = (byte)((value >> 32) & 0xFF);
            destination[5] = (byte)((value >> 40) & 0xFF);
            destination[6] = (byte)((value >> 48) & 0xFF);
            destination[7] = (byte)((value >> 56) & 0xFF);
        }

        public static bool TryConsumeExit(out int exitCode)
        {
            exitCode = 0;
            if (s_exitRequested == 0)
                return false;

            exitCode = s_exitCode;
            s_exitRequested = 0;
            return true;
        }

        private static void WriteString(ulong textAddress)
        {
            if (textAddress == 0)
                return;

            byte* pointer = (byte*)textAddress;
            for (int i = 0; i < MaxWriteStringBytes; i++)
            {
                byte value = pointer[i];
                if (value == 0)
                    return;

                Console.WriteChar((char)value);
            }
        }

        private static void WriteUInt(uint value)
        {
            Console.WriteUInt(value);
        }

        private static void WriteHex(ulong value)
        {
            Console.Write("0x");
            Console.WriteHex(value, 16);
        }

        private static uint GetAbiVersion()
        {
            return s_publishedAbiVersion;
        }

        private static void Exit(int exitCode)
        {
            s_exitCode = exitCode;
            s_exitRequested = 1;
        }

        private static uint FileExists(ulong requestAddress)
        {
            if (requestAddress == 0)
                return (uint)AppServiceStatus.InvalidParameter;

            AppFileExistsRequest* request = (AppFileExistsRequest*)requestAddress;
            if (request->PathAddress == 0)
                return (uint)AppServiceStatus.InvalidParameter;

            char* pathBuffer = stackalloc char[(int)MaxPathChars];
            if (!TryReadAsciiPath(request->PathAddress, pathBuffer, MaxPathChars))
                return (uint)AppServiceStatus.InvalidParameter;

            BootInfo bootInfo = Platform.GetBootInfo();
            if (bootInfo.FileExists == null)
                return (uint)AppServiceStatus.Unsupported;

            uint status = bootInfo.FileExists(pathBuffer);
            return (uint)MapBootFileStatus(status);
        }

        private static uint ReadFile(ulong requestAddress)
        {
            if (requestAddress == 0)
                return (uint)AppServiceStatus.InvalidParameter;

            AppReadFileRequest* request = (AppReadFileRequest*)requestAddress;
            request->BytesRead = 0;

            if (request->PathAddress == 0 || request->BufferAddress == 0 || request->BufferCapacity == 0)
                return (uint)AppServiceStatus.InvalidParameter;

            char* pathBuffer = stackalloc char[(int)MaxPathChars];
            if (!TryReadAsciiPath(request->PathAddress, pathBuffer, MaxPathChars))
                return (uint)AppServiceStatus.InvalidParameter;

            BootInfo bootInfo = Platform.GetBootInfo();
            if (bootInfo.FileReadIntoBuffer == null)
                return (uint)AppServiceStatus.Unsupported;

            uint bytesRead = 0;
            uint status = bootInfo.FileReadIntoBuffer(
                pathBuffer,
                (void*)request->BufferAddress,
                request->BufferCapacity,
                &bytesRead);

            request->BytesRead = bytesRead;
            return (uint)MapBootFileStatus(status);
        }

        private static uint ReadDirEntry(ulong requestAddress)
        {
            if (requestAddress == 0)
                return (uint)AppServiceStatus.InvalidParameter;

            AppReadDirectoryEntryRequest* request = (AppReadDirectoryEntryRequest*)requestAddress;
            request->NameLength = 0;
            request->IsDirectory = 0;

            if (request->DirectoryPathAddress == 0 || request->NameBufferAddress == 0 || request->NameBufferCapacity == 0)
                return (uint)AppServiceStatus.InvalidParameter;

            char* pathBuffer = stackalloc char[(int)MaxPathChars];
            if (!TryReadAsciiPath(request->DirectoryPathAddress, pathBuffer, MaxPathChars))
                return (uint)AppServiceStatus.InvalidParameter;

            BootInfo bootInfo = Platform.GetBootInfo();
            if (bootInfo.DirectoryReadEntry == null)
                return (uint)AppServiceStatus.Unsupported;

            char* nameBufferUtf16 = stackalloc char[(int)MaxNameChars];
            uint nameLengthUtf16 = 0;
            ulong attributes = 0;

            uint status = bootInfo.DirectoryReadEntry(
                pathBuffer,
                request->EntryIndex,
                nameBufferUtf16,
                MaxNameChars,
                &nameLengthUtf16,
                &attributes);

            AppServiceStatus mappedStatus = MapBootFileStatus(status);
            if (mappedStatus != AppServiceStatus.Ok)
                return (uint)mappedStatus;

            AppServiceStatus copyStatus = TryWriteAsciiName(
                nameBufferUtf16,
                nameLengthUtf16,
                (byte*)request->NameBufferAddress,
                request->NameBufferCapacity,
                out uint asciiNameLength);

            request->NameLength = asciiNameLength;
            if (copyStatus != AppServiceStatus.Ok)
                return (uint)copyStatus;

            request->IsDirectory = (attributes & EfiFileAttributeDirectory) == EfiFileAttributeDirectory ? 1U : 0U;
            return (uint)AppServiceStatus.Ok;
        }

        private static uint TryReadKey(ulong requestAddress)
        {
            if (requestAddress == 0)
                return (uint)AppServiceStatus.InvalidParameter;

            AppReadKeyRequest* request = (AppReadKeyRequest*)requestAddress;
            request->UnicodeChar = 0;
            request->ScanCode = 0;
            request->Reserved = 0;

            KeyReadStatus keyReadStatus = Keyboard.TryReadKey(out KeyInfo key);
            if (keyReadStatus == KeyReadStatus.NoKey)
                return (uint)AppServiceStatus.NoData;

            if (keyReadStatus == KeyReadStatus.Unsupported)
                return (uint)AppServiceStatus.Unsupported;

            if (keyReadStatus == KeyReadStatus.DeviceError)
                return (uint)AppServiceStatus.DeviceError;

            request->UnicodeChar = key.UnicodeChar;
            request->ScanCode = key.ScanCode;
            return (uint)AppServiceStatus.Ok;
        }

        private static uint RunApp(ulong requestAddress)
        {
            if (requestAddress == 0)
                return (uint)AppServiceStatus.InvalidParameter;

            AppRunAppRequest* request = (AppRunAppRequest*)requestAddress;
            request->ExitCode = 0;

            if (request->PathAddress == 0)
                return (uint)AppServiceStatus.InvalidParameter;

            if (!TryParseServiceAbi(request->ServiceAbi, out AppServiceAbi serviceAbi))
                return (uint)AppServiceStatus.InvalidParameter;

            uint appAbiVersion = NormalizeAbiVersion(request->AppAbiVersion);

            char* pathBuffer = stackalloc char[(int)MaxPathChars];
            if (!TryReadAsciiPath(request->PathAddress, pathBuffer, MaxPathChars))
                return (uint)AppServiceStatus.InvalidParameter;

            int savedExitRequested = s_exitRequested;
            int savedExitCode = s_exitCode;
            uint savedPublishedAbi = s_publishedAbiVersion;

            s_exitRequested = 0;
            s_exitCode = 0;

            AppServiceStatus runStatus = RunExternalApp(pathBuffer, appAbiVersion, serviceAbi, out int childExitCode);
            request->ExitCode = childExitCode;

            s_exitRequested = savedExitRequested;
            s_exitCode = savedExitCode;
            s_publishedAbiVersion = savedPublishedAbi;

            return (uint)runStatus;
        }

        private static bool TryReadAsciiPath(ulong pathAddress, char* destination, uint destinationChars)
        {
            if (pathAddress == 0 || destination == null || destinationChars < 2)
                return false;

            byte* source = (byte*)pathAddress;
            uint i = 0;
            for (; i < destinationChars - 1; i++)
            {
                byte value = source[i];
                if (value == 0)
                {
                    destination[i] = '\0';
                    return i != 0;
                }

                destination[i] = (char)value;
            }

            destination[destinationChars - 1] = '\0';
            return false;
        }

        private static bool TryParseServiceAbi(uint value, out AppServiceAbi serviceAbi)
        {
            if (value == (uint)AppServiceAbi.WindowsX64)
            {
                serviceAbi = AppServiceAbi.WindowsX64;
                return true;
            }

            if (value == (uint)AppServiceAbi.SystemV)
            {
                serviceAbi = AppServiceAbi.SystemV;
                return true;
            }

            serviceAbi = AppServiceAbi.WindowsX64;
            return false;
        }

        private static AppServiceStatus TryWriteAsciiName(
            char* source,
            uint sourceLength,
            byte* destination,
            uint destinationCapacity,
            out uint outputLength)
        {
            outputLength = sourceLength;
            if (source == null || destination == null || destinationCapacity == 0)
                return AppServiceStatus.InvalidParameter;

            if (sourceLength + 1 > destinationCapacity)
                return AppServiceStatus.BufferTooSmall;

            for (uint i = 0; i < sourceLength; i++)
            {
                char value = source[i];
                destination[i] = value <= 0x7F ? (byte)value : (byte)'?';
            }

            destination[sourceLength] = 0;
            return AppServiceStatus.Ok;
        }

        private static AppServiceStatus MapBootFileStatus(uint status)
        {
            if (status == (uint)BootFileStatus.Ok)
                return AppServiceStatus.Ok;

            if (status == (uint)BootFileStatus.NotFound)
                return AppServiceStatus.NotFound;

            if (status == (uint)BootFileStatus.EndOfDirectory)
                return AppServiceStatus.EndOfDirectory;

            if (status == (uint)BootFileStatus.BufferTooSmall)
                return AppServiceStatus.BufferTooSmall;

            if (status == (uint)BootFileStatus.InvalidParameter)
                return AppServiceStatus.InvalidParameter;

            return AppServiceStatus.DeviceError;
        }

        private static AppServiceStatus RunExternalApp(
            char* path,
            uint appAbiVersion,
            AppServiceAbi serviceAbi,
            out int exitCode)
        {
            exitCode = 0;

            BootInfo bootInfo = Platform.GetBootInfo();
            if (bootInfo.FileReadAll == null)
                return AppServiceStatus.Unsupported;

            if (!ProcessManager.TrySuspendCurrentForNested(out MappingContext parentMappingContext, out bool parentSuspended))
                return AppServiceStatus.Unsupported;

            if (parentSuspended)
                Log.Write(LogLevel.Info, "nested app start");

            AppServiceStatus result = AppServiceStatus.DeviceError;
            ElfLoadedImage loadedImage = default;
            ProcessImage processImage = default;
            bool imageLoaded = false;
            bool processBuilt = false;

            try
            {
                do
                {
                    void* imagePointer = null;
                    uint imageSize = 0;
                    uint readStatus = bootInfo.FileReadAll(path, &imagePointer, &imageSize);
                    AppServiceStatus mappedReadStatus = MapBootFileStatus(readStatus);
                    if (mappedReadStatus != AppServiceStatus.Ok)
                    {
                        result = mappedReadStatus;
                        break;
                    }

                    MemoryBlock image = new MemoryBlock(imagePointer, imageSize);
                    if (!image.IsValid)
                    {
                        result = AppServiceStatus.DeviceError;
                        break;
                    }

                    if (!ElfParser.TryParse(image, out ElfParseResult parseResult, out _))
                    {
                        result = AppServiceStatus.Unsupported;
                        break;
                    }

                    if (!TryValidateSegments(ref parseResult))
                    {
                        result = AppServiceStatus.Unsupported;
                        break;
                    }

                    if (!ElfLoader.TryLoad(ref parseResult, out loadedImage, out _))
                    {
                        result = AppServiceStatus.DeviceError;
                        break;
                    }

                    imageLoaded = true;

                    if (!ProcessImageBuilder.TryBuild(ref loadedImage, 0, serviceAbi, appAbiVersion, out processImage))
                    {
                        result = AppServiceStatus.DeviceError;
                        break;
                    }

                    processBuilt = true;

                    if (!TryValidateProcess(ref processImage, appAbiVersion))
                    {
                        result = AppServiceStatus.DeviceError;
                        break;
                    }

                    if (!JumpStub.Run(
                        processImage.EntryPointPhysical,
                        processImage.StackTopPhysical,
                        processImage.StartupBlockPhysical,
                        out int returnExitCode))
                    {
                        result = AppServiceStatus.DeviceError;
                        break;
                    }

                    bool exitByService = TryConsumeExit(out int serviceExitCode);
                    exitCode = exitByService ? serviceExitCode : returnExitCode;
                    if (parentSuspended)
                    {
                        Log.Begin(LogLevel.Info);
                        Console.Write("nested app exit code = ");
                        Console.WriteInt(exitCode);
                        Log.EndLine();
                    }

                    result = AppServiceStatus.Ok;
                }
                while (false);

                if (processBuilt)
                {
                    if (!CleanupProcessMappings(ref processImage, ref loadedImage))
                        result = AppServiceStatus.DeviceError;
                }
                else if (imageLoaded)
                {
                    CleanupLoadedImageMappings(ref loadedImage);
                }
            }
            finally
            {
                if (parentSuspended)
                {
                    if (!ProcessManager.TryRestoreAfterNested(ref parentMappingContext))
                    {
                        Log.Write(LogLevel.Warn, "parent context restore failed");
                        result = AppServiceStatus.DeviceError;
                    }
                    else
                    {
                        Log.Write(LogLevel.Info, "parent context restored");
                    }
                }
            }

            return result;
        }

        private static bool TryValidateSegments(ref ElfParseResult result)
        {
            if (result.Header.Type != ElfType.Executable)
                return false;

            uint loadSegments = 0;
            for (ushort i = 0; i < result.Header.ProgramHeaderCount; i++)
            {
                if (!ElfParser.TryGetProgramHeader(ref result, i, out Elf64ProgramHeader header))
                    return false;

                if (header.Type == ElfProgramType.Interpreter || header.Type == ElfProgramType.Dynamic)
                    return false;

                if (header.Type != ElfProgramType.Load)
                    continue;

                if (header.FileSize > header.MemorySize)
                    return false;

                if (header.Align != 0)
                {
                    ulong mask = header.Align - 1;
                    if ((header.Align & mask) != 0)
                        return false;
                }

                loadSegments++;
            }

            return loadSegments != 0;
        }

        private static bool TryValidateProcess(ref ProcessImage processImage, uint expectedAbiVersion)
        {
            if (processImage.AbiVersion != expectedAbiVersion)
                return false;

            if (processImage.EntryPoint == 0 ||
                processImage.EntryPointPhysical == 0 ||
                processImage.StackTopPhysical == 0 ||
                processImage.StartupBlockPhysical == 0)
            {
                return false;
            }

            if (!Pager.TryQuery(processImage.EntryPoint, out _, out PageFlags entryFlags))
                return false;

            if ((entryFlags & PageFlags.NoExecute) == PageFlags.NoExecute)
                return false;

            if (!Pager.TryQuery(processImage.StackTop - 1, out _, out PageFlags stackFlags))
                return false;

            if ((stackFlags & PageFlags.Writable) != PageFlags.Writable)
                return false;

            return true;
        }

        private static void CleanupLoadedImageMappings(ref ElfLoadedImage loadedImage)
        {
            UnmapMappedRange(loadedImage.LowestVirtualAddress, loadedImage.HighestVirtualAddressExclusive);
        }

        private static bool CleanupProcessMappings(ref ProcessImage processImage, ref ElfLoadedImage loadedImage)
        {
            bool imageCleanupOk = UnmapMappedRange(loadedImage.LowestVirtualAddress, loadedImage.HighestVirtualAddressExclusive);
            bool stackCleanupOk = UnmapMappedRange(processImage.StackBase, processImage.StackMappedTop);
            return imageCleanupOk && stackCleanupOk;
        }

        private static bool UnmapMappedRange(ulong startInclusive, ulong endExclusive)
        {
            if (endExclusive <= startInclusive)
                return true;

            ulong current = AlignDown(startInclusive);
            ulong limit = AlignUp(endExclusive);
            while (current < limit)
            {
                if (Pager.TryQuery(current, out _, out _) && !Pager.Unmap(current))
                    return false;

                if (!TryAdvancePage(ref current))
                    return false;
            }

            return true;
        }

        private static ulong AlignDown(ulong value)
        {
            return value & ~(PageSize - 1);
        }

        private static ulong AlignUp(ulong value)
        {
            ulong mask = PageSize - 1;
            if ((value & mask) == 0)
                return value;

            return (value + mask) & ~mask;
        }

        private static bool TryAdvancePage(ref ulong address)
        {
            if (address > 0xFFFFFFFFFFFFFFFFUL - PageSize)
                return false;

            address += PageSize;
            return true;
        }
    }
}
