namespace OS.Boot
{
    internal static unsafe class UefiConsole
    {
        // Enumerate all ConOut text modes and switch to the one with most columns*rows.
        // This makes the text fill the screen on systems where firmware defaults to a
        // low-resolution mode (e.g. 800x600 on a desktop GPU).
        public static void TryMaximizeTextMode(EFI_SYSTEM_TABLE* systemTable)
        {
            if (systemTable == null || systemTable->ConOut == null)
                return;

            EFI_SIMPLE_TEXT_OUTPUT_PROTOCOL* conOut = systemTable->ConOut;
            if (conOut->Mode == null || conOut->QueryMode == null || conOut->SetMode == null)
                return;

            int maxMode = conOut->Mode->MaxMode;
            if (maxMode <= 1)
                return;

            int bestMode = conOut->Mode->Mode;
            ulong bestScore = 0;

            for (int i = 0; i < maxMode; i++)
            {
                ulong cols = 0, rows = 0;
                ulong status = conOut->QueryMode(conOut, (ulong)i, &cols, &rows);
                if (status != 0)
                    continue;

                ulong score = cols * rows;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestMode = i;
                }
            }

            if (bestMode != conOut->Mode->Mode)
            {
                conOut->SetMode(conOut, (ulong)bestMode);
                if (conOut->ClearScreen != null)
                    conOut->ClearScreen(conOut);
            }
        }

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
