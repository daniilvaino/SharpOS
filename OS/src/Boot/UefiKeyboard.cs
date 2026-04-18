namespace OS.Boot
{
    internal static unsafe class UefiKeyboard
    {
        private const ulong EfiSuccess = 0;
        private const ulong EfiNotReady = 0x8000000000000006UL;

        public static bool IsAvailable(EFI_SYSTEM_TABLE* systemTable)
        {
            if (systemTable == null || systemTable->ConIn == null)
                return false;

            return systemTable->ConIn->ReadKeyStroke != null;
        }

        public static bool TryReadKey(BootContext context, out ushort unicodeChar, out ushort scanCode, out BootKeyReadStatus status)
        {
            unicodeChar = 0;
            scanCode = 0;
            status = BootKeyReadStatus.DeviceError;

            EFI_SYSTEM_TABLE* systemTable = context.SystemTable;
            if (systemTable == null)
            {
                status = BootKeyReadStatus.InvalidParameter;
                return false;
            }

            EFI_SIMPLE_TEXT_INPUT_PROTOCOL* conIn = systemTable->ConIn;
            if (conIn == null || conIn->ReadKeyStroke == null)
            {
                status = BootKeyReadStatus.Unsupported;
                return false;
            }

            EFI_INPUT_KEY key = default;
            ulong readStatus = conIn->ReadKeyStroke(conIn, &key);
            if (readStatus == EfiSuccess)
            {
                unicodeChar = key.UnicodeChar;
                scanCode = key.ScanCode;
                status = BootKeyReadStatus.Ok;
                return true;
            }

            if (readStatus == EfiNotReady)
            {
                status = BootKeyReadStatus.NotReady;
                return false;
            }

            status = BootKeyReadStatus.DeviceError;
            return false;
        }
    }
}
