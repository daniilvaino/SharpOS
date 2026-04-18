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
        private const uint AbiManifestBufferSize = 64;
        private const uint AbiManifestByteSize = 16;
        private const char AbiManifestSuffixDot = '.';
        private const char AbiManifestSuffixA = 'a';
        private const char AbiManifestSuffixB = 'b';
        private const char AbiManifestSuffixI = 'i';

        private const uint ServiceThunkPageSize = 4096;
        private const uint ServiceThunkSlotSize = 64;
        private const ulong ServiceThunkVirtualBase = 0x0000700000010000UL;
        private const uint ServiceThunkSearchPages = 1024;
        private const ulong KernelLowSyncStart = 0x00100000UL;
        private const ulong KernelLowSyncEndExclusive = 0x20000000UL;

        private enum AbiResolveSource : uint
        {
            Request = 0,
            Manifest = 1,
            Fallback = 2,
        }

        private static int s_exitRequested;
        private static int s_exitCode;
        private static uint s_publishedAbiVersion = AppServiceTable.AbiVersionV1;

        private static bool s_serviceThunksInitialized;
        private static ulong s_serviceThunkPageVirtual;
        private static ulong s_serviceThunkPagePhysical;
        private static ulong s_win64WriteStringThunk;
        private static ulong s_win64WriteUIntThunk;
        private static ulong s_win64WriteHexThunk;
        private static ulong s_win64GetAbiVersionThunk;
        private static ulong s_win64ExitThunk;
        private static ulong s_win64FileExistsThunk;
        private static ulong s_win64ReadFileThunk;
        private static ulong s_win64ReadDirEntryThunk;
        private static ulong s_win64TryReadKeyThunk;
        private static ulong s_win64RunAppThunk;
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
        private static ulong s_win64WriteCharThunk;
        private static ulong s_systemVWriteCharThunk;
        private static ulong s_win64WriteBuildIdThunk;
        private static ulong s_systemVWriteBuildIdThunk;

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
            delegate* managed<uint, void> writeCharAddress = &WriteChar;
            delegate* managed<void> writeBuildIdAddress = &WriteBuildId;

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
            ulong tableWriteCharAddress = 0;
            ulong tableWriteBuildIdAddress = 0;

            if (!EnsureServiceThunks(
                (ulong)writeStringAddress,
                (ulong)writeUIntAddress,
                (ulong)writeHexAddress,
                (ulong)getAbiVersionAddress,
                (ulong)exitAddress,
                (ulong)fileExistsAddress,
                (ulong)readFileAddress,
                (ulong)readDirEntryAddress,
                (ulong)tryReadKeyAddress,
                (ulong)runAppAddress,
                (ulong)writeCharAddress,
                (ulong)writeBuildIdAddress))
            {
                return false;
            }

            if (serviceAbi == AppServiceAbi.SystemV)
            {
                tableWriteStringAddress = s_systemVWriteStringThunk;
                tableWriteUIntAddress = s_systemVWriteUIntThunk;
                tableWriteHexAddress = s_systemVWriteHexThunk;
                tableGetAbiVersionAddress = s_systemVGetAbiVersionThunk;
                tableExitAddress = s_systemVExitThunk;
                tableWriteCharAddress = s_systemVWriteCharThunk;
                tableWriteBuildIdAddress = s_systemVWriteBuildIdThunk;
                if (publishedAbiVersion >= AppServiceTable.AbiVersionV2)
                {
                    tableFileExistsAddress = s_systemVFileExistsThunk;
                    tableReadFileAddress = s_systemVReadFileThunk;
                    tableReadDirEntryAddress = s_systemVReadDirEntryThunk;
                    tableTryReadKeyAddress = s_systemVTryReadKeyThunk;
                    tableRunAppAddress = s_systemVRunAppThunk;
                }
            }
            else
            {
                tableWriteStringAddress = s_win64WriteStringThunk;
                tableWriteUIntAddress = s_win64WriteUIntThunk;
                tableWriteHexAddress = s_win64WriteHexThunk;
                tableGetAbiVersionAddress = s_win64GetAbiVersionThunk;
                tableExitAddress = s_win64ExitThunk;
                tableWriteCharAddress = s_win64WriteCharThunk;
                tableWriteBuildIdAddress = s_win64WriteBuildIdThunk;
                if (publishedAbiVersion >= AppServiceTable.AbiVersionV2)
                {
                    tableFileExistsAddress = s_win64FileExistsThunk;
                    tableReadFileAddress = s_win64ReadFileThunk;
                    tableReadDirEntryAddress = s_win64ReadDirEntryThunk;
                    tableTryReadKeyAddress = s_win64TryReadKeyThunk;
                    tableRunAppAddress = s_win64RunAppThunk;
                }
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
            table.WriteCharAddress = tableWriteCharAddress;
            table.WriteBuildIdAddress = tableWriteBuildIdAddress;

            AppServiceTable* serviceTablePointer = Pager.IsPagerRootActive()
                ? (AppServiceTable*)serviceVirtual
                : (AppServiceTable*)servicePhysical;

            *serviceTablePointer = table;
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

        private static bool EnsureServiceThunks(
            ulong writeStringTarget,
            ulong writeUIntTarget,
            ulong writeHexTarget,
            ulong getAbiVersionTarget,
            ulong exitTarget,
            ulong fileExistsTarget,
            ulong readFileTarget,
            ulong readDirEntryTarget,
            ulong tryReadKeyTarget,
            ulong runAppTarget,
            ulong writeCharTarget,
            ulong writeBuildIdTarget)
        {
            if (s_serviceThunksInitialized)
                return true;

            if (Pager.IsPagerRootActive())
                return false;

            bool initialized = false;
            try
            {
                ulong thunkPagePhysical = global::OS.Kernel.PhysicalMemory.AllocPage();
                if (thunkPagePhysical == 0)
                    return false;

                if (!TryMapServiceThunkPage(thunkPagePhysical, out ulong thunkPageVirtual))
                    return false;

                global::OS.Kernel.Util.Memory.Zero((void*)thunkPagePhysical, ServiceThunkPageSize);

                byte* page = (byte*)thunkPagePhysical;
                uint cursor = 0;

                s_win64WriteStringThunk = thunkPageVirtual + cursor;
                if (!TryWriteWin64OneArgThunk(page + cursor, writeStringTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_win64WriteUIntThunk = thunkPageVirtual + cursor;
                if (!TryWriteWin64OneArgThunk(page + cursor, writeUIntTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_win64WriteHexThunk = thunkPageVirtual + cursor;
                if (!TryWriteWin64OneArgThunk(page + cursor, writeHexTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_win64GetAbiVersionThunk = thunkPageVirtual + cursor;
                if (!TryWriteWin64NoArgThunk(page + cursor, getAbiVersionTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_win64ExitThunk = thunkPageVirtual + cursor;
                if (!TryWriteWin64OneArgThunk(page + cursor, exitTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_win64FileExistsThunk = thunkPageVirtual + cursor;
                if (!TryWriteWin64OneArgThunk(page + cursor, fileExistsTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_win64ReadFileThunk = thunkPageVirtual + cursor;
                if (!TryWriteWin64OneArgThunk(page + cursor, readFileTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_win64ReadDirEntryThunk = thunkPageVirtual + cursor;
                if (!TryWriteWin64OneArgThunk(page + cursor, readDirEntryTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_win64TryReadKeyThunk = thunkPageVirtual + cursor;
                if (!TryWriteWin64OneArgThunk(page + cursor, tryReadKeyTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_win64RunAppThunk = thunkPageVirtual + cursor;
                if (!TryWriteWin64OneArgThunk(page + cursor, runAppTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_systemVWriteStringThunk = thunkPageVirtual + cursor;
                if (!TryWriteSystemVOneArgThunk(page + cursor, writeStringTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_systemVWriteUIntThunk = thunkPageVirtual + cursor;
                if (!TryWriteSystemVOneArgThunk(page + cursor, writeUIntTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_systemVWriteHexThunk = thunkPageVirtual + cursor;
                if (!TryWriteSystemVOneArgThunk(page + cursor, writeHexTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_systemVGetAbiVersionThunk = thunkPageVirtual + cursor;
                if (!TryWriteSystemVNoArgThunk(page + cursor, getAbiVersionTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_systemVExitThunk = thunkPageVirtual + cursor;
                if (!TryWriteSystemVOneArgThunk(page + cursor, exitTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_systemVFileExistsThunk = thunkPageVirtual + cursor;
                if (!TryWriteSystemVOneArgThunk(page + cursor, fileExistsTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_systemVReadFileThunk = thunkPageVirtual + cursor;
                if (!TryWriteSystemVOneArgThunk(page + cursor, readFileTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_systemVReadDirEntryThunk = thunkPageVirtual + cursor;
                if (!TryWriteSystemVOneArgThunk(page + cursor, readDirEntryTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_systemVTryReadKeyThunk = thunkPageVirtual + cursor;
                if (!TryWriteSystemVOneArgThunk(page + cursor, tryReadKeyTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_systemVRunAppThunk = thunkPageVirtual + cursor;
                if (!TryWriteSystemVOneArgThunk(page + cursor, runAppTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_win64WriteCharThunk = thunkPageVirtual + cursor;
                if (!TryWriteWin64OneArgThunk(page + cursor, writeCharTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_systemVWriteCharThunk = thunkPageVirtual + cursor;
                if (!TryWriteSystemVOneArgThunk(page + cursor, writeCharTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_win64WriteBuildIdThunk = thunkPageVirtual + cursor;
                if (!TryWriteWin64NoArgThunk(page + cursor, writeBuildIdTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_systemVWriteBuildIdThunk = thunkPageVirtual + cursor;
                if (!TryWriteSystemVNoArgThunk(page + cursor, writeBuildIdTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_serviceThunkPagePhysical = thunkPagePhysical;
                s_serviceThunkPageVirtual = thunkPageVirtual;
                s_serviceThunksInitialized = true;
                initialized = true;
            }
            finally
            {
                if (!initialized)
                    s_serviceThunksInitialized = false;
            }

            return initialized;
        }

        private static bool TryMapServiceThunkPage(ulong thunkPagePhysical, out ulong thunkPageVirtual)
        {
            thunkPageVirtual = 0;
            if ((thunkPagePhysical & (PageSize - 1)) != 0)
                return false;

            for (uint i = 0; i < ServiceThunkSearchPages; i++)
            {
                ulong candidate = ServiceThunkVirtualBase + ((ulong)i * PageSize);
                if (Pager.TryQuery(candidate, out _, out _))
                    continue;

                if (Pager.Map(candidate, thunkPagePhysical, PageFlags.Writable))
                {
                    thunkPageVirtual = candidate;
                    return true;
                }
            }

            return false;
        }

        private static bool TryWriteWin64OneArgThunk(byte* destination, ulong target)
        {
            if (destination == null || target == 0)
                return false;

            // mov rax, target
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

        private static bool TryWriteWin64NoArgThunk(byte* destination, ulong target)
        {
            return TryWriteWin64OneArgThunk(destination, target);
        }

        private static bool TryWriteSystemVOneArgThunk(byte* destination, ulong target)
        {
            if (destination == null || target == 0)
                return false;

            // mov rcx, rdi
            destination[0] = 0x48;
            destination[1] = 0x89;
            destination[2] = 0xF9;
            // mov rax, target
            destination[3] = 0x48;
            destination[4] = 0xB8;
            WriteU64(destination + 5, target);
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
            return TryWriteWin64OneArgThunk(destination, target);
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

                UiText.WriteChar((char)value);
            }
        }

        private static void WriteUInt(uint value)
        {
            UiText.WriteUInt(value);
        }

        private static void WriteHex(ulong value)
        {
            UiText.Write("0x");
            UiText.WriteHex(value, 16);
        }

        private static void WriteChar(uint codePoint)
        {
            UiText.WriteChar((char)codePoint);
        }

        private static void WriteBuildId()
        {
            UiText.Write(OS.Kernel.SystemBanner.BuildId);
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

            char* pathBuffer = stackalloc char[(int)MaxPathChars];
            if (!TryReadAsciiPath(request->PathAddress, pathBuffer, MaxPathChars))
                return (uint)AppServiceStatus.InvalidParameter;

            if (!TryResolveRunAppAbi(
                pathBuffer,
                request->AppAbiVersion,
                request->ServiceAbi,
                out uint appAbiVersion,
                out AppServiceAbi serviceAbi,
                out AbiResolveSource abiSource))
            {
                return (uint)AppServiceStatus.InvalidParameter;
            }

            LogRunAppAbiSelection(abiSource, appAbiVersion, serviceAbi);

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

        private static bool TryResolveRunAppAbi(
            char* path,
            uint requestedAbiVersion,
            uint requestedServiceAbi,
            out uint appAbiVersion,
            out AppServiceAbi serviceAbi,
            out AbiResolveSource source)
        {
            appAbiVersion = AppServiceTable.AbiVersionV1;
            serviceAbi = AppServiceAbi.WindowsX64;
            source = AbiResolveSource.Fallback;

            bool autoAppAbi = requestedAbiVersion == AppServiceTable.AutoSelectAbiVersion;
            bool autoServiceAbi = requestedServiceAbi == (uint)AppServiceAbi.Auto;

            if (!autoAppAbi && !autoServiceAbi)
            {
                if (!TryParseServiceAbi(requestedServiceAbi, out serviceAbi))
                    return false;

                appAbiVersion = NormalizeAbiVersion(requestedAbiVersion);
                source = AbiResolveSource.Request;
                return true;
            }

            uint resolvedFromRequestAbi = NormalizeAbiVersion(requestedAbiVersion);
            AppServiceAbi resolvedFromRequestService = AppServiceAbi.WindowsX64;
            if (!autoServiceAbi && !TryParseServiceAbi(requestedServiceAbi, out resolvedFromRequestService))
                return false;

            if (TryReadAbiManifest(path, out uint manifestAbiVersion, out AppServiceAbi manifestServiceAbi))
            {
                appAbiVersion = autoAppAbi ? manifestAbiVersion : resolvedFromRequestAbi;
                serviceAbi = autoServiceAbi ? manifestServiceAbi : resolvedFromRequestService;
                source = AbiResolveSource.Manifest;
                return true;
            }

            appAbiVersion = autoAppAbi ? AppServiceTable.AbiVersionV1 : resolvedFromRequestAbi;
            serviceAbi = autoServiceAbi ? AppServiceAbi.WindowsX64 : resolvedFromRequestService;
            source = AbiResolveSource.Fallback;
            return true;
        }

        private static bool TryReadAbiManifest(char* path, out uint appAbiVersion, out AppServiceAbi serviceAbi)
        {
            appAbiVersion = AppServiceTable.AbiVersionV1;
            serviceAbi = AppServiceAbi.WindowsX64;

            BootInfo bootInfo = Platform.GetBootInfo();
            if (bootInfo.FileReadIntoBuffer == null)
                return false;

            char* manifestPath = stackalloc char[(int)MaxPathChars];
            if (!TryBuildAbiManifestPath(path, manifestPath, MaxPathChars))
                return false;

            byte* manifestBuffer = stackalloc byte[(int)AbiManifestBufferSize];
            uint bytesRead = 0;
            uint status = bootInfo.FileReadIntoBuffer(
                manifestPath,
                manifestBuffer,
                AbiManifestBufferSize,
                &bytesRead);

            if (status != (uint)BootFileStatus.Ok)
                return false;

            if (bytesRead < AbiManifestByteSize)
                return false;

            return TryParseAbiManifest(manifestBuffer, out appAbiVersion, out serviceAbi);
        }

        private static bool TryBuildAbiManifestPath(char* path, char* destination, uint destinationChars)
        {
            if (path == null || destination == null || destinationChars < 6)
                return false;

            uint index = 0;
            while (index < destinationChars - 1 && path[index] != '\0')
            {
                destination[index] = path[index];
                index++;
            }

            if (index == destinationChars - 1)
            {
                destination[index] = '\0';
                return false;
            }

            if ((index + 4 + 1) > destinationChars)
            {
                destination[index] = '\0';
                return false;
            }

            destination[index + 0] = AbiManifestSuffixDot;
            destination[index + 1] = AbiManifestSuffixA;
            destination[index + 2] = AbiManifestSuffixB;
            destination[index + 3] = AbiManifestSuffixI;
            destination[index + 4] = '\0';
            return true;
        }

        private static bool TryParseAbiManifest(byte* buffer, out uint appAbiVersion, out AppServiceAbi serviceAbi)
        {
            appAbiVersion = AppServiceTable.AbiVersionV1;
            serviceAbi = AppServiceAbi.WindowsX64;

            if (buffer == null)
                return false;

            if (buffer[0] != (byte)'S' ||
                buffer[1] != (byte)'A' ||
                buffer[2] != (byte)'B' ||
                buffer[3] != (byte)'I')
            {
                return false;
            }

            ushort formatVersion = ReadU16(buffer + 4);
            if (formatVersion != 1)
                return false;

            ushort rawAppAbi = ReadU16(buffer + 6);
            ushort rawServiceAbi = ReadU16(buffer + 8);

            if (rawAppAbi == AppServiceTable.AbiVersionV1)
                appAbiVersion = AppServiceTable.AbiVersionV1;
            else if (rawAppAbi == AppServiceTable.AbiVersionV2)
                appAbiVersion = AppServiceTable.AbiVersionV2;
            else
                return false;

            if (rawServiceAbi == (ushort)AppServiceAbi.WindowsX64)
                serviceAbi = AppServiceAbi.WindowsX64;
            else if (rawServiceAbi == (ushort)AppServiceAbi.SystemV)
                serviceAbi = AppServiceAbi.SystemV;
            else
                return false;

            return true;
        }

        private static ushort ReadU16(byte* source)
        {
            return (ushort)(source[0] | (source[1] << 8));
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

        private static void LogRunAppAbiSelection(AbiResolveSource source, uint appAbiVersion, AppServiceAbi serviceAbi)
        {
            DebugLog.Begin(LogLevel.Info);
            UiText.Write("runapp abi source=");
            UiText.Write(AbiResolveSourceName(source));
            UiText.Write(" app=");
            UiText.WriteUInt(appAbiVersion);
            UiText.Write(" service=");
            UiText.Write(ServiceAbiName(serviceAbi));
            DebugLog.EndLine();
        }

        private static string AbiResolveSourceName(AbiResolveSource source)
        {
            switch (source)
            {
                case AbiResolveSource.Request: return "request";
                case AbiResolveSource.Manifest: return "manifest";
                case AbiResolveSource.Fallback: return "fallback";
                default: return "fallback";
            }
        }

        private static string ServiceAbiName(AppServiceAbi serviceAbi)
        {
            switch (serviceAbi)
            {
                case AppServiceAbi.WindowsX64: return "win64";
                case AppServiceAbi.SystemV: return "sysv";
                default: return "unknown";
            }
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
                DebugLog.Write(LogLevel.Info, "---- child start ----");

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

                    if (!ProcessImageBuilder.TryBuild(ref loadedImage, 0, serviceAbi, appAbiVersion, ProcessImageBuilder.NestedStackMappedTop, out processImage))
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

                    if (!JumpStub.EnsureInitialized())
                    {
                        result = AppServiceStatus.DeviceError;
                        break;
                    }

                    if (!TrySyncKernelLowMappings(ref processImage))
                    {
                        result = AppServiceStatus.DeviceError;
                        break;
                    }

                    if (!Pager.TryGetPagerCr3(out ulong pagerCr3))
                    {
                        result = AppServiceStatus.DeviceError;
                        break;
                    }

                    pagerCr3 &= 0x000FFFFFFFFFF000UL;
                    if (pagerCr3 == 0)
                    {
                        result = AppServiceStatus.DeviceError;
                        break;
                    }

                    int returnExitCode = 0;
                    if (!JumpStub.Run(
                        processImage.EntryPoint,
                        processImage.StackTop,
                        processImage.StartupBlockVirtual,
                        pagerCr3,
                        out returnExitCode))
                    {
                        result = AppServiceStatus.DeviceError;
                        break;
                    }

                    bool exitByService = TryConsumeExit(out int serviceExitCode);
                    exitCode = exitByService ? serviceExitCode : returnExitCode;
                    if (parentSuspended)
                    {
                        DebugLog.Begin(LogLevel.Info);
                        UiText.Write("---- child end: exit=");
                        UiText.WriteInt(exitCode);
                        UiText.Write(" ----");
                        DebugLog.EndLine();
                    }

                    result = AppServiceStatus.Ok;
                }
                while (false);

                if (processBuilt)
                {
                    if (!CleanupProcessMappings(ref processImage, ref loadedImage))
                    {
                        DebugLog.Write(LogLevel.Warn, "child cleanup mappings failed");
                        result = AppServiceStatus.DeviceError;
                    }
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
                        DebugLog.Write(LogLevel.Warn, "parent context restore failed");
                        result = AppServiceStatus.DeviceError;
                    }
                    else
                    {
                        DebugLog.Write(LogLevel.Info, "parent context restored");
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
                processImage.StackTop == 0 ||
                processImage.StartupBlockVirtual == 0)
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

        private static bool TrySyncKernelLowMappings(ref ProcessImage processImage)
        {
            for (ulong current = KernelLowSyncStart; current < KernelLowSyncEndExclusive; current += PageSize)
            {
                if (IsInRange(current, processImage.ImageStart, processImage.ImageEnd))
                    continue;

                if (IsInRange(current, processImage.StackBase, processImage.StackMappedTop))
                    continue;

                if (!Pager.TryQueryKernel(current, out ulong kernelPhysical, out PageFlags kernelFlags))
                    continue;

                ulong kernelPagePhysical = kernelPhysical & ~(PageSize - 1);
                PageFlags normalizedKernelFlags = PageFlagOps.NormalizeForMap(kernelFlags);

                // Skip pages already in pager — don't overwrite intentional mappings
                // (JumpStub, service thunks etc. need executable flags that differ from
                // the kernel CR3 view on real hardware with NX-protected data pages).
                if (Pager.TryQuery(current, out _, out _))
                    continue;

                if (!Pager.Map(current, kernelPagePhysical, kernelFlags))
                    return false;
            }

            return true;
        }

        private static bool IsInRange(ulong address, ulong startInclusive, ulong endExclusive)
        {
            return address >= startInclusive && address < endExclusive;
        }
    }
}
