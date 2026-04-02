using OS.Boot;
using OS.Hal;

namespace OS.Kernel.Input
{
    internal static class Keyboard
    {
        public static bool IsAvailable => Platform.HasCapability(PlatformCapabilities.KeyboardInput);

        public static KeyReadStatus TryReadKey(out KeyInfo key)
        {
            key = default;

            KeyboardReadStatus status = Platform.TryReadKey(out ushort unicodeChar, out ushort scanCode);
            if (status == KeyboardReadStatus.KeyAvailable)
            {
                key.UnicodeChar = unicodeChar;
                key.ScanCode = scanCode;
                return KeyReadStatus.KeyAvailable;
            }

            if (status == KeyboardReadStatus.NoKey)
                return KeyReadStatus.NoKey;

            if (status == KeyboardReadStatus.Unsupported)
                return KeyReadStatus.Unsupported;

            return KeyReadStatus.DeviceError;
        }

        public static bool TryReadChar(out char value)
        {
            value = '\0';

            KeyReadStatus status = TryReadKey(out KeyInfo key);
            if (status != KeyReadStatus.KeyAvailable || key.UnicodeChar == 0)
                return false;

            value = (char)key.UnicodeChar;
            return true;
        }
    }
}
