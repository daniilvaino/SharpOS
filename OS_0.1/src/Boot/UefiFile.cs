using System;

namespace OS.Boot
{
    internal static unsafe class UefiFile
    {
        private const ulong EfiSuccess = 0;
        private const ulong EfiFileModeRead = 0x0000000000000001UL;

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
            file = null;
            if (root == null || root->Open == null)
                return false;

            fixed (char* pathPointer = path)
            {
                EFI_FILE_PROTOCOL* openedFile = null;
                ulong status = root->Open(root, &openedFile, pathPointer, EfiFileModeRead, 0);
                if (status != EfiSuccess || openedFile == null)
                    return false;

                file = openedFile;
                return true;
            }
        }

        public static bool TryReadAll(EFI_BOOT_SERVICES* bootServices, EFI_FILE_PROTOCOL* file, out void* buffer, out uint length)
        {
            buffer = null;
            length = 0;

            if (bootServices == null || file == null || file->GetInfo == null || file->Read == null || bootServices->AllocatePool == null)
                return false;

            ulong infoBufferSize = 512;
            void* infoBuffer = null;
            ulong status = bootServices->AllocatePool(EFI_MEMORY_TYPE.EfiLoaderData, infoBufferSize, &infoBuffer);
            if (status != EfiSuccess || infoBuffer == null)
                return false;

            EFI_GUID fileInfoGuid = FileInfoGuid();
            status = file->GetInfo(file, &fileInfoGuid, &infoBufferSize, infoBuffer);
            if (status != EfiSuccess)
                return false;

            EFI_FILE_INFO* fileInfo = (EFI_FILE_INFO*)infoBuffer;
            if (fileInfo->FileSize == 0 || fileInfo->FileSize > 0xFFFFFFFFUL)
                return false;

            ulong fileSize = fileInfo->FileSize;
            void* fileBuffer = null;
            status = bootServices->AllocatePool(EFI_MEMORY_TYPE.EfiLoaderData, fileSize, &fileBuffer);
            if (status != EfiSuccess || fileBuffer == null)
                return false;

            ulong readSize = fileSize;
            status = file->Read(file, &readSize, fileBuffer);
            if (status != EfiSuccess || readSize != fileSize)
                return false;

            buffer = fileBuffer;
            length = (uint)fileSize;
            return true;
        }

        public static void Close(EFI_FILE_PROTOCOL* file)
        {
            if (file == null || file->Close == null)
                return;

            file->Close(file);
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
