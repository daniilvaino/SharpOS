namespace OS.Boot
{
    internal static unsafe class UefiConsole
    {
        public static void Write(EFI_SYSTEM_TABLE* systemTable, string text)
        {
            fixed (char* p = text)
            {
                systemTable->ConOut->OutputString(systemTable->ConOut, p);
            }
        }

        public static void WriteChar(EFI_SYSTEM_TABLE* systemTable, char value)
        {
            char* buffer = stackalloc char[2];
            buffer[0] = value;
            buffer[1] = '\0';
            systemTable->ConOut->OutputString(systemTable->ConOut, buffer);
        }
    }
}
