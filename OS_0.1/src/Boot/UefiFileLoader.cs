namespace OS.Boot
{
    internal static unsafe class UefiFileLoader
    {
        public static BootFileStatus Exists(BootContext context, char* path)
        {
            if (path == null)
                return BootFileStatus.InvalidParameter;

            return UefiFile.TryExists(context, path) ? BootFileStatus.Ok : BootFileStatus.NotFound;
        }

        public static BootFileStatus ReadAll(BootContext context, char* path, out void* image, out uint imageSize)
        {
            image = null;
            imageSize = 0;

            if (path == null)
                return BootFileStatus.InvalidParameter;

            if (!UefiFile.TryExists(context, path))
                return BootFileStatus.NotFound;

            return UefiFile.TryReadAll(context, path, out image, out imageSize)
                ? BootFileStatus.Ok
                : BootFileStatus.DeviceError;
        }

        public static BootFileStatus ReadIntoBuffer(
            BootContext context,
            char* path,
            void* buffer,
            uint capacity,
            out uint bytesRead)
        {
            bytesRead = 0;

            if (path == null || buffer == null || capacity == 0)
                return BootFileStatus.InvalidParameter;

            if (!UefiFile.TryExists(context, path))
                return BootFileStatus.NotFound;

            return UefiFile.TryReadIntoBuffer(
                context,
                path,
                buffer,
                capacity,
                out bytesRead,
                out BootFileStatus status)
                ? BootFileStatus.Ok
                : status;
        }

        public static BootFileStatus ReadDirectoryEntry(
            BootContext context,
            char* directoryPath,
            uint index,
            char* nameBuffer,
            uint nameBufferChars,
            out uint nameLength,
            out ulong attributes)
        {
            nameLength = 0;
            attributes = 0;

            if (directoryPath == null || nameBuffer == null || nameBufferChars == 0)
                return BootFileStatus.InvalidParameter;

            if (!UefiFile.TryExists(context, directoryPath))
                return BootFileStatus.NotFound;

            return UefiFile.TryReadDirectoryEntry(
                context,
                directoryPath,
                index,
                nameBuffer,
                nameBufferChars,
                out nameLength,
                out attributes,
                out BootFileStatus status)
                ? BootFileStatus.Ok
                : status;
        }
    }
}
