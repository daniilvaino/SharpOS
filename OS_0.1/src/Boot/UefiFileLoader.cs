namespace OS.Boot
{
    internal static unsafe class UefiFileLoader
    {
        private const string PrimaryAppPath = "\\EFI\\BOOT\\APP.ELF";
        private const string FallbackAppPath = "\\APP.ELF";

        public static bool TryLoadAppElf(BootContext context, out void* image, out uint imageSize)
        {
            image = null;
            imageSize = 0;

            EFI_SYSTEM_TABLE* systemTable = context.SystemTable;
            if (systemTable == null || systemTable->BootServices == null)
                return false;

            if (!UefiFile.TryOpenRoot(context, out EFI_FILE_PROTOCOL* root))
                return false;

            EFI_FILE_PROTOCOL* file = null;
            bool opened = UefiFile.TryOpenReadOnly(root, PrimaryAppPath, out file) ||
                UefiFile.TryOpenReadOnly(root, FallbackAppPath, out file);

            if (!opened || file == null)
            {
                UefiFile.Close(root);
                return false;
            }

            bool loaded = UefiFile.TryReadAll(systemTable->BootServices, file, out image, out imageSize);
            UefiFile.Close(file);
            UefiFile.Close(root);
            return loaded;
        }
    }
}
