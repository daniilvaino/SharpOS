using System;

namespace OS.Boot
{
    internal static unsafe class UefiFile
    {
        private const ulong EfiSuccess = 0;
        private const ulong EfiBufferTooSmall = 0x8000000000000005UL;
        private const ulong EfiFileModeRead = 0x0000000000000001UL;

        private const ulong EfiFileAttributeDirectory = 0x0000000000000010UL;
        private const ulong EfiFilePositionStart = 0;

        public static bool TryOpenRoot(BootContext context, out EFI_FILE_PROTOCOL* root)
        {
            root = null;

            EFI_SYSTEM_TABLE* systemTable = context.SystemTable;
            if (systemTable == null || systemTable->BootServices == null)
                return false;

            EFI_BOOT_SERVICES* bootServices = systemTable->BootServices;
            if (bootServices->HandleProtocol == null)
                return false;

            EFI_GUID loadedImageGuid = LoadedImageProtocolGuid();
            EFI_LOADED_IMAGE_PROTOCOL* loadedImage = null;
            ulong status = bootServices->HandleProtocol(context.ImageHandle, &loadedImageGuid, (void**)&loadedImage);
            if (status != EfiSuccess || loadedImage == null)
                return false;

            EFI_GUID simpleFileSystemGuid = SimpleFileSystemProtocolGuid();
            EFI_SIMPLE_FILE_SYSTEM_PROTOCOL* simpleFs = null;
            status = bootServices->HandleProtocol(loadedImage->DeviceHandle, &simpleFileSystemGuid, (void**)&simpleFs);
            if (status != EfiSuccess || simpleFs == null || simpleFs->OpenVolume == null)
                return false;

            EFI_FILE_PROTOCOL* rootHandle = null;
            status = simpleFs->OpenVolume(simpleFs, &rootHandle);
            if (status != EfiSuccess || rootHandle == null)
                return false;

            root = rootHandle;
            return true;
        }

        public static bool TryOpenReadOnly(EFI_FILE_PROTOCOL* root, string path, out EFI_FILE_PROTOCOL* file)
        {
            fixed (char* pathPointer = path)
            {
                return TryOpenReadOnly(root, pathPointer, out file);
            }
        }

        public static bool TryOpenReadOnly(EFI_FILE_PROTOCOL* root, char* path, out EFI_FILE_PROTOCOL* file)
        {
            file = null;
            if (root == null || root->Open == null || path == null)
                return false;

            EFI_FILE_PROTOCOL* openedFile = null;
            ulong status = root->Open(root, &openedFile, path, EfiFileModeRead, 0);
            if (status != EfiSuccess || openedFile == null)
                return false;

            file = openedFile;
            return true;
        }

        public static bool TryExists(BootContext context, char* path)
        {
            if (path == null)
                return false;

            if (!TryOpenRoot(context, out EFI_FILE_PROTOCOL* root))
                return false;

            bool exists = TryOpenReadOnly(root, path, out EFI_FILE_PROTOCOL* file);
            if (exists)
                Close(file);

            Close(root);
            return exists;
        }

        public static bool TryReadAll(BootContext context, char* path, out void* buffer, out uint length)
        {
            buffer = null;
            length = 0;

            EFI_SYSTEM_TABLE* systemTable = context.SystemTable;
            if (systemTable == null || systemTable->BootServices == null || path == null)
                return false;

            if (!TryOpenRoot(context, out EFI_FILE_PROTOCOL* root))
                return false;

            if (!TryOpenReadOnly(root, path, out EFI_FILE_PROTOCOL* file))
            {
                Close(root);
                return false;
            }

            bool loaded = TryReadAll(systemTable->BootServices, file, out buffer, out length);
            Close(file);
            Close(root);
            return loaded;
        }

        public static bool TryReadIntoBuffer(
            BootContext context,
            char* path,
            void* destinationBuffer,
            uint destinationCapacity,
            out uint bytesRead,
            out BootFileStatus status)
        {
            bytesRead = 0;
            status = BootFileStatus.DeviceError;

            EFI_SYSTEM_TABLE* systemTable = context.SystemTable;
            if (systemTable == null || systemTable->BootServices == null || path == null || destinationBuffer == null || destinationCapacity == 0)
            {
                status = BootFileStatus.InvalidParameter;
                return false;
            }

            if (!TryOpenRoot(context, out EFI_FILE_PROTOCOL* root))
            {
                status = BootFileStatus.DeviceError;
                return false;
            }

            if (!TryOpenReadOnly(root, path, out EFI_FILE_PROTOCOL* file))
            {
                Close(root);
                status = BootFileStatus.NotFound;
                return false;
            }

            bool loaded = TryReadIntoBuffer(
                systemTable->BootServices,
                file,
                destinationBuffer,
                destinationCapacity,
                out bytesRead,
                out status);

            Close(file);
            Close(root);
            return loaded;
        }

        public static bool TryReadDirectoryEntry(
            BootContext context,
            char* directoryPath,
            uint targetIndex,
            char* nameBuffer,
            uint nameBufferChars,
            out uint nameLength,
            out ulong attributes,
            out BootFileStatus status)
        {
            nameLength = 0;
            attributes = 0;
            status = BootFileStatus.DeviceError;

            EFI_SYSTEM_TABLE* systemTable = context.SystemTable;
            if (systemTable == null || systemTable->BootServices == null || directoryPath == null)
            {
                status = BootFileStatus.InvalidParameter;
                return false;
            }

            if (nameBuffer == null || nameBufferChars == 0)
            {
                status = BootFileStatus.InvalidParameter;
                return false;
            }

            EFI_BOOT_SERVICES* bootServices = systemTable->BootServices;

            if (!TryOpenRoot(context, out EFI_FILE_PROTOCOL* root))
            {
                status = BootFileStatus.DeviceError;
                return false;
            }

            if (!TryOpenReadOnly(root, directoryPath, out EFI_FILE_PROTOCOL* directory))
            {
                Close(root);
                status = BootFileStatus.NotFound;
                return false;
            }

            ulong infoBufferSize = 1024;
            void* infoBuffer = null;
            if (!TryAllocatePool(bootServices, infoBufferSize, out infoBuffer))
            {
                Close(directory);
                Close(root);
                status = BootFileStatus.DeviceError;
                return false;
            }

            if (directory->SetPosition != null && directory->SetPosition(directory, EfiFilePositionStart) != EfiSuccess)
            {
                FreePool(bootServices, infoBuffer);
                Close(directory);
                Close(root);
                status = BootFileStatus.DeviceError;
                return false;
            }

            uint currentIndex = 0;

            while (true)
            {
                ulong readSize = infoBufferSize;
                ulong readStatus = directory->Read(directory, &readSize, infoBuffer);

                if (readStatus == EfiBufferTooSmall)
                {
                    FreePool(bootServices, infoBuffer);
                    infoBuffer = null;

                    if (readSize == 0 || !TryAllocatePool(bootServices, readSize, out infoBuffer))
                    {
                        status = BootFileStatus.DeviceError;
                        break;
                    }

                    infoBufferSize = readSize;
                    continue;
                }

                if (readStatus != EfiSuccess)
                {
                    status = BootFileStatus.DeviceError;
                    break;
                }

                if (readSize == 0)
                {
                    status = BootFileStatus.EndOfDirectory;
                    break;
                }

                EFI_FILE_INFO* fileInfo = (EFI_FILE_INFO*)infoBuffer;
                char* fileNamePointer = &fileInfo->FileName;
                uint fileNameLength = GetStringLength(fileNamePointer);

                if (IsDotEntry(fileNamePointer, fileNameLength))
                    continue;

                if (currentIndex != targetIndex)
                {
                    currentIndex++;
                    continue;
                }

                if (fileNameLength + 1 > nameBufferChars)
                {
                    status = BootFileStatus.BufferTooSmall;
                    break;
                }

                CopyString(nameBuffer, fileNamePointer, fileNameLength);
                nameBuffer[fileNameLength] = '\0';
                nameLength = fileNameLength;
                attributes = fileInfo->Attribute;
                status = BootFileStatus.Ok;
                break;
            }

            FreePool(bootServices, infoBuffer);
            Close(directory);
            Close(root);
            return status == BootFileStatus.Ok;
        }

        public static bool IsDirectory(ulong attributes)
        {
            return (attributes & EfiFileAttributeDirectory) == EfiFileAttributeDirectory;
        }

        public static bool TryReadAll(EFI_BOOT_SERVICES* bootServices, EFI_FILE_PROTOCOL* file, out void* buffer, out uint length)
        {
            buffer = null;
            length = 0;

            if (bootServices == null || file == null || file->GetInfo == null || file->Read == null)
                return false;

            EFI_GUID fileInfoGuid = FileInfoGuid();

            ulong infoBufferSize = 0;
            ulong infoStatus = file->GetInfo(file, &fileInfoGuid, &infoBufferSize, null);
            if (infoStatus != EfiBufferTooSmall && infoStatus != EfiSuccess)
                return false;

            if (infoBufferSize < (ulong)sizeof(EFI_FILE_INFO))
                infoBufferSize = 1024;

            if (!TryAllocatePool(bootServices, infoBufferSize, out void* infoBuffer))
                return false;

            infoStatus = file->GetInfo(file, &fileInfoGuid, &infoBufferSize, infoBuffer);
            if (infoStatus != EfiSuccess)
            {
                FreePool(bootServices, infoBuffer);
                return false;
            }

            EFI_FILE_INFO* fileInfo = (EFI_FILE_INFO*)infoBuffer;
            if (fileInfo->FileSize == 0 || fileInfo->FileSize > 0xFFFFFFFFUL)
            {
                FreePool(bootServices, infoBuffer);
                return false;
            }

            ulong fileSize = fileInfo->FileSize;
            if (!TryAllocatePool(bootServices, fileSize, out void* fileBuffer))
            {
                FreePool(bootServices, infoBuffer);
                return false;
            }

            ulong readSize = fileSize;
            ulong readStatus = file->Read(file, &readSize, fileBuffer);
            FreePool(bootServices, infoBuffer);

            if (readStatus != EfiSuccess || readSize != fileSize)
            {
                FreePool(bootServices, fileBuffer);
                return false;
            }

            buffer = fileBuffer;
            length = (uint)fileSize;
            return true;
        }

        public static bool TryReadIntoBuffer(
            EFI_BOOT_SERVICES* bootServices,
            EFI_FILE_PROTOCOL* file,
            void* destinationBuffer,
            uint destinationCapacity,
            out uint bytesRead,
            out BootFileStatus status)
        {
            bytesRead = 0;
            status = BootFileStatus.DeviceError;

            if (bootServices == null || file == null || destinationBuffer == null || destinationCapacity == 0)
            {
                status = BootFileStatus.InvalidParameter;
                return false;
            }

            if (!TryGetFileSize(bootServices, file, out ulong fileSize))
            {
                status = BootFileStatus.DeviceError;
                return false;
            }

            if (fileSize > destinationCapacity)
            {
                bytesRead = fileSize > 0xFFFFFFFFUL ? 0xFFFFFFFFU : (uint)fileSize;
                status = BootFileStatus.BufferTooSmall;
                return false;
            }

            ulong readSize = fileSize;
            ulong readStatus = file->Read(file, &readSize, destinationBuffer);
            if (readStatus != EfiSuccess || readSize != fileSize)
            {
                status = BootFileStatus.DeviceError;
                return false;
            }

            bytesRead = (uint)fileSize;
            status = BootFileStatus.Ok;
            return true;
        }

        public static void Close(EFI_FILE_PROTOCOL* file)
        {
            if (file == null || file->Close == null)
                return;

            file->Close(file);
        }

        private static bool IsDotEntry(char* fileName, uint fileNameLength)
        {
            if (fileNameLength == 1 && fileName[0] == '.')
                return true;

            if (fileNameLength == 2 && fileName[0] == '.' && fileName[1] == '.')
                return true;

            return false;
        }

        private static uint GetStringLength(char* text)
        {
            if (text == null)
                return 0;

            uint length = 0;
            while (text[length] != '\0')
                length++;

            return length;
        }

        private static void CopyString(char* destination, char* source, uint length)
        {
            for (uint i = 0; i < length; i++)
                destination[i] = source[i];
        }

        private static bool TryAllocatePool(EFI_BOOT_SERVICES* bootServices, ulong size, out void* buffer)
        {
            buffer = null;
            if (bootServices == null || bootServices->AllocatePool == null)
                return false;

            if (size == 0)
                size = 1;

            void* allocated = null;
            ulong status = bootServices->AllocatePool(EFI_MEMORY_TYPE.EfiLoaderData, size, &allocated);
            buffer = allocated;
            return status == EfiSuccess && allocated != null;
        }

        private static void FreePool(EFI_BOOT_SERVICES* bootServices, void* buffer)
        {
            if (bootServices == null || buffer == null || bootServices->FreePool == null)
                return;

            bootServices->FreePool(buffer);
        }

        private static bool TryGetFileSize(EFI_BOOT_SERVICES* bootServices, EFI_FILE_PROTOCOL* file, out ulong fileSize)
        {
            fileSize = 0;

            if (bootServices == null || file == null || file->GetInfo == null)
                return false;

            EFI_GUID fileInfoGuid = FileInfoGuid();
            ulong infoBufferSize = 0;
            ulong infoStatus = file->GetInfo(file, &fileInfoGuid, &infoBufferSize, null);
            if (infoStatus != EfiBufferTooSmall && infoStatus != EfiSuccess)
                return false;

            if (infoBufferSize < (ulong)sizeof(EFI_FILE_INFO))
                infoBufferSize = 1024;

            if (!TryAllocatePool(bootServices, infoBufferSize, out void* infoBuffer))
                return false;

            infoStatus = file->GetInfo(file, &fileInfoGuid, &infoBufferSize, infoBuffer);
            if (infoStatus != EfiSuccess)
            {
                FreePool(bootServices, infoBuffer);
                return false;
            }

            EFI_FILE_INFO* fileInfo = (EFI_FILE_INFO*)infoBuffer;
            fileSize = fileInfo->FileSize;
            FreePool(bootServices, infoBuffer);
            return true;
        }

        private static EFI_GUID LoadedImageProtocolGuid()
        {
            EFI_GUID guid = default;
            guid.Data1 = 0x5B1B31A1;
            guid.Data2 = 0x9562;
            guid.Data3 = 0x11D2;
            guid.Data4_0 = 0x8E;
            guid.Data4_1 = 0x3F;
            guid.Data4_2 = 0x00;
            guid.Data4_3 = 0xA0;
            guid.Data4_4 = 0xC9;
            guid.Data4_5 = 0x69;
            guid.Data4_6 = 0x72;
            guid.Data4_7 = 0x3B;
            return guid;
        }

        private static EFI_GUID SimpleFileSystemProtocolGuid()
        {
            EFI_GUID guid = default;
            guid.Data1 = 0x964E5B22;
            guid.Data2 = 0x6459;
            guid.Data3 = 0x11D2;
            guid.Data4_0 = 0x8E;
            guid.Data4_1 = 0x39;
            guid.Data4_2 = 0x00;
            guid.Data4_3 = 0xA0;
            guid.Data4_4 = 0xC9;
            guid.Data4_5 = 0x69;
            guid.Data4_6 = 0x72;
            guid.Data4_7 = 0x3B;
            return guid;
        }

        private static EFI_GUID FileInfoGuid()
        {
            EFI_GUID guid = default;
            guid.Data1 = 0x09576E92;
            guid.Data2 = 0x6D3F;
            guid.Data3 = 0x11D2;
            guid.Data4_0 = 0x8E;
            guid.Data4_1 = 0x39;
            guid.Data4_2 = 0x00;
            guid.Data4_3 = 0xA0;
            guid.Data4_4 = 0xC9;
            guid.Data4_5 = 0x69;
            guid.Data4_6 = 0x72;
            guid.Data4_7 = 0x3B;
            return guid;
        }
    }
}
