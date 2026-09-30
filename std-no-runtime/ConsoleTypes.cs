// Console key and colour types, ported from dotnet/runtime v8.0 (MIT):
// src/libraries/System.Console/src/System/{ConsoleColor,ConsoleKey,
// ConsoleModifiers,ConsoleKeyInfo}.cs
//
// Copied as close to the original as the environment allows, because these are
// fully BCL-compatible value types and code written against the real ones has
// to compile here unchanged — which is exactly why they keep the canonical
// System namespace rather than a SharpOS one.
//
// Cuts: the [NotNullWhen] annotation on Equals(object) and the resource lookup
// in the range check, which becomes a plain message. Nothing else.

namespace System
{

    // This enumeration represents the colors that can be used for
    // console text foreground and background colors.

    public enum ConsoleColor
    {
        Black = 0,
        DarkBlue = 1,
        DarkGreen = 2,
        DarkCyan = 3,
        DarkRed = 4,
        DarkMagenta = 5,
        DarkYellow = 6,
        Gray = 7,
        DarkGray = 8,
        Blue = 9,
        Green = 10,
        Cyan = 11,
        Red = 12,
        Magenta = 13,
        Yellow = 14,
        White = 15
    }

    public enum ConsoleKey
    {
        None = 0x0,
        Backspace = 0x8,
        Tab = 0x9,
        Clear = 0xC,
        Enter = 0xD,
        Pause = 0x13,
        Escape = 0x1B,
        Spacebar = 0x20,
        PageUp = 0x21,
        PageDown = 0x22,
        End = 0x23,
        Home = 0x24,
        LeftArrow = 0x25,
        UpArrow = 0x26,
        RightArrow = 0x27,
        DownArrow = 0x28,
        Select = 0x29,
        Print = 0x2A,
        Execute = 0x2B,
        PrintScreen = 0x2C,
        Insert = 0x2D,
        Delete = 0x2E,
        Help = 0x2F,
        D0 = 0x30,  // 0 through 9
        D1 = 0x31,
        D2 = 0x32,
        D3 = 0x33,
        D4 = 0x34,
        D5 = 0x35,
        D6 = 0x36,
        D7 = 0x37,
        D8 = 0x38,
        D9 = 0x39,
        A = 0x41,
        B = 0x42,
        C = 0x43,
        D = 0x44,
        E = 0x45,
        F = 0x46,
        G = 0x47,
        H = 0x48,
        I = 0x49,
        J = 0x4A,
        K = 0x4B,
        L = 0x4C,
        M = 0x4D,
        N = 0x4E,
        O = 0x4F,
        P = 0x50,
        Q = 0x51,
        R = 0x52,
        S = 0x53,
        T = 0x54,
        U = 0x55,
        V = 0x56,
        W = 0x57,
        X = 0x58,
        Y = 0x59,
        Z = 0x5A,
        LeftWindows = 0x5B,  // Microsoft Natural keyboard
        RightWindows = 0x5C,  // Microsoft Natural keyboard
        Applications = 0x5D,  // Microsoft Natural keyboard
        Sleep = 0x5F,
        NumPad0 = 0x60,
        NumPad1 = 0x61,
        NumPad2 = 0x62,
        NumPad3 = 0x63,
        NumPad4 = 0x64,
        NumPad5 = 0x65,
        NumPad6 = 0x66,
        NumPad7 = 0x67,
        NumPad8 = 0x68,
        NumPad9 = 0x69,
        Multiply = 0x6A,
        Add = 0x6B,
        Separator = 0x6C,
        Subtract = 0x6D,
        Decimal = 0x6E,
        Divide = 0x6F,
        F1 = 0x70,
        F2 = 0x71,
        F3 = 0x72,
        F4 = 0x73,
        F5 = 0x74,
        F6 = 0x75,
        F7 = 0x76,
        F8 = 0x77,
        F9 = 0x78,
        F10 = 0x79,
        F11 = 0x7A,
        F12 = 0x7B,
        F13 = 0x7C,
        F14 = 0x7D,
        F15 = 0x7E,
        F16 = 0x7F,
        F17 = 0x80,
        F18 = 0x81,
        F19 = 0x82,
        F20 = 0x83,
        F21 = 0x84,
        F22 = 0x85,
        F23 = 0x86,
        F24 = 0x87,
        BrowserBack = 0xA6,  // Windows 2000/XP
        BrowserForward = 0xA7,  // Windows 2000/XP
        BrowserRefresh = 0xA8,  // Windows 2000/XP
        BrowserStop = 0xA9,  // Windows 2000/XP
        BrowserSearch = 0xAA,  // Windows 2000/XP
        BrowserFavorites = 0xAB,  // Windows 2000/XP
        BrowserHome = 0xAC,  // Windows 2000/XP
        VolumeMute = 0xAD,  // Windows 2000/XP
        VolumeDown = 0xAE,  // Windows 2000/XP
        VolumeUp = 0xAF,  // Windows 2000/XP
        MediaNext = 0xB0,  // Windows 2000/XP
        MediaPrevious = 0xB1,  // Windows 2000/XP
        MediaStop = 0xB2,  // Windows 2000/XP
        MediaPlay = 0xB3,  // Windows 2000/XP
        LaunchMail = 0xB4,  // Windows 2000/XP
        LaunchMediaSelect = 0xB5,  // Windows 2000/XP
        LaunchApp1 = 0xB6,  // Windows 2000/XP
        LaunchApp2 = 0xB7,  // Windows 2000/XP
        Oem1 = 0xBA,
        OemPlus = 0xBB,
        OemComma = 0xBC,
        OemMinus = 0xBD,
        OemPeriod = 0xBE,
        Oem2 = 0xBF,
        Oem3 = 0xC0,
        Oem4 = 0xDB,
        Oem5 = 0xDC,
        Oem6 = 0xDD,
        Oem7 = 0xDE,
        Oem8 = 0xDF,
        Oem102 = 0xE2,  // Win2K/XP: Either angle or backslash on RT 102-key keyboard
        Process = 0xE5,  // Windows: IME Process Key
        Packet = 0xE7,  // Win2K/XP: Used to pass Unicode chars as if keystrokes
        Attention = 0xF6,
        CrSel = 0xF7,
        ExSel = 0xF8,
        EraseEndOfFile = 0xF9,
        Play = 0xFA,
        Zoom = 0xFB,
        NoName = 0xFC,  // Reserved
        Pa1 = 0xFD,
        OemClear = 0xFE,
    }

    [Flags]
    public enum ConsoleModifiers
    {
        None = 0,
        Alt = 1,
        Shift = 2,
        Control = 4
    }

    public readonly struct ConsoleKeyInfo : IEquatable<ConsoleKeyInfo>
    {
        private readonly char _keyChar;
        private readonly ConsoleKey _key;
        private readonly ConsoleModifiers _mods;

        public ConsoleKeyInfo(char keyChar, ConsoleKey key, bool shift, bool alt, bool control)
        {
            // Limit ConsoleKey values to 0 to 255, but don't check whether the
            // key is a valid value in our ConsoleKey enum.  There are a few
            // values in that enum that we didn't define, and reserved keys
            // that might start showing up on keyboards in a few years.
            if (((int)key) < 0 || ((int)key) > 255)
            {
                throw new ArgumentOutOfRangeException(nameof(key),
                    "Console key values must be between 0 and 255.");
            }

            _keyChar = keyChar;
            _key = key;
            _mods = 0;
            if (shift)
                _mods |= ConsoleModifiers.Shift;
            if (alt)
                _mods |= ConsoleModifiers.Alt;
            if (control)
                _mods |= ConsoleModifiers.Control;
        }

        public char KeyChar
        {
            get { return _keyChar; }
        }

        public ConsoleKey Key
        {
            get { return _key; }
        }

        public ConsoleModifiers Modifiers
        {
            get { return _mods; }
        }

        public override bool Equals(object? value)
        {
            return value is ConsoleKeyInfo info && Equals(info);
        }

        public bool Equals(ConsoleKeyInfo obj)
        {
            return obj._keyChar == _keyChar && obj._key == _key && obj._mods == _mods;
        }

        public static bool operator ==(ConsoleKeyInfo a, ConsoleKeyInfo b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(ConsoleKeyInfo a, ConsoleKeyInfo b)
        {
            return !(a == b);
        }

        public override int GetHashCode()
        {
            // For all normal cases we can fit all bits losslessly into the hash code:
            // _keyChar could be any 16-bit value (though is most commonly ASCII). Use all 16 bits without conflict.
            // _key is 32-bit, but the ctor throws for anything over 255. Use those 8 bits without conflict.
            // _mods only has enum defined values for 1,2,4: 3 bits. Use the remaining 8 bits.
            return _keyChar | ((int)_key << 16) | ((int)_mods << 24);
        }
    }
}
