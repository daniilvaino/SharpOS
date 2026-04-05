namespace OS.Boot
{
    internal static unsafe class UefiConsole
    {
        public static void Write(EFI_SYSTEM_TABLE* systemTable, string text)
        {
            if (systemTable == null || systemTable->ConOut == null || text == null || text.Length == 0)
            {
                return;
            }

            fixed (char* p = text)
            {
                for (int i = 0; i < text.Length; i++)
                {
                    WriteChar(systemTable, p[i]);
                }
            }
        }

        public static void WriteChar(EFI_SYSTEM_TABLE* systemTable, char value)
        {
            if (systemTable == null || systemTable->ConOut == null)
                return;

            // Normalize newlines for firmware consoles that need CRLF.
            if (value == '\r')
                return;

            if (value == '\n')
            {
                char* newline = stackalloc char[3];
                newline[0] = '\r';
                newline[1] = '\n';
                newline[2] = '\0';
                systemTable->ConOut->OutputString(systemTable->ConOut, newline);
                return;
            }

            char* buffer = stackalloc char[2];
            buffer[0] = value;
            buffer[1] = '\0';
            systemTable->ConOut->OutputString(systemTable->ConOut, buffer);
        }
    }
}
