using OS.Hal;

namespace OS.Kernel.Input
{
    internal static class InputDiagnostics
    {
        private const ushort EscUnicodeChar = 0x001B;
        private const ushort EscScanCode = 0x0017;

        public static void Run()
        {
            if (!Keyboard.IsAvailable)
            {
                Log.Write(LogLevel.Warn, "keyboard input not available");
                return;
            }

            Log.Write(LogLevel.Info, "keyboard init ok");
            Log.Write(LogLevel.Info, "press keys, ESC to continue");

            while (true)
            {
                KeyReadStatus readStatus = Keyboard.TryReadKey(out KeyInfo key);
                if (readStatus == KeyReadStatus.NoKey)
                    continue;

                if (readStatus == KeyReadStatus.Unsupported)
                {
                    Log.Write(LogLevel.Warn, "keyboard input became unavailable");
                    break;
                }

                if (readStatus == KeyReadStatus.DeviceError)
                {
                    Log.Write(LogLevel.Warn, "keyboard read failed");
                    break;
                }

                WriteKey(ref key);

                if (IsEscape(ref key))
                    break;
            }

            Log.Write(LogLevel.Info, "keyboard demo done");
        }

        private static bool IsEscape(ref KeyInfo key)
        {
            return key.UnicodeChar == EscUnicodeChar || key.ScanCode == EscScanCode;
        }

        private static void WriteKey(ref KeyInfo key)
        {
            Log.Begin(LogLevel.Info);
            Console.Write("key: ");

            if (key.UnicodeChar == 0)
            {
                Console.Write("char=0");
            }
            else if (key.UnicodeChar >= 0x20 && key.UnicodeChar <= 0x7E)
            {
                Console.Write("char='");
                Console.WriteChar((char)key.UnicodeChar);
                Console.Write("'");
            }
            else
            {
                Console.Write("char=0x");
                Console.WriteHex(key.UnicodeChar, 4);
            }

            Console.Write(" scan=");
            Console.WriteUInt(key.ScanCode);
            Log.EndLine();
        }
    }
}
